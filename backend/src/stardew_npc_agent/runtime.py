"""FastAPI 生产服务的唯一组合根。

本模块只做依赖装配和资源所有权，不注册路由、不启动 uvicorn、不运行 migration、
不做 Provider live probe。默认 scripted 分支不会构造 Provider spec 或模型；显式 agent
分支把同一个 storage 交给 EventService、Agent tools 与 DialogueService，并让 Agent
与 Repair 复用同一个 ``BaseChatModel`` 实例。
"""

from __future__ import annotations

from dataclasses import dataclass

from langchain_core.language_models import BaseChatModel

from stardew_npc_agent.config import Settings
from stardew_npc_agent.dialogue_agent import (
    TARGET_DIALOGUE_DOMAIN_TOOL_NAMES,
    AgentBackedDialogueGenerator,
    DialogueAgentFactory,
    DialogueAgentRunner,
    DialogueAgentSettings,
)
from stardew_npc_agent.dialogue_service import DialogueService
from stardew_npc_agent.dialogue_tools import build_domain_dialogue_tools
from stardew_npc_agent.event_service import EventService
from stardew_npc_agent.model_provider import (
    build_chat_model,
    build_provider_spec,
    close_owned_chat_model,
)
from stardew_npc_agent.storage import SqliteStorage


@dataclass(frozen=True, slots=True)
class RuntimeCapabilities:
    """由实际组合根产生、可安全返回给游戏侧的模型能力快照。"""

    provider_configured: bool
    tool_calling: bool
    structured_output: bool


@dataclass(slots=True)
class ApplicationRuntime:
    """一次应用装配的服务、能力与资源所有权。

    ``owned_model`` 只有生产组合根自行创建 Provider 时非 null；测试注入 fake model
    由调用方拥有，lifespan 不得关闭。storage 始终由本组合根创建并在关闭时释放。
    """

    storage: SqliteStorage
    event_service: EventService
    dialogue_service: DialogueService
    capabilities: RuntimeCapabilities
    owned_model: BaseChatModel | None = None

    async def aclose(self) -> None:
        """释放本组合根拥有的 Provider client 与 SQLite engine。

        只在模型确由本组合根创建时调用统一关闭 helper；注入 fake/override 由调用方拥有，
        lifespan 不做任何猜测或越权清理。无论模型释放是否成功，storage 都必须进入 finally。
        """

        try:
            if self.owned_model is not None:
                await close_owned_chat_model(self.owned_model)
        finally:
            await self.storage.dispose()


def build_application_runtime(
    settings: Settings,
    *,
    model_override: BaseChatModel | None = None,
) -> ApplicationRuntime:
    """按显式模式构造共享 storage 的完整应用 runtime。

    ``model_override`` 只服务零网络测试；scripted 模式拒绝它，避免测试 seam
    变成隐式启用 Provider 的旁路。
    """

    storage = SqliteStorage.from_url(
        settings.database_url,
        busy_timeout_ms=settings.sqlite_busy_timeout_ms,
    )
    event_service = EventService(storage)

    if settings.dialogue_generator_mode == "scripted":
        if model_override is not None:
            raise ValueError("scripted 模式不接受 model_override")
        dialogue_service = DialogueService(
            storage,
            max_concurrency=settings.dialogue_generation_max_concurrency,
            fallback_memory_cooldown_days=settings.memory_display_cooldown_days,
            batch_deadline_seconds=settings.dialogue_batch_deadline_seconds,
        )
        return ApplicationRuntime(
            storage=storage,
            event_service=event_service,
            dialogue_service=dialogue_service,
            capabilities=RuntimeCapabilities(False, False, False),
        )

    # ProviderSpec 在模型 override 之前构造：测试 fake 只能替换外部模型调用，不能绕过正式
    # Provider 配置、capabilities 或 generation identity 合同。
    provider_spec = build_provider_spec(settings)
    owned_model: BaseChatModel | None = None
    model = model_override
    if model is None:
        model = build_chat_model(settings, provider_spec)
        owned_model = model

    # 生产 registry 只有“全部 active”才能构造三个领域工具；任何 mixed 状态会在
    # builder 中 fail closed。Factory 与 generator 使用同一冻结集合，避免模型看见
    # 新 Schema、执行层却仍授权旧 query-style 名称，或反向形成隐式混合发布。
    domain_tools = build_domain_dialogue_tools()
    factory = DialogueAgentFactory(
        model=model,
        model_configuration=provider_spec.runtime_identity,
        settings=DialogueAgentSettings(task_deadline_seconds=settings.agent_task_deadline_seconds),
        tools=domain_tools,
    )
    generator = AgentBackedDialogueGenerator(
        runner=DialogueAgentRunner(factory),
        storage=storage,
        allowed_tools=TARGET_DIALOGUE_DOMAIN_TOOL_NAMES,
    )
    dialogue_service = DialogueService(
        storage,
        generator=generator,
        max_concurrency=settings.dialogue_generation_max_concurrency,
        fallback_memory_cooldown_days=settings.memory_display_cooldown_days,
        batch_deadline_seconds=settings.dialogue_batch_deadline_seconds,
    )
    return ApplicationRuntime(
        storage=storage,
        event_service=event_service,
        dialogue_service=dialogue_service,
        capabilities=RuntimeCapabilities(
            provider_configured=True,
            tool_calling=provider_spec.supports_tool_calling,
            structured_output=provider_spec.supports_structured_output,
        ),
        owned_model=owned_model,
    )
