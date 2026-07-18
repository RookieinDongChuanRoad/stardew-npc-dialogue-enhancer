"""Phase 5 Agent 专用事件历史与世界进度只读查询集成测试。"""

from __future__ import annotations

from datetime import UTC, datetime

import pytest

from stardew_npc_agent.storage import (
    EventHistoryQuery,
    InvalidMemoryQueryError,
    MemoryRecord,
    ProgressionContextQuery,
    SqliteStorage,
)


def _memory(
    memory_id: str,
    *,
    save_id: str = "save-1",
    player_id: str = "player-1",
    audience_scope: str = "public",
    audience_npc_id: str | None = None,
    event_type: str = "gift_given",
    day_index: int = 8,
    summary: str | None = None,
    tags: list[str] | None = None,
    expires_day_index: int | None = None,
    last_used_day_index: int | None = None,
    relationship_stages: list[str] | None = None,
    min_friendship_points: int | None = None,
    max_friendship_points: int | None = None,
) -> MemoryRecord:
    """构造可单独改变每个授权维度的记忆投影记录。"""

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
        summary=summary or f"第 {day_index} 天发生了 {memory_id}",
        tags_json=tags or [],
        importance=0.5,
        occurred_day_index=day_index,
        expires_day_index=expires_day_index,
        last_used_day_index=last_used_day_index,
        use_count=0,
        relationship_stages_json=relationship_stages or [],
        min_friendship_points=min_friendship_points,
        max_friendship_points=max_friendship_points,
        created_at_utc=datetime.now(UTC),
    )


async def _seed(storage: SqliteStorage, *records: MemoryRecord) -> None:
    """在单个短事务中写入查询测试数据。"""

    async with storage.session_factory.begin() as session:
        session.add_all(records)


def _event_query(**overrides: object) -> EventHistoryQuery:
    """返回截止第 9 天、目标为 Abigail 的合法历史查询。"""

    values: dict[str, object] = {
        "save_id": "save-1",
        "player_id": "player-1",
        "npc_id": "Abigail",
        "game_day_index": 10,
        "cutoff_day_index": 9,
        "friendship_points": 750,
        "relationship_stage": "friend",
        "topics": ("gift",),
        "event_types": ("gift_given",),
        "since_day_index": 0,
        "cooldown_days": 3,
        "limit": 5,
    }
    values.update(overrides)
    return EventHistoryQuery(**values)  # type: ignore[arg-type]


def _progression_query(**overrides: object) -> ProgressionContextQuery:
    """返回只允许公共 world_progression 的合法查询。"""

    values: dict[str, object] = {
        "save_id": "save-1",
        "player_id": "player-1",
        "npc_id": "Abigail",
        "game_day_index": 10,
        "cutoff_day_index": 9,
        "friendship_points": 750,
        "relationship_stage": "friend",
        "topics": ("mine",),
        "since_day_index": 0,
        "cooldown_days": 3,
        "limit": 3,
    }
    values.update(overrides)
    return ProgressionContextQuery(**values)  # type: ignore[arg-type]


@pytest.mark.asyncio
async def test_event_history_applies_partition_visibility_and_policy_filters(
    storage: SqliteStorage,
) -> None:
    """历史查询必须同时守住分区、NPC、昨日、过期、冷却与关系边界。"""

    await _seed(
        storage,
        _memory(
            "memory:valid-private",
            audience_scope="npc",
            audience_npc_id="Abigail",
            day_index=8,
            tags=["gift", "amethyst"],
        ),
        _memory("memory:valid-public", day_index=7, tags=["gift"]),
        _memory("memory:other-save", save_id="save-2", tags=["gift"]),
        _memory("memory:other-player", player_id="player-2", tags=["gift"]),
        _memory(
            "memory:other-npc",
            audience_scope="npc",
            audience_npc_id="Sebastian",
            tags=["gift"],
        ),
        _memory("memory:today", day_index=10, tags=["gift"]),
        _memory("memory:expired", expires_day_index=8, tags=["gift"]),
        _memory("memory:cooldown", last_used_day_index=8, tags=["gift"]),
        _memory("memory:wrong-stage", relationship_stages=["spouse"], tags=["gift"]),
        _memory("memory:min-points", min_friendship_points=1_000, tags=["gift"]),
        _memory("memory:max-points", max_friendship_points=500, tags=["gift"]),
    )

    evidence = await storage.get_event_history(_event_query())

    assert [item.evidence_id for item in evidence] == [
        "memory:valid-private",
        "memory:valid-public",
    ]
    assert evidence[0].source_event_ids == ("event:memory:valid-private",)
    assert {item.visibility_scope for item in evidence} == {"public", "npc:Abigail"}


