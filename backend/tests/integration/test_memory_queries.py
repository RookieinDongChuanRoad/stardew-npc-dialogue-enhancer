"""记忆仓储硬过滤与稳定候选排序测试。"""

from __future__ import annotations

from datetime import UTC, datetime

import pytest

from stardew_npc_agent.storage import (
    InvalidMemoryQueryError,
    MemoryRecord,
    MemorySearchQuery,
    SqliteStorage,
)


def _memory(
    memory_id: str,
    *,
    save_id: str = "save-1",
    player_id: str = "player-1",
    audience_scope: str = "public",
    audience_npc_id: str | None = None,
    event_type: str = "world_progression",
    day_index: int = 8,
    expires_day_index: int | None = None,
    last_used_day_index: int | None = None,
    importance: float = 0.5,
    use_count: int = 0,
    relationship_stages: list[str] | None = None,
    min_friendship_points: int | None = None,
    max_friendship_points: int | None = None,
    tags: list[str] | None = None,
) -> MemoryRecord:
    """构造一条显式记忆行，使每个硬过滤维度都能被独立变更。"""

    return MemoryRecord(
        memory_id=memory_id,
        event_id=f"event:{memory_id}",
        save_id=save_id,
        player_id=player_id,
        audience_scope=audience_scope,
        audience_npc_id=audience_npc_id,
        event_type=event_type,
        event_version="1",
        source="test",
        payload_json={"fixture": memory_id},
        summary=f"summary:{memory_id}",
        tags_json=tags or [],
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


async def _seed(storage: SqliteStorage, *records: MemoryRecord) -> None:
    """在一个短事务中写入测试记忆投影。"""

    async with storage.session_factory.begin() as session:
        session.add_all(records)


def _query(**overrides: object) -> MemorySearchQuery:
    """返回一个第 10 天的合法查询，evidence 截止于昨日第 9 天。"""

    values: dict[str, object] = {
        "save_id": "save-1",
        "player_id": "player-1",
        "npc_id": "Abigail",
        "game_day_index": 10,
        "cutoff_day_index": 9,
        "friendship_points": 750,
        "relationship_stage": "friend",
        "tags": ("gift",),
        "cooldown_days": 3,
        "limit": 3,
    }
    values.update(overrides)
    return MemorySearchQuery(**values)  # type: ignore[arg-type]


@pytest.mark.asyncio
async def test_cutoff_cannot_be_today_and_today_event_is_absent_from_yesterday_query(
    storage: SqliteStorage,
) -> None:
    """仓储边界不能盲信上层传入的 cutoff，必须二次守住次日语义。"""

    await _seed(storage, _memory("memory:today", day_index=10))

    with pytest.raises(InvalidMemoryQueryError, match="cutoff"):
        await storage.search_memories(_query(cutoff_day_index=10))

    assert await storage.search_memories(_query()) == []


@pytest.mark.asyncio
async def test_query_isolates_save_player_and_public_or_target_npc_audience(
    storage: SqliteStorage,
) -> None:
    """查询只能看到当前分区内的公共记忆和目标 NPC 私有记忆。"""

    await _seed(
        storage,
        _memory("memory:public", event_type="world_progression"),
        _memory(
            "memory:abigail",
            audience_scope="npc",
            audience_npc_id="Abigail",
            event_type="gift_given",
        ),
        _memory(
            "memory:sebastian",
            audience_scope="npc",
            audience_npc_id="Sebastian",
            event_type="quest_completed",
        ),
        _memory("memory:other-save", save_id="save-2", event_type="festival_result"),
        _memory("memory:other-player", player_id="player-2", event_type="relationship_changed"),
    )

    evidence = await storage.search_memories(_query())

    assert {item.evidence_id for item in evidence} == {"memory:public", "memory:abigail"}
    assert {item.visibility_scope for item in evidence} == {"public", "npc:Abigail"}


@pytest.mark.asyncio
async def test_expiry_cooldown_and_relationship_conflicts_are_hard_filtered(
    storage: SqliteStorage,
) -> None:
    """过期、展示冷却和关系不匹配不得只是降分，而必须从候选集消失。"""

    await _seed(
        storage,
        _memory("memory:valid", importance=0.1),
        _memory("memory:expired", expires_day_index=8, importance=1.0),
        _memory("memory:cooldown", last_used_day_index=8, importance=1.0),
        _memory("memory:min-points", min_friendship_points=1_000, importance=1.0),
        _memory("memory:max-points", max_friendship_points=500, importance=1.0),
        _memory("memory:wrong-stage", relationship_stages=["spouse"], importance=1.0),
    )

    evidence = await storage.search_memories(_query())

    assert [item.evidence_id for item in evidence] == ["memory:valid"]


@pytest.mark.asyncio
async def test_stable_scoring_keeps_one_per_event_type_and_at_most_three_total(
    storage: SqliteStorage,
) -> None:
    """多个高分同类记忆不得挤占全部上下文，相同输入排序必须稳定。"""

    await _seed(
        storage,
        _memory(
            "memory:gift-best",
            event_type="gift_given",
            importance=0.9,
            tags=["gift"],
        ),
        _memory(
            "memory:gift-second",
            event_type="gift_given",
            importance=0.8,
            tags=["gift"],
        ),
        _memory("memory:progression", event_type="world_progression", importance=0.7),
        _memory("memory:quest", event_type="quest_completed", importance=0.6),
        _memory("memory:festival", event_type="festival_result", importance=0.1),
    )

    first = await storage.search_memories(_query())
    second = await storage.search_memories(_query())

    assert first == second
    assert [item.evidence_id for item in first] == [
        "memory:gift-best",
        "memory:progression",
        "memory:quest",
    ]
    assert len({item.evidence_type for item in first}) == 3
