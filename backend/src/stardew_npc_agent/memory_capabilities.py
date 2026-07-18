"""Memory producer、分类、检索与领域工具共享的静态能力目录。

本模块刻意只保存“当前构建能够理解什么”这类静态事实，不读取数据库、存档、
环境变量或模型。它是后续 wire 投影、memory 分类、Retriever scope 和 Tool
Description 的单一真值，避免四处手写字符串后产生能力漂移。

三个 activation bundle 已完成自动化与获批的单机 producer 验收，因此生产目录
一次性保持 ``active``。测试和离线 Provider 评测仍可通过
:func:`build_target_capability_registry` 获得独立副本；该函数不会修改生产 tuple，
也不会泄露某个存档当前是否拥有对应记忆。
"""

from __future__ import annotations

from collections.abc import Mapping
from dataclasses import dataclass, replace
from types import MappingProxyType
from typing import Literal

EVENT_PRODUCER_CAPABILITY_VERSION = "event-producer-capabilities-v1"
MEMORY_CLASSIFICATION_VERSION = "memory-classification-v1"
MEMORY_RETRIEVAL_POLICY_VERSION = "memory-retrieval-policy-v1"

MemoryDomain = Literal["npc_history", "player_progression", "world_progression"]
CapabilityStatus = Literal["planned", "active"]
AudienceScope = Literal["npc", "public"]

DOMAIN_TOOL_NAMES: Mapping[MemoryDomain, str] = MappingProxyType(
    {
        "npc_history": "get_npc_history",
        "player_progression": "get_player_progression",
        "world_progression": "get_world_progression",
    }
)

# domain 级边界与 kind/subject 能力一样属于构建时静态产品合同。把它们放在
# registry 模块而不是工具函数 docstring，可避免 Provider description 与实际
# active capability 各自维护一套含义。
DOMAIN_NATURAL_LANGUAGE_SCOPE: Mapping[MemoryDomain, Mapping[str, str]] = MappingProxyType(
    {
        "npc_history": MappingProxyType(
            {
                "zh-CN": "当前 NPC 与玩家共同发生、且只有该 NPC 有权知道的私人历史。",
                "en": "Private history shared by the current NPC and player.",
            }
        ),
        "player_progression": MappingProxyType(
            {
                "zh-CN": "玩家自身能力与长期成长相关的公共历史。",
                "en": "Public history about the player's abilities and long-term growth.",
            }
        ),
        "world_progression": MappingProxyType(
            {
                "zh-CN": "多个 NPC 共同可见的世界状态变化与公共设施里程碑。",
                "en": "World-state changes and public facility milestones visible to many NPCs.",
            }
        ),
    }
)
DOMAIN_EXCLUSIONS_ZH_CN: Mapping[MemoryDomain, str] = MappingProxyType(
    {
        "npc_history": "不包含玩家通用成长、公共世界解锁、所有任务或所有剧情互动。",
        "player_progression": "不包含 NPC 私人关系、公共设施、所有成就、职业或任意 Mod 技能。",
        "world_progression": (
            "不包含 NPC 私人关系、玩家技能、完整路线、公告栏、电影院或所有世界事件。"
        ),
    }
)


@dataclass(frozen=True, slots=True)
class LegacyIngestAlias:
    """一个只用于识别既有事实的旧 producer 合同。

    alias 不代表新 producer 仍可继续制造旧事件。后端投影将在严格校验旧
    ``event_type/version/source/payload`` 后，把合法历史事实映射到新 memory kind。
    """

    event_type: str
    event_version: str
    producer_source: str


