# Stardew NPC Dialogue Enhancer

An unofficial Stardew Valley mod that enhances safe daily dialogue for the twelve vanilla marriage candidates using local memory, a constrained read-only agent, deterministic guards, and fail-closed fallback to the original game dialogue.

> This is an unofficial community project and is not affiliated with, endorsed by, or sponsored by ConcernedApe or Stardew Valley.

## Project status

当前版本为 **v0.1.0**，是范围受限的本地实验性 Mod。它面向单机、十二名原版可婚 NPC 和经过审核的安全日常对话来源；不是通用生产服务，也不承诺覆盖其他 NPC、其他平台或所有游戏对话场景。

以下状态词用于避免把代码存在、自动化验证和实机证据混为一谈：

- **Implemented**：相应运行路径已经实现。
- **Automated**：存在自动化门禁覆盖。
- **Observed**：已经在获准的实机范围内观察到正向行为。
- **Not observed**：功能已实现并有自动化覆盖，但尚未取得对应实机正例。
- **Deferred**：保留为后续工作，本版本不实现。
- **Waiver**：已明确接受缺少某项实机证据的风险。
- **Out of scope**：不属于 v0.1.0 的产品边界。

| 能力或场景 | 当前状态 | 边界说明 |
| --- | --- | --- |
| 十二名原版可婚 NPC | Implemented / Automated | 固定支持 Abigail、Alex、Elliott、Emily、Haley、Harvey、Leah、Maru、Penny、Sam、Sebastian、Shane。 |
| OrdinaryDaily | Implemented / Automated / Observed | 仅增强通过来源、模板和身份检查的普通日常候选。 |
| RainyDaily | Implemented / Automated / Not observed | exact rainy 路径已实现，但尚无实机正例。 |
| 三个零业务参数只读记忆工具、D+1 与 durable outbox | Implemented / Automated / Observed | 昨日事实、权限和回执闭环已有实机证据；Mastery 实机正例为 Waiver。 |
| typed `@` 在本地展开玩家名 | Implemented / Automated / Not observed | Provider 只看到 typed slot，真实玩家名只在本地恢复；尚无实机正例。 |
| Guard、最多一次 Repair、原版 fallback | Implemented / Automated | 不合格、超时、失败或身份不一致时保持原版台词。 |
| `$h` / `$a` 结构化表情增强 | Deferred | 当前版本不生成这类结构化表情控制。 |
| 真正 MarriageDialogue、GreenRain、`%endearment`、Krobus/Mod NPC、多人保证、自由聊天 | Out of scope | 这些能力不会因安装 v0.1.0 而被隐式启用。 |

## What is implemented

- 固定的十二 NPC allowlist，以及 OrdinaryDaily 和 exact RainyDaily 的来源分类、typed template 与身份校验。
- SMAPI 侧事件 durable outbox、后端 SQLite 记忆存储、严格 D+1 可见性和 displayed ACK 闭环。
- 三个由运行时注入 NPC 与日期边界的零业务参数只读工具：`get_npc_history`、`get_player_progression`、`get_world_progression`。
- 受约束的 Agent 决策、确定性 Guard、最多一次 Repair，以及任何不确定情况下的 passthrough/fallback。
- 每日生成 cache。NPC 点击路径只读取已准备好的 cache，不在交互时等待 Provider。
- typed `@` 玩家名 slot：真实玩家身份不进入 Prompt，只有通过 Guard 的 typed slot 才能在本地展开。
- C# 与 Python 共享的 [`contracts/v1`](contracts/v1) wire schema 和 [`contracts/fixtures`](contracts/fixtures) 测试数据。

## How it works

```text
SMAPI
  → FastAPI
  → SQLite / Memory
  → constrained Agent + read-only tools
  → Guard / one Repair
  → daily cache
  → native DialogueBox
  → displayed ACK
```

SMAPI 先把获准的游戏事件写入 durable outbox，再向只监听 loopback 的 FastAPI 后端同步。后端把事实持久化到 SQLite；新事实在 D 日不可用于生成，从 D+1 起才可能被对应 NPC 和记忆领域检索，避免同日“预知”或跨 NPC 泄漏。

每日候选在点击前生成。受约束 Agent 只能选择 passthrough 或安全改写，并且只能调用三个零业务参数、只读的记忆工具；工具的 NPC、日期截止点和权限由可信运行时注入。候选随后经过确定性 Guard。只有可修复的文本问题才允许最多一次 Repair；失败、超时、证据越界、来源不合格或 Repair 后仍不合格时，都返回原版 fallback。