@pytest.mark.asyncio
async def test_event_history_matches_topics_in_tags_or_summary_and_orders_stably(
    storage: SqliteStorage,
) -> None:
    """主题命中可来自标签或摘要；结果按日期降序、ID 升序稳定返回多条。"""

    await _seed(
        storage,
        _memory("memory:b", day_index=8, tags=["gift", "amethyst"]),
        _memory("memory:a", day_index=8, summary="玩家送过一块 Amethyst。"),
        _memory("memory:older", day_index=6, tags=["amethyst"]),
        _memory("memory:irrelevant", day_index=9, tags=["sword"]),
        _memory("memory:wrong-type", event_type="quest_completed", tags=["amethyst"]),
    )

    query = _event_query(topics=("amethyst",), event_types=("gift_given",))
    first = await storage.get_event_history(query)
    second = await storage.get_event_history(query)

    assert first == second
    assert [item.evidence_id for item in first] == [
        "memory:a",
        "memory:b",
        "memory:older",
    ]


@pytest.mark.asyncio
async def test_progression_context_returns_only_public_world_progression(
    storage: SqliteStorage,
) -> None:
    """世界进度工具不能把 NPC 私人事件或其他公共事件伪装成 progression。"""

    await _seed(
        storage,
        _memory(
            "memory:mine-40",
            event_type="world_progression",
            day_index=8,
            tags=["progression", "milestone:mine_level_40"],
        ),
        _memory(
            "memory:mine-20",
            event_type="world_progression",
            day_index=5,
            summary="第 5 天，矿井到达 mine_level_20。",
        ),
        _memory(
            "memory:private-progression",
            audience_scope="npc",
            audience_npc_id="Abigail",
            event_type="world_progression",
            tags=["mine"],
        ),
        _memory("memory:public-gift", event_type="gift_given", tags=["mine"]),
        _memory(
            "memory:bus",
            event_type="world_progression",
            day_index=9,
            tags=["progression", "bus"],
        ),
    )

    evidence = await storage.get_progression_context(_progression_query())

    assert [item.evidence_id for item in evidence] == ["memory:mine-40", "memory:mine-20"]
    assert all(item.evidence_type == "world_progression" for item in evidence)
    assert all(item.visibility_scope == "public" for item in evidence)


@pytest.mark.asyncio
@pytest.mark.parametrize(
    "operation",
    [
        lambda storage: storage.get_event_history(_event_query(cutoff_day_index=10)),
        lambda storage: storage.get_event_history(_event_query(since_day_index=10)),
        lambda storage: storage.get_event_history(_event_query(limit=6)),
        lambda storage: storage.get_event_history(_event_query(topics=(), event_types=())),
        lambda storage: storage.get_progression_context(_progression_query(limit=4)),
        lambda storage: storage.get_progression_context(_progression_query(topics=("x" * 65,))),
    ],
    ids=[
        "today-cutoff",
        "future-since",
        "history-limit",
        "unbounded-history",
        "progression-limit",
        "topic-length",
    ],
)
async def test_agent_evidence_queries_reject_invalid_resource_bounds_before_returning_data(
    storage: SqliteStorage,
    operation: object,
) -> None:
    """Agent 查询不能通过未来日期或无界参数扩大可见数据面。"""

    with pytest.raises(InvalidMemoryQueryError):
        await operation(storage)  # type: ignore[operator]
