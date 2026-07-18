"""Phase 7 后端生产组合根与批次 deadline 集成测试。

这些测试只使用临时 SQLite、ASGI transport 与本地 scripted ``BaseChatModel``。
它们不会启动端口、读取通用 OpenAI 环境变量、调用真实 Provider 或产生费用。
"""

from __future__ import annotations

import asyncio
from collections.abc import Sequence
from typing import Any

import httpx
import pytest
from langchain_core.callbacks import (
    AsyncCallbackManagerForLLMRun,
    CallbackManagerForLLMRun,
)
from langchain_core.language_models import BaseChatModel
from langchain_core.messages import AIMessage, BaseMessage
from langchain_core.outputs import ChatGeneration, ChatResult
from langchain_core.runnables import Runnable
from pydantic import PrivateAttr, SecretStr, ValidationError
from sqlalchemy import func, select
from typing_extensions import override

from stardew_npc_agent.api import create_app
from stardew_npc_agent.config import Settings
from stardew_npc_agent.dialogue_agent import (
    TARGET_DIALOGUE_DOMAIN_TOOL_NAMES,
    AgentBackedDialogueGenerator,
)
from stardew_npc_agent.dialogue_service import DialogueGeneratorDecision, DialogueService
from stardew_npc_agent.model_provider import ModelProviderSpec
from stardew_npc_agent.schemas import (
    DialogueGenerationBatchRequest,
    DialogueGenerationItem,
)
from stardew_npc_agent.storage import (
    DialogueDisplayReceiptRecord,
    DialogueGenerationRecord,
    MemoryRecord,
    ProgressionContextQuery,
    SqliteStorage,
)


class _RuntimeScriptedModel(BaseChatModel):
    """让正式 ``create_agent`` 组合根返回可预测 structured final 的零网络模型。"""

    _steps: list[AIMessage] = PrivateAttr()
    _physical_calls: int = PrivateAttr(default=0)

    def __init__(self, steps: Sequence[AIMessage]) -> None:
        super().__init__()
        self._steps = list(steps)

    @property
    def physical_calls(self) -> int:
        """返回真实模型调用次数，证明 capabilities 探测不会触发模型。"""

        return self._physical_calls

    def queue_steps(self, steps: Sequence[AIMessage]) -> None:
        """在 runtime 已装配后追加响应，供先写入真实 evidence 再生成的集成测试使用。"""

        self._steps.extend(steps)

    @property
    @override
    def _llm_type(self) -> str:
        return "phase7-runtime-scripted-model"

    @override
    def bind_tools(
        self,
        tools: Sequence[dict[str, Any] | type | Any],
        *,
        tool_choice: str | None = None,
        **kwargs: Any,
    ) -> Runnable[Any, BaseMessage]:
        """保留真实 LangChain tool/structured-output 绑定，但不执行外部调用。"""

        del tools, tool_choice, kwargs
        return self

    @override
    def _generate(
        self,
        messages: list[BaseMessage],
        stop: list[str] | None = None,
        run_manager: CallbackManagerForLLMRun | None = None,
        **kwargs: Any,
    ) -> ChatResult:
        del messages, stop, run_manager, kwargs
        return self._next_result()

    @override
    async def _agenerate(
        self,
        messages: list[BaseMessage],
        stop: list[str] | None = None,
        run_manager: AsyncCallbackManagerForLLMRun | None = None,
        **kwargs: Any,
    ) -> ChatResult:
        del messages, stop, run_manager, kwargs
        return self._next_result()

    def _next_result(self) -> ChatResult:
        self._physical_calls += 1
        if not self._steps:
            raise AssertionError("runtime scripted model steps exhausted")
        return ChatResult(generations=[ChatGeneration(message=self._steps.pop(0))])


def _tool_call(name: str, call_id: str, args: dict[str, object]) -> AIMessage:
    """构造 LangChain 标准领域工具调用；执行仍由正式 Agent graph 完成。"""

    return AIMessage(
        content="",
        tool_calls=[{"name": name, "id": call_id, "args": args, "type": "tool_call"}],
    )


