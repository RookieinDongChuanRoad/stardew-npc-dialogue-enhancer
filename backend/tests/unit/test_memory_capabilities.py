"""Memory 领域能力目录的静态、无存档数据合同测试。

这些测试只验证构建时能力，不读取 SQLite、环境变量或真实存档。这样可以证明
Tool Description 与分类规则来自稳定目录，而不是把当前有哪些记忆提前泄露给模型。
"""

from __future__ import annotations

import importlib
import importlib.util
from dataclasses import FrozenInstanceError
from types import ModuleType
from typing import Any, cast

import pytest


def _capability_module() -> ModuleType:
    """加载目标模块，并把“模块尚未实现”表达为清晰的 RED 断言。"""

    module_name = "stardew_npc_agent.memory_capabilities"
    assert importlib.util.find_spec(module_name) is not None, (
        "memory capability registry module is not implemented"
    )
    return importlib.import_module(module_name)


def test_registry_freezes_exact_first_activation_bundle_contracts() -> None:
    """首批八种事实必须逐字绑定领域、wire producer 和 audience。"""

    module = _capability_module()
    capabilities = cast(tuple[Any, ...], module.MEMORY_CAPABILITIES)

    actual = {
        item.kind: (
            item.domain,
            item.wire_event_type,
            item.wire_event_version,
            item.producer_source,
            item.audience_scope,
            item.subject_namespace,
        )
        for item in capabilities
    }
    assert actual == {
        "gift_given": (
            "npc_history",
            "gift_given",
            "2",
            "harmony.farmer.on_gift_given",
            "npc",
            "item_id",
        ),
        "relationship_status_changed": (
            "npc_history",
            "relationship_status_changed",
            "1",
            "smapi.player.friendship_snapshot",
            "npc",
            "relationship_status",
        ),
        "friendship_milestone_reached": (
            "npc_history",
            "friendship_milestone_reached",
            "1",
            "smapi.player.friendship_snapshot",
            "npc",
            "milestone_id",
        ),
        "skill_level_reached": (
            "player_progression",
            "skill_level_reached",
            "1",
            "smapi.player.level_changed",
            "public",
            "skill_id",
        ),
        "mine_depth_milestone_reached": (
            "player_progression",
            "mine_depth_milestone_reached",
            "1",
            "smapi.player.warped",
            "public",
            "mine_id",
        ),
        "tool_upgrade_received": (
            "player_progression",
            "tool_upgrade_received",
            "1",
            "smapi.player.tool_upgrade_observed",
            "public",
            "tool_id",
        ),
        "mastery_claimed": (
            "player_progression",
            "mastery_claimed",
            "1",
            "smapi.player.mastery_snapshot",
            "public",
            "skill_id",
        ),
        "public_facility_restored": (
            "world_progression",
            "world_progression",
            "1",
            "smapi.world.public_facility_restored",
            "public",
            "facility_id",
        ),
    }
    assert {item.status for item in capabilities} == {"active"}


def test_registry_exposes_static_scope_without_dynamic_memory_facts() -> None:
    """静态 scope 可以解释收获与耕种，但不得含日期、数量或 evidence。"""

    module = _capability_module()
    farming = module.capability_for_kind("skill_level_reached")
    assert farming is not None

    phrases = farming.scope_phrases(locale="zh-CN", subject_value="farming")
    assert "收获" in phrases
    assert "作物" in phrases
    assert all("memory:" not in phrase for phrase in phrases)
    assert all(not any(character.isdigit() for character in phrase) for phrase in phrases)
    assert module.capability_for_kind("not_registered") is None


def test_target_fixture_matches_active_production_without_mutating_registry() -> None:
    """离线评测副本与已激活生产目录逐值一致，但仍保持独立不可变对象。"""

    module = _capability_module()
    target = module.build_target_capability_registry()

    assert {item.status for item in target} == {"active"}
    assert module.domain_tool_names(target) == frozenset(
        {"get_npc_history", "get_player_progression", "get_world_progression"}
    )
    assert target == module.MEMORY_CAPABILITIES
    assert target is not module.MEMORY_CAPABILITIES
    assert {item.status for item in module.MEMORY_CAPABILITIES} == {"active"}


def test_registry_values_and_nested_scope_maps_are_immutable() -> None:
    """运行中不能局部改 capability 或 scope，避免描述与分类悄然漂移。"""

    module = _capability_module()
    capability = module.capability_for_kind("skill_level_reached")
    assert capability is not None

    with pytest.raises(FrozenInstanceError):
        capability.status = "active"
    with pytest.raises(TypeError):
        capability.retrieval_scope_phrases_by_locale["zh-CN"] = {}  # type: ignore[index]


def test_static_version_axes_are_non_blank_and_independent() -> None:
    """producer、分类和检索策略各自拥有非空版本，不能共用含糊总版本。"""

    module = _capability_module()
    versions = {
        module.EVENT_PRODUCER_CAPABILITY_VERSION,
        module.MEMORY_CLASSIFICATION_VERSION,
        module.MEMORY_RETRIEVAL_POLICY_VERSION,
    }

    assert len(versions) == 3
    assert all(isinstance(value, str) and value == value.strip() and value for value in versions)
