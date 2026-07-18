"""一次无工具 structured Repair 的 Prompt、权限、证据与错误边界测试。"""

from __future__ import annotations

import json
import time
from collections.abc import Sequence
from typing import Any, cast

import pytest
from langchain_core.callbacks import (
    AsyncCallbackManagerForLLMRun,
    CallbackManagerForLLMRun,
)
from langchain_core.language_models import BaseChatModel
from langchain_core.messages import AIMessage, BaseMessage, HumanMessage, SystemMessage
from langchain_core.outputs import ChatGeneration, ChatResult
from langchain_core.runnables import Runnable
from pydantic import PrivateAttr
from typing_extensions import override

from stardew_npc_agent.dialogue_repair import (
    DIALOGUE_REPAIR_PROMPT_VERSION,
    DialogueRepairExecutionError,
    DialogueRepairRunner,
)
from stardew_npc_agent.dialogue_template import AddressSlot, DialogueTextTemplate
from stardew_npc_agent.dialogue_usage import DialogueModelUsage
from stardew_npc_agent.guard import (
    DialogueGuard,
    DialogueGuardCandidate,
)
from stardew_npc_agent.profiles import get_npc_agent_profile
from stardew_npc_agent.schemas import DialogueGenerationBatchRequest
from stardew_npc_agent.storage import EvidenceRecord


class _RepairScriptedModel(BaseChatModel):
    """记录 structured schema 绑定与 Prompt，并按顺序返回消息/异常。"""

    _steps: list[AIMessage | Exception] = PrivateAttr()
    _bound_tool_names: tuple[str, ...] = PrivateAttr(default=())
    _messages_per_call: list[tuple[BaseMessage, ...]] = PrivateAttr(default_factory=list)
    _physical_calls: int = PrivateAttr(default=0)

    def __init__(self, steps: Sequence[AIMessage | Exception]) -> None:
        """复制脚本，防止测试调用方随后原地修改。"""

        super().__init__()
        self._steps = list(steps)

    @property
    def bound_tool_names(self) -> tuple[str, ...]:
        """返回 `with_structured_output` 实际绑定的 schema 名称。"""

        return self._bound_tool_names

    @property
    def messages_per_call(self) -> tuple[tuple[BaseMessage, ...], ...]:
        """返回每次模型调用收到的消息快照。"""

        return tuple(self._messages_per_call)

    @property
    def physical_calls(self) -> int:
        """返回实际模型调用数，供“最多一次”与 deadline 断言。"""

        return self._physical_calls

    @property
    @override
    def _llm_type(self) -> str:
        return "stardew-scripted-repair-model"

    @override
    def bind_tools(
        self,
        tools: Sequence[dict[str, Any] | type | Any],
        *,
        tool_choice: str | None = None,
        **kwargs: Any,
    ) -> Runnable[Any, BaseMessage]:
        """记录绑定；Repair 不应收到任何领域工具。"""

        del tool_choice, kwargs
        self._bound_tool_names = tuple(_bound_tool_name(value) for value in tools)
        return self

    @override
    def _generate(
        self,
        messages: list[BaseMessage],
        stop: list[str] | None = None,
        run_manager: CallbackManagerForLLMRun | None = None,
        **kwargs: Any,
    ) -> ChatResult:
        """满足 BaseChatModel 同步合同；生产 runner 使用异步路径。"""

        del stop, run_manager, kwargs
        return self._next_result(messages)

    @override
    async def _agenerate(
        self,
        messages: list[BaseMessage],
        stop: list[str] | None = None,
        run_manager: AsyncCallbackManagerForLLMRun | None = None,
        **kwargs: Any,
    ) -> ChatResult:
        """消费一步脚本并保存本次 Prompt。"""

        del stop, run_manager, kwargs
        return self._next_result(messages)

    def _next_result(self, messages: list[BaseMessage]) -> ChatResult:
        """返回下一条 AIMessage 或抛出脚本异常。"""

        self._physical_calls += 1
        self._messages_per_call.append(tuple(messages))
        if not self._steps:
            raise AssertionError("repair scripted model steps exhausted")
        step = self._steps.pop(0)
        if isinstance(step, Exception):
            raise step
        return ChatResult(generations=[ChatGeneration(message=step)])


