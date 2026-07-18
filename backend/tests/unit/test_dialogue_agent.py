"""真实 LangChain ``create_agent`` 图的分支、预算、重试与轨迹测试。"""

from __future__ import annotations

import time
from collections.abc import Sequence
from typing import Any, cast

import httpx
import pytest
from langchain_core.callbacks import (
    AsyncCallbackManagerForLLMRun,
    CallbackManagerForLLMRun,
)
from langchain_core.language_models import BaseChatModel
from langchain_core.messages import AIMessage, BaseMessage, ToolMessage, UsageMetadata
from langchain_core.outputs import ChatGeneration, ChatResult
from langchain_core.runnables import Runnable
from openai import APIConnectionError, APITimeoutError
from pydantic import PrivateAttr
from typing_extensions import override

import stardew_npc_agent.dialogue_tools as dialogue_tools_module
from stardew_npc_agent.dialogue_agent import (
    DialogueAgentExecutionError,
    DialogueAgentFactory,
    DialogueAgentRunner,
    DialogueAgentSettings,
    DialogueAgentTransientModelError,
    DialoguePromptBuilder,
    DialogueRuntimeContext,
)
from stardew_npc_agent.dialogue_template import render_game_template
from stardew_npc_agent.dialogue_tools import (
    DIALOGUE_TOOL_NAMES,
    DialogueToolAuthorizationError,
    build_domain_dialogue_tools,
)
from stardew_npc_agent.dialogue_usage import DialogueModelUsage
from stardew_npc_agent.memory_capabilities import build_target_capability_registry
from stardew_npc_agent.profiles import get_npc_agent_profile
from stardew_npc_agent.schemas import DialogueGenerationBatchRequest
from stardew_npc_agent.storage import EvidenceRecord, SqliteStorage, StorageUnavailableError

_EXPECTED_TOOL_SCHEMA_VALIDATION_MESSAGE = (
    "Tool input does not match the published schema. Correct the arguments or return passthrough."
)
_EXPECTED_TOOL_DYNAMIC_INPUT_MESSAGE = (
    "Tool input violates a dynamic query boundary. Correct the arguments or return passthrough."
)
_EXPECTED_STRUCTURED_OUTPUT_VALIDATION_MESSAGE = (
    "Structured response is invalid. Return exactly one valid DialogueAgentDecision."
)


class _SecretStorageUnavailableError(StorageUnavailableError):
    """模拟包含路径/SQL 的底层瞬态错误，验证 runner 必须清除正文。"""

    def __str__(self) -> str:
        return "database /secret/save.sqlite failed near SELECT private_prompt"


class _ScriptedChatModel(BaseChatModel):
    """可脚本化 AIMessage/异常并记录每次物理调用工具绑定的测试模型。"""

    _steps: list[AIMessage | Exception] = PrivateAttr()
    _current_bound_tools: tuple[str, ...] = PrivateAttr(default=())
    _physical_call_count: int = PrivateAttr(default=0)
    _bindings_per_call: list[tuple[str, ...]] = PrivateAttr(default_factory=list)
    _messages_per_call: list[tuple[BaseMessage, ...]] = PrivateAttr(default_factory=list)

    def __init__(self, steps: Sequence[AIMessage | Exception]) -> None:
        """复制脚本，防止测试调用方随后修改原 list。"""

        super().__init__()
        self._steps = list(steps)

    @property
    def physical_call_count(self) -> int:
        """返回包含网络重试在内的模型物理调用次数。"""

        return self._physical_call_count

    @property
    def bindings_per_call(self) -> tuple[tuple[str, ...], ...]:
        """返回每次物理模型调用实际绑定的工具名。"""

        return tuple(self._bindings_per_call)

    @property
    def messages_per_call(self) -> tuple[tuple[BaseMessage, ...], ...]:
        """返回模型每轮收到的完整消息快照，供真实轨迹断言。"""

        return tuple(self._messages_per_call)

    @property
    @override
    def _llm_type(self) -> str:
        return "stardew-scripted-chat-model"

    @override
    def bind_tools(
        self,
        tools: Sequence[dict[str, Any] | type | Any],
        *,
        tool_choice: str | None = None,
        **kwargs: Any,
    ) -> Runnable[Any, BaseMessage]:
        """记录 LangChain 本轮真正绑定的领域/structured-output 工具。"""

        del tool_choice, kwargs
        self._current_bound_tools = tuple(_bound_tool_name(value) for value in tools)
        return self

    @override
    def _generate(
        self,
        messages: list[BaseMessage],
        stop: list[str] | None = None,
        run_manager: CallbackManagerForLLMRun | None = None,
        **kwargs: Any,
    ) -> ChatResult:
        """同步路径仅为满足 BaseChatModel 合同；生产 runner 使用异步调用。"""

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
        """返回下一脚本步骤；异常用于驱动官方 ModelRetryMiddleware。"""

        del stop, run_manager, kwargs
        return self._next_result(messages)

    def _next_result(self, messages: list[BaseMessage]) -> ChatResult:
        """消费一个物理调用步骤并保存工具/消息证据。"""

        self._physical_call_count += 1
        self._bindings_per_call.append(self._current_bound_tools)
        self._messages_per_call.append(tuple(messages))
        if not self._steps:
            raise AssertionError("scripted model steps exhausted")
        step = self._steps.pop(0)
        if isinstance(step, Exception):
            raise step
        return ChatResult(generations=[ChatGeneration(message=step)])


