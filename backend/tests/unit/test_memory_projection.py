"""首批 memory producer 到语义分类投影的纯函数合同测试。"""

from __future__ import annotations

from collections.abc import Mapping
from typing import Any

import pytest

from stardew_npc_agent.event_service import MemoryProjectionError, project_event_to_memory
from stardew_npc_agent.memory_capabilities import capability_for_kind
from stardew_npc_agent.schemas import GameEvent


def _event(
    *,
    event_type: str,
    event_version: str,
    source: str,
    audience_scope: str,
    audience_npc_id: str | None,
    payload: Mapping[str, Any],
    event_id: str | None = None,
    day_index: int = 13,
) -> GameEvent:
    """构造一条已经通过通用 wire DTO、等待具体 producer 投影校验的事件。"""

    return GameEvent.model_validate(
        {
            "event_id": event_id or f"event-{event_type}-{day_index}",
            "event_type": event_type,
            "event_version": event_version,
            "occurred_day_index": day_index,
            "source": source,
            "audience_scope": audience_scope,
            "audience_npc_id": audience_npc_id,
            "payload": dict(payload),
        }
    )


def _gift_event() -> GameEvent:
    """返回唯一正式 accepted-gift producer 的 v2 私有事件。"""

    return _event(
        event_type="gift_given",
        event_version="2",
        source="harmony.farmer.on_gift_given",
        audience_scope="npc",
        audience_npc_id="Abigail",
        payload={"item_id": "(O)66", "taste": "love"},
        event_id="event-gift-abigail-day-13",
    )


def _legacy_skill_event(milestone: str = "skill_farming_level_5") -> GameEvent:
    """构造旧 SMAPI LevelChanged alias；该形状只用于兼容既有已证明事实。"""

    return _event(
        event_type="world_progression",
        event_version="1",
        source="smapi.player.level_changed",
        audience_scope="public",
        audience_npc_id=None,
        payload={"milestone": milestone},
        event_id=f"event-legacy-{milestone}",
        day_index=14,
    )


