"""Phase 5 受限 Agent 的三个只读 LangChain 工具。

每个工具都固定经过三层边界：

1. ``@tool`` Schema 只暴露模型的查询意图；
2. ``ToolAuthorizationPolicy`` 从 ``ToolRuntime.context`` 读取并复核可信任务字段；
3. ``SqliteStorage`` 执行带分区、昨日截止、关系、过期与冷却硬过滤的只读查询。

工具使用 ``content_and_artifact``：模型只看到有长度上限的 canonical JSON，程序
则从 artifact 获得未改写的 ``EvidenceRecord``。本模块不写数据库、不访问网络，
也不会把底层异常文本转换成 NPC 台词。
"""

# 本模块刻意不启用 ``from __future__ import annotations``。LangChain 1.3.13 在
# 构造工具 Schema 和注入参数时读取原始 ``inspect.signature`` annotation；若它
# 被延迟成字符串，``ToolRuntime`` 会被错误暴露给模型 Schema，也不会在执行时
# 自动注入。这里保留运行时类型对象，是锁定版本下的必要兼容边界。

import asyncio
import json
import math
import time
from collections.abc import Awaitable, Callable
from dataclasses import dataclass
from typing import Annotated, Any, Literal, TypeVar

from langchain.tools import ToolRuntime
from langchain_core.tools import BaseTool, StructuredTool, tool
from pydantic import BaseModel, ConfigDict, Field, StringConstraints

from stardew_npc_agent.dialogue_agent import (
    DIALOGUE_DOMAIN_TOOL_NAMES,
    GET_EVENT_HISTORY_TOOL_NAME,
    GET_PROGRESSION_CONTEXT_TOOL_NAME,
    SEARCH_MEMORIES_TOOL_NAME,
    TOOL_SCHEMA_VALIDATION_MESSAGE,
    DialogueRuntimeContext,
)
from stardew_npc_agent.memory_capabilities import (
    DOMAIN_TOOL_NAMES,
    MEMORY_CAPABILITIES,
    MemoryCapability,
    MemoryDomain,
    build_domain_tool_description,
)
from stardew_npc_agent.storage import (
    DomainMemoryQuery,
    EventHistoryQuery,
    EvidenceRecord,
    MemorySearchQuery,
    ProgressionContextQuery,
)
from stardew_npc_agent.storage_types import is_non_negative_wire_integer, is_wire_integer

DIALOGUE_TOOL_NAMES = DIALOGUE_DOMAIN_TOOL_NAMES
TARGET_DOMAIN_TOOL_NAMES = frozenset(DOMAIN_TOOL_NAMES.values())
ALL_MEMORY_TOOL_NAMES = DIALOGUE_TOOL_NAMES | TARGET_DOMAIN_TOOL_NAMES

# 当前确定性投影只注册这两类事件。工具层拒绝未知类型，避免模型用任意字符串
# 扩大查询面；未来新增正式投影模板时，应在同一变更中扩展这里和测试。
SUPPORTED_EVENT_HISTORY_TYPES = frozenset({"gift_given", "world_progression"})

MAX_QUERY_TERMS = 5
MAX_TOOL_TERM_CHARACTERS = 64
MAX_TOOL_OBSERVATION_CHARACTERS = 4_096
DEFAULT_TOOL_TIMEOUT_SECONDS = 2.0

StorageResultT = TypeVar("StorageResultT")

# JSON Schema 可以稳定表达单字段长度、数组项数、整数范围和枚举。首尾空白、
# 控制字符、大小写折叠重复与可信 cutoff 仍需执行期确定性复核，因为它们要么
# 超出 Provider Schema 的可移植子集，要么依赖隐藏的 ToolRuntime。
ToolTerm = Annotated[
    str,
    StringConstraints(
        strict=True,
        min_length=1,
        max_length=MAX_TOOL_TERM_CHARACTERS,
    ),
    Field(
        description=(
            "One explicit query term. It must have no leading/trailing whitespace "
            "or control characters and must not duplicate another term."
        )
    ),
]
NonNegativeDayIndex = Annotated[
    int,
    Field(
        strict=True,
        ge=0,
        description=(
            "A non-negative absolute game day. It must not be later than the "
            "trusted cutoff day described in the conversation context."
        ),
    ),
]
SupportedEventType = Literal["gift_given", "world_progression"]