def _bound_tool_name(value: object) -> str:
    """从 Pydantic class、BaseTool 或 OpenAI-style dict 提取 schema 名。"""

    name = getattr(value, "name", None)
    if isinstance(name, str):
        return name
    if isinstance(value, type):
        return value.__name__
    if isinstance(value, dict):
        direct_name = value.get("name")
        if isinstance(direct_name, str):
            return direct_name
        function = value.get("function")
        if isinstance(function, dict) and isinstance(function.get("name"), str):
            return cast(str, function["name"])
    raise AssertionError(f"unknown bound tool representation: {type(value).__name__}")


def _repair_message(
    *,
    template: DialogueTextTemplate,
    evidence_ids: list[str],
    input_tokens: int = 12,
    output_tokens: int = 5,
) -> AIMessage:
    """构造 `with_structured_output` 可解析且带 Token usage 的返回。"""

    return AIMessage(
        content="",
        tool_calls=[
            {
                "name": "DialogueRepairDecision",
                "id": "repair-decision-call",
                "args": {
                    "template": template.model_dump(mode="json"),
                    "evidence_ids": evidence_ids,
                },
                "type": "tool_call",
            }
        ],
        usage_metadata={
            "input_tokens": input_tokens,
            "output_tokens": output_tokens,
            "total_tokens": input_tokens + output_tokens,
        },
    )


def _request(
    *,
    source_text: str = "雨天待在屋里也不算太糟。",
) -> DialogueGenerationBatchRequest:
    """构造 Repair/Guard 共用的合法雨天任务。"""

    return DialogueGenerationBatchRequest.model_validate(
        {
            "schema_version": "1.0",
            "request_id": "request-repair",
            "save_id": "save-repair",
            "player_id": "player-repair",
            "game_day_index": 10,
            "required_memory_revision": 1,
            "stable_day_context": {
                "season": "fall",
                "weather": "rain",
                "locale": "zh-CN",
                "progression_signals": {
                    "year": 1,
                    "day_of_month": 10,
                    "mine_level": 40,
                },
            },
            "items": [
                {
                    "task_id": "task-repair",
                    "npc_id": "Abigail",
                    "source_dialogue": {
                        "asset_name": "Characters/Dialogue/Abigail",
                        "dialogue_key": "fall_Mon",
                        "text": source_text,
                        "source_hash": "sha256:repair-source",
                    },
                    "relationship_snapshot": {
                        "friendship_points": 750,
                        "relationship_stage": "friend",
                    },
                    "style_examples": ["样本一。", "样本二。", "样本三。"],
                    "memory_signals": [{"event_type": "gift_given"}],
                }
            ],
        }
    )


def _evidence(evidence_id: str = "memory:gift-amethyst") -> EvidenceRecord:
    """构造可被 Repair 保留但不能扩张的 observed evidence。"""

    return EvidenceRecord(
        evidence_id=evidence_id,
        evidence_type="gift_given",
        source_event_ids=("event:gift-amethyst",),
        summary="玩家曾送给 Abigail 一块紫水晶。",
        occurred_day_index=8,
        tags=("gift", "amethyst"),
        visibility_scope="npc:Abigail",
    )


def _repair_inputs():
    """返回 request/item/profile、可修复首轮报告和原始候选。"""

    request = _request()
    item = request.items[0]
    profile = get_npc_agent_profile(item.npc_id)
    assert profile is not None
    evidence = _evidence()
    candidate = DialogueGuardCandidate(
        template=DialogueTextTemplate.model_construct(
            prefix="雨天 **待在屋里** 也会想起那块紫水晶。",
            address_slot=AddressSlot.NONE,
            suffix="",
        ),
        evidence_ids=(evidence.evidence_id,),
    )
    report = DialogueGuard().validate(
        request,
        item,
        profile,
        candidate,
        (evidence,),
    )
    assert report.error_codes == ("MARKDOWN_NOT_ALLOWED",)
    assert report.repairable is True
    return request, item, profile, evidence, candidate, report


