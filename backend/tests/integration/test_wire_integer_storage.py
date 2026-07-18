"""绕过 Pydantic 后，存储入口仍须守住 Int32 wire integer 范围。"""

from __future__ import annotations

from dataclasses import replace
from datetime import UTC, datetime
from typing import Any, cast

import pytest
from sqlalchemy import event as sqlalchemy_event
from sqlalchemy import func, select, text

from stardew_npc_agent.event_service import project_event_to_memory
from stardew_npc_agent.schemas import GameEvent
from stardew_npc_agent.storage import (
    DialogueDisplayReceiptRecord,
    DialogueGenerationInput,
    DialogueGenerationRecord,
    DisplayNotAllowedStorageError,
    DisplayReceiptInput,
    GameEventRecord,
    InvalidDialogueGenerationError,
    InvalidMemoryQueryError,
    MemoryPartitionStateRecord,
    MemoryRecord,
    MemoryRevisionExhaustedStorageError,
    MemorySearchQuery,
    PreparedEvent,
    SqliteStorage,
)

WIRE_INTEGER_MIN = -2_147_483_648
WIRE_INTEGER_MAX = 2_147_483_647


def _prepared_event(
    day_index: int = 5,
    *,
    event_id: str = "event-wire-storage",
) -> PreparedEvent:
    """以同一日构造事实和投影，再允许反例只替换外层运行时字段。"""

    event = GameEvent.model_validate(
        {
            "event_id": event_id,
            "event_type": "world_progression",
            "event_version": "1",
            "occurred_day_index": day_index,
            "source": "smapi.world.public_facility_restored",
            "audience_scope": "public",
            "audience_npc_id": None,
            "payload": {"milestone": "public_facility_minecarts_restored"},
        }
    )
    projection = project_event_to_memory("save-wire", "player-wire", event)
    return PreparedEvent(
        save_id="save-wire",
        player_id="player-wire",
        event_id=event.event_id,
        event_type=event.event_type,
        event_version=event.event_version,
        occurred_day_index=day_index,
        source=event.source,
        audience_scope=event.audience_scope,
        audience_npc_id=event.audience_npc_id,
        payload_json=dict(event.payload),
        projection=projection,
    )


def _generation(**overrides: object) -> DialogueGenerationInput:
    """返回不依赖 evidence 的合法 generated 保存输入。"""

    values: dict[str, object] = {
        "generation_id": "generation-wire-storage",
        "generation_key": "generation-key-wire-storage",
        "save_id": "save-wire",
        "player_id": "player-wire",
        "game_day_index": 10,
        "npc_id": "Abigail",
        "locale": "zh-CN",
        "source_hash": "sha256:wire-storage",
        "relationship_stage": "friend",
        "friendship_points": 750,
        "memory_cooldown_days": 3,
        "status": "generated",
        "result_text": "通过存储边界测试的增强台词。",
        "reason_code": "TEST_GENERATED",
        "evidence_ids": (),
        "trace_id": "trace-wire-storage",
        "guard_passed": True,
    }
    values.update(overrides)
    return DialogueGenerationInput(**values)  # type: ignore[arg-type]


async def _storage_counts(storage: SqliteStorage) -> tuple[int, int, int]:
    """统计三个受测写表，证明越界输入在任何部分写入前失败。"""

    async with storage.session_factory() as session:
        event_count = await session.scalar(select(func.count()).select_from(GameEventRecord))
        generation_count = await session.scalar(
            select(func.count()).select_from(DialogueGenerationRecord)
        )
        receipt_count = await session.scalar(
            select(func.count()).select_from(DialogueDisplayReceiptRecord)
        )
    return (
        int(event_count or 0),
        int(generation_count or 0),
        int(receipt_count or 0),
    )


async def _event_and_memory_counts(storage: SqliteStorage) -> tuple[int, int]:
    """读取事实与确定性记忆行数，专门证明 revision 溢出没有部分提交。"""

    async with storage.session_factory() as session:
        event_count = await session.scalar(select(func.count()).select_from(GameEventRecord))
        memory_count = await session.scalar(select(func.count()).select_from(MemoryRecord))
    return int(event_count or 0), int(memory_count or 0)


