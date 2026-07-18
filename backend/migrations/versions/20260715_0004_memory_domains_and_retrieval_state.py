"""增加严格 memory 分类与候选状态 revision。

Revision ID: 20260715_0004
Revises: 20260715_0003
Create Date: 2026-07-15

迁移只依据历史行的 raw event type/version/source/audience/payload 分类。能逐字命中
首批 producer 或唯一 legacy skill alias 的行标为 active；其余行保留全部原始数据
并标为 quarantined。摘要和 tags 不参与分类，也不会被本 revision 改写。
"""

from __future__ import annotations

import json
import re
from collections.abc import Mapping, Sequence
from typing import Any

import sqlalchemy as sa
from alembic import op

revision: str = "20260715_0004"
down_revision: str | None = "20260715_0003"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None

_FIVE_SKILLS = frozenset({"farming", "fishing", "foraging", "mining", "combat"})
_RELATIONSHIP_TRANSITIONS = frozenset(
    {
        ("friendly", "dating"),
        ("dating", "engaged"),
        ("engaged", "married"),
        ("dating", "friendly"),
        ("engaged", "friendly"),
        ("married", "divorced"),
    }
)
_TOOLS = frozenset({"axe", "pickaxe", "hoe", "watering_can", "pan", "trash_can"})
_FACILITY_BY_MILESTONE = {
    "public_facility_greenhouse_restored": "greenhouse",
    "public_facility_minecarts_restored": "minecarts",
    "public_facility_bus_service_restored": "bus_service",
    "public_facility_quarry_bridge_restored": "quarry_bridge",
    "public_facility_glittering_boulder_removed": "glittering_boulder",
}
_GIFT_TASTES = frozenset({"love", "like", "neutral", "dislike", "hate", "stardrop_tea"})
_LEGACY_SKILL_PATTERN = re.compile(
    r"^skill_(?P<skill_id>[a-z][a-z0-9_]*)_level_(?P<level>[1-9][0-9]*)$"
)
_WIRE_INTEGER_MAX = 2_147_483_647

