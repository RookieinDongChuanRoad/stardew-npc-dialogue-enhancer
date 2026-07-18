"""持久化边界共享的类型、结果对象、资源上限与稳定异常。

本模块刻意不依赖 SQLAlchemy ORM，也不执行任何 I/O。事件服务、存储门面和
两个扁平 repository 可以共同依赖这里，而不会形成循环导入。异常只表达稳定
业务类别；数据库路径、SQL 和 driver 原始消息不得写入这些异常。
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Any, Literal, TypeGuard

from stardew_npc_agent.contract_limits import WIRE_INTEGER_MAX, WIRE_INTEGER_MIN

AudienceScope = Literal["public", "npc"]
MemoryClassificationStatus = Literal["active", "quarantined"]
MemoryDomain = Literal["npc_history", "player_progression", "world_progression"]
GenerationStatus = Literal["generated", "passthrough", "skipped", "failed"]
EventStorageStatus = Literal["accepted", "duplicate", "conflict"]

# wire DTO、业务 service 与 storage 三层共同执行同一个硬上限。即使内部调用者
# 绕过 Pydantic 构造对象，也不能把无界事件数组带入 SQLite 写事务。
MAX_EVENT_BATCH_ITEMS = 64


def is_wire_integer(value: object) -> TypeGuard[int]:
    """判断运行时值是否是非 bool 且位于完整 Int32 wire 闭区间。

    ``bool`` 是 Python ``int`` 的子类，但 wire contract 的严格整数语义不允许
    ``true/false`` 冒充 1/0。存储入口使用本函数保护绕过 Pydantic 的内部调用。
    """

    return (
        isinstance(value, int)
        and not isinstance(value, bool)
        and WIRE_INTEGER_MIN <= value <= WIRE_INTEGER_MAX
    )


def is_non_negative_wire_integer(value: object) -> TypeGuard[int]:
    """判断运行时值是否是非 bool 且位于 ``0..Int32.MaxValue``。"""

    return is_wire_integer(value) and value >= 0


# readiness 不能只信任 ``alembic_version`` 和表名。列签名使用
# ``(声明类型, NOT NULL, PRIMARY KEY)``，完整覆盖五张核心表的 ORM/migration
# 业务列；否则被漏检列可能在 readiness 通过后才以 500 暴露。更复杂的 constraint
# 与 metadata 漂移再由下方唯一语义探针和 Alembic ``command.check`` 独立验证。
REQUIRED_DATABASE_REVISION = "20260715_0004"
REQUIRED_CORE_TABLES = frozenset(
    {
        "game_events",
        "memories",
        "dialogue_generations",
        "dialogue_display_receipts",
        "memory_partition_states",
    }
)
REQUIRED_COLUMN_SIGNATURES: dict[str, dict[str, tuple[str, bool, bool]]] = {
    "game_events": {
        "id": ("INTEGER", True, True),
        "save_id": ("VARCHAR(255)", True, False),
        "player_id": ("VARCHAR(255)", True, False),
        "event_id": ("VARCHAR(255)", True, False),
        "event_type": ("VARCHAR(100)", True, False),
        "event_version": ("VARCHAR(32)", True, False),
        "occurred_day_index": ("INTEGER", True, False),
        "source": ("VARCHAR(100)", True, False),
        "audience_scope": ("VARCHAR(16)", True, False),
        "audience_npc_id": ("VARCHAR(100)", False, False),
        "payload_json": ("JSON", True, False),
        "created_at_utc": ("DATETIME", True, False),
    },
    "memories": {
        "id": ("INTEGER", True, True),
        "memory_id": ("VARCHAR(96)", True, False),
        "event_id": ("VARCHAR(255)", True, False),
        "save_id": ("VARCHAR(255)", True, False),
        "player_id": ("VARCHAR(255)", True, False),
        "audience_scope": ("VARCHAR(16)", True, False),
        "audience_npc_id": ("VARCHAR(100)", False, False),
        "event_type": ("VARCHAR(100)", True, False),
        "event_version": ("VARCHAR(32)", True, False),
        "source": ("VARCHAR(100)", True, False),
        "payload_json": ("JSON", True, False),
        "classification_status": ("VARCHAR(16)", True, False),
        "memory_domain": ("VARCHAR(32)", False, False),
        "memory_kind": ("VARCHAR(64)", False, False),
        "subject_namespace": ("VARCHAR(64)", False, False),
        "subject_value": ("VARCHAR(255)", False, False),
        "summary": ("TEXT", True, False),
        "tags_json": ("JSON", True, False),
        "importance": ("FLOAT", True, False),
        "occurred_day_index": ("INTEGER", True, False),
        "expires_day_index": ("INTEGER", False, False),
        "last_used_day_index": ("INTEGER", False, False),
        "use_count": ("INTEGER", True, False),
        "relationship_stages_json": ("JSON", True, False),
        "min_friendship_points": ("INTEGER", False, False),
        "max_friendship_points": ("INTEGER", False, False),
        "created_at_utc": ("DATETIME", True, False),
    },
    "memory_partition_states": {
        "id": ("INTEGER", True, True),
        "save_id": ("VARCHAR(255)", True, False),
        "player_id": ("VARCHAR(255)", True, False),
        "memory_revision": ("INTEGER", True, False),
        "retrieval_state_revision": ("INTEGER", True, False),
        "committed_through_day_index": ("INTEGER", True, False),
        "updated_at_utc": ("DATETIME", True, False),
    },
    "dialogue_generations": {
        "id": ("INTEGER", True, True),
        "generation_id": ("VARCHAR(255)", True, False),
        "generation_key": ("VARCHAR(255)", True, False),
        "save_id": ("VARCHAR(255)", True, False),
        "player_id": ("VARCHAR(255)", True, False),
        "game_day_index": ("INTEGER", True, False),
        "npc_id": ("VARCHAR(100)", True, False),
        "locale": ("VARCHAR(32)", True, False),
        "source_hash": ("VARCHAR(255)", True, False),
        "relationship_stage": ("VARCHAR(100)", True, False),
        "friendship_points": ("INTEGER", True, False),
        "memory_cooldown_days": ("INTEGER", True, False),
        "status": ("VARCHAR(16)", True, False),
        "result_text": ("TEXT", False, False),
        "reason_code": ("VARCHAR(100)", True, False),
        "evidence_ids_json": ("JSON", True, False),
        "trace_id": ("VARCHAR(255)", True, False),
        "guard_passed": ("BOOLEAN", True, False),
        "evidence_authorized": ("BOOLEAN", True, False),
        "input_versions_json": ("JSON", False, False),
        "trace_json": ("JSON", False, False),
        "usage_json": ("JSON", False, False),
        "guard_report_json": ("JSON", False, False),
        "created_at_utc": ("DATETIME", True, False),
        "updated_at_utc": ("DATETIME", True, False),
    },
    "dialogue_display_receipts": {
        "id": ("INTEGER", True, True),
        "display_receipt_id": ("VARCHAR(255)", True, False),
        "generation_id": ("VARCHAR(255)", True, False),
        "save_id": ("VARCHAR(255)", True, False),
        "player_id": ("VARCHAR(255)", True, False),
        "displayed_day_index": ("INTEGER", True, False),
        "npc_id": ("VARCHAR(100)", True, False),
        "source_hash": ("VARCHAR(255)", True, False),
        "created_at_utc": ("DATETIME", True, False),
    },
}
REQUIRED_UNIQUE_SIGNATURES: dict[str, frozenset[tuple[str, ...]]] = {
    "game_events": frozenset({("save_id", "player_id", "event_id")}),
    "memories": frozenset(
        {
            ("memory_id",),
            ("save_id", "player_id", "event_id"),
        }
    ),
    "memory_partition_states": frozenset({("save_id", "player_id")}),
    "dialogue_generations": frozenset(
        {
            ("generation_id",),
            ("generation_key",),
        }
    ),
    "dialogue_display_receipts": frozenset({("display_receipt_id",)}),
}


@dataclass(frozen=True, slots=True)
class MemoryProjection:
    """事件服务交给存储层的已验证确定性记忆。

    ``payload_json`` 与可见性字段已经过具体事件版本模板校验；存储层仍会依靠
    数据库约束保护最终完整性，但不会重新解释事件业务语义。
    """

    memory_id: str
    event_id: str
    save_id: str
    player_id: str
    audience_scope: AudienceScope
    audience_npc_id: str | None
    event_type: str
    event_version: str
    source: str
    payload_json: dict[str, Any]
    classification_status: MemoryClassificationStatus
    memory_domain: MemoryDomain
    memory_kind: str
    subject_namespace: str
    subject_value: str
    summary: str
    tags: tuple[str, ...]
    importance: float
    occurred_day_index: int
    expires_day_index: int | None = None
    relationship_stages: tuple[str, ...] = ()
    min_friendship_points: int | None = None
    max_friendship_points: int | None = None


@dataclass(frozen=True, slots=True)
class PreparedEvent:
    """一条可以在同一事务中写入事实表和记忆表的事件。"""

    save_id: str
    player_id: str
    event_id: str
    event_type: str
    event_version: str
    occurred_day_index: int
    source: str
    audience_scope: AudienceScope
    audience_npc_id: str | None
    payload_json: dict[str, Any]
    projection: MemoryProjection


@dataclass(frozen=True, slots=True)
class EventStorageResult:
    """存储层对一条事件的唯一约束裁决。"""

    event_id: str
    status: EventStorageStatus


@dataclass(frozen=True, slots=True)
class EventBatchStorageResult:
    """一个事务提交后的逐项结果和分区水位。"""

    items: tuple[EventStorageResult, ...]
    memory_revision: int
    committed_through_day_index: int


@dataclass(frozen=True, slots=True)
class MemorySearchQuery:
    """记忆 repository 的可信分区与硬过滤参数。"""

    save_id: str
    player_id: str
    npc_id: str
    game_day_index: int
    cutoff_day_index: int
    friendship_points: int
    relationship_stage: str
    tags: tuple[str, ...] = ()
    cooldown_days: int = 3
    limit: int = 3


@dataclass(frozen=True, slots=True)
class EventHistoryQuery:
    """Agent 事件历史工具交给存储层的可信查询。

    ``save_id/player_id/npc_id``、关系快照与 cutoff 来自 runtime context，而非
    模型参数。``topics/event_types/since/limit`` 是模型可表达的查询意图；存储
    入口仍会重新验证全部字段，防止进程内调用绕过工具授权层。
    """

    save_id: str
    player_id: str
    npc_id: str
    game_day_index: int
    cutoff_day_index: int
    friendship_points: int
    relationship_stage: str
    topics: tuple[str, ...] = ()
    event_types: tuple[str, ...] = ()
    since_day_index: int | None = None
    cooldown_days: int = 3
    limit: int = 5


@dataclass(frozen=True, slots=True)
class ProgressionContextQuery:
    """Agent 世界进度工具的可信公共事实查询。

    查询结果只能来自当前 save/player 下的 public ``world_progression`` 投影。
    ``npc_id`` 仍保留在 query 中，使工具入口可以对当前任务执行完整二次授权；
    它不会把 NPC 私有记录放宽为世界事实。
    """

    save_id: str
    player_id: str
    npc_id: str
    game_day_index: int
    cutoff_day_index: int
    friendship_points: int
    relationship_stage: str
    topics: tuple[str, ...] = ()
    since_day_index: int | None = None
    cooldown_days: int = 3
    limit: int = 3


@dataclass(frozen=True, slots=True)
class DomainMemoryQuery:
    """无模型查询参数的领域 Retriever 可信输入。

    所有字段均由生成服务冻结并绑定到零参数工具，模型不能提供或覆盖。两个
    resolved revision 表示本批生成真正读取的 SQLite 候选快照；repository 在
    同一只读事务里逐字复核它们后才允许读取候选。
    """

    save_id: str
    player_id: str
    npc_id: str
    game_day_index: int
    cutoff_day_index: int
    friendship_points: int
    relationship_stage: str
    memory_domain: MemoryDomain
    source_dialogue_text: str
    locale: str
    resolved_memory_revision: int
    resolved_retrieval_state_revision: int
    cooldown_days: int = 3
    limit: int = 5


@dataclass(frozen=True, slots=True)
class EvidenceRecord:
    """可直接供只读工具和 Guard 使用的统一证据记录。

    最后四个分类字段在旧查询兼容期允许为 ``None``；新的领域 repository 只会
    返回四项均非空且已经过 registry 防御性复核的记录。保留默认值可以避免
    尚未从 Agent 注册表移除的旧工具及其历史 fixture 被一次物理迁移打断。
    """

    evidence_id: str
    evidence_type: str
    source_event_ids: tuple[str, ...]
    summary: str
    occurred_day_index: int
    tags: tuple[str, ...]
    visibility_scope: str
    memory_domain: MemoryDomain | None = None
    memory_kind: str | None = None
    subject_namespace: str | None = None
    subject_value: str | None = None


@dataclass(frozen=True, slots=True)
class MemoryPartitionSnapshot:
    """生成服务读取的不可变分区水位。

    同时冻结事实水位、候选状态水位与已提交日；上层不持有 SQLAlchemy record，
    也不会在一次生成中把两个不同 SQLite 状态混合。
    """

    memory_revision: int
    committed_through_day_index: int
    retrieval_state_revision: int = 0


@dataclass(frozen=True, slots=True)
class DialogueGenerationSnapshot:
    """按 generation key 命中后返回的最小不可变生成快照。

    ``evidence_ids`` 使用 tuple 而不是 ORM JSON list，确保缓存结果离开只读
    session 后不会被调用方原地修改，也不会意外触发持久化副作用。
    """

    generation_id: str
    generation_key: str
    status: GenerationStatus
    result_text: str | None
    source_hash: str
    reason_code: str
    evidence_ids: tuple[str, ...]
    trace_id: str


@dataclass(frozen=True, slots=True)
class DialogueGenerationInput:
    """生成服务保存终态记录时使用的类型化输入。

    ``evidence_authorized`` 的唯一合法入参是 ``None``：它明确标记这个值必须
    由存储层在最终保存短事务中计算，调用方不能伪造授权结果。
    """

    generation_id: str
    generation_key: str
    save_id: str
    player_id: str
    game_day_index: int
    npc_id: str
    locale: str
    source_hash: str
    relationship_stage: str
    friendship_points: int
    memory_cooldown_days: int
    status: GenerationStatus
    result_text: str | None
    reason_code: str
    evidence_ids: tuple[str, ...]
    trace_id: str
    guard_passed: bool
    evidence_authorized: None = None
    input_versions: dict[str, Any] | None = None
    trace: dict[str, Any] | None = None
    usage: dict[str, Any] | None = None
    guard_report: dict[str, Any] | None = None


@dataclass(frozen=True, slots=True)
class DisplayReceiptInput:
    """存储层用于原子插入回执的已解析字段。"""

    display_receipt_id: str
    save_id: str
    player_id: str
    displayed_day_index: int
    npc_id: str
    source_hash: str


class InvalidMemoryQueryError(ValueError):
    """记忆查询超出次日截止或资源上限时的 fail-closed 错误。"""


class EventBatchTooLargeError(ValueError):
    """进程内调用者绕过 wire DTO 后仍超过事件批次上限。"""


class MemoryRevisionExhaustedStorageError(ValueError):
    """分区事实或候选状态的下一次合法推进会超出 Int32。"""

    def __init__(self) -> None:
        """使用不含分区 ID、数据库路径或 SQL 的稳定业务消息。"""

        super().__init__("memory revision 已达到 Int32 上限")


class MemoryPartitionStateInvalidStorageError(RuntimeError):
    """既有分区 revision/day 水位违反持久化不变量。"""

    def __init__(self) -> None:
        """使用稳定类别，禁止携带分区身份、腐化原值、SQL 或数据库路径。"""

        super().__init__("memory partition state invalid")


class MemorySnapshotMismatchStorageError(RuntimeError):
    """工具调用看到的二元水位与生成阶段冻结快照不一致。"""

    def __init__(self) -> None:
        """不泄露分区身份或实际 revision，只暴露稳定失败类别。"""

        super().__init__("memory snapshot mismatch")


def validate_memory_partition_state_values(
    memory_revision: object,
    retrieval_state_revision: object,
    committed_through_day_index: object,
) -> None:
    """复核分区三个物理水位；事件、ACK 与只读 snapshot 共用同一不变量。

    ``retrieval_state_revision`` 在 0004 迁移时可从零开始，因此不要求它与
    ``memory_revision`` 大小相等；之后每次候选事实或冷却变化只要求单调推进。
    """

    if not is_non_negative_wire_integer(memory_revision):
        raise MemoryPartitionStateInvalidStorageError()
    if not is_non_negative_wire_integer(retrieval_state_revision):
        raise MemoryPartitionStateInvalidStorageError()
    if not is_wire_integer(committed_through_day_index) or committed_through_day_index < -1:
        raise MemoryPartitionStateInvalidStorageError()
    if not (
        (memory_revision == 0 and committed_through_day_index == -1)
        or (memory_revision > 0 and committed_through_day_index >= 0)
    ):
        raise MemoryPartitionStateInvalidStorageError()


class StorageUnavailableError(RuntimeError):
    """运行时连接、OperationalError 或连接池超时的稳定异常。"""

    def __init__(self) -> None:
        """只保留稳定分类，不接收或拼接 DB path、SQL 与 driver message。"""

        super().__init__("storage unavailable")


class InvalidDialogueGenerationError(ValueError):
    """生成终态、文本、Guard 或 evidence 授权不满足持久化不变量。"""


class GenerationNotFoundStorageError(LookupError):
    """展示 ACK 指向不存在的生成记录。"""


class DisplayNotAllowedStorageError(ValueError):
    """生成状态、Guard、证据或快照不允许消费记忆。"""


class DisplayReceiptConflictStorageError(ValueError):
    """已存在的 receipt ID 被用于不同展示事实。"""
