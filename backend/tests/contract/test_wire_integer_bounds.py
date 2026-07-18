"""跨语言 wire integer 边界的 JSON Schema/Pydantic 合同测试。

这些测试使用 C# ``int`` 能表达的完整 Int32 范围作为三端共同真值。Python 的
任意精度整数不能放宽公开合同，否则一个在 FastAPI 看似合法的请求会在 C# 解析
或 SQLite 写入阶段失败，形成跨语言判断分叉。
"""

from __future__ import annotations

import json
from copy import deepcopy
from functools import reduce
from pathlib import Path
from typing import Any

import pytest
from jsonschema import Draft202012Validator
from pydantic import BaseModel, ValidationError

from stardew_npc_agent.contract_limits import WIRE_INTEGER_MAX, WIRE_INTEGER_MIN
from stardew_npc_agent.schemas import (
    DialogueGenerationBatchRequest,
    DialogueGenerationBatchResponse,
    DisplayAckRequest,
    GameEventBatchRequest,
    GameEventBatchResponse,
)

PROJECT_ROOT = Path(__file__).resolve().parents[3]
CONTRACT_ROOT = PROJECT_ROOT / "contracts"
FIXTURE_ROOT = CONTRACT_ROOT / "fixtures"
SCHEMA_ROOT = CONTRACT_ROOT / "v1"


def _load_json(path: Path) -> dict[str, Any]:
    """读取权威 fixture/schema，并断言顶层仍是 JSON object。"""

    with path.open(encoding="utf-8") as file_handle:
        value = json.load(file_handle)
    assert isinstance(value, dict)
    return value


def test_python_contract_limits_are_exact_int32_boundaries() -> None:
    """Python 生产常量必须与 C# ``int`` 两个精确端点一致。"""

    assert WIRE_INTEGER_MIN == -2_147_483_648
    assert WIRE_INTEGER_MAX == 2_147_483_647


@pytest.mark.parametrize(
    ("schema_name", "field_path"),
    [
        (
            "game_event_batch.schema.json",
            ("$defs", "game_event", "properties", "occurred_day_index"),
        ),
        (
            "dialogue_generation_batch.schema.json",
            ("properties", "game_day_index"),
        ),
        (
            "dialogue_generation_batch.schema.json",
            ("properties", "required_memory_revision"),
        ),
        (
            "dialogue_generation_batch.schema.json",
            (
                "$defs",
                "relationship_snapshot",
                "properties",
                "friendship_points",
            ),
        ),
        (
            "dialogue_display_ack.schema.json",
            ("properties", "displayed_day_index"),
        ),
    ],
)
def test_schema_marks_every_public_integer_with_strict_token_extension(
    schema_name: str,
    field_path: tuple[str, ...],
) -> None:
    """标准 JSON Schema 无法区分 ``13`` 与 ``13.0``，项目扩展必须冻结词法规则。"""

    schema = _load_json(SCHEMA_ROOT / schema_name)
    field = reduce(lambda value, key: value[key], field_path, schema)

    assert field["x-stardew-json-integer-token"] is True


def test_standard_schema_integer_is_only_a_value_level_superset() -> None:
    """显式记录 Draft 语义限制，同时证明 Python raw JSON 入口执行更严格 token 合同。"""

    payload = _load_json(FIXTURE_ROOT / "event_batch.json")
    payload["events"][0]["occurred_day_index"] = 13.0
    schema = _load_json(SCHEMA_ROOT / "game_event_batch.schema.json")

    # Draft 2020-12 把数学值 13.0 视为 integer；这不是本项目最终 wire 判定。
    assert Draft202012Validator(schema).is_valid(payload)
    with pytest.raises(ValidationError):
        GameEventBatchRequest.model_validate_json(json.dumps(payload))


def test_python_raw_json_rejects_integral_decimal_for_every_typed_request_integer() -> None:
    """Schema 扩展标记的五个请求字段都必须由 strict Pydantic 拒绝 float token。"""

    event_payload = _load_json(FIXTURE_ROOT / "event_batch.json")
    event_payload["events"][0]["occurred_day_index"] = 13.0

    dialogue_day = _load_json(FIXTURE_ROOT / "dialogue_batch.json")
    dialogue_day["game_day_index"] = 14.0
    dialogue_revision = _load_json(FIXTURE_ROOT / "dialogue_batch.json")
    dialogue_revision["required_memory_revision"] = 42.0
    dialogue_friendship = _load_json(FIXTURE_ROOT / "dialogue_batch.json")
    dialogue_friendship["items"][0]["relationship_snapshot"]["friendship_points"] = 750.0

    ack_payload = {
        "schema_version": "1.0",
        "request_id": "request-wire-decimal",
        "save_id": "save-wire",
        "player_id": "player-wire",
        "display_receipt_id": "receipt-wire-decimal",
        "displayed_day_index": 14.0,
        "npc_id": "Abigail",
        "source_hash": "sha256:wire-decimal",
    }

    cases: list[tuple[type[BaseModel], dict[str, Any]]] = [
        (GameEventBatchRequest, event_payload),
        (DialogueGenerationBatchRequest, dialogue_day),
        (DialogueGenerationBatchRequest, dialogue_revision),
        (DialogueGenerationBatchRequest, dialogue_friendship),
        (DisplayAckRequest, ack_payload),
    ]
    for model, payload in cases:
        with pytest.raises(ValidationError):
            model.model_validate_json(json.dumps(payload))