def _passthrough_final(*, call_id: str = "phase7-final") -> AIMessage:
    """构造 ToolStrategy 可解析的正常 passthrough final。"""

    return AIMessage(
        content="",
        tool_calls=[
            {
                "name": "DialogueAgentDecision",
                "id": call_id,
                "args": {
                    "decision": "passthrough",
                    "template": None,
                    "evidence_ids": [],
                    "reason_code": "NO_VALUABLE_ENHANCEMENT",
                },
                "type": "tool_call",
            }
        ],
    )


def _memory_rewrite_final(evidence_id: str) -> AIMessage:
    """构造引用真实工具 artifact 的 Abigail rewrite structured final。"""

    return _tool_call(
        "DialogueAgentDecision",
        "phase7-abigail-final",
        {
            "decision": "rewrite",
            "template": {
                "prefix": "矿井里昨天又有了新进展，听起来还不错。",
                "address_slot": "none",
                "suffix": "",
            },
            "evidence_ids": [evidence_id],
            "reason_code": "RELEVANT_WORLD_PROGRESSION",
        },
    )


def _generation_payload() -> dict[str, object]:
    """返回生产组合根测试使用的合法单 NPC 请求。"""

    return {
        "schema_version": "1.0",
        "request_id": "request-phase7-runtime",
        "save_id": "save-phase7-runtime",
        "player_id": "player-phase7-runtime",
        "game_day_index": 10,
        "required_memory_revision": 0,
        "stable_day_context": {
            "season": "fall",
            "weather": "rain",
            "locale": "zh-CN",
            "progression_signals": {"mine_level": 40},
        },
        "items": [
            {
                "task_id": "task-phase7-runtime-abigail",
                "npc_id": "Abigail",
                "source_dialogue": {
                    "asset_name": "Characters/Dialogue/Abigail",
                    "dialogue_key": "fall_Mon",
                    "text": "雨天待在屋里也不算太糟。",
                    "source_hash": "sha256:phase7-runtime-abigail",
                },
                "relationship_snapshot": {
                    "friendship_points": 750,
                    "relationship_stage": "acquaintance",
                },
                "style_examples": ["样本一。", "样本二。", "样本三。"],
                "memory_signals": [],
            }
        ],
    }


def _two_npc_memory_payload() -> dict[str, object]:
    """返回昨日 progression 已就绪时的两 NPC 正式批量生成请求。"""

    return {
        "schema_version": "1.0",
        "request_id": "request-phase7-two-npc-memory",
        "save_id": "save-phase7-memory",
        "player_id": "player-phase7-memory",
        "game_day_index": 10,
        "required_memory_revision": 1,
        "stable_day_context": {
            "season": "fall",
            "weather": "rain",
            "locale": "zh-CN",
            "progression_signals": {"mine_level": 5},
        },
        "items": [
            {
                "task_id": "task-phase7-memory-abigail",
                "npc_id": "Abigail",
                "source_dialogue": {
                    "asset_name": "Characters/Dialogue/Abigail",
                    "dialogue_key": "fall_Mon",
                    "text": "矿井里今天看起来还是老样子。",
                    "source_hash": "sha256:phase7-memory-abigail",
                },
                "relationship_snapshot": {
                    "friendship_points": 1250,
                    "relationship_stage": "friend",
                },
                "style_examples": ["样本一。", "样本二。", "样本三。"],
                "memory_signals": [{"event_type": "world_progression"}],
            },
            {
                "task_id": "task-phase7-memory-sebastian",
                "npc_id": "Sebastian",
                "source_dialogue": {
                    "asset_name": "Characters/Dialogue/Sebastian",
                    "dialogue_key": "fall_Mon",
                    "text": "这种雨天待在屋里也不坏。",
                    "source_hash": "sha256:phase7-memory-sebastian",
                },
                "relationship_snapshot": {
                    "friendship_points": 500,
                    "relationship_stage": "acquaintance",
                },
                "style_examples": ["样本一。", "样本二。", "样本三。"],
                "memory_signals": [],
            },
        ],
    }