class DialogueToolInput(BaseModel):
    """三个只读工具共享的严格模型输入基类。

    LangChain 的 ToolNode 会先把 runtime 注入内部 tool_input，再调用 BaseTool 的
    Pydantic 校验。因此内部 args_schema 必须声明 runtime；ToolRuntime annotation
    会让 tool_call_schema 和 Provider converter 自动隐藏它。extra=forbid 则确保
    save/player/NPC 等其他字段不能作为模型参数穿透到工具函数。
    """

    model_config = ConfigDict(extra="forbid", arbitrary_types_allowed=True)

    runtime: ToolRuntime[DialogueRuntimeContext, Any]


class DomainMemoryToolInput(DialogueToolInput):
    """目标领域工具的内部 Schema；模型可见部分没有任何业务字段。"""


class ZeroArgumentDomainTool(StructuredTool):
    """内部保留 injected runtime、Provider 只看严格空对象的领域工具。

    LangChain 1.3.13 在从含 ``InjectedToolArg`` 的 Pydantic 模型生成 subset 时会
    丢失原模型的 ``extra=forbid``，使空 Schema 缺少 ``additionalProperties``。
    这个窄子类只修正模型可见 Schema；执行期仍使用 ``DomainMemoryToolInput``
    验证注入 runtime 和拒绝额外字段。
    """

    @property
    def tool_call_schema(self) -> dict[str, Any]:
        """返回冻结的零业务参数 JSON Schema，不暴露 injected runtime。"""

        return {
            "type": "object",
            "properties": {},
            "additionalProperties": False,
        }


class SearchMemoriesToolInput(DialogueToolInput):
    """相关记忆查询；模型直接提交标签单位，不再提交待拆词句子。"""

    terms: list[ToolTerm] = Field(
        min_length=1,
        max_length=MAX_QUERY_TERMS,
        description="One to five separate semantic tags, ordered by relevance.",
    )
    limit: int = Field(
        strict=True,
        ge=1,
        le=3,
        description="Maximum number of visible memories to return.",
    )


class GetEventHistoryToolInput(DialogueToolInput):
    """NPC 事件历史查询的静态字段合同。

    topics 与 event_types 至少一个非空属于跨字段规则。当前 LangChain converter
    不保留根级 anyOf，因此该规则同时写进 description 并由执行函数再次拒绝。
    """

    topics: list[ToolTerm] = Field(
        max_length=MAX_QUERY_TERMS,
        description="Zero to five topics. topics and event_types must not both be empty.",
    )
    event_types: list[SupportedEventType] = Field(
        max_length=2,
        description=("Allowed event categories. topics and event_types must not both be empty."),
    )
    since_day_index: NonNegativeDayIndex | None = Field(
        description="Optional inclusive lower day bound; null means no lower bound."
    )
    limit: int = Field(
        strict=True,
        ge=1,
        le=5,
        description="Maximum number of visible history records to return.",
    )


class GetProgressionContextToolInput(DialogueToolInput):
    """公共世界进度查询；空 topics 明确表示读取最近进度。"""

    topics: list[ToolTerm] = Field(
        max_length=MAX_QUERY_TERMS,
        description="Zero to five topics; an empty list requests recent public progress.",
    )
    since_day_index: NonNegativeDayIndex | None = Field(
        description="Optional inclusive lower day bound; null means no lower bound."
    )
    limit: int = Field(
        strict=True,
        ge=1,
        le=3,
        description="Maximum number of visible progression records to return.",
    )


class DialogueToolAuthorizationError(PermissionError):
    """当前任务不允许执行目标工具或可信上下文无效。"""

    error_code = "TOOL_NOT_AUTHORIZED"

    def __init__(self) -> None:
        """使用固定消息，禁止携带 save/player/NPC 等分区身份。"""

        super().__init__("tool not authorized")


