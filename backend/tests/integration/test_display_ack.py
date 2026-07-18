"""展示 ACK 幂等、证据校验和冷却消费测试。"""

from __future__ import annotations

import asyncio

import pytest
from sqlalchemy import func, select, text

from stardew_npc_agent.event_service import (
    DisplayAckConflictError,
    DisplayAckNotAllowedError,
    EventService,
)
from stardew_npc_agent.schemas import DisplayAckRequest, GameEventBatchRequest
from stardew_npc_agent.storage import (
    DialogueDisplayReceiptRecord,
    DialogueGenerationInput,
    DialogueGenerationRecord,
    MemoryRecord,
    SqliteStorage,
)


def _event_request(
    *,
    event_id: str = "event-gift-1",
    occurred_day_index: int = 13,
) -> GameEventBatchRequest:
    """创建一条可被 generated 结果引用的 NPC 私有记忆。"""

    return GameEventBatchRequest.model_validate(
        {
            "schema_version": "1.0",
            "request_id": "request-events-1",
            "save_id": "save-1",
            "player_id": "player-1",
            "events": [
                {
                    "event_id": event_id,
                    "event_type": "gift_given",
                    "event_version": "2",
                    "occurred_day_index": occurred_day_index,
                    "source": "harmony.farmer.on_gift_given",
                    "audience_scope": "npc",
                    "audience_npc_id": "Abigail",
                    "payload": {
                        "item_id": "(O)66",
                        "taste": "love",
                    },
                }
            ],
        }
    )


def _ack(
    *,
    receipt_id: str = "receipt-1",
    displayed_day_index: int = 14,
    source_hash: str = "sha256:source-1",
) -> DisplayAckRequest:
    """构造一个分区与 source 指纹均匹配生成 fixture 的 ACK。"""

    return DisplayAckRequest.model_validate(
        {
            "schema_version": "1.0",
            "request_id": f"request-{receipt_id}-{displayed_day_index}",
            "save_id": "save-1",
            "player_id": "player-1",
            "display_receipt_id": receipt_id,
            "displayed_day_index": displayed_day_index,
            "npc_id": "Abigail",
            "source_hash": source_hash,
        }
    )


async def _memory_id(storage: SqliteStorage) -> str:
    """返回唯一投影记忆 ID；若测试前置已漂移则立即明确失败。"""

    async with storage.session_factory() as session:
        result = await session.scalar(select(MemoryRecord.memory_id))
    assert result is not None
    return result


async def _memory_id_for_event(storage: SqliteStorage, event_id: str) -> str:
    """按 event ID 读取并发轮次专属记忆，避免多轮测试误取其他行。"""

    async with storage.session_factory() as session:
        result = await session.scalar(
            select(MemoryRecord.memory_id).where(MemoryRecord.event_id == event_id)
        )
    assert result is not None
    return result


async def _save_generation(
    storage: SqliteStorage,
    *,
    generation_id: str = "generation-1",
    status: str = "generated",
    guard_passed: bool = True,
    evidence_ids: tuple[str, ...],
    game_day_index: int = 14,
    source_hash: str = "sha256:source-1",
) -> None:
    """持久化 ACK 校验必须重新核对的精确生成状态。"""

    await storage.save_dialogue_generation(
        DialogueGenerationInput(
            generation_id=generation_id,
            generation_key=f"key:{generation_id}",
            save_id="save-1",
            player_id="player-1",
            game_day_index=game_day_index,
            npc_id="Abigail",
            locale="zh-CN",
            source_hash=source_hash,
            relationship_stage="friend",
            friendship_points=750,
            memory_cooldown_days=3,
            status=status,
            result_text="增强台词" if status == "generated" else None,
            reason_code="TEST_RESULT",
            evidence_ids=evidence_ids,
            trace_id=f"trace:{generation_id}",
            guard_passed=guard_passed,
        )
    )


