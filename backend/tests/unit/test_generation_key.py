"""Phase 4 generation key 与最小角色元数据的纯单元测试。

这些测试故意只使用已经冻结的 Pydantic 请求 DTO，避免 generation key 函数
接收任意 ``dict`` 后自行猜测字段。测试同时把“对象键无序、数组有序”的边界
写成可执行合同，后续切换 Agent/Prompt 时可据此判断是否需要主动换版本。
"""

from __future__ import annotations

import hashlib
import json

import pytest
from pydantic import SecretStr

import stardew_npc_agent.generation_key as generation_key_module
from stardew_npc_agent.config import Settings
from stardew_npc_agent.model_provider import build_provider_spec
from stardew_npc_agent.schemas import (
    DialogueGenerationBatchRequest,
    DialogueGenerationItem,
    RelationshipSnapshot,
    SourceDialogue,
    StableDayContext,
)


def _request(
    *,
    progression_signals: dict[str, object] | None = None,
    memory_signals: list[dict[str, object]] | None = None,
) -> DialogueGenerationBatchRequest:
    """构造一个最小合法批次，允许测试只替换正在验证的上下文字段。"""

    item = DialogueGenerationItem(
        task_id="task-abigail-day-14",
        npc_id="Abigail",
        source_dialogue=SourceDialogue(
            asset_name="Characters/Dialogue/Abigail",
            dialogue_key="spring_Mon",
            text="我喜欢雨天里的山谷。",
            source_hash="sha256:source-abigail-day-14",
        ),
        relationship_snapshot=RelationshipSnapshot(
            friendship_points=750,
            relationship_stage="friend",
        ),
        style_examples=["样本一。", "样本二。", "样本三。"],
        memory_signals=memory_signals
        if memory_signals is not None
        else [{"event": "gift", "details": {"item": "Amethyst", "taste": "love"}}],
    )
    return DialogueGenerationBatchRequest(
        schema_version="1.0",
        request_id="request-day-14",
        save_id="save-1",
        player_id="player-1",
        game_day_index=14,
        required_memory_revision=5,
        stable_day_context=StableDayContext(
            season="spring",
            weather="rain",
            locale="zh-CN",
            progression_signals=progression_signals
            if progression_signals is not None
            else {"community_center": {"pantry": True, "boiler": False}},
        ),
        items=[item],
    )


def _with_item(
    request: DialogueGenerationBatchRequest,
    item: DialogueGenerationItem,
) -> DialogueGenerationBatchRequest:
    """返回只替换首个 NPC 任务的请求，不修改其他 envelope 字段。"""

    return request.model_copy(update={"items": [item]})


def _build(
    request: DialogueGenerationBatchRequest,
    *,
    profile_version: str = "abigail-profile-v1",
    prompt_version: str = "scripted-prompt-v1",
    model_configuration: str = "scripted-passthrough",
    memory_projection_version: str = "memory-projection-v2",
    event_producer_capability_version: str = "event-producer-capabilities-v1",
    memory_classification_version: str = "memory-classification-v1",
    memory_retrieval_policy_version: str = "memory-retrieval-policy-v1",
    dialogue_source_policy_version: str = "dialogue-source-policy-v1",
    display_token_policy_version: str = "display-token-policy-v1",
    resolved_memory_revision: int | None = None,
    resolved_retrieval_state_revision: int = 0,
):  # type: ignore[no-untyped-def]
    """通过公开边界构造首个任务的 key，集中测试全部冻结版本轴。"""

    from stardew_npc_agent.generation_key import build_generation_key

    return build_generation_key(
        request,
        request.items[0],
        profile_version=profile_version,
        prompt_version=prompt_version,
        model_configuration=model_configuration,
        memory_projection_version=memory_projection_version,
        event_producer_capability_version=event_producer_capability_version,
        memory_classification_version=memory_classification_version,
        memory_retrieval_policy_version=memory_retrieval_policy_version,
        dialogue_source_policy_version=dialogue_source_policy_version,
        display_token_policy_version=display_token_policy_version,
        resolved_memory_revision=(
            request.required_memory_revision
            if resolved_memory_revision is None
            else resolved_memory_revision
        ),
        resolved_retrieval_state_revision=resolved_retrieval_state_revision,
    )


def _provider_identity(**overrides: object) -> str:
    """用真实 ProviderSpec 生成不访问网络的模型配置身份。"""

    values: dict[str, object] = {
        "dialogue_generator_mode": "agent",
        "provider_id": "openai",
        "provider_model": "model-a",
        "provider_api_key": SecretStr("not-a-real-key"),
        "_env_file": None,
    }
    values.update(overrides)
    return build_provider_spec(Settings(**values)).runtime_identity