class DialogueToolDeadlineExceededError(TimeoutError):
    """任务总 deadline 或单工具等待预算已经耗尽。"""

    error_code = "TOOL_DEADLINE_EXCEEDED"

    def __init__(self) -> None:
        """使用固定消息，不保留底层路径、SQL 或 timing 细节。"""

        super().__init__("tool deadline exceeded")


class DialogueToolInputError(ValueError):
    """模型提供的查询意图超出字段或资源边界。"""

    error_code = "TOOL_INPUT_INVALID"

    def __init__(self) -> None:
        """错误正文保持稳定，不回显可能含指令或敏感内容的模型参数。"""

        super().__init__("tool input invalid")


@dataclass(frozen=True, slots=True)
class ToolAuthorizationPolicy:
    """在真实工具执行前复核 allowlist、分区快照和资源预算。

    ``clock`` 可在单元测试中注入，但生产默认使用单调时钟。授权成功返回距离
    总 deadline 的秒数，调用方据此同时执行 2 秒单工具 timeout 和任务级上限。
    """

    clock: Callable[[], float] = time.monotonic

    def authorize(
        self,
        *,
        tool_name: str,
        runtime: ToolRuntime[DialogueRuntimeContext, Any],
        limit: int,
        max_limit: int,
        since_day_index: int | None,
    ) -> float:
        """验证当前调用且不执行任何 I/O。

        Raises:
            DialogueToolAuthorizationError: runtime/allowlist/可信任务字段无效。
            DialogueToolInputError: limit 或 since 属于非法模型查询意图。
            DialogueToolDeadlineExceededError: 总任务 deadline 已过。
        """

        remaining_seconds = self._authorize_runtime(tool_name=tool_name, runtime=runtime)
        context = runtime.context
        if not isinstance(context, DialogueRuntimeContext):
            # ``_authorize_runtime`` 已经拒绝；此分支仅帮助静态类型缩窄。
            raise DialogueToolAuthorizationError() from None

        if not is_wire_integer(limit) or not 1 <= limit <= max_limit:
            raise DialogueToolInputError() from None
        if since_day_index is not None:
            if not is_non_negative_wire_integer(since_day_index):
                raise DialogueToolInputError() from None
            if since_day_index > context.cutoff_day_index:
                raise DialogueToolInputError() from None
        return remaining_seconds

    def authorize_domain(
        self,
        *,
        tool_name: str,
        runtime: ToolRuntime[DialogueRuntimeContext, Any],
    ) -> float:
        """授权零业务参数领域工具，并复核可信原文与二元 revision 锚点。"""

        if tool_name not in TARGET_DOMAIN_TOOL_NAMES:
            raise DialogueToolAuthorizationError() from None
        remaining_seconds = self._authorize_runtime(tool_name=tool_name, runtime=runtime)
        context = runtime.context
        if not isinstance(context, DialogueRuntimeContext):
            raise DialogueToolAuthorizationError() from None
        for value in (context.source_hash, context.locale):
            if not isinstance(value, str) or not value or value != value.strip():
                raise DialogueToolAuthorizationError() from None
        if (
            not isinstance(context.source_dialogue_text, str)
            or not context.source_dialogue_text
            or "\x00" in context.source_dialogue_text
        ):
            raise DialogueToolAuthorizationError() from None
        for revision in (
            context.required_memory_revision,
            context.resolved_memory_revision,
            context.resolved_retrieval_state_revision,
        ):
            if not is_non_negative_wire_integer(revision):
                raise DialogueToolAuthorizationError() from None
        if context.resolved_memory_revision < context.required_memory_revision:
            raise DialogueToolAuthorizationError() from None
        return remaining_seconds

    def _authorize_runtime(
        self,
        *,
        tool_name: str,
        runtime: ToolRuntime[DialogueRuntimeContext, Any],
    ) -> float:
        """复核新旧只读工具共享的 allowlist、分区、日期与 deadline。"""

        context = runtime.context
        if not isinstance(context, DialogueRuntimeContext):
            raise DialogueToolAuthorizationError() from None
        if tool_name not in ALL_MEMORY_TOOL_NAMES or tool_name not in context.allowed_tools:
            raise DialogueToolAuthorizationError() from None
        if not isinstance(context.allowed_tools, frozenset) or not context.allowed_tools.issubset(
            ALL_MEMORY_TOOL_NAMES
        ):
            raise DialogueToolAuthorizationError() from None

        for value in (
            context.task_id,
            context.save_id,
            context.player_id,
            context.npc_id,
            context.relationship_stage,
        ):
            if not isinstance(value, str) or not value or value != value.strip():
                raise DialogueToolAuthorizationError() from None
        if not is_non_negative_wire_integer(context.game_day_index):
            raise DialogueToolAuthorizationError() from None
        if not is_non_negative_wire_integer(context.cutoff_day_index):
            raise DialogueToolAuthorizationError() from None
        if context.cutoff_day_index > context.game_day_index - 1:
            raise DialogueToolAuthorizationError() from None
        if not is_wire_integer(context.friendship_points):
            raise DialogueToolAuthorizationError() from None
        if not is_non_negative_wire_integer(context.memory_cooldown_days):
            raise DialogueToolAuthorizationError() from None

        if not isinstance(context.deadline_monotonic, (int, float)) or isinstance(
            context.deadline_monotonic, bool
        ):
            raise DialogueToolAuthorizationError() from None
        deadline = float(context.deadline_monotonic)
        if not math.isfinite(deadline):
            raise DialogueToolAuthorizationError() from None
        remaining_seconds = deadline - self.clock()
        if remaining_seconds <= 0:
            raise DialogueToolDeadlineExceededError() from None
        return remaining_seconds


