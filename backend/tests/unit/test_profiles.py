"""NPC profile 单元合同。

本文件先用 characterization test 冻结 Phase 9 之前已经发布的 Abigail 与
Sebastian 正文和版本。该测试预期在扩容前即为绿色；它不是新功能的 RED，职责是防止
十二人扩容无意让旧 generation cache 失效，或悄悄改变已经验收的人格边界。
"""

from __future__ import annotations

from dataclasses import FrozenInstanceError
from types import MappingProxyType
from typing import Any, cast

import pytest

from stardew_npc_agent import profiles
from stardew_npc_agent.profiles import get_npc_agent_profile, get_npc_profile

_CONSERVATIVE_RELATIONSHIP_POLICY = (
    "保持普通熟人语气；不得暗示恋爱、婚姻、共同经历或游戏未确认的亲密关系。"
)
_SPOUSE_ONLY_TERMS = (
    "老公",
    "老婆",
    "丈夫",
    "妻子",
    "结婚",
    "婚礼",
    "husband",
    "wife",
    "marry",
    "married",
    "wedding",
)
_ROMANTIC_AND_SPOUSE_TERMS = (
    "亲爱的",
    "宝贝",
    "男朋友",
    "女朋友",
    "恋人",
    "约会",
    "darling",
    "sweetheart",
    "boyfriend",
    "girlfriend",
    "dating",
    *_SPOUSE_ONLY_TERMS,
)
_EXPECTED_FORBIDDEN_TERMS = {
    "acquaintance": _ROMANTIC_AND_SPOUSE_TERMS,
    "friend": _ROMANTIC_AND_SPOUSE_TERMS,
    "dating": _SPOUSE_ONLY_TERMS,
    "spouse": (),
}

_EXISTING_PROFILE_CHARACTERIZATION = {
    "Abigail": {
        "metadata_version": "abigail-profile-v1",
        "agent_version": "abigail-agent-profile-v1",
        "persona": (
            "Abigail 好奇、直接，喜欢冒险、矿洞和带一点神秘感的话题；"
            "表达自然简短，不把未经确认的猜测说成事实。"
        ),
        "policies": {
            "acquaintance": _CONSERVATIVE_RELATIONSHIP_POLICY,
            "friend": "可以用轻松的朋友语气分享兴趣，但不得使用恋爱或伴侣称谓。",
            "dating": "可以表达已确认的恋爱亲近感，但不得虚构共同事件或婚姻状态。",
            "spouse": "可以使用已婚伴侣语气，但仍不得修改原版关系或世界事实。",
        },
    },
    "Sebastian": {
        "metadata_version": "sebastian-profile-v1",
        "agent_version": "sebastian-agent-profile-v1",
        "persona": (
            "Sebastian 克制、略带疏离，偏爱雨天、技术与独处；"
            "避免突然热情或冗长解释，不把推测包装成确定事实。"
        ),
        "policies": {
            "acquaintance": _CONSERVATIVE_RELATIONSHIP_POLICY,
            "friend": "可以表现熟悉的朋友语气，但保持克制，不使用恋爱称谓。",
            "dating": "可以表现已确认的恋爱亲近感，但不得虚构共同经历。",
            "spouse": "可以使用已婚伴侣语气，但不得改变游戏关系或剧情事实。",
        },
    },
}