@dataclass(frozen=True, slots=True)
class MemoryCapability:
    """一类可检索 memory 的完整静态能力声明。

    ``subject_value_policy`` 是面向校验器的稳定策略名；固定枚举同时保存在
    ``allowed_subject_values``。gift 的物品 ID 属于运行时动态事实，因此仅声明
    ``qualified_item_id``，不能把某个存档里的实际物品写入目录。

    两个 locale mapping 在构造时都递归冻结。调用方只能读取，不能在进程运行中
    局部补词或改 Tool Description；任何语义变化都必须修改源码、版本和测试。
    """

    domain: MemoryDomain
    kind: str
    activation_bundle: str
    status: CapabilityStatus
    wire_event_type: str
    wire_event_version: str
    producer_source: str
    legacy_ingest_aliases: tuple[LegacyIngestAlias, ...]
    audience_scope: AudienceScope
    subject_namespace: str
    subject_value_policy: str
    allowed_subject_values: frozenset[str] | None
    natural_language_scope_by_locale: Mapping[str, str]
    retrieval_scope_phrases_by_locale: Mapping[str, Mapping[str, tuple[str, ...]]]
    projection_version: str
    retrieval_bucket_policy: str
    max_candidates_per_bucket: int

    def scope_phrases(self, *, locale: str, subject_value: str) -> tuple[str, ...]:
        """返回 kind 级与 subject 级静态短语，并按首次出现稳定去重。

        缺少 locale 或动态 subject 没有专属短语时，只返回已声明的 kind 级 ``*``
        短语。这里不读取 memory summary，也不根据当前存档生成同义词。
        """

        locale_phrases = self.retrieval_scope_phrases_by_locale.get(locale)
        if locale_phrases is None:
            return ()
        combined = (*locale_phrases.get("*", ()), *locale_phrases.get(subject_value, ()))
        return tuple(dict.fromkeys(combined))


def _frozen_mapping(values: Mapping[str, str]) -> Mapping[str, str]:
    """复制并冻结一层字符串 mapping，隔离构造方后续修改。"""

    return MappingProxyType(dict(values))


def _frozen_scope_mapping(
    values: Mapping[str, Mapping[str, tuple[str, ...]]],
) -> Mapping[str, Mapping[str, tuple[str, ...]]]:
    """递归复制并冻结 locale/subject/phrases 三层静态 scope。"""

    return MappingProxyType(
        {
            locale: MappingProxyType(
                {subject: tuple(phrases) for subject, phrases in subject_map.items()}
            )
            for locale, subject_map in values.items()
        }
    )


def _capability(
    *,
    domain: MemoryDomain,
    kind: str,
    activation_bundle: str,
    wire_event_type: str,
    wire_event_version: str,
    producer_source: str,
    audience_scope: AudienceScope,
    subject_namespace: str,
    subject_value_policy: str,
    allowed_subject_values: frozenset[str] | None,
    natural_language_scope_by_locale: Mapping[str, str],
    retrieval_scope_phrases_by_locale: Mapping[str, Mapping[str, tuple[str, ...]]],
    retrieval_bucket_policy: str,
    legacy_ingest_aliases: tuple[LegacyIngestAlias, ...] = (),
) -> MemoryCapability:
    """以统一默认值构造一条已激活 capability，并冻结全部嵌套字段。"""

    return MemoryCapability(
        domain=domain,
        kind=kind,
        activation_bundle=activation_bundle,
        status="active",
        wire_event_type=wire_event_type,
        wire_event_version=wire_event_version,
        producer_source=producer_source,
        legacy_ingest_aliases=legacy_ingest_aliases,
        audience_scope=audience_scope,
        subject_namespace=subject_namespace,
        subject_value_policy=subject_value_policy,
        allowed_subject_values=allowed_subject_values,
        natural_language_scope_by_locale=_frozen_mapping(natural_language_scope_by_locale),
        retrieval_scope_phrases_by_locale=_frozen_scope_mapping(retrieval_scope_phrases_by_locale),
        projection_version="memory-projection-v3",
        retrieval_bucket_policy=retrieval_bucket_policy,
        max_candidates_per_bucket=1,
    )


_FIVE_SKILLS = frozenset({"farming", "fishing", "foraging", "mining", "combat"})

