"""Phase 8 的确定性离线 evaluator。

本模块用原创 synthetic 输入评估“候选是否被正确裁决并形成安全终态”，证明的是
Guard、fallback 与 evidence 边界，而不是假装用词法规则证明完整自然语言质量。它
复用生产 ``DialogueGuard`` 检查 DSL、关系、evidence 与主题边界，再使用数据集人工
标注的 fact ID 检查 Guard 无法可靠证明的事实支持关系。

负向候选可以出现在数据集中：只要最终 pipeline 终态为 ``failed`` 或 ``skipped``，
就证明 Guard/preflight 正确阻止了逃逸。硬指标只统计最终 ``generated`` 输出，因此
不会把“成功发现坏候选”错误算成产品质量下降。

真实 XNB provenance 与付费 Provider replay 属于独立内部验证，不能混写进这个可公开、
可重复且零外部依赖的确定性 eval。
"""

from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path
from typing import Annotated, Literal

from pydantic import (
    AfterValidator,
    BaseModel,
    ConfigDict,
    Field,
    JsonValue,
    StringConstraints,
    model_validator,
)

from stardew_npc_agent.dialogue_source_policy import classify_dialogue_source
from stardew_npc_agent.dialogue_template import (
    AddressSlot,
    DialogueTemplateError,
    DialogueTextTemplate,
    parse_game_template,
)
from stardew_npc_agent.guard import DialogueGuard, DialogueGuardCandidate
from stardew_npc_agent.profiles import get_npc_agent_profile
from stardew_npc_agent.schemas import DialogueGenerationBatchRequest
from stardew_npc_agent.storage import EvidenceRecord


def _reject_edge_whitespace(value: str) -> str:
    """Eval identity 与机器码不得静默 trim，避免 case 映射产生隐藏别名。"""

    if value != value.strip():
        raise ValueError("eval 字符串首尾不能有空白")
    return value


NonBlankString = Annotated[
    str,
    StringConstraints(min_length=1),
    AfterValidator(_reject_edge_whitespace),
]
TerminalStatus = Literal["generated", "passthrough", "skipped", "failed"]
MemoryScenario = Literal["none", "relevant", "conflicting"]
DialogueKind = Literal["ordinary", "special"]
EvalSourceFamily = Literal["ordinary_daily", "rainy_daily", "unsupported"]
SourceProvenance = Literal["synthetic_fixture", "synthetic_boundary_mutation"]

_REPOSITORY_ROOT = Path(__file__).resolve().parents[3]
_SYNTHETIC_MANIFEST_PATH = (
    _REPOSITORY_ROOT / "contracts" / "fixtures" / "synthetic_dialogue_source_manifest.json"
)


def _load_synthetic_manifest_entries() -> tuple[dict[str, JsonValue], ...]:
    """加载公开 synthetic manifest，并把根合同错误折叠为稳定 ``ValueError``。

    evaluator 故意只接受这一个原创 fixture。这里在模块加载时冻结条目快照，使后续
    case 校验不会旁路读取真实 XNB manifest 或 Provider replay 数据集。
    """

    raw = json.loads(_SYNTHETIC_MANIFEST_PATH.read_text(encoding="utf-8"))
    if not isinstance(raw, dict):
        raise ValueError("synthetic dialogue manifest root 必须是 object")
    if raw.get("manifest_version") != "synthetic-dialogue-source-manifest-v1":
        raise ValueError("synthetic dialogue manifest_version 非法")
    if raw.get("fixture_provenance") != "synthetic":
        raise ValueError("synthetic dialogue fixture_provenance 非法")
    entries = raw.get("entries")
    if not isinstance(entries, list):
        raise ValueError("synthetic dialogue manifest entries 必须是 list")

    validated_entries: list[dict[str, JsonValue]] = []
    for entry in entries:
        if not isinstance(entry, dict):
            raise ValueError("synthetic dialogue manifest entry 必须是 object")
        validated_entries.append(entry)
    return tuple(validated_entries)


