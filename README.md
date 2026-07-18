# Stardew NPC Dialogue Enhancer

> 🌾 让星露谷的日常对话，在合适的时候记起你们共同经历过的事。

Stardew NPC Dialogue Enhancer 是一个在本机运行的 SMAPI Mod。它会在每天开始时读取当天原台词，让 Agent 自己判断是否需要借助最近的送礼、关系、成长或世界进度来增强这句话；如果没有值得补充的内容，或者生成与检查失败，游戏继续显示原版台词。

当前版本面向十二名原版可婚 NPC。它不是自由聊天系统，也不会为了“看起来像 AI”而改写每一句话。

> 这是非官方社区项目，与 ConcernedApe 或 Stardew Valley 没有隶属、认可或赞助关系。

## 💬 对话会发生什么变化

> **效果示意，不是原版台词，也不是模型的固定输出**  
> 昨天，你送给 Abigail 一块紫水晶。第二天，她的普通日常台词可能自然地提起这件事，而不是突然忘记你们刚刚经历过什么。

真实结果取决于当天原台词、当时可见的相关记忆，以及 Agent 是否认为增强确实有价值。

- 🧠 **记得最近共同经历**：送礼、关系变化、技能成长和世界进度可能在之后的合适台词中得到回应。
- 🎮 **保留星露谷的交互方式**：仍然使用游戏原生肖像、表情与 `DialogueBox`，不另做聊天窗口。
- 🛡️ **不合适就不改**：Agent 可以原样返回；超时、检查失败或身份不一致时也会安全回退到原版。

## 🌱 目前能做什么

当前固定覆盖十二名原版可婚 NPC：

`Abigail`、`Alex`、`Elliott`、`Emily`、`Haley`、`Harvey`、`Leah`、`Maru`、`Penny`、`Sam`、`Sebastian`、`Shane`。

Agent 在判断有必要时，可以按需读取三类本地记忆：

- **NPC 相关经历**：送礼、关系变化等只属于当前 NPC 的记忆；
- **玩家成长**：技能等级、矿井进度、工具升级与 Mastery 等；
- **世界进度**：社区中心和其他已记录的世界变化。

今天新发生的事件不会立刻进入今天的台词，通常从第二天起才可能被使用。Agent 也不必提及每条可用记忆：原台词已经足够自然时，正确结果就是保留原文。

这个 Mod 只增强文字。它不会替 NPC 行走，不会替玩家作决定，也不会推进剧情或改变游戏进度。

## 🧭 使用前需要知道

- 当前是**开发者预览版**，暂未提供一键安装包；需要在本机运行 Python 后端并构建 SMAPI Mod。
- 它不是可以任意追问的自由聊天系统，也不会增强每名 NPC 的每一句台词。
- 当前只覆盖十二名原版可婚 NPC 的合格日常候选，不任意改写事件、过场、节日或 Green Rain 台词。
- 配偶的普通日常候选可以进入增强，但真正的 `MarriageDialogue` 仍不在当前版本范围内。
- `RainyDaily` 与 typed `@` 玩家名槽已有实现和自动化覆盖，但当前仍缺少对应实机正例。
- Mastery 记忆已有实现和自动化覆盖；其独立实机正例在当前版本被明确豁免，后续发现问题时再补充验证。
- `$h/$a` 表情增强、`%endearment`、Krobus、第三方 NPC、自由聊天和多人一致性保证尚未提供。
- 当前验证环境是 macOS、Stardew Valley 1.6.15 与 SMAPI 4.5.2；不能直接外推到其他系统和版本组合。

## 🚀 如何体验

完整体验分为四步：

1. 在本机准备并启动 FastAPI 后端；
2. 构建并安装 SMAPI Mod；
3. 先用默认 `scripted` 模式确认基础链路，再显式启用真正的 Agent 模式；
4. 进入存档，完成一次可记录的游戏经历并睡到下一天，再与受支持 NPC 交谈。

### ✅ 先验证安装是否正常

默认 `scripted` 模式不会构造 Provider，不需要 API key，也不会产生模型费用。它会正常返回 `passthrough`，让 Mod 保留原版台词。因此这一步用于确认后端、Mod 和基础请求链路能工作，**不代表 AI 对话增强已经启用**。

<details>
<summary>准备后端并以零费用 scripted 模式启动</summary>