_AUTHORIZATION_POLICY = ToolAuthorizationPolicy()


async def execute_search_memories(
    *,
    terms: list[str],
    limit: int,
    runtime: ToolRuntime[DialogueRuntimeContext, Any],
) -> tuple[str, tuple[EvidenceRecord, ...]]:
    """使用模型已经结构化的标签执行相关记忆检索。

    Args:
        terms: 一到五个独立语义标签；顺序会原样进入确定性存储查询。
        limit: 最多返回三条可见记忆。
        runtime: LangChain 注入的可信任务分区、截止日、存储和 deadline。
    Returns:
        有界 canonical JSON observation 与未改写的 typed evidence artifact。
    Raises:
        DialogueToolInputError: 标签或 limit 越过静态/执行期边界。
        DialogueToolAuthorizationError: runtime 或任务授权无效。
        DialogueToolDeadlineExceededError: 工具等待预算已经耗尽。
    """

    remaining_seconds = _AUTHORIZATION_POLICY.authorize(
        tool_name=SEARCH_MEMORIES_TOOL_NAME,
        runtime=runtime,
        limit=limit,
        max_limit=3,
        since_day_index=None,
    )
    normalized_terms = _normalize_terms(terms, allow_empty=False)
    context = runtime.context
    storage_query = MemorySearchQuery(
        save_id=context.save_id,
        player_id=context.player_id,
        npc_id=context.npc_id,
        game_day_index=context.game_day_index,
        cutoff_day_index=context.cutoff_day_index,
        friendship_points=context.friendship_points,
        relationship_stage=context.relationship_stage,
        tags=normalized_terms,
        cooldown_days=context.memory_cooldown_days,
        limit=limit,
    )
    records = await _await_storage(
        context.storage.search_memories(storage_query),
        remaining_seconds=remaining_seconds,
    )
    artifact = tuple(records)
    return _serialize_observation(SEARCH_MEMORIES_TOOL_NAME, artifact), artifact


