"""Phase 6 的纯确定性台词 Guard。

模型 structured output 只保证字段形状，不代表内容可进入游戏。本模块在模型与
持久化之间执行无 I/O、可复现的硬校验：文本长度、Markdown/Dialogue DSL、
工具与内部标识泄露、evidence 归属和昨日截止、关系称谓、显式游戏状态修改、
推测升级为事实，以及源台词主题锚点。

Guard 故意不实现通用自然语言蕴含证明。关系和主题使用保守词法近似；难以确认
的 rewrite 会失败并回退原版，剩余语义风险由 Phase 8 离线评测量化，而不是在
在线链路增加第二个审核 Agent。
"""

from __future__ import annotations

import re
import unicodedata
from dataclasses import dataclass

from stardew_npc_agent.dialogue_context import visible_calendar_progression_signals
from stardew_npc_agent.dialogue_template import (
    AddressSlot,
    DialogueTemplateError,
    DialogueTextTemplate,
    parse_game_template,
    render_game_template,
    source_requires_player_name,
)
from stardew_npc_agent.profiles import NpcAgentProfile
from stardew_npc_agent.schemas import (
    DialogueGenerationBatchRequest,
    DialogueGenerationItem,
)
from stardew_npc_agent.storage import EvidenceRecord

DIALOGUE_GUARD_VERSION = "dialogue-guard-v2"
DEFAULT_MAX_TEXT_CHARACTERS = 240

# Guard 保持为纯领域模块，不导入 LangChain Agent runtime。兼容期同时拦截旧
# query-style 名称和目标领域名称；生产 registry 原子切换后旧名称虽然不再绑定，
# 仍不能被模型作为内部实现细节写进台词。
_TOOL_NAMES = (
    "search_memories",
    "get_event_history",
    "get_progression_context",
    "get_npc_history",
    "get_player_progression",
    "get_world_progression",
)

# 与游戏侧 preflight 的保守语法边界一致。Stardew Dialogue 中这些字符可能
# 改变段落、说话者、命令或显示控制；rewrite 不需要它们，因此一律 fail closed。
_DIALOGUE_DSL_CHARACTERS = frozenset("$#@%^¦[]{}\r\n")

_MARKDOWN_PATTERNS = (
    re.compile(r"```|`"),
    re.compile(r"\*\*|__|~~"),
    re.compile(r"!\[[^\]]*\]\([^)]*\)|\[[^\]]+\]\([^)]*\)"),
    re.compile(r"(?:^|\n)\s{0,3}(?:[-+*]|\d+[.)])\s+"),
)

_STATIC_INTERNAL_MARKERS = (
    "system prompt",
    "system message",
    "prompt version",
    "toolruntime",
    "dialogueagentdecision",
    "untrusted_game_data",
    "memory:",
    "event:",
    "sha256:",
)

# 只拦截明确表示修改游戏状态的元语言，不把“社区中心已经修好”等可能来自
# mandatory progression context 的普通事实全部禁掉。更细的事实支持率留给 eval。
_GAME_STATE_MUTATION_TERMS = (
    "增加好感度",
    "降低好感度",
    "好感度+",
    "好感度-",
    "任务已完成",
    "任务完成了",
    "任务状态",
    "friendship points",
    "friendship +",
    "friendship -",
    "quest completed",
    "quest status",
    "setflag",
    "mail flag",
)

_UNCERTAINTY_MARKERS = (
    "也许",
    "可能",
    "大概",
    "好像",
    "似乎",
    "maybe",
    "might",
    "perhaps",
    "probably",
    "seems",
)
_CERTAINTY_MARKERS = (
    "肯定",
    "一定",
    "毫无疑问",
    "确定无疑",
    "definitely",
    "certainly",
    "no doubt",
    "for sure",
)

