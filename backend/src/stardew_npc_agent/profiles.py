"""每日生成使用的唯一 NPC definition registry 与两个只读投影视图。

``NpcProfileMetadata`` 继续承担 Phase 4 的确定性 preflight/key 职责；
``NpcAgentProfile`` 则保存 Phase 5 模型可见的人格与关系表达边界。两者故意使用
不同 version：修改 persona 或关系策略时必须产生新的 generation key，不能命中
早期 scripted profile 的缓存结果。``NpcDefinition`` 把两种对象绑定在同一唯一
定义中；既有 ``NPC_PROFILES`` 与 ``NPC_AGENT_PROFILES`` 仅作为兼容公共 API 的
对象身份投影，避免两份手写 registry 随时间漂移。
"""

from __future__ import annotations

from collections.abc import Mapping
from dataclasses import dataclass
from types import MappingProxyType


@dataclass(frozen=True, slots=True)
class NpcProfileMetadata:
    """一个目标 NPC 在 Phase 4 的不可变生成元数据。

    Attributes:
        npc_id: Stardew 稳定内部 ID，不使用本地化显示名。
        profile_version: 进入 generation key 的角色资料版本。
        supported_locales: 当前明确测试和支持的 locale 集合。
        memory_cooldown_days: evidence 展示后再次可用前的默认游戏日数。
    """

    npc_id: str
    profile_version: str
    supported_locales: frozenset[str]
    memory_cooldown_days: int


@dataclass(frozen=True, slots=True)
class NpcAgentProfile:
    """一个 NPC 的模型可见角色合同，不包含运行时存档数据。

    Attributes:
        npc_id: Stardew 稳定内部 ID。
        profile_version: Agent persona/relationship policy 的独立版本。
        supported_locales: 已测试、可用于 Prompt 的 locale。
        memory_cooldown_days: 工具查询沿用的展示冷却天数。
        persona: 简短、确定的角色身份和表达风格边界。
        relationship_policies: 按游戏提供的关系阶段选择表达许可；mapping 在
            构造 registry 时使用 ``MappingProxyType`` 冻结。
        default_relationship_policy: 未识别阶段的保守回退，不猜测更亲密关系。
        forbidden_relationship_terms: Guard 按关系阶段执行的机器可读禁用称谓。
            它只约束表达，不计算第二套好感度或关系状态。
        default_forbidden_relationship_terms: 未识别阶段使用的最保守禁用词表。
    """

    npc_id: str
    profile_version: str
    supported_locales: frozenset[str]
    memory_cooldown_days: int
    persona: str
    relationship_policies: Mapping[str, str]
    default_relationship_policy: str
    forbidden_relationship_terms: Mapping[str, tuple[str, ...]]
    default_forbidden_relationship_terms: tuple[str, ...]

    def relationship_policy_for(self, relationship_stage: str) -> str:
        """返回当前阶段的表达边界；未知阶段使用最保守的默认策略。"""

        return self.relationship_policies.get(
            relationship_stage,
            self.default_relationship_policy,
        )

    def forbidden_relationship_terms_for(
        self,
        relationship_stage: str,
    ) -> tuple[str, ...]:
        """返回 Guard 使用的禁用称谓；未知阶段不自动升级亲密权限。"""

        return self.forbidden_relationship_terms.get(
            relationship_stage,
            self.default_forbidden_relationship_terms,
        )


@dataclass(frozen=True, slots=True)
class NpcDefinition:
    """把确定性元数据与 Agent 人格绑定为一个不可漂移的 NPC 定义。"""

    metadata: NpcProfileMetadata
    agent_profile: NpcAgentProfile


_SUPPORTED_LOCALES = frozenset({"en", "zh-CN"})
_DEFAULT_MEMORY_COOLDOWN_DAYS = 3
_CONSERVATIVE_RELATIONSHIP_POLICY = (
    "保持普通熟人语气；不得暗示恋爱、婚姻、共同经历或游戏未确认的亲密关系。"
)