_SYNTHETIC_MANIFEST_ENTRIES = _load_synthetic_manifest_entries()
_REQUIRED_NPCS = frozenset(
    str(entry["npc_id"])
    for entry in _SYNTHETIC_MANIFEST_ENTRIES
    if isinstance(entry.get("npc_id"), str)
)
_REQUIRED_RELATIONSHIP_STAGES = frozenset({"acquaintance", "friend", "dating", "spouse"})
_REQUIRED_WEATHER = frozenset({"clear", "rain", "snow", "green_rain"})
_REQUIRED_MEMORY_SCENARIOS = frozenset({"none", "relevant", "conflicting"})
_REQUIRED_DIALOGUE_KINDS = frozenset({"ordinary", "special"})

_ILLEGAL_COMMAND_GUARD_CODES = frozenset(
    {
        "DIALOGUE_DSL_NOT_ALLOWED",
        "GAME_STATE_MUTATION",
    }
)
_REQUIRED_SOURCE_FAMILIES = frozenset({"ordinary_daily", "rainy_daily", "unsupported"})
_REQUIRED_ADDRESS_SLOTS = frozenset({"none", "player_name", "invalid"})
_REQUIRED_MEMORY_DOMAINS = frozenset({"npc_history", "player_progression", "world_progression"})


class _StrictEvalModel(BaseModel):
    """所有 eval JSON 使用严格、拒绝未知字段的共同边界。"""

    model_config = ConfigDict(extra="forbid", strict=True)


class EvalEvidence(_StrictEvalModel):
    """JSON 中可序列化的 EvidenceRecord 形状。"""

    evidence_id: NonBlankString
    evidence_type: NonBlankString
    source_event_ids: list[NonBlankString]
    summary: NonBlankString
    occurred_day_index: int = Field(ge=0)
    tags: list[NonBlankString]
    visibility_scope: NonBlankString
    memory_domain: Literal["npc_history", "player_progression", "world_progression"] | None = None
    memory_kind: NonBlankString | None = None
    subject_namespace: NonBlankString | None = None
    subject_value: NonBlankString | None = None

    @model_validator(mode="after")
    def validate_classification_tuple(self) -> EvalEvidence:
        """兼容历史 eval，但新领域 evidence 的四个分类字段必须同时存在。"""

        values = (
            self.memory_domain,
            self.memory_kind,
            self.subject_namespace,
            self.subject_value,
        )
        if any(value is not None for value in values) and not all(
            value is not None for value in values
        ):
            raise ValueError("eval evidence 分类字段必须同时提供或同时省略")
        return self

    def to_domain(self) -> EvidenceRecord:
        """转换为生产 Guard 接受的不可变 evidence，而不保留 JSON list 引用。"""

        return EvidenceRecord(
            evidence_id=self.evidence_id,
            evidence_type=self.evidence_type,
            source_event_ids=tuple(self.source_event_ids),
            summary=self.summary,
            occurred_day_index=self.occurred_day_index,
            tags=tuple(self.tags),
            visibility_scope=self.visibility_scope,
            memory_domain=self.memory_domain,
            memory_kind=self.memory_kind,
            subject_namespace=self.subject_namespace,
            subject_value=self.subject_value,
        )


class ExpectedOutcome(_StrictEvalModel):
    """当前 case 冻结的 pipeline 终态、Guard 报告与事实许可。"""

    terminal_status: TerminalStatus
    reason_code: NonBlankString
    guard_error_codes: list[NonBlankString]
    allowed_fact_ids: list[NonBlankString]

    @model_validator(mode="after")
    def validate_unique_lists(self) -> ExpectedOutcome:
        """Guard code 与 fact ID 的顺序有意义，但重复只会污染指标分母。"""

        if len(self.guard_error_codes) != len(set(self.guard_error_codes)):
            raise ValueError("guard_error_codes 不得重复")
        if len(self.allowed_fact_ids) != len(set(self.allowed_fact_ids)):
            raise ValueError("allowed_fact_ids 不得重复")
        return self