# 主题组只覆盖 MVP 常见日常话题；它们允许“雨”与“天气”等同主题改写，
# 同时避免必须逐字复制 source。未知话题再回退到显著词/中文二元词重叠。
_TOPIC_GROUPS = (
    (
        "雨",
        "雨天",
        "天气",
        "阳光",
        "晴天",
        "风",
        "雪",
        "rain",
        "weather",
        "sun",
        "sunny",
        "wind",
        "snow",
        "storm",
    ),
    (
        "屋里",
        "室内",
        "家里",
        "待着",
        "发呆",
        "indoors",
        "inside",
        "home",
        "stay",
        "relax",
    ),
    (
        "矿",
        "矿井",
        "洞穴",
        "冒险",
        "怪物",
        "剑",
        "mine",
        "cave",
        "adventure",
        "monster",
        "sword",
    ),
    ("电脑", "编程", "代码", "摩托", "computer", "programming", "code", "motorcycle"),
    ("礼物", "紫水晶", "水晶", "宝石", "gift", "amethyst", "crystal", "gem"),
    ("农场", "作物", "收获", "土地", "farm", "crop", "harvest", "soil"),
    ("春天", "夏天", "秋天", "冬天", "spring", "summer", "fall", "autumn", "winter"),
    ("你好", "嗨", "早上好", "晚安", "hello", "hi", "morning", "night"),
)

_ENGLISH_TOKEN_PATTERN = re.compile(r"[a-z0-9]+")
_HAN_RUN_PATTERN = re.compile(r"[\u3400-\u4dbf\u4e00-\u9fff]+")
_ENGLISH_STOP_WORDS = frozenset(
    {
        "about",
        "again",
        "also",
        "been",
        "from",
        "have",
        "just",
        "really",
        "that",
        "there",
        "they",
        "this",
        "today",
        "with",
        "would",
        "your",
    }
)
_HAN_BIGRAM_STOP_WORDS = frozenset(
    {
        "今天",
        "这里",
        "真的",
        "感觉",
        "有点",
        "还是",
        "就是",
        "可以",
        "这个",
        "那个",
    }
)


@dataclass(frozen=True, slots=True)
class DialogueGuardSettings:
    """Guard 的有限产品配置。

    当前只暴露文本最大长度，因为其他规则属于冻结安全合同，不允许运行时关闭。
    上界 1000 仅用于显式测试/未来产品调整；默认 240 保持短 NPC 台词体验。
    """

    max_text_characters: int = DEFAULT_MAX_TEXT_CHARACTERS

    def __post_init__(self) -> None:
        """拒绝 bool、非整数及无界长度，避免配置绕过 Guard。"""

        if (
            not isinstance(self.max_text_characters, int)
            or isinstance(self.max_text_characters, bool)
            or not 1 <= self.max_text_characters <= 1_000
        ):
            raise ValueError("max_text_characters 必须位于 1..1000")


@dataclass(frozen=True, slots=True)
class DialogueGuardCandidate:
    """待裁决的 typed 模板与模型声明使用的显式 evidence。"""

    template: DialogueTextTemplate | None
    evidence_ids: tuple[str, ...]


@dataclass(frozen=True, slots=True)
class GuardViolation:
    """一个稳定 Guard 失败类别，不包含候选或命中片段。"""

    code: str
    repairable: bool

    def to_dict(self) -> dict[str, object]:
        """返回可直接保存为 JSON 的固定审计形状。"""

        return {"code": self.code, "repairable": self.repairable}


@dataclass(frozen=True, slots=True)
class GuardReport:
    """一次 Guard 裁决的不可变、可序列化审计报告。"""

    validator_version: str
    passed: bool
    violations: tuple[GuardViolation, ...]
    checked_evidence_ids: tuple[str, ...]

    @property
    def error_codes(self) -> tuple[str, ...]:
        """按固定校验顺序返回机器码，便于服务选择是否 Repair。"""

        return tuple(violation.code for violation in self.violations)

    @property
    def repairable(self) -> bool:
        """只有至少一个错误且全部属于文本修复范围时才允许 Repair。"""

        return bool(self.violations) and all(violation.repairable for violation in self.violations)

    def to_dict(self) -> dict[str, object]:
        """序列化稳定字段；不保存候选、违规片段或分区身份。"""

        return {
            "validator_version": self.validator_version,
            "passed": self.passed,
            "violations": [violation.to_dict() for violation in self.violations],
            "checked_evidence_ids": list(self.checked_evidence_ids),
        }


