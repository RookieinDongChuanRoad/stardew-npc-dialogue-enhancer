"""三个只读工具通过真实迁移 SQLite 的集成测试。"""

from __future__ import annotations

import json
import time
from datetime import UTC, datetime
from typing import Any

import pytest
from langchain.tools import ToolRuntime

from stardew_npc_agent.dialogue_agent import DialogueRuntimeContext
from stardew_npc_agent.dialogue_tools import (
    DIALOGUE_TOOL_NAMES,
    execute_get_event_history,
    execute_get_npc_history,
    execute_get_player_progression,
    execute_get_progression_context,
    execute_get_world_progression,
    execute_search_memories,
)
from stardew_npc_agent.storage import (
    MemoryPartitionStateRecord,
    MemoryRecord,
    SqliteStorage,
)


def _memory(
    memory_id: str,
    *,
    audience_scope: str,
    audience_npc_id: str | None,
    event_type: str,
    day_index: int,
    tags: list[str],
) -> MemoryRecord:
    """构造真实工具链可读取的确定性记忆行。"""

    return MemoryRecord(
        memory_id=memory_id,
        event_id=f"event:{memory_id}",
        save_id="save-1",
        player_id="player-1",
        audience_scope=audience_scope,
        audience_npc_id=audience_npc_id,
        event_type=event_type,
        event_version="1",
        source="test",
        payload_json={"fixture": memory_id},
        summary=f"第 {day_index} 天，{memory_id}",
        tags_json=tags,
        importance=0.8,
        occurred_day_index=day_index,
        expires_day_index=None,
        last_used_day_index=None,
        use_count=0,
        relationship_stages_json=[],
        min_friendship_points=None,
        max_friendship_points=None,
        created_at_utc=datetime.now(UTC),
    )


def _runtime(storage: SqliteStorage) -> ToolRuntime[DialogueRuntimeContext, dict[str, Any]]:
    """把真实 storage 放入模型不可见的 runtime context。"""

    return ToolRuntime(
        state={"messages": []},
        context=DialogueRuntimeContext(
            task_id="task-abigail",
            save_id="save-1",
            player_id="player-1",
            npc_id="Abigail",
            game_day_index=10,
            cutoff_day_index=9,
            friendship_points=750,
            relationship_stage="friend",
            memory_cooldown_days=3,
            allowed_tools=DIALOGUE_TOOL_NAMES,
            storage=storage,
            deadline_monotonic=time.monotonic() + 30,
        ),
        config={},
        stream_writer=lambda _value: None,
        tool_call_id="tool-call-integration",
        store=None,
    )


@pytest.mark.asyncio
async def test_all_three_tools_return_real_partitioned_evidence(
    storage: SqliteStorage,
) -> None:
    """工具 wrapper、授权、query DTO 与 SQLite 查询必须在同一真实链路工作。"""

    async with storage.session_factory.begin() as session:
        session.add_all(
            [
                _memory(
                    "memory:gift",
                    audience_scope="npc",
                    audience_npc_id="Abigail",
                    event_type="gift_given",
                    day_index=8,
                    tags=["gift", "amethyst"],
                ),
                _memory(
                    "memory:mine",
                    audience_scope="public",
                    audience_npc_id=None,
                    event_type="world_progression",
                    day_index=7,
                    tags=["progression", "mine"],
                ),
                _memory(
                    "memory:sebastian",
                    audience_scope="npc",
                    audience_npc_id="Sebastian",
                    event_type="gift_given",
                    day_index=9,
                    tags=["gift", "amethyst"],
                ),
            ]
        )

    runtime = _runtime(storage)
    memory_content, memory_artifact = await execute_search_memories(
        terms=["gift", "amethyst"],
        limit=3,
        runtime=runtime,
    )
    history_content, history_artifact = await execute_get_event_history(
        topics=["amethyst"],
        event_types=["gift_given"],
        since_day_index=0,
        limit=5,
        runtime=runtime,
    )
    progression_content, progression_artifact = await execute_get_progression_context(
        topics=["mine"],
        since_day_index=0,
        limit=3,
        runtime=runtime,
    )

    assert {item.evidence_id for item in memory_artifact} == {"memory:gift", "memory:mine"}
    assert [item.evidence_id for item in history_artifact] == ["memory:gift"]
    assert [item.evidence_id for item in progression_artifact] == ["memory:mine"]
    assert json.loads(memory_content)["result_count"] == 2
    assert json.loads(history_content)["result_count"] == 1
    assert json.loads(progression_content)["result_count"] == 1
    assert "memory:sebastian" not in memory_content + history_content + progression_content


