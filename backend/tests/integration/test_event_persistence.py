"""事件入库、幂等和记忆水位的集成测试。"""

from __future__ import annotations

import asyncio

import pytest
from sqlalchemy import func, select

from stardew_npc_agent.event_service import EventService, project_event_to_memory
from stardew_npc_agent.schemas import GameEvent, GameEventBatchRequest
from stardew_npc_agent.storage import (
    EventBatchTooLargeError,
    GameEventRecord,
    MemoryRecord,
    PreparedEvent,
    SqliteStorage,
)


def _request(
    *,
    request_id: str = "request-events-1",
    event_id: str = "event-gift-1",
    item_id: str = "(O)66",
    day_index: int = 13,
) -> GameEventBatchRequest:
    """构造一个合法请求，并允许测试显式改变幂等身份字段。"""

    return GameEventBatchRequest.model_validate(
        {
            "schema_version": "1.0",
            "request_id": request_id,
            "save_id": "save-1",
            "player_id": "player-1",
            "events": [
                {
                    "event_id": event_id,
                    "event_type": "gift_given",
                    "event_version": "2",
                    "occurred_day_index": day_index,
                    "source": "harmony.farmer.on_gift_given",
                    "audience_scope": "npc",
                    "audience_npc_id": "Abigail",
                    "payload": {
                        "item_id": item_id,
                        "taste": "love",
                    },
                }
            ],
        }
    )


async def _row_counts(storage: SqliteStorage) -> tuple[int, int]:
    """通过短时只读测试会话统计事实行和记忆投影行。"""

    async with storage.session_factory() as session:
        event_count = await session.scalar(select(func.count()).select_from(GameEventRecord))
        memory_count = await session.scalar(select(func.count()).select_from(MemoryRecord))
    return int(event_count or 0), int(memory_count or 0)


@pytest.mark.asyncio
async def test_replaying_same_event_ten_times_creates_one_fact_and_one_memory(
    storage: SqliteStorage,
) -> None:
    """更换 request_id 的合法重传仍必须以事件三元组幂等。"""

    service = EventService(storage)
    responses = []
    for replay_index in range(10):
        responses.append(
            await service.ingest_batch(_request(request_id=f"request-replay-{replay_index}"))
        )

    assert [response.items[0].status for response in responses] == [
        "accepted",
        *("duplicate" for _ in range(9)),
    ]
    assert all(response.memory_revision == 1 for response in responses)
    assert all(response.committed_through_day_index == 13 for response in responses)
    assert await _row_counts(storage) == (1, 1)
    snapshot = await storage.get_memory_partition_snapshot("save-1", "player-1")
    assert snapshot.memory_revision == 1
    assert snapshot.retrieval_state_revision == 1


@pytest.mark.asyncio
async def test_concurrent_duplicate_events_are_fenced_by_database_uniqueness(
    storage: SqliteStorage,
) -> None:
    """进程内竞态不得穿透 DB unique constraint 并产生双份事实。"""

    service = EventService(storage)
    responses = await asyncio.gather(
        *(
            service.ingest_batch(_request(request_id=f"request-concurrent-{index}"))
            for index in range(10)
        )
    )

    statuses = [response.items[0].status for response in responses]
    assert statuses.count("accepted") == 1
    assert statuses.count("duplicate") == 9
    assert {response.memory_revision for response in responses} == {1}
    assert await _row_counts(storage) == (1, 1)


@pytest.mark.asyncio
async def test_concurrent_distinct_events_advance_partition_revision_without_lost_updates(
    storage: SqliteStorage,
) -> None:
    """十个不同事件并发写入同一分区时，revision 不得因 read-modify-write 竞态丢增量。"""

    service = EventService(storage)
    responses = await asyncio.gather(
        *(
            service.ingest_batch(
                _request(
                    request_id=f"request-distinct-{index}",
                    event_id=f"event-distinct-{index}",
                    item_id=f"(O)Item-{index}",
                )
            )
            for index in range(10)
        )
    )

    assert all(response.items[0].status == "accepted" for response in responses)
    # 并发请求的返回顺序不等于提交顺序，因此只要有一个请求看到
    # 最终 revision=10 即可；最终事实数与分区状态会在下方独立查询。
    assert max(response.memory_revision for response in responses) == 10
    assert await _row_counts(storage) == (10, 10)
    concurrent_snapshot = await storage.get_memory_partition_snapshot("save-1", "player-1")
    assert concurrent_snapshot.memory_revision == 10
    assert concurrent_snapshot.retrieval_state_revision == 10
    final_state = await service.ingest_batch(
        _request(
            request_id="request-final-state",
            event_id="event-distinct-0",
            item_id="(O)Item-0",
        )
    )
    assert final_state.items[0].status == "duplicate"
    assert final_state.memory_revision == 10


