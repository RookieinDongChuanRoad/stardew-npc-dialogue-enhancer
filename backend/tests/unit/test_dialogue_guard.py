"""Phase 6 确定性 Dialogue Guard 的硬边界与稳定报告测试。"""

from __future__ import annotations

import json

import pytest

from stardew_npc_agent.dialogue_template import AddressSlot, DialogueTextTemplate
from stardew_npc_agent.guard import (
    DialogueGuard,
    DialogueGuardCandidate,
    DialogueGuardSettings,
)
from stardew_npc_agent.profiles import get_npc_agent_profile
from stardew_npc_agent.schemas import DialogueGenerationBatchRequest
from stardew_npc_agent.storage import EvidenceRecord


def _request(
    *,
    source_text: str = "雨天待在屋里也不算太糟。",
    relationship_stage: str = "friend",
) -> DialogueGenerationBatchRequest:
    """构造只含一个 Abigail 任务的稳定 Guard 输入。"""

    return DialogueGenerationBatchRequest.model_validate(
        {
            "schema_version": "1.0",
            "request_id": "request-guard-test",
            "save_id": "save-guard-test",
            "player_id": "player-guard-test",
            "game_day_index": 10,
            "required_memory_revision": 1,
            "stable_day_context": {
                "season": "fall",
                "weather": "rain",
                "locale": "zh-CN",
                "progression_signals": {"mine_level": 40},
            },
            "items": [
                {
                    "task_id": "task-guard-test",
                    "npc_id": "Abigail",
                    "source_dialogue": {
                        "asset_name": "Characters/Dialogue/Abigail",
                        "dialogue_key": "fall_Mon",
                        "text": source_text,
                        "source_hash": "sha256:guard-source",
                    },
                    "relationship_snapshot": {
                        "friendship_points": 750,
                        "relationship_stage": relationship_stage,
                    },
                    "style_examples": ["样本一。", "样本二。", "样本三。"],
                    "memory_signals": [{"event_type": "gift_given"}],
                }
            ],
        }
    )


def _evidence(
    *,
    evidence_id: str = "memory:gift-amethyst",
    occurred_day_index: int = 8,
) -> EvidenceRecord:
    """构造一条可追溯的 NPC 私人记忆 evidence。"""

    return EvidenceRecord(
        evidence_id=evidence_id,
        evidence_type="gift_given",
        source_event_ids=("event:gift-amethyst",),
        summary="玩家曾送给 Abigail 一块紫水晶。",
        occurred_day_index=occurred_day_index,
        tags=("gift", "amethyst"),
        visibility_scope="npc:Abigail",
    )


def _validate(
    text: str | None,
    *,
    evidence_ids: tuple[str, ...] = (),
    observations: tuple[EvidenceRecord, ...] = (),
    source_text: str = "雨天待在屋里也不算太糟。",
    relationship_stage: str = "friend",
    guard: DialogueGuard | None = None,
):
    """通过真实 request/item/profile 调用 Guard，避免绕过任务身份校验。"""

    request = _request(
        source_text=source_text,
        relationship_stage=relationship_stage,
    )
    profile = get_npc_agent_profile("Abigail")
    assert profile is not None
    return (guard or DialogueGuard()).validate(
        request,
        request.items[0],
        profile,
        DialogueGuardCandidate(
            # 这些 case 验证 Guard 本身。故意含 DSL 的样本通过低层构造模拟
            # structured/Pydantic 边界被绕过后的纵深防御。
            template=(
                None
                if text is None
                else DialogueTextTemplate.model_construct(
                    prefix=text,
                    address_slot=AddressSlot.NONE,
                    suffix="",
                )
            ),
            evidence_ids=evidence_ids,
        ),
        observations,
    )


def test_valid_source_only_and_observed_evidence_rewrites_pass() -> None:
    """合法候选可以只用 mandatory context，也可以显式引用本次 observed evidence。"""

    source_only = _validate("这场雨的声音倒挺适合发呆的。")
    evidence = _evidence()
    with_evidence = _validate(
        "这种雨声让我想起那块紫水晶。",
        evidence_ids=(evidence.evidence_id,),
        observations=(evidence,),
    )

    assert source_only.passed is True
    assert source_only.error_codes == ()
    assert source_only.checked_evidence_ids == ()
    assert source_only.repairable is False
    assert with_evidence.passed is True
    assert with_evidence.error_codes == ()
    assert with_evidence.checked_evidence_ids == (evidence.evidence_id,)