async def execute_get_event_history(
    *,
    topics: list[str],
    event_types: list[str],
    since_day_index: int | None,
    limit: int,
    runtime: ToolRuntime[DialogueRuntimeContext, Any],
) -> tuple[str, tuple[EvidenceRecord, ...]]:
    """执行带主题/类型/起始日意图的结构化历史查询。"""

    remaining_seconds = _AUTHORIZATION_POLICY.authorize(
        tool_name=GET_EVENT_HISTORY_TOOL_NAME,
        runtime=runtime,
        limit=limit,
        max_limit=5,
        since_day_index=since_day_index,
    )
    normalized_topics = _normalize_terms(topics, allow_empty=True)
    normalized_event_types = _normalize_terms(event_types, allow_empty=True)
    if not normalized_topics and not normalized_event_types:
        raise DialogueToolInputError() from None
    if not set(normalized_event_types).issubset(SUPPORTED_EVENT_HISTORY_TYPES):
        raise DialogueToolInputError() from None

    context = runtime.context
    storage_query = EventHistoryQuery(
        save_id=context.save_id,
        player_id=context.player_id,
        npc_id=context.npc_id,
        game_day_index=context.game_day_index,
        cutoff_day_index=context.cutoff_day_index,
        friendship_points=context.friendship_points,
        relationship_stage=context.relationship_stage,
        topics=normalized_topics,
        event_types=normalized_event_types,
        since_day_index=since_day_index,
        cooldown_days=context.memory_cooldown_days,
        limit=limit,
    )
    records = await _await_storage(
        context.storage.get_event_history(storage_query),
        remaining_seconds=remaining_seconds,
    )
    artifact = tuple(records)
    return _serialize_observation(GET_EVENT_HISTORY_TOOL_NAME, artifact), artifact


async def execute_get_progression_context(
    *,
    topics: list[str],
    since_day_index: int | None,
    limit: int,
    runtime: ToolRuntime[DialogueRuntimeContext, Any],
) -> tuple[str, tuple[EvidenceRecord, ...]]:
    """执行公共世界进度查询；空 topics 表示读取最近进度。"""

    remaining_seconds = _AUTHORIZATION_POLICY.authorize(
        tool_name=GET_PROGRESSION_CONTEXT_TOOL_NAME,
        runtime=runtime,
        limit=limit,
        max_limit=3,
        since_day_index=since_day_index,
    )
    normalized_topics = _normalize_terms(topics, allow_empty=True)
    context = runtime.context
    storage_query = ProgressionContextQuery(
        save_id=context.save_id,
        player_id=context.player_id,
        npc_id=context.npc_id,
        game_day_index=context.game_day_index,
        cutoff_day_index=context.cutoff_day_index,
        friendship_points=context.friendship_points,
        relationship_stage=context.relationship_stage,
        topics=normalized_topics,
        since_day_index=since_day_index,
        cooldown_days=context.memory_cooldown_days,
        limit=limit,
    )
    records = await _await_storage(
        context.storage.get_progression_context(storage_query),
        remaining_seconds=remaining_seconds,
    )
    artifact = tuple(records)
    return _serialize_observation(GET_PROGRESSION_CONTEXT_TOOL_NAME, artifact), artifact


async def _execute_domain_memory_tool(
    *,
    tool_name: str,
    memory_domain: MemoryDomain,
    runtime: ToolRuntime[DialogueRuntimeContext, Any],
) -> tuple[str, tuple[EvidenceRecord, ...]]:
    """用固定领域与可信 runtime 执行一次零业务参数候选读取。"""

    remaining_seconds = _AUTHORIZATION_POLICY.authorize_domain(
        tool_name=tool_name,
        runtime=runtime,
    )
    context = runtime.context
    storage_query = DomainMemoryQuery(
        save_id=context.save_id,
        player_id=context.player_id,
        npc_id=context.npc_id,
        game_day_index=context.game_day_index,
        cutoff_day_index=context.cutoff_day_index,
        friendship_points=context.friendship_points,
        relationship_stage=context.relationship_stage,
        memory_domain=memory_domain,
        source_dialogue_text=context.source_dialogue_text,
        locale=context.locale,
        resolved_memory_revision=context.resolved_memory_revision,
        resolved_retrieval_state_revision=context.resolved_retrieval_state_revision,
        cooldown_days=context.memory_cooldown_days,
        limit=5,
    )
    records = await _await_storage(
        context.storage.get_domain_memory_candidates(storage_query),
        remaining_seconds=remaining_seconds,
    )
    return _serialize_domain_observation(tuple(records))