@pytest.mark.asyncio
async def test_repair_uses_only_structured_schema_and_returns_usage() -> None:
    """一次合法修复只绑定 Repair DTO，并返回可审计 Token usage。"""

    request, item, profile, evidence, candidate, report = _repair_inputs()
    model = _RepairScriptedModel(
        [
            _repair_message(
                template=DialogueTextTemplate(
                    prefix="雨天待在屋里，也会让我想起那块紫水晶。",
                    address_slot=AddressSlot.NONE,
                    suffix="",
                ),
                evidence_ids=[evidence.evidence_id],
            )
        ]
    )

    result = await DialogueRepairRunner(model).repair(
        request=request,
        item=item,
        profile=profile,
        original_candidate=candidate,
        guard_report=report,
        observations=(evidence,),
        deadline_monotonic=time.monotonic() + 10,
    )

    assert result.decision.template == DialogueTextTemplate(
        prefix="雨天待在屋里，也会让我想起那块紫水晶。",
        address_slot=AddressSlot.NONE,
        suffix="",
    )
    assert result.decision.evidence_ids == (evidence.evidence_id,)
    assert result.usage == DialogueModelUsage(
        input_tokens=12,
        output_tokens=5,
        total_tokens=17,
        reported_calls=1,
    )
    assert model.physical_calls == 1
    assert model.bound_tool_names == ("DialogueRepairDecision",)


@pytest.mark.asyncio
async def test_repair_prompt_separates_rules_from_untrusted_data() -> None:
    """候选、游戏资产与 evidence 只能进入数据区，不能覆盖固定 Repair 规则。"""

    request, item, profile, evidence, candidate, report = _repair_inputs()
    model = _RepairScriptedModel(
        [
            _repair_message(
                template=DialogueTextTemplate(
                    prefix="雨天待在屋里，也会让我想起那块紫水晶。",
                    address_slot=AddressSlot.NONE,
                    suffix="",
                ),
                evidence_ids=[evidence.evidence_id],
            )
        ]
    )

    await DialogueRepairRunner(model).repair(
        request=request,
        item=item,
        profile=profile,
        original_candidate=candidate,
        guard_report=report,
        observations=(evidence,),
        deadline_monotonic=time.monotonic() + 10,
    )

    messages = model.messages_per_call[0]
    assert len(messages) == 2
    assert isinstance(messages[0], SystemMessage)
    assert isinstance(messages[1], HumanMessage)
    assert "不得新增 evidence" in cast(str, messages[0].content)
    data_content = cast(str, messages[1].content)
    prefix = "[数据区]\n<untrusted_repair_data>\n"
    suffix = "\n</untrusted_repair_data>"
    assert data_content.startswith(prefix)
    assert data_content.endswith(suffix)
    payload = json.loads(data_content[len(prefix) : -len(suffix)])
    assert payload["source_dialogue"] == {
        "requires_player_name": False,
        "template": {
            "address_slot": "none",
            "prefix": item.source_dialogue.text,
            "suffix": "",
        },
    }
    assert payload["candidate"]["template"] == candidate.template.model_dump(mode="json")
    assert payload["candidate"]["evidence_ids"] == [evidence.evidence_id]
    assert payload["guard_error_codes"] == ["MARKDOWN_NOT_ALLOWED"]
    assert payload["allowed_evidence"][0]["evidence_id"] == evidence.evidence_id
    assert payload["relationship_policy"] == profile.relationship_policy_for("friend")
    assert payload["repair_prompt_version"] == DIALOGUE_REPAIR_PROMPT_VERSION
    assert DIALOGUE_REPAIR_PROMPT_VERSION == "dialogue-repair-prompt-v3"
    assert payload["stable_day_context"]["progression_signals"] == {
        "day_of_month": 10,
        "year": 1,
    }
    assert "mine_level" not in data_content
    assert "memory_signals" not in data_content