def test_generation_key_ignores_recursive_json_object_key_order() -> None:
    """JSON object 没有业务顺序；嵌套键换序不得制造重复生成。"""

    first = _request(
        progression_signals={"community_center": {"pantry": True, "boiler": False}},
        memory_signals=[{"event": "gift", "details": {"item": "Amethyst", "taste": "love"}}],
    )
    second = _request(
        progression_signals={"community_center": {"boiler": False, "pantry": True}},
        memory_signals=[{"details": {"taste": "love", "item": "Amethyst"}, "event": "gift"}],
    )

    first_key = _build(first)
    second_key = _build(second)

    assert first_key == second_key


def test_generation_key_preserves_array_order() -> None:
    """style example 的先后是模型输入语义；换序必须主动生成另一把 key。"""

    request = _request()
    item = request.items[0]
    reordered = _with_item(
        request,
        item.model_copy(update={"style_examples": list(reversed(item.style_examples))}),
    )

    assert _build(request).generation_key != _build(reordered).generation_key
    assert _build(request).context_hash != _build(reordered).context_hash


def test_request_and_task_transport_ids_do_not_change_generation_key() -> None:
    """传输重试可换 request/task ID，但不能重复调用模型或产生新结果。"""

    request = _request()
    retried_item = request.items[0].model_copy(update={"task_id": "retry-task-id"})
    retried = request.model_copy(update={"request_id": "retry-request-id", "items": [retried_item]})

    assert _build(request) == _build(retried)


def test_generation_key_changes_for_every_frozen_identity_and_version_input() -> None:
    """幂等边界中的身份、日期、水位和实现版本任一变化都必须 cache miss。"""

    request = _request()
    item = request.items[0]
    source = item.source_dialogue

    changed_requests = {
        "schema_version": request.model_copy(update={"schema_version": "future-schema"}),
        "save_id": request.model_copy(update={"save_id": "save-2"}),
        "player_id": request.model_copy(update={"player_id": "player-2"}),
        "game_day_index": request.model_copy(update={"game_day_index": 15}),
        "npc_id": _with_item(request, item.model_copy(update={"npc_id": "Sebastian"})),
        "source_hash": _with_item(
            request,
            item.model_copy(
                update={
                    "source_dialogue": source.model_copy(
                        update={"source_hash": "sha256:different-source"}
                    )
                }
            ),
        ),
        "source_text": _with_item(
            request,
            item.model_copy(
                update={"source_dialogue": source.model_copy(update={"text": "正文已经改变。"})}
            ),
        ),
        "locale": request.model_copy(
            update={
                "stable_day_context": request.stable_day_context.model_copy(update={"locale": "en"})
            }
        ),
        "required_memory_revision": request.model_copy(update={"required_memory_revision": 6}),
        "relationship": _with_item(
            request,
            item.model_copy(
                update={
                    "relationship_snapshot": item.relationship_snapshot.model_copy(
                        update={"friendship_points": 751}
                    )
                }
            ),
        ),
        "memory_signal": _with_item(
            request,
            item.model_copy(update={"memory_signals": [{"event": "quest"}]}),
        ),
        "stable_day_context": request.model_copy(
            update={
                "stable_day_context": request.stable_day_context.model_copy(
                    update={"weather": "sun"}
                )
            }
        ),
    }
    original = _build(request).generation_key

    for field_name, changed_request in changed_requests.items():
        assert _build(changed_request).generation_key != original, field_name

    assert _build(request, profile_version="abigail-profile-v2").generation_key != original
    assert _build(request, prompt_version="scripted-prompt-v2").generation_key != original
    assert _build(request, model_configuration="another-scripted-model").generation_key != original
    assert (
        _build(request, memory_projection_version="memory-projection-v3").generation_key != original
    )
    assert (
        _build(
            request,
            event_producer_capability_version="event-producer-capabilities-v2",
        ).generation_key
        != original
    )
    assert (
        _build(
            request,
            memory_classification_version="memory-classification-v2",
        ).generation_key
        != original
    )
    assert (
        _build(
            request,
            memory_retrieval_policy_version="memory-retrieval-policy-v2",
        ).generation_key
        != original
    )
    assert (
        _build(
            request,
            dialogue_source_policy_version="dialogue-source-policy-v2",
        ).generation_key
        != original
    )
    assert (
        _build(
            request,
            display_token_policy_version="display-token-policy-v2",
        ).generation_key
        != original
    )
    assert _build(request, resolved_memory_revision=6).generation_key != original
    assert _build(request, resolved_retrieval_state_revision=1).generation_key != original


