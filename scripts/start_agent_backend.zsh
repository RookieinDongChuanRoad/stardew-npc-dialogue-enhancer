#!/bin/zsh

# 使用仓库外非秘密配置和 macOS Keychain 启动真实 Agent 后端。
#
# 职责边界：
# 1. 只解析五个明确允许的非秘密 Provider 字段，不执行 env 文件中的 shell 代码；
# 2. 从 Keychain 读取 API key，但绝不打印、写盘或传给不需要它的 migration 子进程；
# 3. migration 成功后才把 key export 给 Uvicorn，并固定监听 127.0.0.1:8000；
# 4. 任一配置、Keychain、uv 或 migration 错误都会 fail closed，不退回通用 OPENAI_* 环境变量。

set -euo pipefail
umask 077

readonly SCRIPT_DIRECTORY="${0:A:h}"
readonly REPOSITORY_ROOT="${SCRIPT_DIRECTORY:h}"
readonly BACKEND_DIRECTORY="${REPOSITORY_ROOT}/backend"
readonly PROVIDER_ENV_FILE="${STARDEW_NPC_AGENT_PROVIDER_ENV_FILE:-${HOME}/.config/stardew-npc-agent/provider.env}"
readonly KEYCHAIN_ACCOUNT="${STARDEW_NPC_AGENT_KEYCHAIN_ACCOUNT:-${USER:?USER 未设置}}"
readonly KEYCHAIN_SERVICE="${STARDEW_NPC_AGENT_KEYCHAIN_SERVICE:-stardew-npc-agent-provider}"

function fail() {
  # 只输出稳定错误码；不能附带配置行、URL、Keychain 内容或自由异常正文。
  print -u2 -r -- "ERROR: $1"
  exit 1
}

function load_non_secret_provider_environment() {
  # 逐行解析固定 ``NAME=value`` 格式，避免 ``source`` 执行命令替换或其他 shell 语句。
  # 值按原始字符串导出；Pydantic Settings 仍负责类型、URL 和字段组合的最终校验。
  local line
  local name
  local value

  [[ -f "$PROVIDER_ENV_FILE" ]] || fail "PROVIDER_ENV_FILE_NOT_FOUND"
  while IFS= read -r line || [[ -n "$line" ]]; do
    line="${line%$'\r'}"
    [[ -z "${line//[[:space:]]/}" ]] && continue
    [[ "$line" == \#* ]] && continue
    [[ "$line" == *=* ]] || fail "INVALID_PROVIDER_ENV_LINE"

    name="${line%%=*}"
    value="${line#*=}"
    case "$name" in
      STARDEW_NPC_AGENT_PROVIDER_API_KEY)
        fail "CONFIG_CONTAINS_PROVIDER_SECRET"
        ;;
      STARDEW_NPC_AGENT_DIALOGUE_GENERATOR_MODE \
        | STARDEW_NPC_AGENT_PROVIDER_ID \
        | STARDEW_NPC_AGENT_PROVIDER_MODEL \
        | STARDEW_NPC_AGENT_PROVIDER_BASE_URL \
        | STARDEW_NPC_AGENT_PROVIDER_WIRE_API)
        export "$name=$value"
        ;;
      *)
        fail "UNSUPPORTED_PROVIDER_ENV_FIELD"
        ;;
    esac
  done < "$PROVIDER_ENV_FILE"

  [[ -n "${STARDEW_NPC_AGENT_DIALOGUE_GENERATOR_MODE:-}" ]] \
    || fail "MISSING_DIALOGUE_GENERATOR_MODE"
  [[ -n "${STARDEW_NPC_AGENT_PROVIDER_ID:-}" ]] \
    || fail "MISSING_PROVIDER_ID"
  [[ -n "${STARDEW_NPC_AGENT_PROVIDER_MODEL:-}" ]] \
    || fail "MISSING_PROVIDER_MODEL"
  [[ -n "${STARDEW_NPC_AGENT_PROVIDER_BASE_URL:-}" ]] \
    || fail "MISSING_PROVIDER_BASE_URL"
  [[ -n "${STARDEW_NPC_AGENT_PROVIDER_WIRE_API:-}" ]] \
    || fail "MISSING_PROVIDER_WIRE_API"
}

function resolve_uv_binary() {
  # 当前工作区优先使用已锁定的本地 uv；测试或不同机器可通过显式变量替换路径。
  local project_uv="${REPOSITORY_ROOT}/.tools/uv-aarch64-apple-darwin/uv"
  if [[ -n "${STARDEW_NPC_AGENT_UV_BIN:-}" ]]; then
    print -r -- "$STARDEW_NPC_AGENT_UV_BIN"
  elif [[ -x "$project_uv" ]]; then
    print -r -- "$project_uv"
  elif command -v uv >/dev/null 2>&1; then
    command -v uv
  else
    fail "UV_COMMAND_NOT_FOUND"
  fi
}

# 即使调用方 shell 里残留旧项目 key，也必须以本次 Keychain 条目为唯一秘密来源。
unset STARDEW_NPC_AGENT_PROVIDER_API_KEY || true
load_non_secret_provider_environment

command -v security >/dev/null 2>&1 || fail "MACOS_SECURITY_COMMAND_NOT_FOUND"
provider_api_key="$(
  security find-generic-password \
    -a "$KEYCHAIN_ACCOUNT" \
    -s "$KEYCHAIN_SERVICE" \
    -w 2>/dev/null
)" || fail "KEYCHAIN_ITEM_NOT_FOUND"
[[ -n "$provider_api_key" ]] || fail "KEYCHAIN_ITEM_EMPTY"

uv_binary="$(resolve_uv_binary)"
[[ -x "$uv_binary" ]] || fail "UV_COMMAND_NOT_EXECUTABLE"
[[ -d "$BACKEND_DIRECTORY" ]] || fail "BACKEND_DIRECTORY_NOT_FOUND"

cd "$BACKEND_DIRECTORY"
print -r -- "正在升级本地数据库到 Alembic head（此步骤不会接收 Provider key）。"
"$uv_binary" run alembic upgrade head

# 只有 Uvicorn/应用进程需要密钥。export 后立即清除临时局部变量，减少 shell 内重复副本。
export STARDEW_NPC_AGENT_PROVIDER_API_KEY="$provider_api_key"
unset provider_api_key

print -r -- \
  "正在启动 Stardew NPC Agent 后端：provider=${STARDEW_NPC_AGENT_PROVIDER_ID}，model=${STARDEW_NPC_AGENT_PROVIDER_MODEL}，wire=${STARDEW_NPC_AGENT_PROVIDER_WIRE_API}。"
exec "$uv_binary" run uvicorn \
  stardew_npc_agent.main:app \
  --host 127.0.0.1 \
  --port 8000
