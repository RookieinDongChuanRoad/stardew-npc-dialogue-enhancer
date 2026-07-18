"""领域 memory repository 的快照、授权与硬过滤集成测试。"""

from __future__ import annotations

from datetime import UTC, datetime

import pytest
from sqlalchemy import text

from stardew_npc_agent.storage import (
    DomainMemoryQuery,
    InvalidMemoryQueryError,
    MemoryPartitionStateInvalidStorageError,
    MemoryPartitionStateRecord,
    MemoryRecord,
    MemorySnapshotMismatchStorageError,
    SqliteStorage,
)


def _memory(
    memory_id: str,
    *,
    domain: str = "player_progression",
    kind: str = "skill_level_reached",
    namespace: str = "skill_id",
    subject: str = "farming",
    save_id: str = "save-1",
    player_id: str = "player-1",
    audience_scope: str = "public",
    audience_npc_id: str | None = None,
    classification_status: str = "active",
    day_index: int = 8,
    expires_day_index: int | None = None,
    last_used_day_index: int | None = None,
    importance: float = 0.5,
    use_count: int = 0,
    relationship_stages: list[str] | None = None,
    min_friendship_points: int | None = None,
    max_friendship_points: int | None = None,
) -> MemoryRecord:
    """构造一条符合 0004 分类约束的 memory；测试可显式覆盖单一维度。"""

    return MemoryRecord(
        memory_id=memory_id,
        event_id=f"event:{memory_id}",
        save_id=save_id,
        player_id=player_id,
        audience_scope=audience_scope,
        audience_npc_id=audience_npc_id,
        event_type=kind,
        event_version="1",
        source="test.fixture",
        payload_json={"fixture": memory_id},
        classification_status=classification_status,
        memory_domain=domain if classification_status == "active" else None,
        memory_kind=kind if classification_status == "active" else None,
        subject_namespace=namespace if classification_status == "active" else None,
        subject_value=subject if classification_status == "active" else None,
        summary=f"summary:{memory_id}",
        tags_json=[],
        importance=importance,
        occurred_day_index=day_index,
        expires_day_index=expires_day_index,
        last_used_day_index=last_used_day_index,
        use_count=use_count,
        relationship_stages_json=relationship_stages or [],
        min_friendship_points=min_friendship_points,
        max_friendship_points=max_friendship_points,
        created_at_utc=datetime.now(UTC),
    )


def _state(
    *,
    save_id: str = "save-1",
    player_id: str = "player-1",
    memory_revision: int = 8,
    retrieval_revision: int = 5,
) -> MemoryPartitionStateRecord:
    """构造与查询 fixture 对应的合法二元水位。"""

    return MemoryPartitionStateRecord(
        save_id=save_id,
        player_id=player_id,
        memory_revision=memory_revision,
        retrieval_state_revision=retrieval_revision,
        committed_through_day_index=8,
        updated_at_utc=datetime.now(UTC),
    )


async def _seed(storage: SqliteStorage, *records: object) -> None:
    """在一个短事务内写入 state/memory fixture。"""

    async with storage.session_factory.begin() as session:
        session.add_all(records)


def _query(**overrides: object) -> DomainMemoryQuery:
    """返回第 10 天、冻结在 8/5 二元快照的 player progression 查询。"""

    values: dict[str, object] = {
        "save_id": "save-1",
        "player_id": "player-1",
        "npc_id": "Abigail",
        "game_day_index": 10,
        "cutoff_day_index": 9,
        "friendship_points": 750,
        "relationship_stage": "friend",
        "memory_domain": "player_progression",
        "source_dialogue_text": "今年的收获和作物都很不错。",
        "locale": "zh-CN",
        "resolved_memory_revision": 8,
        "resolved_retrieval_state_revision": 5,
        "cooldown_days": 3,
        "limit": 5,
    }
    values.update(overrides)
    return DomainMemoryQuery(**values)  # type: ignore[arg-type]


@pytest.mark.asyncio
async def test_each_domain_query_enforces_exact_domain_audience_and_quarantine(
    storage: SqliteStorage,
) -> None:
    """三个领域只能返回各自 audience 组合，quarantined 行永不参与检索。"""

    await _seed(
        storage,
        _state(),
        _memory("memory:player"),
        _memory(
            "memory:npc",
            domain="npc_history",
            kind="gift_given",
            namespace="item_id",
            subject="(O)66",
            audience_scope="npc",
            audience_npc_id="Abigail",
        ),
        _memory(
            "memory:other-npc",
            domain="npc_history",
            kind="gift_given",
            namespace="item_id",
            subject="(O)72",
            audience_scope="npc",
            audience_npc_id="Sebastian",
        ),
        _memory(
            "memory:world",
            domain="world_progression",
            kind="public_facility_restored",
            namespace="facility_id",
            subject="greenhouse",
        ),
        _memory("memory:quarantined", classification_status="quarantined", importance=1.0),
    )

    player = await storage.get_domain_memory_candidates(_query())
    npc = await storage.get_domain_memory_candidates(
        _query(memory_domain="npc_history", source_dialogue_text="那份礼物我还记得。")
    )
    world = await storage.get_domain_memory_candidates(
        _query(memory_domain="world_progression", source_dialogue_text="温室修好了。")
    )

    assert [item.evidence_id for item in player] == ["memory:player"]
    assert [item.evidence_id for item in npc] == ["memory:npc"]
    assert [item.evidence_id for item in world] == ["memory:world"]
    assert npc[0].memory_domain == "npc_history"
    assert npc[0].memory_kind == "gift_given"
    assert npc[0].subject_namespace == "item_id"
    assert npc[0].subject_value == "(O)66"