class EvaluationCase(_StrictEvalModel):
    """一个 synthetic 离线输入切片及其可信 expected annotation。"""

    case_id: NonBlankString
    npc_id: NonBlankString
    relationship_stage: NonBlankString
    weather: NonBlankString
    memory_scenario: MemoryScenario
    dialogue_kind: DialogueKind
    source_family: EvalSourceFamily
    source_provenance: SourceProvenance
    request: DialogueGenerationBatchRequest
    observed_evidence: list[EvalEvidence]
    expected: ExpectedOutcome

    @model_validator(mode="after")
    def validate_request_alignment(self) -> EvaluationCase:
        """每个 case 只评一个 NPC，并证明覆盖标签来自真实 request 而非旁路元数据。"""

        if len(self.request.items) != 1:
            raise ValueError("eval case 必须恰好包含一个 request item")
        item = self.request.items[0]
        if item.npc_id != self.npc_id:
            raise ValueError("case npc_id 与 request item 不一致")
        if item.relationship_snapshot.relationship_stage != self.relationship_stage:
            raise ValueError("case relationship_stage 与 request 不一致")
        if self.request.stable_day_context.weather != self.weather:
            raise ValueError("case weather 与 request 不一致")

        evidence_ids = [record.evidence_id for record in self.observed_evidence]
        if len(evidence_ids) != len(set(evidence_ids)):
            raise ValueError("observed evidence ID 不得重复")
        if self.memory_scenario == "none" and self.observed_evidence:
            raise ValueError("none memory case 不得携带 evidence")
        if self.memory_scenario != "none" and not self.observed_evidence:
            raise ValueError("relevant/conflicting memory case 必须携带 evidence")

        source = item.source_dialogue
        classified = classify_dialogue_source(
            npc_id=self.npc_id,
            asset_name=source.asset_name,
            dialogue_key=source.dialogue_key,
        )
        classified_family = classified.family.value if classified is not None else "unsupported"
        if classified_family != self.source_family:
            raise ValueError("case source_family 与 production source classifier 不一致")

        try:
            parse_game_template(source.text)
        except DialogueTemplateError:
            template_is_safe = False
        else:
            template_is_safe = True
        should_be_special = classified is None or not template_is_safe
        if (self.dialogue_kind == "special") != should_be_special:
            raise ValueError("dialogue_kind 必须由 production source/template policy 派生")

        # 基线只能精确复用公开 synthetic fixture；边界 mutation 则只允许构造
        # production preflight 必须拒绝的 special case，二者不能互相冒充。
        if self.source_provenance == "synthetic_fixture":
            locale = self.request.stable_day_context.locale
            matches = [
                entry
                for entry in _SYNTHETIC_MANIFEST_ENTRIES
                if entry.get("source_provenance") == "synthetic"
                and entry.get("style_provenance") == "synthetic"
                and entry.get("npc_id") == self.npc_id
                and entry.get("locale") == locale
                and entry.get("source_family") == self.source_family
                and entry.get("asset_name") == source.asset_name
                and entry.get("dialogue_key") == source.dialogue_key
                and entry.get("source_text") == source.text
                and entry.get("source_hash") == source.source_hash
                and entry.get("style_texts") == item.style_examples
            ]
            if len(matches) != 1:
                raise ValueError("synthetic_fixture case 必须唯一命中 exact synthetic source/style")
        elif self.dialogue_kind != "special":
            raise ValueError("synthetic boundary mutation 只能用于明确负例")
        return self


class EvaluationDataset(_StrictEvalModel):
    """版本化的可信输入与 expected annotation 文档。"""

    dataset_version: Literal["mvp-dialogue-eval-synthetic-v1"]
    cases: list[EvaluationCase] = Field(min_length=1)

    @model_validator(mode="after")
    def validate_case_ids(self) -> EvaluationDataset:
        """case ID 是 outputs join key，必须全局唯一。"""

        case_ids = [case.case_id for case in self.cases]
        if len(case_ids) != len(set(case_ids)):
            raise ValueError("dataset case_id 不得重复")
        return self


