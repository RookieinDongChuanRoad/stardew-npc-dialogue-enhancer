"""SQLAlchemy 2 持久化模型。

本模块只声明表、列、索引与数据库级约束，不创建 engine、不打开连接，也不承载
事件投影或 ACK 业务流程。Alembic 通过兼容门面导出的 ``Base`` 读取同一份
metadata，从而让 ``alembic check`` 能发现 ORM 与 initial migration 的漂移。
"""

from __future__ import annotations

from datetime import UTC, datetime
from typing import Any

from sqlalchemy import (
    JSON,
    Boolean,
    CheckConstraint,
    DateTime,
    Float,
    ForeignKey,
    Index,
    Integer,
    String,
    Text,
    UniqueConstraint,
)
from sqlalchemy.orm import DeclarativeBase, Mapped, mapped_column


def utc_now() -> datetime:
    """生成带 UTC 时区的当前时间，供模型默认值和短事务显式时间戳复用。"""

    return datetime.now(UTC)


# Python 3.11 ``str.strip()`` 识别的完整 whitespace code point 集合。使用 SQLite
# ``char(...)`` 明确构造 trim 字符集，避免默认 trim 只处理普通空格，导致 direct
# INSERT 可以保存 Python 正常 API 会拒绝的 tab、CR/LF、NBSP 或 Unicode 空格。
PYTHON_STRIP_SQLITE_CHARACTERS_SQL = (
    "char(9,10,11,12,13,28,29,30,31,32,133,160,5760,"
    "8192,8193,8194,8195,8196,8197,8198,8199,8200,8201,8202,"
    "8232,8233,8239,8287,12288)"
)
DIALOGUE_GENERATION_EVIDENCE_ARRAY_CHECK_SQL = (
    "json_valid(evidence_ids_json) = 1 AND json_type(evidence_ids_json) = 'array'"
)
DIALOGUE_GENERATION_RESULT_AUTHORIZATION_CHECK_SQL = (
    "(status = 'generated' AND result_text IS NOT NULL "
    "AND length(result_text) > 0 "
    "AND instr(result_text, char(0)) = 0 "
    f"AND trim(result_text, {PYTHON_STRIP_SQLITE_CHARACTERS_SQL}) = result_text "
    "AND guard_passed = 1 AND evidence_authorized = 1) OR "
    "(status != 'generated' AND result_text IS NULL AND evidence_authorized = 0 "
    "AND json_array_length(evidence_ids_json) = 0)"
)
MEMORY_ACTIVE_CLASSIFICATION_CHECK_SQL = (
    "classification_status = 'quarantined' OR (classification_status = 'active' AND ("
    "(memory_domain = 'npc_history' AND audience_scope = 'npc' "
    "AND audience_npc_id IS NOT NULL AND ("
    "(memory_kind = 'gift_given' AND subject_namespace = 'item_id' "
    "AND subject_value IS NOT NULL AND length(subject_value) > 0 "
    "AND instr(subject_value, char(0)) = 0 "
    "AND substr(subject_value, 1, 1) = '(' "
    "AND instr(subject_value, ')') > 2 "
    "AND instr(subject_value, ')') < length(subject_value) "
    f"AND trim(subject_value, {PYTHON_STRIP_SQLITE_CHARACTERS_SQL}) = subject_value) OR "
    "(memory_kind = 'relationship_status_changed' "
    "AND subject_namespace = 'relationship_status' "
    "AND subject_value IN ('friendly','dating','engaged','married','divorced')) OR "
    "(memory_kind = 'friendship_milestone_reached' "
    "AND subject_namespace = 'milestone_id' AND subject_value = 'friend'))) OR "
    "(memory_domain = 'player_progression' AND audience_scope = 'public' "
    "AND audience_npc_id IS NULL AND ("
    "(memory_kind = 'skill_level_reached' AND subject_namespace = 'skill_id' "
    "AND subject_value IN ('farming','fishing','foraging','mining','combat')) OR "
    "(memory_kind = 'mine_depth_milestone_reached' AND subject_namespace = 'mine_id' "
    "AND subject_value IN ('the_mines','skull_cavern')) OR "
    "(memory_kind = 'tool_upgrade_received' AND subject_namespace = 'tool_id' "
    "AND subject_value IN ('axe','pickaxe','hoe','watering_can','pan','trash_can')) OR "
    "(memory_kind = 'mastery_claimed' AND subject_namespace = 'skill_id' "
    "AND subject_value IN ('farming','fishing','foraging','mining','combat')))) OR "
    "(memory_domain = 'world_progression' AND audience_scope = 'public' "
    "AND audience_npc_id IS NULL AND memory_kind = 'public_facility_restored' "
    "AND subject_namespace = 'facility_id' "
    "AND subject_value IN "
    "('greenhouse','minecarts','bus_service','quarry_bridge','glittering_boulder'))))"
)


