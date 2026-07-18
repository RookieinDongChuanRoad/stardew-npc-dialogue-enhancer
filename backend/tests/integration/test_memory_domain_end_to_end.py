"""八类 producer wire 事实到 D+1 领域工具 observation 的端到端 fixture。

这些测试不启动 Stardew，也不伪造分类行：每条事实都先经过正式 Pydantic wire
合同、EventService 投影、SQLite 事务和 revision，再由目标零参数领域工具读取。
C# collector 对应的公开状态映射由各自 xUnit 覆盖；这里验证跨 HTTP 语义后的
Python 半链路、next-day cutoff、分类和模型可见 observation。
"""

from __future__ import annotations

import json
import time
from dataclasses import dataclass
from typing import Any, Literal

import pytest
from langchain.tools import ToolRuntime

from stardew_npc_agent.dialogue_agent import DialogueRuntimeContext
from stardew_npc_agent.dialogue_tools import (
    execute_get_npc_history,
    execute_get_player_progression,
    execute_get_world_progression,
)
from stardew_npc_agent.event_service import EventService
from stardew_npc_agent.schemas import GameEventBatchRequest
from stardew_npc_agent.storage import EvidenceRecord, SqliteStorage

Domain = Literal["npc_history", "player_progression", "world_progression"]


@dataclass(frozen=True, slots=True)
class _ProducerFixture:
    """一条首批 producer 的完整 wire 与预期分类。"""

    case_id: str
    event_type: str
    event_version: str
    source: str
    audience_scope: str
    audience_npc_id: str | None
    payload: dict[str, object]
    domain: Domain
    kind: str
    subject_namespace: str
    subject_value: str
    source_dialogue_text: str


_FIXTURES = (
    _ProducerFixture(
        case_id="gift",
        event_type="gift_given",
        event_version="2",
        source="harmony.farmer.on_gift_given",
        audience_scope="npc",
        audience_npc_id="Abigail",
        payload={"item_id": "(O)66", "taste": "love"},
        domain="npc_history",
        kind="gift_given",
        subject_namespace="item_id",
        subject_value="(O)66",
        source_dialogue_text="那份礼物我还记得。",
    ),
    _ProducerFixture(
        case_id="relationship",
        event_type="relationship_status_changed",
        event_version="1",
        source="smapi.player.friendship_snapshot",
        audience_scope="npc",
        audience_npc_id="Abigail",
        payload={"old_status": "dating", "new_status": "engaged"},
        domain="npc_history",
        kind="relationship_status_changed",
        subject_namespace="relationship_status",
        subject_value="engaged",
        source_dialogue_text="我们的关系发生了变化。",
    ),
    _ProducerFixture(
        case_id="friendship",
        event_type="friendship_milestone_reached",
        event_version="1",
        source="smapi.player.friendship_snapshot",
        audience_scope="npc",
        audience_npc_id="Abigail",
        payload={"milestone_id": "friend", "threshold_points": 1000},
        domain="npc_history",
        kind="friendship_milestone_reached",
        subject_namespace="milestone_id",
        subject_value="friend",
        source_dialogue_text="我们已经是朋友了。",
    ),
    _ProducerFixture(
        case_id="skill",
        event_type="skill_level_reached",
        event_version="1",
        source="smapi.player.level_changed",
        audience_scope="public",
        audience_npc_id=None,
        payload={"skill_id": "farming", "old_level": 1, "new_level": 2},
        domain="player_progression",
        kind="skill_level_reached",
        subject_namespace="skill_id",
        subject_value="farming",
        source_dialogue_text="今年的收获和作物不错。",
    ),
    _ProducerFixture(
        case_id="mine",
        event_type="mine_depth_milestone_reached",
        event_version="1",
        source="smapi.player.warped",
        audience_scope="public",
        audience_npc_id=None,
        payload={
            "mine_id": "the_mines",
            "milestone_depth": 40,
            "observed_depth": 42,
        },
        domain="player_progression",
        kind="mine_depth_milestone_reached",
        subject_namespace="mine_id",
        subject_value="the_mines",
        source_dialogue_text="矿洞深处还有什么？",
    ),
    _ProducerFixture(
        case_id="tool",
        event_type="tool_upgrade_received",
        event_version="1",
        source="smapi.player.tool_upgrade_observed",
        audience_scope="public",
        audience_npc_id=None,
        payload={"tool_id": "axe", "upgrade_level": 2},
        domain="player_progression",
        kind="tool_upgrade_received",
        subject_namespace="tool_id",
        subject_value="axe",
        source_dialogue_text="那把斧头和工具怎么样？",
    ),
    _ProducerFixture(
        case_id="mastery",
        event_type="mastery_claimed",
        event_version="1",
        source="smapi.player.mastery_snapshot",
        audience_scope="public",
        audience_npc_id=None,
        payload={"skill_id": "combat"},
        domain="player_progression",
        kind="mastery_claimed",
        subject_namespace="skill_id",
        subject_value="combat",
        source_dialogue_text="拿剑对付怪物需要战斗精通。",
    ),
    _ProducerFixture(
        case_id="facility",
        event_type="world_progression",
        event_version="1",
        source="smapi.world.public_facility_restored",
        audience_scope="public",
        audience_npc_id=None,
        payload={"milestone": "public_facility_greenhouse_restored"},
        domain="world_progression",
        kind="public_facility_restored",
        subject_namespace="facility_id",
        subject_value="greenhouse",
        source_dialogue_text="温室里的作物不受季节影响。",
    ),
)