_PYTHON_STRIP_SQLITE_CHARACTERS_SQL = (
    "char(9,10,11,12,13,28,29,30,31,32,133,160,5760,"
    "8192,8193,8194,8195,8196,8197,8198,8199,8200,8201,8202,"
    "8232,8233,8239,8287,12288)"
)
_ACTIVE_CLASSIFICATION_CHECK_SQL = (
    "classification_status = 'quarantined' OR (classification_status = 'active' AND ("
    "(memory_domain = 'npc_history' AND audience_scope = 'npc' "
    "AND audience_npc_id IS NOT NULL AND ("
    "(memory_kind = 'gift_given' AND subject_namespace = 'item_id' "
    "AND subject_value IS NOT NULL AND length(subject_value) > 0 "
    "AND instr(subject_value, char(0)) = 0 "
    "AND substr(subject_value, 1, 1) = '(' "
    "AND instr(subject_value, ')') > 2 "
    "AND instr(subject_value, ')') < length(subject_value) "
    f"AND trim(subject_value, {_PYTHON_STRIP_SQLITE_CHARACTERS_SQL}) = subject_value) OR "
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

_Classification = tuple[str, str | None, str | None, str | None, str | None]


def upgrade() -> None:
    """先完成只读分类，再增加列、回填与约束，避免中途发现无法解释的数据。"""

    bind = op.get_bind()
    rows = (
        bind.execute(
            sa.text(
                "SELECT id,event_type,event_version,source,audience_scope,"
                "audience_npc_id,payload_json FROM memories ORDER BY id"
            )
        )
        .mappings()
        .all()
    )
    updates = [{"id": int(row["id"]), "classification": _classify_legacy_row(row)} for row in rows]

    op.add_column(
        "memories",
        sa.Column(
            "classification_status",
            sa.String(length=16),
            nullable=False,
            server_default="quarantined",
        ),
    )
    op.add_column("memories", sa.Column("memory_domain", sa.String(length=32), nullable=True))
    op.add_column("memories", sa.Column("memory_kind", sa.String(length=64), nullable=True))
    op.add_column(
        "memories",
        sa.Column("subject_namespace", sa.String(length=64), nullable=True),
    )
    op.add_column("memories", sa.Column("subject_value", sa.String(length=255), nullable=True))

    for update in updates:
        status, domain, kind, namespace, subject = update["classification"]
        bind.execute(
            sa.text(
                "UPDATE memories SET classification_status=:status,memory_domain=:domain,"
                "memory_kind=:kind,subject_namespace=:namespace,subject_value=:subject "
                "WHERE id=:id"
            ),
            {
                "id": update["id"],
                "status": status,
                "domain": domain,
                "kind": kind,
                "namespace": namespace,
                "subject": subject,
            },
        )

    with op.batch_alter_table("memories") as batch_op:
        batch_op.create_check_constraint(
            "ck_memories_classification_status",
            "classification_status IN ('active', 'quarantined')",
        )
        batch_op.create_check_constraint(
            "ck_memories_subject_pair",
            "(subject_namespace IS NULL AND subject_value IS NULL) OR "
            "(subject_namespace IS NOT NULL AND subject_value IS NOT NULL)",
        )
        batch_op.create_check_constraint(
            "ck_memories_active_classification_contract",
            _ACTIVE_CLASSIFICATION_CHECK_SQL,
        )

    op.create_index(
        "ix_memories_partition_classification_visibility_day",
        "memories",
        [
            "save_id",
            "player_id",
            "classification_status",
            "memory_domain",
            "audience_scope",
            "audience_npc_id",
            "occurred_day_index",
        ],
        unique=False,
    )

    op.add_column(
        "memory_partition_states",
        sa.Column(
            "retrieval_state_revision",
            sa.Integer(),
            nullable=False,
            server_default="0",
        ),
    )
    with op.batch_alter_table("memory_partition_states") as batch_op:
        batch_op.create_check_constraint(
            "ck_memory_partition_retrieval_revision_wire_bounds",
            "typeof(retrieval_state_revision) = 'integer' "
            "AND retrieval_state_revision >= 0 "
            "AND retrieval_state_revision <= 2147483647",
        )


def downgrade() -> None:
    """移除派生分类与候选水位，保留所有原始 event/memory/使用状态。"""

    with op.batch_alter_table("memory_partition_states") as batch_op:
        batch_op.drop_constraint(
            "ck_memory_partition_retrieval_revision_wire_bounds",
            type_="check",
        )
        batch_op.drop_column("retrieval_state_revision")

    op.drop_index(
        "ix_memories_partition_classification_visibility_day",
        table_name="memories",
    )
    with op.batch_alter_table("memories") as batch_op:
        batch_op.drop_constraint("ck_memories_active_classification_contract", type_="check")
        batch_op.drop_constraint("ck_memories_subject_pair", type_="check")
        batch_op.drop_constraint("ck_memories_classification_status", type_="check")
        batch_op.drop_column("subject_value")
        batch_op.drop_column("subject_namespace")
        batch_op.drop_column("memory_kind")
        batch_op.drop_column("memory_domain")
        batch_op.drop_column("classification_status")


def _classify_legacy_row(row: Mapping[str, Any]) -> _Classification:
    """按首批 raw producer 矩阵分类；任一不确定点都返回 quarantine。"""

    payload = _decode_payload(row.get("payload_json"))
    if payload is None:
        return _quarantined()

    event_type = row.get("event_type")
    event_version = row.get("event_version")
    source = row.get("source")
    audience_scope = row.get("audience_scope")
    audience_npc_id = row.get("audience_npc_id")
    is_npc = audience_scope == "npc" and isinstance(audience_npc_id, str)
    is_public = audience_scope == "public" and audience_npc_id is None

    if (
        event_type == "gift_given"
        and event_version == "2"
        and source == "harmony.farmer.on_gift_given"
        and is_npc
        and set(payload) == {"item_id", "taste"}
    ):
        item_id = payload.get("item_id")
        taste = payload.get("taste")
        if _is_qualified_item_id(item_id) and taste in _GIFT_TASTES:
            return _active("npc_history", "gift_given", "item_id", item_id)

    if (
        event_type == "relationship_status_changed"
        and event_version == "1"
        and source == "smapi.player.friendship_snapshot"
        and is_npc
        and set(payload) == {"old_status", "new_status"}
    ):
        old_status = payload.get("old_status")
        new_status = payload.get("new_status")
        if isinstance(old_status, str) and isinstance(new_status, str):
            if (old_status, new_status) in _RELATIONSHIP_TRANSITIONS:
                return _active(
                    "npc_history",
                    "relationship_status_changed",
                    "relationship_status",
                    new_status,
                )

    if (
        event_type == "friendship_milestone_reached"
        and event_version == "1"
        and source == "smapi.player.friendship_snapshot"
        and is_npc
        and set(payload) == {"milestone_id", "threshold_points"}
        and payload.get("milestone_id") == "friend"
        and _is_strict_integer(payload.get("threshold_points"), expected=1000)
    ):
        return _active(
            "npc_history",
            "friendship_milestone_reached",
            "milestone_id",
            "friend",
        )

    if (
        event_type == "skill_level_reached"
        and event_version == "1"
        and source == "smapi.player.level_changed"
        and is_public
    ):
        skill = _classify_new_skill(payload)
        if skill is not None:
            return _active(
                "player_progression",
                "skill_level_reached",
                "skill_id",
                skill,
            )

    if (
        event_type == "world_progression"
        and event_version == "1"
        and source == "smapi.player.level_changed"
        and is_public
    ):
        skill = _classify_legacy_skill(payload)
        if skill is not None:
            return _active(
                "player_progression",
                "skill_level_reached",
                "skill_id",
                skill,
            )

    if (
        event_type == "mine_depth_milestone_reached"
        and event_version == "1"
        and source == "smapi.player.warped"
        and is_public
    ):
        mine_id = _classify_mine(payload)
        if mine_id is not None:
            return _active(
                "player_progression",
                "mine_depth_milestone_reached",
                "mine_id",
                mine_id,
            )

    if (
        event_type == "tool_upgrade_received"
        and event_version == "1"
        and source == "smapi.player.tool_upgrade_observed"
        and is_public
        and set(payload) == {"tool_id", "upgrade_level"}
    ):
        tool_id = payload.get("tool_id")
        upgrade_level = payload.get("upgrade_level")
        if tool_id in _TOOLS and _is_strict_integer_between(upgrade_level, 1, 4):
            return _active(
                "player_progression",
                "tool_upgrade_received",
                "tool_id",
                str(tool_id),
            )

    if (
        event_type == "mastery_claimed"
        and event_version == "1"
        and source == "smapi.player.mastery_snapshot"
        and is_public
        and set(payload) == {"skill_id"}
        and payload.get("skill_id") in _FIVE_SKILLS
    ):
        return _active(
            "player_progression",
            "mastery_claimed",
            "skill_id",
            str(payload["skill_id"]),
        )

    if (
        event_type == "world_progression"
        and event_version == "1"
        and source == "smapi.world.public_facility_restored"
        and is_public
        and set(payload) == {"milestone"}
    ):
        facility_id = _FACILITY_BY_MILESTONE.get(payload.get("milestone"))
        if facility_id is not None:
            return _active(
                "world_progression",
                "public_facility_restored",
                "facility_id",
                facility_id,
            )

    return _quarantined()


def _classify_new_skill(payload: dict[str, Any]) -> str | None:
    """识别新三字段五技能升级合同。"""

    if set(payload) != {"skill_id", "old_level", "new_level"}:
        return None
    skill_id = payload.get("skill_id")
    old_level = payload.get("old_level")
    new_level = payload.get("new_level")
    if skill_id not in _FIVE_SKILLS:
        return None
    if not _is_strict_integer_between(old_level, 0, 10):
        return None
    if not _is_strict_integer_between(new_level, 0, 10) or new_level <= old_level:
        return None
    return str(skill_id)


def _classify_legacy_skill(payload: dict[str, Any]) -> str | None:
    """识别唯一 legacy milestone；未知/luck/0/前导零/11+ 都 quarantine。"""

    if set(payload) != {"milestone"}:
        return None
    milestone = payload.get("milestone")
    if not isinstance(milestone, str):
        return None
    match = _LEGACY_SKILL_PATTERN.fullmatch(milestone)
    if match is None:
        return None
    skill_id = match.group("skill_id")
    level = int(match.group("level"))
    return skill_id if skill_id in _FIVE_SKILLS and 1 <= level <= 10 else None


def _classify_mine(payload: dict[str, Any]) -> str | None:
    """复核矿区、展示里程碑与 observed depth 的可生产组合。"""

    if set(payload) != {"mine_id", "milestone_depth", "observed_depth"}:
        return None
    mine_id = payload.get("mine_id")
    milestone = payload.get("milestone_depth")
    observed = payload.get("observed_depth")
    if not _is_strict_integer_between(milestone, 0, _WIRE_INTEGER_MAX):
        return None
    if not _is_strict_integer_between(observed, 0, _WIRE_INTEGER_MAX):
        return None
    if mine_id == "the_mines":
        return "the_mines" if 5 <= milestone <= observed <= 120 and milestone % 5 == 0 else None
    if mine_id == "skull_cavern":
        is_milestone = milestone in {25, 50} or (milestone >= 100 and milestone % 100 == 0)
        return "skull_cavern" if is_milestone and observed >= milestone else None
    return None


def _decode_payload(value: object) -> dict[str, Any] | None:
    """兼容 driver 已解码值与 SQLite JSON text；错误只导致 quarantine。"""

    if isinstance(value, str):
        try:
            value = json.loads(value)
        except (TypeError, json.JSONDecodeError):
            return None
    if not isinstance(value, dict) or not all(isinstance(key, str) for key in value):
        return None
    return value


def _is_qualified_item_id(value: object) -> bool:
    """匹配 subject VARCHAR 边界及 ``(<type>)<item-id>`` 公开形状。"""

    if not (
        isinstance(value, str)
        and bool(value)
        and value == value.strip()
        and "\x00" not in value
        and len(value) <= 255
    ):
        return False
    closing_parenthesis = value.find(")")
    return value.startswith("(") and 1 < closing_parenthesis < len(value) - 1


def _is_strict_integer(value: object, *, expected: int) -> bool:
    """拒绝 Python/JSON bool 冒充 integer。"""

    return isinstance(value, int) and not isinstance(value, bool) and value == expected


def _is_strict_integer_between(value: object, minimum: int, maximum: int) -> bool:
    """判断非 bool integer 是否落在闭区间。"""

    return isinstance(value, int) and not isinstance(value, bool) and minimum <= value <= maximum


def _active(domain: str, kind: str, namespace: str, subject: str) -> _Classification:
    """构造 active 分类 tuple，集中固定字段顺序。"""

    return ("active", domain, kind, namespace, subject)


def _quarantined() -> _Classification:
    """未知历史事实只隔离分类，不删除或改写原始行。"""

    return ("quarantined", None, None, None, None)
