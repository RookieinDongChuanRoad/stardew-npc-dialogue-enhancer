"""受控模型 Provider 配置、LangChain 初始化与资源释放。

本模块是生产代码中唯一知道模型 SDK 构造参数的位置。它把已经验证的 ``Settings`` 冻结为
不含秘密的 ``ModelProviderSpec``，显式选择 endpoint/wire API，并禁止 SDK 自己读取通用 base URL
或执行额外 retry。Agent、工具、Guard 和 HTTP 层只依赖 ``BaseChatModel``，不感知具体 SDK。
"""

from __future__ import annotations

import hashlib
import inspect
from dataclasses import dataclass
from typing import Literal
from urllib.parse import urlsplit, urlunsplit

from langchain.chat_models import init_chat_model
from langchain_core.language_models import BaseChatModel
from pydantic import HttpUrl

from stardew_npc_agent.config import ProviderId, ProviderWireApi, Settings

# 官方 endpoint 在代码中显式固定，避免 ChatOpenAI/OpenAI SDK 从宿主机的
# OPENAI_BASE_URL/OPENAI_API_BASE 偷偷重定向项目命名空间下的 key。
OFFICIAL_OPENAI_BASE_URL = "https://api.openai.com/v1"

# identity 版本变化代表模型装配语义变化；升级该值会安全失效旧 generation cache。
MODEL_PROVIDER_RUNTIME_VERSION = "model-provider-runtime-v2"


@dataclass(frozen=True, slots=True)
class ModelProviderSpec:
    """已经验证并可进入模型组合根的不可变 Provider 快照。

    Attributes:
        provider_id: 产品层 Provider 选择；区分官方与 compatible endpoint。
        langchain_provider: 交给 ``init_chat_model`` 的 adapter，本版本只复用 OpenAI 协议。
        model: Provider 接受的模型标识。
        effective_base_url: 实际传入 SDK 的稳定 endpoint；绝不从通用环境变量补齐。
        wire_api: 使用 Chat Completions 还是 Responses API。
        stream_usage: 是否要求 SDK 在流式路径回传 usage；compatible 默认关闭以提高兼容性。
        supports_tool_calling: 组合根可静态声明的工具调用能力。
        supports_structured_output: 组合根可静态声明的结构化输出能力。
        runtime_identity: 不含 key 和明文 compatible endpoint 的 generation cache 身份。
    """

    provider_id: ProviderId
    langchain_provider: Literal["openai"]
    model: str
    effective_base_url: str
    wire_api: ProviderWireApi
    stream_usage: bool
    supports_tool_calling: bool
    supports_structured_output: bool
    runtime_identity: str


def _normalize_base_url(url: HttpUrl) -> str:
    """生成稳定 endpoint 表示，用于模型初始化和 generation identity 指纹。

    Args:
        url: 已通过 Settings 安全校验的 compatible base URL。

    Returns:
        scheme/host 小写、默认端口省略、无语义末尾斜线移除后的 URL。path 的大小写和
        percent-encoding 保持不变，因为部分 Provider 会把它们视为路由语义。

    Raises:
        ValueError: 防御性拒绝没有 host 的 URL；正常 ``HttpUrl`` 不应产生这种输入。
    """

    parsed = urlsplit(str(url))
    host = parsed.hostname
    if host is None:
        raise ValueError("provider_base_url 缺少 host")

    normalized_host = host.lower()
    # urlsplit.hostname 会移除 IPv6 方括号；重新渲染时必须恢复，否则 netloc 将不可解析。
    rendered_host = f"[{normalized_host}]" if ":" in normalized_host else normalized_host
    default_port = 443 if parsed.scheme.lower() == "https" else 80
    netloc = rendered_host
    if parsed.port is not None and parsed.port != default_port:
        netloc = f"{rendered_host}:{parsed.port}"

    normalized_path = parsed.path.rstrip("/")
    return urlunsplit((parsed.scheme.lower(), netloc, normalized_path, "", ""))