async def execute_get_npc_history(
    *,
    runtime: ToolRuntime[DialogueRuntimeContext, Any],
) -> tuple[str, tuple[EvidenceRecord, ...]]:
    """读取当前 NPC 的私人共同历史；模型不能提交任何查询参数。"""

    return await _execute_domain_memory_tool(
        tool_name=DOMAIN_TOOL_NAMES["npc_history"],
        memory_domain="npc_history",
        runtime=runtime,
    )


async def execute_get_player_progression(
    *,
    runtime: ToolRuntime[DialogueRuntimeContext, Any],
) -> tuple[str, tuple[EvidenceRecord, ...]]:
    """读取玩家能力与长期成长；模型不能提交任何查询参数。"""

    return await _execute_domain_memory_tool(
        tool_name=DOMAIN_TOOL_NAMES["player_progression"],
        memory_domain="player_progression",
        runtime=runtime,
    )


async def execute_get_world_progression(
    *,
    runtime: ToolRuntime[DialogueRuntimeContext, Any],
) -> tuple[str, tuple[EvidenceRecord, ...]]:
    """读取公共世界设施进展；模型不能提交任何查询参数。"""

    return await _execute_domain_memory_tool(
        tool_name=DOMAIN_TOOL_NAMES["world_progression"],
        memory_domain="world_progression",
        runtime=runtime,
    )


@tool(
    SEARCH_MEMORIES_TOOL_NAME,
    args_schema=SearchMemoriesToolInput,
    response_format="content_and_artifact",
)
async def search_memories_tool(
    terms: list[str],
    limit: int,
    runtime: ToolRuntime[DialogueRuntimeContext, Any],
) -> tuple[str, tuple[EvidenceRecord, ...]]:
    """Search memories using one to five separate semantic tags."""

    return await execute_search_memories(terms=terms, limit=limit, runtime=runtime)


@tool(
    GET_EVENT_HISTORY_TOOL_NAME,
    args_schema=GetEventHistoryToolInput,
    response_format="content_and_artifact",
)
async def get_event_history_tool(
    topics: list[str],
    event_types: list[str],
    since_day_index: int | None,
    limit: int,
    runtime: ToolRuntime[DialogueRuntimeContext, Any],
) -> tuple[str, tuple[EvidenceRecord, ...]]:
    """Read visible event history; topics and event_types cannot both be empty."""

    return await execute_get_event_history(
        topics=topics,
        event_types=event_types,
        since_day_index=since_day_index,
        limit=limit,
        runtime=runtime,
    )


@tool(
    GET_PROGRESSION_CONTEXT_TOOL_NAME,
    args_schema=GetProgressionContextToolInput,
    response_format="content_and_artifact",
)
async def get_progression_context_tool(
    topics: list[str],
    since_day_index: int | None,
    limit: int,
    runtime: ToolRuntime[DialogueRuntimeContext, Any],
) -> tuple[str, tuple[EvidenceRecord, ...]]:
    """Read public world progression; empty topics means recent public progress."""

    return await execute_get_progression_context(
        topics=topics,
        since_day_index=since_day_index,
        limit=limit,
        runtime=runtime,
    )


# BaseTool 默认 validation 文本会回显完整模型参数和 Pydantic 细节。三个工具统一
# 使用固定消息，让模型能纠正静态 Schema 错误，同时不把自由参数或内部约束写回
# ToolMessage。该字段只处理 Pydantic ValidationError，不会吞授权或存储异常。
for tool_value in (
    search_memories_tool,
    get_event_history_tool,
    get_progression_context_tool,
):
    tool_value.handle_validation_error = TOOL_SCHEMA_VALIDATION_MESSAGE


# 固定 tuple 顺序既让测试和 Prompt 可复现，也避免调用方运行时增删工具。
DIALOGUE_TOOLS: tuple[BaseTool, ...] = (
    search_memories_tool,
    get_event_history_tool,
    get_progression_context_tool,
)


