"""FastAPI 进程级健康与能力声明测试。

测试通过 httpx AsyncClient 的 ASGITransport 直接驱动应用，确保验证过程不会启动
端口监听或长驻 uvicorn 进程，也不依赖 Starlette 已弃用的同步客户端回退路径。
"""

from __future__ import annotations

from importlib import import_module
from pathlib import Path

import httpx
import pytest
from fastapi import FastAPI
from pydantic import ValidationError

SETTING_ENV_NAMES = (
    "STARDEW_NPC_AGENT_HOST",
    "STARDEW_NPC_AGENT_PORT",
    "STARDEW_NPC_AGENT_SCHEMA_VERSION",
    "STARDEW_NPC_AGENT_BATCH_MAX_ITEMS",
    "STARDEW_NPC_AGENT_DATABASE_URL",
    "STARDEW_NPC_AGENT_SQLITE_BUSY_TIMEOUT_MS",
    "STARDEW_NPC_AGENT_MEMORY_DISPLAY_COOLDOWN_DAYS",
    "STARDEW_NPC_AGENT_DIALOGUE_GENERATION_MAX_CONCURRENCY",
    "STARDEW_NPC_AGENT_DIALOGUE_GENERATOR_MODE",
    "STARDEW_NPC_AGENT_PROVIDER_ID",
    "STARDEW_NPC_AGENT_PROVIDER_MODEL",
    "STARDEW_NPC_AGENT_PROVIDER_API_KEY",
    "STARDEW_NPC_AGENT_PROVIDER_BASE_URL",
    "STARDEW_NPC_AGENT_PROVIDER_WIRE_API",
    "STARDEW_NPC_AGENT_PROVIDER_REQUEST_TIMEOUT_SECONDS",
    "STARDEW_NPC_AGENT_PROVIDER_MAX_OUTPUT_TOKENS",
    "STARDEW_NPC_AGENT_AGENT_TASK_DEADLINE_SECONDS",
    "STARDEW_NPC_AGENT_DIALOGUE_BATCH_DEADLINE_SECONDS",
)


def test_settings_have_safe_local_defaults(monkeypatch) -> None:
    """默认配置必须只绑定本机，并固定 v1 wire contract 与批次上限。"""

    for environment_name in SETTING_ENV_NAMES:
        monkeypatch.delenv(environment_name, raising=False)

    config = import_module("stardew_npc_agent.config")
    settings = config.Settings(_env_file=None)

    assert settings.host == "127.0.0.1"
    assert settings.port == 8000
    assert settings.schema_version == "1.0"
    assert settings.batch_max_items == 8
    assert settings.database_url == "sqlite+aiosqlite:///./stardew_npc_agent.sqlite3"
    assert settings.sqlite_busy_timeout_ms == 5_000
    assert settings.memory_display_cooldown_days == 3
    assert settings.dialogue_generation_max_concurrency == 2
    assert settings.dialogue_generator_mode == "scripted"
    assert settings.provider_id is None
    assert settings.provider_model is None
    assert settings.provider_api_key is None
    assert settings.provider_base_url is None
    assert settings.provider_wire_api == "chat_completions"
    assert settings.provider_request_timeout_seconds == 45.0
    assert settings.provider_max_output_tokens == 256
    assert settings.agent_task_deadline_seconds == 90.0
    assert settings.dialogue_batch_deadline_seconds == 105.0


def test_agent_task_deadline_accepts_approved_default_and_rejects_over_120_seconds() -> None:
    """应用配置允许已批准的 90 秒任务预算，但继续拒绝无界长任务。

    Provider 的单次 timeout、Agent 总 deadline、批次 deadline 和 SMAPI HTTP timeout
    必须形成严格递增链。这里单独冻结 Agent 配置层的 120 秒上限，防止未来为了兼容
    慢 Provider 而悄悄移除总任务边界。
    """

    config = import_module("stardew_npc_agent.config")

    accepted = config.Settings(agent_task_deadline_seconds=90.0, _env_file=None)
    assert accepted.agent_task_deadline_seconds == 90.0

    with pytest.raises(ValidationError):
        config.Settings(agent_task_deadline_seconds=120.001, _env_file=None)


