"""把既有 SMAPI LevelChanged memory backfill 为带玩家主语的技能事实。

Revision ID: 20260715_0003
Revises: 20260713_0002
Create Date: 2026-07-15

0002 已经发布，且真实存档中已经存在由 ``smapi.player.level_changed`` 产生的
通用世界进度摘要。本 revision 只修正这些历史 memory 的派生文本与检索标签；
事件本身、memory ID、发生日期、使用状态、生成审计和分区 revision 都不改变。

迁移刻意自包含历史常量，不导入运行时投影代码。这样未来运行时词汇或标签再次
升级时，已经发布的 migration 仍保持可重放、可审计的固定语义。
"""

from __future__ import annotations

import json
import re
from collections.abc import Sequence

import sqlalchemy as sa
from alembic import op

revision: str = "20260715_0003"
down_revision: str | None = "20260713_0002"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None

_PLAYER_LEVEL_CHANGED_SOURCE = "smapi.player.level_changed"
_PLAYER_SKILL_MILESTONE_PATTERN = re.compile(
    r"^skill_(?P<skill_id>[a-z][a-z0-9_]*)_level_(?P<level>[1-9][0-9]*)$"
)
_PLAYER_SKILL_DISPLAY_NAMES = {
    "farming": "耕种",
    "fishing": "钓鱼",
    "foraging": "采集",
    "mining": "采矿",
    "combat": "战斗",
    "luck": "幸运",
}
_WIRE_INTEGER_MAX = 2_147_483_647


def upgrade() -> None:
    """验证并回填全部旧玩家技能投影，不改变事件同步水位。

    先构造完整 ``updates`` 再执行任何 DML，避免目标数据中后部存在异常时留下
    半迁移摘要。每一行只允许处于两种状态：0002 的逐字旧投影，或本 revision
    的逐字新投影。其他人工编辑或未知 producer 形状必须由运维确认，migration
    不能猜测如何覆盖。
    """

    bind = op.get_bind()
    rows = (
        bind.execute(
            sa.text(
                "SELECT id,occurred_day_index,payload_json,summary,tags_json "
                "FROM memories "
                "WHERE event_type='world_progression' AND source=:source "
                "ORDER BY id"
            ),
            {"source": _PLAYER_LEVEL_CHANGED_SOURCE},
        )
        .mappings()
        .all()
    )

    updates: list[dict[str, object]] = []
    for row in rows:
        payload = _decode_json_object(row["payload_json"], "payload_json")
        milestone = payload.get("milestone")
        if not isinstance(milestone, str) or set(payload) != {"milestone"}:
            raise RuntimeError(
                "legacy player skill memory payload invalid; repair data before retry"
            )

        milestone_match = _PLAYER_SKILL_MILESTONE_PATTERN.fullmatch(milestone)
        if milestone_match is None:
            raise RuntimeError("legacy player skill milestone invalid; repair data before retry")

        skill_id = milestone_match.group("skill_id")
        skill_display_name = _PLAYER_SKILL_DISPLAY_NAMES.get(skill_id)
        level = int(milestone_match.group("level"))
        if skill_display_name is None or level > _WIRE_INTEGER_MAX:
            raise RuntimeError(
                "legacy player skill milestone unsupported; repair data before retry"
            )

        occurred_day_index = row["occurred_day_index"]
        if (
            not isinstance(occurred_day_index, int)
            or isinstance(occurred_day_index, bool)
            or not 0 <= occurred_day_index <= _WIRE_INTEGER_MAX
        ):
            raise RuntimeError("legacy player skill occurred day invalid; repair data before retry")

        old_summary = f"第 {occurred_day_index} 天，世界进度达成：{milestone}。"
        old_tags = ["progression", f"milestone:{milestone}"]
        new_summary = (
            f"第 {occurred_day_index} 天，玩家的{skill_display_name}技能提升到 {level} 级。"
        )
        new_tags = [
            "progression",
            "actor:player",
            f"skill:{skill_id}",
            f"level:{level}",
            f"milestone:{milestone}",
        ]
        stored_tags = _decode_json_array(row["tags_json"], "tags_json")

        # 已升级行是 downgrade/no-op 后重新 upgrade 的合法输入，必须逐字保持。
        if row["summary"] == new_summary and stored_tags == new_tags:
            continue
        if row["summary"] != old_summary or stored_tags != old_tags:
            raise RuntimeError(
                "legacy player skill memory projection invalid; repair data before retry"
            )

        updates.append(
            {
                "id": int(row["id"]),
                "summary": new_summary,
                "tags": json.dumps(
                    new_tags,
                    ensure_ascii=False,
                    separators=(",", ":"),
                ),
            }
        )

    for values in updates:
        bind.execute(
            sa.text("UPDATE memories SET summary=:summary,tags_json=:tags WHERE id=:id"),
            values,
        )


def downgrade() -> None:
    """只回退 revision 标记，保留与 0002 Schema 完全兼容的改进文本。

    旧通用摘要会丢失“这是玩家技能”的确定语义，因此不能为形式上的可逆性
    主动降质。保留新文本也让随后重新 upgrade 能以幂等路径安全通过。
    """


def _decode_json_object(value: object, field_name: str) -> dict[str, object]:
    """读取历史 JSON object；解码错误转换为稳定的 migration 失败。"""

    decoded = _decode_json(value, field_name)
    if not isinstance(decoded, dict):
        raise RuntimeError(f"legacy {field_name} must be object; repair data before retry")
    return decoded


def _decode_json_array(value: object, field_name: str) -> list[object]:
    """读取历史 JSON array；拒绝把其他 JSON 类型静默解释成标签列表。"""

    decoded = _decode_json(value, field_name)
    if not isinstance(decoded, list):
        raise RuntimeError(f"legacy {field_name} must be array; repair data before retry")
    return decoded


def _decode_json(value: object, field_name: str) -> object:
    """兼容 SQLite text 与已由 driver 解码的 JSON，并隐藏底层异常细节。"""

    if not isinstance(value, str):
        return value
    try:
        return json.loads(value)
    except (TypeError, json.JSONDecodeError) as error:
        raise RuntimeError(
            f"legacy {field_name} contains invalid JSON; repair data before retry"
        ) from error