async def _seed_corrupt_generation(
    storage: SqliteStorage,
    *,
    generation_id: str,
    status: str,
    guard_passed: bool,
    evidence_payload: object,
    evidence_authorized: bool,
    ignore_check_constraints: bool = False,
) -> None:
    """直接写入一条不可由正常 API 创建的腐化行，仅用于验证 ACK 防御。

    默认仍遵守数据库约束，用“数据库允许但业务非法”的 unknown evidence
    验证 ACK 防腐。只有专门验证 Guard 二次防线时才显式忽略 CHECK，并在同一
    物理连接提交后立即恢复；生产代码不暴露这个测试路径。
    """

    async with storage.session_factory() as session:
        if ignore_check_constraints:
            await session.execute(text("PRAGMA ignore_check_constraints=ON"))
        try:
            session.add(
                DialogueGenerationRecord(
                    generation_id=generation_id,
                    generation_key=f"key:{generation_id}",
                    save_id="save-1",
                    player_id="player-1",
                    game_day_index=14,
                    npc_id="Abigail",
                    locale="zh-CN",
                    source_hash="sha256:source-1",
                    relationship_stage="friend",
                    friendship_points=750,
                    memory_cooldown_days=3,
                    status=status,
                    result_text="腐化生成文本" if status == "generated" else None,
                    reason_code="CORRUPT_TEST_ROW",
                    evidence_ids_json=evidence_payload,
                    trace_id=f"trace:{generation_id}",
                    guard_passed=guard_passed,
                    evidence_authorized=evidence_authorized,
                    input_versions_json=None,
                    trace_json=None,
                    usage_json=None,
                    guard_report_json=None,
                )
            )
            await session.commit()
        finally:
            if ignore_check_constraints:
                await session.execute(text("PRAGMA ignore_check_constraints=OFF"))


async def _usage_and_receipts(storage: SqliteStorage) -> tuple[int, int | None, int]:
    """读取唯一记忆的使用状态与展示回执总行数。"""

    async with storage.session_factory() as session:
        memory = await session.scalar(select(MemoryRecord))
        receipt_count = await session.scalar(
            select(func.count()).select_from(DialogueDisplayReceiptRecord)
        )
    assert memory is not None
    return memory.use_count, memory.last_used_day_index, int(receipt_count or 0)


@pytest.mark.asyncio
async def test_ack_replay_ten_times_consumes_evidence_exactly_once(
    storage: SqliteStorage,
) -> None:
    """只有首次合法 ACK 在与 receipt 同一事务中更新使用计数和冷却日。"""

    service = EventService(storage)
    await service.ingest_batch(_event_request())
    evidence_id = await _memory_id(storage)
    await _save_generation(storage, evidence_ids=(evidence_id,))

    responses = [await service.acknowledge_display("generation-1", _ack()) for _ in range(10)]

    assert [response.status for response in responses] == [
        "accepted",
        *("duplicate" for _ in range(9)),
    ]
    assert await _usage_and_receipts(storage) == (1, 14, 1)
    snapshot = await storage.get_memory_partition_snapshot("save-1", "player-1")
    assert snapshot.memory_revision == 1
    assert snapshot.retrieval_state_revision == 2


@pytest.mark.asyncio
async def test_generated_ack_without_evidence_does_not_advance_retrieval_state(
    storage: SqliteStorage,
) -> None:
    """合法无 evidence 文本可记录 receipt，但没有改变任何候选冷却状态。"""

    service = EventService(storage)
    await service.ingest_batch(_event_request())
    await _save_generation(storage, evidence_ids=())

    response = await service.acknowledge_display("generation-1", _ack())
    snapshot = await storage.get_memory_partition_snapshot("save-1", "player-1")

    assert response.status == "accepted"
    assert snapshot.memory_revision == 1
    assert snapshot.retrieval_state_revision == 1


@pytest.mark.asyncio
async def test_concurrent_ack_replay_is_fenced_by_receipt_unique_constraint(
    storage: SqliteStorage,
) -> None:
    """十个并发重放也必须只有一个请求插入回执并消费 evidence。"""

    service = EventService(storage)
    await service.ingest_batch(_event_request())
    evidence_id = await _memory_id(storage)
    await _save_generation(storage, evidence_ids=(evidence_id,))

    responses = await asyncio.gather(
        *(service.acknowledge_display("generation-1", _ack()) for _ in range(10))
    )

    assert [response.status for response in responses].count("accepted") == 1
    assert [response.status for response in responses].count("duplicate") == 9
    assert await _usage_and_receipts(storage) == (1, 14, 1)


