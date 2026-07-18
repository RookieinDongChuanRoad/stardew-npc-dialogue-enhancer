"""生成终态的 evidence 授权快照与严格输入不变量测试。"""

from __future__ import annotations

from datetime import UTC, datetime
from typing import Any, cast

import pytest
from sqlalchemy import func, select

from stardew_npc_agent.event_service import EventService
from stardew_npc_agent.schemas import DisplayAckRequest
from stardew_npc_agent.storage import (
    DialogueDisplayReceiptRecord,
    DialogueGenerationInput,
    DialogueGenerationRecord,
    InvalidDialogueGenerationError,
    MemoryPartitionStateRecord,
    MemoryRecord,
    SqliteStorage,
)


def _memory(
    *,
    memory_id: str = "memory:authorized",
    save_id: str = "save-auth",
    player_id: str = "player-auth",
    audience_scope: str = "public",
    audience_npc_id: str | None = None,
    occurred_day_index: int = 13,
    expires_day_index: int | None = None,
    last_used_day_index: int | None = None,
    relationship_stages: list[str] | None = None,
    min_friendship_points: int | None = None,
    max_friendship_points: int | None = None,
) -> MemoryRecord:
    """构造一条可独立变更授权条件的记忆行。"""

    return MemoryRecord(
        memory_id=memory_id,
        event_id=f"event:{memory_id}",
        save_id=save_id,
        player_id=player_id,
        audience_scope=audience_scope,
        audience_npc_id=audience_npc_id,
        event_type="world_progression",
        event_version="1",
        source="test",
        payload_json={"fixture": memory_id},
        summary=f"summary:{memory_id}",
        tags_json=["authorization"],
        importance=0.8,
        occurred_day_index=occurred_day_index,
        expires_day_index=expires_day_index,
        last_used_day_index=last_used_day_index,
        use_count=0,
        relationship_stages_json=relationship_stages or [],
        min_friendship_points=min_friendship_points,
        max_friendship_points=max_friendship_points,
        created_at_utc=datetime.now(UTC),
    )


def _generation(
    *,
    generation_id: str = "generation-auth",
    evidence_ids: tuple[str, ...] = ("memory:authorized",),
    status: str = "generated",
    result_text: str | None = "通过授权的增强台词。",
    guard_passed: bool = True,
    relationship_stage: str = "friend",
    friendship_points: int = 750,
    memory_cooldown_days: int = 3,
    evidence_authorized: bool | None = None,
) -> DialogueGenerationInput:
    """构造一个由 storage 负责授权 evidence 的生成终态输入。"""

    return DialogueGenerationInput(
        generation_id=generation_id,
        generation_key=f"key:{generation_id}",
        save_id="save-auth",
        player_id="player-auth",
        game_day_index=14,
        npc_id="Abigail",
        locale="zh-CN",
        source_hash="sha256:auth-source",
        relationship_stage=relationship_stage,
        friendship_points=friendship_points,
        memory_cooldown_days=memory_cooldown_days,
        status=status,
        result_text=result_text,
        reason_code="TEST_RESULT",
        evidence_ids=evidence_ids,
        trace_id=f"trace:{generation_id}",
        guard_passed=guard_passed,
        evidence_authorized=evidence_authorized,
    )


async def _seed_memory(storage: SqliteStorage, memory: MemoryRecord) -> None:
    """在一个短事务中写入授权测试记忆及其不可缺失的分区水位。"""

    async with storage.session_factory.begin() as session:
        session.add(memory)
        session.add(
            MemoryPartitionStateRecord(
                save_id=memory.save_id,
                player_id=memory.player_id,
                memory_revision=1,
                retrieval_state_revision=1,
                committed_through_day_index=memory.occurred_day_index,
            )
        )


async def _counts(storage: SqliteStorage) -> tuple[int, int]:
    """返回 generation 与 receipt 行数，用于确认拒绝时无部分写入。"""

    async with storage.session_factory() as session:
        generation_count = await session.scalar(
            select(func.count()).select_from(DialogueGenerationRecord)
        )
        receipt_count = await session.scalar(
            select(func.count()).select_from(DialogueDisplayReceiptRecord)
        )
    return int(generation_count or 0), int(receipt_count or 0)


@pytest.mark.asyncio
@pytest.mark.parametrize(
    "memory",
    [
        _memory(relationship_stages=["spouse"]),
        _memory(min_friendship_points=9_999),
        _memory(last_used_day_index=13),
        _memory(occurred_day_index=14),
        _memory(expires_day_index=12),
        _memory(audience_scope="npc", audience_npc_id="Sebastian"),
        _memory(save_id="other-save"),
    ],
    ids=[
        "spouse-only",
        "min-9999",
        "in-cooldown",
        "same-day",
        "expired",
        "wrong-npc",
        "wrong-partition",
    ],
)
async def test_invalid_evidence_is_rejected_when_generation_is_saved(
    storage: SqliteStorage,
    memory: MemoryRecord,
) -> None:
    """分区、可见性、截止日、过期、关系和当时冷却都必须在保存短事务内授权。"""

    await _seed_memory(storage, memory)

    with pytest.raises(InvalidDialogueGenerationError, match="evidence"):
        await storage.save_dialogue_generation(_generation())

    assert await _counts(storage) == (0, 0)
    async with storage.session_factory() as session:
        persisted_memory = await session.scalar(select(MemoryRecord))
    assert persisted_memory is not None
    assert (persisted_memory.use_count, persisted_memory.last_used_day_index) == (
        0,
        memory.last_used_day_index,
    )