class _AgentStorage:
    """真实 Agent 工具可调用的无 I/O 存储替身，支持瞬态/永久失败脚本。"""

    def __init__(
        self,
        *,
        failures: Sequence[Exception] = (),
    ) -> None:
        self.calls: list[tuple[str, object]] = []
        self._failures = list(failures)

    async def search_memories(self, query: object) -> list[EvidenceRecord]:
        """返回 NPC 私有礼物证据。"""

        self.calls.append(("search_memories", query))
        self._raise_scripted_failure()
        return [_evidence("memory:gift", "gift_given", "npc:Abigail")]

    async def get_event_history(self, query: object) -> list[EvidenceRecord]:
        """返回事件历史证据。"""

        self.calls.append(("get_event_history", query))
        self._raise_scripted_failure()
        return [_evidence("memory:history", "gift_given", "npc:Abigail")]

    async def get_progression_context(self, query: object) -> list[EvidenceRecord]:
        """返回公共世界进度证据。"""

        self.calls.append(("get_progression_context", query))
        self._raise_scripted_failure()
        return [_evidence("memory:mine", "world_progression", "public")]

    async def get_domain_memory_candidates(self, query: object) -> list[EvidenceRecord]:
        """返回目标零参数 player progression 工具的分类完整证据。"""

        self.calls.append(("get_domain_memory_candidates", query))
        self._raise_scripted_failure()
        return [
            EvidenceRecord(
                evidence_id="memory:farming",
                evidence_type="skill_level_reached",
                source_event_ids=("event:memory:farming",),
                summary="第 8 天，玩家的耕种技能提升到 2 级。",
                occurred_day_index=8,
                tags=("skill:farming",),
                visibility_scope="public",
                memory_domain="player_progression",
                memory_kind="skill_level_reached",
                subject_namespace="skill_id",
                subject_value="farming",
            )
        ]

    def _raise_scripted_failure(self) -> None:
        """每次 storage 调用最多消费一个预设异常。"""

        if self._failures:
            raise self._failures.pop(0)


def _bound_tool_name(value: object) -> str:
    """兼容 BaseTool 与 OpenAI-style dict，提取测试所需工具名。"""

    name = getattr(value, "name", None)
    if isinstance(name, str):
        return name
    if isinstance(value, dict):
        direct_name = value.get("name")
        if isinstance(direct_name, str):
            return direct_name
        function = value.get("function")
        if isinstance(function, dict) and isinstance(function.get("name"), str):
            return cast(str, function["name"])
    raise AssertionError(f"unknown bound tool representation: {type(value).__name__}")


def _evidence(evidence_id: str, evidence_type: str, visibility_scope: str) -> EvidenceRecord:
    """构造可从 ToolMessage.artifact 提取的 evidence。"""

    return EvidenceRecord(
        evidence_id=evidence_id,
        evidence_type=evidence_type,
        source_event_ids=(f"event:{evidence_id}",),
        summary=f"summary:{evidence_id}",
        occurred_day_index=8,
        tags=("gift",),
        visibility_scope=visibility_scope,
    )