@pytest.mark.asyncio
async def test_same_event_id_with_conflicting_payload_is_rejected_not_hidden_as_duplicate(
    storage: SqliteStorage,
) -> None:
    """幂等键冲突时必须区分合法重传和身份复用，避免静默丢失新事实。"""

    service = EventService(storage)
    first = await service.ingest_batch(_request())
    conflicting = await service.ingest_batch(
        _request(request_id="request-conflict", item_id="(O)Quartz")
    )

    assert first.items[0].status == "accepted"
    assert conflicting.items[0].status == "rejected"
    assert conflicting.items[0].reason_code == "EVENT_ID_CONFLICT"
    assert conflicting.memory_revision == 1
    assert await _row_counts(storage) == (1, 1)


@pytest.mark.asyncio
async def test_invalid_event_is_rejected_per_item_without_poisoning_valid_sibling(
    storage: SqliteStorage,
) -> None:
    """合法 envelope 中单条 payload 无法投影时，其他事件仍应正常提交。"""

    service = EventService(storage)
    request = _request().model_copy(
        update={
            "events": [
                _request().events[0],
                _request(event_id="event-invalid-1")
                .events[0]
                .model_copy(update={"payload": {"taste": "love"}}),
            ]
        }
    )

    response = await service.ingest_batch(request)

    assert [item.status for item in response.items] == ["accepted", "rejected"]
    assert response.items[1].reason_code == "INVALID_EVENT_PAYLOAD"
    assert response.memory_revision == 1
    assert response.committed_through_day_index == 13
    assert await _row_counts(storage) == (1, 1)


def _distinct_events(count: int) -> list[GameEvent]:
    """构造指定数量的合法且幂等键不同的 GameEvent 对象。"""

    return [
        _request(event_id=f"event-batch-{index}", item_id=f"(O)Item-{index}").events[0]
        for index in range(count)
    ]


@pytest.mark.asyncio
async def test_sixty_four_events_are_accepted_in_one_transaction(
    storage: SqliteStorage,
) -> None:
    """资源上限本身必须可用，64 条事件不得因 off-by-one 被拒绝。"""

    service = EventService(storage)
    base = _request()
    request = base.model_copy(update={"events": _distinct_events(64)})

    response = await service.ingest_batch(request)

    assert len(response.items) == 64
    assert all(item.status == "accepted" for item in response.items)
    assert response.memory_revision == 64
    assert await _row_counts(storage) == (64, 64)


@pytest.mark.asyncio
async def test_service_and_storage_both_reject_sixty_five_events_without_writes(
    storage: SqliteStorage,
) -> None:
    """即使进程内调用者绕过 Pydantic，service 和 storage 也必须各自守住 64 上限。"""

    service = EventService(storage)
    base = _request()
    events = _distinct_events(65)
    oversized_request = GameEventBatchRequest.model_construct(
        schema_version=base.schema_version,
        request_id="request-oversized-service",
        save_id=base.save_id,
        player_id=base.player_id,
        events=events,
    )

    with pytest.raises(EventBatchTooLargeError):
        await service.ingest_batch(oversized_request)
    assert await _row_counts(storage) == (0, 0)

    prepared_events = [
        PreparedEvent(
            save_id=base.save_id,
            player_id=base.player_id,
            event_id=event.event_id,
            event_type=event.event_type,
            event_version=event.event_version,
            occurred_day_index=event.occurred_day_index,
            source=event.source,
            audience_scope=event.audience_scope,
            audience_npc_id=event.audience_npc_id,
            payload_json=dict(event.payload),
            projection=project_event_to_memory(base.save_id, base.player_id, event),
        )
        for event in events
    ]
    with pytest.raises(EventBatchTooLargeError):
        await storage.ingest_events(base.save_id, base.player_id, prepared_events)
    assert await _row_counts(storage) == (0, 0)