MEMORY_CAPABILITIES: tuple[MemoryCapability, ...] = (
    _capability(
        domain="npc_history",
        kind="gift_given",
        activation_bundle="npc-history-v1",
        wire_event_type="gift_given",
        wire_event_version="2",
        producer_source="harmony.farmer.on_gift_given",
        audience_scope="npc",
        subject_namespace="item_id",
        subject_value_policy="qualified_item_id",
        allowed_subject_values=None,
        natural_language_scope_by_locale={
            "zh-CN": "当前 NPC 确实接受过的礼物及其喜好反应。",
            "en": "Gifts this NPC actually accepted and their gift taste response.",
        },
        retrieval_scope_phrases_by_locale={
            "zh-CN": {"*": ("礼物", "赠送", "送给", "喜欢", "讨厌")},
            "en": {"*": ("gift", "gave", "present", "liked", "hated")},
        },
        retrieval_bucket_policy="kind",
    ),
    _capability(
        domain="npc_history",
        kind="relationship_status_changed",
        activation_bundle="npc-history-v1",
        wire_event_type="relationship_status_changed",
        wire_event_version="1",
        producer_source="smapi.player.friendship_snapshot",
        audience_scope="npc",
        subject_namespace="relationship_status",
        subject_value_policy="relationship_status_enum",
        allowed_subject_values=frozenset({"friendly", "dating", "engaged", "married", "divorced"}),
        natural_language_scope_by_locale={
            "zh-CN": "玩家与当前 NPC 的交往、订婚、婚姻、分手或离婚历史。",
            "en": "Dating, engagement, marriage, breakup, or divorce history with this NPC.",
        },
        retrieval_scope_phrases_by_locale={
            "zh-CN": {"*": ("关系", "交往", "约会", "订婚", "结婚", "分手", "离婚")},
            "en": {"*": ("relationship", "dating", "engaged", "married", "breakup", "divorce")},
        },
        retrieval_bucket_policy="kind",
    ),
    _capability(
        domain="npc_history",
        kind="friendship_milestone_reached",
        activation_bundle="npc-history-v1",
        wire_event_type="friendship_milestone_reached",
        wire_event_version="1",
        producer_source="smapi.player.friendship_snapshot",
        audience_scope="npc",
        subject_namespace="milestone_id",
        subject_value_policy="friend_only",
        allowed_subject_values=frozenset({"friend"}),
        natural_language_scope_by_locale={
            "zh-CN": "玩家与当前 NPC 首次达到四心朋友里程碑。",
            "en": "The first four-heart friendship milestone with this NPC.",
        },
        retrieval_scope_phrases_by_locale={
            "zh-CN": {"*": ("朋友", "友情", "关系")},
            "en": {"*": ("friend", "friendship", "relationship")},
        },
        retrieval_bucket_policy="kind",
    ),
    _capability(
        domain="player_progression",
        kind="skill_level_reached",
        activation_bundle="player-progression-v1",
        wire_event_type="skill_level_reached",
        wire_event_version="1",
        producer_source="smapi.player.level_changed",
        legacy_ingest_aliases=(
            LegacyIngestAlias(
                event_type="world_progression",
                event_version="1",
                producer_source="smapi.player.level_changed",
            ),
        ),
        audience_scope="public",
        subject_namespace="skill_id",
        subject_value_policy="five_vanilla_skills",
        allowed_subject_values=_FIVE_SKILLS,
        natural_language_scope_by_locale={
            "zh-CN": "玩家五种原版技能及其相关活动的等级成长。",
            "en": "Level growth in the player's five vanilla skills and related activities.",
        },
        retrieval_scope_phrases_by_locale={
            "zh-CN": {
                "farming": ("耕种", "种植", "作物", "农田", "播种", "浇水", "收获"),
                "fishing": ("钓鱼", "鱼类", "水域", "捕鱼"),
                "foraging": ("采集", "野外资源", "植物", "木材", "树木"),
                "mining": ("采矿", "矿洞", "矿石", "石头", "镐子"),
                "combat": ("战斗", "武器", "剑", "怪物", "洞穴", "危险"),
            },
            "en": {
                "farming": ("farming", "plant", "crop", "field", "seed", "water", "harvest"),
                "fishing": ("fishing", "fish", "water", "catch"),
                "foraging": ("foraging", "wild resource", "plant", "wood", "tree"),
                "mining": ("mining", "mine", "ore", "stone", "pickaxe"),
                "combat": ("combat", "weapon", "sword", "monster", "cave", "danger"),
            },
        },
        retrieval_bucket_policy="subject",
    ),
    _capability(
        domain="player_progression",
        kind="mine_depth_milestone_reached",
        activation_bundle="player-progression-v1",
        wire_event_type="mine_depth_milestone_reached",
        wire_event_version="1",
        producer_source="smapi.player.warped",
        audience_scope="public",
        subject_namespace="mine_id",
        subject_value_policy="mine_enum",
        allowed_subject_values=frozenset({"the_mines", "skull_cavern"}),
        natural_language_scope_by_locale={
            "zh-CN": "玩家在普通矿井或骷髅洞穴首次到达更深的里程碑。",
            "en": "The player's first arrival at deeper Mines or Skull Cavern milestones.",
        },
        retrieval_scope_phrases_by_locale={
            "zh-CN": {
                "*": ("矿洞", "深度", "层数"),
                "the_mines": ("矿井", "电梯"),
                "skull_cavern": ("骷髅洞穴", "沙漠洞穴"),
            },
            "en": {
                "*": ("mine", "depth", "floor"),
                "the_mines": ("mines", "elevator"),
                "skull_cavern": ("skull cavern", "desert cave"),
            },
        },
        retrieval_bucket_policy="subject",
    ),
    _capability(
        domain="player_progression",
        kind="tool_upgrade_received",
        activation_bundle="player-progression-v1",
        wire_event_type="tool_upgrade_received",
        wire_event_version="1",
        producer_source="smapi.player.tool_upgrade_observed",
        audience_scope="public",
        subject_namespace="tool_id",
        subject_value_policy="six_upgradeable_tools",
        allowed_subject_values=frozenset(
            {"axe", "pickaxe", "hoe", "watering_can", "pan", "trash_can"}
        ),
        natural_language_scope_by_locale={
            "zh-CN": "玩家已经取回并拥有六类工具之一的升级版本。",
            "en": "The player received and owns an upgraded version of one of six tools.",
        },
        retrieval_scope_phrases_by_locale={
            "zh-CN": {
                "*": ("工具", "升级", "铜", "钢", "金", "铱"),
                "axe": ("斧头",),
                "pickaxe": ("镐子", "采矿"),
                "hoe": ("锄头", "耕地"),
                "watering_can": ("水壶", "浇水"),
                "pan": ("淘金盘", "淘金"),
                "trash_can": ("垃圾桶",),
            },
            "en": {
                "*": ("tool", "upgrade", "copper", "steel", "gold", "iridium"),
                "axe": ("axe",),
                "pickaxe": ("pickaxe", "mining"),
                "hoe": ("hoe", "soil"),
                "watering_can": ("watering can", "water"),
                "pan": ("pan", "panning"),
                "trash_can": ("trash can",),
            },
        },
        retrieval_bucket_policy="subject",
    ),
    _capability(
        domain="player_progression",
        kind="mastery_claimed",
        activation_bundle="player-progression-v1",
        wire_event_type="mastery_claimed",
        wire_event_version="1",
        producer_source="smapi.player.mastery_snapshot",
        audience_scope="public",
        subject_namespace="skill_id",
        subject_value_policy="five_vanilla_skills",
        allowed_subject_values=_FIVE_SKILLS,
        natural_language_scope_by_locale={
            "zh-CN": "玩家领取了五种原版技能之一的精通奖励。",
            "en": "The player claimed a mastery reward for one of the five vanilla skills.",
        },
        retrieval_scope_phrases_by_locale={
            "zh-CN": {
                "*": ("精通", "奖励"),
                "farming": ("耕种", "作物", "收获"),
                "fishing": ("钓鱼", "鱼"),
                "foraging": ("采集", "树木"),
                "mining": ("采矿", "矿石"),
                "combat": ("战斗", "怪物"),
            },
            "en": {
                "*": ("mastery", "reward"),
                "farming": ("farming", "crop", "harvest"),
                "fishing": ("fishing", "fish"),
                "foraging": ("foraging", "tree"),
                "mining": ("mining", "ore"),
                "combat": ("combat", "monster"),
            },
        },
        retrieval_bucket_policy="subject",
    ),
    _capability(
        domain="world_progression",
        kind="public_facility_restored",
        activation_bundle="world-progression-v1",
        wire_event_type="world_progression",
        wire_event_version="1",
        producer_source="smapi.world.public_facility_restored",
        audience_scope="public",
        subject_namespace="facility_id",
        subject_value_policy="five_public_facilities",
        allowed_subject_values=frozenset(
            {"greenhouse", "minecarts", "bus_service", "quarry_bridge", "glittering_boulder"}
        ),
        natural_language_scope_by_locale={
            "zh-CN": "温室、矿车、巴士、采石场桥或闪光巨石的路线中立公共设施结果。",
            "en": (
                "Route-neutral restoration of the greenhouse, minecarts, bus, quarry bridge, "
                "or glittering boulder."
            ),
        },
        retrieval_scope_phrases_by_locale={
            "zh-CN": {
                "greenhouse": ("温室", "作物", "种植"),
                "minecarts": ("矿车", "交通", "车站"),
                "bus_service": ("巴士", "公交车", "沙漠"),
                "quarry_bridge": ("采石场", "桥"),
                "glittering_boulder": ("闪光巨石", "淘金"),
            },
            "en": {
                "greenhouse": ("greenhouse", "crop", "plant"),
                "minecarts": ("minecart", "transport", "station"),
                "bus_service": ("bus", "desert"),
                "quarry_bridge": ("quarry", "bridge"),
                "glittering_boulder": ("glittering boulder", "panning"),
            },
        },
        retrieval_bucket_policy="subject",
    ),
)