class DialogueGuard:
    """对单条 rewrite 执行无副作用、固定顺序的确定性裁决。"""

    def __init__(self, settings: DialogueGuardSettings | None = None) -> None:
        """冻结有限配置；构造不会访问模型、数据库或环境变量。"""

        self.settings = settings or DialogueGuardSettings()

    def validate(
        self,
        request: DialogueGenerationBatchRequest,
        item: DialogueGenerationItem,
        profile: NpcAgentProfile,
        candidate: DialogueGuardCandidate,
        observations: tuple[EvidenceRecord, ...],
    ) -> GuardReport:
        """验证一个 Agent/Repair 候选并返回稳定报告。

        Args:
            request: 已通过公开 Pydantic contract 的每日批次。
            item: 必须原样属于 request 的当前 NPC 任务。
            profile: 必须与 item NPC/locale 匹配的版本化 Agent profile。
            candidate: 候选 typed 模板及模型声明的 evidence IDs。
            observations: 仅来自本次 ToolMessage artifact 的真实 evidence。
        Raises:
            ValueError: 调用方混配 request/item/profile 或传入非 tuple observations。
                这属于编程合同损坏，不应交给 Repair 猜测修复。
        """

        _validate_guard_context(request, item, profile, observations)
        violations: list[GuardViolation] = []
        text: str | None = None
        template = candidate.template

        # 原文也必须经过唯一 codec。正常服务会在进入 Agent 前先做同一 source
        # preflight；这里再次解析是为了让 Guard 的称呼槽决策不依赖调用方暗示。
        try:
            source_template = parse_game_template(item.source_dialogue.text)
        except DialogueTemplateError as error:
            raise ValueError("source dialogue template invalid") from error

        if template is None:
            _add_violation(violations, "TEXT_EMPTY", repairable=True)
        else:
            try:
                text = render_game_template(template)
            except DialogueTemplateError as error:
                _add_violation(violations, error.code, repairable=True)
            else:
                if (
                    source_requires_player_name(source_template)
                    and template.address_slot is not AddressSlot.PLAYER_NAME
                ):
                    _add_violation(
                        violations,
                        "ADDRESS_SLOT_REQUIRED",
                        repairable=True,
                    )

        if not isinstance(text, str) or not text.strip():
            if template is None:
                _add_violation(violations, "TEXT_EMPTY", repairable=True)
        else:
            if text != text.strip():
                _add_violation(
                    violations,
                    "TEXT_BOUNDARY_WHITESPACE",
                    repairable=True,
                )
            if len(text) > self.settings.max_text_characters:
                _add_violation(violations, "TEXT_TOO_LONG", repairable=True)
            if _contains_markdown(text):
                _add_violation(violations, "MARKDOWN_NOT_ALLOWED", repairable=True)
            # 通用 scanner 继续把 ``@`` 视作危险 DSL；这里只移除由 typed codec
            # 确认的唯一槽后再调用它，不能让自由文本获得全局豁免。
            syntax_scan_text = (
                text.replace("@", "", 1)
                if template is not None and template.address_slot is AddressSlot.PLAYER_NAME
                else text
            )
            if _contains_dialogue_dsl(syntax_scan_text):
                _add_violation(
                    violations,
                    "DIALOGUE_DSL_NOT_ALLOWED",
                    repairable=True,
                )
            if _contains_tool_name(text):
                _add_violation(violations, "TOOL_NAME_LEAK", repairable=True)
            if _contains_internal_marker(request, item, candidate, observations, text):
                _add_violation(violations, "INTERNAL_ID_LEAK", repairable=True)

        checked_evidence_ids = _validate_evidence_claim(
            request,
            candidate.evidence_ids,
            observations,
            violations,
        )

        if isinstance(text, str) and text.strip():
            if _contains_forbidden_relationship_term(profile, item, text):
                _add_violation(
                    violations,
                    "RELATIONSHIP_POLICY_VIOLATION",
                    repairable=True,
                )
            if _contains_game_state_mutation(text):
                _add_violation(violations, "GAME_STATE_MUTATION", repairable=True)
            if _promotes_speculation_to_fact(
                request,
                item,
                observations,
                text,
            ):
                _add_violation(
                    violations,
                    "SPECULATION_PROMOTED_TO_FACT",
                    repairable=True,
                )
            if not _preserves_source_topic(item.source_dialogue.text, text):
                _add_violation(
                    violations,
                    "SOURCE_TOPIC_UNANCHORED",
                    repairable=True,
                )

        return GuardReport(
            validator_version=DIALOGUE_GUARD_VERSION,
            passed=not violations,
            violations=tuple(violations),
            checked_evidence_ids=checked_evidence_ids,
        )


