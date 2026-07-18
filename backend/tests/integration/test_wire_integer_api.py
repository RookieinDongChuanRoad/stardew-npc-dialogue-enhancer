"""公开 HTTP 路由对 Int32 wire integer 边界的集成测试。"""

from __future__ import annotations

from datetime import UTC, datetime

import httpx
import pytest
from sqlalchemy import func, select, text

from stardew_npc_agent.api import create_app
from stardew_npc_agent.config import Settings
from stardew_npc_agent.event_service import EventService
from stardew_npc_agent.schemas import GameEventBatchRequest
from stardew_npc_agent.storage import (
    DialogueDisplayReceiptRecord,
    DialogueGenerationInput,
    GameEventRecord,
    MemoryPartitionStateRecord,
    MemoryRecord,
    SqliteStorage,
)

WIRE_INTEGER_MAX = 2_147_483_647


def _event_payload(*, day_index: int, request_id: str = "request-wire-event") -> dict[str, object]:
    """构造只改变事件日的真实路由 payload。"""

    return {
        "schema_version": "1.0",
        "request_id": request_id,
        "save_id": "save-wire",
        "player_id": "player-wire",
        "events": [
            {
                "event_id": "event-wire-1",
                "event_type": "world_progression",
                "event_version": "1",
                "occurred_day_index": day_index,
                "source": "smapi.world.public_facility_restored",
                "audience_scope": "public",
                "audience_npc_id": None,
                "payload": {"milestone": "public_facility_minecarts_restored"},
            }
        ],
    }


async def _fact_and_memory_counts(storage: SqliteStorage) -> tuple[int, int]:
    """读取事实和记忆行数，证明 422 没有发生部分写入。"""

    async with storage.session_factory() as session:
        event_count = await session.scalar(select(func.count()).select_from(GameEventRecord))
        memory_count = await session.scalar(select(func.count()).select_from(MemoryRecord))
    return int(event_count or 0), int(memory_count or 0)


async def _seed_corrupt_partition_state(
    storage: SqliteStorage,
    *,
    revision: int | float,
    committed_day: int | float,
) -> None:
    """绕过数据库 CHECK 注入外部腐化水位，验证公开路由的稳定错误映射。

    这里仅在测试夹具中临时关闭 SQLite CHECK；生产代码没有也不应提供绕过
    约束的入口。提交腐化行后立即恢复连接级开关，避免影响后续业务写入断言。
    """

    async with storage.session_factory() as session:
        await session.execute(text("PRAGMA ignore_check_constraints=ON"))
        try:
            session.add(
                MemoryPartitionStateRecord(
                    save_id="save-wire",
                    player_id="player-wire",
                    memory_revision=revision,
                    committed_through_day_index=committed_day,
                    updated_at_utc=datetime.now(UTC),
                )
            )
            await session.commit()
        finally:
            await session.execute(text("PRAGMA ignore_check_constraints=OFF"))


@pytest.mark.asyncio
async def test_event_route_rejects_day_above_int32_before_writes_and_accepts_maximum(
    storage: SqliteStorage,
    migrated_database_url: str,
) -> None:
    """max+1 必须是 wire 422；恰好 max 仍能走完真实投影与事务。"""

    service = EventService(storage)
    app = create_app(Settings(database_url=migrated_database_url), event_service=service)
    transport = httpx.ASGITransport(app=app, raise_app_exceptions=False)

    async with httpx.AsyncClient(transport=transport, base_url="http://testserver") as client:
        rejected = await client.post(
            "/api/v1/game-events/batches",
            json=_event_payload(day_index=WIRE_INTEGER_MAX + 1),
        )
        counts_after_rejection = await _fact_and_memory_counts(storage)
        accepted = await client.post(
            "/api/v1/game-events/batches",
            json=_event_payload(
                day_index=WIRE_INTEGER_MAX,
                request_id="request-wire-event-max",
            ),
        )

    assert rejected.status_code == 422
    assert isinstance(rejected.json().get("detail"), list)
    assert counts_after_rejection == (0, 0)
    assert "sqlite" not in rejected.text.lower()
    assert "phase3.sqlite3" not in rejected.text

    assert accepted.status_code == 200
    assert accepted.json()["committed_through_day_index"] == WIRE_INTEGER_MAX
    assert await _fact_and_memory_counts(storage) == (1, 1)
    async with storage.session_factory() as session:
        stored_day = await session.scalar(select(GameEventRecord.occurred_day_index))
    assert stored_day == WIRE_INTEGER_MAX