def _active_memory(
    memory_id: str,
    *,
    domain: str,
    kind: str,
    namespace: str,
    subject: str,
    audience_scope: str,
    audience_npc_id: str | None,
) -> MemoryRecord:
    """构造可由目标领域 repository 返回的严格 active 分类行。"""

    return MemoryRecord(
        memory_id=memory_id,
        event_id=f"event:{memory_id}",
        save_id="save-1",
        player_id="player-1",
        audience_scope=audience_scope,
        audience_npc_id=audience_npc_id,
        event_type=kind,
        event_version="1",
        source="test.target.fixture",
        payload_json={"fixture": memory_id},
        classification_status="active",
        memory_domain=domain,
        memory_kind=kind,
        subject_namespace=namespace,
        subject_value=subject,
        summary=f"第 8 天，{memory_id}",
        tags_json=[],
        importance=0.8,
        occurred_day_index=8,
        expires_day_index=None,
        last_used_day_index=None,
        use_count=0,
        relationship_stages_json=[],
        min_friendship_points=None,
        max_friendship_points=None,
        created_at_utc=datetime.now(UTC),
    )


def _target_runtime(
    storage: SqliteStorage,
) -> ToolRuntime[DialogueRuntimeContext, dict[str, Any]]:
    """构造目标零参数工具所需的完整可信 context。"""

    target_tools = frozenset({"get_npc_history", "get_player_progression", "get_world_progression"})
    return ToolRuntime(
        state={"messages": []},
        context=DialogueRuntimeContext(
            task_id="task-abigail",
            save_id="save-1",
            player_id="player-1",
            npc_id="Abigail",
            game_day_index=10,
            cutoff_day_index=9,
            friendship_points=750,
            relationship_stage="friend",
            memory_cooldown_days=3,
            allowed_tools=target_tools,
            storage=storage,
            deadline_monotonic=time.monotonic() + 30,
            source_dialogue_text="收获、礼物和温室。",
            source_hash="sha256:source",
            locale="zh-CN",
            required_memory_revision=3,
            resolved_memory_revision=3,
            resolved_retrieval_state_revision=0,
        ),
        config={},
        stream_writer=lambda _value: None,
        tool_call_id="target-tool-call-integration",
        store=None,
    )


@pytest.mark.asyncio
async def test_target_zero_argument_tools_reach_real_snapshot_domain_repository(
    storage: SqliteStorage,
) -> None:
    """三个目标 wrapper 必须穿过授权、双 revision、SQL 和完整 observation 链路。"""

    async with storage.session_factory.begin() as session:
        session.add_all(
            [
                MemoryPartitionStateRecord(
                    save_id="save-1",
                    player_id="player-1",
                    memory_revision=3,
                    retrieval_state_revision=0,
                    committed_through_day_index=8,
                    updated_at_utc=datetime.now(UTC),
                ),
                _active_memory(
                    "memory:gift-target",
                    domain="npc_history",
                    kind="gift_given",
                    namespace="item_id",
                    subject="(O)66",
                    audience_scope="npc",
                    audience_npc_id="Abigail",
                ),
                _active_memory(
                    "memory:farming-target",
                    domain="player_progression",
                    kind="skill_level_reached",
                    namespace="skill_id",
                    subject="farming",
                    audience_scope="public",
                    audience_npc_id=None,
                ),
                _active_memory(
                    "memory:greenhouse-target",
                    domain="world_progression",
                    kind="public_facility_restored",
                    namespace="facility_id",
                    subject="greenhouse",
                    audience_scope="public",
                    audience_npc_id=None,
                ),
            ]
        )

    runtime = _target_runtime(storage)
    npc_content, npc_artifact = await execute_get_npc_history(runtime=runtime)
    player_content, player_artifact = await execute_get_player_progression(runtime=runtime)
    world_content, world_artifact = await execute_get_world_progression(runtime=runtime)

    assert [item.evidence_id for item in npc_artifact] == ["memory:gift-target"]
    assert [item.evidence_id for item in player_artifact] == ["memory:farming-target"]
    assert [item.evidence_id for item in world_artifact] == ["memory:greenhouse-target"]
    assert json.loads(npc_content)["candidates"][0]["memory_domain"] == "npc_history"
    assert json.loads(player_content)["candidates"][0]["memory_domain"] == "player_progression"
    assert json.loads(world_content)["candidates"][0]["memory_domain"] == "world_progression"