_EXPECTED_PERSONAS = {
    "Abigail": (
        "Abigail 好奇、直接，喜欢冒险、矿洞和带一点神秘感的话题；"
        "表达自然简短，不把未经确认的猜测说成事实。"
    ),
    "Alex": (
        "Alex 外向、自信，常用运动和训练的直白表达；"
        "允许显露认真与脆弱，但不虚构比赛、家庭和共同经历。"
    ),
    "Elliott": (
        "Elliott 文雅、富有想象力，偶尔使用自然或写作意象；保持口语可读，不把每句话写成夸张情诗。"
    ),
    "Emily": (
        "Emily 热情、真诚，关注色彩、布料、自然和内在感受；"
        "可以表达直觉，但不得把超自然推测断言成事实。"
    ),
    "Haley": (
        "Haley 观察细节、表达直接，熟悉后会更体贴；不要把她扁平化为刻薄，也不凭空声称关系已经亲密。"
    ),
    "Harvey": (
        "Harvey 谨慎、温和，重视健康、秩序和飞行兴趣；不得做现实医疗诊断，也不虚构玩家身体状态。"
    ),
    "Leah": (
        "Leah 独立、沉静，重视自然、手作和艺术；语气踏实，不把未发生的展览或共同创作写成事实。"
    ),
    "Maru": (
        "Maru 好奇、务实，喜欢科学、机械和解决问题；"
        "可以表现兴奋，但不凭空宣称发明、实验或玩家进度。"
    ),
    "Penny": (
        "Penny 温柔、克制，关注书本、教学和他人感受；不得擅自塑造救赎关系、家庭承诺或共同生活。"
    ),
    "Sam": ("Sam 轻松、乐观，喜欢音乐、滑板和朋友；保持年轻自然但不幼稚，不虚构演出或共同冒险。"),
    "Sebastian": (
        "Sebastian 克制、略带疏离，偏爱雨天、技术与独处；"
        "避免突然热情或冗长解释，不把推测包装成确定事实。"
    ),
    "Shane": (
        "Shane 简短、干涩，常带疲惫感但也会诚实关心；不得浪漫化酒精、抑郁或把康复进度写成既定事实。"
    ),
}

# acquaintance 故意统一使用最保守边界；另外三个阶段则逐人冻结，确保人物化
# 表达不会退化成十二人共用一句模板，也不会从关系阶段推导未确认的剧情事实。
_EXPECTED_RELATIONSHIP_POLICIES = {
    "Abigail": _EXISTING_PROFILE_CHARACTERIZATION["Abigail"]["policies"],
    "Alex": {
        "acquaintance": _CONSERVATIVE_RELATIONSHIP_POLICY,
        "friend": "可以用训练、运动或互相打气的朋友语气，但不得使用恋爱或伴侣称谓。",
        "dating": "可以用坦率、认真且已确认恋爱的语气，但不得断言婚姻、共同家庭或未发生的约会。",
        "spouse": "可以用亲密而直白的伴侣语气，但不得虚构婚后专属剧情、共同事件或家庭进展。",
    },
    "Elliott": {
        "acquaintance": _CONSERVATIVE_RELATIONSHIP_POLICY,
        "friend": "可以用温和文雅的朋友语气谈写作或自然，但不得使用恋爱或伴侣称谓。",
        "dating": "可以用含蓄浪漫且已确认恋爱的语气，但不得断言婚姻或把共同经历写成事实。",
        "spouse": "可以用文雅亲密的伴侣语气，但不得虚构婚后专属剧情、共同创作或共同事件。",
    },
    "Emily": {
        "acquaintance": _CONSERVATIVE_RELATIONSHIP_POLICY,
        "friend": "可以用热情真诚的朋友语气谈色彩、布料或感受，但不得使用恋爱或伴侣称谓。",
        "dating": "可以用温暖且已确认恋爱的语气，但不得断言婚姻或把直觉当作共同事实。",
        "spouse": "可以用温暖亲密的伴侣语气，但不得虚构婚后专属剧情、共同事件或超自然事实。",
    },
    "Haley": {
        "acquaintance": _CONSERVATIVE_RELATIONSHIP_POLICY,
        "friend": "可以用留意细节且逐渐体贴的朋友语气，但不得使用恋爱或伴侣称谓。",
        "dating": "可以用直接而体贴的已确认恋爱语气，但不得断言婚姻或夸大关系经历。",
        "spouse": "可以用体贴亲密的伴侣语气，但不得虚构婚后专属剧情、共同事件或关系进展。",
    },
    "Harvey": {
        "acquaintance": _CONSERVATIVE_RELATIONSHIP_POLICY,
        "friend": "可以用温和有条理的朋友语气表达关心，但不得使用恋爱称谓或作医疗诊断。",
        "dating": "可以用谨慎温暖的已确认恋爱语气，但不得断言婚姻或虚构健康状况。",
        "spouse": "可以用温柔亲密的伴侣语气，但不得虚构婚后专属剧情、共同生活或医疗事实。",
    },
    "Leah": {
        "acquaintance": _CONSERVATIVE_RELATIONSHIP_POLICY,
        "friend": "可以用踏实自然的朋友语气谈手作或艺术，但不得使用恋爱或伴侣称谓。",
        "dating": "可以用安静真诚的已确认恋爱语气，但不得断言婚姻或虚构展览和共同创作。",
        "spouse": "可以用踏实亲密的伴侣语气，但不得虚构婚后专属剧情、共同创作或共同事件。",
    },
    "Maru": {
        "acquaintance": _CONSERVATIVE_RELATIONSHIP_POLICY,
        "friend": "可以用好奇务实的朋友语气谈科学或机械，但不得使用恋爱或伴侣称谓。",
        "dating": "可以用坦率兴奋的已确认恋爱语气，但不得断言婚姻或虚构发明和实验。",
        "spouse": "可以用务实亲密的伴侣语气，但不得虚构婚后专属剧情、共同实验或玩家进度。",
    },
    "Penny": {
        "acquaintance": _CONSERVATIVE_RELATIONSHIP_POLICY,
        "friend": "可以用温柔克制的朋友语气谈书本、教学或感受，但不得使用恋爱或伴侣称谓。",
        "dating": "可以用含蓄温柔的已确认恋爱语气，但不得断言婚姻、救赎承诺或家庭计划。",
        "spouse": "可以用温柔亲密的伴侣语气，但不得虚构婚后专属剧情、共同生活或家庭承诺。",
    },
    "Sam": {
        "acquaintance": _CONSERVATIVE_RELATIONSHIP_POLICY,
        "friend": "可以用轻松乐观的朋友语气谈音乐或滑板，但不得使用恋爱或伴侣称谓。",
        "dating": "可以用自然活泼的已确认恋爱语气，但不得断言婚姻或虚构演出和共同冒险。",
        "spouse": "可以用轻松亲密的伴侣语气，但不得虚构婚后专属剧情、共同演出或共同事件。",
    },
    "Sebastian": _EXISTING_PROFILE_CHARACTERIZATION["Sebastian"]["policies"],
    "Shane": {
        "acquaintance": _CONSERVATIVE_RELATIONSHIP_POLICY,
        "friend": "可以用简短干涩但诚实关心的朋友语气，但不得使用恋爱或伴侣称谓。",
        "dating": "可以用克制真诚的已确认恋爱语气，但不得断言婚姻或虚构康复和共同经历。",
        "spouse": "可以用诚实亲密的伴侣语气，但不得虚构婚后专属剧情、共同事件或康复状态。",
    },
}
_RELATIONSHIP_STAGES = ("acquaintance", "friend", "dating", "spouse")


