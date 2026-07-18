"""后端进程的运行配置与显式 Agent/Provider 启用边界。

默认模式始终是零模型、零费用的 ``scripted``。只有用户明确把项目命名空间下的
``dialogue_generator_mode`` 设为 ``agent``，并同时提供受支持的 Provider、model 与
``SecretStr`` key，生产组合根才会创建模型。通用 ``OPENAI_*`` 环境变量不属于本
Settings 的输入，因此不会仅因宿主环境存在这些变量就隐式启用或重定向模型。
"""

from __future__ import annotations

from ipaddress import ip_address
from typing import Literal, TypeAlias

from pydantic import Field, HttpUrl, SecretStr, model_validator
from pydantic_settings import BaseSettings, SettingsConfigDict

DialogueGeneratorMode: TypeAlias = Literal["scripted", "agent"]
ProviderId: TypeAlias = Literal["openai", "openai_compatible"]
ProviderWireApi: TypeAlias = Literal["chat_completions", "responses"]


def _is_loopback_provider_host(host: str) -> bool:
    """判断 endpoint host 是否为 loopback。

    Args:
        host: Pydantic ``HttpUrl`` 解析后的 host；IPv6 literal 可能仍带方括号。

    Returns:
        ``True`` 表示请求只会发往本机，可以在显式开发配置下使用 HTTP。

    普通主机名即使将来可能解析到 loopback 也不放行，避免 DNS 或 hosts 文件变化悄悄
    改变传输安全边界。
    """

    normalized_host = host.strip("[]").lower()
    if normalized_host == "localhost":
        return True
    try:
        return ip_address(normalized_host).is_loopback
    except ValueError:
        return False


def _validate_compatible_base_url(url: HttpUrl) -> None:
    """校验 OpenAI-compatible endpoint 的传输与路由安全边界。

    Args:
        url: 已完成基础 URL 语法检查的 Pydantic ``HttpUrl``。

    Raises:
        ValueError: URL 携带 user-info、query、fragment，或非 loopback endpoint 使用 HTTP。

    Provider key 只能由独立环境字段传递；禁止 URL user-info 可以避免凭据进入日志、异常或
    runtime identity。query/fragment 会让同一个 base URL 的路由语义不稳定，因此也明确拒绝。
    """

    if url.username is not None or url.password is not None:
        raise ValueError("provider_base_url 不得包含 user-info")
    if url.query is not None or url.fragment is not None:
        raise ValueError("provider_base_url 不得包含 query 或 fragment")
    if url.host is None:
        # HttpUrl 运行时通常已保证 host，但保留显式检查可同时满足静态类型与未来类型变更的
        # fail-closed 语义，避免空 host 被误当作可信 loopback。
        raise ValueError("provider_base_url 缺少 host")
    if url.scheme != "https" and not _is_loopback_provider_host(url.host):
        raise ValueError("非 loopback provider_base_url 必须使用 HTTPS")


