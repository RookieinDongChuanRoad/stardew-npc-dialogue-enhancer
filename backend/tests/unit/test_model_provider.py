"""模型 Provider 规范化、LangChain 构造参数与资源所有权单元测试。

这些测试必须保持完全离线：只在 ``init_chat_model`` 这一外部构造边界使用 monkeypatch，
ProviderSpec、URL 规范化和 identity 均运行真实生产逻辑。占位 key 只用于验证传参和秘密隔离。
"""

from __future__ import annotations

from typing import cast

import pytest
from langchain_core.language_models import BaseChatModel
from langchain_core.language_models.fake_chat_models import FakeListChatModel
from pydantic import SecretStr

from stardew_npc_agent.config import Settings
from stardew_npc_agent.model_provider import (
    build_chat_model,
    build_provider_spec,
    close_owned_chat_model,
)


def _settings(**overrides: object) -> Settings:
    """构造不访问环境文件的完整 Agent Settings，允许单个测试只覆盖关注字段。"""

    values: dict[str, object] = {
        "dialogue_generator_mode": "agent",
        "provider_id": "openai",
        "provider_model": "test-model",
        "provider_api_key": SecretStr("not-a-real-key"),
        "_env_file": None,
    }
    values.update(overrides)
    return Settings(**values)


def test_openai_spec_uses_explicit_official_endpoint() -> None:
    """官方分支必须固定 endpoint，不能把路由决定留给 SDK 通用环境变量。"""

    spec = build_provider_spec(_settings())

    assert spec.provider_id == "openai"
    assert spec.langchain_provider == "openai"
    assert spec.effective_base_url == "https://api.openai.com/v1"
    assert spec.wire_api == "chat_completions"
    assert spec.stream_usage is True
    assert spec.runtime_identity.endswith(":openai-default")


def test_compatible_spec_normalizes_endpoint_and_hides_it_in_identity() -> None:
    """Compatible URL 需稳定规范化，缓存 identity 只能保存不可逆 endpoint 指纹。"""

    spec = build_provider_spec(
        _settings(
            provider_id="openai_compatible",
            provider_base_url="https://Provider.Example:443/v1/",
        )
    )

    assert spec.effective_base_url == "https://provider.example/v1"
    assert spec.stream_usage is False
    assert "provider.example" not in spec.runtime_identity
    assert len(spec.runtime_identity.rsplit(":", 1)[1]) == 64


def test_api_key_does_not_change_runtime_identity() -> None:
    """轮换凭据不能使同一模型配置的生成缓存失效，也不能把秘密写入 identity。"""

    first = build_provider_spec(_settings(provider_api_key=SecretStr("key-one")))
    second = build_provider_spec(_settings(provider_api_key=SecretStr("key-two")))

    assert first.runtime_identity == second.runtime_identity
    assert "key-one" not in first.runtime_identity
    assert "key-two" not in second.runtime_identity


def test_provider_model_wire_api_or_endpoint_changes_identity() -> None:
    """所有会改变远端生成语义的配置都必须形成不同缓存身份。"""

    identities = {
        build_provider_spec(_settings()).runtime_identity,
        build_provider_spec(_settings(provider_model="other-model")).runtime_identity,
        build_provider_spec(_settings(provider_wire_api="responses")).runtime_identity,
        build_provider_spec(
            _settings(
                provider_id="openai_compatible",
                provider_base_url="https://one.example/v1",
            )
        ).runtime_identity,
        build_provider_spec(
            _settings(
                provider_id="openai_compatible",
                provider_base_url="https://two.example/v1",
            )
        ).runtime_identity,
    }

    assert len(identities) == 5


def test_endpoint_trailing_slash_is_equivalent_but_path_case_is_not() -> None:
    """只消除无语义末尾斜线，不擅自改写可能区分大小写的 URL path。"""

    without_slash = build_provider_spec(
        _settings(
            provider_id="openai_compatible",
            provider_base_url="https://provider.example/v1",
        )
    )
    with_slash = build_provider_spec(
        _settings(
            provider_id="openai_compatible",
            provider_base_url="https://provider.example/v1/",
        )
    )
    different_path_case = build_provider_spec(
        _settings(
            provider_id="openai_compatible",
            provider_base_url="https://provider.example/V1",
        )
    )

    assert without_slash.runtime_identity == with_slash.runtime_identity
    assert without_slash.runtime_identity != different_path_case.runtime_identity