@pytest.mark.asyncio
async def test_default_scripted_runtime_ignores_generic_openai_key_and_reports_no_agent(
    monkeypatch: pytest.MonkeyPatch,
    migrated_database_url: str,
) -> None:
    """默认模式必须零 Provider：通用 key 存在也不能隐式启用或调用模型。"""

    sentinel = "generic-key-must-not-enable-provider"
    monkeypatch.setenv("OPENAI_API_KEY", sentinel)

    import stardew_npc_agent.runtime as runtime_module

    def fail_if_provider_is_built(*_args: object, **_kwargs: object) -> object:
        raise AssertionError("scripted runtime 不得构造 Provider spec 或模型")

    monkeypatch.setattr(runtime_module, "build_provider_spec", fail_if_provider_is_built)
    monkeypatch.setattr(runtime_module, "build_chat_model", fail_if_provider_is_built)
    app = create_app(Settings(database_url=migrated_database_url, _env_file=None))
    transport = httpx.ASGITransport(app=app)
    async with app.router.lifespan_context(app):
        async with httpx.AsyncClient(transport=transport, base_url="http://testserver") as client:
            capabilities = await client.get("/api/v1/capabilities")

    assert capabilities.status_code == 200
    assert capabilities.json()["provider_configured"] is False
    assert capabilities.json()["tool_calling"] is False
    assert capabilities.json()["structured_output"] is False
    assert sentinel not in capabilities.text
    assert not hasattr(app.state, "settings"), "含秘密的完整 Settings 不得进入 app.state"


@pytest.mark.parametrize(
    "settings_kwargs",
    [
        {
            "dialogue_generator_mode": "agent",
            "provider_id": "openai",
            "provider_api_key": SecretStr("secret-without-model"),
        },
        {
            "dialogue_generator_mode": "agent",
            "provider_id": "openai",
            "provider_model": "explicit-test-model",
        },
    ],
    ids=["missing-model", "missing-project-key"],
)
def test_agent_mode_requires_explicit_project_configuration_without_secret_leak(
    settings_kwargs: dict[str, object],
) -> None:
    """显式 Agent 模式缺配置必须在构造期失败，且 ValidationError 不回显 key。"""

    with pytest.raises(ValidationError) as error_info:
        Settings(_env_file=None, **settings_kwargs)

    error_text = str(error_info.value)
    assert "agent" in error_text.lower()
    assert "secret-without-model" not in error_text


@pytest.mark.asyncio
async def test_runtime_uses_provider_spec_identity_and_factory_model(
    monkeypatch: pytest.MonkeyPatch,
    migrated_database_url: str,
) -> None:
    """正式组合根必须把同一 ProviderSpec 同时用于模型构造和 generation identity。"""

    import stardew_npc_agent.runtime as runtime_module

    captured_specs: list[ModelProviderSpec] = []
    sentinel_model = _RuntimeScriptedModel([_passthrough_final()])

    def capture_chat_model(
        _settings: Settings,
        spec: ModelProviderSpec,
    ) -> BaseChatModel:
        captured_specs.append(spec)
        return sentinel_model

    monkeypatch.setattr(runtime_module, "build_chat_model", capture_chat_model)
    settings = Settings(
        database_url=migrated_database_url,
        dialogue_generator_mode="agent",
        provider_id="openai",
        provider_model="explicit-test-model",
        provider_api_key=SecretStr("not-a-real-key"),
        provider_request_timeout_seconds=17.0,
        provider_max_output_tokens=192,
        _env_file=None,
    )
    app = create_app(settings)

    async with app.router.lifespan_context(app):
        generator = app.state.dialogue_service._generator
        assert isinstance(generator, AgentBackedDialogueGenerator)
        assert len(captured_specs) == 1
        assert (
            generator.generation_identity.model_configuration == captured_specs[0].runtime_identity
        )


