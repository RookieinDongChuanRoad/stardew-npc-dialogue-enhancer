"""Stardew 对话模板与模型 typed 显示槽之间的唯一转换边界。

Provider 永远不应接触真实玩家名，也不能直接编写 Stardew 的自由格式 Dialogue
DSL。本模块因此把唯一获准的动态标记 ``@`` 表示成封闭的
:class:`AddressSlot` 枚举；模型只能编辑普通字面量并选择是否放置一个玩家名槽。

codec 是纯函数：不读取游戏、配置、数据库或环境变量。调用方可以先解析游戏
模板、让 Agent/Repair 处理 typed model，最后再确定性还原为仍含 ``@`` 的公共
v1 response。真实姓名只会在后续游戏本地显示阶段展开。
"""

from __future__ import annotations

from enum import StrEnum

from pydantic import BaseModel, ConfigDict, field_validator

# display token 的语义独立于 ordinary/rainy source 分类。策略变化必须通过该
# 版本轴使旧 generation cache miss，不能依赖 Prompt 或 source policy 恰好变更。
DISPLAY_TOKEN_POLICY_VERSION = "display-token-policy-v1"

_PLAYER_NAME_TOKEN = "@"
_DIALOGUE_DSL_CHARACTERS = frozenset("$#%^¦[]{}\r\n")


class DialogueTemplateError(ValueError):
    """typed template 无法安全解析或还原时的稳定错误。

    Attributes:
        code: 可进入 Guard/服务审计的稳定大写机器码。异常正文刻意不包含输入
            文本，避免未来日志无意记录候选内容或动态数据。
    """

    def __init__(self, code: str) -> None:
        """保存稳定机器码，不回显导致失败的字面量。"""

        self.code = code
        super().__init__("invalid dialogue text template")


class AddressSlot(StrEnum):
    """Provider 可选择的封闭称呼槽集合。"""

    NONE = "none"
    PLAYER_NAME = "player_name"


class DialogueTextTemplate(BaseModel):
    """一条只含普通字面量与至多一个 typed 称呼槽的候选。

    ``prefix`` 和 ``suffix`` 允许为空，以支持句首或句尾称呼；整条候选是否非空、
    长度是否合适仍由 Guard 在 render 后统一裁决。模型 frozen 且 extra-forbid，
    防止调用方另塞 raw text 或未知 token 字段绕开 codec。
    """

    model_config = ConfigDict(extra="forbid", frozen=True)

    prefix: str
    address_slot: AddressSlot
    suffix: str

    @field_validator("prefix", "suffix")
    @classmethod
    def validate_literal_fields(cls, value: str) -> str:
        """复用唯一字面量规则，让 structured output 在进入 Guard 前即失败。"""

        return validate_literal(value)


def validate_literal(segment: str) -> str:
    """验证模型可编辑的一个普通字面量片段。

    Args:
        segment: ``prefix`` 或 ``suffix``。空字符串合法；必须是严格字符串。
    Returns:
        未修改的原始片段。codec 从不 trim、normalize 或替换游戏文本。
    Raises:
        DialogueTemplateError: 片段含 raw ``@``，或任何未批准的 Stardew DSL。
    """

    if not isinstance(segment, str):
        raise DialogueTemplateError("RAW_DIALOGUE_TOKEN_NOT_ALLOWED")
    if _PLAYER_NAME_TOKEN in segment:
        raise DialogueTemplateError("RAW_DIALOGUE_TOKEN_NOT_ALLOWED")
    if "||" in segment or any(character in segment for character in _DIALOGUE_DSL_CHARACTERS):
        raise DialogueTemplateError("DIALOGUE_DSL_NOT_ALLOWED")
    return segment


def parse_game_template(text: str) -> DialogueTextTemplate:
    """把含零或一个 ``@`` 的游戏字符串解析成 typed template。

    两个及以上 ``@`` 没有无歧义的单槽表达，因此直接拒绝。其他控制字符不会
    因为恰好位于 ``@`` 两侧而被放行，而是继续经过 :func:`validate_literal`。
    """

    if not isinstance(text, str):
        raise DialogueTemplateError("RAW_DIALOGUE_TOKEN_NOT_ALLOWED")
    token_count = text.count(_PLAYER_NAME_TOKEN)
    if token_count > 1:
        raise DialogueTemplateError("ADDRESS_SLOT_NOT_ALLOWED")
    if token_count == 0:
        return DialogueTextTemplate(
            prefix=validate_literal(text),
            address_slot=AddressSlot.NONE,
            suffix="",
        )

    prefix, suffix = text.split(_PLAYER_NAME_TOKEN, maxsplit=1)
    return DialogueTextTemplate(
        prefix=validate_literal(prefix),
        address_slot=AddressSlot.PLAYER_NAME,
        suffix=validate_literal(suffix),
    )


def render_game_template(template: DialogueTextTemplate) -> str:
    """把可信 typed template 确定性还原为仍含占位符的游戏字符串。

    函数不会展开真实玩家名。即使调用方通过 Pydantic 的低层 ``model_construct``
    绕过了字段 validator，这里仍再次验证两个字面量并拒绝未知对象类型。
    """

    if not isinstance(template, DialogueTextTemplate):
        raise DialogueTemplateError("RAW_DIALOGUE_TOKEN_NOT_ALLOWED")
    prefix = validate_literal(template.prefix)
    suffix = validate_literal(template.suffix)
    if template.address_slot is AddressSlot.NONE:
        token = ""
    elif template.address_slot is AddressSlot.PLAYER_NAME:
        token = _PLAYER_NAME_TOKEN
    else:
        raise DialogueTemplateError("ADDRESS_SLOT_NOT_ALLOWED")
    return f"{prefix}{token}{suffix}"


def source_requires_player_name(source_template: DialogueTextTemplate) -> bool:
    """返回原台词是否已包含必须保留的玩家名槽。"""

    if not isinstance(source_template, DialogueTextTemplate):
        raise DialogueTemplateError("RAW_DIALOGUE_TOKEN_NOT_ALLOWED")
    return source_template.address_slot is AddressSlot.PLAYER_NAME