class Base(DeclarativeBase):
    """Alembic 与运行时共用的 SQLAlchemy declarative metadata 根。"""


class GameEventRecord(Base):
    """由 SMAPI 确认已发生的结构化事实。"""

    __tablename__ = "game_events"
    __table_args__ = (
        UniqueConstraint(
            "save_id",
            "player_id",
            "event_id",
            name="uq_game_events_partition_event",
        ),
        CheckConstraint(
            "audience_scope IN ('public', 'npc')",
            name="ck_game_events_audience_scope",
        ),
        CheckConstraint(
            "(audience_scope = 'public' AND audience_npc_id IS NULL) OR "
            "(audience_scope = 'npc' AND audience_npc_id IS NOT NULL)",
            name="ck_game_events_audience_target",
        ),
        Index(
            "ix_game_events_partition_day",
            "save_id",
            "player_id",
            "occurred_day_index",
        ),
    )

    id: Mapped[int] = mapped_column(Integer, primary_key=True, autoincrement=True)
    save_id: Mapped[str] = mapped_column(String(255), nullable=False)
    player_id: Mapped[str] = mapped_column(String(255), nullable=False)
    event_id: Mapped[str] = mapped_column(String(255), nullable=False)
    event_type: Mapped[str] = mapped_column(String(100), nullable=False)
    event_version: Mapped[str] = mapped_column(String(32), nullable=False)
    occurred_day_index: Mapped[int] = mapped_column(Integer, nullable=False)
    source: Mapped[str] = mapped_column(String(100), nullable=False)
    audience_scope: Mapped[str] = mapped_column(String(16), nullable=False)
    audience_npc_id: Mapped[str | None] = mapped_column(String(100), nullable=True)
    payload_json: Mapped[dict[str, Any]] = mapped_column(JSON, nullable=False)
    created_at_utc: Mapped[datetime] = mapped_column(
        DateTime(timezone=True),
        nullable=False,
        default=utc_now,
    )


class MemoryRecord(Base):
    """从一条游戏事件经确定性模板派生的可检索记忆。"""

    __tablename__ = "memories"
    __table_args__ = (
        UniqueConstraint("memory_id", name="uq_memories_memory_id"),
        UniqueConstraint(
            "save_id",
            "player_id",
            "event_id",
            name="uq_memories_partition_event",
        ),
        CheckConstraint(
            "audience_scope IN ('public', 'npc')",
            name="ck_memories_audience_scope",
        ),
        CheckConstraint(
            "(audience_scope = 'public' AND audience_npc_id IS NULL) OR "
            "(audience_scope = 'npc' AND audience_npc_id IS NOT NULL)",
            name="ck_memories_audience_target",
        ),
        CheckConstraint("importance >= 0 AND importance <= 1", name="ck_memories_importance"),
        CheckConstraint("use_count >= 0", name="ck_memories_use_count"),
        CheckConstraint(
            "classification_status IN ('active', 'quarantined')",
            name="ck_memories_classification_status",
        ),
        CheckConstraint(
            "(subject_namespace IS NULL AND subject_value IS NULL) OR "
            "(subject_namespace IS NOT NULL AND subject_value IS NOT NULL)",
            name="ck_memories_subject_pair",
        ),
        CheckConstraint(
            MEMORY_ACTIVE_CLASSIFICATION_CHECK_SQL,
            name="ck_memories_active_classification_contract",
        ),
        Index(
            "ix_memories_partition_visibility_day",
            "save_id",
            "player_id",
            "audience_scope",
            "audience_npc_id",
            "occurred_day_index",
        ),
        Index(
            "ix_memories_partition_classification_visibility_day",
            "save_id",
            "player_id",
            "classification_status",
            "memory_domain",
            "audience_scope",
            "audience_npc_id",
            "occurred_day_index",
        ),
    )

    id: Mapped[int] = mapped_column(Integer, primary_key=True, autoincrement=True)
    memory_id: Mapped[str] = mapped_column(String(96), nullable=False)
    event_id: Mapped[str] = mapped_column(String(255), nullable=False)
    save_id: Mapped[str] = mapped_column(String(255), nullable=False)
    player_id: Mapped[str] = mapped_column(String(255), nullable=False)
    audience_scope: Mapped[str] = mapped_column(String(16), nullable=False)
    audience_npc_id: Mapped[str | None] = mapped_column(String(100), nullable=True)
    event_type: Mapped[str] = mapped_column(String(100), nullable=False)
    event_version: Mapped[str] = mapped_column(String(32), nullable=False)
    source: Mapped[str] = mapped_column(String(100), nullable=False)
    payload_json: Mapped[dict[str, Any]] = mapped_column(JSON, nullable=False)
    classification_status: Mapped[str] = mapped_column(
        String(16),
        nullable=False,
        default="quarantined",
        server_default="quarantined",
    )
    memory_domain: Mapped[str | None] = mapped_column(String(32), nullable=True)
    memory_kind: Mapped[str | None] = mapped_column(String(64), nullable=True)
    subject_namespace: Mapped[str | None] = mapped_column(String(64), nullable=True)
    subject_value: Mapped[str | None] = mapped_column(String(255), nullable=True)
    summary: Mapped[str] = mapped_column(Text, nullable=False)
    tags_json: Mapped[list[str]] = mapped_column(JSON, nullable=False)
    importance: Mapped[float] = mapped_column(Float, nullable=False)
    occurred_day_index: Mapped[int] = mapped_column(Integer, nullable=False)
    expires_day_index: Mapped[int | None] = mapped_column(Integer, nullable=True)
    last_used_day_index: Mapped[int | None] = mapped_column(Integer, nullable=True)
    use_count: Mapped[int] = mapped_column(Integer, nullable=False, default=0)
    # 关系约束是存储层的硬过滤元数据；空数组表示任意关系阶段可见。
    relationship_stages_json: Mapped[list[str]] = mapped_column(JSON, nullable=False, default=list)
    min_friendship_points: Mapped[int | None] = mapped_column(Integer, nullable=True)
    max_friendship_points: Mapped[int | None] = mapped_column(Integer, nullable=True)
    created_at_utc: Mapped[datetime] = mapped_column(
        DateTime(timezone=True),
        nullable=False,
        default=utc_now,
    )


