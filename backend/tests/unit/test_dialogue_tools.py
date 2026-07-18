"""三个只读 LangChain 工具的 Schema、授权与 observation 单元测试。"""

from __future__ import annotations

import json
import time
from dataclasses import replace
from typing import Any, cast

import pytest
from langchain.tools import ToolRuntime
from langchain_core.tools import BaseTool
from langchain_core.utils.function_calling import convert_to_openai_tool
from pydantic import ValidationError

from stardew_npc_agent.dialogue_agent import DialogueRuntimeContext
from stardew_npc_agent.dialogue_tools import (
    DIALOGUE_TOOL_NAMES,
    DIALOGUE_TOOLS,
    DialogueToolAuthorizationError,
    DialogueToolDeadlineExceededError,
    DialogueToolInputError,
    build_domain_dialogue_tools,
    execute_get_event_history,
    execute_get_player_progression,
    execute_get_progression_context,
    execute_search_memories,
)
from stardew_npc_agent.memory_capabilities import (
    MEMORY_CAPABILITIES,
    build_target_capability_registry,
)
from stardew_npc_agent.storage import EvidenceRecord, SqliteStorage


class _RecordingStorage:
    """记录收到的可信 query，并返回固定 typed evidence。"""

    def __init__(self) -> None:
        self.calls: list[tuple[str, object]] = []

    async def search_memories(self, query: object) -> list[EvidenceRecord]:
        """记录记忆查询。"""

        self.calls.append(("search_memories", query))
        return [_evidence("memory:gift", "gift_given", "npc:Abigail")]

    async def get_event_history(self, query: object) -> list[EvidenceRecord]:
        """记录事件历史查询。"""

        self.calls.append(("get_event_history", query))
        return [_evidence("memory:event", "gift_given", "npc:Abigail")]

    async def get_progression_context(self, query: object) -> list[EvidenceRecord]:
        """记录世界进度查询。"""

        self.calls.append(("get_progression_context", query))
        return [_evidence("memory:mine", "world_progression", "public")]

    async def get_domain_memory_candidates(self, query: object) -> list[EvidenceRecord]:
        """记录无参数领域工具构造的可信 query，并返回分类完整的 farming 证据。"""

        self.calls.append(("get_domain_memory_candidates", query))
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


def _evidence(evidence_id: str, evidence_type: str, visibility_scope: str) -> EvidenceRecord:
    """构造工具 artifact 使用的最小不可变证据。"""

    return EvidenceRecord(
        evidence_id=evidence_id,
        evidence_type=evidence_type,
        source_event_ids=(f"event:{evidence_id}",),
        summary=f"summary:{evidence_id}",
        occurred_day_index=8,
        tags=("gift", "amethyst"),
        visibility_scope=visibility_scope,
    )


def _runtime(
    storage: _RecordingStorage,
    *,
    allowed_tools: frozenset[str] = DIALOGUE_TOOL_NAMES,
    deadline_monotonic: float | None = None,
    cutoff_day_index: int = 9,
) -> ToolRuntime[DialogueRuntimeContext, dict[str, Any]]:
    """构造与真实 Agent 注入字段相同的 ToolRuntime。"""

    context = DialogueRuntimeContext(
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
        deadline_monotonic=(
            time.monotonic() + 30 if deadline_monotonic is None else deadline_monotonic
        ),
        source_dialogue_text="今年的收获和作物看起来真不错。",
        source_hash="sha256:source",
        locale="zh-CN",
        required_memory_revision=2,
        resolved_memory_revision=8,
        resolved_retrieval_state_revision=5,
    )
    return ToolRuntime(
        state={"messages": []},
        context=context,
        config={},
        stream_writer=lambda _value: None,
        tool_call_id="tool-call-1",
        store=None,
    )


def _provider_parameters(tool_value: BaseTool) -> dict[str, Any]:
    """返回当前 LangChain 实际转换的 OpenAI-style parameters。

    测试必须经过与 ChatOpenAI bind_tools 相同的 converter，而不能只看项目输入模型；
    否则 converter 丢失字段约束时，单测仍可能产生“模型看得见”的假阳性。
    """

    converted = convert_to_openai_tool(tool_value)
    function = cast(dict[str, Any], converted["function"])
    return cast(dict[str, Any], function["parameters"])