def test_python_raw_json_rejects_integral_exponent_token() -> None:
    """Python JSON 解析把指数形式读成 float，strict DTO 必须与 C# 一致拒绝。"""

    raw_json = (
        (FIXTURE_ROOT / "event_batch.json")
        .read_text(encoding="utf-8")
        .replace(
            '"occurred_day_index": 13',
            '"occurred_day_index": 1.3e1',
        )
    )

    with pytest.raises(ValidationError):
        GameEventBatchRequest.model_validate_json(raw_json)


def _assert_schema_and_pydantic_agree(
    *,
    schema_name: str,
    payload: dict[str, Any],
    model: type[BaseModel],
    should_be_valid: bool,
) -> None:
    """同时执行共享 Schema 与 Python DTO，防止其中一端被短路而漏测。"""

    schema = _load_json(SCHEMA_ROOT / schema_name)
    schema_is_valid = Draft202012Validator(schema).is_valid(payload)
    pydantic_is_valid = True
    try:
        model.model_validate(payload)
    except ValidationError:
        pydantic_is_valid = False

    assert (schema_is_valid, pydantic_is_valid) == (
        should_be_valid,
        should_be_valid,
    )


@pytest.mark.parametrize(
    ("day_index", "should_be_valid"),
    [
        (WIRE_INTEGER_MAX, True),
        (WIRE_INTEGER_MAX + 1, False),
    ],
)
def test_event_day_uses_non_negative_int32_wire_range(
    day_index: int,
    should_be_valid: bool,
) -> None:
    """事件日上界必须在进入投影和 SQLite 前由两套公开合同一致裁决。"""

    payload = _load_json(FIXTURE_ROOT / "event_batch.json")
    payload["events"][0]["occurred_day_index"] = day_index

    _assert_schema_and_pydantic_agree(
        schema_name="game_event_batch.schema.json",
        payload=payload,
        model=GameEventBatchRequest,
        should_be_valid=should_be_valid,
    )


@pytest.mark.parametrize("field_name", ["game_day_index", "required_memory_revision"])
@pytest.mark.parametrize(
    ("field_value", "should_be_valid"),
    [
        (WIRE_INTEGER_MAX, True),
        (WIRE_INTEGER_MAX + 1, False),
    ],
)
def test_dialogue_day_and_revision_use_non_negative_int32_wire_range(
    field_name: str,
    field_value: int,
    should_be_valid: bool,
) -> None:
    """每日生成日与所需 revision 共享同一个非负 Int32 闭区间。"""

    payload = _load_json(FIXTURE_ROOT / "dialogue_batch.json")
    payload[field_name] = field_value

    _assert_schema_and_pydantic_agree(
        schema_name="dialogue_generation_batch.schema.json",
        payload=payload,
        model=DialogueGenerationBatchRequest,
        should_be_valid=should_be_valid,
    )


@pytest.mark.parametrize(
    ("friendship_points", "should_be_valid"),
    [
        (WIRE_INTEGER_MIN, True),
        (WIRE_INTEGER_MAX, True),
        (WIRE_INTEGER_MIN - 1, False),
        (WIRE_INTEGER_MAX + 1, False),
    ],
)
def test_friendship_points_use_full_int32_wire_range(
    friendship_points: int,
    should_be_valid: bool,
) -> None:
    """好感点允许负数，但不能超出 C# ``int`` 可无损解析的范围。"""

    payload = _load_json(FIXTURE_ROOT / "dialogue_batch.json")
    payload["items"][0]["relationship_snapshot"]["friendship_points"] = friendship_points

    _assert_schema_and_pydantic_agree(
        schema_name="dialogue_generation_batch.schema.json",
        payload=payload,
        model=DialogueGenerationBatchRequest,
        should_be_valid=should_be_valid,
    )