@pytest.mark.parametrize(
    (
        "event",
        "expected_domain",
        "expected_kind",
        "expected_namespace",
        "expected_subject",
        "expected_summary",
    ),
    [
        (
            _gift_event(),
            "npc_history",
            "gift_given",
            "item_id",
            "(O)66",
            "第 13 天，玩家向 Abigail 赠送了 (O)66，礼物反应为 love。",
        ),
        (
            _event(
                event_type="relationship_status_changed",
                event_version="1",
                source="smapi.player.friendship_snapshot",
                audience_scope="npc",
                audience_npc_id="Abigail",
                payload={"old_status": "friendly", "new_status": "dating"},
            ),
            "npc_history",
            "relationship_status_changed",
            "relationship_status",
            "dating",
            "第 13 天，玩家与 Abigail 开始交往。",
        ),
        (
            _event(
                event_type="friendship_milestone_reached",
                event_version="1",
                source="smapi.player.friendship_snapshot",
                audience_scope="npc",
                audience_npc_id="Sebastian",
                payload={"milestone_id": "friend", "threshold_points": 1000},
            ),
            "npc_history",
            "friendship_milestone_reached",
            "milestone_id",
            "friend",
            "第 13 天，玩家与 Sebastian 首次达到四心朋友里程碑。",
        ),
        (
            _event(
                event_type="skill_level_reached",
                event_version="1",
                source="smapi.player.level_changed",
                audience_scope="public",
                audience_npc_id=None,
                payload={"skill_id": "farming", "old_level": 4, "new_level": 5},
            ),
            "player_progression",
            "skill_level_reached",
            "skill_id",
            "farming",
            "第 13 天，玩家的耕种技能提升到 5 级。",
        ),
        (
            _legacy_skill_event(),
            "player_progression",
            "skill_level_reached",
            "skill_id",
            "farming",
            "第 14 天，玩家的耕种技能提升到 5 级。",
        ),
        (
            _event(
                event_type="mine_depth_milestone_reached",
                event_version="1",
                source="smapi.player.warped",
                audience_scope="public",
                audience_npc_id=None,
                payload={
                    "mine_id": "skull_cavern",
                    "milestone_depth": 100,
                    "observed_depth": 117,
                },
            ),
            "player_progression",
            "mine_depth_milestone_reached",
            "mine_id",
            "skull_cavern",
            "第 13 天，玩家在骷髅洞穴到达了第 100 层里程碑。",
        ),
        (
            _event(
                event_type="tool_upgrade_received",
                event_version="1",
                source="smapi.player.tool_upgrade_observed",
                audience_scope="public",
                audience_npc_id=None,
                payload={"tool_id": "watering_can", "upgrade_level": 3},
            ),
            "player_progression",
            "tool_upgrade_received",
            "tool_id",
            "watering_can",
            "第 13 天，玩家取回并拥有了金水壶。",
        ),
        (
            _event(
                event_type="mastery_claimed",
                event_version="1",
                source="smapi.player.mastery_snapshot",
                audience_scope="public",
                audience_npc_id=None,
                payload={"skill_id": "combat"},
            ),
            "player_progression",
            "mastery_claimed",
            "skill_id",
            "combat",
            "第 13 天，玩家领取了战斗技能的精通奖励。",
        ),
        (
            _event(
                event_type="world_progression",
                event_version="1",
                source="smapi.world.public_facility_restored",
                audience_scope="public",
                audience_npc_id=None,
                payload={"milestone": "public_facility_greenhouse_restored"},
            ),
            "world_progression",
            "public_facility_restored",
            "facility_id",
            "greenhouse",
            "第 13 天，温室恢复并可以使用了。",
        ),
    ],
    ids=[
        "gift",
        "relationship",
        "friendship",
        "skill-new",
        "skill-legacy",
        "mine",
        "tool",
        "mastery",
        "facility",
    ],
)
def test_registered_producer_matrix_projects_active_registry_classification(
    event: GameEvent,
    expected_domain: str,
    expected_kind: str,
    expected_namespace: str,
    expected_subject: str,
    expected_summary: str,
) -> None:
    """每条新 producer 与唯一 legacy alias 都必须进入 registry 声明的精确领域。"""

    projection = project_event_to_memory("save-1", "player-1", event)
    capability = capability_for_kind(expected_kind)

    assert capability is not None
    assert projection.classification_status == "active"
    assert projection.memory_domain == expected_domain == capability.domain
    assert projection.memory_kind == expected_kind == capability.kind
    assert projection.subject_namespace == expected_namespace == capability.subject_namespace
    assert projection.subject_value == expected_subject
    assert projection.summary == expected_summary
    assert "昨天" not in projection.summary
    assert "最近" not in projection.summary


def test_gift_projection_is_deterministic_and_supports_all_six_frozen_tastes() -> None:
    """送礼摘要、ID、标签与六值 taste 必须逐次稳定，且不接受旧 friendship delta。"""

    first = project_event_to_memory("save-1", "player-1", _gift_event())
    second = project_event_to_memory("save-1", "player-1", _gift_event())

    assert first == second
    assert first.memory_id.startswith("memory:")
    assert first.tags == ("gift", "item:(O)66", "taste:love")
    assert first.importance == 0.9

    tea = _gift_event().model_copy(
        update={"payload": {"item_id": "(O)StardropTea", "taste": "stardrop_tea"}}
    )
    tea_projection = project_event_to_memory("save-1", "player-1", tea)
    assert tea_projection.subject_value == "(O)StardropTea"
    assert "taste:stardrop_tea" in tea_projection.tags