@pytest.mark.asyncio
@pytest.mark.parametrize(
    ("generation_days", "reverse_gather"),
    [((14, 14), False), ((14, 15), True)],
    ids=["same-day", "different-day-reversed"],
)
async def test_concurrent_distinct_receipts_atomically_consume_shared_evidence(
    storage: SqliteStorage,
    generation_days: tuple[int, int],
    reverse_gather: bool,
) -> None:
    """两个首次 ACK 都必须消费一次，且较早展示日不得覆盖较晚冷却日。

    每种调度连续执行十轮真实 SQLite 并发。旧 ORM read-modify-write 会出现两张
    receipt 已提交但 ``use_count`` 只加一，或 reversed gather 把 day 15 回退为
    day 14；测试汇总全部轮次，避免偶然一次调度掩盖竞争。
    """

    service = EventService(storage)
    observed_rounds: list[tuple[int, int | None, int]] = []
    for round_index in range(10):
        event_id = f"event-concurrent-shared-{reverse_gather}-{round_index}"
        await service.ingest_batch(_event_request(event_id=event_id))
        evidence_id = await _memory_id_for_event(storage, event_id)
        generation_ids = (
            f"generation-concurrent-a-{reverse_gather}-{round_index}",
            f"generation-concurrent-b-{reverse_gather}-{round_index}",
        )
        source_hashes = (
            f"sha256:concurrent-a-{reverse_gather}-{round_index}",
            f"sha256:concurrent-b-{reverse_gather}-{round_index}",
        )
        for generation_id, game_day_index, source_hash in zip(
            generation_ids,
            generation_days,
            source_hashes,
            strict=True,
        ):
            await _save_generation(
                storage,
                generation_id=generation_id,
                evidence_ids=(evidence_id,),
                game_day_index=game_day_index,
                source_hash=source_hash,
            )

        calls = [
            service.acknowledge_display(
                generation_id,
                _ack(
                    receipt_id=f"receipt-{generation_id}",
                    displayed_day_index=game_day_index,
                    source_hash=source_hash,
                ),
            )
            for generation_id, game_day_index, source_hash in zip(
                generation_ids,
                generation_days,
                source_hashes,
                strict=True,
            )
        ]
        if reverse_gather:
            calls.reverse()
        responses = await asyncio.gather(*calls)
        assert [response.status for response in responses] == ["accepted", "accepted"]

        async with storage.session_factory() as session:
            memory = await session.scalar(
                select(MemoryRecord).where(MemoryRecord.memory_id == evidence_id)
            )
            receipt_count = await session.scalar(
                select(func.count())
                .select_from(DialogueDisplayReceiptRecord)
                .where(DialogueDisplayReceiptRecord.generation_id.in_(generation_ids))
            )
        assert memory is not None
        observed_rounds.append(
            (
                memory.use_count,
                memory.last_used_day_index,
                int(receipt_count or 0),
            )
        )

    assert observed_rounds == [(2, max(generation_days), 2) for _ in range(10)]


@pytest.mark.asyncio
@pytest.mark.parametrize("status", ["passthrough", "skipped", "failed"])
async def test_non_generated_status_never_consumes_memory(
    storage: SqliteStorage,
    status: str,
) -> None:
    """数据库合法的非 generated 终态也绝不能被 ACK 当作展示结果消费。"""

    service = EventService(storage)
    await service.ingest_batch(_event_request())
    await _save_generation(
        storage,
        generation_id="generation-1",
        status=status,
        evidence_ids=(),
    )

    with pytest.raises(DisplayAckNotAllowedError, match="generated"):
        await service.acknowledge_display("generation-1", _ack())

    assert await _usage_and_receipts(storage) == (0, None, 0)


@pytest.mark.asyncio
async def test_guard_failure_or_unknown_evidence_rejects_ack_without_partial_receipt(
    storage: SqliteStorage,
) -> None:
    """未通过 Guard 或无法回溯记忆的 generated 记录不得产生部分消费。"""

    service = EventService(storage)
    await service.ingest_batch(_event_request())
    evidence_id = await _memory_id(storage)
    await _seed_corrupt_generation(
        storage,
        generation_id="generation-guard-failed",
        status="generated",
        guard_passed=False,
        evidence_payload=[evidence_id],
        evidence_authorized=False,
        ignore_check_constraints=True,
    )
    await _seed_corrupt_generation(
        storage,
        generation_id="generation-unknown-evidence",
        status="generated",
        guard_passed=True,
        evidence_payload=["memory:does-not-exist"],
        evidence_authorized=True,
    )

    with pytest.raises(DisplayAckNotAllowedError, match="Guard"):
        await service.acknowledge_display("generation-guard-failed", _ack(receipt_id="r1"))
    with pytest.raises(DisplayAckNotAllowedError, match="evidence"):
        await service.acknowledge_display(
            "generation-unknown-evidence",
            _ack(receipt_id="r2"),
        )

    assert await _usage_and_receipts(storage) == (0, None, 0)


