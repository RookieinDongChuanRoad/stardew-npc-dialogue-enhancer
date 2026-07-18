"""共享 JSON 合同的回归测试。

这些测试先从仓库根目录读取 C# 与 Python 将共同消费的 fixture，再分别交给
JSON Schema 和 Pydantic 校验。测试刻意不复制 fixture 内容，避免两套测试数据
逐渐漂移后仍各自通过。
"""

from __future__ import annotations

import json
from copy import deepcopy
from importlib import import_module
from pathlib import Path
from typing import Any

import pytest
from jsonschema import Draft202012Validator
from jsonschema.exceptions import ValidationError as JsonSchemaValidationError
from pydantic import ValidationError as PydanticValidationError

PROJECT_ROOT = Path(__file__).resolve().parents[3]
CONTRACT_ROOT = PROJECT_ROOT / "contracts"
FIXTURE_ROOT = CONTRACT_ROOT / "fixtures"
SCHEMA_ROOT = CONTRACT_ROOT / "v1"


def _load_json(path: Path) -> dict[str, Any]:
    """读取一个 JSON object fixture，并在数据形状错误时立即给出清晰失败。"""

    with path.open(encoding="utf-8") as file_handle:
        value = json.load(file_handle)
    assert isinstance(value, dict), f"合同 fixture 顶层必须是 object: {path}"
    return value


def _event_request_rejection_flags(payload: dict[str, Any]) -> tuple[bool, bool]:
    """同时运行 JSON Schema 与 Pydantic，返回两者是否拒绝同一事件请求。

    联合结果用于防止测试在第一套校验器处短路。wire contract 的关键不是“至少一端
    拒绝”，而是共享 Schema 的结构/数值约束与 Python DTO 对常规 JSON 类型给出相同
    判断。整数 token 的词法限制由 ``x-stardew-json-integer-token`` 与两端 raw JSON
    入口执行，因为标准 Draft 2020-12 会把数学值 ``13.0`` 也视为 integer。
    """

    schemas = import_module("stardew_npc_agent.schemas")
    schema = _load_json(SCHEMA_ROOT / "game_event_batch.schema.json")
    schema_rejected = bool(list(Draft202012Validator(schema).iter_errors(payload)))
    pydantic_rejected = False
    try:
        schemas.GameEventBatchRequest.model_validate(payload)
    except PydanticValidationError:
        pydantic_rejected = True
    return schema_rejected, pydantic_rejected


@pytest.mark.parametrize(
    ("schema_name", "fixture_name"),
    [
        ("game_event_batch.schema.json", "event_batch.json"),
        ("dialogue_generation_batch.schema.json", "dialogue_batch.json"),
    ],
)
def test_request_fixture_matches_shared_json_schema(
    schema_name: str,
    fixture_name: str,
) -> None:
    """两个业务批次 fixture 必须满足可跨语言复用的 Draft 2020-12 Schema。"""

    schema = _load_json(SCHEMA_ROOT / schema_name)
    Draft202012Validator.check_schema(schema)
    Draft202012Validator(schema).validate(_load_json(FIXTURE_ROOT / fixture_name))


def test_display_ack_payload_matches_shared_json_schema() -> None:
    """展示 ACK 没有单独 fixture，因此用最小合法 payload 固定其 wire contract。"""

    schema = _load_json(SCHEMA_ROOT / "dialogue_display_ack.schema.json")
    payload = {
        "schema_version": "1.0",
        "request_id": "request-display-ack-001",
        "save_id": "save-standard-farm-001",
        "player_id": "player-farmer-001",
        "display_receipt_id": "receipt-abigail-spring-15",
        "displayed_day_index": 14,
        "npc_id": "Abigail",
        "source_hash": "sha256:abigail-spring-mon-zh-cn",
    }

    Draft202012Validator.check_schema(schema)
    Draft202012Validator(schema).validate(payload)


def test_json_schema_rejects_unknown_schema_version_and_blank_business_id() -> None:
    """版本与业务身份字段是幂等边界，不能接受未知版本或纯空白 ID。"""

    schema = _load_json(SCHEMA_ROOT / "game_event_batch.schema.json")
    invalid_version = _load_json(FIXTURE_ROOT / "event_batch.json")
    invalid_version["schema_version"] = "2.0"
    blank_save_id = _load_json(FIXTURE_ROOT / "event_batch.json")
    blank_save_id["save_id"] = "   "
    validator = Draft202012Validator(schema)

    with pytest.raises(JsonSchemaValidationError):
        validator.validate(invalid_version)
    with pytest.raises(JsonSchemaValidationError):
        validator.validate(blank_save_id)