def test_exactly_three_read_only_tools_expose_only_model_query_intent() -> None:
    """可信分区和执行依赖不得出现在模型可伪造的 JSON Schema 中。"""

    assert DIALOGUE_TOOL_NAMES == frozenset(
        {"search_memories", "get_event_history", "get_progression_context"}
    )
    assert tuple(tool.name for tool in DIALOGUE_TOOLS) == (
        "search_memories",
        "get_event_history",
        "get_progression_context",
    )
    # ``args_schema`` 是 LangChain 内部执行 Schema，故意保留 runtime 以便注入；
    # 真正绑定给模型的是 ``tool_call_schema``，它必须剔除所有 injected 参数。
    properties_by_tool = {
        tool.name: set(_provider_parameters(tool)["properties"]) for tool in DIALOGUE_TOOLS
    }
    assert properties_by_tool == {
        "search_memories": {"terms", "limit"},
        "get_event_history": {"topics", "event_types", "since_day_index", "limit"},
        "get_progression_context": {"topics", "since_day_index", "limit"},
    }
    forbidden = {
        "save_id",
        "player_id",
        "npc_id",
        "game_day_index",
        "cutoff_day_index",
        "friendship_points",
        "relationship_stage",
        "storage",
        "deadline_monotonic",
        "runtime",
    }
    assert all(not forbidden.intersection(properties) for properties in properties_by_tool.values())


def test_provider_tool_schemas_publish_static_bounds_and_event_enum() -> None:
    """Provider 应在生成调用前看见范围、数组长度和事件枚举。"""

    parameters = {
        tool_value.name: _provider_parameters(tool_value) for tool_value in DIALOGUE_TOOLS
    }

    search_properties = parameters["search_memories"]["properties"]
    assert search_properties["terms"]["minItems"] == 1
    assert search_properties["terms"]["maxItems"] == 5
    assert search_properties["terms"]["items"]["minLength"] == 1
    assert search_properties["terms"]["items"]["maxLength"] == 64
    assert search_properties["limit"]["minimum"] == 1
    assert search_properties["limit"]["maximum"] == 3

    history_properties = parameters["get_event_history"]["properties"]
    assert history_properties["topics"]["maxItems"] == 5
    assert history_properties["event_types"]["maxItems"] == 2
    assert history_properties["event_types"]["items"]["enum"] == [
        "gift_given",
        "world_progression",
    ]
    history_day_variants = history_properties["since_day_index"]["anyOf"]
    history_integer_day = next(
        variant for variant in history_day_variants if variant.get("type") == "integer"
    )
    assert history_integer_day["minimum"] == 0
    assert history_properties["limit"]["minimum"] == 1
    assert history_properties["limit"]["maximum"] == 5

    progression_properties = parameters["get_progression_context"]["properties"]
    assert progression_properties["topics"]["maxItems"] == 5
    progression_day_variants = progression_properties["since_day_index"]["anyOf"]
    progression_integer_day = next(
        variant for variant in progression_day_variants if variant.get("type") == "integer"
    )
    assert progression_integer_day["minimum"] == 0
    assert progression_properties["limit"]["minimum"] == 1
    assert progression_properties["limit"]["maximum"] == 3


def test_internal_tool_argument_models_forbid_extra_fields() -> None:
    """内部 Schema 接受 injected runtime，但拒绝模型伪造的分区字段。

    LangChain 在 BaseTool 校验前把 runtime 放入内部 tool_input，因此显式 args_schema
    必须声明该 injected 字段；Provider converter 仍由上一测试证明不会公开它。
    """

    for tool_value in DIALOGUE_TOOLS:
        args_schema = tool_value.args_schema
        assert isinstance(args_schema, type)
        assert args_schema.model_config["extra"] == "forbid"
        assert "runtime" in args_schema.model_fields

    search_schema = DIALOGUE_TOOLS[0].args_schema
    assert isinstance(search_schema, type)
    with pytest.raises(ValidationError):
        search_schema.model_validate(
            {
                "terms": ["gift"],
                "limit": 1,
                "runtime": _runtime(_RecordingStorage()),
                "save_id": "model-must-not-control-partition",
            }
        )


