"""Agent 与 Repair 共享的 Token usage 聚合合同。

Provider 只有在成功返回 `AIMessage.usage_metadata` 时才能提供可靠 Token 数。本模块
只聚合这些已报告、非负整数，不估算失败重试或从文本长度推测费用；调用方可通过
`reported_calls` 区分“真实报告为 0”和“本次消息没有 usage metadata”。
"""

from __future__ import annotations

from collections.abc import Sequence
from dataclasses import dataclass

from langchain_core.messages import AIMessage, BaseMessage


@dataclass(frozen=True, slots=True)
class DialogueModelUsage:
    """一段模型消息轨迹中由 Provider 实际报告的 Token 合计。"""

    input_tokens: int = 0
    output_tokens: int = 0
    total_tokens: int = 0
    reported_calls: int = 0

    def __post_init__(self) -> None:
        """拒绝 bool/负数，避免腐化 usage 进入 generation 审计。"""

        for field_name, value in (
            ("input_tokens", self.input_tokens),
            ("output_tokens", self.output_tokens),
            ("total_tokens", self.total_tokens),
            ("reported_calls", self.reported_calls),
        ):
            if not isinstance(value, int) or isinstance(value, bool) or value < 0:
                raise ValueError(f"{field_name} 必须是非负整数")

    def __add__(self, other: DialogueModelUsage) -> DialogueModelUsage:
        """逐字段合并 Agent/Repair 或多条 AIMessage 的已报告 usage。"""

        if not isinstance(other, DialogueModelUsage):
            return NotImplemented
        return DialogueModelUsage(
            input_tokens=self.input_tokens + other.input_tokens,
            output_tokens=self.output_tokens + other.output_tokens,
            total_tokens=self.total_tokens + other.total_tokens,
            reported_calls=self.reported_calls + other.reported_calls,
        )

    def to_dict(self) -> dict[str, int]:
        """返回固定 JSON primitive 形状，供 generation `usage_json` 保存。"""

        return {
            "input_tokens": self.input_tokens,
            "output_tokens": self.output_tokens,
            "total_tokens": self.total_tokens,
            "reported_calls": self.reported_calls,
        }


def usage_from_ai_message(message: AIMessage) -> DialogueModelUsage:
    """从一条真实 AIMessage 提取已报告 usage；缺失或非法 shape 返回零报告。"""

    metadata = message.usage_metadata
    if metadata is None:
        return DialogueModelUsage()
    input_tokens = metadata.get("input_tokens")
    output_tokens = metadata.get("output_tokens")
    total_tokens = metadata.get("total_tokens")
    values = (input_tokens, output_tokens, total_tokens)
    if any(not isinstance(value, int) or isinstance(value, bool) or value < 0 for value in values):
        return DialogueModelUsage()
    return DialogueModelUsage(
        input_tokens=input_tokens,
        output_tokens=output_tokens,
        total_tokens=total_tokens,
        reported_calls=1,
    )


def aggregate_message_usage(messages: Sequence[BaseMessage]) -> DialogueModelUsage:
    """按消息顺序聚合真实 AIMessage 的 Provider usage metadata。"""

    usage = DialogueModelUsage()
    for message in messages:
        if isinstance(message, AIMessage):
            usage = usage + usage_from_ai_message(message)
    return usage
