"""compatible Provider 的 Keychain 录入与后端启动器回归测试。

测试通过临时 HOME、临时非秘密配置以及 PATH 中的 fake ``security``/``uv`` 驱动真实
zsh 脚本。这样可以验证参数、环境传递和失败顺序，同时保证测试永远不会访问用户真实
Keychain、启动 Uvicorn、执行 migration 或发送 Provider 请求。
"""

from __future__ import annotations

import os
import subprocess
from pathlib import Path

REPOSITORY_ROOT = Path(__file__).resolve().parents[3]
CONFIGURE_KEY_SCRIPT = REPOSITORY_ROOT / "scripts" / "configure_provider_key.zsh"
START_BACKEND_SCRIPT = REPOSITORY_ROOT / "scripts" / "start_agent_backend.zsh"


def _write_executable(path: Path, source: str) -> None:
    """创建只供当前测试进程使用的可执行替身。"""

    path.write_text(source, encoding="utf-8")
    path.chmod(0o700)


def _write_provider_environment(path: Path) -> None:
    """写入与用户已确认映射一致、但完全不含密钥的临时配置。"""

    path.write_text(
        "\n".join(
            (
                "STARDEW_NPC_AGENT_DIALOGUE_GENERATOR_MODE=agent",
                "STARDEW_NPC_AGENT_PROVIDER_ID=openai_compatible",
                "STARDEW_NPC_AGENT_PROVIDER_MODEL=test-model",
                "STARDEW_NPC_AGENT_PROVIDER_BASE_URL=https://provider.example/v1",
                "STARDEW_NPC_AGENT_PROVIDER_WIRE_API=responses",
                "",
            )
        ),
        encoding="utf-8",
    )


def test_provider_environment_fixture_uses_public_neutral_placeholders(tmp_path: Path) -> None:
    """公开测试不得固化维护者的真实模型 ID、endpoint 或付费 replay 入口。"""

    provider_environment = tmp_path / "provider.env"
    _write_provider_environment(provider_environment)
    contents = provider_environment.read_text(encoding="utf-8")

    assert "STARDEW_NPC_AGENT_PROVIDER_MODEL=test-model" in contents
    assert "STARDEW_NPC_AGENT_PROVIDER_BASE_URL=https://provider.example/v1" in contents
    replay_entrypoint = "run_domain_" + "provider_replay"
    assert replay_entrypoint not in Path(__file__).read_text(encoding="utf-8")


def _base_environment(tmp_path: Path, fake_bin: Path) -> dict[str, str]:
    """构造隔离的脚本环境，避免继承真实 Provider 或 Keychain 配置。"""

    environment = os.environ.copy()
    for name in tuple(environment):
        if name.startswith("STARDEW_NPC_AGENT_"):
            environment.pop(name)
    environment.update(
        {
            "HOME": str(tmp_path / "home"),
            "USER": "launcher-test-user",
            "PATH": f"{fake_bin}:/usr/bin:/bin",
        }
    )
    return environment