class MemoryPartitionStateRecord(Base):
    """每个存档/玩家分区的单调记忆版本与提交日水位。"""

    __tablename__ = "memory_partition_states"
    __table_args__ = (
        UniqueConstraint(
            "save_id",
            "player_id",
            name="uq_memory_partition_states_partition",
        ),
        CheckConstraint("memory_revision >= 0", name="ck_memory_partition_revision"),
        # SQLite ``INTEGER`` 只是类型 affinity；1.5 会保留为 REAL，仍可能通过
        # 单纯的范围和配对表达式。``typeof`` 将物理存储类型纳入硬约束。
        CheckConstraint(
            "typeof(memory_revision) = 'integer' "
            "AND memory_revision >= 0 AND memory_revision <= 2147483647",
            name="ck_memory_partition_revision_wire_bounds",
        ),
        CheckConstraint(
            "typeof(retrieval_state_revision) = 'integer' "
            "AND retrieval_state_revision >= 0 "
            "AND retrieval_state_revision <= 2147483647",
            name="ck_memory_partition_retrieval_revision_wire_bounds",
        ),
        CheckConstraint(
            "typeof(committed_through_day_index) = 'integer' "
            "AND committed_through_day_index >= -1 "
            "AND committed_through_day_index <= 2147483647",
            name="ck_memory_partition_committed_day_wire_bounds",
        ),
        CheckConstraint(
            "(memory_revision = 0 AND committed_through_day_index = -1) OR "
            "(memory_revision > 0 AND committed_through_day_index >= 0)",
            name="ck_memory_partition_state_consistency",
        ),
    )

    id: Mapped[int] = mapped_column(Integer, primary_key=True, autoincrement=True)
    save_id: Mapped[str] = mapped_column(String(255), nullable=False)
    player_id: Mapped[str] = mapped_column(String(255), nullable=False)
    memory_revision: Mapped[int] = mapped_column(Integer, nullable=False, default=0)
    retrieval_state_revision: Mapped[int] = mapped_column(
        Integer,
        nullable=False,
        default=0,
        server_default="0",
    )
    committed_through_day_index: Mapped[int] = mapped_column(Integer, nullable=False, default=-1)
    updated_at_utc: Mapped[datetime] = mapped_column(
        DateTime(timezone=True),
        nullable=False,
        default=utc_now,
        onupdate=utc_now,
    )