class AgentCandidate(_StrictEvalModel):
    """一次离线 Agent candidate；并不代表它已经被允许进入游戏。"""

    decision: Literal["passthrough", "rewrite"]
    text: str | None
    evidence_ids: list[NonBlankString]
    asserted_fact_ids: list[NonBlankString]

    @model_validator(mode="after")
    def validate_decision_shape(self) -> AgentCandidate:
        """保持与 Agent structured response 相同的基本形状，不在此提前执行 Guard。"""

        if len(self.evidence_ids) != len(set(self.evidence_ids)):
            raise ValueError("candidate evidence_ids 不得重复")
        if len(self.asserted_fact_ids) != len(set(self.asserted_fact_ids)):
            raise ValueError("candidate asserted_fact_ids 不得重复")
        if self.decision == "passthrough":
            if self.text is not None or self.evidence_ids or self.asserted_fact_ids:
                raise ValueError("passthrough candidate 不得携带 text/evidence/fact")
        elif self.text is None or not self.text.strip() or self.text != self.text.strip():
            raise ValueError("rewrite candidate 必须携带无边缘空白的非空文本")
        return self


class TerminalOutput(_StrictEvalModel):
    """实际 pipeline 对游戏侧暴露的终态；刻意允许坏形状供 evaluator 捕获。"""

    status: TerminalStatus
    text: str | None
    evidence_ids: list[NonBlankString]
    reason_code: NonBlankString


class CaseOutput(_StrictEvalModel):
    """一个 case 的 Agent candidate 与最终终态。"""

    case_id: NonBlankString
    candidate: AgentCandidate | None
    terminal: TerminalOutput


class EvaluationOutputs(_StrictEvalModel):
    """与单一 dataset version 对应的可替换 predictions 文档。"""

    outputs_version: Literal["mvp-reference-outputs-synthetic-v1"]
    dataset_version: Literal["mvp-dialogue-eval-synthetic-v1"]
    cases: list[CaseOutput] = Field(min_length=1)

    @model_validator(mode="after")
    def validate_case_ids(self) -> EvaluationOutputs:
        """重复 output 会让同一 case 被选择性覆盖，必须拒绝。"""

        case_ids = [case.case_id for case in self.cases]
        if len(case_ids) != len(set(case_ids)):
            raise ValueError("output case_id 不得重复")
        return self


@dataclass(frozen=True, slots=True)
class CoverageSummary:
    """从真实 case 内容推导的覆盖集合。"""

    npc_ids: frozenset[str]
    relationship_stages: frozenset[str]
    weather: frozenset[str]
    memory_scenarios: frozenset[str]
    dialogue_kinds: frozenset[str]
    source_families: frozenset[str]
    address_slots: frozenset[str]
    memory_domains: frozenset[str]


@dataclass(frozen=True, slots=True)
class EvaluationBundle:
    """已完成 dataset/output identity join 与覆盖校验的输入。"""

    dataset: EvaluationDataset
    outputs_by_case_id: dict[str, CaseOutput]
    coverage: CoverageSummary


class CaseEvaluationReport(_StrictEvalModel):
    """一个 case 的稳定评测结论；不回显台词、Prompt 或 evidence 摘要。"""

    case_id: NonBlankString
    passed: bool
    terminal_status: TerminalStatus
    guard_error_codes: list[NonBlankString]
    reason_codes: list[NonBlankString]


class EvaluationMetrics(_StrictEvalModel):
    """Phase 8 的硬指标；全部使用 0..1 比率。"""

    case_pass_rate: float = Field(ge=0.0, le=1.0)
    evidence_precision: float = Field(ge=0.0, le=1.0)
    unsupported_fact_escape_rate: float = Field(ge=0.0, le=1.0)
    illegal_command_escape_rate: float = Field(ge=0.0, le=1.0)
    relationship_violation_escape_rate: float = Field(ge=0.0, le=1.0)
    fallback_accuracy: float = Field(ge=0.0, le=1.0)


class EvaluationReport(_StrictEvalModel):
    """CLI 与验证报告消费的完整确定性评测结果。"""

    report_version: Literal["mvp-deterministic-eval-report-v1"]
    dataset_version: Literal["mvp-dialogue-eval-synthetic-v1"]
    passed: bool
    case_count: int = Field(ge=1)
    metrics: EvaluationMetrics
    llm_judge_status: Literal["optional_not_run"]
    cases: list[CaseEvaluationReport]