@pytest.mark.parametrize(
    "event",
    [
        _gift_event(),
        _event(
            event_type="relationship_status_changed",
            event_version="1",
            source="smapi.player.friendship_snapshot",
            audience_scope="npc",
            audience_npc_id="Abigail",
            payload={"old_status": "dating", "new_status": "engaged"},
        ),
        _event(
            event_type="friendship_milestone_reached",
            event_version="1",
            source="smapi.player.friendship_snapshot",
            audience_scope="npc",
            audience_npc_id="Abigail",
            payload={"milestone_id": "friend", "threshold_points": 1000},
        ),
        _event(
            event_type="skill_level_reached",
            event_version="1",
            source="smapi.player.level_changed",
            audience_scope="public",
            audience_npc_id=None,
            payload={"skill_id": "mining", "old_level": 1, "new_level": 2},
        ),
        _event(
            event_type="mine_depth_milestone_reached",
            event_version="1",
            source="smapi.player.warped",
            audience_scope="public",
            audience_npc_id=None,
            payload={"mine_id": "the_mines", "milestone_depth": 10, "observed_depth": 12},
        ),
        _event(
            event_type="tool_upgrade_received",
            event_version="1",
            source="smapi.player.tool_upgrade_observed",
            audience_scope="public",
            audience_npc_id=None,
            payload={"tool_id": "axe", "upgrade_level": 2},
        ),
        _event(
            event_type="mastery_claimed",
            event_version="1",
            source="smapi.player.mastery_snapshot",
            audience_scope="public",
            audience_npc_id=None,
            payload={"skill_id": "fishing"},
        ),
        _event(
            event_type="world_progression",
            event_version="1",
            source="smapi.world.public_facility_restored",
            audience_scope="public",
            audience_npc_id=None,
            payload={"milestone": "public_facility_minecarts_restored"},
        ),
    ],
    ids=["gift", "relationship", "friendship", "skill", "mine", "tool", "mastery", "facility"],
)
def test_every_registered_payload_rejects_unknown_fields(event: GameEvent) -> None:
    """具体版本的 payload 必须 exact-match，不能吞掉 producer 忘记升级版本的字段。"""

    drifted = event.model_copy(update={"payload": {**event.payload, "unexpected": True}})

    with pytest.raises(MemoryProjectionError) as error_info:
        project_event_to_memory("save-1", "player-1", drifted)

    assert error_info.value.reason_code == "INVALID_EVENT_PAYLOAD"


@pytest.mark.parametrize(
    "event",
    [
        _gift_event().model_copy(update={"audience_scope": "public", "audience_npc_id": None}),
        _event(
            event_type="skill_level_reached",
            event_version="1",
            source="smapi.player.level_changed",
            audience_scope="npc",
            audience_npc_id="Abigail",
            payload={"skill_id": "farming", "old_level": 1, "new_level": 2},
        ),
        _event(
            event_type="world_progression",
            event_version="1",
            source="smapi.world.public_facility_restored",
            audience_scope="npc",
            audience_npc_id="Abigail",
            payload={"milestone": "public_facility_bus_service_restored"},
        ),
    ],
    ids=["npc-history-made-public", "player-made-private", "world-made-private"],
)
def test_domain_audience_mismatch_is_rejected(event: GameEvent) -> None:
    """领域分类不能覆盖 wire audience 错误；私人和公共事实边界必须同时成立。"""

    with pytest.raises(MemoryProjectionError) as error_info:
        project_event_to_memory("save-1", "player-1", event)

    assert error_info.value.reason_code == "INVALID_EVENT_AUDIENCE"