class DialogueGenerationRecord(Base):
    """单条台词生成的幂等、Guard 和证据审计记录。"""

    __tablename__ = "dialogue_generations"
    __table_args__ = (
        UniqueConstraint("generation_id", name="uq_dialogue_generations_generation_id"),
        UniqueConstraint("generation_key", name="uq_dialogue_generations_generation_key"),
        CheckConstraint(
            "status IN ('generated', 'passthrough', 'skipped', 'failed')",
            name="ck_dialogue_generations_status",
        ),
        CheckConstraint(
            "memory_cooldown_days >= 0",
            name="ck_dialogue_generations_cooldown",
        ),
        CheckConstraint(
            DIALOGUE_GENERATION_EVIDENCE_ARRAY_CHECK_SQL,
            name="ck_dialogue_generations_evidence_array",
        ),
        CheckConstraint(
            DIALOGUE_GENERATION_RESULT_AUTHORIZATION_CHECK_SQL,
            name="ck_dialogue_generations_result_authorization",
        ),
        Index(
            "ix_dialogue_generations_partition_day_npc",
            "save_id",
            "player_id",
            "game_day_index",
            "npc_id",
        ),
    )

    id: Mapped[int] = mapped_column(Integer, primary_key=True, autoincrement=True)
    generation_id: Mapped[str] = mapped_column(String(255), nullable=False)
    generation_key: Mapped[str] = mapped_column(String(255), nullable=False)
    save_id: Mapped[str] = mapped_column(String(255), nullable=False)
    player_id: Mapped[str] = mapped_column(String(255), nullable=False)
    game_day_index: Mapped[int] = mapped_column(Integer, nullable=False)
    npc_id: Mapped[str] = mapped_column(String(100), nullable=False)
    locale: Mapped[str] = mapped_column(String(32), nullable=False)
    source_hash: Mapped[str] = mapped_column(String(255), nullable=False)
    relationship_stage: Mapped[str] = mapped_column(String(100), nullable=False)
    friendship_points: Mapped[int] = mapped_column(Integer, nullable=False)
    memory_cooldown_days: Mapped[int] = mapped_column(Integer, nullable=False)
    status: Mapped[str] = mapped_column(String(16), nullable=False)
    result_text: Mapped[str | None] = mapped_column(Text, nullable=True)
    reason_code: Mapped[str] = mapped_column(String(100), nullable=False)
    evidence_ids_json: Mapped[list[str]] = mapped_column(JSON, nullable=False, default=list)
    trace_id: Mapped[str] = mapped_column(String(255), nullable=False)
    guard_passed: Mapped[bool] = mapped_column(Boolean, nullable=False, default=False)
    evidence_authorized: Mapped[bool] = mapped_column(Boolean, nullable=False, default=False)
    # 后续 Agent/Guard 阶段会填充这些审计列；可空 JSON 避免为同一生成另造平行表。
    input_versions_json: Mapped[dict[str, Any] | None] = mapped_column(JSON, nullable=True)
    trace_json: Mapped[dict[str, Any] | None] = mapped_column(JSON, nullable=True)
    usage_json: Mapped[dict[str, Any] | None] = mapped_column(JSON, nullable=True)
    guard_report_json: Mapped[dict[str, Any] | None] = mapped_column(JSON, nullable=True)
    created_at_utc: Mapped[datetime] = mapped_column(
        DateTime(timezone=True),
        nullable=False,
        default=utc_now,
    )
    updated_at_utc: Mapped[datetime] = mapped_column(
        DateTime(timezone=True),
        nullable=False,
        default=utc_now,
        onupdate=utc_now,
    )


class DialogueDisplayReceiptRecord(Base):
    """游戏确认 generated 文本已实际展示的幂等回执。"""

    __tablename__ = "dialogue_display_receipts"
    __table_args__ = (
        UniqueConstraint(
            "display_receipt_id",
            name="uq_dialogue_display_receipts_receipt_id",
        ),
        Index(
            "ix_dialogue_display_receipts_partition_day",
            "save_id",
            "player_id",
            "displayed_day_index",
        ),
    )

    id: Mapped[int] = mapped_column(Integer, primary_key=True, autoincrement=True)
    display_receipt_id: Mapped[str] = mapped_column(String(255), nullable=False)
    generation_id: Mapped[str] = mapped_column(
        String(255),
        ForeignKey("dialogue_generations.generation_id", ondelete="RESTRICT"),
        nullable=False,
    )
    save_id: Mapped[str] = mapped_column(String(255), nullable=False)
    player_id: Mapped[str] = mapped_column(String(255), nullable=False)
    displayed_day_index: Mapped[int] = mapped_column(Integer, nullable=False)
    npc_id: Mapped[str] = mapped_column(String(100), nullable=False)
    source_hash: Mapped[str] = mapped_column(String(255), nullable=False)
    created_at_utc: Mapped[datetime] = mapped_column(
        DateTime(timezone=True),
        nullable=False,
        default=utc_now,
    )