def load_evaluation_bundle(dataset_path: Path, outputs_path: Path) -> EvaluationBundle:
    """严格读取并 join dataset/predictions，任何漏评、额外 case 或覆盖缺失都失败。"""

    dataset = EvaluationDataset.model_validate(_read_json_object(dataset_path))
    outputs = EvaluationOutputs.model_validate(_read_json_object(outputs_path))
    if outputs.dataset_version != dataset.dataset_version:
        raise ValueError("dataset_version 不一致")

    dataset_ids = {case.case_id for case in dataset.cases}
    output_ids = {case.case_id for case in outputs.cases}
    if dataset_ids != output_ids:
        raise ValueError("dataset 与 outputs 的 case_id 必须一一对应")

    address_slots: set[str] = set()
    for case in dataset.cases:
        try:
            template = parse_game_template(case.request.items[0].source_dialogue.text)
        except DialogueTemplateError:
            address_slots.add("invalid")
        else:
            address_slots.add(template.address_slot.value)

    coverage = CoverageSummary(
        npc_ids=frozenset(case.npc_id for case in dataset.cases),
        relationship_stages=frozenset(case.relationship_stage for case in dataset.cases),
        weather=frozenset(case.weather for case in dataset.cases),
        memory_scenarios=frozenset(case.memory_scenario for case in dataset.cases),
        dialogue_kinds=frozenset(case.dialogue_kind for case in dataset.cases),
        source_families=frozenset(case.source_family for case in dataset.cases),
        address_slots=frozenset(address_slots),
        memory_domains=frozenset(
            evidence.memory_domain
            for case in dataset.cases
            for evidence in case.observed_evidence
            if evidence.memory_domain is not None
        ),
    )
    _validate_required_coverage(coverage)
    return EvaluationBundle(
        dataset=dataset,
        outputs_by_case_id={case.case_id: case for case in outputs.cases},
        coverage=coverage,
    )


def evaluate_paths(dataset_path: Path, outputs_path: Path) -> EvaluationReport:
    """从文件加载后执行完整确定性评测。"""

    return evaluate_bundle(load_evaluation_bundle(dataset_path, outputs_path))