def _validate_registry(registry: tuple[MemoryCapability, ...]) -> None:
    """在 import 时拒绝静态目录自身的重复和不完整配置。"""

    kinds = [item.kind for item in registry]
    direct_producer_contracts = [
        (item.wire_event_type, item.wire_event_version, item.producer_source) for item in registry
    ]
    legacy_producer_contracts = [
        (alias.event_type, alias.event_version, alias.producer_source)
        for item in registry
        for alias in item.legacy_ingest_aliases
    ]
    producer_contracts = [*direct_producer_contracts, *legacy_producer_contracts]
    if len(kinds) != len(set(kinds)):
        raise RuntimeError("memory capability kinds must be unique")
    if len(producer_contracts) != len(set(producer_contracts)):
        raise RuntimeError("memory producer contracts must be unique")
    for item in registry:
        if item.domain not in DOMAIN_TOOL_NAMES:
            raise RuntimeError("memory capability domain is not registered")
        if item.max_candidates_per_bucket < 1:
            raise RuntimeError("memory capability bucket cap must be positive")
        if item.allowed_subject_values is not None and not item.allowed_subject_values:
            raise RuntimeError("fixed subject policy must declare at least one value")


_validate_registry(MEMORY_CAPABILITIES)

_CAPABILITIES_BY_KIND: Mapping[str, MemoryCapability] = MappingProxyType(
    {item.kind: item for item in MEMORY_CAPABILITIES}
)