def _request() -> DialogueGenerationBatchRequest:
    """构造 Agent runner 共用的合法单 NPC 每日任务。"""

    return DialogueGenerationBatchRequest.model_validate(
        {
            "schema_version": "1.0",
            "request_id": "request-agent",
            "save_id": "save-1",
            "player_id": "player-1",
            "game_day_index": 10,
            "required_memory_revision": 2,
            "stable_day_context": {
                "season": "fall",
                "weather": "rain",
                "locale": "zh-CN",
                "progression_signals": {"mine_level": 40},
            },
            "items": [
                {
                    "task_id": "task-abigail",
                    "npc_id": "Abigail",
                    "source_dialogue": {
                        "asset_name": "Characters/Dialogue/Abigail",
                        "dialogue_key": "fall_Mon",
                        "text": "雨天待在屋里也不算太糟。",
                        "source_hash": "sha256:source",
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


def _runtime(
    storage: _AgentStorage,
    *,
    allowed_tools: frozenset[str] = DIALOGUE_TOOL_NAMES,
    cutoff_day_index: int = 9,
) -> DialogueRuntimeContext:
    """构造可信 runtime；模型不能看到其中的分区字段和 storage。"""

    return DialogueRuntimeContext(
        task_id="task-abigail",
        save_id="save-1",
        player_id="player-1",
        npc_id="Abigail",
        game_day_index=10,
        cutoff_day_index=cutoff_day_index,
        friendship_points=750,
        relationship_stage="friend",
        memory_cooldown_days=3,
        allowed_tools=allowed_tools,
        storage=cast(SqliteStorage, storage),
        deadline_monotonic=time.monotonic() + 10,
        source_dialogue_text="今年的收获和作物看起来真不错。",
        source_hash="sha256:source",
        locale="zh-CN",
        required_memory_revision=2,
        resolved_memory_revision=8,
        resolved_retrieval_state_revision=5,
    )


def _prompt(allowed_tools: frozenset[str]) -> object:
    """构造与 runtime allowlist 一致的版本化 Prompt。"""

    request = _request()
    profile = get_npc_agent_profile("Abigail")
    assert profile is not None
    return DialoguePromptBuilder().build(
        request,
        request.items[0],
        profile,
        allowed_tools=allowed_tools,
    )


def _tool_call(name: str, call_id: str, args: dict[str, object]) -> dict[str, object]:
    """构造 LangChain 标准 tool call dict。"""

    return {"name": name, "id": call_id, "args": args, "type": "tool_call"}


def _openai_request() -> httpx.Request:
    """构造不含真实 endpoint、凭据或游戏内容的 OpenAI SDK 异常上下文。"""

    return httpx.Request("POST", "https://provider.invalid/v1/responses")


def test_agent_settings_accept_approved_deadline_and_reject_over_120_seconds() -> None:
    """Agent 内部配置必须与 Pydantic 应用配置使用同一 120 秒硬上限。

    这个测试防止组合根接受 90 秒配置后，工厂又以旧的 60 秒内部上限拒绝启动；同时
    保留有限 deadline，确保慢 Provider 不会把一次日更任务变成无限后台工作。
    """

    assert DialogueAgentSettings(task_deadline_seconds=90.0).task_deadline_seconds == 90.0
    with pytest.raises(ValueError, match="task_deadline_seconds"):
        DialogueAgentSettings(task_deadline_seconds=120.001)


def _tool_calls(
    *calls: dict[str, object],
    usage_metadata: UsageMetadata | None = None,
) -> AIMessage:
    """返回一个可包含同轮并行调用的 AIMessage。"""

    return AIMessage(
        content="",
        tool_calls=list(calls),
        usage_metadata=usage_metadata,
    )


def _usage(input_tokens: int, output_tokens: int) -> UsageMetadata:
    """构造 LangChain 标准 usage metadata，避免测试使用任意 dict shape。"""

    return UsageMetadata(
        input_tokens=input_tokens,
        output_tokens=output_tokens,
        total_tokens=input_tokens + output_tokens,
    )


def _final(
    decision: str,
    *,
    text: str | None,
    evidence_ids: list[str] | None = None,
    reason_code: str,
    call_id: str = "decision-call",
    usage_metadata: UsageMetadata | None = None,
) -> AIMessage:
    """通过 ToolStrategy 的结构化输出工具返回最终业务决策。"""

    return _tool_calls(
        _tool_call(
            "DialogueAgentDecision",
            call_id,
            {
                "decision": decision,
                "template": (
                    None
                    if text is None
                    else {
                        "prefix": text,
                        "address_slot": "none",
                        "suffix": "",
                    }
                ),
                "evidence_ids": evidence_ids or [],
                "reason_code": reason_code,
            },
        ),
        usage_metadata=usage_metadata,
    )


def _runner(
    model: _ScriptedChatModel,
    *,
    settings: DialogueAgentSettings | None = None,
) -> tuple[DialogueAgentFactory, DialogueAgentRunner]:
    """创建零 Provider、零网络的 factory/runner。"""

    factory = DialogueAgentFactory(
        model=model,
        model_configuration="scripted-agent-test-v1",
        settings=settings,
    )
    return factory, DialogueAgentRunner(factory)


def _target_runner(
    model: _ScriptedChatModel,
) -> tuple[DialogueAgentFactory, DialogueAgentRunner]:
    """创建显式 target active 工具副本；不会修改生产 active registry。"""

    tools = build_domain_dialogue_tools(build_target_capability_registry())
    factory = DialogueAgentFactory(
        model=model,
        model_configuration="scripted-target-domain-test-v1",
        tools=tools,
    )
    return factory, DialogueAgentRunner(factory)


@pytest.mark.asyncio
@pytest.mark.parametrize(
    ("final_message", "expected_decision", "expected_text"),
    [
        (
            _final(
                "passthrough",
                text=None,
                reason_code="NO_VALUABLE_ENHANCEMENT",
            ),
            "passthrough",
            None,
        ),
        (
            _final(
                "rewrite",
                text="雨声其实还挺适合发呆的。",
                reason_code="SOURCE_STYLE_REWRITE",
            ),
            "rewrite",
            "雨声其实还挺适合发呆的。",
        ),
    ],
    ids=["zero-tool-passthrough", "zero-tool-rewrite"],
)
async def test_zero_tool_decisions_come_from_real_structured_agent_response(
    final_message: AIMessage,
    expected_decision: str,
    expected_text: str | None,
) -> None:
    """Agent 可以不调用领域工具；rewrite 在本阶段仍只是未 Guard 候选。"""

    model = _ScriptedChatModel([final_message])
    storage = _AgentStorage()
    _factory, runner = _runner(model)

    result = await runner.run(
        prompt=_prompt(frozenset()),
        runtime=_runtime(storage, allowed_tools=frozenset()),
    )

    assert result.decision.decision == expected_decision
    assert (
        None if result.decision.template is None else render_game_template(result.decision.template)
    ) == expected_text
    assert result.used_tools == ()
    assert result.tool_traces == ()
    assert result.observed_evidence == ()
    assert result.logical_model_calls == 1
    assert storage.calls == []
    assert model.bindings_per_call == (("DialogueAgentDecision",),)


@pytest.mark.asyncio
async def test_one_tool_trace_and_evidence_are_derived_from_real_tool_message_artifact() -> None:
    """模型 claim 不能制造 observed evidence；artifact 才是事实来源。"""

    model = _ScriptedChatModel(
        [
            _tool_calls(
                _tool_call(
                    "search_memories",
                    "call-search",
                    {"terms": ["gift"], "limit": 1},
                )
            ),
            _final(
                "rewrite",
                text="那块紫水晶确实很有意思。",
                evidence_ids=["memory:model-claim-not-observed"],
                reason_code="RELEVANT_SHARED_MEMORY",
            ),
        ]
    )
    storage = _AgentStorage()
    _factory, runner = _runner(model)
    allowed = frozenset({"search_memories"})

    result = await runner.run(
        prompt=_prompt(allowed), runtime=_runtime(storage, allowed_tools=allowed)
    )

    assert result.decision.evidence_ids == ("memory:model-claim-not-observed",)
    assert [item.evidence_id for item in result.observed_evidence] == ["memory:gift"]
    assert result.used_tools == ("search_memories",)
    assert len(result.tool_traces) == 1
    assert result.tool_traces[0].tool_call_id == "call-search"
    assert result.tool_traces[0].outcome == "succeeded"
    assert result.tool_traces[0].evidence_ids == ("memory:gift",)
    assert result.logical_model_calls == 2
    assert [name for name, _query in storage.calls] == ["search_memories"]


@pytest.mark.asyncio
async def test_two_parallel_tools_execute_in_one_real_agent_round() -> None:
    """同轮两个领域工具合法，结果顺序按 AIMessage tool call 顺序审计。"""

    model = _ScriptedChatModel(
        [
            _tool_calls(
                _tool_call(
                    "search_memories",
                    "call-search",
                    {"terms": ["gift"], "limit": 1},
                ),
                _tool_call(
                    "get_progression_context",
                    "call-progression",
                    {"topics": ["mine"], "since_day_index": 0, "limit": 1},
                ),
            ),
            _final(
                "rewrite",
                text="雨天去矿井也许会挺有意思。",
                evidence_ids=["memory:mine"],
                reason_code="RELEVANT_WORLD_PROGRESS",
            ),
        ]
    )
    storage = _AgentStorage()
    _factory, runner = _runner(model)
    allowed = frozenset({"search_memories", "get_progression_context"})

    result = await runner.run(
        prompt=_prompt(allowed), runtime=_runtime(storage, allowed_tools=allowed)
    )

    assert [trace.tool_name for trace in result.tool_traces] == [
        "search_memories",
        "get_progression_context",
    ]
    assert all(trace.outcome == "succeeded" for trace in result.tool_traces)
    assert {item.evidence_id for item in result.observed_evidence} == {
        "memory:gift",
        "memory:mine",
    }
    assert len(storage.calls) == 2


@pytest.mark.asyncio
async def test_target_agent_executes_zero_argument_domain_tool_and_audits_empty_args() -> None:
    """target fixture 必须真实经过 create_agent/ToolMessage，而非只测独立函数。"""

    model = _ScriptedChatModel(
        [
            _tool_calls(
                _tool_call(
                    "get_player_progression",
                    "call-player-progression",
                    {},
                )
            ),
            _final(
                "rewrite",
                text="今年的收获看起来也许会更顺手。",
                evidence_ids=["memory:farming"],
                reason_code="RELEVANT_PLAYER_PROGRESSION",
            ),
        ]
    )
    storage = _AgentStorage()
    factory, runner = _target_runner(model)
    allowed = frozenset({"get_player_progression"})

    result = await runner.run(
        prompt=_prompt(allowed),
        runtime=_runtime(storage, allowed_tools=allowed),
    )

    assert factory.available_tool_names == frozenset(
        {"get_npc_history", "get_player_progression", "get_world_progression"}
    )
    assert result.used_tools == ("get_player_progression",)
    assert result.tool_traces[0].arguments == {}
    assert result.tool_traces[0].evidence_ids == ("memory:farming",)
    assert [item.evidence_id for item in result.observed_evidence] == ["memory:farming"]
    assert [name for name, _query in storage.calls] == ["get_domain_memory_candidates"]
    assert model.bindings_per_call[0] == (
        "get_player_progression",
        "DialogueAgentDecision",
    )


@pytest.mark.asyncio
async def test_after_two_tool_rounds_next_model_binding_contains_only_structured_output() -> None:
    """ForceFinal middleware 必须改变真实模型绑定，而不只是追加 Prompt 建议。"""

    model = _ScriptedChatModel(
        [
            _tool_calls(_tool_call("search_memories", "call-1", {"terms": ["gift"], "limit": 1})),
            _tool_calls(
                _tool_call(
                    "get_event_history",
                    "call-2",
                    {
                        "topics": ["gift"],
                        "event_types": ["gift_given"],
                        "since_day_index": 0,
                        "limit": 1,
                    },
                )
            ),
            _final(
                "passthrough",
                text=None,
                reason_code="NO_VALUABLE_ENHANCEMENT",
            ),
        ]
    )
    storage = _AgentStorage()
    _factory, runner = _runner(model)
    allowed = frozenset({"search_memories", "get_event_history"})

    result = await runner.run(
        prompt=_prompt(allowed), runtime=_runtime(storage, allowed_tools=allowed)
    )

    assert result.logical_model_calls == 3
    assert set(model.bindings_per_call[0]) == {*allowed, "DialogueAgentDecision"}
    assert set(model.bindings_per_call[1]) == {*allowed, "DialogueAgentDecision"}
    assert model.bindings_per_call[2] == ("DialogueAgentDecision",)


@pytest.mark.asyncio
async def test_official_total_limit_blocks_fourth_domain_call() -> None:
    """两轮内前三次可执行，第四次由官方 ToolCallLimitMiddleware 阻止。"""

    model = _ScriptedChatModel(
        [
            _tool_calls(
                _tool_call(
                    "search_memories",
                    "call-1",
                    {"terms": ["gift"], "limit": 1},
                ),
                _tool_call(
                    "get_event_history",
                    "call-2",
                    {
                        "topics": ["gift"],
                        "event_types": ["gift_given"],
                        "since_day_index": 0,
                        "limit": 1,
                    },
                ),
            ),
            _tool_calls(
                _tool_call(
                    "get_progression_context",
                    "call-3",
                    {"topics": ["mine"], "since_day_index": 0, "limit": 1},
                ),
                _tool_call(
                    "search_memories",
                    "call-4",
                    {"terms": ["rain"], "limit": 1},
                ),
            ),
            _final(
                "passthrough",
                text=None,
                reason_code="NO_VALUABLE_ENHANCEMENT",
            ),
        ]
    )
    storage = _AgentStorage()
    _factory, runner = _runner(model)

    result = await runner.run(
        prompt=_prompt(DIALOGUE_TOOL_NAMES),
        runtime=_runtime(storage),
    )

    assert len(storage.calls) == 3
    assert [trace.outcome for trace in result.tool_traces] == [
        "succeeded",
        "succeeded",
        "succeeded",
        "blocked",
    ]
    assert result.tool_traces[-1].error_code == "TOOL_CALL_LIMIT_EXCEEDED"


@pytest.mark.asyncio
async def test_parallel_limit_blocks_third_call_before_execution() -> None:
    """同轮第三个调用不进入 storage，但前两个仍可正常完成。"""

    model = _ScriptedChatModel(
        [
            _tool_calls(
                _tool_call(
                    "search_memories",
                    "call-1",
                    {"terms": ["gift"], "limit": 1},
                ),
                _tool_call(
                    "get_event_history",
                    "call-2",
                    {
                        "topics": ["gift"],
                        "event_types": ["gift_given"],
                        "since_day_index": 0,
                        "limit": 1,
                    },
                ),
                _tool_call(
                    "get_progression_context",
                    "call-3",
                    {"topics": ["mine"], "since_day_index": 0, "limit": 1},
                ),
            ),
            _final(
                "passthrough",
                text=None,
                reason_code="NO_VALUABLE_ENHANCEMENT",
            ),
        ]
    )
    storage = _AgentStorage()
    _factory, runner = _runner(model)

    result = await runner.run(prompt=_prompt(DIALOGUE_TOOL_NAMES), runtime=_runtime(storage))

    assert len(storage.calls) == 2
    assert [trace.outcome for trace in result.tool_traces] == [
        "succeeded",
        "succeeded",
        "blocked",
    ]
    assert result.tool_traces[-1].error_code == "PARALLEL_TOOL_CALL_LIMIT_EXCEEDED"


@pytest.mark.asyncio
async def test_duplicate_parallel_tool_call_ids_block_entire_round_before_storage() -> None:
    """重复 call ID 不能让三个调用都冒充本轮第一项绕过并行硬限制。"""

    duplicate_id = "duplicate-call-id"
    model = _ScriptedChatModel(
        [
            _tool_calls(
                _tool_call(
                    "search_memories",
                    duplicate_id,
                    {"terms": ["gift"], "limit": 1},
                ),
                _tool_call(
                    "get_event_history",
                    duplicate_id,
                    {
                        "topics": ["gift"],
                        "event_types": ["gift_given"],
                        "since_day_index": 0,
                        "limit": 1,
                    },
                ),
                _tool_call(
                    "get_progression_context",
                    duplicate_id,
                    {"topics": ["mine"], "since_day_index": 0, "limit": 1},
                ),
            ),
            _final(
                "passthrough",
                text=None,
                reason_code="NO_VALUABLE_ENHANCEMENT",
            ),
        ]
    )
    storage = _AgentStorage()
    _factory, runner = _runner(model)

    with pytest.raises(DialogueAgentExecutionError) as error_info:
        await runner.run(prompt=_prompt(DIALOGUE_TOOL_NAMES), runtime=_runtime(storage))

    assert error_info.value.reason_code == "AGENT_TRACE_INVALID"
    assert storage.calls == []


@pytest.mark.asyncio
async def test_model_call_limit_raises_before_fifth_logical_call() -> None:
    """逻辑模型预算由官方 middleware 硬阻断，不依赖模型自觉停止。"""

    repeated_call = lambda index: _tool_calls(  # noqa: E731 - 测试脚本可读性优先
        _tool_call(
            "search_memories",
            f"call-{index}",
            {"terms": [f"gift-{index}"], "limit": 1},
        )
    )
    model = _ScriptedChatModel(
        [
            repeated_call(1),
            repeated_call(2),
            repeated_call(3),
            repeated_call(4),
            _final(
                "passthrough",
                text=None,
                reason_code="SHOULD_NEVER_RUN",
            ),
        ]
    )
    settings = DialogueAgentSettings(
        max_tool_rounds=10,
        max_tool_calls=10,
        max_parallel_tool_calls=2,
        max_logical_model_calls=4,
    )
    storage = _AgentStorage()
    _factory, runner = _runner(model, settings=settings)
    allowed = frozenset({"search_memories"})

    with pytest.raises(DialogueAgentExecutionError, match="execution failed") as error_info:
        await runner.run(prompt=_prompt(allowed), runtime=_runtime(storage, allowed_tools=allowed))

    assert error_info.value.reason_code == "AGENT_MODEL_CALL_LIMIT_EXCEEDED"
    assert model.physical_call_count == 4
    assert len(storage.calls) == 4


@pytest.mark.asyncio
async def test_transient_model_failure_retries_once_inside_same_logical_call() -> None:
    """官方 ModelRetry 允许一次物理重试，但逻辑模型调用仍只有一轮。"""

    model = _ScriptedChatModel(
        [
            DialogueAgentTransientModelError("provider 503 /secret/key"),
            _final(
                "passthrough",
                text=None,
                reason_code="NO_VALUABLE_ENHANCEMENT",
            ),
        ]
    )
    storage = _AgentStorage()
    _factory, runner = _runner(model)

    result = await runner.run(
        prompt=_prompt(frozenset()),
        runtime=_runtime(storage, allowed_tools=frozenset()),
    )

    assert model.physical_call_count == 2
    assert result.logical_model_calls == 1
    assert result.decision.decision == "passthrough"


@pytest.mark.asyncio
@pytest.mark.parametrize(
    "provider_error",
    [
        APITimeoutError(_openai_request()),
        APIConnectionError(request=_openai_request()),
    ],
)
async def test_openai_transport_failure_retries_once(provider_error: Exception) -> None:
    """包装后的 OpenAI timeout/connection 必须进入现有的一次模型重试。

    真实 LangChain/OpenAI 调用可能把 transport 异常包在 cause/context 中。测试故意构造
    反向 context 引用，既冻结包装异常分类，也要求遍历实现不会因异常循环而卡死。
    """

    wrapped = RuntimeError("wrapper must not escape")
    wrapped.__cause__ = provider_error
    provider_error.__context__ = wrapped
    model = _ScriptedChatModel(
        [
            wrapped,
            _final(
                "passthrough",
                text=None,
                reason_code="NO_VALUABLE_ENHANCEMENT",
            ),
        ]
    )
    storage = _AgentStorage()
    _factory, runner = _runner(model)

    result = await runner.run(
        prompt=_prompt(frozenset()),
        runtime=_runtime(storage, allowed_tools=frozenset()),
    )

    assert model.physical_call_count == 2
    assert result.decision.decision == "passthrough"


@pytest.mark.asyncio
async def test_openai_transport_retry_exhaustion_uses_stable_sanitized_reason() -> None:
    """两次 transport 失败后只暴露稳定 reason，不泄露 Provider 原始正文。"""

    secret = "provider-secret-response-body"
    model = _ScriptedChatModel(
        [
            APIConnectionError(message=secret, request=_openai_request()),
            APITimeoutError(_openai_request()),
        ]
    )
    storage = _AgentStorage()
    _factory, runner = _runner(model)

    with pytest.raises(DialogueAgentExecutionError) as error_info:
        await runner.run(
            prompt=_prompt(frozenset()),
            runtime=_runtime(storage, allowed_tools=frozenset()),
        )

    assert model.physical_call_count == 2
    assert error_info.value.reason_code == "AGENT_PROVIDER_TRANSIENT_FAILURE"
    assert secret not in str(error_info.value)


@pytest.mark.asyncio
async def test_transient_tool_failure_retries_once_then_returns_real_artifact() -> None:
    """只有 StorageUnavailableError 使用官方 ToolRetry；成功后轨迹仍是一条调用。"""

    model = _ScriptedChatModel(
        [
            _tool_calls(
                _tool_call(
                    "search_memories",
                    "call-search",
                    {"terms": ["gift"], "limit": 1},
                )
            ),
            _final(
                "rewrite",
                text="我还记得那份礼物。",
                evidence_ids=["memory:gift"],
                reason_code="RELEVANT_SHARED_MEMORY",
            ),
        ]
    )
    storage = _AgentStorage(failures=[StorageUnavailableError()])
    _factory, runner = _runner(model)
    allowed = frozenset({"search_memories"})

    result = await runner.run(
        prompt=_prompt(allowed), runtime=_runtime(storage, allowed_tools=allowed)
    )

    assert len(storage.calls) == 2
    assert len(result.tool_traces) == 1
    assert result.tool_traces[0].outcome == "succeeded"
    assert result.tool_traces[0].evidence_ids == ("memory:gift",)


@pytest.mark.asyncio
async def test_invalid_tool_schema_gets_fixed_message_then_model_can_correct() -> None:
    """静态 Schema 错误只回固定文本，修正调用仍受原工具预算计数。"""

    invalid_terms = ["one", "two", "three", "four", "five", "secret-six"]
    model = _ScriptedChatModel(
        [
            _tool_calls(
                _tool_call(
                    "search_memories",
                    "invalid-schema-call",
                    {"terms": invalid_terms, "limit": 1},
                )
            ),
            _tool_calls(
                _tool_call(
                    "search_memories",
                    "corrected-call",
                    {"terms": ["gift"], "limit": 1},
                )
            ),
            _final(
                "passthrough",
                text=None,
                reason_code="SOURCE_ALREADY_STRONG",
            ),
        ]
    )
    storage = _AgentStorage()
    _factory, runner = _runner(model)
    allowed = frozenset({"search_memories"})

    result = await runner.run(
        prompt=_prompt(allowed),
        runtime=_runtime(storage, allowed_tools=allowed),
    )

    validation_messages = [
        message
        for message in model.messages_per_call[1]
        if isinstance(message, ToolMessage) and message.tool_call_id == "invalid-schema-call"
    ]
    assert [message.content for message in validation_messages] == [
        _EXPECTED_TOOL_SCHEMA_VALIDATION_MESSAGE
    ]
    assert "secret-six" not in _EXPECTED_TOOL_SCHEMA_VALIDATION_MESSAGE
    assert len(storage.calls) == 1
    assert [trace.error_code for trace in result.tool_traces] == [
        "TOOL_INPUT_SCHEMA_INVALID",
        None,
    ]
    assert result.decision.decision == "passthrough"


@pytest.mark.asyncio
async def test_dynamic_tool_input_error_is_not_reexecuted(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """重复 term 由执行期拒绝，但 ToolRetry 不能重复同一个非法执行。"""

    execution_attempts = 0
    original_execute = dialogue_tools_module.execute_search_memories

    async def counting_execute(**kwargs: Any) -> tuple[str, tuple[EvidenceRecord, ...]]:
        """记录真实工具函数进入次数，再委托生产实现执行完整边界。"""

        nonlocal execution_attempts
        execution_attempts += 1
        return await original_execute(**kwargs)

    monkeypatch.setattr(
        dialogue_tools_module,
        "execute_search_memories",
        counting_execute,
    )
    model = _ScriptedChatModel(
        [
            _tool_calls(
                _tool_call(
                    "search_memories",
                    "dynamic-invalid-call",
                    {"terms": ["gift", "GIFT"], "limit": 1},
                )
            ),
            _final(
                "passthrough",
                text=None,
                reason_code="NO_VALUABLE_ENHANCEMENT",
            ),
        ]
    )
    storage = _AgentStorage()
    _factory, runner = _runner(model)
    allowed = frozenset({"search_memories"})

    result = await runner.run(
        prompt=_prompt(allowed),
        runtime=_runtime(storage, allowed_tools=allowed),
    )

    assert execution_attempts == 1
    assert storage.calls == []
    dynamic_messages = [
        message
        for message in model.messages_per_call[1]
        if isinstance(message, ToolMessage) and message.tool_call_id == "dynamic-invalid-call"
    ]
    assert [message.content for message in dynamic_messages] == [
        _EXPECTED_TOOL_DYNAMIC_INPUT_MESSAGE
    ]
    assert result.tool_traces[0].error_code == "TOOL_INPUT_DYNAMIC_INVALID"


@pytest.mark.asyncio
async def test_invalid_structured_output_gets_fixed_message_then_recovers() -> None:
    """跨字段终态错误由官方 ToolStrategy 进入下一逻辑模型调用。"""

    model = _ScriptedChatModel(
        [
            _final(
                "passthrough",
                text="passthrough must not carry text",
                reason_code="INVALID_FIRST_ATTEMPT",
                call_id="invalid-decision",
            ),
            _final(
                "passthrough",
                text=None,
                reason_code="SOURCE_ALREADY_STRONG",
                call_id="valid-decision",
            ),
        ]
    )
    storage = _AgentStorage()
    _factory, runner = _runner(model)

    result = await runner.run(
        prompt=_prompt(frozenset()),
        runtime=_runtime(storage, allowed_tools=frozenset()),
    )

    retry_messages = [
        message
        for message in model.messages_per_call[1]
        if isinstance(message, ToolMessage) and message.tool_call_id == "invalid-decision"
    ]
    assert [message.content for message in retry_messages] == [
        _EXPECTED_STRUCTURED_OUTPUT_VALIDATION_MESSAGE
    ]
    assert result.logical_model_calls == 2
    assert result.decision.reason_code == "SOURCE_ALREADY_STRONG"


@pytest.mark.asyncio
async def test_repeated_invalid_structured_output_stops_at_model_call_limit() -> None:
    """纠错沿用四次逻辑模型调用上限，不能形成无自然终点的循环。"""

    model = _ScriptedChatModel(
        [
            _final(
                "passthrough",
                text="invalid",
                reason_code="INVALID_ATTEMPT",
                call_id=f"invalid-decision-{index}",
            )
            for index in range(4)
        ]
    )
    storage = _AgentStorage()
    _factory, runner = _runner(model)

    with pytest.raises(DialogueAgentExecutionError) as error_info:
        await runner.run(
            prompt=_prompt(frozenset()),
            runtime=_runtime(storage, allowed_tools=frozenset()),
        )

    assert model.physical_call_count == 4
    assert error_info.value.reason_code == "AGENT_MODEL_CALL_LIMIT_EXCEEDED"


@pytest.mark.asyncio
async def test_permanent_model_or_tool_failure_is_sanitized_and_retried_at_most_once() -> None:
    """异常正文、路径、SQL 和 Prompt 不能成为 decision 或对外错误消息。"""

    model_error = _ScriptedChatModel([RuntimeError("/secret/key prompt=SYSTEM")])
    storage = _AgentStorage()
    _factory, runner = _runner(model_error)
    with pytest.raises(DialogueAgentExecutionError) as model_error_info:
        await runner.run(
            prompt=_prompt(frozenset()),
            runtime=_runtime(storage, allowed_tools=frozenset()),
        )
    assert model_error.physical_call_count == 1
    assert str(model_error_info.value) == "dialogue agent execution failed"

    tool_model = _ScriptedChatModel(
        [
            _tool_calls(
                _tool_call(
                    "search_memories",
                    "call-search",
                    {"terms": ["gift"], "limit": 1},
                )
            )
        ]
    )
    failing_storage = _AgentStorage(
        failures=[_SecretStorageUnavailableError(), _SecretStorageUnavailableError()]
    )
    _factory, runner = _runner(tool_model)
    allowed = frozenset({"search_memories"})
    with pytest.raises(DialogueAgentExecutionError) as tool_error_info:
        await runner.run(
            prompt=_prompt(allowed),
            runtime=_runtime(failing_storage, allowed_tools=allowed),
        )
    assert len(failing_storage.calls) == 2
    assert str(tool_error_info.value) == "dialogue agent execution failed"
    assert "/secret" not in str(tool_error_info.value)
    assert "SELECT" not in str(tool_error_info.value)


@pytest.mark.asyncio
async def test_unknown_tool_exception_remains_fail_closed_and_sanitized() -> None:
    """未知工具异常不能被伪装成模型可恢复输入错误。"""

    secret = "unknown tool failure /secret/path SELECT private_prompt"
    model = _ScriptedChatModel(
        [
            _tool_calls(
                _tool_call(
                    "search_memories",
                    "unknown-failure-call",
                    {"terms": ["gift"], "limit": 1},
                )
            )
        ]
    )
    storage = _AgentStorage(failures=[RuntimeError(secret)])
    _factory, runner = _runner(model)
    allowed = frozenset({"search_memories"})

    with pytest.raises(DialogueAgentExecutionError) as error_info:
        await runner.run(
            prompt=_prompt(allowed),
            runtime=_runtime(storage, allowed_tools=allowed),
        )

    assert len(storage.calls) == 1
    assert model.physical_call_count == 1
    assert error_info.value.reason_code == "AGENT_EXECUTION_FAILED"
    assert secret not in str(error_info.value)


def test_factory_caches_compiled_agents_by_sorted_allowlist() -> None:
    """集合顺序不同必须命中同一 graph；不同权限集合不能共享 graph。"""

    model = _ScriptedChatModel([])
    factory, _runner_instance = _runner(model)

    first = factory.get_compiled_agent(frozenset({"get_event_history", "search_memories"}))
    second = factory.get_compiled_agent(frozenset({"search_memories", "get_event_history"}))
    third = factory.get_compiled_agent(frozenset({"search_memories"}))

    assert first is second
    assert first is not third
    assert factory.compiled_agent_count == 2
    with pytest.raises(ValueError, match="allowlist"):
        factory.get_compiled_agent(frozenset({"write_memory"}))


@pytest.mark.asyncio
async def test_hidden_tool_is_not_bound_or_executed_even_if_model_hallucinates_call() -> None:
    """第一层 allowlist 让隐藏工具不可见；ToolNode 仍应 fail closed 处理伪造调用。"""

    model = _ScriptedChatModel(
        [
            _tool_calls(
                _tool_call(
                    "get_event_history",
                    "call-hidden",
                    {
                        "topics": ["gift"],
                        "event_types": ["gift_given"],
                        "since_day_index": 0,
                        "limit": 1,
                    },
                )
            ),
            _final(
                "passthrough",
                text=None,
                reason_code="NO_VALUABLE_ENHANCEMENT",
            ),
        ]
    )
    storage = _AgentStorage()
    _factory, runner = _runner(model)
    allowed = frozenset({"search_memories"})

    result = await runner.run(
        prompt=_prompt(allowed), runtime=_runtime(storage, allowed_tools=allowed)
    )

    assert "get_event_history" not in model.bindings_per_call[0]
    assert storage.calls == []
    assert result.tool_traces[0].tool_name == "get_event_history"
    assert result.tool_traces[0].outcome == "blocked"
    assert result.tool_traces[0].error_code == "TOOL_NOT_REGISTERED"


@pytest.mark.asyncio
async def test_visible_tool_still_requires_runtime_authorization_and_valid_cutoff() -> None:
    """即使 graph 已注册工具，第二层 context 授权和昨日语义仍必须生效。"""

    call = _tool_calls(
        _tool_call(
            "search_memories",
            "call-search",
            {"terms": ["gift"], "limit": 1},
        )
    )
    allowed = frozenset({"search_memories"})

    denied_model = _ScriptedChatModel([call])
    denied_storage = _AgentStorage()
    factory, _runner_instance = _runner(denied_model)
    compiled = factory.get_compiled_agent(allowed)
    with pytest.raises(DialogueToolAuthorizationError):
        await compiled.ainvoke(
            {"messages": list(_prompt(allowed).messages)},  # type: ignore[attr-defined]
            context=_runtime(denied_storage, allowed_tools=frozenset()),
        )
    assert denied_storage.calls == []

    future_model = _ScriptedChatModel([call])
    future_storage = _AgentStorage()
    future_factory, _runner_instance = _runner(future_model)
    future_compiled = future_factory.get_compiled_agent(allowed)
    with pytest.raises(DialogueToolAuthorizationError):
        await future_compiled.ainvoke(
            {"messages": list(_prompt(allowed).messages)},  # type: ignore[attr-defined]
            context=_runtime(
                future_storage,
                allowed_tools=allowed,
                cutoff_day_index=10,
            ),
        )
    assert future_storage.calls == []


@pytest.mark.asyncio
async def test_agent_aggregates_reported_token_usage_from_real_ai_messages() -> None:
    """Agent usage 只能从真实 AIMessage metadata 聚合，不能由模型最终字段自报。"""

    model = _ScriptedChatModel(
        [
            _tool_calls(
                _tool_call(
                    "search_memories",
                    "call-usage-search",
                    {"terms": ["gift"], "limit": 1},
                ),
                usage_metadata=_usage(7, 2),
            ),
            _final(
                "rewrite",
                text="这场雨让我想起那份礼物。",
                evidence_ids=["memory:gift"],
                reason_code="RELEVANT_SHARED_MEMORY",
                usage_metadata=_usage(11, 4),
            ),
        ]
    )
    storage = _AgentStorage()
    _factory, runner = _runner(model)
    allowed = frozenset({"search_memories"})

    result = await runner.run(
        prompt=_prompt(allowed),
        runtime=_runtime(storage, allowed_tools=allowed),
    )

    assert result.usage == DialogueModelUsage(
        input_tokens=18,
        output_tokens=6,
        total_tokens=24,
        reported_calls=2,
    )


@pytest.mark.asyncio
async def test_tool_trace_whitelists_public_intent_arguments_before_audit() -> None:
    """模型附加的分区、Prompt 或任意 nested 参数不能进入内存 trace/未来数据库。"""

    secret = "/secret/save.sqlite SYSTEM_PROMPT"
    model = _ScriptedChatModel(
        [
            _tool_calls(
                _tool_call(
                    "search_memories",
                    "call-sanitized-trace",
                    {
                        "terms": ["gift"],
                        "limit": 1,
                        "save_id": secret,
                        "nested": {"prompt": secret},
                    },
                )
            ),
            _final(
                "passthrough",
                text=None,
                reason_code="NO_VALUABLE_ENHANCEMENT",
            ),
        ]
    )
    storage = _AgentStorage()
    _factory, runner = _runner(model)
    allowed = frozenset({"search_memories"})

    result = await runner.run(
        prompt=_prompt(allowed),
        runtime=_runtime(storage, allowed_tools=allowed),
    )

    assert result.tool_traces[0].arguments == {"terms": ["gift"], "limit": 1}
    assert secret not in str(result.tool_traces)


@pytest.mark.asyncio
async def test_invalid_control_character_terms_are_omitted_from_trace() -> None:
    """被阻止的控制字符参数也不能污染内存或持久化审计。"""

    secret = "gift" + chr(10) + "SYSTEM_PROMPT"
    model = _ScriptedChatModel(
        [
            _tool_calls(
                _tool_call(
                    "search_memories",
                    "control-character-call",
                    {"terms": [secret], "limit": 1},
                )
            ),
            _final(
                "passthrough",
                text=None,
                reason_code="NO_VALUABLE_ENHANCEMENT",
            ),
        ]
    )
    storage = _AgentStorage()
    _factory, runner = _runner(model)
    allowed = frozenset({"search_memories"})

    result = await runner.run(
        prompt=_prompt(allowed),
        runtime=_runtime(storage, allowed_tools=allowed),
    )

    assert result.tool_traces[0].arguments == {"limit": 1}
    assert "SYSTEM_PROMPT" not in str(result.tool_traces)
