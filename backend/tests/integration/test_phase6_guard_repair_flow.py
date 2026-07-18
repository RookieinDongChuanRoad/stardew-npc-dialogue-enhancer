"""Phase 6 Agent→Guard→一次 Repair→SQLite 审计的完整行为切片。"""

from __future__ import annotations

from collections.abc import Sequence
from typing import Any, cast

import pytest
from langchain_core.callbacks import (
    AsyncCallbackManagerForLLMRun,
    CallbackManagerForLLMRun,
)
from langchain_core.language_models import BaseChatModel
from langchain_core.messages import AIMessage, BaseMessage, UsageMetadata
from langchain_core.outputs import ChatGeneration, ChatResult
from langchain_core.runnables import Runnable
from pydantic import PrivateAttr
from sqlalchemy import select
from typing_extensions import override

from stardew_npc_agent.dialogue_agent import (
    AgentBackedDialogueGenerator,
    DialogueAgentFactory,
    DialogueAgentRunner,
)
from stardew_npc_agent.dialogue_service import DialogueService
from stardew_npc_agent.event_service import EventService
from stardew_npc_agent.schemas import (
    DialogueGenerationBatchRequest,
    GameEventBatchRequest,
)
from stardew_npc_agent.storage import (
    DialogueGenerationRecord,
    MemorySearchQuery,
    SqliteStorage,
)


class _PipelineScriptedModel(BaseChatModel):
    """按顺序服务 Agent 与 Repair structured calls 的零网络模型。"""

    _steps: list[AIMessage | Exception] = PrivateAttr()
    _physical_calls: int = PrivateAttr(default=0)
    _bound_names: tuple[str, ...] = PrivateAttr(default=())
    _bindings_per_call: list[tuple[str, ...]] = PrivateAttr(default_factory=list)

    def __init__(self, steps: Sequence[AIMessage | Exception]) -> None:
        """复制脚本，确保协议重试若误调用模型会立刻耗尽并失败。"""

        super().__init__()
        self._steps = list(steps)

    @property
    def physical_calls(self) -> int:
        """返回 Agent 与 Repair 合计物理模型调用数。"""

        return self._physical_calls

    @property
    def bindings_per_call(self) -> tuple[tuple[str, ...], ...]:
        """返回每次调用前 fake model 观察到的绑定名称。"""

        return tuple(self._bindings_per_call)

    @property
    @override
    def _llm_type(self) -> str:
        return "stardew-phase6-pipeline-model"

    @override
    def bind_tools(
        self,
        tools: Sequence[dict[str, Any] | type | Any],
        *,
        tool_choice: str | None = None,
        **kwargs: Any,
    ) -> Runnable[Any, BaseMessage]:
        """记录 schema/领域工具绑定；不执行任何真实 Provider 逻辑。"""

        del tool_choice, kwargs
        self._bound_names = tuple(_bound_name(value) for value in tools)
        return self

    @override
    def _generate(
        self,
        messages: list[BaseMessage],
        stop: list[str] | None = None,
        run_manager: CallbackManagerForLLMRun | None = None,
        **kwargs: Any,
    ) -> ChatResult:
        """满足 BaseChatModel 同步合同。"""

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
        """消费一个 Agent 或 Repair scripted response。"""

        del messages, stop, run_manager, kwargs
        return self._next_result()

    def _next_result(self) -> ChatResult:
        """记录调用绑定并返回下一步，异常用于验证稳定失败。"""

        self._physical_calls += 1
        self._bindings_per_call.append(self._bound_names)
        if not self._steps:
            raise AssertionError("phase6 pipeline model steps exhausted")
        step = self._steps.pop(0)
        if isinstance(step, Exception):
            raise step
        return ChatResult(generations=[ChatGeneration(message=step)])


def _bound_name(value: object) -> str:
    """兼容 Pydantic class、BaseTool 和 OpenAI-style dict 的名称提取。"""

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