@pytest.mark.asyncio
async def test_valid_generation_persists_typed_authorization_snapshot(
    storage: SqliteStorage,
) -> None:
    """授权成功时必须持久化关系快照、冷却配置与 computed true flag。"""

    await _seed_memory(storage, _memory())

    await storage.save_dialogue_generation(_generation())

    async with storage.session_factory() as session:
        generation = await session.scalar(select(DialogueGenerationRecord))
    assert generation is not None
    assert generation.relationship_stage == "friend"
    assert generation.friendship_points == 750
    assert generation.memory_cooldown_days == 3
    assert generation.evidence_authorized is True


@pytest.mark.asyncio
async def test_generated_without_evidence_is_authorized_without_memory_rows(
    storage: SqliteStorage,
) -> None:
    """generated 可以只使用 mandatory context，空 evidence 集合仍是合法授权快照。"""

    await storage.save_dialogue_generation(_generation(evidence_ids=()))

    async with storage.session_factory() as session:
        generation = await session.scalar(select(DialogueGenerationRecord))
    assert generation is not None
    assert generation.evidence_ids_json == []
    assert generation.evidence_authorized is True


@pytest.mark.asyncio
async def test_ack_trusts_saved_authorization_after_another_display_changes_cooldown(
    storage: SqliteStorage,
) -> None:
    """ACK 不得用已变化的 last_used 重算历史授权，否则同日预生成会不确定。"""

    await _seed_memory(storage, _memory())
    await storage.save_dialogue_generation(_generation(generation_id="generation-auth-1"))
    await storage.save_dialogue_generation(_generation(generation_id="generation-auth-2"))
    service = EventService(storage)

    async def acknowledge(generation_id: str, receipt_id: str) -> None:
        response = await service.acknowledge_display(
            generation_id,
            DisplayAckRequest(
                schema_version="1.0",
                request_id=f"request:{receipt_id}",
                save_id="save-auth",
                player_id="player-auth",
                display_receipt_id=receipt_id,
                displayed_day_index=14,
                npc_id="Abigail",
                source_hash="sha256:auth-source",
            ),
        )
        assert response.status == "accepted"

    await acknowledge("generation-auth-1", "receipt-auth-1")
    await acknowledge("generation-auth-2", "receipt-auth-2")

    async with storage.session_factory() as session:
        memory = await session.scalar(select(MemoryRecord))
    assert memory is not None
    assert (memory.use_count, memory.last_used_day_index) == (2, 14)
    snapshot = await storage.get_memory_partition_snapshot("save-auth", "player-auth")
    assert snapshot.memory_revision == 1
    assert snapshot.retrieval_state_revision == 3


@pytest.mark.asyncio
@pytest.mark.parametrize(
    "invalid_text",
    [
        None,
        "",
        "   ",
        " padded",
        "padded ",
        "\tpadded",
        "padded\r\n",
        "\x00",
        "\x00abc",
        "abc\x00",
    ],
    ids=[
        "null",
        "empty",
        "blank",
        "leading-space",
        "trailing-space",
        "tab",
        "crlf",
        "nul-only",
        "nul-prefix",
        "nul-suffix",
    ],
)
async def test_generated_text_is_strict_and_never_trimmed(
    storage: SqliteStorage,
    invalid_text: str | None,
) -> None:
    """generated 文本为空或含边缘空白时必须拒绝，不得 strip 后保存。"""

    with pytest.raises(InvalidDialogueGenerationError) as error_info:
        await storage.save_dialogue_generation(
            _generation(evidence_ids=(), result_text=invalid_text)
        )

    if isinstance(invalid_text, str) and "\x00" in invalid_text:
        # 精确匹配稳定类别，同时证明异常不会拼接可能包含模型输出的原文本。
        assert str(error_info.value) == "generated 文本不得包含 NUL 控制字符"

    assert await _counts(storage) == (0, 0)


@pytest.mark.asyncio
@pytest.mark.parametrize(
    ("invalid_evidence_ids", "expected_message"),
    [
        ({"not": "a list"}, "generation evidence ID 格式非法"),
        (({"not": "an id"},), "generation evidence ID 格式非法"),
        (("",), "generation evidence ID 格式非法"),
        ((" leading",), "generation evidence ID 格式非法"),
        (("trailing ",), "generation evidence ID 格式非法"),
        (("memory:\x00bad",), "generation evidence ID 格式非法"),
        (
            ("memory:duplicate", "memory:duplicate"),
            "generation evidence ID 不得重复",
        ),
    ],
    ids=[
        "root-dict",
        "dict-item",
        "blank",
        "leading-space",
        "trailing-space",
        "nul",
        "duplicate",
    ],
)
async def test_generation_save_rejects_malformed_evidence_before_database_work(
    storage: SqliteStorage,
    invalid_evidence_ids: object,
    expected_message: str,
) -> None:
    """正常保存边界必须先验证 evidence 容器与 ID，再执行 set 或 SQL。"""

    with pytest.raises(InvalidDialogueGenerationError) as error_info:
        await storage.save_dialogue_generation(
            _generation(evidence_ids=cast(Any, invalid_evidence_ids))
        )

    assert str(error_info.value) == expected_message
    assert await _counts(storage) == (0, 0)


@pytest.mark.asyncio
async def test_generated_guard_failure_and_caller_forged_authorization_are_rejected(
    storage: SqliteStorage,
) -> None:
    """generated 正常保存 API 不得接受 Guard 失败或调用方自报授权。"""

    with pytest.raises(InvalidDialogueGenerationError, match="Guard"):
        await storage.save_dialogue_generation(_generation(evidence_ids=(), guard_passed=False))
    with pytest.raises(InvalidDialogueGenerationError, match="authorized"):
        await storage.save_dialogue_generation(
            _generation(evidence_ids=(), evidence_authorized=True)
        )

    assert await _counts(storage) == (0, 0)
