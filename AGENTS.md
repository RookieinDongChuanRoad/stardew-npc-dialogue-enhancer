## 适用范围与只读启动

本文件适用于仓库根目录及所有子目录，供公开维护者和自动化 agent 执行代码阅读、修改、验证与审查。进入新会话后先确认真实仓库、分支和变更边界，不要从 README、历史记录或印象猜测当前状态。

先运行以下只读命令：

```bash
git rev-parse --show-toplevel
git status --short
git branch --show-current
git log -1 --oneline
rg --files backend smapi contracts tools
```

随后只对任务涉及的目录做 scoped `rg`。先识别现有未提交变更并保留其所有权；未经授权不得整理、覆盖、暂存或撤销与当前任务无关的文件。

## 仓库地图

- `backend/`：Python 3.11 FastAPI、SQLite/Memory、受约束 Agent、三个只读工具、Guard/Repair、eval 与 Python 测试。
- `smapi/`：.NET 6 SMAPI Mod、配置与兼容策略、事件 outbox、每日生成、cache-only 点击路径、原生对话展示与 C# 测试。
- `contracts/v1/`：C# 与 Python 共享的 wire schema；任何字段或数值边界变更必须两端一致。
- `contracts/fixtures/`：共享 wire fixtures 和公开 synthetic fixture；不要把 synthetic 数据描述成真实 XNB 证据。
- `tools/VanillaDialogueManifestExtractor/`：用户本机真实游戏资产的只读 extractor，默认 dry-run。
- `tools/VanillaDialogueManifestExtractor.Tests/`：extractor 的 synthetic/in-memory 测试；公开门禁不得依赖真实对话正文。
- `README.md`：面向使用者的公开入口；`LICENSE`：仓库作者有权许可的原创代码与文档许可证。

## 事实来源优先级

发生冲突时按以下顺序裁决，并在结论中说明实际核对了什么：

1. 当前用户/系统指令与路径上最近的 `AGENTS.md`。
2. live code、当前配置模型与真实 tree。
3. schema、migration 与测试所表达的可执行合同。
4. 本轮 fresh 命令输出。
5. 根 `README.md`。
6. 历史资料。

README 或历史资料与实现不一致时，不得为迁就文档而歪曲当前行为。先定位入口、调用方、数据合同、失败路径和测试，再决定修代码还是修文档；对未验证内容明确标为推断或 Not run。

## 不可破坏的不变量

- 保持 Mod `UniqueID` 为 `Liurongfu.StardewNpcAgent`；它是安装、存档关联和包身份的稳定合同。
- `contracts/v1` 与共享 fixtures 必须保持 C#/Python 兼容。修改 wire shape 时同步 schema、DTO、raw JSON 校验和两端 contract tests。
- 新事实在 D 日不可用于生成，只能从 D+1 起在正确 NPC、领域和权限边界内可见；不得以“更及时”为由改成同日读取。
- `get_npc_history`、`get_player_progression`、`get_world_progression` 对模型公开的业务参数必须保持为零。NPC、`cutoff_day_index` 和权限由可信 runtime 注入，工具只读。
- `scripted` 是零 Provider、零费用默认值；正常结果是 passthrough。通用 `OPENAI_*` key 不得隐式启用 Agent。
- 玩家点击路径只读 daily cache，不发 Provider 请求、不现场生成。cache miss 返回原版。
- NPC、日期、来源或 generation identity mismatch 必须 fallback，不能把候选展示给错误 NPC。
- 所有改写必须经过确定性 Guard；仅可修复问题最多一次 Repair，之后仍失败就回退原版。不得引入无界重试。
- C# build/test 始终显式传入 `-p:EnableModDeploy=false`；普通验证只能写仓库内 `bin/obj` 等构建产物，不能部署 active Mod。
- 固定支持集是十二名原版可婚 NPC，不从动态可婚属性、显示名、第三方 NPC 或模糊匹配扩张。
- 配置解析、Provider 配置和外部失败均 fail closed；不得用猜测、静默默认或宽松解析越过授权边界。

## 可移植命令

Python full gate，从仓库根目录执行：

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

Alembic 验证必须注入临时 SQLite 路径，不能使用默认运行库：

```bash
TEMP_DB_DIR="$(mktemp -d /tmp/stardew-agent-alembic.XXXXXX)"
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

C# full gate，从仓库根目录执行。先根据本机安装设置路径；当前只验证过 macOS Steam 布局：

```bash
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

Extractor synthetic gate 只选择 in-memory 测试，不读取真实台词正文：

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

这些门禁不调用收费 Provider、不启动游戏、不部署 Mod。`uv sync` 与 `dotnet` 会生成本地依赖或构建产物；先确认工作区和磁盘边界，再按任务授权运行。

## 变更工作流

1. 用失败测试固定需求：先写或确认 RED，且失败原因必须指向预期行为，而不是环境或夹具错误。
2. 用最小、可维护改动达到 GREEN；保持函数职责单一，注释解释边界与“为什么”。
3. 运行与改动直接相关的 focused regression，覆盖成功、fallback、异常与身份边界。
4. 在 Task 边界做一次集中审查：公共接口、contracts、D+1、授权、包内容、文档状态和未授权副作用。
5. 在最后一次业务改动之后运行 fresh full gate。任何后续业务修改都会使先前 full gate 失效，必须重跑。