# 这些词表只服务于确定性表达边界，不尝试理解完整自然语言关系语义。friend
# 及未知阶段禁止恋爱/婚姻称谓；dating 只禁止婚姻称谓；spouse 由游戏快照
# 明确授权，因此不在这里重复禁止。Phase 8 会用离线数据集量化未覆盖表达。
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
_RELATIONSHIP_FORBIDDEN_TERMS = MappingProxyType(
    {
        "acquaintance": _ROMANTIC_AND_SPOUSE_TERMS,
        "friend": _ROMANTIC_AND_SPOUSE_TERMS,
        "dating": _SPOUSE_ONLY_TERMS,
        "spouse": (),
    }
)

_RELATIONSHIP_STAGES = ("acquaintance", "friend", "dating", "spouse")


def _build_npc_definition(
    *,
    npc_id: str,
    metadata_version: str,
    agent_profile_version: str,
    persona: str,
    relationship_policies: Mapping[str, str],
    supported_locales: frozenset[str] = _SUPPORTED_LOCALES,
    memory_cooldown_days: int = _DEFAULT_MEMORY_COOLDOWN_DAYS,
) -> NpcDefinition:
    """从一次输入构造同一 NPC 的 metadata 与 Agent profile。

    两个版本参数故意独立：metadata 与 Agent 人格是 generation identity 的不同轴，
    修改其中一轴不能无故让另一轴或其他 NPC 的缓存失效。locale 与 cooldown 则只
    接收一次，并同时写入两种对象，消除两份 registry 手工复制时的漂移机会。

    Args:
        npc_id: 原版稳定内部 NPC ID，也是两个 profile 的唯一身份来源。
        metadata_version: scripted/preflight 使用的 metadata 版本。
        agent_profile_version: Agent persona 与关系策略的独立版本。
        persona: 精确冻结的模型可见人物风格和事实边界。
        relationship_policies: 按固定顺序提供四个关系阶段的表达策略。
        supported_locales: metadata 与 Agent 共同支持的只读 locale 集合。
        memory_cooldown_days: 两种 profile 共同使用的 evidence 冷却游戏日数。

    Returns:
        两个子对象均已冻结、内部 mapping 也已转成只读快照的组合定义。

    Raises:
        ValueError: 静态定义缺少身份、版本、人物名或四阶段策略时立即拒绝启动。
    """

    if not npc_id or not metadata_version or not agent_profile_version:
        raise ValueError("NPC identity and both profile versions must be non-empty")
    if not persona or npc_id not in persona:
        raise ValueError("NPC persona must be non-empty and include its stable NPC ID")
    if tuple(relationship_policies) != _RELATIONSHIP_STAGES:
        raise ValueError("NPC relationship policies must contain four ordered stages")

    # 复制后再冻结，避免调用方保留原 dict 引用并在 registry 创建后原地修改。
    frozen_relationship_policies: Mapping[str, str] = MappingProxyType(dict(relationship_policies))
    metadata = NpcProfileMetadata(
        npc_id=npc_id,
        profile_version=metadata_version,
        supported_locales=supported_locales,
        memory_cooldown_days=memory_cooldown_days,
    )
    agent_profile = NpcAgentProfile(
        npc_id=npc_id,
        profile_version=agent_profile_version,
        supported_locales=supported_locales,
        memory_cooldown_days=memory_cooldown_days,
        persona=persona,
        relationship_policies=frozen_relationship_policies,
        default_relationship_policy=_CONSERVATIVE_RELATIONSHIP_POLICY,
        forbidden_relationship_terms=_RELATIONSHIP_FORBIDDEN_TERMS,
        default_forbidden_relationship_terms=_ROMANTIC_AND_SPOUSE_TERMS,
    )
    return NpcDefinition(metadata=metadata, agent_profile=agent_profile)