@pytest.mark.parametrize(
    "invalid_day_index",
    ["13", True],
    ids=["string", "boolean"],
)
def test_event_day_index_rejects_non_integer_json_types(invalid_day_index: Any) -> None:
    """字符串和布尔值不能由 Python 自动转换成游戏日整数。"""

    payload = _load_json(FIXTURE_ROOT / "event_batch.json")
    payload["events"][0]["occurred_day_index"] = invalid_day_index

    assert _event_request_rejection_flags(payload) == (True, True)


@pytest.mark.parametrize(
    "invalid_request_id",
    [
        pytest.param(" request-events-spring-15", id="leading-space"),
        pytest.param("request-events-spring-15 ", id="trailing-space"),
        pytest.param("\trequest-events-spring-15", id="leading-tab"),
        pytest.param("request-events-spring-15\t", id="trailing-tab"),
        pytest.param("\rrequest-events-spring-15", id="leading-cr"),
        pytest.param("request-events-spring-15\r", id="trailing-cr"),
        pytest.param("\nrequest-events-spring-15", id="leading-lf"),
        pytest.param("request-events-spring-15\n", id="trailing-lf"),
    ],
)
def test_business_id_rejects_all_edge_whitespace(invalid_request_id: str) -> None:
    """两端必须一致拒绝 ID 开头或结尾的 space、tab、CR 与 LF。"""

    payload = _load_json(FIXTURE_ROOT / "event_batch.json")
    payload["request_id"] = invalid_request_id

    assert _event_request_rejection_flags(payload) == (True, True)


@pytest.mark.parametrize(
    "valid_request_id",
    [
        pytest.param("request events spring 15", id="internal-space"),
        pytest.param("request\tevents\tspring\t15", id="internal-tab"),
        pytest.param("request\r\nevents spring 15", id="internal-crlf"),
    ],
)
def test_business_id_preserves_legal_internal_whitespace(valid_request_id: str) -> None:
    """内部空白必须合法且在 Pydantic round-trip 中保持逐字符不变。"""

    schemas = import_module("stardew_npc_agent.schemas")
    payload = _load_json(FIXTURE_ROOT / "event_batch.json")
    payload["request_id"] = valid_request_id

    assert _event_request_rejection_flags(payload) == (False, False)
    parsed_request = schemas.GameEventBatchRequest.model_validate(payload)
    assert parsed_request.request_id == valid_request_id
    assert parsed_request.model_dump(mode="json") == payload


@pytest.mark.parametrize(
    ("audience_scope", "audience_npc_id"),
    [
        ("public", "Abigail"),
        ("npc", None),
    ],
)
def test_event_audience_scope_requires_matching_npc_id(
    audience_scope: str,
    audience_npc_id: str | None,
) -> None:
    """公共事实只能使用 null，NPC 私有事实必须携带非空内部 NPC ID。"""

    payload = _load_json(FIXTURE_ROOT / "event_batch.json")
    payload["events"][0]["audience_scope"] = audience_scope
    payload["events"][0]["audience_npc_id"] = audience_npc_id

    assert _event_request_rejection_flags(payload) == (True, True)


def test_pydantic_models_parse_all_shared_fixtures() -> None:
    """Python DTO 必须无损解析并序列化三个共享 fixture。"""

    schemas = import_module("stardew_npc_agent.schemas")
    event_payload = _load_json(FIXTURE_ROOT / "event_batch.json")
    dialogue_payload = _load_json(FIXTURE_ROOT / "dialogue_batch.json")
    response_payload = _load_json(FIXTURE_ROOT / "dialogue_batch_response.json")

    event_request = schemas.GameEventBatchRequest.model_validate(event_payload)
    dialogue_request = schemas.DialogueGenerationBatchRequest.model_validate(dialogue_payload)
    dialogue_response = schemas.DialogueGenerationBatchResponse.model_validate(response_payload)

    assert event_request.events[0].audience_npc_id == "Abigail"
    assert dialogue_request.stable_day_context.locale == "zh-CN"
    assert [item.npc_id for item in dialogue_request.items] == ["Abigail", "Sebastian"]
    assert [item.status for item in dialogue_response.items] == ["generated", "passthrough"]
    # 严格相等可发现字段被静默丢弃、默认值被意外注入或嵌套结构发生漂移；只验证
    # “能解析”不足以证明 C# 与 Python 仍共享同一份 wire contract。
    assert event_request.model_dump(mode="json") == event_payload
    assert dialogue_request.model_dump(mode="json") == dialogue_payload
    assert dialogue_response.model_dump(mode="json") == response_payload


