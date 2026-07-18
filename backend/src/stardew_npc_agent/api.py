"""FastAPI 应用工厂、进程探测与 Phase 4 业务路由。

路由层只解析 HTTP DTO、调用 service 并映射稳定错误码，不接触 SQLAlchemy
``Session``。应用工厂会创建一个延迟连接的 async engine，但导入模块仍不会
连接数据库、启动端口、调用模型或读取 Provider 密钥。
"""

from __future__ import annotations

from collections.abc import AsyncIterator
from contextlib import asynccontextmanager

from fastapi import Depends, FastAPI, HTTPException, status
from langchain_core.language_models import BaseChatModel

from stardew_npc_agent.config import Settings
from stardew_npc_agent.dialogue_service import (
    DialogueBatchEnvelopeError,
    DialogueService,
    DialogueServiceUnavailableError,
    MemoryRevisionNotReadyError,
)
from stardew_npc_agent.event_service import (
    DisplayAckConflictError,
    DisplayAckNotAllowedError,
    DisplayGenerationNotFoundError,
    EventService,
    EventServiceUnavailableError,
    MemoryPartitionStateInvalidError,
    MemoryRevisionExhaustedError,
)
from stardew_npc_agent.runtime import (
    ApplicationRuntime,
    RuntimeCapabilities,
    build_application_runtime,
)
from stardew_npc_agent.schemas import (
    CapabilitiesResponse,
    DialogueGenerationBatchRequest,
    DialogueGenerationBatchResponse,
    DisplayAckRequest,
    DisplayAckResponse,
    GameEventBatchRequest,
    GameEventBatchResponse,
    HealthStatusResponse,
)
from stardew_npc_agent.storage import SqliteStorage