修改已有代码时，在交付中说明改了什么、为什么、影响了哪些入口和合同。优先保持外部 API 与文件格式兼容；确需破坏性变更时，先获得明确授权并提供迁移与回滚边界。

## 审查严重度

- **Critical**：秘密泄露、数据破坏、身份合同破坏、候选展示给错误 NPC。发现即阻塞提交或发布。
- **Important**：外部 API/共享合同回归、fallback 失效、D+1 破坏、越过用户授权、发布包回归。修复并完成 focused 与 full gate 后才能继续。
- **Minor**：局部命名、注释、文档清晰度或非阻塞维护性问题。应记录清楚，但不得反复制造没有新证据的无限审核循环。

严重度必须对应可复现影响、合同或风险；不要用风格偏好冒充阻塞项。多个审查者结论冲突时，由主 agent 回到 live code/tests 和用户授权仲裁。

## 运行与数据安全

未经用户明确授权，不得：

- 运行会产生费用或真实外部请求的 Provider 调用；
- 对正式数据库执行 migration，或把测试指向默认正式 SQLite；
- 写入 active Mod 目录、游戏存档、真实 outbox、用户本地 manifest 或游戏 Content；
- 启动游戏、SMAPI、正式后端或 Mod 部署；
- 中断、终止、杀死、取消或放弃任何已启动的程序、测试、批处理、训练、推理或数据任务。

若运行任务仍在进行，应继续等待并报告状态；只有在发现明显数据破坏、资源失控或计费风险时，才先向用户说明风险与可选动作，并等待确认。测试数据使用临时目录、synthetic fixture 和 fake/scripted 路径；秘密不得进入命令历史、Mod 配置、仓库、日志、异常文本或截图。

## Git 与 worktree 纪律

- 功能工作优先使用隔离 worktree；开始前核对 top-level、当前分支、共同 Git 目录和 dirty state。
- 保留用户与其他任务的已有变更。不同 agent 不得无约束地同时修改同一区域。
- 只精确暂存当前任务文件，例如 `git add README.md AGENTS.md`；提交前运行 `git diff --cached --check` 并审阅 cached diff。
- 不使用 `git add -A`，避免把无关文件带入提交。
- 不使用 `git reset --hard` 或 `git clean`；也不得用等效破坏性命令绕过这条边界。
- 不修改、重写或强推用户未授权的历史。remote、发布和 PR 都是单独的外部状态变更，需要对应授权。
- 提交后重新运行 `git status --short`，明确报告是否只剩预期状态。

## 文档状态词

- **Implemented**：当前代码路径已经存在；不等同于通过测试或实机观察。
- **Automated**：当前自动化测试或静态门禁覆盖相应合同；必须能指出 fresh 命令。
- **Observed**：在明确环境和授权范围内观察到真实行为；不得由单元测试推断。
- **Deferred**：已知工作被明确延后；当前版本不能暗示其可用。
- **Waiver**：用户明确接受缺少某项证据或验证的风险；记录范围，不扩张解释。
- **Out of scope**：当前版本边界外的能力；不得以 future intent 写成现状。
- **Not run**：本轮没有执行相应检查；不能用历史数量或旧报告替代。

状态必须逐项标注。不要用“支持”“完成”“安全”等宽泛词把不同证据层混成一个结论；必要时并列多个状态并补充环境边界。

## Subagent 协作

只在任务能自然拆成独立输入、输出和验收标准时使用最小必要 subagent。适合拆分架构阅读、兼容性审查、测试验证或文档核对；强耦合改动由单一 owner 负责，避免重叠写入。

派发时明确文件范围、禁止事项、外部接口、输出语言、验证命令和不得中断运行任务的红线。subagent 只能提交证据和范围内改动；主 agent 负责交叉核对、冲突仲裁、术语统一、精确 staging，并在所有结果整合后亲自运行 fresh full gate。

## 完成证据

只有同时具备以下证据，才可声明任务完成：

- `git status --short`、任务 scoped diff 与 staged diff 证明文件范围准确，没有夹带用户变更；
- RED 原因、GREEN 结果和 focused regression 与需求直接对应；纯文档任务至少完成 link、路径、禁词与 `git diff --check`；
- 最后一次业务改动后的 Python、临时 Alembic、C# 和适用 extractor fresh full gate 全部成功，或明确标为 Not run 并说明授权/环境边界；
- 公共接口、UniqueID、共享 contracts、D+1、只读工具、fallback、包内容与部署开关经过集中审查；
- 没有收费 Provider、正式数据库、active Mod、存档、outbox 或进程控制等未授权副作用；
- 最终报告明确区分已确认事实、合理推断、Waiver、Out of scope 与剩余 Minor。

测试失败时报告具体命令、首个根因和影响范围，不把“已运行”写成“已通过”。任何无法 fresh 复现的历史数字都不能作为完成证据。
