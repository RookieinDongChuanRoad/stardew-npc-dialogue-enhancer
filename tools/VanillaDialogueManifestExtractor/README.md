# VanillaDialogueManifestExtractor

该工具只读用户本机的 Stardew Valley 对话 XNB，并复用 Mod 的 source classifier、typed
`@` template policy 和 style selector 来生成本地来源 manifest。它不会启动游戏/SMAPI/后端，
不会调用 Provider，也不会修改游戏、Content、Mods 或存档。

默认是 dry-run：完成全部 NPC/locale 检查后只打印条目数与 manifest hash，不写文件。

先在自己的 shell 中定义以下可移植路径；不要把真实安装路径或生成结果放进仓库：

```bash
export STARDEW_GAME_ROOT="/path/to/Stardew Valley"
export STARDEW_CONTENT_ROOT="$STARDEW_GAME_ROOT/Contents/Resources/Content"
export LOCAL_MANIFEST_OUTPUT="$HOME/.local/share/stardew-npc-dialogue-enhancer/vanilla-dialogue-source-manifest.json"
```

默认 dry-run 不写文件：

```bash
dotnet run --project tools/VanillaDialogueManifestExtractor/VanillaDialogueManifestExtractor.csproj \
  -p:StardewGamePath="$STARDEW_GAME_ROOT" -c Release -- \
  --game-content-root "$STARDEW_CONTENT_ROOT" --locale en --locale zh-CN
```

只有人工确认 dry-run 成功后，用户才可自行创建上述输出路径的外部目录，再显式传入
`--output` 写入本地 manifest。写入使用目标目录内临时文件后原子替换，生成产物默认不提交：

```bash
dotnet run --project tools/VanillaDialogueManifestExtractor/VanillaDialogueManifestExtractor.csproj \
  -p:StardewGamePath="$STARDEW_GAME_ROOT" -c Release -- \
  --game-content-root "$STARDEW_CONTENT_ROOT" --locale en --locale zh-CN \
  --output "$LOCAL_MANIFEST_OUTPUT"
```

manifest 是本地资产 provenance，不是 eligibility allowlist：ordinary 必须通过生产 source/template
规则；exact rainy 即使含生产规则会拒绝的特殊 DSL 也会原样记录，供 evaluator 验证 fallback，绝不据此
放宽运行时 policy。任一 NPC/locale 缺少安全 ordinary、exact rainy 或两条关系门槛安全 style examples 时，
整次命令以非零退出且不生成部分输出。manifest 只保存 Content-relative POSIX XNB path；绝对安装路径
仅属于本机 CLI 输入。