@pytest.mark.parametrize(
    ("text", "expected_code", "source_text", "relationship_stage"),
    [
        ("雨" * 241, "TEXT_TOO_LONG", "雨天待在屋里也不算太糟。", "friend"),
        ("雨天 **待在屋里** 也行。", "MARKDOWN_NOT_ALLOWED", "雨天待在屋里也不算太糟。", "friend"),
        ("雨天$待在屋里也行。", "DIALOGUE_DSL_NOT_ALLOWED", "雨天待在屋里也不算太糟。", "friend"),
        (
            "雨天我会调用 search_memories 看看。",
            "TOOL_NAME_LEAK",
            "雨天待在屋里也不算太糟。",
            "friend",
        ),
        (
            "雨天也不错，save-guard-test。",
            "INTERNAL_ID_LEAK",
            "雨天待在屋里也不算太糟。",
            "friend",
        ),
        (
            "雨天也想叫你亲爱的。",
            "RELATIONSHIP_POLICY_VIOLATION",
            "雨天待在屋里也不算太糟。",
            "friend",
        ),
        (
            "雨天也会增加好感度。",
            "GAME_STATE_MUTATION",
            "雨天待在屋里也不算太糟。",
            "friend",
        ),
        (
            "雨天待在屋里肯定最好。",
            "SPECULATION_PROMOTED_TO_FACT",
            "也许雨天待在屋里也不算太糟。",
            "friend",
        ),
        (
            "那块紫水晶真的很漂亮。",
            "SOURCE_TOPIC_UNANCHORED",
            "雨天待在屋里也不算太糟。",
            "friend",
        ),
    ],
    ids=[
        "length",
        "markdown",
        "dialogue-dsl",
        "tool-name",
        "internal-id",
        "relationship",
        "game-state",
        "speculation",
        "topic-anchor",
    ],
)
def test_each_text_guard_returns_one_stable_repairable_error(
    text: str,
    expected_code: str,
    source_text: str,
    relationship_stage: str,
) -> None:
    """每类文本硬边界使用单一稳定机器码，且可进入最多一次 Repair。"""

    report = _validate(
        text,
        source_text=source_text,
        relationship_stage=relationship_stage,
    )

    assert report.passed is False
    assert report.error_codes == (expected_code,)
    assert report.repairable is True


def test_unobserved_and_future_evidence_are_non_repairable() -> None:
    """Repair 不能把不可信 evidence 变成可信事实，因此两类证据错误直接拒绝。"""

    missing = _validate(
        "这种雨声让我想起那块紫水晶。",
        evidence_ids=("memory:not-observed",),
    )
    future = _evidence(occurred_day_index=10)
    after_cutoff = _validate(
        "这种雨声让我想起那块紫水晶。",
        evidence_ids=(future.evidence_id,),
        observations=(future,),
    )

    assert missing.error_codes == ("EVIDENCE_NOT_OBSERVED",)
    assert missing.repairable is False
    assert after_cutoff.error_codes == ("EVIDENCE_AFTER_CUTOFF",)
    assert after_cutoff.repairable is False


def test_rewrite_after_nonempty_observation_must_claim_observed_evidence() -> None:
    """调用工具后省略 evidence 会绕过展示冷却，因此必须不可修复地拒绝。"""

    evidence = _evidence()
    report = _validate(
        "这种雨声让我想起那块紫水晶。",
        evidence_ids=(),
        observations=(evidence,),
    )

    assert report.error_codes == ("EVIDENCE_REQUIRED_AFTER_OBSERVATION",)
    assert report.repairable is False
    assert report.checked_evidence_ids == ()


def test_multiple_violations_have_deterministic_order_and_sanitized_report() -> None:
    """报告只记录稳定类别，不回显候选、违规片段或可信分区字段。"""

    candidate = "search_memories **亲爱的**"
    report = _validate(
        candidate,
        evidence_ids=("memory:not-observed",),
    )
    serialized = json.dumps(report.to_dict(), ensure_ascii=False, sort_keys=True)

    assert report.error_codes == (
        "MARKDOWN_NOT_ALLOWED",
        "TOOL_NAME_LEAK",
        "EVIDENCE_NOT_OBSERVED",
        "RELATIONSHIP_POLICY_VIOLATION",
        "SOURCE_TOPIC_UNANCHORED",
    )
    assert report.repairable is False
    assert candidate not in serialized
    assert "save-guard-test" not in serialized
    assert "player-guard-test" not in serialized
    assert "task-guard-test" not in serialized
    assert "sha256:guard-source" not in serialized