def build_domain_dialogue_tools(
    registry: tuple[MemoryCapability, ...] = MEMORY_CAPABILITIES,
) -> tuple[BaseTool, ...]:
    """从完整 active registry 原子构造三个目标领域工具。

    全 planned 表示生产构建尚未达到激活门槛，安全返回空 tuple。任何 mixed
    状态、缺 kind 或额外 kind 都是原子发布配置错误，必须 fail closed；绝不
    根据某个存档有没有记录动态增删工具。
    """

    expected_kinds = {item.kind for item in MEMORY_CAPABILITIES}
    actual_kinds = {item.kind for item in registry}
    statuses = {item.status for item in registry}
    if actual_kinds == expected_kinds and statuses == {"planned"}:
        return ()
    if actual_kinds != expected_kinds or statuses != {"active"}:
        raise RuntimeError("domain tool activation must be atomic")

    ordered_domains: tuple[MemoryDomain, ...] = (
        "npc_history",
        "player_progression",
        "world_progression",
    )
    return tuple(_build_domain_tool(domain=domain, registry=registry) for domain in ordered_domains)


def _build_domain_tool(
    *,
    domain: MemoryDomain,
    registry: tuple[MemoryCapability, ...],
) -> BaseTool:
    """构造一个 closure 绑定固定 domain 的 LangChain StructuredTool。"""

    tool_name = DOMAIN_TOOL_NAMES[domain]
    description = build_domain_tool_description(domain, registry)

    async def domain_memory_tool(
        runtime: ToolRuntime[DialogueRuntimeContext, Any],
    ) -> tuple[str, tuple[EvidenceRecord, ...]]:
        """执行 closure 已绑定的只读 memory 领域查询。"""

        return await _execute_domain_memory_tool(
            tool_name=tool_name,
            memory_domain=domain,
            runtime=runtime,
        )

    return ZeroArgumentDomainTool(
        name=tool_name,
        description=description,
        args_schema=DomainMemoryToolInput,
        response_format="content_and_artifact",
        coroutine=domain_memory_tool,
        handle_validation_error=TOOL_SCHEMA_VALIDATION_MESSAGE,
    )


async def _await_storage(
    operation: Awaitable[StorageResultT],
    *,
    remaining_seconds: float,
) -> StorageResultT:
    """同时执行 2 秒单工具 timeout 与任务剩余 deadline。

    只翻译 timeout；``StorageUnavailableError`` 等稳定存储异常保持原类型，供后续
    官方 ``ToolRetryMiddleware`` 精确决定是否最多重试一次。取消属于宿主控制流，
    ``CancelledError`` 不会被这里捕获。
    """

    timeout_seconds = min(DEFAULT_TOOL_TIMEOUT_SECONDS, remaining_seconds)
    try:
        async with asyncio.timeout(timeout_seconds):
            return await operation
    except TimeoutError:
        raise DialogueToolDeadlineExceededError() from None


def _normalize_terms(values: list[str], *, allow_empty: bool) -> tuple[str, ...]:
    """验证模型提供的 topic/type 词组，不静默修剪或截断。"""

    if not isinstance(values, list) or len(values) > MAX_QUERY_TERMS:
        raise DialogueToolInputError() from None
    if not values and not allow_empty:
        raise DialogueToolInputError() from None

    normalized: list[str] = []
    seen: set[str] = set()
    for value in values:
        if (
            not isinstance(value, str)
            or not value
            or value != value.strip()
            or len(value) > MAX_TOOL_TERM_CHARACTERS
            or _contains_control_character(value)
        ):
            raise DialogueToolInputError() from None
        folded = value.casefold()
        if folded in seen:
            raise DialogueToolInputError() from None
        seen.add(folded)
        normalized.append(value)
    return tuple(normalized)


def _contains_control_character(value: str) -> bool:
    """拒绝 C0/C1 控制字符，避免 observation/log 边界被自由文本打断。"""

    return any(ord(character) < 32 or 127 <= ord(character) <= 159 for character in value)