def build_provider_spec(settings: Settings) -> ModelProviderSpec:
    """把已验证 Agent 配置冻结为不含秘密的 Provider 运行规格。

    Args:
        settings: 应用 Settings。调用方只能在显式 ``agent`` 模式使用本函数。

    Returns:
        可供组合根构造模型、声明 capabilities 和生成缓存身份的不可变规格。

    Raises:
        ValueError: 模式或必填字段不完整，或生成 identity 超过数据库既有 255 字符边界。

    ``Settings`` 已负责用户输入校验，这里仍保留完整性检查，避免测试 seam、未来重构或
    ``model_copy(update=...)`` 绕过校验后把 SDK 推入隐式环境变量回退路径。
    """

    if settings.dialogue_generator_mode != "agent":
        raise ValueError("只有 agent 模式可以构造 Provider spec")
    if (
        settings.provider_id is None
        or settings.provider_model is None
        or settings.provider_api_key is None
    ):
        raise ValueError("agent Provider 配置不完整")

    if settings.provider_id == "openai":
        effective_base_url = OFFICIAL_OPENAI_BASE_URL
        endpoint_fingerprint = "openai-default"
        stream_usage = True
    else:
        if settings.provider_base_url is None:
            raise ValueError("openai_compatible provider 缺少 base URL")
        effective_base_url = _normalize_base_url(settings.provider_base_url)
        endpoint_fingerprint = hashlib.sha256(effective_base_url.encode("utf-8")).hexdigest()
        stream_usage = False

    runtime_identity = ":".join(
        (
            MODEL_PROVIDER_RUNTIME_VERSION,
            settings.provider_id,
            "openai",
            settings.provider_model,
            settings.provider_wire_api,
            endpoint_fingerprint,
        )
    )
    if len(runtime_identity) > 255:
        raise ValueError("Provider runtime identity 超过 255 字符")

    return ModelProviderSpec(
        provider_id=settings.provider_id,
        langchain_provider="openai",
        model=settings.provider_model,
        effective_base_url=effective_base_url,
        wire_api=settings.provider_wire_api,
        stream_usage=stream_usage,
        supports_tool_calling=True,
        supports_structured_output=True,
        runtime_identity=runtime_identity,
    )


def build_chat_model(settings: Settings, spec: ModelProviderSpec) -> BaseChatModel:
    """使用固定参数集构造 LangChain ``BaseChatModel``。

    Args:
        settings: 提供项目命名空间的 SecretStr key、timeout 与输出 token 上限。
        spec: 与同一 Settings 构造的不可变 Provider 规格。

    Returns:
        可注入既有 ``DialogueAgentFactory`` 的 LangChain chat model。

    Raises:
        ValueError: 项目 key 缺失，防止 SDK 回退读取通用 ``OPENAI_API_KEY``。
        RuntimeError: LangChain initializer 违反约定，没有返回 ``BaseChatModel``。

    SDK retry 固定为 0；业务预算与重试已经由 Agent middleware 管理，双层重试会导致物理调用数和
    费用相乘。本工厂也不开放 ``configurable_fields``、任意 header/query 或 ``extra_body``。
    """

    if settings.provider_api_key is None:
        raise ValueError("provider_api_key 缺失，不能构造模型")

    model = init_chat_model(
        spec.model,
        model_provider=spec.langchain_provider,
        api_key=settings.provider_api_key,
        base_url=spec.effective_base_url,
        use_responses_api=spec.wire_api == "responses",
        timeout=settings.provider_request_timeout_seconds,
        max_retries=0,
        max_completion_tokens=settings.provider_max_output_tokens,
        stream_usage=spec.stream_usage,
    )
    if not isinstance(model, BaseChatModel):
        raise RuntimeError("init_chat_model 未返回 BaseChatModel")
    return model


async def close_owned_chat_model(model: BaseChatModel) -> None:
    """释放组合根自行创建的同步与异步 HTTP client。

    Args:
        model: 由 ``build_chat_model`` 创建并归 ``ApplicationRuntime`` 所有的模型。

    本函数采用 SDK 当前公开在模型对象上的 root client 形状，但保持防御性 ``getattr``，从而兼容
    测试模型或未来只提供一种 client 的 adapter。异步关闭失败时仍在 ``finally`` 中尝试同步关闭；
    原始异常继续传播，由更外层 lifespan 负责记录，同时 storage 仍由其独立 ``finally`` 释放。
    """

    root_async_client = getattr(model, "root_async_client", None)
    close_async = getattr(root_async_client, "close", None)
    try:
        if callable(close_async):
            close_result = close_async()
            if inspect.isawaitable(close_result):
                await close_result
    finally:
        root_client = getattr(model, "root_client", None)
        close_sync = getattr(root_client, "close", None)
        if callable(close_sync):
            close_sync()