class Settings(BaseSettings):
    """FastAPI 应用的最小运行配置。

    Attributes:
        host: 将来由显式服务器命令使用的监听地址。默认仅绑定 loopback，避免本地
            开发服务无意暴露到局域网。
        port: 将来启动 ASGI 服务器时使用的 TCP 端口；Phase 1 测试不会监听它。
        schema_version: 当前唯一支持的共享 JSON 合同版本，不能由环境切到未知值。
        batch_max_items: MVP 固定为 8，与 JSON Schema、service 和 capabilities 保持一致。
        database_url: SQLAlchemy 2 异步 SQLite URL。默认值是工作目录下的本地文件；
            测试必须显式注入临时路径。
        sqlite_busy_timeout_ms: 短写事务遇到 SQLite 写锁时的等待上限。
        memory_display_cooldown_days: 已实际展示 evidence 的默认检索冷却天数。
        dialogue_generation_max_concurrency: 同一后端进程同时等待 scripted generator
            的最大 NPC 数；Phase 4 两个目标 NPC 默认使用 2，公开上限仍不超过批次 8。
        dialogue_generator_mode: 显式选择零费用 scripted 或正式 Agent 组合根。
        provider_id: 正式 Agent 使用 OpenAI 官方还是 OpenAI-compatible endpoint。
        provider_model: Agent 模式使用的模型名；进入缓存 identity，但不会进入普通响应或日志。
        provider_api_key: 只从项目命名空间环境字段读取的 SecretStr。
        provider_base_url: compatible endpoint；OpenAI 官方分支禁止由用户配置。
        provider_wire_api: 显式选择 Chat Completions 或 Responses wire API。
        provider_request_timeout_seconds: Provider 单次物理请求的有界 timeout。
        provider_max_output_tokens: 每次 Agent/Repair 响应的输出 token 上限。
        agent_task_deadline_seconds: 单 NPC Agent、工具、Guard/Repair 共享的总 deadline。
        dialogue_batch_deadline_seconds: 整个合法批次（含 semaphore 排队）的总 deadline。

    通用 ``OPENAI_*`` 等未识别环境变量会被忽略。项目命名空间的 Provider key 会被解析成
    ``SecretStr``，但不会进入 ``app.state``、capabilities、普通响应或日志；scripted 组合根也
    不会用它构造模型。
    """

    model_config = SettingsConfigDict(
        env_prefix="STARDEW_NPC_AGENT_",
        env_file=None,
        extra="ignore",
        # Pydantic 的 model validator 错误默认会附带原始输入字典。SecretStr 只保护模型内部
        # repr，不能隐藏调用方最初传入的普通字符串，因此必须在配置层统一关闭错误输入回显。
        hide_input_in_errors=True,
    )

    host: str = Field(default="127.0.0.1", min_length=1)
    port: int = Field(default=8000, ge=1, le=65535)
    schema_version: Literal["1.0"] = "1.0"
    batch_max_items: Literal[8] = 8
    database_url: str = Field(
        default="sqlite+aiosqlite:///./stardew_npc_agent.sqlite3",
        min_length=1,
    )
    sqlite_busy_timeout_ms: int = Field(default=5_000, ge=1, le=60_000)
    memory_display_cooldown_days: int = Field(default=3, ge=0, le=112)
    dialogue_generation_max_concurrency: int = Field(default=2, ge=1, le=8)
    dialogue_generator_mode: DialogueGeneratorMode = "scripted"
    provider_id: ProviderId | None = None
    provider_model: str | None = Field(default=None, max_length=96)
    provider_api_key: SecretStr | None = None
    provider_base_url: HttpUrl | None = None
    provider_wire_api: ProviderWireApi = "chat_completions"
    provider_request_timeout_seconds: float = Field(default=45.0, ge=1.0, le=60.0)
    provider_max_output_tokens: int = Field(default=256, ge=32, le=2_048)
    agent_task_deadline_seconds: float = Field(default=90.0, ge=0.1, le=120.0)
    dialogue_batch_deadline_seconds: float = Field(default=105.0, ge=0.1, le=120.0)

    @model_validator(mode="after")
    def validate_explicit_provider_mode(self) -> Settings:
        """只在显式 Agent 模式要求完整 Provider 配置。

        scripted 模式允许预先存在项目命名空间配置，但组合根仍不得读取或使用它；
        这样切换模式需要一个明确开关，而不是“检测到 key 即启用”的隐式行为。所有错误都只
        描述稳定字段名；``hide_input_in_errors`` 负责阻止 Pydantic 附带原始输入。
        """

        # 单字段上下限不足以保证恢复性重试可用：如果一次 Provider 请求的 timeout
        # 已经等于或超过整个 Agent deadline，第一次卡死后就没有时间执行第二次物理
        # 调用。批次 deadline 同理必须给单 NPC 任务保留收尾与调度余量。
        if not (
            self.provider_request_timeout_seconds
            < self.agent_task_deadline_seconds
            < self.dialogue_batch_deadline_seconds
        ):
            raise ValueError("timeout 层级必须满足 Provider < Agent < batch")

        if self.provider_model is not None and (
            not self.provider_model or self.provider_model != self.provider_model.strip()
        ):
            raise ValueError("provider_model 必须是无首尾空白的非空字符串")
        if self.provider_api_key is not None and not self.provider_api_key.get_secret_value():
            raise ValueError("provider_api_key 不能为空")
        if self.dialogue_generator_mode == "scripted":
            return self

        missing_fields: list[str] = []
        if self.provider_id is None:
            missing_fields.append("provider_id")
        if self.provider_model is None:
            missing_fields.append("provider_model")
        if self.provider_api_key is None:
            missing_fields.append("provider_api_key")
        if missing_fields:
            raise ValueError("agent 模式缺少显式项目配置：" + ", ".join(missing_fields))

        if self.provider_id == "openai":
            if self.provider_base_url is not None:
                raise ValueError("openai provider 不允许配置自定义 base URL")
        elif self.provider_id == "openai_compatible":
            if self.provider_base_url is None:
                raise ValueError("openai_compatible provider 缺少 provider_base_url")
            _validate_compatible_base_url(self.provider_base_url)
        return self