def _usage(input_tokens: int, output_tokens: int) -> UsageMetadata:
    """构造可被 Agent/Repair 审计聚合的标准 Token metadata。"""

    return UsageMetadata(
        input_tokens=input_tokens,
        output_tokens=output_tokens,
        total_tokens=input_tokens + output_tokens,
    )


def _tool_call(
    name: str,
    call_id: str,
    args: dict[str, object],
    *,
    usage: UsageMetadata | None = None,
) -> AIMessage:
    """构造一个 LangChain 标准 tool/structured call。"""

    return AIMessage(
        content="",
        tool_calls=[{"name": name, "id": call_id, "args": args, "type": "tool_call"}],
        usage_metadata=usage,
    )


def _agent_final(
    *,
    text: str | None,
    evidence_ids: list[str],
    address_slot: str = "none",
    suffix: str = "",
    decision: str = "rewrite",
    reason_code: str = "SOURCE_STYLE_REWRITE",
    usage: UsageMetadata | None = None,
) -> AIMessage:
    """构造 Agent ToolStrategy 的 DialogueAgentDecision。"""

    return _tool_call(
        "DialogueAgentDecision",
        "agent-decision-call",
        {
            "decision": decision,
            "template": (
                None
                if text is None
                else {
                    "prefix": text,
                    "address_slot": address_slot,
                    "suffix": suffix,
                }
            ),
            "evidence_ids": evidence_ids,
            "reason_code": reason_code,
        },
        usage=usage,
    )


def _repair_final(
    *,
    text: str,
    evidence_ids: list[str],
    address_slot: str = "none",
    suffix: str = "",
    usage: UsageMetadata | None = None,
) -> AIMessage:
    """构造普通 structured Repair 的 DialogueRepairDecision。"""

    return _tool_call(
        "DialogueRepairDecision",
        "repair-decision-call",
        {
            "template": {
                "prefix": text,
                "address_slot": address_slot,
                "suffix": suffix,
            },
            "evidence_ids": evidence_ids,
        },
        usage=usage,
    )