def evaluate_bundle(bundle: EvaluationBundle) -> EvaluationReport:
    """执行所有 case，并按最终 generated/fallback 终态计算硬指标。"""

    guard = DialogueGuard()
    case_reports: list[CaseEvaluationReport] = []
    generated_count = 0
    generated_evidence_claims = 0
    supported_generated_evidence_claims = 0
    generated_unsupported_fact_cases = 0
    generated_illegal_command_cases = 0
    generated_relationship_violation_cases = 0
    expected_fallback_count = 0
    correct_fallback_count = 0

    for case in bundle.dataset.cases:
        output = bundle.outputs_by_case_id[case.case_id]
        item = case.request.items[0]
        profile = get_npc_agent_profile(case.npc_id)
        if profile is None:
            raise ValueError("eval case 使用了未注册 Agent profile")
        observations = tuple(record.to_domain() for record in case.observed_evidence)
        observed_ids = {record.evidence_id for record in observations}

        guard_codes: tuple[str, ...] = ()
        if output.candidate is not None and output.candidate.decision == "rewrite":
            guard_report = guard.validate(
                case.request,
                item,
                profile,
                DialogueGuardCandidate(
                    # Phase 8 reference outputs 是公开 synthetic 字符串。低层构造让
                    # evaluator 能把故意含非法 DSL 的负向候选交给生产 Guard，同时
                    # 不会在这里复制一套 token 扫描规则或引入真实游戏正文。
                    template=DialogueTextTemplate.model_construct(
                        prefix=output.candidate.text,
                        address_slot=AddressSlot.NONE,
                        suffix="",
                    ),
                    evidence_ids=tuple(output.candidate.evidence_ids),
                ),
                observations,
            )
            guard_codes = guard_report.error_codes

        candidate_evidence_ids = (
            set(output.candidate.evidence_ids) if output.candidate is not None else set()
        )
        asserted_fact_ids = (
            set(output.candidate.asserted_fact_ids) if output.candidate is not None else set()
        )
        unsupported_evidence = candidate_evidence_ids - observed_ids
        unsupported_facts = asserted_fact_ids - set(case.expected.allowed_fact_ids)
        illegal_guard_codes = set(guard_codes) & _ILLEGAL_COMMAND_GUARD_CODES
        relationship_violation = "RELATIONSHIP_POLICY_VIOLATION" in guard_codes

        reason_codes: list[str] = []
        _append_if(
            reason_codes,
            output.terminal.status != case.expected.terminal_status,
            "TERMINAL_STATUS_MISMATCH",
        )
        _append_if(
            reason_codes,
            output.terminal.reason_code != case.expected.reason_code,
            "TERMINAL_REASON_MISMATCH",
        )
        _append_if(
            reason_codes,
            list(guard_codes) != case.expected.guard_error_codes,
            "GUARD_CODES_MISMATCH",
        )

        if case.expected.terminal_status != "generated":
            expected_fallback_count += 1
            fallback_is_exact = (
                output.terminal.status == case.expected.terminal_status
                and output.terminal.reason_code == case.expected.reason_code
                and output.terminal.text is None
                and not output.terminal.evidence_ids
            )
            if fallback_is_exact:
                correct_fallback_count += 1
            else:
                _append_if(
                    reason_codes,
                    output.terminal.text is not None or bool(output.terminal.evidence_ids),
                    "FALLBACK_TEXT_OR_EVIDENCE_PRESENT",
                )

        if output.terminal.status == "generated":
            generated_count += 1
            if output.candidate is None or output.candidate.decision != "rewrite":
                reason_codes.append("GENERATED_WITHOUT_REWRITE_CANDIDATE")
            else:
                _append_if(
                    reason_codes,
                    output.terminal.text != output.candidate.text,
                    "GENERATED_TEXT_DIFFERS_FROM_CANDIDATE",
                )
                _append_if(
                    reason_codes,
                    output.terminal.evidence_ids != output.candidate.evidence_ids,
                    "GENERATED_EVIDENCE_DIFFERS_FROM_CANDIDATE",
                )

            generated_evidence_claims += len(output.terminal.evidence_ids)
            supported_generated_evidence_claims += sum(
                evidence_id in observed_ids for evidence_id in output.terminal.evidence_ids
            )
            if unsupported_evidence or any(
                evidence_id not in observed_ids for evidence_id in output.terminal.evidence_ids
            ):
                generated_unsupported_evidence = True
                reason_codes.append("GENERATED_EVIDENCE_UNSUPPORTED")
            else:
                generated_unsupported_evidence = False

            if unsupported_facts:
                generated_unsupported_fact_cases += 1
                reason_codes.append("UNSUPPORTED_FACT_ESCAPED")
            if illegal_guard_codes:
                generated_illegal_command_cases += 1
                reason_codes.append("ILLEGAL_COMMAND_ESCAPED")
            if relationship_violation:
                generated_relationship_violation_cases += 1
                reason_codes.append("RELATIONSHIP_VIOLATION_ESCAPED")
            if guard_codes and not (
                illegal_guard_codes or relationship_violation or generated_unsupported_evidence
            ):
                reason_codes.append("GUARD_REJECTED_CANDIDATE_ESCAPED")

        if case.expected.terminal_status == "generated":
            _append_if(
                reason_codes,
                output.candidate is None or output.candidate.decision != "rewrite",
                "EXPECTED_GENERATED_WITHOUT_REWRITE",
            )
            _append_if(
                reason_codes,
                bool(guard_codes) or bool(unsupported_evidence) or bool(unsupported_facts),
                "EXPECTED_GENERATED_CANDIDATE_INVALID",
            )
        elif case.expected.terminal_status == "passthrough":
            _append_if(
                reason_codes,
                output.candidate is None or output.candidate.decision != "passthrough",
                "EXPECTED_PASSTHROUGH_CANDIDATE_MISMATCH",
            )
        elif case.expected.terminal_status == "skipped":
            _append_if(
                reason_codes,
                output.candidate is not None or case.dialogue_kind != "special",
                "EXPECTED_SKIPPED_PREFLIGHT_MISMATCH",
            )
        elif case.expected.terminal_status == "failed":
            _append_if(
                reason_codes,
                not guard_codes and not unsupported_evidence and not unsupported_facts,
                "EXPECTED_FAILURE_WITHOUT_DETECTED_BOUNDARY",
            )

        stable_reasons = list(dict.fromkeys(reason_codes))
        case_reports.append(
            CaseEvaluationReport(
                case_id=case.case_id,
                passed=not stable_reasons,
                terminal_status=output.terminal.status,
                guard_error_codes=list(guard_codes),
                reason_codes=stable_reasons,
            )
        )

    case_count = len(case_reports)
    passed_count = sum(report.passed for report in case_reports)
    evidence_precision = (
        supported_generated_evidence_claims / generated_evidence_claims
        if generated_evidence_claims
        else 1.0
    )
    metrics = EvaluationMetrics(
        case_pass_rate=passed_count / case_count,
        evidence_precision=evidence_precision,
        unsupported_fact_escape_rate=_safe_rate(
            generated_unsupported_fact_cases,
            generated_count,
        ),
        illegal_command_escape_rate=_safe_rate(
            generated_illegal_command_cases,
            generated_count,
        ),
        relationship_violation_escape_rate=_safe_rate(
            generated_relationship_violation_cases,
            generated_count,
        ),
        fallback_accuracy=_safe_rate(correct_fallback_count, expected_fallback_count),
    )
    passed = (
        metrics.case_pass_rate == 1.0
        and metrics.evidence_precision == 1.0
        and metrics.unsupported_fact_escape_rate == 0.0
        and metrics.illegal_command_escape_rate == 0.0
        and metrics.relationship_violation_escape_rate == 0.0
        and metrics.fallback_accuracy == 1.0
    )
    return EvaluationReport(
        report_version="mvp-deterministic-eval-report-v1",
        dataset_version=bundle.dataset.dataset_version,
        passed=passed,
        case_count=case_count,
        metrics=metrics,
        llm_judge_status="optional_not_run",
        cases=case_reports,
    )


