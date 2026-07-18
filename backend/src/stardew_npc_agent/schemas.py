"""v1 HTTP/JSON 合同对应的 Pydantic DTO。

此模块只负责 wire-level 解析和局部不变量，不实现数据库幂等、Agent 决策或游戏
状态变更。所有 DTO 默认拒绝未知字段，目的是让 C# 与 Python 合同漂移尽早表现为
4xx/测试失败，而不是静默丢弃调用方以为已经生效的数据。
"""

from __future__ import annotations

from typing import Annotated, Literal

from pydantic import (
    AfterValidator,
    BaseModel,
    ConfigDict,
    Field,
    JsonValue,
    StringConstraints,
    model_validator,
)

from stardew_npc_agent.contract_limits import WIRE_INTEGER_MAX, WIRE_INTEGER_MIN

SchemaVersion = Literal["1.0"]


def _reject_edge_whitespace(value: str) -> str:
    """拒绝首尾空白但原样返回合法值，因此内部空白不会被删除或规范化。"""

    if value != value.strip():
        raise ValueError("字符串首尾不能包含空白字符")
    return value


NonBlankString = Annotated[
    str,
    StringConstraints(min_length=1),
    AfterValidator(_reject_edge_whitespace),
]
JsonObject = dict[str, JsonValue]
WireInteger = Annotated[int, Field(ge=WIRE_INTEGER_MIN, le=WIRE_INTEGER_MAX)]
NonNegativeWireInteger = Annotated[int, Field(ge=0, le=WIRE_INTEGER_MAX)]
CommittedThroughDayIndex = Annotated[int, Field(ge=-1, le=WIRE_INTEGER_MAX)]


class StrictContractModel(BaseModel):
    """所有公开 DTO 的共同严格解析策略。

    `extra="forbid"` 是兼容性保护：新增字段必须先升级共享 Schema 与两端 DTO，不能
    由一端悄悄发送、另一端静默忽略。`strict=True` 禁止把字符串或布尔值自动转换成
    整数；`NonBlankString` 拒绝首尾空白而不静默改写调用方输入。
    """

    model_config = ConfigDict(extra="forbid", strict=True)


class GameEvent(StrictContractModel):
    """游戏侧已经确认发生的一条结构化事实事件。

    `payload` 保持为 JSON object，以允许不同 event_type 拥有不同字段；具体事件版本
    的 payload 语义将在事件服务阶段校验。`audience_npc_id` 允许 null，以支持公共
    世界事实而不引入 `*` 之类的魔法 NPC 主键。
    """

    event_id: NonBlankString
    event_type: NonBlankString
    event_version: NonBlankString
    occurred_day_index: NonNegativeWireInteger
    source: NonBlankString
    audience_scope: Literal["public", "npc"]
    audience_npc_id: NonBlankString | None
    payload: JsonObject

    @model_validator(mode="after")
    def validate_audience_target(self) -> GameEvent:
        """保证事件可见范围与 NPC 目标字段具有唯一、无歧义的组合。"""

        if self.audience_scope == "public" and self.audience_npc_id is not None:
            raise ValueError("public 事件的 audience_npc_id 必须为 null")
        if self.audience_scope == "npc" and self.audience_npc_id is None:
            raise ValueError("npc 事件必须包含非空 audience_npc_id")
        return self


class GameEventBatchRequest(StrictContractModel):
    """按存档和玩家分区提交的一批游戏事件。

    64 条上限是 wire 层的第一道资源保护；service 和 storage 还会二次校验，
    防止进程内调用方使用 ``model_construct`` 或内部类型绕过 Pydantic。
    """

    schema_version: SchemaVersion
    request_id: NonBlankString
    save_id: NonBlankString
    player_id: NonBlankString
    events: list[GameEvent] = Field(min_length=1, max_length=64)


class GameEventItemResult(StrictContractModel):
    """单条事件的幂等接收结果。

    `reason_code` 对 rejected 应给出机器可读原因，对 accepted 可以为 null；Phase 1
    只冻结 DTO，不在此处推断业务原因。
    """

    event_id: NonBlankString
    status: Literal["accepted", "duplicate", "rejected"]
    reason_code: NonBlankString | None


class GameEventBatchResponse(StrictContractModel):
    """事件批次响应及提交完成后的记忆版本水位。"""

    schema_version: SchemaVersion
    request_id: NonBlankString
    memory_revision: NonNegativeWireInteger
    committed_through_day_index: CommittedThroughDayIndex
    items: list[GameEventItemResult] = Field(min_length=1)


class StableDayContext(StrictContractModel):
    """每日预生成期间不会随点击时机变化的上下文快照。"""

    season: NonBlankString
    weather: NonBlankString
    locale: NonBlankString
    progression_signals: JsonObject