@pytest.mark.parametrize(
    "event",
    [
        _gift_event().model_copy(update={"payload": {"item_id": "Amethyst", "taste": "love"}}),
        _gift_event().model_copy(update={"payload": {"item_id": "(O)66", "taste": "legendary"}}),
        _event(
            event_type="relationship_status_changed",
            event_version="1",
            source="smapi.player.friendship_snapshot",
            audience_scope="npc",
            audience_npc_id="Abigail",
            payload={"old_status": "divorced", "new_status": "friendly"},
        ),
        _event(
            event_type="friendship_milestone_reached",
            event_version="1",
            source="smapi.player.friendship_snapshot",
            audience_scope="npc",
            audience_npc_id="Abigail",
            payload={"milestone_id": "friend", "threshold_points": 999},
        ),
        _event(
            event_type="skill_level_reached",
            event_version="1",
            source="smapi.player.level_changed",
            audience_scope="public",
            audience_npc_id=None,
            payload={"skill_id": "luck", "old_level": 0, "new_level": 1},
        ),
        _event(
            event_type="skill_level_reached",
            event_version="1",
            source="smapi.player.level_changed",
            audience_scope="public",
            audience_npc_id=None,
            payload={"skill_id": "farming", "old_level": 5, "new_level": 5},
        ),
        _event(
            event_type="mine_depth_milestone_reached",
            event_version="1",
            source="smapi.player.warped",
            audience_scope="public",
            audience_npc_id=None,
            payload={"mine_id": "the_mines", "milestone_depth": 7, "observed_depth": 7},
        ),
        _event(
            event_type="mine_depth_milestone_reached",
            event_version="1",
            source="smapi.player.warped",
            audience_scope="public",
            audience_npc_id=None,
            payload={"mine_id": "skull_cavern", "milestone_depth": 100, "observed_depth": 99},
        ),
        _event(
            event_type="tool_upgrade_received",
            event_version="1",
            source="smapi.player.tool_upgrade_observed",
            audience_scope="public",
            audience_npc_id=None,
            payload={"tool_id": "fishing_rod", "upgrade_level": 2},
        ),
        _event(
            event_type="tool_upgrade_received",
            event_version="1",
            source="smapi.player.tool_upgrade_observed",
            audience_scope="public",
            audience_npc_id=None,
            payload={"tool_id": "axe", "upgrade_level": True},
        ),
        _event(
            event_type="mastery_claimed",
            event_version="1",
            source="smapi.player.mastery_snapshot",
            audience_scope="public",
            audience_npc_id=None,
            payload={"skill_id": "luck"},
        ),
        _event(
            event_type="world_progression",
            event_version="1",
            source="smapi.world.public_facility_restored",
            audience_scope="public",
            audience_npc_id=None,
            payload={"milestone": "community_center_pantry_completed"},
        ),
    ],
    ids=[
        "gift-unqualified-item",
        "gift-taste",
        "relationship-transition",
        "friend-threshold",
        "skill-id",
        "skill-non-increase",
        "mine-threshold",
        "mine-observed-before-threshold",
        "tool-id",
        "tool-bool-level",
        "mastery-skill",
        "facility-milestone",
    ],
)
def test_registered_producers_reject_unsupported_subjects_and_state_combinations(
    event: GameEvent,
) -> None:
    """合法 JSON 但无法由正式 producer 产生的状态组合不能进入 active memory。"""

    with pytest.raises(MemoryProjectionError) as error_info:
        project_event_to_memory("save-1", "player-1", event)

    assert error_info.value.reason_code == "INVALID_EVENT_PAYLOAD"


@pytest.mark.parametrize(
    "milestone",
    [
        "skill_combat_level_0",
        "skill_combat_level_01",
        "skill_luck_level_1",
        "skill_unknown_level_1",
        "skill_combat_level_11",
        "community_center_pantry_completed",
    ],
)
def test_legacy_level_alias_accepts_only_five_skill_level_shape(milestone: str) -> None:
    """旧 alias 只兼容已证明的五技能 1..10，不能退回通用 world 文本猜测。"""

    with pytest.raises(MemoryProjectionError) as error_info:
        project_event_to_memory("save-1", "player-1", _legacy_skill_event(milestone))

    assert error_info.value.reason_code == "INVALID_EVENT_PAYLOAD"


@pytest.mark.parametrize(
    ("event", "reason_code"),
    [
        (
            _gift_event().model_copy(update={"event_version": "1"}),
            "UNSUPPORTED_EVENT_VERSION",
        ),
        (
            _gift_event().model_copy(update={"source": "smapi"}),
            "UNSUPPORTED_EVENT_SOURCE",
        ),
        (
            _event(
                event_type="world_progression",
                event_version="1",
                source="smapi.other_progression",
                audience_scope="public",
                audience_npc_id=None,
                payload={"milestone": "public_facility_greenhouse_restored"},
            ),
            "UNSUPPORTED_EVENT_SOURCE",
        ),
        (
            _gift_event().model_copy(update={"event_type": "unregistered_event"}),
            "UNSUPPORTED_EVENT_TYPE",
        ),
    ],
)
def test_projection_requires_exact_registered_producer_identity(
    event: GameEvent,
    reason_code: str,
) -> None:
    """event type、version 和 source 必须共同命中 registry，不按摘要或相似字符串兜底。"""

    with pytest.raises(MemoryProjectionError) as error_info:
        project_event_to_memory("save-1", "player-1", event)

    assert error_info.value.reason_code == reason_code


def test_memory_id_encoding_cannot_collide_when_identity_contains_internal_nul() -> None:
    """分区身份允许内部 NUL 时，不同三元组仍必须生成不同 memory ID。"""

    first = project_event_to_memory("save", "player\x00npc", _gift_event())
    second = project_event_to_memory("save\x00player", "npc", _gift_event())

    assert (first.save_id, first.player_id) != (second.save_id, second.player_id)
    assert first.memory_id != second.memory_id