async def _set_partition_revision(storage: SqliteStorage, revision: int) -> None:
    """建立或更新测试分区水位，不创建事实或记忆行。"""

    async with storage.session_factory.begin() as session:
        state = await session.scalar(select(MemoryPartitionStateRecord))
        if state is None:
            session.add(
                MemoryPartitionStateRecord(
                    save_id="save-wire",
                    player_id="player-wire",
                    memory_revision=revision,
                    committed_through_day_index=-1 if revision == 0 else 0,
                    updated_at_utc=datetime.now(UTC),
                )
            )
        else:
            state.memory_revision = revision
            if revision > 0 and state.committed_through_day_index < 0:
                state.committed_through_day_index = 0
            state.updated_at_utc = datetime.now(UTC)


async def _set_retrieval_state_revision(storage: SqliteStorage, revision: int) -> None:
    """只调整候选状态水位，保留 memory revision 与 committed day。"""

    async with storage.session_factory.begin() as session:
        state = await session.scalar(select(MemoryPartitionStateRecord))
        if state is None:
            session.add(
                MemoryPartitionStateRecord(
                    save_id="save-wire",
                    player_id="player-wire",
                    memory_revision=1,
                    retrieval_state_revision=revision,
                    committed_through_day_index=0,
                    updated_at_utc=datetime.now(UTC),
                )
            )
        else:
            state.retrieval_state_revision = revision
            state.updated_at_utc = datetime.now(UTC)


async def _seed_corrupt_partition_state(
    storage: SqliteStorage,
    *,
    revision: int | float,
    committed_day: int | float,
) -> None:
    """绕过 CHECK 注入外部腐化水位，并立即恢复连接级约束开关。"""

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


async def _partition_revision(storage: SqliteStorage) -> int:
    """读取唯一测试分区的 revision，并在 fixture 漂移时明确失败。"""

    async with storage.session_factory() as session:
        revision = await session.scalar(select(MemoryPartitionStateRecord.memory_revision))
    assert revision is not None
    return revision


@pytest.mark.asyncio
@pytest.mark.parametrize(
    "invalid_day",
    [WIRE_INTEGER_MAX + 1, -1, pytest.param(True, id="bool")],
)
async def test_event_storage_rejects_invalid_prepared_day_before_writes(
    storage: SqliteStorage,
    invalid_day: object,
) -> None:
    """内部调用者不能用 dataclass 绕过事件日的严格类型与闭区间。"""

    event = replace(_prepared_event(), occurred_day_index=cast(Any, invalid_day))

    with pytest.raises(ValueError, match="occurred_day_index"):
        await storage.ingest_events("save-wire", "player-wire", [event])

    assert await _storage_counts(storage) == (0, 0, 0)


@pytest.mark.asyncio
@pytest.mark.parametrize(
    "projection_day",
    [WIRE_INTEGER_MAX + 1, 6],
    ids=["projection-overflow", "event-projection-mismatch"],
)
async def test_event_storage_rejects_invalid_or_mismatched_projection_day(
    storage: SqliteStorage,
    projection_day: int,
) -> None:
    """嵌套投影日同样进入 SQLite，且必须逐字继承已验证的事件日。"""

    event = _prepared_event(day_index=5)
    event = replace(
        event,
        projection=replace(event.projection, occurred_day_index=projection_day),
    )

    with pytest.raises(ValueError, match="projection.occurred_day_index"):
        await storage.ingest_events("save-wire", "player-wire", [event])

    assert await _storage_counts(storage) == (0, 0, 0)


@pytest.mark.asyncio
@pytest.mark.parametrize(
    ("field_name", "invalid_value"),
    [
        ("expires_day_index", WIRE_INTEGER_MAX + 1),
        ("expires_day_index", -1),
        ("expires_day_index", True),
        ("min_friendship_points", WIRE_INTEGER_MIN - 1),
        ("min_friendship_points", WIRE_INTEGER_MAX + 1),
        ("min_friendship_points", True),
        ("max_friendship_points", WIRE_INTEGER_MIN - 1),
        ("max_friendship_points", WIRE_INTEGER_MAX + 1),
        ("max_friendship_points", True),
    ],
)
async def test_event_storage_rejects_invalid_projection_optional_integer_fields(
    storage: SqliteStorage,
    field_name: str,
    invalid_value: object,
) -> None:
    """可选投影日/好感阈值也必须在打开事务前满足严格 Int32 合同。"""

    event = _prepared_event()
    event = replace(
        event,
        projection=replace(event.projection, **{field_name: invalid_value}),
    )

    with pytest.raises(ValueError, match=field_name):
        await storage.ingest_events("save-wire", "player-wire", [event])

    assert await _storage_counts(storage) == (0, 0, 0)