@pytest.mark.asyncio
async def test_agent_runtime_atomically_publishes_only_target_domain_tools(
    migrated_database_url: str,
) -> None:
    """生产组合根必须同时切换 Factory registry 与每次生成的 allowlist。"""

    model = _RuntimeScriptedModel([])
    settings = Settings(
        database_url=migrated_database_url,
        dialogue_generator_mode="agent",
        provider_id="openai",
        provider_model="atomic-domain-activation-test-model",
        provider_api_key=SecretStr("not-a-real-key"),
        _env_file=None,
    )
    app = create_app(settings, model_override=model)

    async with app.router.lifespan_context(app):
        generator = app.state.dialogue_service._generator
        assert isinstance(generator, AgentBackedDialogueGenerator)
        assert generator._runner.factory.available_tool_names == TARGET_DIALOGUE_DOMAIN_TOOL_NAMES
        assert generator._allowed_tools == TARGET_DIALOGUE_DOMAIN_TOOL_NAMES
        assert model.physical_calls == 0


@pytest.mark.asyncio
async def test_owned_model_is_closed_but_override_is_not(
    monkeypatch: pytest.MonkeyPatch,
    migrated_database_url: str,
) -> None:
    """应用只关闭组合根自己创建的模型，调用方注入的 override 保持调用方所有。"""

    import stardew_npc_agent.runtime as runtime_module

    closed: list[BaseChatModel] = []
    owned = _RuntimeScriptedModel([_passthrough_final()])

    def return_owned_model(
        _settings: Settings,
        _spec: ModelProviderSpec,
    ) -> BaseChatModel:
        return owned

    async def capture_close(model: BaseChatModel) -> None:
        closed.append(model)

    monkeypatch.setattr(runtime_module, "build_chat_model", return_owned_model)
    monkeypatch.setattr(runtime_module, "close_owned_chat_model", capture_close)
    settings = Settings(
        database_url=migrated_database_url,
        dialogue_generator_mode="agent",
        provider_id="openai",
        provider_model="owned-test-model",
        provider_api_key=SecretStr("not-a-real-key"),
        _env_file=None,
    )

    owned_app = create_app(settings)
    async with owned_app.router.lifespan_context(owned_app):
        pass
    assert closed == [owned]

    closed.clear()
    override = _RuntimeScriptedModel([_passthrough_final()])
    override_app = create_app(settings, model_override=override)
    async with override_app.router.lifespan_context(override_app):
        pass
    assert closed == []


@pytest.mark.asyncio
async def test_explicit_agent_runtime_uses_formal_pipeline_and_true_capabilities(
    migrated_database_url: str,
) -> None:
    """capabilities 只有在正式 AgentBacked 组合根完成后才允许为 true。"""

    secret = "phase7-provider-secret-must-stay-hidden"
    model = _RuntimeScriptedModel([_passthrough_final()])
    settings = Settings(
        database_url=migrated_database_url,
        dialogue_generator_mode="agent",
        provider_id="openai",
        provider_model="scripted-runtime-model",
        provider_api_key=SecretStr(secret),
        _env_file=None,
    )
    app = create_app(settings, model_override=model)
    transport = httpx.ASGITransport(app=app)
    async with app.router.lifespan_context(app):
        async with httpx.AsyncClient(transport=transport, base_url="http://testserver") as client:
            capabilities = await client.get("/api/v1/capabilities")
            assert model.physical_calls == 0
            generation = await client.post(
                "/api/v1/dialogue-generations/batch",
                json=_generation_payload(),
            )

    assert capabilities.status_code == 200
    assert capabilities.json() == {
        "schema_versions": ["1.0"],
        "locales": ["zh-CN", "en"],
        "batch_max_items": 8,
        "provider_configured": True,
        "tool_calling": True,
        "structured_output": True,
    }
    assert generation.status_code == 200
    assert generation.json()["items"][0]["status"] == "passthrough"
    assert generation.json()["items"][0]["reason_code"] == "NO_VALUABLE_ENHANCEMENT"
    assert model.physical_calls == 1
    assert isinstance(app.state.dialogue_service._generator, AgentBackedDialogueGenerator)
    assert app.state.event_service._storage is app.state.dialogue_service._storage
    assert app.state.dialogue_service._generator.storage is app.state.dialogue_service._storage
    assert secret not in capabilities.text
    assert secret not in generation.text