@pytest.mark.parametrize(
    ("displayed_day_index", "should_be_valid"),
    [
        (WIRE_INTEGER_MAX, True),
        (WIRE_INTEGER_MAX + 1, False),
    ],
)
def test_displayed_day_uses_non_negative_int32_wire_range(
    displayed_day_index: int,
    should_be_valid: bool,
) -> None:
    """展示日必须在路由调用 ACK service 前满足非负 Int32 合同。"""

    payload = {
        "schema_version": "1.0",
        "request_id": "request-wire-display",
        "save_id": "save-wire",
        "player_id": "player-wire",
        "display_receipt_id": "receipt-wire",
        "displayed_day_index": displayed_day_index,
        "npc_id": "Abigail",
        "source_hash": "sha256:wire-source",
    }

    _assert_schema_and_pydantic_agree(
        schema_name="dialogue_display_ack.schema.json",
        payload=payload,
        model=DisplayAckRequest,
        should_be_valid=should_be_valid,
    )


def test_boolean_never_masquerades_as_any_public_wire_integer() -> None:
    """JSON Schema 与 strict Pydantic 必须一致拒绝所有目标字段中的 ``true``。"""

    event_payload = _load_json(FIXTURE_ROOT / "event_batch.json")
    event_payload["events"][0]["occurred_day_index"] = True
    _assert_schema_and_pydantic_agree(
        schema_name="game_event_batch.schema.json",
        payload=event_payload,
        model=GameEventBatchRequest,
        should_be_valid=False,
    )

    for field_name in ("game_day_index", "required_memory_revision"):
        dialogue_payload = _load_json(FIXTURE_ROOT / "dialogue_batch.json")
        dialogue_payload[field_name] = True
        _assert_schema_and_pydantic_agree(
            schema_name="dialogue_generation_batch.schema.json",
            payload=dialogue_payload,
            model=DialogueGenerationBatchRequest,
            should_be_valid=False,
        )

    friendship_payload = _load_json(FIXTURE_ROOT / "dialogue_batch.json")
    friendship_payload["items"][0]["relationship_snapshot"]["friendship_points"] = True
    _assert_schema_and_pydantic_agree(
        schema_name="dialogue_generation_batch.schema.json",
        payload=friendship_payload,
        model=DialogueGenerationBatchRequest,
        should_be_valid=False,
    )

    ack_payload = {
        "schema_version": "1.0",
        "request_id": "request-wire-bool",
        "save_id": "save-wire",
        "player_id": "player-wire",
        "display_receipt_id": "receipt-wire-bool",
        "displayed_day_index": True,
        "npc_id": "Abigail",
        "source_hash": "sha256:wire-bool",
    }
    _assert_schema_and_pydantic_agree(
        schema_name="dialogue_display_ack.schema.json",
        payload=ack_payload,
        model=DisplayAckRequest,
        should_be_valid=False,
    )


@pytest.mark.parametrize(
    ("memory_revision", "committed_day", "should_be_valid"),
    [
        (0, -1, True),
        (WIRE_INTEGER_MAX, WIRE_INTEGER_MAX, True),
        (WIRE_INTEGER_MAX + 1, 0, False),
        (0, WIRE_INTEGER_MAX + 1, False),
        (0, -2, False),
    ],
)
def test_event_response_watermarks_remain_representable_by_csharp(
    memory_revision: int,
    committed_day: int,
    should_be_valid: bool,
) -> None:
    """响应 revision 为非负 Int32；空分区水位仅额外允许哨兵值 ``-1``。"""

    payload = {
        "schema_version": "1.0",
        "request_id": "request-wire-response",
        "memory_revision": memory_revision,
        "committed_through_day_index": committed_day,
        "items": [
            {
                "event_id": "event-wire-response",
                "status": "accepted",
                "reason_code": None,
            }
        ],
    }

    if should_be_valid:
        GameEventBatchResponse.model_validate(payload)
    else:
        with pytest.raises(ValidationError):
            GameEventBatchResponse.model_validate(payload)


@pytest.mark.parametrize(
    ("memory_revision", "should_be_valid"),
    [
        (WIRE_INTEGER_MAX, True),
        (WIRE_INTEGER_MAX + 1, False),
    ],
)
def test_dialogue_response_revision_uses_non_negative_int32_wire_range(
    memory_revision: int,
    should_be_valid: bool,
) -> None:
    """生成响应不能返回 C# 客户端无法解析的 revision。"""

    payload = deepcopy(_load_json(FIXTURE_ROOT / "dialogue_batch_response.json"))
    payload["memory_revision"] = memory_revision

    if should_be_valid:
        DialogueGenerationBatchResponse.model_validate(payload)
    else:
        with pytest.raises(ValidationError):
            DialogueGenerationBatchResponse.model_validate(payload)