class SourceDialogue(StrictContractModel):
    """原版台词语义锚点与展示前一致性指纹。"""

    asset_name: NonBlankString
    dialogue_key: NonBlankString
    text: NonBlankString
    source_hash: NonBlankString


class RelationshipSnapshot(StrictContractModel):
    """由游戏提供的权威关系读快照，后端只能读取、不能修改。"""

    friendship_points: WireInteger
    relationship_stage: NonBlankString


class DialogueGenerationItem(StrictContractModel):
    """批次中的单 NPC 台词生成任务。

    `style_examples` 必须包含同 NPC、同 locale 的 2～5 条确定性原版风格样本；
    `memory_signals` 只提供轻量线索。两者都保持数据输入身份，不在 DTO 层执行
    检索、生成或跨 NPC 聚合。
    """

    task_id: NonBlankString
    npc_id: NonBlankString
    source_dialogue: SourceDialogue
    relationship_snapshot: RelationshipSnapshot
    style_examples: list[NonBlankString] = Field(min_length=2, max_length=5)
    memory_signals: list[JsonObject]


class DialogueGenerationBatchRequest(StrictContractModel):
    """每日台词预生成批次，请求最多包含八个独立 NPC 任务。"""

    schema_version: SchemaVersion
    request_id: NonBlankString
    save_id: NonBlankString
    player_id: NonBlankString
    game_day_index: NonNegativeWireInteger
    required_memory_revision: NonNegativeWireInteger
    stable_day_context: StableDayContext
    items: list[DialogueGenerationItem] = Field(min_length=1, max_length=8)


class DialogueGenerationItemResult(StrictContractModel):
    """单 NPC 生成任务的终态结果。

    `generated` 是唯一可携带展示文本的状态；`passthrough`、`skipped` 与 `failed`
    必须返回 null。这个不变量防止调用方误把失败说明或原文副本当作已通过 Guard
    的增强台词。`source_hash` 由调用方在展示前再次比对。
    """

    task_id: NonBlankString
    generation_id: NonBlankString
    generation_key: NonBlankString
    status: Literal["generated", "passthrough", "skipped", "failed"]
    text: str | None
    source_hash: NonBlankString
    reason_code: NonBlankString
    evidence_ids: list[NonBlankString]
    trace_id: NonBlankString

    @model_validator(mode="after")
    def validate_text_for_status(self) -> DialogueGenerationItemResult:
        """强制终态与文本可展示性一致，拒绝边缘空白且不改写合法内容。"""

        if self.status == "generated":
            if self.text is None or not self.text.strip():
                raise ValueError("generated 状态必须包含非空 text")
            if self.text != self.text.strip():
                # 不静默规范化：调用方、缓存、审计与最终显示必须看到同一逐字符文本。
                raise ValueError("generated text 首尾不能包含空白字符")
        elif self.text is not None:
            raise ValueError(f"{self.status} 状态的 text 必须为 null")
        return self


class DialogueGenerationBatchResponse(StrictContractModel):
    """逐 NPC 返回结果的批次响应；单项失败不会改变其他项的终态。"""

    schema_version: SchemaVersion
    request_id: NonBlankString
    memory_revision: NonNegativeWireInteger
    items: list[DialogueGenerationItemResult] = Field(min_length=1, max_length=8)


class DisplayAckRequest(StrictContractModel):
    """游戏确认某条增强台词实际显示后的幂等回执。

    `save_id/player_id` 与其他业务请求保持一致，防止只凭 NPC ID 与 source hash
    在多个存档之间错误归属展示记录。
    """

    schema_version: SchemaVersion
    request_id: NonBlankString
    save_id: NonBlankString
    player_id: NonBlankString
    display_receipt_id: NonBlankString
    displayed_day_index: NonNegativeWireInteger
    npc_id: NonBlankString
    source_hash: NonBlankString


class DisplayAckResponse(StrictContractModel):
    """展示回执首次接收或重复提交的结果。"""

    schema_version: SchemaVersion
    request_id: NonBlankString
    display_receipt_id: NonBlankString
    status: Literal["accepted", "duplicate"]


class HealthStatusResponse(StrictContractModel):
    """无秘密、无内部路径的进程健康状态。"""

    status: Literal["alive", "ready"]


class CapabilitiesResponse(StrictContractModel):
    """调用方可安全探测的合同和模型能力声明。"""

    schema_versions: list[SchemaVersion] = Field(min_length=1)
    locales: list[NonBlankString] = Field(min_length=1)
    batch_max_items: int = Field(ge=1, le=8)
    provider_configured: bool
    tool_calling: bool
    structured_output: bool