@pytest.mark.asyncio
async def test_search_memories_uses_only_trusted_runtime_partition_and_returns_artifact() -> None:
    """模型只提供检索意图；完整分区/关系/cutoff 必须来自 runtime context。"""

    storage = _RecordingStorage()
    content, artifact = await execute_search_memories(
        terms=["gift", "amethyst"],
        limit=2,
        runtime=_runtime(storage),
    )

    assert len(storage.calls) == 1
    operation, query = storage.calls[0]
    assert operation == "search_memories"
    assert query.save_id == "save-1"  # type: ignore[attr-defined]
    assert query.player_id == "player-1"  # type: ignore[attr-defined]
    assert query.npc_id == "Abigail"  # type: ignore[attr-defined]
    assert query.cutoff_day_index == 9  # type: ignore[attr-defined]
    assert query.tags == ("gift", "amethyst")  # type: ignore[attr-defined]
    assert query.limit == 2  # type: ignore[attr-defined]
    assert artifact == (_evidence("memory:gift", "gift_given", "npc:Abigail"),)
    assert json.loads(content)["evidence"][0]["evidence_id"] == "memory:gift"


@pytest.mark.asyncio
async def test_history_and_progression_map_intent_to_typed_storage_queries() -> None:
    """两个工具应保留模型意图，但不能允许模型覆盖可信上下文。"""

    storage = _RecordingStorage()
    runtime = _runtime(storage)

    history_content, history_artifact = await execute_get_event_history(
        topics=["gift"],
        event_types=["gift_given"],
        since_day_index=4,
        limit=4,
        runtime=runtime,
    )
    progression_content, progression_artifact = await execute_get_progression_context(
        topics=["mine"],
        since_day_index=2,
        limit=3,
        runtime=runtime,
    )

    history_query = storage.calls[0][1]
    progression_query = storage.calls[1][1]
    assert history_query.topics == ("gift",)  # type: ignore[attr-defined]
    assert history_query.event_types == ("gift_given",)  # type: ignore[attr-defined]
    assert history_query.since_day_index == 4  # type: ignore[attr-defined]
    assert progression_query.topics == ("mine",)  # type: ignore[attr-defined]
    assert progression_query.since_day_index == 2  # type: ignore[attr-defined]
    assert json.loads(history_content)["result_count"] == 1
    assert json.loads(progression_content)["result_count"] == 1
    assert history_artifact[0].evidence_type == "gift_given"
    assert progression_artifact[0].evidence_type == "world_progression"


@pytest.mark.asyncio
async def test_allowlist_and_expired_deadline_fail_before_storage_access() -> None:
    """隐藏工具与过期任务必须在任何 SQLite 调用前稳定拒绝。"""

    storage = _RecordingStorage()
    with pytest.raises(DialogueToolAuthorizationError, match="not authorized"):
        await execute_search_memories(
            terms=["gift"],
            limit=1,
            runtime=_runtime(storage, allowed_tools=frozenset({"get_event_history"})),
        )
    with pytest.raises(DialogueToolDeadlineExceededError, match="deadline"):
        await execute_get_event_history(
            topics=["gift"],
            event_types=["gift_given"],
            since_day_index=None,
            limit=1,
            runtime=_runtime(storage, deadline_monotonic=time.monotonic() - 1),
        )

    assert storage.calls == []


@pytest.mark.asyncio
@pytest.mark.parametrize(
    "call",
    [
        lambda storage: execute_search_memories(
            terms=[],
            limit=1,
            runtime=_runtime(storage),
        ),
        lambda storage: execute_search_memories(
            terms=["one", "two", "three", "four", "five", "six"],
            limit=1,
            runtime=_runtime(storage),
        ),
        lambda storage: execute_search_memories(
            terms=["gift", "GIFT"],
            limit=1,
            runtime=_runtime(storage),
        ),
        lambda storage: execute_search_memories(
            terms=["gift"],
            limit=4,
            runtime=_runtime(storage),
        ),
        lambda storage: execute_get_event_history(
            topics=[],
            event_types=[],
            since_day_index=None,
            limit=1,
            runtime=_runtime(storage),
        ),
        lambda storage: execute_get_event_history(
            topics=["gift"],
            event_types=["unknown_type"],
            since_day_index=None,
            limit=1,
            runtime=_runtime(storage),
        ),
        lambda storage: execute_get_event_history(
            topics=["gift"],
            event_types=["gift_given"],
            since_day_index=10,
            limit=1,
            runtime=_runtime(storage),
        ),
        lambda storage: execute_get_progression_context(
            topics=["mine", "MINE"],
            since_day_index=None,
            limit=1,
            runtime=_runtime(storage),
        ),
        lambda storage: execute_get_progression_context(
            topics=["x" * 65],
            since_day_index=None,
            limit=1,
            runtime=_runtime(storage),
        ),
    ],
    ids=[
        "empty-search-terms",
        "too-many-search-terms",
        "duplicate-search-terms",
        "memory-limit",
        "unbounded-history",
        "unknown-event-type",
        "future-since",
        "duplicate-progression-topic",
        "topic-too-long",
    ],
)
async def test_invalid_model_intent_is_rejected_before_storage(
    call: object,
) -> None:
    """参数错误不是可重试存储错误，也不能扩大查询资源边界。"""

    storage = _RecordingStorage()
    with pytest.raises(DialogueToolInputError):
        await call(storage)  # type: ignore[operator]
    assert storage.calls == []