def _runtime(
    storage: SqliteStorage,
    fixture: _ProducerFixture,
    *,
    game_day_index: int,
    memory_revision: int,
    retrieval_revision: int,
) -> ToolRuntime[DialogueRuntimeContext, dict[str, Any]]:
    """把固定领域之外的全部查询输入绑定为模型不可见的可信 context。"""

    allowed = frozenset({"get_npc_history", "get_player_progression", "get_world_progression"})
    return ToolRuntime(
        state={"messages": []},
        context=DialogueRuntimeContext(
            task_id=f"task:{fixture.case_id}:{game_day_index}",
            save_id="save-domain-e2e",
            player_id="player-domain-e2e",
            npc_id="Abigail",
            game_day_index=game_day_index,
            cutoff_day_index=game_day_index - 1,
            friendship_points=1_250,
            relationship_stage="friend",
            memory_cooldown_days=3,
            allowed_tools=allowed,
            storage=storage,
            deadline_monotonic=time.monotonic() + 30,
            source_dialogue_text=fixture.source_dialogue_text,
            source_hash=f"sha256:{fixture.case_id}:{game_day_index}",
            locale="zh-CN",
            required_memory_revision=memory_revision,
            resolved_memory_revision=memory_revision,
            resolved_retrieval_state_revision=retrieval_revision,
        ),
        config={},
        stream_writer=lambda _value: None,
        tool_call_id=f"tool:{fixture.case_id}:{game_day_index}",
        store=None,
    )


async def _execute_domain(
    fixture: _ProducerFixture,
    runtime: ToolRuntime[DialogueRuntimeContext, dict[str, Any]],
) -> tuple[str, tuple[EvidenceRecord, ...]]:
    """按 fixture 领域选择固定 wrapper；模型没有 domain 参数可提交。"""

    if fixture.domain == "npc_history":
        return await execute_get_npc_history(runtime=runtime)
    if fixture.domain == "player_progression":
        return await execute_get_player_progression(runtime=runtime)
    return await execute_get_world_progression(runtime=runtime)


@pytest.mark.asyncio
@pytest.mark.parametrize("fixture", _FIXTURES, ids=lambda item: item.case_id)
async def test_each_producer_fact_is_hidden_on_day_d_and_visible_on_day_d_plus_one(
    storage: SqliteStorage,
    fixture: _ProducerFixture,
) -> None:
    """八类正式事实必须先持久化，D 日不可见，D+1 才进入正确领域 observation。"""

    response = await EventService(storage).ingest_batch(
        GameEventBatchRequest.model_validate(
            {
                "schema_version": "1.0",
                "request_id": f"request:{fixture.case_id}",
                "save_id": "save-domain-e2e",
                "player_id": "player-domain-e2e",
                "events": [
                    {
                        "event_id": f"event:{fixture.case_id}",
                        "event_type": fixture.event_type,
                        "event_version": fixture.event_version,
                        "occurred_day_index": 8,
                        "source": fixture.source,
                        "audience_scope": fixture.audience_scope,
                        "audience_npc_id": fixture.audience_npc_id,
                        "payload": fixture.payload,
                    }
                ],
            }
        )
    )
    snapshot = await storage.get_memory_partition_snapshot(
        "save-domain-e2e",
        "player-domain-e2e",
    )
    assert response.memory_revision == snapshot.memory_revision == 1
    assert snapshot.retrieval_state_revision == 1

    same_day_content, same_day_artifact = await _execute_domain(
        fixture,
        _runtime(
            storage,
            fixture,
            game_day_index=8,
            memory_revision=1,
            retrieval_revision=1,
        ),
    )
    assert same_day_artifact == ()
    assert json.loads(same_day_content) == {"candidate_count": 0, "candidates": []}

    next_day_content, next_day_artifact = await _execute_domain(
        fixture,
        _runtime(
            storage,
            fixture,
            game_day_index=9,
            memory_revision=1,
            retrieval_revision=1,
        ),
    )
    assert len(next_day_artifact) == 1
    evidence = next_day_artifact[0]
    assert evidence.memory_domain == fixture.domain
    assert evidence.memory_kind == fixture.kind
    assert evidence.subject_namespace == fixture.subject_namespace
    assert evidence.subject_value == fixture.subject_value
    assert json.loads(next_day_content)["candidates"][0]["evidence_id"] == evidence.evidence_id