def capability_for_kind(kind: str) -> MemoryCapability | None:
    """按内部稳定 kind 读取生产 capability；未知值保守返回 ``None``。"""

    return _CAPABILITIES_BY_KIND.get(kind)


def retrieval_bucket_key(capability: MemoryCapability, subject_value: str) -> str:
    """为一条已分类 memory 生成低基数、可审计的检索 bucket key。

    bucket 不是持久化身份，也不会暴露给模型。它只用于防止同一主题的旧事实
    占满 Top-5：技能等级与精通共享 ``skill:<subject>``，矿区、工具和公共设施
    则各自按静态 subject 分桶；NPC 礼物及关系历史按 kind 分桶。调用方必须先
    保证 capability 与 subject 来自可信分类，本函数仍会二次拒绝未知策略。

    Args:
        capability: capability registry 中的静态能力声明。
        subject_value: memory 分类字段中的规范 subject 值。
    Returns:
        只供 Retriever 内部排序使用的稳定 bucket key。
    Raises:
        ValueError: subject 为空，或 capability 声明了未知 bucket 策略。
    """

    if not subject_value:
        raise ValueError("retrieval bucket subject 不能为空")
    if capability.retrieval_bucket_policy == "kind":
        return capability.kind
    if capability.retrieval_bucket_policy != "subject":
        raise ValueError("未知 retrieval bucket policy")

    if capability.kind in {"skill_level_reached", "mastery_claimed"}:
        prefix = "skill"
    elif capability.kind == "mine_depth_milestone_reached":
        prefix = "mine_depth"
    elif capability.kind == "tool_upgrade_received":
        prefix = "tool"
    elif capability.kind == "public_facility_restored":
        prefix = "facility"
    else:
        # subject 策略属于有限的构建时合同。静默回退为 kind:subject 会掩盖
        # registry 漂移，导致同一输入在版本升级后改变候选多样性。
        raise ValueError("subject bucket capability 未注册稳定前缀")
    return f"{prefix}:{subject_value}"