@pytest.mark.parametrize(
    ("example_count", "should_be_valid"),
    [
        (1, False),
        (2, True),
        (5, True),
        (6, False),
    ],
)
def test_style_examples_require_two_to_five_entries(
    example_count: int,
    should_be_valid: bool,
) -> None:
    """JSON Schema 与 Pydantic 必须共享 2～5 条风格样本的闭区间。"""

    schemas = import_module("stardew_npc_agent.schemas")
    schema = _load_json(SCHEMA_ROOT / "dialogue_generation_batch.schema.json")
    payload = _load_json(FIXTURE_ROOT / "dialogue_batch.json")
    payload["items"][0]["style_examples"] = [
        f"Abigail 原版风格样例 {index}" for index in range(example_count)
    ]
    validator = Draft202012Validator(schema)
    schema_errors = list(validator.iter_errors(payload))
    pydantic_error: PydanticValidationError | None = None
    try:
        schemas.DialogueGenerationBatchRequest.model_validate(payload)
    except PydanticValidationError as error:
        pydantic_error = error

    if should_be_valid:
        assert (bool(schema_errors), pydantic_error is not None) == (False, False)
        return

    # 用一个联合断言同时观察两套校验器，避免第一套失败后短路而没有真正证明
    # 另一套合同也拒绝越界值。
    assert (bool(schema_errors), pydantic_error is not None) == (True, True)


def test_pydantic_rejects_more_than_eight_dialogue_items() -> None:
    """批次上限属于资源保护合同，Python DTO 必须在业务执行前拒绝第九项。"""

    schemas = import_module("stardew_npc_agent.schemas")
    payload = _load_json(FIXTURE_ROOT / "dialogue_batch.json")
    template_item = payload["items"][0]
    payload["items"] = [deepcopy(template_item) for _ in range(9)]

    with pytest.raises(PydanticValidationError):
        schemas.DialogueGenerationBatchRequest.model_validate(payload)


@pytest.mark.parametrize(
    ("event_count", "should_be_valid"),
    [(64, True), (65, False)],
)
def test_event_batch_has_explicit_sixty_four_item_resource_limit(
    event_count: int,
    should_be_valid: bool,
) -> None:
    """事件批次上限为 64，避免开放 events 数组绕过资源保护边界。"""

    schemas = import_module("stardew_npc_agent.schemas")
    payload = _load_json(FIXTURE_ROOT / "event_batch.json")
    template_event = payload["events"][0]
    payload["events"] = []
    for index in range(event_count):
        event = deepcopy(template_event)
        event["event_id"] = f"event-batch-boundary-{index}"
        payload["events"].append(event)

    schema = _load_json(SCHEMA_ROOT / "game_event_batch.schema.json")
    schema_rejected = bool(list(Draft202012Validator(schema).iter_errors(payload)))
    pydantic_rejected = False
    parsed = None
    try:
        parsed = schemas.GameEventBatchRequest.model_validate(payload)
    except PydanticValidationError:
        pydantic_rejected = True

    if should_be_valid:
        assert (schema_rejected, pydantic_rejected) == (False, False)
        assert parsed is not None
        assert len(parsed.events) == 64
        return

    assert (schema_rejected, pydantic_rejected) == (True, True)


@pytest.mark.parametrize("status", ["passthrough", "skipped", "failed"])
def test_non_generated_dialogue_results_reject_text(status: str) -> None:
    """三个非 generated 终态都不能携带可展示文本。"""

    schemas = import_module("stardew_npc_agent.schemas")
    response_payload = _load_json(FIXTURE_ROOT / "dialogue_batch_response.json")
    non_generated_with_text = deepcopy(response_payload["items"][1])
    non_generated_with_text["status"] = status
    non_generated_with_text["text"] = f"不应由 {status} 返回的文本"

    with pytest.raises(PydanticValidationError):
        schemas.DialogueGenerationItemResult.model_validate(non_generated_with_text)