def test_unknown_relationship_stage_uses_conservative_forbidden_terms() -> None:
    """Modded/未知阶段不能被默认升级成恋爱或婚姻表达权限。"""

    report = _validate(
        "雨天也想叫你亲爱的。",
        relationship_stage="modded-unknown-stage",
    )

    assert report.error_codes == ("RELATIONSHIP_POLICY_VIOLATION",)


@pytest.mark.parametrize("maximum", [0, 1_001, True, 1.5])
def test_guard_settings_reject_invalid_character_limits(maximum: object) -> None:
    """配置只允许明确的有限整数范围，避免 bool 或异常大值绕过产品边界。"""

    with pytest.raises(ValueError, match="max_text_characters"):
        DialogueGuardSettings(max_text_characters=maximum)  # type: ignore[arg-type]


def test_custom_maximum_can_only_change_the_length_boundary() -> None:
    """显式收紧长度配置不改变其他 Guard 规则或错误顺序。"""

    report = _validate(
        "雨天也不错。",
        guard=DialogueGuard(DialogueGuardSettings(max_text_characters=5)),
    )

    assert report.error_codes == ("TEXT_TOO_LONG",)


def test_source_player_name_slot_is_required_but_optional_source_may_add_it() -> None:
    """原文已有玩家名时不得疏远玩家；普通原文则允许 Agent 自然增加一次称呼。"""

    required_request = _request(source_text="@，雨天待在屋里也不算太糟。")
    required_item = required_request.items[0]
    profile = get_npc_agent_profile(required_item.npc_id)
    assert profile is not None
    without_slot = DialogueTextTemplate(
        prefix="雨天待在屋里也不算太糟。",
        address_slot=AddressSlot.NONE,
        suffix="",
    )

    required_report = DialogueGuard().validate(
        required_request,
        required_item,
        profile,
        DialogueGuardCandidate(template=without_slot, evidence_ids=()),  # type: ignore[call-arg]
        (),
    )

    optional_request = _request(source_text="雨天待在屋里也不算太糟。")
    optional_item = optional_request.items[0]
    with_slot = DialogueTextTemplate(
        prefix="",
        address_slot=AddressSlot.PLAYER_NAME,
        suffix="，雨天待在屋里也不算太糟。",
    )
    optional_report = DialogueGuard().validate(
        optional_request,
        optional_item,
        profile,
        DialogueGuardCandidate(template=with_slot, evidence_ids=()),  # type: ignore[call-arg]
        (),
    )

    assert required_report.error_codes == ("ADDRESS_SLOT_REQUIRED",)
    assert required_report.repairable is True
    assert optional_report.passed is True


@pytest.mark.parametrize(
    ("template", "expected_code"),
    [
        (
            DialogueTextTemplate.model_construct(
                prefix="雨天@",
                address_slot=AddressSlot.NONE,
                suffix="待在屋里。",
            ),
            "RAW_DIALOGUE_TOKEN_NOT_ALLOWED",
        ),
        (
            DialogueTextTemplate.model_construct(
                prefix="雨天$",
                address_slot=AddressSlot.NONE,
                suffix="待在屋里。",
            ),
            "DIALOGUE_DSL_NOT_ALLOWED",
        ),
        (
            DialogueTextTemplate.model_construct(
                prefix="雨天",
                address_slot="future_slot",
                suffix="待在屋里。",
            ),
            "ADDRESS_SLOT_NOT_ALLOWED",
        ),
    ],
)
def test_guard_reports_stable_template_error_codes_even_for_bypassed_models(
    template: DialogueTextTemplate,
    expected_code: str,
) -> None:
    """低层构造绕过 Pydantic 时，Guard 仍应失败而不是让非法字符串逃逸。"""

    request = _request()
    item = request.items[0]
    profile = get_npc_agent_profile(item.npc_id)
    assert profile is not None

    report = DialogueGuard().validate(
        request,
        item,
        profile,
        DialogueGuardCandidate(template=template, evidence_ids=()),  # type: ignore[call-arg]
        (),
    )

    assert report.error_codes == (expected_code,)
    assert report.repairable is True