@pytest.mark.asyncio
async def test_event_storage_rejects_inverted_projection_friendship_range(
    storage: SqliteStorage,
) -> None:
    """同时存在的投影好感上下限必须形成非空闭区间。"""

    event = _prepared_event()
    event = replace(
        event,
        projection=replace(
            event.projection,
            min_friendship_points=101,
            max_friendship_points=100,
        ),
    )

    with pytest.raises(ValueError, match="min_friendship_points"):
        await storage.ingest_events("save-wire", "player-wire", [event])

    assert await _storage_counts(storage) == (0, 0, 0)


@pytest.mark.asyncio
async def test_event_storage_accepts_projection_optional_integer_boundaries(
    storage: SqliteStorage,
) -> None:
    """合法端点不能被纵深校验误拒绝，且必须原样持久化。"""

    event = _prepared_event(day_index=WIRE_INTEGER_MAX)
    event = replace(
        event,
        projection=replace(
            event.projection,
            expires_day_index=WIRE_INTEGER_MAX,
            min_friendship_points=WIRE_INTEGER_MIN,
            max_friendship_points=WIRE_INTEGER_MAX,
        ),
    )

    await storage.ingest_events("save-wire", "player-wire", [event])

    async with storage.session_factory() as session:
        memory = await session.scalar(select(MemoryRecord))
    assert memory is not None
    assert (
        memory.expires_day_index,
        memory.min_friendship_points,
        memory.max_friendship_points,
    ) == (WIRE_INTEGER_MAX, WIRE_INTEGER_MIN, WIRE_INTEGER_MAX)


@pytest.mark.asyncio
async def test_event_storage_accepts_exact_int32_maximum(
    storage: SqliteStorage,
) -> None:
    """纵深校验不能把合法上界误写成排他上限。"""

    event = _prepared_event(day_index=WIRE_INTEGER_MAX)

    result = await storage.ingest_events("save-wire", "player-wire", [event])

    assert result.committed_through_day_index == WIRE_INTEGER_MAX
    assert await _storage_counts(storage) == (1, 0, 0)


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
async def test_corrupt_partition_state_is_rejected_before_any_business_dml(
    storage: SqliteStorage,
    revision: int | float,
    committed_day: int | float,
) -> None:
    """外部腐化水位不能被修复、推进或误报为正常 revision exhausted。"""

    await _seed_corrupt_partition_state(
        storage,
        revision=revision,
        committed_day=committed_day,
    )
    dml_statements: list[str] = []

    def capture_dml(
        _connection: object,
        _cursor: object,
        statement: str,
        _parameters: object,
        _context: object,
        _executemany: bool,
    ) -> None:
        normalized = statement.lstrip().upper()
        if normalized.startswith(("INSERT", "UPDATE", "DELETE")):
            dml_statements.append(normalized)

    sqlalchemy_event.listen(storage.engine.sync_engine, "before_cursor_execute", capture_dml)
    try:
        with pytest.raises(RuntimeError, match="memory partition state invalid"):
            await storage.ingest_events(
                "save-wire",
                "player-wire",
                [_prepared_event()],
            )
    finally:
        sqlalchemy_event.remove(
            storage.engine.sync_engine,
            "before_cursor_execute",
            capture_dml,
        )

    assert dml_statements == []
    assert await _event_and_memory_counts(storage) == (0, 0)
    async with storage.session_factory() as session:
        state = await session.scalar(select(MemoryPartitionStateRecord))
    assert state is not None
    assert (state.memory_revision, state.committed_through_day_index) == (
        revision,
        committed_day,
    )


