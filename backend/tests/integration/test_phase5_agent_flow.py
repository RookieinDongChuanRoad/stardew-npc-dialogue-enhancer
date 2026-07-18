"""Phase 5 事件→工具→Agent 与 passthrough→service cache 的跨组件验收。"""

from __future__ import annotations

import time
from collections.abc import Sequence
from typing import Any

import pytest
from langchain_core.callbacks import (
    AsyncCallbackManagerForLLMRun,
    CallbackManagerForLLMRun,
)
from langchain_core.language_models import BaseChatModel
from langchain_core.messages import AIMessage, BaseMessage
from langchain_core.outputs import ChatGeneration, ChatResult
from langchain_core.runnables import Runnable
from pydantic import PrivateAttr
from typing_extensions import override

from stardew_npc_agent.dialogue_agent import (
    AgentBackedDialogueGenerator,
    DialogueAgentFactory,
    DialogueAgentRunner,
    DialoguePromptBuilder,
    DialogueRuntimeContext,
)
from stardew_npc_agent.dialogue_service import DialogueService
from stardew_npc_agent.event_service import EventService
from stardew_npc_agent.profiles import get_npc_agent_profile
from stardew_npc_agent.schemas import (
    DialogueGenerationBatchRequest,
    GameEventBatchRequest,
)
from stardew_npc_agent.storage import SqliteStorage


class _FlowScriptedModel(BaseChatModel):
    """按顺序返回 tool call/structured decision 的本地零网络模型。"""

    _responses: list[AIMessage] = PrivateAttr()
    _physical_calls: int = PrivateAttr(default=0)

    def __init__(self, responses: Sequence[AIMessage]) -> None:
        super().__init__()
        self._responses = list(responses)

    @property
    def physical_calls(self) -> int:
        return self._physical_calls

    @property
    @override
    def _llm_type(self) -> str:
        return "stardew-phase5-flow-model"

    @override
    def bind_tools(
        self,
        tools: Sequence[dict[str, Any] | type | Any],
        *,
        tool_choice: str | None = None,
        **kwargs: Any,
    ) -> Runnable[Any, BaseMessage]:
        del tools, tool_choice, kwargs
        return self

    @override
    def _generate(
        self,
        messages: list[BaseMessage],
        stop: list[str] | None = None,
        run_manager: CallbackManagerForLLMRun | None = None,
        **kwargs: Any,
    ) -> ChatResult:
        del messages, stop, run_manager, kwargs
        return self._next_result()

    @override
    async def _agenerate(
        self,
        messages: list[BaseMessage],
        stop: list[str] | None = None,
        run_manager: AsyncCallbackManagerForLLMRun | None = None,
        **kwargs: Any,
    ) -> ChatResult:
        del messages, stop, run_manager, kwargs
        return self._next_result()

    def _next_result(self) -> ChatResult:
        self._physical_calls += 1
        if not self._responses:
            raise AssertionError("phase5 flow model responses exhausted")
        return ChatResult(generations=[ChatGeneration(message=self._responses.pop(0))])


def _tool_call(name: str, call_id: str, args: dict[str, object]) -> AIMessage:
    """构造单个领域工具调用。"""

    return AIMessage(
        content="",
        tool_calls=[{"name": name, "id": call_id, "args": args, "type": "tool_call"}],
    )


def _final(
    decision: str,
    *,
    text: str | None,
    evidence_ids: list[str],
    reason_code: str,
) -> AIMessage:
    """构造 ToolStrategy structured response。"""

    return _tool_call(
        "DialogueAgentDecision",
        "decision-call",
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
            "evidence_ids": evidence_ids,
            "reason_code": reason_code,
        },
    )