@pytest.mark.asyncio
@pytest.mark.parametrize(
    "corruption_case",
    [
        "dict-item",
        "root-dict",
        "blank",
        "edge-whitespace",
        "nul",
        "duplicate",
        "unknown",
    ],
)
async def test_ack_rejects_corrupt_evidence_shape_without_partial_writes(
    storage: SqliteStorage,
    corruption_case: str,
) -> None:
    """腐化 JSON 必须稳定拒绝，不能在 set/SQL 中产生 TypeError 或部分消费。"""

    service = EventService(storage)
    await service.ingest_batch(_event_request())
    evidence_id = await _memory_id(storage)
    evidence_payload_by_case: dict[str, object] = {
        "dict-item": [{"not": "an id"}],
        "root-dict": {"not": "a list"},
        "blank": [""],
        "edge-whitespace": [" padded"],
        "nul": ["memory:\x00bad"],
        "duplicate": [evidence_id, evidence_id],
        "unknown": ["memory:does-not-exist"],
    }
    expected_message = (
        "generation evidence ID 不得重复"
        if corruption_case == "duplicate"
        else (
            "generation evidence 无法完整回溯到当前分区"
            if corruption_case == "unknown"
            else "generation evidence ID 格式非法"
        )
    )
    await _seed_corrupt_generation(
        storage,
        generation_id=f"generation-corrupt-{corruption_case}",
        status="generated",
        guard_passed=True,
        evidence_payload=evidence_payload_by_case[corruption_case],
        evidence_authorized=True,
        # ORM/migration 已正确拒绝 root object；这里只模拟磁盘腐化后 ACK
        # 仍需 fail closed，不因此放宽生产 CHECK。
        ignore_check_constraints=corruption_case == "root-dict",
    )

    with pytest.raises(DisplayAckNotAllowedError) as error_info:
        await service.acknowledge_display(
            f"generation-corrupt-{corruption_case}",
            _ack(receipt_id=f"receipt-corrupt-{corruption_case}"),
        )

    assert str(error_info.value) == expected_message
    assert await _usage_and_receipts(storage) == (0, None, 0)


@pytest.mark.asyncio
@pytest.mark.parametrize("displayed_day_index", [15, 10_000], ids=["next-day", "far-future"])
async def test_ack_rejects_display_day_different_from_generation_day(
    storage: SqliteStorage,
    displayed_day_index: int,
) -> None:
    """延迟 outbox 上传仍携带实际展示日，不能把未来日冒充生成日消费冷却。"""

    service = EventService(storage)
    await service.ingest_batch(_event_request())
    evidence_id = await _memory_id(storage)
    await _save_generation(storage, evidence_ids=(evidence_id,))

    with pytest.raises(DisplayAckNotAllowedError) as error_info:
        await service.acknowledge_display(
            "generation-1",
            _ack(
                receipt_id=f"receipt-day-{displayed_day_index}",
                displayed_day_index=displayed_day_index,
            ),
        )

    assert str(error_info.value) == "ACK 展示日必须等于 generation 游戏日"
    assert await _usage_and_receipts(storage) == (0, None, 0)


@pytest.mark.asyncio
async def test_reusing_receipt_id_with_different_payload_is_conflict_not_duplicate(
    storage: SqliteStorage,
) -> None:
    """回执 ID 只对完全相同的重放幂等，不能隐藏展示日或来源变更。"""

    service = EventService(storage)
    await service.ingest_batch(_event_request())
    evidence_id = await _memory_id(storage)
    await _save_generation(storage, evidence_ids=(evidence_id,))
    await service.acknowledge_display("generation-1", _ack())

    with pytest.raises(DisplayAckConflictError):
        await service.acknowledge_display(
            "generation-1",
            _ack(displayed_day_index=15),
        )

    assert await _usage_and_receipts(storage) == (1, 14, 1)