def _validate_guard_context(
    request: DialogueGenerationBatchRequest,
    item: DialogueGenerationItem,
    profile: NpcAgentProfile,
    observations: tuple[EvidenceRecord, ...],
) -> None:
    """拒绝跨任务/profile 混配，避免 Guard 在错误身份上给出通过结论。"""

    if item not in request.items:
        raise ValueError("item 必须原样属于 request.items")
    if profile.npc_id != item.npc_id:
        raise ValueError("profile 必须与当前 NPC 匹配")
    if request.stable_day_context.locale not in profile.supported_locales:
        raise ValueError("locale 不在当前 Agent profile 的支持范围")
    if not isinstance(observations, tuple) or not all(
        isinstance(record, EvidenceRecord) for record in observations
    ):
        raise ValueError("observations 必须是 EvidenceRecord tuple")


def _add_violation(
    violations: list[GuardViolation],
    code: str,
    *,
    repairable: bool,
) -> None:
    """按首次发现顺序追加唯一机器码，避免同类命中产生不稳定报告。"""

    if any(existing.code == code for existing in violations):
        return
    violations.append(GuardViolation(code=code, repairable=repairable))


def _contains_markdown(text: str) -> bool:
    """识别明确 Markdown 语法；普通星号之外的歧义表达不做无限扩张。"""

    return any(pattern.search(text) is not None for pattern in _MARKDOWN_PATTERNS)


def _contains_dialogue_dsl(text: str) -> bool:
    """拒绝可能改变 Stardew 原生对话解析的控制字符或段落分隔。"""

    return "||" in text or any(character in text for character in _DIALOGUE_DSL_CHARACTERS)


def _normalized(text: str) -> str:
    """使用 NFKC + casefold 做稳定、locale 无关的保守词法比较。"""

    return unicodedata.normalize("NFKC", text).casefold()


def _contains_tool_name(text: str) -> bool:
    """工具名称属于内部执行细节，不能作为 NPC 台词内容。"""

    normalized = _normalized(text)
    return any(tool_name in normalized for tool_name in _TOOL_NAMES)


def _contains_internal_marker(
    request: DialogueGenerationBatchRequest,
    item: DialogueGenerationItem,
    candidate: DialogueGuardCandidate,
    observations: tuple[EvidenceRecord, ...],
    text: str,
) -> bool:
    """检查 Prompt 标记及当前任务真实内部 ID，避免自由文本泄露。"""

    normalized = _normalized(text)
    if any(marker in normalized for marker in _STATIC_INTERNAL_MARKERS):
        return True

    dynamic_values = {
        request.request_id,
        request.save_id,
        request.player_id,
        item.task_id,
        item.source_dialogue.asset_name,
        item.source_dialogue.dialogue_key,
        item.source_dialogue.source_hash,
        *candidate.evidence_ids,
    }
    for record in observations:
        dynamic_values.add(record.evidence_id)
        dynamic_values.update(record.source_event_ids)
    return any(
        isinstance(value, str) and len(value) >= 3 and _normalized(value) in normalized
        for value in dynamic_values
    )


def _validate_evidence_claim(
    request: DialogueGenerationBatchRequest,
    claimed_ids: object,
    observations: tuple[EvidenceRecord, ...],
    violations: list[GuardViolation],
) -> tuple[str, ...]:
    """交叉验证模型 claim 与本次 artifact，并检查昨日截止。"""

    if not isinstance(claimed_ids, tuple) or any(
        not isinstance(evidence_id, str)
        or not evidence_id
        or evidence_id != evidence_id.strip()
        or "\x00" in evidence_id
        for evidence_id in claimed_ids
    ):
        _add_violation(
            violations,
            "EVIDENCE_CLAIM_INVALID",
            repairable=False,
        )
        return ()
    if len(claimed_ids) != len(set(claimed_ids)):
        _add_violation(
            violations,
            "EVIDENCE_CLAIM_INVALID",
            repairable=False,
        )
        return ()

    if observations and not claimed_ids:
        # 一旦 Agent 实际读取到非空 evidence，rewrite 必须显式声明至少一条。
        # 否则 storage 会把空 evidence 视为“仅使用 mandatory context”，展示 ACK
        # 也不会消费真实使用过的记忆，导致同一记忆绕过冷却反复出现。Guard
        # 无法可靠判断候选究竟用了哪条 observation，因此不交给 Repair 猜测。
        _add_violation(
            violations,
            "EVIDENCE_REQUIRED_AFTER_OBSERVATION",
            repairable=False,
        )
        return ()

    observed_by_id: dict[str, EvidenceRecord] = {}
    for record in observations:
        existing = observed_by_id.get(record.evidence_id)
        if existing is not None and existing != record:
            _add_violation(
                violations,
                "EVIDENCE_OBSERVATION_INVALID",
                repairable=False,
            )
            return ()
        observed_by_id.setdefault(record.evidence_id, record)

    checked: list[str] = []
    missing = False
    after_cutoff = False
    cutoff_day_index = request.game_day_index - 1
    for evidence_id in claimed_ids:
        claimed_record = observed_by_id.get(evidence_id)
        if claimed_record is None:
            missing = True
            continue
        checked.append(evidence_id)
        if claimed_record.occurred_day_index > cutoff_day_index:
            after_cutoff = True

    if missing:
        _add_violation(
            violations,
            "EVIDENCE_NOT_OBSERVED",
            repairable=False,
        )
    if after_cutoff:
        _add_violation(
            violations,
            "EVIDENCE_AFTER_CUTOFF",
            repairable=False,
        )
    return tuple(checked)