@pytest.mark.asyncio
async def test_display_ack_route_rejects_day_above_int32_at_wire_boundary_without_consumption(
    storage: SqliteStorage,
    migrated_database_url: str,
) -> None:
    """ACK max+1 不得依赖后续“与生成日不一致”碰巧拒绝，而应由 DTO 返回 422。"""

    service = EventService(storage)
    await service.ingest_batch(GameEventBatchRequest.model_validate(_event_payload(day_index=5)))
    async with storage.session_factory() as session:
        evidence_id = await session.scalar(select(MemoryRecord.memory_id))
    assert evidence_id is not None
    await storage.save_dialogue_generation(
        DialogueGenerationInput(
            generation_id="generation-wire-ack",
            generation_key="generation-key-wire-ack",
            save_id="save-wire",
            player_id="player-wire",
            game_day_index=WIRE_INTEGER_MAX,
            npc_id="Abigail",
            locale="zh-CN",
            source_hash="sha256:wire-ack-source",
            relationship_stage="friend",
            friendship_points=750,
            memory_cooldown_days=3,
            status="generated",
            result_text="用于验证 ACK wire 上界的增强台词。",
            reason_code="TEST_GENERATED",
            evidence_ids=(evidence_id,),
            trace_id="trace-wire-ack",
            guard_passed=True,
        )
    )

    app = create_app(Settings(database_url=migrated_database_url), event_service=service)
    transport = httpx.ASGITransport(app=app, raise_app_exceptions=False)
    async with httpx.AsyncClient(transport=transport, base_url="http://testserver") as client:
        response = await client.post(
            "/api/v1/dialogue-generations/generation-wire-ack/displayed",
            json={
                "schema_version": "1.0",
                "request_id": "request-wire-ack",
                "save_id": "save-wire",
                "player_id": "player-wire",
                "display_receipt_id": "receipt-wire-ack",
                "displayed_day_index": WIRE_INTEGER_MAX + 1,
                "npc_id": "Abigail",
                "source_hash": "sha256:wire-ack-source",
            },
        )

    async with storage.session_factory() as session:
        receipt_count = await session.scalar(
            select(func.count()).select_from(DialogueDisplayReceiptRecord)
        )
        memory = await session.scalar(select(MemoryRecord))

    assert response.status_code == 422
    assert isinstance(response.json().get("detail"), list)
    assert "sqlite" not in response.text.lower()
    assert int(receipt_count or 0) == 0
    assert memory is not None
    assert (memory.use_count, memory.last_used_day_index) == (0, None)


@pytest.mark.asyncio
async def test_event_route_maps_revision_exhaustion_without_partial_writes(
    storage: SqliteStorage,
    migrated_database_url: str,
) -> None:
    """分区 revision 已满时必须稳定返回 409，不能提交后在响应 DTO 阶段变成 500。"""

    async with storage.session_factory.begin() as session:
        session.add(
            MemoryPartitionStateRecord(
                save_id="save-wire",
                player_id="player-wire",
                memory_revision=WIRE_INTEGER_MAX,
                committed_through_day_index=0,
                updated_at_utc=datetime.now(UTC),
            )
        )

    service = EventService(storage)
    app = create_app(Settings(database_url=migrated_database_url), event_service=service)
    transport = httpx.ASGITransport(app=app, raise_app_exceptions=False)
    async with httpx.AsyncClient(transport=transport, base_url="http://testserver") as client:
        response = await client.post(
            "/api/v1/game-events/batches",
            json=_event_payload(day_index=5, request_id="request-wire-revision-full"),
        )

    assert response.status_code == 409
    assert response.json() == {"detail": "MEMORY_REVISION_EXHAUSTED"}
    assert "sqlite" not in response.text.lower()
    assert await _fact_and_memory_counts(storage) == (0, 0)
    async with storage.session_factory() as session:
        revision = await session.scalar(select(MemoryPartitionStateRecord.memory_revision))
    assert revision == WIRE_INTEGER_MAX


@pytest.mark.asyncio
@pytest.mark.parametrize(
    ("revision", "committed_day"),
    [
        (1, WIRE_INTEGER_MAX + 1),
        (1, -2),
        (-1, -1),
        (WIRE_INTEGER_MAX + 1, 0),
        (0, 0),
        (1, -1),
        (1.5, 0),
        (1, 0.5),
    ],
    ids=[
        "day-overflow",
        "day-below-minus-one",
        "negative-revision",
        "revision-overflow",
        "zero-revision-with-day",
        "positive-revision-with-no-day",
        "fractional-revision",
        "fractional-committed-day",
    ],
)
async def test_event_route_maps_corrupt_partition_state_without_partial_writes(
    storage: SqliteStorage,
    migrated_database_url: str,
    revision: int | float,
    committed_day: int | float,
) -> None:
    """腐化水位必须稳定映射 503，且不能泄漏 SQLite 细节或写入业务行。"""

    await _seed_corrupt_partition_state(
        storage,
        revision=revision,
        committed_day=committed_day,
    )
    service = EventService(storage)
    app = create_app(Settings(database_url=migrated_database_url), event_service=service)
    transport = httpx.ASGITransport(app=app, raise_app_exceptions=False)

    async with httpx.AsyncClient(transport=transport, base_url="http://testserver") as client:
        response = await client.post(
            "/api/v1/game-events/batches",
            json=_event_payload(
                day_index=5,
                request_id=f"request-corrupt-{revision}-{committed_day}",
            ),
        )

    assert response.status_code == 503
    assert response.json() == {"detail": "MEMORY_PARTITION_STATE_INVALID"}
    assert "sqlite" not in response.text.lower()
    assert "memory_partition_states" not in response.text
    assert await _fact_and_memory_counts(storage) == (0, 0)
    async with storage.session_factory() as session:
        state = await session.scalar(select(MemoryPartitionStateRecord))
    assert state is not None
    assert (state.memory_revision, state.committed_through_day_index) == (
        revision,
        committed_day,
    )