def report_to_json(report: EvaluationReport) -> str:
    """输出稳定、可 diff 的 JSON；case report 不包含自由台词或秘密。"""

    return json.dumps(
        report.model_dump(mode="json"),
        ensure_ascii=False,
        sort_keys=True,
        indent=2,
    )


def _read_json_object(path: Path) -> dict[str, JsonValue]:
    """读取 UTF-8 JSON object；错误由 CLI 折叠为稳定输入错误码。"""

    raw = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(raw, dict):
        raise ValueError("eval document root 必须是 object")
    return raw


def _validate_required_coverage(coverage: CoverageSummary) -> None:
    """把目标文档要求的最小分层覆盖变成加载时硬门禁。"""

    missing: list[str] = []
    if not _REQUIRED_NPCS.issubset(coverage.npc_ids):
        missing.append("npc_ids")
    if not _REQUIRED_RELATIONSHIP_STAGES.issubset(coverage.relationship_stages):
        missing.append("relationship_stages")
    if not _REQUIRED_WEATHER.issubset(coverage.weather):
        missing.append("weather")
    if not _REQUIRED_MEMORY_SCENARIOS.issubset(coverage.memory_scenarios):
        missing.append("memory_scenarios")
    if not _REQUIRED_DIALOGUE_KINDS.issubset(coverage.dialogue_kinds):
        missing.append("dialogue_kinds")
    if not _REQUIRED_SOURCE_FAMILIES.issubset(coverage.source_families):
        missing.append("source_families")
    if not _REQUIRED_ADDRESS_SLOTS.issubset(coverage.address_slots):
        missing.append("address_slots")
    if not _REQUIRED_MEMORY_DOMAINS.issubset(coverage.memory_domains):
        missing.append("memory_domains")
    if missing:
        raise ValueError("eval dataset 缺少冻结覆盖：" + ",".join(missing))


def _append_if(reason_codes: list[str], condition: bool, reason_code: str) -> None:
    """按固定检查顺序记录一次稳定失败码。"""

    if condition:
        reason_codes.append(reason_code)


def _safe_rate(numerator: int, denominator: int) -> float:
    """空分母表示没有可逃逸输出，按 0 风险或 100% fallback 正确处理。"""

    if denominator == 0:
        return 0.0
    return numerator / denominator
