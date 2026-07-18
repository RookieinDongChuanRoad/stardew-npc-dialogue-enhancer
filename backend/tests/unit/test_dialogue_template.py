"""Typed 玩家名显示槽的纯 codec 合同测试。

这些测试只描述 Python 内部模型与 Stardew 公共模板字符串之间的确定性转换。
真实玩家名不属于 codec 输入，因此测试也不会引入任何姓名、Provider 或存储依赖。
"""

from __future__ import annotations

import pytest
from pydantic import ValidationError

from stardew_npc_agent.dialogue_template import (
    DISPLAY_TOKEN_POLICY_VERSION,
    AddressSlot,
    DialogueTemplateError,
    DialogueTextTemplate,
    parse_game_template,
    render_game_template,
    source_requires_player_name,
    validate_literal,
)


def test_display_token_policy_has_an_independent_frozen_version() -> None:
    """display token 语义必须拥有独立版本轴，不能借用 source policy 版本。"""

    assert DISPLAY_TOKEN_POLICY_VERSION == "display-token-policy-v1"


@pytest.mark.parametrize(
    ("source", "expected"),
    [
        (
            "雨天待在屋里也不算太糟。",
            DialogueTextTemplate(
                prefix="雨天待在屋里也不算太糟。",
                address_slot=AddressSlot.NONE,
                suffix="",
            ),
        ),
        (
            "@，这种雨声让我想起了那块紫水晶。",
            DialogueTextTemplate(
                prefix="",
                address_slot=AddressSlot.PLAYER_NAME,
                suffix="，这种雨声让我想起了那块紫水晶。",
            ),
        ),
        (
            "回头见，@",
            DialogueTextTemplate(
                prefix="回头见，",
                address_slot=AddressSlot.PLAYER_NAME,
                suffix="",
            ),
        ),
    ],
)
def test_parse_and_render_game_template_round_trip(
    source: str,
    expected: DialogueTextTemplate,
) -> None:
    """零或一个 ``@`` 应转换为封闭 enum，并可逐字还原游戏模板。"""

    parsed = parse_game_template(source)

    assert parsed == expected
    assert render_game_template(parsed) == source
    assert source_requires_player_name(parsed) is (parsed.address_slot is AddressSlot.PLAYER_NAME)


@pytest.mark.parametrize(
    ("source", "expected_code"),
    [
        ("你好，@@", "ADDRESS_SLOT_NOT_ALLOWED"),
        ("你好，%endearment", "DIALOGUE_DSL_NOT_ALLOWED"),
        ("你好，$", "DIALOGUE_DSL_NOT_ALLOWED"),
        ("你好，#", "DIALOGUE_DSL_NOT_ALLOWED"),
        ("你好，^", "DIALOGUE_DSL_NOT_ALLOWED"),
        ("你好，¦", "DIALOGUE_DSL_NOT_ALLOWED"),
        ("你好，[秘密]", "DIALOGUE_DSL_NOT_ALLOWED"),
        ("你好，{秘密}", "DIALOGUE_DSL_NOT_ALLOWED"),
        ("你好\r再见", "DIALOGUE_DSL_NOT_ALLOWED"),
        ("你好\n再见", "DIALOGUE_DSL_NOT_ALLOWED"),
        ("你好||再见", "DIALOGUE_DSL_NOT_ALLOWED"),
    ],
)
def test_parse_rejects_every_unapproved_game_control_token(
    source: str,
    expected_code: str,
) -> None:
    """只有单个 ``@`` 可成为槽；其余 Stardew DSL 必须稳定失败。"""

    with pytest.raises(DialogueTemplateError) as error:
        parse_game_template(source)

    assert error.value.code == expected_code


@pytest.mark.parametrize(
    "literal",
    [
        "raw@name",
        "%endearment",
        "$command",
        "#speaker",
        "^gender",
        "¦split",
        "[link]",
        "{token}",
        "line\rbreak",
        "line\nbreak",
        "paragraph||break",
    ],
)
def test_literal_validator_and_pydantic_model_reject_raw_tokens(literal: str) -> None:
    """模型只能选 typed slot，不能把 raw token 偷渡进 prefix/suffix。"""

    with pytest.raises(DialogueTemplateError) as codec_error:
        validate_literal(literal)
    assert codec_error.value.code in {
        "RAW_DIALOGUE_TOKEN_NOT_ALLOWED",
        "DIALOGUE_DSL_NOT_ALLOWED",
    }

    with pytest.raises(ValidationError):
        DialogueTextTemplate(
            prefix=literal,
            address_slot=AddressSlot.NONE,
            suffix="",
        )


def test_empty_literals_are_valid_but_extra_fields_and_mutation_are_rejected() -> None:
    """槽可位于首尾；模型仍须 frozen 且拒绝未声明字段。"""

    leading = DialogueTextTemplate(
        prefix="",
        address_slot=AddressSlot.PLAYER_NAME,
        suffix="，今天还好吗？",
    )
    trailing = DialogueTextTemplate(
        prefix="回头见，",
        address_slot=AddressSlot.PLAYER_NAME,
        suffix="",
    )

    assert render_game_template(leading) == "@，今天还好吗？"
    assert render_game_template(trailing) == "回头见，@"
    with pytest.raises(ValidationError):
        DialogueTextTemplate.model_validate(
            {
                "prefix": "你好",
                "address_slot": "none",
                "suffix": "",
                "raw_text": "不允许",
            }
        )
    with pytest.raises(ValidationError):
        leading.prefix = "修改"  # type: ignore[misc]