def create_app(
    settings: Settings | None = None,
    *,
    event_service: EventService | None = None,
    dialogue_service: DialogueService | None = None,
    model_override: BaseChatModel | None = None,
) -> FastAPI:
    """创建一个不自动连库或启动网络的 FastAPI 应用。

    Args:
        settings: 可选的显式配置；省略时从受控默认值/环境字段构造。
        event_service: 测试可显式注入已连接临时库的 service。省略时工厂
            创建自己的 storage/service，并在 ASGI lifespan 结束时释放连接池。
        dialogue_service: 可选的台词生成服务。两个 service 都省略时复用同一个
            应用自建 storage；显式注入对象始终由调用方拥有，应用不会释放它。
        model_override: 只供显式 agent 模式的零网络测试注入 fake model。生产省略后
            由唯一组合根通过 Provider 工厂创建模型；scripted 或手工 service 注入路径拒绝该参数。

    Returns:
        已注册健康探测、事件批次、台词生成和展示 ACK 的 ASGI 应用。函数不启动
        uvicorn，也不自动运行 migration；部署必须先显式执行 ``alembic upgrade head``。

    安全边界：capabilities 只来自实际组合根。默认 scripted 或手工 service 注入路径
    均为 false；只有正式 AgentBacked runtime 完整装配后才为 true。
    """

    effective_settings = settings or Settings()
    owned_runtime: ApplicationRuntime | None = None
    owned_storage: SqliteStorage | None = None
    if event_service is None and dialogue_service is None:
        owned_runtime = build_application_runtime(
            effective_settings,
            model_override=model_override,
        )
        effective_event_service = owned_runtime.event_service
        effective_dialogue_service = owned_runtime.dialogue_service
        runtime_capabilities = owned_runtime.capabilities
    else:
        if model_override is not None:
            raise ValueError("手工 service 注入路径不接受 model_override")
        if effective_settings.dialogue_generator_mode != "scripted":
            raise ValueError("agent 模式必须使用正式应用组合根，不能手工替换 service")
        if event_service is None or dialogue_service is None:
            owned_storage = SqliteStorage.from_url(
                effective_settings.database_url,
                busy_timeout_ms=effective_settings.sqlite_busy_timeout_ms,
            )
        if event_service is None:
            assert owned_storage is not None
            effective_event_service = EventService(owned_storage)
        else:
            effective_event_service = event_service
        if dialogue_service is None:
            assert owned_storage is not None
            effective_dialogue_service = DialogueService(
                owned_storage,
                max_concurrency=effective_settings.dialogue_generation_max_concurrency,
                fallback_memory_cooldown_days=effective_settings.memory_display_cooldown_days,
                batch_deadline_seconds=effective_settings.dialogue_batch_deadline_seconds,
            )
        else:
            effective_dialogue_service = dialogue_service
        runtime_capabilities = RuntimeCapabilities(False, False, False)

    @asynccontextmanager
    async def lifespan(_app: FastAPI) -> AsyncIterator[None]:
        """只释放由应用工厂自己创建的连接池，不越权处理注入对象。"""

        try:
            yield
        finally:
            # ``asynccontextmanager`` 在 yield 之后的普通语句会在上下文异常
            # 时被跳过；只有 finally 能保证正常停机与异常停机都释放连接池。
            if owned_runtime is not None:
                await owned_runtime.aclose()
            elif owned_storage is not None:
                await owned_storage.dispose()

    app = FastAPI(
        title="Stardew NPC Agent Backend",
        version="0.1.0",
        lifespan=lifespan,
    )
    # app.state 只保留业务服务，不保存完整 Settings。agent 模式的 Settings 含
    # SecretStr，即使 repr 会掩码，也没有理由扩大进程内可访问面。
    app.state.event_service = effective_event_service
    app.state.dialogue_service = effective_dialogue_service

    @app.get("/api/v1/health/live", response_model=HealthStatusResponse)
    def health_live() -> HealthStatusResponse:
        """说明 ASGI 应用已成功构造并能处理请求。"""

        return HealthStatusResponse(status="alive")

    async def require_service_ready() -> None:
        """为 readiness 与所有业务写路由提供同一个 fail-closed 守门。"""

        if not await effective_event_service.is_ready():
            raise HTTPException(
                status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
                detail="SERVICE_NOT_READY",
            )

    @app.get("/api/v1/health/ready", response_model=HealthStatusResponse)
    async def health_ready() -> HealthStatusResponse:
        """只有当数据库已到 Alembic head 时才返回 ready。

        Provider 能力仍需读取 capabilities 的显式位；数据库未迁移、不可连接或
        Schema 漂移时统一返回 503，不泄露本地路径。
        """

        await require_service_ready()
        return HealthStatusResponse(status="ready")

    @app.get("/api/v1/capabilities", response_model=CapabilitiesResponse)
    def capabilities() -> CapabilitiesResponse:
        """返回安全、稳定且不包含环境变量值的能力声明。"""

        return CapabilitiesResponse(
            schema_versions=[effective_settings.schema_version],
            locales=["zh-CN", "en"],
            batch_max_items=effective_settings.batch_max_items,
            provider_configured=runtime_capabilities.provider_configured,
            tool_calling=runtime_capabilities.tool_calling,
            structured_output=runtime_capabilities.structured_output,
        )

    @app.post(
        "/api/v1/game-events/batches",
        response_model=GameEventBatchResponse,
        dependencies=[Depends(require_service_ready)],
    )
    async def ingest_game_event_batch(
        request: GameEventBatchRequest,
    ) -> GameEventBatchResponse:
        """将合法 envelope 交给事件服务逐项投影和幂等提交。"""

        try:
            return await effective_event_service.ingest_batch(request)
        except MemoryPartitionStateInvalidError:
            raise HTTPException(
                status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
                detail="MEMORY_PARTITION_STATE_INVALID",
            ) from None
        except MemoryRevisionExhaustedError:
            raise HTTPException(
                status_code=status.HTTP_409_CONFLICT,
                detail="MEMORY_REVISION_EXHAUSTED",
            ) from None
        except EventServiceUnavailableError:
            raise HTTPException(
                status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
                detail="SERVICE_NOT_READY",
            ) from None

    @app.post(
        "/api/v1/dialogue-generations/batch",
        response_model=DialogueGenerationBatchResponse,
        dependencies=[Depends(require_service_ready)],
    )
    async def generate_dialogue_batch(
        request: DialogueGenerationBatchRequest,
    ) -> DialogueGenerationBatchResponse:
        """执行确定性逐 NPC 生成，并把批次级失败映射为稳定 HTTP 分类。"""

        try:
            return await effective_dialogue_service.generate_batch(request)
        except MemoryRevisionNotReadyError:
            raise HTTPException(
                status_code=status.HTTP_409_CONFLICT,
                detail="MEMORY_REVISION_NOT_READY",
            ) from None
        except DialogueBatchEnvelopeError:
            raise HTTPException(
                status_code=status.HTTP_422_UNPROCESSABLE_CONTENT,
                detail="INVALID_BATCH_ENVELOPE",
            ) from None
        except DialogueServiceUnavailableError:
            raise HTTPException(
                status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
                detail="SERVICE_NOT_READY",
            ) from None

    @app.post(
        "/api/v1/dialogue-generations/{generation_id}/displayed",
        response_model=DisplayAckResponse,
        dependencies=[Depends(require_service_ready)],
    )
    async def acknowledge_display(
        generation_id: str,
        request: DisplayAckRequest,
    ) -> DisplayAckResponse:
        """记录 generated 台词的实际展示，并将预期业务失败映射为稳定 4xx。"""

        try:
            return await effective_event_service.acknowledge_display(generation_id, request)
        except MemoryPartitionStateInvalidError:
            raise HTTPException(
                status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
                detail="MEMORY_PARTITION_STATE_INVALID",
            ) from None
        except MemoryRevisionExhaustedError:
            raise HTTPException(
                status_code=status.HTTP_409_CONFLICT,
                detail="MEMORY_REVISION_EXHAUSTED",
            ) from None
        except EventServiceUnavailableError:
            raise HTTPException(
                status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
                detail="SERVICE_NOT_READY",
            ) from None
        except DisplayGenerationNotFoundError:
            raise HTTPException(
                status_code=status.HTTP_404_NOT_FOUND,
                detail="GENERATION_NOT_FOUND",
            ) from None
        except DisplayAckConflictError:
            raise HTTPException(
                status_code=status.HTTP_409_CONFLICT,
                detail="DISPLAY_RECEIPT_CONFLICT",
            ) from None
        except DisplayAckNotAllowedError:
            raise HTTPException(
                status_code=status.HTTP_422_UNPROCESSABLE_CONTENT,
                detail="DISPLAY_ACK_NOT_ALLOWED",
            ) from None

    return app