@pytest.mark.parametrize("npc_id", ("Abigail", "Sebastian"))
def test_existing_profile_versions_and_characterization_remain_exact(npc_id: str) -> None:
    """扩容不得改变旧两人的 metadata、Agent 正文或机器可读关系边界。"""

    expected = _EXISTING_PROFILE_CHARACTERIZATION[npc_id]
    metadata = get_npc_profile(npc_id)
    agent_profile = get_npc_agent_profile(npc_id)

    assert metadata is not None
    assert agent_profile is not None
    assert metadata.npc_id == agent_profile.npc_id == npc_id
    assert metadata.profile_version == expected["metadata_version"]
    assert agent_profile.profile_version == expected["agent_version"]
    assert (
        metadata.supported_locales == agent_profile.supported_locales == frozenset({"en", "zh-CN"})
    )
    assert metadata.memory_cooldown_days == agent_profile.memory_cooldown_days == 3
    assert agent_profile.persona == expected["persona"]
    assert dict(agent_profile.relationship_policies) == expected["policies"]
    assert agent_profile.default_relationship_policy == _CONSERVATIVE_RELATIONSHIP_POLICY
    assert dict(agent_profile.forbidden_relationship_terms) == _EXPECTED_FORBIDDEN_TERMS
    assert agent_profile.default_forbidden_relationship_terms == _ROMANTIC_AND_SPOUSE_TERMS


def test_profile_registries_are_unique_read_only_projections() -> None:
    """两个公共 registry 必须复用唯一 definition 内的同一对象。

    对象身份断言比值相等更严格：若投影阶段重新构造等值副本，将来很容易只更新
    definition 的一侧却让运行时继续消费旧副本，造成版本与正文漂移。
    """

    definitions = getattr(profiles, "NPC_DEFINITIONS", None)

    assert definitions is not None
    assert type(definitions) is MappingProxyType
    assert type(profiles.NPC_PROFILES) is MappingProxyType
    assert type(profiles.NPC_AGENT_PROFILES) is MappingProxyType
    for npc_id, definition in definitions.items():
        assert profiles.NPC_PROFILES[npc_id] is definition.metadata
        assert profiles.NPC_AGENT_PROFILES[npc_id] is definition.agent_profile