def test_build_chat_model_passes_only_controlled_options(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """模型工厂只透传冻结参数，并关闭会与 Agent middleware 相乘的 SDK retry。"""

    captured: dict[str, object] = {}
    sentinel = FakeListChatModel(responses=["unused"])

    def capture_init_chat_model(
        model: str,
        *,
        model_provider: str,
        **kwargs: object,
    ) -> FakeListChatModel:
        captured.update({"model": model, "model_provider": model_provider, **kwargs})
        return sentinel

    monkeypatch.setattr(
        "stardew_npc_agent.model_provider.init_chat_model",
        capture_init_chat_model,
    )
    settings = _settings(
        provider_id="openai_compatible",
        provider_base_url="https://provider.example/v1",
        provider_wire_api="responses",
        provider_request_timeout_seconds=17.0,
        provider_max_output_tokens=192,
    )
    spec = build_provider_spec(settings)

    model = build_chat_model(settings, spec)

    assert model is sentinel
    assert captured == {
        "model": "test-model",
        "model_provider": "openai",
        "api_key": settings.provider_api_key,
        "base_url": "https://provider.example/v1",
        "use_responses_api": True,
        "timeout": 17.0,
        "max_retries": 0,
        "max_completion_tokens": 192,
        "stream_usage": False,
    }


def test_openai_factory_ignores_generic_base_url_environment(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """宿主机通用 OpenAI base URL 变量不能重定向项目的官方 Provider key。"""

    captured: dict[str, object] = {}
    sentinel = FakeListChatModel(responses=["unused"])

    def capture_init_chat_model(
        model: str,
        *,
        model_provider: str,
        **kwargs: object,
    ) -> FakeListChatModel:
        captured.update({"model": model, "model_provider": model_provider, **kwargs})
        return sentinel

    monkeypatch.setenv("OPENAI_BASE_URL", "https://evil.example/v1")
    monkeypatch.setenv("OPENAI_API_BASE", "https://evil.example/v1")
    monkeypatch.setattr(
        "stardew_npc_agent.model_provider.init_chat_model",
        capture_init_chat_model,
    )
    settings = _settings()

    model = build_chat_model(settings, build_provider_spec(settings))

    assert model is sentinel
    assert captured["base_url"] == "https://api.openai.com/v1"


def test_build_chat_model_rejects_missing_project_key_before_initializer(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """即使调用方绕过 Settings 赋值校验，工厂也不能回退到通用 SDK key。"""

    settings = _settings()
    spec = build_provider_spec(settings)
    settings_without_key = settings.model_copy(update={"provider_api_key": None})

    def fail_if_initialized(*_args: object, **_kwargs: object) -> BaseChatModel:
        raise AssertionError("缺少项目 key 时不得调用 init_chat_model")

    monkeypatch.setattr(
        "stardew_npc_agent.model_provider.init_chat_model",
        fail_if_initialized,
    )

    with pytest.raises(ValueError, match="provider_api_key"):
        build_chat_model(settings_without_key, spec)


class _AsyncClientSentinel:
    """记录异步 client 关闭调用，并可模拟释放异常。"""

    def __init__(self, failure: RuntimeError | None = None) -> None:
        self.closed = False
        self.failure = failure

    async def close(self) -> None:
        """标记调用；如配置异常则在标记后抛出，便于验证 finally 语义。"""

        self.closed = True
        if self.failure is not None:
            raise self.failure


class _SyncClientSentinel:
    """记录同步 client 是否完成关闭调用。"""

    def __init__(self) -> None:
        self.closed = False

    def close(self) -> None:
        """模拟 SDK 同步 client 的无参数 close。"""

        self.closed = True


class _OwnedClientsSentinel:
    """仅在测试中提供与 ChatOpenAI 相同的 root client 属性形状。"""

    def __init__(
        self,
        async_client: _AsyncClientSentinel,
        sync_client: _SyncClientSentinel,
    ) -> None:
        self.root_async_client = async_client
        self.root_client = sync_client


@pytest.mark.asyncio
async def test_close_owned_chat_model_closes_async_and_sync_clients() -> None:
    """正常关闭必须同时释放模型持有的异步与同步 HTTP client。"""

    async_client = _AsyncClientSentinel()
    sync_client = _SyncClientSentinel()
    model = cast(BaseChatModel, _OwnedClientsSentinel(async_client, sync_client))

    await close_owned_chat_model(model)

    assert async_client.closed is True
    assert sync_client.closed is True


@pytest.mark.asyncio
async def test_close_owned_chat_model_still_closes_sync_client_after_async_failure() -> None:
    """异步释放异常不能跳过独立同步 client，但原异常仍需向上传播。"""

    expected_error = RuntimeError("async-close-failed")
    async_client = _AsyncClientSentinel(expected_error)
    sync_client = _SyncClientSentinel()
    model = cast(BaseChatModel, _OwnedClientsSentinel(async_client, sync_client))

    with pytest.raises(RuntimeError, match="async-close-failed") as error_info:
        await close_owned_chat_model(model)

    assert error_info.value is expected_error
    assert async_client.closed is True
    assert sync_client.closed is True
