"""建立 Phase 3 事件、记忆、生成与展示回执持久化。

Revision ID: 20260713_0001
Revises: None
Create Date: 2026-07-13
"""

from __future__ import annotations

from collections.abc import Sequence

import sqlalchemy as sa
from alembic import op

revision: str = "20260713_0001"
down_revision: str | None = None
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None

# 这是 migration 的历史 Schema 表达式，不从运行时 ORM 导入。字符集合与创建该
# revision 时的 Python 3.11 ``str.strip()`` 保持等价，使未来模型常量变化不会
# 静默重写已经发布的 migration 历史。
_PYTHON_STRIP_SQLITE_CHARACTERS_SQL = (
    "char(9,10,11,12,13,28,29,30,31,32,133,160,5760,"
    "8192,8193,8194,8195,8196,8197,8198,8199,8200,8201,8202,"
    "8232,8233,8239,8287,12288)"
)
_DIALOGUE_GENERATION_EVIDENCE_ARRAY_CHECK_SQL = (
    "json_valid(evidence_ids_json) = 1 AND json_type(evidence_ids_json) = 'array'"
)
_DIALOGUE_GENERATION_RESULT_AUTHORIZATION_CHECK_SQL = (
    "(status = 'generated' AND result_text IS NOT NULL "
    "AND length(result_text) > 0 "
    "AND instr(result_text, char(0)) = 0 "
    f"AND trim(result_text, {_PYTHON_STRIP_SQLITE_CHARACTERS_SQL}) = result_text "
    "AND guard_passed = 1 AND evidence_authorized = 1) OR "
    "(status != 'generated' AND result_text IS NULL AND evidence_authorized = 0 "
    "AND json_array_length(evidence_ids_json) = 0)"
)