def test_generation_key_v4_includes_source_and_display_policy_versions() -> None:
    """source 与 display token 两条策略轴都必须独立制造 cache miss。"""

    assert generation_key_module.GENERATION_KEY_FORMAT_VERSION == "generation-key-v4"
    source_policy_version = getattr(
        generation_key_module,
        "DIALOGUE_SOURCE_POLICY_VERSION",
        None,
    )
    assert source_policy_version == "dialogue-source-policy-v1"
    display_policy_version = getattr(
        generation_key_module,
        "DISPLAY_TOKEN_POLICY_VERSION",
        None,
    )
    assert display_policy_version == "display-token-policy-v1"

    request = _request()
    common = {
        "profile_version": "abigail-profile-v1",
        "memory_projection_version": "memory-projection-v3",
        "resolved_memory_revision": request.required_memory_revision,
        "resolved_retrieval_state_revision": 0,
    }
    current = generation_key_module.build_generation_key(
        request,
        request.items[0],
        dialogue_source_policy_version=source_policy_version,
        **common,
    )
    changed = generation_key_module.build_generation_key(
        request,
        request.items[0],
        dialogue_source_policy_version="dialogue-source-policy-v2",
        **common,
    )
    display_changed = generation_key_module.build_generation_key(
        request,
        request.items[0],
        dialogue_source_policy_version=source_policy_version,
        display_token_policy_version="display-token-policy-v2",
        **common,
    )

    assert current.generation_key != changed.generation_key
    assert current.generation_key != display_changed.generation_key


def test_generation_key_rejects_resolved_memory_revision_below_required_lower_bound() -> None:
    """resolved memory 水位是实际快照，不能低于公开请求 required 下界。"""

    request = _request()

    with pytest.raises(ValueError, match="resolved_memory_revision"):
        _build(request, resolved_memory_revision=4)


def test_provider_model_protocol_and_endpoint_each_change_generation_key() -> None:
    """Provider 语义变化必须沿正式 identity 进入既有 canonical generation key。"""

    request = _request()
    configurations = [
        _provider_identity(),
        _provider_identity(provider_model="model-b"),
        _provider_identity(provider_wire_api="responses"),
        _provider_identity(
            provider_id="openai_compatible",
            provider_base_url="https://one.example/v1",
        ),
        _provider_identity(
            provider_id="openai_compatible",
            provider_base_url="https://two.example/v1",
        ),
    ]

    keys = {
        _build(request, model_configuration=configuration).generation_key
        for configuration in configurations
    }

    assert len(keys) == len(configurations)


def test_rotating_provider_key_does_not_invalidate_generation_cache() -> None:
    """凭据轮换既不能进入 identity，也不能制造同一输入的重复模型费用。"""

    request = _request()
    first = _provider_identity(provider_api_key=SecretStr("key-one"))
    second = _provider_identity(provider_api_key=SecretStr("key-two"))

    assert first == second
    assert _build(request, model_configuration=first) == _build(
        request,
        model_configuration=second,
    )


def test_context_hash_uses_utf8_without_ascii_escape_loss() -> None:
    """中文正文必须以 UTF-8 canonical JSON 参与摘要，不能先转成 ``\\u`` 文本。"""

    request = _request()
    result = _build(request)
    item = request.items[0]
    expected_context = {
        "memory_signals": item.memory_signals,
        "relationship": item.relationship_snapshot.model_dump(mode="json"),
        "source_dialogue": item.source_dialogue.model_dump(mode="json"),
        "stable_day_context": request.stable_day_context.model_dump(mode="json"),
        "style_examples": item.style_examples,
    }
    utf8_bytes = json.dumps(
        expected_context,
        ensure_ascii=False,
        sort_keys=True,
        separators=(",", ":"),
        allow_nan=False,
    ).encode("utf-8")
    ascii_escaped_bytes = json.dumps(
        expected_context,
        ensure_ascii=True,
        sort_keys=True,
        separators=(",", ":"),
        allow_nan=False,
    ).encode("utf-8")

    assert result.context_hash == f"sha256:{hashlib.sha256(utf8_bytes).hexdigest()}"
    assert result.context_hash != f"sha256:{hashlib.sha256(ascii_escaped_bytes).hexdigest()}"
    assert result.generation_key.startswith("sha256:")


def test_phase4_profiles_expose_only_version_locale_and_memory_cooldown_metadata() -> None:
    """Phase 4 只冻结生成所需元数据，不提前把 persona/Prompt 混入 profile 模块。"""

    from stardew_npc_agent.profiles import get_npc_profile

    abigail = get_npc_profile("Abigail")
    sebastian = get_npc_profile("Sebastian")

    assert abigail is not None
    assert abigail.npc_id == "Abigail"
    assert abigail.profile_version == "abigail-profile-v1"
    assert abigail.supported_locales == frozenset({"en", "zh-CN"})
    assert abigail.memory_cooldown_days == 3

    assert sebastian is not None
    assert sebastian.npc_id == "Sebastian"
    assert sebastian.profile_version == "sebastian-profile-v1"
    assert sebastian.supported_locales == frozenset({"en", "zh-CN"})
    assert sebastian.memory_cooldown_days == 3
    assert get_npc_profile("UnknownNpc") is None