def _contains_forbidden_relationship_term(
    profile: NpcAgentProfile,
    item: DialogueGenerationItem,
    text: str,
) -> bool:
    """使用游戏关系阶段选择禁用称谓，不计算额外关系状态。"""

    normalized = _normalized(text)
    return any(
        _normalized(term) in normalized
        for term in profile.forbidden_relationship_terms_for(
            item.relationship_snapshot.relationship_stage
        )
    )


def _contains_game_state_mutation(text: str) -> bool:
    """拒绝明确宣称修改好感、任务或内部 flag 的元语言。"""

    normalized = _normalized(text)
    return any(_normalized(term) in normalized for term in _GAME_STATE_MUTATION_TERMS)


def _promotes_speculation_to_fact(
    request: DialogueGenerationBatchRequest,
    item: DialogueGenerationItem,
    observations: tuple[EvidenceRecord, ...],
    candidate_text: str,
) -> bool:
    """当输入存在不确定措辞时，阻止候选改写成显式强确定性断言。"""

    source_material = "\n".join(
        (
            item.source_dialogue.text,
            *item.style_examples,
            *(record.summary for record in observations),
            str(
                visible_calendar_progression_signals(request.stable_day_context.progression_signals)
            ),
        )
    )
    normalized_source = _normalized(source_material)
    normalized_candidate = _normalized(candidate_text)
    return any(marker in normalized_source for marker in _UNCERTAINTY_MARKERS) and any(
        marker in normalized_candidate for marker in _CERTAINTY_MARKERS
    )


def _preserves_source_topic(source_text: str, candidate_text: str) -> bool:
    """用保守主题组和显著词重叠验证原台词仍是语义锚点。"""

    normalized_source = _normalized(source_text)
    normalized_candidate = _normalized(candidate_text)
    for group in _TOPIC_GROUPS:
        source_has_group = any(_normalized(term) in normalized_source for term in group)
        candidate_has_group = any(_normalized(term) in normalized_candidate for term in group)
        if source_has_group and candidate_has_group:
            return True

    source_english = _significant_english_tokens(normalized_source)
    candidate_english = _significant_english_tokens(normalized_candidate)
    if source_english & candidate_english:
        return True

    source_han = _significant_han_bigrams(normalized_source)
    candidate_han = _significant_han_bigrams(normalized_candidate)
    if source_han & candidate_han:
        return True

    # 极短问候或语气词可能无法形成二元词；允许逐字保留，但不允许完全换题。
    source_compact = _compact_letters_and_numbers(normalized_source)
    return bool(source_compact) and source_compact in _compact_letters_and_numbers(
        normalized_candidate
    )


def _significant_english_tokens(text: str) -> set[str]:
    """提取长度至少四且非高频功能词的英文/数字 token。"""

    return {
        token
        for token in _ENGLISH_TOKEN_PATTERN.findall(text)
        if len(token) >= 4 and token not in _ENGLISH_STOP_WORDS
    }


def _significant_han_bigrams(text: str) -> set[str]:
    """提取中文连续片段的二元词，并去除容易造成假锚点的高频短语。"""

    bigrams: set[str] = set()
    for run in _HAN_RUN_PATTERN.findall(text):
        for index in range(len(run) - 1):
            bigram = run[index : index + 2]
            if bigram not in _HAN_BIGRAM_STOP_WORDS:
                bigrams.add(bigram)
    return bigrams


def _compact_letters_and_numbers(text: str) -> str:
    """移除标点和空白，仅用于极短 source 的保守逐字锚点。"""

    return "".join(character for character in text if character.isalnum())