def _dialogue_request(
    *,
    request_id: str = "request-phase5-flow",
    task_id: str = "task-phase5-flow",
) -> DialogueGenerationBatchRequest:
    """构造依赖 revision=1 的第 10 天 Abigail 生成任务。"""

    return DialogueGenerationBatchRequest.model_validate(
        {
            "schema_version": "1.0",
            "request_id": request_id,
            "save_id": "save-phase5-flow",
            "player_id": "player-phase5-flow",
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
                    "task_id": task_id,
                    "npc_id": "Abigail",
                    "source_dialogue": {
                        "asset_name": "Characters/Dialogue/Abigail",
                        "dialogue_key": "fall_Mon",
                        "text": "雨天待在屋里也不算太糟。",
                        "source_hash": "sha256:phase5-flow-source",
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


async def _ingest_gift_event(storage: SqliteStorage) -> None:
    """通过真实 EventService 写入事实、记忆投影和 revision。"""

    response = await EventService(storage).ingest_batch(
        GameEventBatchRequest.model_validate(
            {
                "schema_version": "1.0",
                "request_id": "event-request-phase5-flow",
                "save_id": "save-phase5-flow",
                "player_id": "player-phase5-flow",
                "events": [
                    {
                        "event_id": "event-gift-amethyst-day-8",
                        "event_type": "gift_given",
                        "event_version": "2",
                        "occurred_day_index": 8,
                        "source": "harmony.farmer.on_gift_given",
                        "audience_scope": "npc",
                        "audience_npc_id": "Abigail",
                        "payload": {"item_id": "(O)66", "taste": "love"},
                    }
                ],
            }
        )
    )
    assert response.memory_revision == 1
    assert response.committed_through_day_index == 8
    assert response.items[0].status == "accepted"


@pytest.mark.asyncio
async def test_real_event_projection_flows_through_agent_tool_trace_and_artifact(
    storage: SqliteStorage,
) -> None:
    """真实 SQLite evidence 必须从工具 artifact 进入 Agent 轨迹，而非模型自报。"""

    await _ingest_gift_event(storage)
    model = _FlowScriptedModel(
        [
            _tool_call(
                "search_memories",
                "search-call",
                {"terms": ["gift", "(O)66"], "limit": 1},
            ),
            _final(
                "rewrite",
                text="这种雨声让我想起那块紫水晶。",
                evidence_ids=["memory:model-claim-is-not-source-of-truth"],
                reason_code="RELEVANT_SHARED_MEMORY",
            ),
        ]
    )
    factory = DialogueAgentFactory(
        model=model,
        model_configuration="phase5-flow-rewrite-model-v1",
    )
    runner = DialogueAgentRunner(factory)
    request = _dialogue_request()
    item = request.items[0]
    profile = get_npc_agent_profile(item.npc_id)
    assert profile is not None
    allowed_tools = frozenset({"search_memories"})
    prompt = DialoguePromptBuilder().build(
        request,
        item,
        profile,
        allowed_tools=allowed_tools,
    )
    runtime = DialogueRuntimeContext(
        task_id=item.task_id,
        save_id=request.save_id,
        player_id=request.player_id,
        npc_id=item.npc_id,
        game_day_index=request.game_day_index,
        cutoff_day_index=request.game_day_index - 1,
        friendship_points=item.relationship_snapshot.friendship_points,
        relationship_stage=item.relationship_snapshot.relationship_stage,
        memory_cooldown_days=profile.memory_cooldown_days,
        allowed_tools=allowed_tools,
        storage=storage,
        deadline_monotonic=time.monotonic() + 10,
    )

    result = await runner.run(prompt=prompt, runtime=runtime)

    assert result.decision.decision == "rewrite"
    assert result.decision.evidence_ids == ("memory:model-claim-is-not-source-of-truth",)
    assert result.used_tools == ("search_memories",)
    assert len(result.tool_traces) == 1
    assert result.tool_traces[0].outcome == "succeeded"
    assert len(result.observed_evidence) == 1
    evidence = result.observed_evidence[0]
    assert evidence.evidence_id.startswith("memory:")
    assert evidence.evidence_id != result.decision.evidence_ids[0]
    assert evidence.source_event_ids == ("event-gift-amethyst-day-8",)
    assert "(O)66" in evidence.summary


@pytest.mark.asyncio
async def test_no_tool_agent_passthrough_flows_through_service_and_generation_cache(
    storage: SqliteStorage,
) -> None:
    """事件 revision 就绪后，Agent passthrough 应持久化且传输重试不再调用模型。"""

    await _ingest_gift_event(storage)
    model = _FlowScriptedModel(
        [
            _final(
                "passthrough",
                text=None,
                evidence_ids=[],
                reason_code="NO_VALUABLE_ENHANCEMENT",
            )
        ]
    )
    factory = DialogueAgentFactory(
        model=model,
        model_configuration="phase5-flow-passthrough-model-v1",
    )
    generator = AgentBackedDialogueGenerator(
        runner=DialogueAgentRunner(factory),
        storage=storage,
        allowed_tools=frozenset(),
    )
    service = DialogueService(storage, generator=generator)

    first = await service.generate_batch(_dialogue_request())
    retry = await service.generate_batch(
        _dialogue_request(request_id="transport-retry", task_id="retry-task")
    )

    assert model.physical_calls == 1
    assert first.items[0].status == retry.items[0].status == "passthrough"
    assert first.items[0].text is retry.items[0].text is None
    assert first.items[0].reason_code == "NO_VALUABLE_ENHANCEMENT"
    assert first.items[0].generation_key == retry.items[0].generation_key
    assert first.items[0].generation_id == retry.items[0].generation_id
    assert retry.items[0].task_id == "retry-task"