def upgrade() -> None:
    """从空库建立 MVP 的四类核心表与分区水位表。"""

    op.create_table(
        "game_events",
        sa.Column("id", sa.Integer(), autoincrement=True, nullable=False),
        sa.Column("save_id", sa.String(length=255), nullable=False),
        sa.Column("player_id", sa.String(length=255), nullable=False),
        sa.Column("event_id", sa.String(length=255), nullable=False),
        sa.Column("event_type", sa.String(length=100), nullable=False),
        sa.Column("event_version", sa.String(length=32), nullable=False),
        sa.Column("occurred_day_index", sa.Integer(), nullable=False),
        sa.Column("source", sa.String(length=100), nullable=False),
        sa.Column("audience_scope", sa.String(length=16), nullable=False),
        sa.Column("audience_npc_id", sa.String(length=100), nullable=True),
        sa.Column("payload_json", sa.JSON(), nullable=False),
        sa.Column("created_at_utc", sa.DateTime(timezone=True), nullable=False),
        sa.CheckConstraint(
            "audience_scope IN ('public', 'npc')",
            name="ck_game_events_audience_scope",
        ),
        sa.CheckConstraint(
            "(audience_scope = 'public' AND audience_npc_id IS NULL) OR "
            "(audience_scope = 'npc' AND audience_npc_id IS NOT NULL)",
            name="ck_game_events_audience_target",
        ),
        sa.PrimaryKeyConstraint("id"),
        sa.UniqueConstraint(
            "save_id",
            "player_id",
            "event_id",
            name="uq_game_events_partition_event",
        ),
    )
    op.create_index(
        "ix_game_events_partition_day",
        "game_events",
        ["save_id", "player_id", "occurred_day_index"],
        unique=False,
    )

    op.create_table(
        "memories",
        sa.Column("id", sa.Integer(), autoincrement=True, nullable=False),
        sa.Column("memory_id", sa.String(length=96), nullable=False),
        sa.Column("event_id", sa.String(length=255), nullable=False),
        sa.Column("save_id", sa.String(length=255), nullable=False),
        sa.Column("player_id", sa.String(length=255), nullable=False),
        sa.Column("audience_scope", sa.String(length=16), nullable=False),
        sa.Column("audience_npc_id", sa.String(length=100), nullable=True),
        sa.Column("event_type", sa.String(length=100), nullable=False),
        sa.Column("event_version", sa.String(length=32), nullable=False),
        sa.Column("source", sa.String(length=100), nullable=False),
        sa.Column("payload_json", sa.JSON(), nullable=False),
        sa.Column("summary", sa.Text(), nullable=False),
        sa.Column("tags_json", sa.JSON(), nullable=False),
        sa.Column("importance", sa.Float(), nullable=False),
        sa.Column("occurred_day_index", sa.Integer(), nullable=False),
        sa.Column("expires_day_index", sa.Integer(), nullable=True),
        sa.Column("last_used_day_index", sa.Integer(), nullable=True),
        sa.Column("use_count", sa.Integer(), nullable=False),
        sa.Column("relationship_stages_json", sa.JSON(), nullable=False),
        sa.Column("min_friendship_points", sa.Integer(), nullable=True),
        sa.Column("max_friendship_points", sa.Integer(), nullable=True),
        sa.Column("created_at_utc", sa.DateTime(timezone=True), nullable=False),
        sa.CheckConstraint(
            "audience_scope IN ('public', 'npc')",
            name="ck_memories_audience_scope",
        ),
        sa.CheckConstraint(
            "(audience_scope = 'public' AND audience_npc_id IS NULL) OR "
            "(audience_scope = 'npc' AND audience_npc_id IS NOT NULL)",
            name="ck_memories_audience_target",
        ),
        sa.CheckConstraint(
            "importance >= 0 AND importance <= 1",
            name="ck_memories_importance",
        ),
        sa.CheckConstraint("use_count >= 0", name="ck_memories_use_count"),
        sa.PrimaryKeyConstraint("id"),
        sa.UniqueConstraint("memory_id", name="uq_memories_memory_id"),
        sa.UniqueConstraint(
            "save_id",
            "player_id",
            "event_id",
            name="uq_memories_partition_event",
        ),
    )
    op.create_index(
        "ix_memories_partition_visibility_day",
        "memories",
        [
            "save_id",
            "player_id",
            "audience_scope",
            "audience_npc_id",
            "occurred_day_index",
        ],
        unique=False,
    )

    op.create_table(
        "memory_partition_states",
        sa.Column("id", sa.Integer(), autoincrement=True, nullable=False),
        sa.Column("save_id", sa.String(length=255), nullable=False),
        sa.Column("player_id", sa.String(length=255), nullable=False),
        sa.Column("memory_revision", sa.Integer(), nullable=False),
        sa.Column("committed_through_day_index", sa.Integer(), nullable=False),
        sa.Column("updated_at_utc", sa.DateTime(timezone=True), nullable=False),
        sa.CheckConstraint(
            "memory_revision >= 0",
            name="ck_memory_partition_revision",
        ),
        sa.PrimaryKeyConstraint("id"),
        sa.UniqueConstraint(
            "save_id",
            "player_id",
            name="uq_memory_partition_states_partition",
        ),
    )

    op.create_table(
        "dialogue_generations",
        sa.Column("id", sa.Integer(), autoincrement=True, nullable=False),
        sa.Column("generation_id", sa.String(length=255), nullable=False),
        sa.Column("generation_key", sa.String(length=255), nullable=False),
        sa.Column("save_id", sa.String(length=255), nullable=False),
        sa.Column("player_id", sa.String(length=255), nullable=False),
        sa.Column("game_day_index", sa.Integer(), nullable=False),
        sa.Column("npc_id", sa.String(length=100), nullable=False),
        sa.Column("locale", sa.String(length=32), nullable=False),
        sa.Column("source_hash", sa.String(length=255), nullable=False),
        sa.Column("relationship_stage", sa.String(length=100), nullable=False),
        sa.Column("friendship_points", sa.Integer(), nullable=False),
        sa.Column("memory_cooldown_days", sa.Integer(), nullable=False),
        sa.Column("status", sa.String(length=16), nullable=False),
        sa.Column("result_text", sa.Text(), nullable=True),
        sa.Column("reason_code", sa.String(length=100), nullable=False),
        sa.Column("evidence_ids_json", sa.JSON(), nullable=False),
        sa.Column("trace_id", sa.String(length=255), nullable=False),
        sa.Column("guard_passed", sa.Boolean(), nullable=False),
        sa.Column("evidence_authorized", sa.Boolean(), nullable=False),
        sa.Column("input_versions_json", sa.JSON(), nullable=True),
        sa.Column("trace_json", sa.JSON(), nullable=True),
        sa.Column("usage_json", sa.JSON(), nullable=True),
        sa.Column("guard_report_json", sa.JSON(), nullable=True),
        sa.Column("created_at_utc", sa.DateTime(timezone=True), nullable=False),
        sa.Column("updated_at_utc", sa.DateTime(timezone=True), nullable=False),
        sa.CheckConstraint(
            "status IN ('generated', 'passthrough', 'skipped', 'failed')",
            name="ck_dialogue_generations_status",
        ),
        sa.CheckConstraint(
            "memory_cooldown_days >= 0",
            name="ck_dialogue_generations_cooldown",
        ),
        sa.CheckConstraint(
            _DIALOGUE_GENERATION_EVIDENCE_ARRAY_CHECK_SQL,
            name="ck_dialogue_generations_evidence_array",
        ),
        sa.CheckConstraint(
            _DIALOGUE_GENERATION_RESULT_AUTHORIZATION_CHECK_SQL,
            name="ck_dialogue_generations_result_authorization",
        ),
        sa.PrimaryKeyConstraint("id"),
        sa.UniqueConstraint(
            "generation_id",
            name="uq_dialogue_generations_generation_id",
        ),
        sa.UniqueConstraint(
            "generation_key",
            name="uq_dialogue_generations_generation_key",
        ),
    )
    op.create_index(
        "ix_dialogue_generations_partition_day_npc",
        "dialogue_generations",
        ["save_id", "player_id", "game_day_index", "npc_id"],
        unique=False,
    )

    op.create_table(
        "dialogue_display_receipts",
        sa.Column("id", sa.Integer(), autoincrement=True, nullable=False),
        sa.Column("display_receipt_id", sa.String(length=255), nullable=False),
        sa.Column("generation_id", sa.String(length=255), nullable=False),
        sa.Column("save_id", sa.String(length=255), nullable=False),
        sa.Column("player_id", sa.String(length=255), nullable=False),
        sa.Column("displayed_day_index", sa.Integer(), nullable=False),
        sa.Column("npc_id", sa.String(length=100), nullable=False),
        sa.Column("source_hash", sa.String(length=255), nullable=False),
        sa.Column("created_at_utc", sa.DateTime(timezone=True), nullable=False),
        sa.ForeignKeyConstraint(
            ["generation_id"],
            ["dialogue_generations.generation_id"],
            name="fk_dialogue_display_receipts_generation_id",
            ondelete="RESTRICT",
        ),
        sa.PrimaryKeyConstraint("id"),
        sa.UniqueConstraint(
            "display_receipt_id",
            name="uq_dialogue_display_receipts_receipt_id",
        ),
    )
    op.create_index(
        "ix_dialogue_display_receipts_partition_day",
        "dialogue_display_receipts",
        ["save_id", "player_id", "displayed_day_index"],
        unique=False,
    )


def downgrade() -> None:
    """按外键依赖的逆序删除 Phase 3 持久化对象。"""

    op.drop_index(
        "ix_dialogue_display_receipts_partition_day",
        table_name="dialogue_display_receipts",
    )
    op.drop_table("dialogue_display_receipts")
    op.drop_index(
        "ix_dialogue_generations_partition_day_npc",
        table_name="dialogue_generations",
    )
    op.drop_table("dialogue_generations")
    op.drop_table("memory_partition_states")
    op.drop_index("ix_memories_partition_visibility_day", table_name="memories")
    op.drop_table("memories")
    op.drop_index("ix_game_events_partition_day", table_name="game_events")
    op.drop_table("game_events")