@pytest.mark.asyncio
async def test_event_storage_rejects_revision_overflow_before_any_dml(
    storage: SqliteStorage,
) -> None:
    """max 水位再提交新事实必须在 event/memory/revision DML 之前稳定失败。"""

    await _set_partition_revision(storage, WIRE_INTEGER_MAX)
    dml_statements: list[str] = []

    def capture_dml(
        _connection: object,
        _cursor: object,
        statement: str,
        _parameters: object,
        _context: object,
        _executemany: bool,
    ) -> None:
        """只记录可能改变业务数据的 SQL；BEGIN IMMEDIATE 不算业务 DML。"""

        normalized = statement.lstrip().upper()
        if normalized.startswith(("INSERT", "UPDATE", "DELETE")):
            dml_statements.append(normalized)

    sqlalchemy_event.listen(
        storage.engine.sync_engine,
        "before_cursor_execute",
        capture_dml,
    )
    try:
        with pytest.raises(MemoryRevisionExhaustedStorageError) as error_info:
            await storage.ingest_events(
                "save-wire",
                "player-wire",
                [_prepared_event()],
            )
    finally:
        sqlalchemy_event.remove(
            storage.engine.sync_engine,
            "before_cursor_execute",
            capture_dml,
        )

    assert str(error_info.value) == "memory revision 已达到 Int32 上限"
    assert dml_statements == []
    assert await _event_and_memory_counts(storage) == (0, 0)
    assert await _partition_revision(storage) == WIRE_INTEGER_MAX


@pytest.mark.asyncio
async def test_event_storage_rejects_retrieval_revision_overflow_before_fact_write(
    storage: SqliteStorage,
) -> None:
    """即使 memory revision 尚有空间，新事实也不能让 retrieval 水位溢出。"""

    await _set_retrieval_state_revision(storage, WIRE_INTEGER_MAX)

    with pytest.raises(MemoryRevisionExhaustedStorageError):
        await storage.ingest_events("save-wire", "player-wire", [_prepared_event()])

    assert await _event_and_memory_counts(storage) == (0, 0)
    snapshot = await storage.get_memory_partition_snapshot("save-wire", "player-wire")
    assert snapshot.memory_revision == 1
    assert snapshot.retrieval_state_revision == WIRE_INTEGER_MAX


@pytest.mark.asyncio
async def test_display_ack_rejects_retrieval_revision_overflow_without_consumption(
    storage: SqliteStorage,
) -> None:
    """候选水位已满时，ACK 不得先写 receipt 或更新 use_count。"""

    event = _prepared_event()
    await storage.ingest_events("save-wire", "player-wire", [event])
    async with storage.session_factory() as session:
        evidence_id = await session.scalar(select(MemoryRecord.memory_id))
    assert evidence_id is not None
    await storage.save_dialogue_generation(_generation(evidence_ids=(evidence_id,)))
    await _set_retrieval_state_revision(storage, WIRE_INTEGER_MAX)

    with pytest.raises(MemoryRevisionExhaustedStorageError):
        await storage.acknowledge_display(
            "generation-wire-storage",
            DisplayReceiptInput(
                display_receipt_id="receipt-retrieval-overflow",
                save_id="save-wire",
                player_id="player-wire",
                displayed_day_index=10,
                npc_id="Abigail",
                source_hash="sha256:wire-storage",
            ),
        )

    async with storage.session_factory() as session:
        memory = await session.scalar(select(MemoryRecord))
        receipt_count = await session.scalar(
            select(func.count()).select_from(DialogueDisplayReceiptRecord)
        )
    assert memory is not None
    assert (memory.use_count, memory.last_used_day_index) == (0, None)
    assert int(receipt_count or 0) == 0


@pytest.mark.asyncio
async def test_event_storage_rejects_batch_that_would_cross_revision_maximum(
    storage: SqliteStorage,
) -> None:
    """预检必须按真实新增 event ID 计数，不能先写第一条再发现第二条溢出。"""

    await _set_partition_revision(storage, WIRE_INTEGER_MAX - 1)
    events = [
        _prepared_event(event_id="event-wire-revision-a"),
        _prepared_event(event_id="event-wire-revision-b"),
    ]

    with pytest.raises(MemoryRevisionExhaustedStorageError):
        await storage.ingest_events("save-wire", "player-wire", events)

    assert await _event_and_memory_counts(storage) == (0, 0)
    assert await _partition_revision(storage) == WIRE_INTEGER_MAX - 1


