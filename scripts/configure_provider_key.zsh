#!/bin/zsh

# 将 Stardew NPC Agent 的 Provider API key 一次性写入 macOS Keychain。
#
# 安全设计：
# - 脚本不从参数、环境文件或 stdin 管道读取密钥；
# - ``security -w`` 必须是最后一个参数，由系统工具直接进行交互式提示；
# - 不使用 ``-A``，因此不会把该条目开放给任意应用；
# - ``-U`` 允许同一命令安全轮换已有 key。

set -euo pipefail
umask 077

readonly KEYCHAIN_ACCOUNT="${STARDEW_NPC_AGENT_KEYCHAIN_ACCOUNT:-${USER:?USER 未设置}}"
readonly KEYCHAIN_SERVICE="${STARDEW_NPC_AGENT_KEYCHAIN_SERVICE:-stardew-npc-agent-provider}"
readonly KEYCHAIN_LABEL="Stardew NPC Agent Provider API Key"

if ! command -v security >/dev/null 2>&1; then
  print -u2 -r -- "ERROR: MACOS_SECURITY_COMMAND_NOT_FOUND"
  exit 1
fi

print -r -- "即将由 macOS Keychain 提示输入 Provider API key。输入内容不会显示。"
security add-generic-password \
  -U \
  -a "$KEYCHAIN_ACCOUNT" \
  -s "$KEYCHAIN_SERVICE" \
  -l "$KEYCHAIN_LABEL" \
  -w
print -r -- "Provider API key 已写入或更新到 macOS Keychain。"