通过 Guard 的结果进入 daily cache。玩家点击 NPC 时，Mod 只读取 cache，并复核 NPC、日期、来源与候选 identity；任何 mismatch 都回退原版，而不会在点击路径发起生成。展示仍使用游戏原生 `DialogueBox`。只有真正显示的生成结果才发送 displayed ACK，用于完成回执和记忆冷却；ACK 发送失败时保留在 durable outbox 中重试。

## Requirements

- Stardew Valley **1.6.15**
- SMAPI **4.5.2**
- **.NET 6 SDK**
- Python **`>=3.11,<3.12`**
- [`uv`](https://docs.astral.sh/uv/)

当前只在 macOS 上完成验证。本文不把验证结论外推到 Windows、Linux、更早的 Stardew Valley 或更早的 SMAPI 版本。当前构建和 extractor 的游戏路径示例只适用于已验证的 macOS Steam 布局；其他布局需要用户自行核对 DLL 与 Content 的实际位置。

## Quick start: scripted mode

默认 `scripted` 模式不会构造 Provider，不读取通用 `OPENAI_*` key，不产生模型费用；生成接口正常返回 passthrough，由 Mod 保留原版台词。

```bash
git clone https://github.com/rookieindongchuanroad/stardew-npc-dialogue-enhancer.git
cd stardew-npc-dialogue-enhancer/backend
uv sync --locked
uv lock --check
uv run alembic upgrade head
uv run uvicorn stardew_npc_agent.main:app --host 127.0.0.1 --port 8000
```

上述 `alembic upgrade head` 会创建或升级当前 backend 目录使用的本地 SQLite 数据库。请先确认这是你希望使用的本地运行数据路径；测试与发布门禁应改用后文给出的临时数据库。

## Agent mode

Agent mode 必须由项目命名空间变量显式启用。下面两组 Provider 配置二选一；模型名必须由用户替换 `<model>`，不要把真实 endpoint、model 或 key 提交到仓库。

OpenAI：

```bash
export STARDEW_NPC_AGENT_DIALOGUE_GENERATOR_MODE=agent
export STARDEW_NPC_AGENT_PROVIDER_ID=openai
export STARDEW_NPC_AGENT_PROVIDER_MODEL="<model>"
export STARDEW_NPC_AGENT_PROVIDER_WIRE_API=responses
unset STARDEW_NPC_AGENT_PROVIDER_BASE_URL
```

OpenAI-compatible：

```bash
export STARDEW_NPC_AGENT_DIALOGUE_GENERATOR_MODE=agent
export STARDEW_NPC_AGENT_PROVIDER_ID=openai_compatible
export STARDEW_NPC_AGENT_PROVIDER_MODEL="<model>"
export STARDEW_NPC_AGENT_PROVIDER_BASE_URL="https://provider.example/v1"
export STARDEW_NPC_AGENT_PROVIDER_WIRE_API=chat_completions
```

在同一个 shell 中隐藏输入 API key：

```bash
printf 'Provider API key: '
IFS= read -r -s STARDEW_NPC_AGENT_PROVIDER_API_KEY
printf '\n'
export STARDEW_NPC_AGENT_PROVIDER_API_KEY
```

然后使用 Quick start 中相同的 loopback `uvicorn` 命令启动后端。仓库内的 `.env.example` 仅用于说明字段，Settings **不会自动加载它**；通用 `OPENAI_API_KEY` 也**不会**启用 Agent mode。Provider 配置缺失或不合法时，后端 fail closed，不猜测用户意图。

## Build and install the Mod

以下命令从仓库根目录运行。`STARDEW_GAME_PATH` 必须指向包含 `Stardew Valley.dll` 的目录。当前这一定位方式只在已验证的 macOS Steam 布局成立，不代表其他平台或安装布局已经受支持。

```bash
export STARDEW_GAME_PATH="/path/to/directory-containing-Stardew Valley.dll"

dotnet restore smapi/StardewNpcAgent.sln \
  -p:GamePath="$STARDEW_GAME_PATH"
dotnet build smapi/StardewNpcAgent/StardewNpcAgent.csproj \
  -c Release \
  --no-restore \
  -p:GamePath="$STARDEW_GAME_PATH" \
  -p:EnableModDeploy=false \
  --nologo
```

`EnableModDeploy=false` 禁止 build 自动复制到 active Mods 目录，但 `dotnet` 仍可能在仓库内生成 `bin/` 和 `obj/`。若目标 `StardewNpcAgent` Mod 目录尚不存在，可在确认 zip 后首次安装：

```bash
export STARDEW_MODS_DIR="/path/to/Stardew Valley/Mods"
export MOD_ZIP="smapi/StardewNpcAgent/bin/Release/net6.0/StardewNpcAgent 0.1.0.zip"
test ! -e "$STARDEW_MODS_DIR/StardewNpcAgent"
unzip "$MOD_ZIP" -d "$STARDEW_MODS_DIR"
```

如果已有 `StardewNpcAgent` 目录，不要执行覆盖式解压。先在仓库和游戏目录之外备份现有 `config.json` 与 `data/`，检查新包内容，再人工替换 manifest 和 DLL；保留并复核原配置与数据。

本仓库另含本地只读 [`VanillaDialogueManifestExtractor`](tools/VanillaDialogueManifestExtractor/README.md)。它默认 dry-run，不写 manifest；`dotnet` 仍可能生成仓库内 `bin/obj`。只有显式传入 `--output` 才进入 extractor 的业务写入边界，真实输出应放在仓库外，且默认不提交。

## Configure the twelve NPCs

首次生成的新配置默认列出十二人，但 `EnableStaticDialogueSpike` 与 `EnableAgentDialogue` 默认都为 `false`。正式模式只开启 `EnableAgentDialogue`；静态 Spike 与 Agent 互斥，不能同时为 `true`。

完整 `config.json` 示例：

```json
{
  "EnableStaticDialogueSpike": false,
  "EnableAgentDialogue": true,
  "BackendBaseUrl": "http://127.0.0.1:8000/",
  "EventRequestTimeoutMilliseconds": 3000,
  "GenerationRequestTimeoutMilliseconds": 120000,
  "DisplayAckRequestTimeoutMilliseconds": 3000,
  "TargetNpcIds": [
    "Abigail", "Alex", "Elliott", "Emily", "Haley", "Harvey",
    "Leah", "Maru", "Penny", "Sam", "Sebastian", "Shane"
  ],
  "StaticDialogueMarker": "【NPC Agent 静态增强测试】"
}
```

兼容与失败边界：

- 旧配置缺少 `TargetNpcIds` 时，仅沿用 Abigail、Sebastian 两人的兼容默认值，不会自动扩大到十二人。
- `TargetNpcIds: null` 或空数组表示启用零名 NPC。
- 未知 ID 会被忽略；匹配使用稳定内部 ID，而不是显示名或模糊匹配。
- malformed JSON、读取竞态、模式冲突、非 loopback backend URL 或超出范围的 timeout 都会 fail closed，不会猜测或覆盖坏配置。

## Test and verification

以下是**首个公开快照发布前门禁，任一失败不推送**。命令不调用收费 Provider，不启动游戏，不部署 active Mod；它们不复用历史测试数量、真实 XNB hash 或 Provider replay 结果。先自行设置本机合法游戏副本的可移植环境变量。

Python、eval 与静态检查：

```bash
cd backend
uv sync --locked
uv lock --check
uv run ruff check .
uv run ruff format --check .
uv run mypy --strict src evals
uv run pytest -q
uv run python -m evals.run_eval
```

Alembic 只使用临时 SQLite，不触碰默认正式库：

```bash
TEMP_DB_DIR="$(mktemp -d /tmp/stardew-public-alembic.XXXXXX)"
TEMP_DB_PATH="$TEMP_DB_DIR/verification.sqlite3"
uv run python - "$TEMP_DB_PATH" <<'PY'
from pathlib import Path
import sys

from alembic import command
from alembic.config import Config

database_path = Path(sys.argv[1]).resolve()
config = Config("alembic.ini")
config.set_main_option("sqlalchemy.url", f"sqlite+aiosqlite:///{database_path}")
command.upgrade(config, "head")
command.check(config)
print("temporary Alembic upgrade/check: ok")
PY
```

C# full gate（先返回仓库根目录）：

```bash
cd ..
export STARDEW_GAME_ROOT="/path/to/Stardew Valley"
export STARDEW_GAME_PATH="$STARDEW_GAME_ROOT/Contents/MacOS"
test -f "$STARDEW_GAME_PATH/Stardew Valley.dll"

dotnet restore smapi/StardewNpcAgent.sln \
  -p:GamePath="$STARDEW_GAME_PATH"
dotnet format smapi/StardewNpcAgent.sln \
  --verify-no-changes \
  --no-restore
dotnet build smapi/StardewNpcAgent/StardewNpcAgent.csproj \
  -c Release \
  --no-restore \
  -p:GamePath="$STARDEW_GAME_PATH" \
  -p:EnableModDeploy=false \
  --nologo
dotnet test smapi/StardewNpcAgent.Tests/StardewNpcAgent.Tests.csproj \
  -c Release \
  --no-restore \
  -p:GamePath="$STARDEW_GAME_PATH" \
  -p:EnableModDeploy=false \
  --nologo
```

Extractor 只运行 synthetic/in-memory 测试，不读取真实对话正文：

```bash
dotnet restore \
  tools/VanillaDialogueManifestExtractor.Tests/VanillaDialogueManifestExtractor.Tests.csproj \
  -p:StardewGamePath="$STARDEW_GAME_ROOT"
dotnet test \
  tools/VanillaDialogueManifestExtractor.Tests/VanillaDialogueManifestExtractor.Tests.csproj \
  -c Release \
  --no-restore \
  -p:StardewGamePath="$STARDEW_GAME_ROOT" \
  --filter 'FullyQualifiedName~DialogueManifestExtractorTests|FullyQualifiedName~ExtractorOptionsTests' \
  --nologo
```

## Known limitations

- v0.1.0 只处理固定十二名原版可婚 NPC 的安全 daily source；不把运行时婚姻状态等同于 MarriageDialogue 增强资格。
- RainyDaily 与 typed `@` 本地展开已有实现和自动化门禁，但尚未取得实机正例。
- Mastery producer 的实机正例为 Waiver；多人 producer 与多人一致性保证不在本版本范围。
- `$h/$a` 结构化表情增强仍为 Deferred。
- 真正 MarriageDialogue、GreenRain、`%endearment`、Krobus/Mod NPC 和自由聊天均为 Out of scope。
- Agent 输出受 Guard、Repair 和 fallback 约束，但这不是对语言模型绝对正确性的承诺；任何不确定候选都应回退原版。
- 目前只有已验证的 macOS 版本组合与 Steam 路径布局证据。

## Privacy and security

默认 scripted 模式不构造 Provider。显式启用 Agent mode 后，后端可能向**用户配置的 Provider**发送被选 source、style examples、NPC 上下文和相关 evidence；请先阅读并接受相应 Provider 的数据与费用政策。

真实玩家名不会发送给 Provider。Prompt 中只发送 typed player-name slot，只有本地 Guard 通过后才由 Mod 在本地展开。SMAPI 到 FastAPI 后端的连接被限制为 loopback；FastAPI 后端到外部 Provider 的连接**不是** loopback，可能离开本机。

API key 只应通过隐藏终端输入或本地 secrets manager 提供，不得写入 Mod `config.json`、仓库、日志或截图。不要提交真实 extractor manifest、存档、数据库、outbox、崩溃转储或任何含玩家信息的本地产物。安全检查只能证明指定扫描范围内未发现命中，不能替代人工秘密审查。

## Contributing

提交修改前请先阅读根目录 [`AGENTS.md`](AGENTS.md)。保持任务边界小而可验证，优先从失败测试开始，完成 focused regression 后再运行 fresh full gate。不得削弱固定 NPC allowlist、共享合同、D+1、只读工具、cache-only 点击路径、Guard/Repair/fallback 或 `EnableModDeploy=false` 等安全边界。

Issue 或变更说明应明确区分已确认事实、合理推断与尚未运行的验证；不要用旧测试数量或旧实机记录替代当前证据。涉及 Provider、真实游戏资产、active Mod、存档或正式数据的操作，必须先取得明确授权。

## License and third-party rights

仓库原创代码与文档按根目录 [`LICENSE`](LICENSE) 中的 MIT License 提供。Stardew Valley 名称、商标、游戏资产、对话与截图仍归各自权利人所有；本仓库不重新许可这些第三方内容。

The MIT License applies only to original code and documentation the repository authors are entitled to license. It does not license Stardew Valley names, trademarks, assets, dialogue, or screenshots.