@pytest.mark.asyncio
async def test_event_storage_can_advance_revision_exactly_to_int32_maximum(
    storage: SqliteStorage,
) -> None:
    """边界预检必须允许 ``max-1 + 1 == max``，不能出现 off-by-one。"""

    await _set_partition_revision(storage, WIRE_INTEGER_MAX - 1)

    result = await storage.ingest_events(
        "save-wire",
        "player-wire",
        [_prepared_event()],
    )

    assert result.memory_revision == WIRE_INTEGER_MAX
    assert await _event_and_memory_counts(storage) == (1, 1)
    assert await _partition_revision(storage) == WIRE_INTEGER_MAX


@pytest.mark.asyncio
async def test_event_storage_allows_duplicate_replay_when_revision_is_at_maximum(
    storage: SqliteStorage,
) -> None:
    """max 水位只禁止新增事实；纯幂等重放仍应返回 duplicate 和现有水位。"""

    event = _prepared_event()
    await storage.ingest_events("save-wire", "player-wire", [event])
    await _set_partition_revision(storage, WIRE_INTEGER_MAX)

    result = await storage.ingest_events("save-wire", "player-wire", [event])

    assert result.items[0].status == "duplicate"
    assert result.memory_revision == WIRE_INTEGER_MAX
    assert await _event_and_memory_counts(storage) == (1, 1)


@pytest.mark.asyncio
@pytest.mark.parametrize(
    ("field_name", "invalid_value"),
    [
        ("game_day_index", WIRE_INTEGER_MAX + 1),
        ("game_day_index", -1),
        ("game_day_index", True),
        ("friendship_points", WIRE_INTEGER_MIN - 1),
        ("friendship_points", WIRE_INTEGER_MAX + 1),
        ("friendship_points", True),
        ("memory_cooldown_days", WIRE_INTEGER_MAX + 1),
        ("memory_cooldown_days", -1),
        ("memory_cooldown_days", True),
    ],
)
async def test_generation_storage_rejects_invalid_wire_integer_before_writes(
    storage: SqliteStorage,
    field_name: str,
    invalid_value: object,
) -> None:
    """生成快照中进入 SQLite 的整数必须逐字段拒绝越界和 bool。"""

    with pytest.raises(InvalidDialogueGenerationError, match=field_name):
        await storage.save_dialogue_generation(_generation(**{field_name: invalid_value}))

    assert await _storage_counts(storage) == (0, 0, 0)


@pytest.mark.asyncio
@pytest.mark.parametrize("friendship_points", [WIRE_INTEGER_MIN, WIRE_INTEGER_MAX])
async def test_generation_storage_accepts_exact_int32_boundaries(
    storage: SqliteStorage,
    friendship_points: int,
) -> None:
    """完整 Int32 好感范围及非负日/冷却上界都必须可保存。"""

    await storage.save_dialogue_generation(
        _generation(
            game_day_index=WIRE_INTEGER_MAX,
            friendship_points=friendship_points,
            memory_cooldown_days=WIRE_INTEGER_MAX,
        )
    )

    assert await _storage_counts(storage) == (0, 1, 0)


@pytest.mark.asyncio
@pytest.mark.parametrize(
    "invalid_displayed_day",
    [WIRE_INTEGER_MAX + 1, -1, pytest.param(True, id="bool")],
)
async def test_ack_storage_rejects_invalid_displayed_day_without_receipt(
    storage: SqliteStorage,
    invalid_displayed_day: object,
) -> None:
    """ACK 纵深边界应先报告 wire day 非法，而不是依赖 SQLite 或日期碰撞。"""

    await storage.save_dialogue_generation(_generation(game_day_index=WIRE_INTEGER_MAX))
    receipt = DisplayReceiptInput(
        display_receipt_id="receipt-wire-storage",
        save_id="save-wire",
        player_id="player-wire",
        displayed_day_index=cast(Any, invalid_displayed_day),
        npc_id="Abigail",
        source_hash="sha256:wire-storage",
    )

    with pytest.raises(DisplayNotAllowedStorageError, match="displayed_day_index"):
        await storage.acknowledge_display("generation-wire-storage", receipt)

    assert await _storage_counts(storage) == (0, 1, 0)


