"""领域 memory 候选的静态 scope、bucket 与稳定排序纯函数测试。"""

from __future__ import annotations

from dataclasses import replace

import pytest

from stardew_npc_agent.memory_capabilities import MEMORY_CAPABILITIES
from stardew_npc_agent.memory_retrieval import (
    DomainMemoryCandidate,
    rank_domain_memory_candidates,
)


def _candidate(
    memory_id: str,
    *,
    domain: str = "player_progression",
    kind: str = "skill_level_reached",
    namespace: str = "skill_id",
    subject: str = "farming",
    day: int = 8,
    importance: float = 0.5,
    use_count: int = 0,
    summary: str | None = None,
) -> DomainMemoryCandidate:
    """构造一个已经通过 repository 硬过滤的纯候选。"""

    return DomainMemoryCandidate(
        memory_id=memory_id,
        event_id=f"event:{memory_id}",
        event_type=kind,
        summary=summary or f"summary:{memory_id}",
        tags=(),
        occurred_day_index=day,
        importance=importance,
        use_count=use_count,
        memory_domain=domain,
        memory_kind=kind,
        subject_namespace=namespace,
        subject_value=subject,
        visibility_scope="npc:Abigail" if domain == "npc_history" else "public",
    )


def test_scope_matching_uses_nfkc_casefold_and_overrides_higher_importance_fallback() -> None:
    """全角大写 HARVEST 必须命中 farming，且 scope 命中优先于无命中的高 importance。"""

    farming = _candidate("memory:farming", importance=0.1)
    combat = _candidate(
        "memory:combat",
        subject="combat",
        importance=1.0,
    )

    ranked = rank_domain_memory_candidates(
        [combat, farming],
        source_dialogue_text="Time to ＨＡＲＶＥＳＴ the crops.",
        locale="en",
        limit=5,
    )

    assert [item.memory_id for item in ranked] == ["memory:farming", "memory:combat"]


@pytest.mark.parametrize(
    ("source_text", "expected_subject"),
    [
        ("今年的收获和作物看起来真不错。", "farming"),
        ("你的剑能对付矿洞里的怪物吗？", "combat"),
    ],
)
def test_chinese_skill_scope_selects_farming_or_combat(
    source_text: str,
    expected_subject: str,
) -> None:
    """Sebastian 收获语境与 Abigail 剑/怪物语境必须分别召回对应技能 bucket。"""

    candidates = [
        _candidate("memory:farming", subject="farming", importance=0.5),
        _candidate("memory:combat", subject="combat", importance=0.5),
    ]

    ranked = rank_domain_memory_candidates(
        candidates,
        source_dialogue_text=source_text,
        locale="zh-CN",
        limit=1,
    )

    assert ranked[0].subject_value == expected_subject


@pytest.mark.parametrize(
    ("source_text", "expected_memory_id"),
    [
        ("秋天到了。又是收获季，对吧？", "memory:farming"),
        ("真希望我有勇气拿着剑去山洞里探险。", "memory:combat"),
    ],
    ids=["sebastian-harvest-regression", "abigail-sword-cave-regression"],
)
def test_captured_provider_regressions_recall_expected_skill_at_top_one(
    source_text: str,
    expected_memory_id: str,
) -> None:
    """两条真实故障输入不再依赖模型猜 terms，也不能被高 importance 干扰项挤出。"""

    candidates = [
        _candidate("memory:farming", subject="farming", importance=0.2),
        _candidate("memory:combat", subject="combat", importance=0.2),
        _candidate(
            "memory:tool",
            kind="tool_upgrade_received",
            namespace="tool_id",
            subject="watering_can",
            importance=1.0,
        ),
        _candidate(
            "memory:mine",
            kind="mine_depth_milestone_reached",
            namespace="mine_id",
            subject="the_mines",
            importance=0.9,
        ),
    ]

    ranked = rank_domain_memory_candidates(
        candidates,
        source_dialogue_text=source_text,
        locale="zh-CN",
        limit=5,
    )

    assert ranked[0].memory_id == expected_memory_id
    assert expected_memory_id in {item.memory_id for item in ranked[:5]}


