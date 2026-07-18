"""Phase 4 生成前置水位与 generation cache 的只读集成测试。

测试通过 Alembic head 创建真实临时 SQLite 文件，只验证本项目所需的两个
查询，不扩张为通用 repository SDK。返回值必须在 session 关闭后仍可安全
使用，且磁盘水位腐化不能被默认值掩盖。
"""

from __future__ import annotations

from dataclasses import FrozenInstanceError
from datetime import UTC, datetime

import pytest
from sqlalchemy import text
from sqlalchemy.exc import OperationalError

from stardew_npc_agent.storage import (
    DialogueGenerationRecord,
    MemoryPartitionStateInvalidStorageError,
    MemoryPartitionStateRecord,
    SqliteStorage,
    StorageUnavailableError,
)


async def _seed_partition(
    storage: SqliteStorage,
    *,
    memory_revision: int,
    committed_through_day_index: int,
    ignore_check_constraints: bool = False,
) -> None:
    """写入测试水位；仅腐化用例临时绕过数据库 CHECK 后立即恢复。"""

    async with storage.session_factory() as session:
        if ignore_check_constraints:
            await session.execute(text("PRAGMA ignore_check_constraints=ON"))
        try:
            session.add(
                MemoryPartitionStateRecord(
                    save_id="save-read",
                    player_id="player-read",
                    memory_revision=memory_revision,
                    committed_through_day_index=committed_through_day_index,
                    updated_at_utc=datetime.now(UTC),
                )
            )
            await session.commit()
        finally:
            if ignore_check_constraints:
                await session.execute(text("PRAGMA ignore_check_constraints=OFF"))


async def _seed_generation(storage: SqliteStorage) -> None:
    """直接保存一条合法已生成快照，使读取测试不依赖未来 DialogueService。"""

    async with storage.session_factory.begin() as session:
        session.add(
            DialogueGenerationRecord(
                generation_id="generation-read-1",
                generation_key="sha256:generation-read-1",
                save_id="save-read",
                player_id="player-read",
                game_day_index=14,
                npc_id="Abigail",
                locale="zh-CN",
                source_hash="sha256:source-read-1",
                relationship_stage="friend",
                friendship_points=750,
                memory_cooldown_days=3,
                status="generated",
                result_text="记住昨天那块紫水晶了吗？",
                reason_code="TEST_GENERATED",
                evidence_ids_json=["memory:gift-1", "memory:quest-1"],
                trace_id="trace-read-1",
                guard_passed=True,
                evidence_authorized=True,
                input_versions_json={"profile": "abigail-profile-v1"},
                trace_json=None,
                usage_json=None,
                guard_report_json=None,
                created_at_utc=datetime.now(UTC),
                updated_at_utc=datetime.now(UTC),
            )
        )


@pytest.mark.asyncio
async def test_missing_memory_partition_returns_explicit_initial_snapshot(
    storage: SqliteStorage,
) -> None:
    """新存档没有分区行时使用冻结初值，而不是为了查询主动创建一行。"""

    from stardew_npc_agent.storage import MemoryPartitionSnapshot

    snapshot = await storage.get_memory_partition_snapshot("save-read", "player-read")

    assert snapshot == MemoryPartitionSnapshot(
        memory_revision=0,
        committed_through_day_index=-1,
    )


@pytest.mark.asyncio
async def test_valid_memory_partition_returns_session_decoupled_snapshot(
    storage: SqliteStorage,
) -> None:
    """已有合法分区只返回两个水位值，不把 ORM record 泄漏到 service。"""

    await _seed_partition(storage, memory_revision=5, committed_through_day_index=13)

    snapshot = await storage.get_memory_partition_snapshot("save-read", "player-read")

    assert (snapshot.memory_revision, snapshot.committed_through_day_index) == (5, 13)
    assert not isinstance(snapshot, MemoryPartitionStateRecord)
    with pytest.raises(FrozenInstanceError):
        snapshot.memory_revision = 6  # type: ignore[misc]


@pytest.mark.asyncio
async def test_corrupt_memory_partition_raises_stable_state_error(
    storage: SqliteStorage,
) -> None:
    """磁盘上 revision/day 组合腐化时必须复用写路径的不变量检查并失败。"""

    await _seed_partition(
        storage,
        memory_revision=1,
        committed_through_day_index=-1,
        ignore_check_constraints=True,
    )

    with pytest.raises(MemoryPartitionStateInvalidStorageError):
        await storage.get_memory_partition_snapshot("save-read", "player-read")


@pytest.mark.asyncio
async def test_generation_key_miss_returns_none(storage: SqliteStorage) -> None:
    """cache miss 是正常只读结果，不应伪装为 unavailable 或写入占位记录。"""

    assert await storage.get_dialogue_generation_by_key("sha256:missing") is None


@pytest.mark.asyncio
async def test_generation_key_hit_returns_immutable_session_decoupled_snapshot(
    storage: SqliteStorage,
) -> None:
    """命中只暴露服务所需字段，并把可变 JSON evidence 数组冻结为 tuple。"""

    await _seed_generation(storage)

    snapshot = await storage.get_dialogue_generation_by_key("sha256:generation-read-1")

    assert snapshot is not None
    assert snapshot.generation_id == "generation-read-1"
    assert snapshot.generation_key == "sha256:generation-read-1"
    assert snapshot.status == "generated"
    assert snapshot.result_text == "记住昨天那块紫水晶了吗？"
    assert snapshot.source_hash == "sha256:source-read-1"
    assert snapshot.reason_code == "TEST_GENERATED"
    assert snapshot.evidence_ids == ("memory:gift-1", "memory:quest-1")
    assert snapshot.trace_id == "trace-read-1"
    assert not isinstance(snapshot, DialogueGenerationRecord)
    with pytest.raises(FrozenInstanceError):
        snapshot.reason_code = "CHANGED"  # type: ignore[misc]


@pytest.mark.asyncio
async def test_read_programming_error_is_not_translated_to_storage_unavailable(
    storage: SqliteStorage,
) -> None:
    """缺表属于 Schema/编程错误，薄门面不得把它伪装成可重试 503。"""

    async with storage.engine.begin() as connection:
        await connection.execute(text("DROP TABLE memory_partition_states"))

    with pytest.raises(OperationalError) as error_info:
        await storage.get_memory_partition_snapshot("save-read", "player-read")

    assert not isinstance(error_info.value, StorageUnavailableError)