@pytest.mark.asyncio
async def test_ack_storage_accepts_exact_int32_maximum(
    storage: SqliteStorage,
) -> None:
    """generation day 与 displayed day 同为 Int32 max 时 ACK 仍可提交。"""

    await storage.save_dialogue_generation(_generation(game_day_index=WIRE_INTEGER_MAX))
    receipt = DisplayReceiptInput(
        display_receipt_id="receipt-wire-storage",
        save_id="save-wire",
        player_id="player-wire",
        displayed_day_index=WIRE_INTEGER_MAX,
        npc_id="Abigail",
        source_hash="sha256:wire-storage",
    )

    status = await storage.acknowledge_display("generation-wire-storage", receipt)

    assert status == "accepted"
    assert await _storage_counts(storage) == (0, 1, 1)


def _memory_query(**overrides: object) -> MemorySearchQuery:
    """构造合法的 next-day 查询，供单字段越界反例复用。"""

    values: dict[str, object] = {
        "save_id": "save-wire",
        "player_id": "player-wire",
        "npc_id": "Abigail",
        "game_day_index": 10,
        "cutoff_day_index": 9,
        "friendship_points": 750,
        "relationship_stage": "friend",
        "cooldown_days": 3,
        "limit": 3,
    }
    values.update(overrides)
    return MemorySearchQuery(**values)  # type: ignore[arg-type]


@pytest.mark.asyncio
@pytest.mark.parametrize(
    ("field_name", "invalid_value", "paired_overrides"),
    [
        ("game_day_index", WIRE_INTEGER_MAX + 1, {}),
        ("game_day_index", True, {"cutoff_day_index": 0}),
        (
            "cutoff_day_index",
            WIRE_INTEGER_MAX + 1,
            {"game_day_index": WIRE_INTEGER_MAX},
        ),
        ("cutoff_day_index", True, {"game_day_index": 2}),
        ("friendship_points", WIRE_INTEGER_MIN - 1, {}),
        ("friendship_points", WIRE_INTEGER_MAX + 1, {}),
        ("friendship_points", True, {}),
        ("cooldown_days", WIRE_INTEGER_MAX + 1, {}),
        ("cooldown_days", True, {}),
    ],
)
async def test_memory_query_rejects_invalid_wire_integer_fields(
    storage: SqliteStorage,
    field_name: str,
    invalid_value: object,
    paired_overrides: dict[str, object],
) -> None:
    """只读 repository 也不能把越界 Python 整数绑定到 SQLite。"""

    query = _memory_query(**paired_overrides, **{field_name: invalid_value})

    with pytest.raises(InvalidMemoryQueryError, match=field_name):
        await storage.search_memories(query)


@pytest.mark.asyncio
@pytest.mark.parametrize(
    "invalid_limit",
    [pytest.param(True, id="bool"), pytest.param(1.0, id="float"), pytest.param("1", id="string")],
)
async def test_memory_query_limit_requires_strict_integer(
    storage: SqliteStorage,
    invalid_limit: object,
) -> None:
    """Python 的比较运算不能让 bool/float/string 绕过内部 limit 合同。"""

    with pytest.raises(InvalidMemoryQueryError, match="limit"):
        await storage.search_memories(_memory_query(limit=invalid_limit))


@pytest.mark.asyncio
@pytest.mark.parametrize("friendship_points", [WIRE_INTEGER_MIN, WIRE_INTEGER_MAX])
async def test_memory_query_accepts_exact_int32_boundaries(
    storage: SqliteStorage,
    friendship_points: int,
) -> None:
    """合法边界查询在空库上应返回空结果，而不是发生算术或 bind 错误。"""

    result = await storage.search_memories(
        _memory_query(
            game_day_index=WIRE_INTEGER_MAX,
            cutoff_day_index=WIRE_INTEGER_MAX - 1,
            friendship_points=friendship_points,
            cooldown_days=WIRE_INTEGER_MAX,
        )
    )

    assert result == []