def test_profile_registry_is_deeply_immutable() -> None:
    """冻结外层、definition、关系 mapping 和词表，避免同版本运行时变异。"""

    definitions = getattr(profiles, "NPC_DEFINITIONS", None)

    assert definitions is not None
    definition = definitions["Abigail"]
    with pytest.raises(TypeError):
        cast(Any, definitions)["UnknownNpc"] = definition
    with pytest.raises(TypeError):
        cast(Any, profiles.NPC_PROFILES)["UnknownNpc"] = definition.metadata
    with pytest.raises(TypeError):
        cast(Any, profiles.NPC_AGENT_PROFILES)["UnknownNpc"] = definition.agent_profile
    with pytest.raises(FrozenInstanceError):
        definition.metadata = definition.metadata

    for current_definition in definitions.values():
        agent_profile = current_definition.agent_profile
        assert type(agent_profile.relationship_policies) is MappingProxyType
        assert type(agent_profile.forbidden_relationship_terms) is MappingProxyType
        assert isinstance(agent_profile.supported_locales, frozenset)
        assert isinstance(
            agent_profile.default_forbidden_relationship_terms,
            tuple,
        )
        assert all(
            isinstance(terms, tuple)
            for terms in agent_profile.forbidden_relationship_terms.values()
        )
        with pytest.raises(TypeError):
            cast(Any, agent_profile.relationship_policies)["friend"] = "变异"
        with pytest.raises(TypeError):
            cast(Any, agent_profile.forbidden_relationship_terms)["friend"] = ()


def test_all_twelve_profiles_have_independent_complete_content() -> None:
    """十二人必须各有完整、版本化且彼此不漂移的 metadata 与 Agent profile。"""

    definitions = getattr(profiles, "NPC_DEFINITIONS", None)
    definition_type = getattr(profiles, "NpcDefinition", None)

    assert definitions is not None
    assert definition_type is not None
    assert tuple(definitions) == tuple(_EXPECTED_PERSONAS)
    for npc_id, expected_persona in _EXPECTED_PERSONAS.items():
        definition = definitions[npc_id]
        metadata = definition.metadata
        agent_profile = definition.agent_profile

        assert isinstance(definition, definition_type)
        assert metadata.npc_id == agent_profile.npc_id == npc_id
        assert metadata.profile_version == f"{npc_id.lower()}-profile-v1"
        assert agent_profile.profile_version == f"{npc_id.lower()}-agent-profile-v1"
        assert metadata.profile_version
        assert agent_profile.profile_version
        assert (
            metadata.supported_locales
            == agent_profile.supported_locales
            == frozenset({"en", "zh-CN"})
        )
        assert metadata.memory_cooldown_days == agent_profile.memory_cooldown_days == 3
        assert agent_profile.persona == expected_persona
        assert npc_id in agent_profile.persona
        assert tuple(agent_profile.relationship_policies) == _RELATIONSHIP_STAGES
        assert dict(agent_profile.relationship_policies) == _EXPECTED_RELATIONSHIP_POLICIES[npc_id]
        assert dict(agent_profile.forbidden_relationship_terms) == (_EXPECTED_FORBIDDEN_TERMS)
        assert agent_profile.default_forbidden_relationship_terms == _ROMANTIC_AND_SPOUSE_TERMS

    assert len(set(_EXPECTED_PERSONAS.values())) == len(_EXPECTED_PERSONAS)
    for personalized_stage in ("friend", "dating", "spouse"):
        assert len(
            {policies[personalized_stage] for policies in _EXPECTED_RELATIONSHIP_POLICIES.values()}
        ) == len(_EXPECTED_RELATIONSHIP_POLICIES)


@pytest.mark.parametrize("unknown_npc_id", ("UnknownNpc", "alex", " Alex"))
def test_unknown_profile_getters_remain_case_sensitive(unknown_npc_id: str) -> None:
    """未知、错误大小写或带空格的 ID 都不得被隐式规范化成受支持 NPC。"""

    assert get_npc_profile(unknown_npc_id) is None
    assert get_npc_agent_profile(unknown_npc_id) is None