@pytest.mark.asyncio
async def test_domain_query_applies_partition_time_policy_and_cooldown_as_hard_filters(
    storage: SqliteStorage,
) -> None:
    """分区、昨日、过期、关系、好感和展示冷却均必须在排序前剔除。"""

    await _seed(
        storage,
        _state(),
        _memory("memory:valid", importance=0.1),
        _memory("memory:other-save", save_id="save-2", importance=1.0),
        _memory("memory:other-player", player_id="player-2", importance=1.0),
        _memory("memory:today", day_index=10, importance=1.0),
        _memory("memory:expired", expires_day_index=8, importance=1.0),
        _memory("memory:cooldown", last_used_day_index=8, importance=1.0),
        _memory("memory:wrong-stage", relationship_stages=["spouse"], importance=1.0),
        _memory("memory:min-points", min_friendship_points=1_000, importance=1.0),
        _memory("memory:max-points", max_friendship_points=500, importance=1.0),
    )

    evidence = await storage.get_domain_memory_candidates(_query())

    assert [item.evidence_id for item in evidence] == ["memory:valid"]


@pytest.mark.asyncio
@pytest.mark.parametrize(
    ("memory_revision", "retrieval_revision"),
    [(7, 5), (8, 4), (9, 6)],
)
async def test_domain_query_requires_exact_frozen_dual_revision(
    storage: SqliteStorage,
    memory_revision: int,
    retrieval_revision: int,
) -> None:
    """任一水位在 resolve 后变化都必须 fail closed，不能读取“最新”候选。"""

    await _seed(storage, _state(), _memory("memory:valid"))

    with pytest.raises(MemorySnapshotMismatchStorageError):
        await storage.get_domain_memory_candidates(
            _query(
                resolved_memory_revision=memory_revision,
                resolved_retrieval_state_revision=retrieval_revision,
            )
        )


@pytest.mark.asyncio
async def test_missing_partition_state_is_empty_only_when_no_partition_memory_exists(
    storage: SqliteStorage,
) -> None:
    """真正的新分区可返回空；有 memory 却无 state 是腐化，不能伪装成空结果。"""

    empty_query = _query(
        resolved_memory_revision=0,
        resolved_retrieval_state_revision=0,
    )
    assert await storage.get_domain_memory_candidates(empty_query) == []

    await _seed(storage, _memory("memory:orphan"))
    with pytest.raises(MemoryPartitionStateInvalidStorageError):
        await storage.get_domain_memory_candidates(empty_query)


@pytest.mark.asyncio
async def test_repository_defensively_rejects_selected_corrupt_classification(
    storage: SqliteStorage,
) -> None:
    """即使磁盘约束被外部绕过，repository 也不能返回错误 subject namespace。"""

    # ``ignore_check_constraints`` 仅在这个测试连接/事务中模拟被外部工具损坏的
    # SQLite 文件；生产代码不会设置该 PRAGMA，也不会尝试修复坏行。
    async with storage.session_factory.begin() as session:
        await session.execute(text("PRAGMA ignore_check_constraints = ON"))
        session.add_all(
            [
                _state(),
                _memory("memory:corrupt", namespace="facility_id"),
            ]
        )

    with pytest.raises(MemoryPartitionStateInvalidStorageError):
        await storage.get_domain_memory_candidates(_query())


@pytest.mark.asyncio
@pytest.mark.parametrize(
    "query",
    [
        _query(cutoff_day_index=10),
        _query(memory_domain="unknown"),
        _query(limit=6),
        _query(resolved_memory_revision=-1),
        _query(source_dialogue_text=""),
    ],
    ids=["today-cutoff", "unknown-domain", "limit", "negative-revision", "empty-source"],
)
async def test_domain_query_rejects_invalid_trusted_runtime_fields(
    storage: SqliteStorage,
    query: DomainMemoryQuery,
) -> None:
    """进程内调用绕过 ToolRuntime 时，repository 仍需验证全部可信字段。"""

    with pytest.raises(InvalidMemoryQueryError):
        await storage.get_domain_memory_candidates(query)
