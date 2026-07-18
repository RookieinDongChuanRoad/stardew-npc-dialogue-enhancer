"""为分区记忆水位增加物理 integer、Int32 边界与状态一致性约束。

Revision ID: 20260713_0002
Revises: 20260713_0001
Create Date: 2026-07-13

0001 已经发布，因此本 revision 只追加约束，不改写历史 migration。SQLite
不能直接 ALTER TABLE ADD CHECK，Alembic batch 会以受约束临时表重建原表；
约束用 ``typeof(...) = 'integer'`` 阻止 1.5 借 INTEGER affinity 以 REAL 落盘。
若 0001 数据中已存在腐化水位，迁移会在创建 batch 临时表前只读预检失败，
保留旧 revision，并要求运维明确修复数据后重试；迁移不得猜测应该删除还是
改写业务水位。
"""

from __future__ import annotations

from collections.abc import Sequence

import sqlalchemy as sa
from alembic import op

revision: str = "20260713_0002"
down_revision: str | None = "20260713_0001"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None


def upgrade() -> None:
    """重建分区水位表，并增加范围与 revision/day 配对不变量。"""

    # SQLite batch 在复制旧数据触发 CHECK 失败时，会留下内部临时表，导致运维
    # 修复原行后仍无法重试 migration。必须在 Alembic 创建临时表之前只读预检；
    # 发现腐化时稳定失败，不猜测应删除或改写哪个业务水位。
    invalid_state = (
        op.get_bind()
        .execute(
            sa.text(
                "SELECT 1 FROM memory_partition_states "
                "WHERE NOT ("
                "typeof(memory_revision) = 'integer' "
                "AND memory_revision >= 0 AND memory_revision <= 2147483647 "
                "AND typeof(committed_through_day_index) = 'integer' "
                "AND committed_through_day_index >= -1 "
                "AND committed_through_day_index <= 2147483647 "
                "AND ((memory_revision = 0 AND committed_through_day_index = -1) "
                "OR (memory_revision > 0 AND committed_through_day_index >= 0))"
                ") LIMIT 1"
            )
        )
        .first()
    )
    if invalid_state is not None:
        raise RuntimeError("memory partition state invalid; repair data before retry")

    with op.batch_alter_table(
        "memory_partition_states",
        recreate="always",
    ) as batch_op:
        batch_op.create_check_constraint(
            "ck_memory_partition_revision_wire_bounds",
            "typeof(memory_revision) = 'integer' "
            "AND memory_revision >= 0 AND memory_revision <= 2147483647",
        )
        batch_op.create_check_constraint(
            "ck_memory_partition_committed_day_wire_bounds",
            "typeof(committed_through_day_index) = 'integer' "
            "AND committed_through_day_index >= -1 "
            "AND committed_through_day_index <= 2147483647",
        )
        batch_op.create_check_constraint(
            "ck_memory_partition_state_consistency",
            "(memory_revision = 0 AND committed_through_day_index = -1) OR "
            "(memory_revision > 0 AND committed_through_day_index >= 0)",
        )


def downgrade() -> None:
    """只移除 0002 新增约束，恢复 0001 的历史 Schema 能力。"""

    with op.batch_alter_table(
        "memory_partition_states",
        recreate="always",
    ) as batch_op:
        batch_op.drop_constraint(
            "ck_memory_partition_state_consistency",
            type_="check",
        )
        batch_op.drop_constraint(
            "ck_memory_partition_committed_day_wire_bounds",
            type_="check",
        )
        batch_op.drop_constraint(
            "ck_memory_partition_revision_wire_bounds",
            type_="check",
        )