def build_target_capability_registry() -> tuple[MemoryCapability, ...]:
    """为自动化/离线评测返回全量 active 副本，不共享生产 tuple 身份。"""

    target = tuple(replace(item, status="active") for item in MEMORY_CAPABILITIES)
    _validate_registry(target)
    return target


def build_domain_tool_description(
    domain: MemoryDomain,
    registry: tuple[MemoryCapability, ...],
) -> str:
    """从 active registry 生成一个不含存档动态事实的稳定工具说明。

    description 明确说明领域、当前构建支持的 kind 自然语言范围、排除项以及
    只读/昨日/可为空语义。它不会读取记录数量、日期、evidence ID 或动态 gift
    item ID，因此可安全进入 Provider Schema。
    """

    active_capabilities = tuple(
        item for item in registry if item.domain == domain and item.status == "active"
    )
    if not active_capabilities:
        raise ValueError("domain 没有 active capability，不能生成工具说明")
    capability_lines: list[str] = []
    for item in active_capabilities:
        subject_map = item.retrieval_scope_phrases_by_locale.get("zh-CN", {})
        static_phrases = tuple(
            dict.fromkeys(phrase for phrases in subject_map.values() for phrase in phrases)
        )
        phrase_suffix = (
            " 静态适用主题包括：" + "、".join(static_phrases) + "。" if static_phrases else ""
        )
        capability_lines.append(
            f"- {item.kind}: {item.natural_language_scope_by_locale['zh-CN']}{phrase_suffix}"
        )
    return "\n".join(
        (
            DOMAIN_NATURAL_LANGUAGE_SCOPE[domain]["zh-CN"],
            "当前构建支持：",
            *capability_lines,
            DOMAIN_EXCLUSIONS_ZH_CN[domain],
            "这是只读工具，只返回截至昨日的合法候选，不会改变游戏状态。",
            "结果可能为空；即使非空，也不保证适合当前台词，必要时应原样返回。",
        )
    )


def domain_tool_names(
    registry: tuple[MemoryCapability, ...] = MEMORY_CAPABILITIES,
) -> frozenset[str]:
    """返回至少含一个 active kind 的领域工具名。

    可见性只取决于构建时 registry，不取决于某个存档是否有记录。生产目录尚未
    激活时结果为空；旧工具在原子切换前继续由现有 Agent 注册表提供。
    """

    active_domains = {item.domain for item in registry if item.status == "active"}
    return frozenset(DOMAIN_TOOL_NAMES[domain] for domain in active_domains)