```bash
git clone https://github.com/rookieindongchuanroad/stardew-npc-dialogue-enhancer.git
cd stardew-npc-dialogue-enhancer/backend
uv sync --locked
uv lock --check
uv run alembic upgrade head
uv run uvicorn stardew_npc_agent.main:app --host 127.0.0.1 --port 8000
```

`alembic upgrade head` 会创建或升级当前 `backend` 目录使用的本地 SQLite 数据库。请先确认这正是你希望使用的运行数据路径；测试不应指向这份默认运行库。

</details>

<details>
<summary>构建并首次安装 SMAPI Mod</summary>

需要先准备：

- Stardew Valley 1.6.15；
- SMAPI 4.5.2；
- .NET 6 SDK；
- Python `>=3.11,<3.12`；
- [`uv`](https://docs.astral.sh/uv/)。

从仓库根目录运行。`STARDEW_GAME_PATH` 必须指向包含 `Stardew Valley.dll` 的目录：

```bash
cd /path/to/stardew-npc-dialogue-enhancer
export STARDEW_GAME_PATH="/path/to/directory-containing-Stardew Valley.dll"
test -f "$STARDEW_GAME_PATH/Stardew Valley.dll"

dotnet restore smapi/StardewNpcAgent.sln \
  -p:GamePath="$STARDEW_GAME_PATH"
dotnet build smapi/StardewNpcAgent/StardewNpcAgent.csproj \
  -c Release \
  --no-restore \
  -p:GamePath="$STARDEW_GAME_PATH" \
  -p:EnableModDeploy=false \
  --nologo
```

`EnableModDeploy=false` 会阻止 build 自动写入 active Mods 目录。首次安装前先检查 Release zip，然后确认目标目录不存在：

```bash
export STARDEW_MODS_DIR="/path/to/Stardew Valley/Mods"
export MOD_ZIP="smapi/StardewNpcAgent/bin/Release/net6.0/StardewNpcAgent 0.1.0.zip"
test ! -e "$STARDEW_MODS_DIR/StardewNpcAgent"
unzip "$MOD_ZIP" -d "$STARDEW_MODS_DIR"
```

如果已经存在 `StardewNpcAgent` 目录，不要覆盖式解压。先在游戏目录外备份原有 `config.json` 与 `data/`，检查新包内容，再人工替换程序文件并保留所需配置和数据。

</details>

<details>
<summary>启用十二名 NPC 的基础请求链路</summary>

首次生成的 `config.json` 默认列出十二人，但 `EnableStaticDialogueSpike` 和 `EnableAgentDialogue` 默认都为 `false`。正式链路只开启 `EnableAgentDialogue`；两个模式不能同时为 `true`。

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

旧配置缺少 `TargetNpcIds` 时只沿用 Abigail、Sebastian 两人的兼容默认值；要覆盖十二人，请显式保存上面的固定 ID 列表。未知 ID 会被忽略，匹配不使用显示名或模糊搜索。

</details>

### 🤖 启用真正的对话增强

真正的增强必须显式选择 `agent` 模式，并提供 Provider、模型 ID 和 API key。OpenAI 与 OpenAI-compatible Provider 二选一；不要把真实 endpoint、model 或 key 提交到仓库。

<details>
<summary>OpenAI 配置</summary>

```bash
export STARDEW_NPC_AGENT_DIALOGUE_GENERATOR_MODE=agent
export STARDEW_NPC_AGENT_PROVIDER_ID=openai
export STARDEW_NPC_AGENT_PROVIDER_MODEL="<model>"
export STARDEW_NPC_AGENT_PROVIDER_WIRE_API=responses
unset STARDEW_NPC_AGENT_PROVIDER_BASE_URL
```

</details>

<details>
<summary>OpenAI-compatible 配置</summary>

```bash
export STARDEW_NPC_AGENT_DIALOGUE_GENERATOR_MODE=agent
export STARDEW_NPC_AGENT_PROVIDER_ID=openai_compatible
export STARDEW_NPC_AGENT_PROVIDER_MODEL="<model>"
export STARDEW_NPC_AGENT_PROVIDER_BASE_URL="https://provider.example/v1"
export STARDEW_NPC_AGENT_PROVIDER_WIRE_API=chat_completions
```

</details>

结束用于验证的 scripted 后端后，在 `backend/` 目录的同一个 shell 中设置 Provider 配置、隐藏输入 API key 并重新启动后端：

```bash
printf 'Provider API key: '
IFS= read -r -s STARDEW_NPC_AGENT_PROVIDER_API_KEY
printf '\n'
export STARDEW_NPC_AGENT_PROVIDER_API_KEY
uv run uvicorn stardew_npc_agent.main:app --host 127.0.0.1 --port 8000
```

仓库中的 `.env.example` 只说明字段，程序不会自动加载它。通用 `OPENAI_API_KEY` 也不会隐式启用 Agent。配置缺失或不合法时，后端会拒绝启用，而不会猜测你的意图。

## 🧠 它是怎么工作的

```text
今天发生的游戏事件
        ↓
保存在本地记忆中
        ↓
第二天开始时，Agent 读取相关记忆
        ↓
判断是否值得增强当天原台词
        ↓
安全检查通过后放入每日缓存
        ↓
玩家与 NPC 交谈
        ↓
显示增强台词，或安全回退到原版
```

新事件遵守 D+1，避免 NPC 在同一天“预知”刚发生的事。生成在当天开始阶段进行，玩家点击 NPC 时只读取每日缓存，不会在对话框打开时现场等待 Provider。

Agent 可以按需调用 NPC、玩家或世界记忆工具。候选台词随后经过确定性 Guard；只有可修复的文本问题才允许最多一次 Repair。检查、Repair、缓存或 NPC/日期/来源身份核对任一失败，都会回到原版台词。

## 🔐 隐私与费用

- 默认 `scripted` 模式不构造 Provider，也不产生模型费用。
- 显式启用 Agent 后，获准的原台词、风格示例、NPC 上下文和相关记忆证据可能发送给你配置的 Provider；请自行阅读并接受其数据与计费政策。
- 真实玩家名不会发送给 Provider。Prompt 只包含 typed player-name slot，通过检查后再由 Mod 在本地恢复名字。
- SMAPI 到 FastAPI 的连接限制为 loopback；FastAPI 到外部 Provider 的请求可能离开本机。
- API key 只应通过隐藏终端输入或本地 secrets manager 提供，不要写入 Mod `config.json`、仓库、日志、截图或命令示例。
- 不要提交存档、数据库、outbox、本地 extractor 输出、崩溃转储或其他含玩家信息的文件。

## 🛠️ 给开发者

- [`smapi/`](smapi/)：游戏事件、durable outbox、每日生成协调、缓存和原生对话展示；
- [`backend/`](backend/)：FastAPI、SQLite 记忆、受约束 Agent、三个只读工具、Guard 与一次 Repair；
- [`contracts/v1/`](contracts/v1/)：C# 与 Python 共用的 wire schema；
- [`tools/VanillaDialogueManifestExtractor/`](tools/VanillaDialogueManifestExtractor/)：从用户自己的合法游戏安装中本地提取来源 manifest，默认 dry-run。

开发、测试和安全边界统一记录在 [`AGENTS.md`](AGENTS.md)。运行验证时必须使用临时 SQLite、synthetic fixture 和 `-p:EnableModDeploy=false`，不要把测试指向 active Mod、存档或正式运行数据。

## 🚧 当前限制

- 只处理固定十二名原版可婚 NPC 的合格日常来源；不把运行时婚姻状态直接等同于真正 `MarriageDialogue` 支持。
- 不增强过场事件、节日、Green Rain、Krobus、第三方 NPC 或自由聊天。
- `$h/$a` 结构化表情增强和 `%endearment` 尚未实现。
- 多人模式没有当前版本一致性保证。
- 模型输出仍可能不理想；当前保障是 Guard、最多一次 Repair 与原版 fallback，而不是“模型永远正确”。
- 目前只有 macOS、Stardew Valley 1.6.15 与 SMAPI 4.5.2 的验证证据。

## 🤝 参与项目

提交修改前请先阅读 [`AGENTS.md`](AGENTS.md)。请保持变更范围小而可验证，不要削弱固定 NPC allowlist、D+1、只读工具、cache-only 点击路径、Guard/Repair/fallback 或共享 contracts。

欢迎通过 Issue 描述问题或使用场景。报告时请区分已经观察到的现象、合理推测和尚未运行的验证，并避免上传存档、日志、截图或其他可能含秘密与玩家信息的材料。

## 📜 许可证与第三方权利

仓库原创代码与文档按 [`LICENSE`](LICENSE) 中的 MIT License 提供。

Stardew Valley 的名称、商标、游戏资产、对话与截图仍归各自权利人所有；MIT License 只覆盖仓库作者有权许可的原创代码和文档，不重新许可任何第三方游戏材料。