@pytest.mark.asyncio
async def test_two_npc_formal_agent_reads_player_progression_then_ack_applies_cooldown_once(
    migrated_database_url: str,
) -> None:
    """正式组合根应把昨日事件、工具 evidence、双 NPC 终态和 displayed 冷却闭环。

    测试刻意让 Abigail 自主调用 progression 工具并 rewrite，让 Sebastian 判断无增强价值
    后 passthrough。只有实际 generated 项收到 ACK；同一 receipt 重放不能再次消费 evidence。
    """

    model = _RuntimeScriptedModel([])
    settings = Settings(
        database_url=migrated_database_url,
        dialogue_generator_mode="agent",
        provider_id="openai",
        provider_model="phase7-two-npc-scripted-model",
        provider_api_key=SecretStr("phase7-two-npc-local-test-key"),
        dialogue_generation_max_concurrency=1,
        _env_file=None,
    )
    app = create_app(settings, model_override=model)
    transport = httpx.ASGITransport(app=app)
    async with app.router.lifespan_context(app):
        async with httpx.AsyncClient(
            transport=transport,
            base_url="http://testserver",
        ) as client:
            event_response = await client.post(
                "/api/v1/game-events/batches",
                json={
                    "schema_version": "1.0",
                    "request_id": "request-phase7-progression-event",
                    "save_id": "save-phase7-memory",
                    "player_id": "player-phase7-memory",
                    "events": [
                        {
                            "event_id": "event-phase7-mining-level-5",
                            "event_type": "world_progression",
                            "event_version": "1",
                            "occurred_day_index": 9,
                            "source": "smapi.player.level_changed",
                            "audience_scope": "public",
                            "audience_npc_id": None,
                            "payload": {"milestone": "skill_mining_level_5"},
                        }
                    ],
                },
            )
            assert event_response.status_code == 200
            assert event_response.json()["memory_revision"] == 1

            # evidence ID 由真实投影层确定。先读取它再排入 fake model 的 structured final，
            # 可证明最终 claim 来自本次工具 artifact，而不是测试预先伪造的固定 ID。
            storage = app.state.dialogue_service._storage
            async with storage.session_factory() as session:
                evidence_id = await session.scalar(
                    select(MemoryRecord.memory_id).where(
                        MemoryRecord.event_id == "event-phase7-mining-level-5"
                    )
                )
            assert evidence_id is not None
            model.queue_steps(
                [
                    _tool_call(
                        "get_player_progression",
                        "phase7-progression-tool-call",
                        {},
                    ),
                    _memory_rewrite_final(evidence_id),
                    _passthrough_final(call_id="phase7-sebastian-final"),
                ]
            )

            generation_response = await client.post(
                "/api/v1/dialogue-generations/batch",
                json=_two_npc_memory_payload(),
            )
            assert generation_response.status_code == 200
            generation_items = generation_response.json()["items"]
            assert [item["status"] for item in generation_items] == [
                "generated",
                "passthrough",
            ]
            assert generation_items[0]["evidence_ids"] == [evidence_id]
            assert generation_items[1]["evidence_ids"] == []

            generation_id = generation_items[0]["generation_id"]
            ack_payload = {
                "schema_version": "1.0",
                "request_id": "request-phase7-display-ack",
                "save_id": "save-phase7-memory",
                "player_id": "player-phase7-memory",
                "display_receipt_id": "receipt-phase7-abigail-day-10",
                "displayed_day_index": 10,
                "npc_id": "Abigail",
                "source_hash": "sha256:phase7-memory-abigail",
            }
            accepted_ack = await client.post(
                f"/api/v1/dialogue-generations/{generation_id}/displayed",
                json=ack_payload,
            )
            duplicate_ack = await client.post(
                f"/api/v1/dialogue-generations/{generation_id}/displayed",
                json={
                    **ack_payload,
                    "request_id": "request-phase7-display-ack-replay",
                },
            )

            assert accepted_ack.status_code == 200
            assert accepted_ack.json()["status"] == "accepted"
            assert duplicate_ack.status_code == 200
            assert duplicate_ack.json()["status"] == "duplicate"

            async with storage.session_factory() as session:
                memory = await session.scalar(
                    select(MemoryRecord).where(MemoryRecord.memory_id == evidence_id)
                )
                receipt_count = await session.scalar(
                    select(func.count()).select_from(DialogueDisplayReceiptRecord)
                )
                generations = list(
                    (
                        await session.scalars(
                            select(DialogueGenerationRecord).order_by(
                                DialogueGenerationRecord.npc_id
                            )
                        )
                    ).all()
                )

            assert memory is not None
            assert memory.use_count == 1
            assert memory.last_used_day_index == 10
            assert receipt_count == 1
            assert [record.npc_id for record in generations] == ["Abigail", "Sebastian"]
            assert generations[0].trace_json is not None
            assert generations[0].trace_json["agent"]["used_tools"] == ["get_player_progression"]
            assert generations[0].trace_json["agent"]["tool_calls"][0]["evidence_ids"] == [
                evidence_id
            ]
            assert generations[1].trace_json is not None
            assert generations[1].trace_json["agent"]["used_tools"] == []

            # cooldown=3：第 13 天生成时 cutoff=12，threshold=9，day10 仍被排除；
            # 第 14 天 cutoff=13，threshold=10，才重新变为可读。
            during_cooldown = await storage.get_progression_context(
                ProgressionContextQuery(
                    save_id="save-phase7-memory",
                    player_id="player-phase7-memory",
                    npc_id="Abigail",
                    game_day_index=13,
                    cutoff_day_index=12,
                    friendship_points=1250,
                    relationship_stage="friend",
                    topics=("skill_mining_level_5",),
                    since_day_index=9,
                    cooldown_days=3,
                    limit=1,
                )
            )
            after_cooldown = await storage.get_progression_context(
                ProgressionContextQuery(
                    save_id="save-phase7-memory",
                    player_id="player-phase7-memory",
                    npc_id="Abigail",
                    game_day_index=14,
                    cutoff_day_index=13,
                    friendship_points=1250,
                    relationship_stage="friend",
                    topics=("skill_mining_level_5",),
                    since_day_index=9,
                    cooldown_days=3,
                    limit=1,
                )
            )

    assert during_cooldown == []
    assert [record.evidence_id for record in after_cooldown] == [evidence_id]
    assert model.physical_calls == 3