@pytest.mark.asyncio
async def test_repair_cannot_add_evidence() -> None:
    """structured shape 合法也不能把新 ID 注入第二次 Guard。"""

    request, item, profile, evidence, candidate, report = _repair_inputs()
    model = _RepairScriptedModel(
        [
            _repair_message(
                template=DialogueTextTemplate(
                    prefix="雨天待在屋里，也会想起新的秘密。",
                    address_slot=AddressSlot.NONE,
                    suffix="",
                ),
                evidence_ids=["memory:new-unobserved"],
            )
        ]
    )

    with pytest.raises(DialogueRepairExecutionError) as caught:
        await DialogueRepairRunner(model).repair(
            request=request,
            item=item,
            profile=profile,
            original_candidate=candidate,
            guard_report=report,
            observations=(evidence,),
            deadline_monotonic=time.monotonic() + 10,
        )

    assert caught.value.reason_code == "REPAIR_EVIDENCE_ESCALATION"
    assert caught.value.usage == DialogueModelUsage(
        input_tokens=12,
        output_tokens=5,
        total_tokens=17,
        reported_calls=1,
    )
    assert model.physical_calls == 1


@pytest.mark.asyncio
async def test_repair_receives_slot_policy_and_can_restore_required_player_name() -> None:
    """首轮 Guard 丢失称呼槽后，唯一一次 Repair 应按错误码补回 typed 槽。"""

    request = _request(source_text="@，雨天待在屋里也不算太糟。")
    request = request.model_copy(update={"player_id": "actual-player-name-sentinel"})
    item = request.items[0]
    profile = get_npc_agent_profile(item.npc_id)
    assert profile is not None
    candidate = DialogueGuardCandidate(
        template=DialogueTextTemplate(
            prefix="雨天待在屋里也不算太糟。",
            address_slot=AddressSlot.NONE,
            suffix="",
        ),
        evidence_ids=(),
    )
    report = DialogueGuard().validate(request, item, profile, candidate, ())
    assert report.error_codes == ("ADDRESS_SLOT_REQUIRED",)

    repaired_template = DialogueTextTemplate(
        prefix="",
        address_slot=AddressSlot.PLAYER_NAME,
        suffix="，雨天待在屋里也不算太糟。",
    )
    model = _RepairScriptedModel([_repair_message(template=repaired_template, evidence_ids=[])])

    result = await DialogueRepairRunner(model).repair(
        request=request,
        item=item,
        profile=profile,
        original_candidate=candidate,
        guard_report=report,
        observations=(),
        deadline_monotonic=time.monotonic() + 10,
    )

    assert result.decision.template == repaired_template
    prompt_content = "\n".join(str(message.content) for message in model.messages_per_call[0])
    assert "ADDRESS_SLOT_REQUIRED" in prompt_content
    assert "player_name" in prompt_content
    assert "@" not in prompt_content
    assert request.player_id not in prompt_content


@pytest.mark.asyncio
async def test_repair_parsing_model_error_and_deadline_use_stable_codes() -> None:
    """解析、Provider 和 deadline 失败均不得回显异常或 Prompt。"""

    request, item, profile, evidence, candidate, report = _repair_inputs()
    secret = "/secret/provider.key SELECT prompt FROM internal"
    cases = [
        (
            _RepairScriptedModel([AIMessage(content="not structured")]),
            time.monotonic() + 10,
            "REPAIR_STRUCTURED_RESPONSE_INVALID",
            1,
        ),
        (
            _RepairScriptedModel([RuntimeError(secret)]),
            time.monotonic() + 10,
            "REPAIR_EXECUTION_FAILED",
            1,
        ),
        (
            _RepairScriptedModel([AssertionError("must not run")]),
            time.monotonic() - 1,
            "REPAIR_DEADLINE_EXCEEDED",
            0,
        ),
    ]

    for model, deadline, expected_code, expected_calls in cases:
        with pytest.raises(DialogueRepairExecutionError) as caught:
            await DialogueRepairRunner(model).repair(
                request=request,
                item=item,
                profile=profile,
                original_candidate=candidate,
                guard_report=report,
                observations=(evidence,),
                deadline_monotonic=deadline,
            )
        assert caught.value.reason_code == expected_code
        assert secret not in str(caught.value)
        assert model.physical_calls == expected_calls
