"""Agent、Repair 与 Guard 共享的模型可见 mandatory context 白名单。

公开 HTTP 合同继续解析完整 progression/memory signals，并让它们参与 generation
identity；但模型初始 Prompt 只能看到当前日历事实。需要历史证据的矿洞、技能、
工具和设施必须在调用领域工具后才可见，避免同一事实既提前注入又要求取证。
"""

from __future__ import annotations

from collections.abc import Mapping
from typing import Any

CALENDAR_PROGRESSION_SIGNAL_KEYS = ("year", "day_of_month")


def visible_calendar_progression_signals(
    progression_signals: Mapping[str, Any],
) -> dict[str, Any]:
    """复制允许无 evidence 使用的年与月内日，稳定保持白名单顺序。

    Args:
        progression_signals: 已通过公开 Pydantic Schema 的完整动态信号 mapping。
    Returns:
        只含存在的 ``year/day_of_month`` 的新字典；不修改输入。
    """

    return {
        key: progression_signals[key]
        for key in CALENDAR_PROGRESSION_SIGNAL_KEYS
        if key in progression_signals
    }