@pytest.mark.parametrize(
    "overrides",
    [
        {"provider_request_timeout_seconds": 45.0, "agent_task_deadline_seconds": 45.0},
        {"agent_task_deadline_seconds": 90.0, "dialogue_batch_deadline_seconds": 90.0},
    ],
)
def test_timeout_layers_must_be_strictly_increasing(overrides: dict[str, float]) -> None:
    """自定义 timeout 也必须保持 Provider、Agent、批次的严格递增关系。

    单独的字段上下限不能阻止调用方把 Agent deadline 配得短于一次 Provider 请求，
    那会再次使恢复性重试名存实亡。错误只暴露稳定层级名称，不回显具体配置值。
    """

    config = import_module("stardew_npc_agent.config")
    with pytest.raises(ValidationError, match="Provider < Agent < batch"):
        config.Settings(_env_file=None, **overrides)


@pytest.mark.parametrize(
    ("kwargs", "expected_fragment"),
    [
        ({"dialogue_generator_mode": "agent"}, "provider_id"),
        (
            {
                "dialogue_generator_mode": "agent",
                "provider_id": "openai",
                "provider_api_key": "must-not-appear-in-validation-errors",
            },
            "provider_model",
        ),
        (
            {
                "dialogue_generator_mode": "agent",
                "provider_id": "openai",
                "provider_model": "model",
            },
            "provider_api_key",
        ),
    ],
)
def test_agent_mode_requires_explicit_provider_configuration(
    kwargs: dict[str, object],
    expected_fragment: str,
) -> None:
    """Agent 模式必须显式给出完整项目配置，且错误不得回显原始 key。"""

    with pytest.raises(ValidationError) as error_info:
        config = import_module("stardew_npc_agent.config")
        config.Settings(_env_file=None, **kwargs)

    error_text = str(error_info.value)
    assert expected_fragment in error_text
    assert "must-not-appear-in-validation-errors" not in error_text


def test_openai_rejects_configurable_base_url() -> None:
    """官方 OpenAI endpoint 必须由应用固定，不能被项目配置重定向。"""

    config = import_module("stardew_npc_agent.config")
    with pytest.raises(ValidationError, match="openai.*base URL") as error_info:
        config.Settings(
            dialogue_generator_mode="agent",
            provider_id="openai",
            provider_model="model",
            provider_api_key="must-not-appear-in-validation-errors",
            provider_base_url="https://proxy.example/v1",
            _env_file=None,
        )

    assert "must-not-appear-in-validation-errors" not in str(error_info.value)


@pytest.mark.parametrize(
    "base_url",
    [
        "https://user:pass@provider.example/v1",
        "https://provider.example/v1?api-version=1",
        "https://provider.example/v1#fragment",
        "http://provider.example/v1",
    ],
)
def test_openai_compatible_rejects_unsafe_base_url(base_url: str) -> None:
    """Compatible endpoint 不能携带凭据/路由附加项或使用公网明文 HTTP。"""

    config = import_module("stardew_npc_agent.config")
    with pytest.raises(ValidationError) as error_info:
        config.Settings(
            dialogue_generator_mode="agent",
            provider_id="openai_compatible",
            provider_model="model",
            provider_api_key="must-not-appear-in-validation-errors",
            provider_base_url=base_url,
            _env_file=None,
        )

    error_text = str(error_info.value)
    assert "must-not-appear-in-validation-errors" not in error_text
    assert base_url not in error_text


@pytest.mark.parametrize(
    "base_url",
    [
        "http://localhost:8000/v1",
        "http://127.0.0.1:8000/v1",
        "http://[::1]:8000/v1",
        "https://provider.example/v1",
    ],
)
def test_openai_compatible_accepts_https_or_loopback_http(base_url: str) -> None:
    """本机调试可使用 HTTP，非 loopback 服务必须使用 HTTPS。"""

    config = import_module("stardew_npc_agent.config")
    settings = config.Settings(
        dialogue_generator_mode="agent",
        provider_id="openai_compatible",
        provider_model="model",
        provider_api_key="not-a-real-key",
        provider_base_url=base_url,
        _env_file=None,
    )

    assert settings.provider_base_url is not None