@pytest.mark.asyncio
async def test_observation_content_is_canonical_and_bounded_without_changing_artifact() -> None:
    """模型可见摘要受长度约束，审计 artifact 仍保留原始 typed evidence。"""

    storage = _RecordingStorage()
    long_summary = "长" * 2_000
    original = _evidence("memory:long", "gift_given", "npc:Abigail")
    original = EvidenceRecord(
        evidence_id=original.evidence_id,
        evidence_type=original.evidence_type,
        source_event_ids=original.source_event_ids,
        summary=long_summary,
        occurred_day_index=original.occurred_day_index,
        tags=tuple(f"tag-{index}-{'x' * 100}" for index in range(20)),
        visibility_scope=original.visibility_scope,
    )

    async def return_long(_query: object) -> list[EvidenceRecord]:
        storage.calls.append(("search_memories", _query))
        return [original]

    storage.search_memories = return_long  # type: ignore[method-assign]
    first_content, first_artifact = await execute_search_memories(
        terms=["gift"],
        limit=1,
        runtime=_runtime(storage),
    )
    second_content, second_artifact = await execute_search_memories(
        terms=["gift"],
        limit=1,
        runtime=_runtime(storage),
    )

    assert first_content == second_content
    assert len(first_content) <= 4_096
    assert json.loads(first_content)["evidence"][0]["summary"].endswith("…")
    assert first_artifact == second_artifact == (original,)
    assert first_artifact[0].summary == long_summary


def test_target_domain_tools_publish_atomic_zero_argument_provider_schemas() -> None:
    """完整 target registry 一次发布三个领域工具，Provider 业务属性必须为零。"""

    tools = build_domain_dialogue_tools(build_target_capability_registry())

    assert tuple(item.name for item in tools) == (
        "get_npc_history",
        "get_player_progression",
        "get_world_progression",
    )
    assert not {
        "search_memories",
        "get_event_history",
        "get_progression_context",
    }.intersection(item.name for item in tools)
    for tool_value in tools:
        parameters = _provider_parameters(tool_value)
        assert parameters == {
            "properties": {},
            "additionalProperties": False,
            "type": "object",
        }
        assert "截至昨日" in tool_value.description
        assert "只读" in tool_value.description
        assert "memory:" not in tool_value.description

    descriptions = {item.name: item.description for item in tools}
    assert "收获" in descriptions["get_player_progression"]
    assert "礼物" in descriptions["get_npc_history"]
    assert "温室" in descriptions["get_world_progression"]


def test_active_production_registry_publishes_all_tools_but_non_active_states_fail_closed() -> None:
    """生产目录必须发布完整三工具；全 planned 为空，mixed activation 则拒绝启动。"""

    assert tuple(tool_value.name for tool_value in build_domain_dialogue_tools()) == (
        "get_npc_history",
        "get_player_progression",
        "get_world_progression",
    )
    planned = tuple(replace(item, status="planned") for item in MEMORY_CAPABILITIES)
    assert build_domain_dialogue_tools(planned) == ()
    partial = (replace(MEMORY_CAPABILITIES[0], status="planned"), *MEMORY_CAPABILITIES[1:])

    with pytest.raises(RuntimeError, match="atomic"):
        build_domain_dialogue_tools(partial)


def test_target_domain_tool_internal_schema_rejects_any_model_business_argument() -> None:
    """runtime 可由 LangChain 注入，但任何额外模型字段都必须被 extra=forbid 拒绝。"""

    tool_value = build_domain_dialogue_tools(build_target_capability_registry())[1]
    args_schema = tool_value.args_schema
    assert isinstance(args_schema, type)
    assert set(args_schema.model_fields) == {"runtime"}

    with pytest.raises(ValidationError):
        args_schema.model_validate(
            {
                "runtime": _runtime(_RecordingStorage()),
                "terms": ["farming"],
            }
        )