def _two_npc_request() -> DialogueGenerationBatchRequest:
    """构造一个快项和一个阻塞项共享的合法两 NPC 批次。"""

    payload = _generation_payload()
    first_item = payload["items"][0]  # type: ignore[index]
    assert isinstance(first_item, dict)
    second_item = {
        **first_item,
        "task_id": "task-phase7-runtime-sebastian",
        "npc_id": "Sebastian",
        "source_dialogue": {
            "asset_name": "Characters/Dialogue/Sebastian",
            "dialogue_key": "fall_Mon",
            "text": "这种天气倒是挺安静。",
            "source_hash": "sha256:phase7-runtime-sebastian",
        },
    }
    payload["items"] = [first_item, second_item]
    return DialogueGenerationBatchRequest.model_validate(payload)


@pytest.mark.asyncio
async def test_batch_deadline_returns_per_item_failure_without_turning_batch_into_500(
    storage: SqliteStorage,
) -> None:
    """排队中的慢 NPC 超过批次预算时应独立失败，已完成 NPC 结果必须保留。"""

    never_complete = asyncio.Event()

    async def mixed_speed_generator(
        _request: DialogueGenerationBatchRequest,
        item: DialogueGenerationItem,
    ) -> DialogueGeneratorDecision:
        if item.npc_id == "Abigail":
            return DialogueGeneratorDecision(
                status="passthrough",
                text=None,
                reason_code="FAST_ITEM_COMPLETED",
            )
        await never_complete.wait()
        raise AssertionError("阻塞 generator 不应越过 batch deadline")

    response = await asyncio.wait_for(
        DialogueService(
            storage,
            generator=mixed_speed_generator,
            max_concurrency=1,
            batch_deadline_seconds=0.05,
        ).generate_batch(_two_npc_request()),
        timeout=1.0,
    )

    assert [item.status for item in response.items] == ["passthrough", "failed"]
    assert [item.reason_code for item in response.items] == [
        "FAST_ITEM_COMPLETED",
        "BATCH_DEADLINE_EXCEEDED",
    ]
    assert response.items[1].text is None
    assert response.items[1].evidence_ids == []