def test_start_launcher_loads_non_secret_config_keychain_key_and_fixed_server_command(
    tmp_path: Path,
) -> None:
    """成功启动前必须先取 Keychain key、迁移同一默认库，再固定监听 loopback。

    fake ``uv`` 只记录环境和参数，不运行任何真实 Python 命令。测试使用的占位 key
    也只比较是否正确传递，禁止让启动器把它打印到 stdout/stderr。
    """

    assert START_BACKEND_SCRIPT.is_file(), "启动器尚未实现"
    fake_bin = tmp_path / "bin"
    fake_bin.mkdir()
    provider_environment = tmp_path / "provider.env"
    _write_provider_environment(provider_environment)
    security_log = tmp_path / "security.log"
    uv_log = tmp_path / "uv.log"

    _write_executable(
        fake_bin / "security",
        """#!/bin/zsh
print -r -- "$@" > "$TEST_SECURITY_LOG"
print -r -- "$TEST_PROVIDER_KEY"
""",
    )
    _write_executable(
        fake_bin / "uv",
        """#!/bin/zsh
{
  print -r -- "ARGS:$*"
  print -r -- "PWD:$PWD"
  print -r -- "MODE:${STARDEW_NPC_AGENT_DIALOGUE_GENERATOR_MODE:-}"
  print -r -- "PROVIDER:${STARDEW_NPC_AGENT_PROVIDER_ID:-}"
  print -r -- "MODEL:${STARDEW_NPC_AGENT_PROVIDER_MODEL:-}"
  print -r -- "BASE_URL:${STARDEW_NPC_AGENT_PROVIDER_BASE_URL:-}"
  print -r -- "WIRE_API:${STARDEW_NPC_AGENT_PROVIDER_WIRE_API:-}"
  if [[ "${STARDEW_NPC_AGENT_PROVIDER_API_KEY:-}" == "$TEST_PROVIDER_KEY" ]]; then
    print -r -- "KEY_MATCH:true"
  else
    print -r -- "KEY_MATCH:false"
  fi
} >> "$TEST_UV_LOG"
""",
    )
    environment = _base_environment(tmp_path, fake_bin)
    environment.update(
        {
            "STARDEW_NPC_AGENT_PROVIDER_ENV_FILE": str(provider_environment),
            "STARDEW_NPC_AGENT_UV_BIN": str(fake_bin / "uv"),
            "TEST_SECURITY_LOG": str(security_log),
            "TEST_UV_LOG": str(uv_log),
            "TEST_PROVIDER_KEY": "launcher-placeholder-secret",
        }
    )

    completed = subprocess.run(
        [str(START_BACKEND_SCRIPT)],
        check=False,
        capture_output=True,
        text=True,
        env=environment,
        timeout=10,
    )

    assert completed.returncode == 0, completed.stderr
    assert security_log.read_text(encoding="utf-8").strip() == (
        "find-generic-password -a launcher-test-user -s stardew-npc-agent-provider -w"
    )
    uv_calls = uv_log.read_text(encoding="utf-8")
    assert "ARGS:run alembic upgrade head" in uv_calls
    assert "ARGS:run uvicorn stardew_npc_agent.main:app --host 127.0.0.1 --port 8000" in uv_calls
    assert f"PWD:{REPOSITORY_ROOT / 'backend'}" in uv_calls
    assert "MODE:agent" in uv_calls
    assert "PROVIDER:openai_compatible" in uv_calls
    assert "MODEL:test-model" in uv_calls
    assert "BASE_URL:https://provider.example/v1" in uv_calls
    assert "WIRE_API:responses" in uv_calls
    # Alembic 不需要 Provider key。启动器应先在 shell 内取到秘密，但只在迁移成功后
    # export 给 Uvicorn，缩小子进程可见面。
    assert uv_calls.count("KEY_MATCH:false") == 1
    assert uv_calls.count("KEY_MATCH:true") == 1
    assert "launcher-placeholder-secret" not in completed.stdout
    assert "launcher-placeholder-secret" not in completed.stderr


def test_start_launcher_stops_before_migration_when_keychain_item_is_missing(
    tmp_path: Path,
) -> None:
    """Keychain 查询失败必须 fail closed，不能退回通用环境 key 或继续启动。"""

    assert START_BACKEND_SCRIPT.is_file(), "启动器尚未实现"
    fake_bin = tmp_path / "bin"
    fake_bin.mkdir()
    provider_environment = tmp_path / "provider.env"
    _write_provider_environment(provider_environment)
    uv_log = tmp_path / "uv.log"

    _write_executable(
        fake_bin / "security",
        """#!/bin/zsh
exit 44
""",
    )
    _write_executable(
        fake_bin / "uv",
        """#!/bin/zsh
print -r -- "unexpected" >> "$TEST_UV_LOG"
""",
    )
    environment = _base_environment(tmp_path, fake_bin)
    environment.update(
        {
            "STARDEW_NPC_AGENT_PROVIDER_ENV_FILE": str(provider_environment),
            "STARDEW_NPC_AGENT_UV_BIN": str(fake_bin / "uv"),
            "TEST_UV_LOG": str(uv_log),
        }
    )

    completed = subprocess.run(
        [str(START_BACKEND_SCRIPT)],
        check=False,
        capture_output=True,
        text=True,
        env=environment,
        timeout=10,
    )

    assert completed.returncode != 0
    assert "KEYCHAIN_ITEM_NOT_FOUND" in completed.stderr
    assert not uv_log.exists(), "缺少 key 时不得执行 migration 或启动 Uvicorn"