def test_generated_dialogue_result_requires_non_blank_text() -> None:
    """generated 终态必须携带非空白文本。"""

    schemas = import_module("stardew_npc_agent.schemas")
    response_payload = _load_json(FIXTURE_ROOT / "dialogue_batch_response.json")
    generated_without_text = deepcopy(response_payload["items"][0])
    generated_without_text["text"] = "   "

    with pytest.raises(PydanticValidationError):
        schemas.DialogueGenerationItemResult.model_validate(generated_without_text)


@pytest.mark.parametrize(
    "padded_text",
    [
        pytest.param(" padded", id="leading-space"),
        pytest.param("padded ", id="trailing-space"),
        pytest.param("\tpadded", id="leading-tab"),
        pytest.param("padded\r\n", id="trailing-crlf"),
    ],
)
def test_generated_dialogue_result_rejects_edge_whitespace_without_normalizing(
    padded_text: str,
) -> None:
    """generated 文本首尾空白必须直接拒绝，不能通过 strip 静默改写。"""

    schemas = import_module("stardew_npc_agent.schemas")
    response_payload = _load_json(FIXTURE_ROOT / "dialogue_batch_response.json")
    generated_with_padding = deepcopy(response_payload["items"][0])
    generated_with_padding["text"] = padded_text

    with pytest.raises(PydanticValidationError):
        schemas.DialogueGenerationItemResult.model_validate(generated_with_padding)

    assert generated_with_padding["text"] == padded_text


def test_generated_dialogue_result_preserves_internal_crlf_exactly() -> None:
    """generated 文本内部 CRLF 合法，Pydantic round-trip 必须逐字符保留。"""

    schemas = import_module("stardew_npc_agent.schemas")
    response_payload = _load_json(FIXTURE_ROOT / "dialogue_batch_response.json")
    generated_with_crlf = deepcopy(response_payload["items"][0])
    text_with_crlf = "第一行增强台词。\r\n第二行保持原样。"
    generated_with_crlf["text"] = text_with_crlf

    result = schemas.DialogueGenerationItemResult.model_validate(generated_with_crlf)

    assert result.text == text_with_crlf
    assert result.model_dump(mode="json")["text"] == text_with_crlf


def test_event_and_display_ack_response_dtos_cover_idempotent_statuses() -> None:
    """事件与展示 ACK 的逐项状态必须显式区分首次接受和重复提交。"""

    schemas = import_module("stardew_npc_agent.schemas")
    event_response = schemas.GameEventBatchResponse.model_validate(
        {
            "schema_version": "1.0",
            "request_id": "request-events-spring-15",
            "memory_revision": 42,
            "committed_through_day_index": 13,
            "items": [
                {
                    "event_id": "event-gift-abigail-spring-14",
                    "status": "accepted",
                    "reason_code": None,
                },
                {
                    "event_id": "event-pantry-completed-spring-14",
                    "status": "duplicate",
                    "reason_code": "EVENT_ALREADY_COMMITTED",
                },
                {
                    "event_id": "event-invalid-001",
                    "status": "rejected",
                    "reason_code": "INVALID_EVENT_PAYLOAD",
                },
            ],
        }
    )
    ack_request = schemas.DisplayAckRequest.model_validate(
        {
            "schema_version": "1.0",
            "request_id": "request-display-ack-001",
            "save_id": "save-standard-farm-001",
            "player_id": "player-farmer-001",
            "display_receipt_id": "receipt-abigail-spring-15",
            "displayed_day_index": 14,
            "npc_id": "Abigail",
            "source_hash": "sha256:abigail-spring-mon-zh-cn",
        }
    )
    duplicate_ack = schemas.DisplayAckResponse.model_validate(
        {
            "schema_version": "1.0",
            "request_id": "request-display-ack-001",
            "display_receipt_id": "receipt-abigail-spring-15",
            "status": "duplicate",
        }
    )

    assert [item.status for item in event_response.items] == [
        "accepted",
        "duplicate",
        "rejected",
    ]
    assert ack_request.npc_id == "Abigail"
    assert duplicate_ack.status == "duplicate"