def _request(
    *,
    required_memory_revision: int = 0,
    request_id: str = "request-phase6",
    task_id: str = "task-phase6",
    source_text: str = "雨天待在屋里也不算太糟。",
) -> DialogueGenerationBatchRequest:
    """构造协议重试只改变 request/task ID 的稳定 Abigail 任务。"""

    return DialogueGenerationBatchRequest.model_validate(
        {
            "schema_version": "1.0",
            "request_id": request_id,
            "save_id": "save-phase6",
            "player_id": "player-phase6",
            "game_day_index": 10,
            "required_memory_revision": required_memory_revision,
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
                        "text": source_text,
                        "source_hash": "sha256:phase6-source",
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


def _service(storage: SqliteStorage, model: _PipelineScriptedModel) -> DialogueService:
    """使用同一 storage 装配 Agent-backed generator 与 service。"""

    factory = DialogueAgentFactory(
        model=model,
        model_configuration="phase6-scripted-model-v1",
    )
    generator = AgentBackedDialogueGenerator(
        runner=DialogueAgentRunner(factory),
        storage=storage,
        allowed_tools=frozenset({"search_memories"}),
    )
    return DialogueService(storage, generator=generator)


async def _record_for_result(
    storage: SqliteStorage,
    generation_key: str,
) -> DialogueGenerationRecord:
    """从真实 SQLite 读取完整审计列，而非只读 cache snapshot。"""

    async with storage.session_factory() as session:
        record = await session.scalar(
            select(DialogueGenerationRecord).where(
                DialogueGenerationRecord.generation_key == generation_key
            )
        )
    assert record is not None
    return record


@pytest.mark.asyncio
async def test_source_only_rewrite_passes_guard_and_transport_retry_hits_cache(
    storage: SqliteStorage,
) -> None:
    """首次 Guard 通过即可 generated；协议重试不得再次调用 Agent/Repair。"""

    model = _PipelineScriptedModel(
        [
            _agent_final(
                text="这场雨的声音倒挺适合发呆的。",
                evidence_ids=[],
                usage=_usage(10, 4),
            )
        ]
    )
    service = _service(storage, model)

    first = await service.generate_batch(_request())
    retry = await service.generate_batch(
        _request(request_id="request-phase6-retry", task_id="task-phase6-retry")
    )

    result = first.items[0]
    assert result.status == "generated"
    assert result.text == "这场雨的声音倒挺适合发呆的。"
    assert result.evidence_ids == []
    assert retry.items[0].generation_key == result.generation_key
    assert retry.items[0].task_id == "task-phase6-retry"
    assert model.physical_calls == 1

    record = await _record_for_result(storage, result.generation_key)
    assert record.guard_passed is True
    assert record.evidence_authorized is True
    assert record.trace_json is not None
    assert record.trace_json["agent"]["decision"]["template"] == {
        "address_slot": "none",
        "prefix": result.text,
        "suffix": "",
    }
    assert record.trace_json["repair"] is None
    assert record.usage_json is not None
    assert record.usage_json["agent"]["total_tokens"] == 14
    assert record.usage_json["combined"]["total_tokens"] == 14
    assert record.guard_report_json is not None
    assert record.guard_report_json["final_passed"] is True
    assert len(record.guard_report_json["attempts"]) == 1


@pytest.mark.asyncio
async def test_required_player_name_slot_survives_agent_guard_service_and_sqlite(
    storage: SqliteStorage,
) -> None:
    """原文 ``@`` 必须以 typed 槽走完整链路，并只以游戏模板写入公共结果。"""

    model = _PipelineScriptedModel(
        [
            _agent_final(
                text="",
                address_slot="player_name",
                suffix="，这场雨的声音倒挺适合发呆的。",
                evidence_ids=[],
            )
        ]
    )

    result = (
        await _service(storage, model).generate_batch(
            _request(source_text="@，雨天待在屋里也不算太糟。")
        )
    ).items[0]

    assert result.status == "generated"
    assert result.text == "@，这场雨的声音倒挺适合发呆的。"
    record = await _record_for_result(storage, result.generation_key)
    assert record.result_text == result.text
    assert record.input_versions_json is not None
    assert record.input_versions_json["display_token_policy_version"] == "display-token-policy-v1"
    assert record.trace_json is not None
    assert record.trace_json["agent"]["decision"]["template"] == {
        "address_slot": "player_name",
        "prefix": "",
        "suffix": "，这场雨的声音倒挺适合发呆的。",
    }


async def _ingest_gift_and_get_evidence_id(storage: SqliteStorage) -> str:
    """通过真实事件投影写入一条昨日礼物记忆，并返回稳定 memory ID。"""

    response = await EventService(storage).ingest_batch(
        GameEventBatchRequest.model_validate(
            {
                "schema_version": "1.0",
                "request_id": "event-request-phase6",
                "save_id": "save-phase6",
                "player_id": "player-phase6",
                "events": [
                    {
                        "event_id": "event-phase6-amethyst",
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
    evidence = await storage.search_memories(
        MemorySearchQuery(
            save_id="save-phase6",
            player_id="player-phase6",
            npc_id="Abigail",
            game_day_index=10,
            cutoff_day_index=9,
            friendship_points=750,
            relationship_stage="friend",
            tags=("gift", "item:(O)66"),
            cooldown_days=3,
            limit=1,
        )
    )
    assert len(evidence) == 1
    return evidence[0].evidence_id


@pytest.mark.asyncio
async def test_tool_evidence_rewrite_is_guarded_authorized_and_fully_audited(
    storage: SqliteStorage,
) -> None:
    """observed evidence 必须同时通过 artifact subset、Guard cutoff 与保存时授权。"""

    evidence_id = await _ingest_gift_and_get_evidence_id(storage)
    model = _PipelineScriptedModel(
        [
            _tool_call(
                "search_memories",
                "search-memory-call",
                {"terms": ["gift", "(O)66"], "limit": 1},
                usage=_usage(8, 2),
            ),
            _agent_final(
                text="这种雨声让我想起那块紫水晶。",
                evidence_ids=[evidence_id],
                reason_code="RELEVANT_SHARED_MEMORY",
                usage=_usage(12, 5),
            ),
        ]
    )

    result = (
        await _service(storage, model).generate_batch(_request(required_memory_revision=1))
    ).items[0]

    assert result.status == "generated"
    assert result.evidence_ids == [evidence_id]
    record = await _record_for_result(storage, result.generation_key)
    assert record.evidence_authorized is True
    assert record.trace_json is not None
    assert record.trace_json["trace_version"] == "dialogue-generation-trace-v4"
    assert record.trace_json["agent"]["used_tools"] == ["search_memories"]
    assert record.trace_json["agent"]["tool_calls"][0]["arguments"] == {
        "terms": ["gift", "(O)66"],
        "limit": 1,
    }
    assert record.trace_json["agent"]["tool_calls"][0]["evidence_ids"] == [evidence_id]
    assert record.usage_json["combined"]["total_tokens"] == 27
    assert record.guard_report_json["attempts"][0]["checked_evidence_ids"] == [evidence_id]


@pytest.mark.asyncio
async def test_tool_observation_without_evidence_claim_fails_before_repair(
    storage: SqliteStorage,
) -> None:
    """真实工具结果不能靠省略 ID 绕过 generation 授权、ACK 消费与冷却。"""

    await _ingest_gift_and_get_evidence_id(storage)
    model = _PipelineScriptedModel(
        [
            _tool_call(
                "search_memories",
                "search-without-claim",
                {"terms": ["gift", "(O)66"], "limit": 1},
            ),
            _agent_final(
                text="这种雨声让我想起那块紫水晶。",
                evidence_ids=[],
                reason_code="RELEVANT_SHARED_MEMORY",
            ),
        ]
    )

    result = (
        await _service(storage, model).generate_batch(_request(required_memory_revision=1))
    ).items[0]

    assert result.status == "failed"
    assert result.text is None
    assert result.evidence_ids == []
    assert result.reason_code == "GUARD_REJECTED"
    assert model.physical_calls == 2
    record = await _record_for_result(storage, result.generation_key)
    assert record.evidence_authorized is False
    assert record.guard_report_json["repair_attempted"] is False
    assert record.guard_report_json["attempts"][0]["violations"] == [
        {"code": "EVIDENCE_REQUIRED_AFTER_OBSERVATION", "repairable": False}
    ]


@pytest.mark.asyncio
async def test_one_repair_can_fix_text_then_second_guard_generates(
    storage: SqliteStorage,
) -> None:
    """可修复 Markdown 只触发一次普通模型调用，合法结果通过第二次 Guard。"""

    model = _PipelineScriptedModel(
        [
            _agent_final(
                text="雨天 **待在屋里** 也不坏。",
                evidence_ids=[],
                usage=_usage(9, 3),
            ),
            _repair_final(
                text="雨天待在屋里也不坏。",
                evidence_ids=[],
                usage=_usage(7, 2),
            ),
        ]
    )

    result = (await _service(storage, model).generate_batch(_request())).items[0]

    assert result.status == "generated"
    assert result.text == "雨天待在屋里也不坏。"
    assert result.reason_code == "REPAIRED_REWRITE"
    assert model.physical_calls == 2
    record = await _record_for_result(storage, result.generation_key)
    assert record.trace_json["repair"]["decision"]["template"] == {
        "address_slot": "none",
        "prefix": result.text,
        "suffix": "",
    }
    assert record.usage_json["repair"]["total_tokens"] == 9
    assert record.usage_json["combined"]["total_tokens"] == 21
    assert record.guard_report_json["repair_attempted"] is True
    assert record.guard_report_json["final_passed"] is True
    assert [attempt["passed"] for attempt in record.guard_report_json["attempts"]] == [False, True]


@pytest.mark.asyncio
async def test_second_guard_failure_returns_failed_without_third_model_call(
    storage: SqliteStorage,
) -> None:
    """Repair 后仍换题时直接 failed；候选只留审计，不进入 API 可展示字段。"""

    original_candidate = "雨天 **待在屋里** 也不坏。"
    repaired_candidate = "那块紫水晶真的很漂亮。"
    model = _PipelineScriptedModel(
        [
            _agent_final(text=original_candidate, evidence_ids=[]),
            _repair_final(text=repaired_candidate, evidence_ids=[]),
        ]
    )

    result = (await _service(storage, model).generate_batch(_request())).items[0]

    assert result.status == "failed"
    assert result.text is None
    assert result.evidence_ids == []
    assert result.reason_code == "GUARD_REJECTED_AFTER_REPAIR"
    assert model.physical_calls == 2
    record = await _record_for_result(storage, result.generation_key)
    assert record.result_text is None
    assert record.guard_passed is False
    assert record.trace_json["agent"]["decision"]["template"]["prefix"] == original_candidate
    assert record.trace_json["repair"]["decision"]["template"]["prefix"] == repaired_candidate
    assert record.guard_report_json["final_passed"] is False
    assert record.guard_report_json["attempts"][1]["violations"] == [
        {"code": "SOURCE_TOPIC_UNANCHORED", "repairable": True}
    ]


@pytest.mark.asyncio
async def test_repair_evidence_escalation_fails_and_preserves_only_audit_candidate(
    storage: SqliteStorage,
) -> None:
    """Repair 新增 evidence 在第二次 Guard 前即被拒绝，且最终证据保持空。"""

    model = _PipelineScriptedModel(
        [
            _agent_final(text="雨天 **待在屋里** 也不坏。", evidence_ids=[]),
            _repair_final(
                text="雨天待在屋里，也想起一个新秘密。",
                evidence_ids=["memory:new-secret"],
                usage=_usage(5, 2),
            ),
        ]
    )

    result = (await _service(storage, model).generate_batch(_request())).items[0]

    assert result.status == "failed"
    assert result.text is None
    assert result.evidence_ids == []
    assert result.reason_code == "REPAIR_EVIDENCE_ESCALATION"
    assert model.physical_calls == 2
    record = await _record_for_result(storage, result.generation_key)
    assert record.evidence_ids_json == []
    assert record.trace_json["repair"] == {
        "attempted": True,
        "error_code": "REPAIR_EVIDENCE_ESCALATION",
    }
    assert record.usage_json["repair"]["total_tokens"] == 7
    assert record.usage_json["combined"]["total_tokens"] == 7
    assert len(record.guard_report_json["attempts"]) == 1


@pytest.mark.asyncio
async def test_agent_generator_and_service_must_share_same_storage_instance(
    storage: SqliteStorage,
    migrated_database_url: str,
) -> None:
    """即使指向同一文件，不同 storage 组合根也必须在任何调用前拒绝。"""

    other_storage = SqliteStorage.from_url(migrated_database_url, busy_timeout_ms=5_000)
    try:
        model = _PipelineScriptedModel([])
        factory = DialogueAgentFactory(
            model=model,
            model_configuration="phase6-storage-identity-v1",
        )
        generator = AgentBackedDialogueGenerator(
            runner=DialogueAgentRunner(factory),
            storage=storage,
            allowed_tools=frozenset(),
        )

        with pytest.raises(ValueError, match="同一个 storage"):
            DialogueService(other_storage, generator=generator)

        assert model.physical_calls == 0
    finally:
        await other_storage.dispose()