@pytest.mark.parametrize(
    ("source_text", "facility_id"),
    [
        ("温室里的作物不受季节影响。", "greenhouse"),
        ("矿车站今天很方便。", "minecarts"),
        ("坐巴士去沙漠吧。", "bus_service"),
        ("采石场那座桥修好了。", "quarry_bridge"),
        ("闪光巨石不见后可以淘金了。", "glittering_boulder"),
    ],
)
def test_five_public_facilities_have_distinct_static_scope(
    source_text: str,
    facility_id: str,
) -> None:
    """五项设施不能退化成同一个全局 world bucket。"""

    facilities = [
        _candidate(
            f"memory:{subject}",
            domain="world_progression",
            kind="public_facility_restored",
            namespace="facility_id",
            subject=subject,
        )
        for subject in (
            "greenhouse",
            "minecarts",
            "bus_service",
            "quarry_bridge",
            "glittering_boulder",
        )
    ]

    ranked = rank_domain_memory_candidates(
        facilities,
        source_dialogue_text=source_text,
        locale="zh-CN",
        limit=1,
    )

    assert ranked[0].subject_value == facility_id


def test_scope_never_reads_memory_summary() -> None:
    """事实摘要含“收获”不能在原台词无命中时偷偷变成 routing signal。"""

    summary_only = _candidate(
        "memory:summary-only",
        subject="farming",
        importance=0.1,
        summary="这里写着收获和作物，但它不是可信原台词。",
    )
    fallback = _candidate(
        "memory:fallback",
        subject="combat",
        importance=0.9,
        summary="无关摘要。",
    )

    ranked = rank_domain_memory_candidates(
        [summary_only, fallback],
        source_dialogue_text="今天天气不错。",
        locale="zh-CN",
        limit=2,
    )

    assert [item.memory_id for item in ranked] == ["memory:fallback", "memory:summary-only"]


def test_bucket_representatives_preserve_diversity_and_top_five_deterministically() -> None:
    """同一 farming bucket 的旧等级不能挤掉其他领域代表，结果固定为五条。"""

    candidates = [
        _candidate("memory:farming-old", subject="farming", day=5, importance=1.0),
        # bucket 内日期先于 importance，因此较新的 0.95 记录代表 farming；
        # 随后 bucket 间按 representative importance 排序，它应稳定排在首位。
        _candidate("memory:farming-new", subject="farming", day=9, importance=0.95),
        _candidate("memory:combat", subject="combat", importance=0.9),
        _candidate("memory:fishing", subject="fishing", importance=0.8),
        _candidate(
            "memory:mine",
            kind="mine_depth_milestone_reached",
            namespace="mine_id",
            subject="the_mines",
            importance=0.7,
        ),
        _candidate(
            "memory:tool",
            kind="tool_upgrade_received",
            namespace="tool_id",
            subject="axe",
            importance=0.6,
        ),
        _candidate(
            "memory:facility",
            domain="world_progression",
            kind="public_facility_restored",
            namespace="facility_id",
            subject="greenhouse",
            importance=0.5,
        ),
    ]

    first = rank_domain_memory_candidates(
        candidates,
        source_dialogue_text="没有任何已注册主题。",
        locale="zh-CN",
        limit=5,
    )
    second = rank_domain_memory_candidates(
        list(reversed(candidates)),
        source_dialogue_text="没有任何已注册主题。",
        locale="zh-CN",
        limit=5,
    )

    assert first == second
    assert [item.memory_id for item in first] == [
        "memory:farming-new",
        "memory:combat",
        "memory:fishing",
        "memory:mine",
        "memory:tool",
    ]
    assert "memory:farming-old" not in {item.memory_id for item in first}


def test_remaining_pool_refills_only_to_registry_bucket_cap() -> None:
    """只有显式把 gift bucket cap 提高到 2 时，同 bucket 第二条才可回填。"""

    target_registry = tuple(
        replace(item, max_candidates_per_bucket=2) if item.kind == "gift_given" else item
        for item in MEMORY_CAPABILITIES
    )
    gifts = [
        _candidate(
            "memory:gift-new",
            domain="npc_history",
            kind="gift_given",
            namespace="item_id",
            subject="(O)66",
            day=9,
        ),
        _candidate(
            "memory:gift-old",
            domain="npc_history",
            kind="gift_given",
            namespace="item_id",
            subject="(O)72",
            day=7,
        ),
        _candidate(
            "memory:gift-oldest",
            domain="npc_history",
            kind="gift_given",
            namespace="item_id",
            subject="(O)74",
            day=5,
        ),
    ]

    ranked = rank_domain_memory_candidates(
        gifts,
        source_dialogue_text="那份礼物我还记得。",
        locale="zh-CN",
        limit=5,
        registry=target_registry,
    )

    assert [item.memory_id for item in ranked] == ["memory:gift-new", "memory:gift-old"]
