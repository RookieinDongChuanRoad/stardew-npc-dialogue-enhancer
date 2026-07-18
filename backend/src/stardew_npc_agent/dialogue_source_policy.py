"""ordinary/rainy daily source 的本地、确定性分类规则。

公共 v1 DTO 不携带 ``source_family``。因此后端必须根据请求中的 NPC、资产和
字典 key 独立推导 family，不能信任游戏侧的隐式结论。这里刻意保持纯函数边界，
让 HTTP service 与 Agent adapter 的独立入口可以复用完全相同的 fail-closed policy。
"""

from __future__ import annotations

import re
from dataclasses import dataclass
from enum import StrEnum

# source classifier 的独立 cache identity 轴。规则或允许 family 变化时必须 bump；
# 后续 display-token policy 使用另一版本轴，不能复用本常量掩盖两类语义变化。
DIALOGUE_SOURCE_POLICY_VERSION = "dialogue-source-policy-v1"


class DialogueSourceFamily(StrEnum):
    """当前版本允许进入生成链的原版日常来源族。"""

    ORDINARY_DAILY = "ordinary_daily"
    RAINY_DAILY = "rainy_daily"


@dataclass(frozen=True, slots=True)
class DialogueSourceIdentity:
    """由 exact 请求字段独立证明的来源身份。

    Attributes:
        family: ordinary 或 rainy daily 来源族。
        npc_id: 大小写敏感的目标 NPC 内部 ID。
        asset_name: 仅规范化路径分隔符后的 exact 资产名。
        dialogue_key: 资产字典中的 exact key。
    """

    family: DialogueSourceFamily
    npc_id: str
    asset_name: str
    dialogue_key: str


_RAINY_DIALOGUE_ASSET_NAME = "Characters/Dialogue/rainy"

# 与游戏侧 DialogueKeyClassifier 的有限 ordinary daily key 白名单保持同构。
# fullmatch 是合同的一部分：Python ``$`` 会容忍终止换行，不能用于安全边界。
_ORDINARY_DAILY_KEY_PATTERN = re.compile(
    r"(?:(?:spring|summer|fall|winter)_)?(?:"
    r"(?:[1-9]|1[0-9]|2[0-8])(?:_(?:1|2|\*))?"
    r"|(?:Mon|Tue|Wed|Thu|Fri|Sat|Sun)(?:2|4|6|8|10)?(?:_(?:1|2))?"
    r")"
)


def classify_dialogue_source(
    *,
    npc_id: str,
    asset_name: str,
    dialogue_key: str,
) -> DialogueSourceIdentity | None:
    """按 exact NPC/asset/key 分类受支持的 daily source。

    只把 ``\\`` 替换为 ``/``；不会 Trim、case-fold、Unicode normalize 或折叠
    路径段。rainy 共享资产还要求 key 与目标 NPC ID 逐字符相同。任一条件不成立
    都返回 ``None``，调用方不得继续 cache lookup 或生成。
    """

    if not npc_id or not asset_name or not dialogue_key:
        return None

    normalized_asset_name = asset_name.replace("\\", "/")
    if normalized_asset_name == _RAINY_DIALOGUE_ASSET_NAME:
        if dialogue_key != npc_id:
            return None
        return DialogueSourceIdentity(
            family=DialogueSourceFamily.RAINY_DAILY,
            npc_id=npc_id,
            asset_name=normalized_asset_name,
            dialogue_key=dialogue_key,
        )

    expected_ordinary_asset_name = f"Characters/Dialogue/{npc_id}"
    if (
        normalized_asset_name != expected_ordinary_asset_name
        or _ORDINARY_DAILY_KEY_PATTERN.fullmatch(dialogue_key) is None
    ):
        return None

    return DialogueSourceIdentity(
        family=DialogueSourceFamily.ORDINARY_DAILY,
        npc_id=npc_id,
        asset_name=normalized_asset_name,
        dialogue_key=dialogue_key,
    )