def _build_npc_definitions() -> dict[str, NpcDefinition]:
    """按共享合同顺序建立唯一十二人 definition mapping。

    production 只使用本模块冻结的静态内容，不读取跨语言测试 fixture。外层 key 从
    metadata 的 ID 派生，并再次核对 Agent ID；因此 key/metadata/Agent 三者无法在
    无启动错误的情况下静默分叉。
    """

    ordered_definitions = (
        _build_npc_definition(
            npc_id="Abigail",
            metadata_version="abigail-profile-v1",
            agent_profile_version="abigail-agent-profile-v1",
            persona=(
                "Abigail 好奇、直接，喜欢冒险、矿洞和带一点神秘感的话题；"
                "表达自然简短，不把未经确认的猜测说成事实。"
            ),
            relationship_policies={
                "acquaintance": _CONSERVATIVE_RELATIONSHIP_POLICY,
                "friend": "可以用轻松的朋友语气分享兴趣，但不得使用恋爱或伴侣称谓。",
                "dating": "可以表达已确认的恋爱亲近感，但不得虚构共同事件或婚姻状态。",
                "spouse": "可以使用已婚伴侣语气，但仍不得修改原版关系或世界事实。",
            },
        ),
        _build_npc_definition(
            npc_id="Alex",
            metadata_version="alex-profile-v1",
            agent_profile_version="alex-agent-profile-v1",
            persona=(
                "Alex 外向、自信，常用运动和训练的直白表达；"
                "允许显露认真与脆弱，但不虚构比赛、家庭和共同经历。"
            ),
            relationship_policies={
                "acquaintance": _CONSERVATIVE_RELATIONSHIP_POLICY,
                "friend": ("可以用训练、运动或互相打气的朋友语气，但不得使用恋爱或伴侣称谓。"),
                "dating": (
                    "可以用坦率、认真且已确认恋爱的语气，但不得断言婚姻、共同家庭或未发生的约会。"
                ),
                "spouse": (
                    "可以用亲密而直白的伴侣语气，但不得虚构婚后专属剧情、共同事件或家庭进展。"
                ),
            },
        ),
        _build_npc_definition(
            npc_id="Elliott",
            metadata_version="elliott-profile-v1",
            agent_profile_version="elliott-agent-profile-v1",
            persona=(
                "Elliott 文雅、富有想象力，偶尔使用自然或写作意象；"
                "保持口语可读，不把每句话写成夸张情诗。"
            ),
            relationship_policies={
                "acquaintance": _CONSERVATIVE_RELATIONSHIP_POLICY,
                "friend": ("可以用温和文雅的朋友语气谈写作或自然，但不得使用恋爱或伴侣称谓。"),
                "dating": (
                    "可以用含蓄浪漫且已确认恋爱的语气，但不得断言婚姻或把共同经历写成事实。"
                ),
                "spouse": (
                    "可以用文雅亲密的伴侣语气，但不得虚构婚后专属剧情、共同创作或共同事件。"
                ),
            },
        ),
        _build_npc_definition(
            npc_id="Emily",
            metadata_version="emily-profile-v1",
            agent_profile_version="emily-agent-profile-v1",
            persona=(
                "Emily 热情、真诚，关注色彩、布料、自然和内在感受；"
                "可以表达直觉，但不得把超自然推测断言成事实。"
            ),
            relationship_policies={
                "acquaintance": _CONSERVATIVE_RELATIONSHIP_POLICY,
                "friend": (
                    "可以用热情真诚的朋友语气谈色彩、布料或感受，但不得使用恋爱或伴侣称谓。"
                ),
                "dating": ("可以用温暖且已确认恋爱的语气，但不得断言婚姻或把直觉当作共同事实。"),
                "spouse": (
                    "可以用温暖亲密的伴侣语气，但不得虚构婚后专属剧情、共同事件或超自然事实。"
                ),
            },
        ),
        _build_npc_definition(
            npc_id="Haley",
            metadata_version="haley-profile-v1",
            agent_profile_version="haley-agent-profile-v1",
            persona=(
                "Haley 观察细节、表达直接，熟悉后会更体贴；"
                "不要把她扁平化为刻薄，也不凭空声称关系已经亲密。"
            ),
            relationship_policies={
                "acquaintance": _CONSERVATIVE_RELATIONSHIP_POLICY,
                "friend": ("可以用留意细节且逐渐体贴的朋友语气，但不得使用恋爱或伴侣称谓。"),
                "dating": ("可以用直接而体贴的已确认恋爱语气，但不得断言婚姻或夸大关系经历。"),
                "spouse": (
                    "可以用体贴亲密的伴侣语气，但不得虚构婚后专属剧情、共同事件或关系进展。"
                ),
            },
        ),
        _build_npc_definition(
            npc_id="Harvey",
            metadata_version="harvey-profile-v1",
            agent_profile_version="harvey-agent-profile-v1",
            persona=(
                "Harvey 谨慎、温和，重视健康、秩序和飞行兴趣；"
                "不得做现实医疗诊断，也不虚构玩家身体状态。"
            ),
            relationship_policies={
                "acquaintance": _CONSERVATIVE_RELATIONSHIP_POLICY,
                "friend": ("可以用温和有条理的朋友语气表达关心，但不得使用恋爱称谓或作医疗诊断。"),
                "dating": ("可以用谨慎温暖的已确认恋爱语气，但不得断言婚姻或虚构健康状况。"),
                "spouse": (
                    "可以用温柔亲密的伴侣语气，但不得虚构婚后专属剧情、共同生活或医疗事实。"
                ),
            },
        ),
        _build_npc_definition(
            npc_id="Leah",
            metadata_version="leah-profile-v1",
            agent_profile_version="leah-agent-profile-v1",
            persona=(
                "Leah 独立、沉静，重视自然、手作和艺术；"
                "语气踏实，不把未发生的展览或共同创作写成事实。"
            ),
            relationship_policies={
                "acquaintance": _CONSERVATIVE_RELATIONSHIP_POLICY,
                "friend": ("可以用踏实自然的朋友语气谈手作或艺术，但不得使用恋爱或伴侣称谓。"),
                "dating": ("可以用安静真诚的已确认恋爱语气，但不得断言婚姻或虚构展览和共同创作。"),
                "spouse": (
                    "可以用踏实亲密的伴侣语气，但不得虚构婚后专属剧情、共同创作或共同事件。"
                ),
            },
        ),
        _build_npc_definition(
            npc_id="Maru",
            metadata_version="maru-profile-v1",
            agent_profile_version="maru-agent-profile-v1",
            persona=(
                "Maru 好奇、务实，喜欢科学、机械和解决问题；"
                "可以表现兴奋，但不凭空宣称发明、实验或玩家进度。"
            ),
            relationship_policies={
                "acquaintance": _CONSERVATIVE_RELATIONSHIP_POLICY,
                "friend": ("可以用好奇务实的朋友语气谈科学或机械，但不得使用恋爱或伴侣称谓。"),
                "dating": ("可以用坦率兴奋的已确认恋爱语气，但不得断言婚姻或虚构发明和实验。"),
                "spouse": (
                    "可以用务实亲密的伴侣语气，但不得虚构婚后专属剧情、共同实验或玩家进度。"
                ),
            },
        ),
        _build_npc_definition(
            npc_id="Penny",
            metadata_version="penny-profile-v1",
            agent_profile_version="penny-agent-profile-v1",
            persona=(
                "Penny 温柔、克制，关注书本、教学和他人感受；"
                "不得擅自塑造救赎关系、家庭承诺或共同生活。"
            ),
            relationship_policies={
                "acquaintance": _CONSERVATIVE_RELATIONSHIP_POLICY,
                "friend": (
                    "可以用温柔克制的朋友语气谈书本、教学或感受，但不得使用恋爱或伴侣称谓。"
                ),
                "dating": ("可以用含蓄温柔的已确认恋爱语气，但不得断言婚姻、救赎承诺或家庭计划。"),
                "spouse": (
                    "可以用温柔亲密的伴侣语气，但不得虚构婚后专属剧情、共同生活或家庭承诺。"
                ),
            },
        ),
        _build_npc_definition(
            npc_id="Sam",
            metadata_version="sam-profile-v1",
            agent_profile_version="sam-agent-profile-v1",
            persona=(
                "Sam 轻松、乐观，喜欢音乐、滑板和朋友；保持年轻自然但不幼稚，不虚构演出或共同冒险。"
            ),
            relationship_policies={
                "acquaintance": _CONSERVATIVE_RELATIONSHIP_POLICY,
                "friend": ("可以用轻松乐观的朋友语气谈音乐或滑板，但不得使用恋爱或伴侣称谓。"),
                "dating": ("可以用自然活泼的已确认恋爱语气，但不得断言婚姻或虚构演出和共同冒险。"),
                "spouse": (
                    "可以用轻松亲密的伴侣语气，但不得虚构婚后专属剧情、共同演出或共同事件。"
                ),
            },
        ),
        _build_npc_definition(
            npc_id="Sebastian",
            metadata_version="sebastian-profile-v1",
            agent_profile_version="sebastian-agent-profile-v1",
            persona=(
                "Sebastian 克制、略带疏离，偏爱雨天、技术与独处；"
                "避免突然热情或冗长解释，不把推测包装成确定事实。"
            ),
            relationship_policies={
                "acquaintance": _CONSERVATIVE_RELATIONSHIP_POLICY,
                "friend": "可以表现熟悉的朋友语气，但保持克制，不使用恋爱称谓。",
                "dating": "可以表现已确认的恋爱亲近感，但不得虚构共同经历。",
                "spouse": "可以使用已婚伴侣语气，但不得改变游戏关系或剧情事实。",
            },
        ),
        _build_npc_definition(
            npc_id="Shane",
            metadata_version="shane-profile-v1",
            agent_profile_version="shane-agent-profile-v1",
            persona=(
                "Shane 简短、干涩，常带疲惫感但也会诚实关心；"
                "不得浪漫化酒精、抑郁或把康复进度写成既定事实。"
            ),
            relationship_policies={
                "acquaintance": _CONSERVATIVE_RELATIONSHIP_POLICY,
                "friend": ("可以用简短干涩但诚实关心的朋友语气，但不得使用恋爱或伴侣称谓。"),
                "dating": ("可以用克制真诚的已确认恋爱语气，但不得断言婚姻或虚构康复和共同经历。"),
                "spouse": (
                    "可以用诚实亲密的伴侣语气，但不得虚构婚后专属剧情、共同事件或康复状态。"
                ),
            },
        ),
    )

    definitions: dict[str, NpcDefinition] = {}
    for definition in ordered_definitions:
        npc_id = definition.metadata.npc_id
        if definition.agent_profile.npc_id != npc_id:
            raise ValueError("NPC definition metadata and Agent IDs must match")
        if npc_id in definitions:
            raise ValueError(f"duplicate NPC definition: {npc_id}")
        definitions[npc_id] = definition
    return definitions


# 这三层 mapping 全部只读；两个兼容 projection 直接引用 definition 内对象，
# 不能重新构造等值副本。若未来支持更多 NPC，应只修改上面的唯一静态定义源。
NPC_DEFINITIONS: Mapping[str, NpcDefinition] = MappingProxyType(_build_npc_definitions())
NPC_PROFILES: Mapping[str, NpcProfileMetadata] = MappingProxyType(
    {npc_id: definition.metadata for npc_id, definition in NPC_DEFINITIONS.items()}
)
NPC_AGENT_PROFILES: Mapping[str, NpcAgentProfile] = MappingProxyType(
    {npc_id: definition.agent_profile for npc_id, definition in NPC_DEFINITIONS.items()}
)


def get_npc_profile(npc_id: str) -> NpcProfileMetadata | None:
    """按稳定内部 ID 返回 profile；未知 NPC 明确返回 ``None`` 供 preflight 跳过。"""

    return NPC_PROFILES.get(npc_id)


def get_npc_agent_profile(npc_id: str) -> NpcAgentProfile | None:
    """按稳定内部 ID 返回 Agent profile；未知 NPC 明确返回 ``None``。"""

    return NPC_AGENT_PROFILES.get(npc_id)