@pytest.mark.parametrize(
    "provider_model",
    ["", " model", "model ", "m" * 97],
)
def test_provider_model_rejects_blank_whitespace_or_overlong_value(
    provider_model: str,
) -> None:
    """模型名必须可稳定进入 255 字符以内的 generation identity。"""

    config = import_module("stardew_npc_agent.config")
    with pytest.raises(ValidationError) as error_info:
        config.Settings(
            dialogue_generator_mode="agent",
            provider_id="openai",
            provider_model=provider_model,
            provider_api_key="must-not-appear-in-validation-errors",
            _env_file=None,
        )

    assert "must-not-appear-in-validation-errors" not in str(error_info.value)


def test_provider_model_accepts_exactly_96_characters() -> None:
    """96 字符是保证 runtime identity 长度上界的合法闭区间端点。"""

    config = import_module("stardew_npc_agent.config")
    settings = config.Settings(
        dialogue_generator_mode="agent",
        provider_id="openai",
        provider_model="m" * 96,
        provider_api_key="not-a-real-key",
        _env_file=None,
    )

    assert settings.provider_model == "m" * 96


@pytest.mark.asyncio
async def test_health_and_capabilities_are_available_without_provider_secret(
    monkeypatch,
    storage,
    migrated_database_url: str,
) -> None:
    """Phase 1 应用可自检，但不得谎报模型能力或回显未知环境变量。"""

    secret_value = "must-not-appear-in-any-response"
    monkeypatch.setenv("STARDEW_NPC_AGENT_PROVIDER_API_KEY", secret_value)

    api = import_module("stardew_npc_agent.api")
    config = import_module("stardew_npc_agent.config")
    event_service = import_module("stardew_npc_agent.event_service")
    app = api.create_app(
        config.Settings(database_url=migrated_database_url, _env_file=None),
        event_service=event_service.EventService(storage),
    )
    transport = httpx.ASGITransport(app=app)
    async with httpx.AsyncClient(transport=transport, base_url="http://testserver") as client:
        live_response = await client.get("/api/v1/health/live")
        ready_response = await client.get("/api/v1/health/ready")
        capabilities_response = await client.get("/api/v1/capabilities")

    assert live_response.status_code == 200
    assert live_response.json() == {"status": "alive"}
    assert ready_response.status_code == 200
    assert ready_response.json() == {"status": "ready"}
    assert capabilities_response.status_code == 200
    assert capabilities_response.json() == {
        "schema_versions": ["1.0"],
        "locales": ["zh-CN", "en"],
        "batch_max_items": 8,
        "provider_configured": False,
        "tool_calling": False,
        "structured_output": False,
    }
    assert secret_value not in live_response.text
    assert secret_value not in ready_response.text
    assert secret_value not in capabilities_response.text


@pytest.mark.asyncio
async def test_unmigrated_database_is_not_ready_and_business_route_returns_503(
    tmp_path: Path,
) -> None:
    """空库或库文件不存在时，ready 和写路由都必须 fail closed 且不自动迁移。"""

    config = import_module("stardew_npc_agent.config")
    api = import_module("stardew_npc_agent.api")
    database_path = tmp_path / "must-not-be-created-by-readiness.sqlite3"
    settings = config.Settings(
        database_url=f"sqlite+aiosqlite:///{database_path}",
        _env_file=None,
    )
    app = api.create_app(settings)
    transport = httpx.ASGITransport(app=app)
    event_payload = {
        "schema_version": "1.0",
        "request_id": "request-not-ready",
        "save_id": "save-1",
        "player_id": "player-1",
        "events": [
            {
                "event_id": "event-not-ready",
                "event_type": "world_progression",
                "event_version": "1",
                "occurred_day_index": 1,
                "source": "smapi",
                "audience_scope": "public",
                "audience_npc_id": None,
                "payload": {"milestone": "test"},
            }
        ],
    }
    async with httpx.AsyncClient(transport=transport, base_url="http://testserver") as client:
        ready_response = await client.get("/api/v1/health/ready")
        event_response = await client.post("/api/v1/game-events/batches", json=event_payload)

    assert ready_response.status_code == 503
    assert ready_response.json() == {"detail": "SERVICE_NOT_READY"}
    assert event_response.status_code == 503
    assert event_response.json() == {"detail": "SERVICE_NOT_READY"}
    assert str(database_path) not in ready_response.text
    assert str(database_path) not in event_response.text
    assert not database_path.exists(), "readiness probe 不得为缺失数据库创建空文件"