@pytest.mark.asyncio
async def test_zero_argument_player_progression_uses_only_trusted_runtime_query() -> None:
    """模型只选择工具；领域、scope、limit、分区和二元快照全部由 runtime 绑定。"""

    storage = _RecordingStorage()
    content, artifact = await execute_get_player_progression(
        runtime=_runtime(storage, allowed_tools=frozenset({"get_player_progression"}))
    )

    assert len(storage.calls) == 1
    operation, query = storage.calls[0]
    assert operation == "get_domain_memory_candidates"
    assert query.memory_domain == "player_progression"  # type: ignore[attr-defined]
    assert query.source_dialogue_text == "今年的收获和作物看起来真不错。"  # type: ignore[attr-defined]
    assert query.locale == "zh-CN"  # type: ignore[attr-defined]
    assert query.resolved_memory_revision == 8  # type: ignore[attr-defined]
    assert query.resolved_retrieval_state_revision == 5  # type: ignore[attr-defined]
    assert query.limit == 5  # type: ignore[attr-defined]
    assert artifact[0].evidence_id == "memory:farming"
    payload = json.loads(content)
    assert payload["candidate_count"] == 1
    assert payload["candidates"][0] == {
        "evidence_id": "memory:farming",
        "memory_domain": "player_progression",
        "memory_kind": "skill_level_reached",
        "subject_namespace": "skill_id",
        "subject_value": "farming",
        "summary": "第 8 天，玩家的耕种技能提升到 2 级。",
        "occurred_day_index": 8,
    }


@pytest.mark.asyncio
async def test_domain_observation_tail_drops_whole_candidates_and_artifact_matches() -> None:
    """超过 4096 字符时只能从尾部删除整条记录，Guard 不得看到模型未见证据。"""

    storage = _RecordingStorage()
    records = [
        EvidenceRecord(
            evidence_id=f"memory:{index}",
            evidence_type="skill_level_reached",
            source_event_ids=(f"event:{index}",),
            summary=f"第 {index} 条：" + "长" * 1_350,
            occurred_day_index=8 - index,
            tags=(),
            visibility_scope="public",
            memory_domain="player_progression",
            memory_kind="skill_level_reached",
            subject_namespace="skill_id",
            subject_value=("farming", "combat", "fishing")[index],
        )
        for index in range(3)
    ]

    async def return_long(_query: object) -> list[EvidenceRecord]:
        storage.calls.append(("get_domain_memory_candidates", _query))
        return records

    storage.get_domain_memory_candidates = return_long  # type: ignore[method-assign]
    content, artifact = await execute_get_player_progression(
        runtime=_runtime(storage, allowed_tools=frozenset({"get_player_progression"}))
    )
    payload = json.loads(content)

    assert len(content) <= 4_096
    assert 0 < len(artifact) < len(records)
    assert artifact == tuple(records[: len(artifact)])
    assert payload["candidate_count"] == len(artifact)
    assert [item["evidence_id"] for item in payload["candidates"]] == [
        record.evidence_id for record in artifact
    ]
    assert all(candidate["summary"].endswith("长") for candidate in payload["candidates"])


@pytest.mark.asyncio
async def test_single_oversize_domain_candidate_returns_fixed_error_and_empty_artifact() -> None:
    """单条候选不能完整放入 observation 时不得截断字段或授权该 evidence。"""

    storage = _RecordingStorage()
    original = EvidenceRecord(
        evidence_id="memory:oversize",
        evidence_type="skill_level_reached",
        source_event_ids=("event:oversize",),
        summary="长" * 5_000,
        occurred_day_index=8,
        tags=(),
        visibility_scope="public",
        memory_domain="player_progression",
        memory_kind="skill_level_reached",
        subject_namespace="skill_id",
        subject_value="farming",
    )

    async def return_oversize(_query: object) -> list[EvidenceRecord]:
        return [original]

    storage.get_domain_memory_candidates = return_oversize  # type: ignore[method-assign]
    content, artifact = await execute_get_player_progression(
        runtime=_runtime(storage, allowed_tools=frozenset({"get_player_progression"}))
    )

    assert artifact == ()
    assert json.loads(content) == {
        "candidate_count": 0,
        "candidates": [],
        "error_code": "TOOL_OBSERVATION_TOO_LARGE",
    }