def test_start_launcher_rejects_api_key_in_non_secret_environment_file(
    tmp_path: Path,
) -> None:
    """仓库外 env 文件也必须保持非秘密；发现 key 后不得触碰 Keychain 或 uv。"""

    assert START_BACKEND_SCRIPT.is_file(), "启动器尚未实现"
    fake_bin = tmp_path / "bin"
    fake_bin.mkdir()
    provider_environment = tmp_path / "provider.env"
    _write_provider_environment(provider_environment)
    with provider_environment.open("a", encoding="utf-8") as handle:
        handle.write("STARDEW_NPC_AGENT_PROVIDER_API_KEY=must-not-be-loaded\n")
    security_log = tmp_path / "security.log"
    uv_log = tmp_path / "uv.log"
    _write_executable(
        fake_bin / "security",
        """#!/bin/zsh
print -r -- "unexpected" > "$TEST_SECURITY_LOG"
""",
    )
    _write_executable(
        fake_bin / "uv",
        """#!/bin/zsh
print -r -- "unexpected" > "$TEST_UV_LOG"
""",
    )
    environment = _base_environment(tmp_path, fake_bin)
    environment.update(
        {
            "STARDEW_NPC_AGENT_PROVIDER_ENV_FILE": str(provider_environment),
            "STARDEW_NPC_AGENT_UV_BIN": str(fake_bin / "uv"),
            "TEST_SECURITY_LOG": str(security_log),
            "TEST_UV_LOG": str(uv_log),
        }
    )

    completed = subprocess.run(
        [str(START_BACKEND_SCRIPT)],
        check=False,
        capture_output=True,
        text=True,
        env=environment,
        timeout=10,
    )

    assert completed.returncode != 0
    assert "CONFIG_CONTAINS_PROVIDER_SECRET" in completed.stderr
    assert "must-not-be-loaded" not in completed.stdout
    assert "must-not-be-loaded" not in completed.stderr
    assert not security_log.exists()
    assert not uv_log.exists()


def test_configure_key_script_uses_interactive_security_prompt_without_secret_argument(
    tmp_path: Path,
) -> None:
    """一次性录入只能把 ``-w`` 放在末尾，让 Keychain 自己交互读取秘密。"""

    assert CONFIGURE_KEY_SCRIPT.is_file(), "Keychain 录入脚本尚未实现"
    fake_bin = tmp_path / "bin"
    fake_bin.mkdir()
    security_log = tmp_path / "security.log"
    _write_executable(
        fake_bin / "security",
        """#!/bin/zsh
print -r -- "$@" > "$TEST_SECURITY_LOG"
""",
    )
    environment = _base_environment(tmp_path, fake_bin)
    environment["TEST_SECURITY_LOG"] = str(security_log)

    completed = subprocess.run(
        [str(CONFIGURE_KEY_SCRIPT)],
        check=False,
        capture_output=True,
        text=True,
        env=environment,
        timeout=10,
    )

    assert completed.returncode == 0, completed.stderr
    arguments = security_log.read_text(encoding="utf-8").strip().split()
    assert arguments == [
        "add-generic-password",
        "-U",
        "-a",
        "launcher-test-user",
        "-s",
        "stardew-npc-agent-provider",
        "-l",
        "Stardew",
        "NPC",
        "Agent",
        "Provider",
        "API",
        "Key",
        "-w",
    ]
    assert arguments[-1] == "-w"