@pytest.mark.asyncio
async def test_owned_storage_is_disposed_when_lifespan_body_raises(
    monkeypatch,
    tmp_path: Path,
) -> None:
    """ASGI lifespan 异常退出时也必须通过 finally 释放应用自建连接池。"""

    config = import_module("stardew_npc_agent.config")
    api = import_module("stardew_npc_agent.api")
    storage_module = import_module("stardew_npc_agent.storage")
    original_dispose = storage_module.SqliteStorage.dispose
    disposed = False

    async def tracked_dispose(storage_instance) -> None:
        nonlocal disposed
        disposed = True
        await original_dispose(storage_instance)

    monkeypatch.setattr(storage_module.SqliteStorage, "dispose", tracked_dispose)
    app = api.create_app(
        config.Settings(
            database_url=f"sqlite+aiosqlite:///{tmp_path / 'lifespan.sqlite3'}",
            _env_file=None,
        )
    )

    with pytest.raises(RuntimeError, match="lifespan-test-error"):
        async with app.router.lifespan_context(app):
            raise RuntimeError("lifespan-test-error")

    assert disposed is True


@pytest.mark.asyncio
async def test_injected_storage_is_not_disposed_by_application_lifespan(
    monkeypatch,
    storage,
    migrated_database_url: str,
) -> None:
    """应用只能释放自己创建的连接池，不能越权关闭调用方注入的 storage。"""

    api = import_module("stardew_npc_agent.api")
    config = import_module("stardew_npc_agent.config")
    event_service = import_module("stardew_npc_agent.event_service")
    dialogue_service = import_module("stardew_npc_agent.dialogue_service")
    storage_module = import_module("stardew_npc_agent.storage")
    original_dispose = storage_module.SqliteStorage.dispose
    disposed_instances = []

    async def tracked_dispose(storage_instance) -> None:
        disposed_instances.append(storage_instance)
        await original_dispose(storage_instance)

    monkeypatch.setattr(storage_module.SqliteStorage, "dispose", tracked_dispose)
    app = api.create_app(
        config.Settings(database_url=migrated_database_url, _env_file=None),
        event_service=event_service.EventService(storage),
        dialogue_service=dialogue_service.DialogueService(storage),
    )

    async with app.router.lifespan_context(app):
        pass

    assert disposed_instances == []


def test_default_services_share_the_single_application_owned_storage(tmp_path: Path) -> None:
    """默认 event/dialogue service 必须复用同一连接池，避免同进程重复 ownership。"""

    api = import_module("stardew_npc_agent.api")
    config = import_module("stardew_npc_agent.config")
    app = api.create_app(
        config.Settings(
            database_url=f"sqlite+aiosqlite:///{tmp_path / 'shared-owned.sqlite3'}",
            _env_file=None,
        )
    )

    assert app.state.event_service._storage is app.state.dialogue_service._storage


def test_main_exposes_an_app_without_starting_a_server() -> None:
    """导入入口模块只应构造 ASGI app，不应进入 uvicorn 的阻塞运行循环。"""

    main = import_module("stardew_npc_agent.main")

    assert isinstance(main.app, FastAPI)