def _serialize_observation(
    tool_name: str,
    evidence: tuple[EvidenceRecord, ...],
) -> str:
    """生成不超过 4096 字符的 canonical JSON observation。

    Artifact 始终保留完整记录；这里只逐级缩短模型可见摘要、标签和 source event
    ID。每一级都是确定的，因此相同 evidence 会产生逐字相同内容。
    """

    tiers = (
        (320, 4, 48, 2, 128),
        (160, 2, 40, 1, 96),
        (80, 0, 0, 0, 0),
    )
    for summary_limit, tag_limit, tag_char_limit, source_limit, source_char_limit in tiers:
        payload = {
            "tool": tool_name,
            "result_count": len(evidence),
            "evidence": [
                {
                    "evidence_id": _truncate_text(record.evidence_id, 128),
                    "evidence_type": _truncate_text(record.evidence_type, 100),
                    "source_event_ids": [
                        _truncate_text(event_id, source_char_limit)
                        for event_id in record.source_event_ids[:source_limit]
                    ],
                    "summary": _truncate_text(record.summary, summary_limit),
                    "occurred_day_index": record.occurred_day_index,
                    "tags": [
                        _truncate_text(tag, tag_char_limit) for tag in record.tags[:tag_limit]
                    ],
                    "visibility_scope": _truncate_text(record.visibility_scope, 128),
                }
                for record in evidence
            ],
        }
        encoded = json.dumps(
            payload,
            ensure_ascii=False,
            sort_keys=True,
            separators=(",", ":"),
            allow_nan=False,
        )
        if len(encoded) <= MAX_TOOL_OBSERVATION_CHARACTERS:
            return encoded

    # 最小 tier 只含受限固定字段，正常数据库约束下不会到达这里。若持久化数据
    # 已异常膨胀，fail closed 比把截断不完整的 JSON 交给模型更安全。
    raise DialogueToolInputError() from None


def _serialize_domain_observation(
    evidence: tuple[EvidenceRecord, ...],
) -> tuple[str, tuple[EvidenceRecord, ...]]:
    """序列化领域候选，并只从尾部整条删除直到 observation 合法。

    与旧兼容工具的字段截断不同，目标合同不允许截断 summary、ID 或分类字段。
    返回的 artifact 必须与模型最终实际看见的候选逐条一致，Guard 因而不可能
    授权一个已被长度裁剪移除的 evidence。
    """

    if len(evidence) > 5:
        raise DialogueToolInputError() from None
    for record in evidence:
        if (
            record.memory_domain
            not in {
                "npc_history",
                "player_progression",
                "world_progression",
            }
            or not record.memory_kind
            or not record.subject_namespace
            or not record.subject_value
        ):
            raise DialogueToolInputError() from None

    visible = evidence
    while visible:
        encoded = _encode_domain_observation(visible)
        if len(encoded) <= MAX_TOOL_OBSERVATION_CHARACTERS:
            return encoded, visible
        visible = visible[:-1]

    empty_encoded = _encode_domain_observation(())
    if not evidence:
        return empty_encoded, ()
    error_encoded = json.dumps(
        {
            "candidate_count": 0,
            "candidates": [],
            "error_code": "TOOL_OBSERVATION_TOO_LARGE",
        },
        ensure_ascii=False,
        sort_keys=True,
        separators=(",", ":"),
        allow_nan=False,
    )
    return error_encoded, ()


def _encode_domain_observation(evidence: tuple[EvidenceRecord, ...]) -> str:
    """把已确定最终可见的完整候选编码为 canonical JSON。"""

    return json.dumps(
        {
            "candidate_count": len(evidence),
            "candidates": [
                {
                    "evidence_id": record.evidence_id,
                    "memory_domain": record.memory_domain,
                    "memory_kind": record.memory_kind,
                    "subject_namespace": record.subject_namespace,
                    "subject_value": record.subject_value,
                    "summary": record.summary,
                    "occurred_day_index": record.occurred_day_index,
                }
                for record in evidence
            ],
        },
        ensure_ascii=False,
        sort_keys=True,
        separators=(",", ":"),
        allow_nan=False,
    )


def _truncate_text(value: str, limit: int) -> str:
    """按字符数确定性截断；limit=0 返回空串。"""

    if limit <= 0:
        return ""
    if len(value) <= limit:
        return value
    if limit == 1:
        return "…"
    return value[: limit - 1] + "…"
