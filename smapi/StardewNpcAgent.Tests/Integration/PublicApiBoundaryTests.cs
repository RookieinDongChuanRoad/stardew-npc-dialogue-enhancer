namespace StardewNpcAgent.Tests.Integration;

/// <summary>
/// 以真实源码做架构边界检查，防止后续重构悄悄改为输入拦截、反射或私有注入。
/// </summary>
public sealed class PublicApiBoundaryTests
{
    /// <summary>
    /// Production C# 只能使用公开资产和 NPC API；这些禁用入口一旦出现就立即失败。
    /// </summary>
    [Fact]
    public void ProductionSource_DoesNotUseForbiddenInterceptionOrPrivateApi()
    {
        string sourceRoot = FindProjectDirectory("StardewNpcAgent");
        string[] productionSources = EnumerateRuntimeProductionSources(sourceRoot)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        string combinedSource = string.Join(
            "\n",
            productionSources.Select(File.ReadAllText));

        Assert.DoesNotContain("ButtonPressed", combinedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Events.Input", combinedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("resetCurrentDialogue", combinedSource, StringComparison.Ordinal);
        Assert.False(
            System.Text.RegularExpressions.Regex.IsMatch(
                combinedSource,
                @"\.CurrentDialogue\b(?!AlreadyLoaded)"),
            "production 源码不得读取会懒加载并消费候选选择时机的对话属性。");
        Assert.DoesNotContain("Helper.Reflection", combinedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Reflection", combinedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("new DialogueBox", combinedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Game1.DrawDialogue", combinedSource, StringComparison.Ordinal);

        string[] dialogueBoxUsers = EnumerateRuntimeProductionSources(sourceRoot)
            .Where(path => File.ReadAllText(path).Contains("DialogueBox", StringComparison.Ordinal))
            .Select(path => Path.GetFileName(path)
                ?? throw new InvalidOperationException($"无法从源码路径提取文件名：{path}"))
            .ToArray();
        Assert.Equal(new[] { "SaveSessionRuntime.cs" }, dialogueBoxUsers);
    }

    /// <summary>
    /// Harmony 例外严格收口到一个 accepted-gift 观察文件；其余生产代码和全部对话注入路径保持零容忍。
    /// </summary>
    [Fact]
    public void ProductionSource_AllowsHarmonyOnlyInSingleGiftObserver()
    {
        string sourceRoot = FindProjectDirectory("StardewNpcAgent");
        string[] harmonyUsers = EnumerateRuntimeProductionSources(sourceRoot)
            .Where(path => File.ReadAllText(path).Contains("HarmonyLib", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(sourceRoot, path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[] { Path.Combine("Integration", "GiftGivenHarmonyPatch.cs") },
            harmonyUsers);

        string patchSource = File.ReadAllText(Path.Combine(sourceRoot, harmonyUsers[0]));
        Assert.Equal(1, patchSource.Split("[HarmonyPostfix]", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("HarmonyPrefix", patchSource, StringComparison.Ordinal);
        Assert.DoesNotContain("HarmonyTranspiler", patchSource, StringComparison.Ordinal);

        foreach (string dialogueSource in Directory.EnumerateFiles(
            Path.Combine(sourceRoot, "Game"),
            "*Dialogue*.cs",
            SearchOption.AllDirectories))
        {
            Assert.DoesNotContain("Harmony", File.ReadAllText(dialogueSource), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// 只扫描 Phase 2 游戏适配代码；Phase 1 wire contract 的 required-field 校验合法使用反射。
    /// </summary>
    private static IEnumerable<string> EnumerateRuntimeProductionSources(string sourceRoot)
    {
        yield return Path.Combine(sourceRoot, "ModEntry.cs");

        foreach (string directoryName in new[]
                 {
                     "Application",
                     "Configuration",
                     "Game",
                     "Infrastructure",
                     "Integration",
                 })
        {
            string directoryPath = Path.Combine(sourceRoot, directoryName);
            foreach (string sourcePath in Directory.EnumerateFiles(directoryPath, "*.cs", SearchOption.AllDirectories))
            {
                yield return sourcePath;
            }
        }
    }

    /// <summary>
    /// Spike 的适配器必须保留经程序集验证的公开 API 锚点，并以 Late 资产编辑做最终 hash gate。
    /// </summary>
    [Fact]
    public void ProductionSource_ContainsApprovedPublicApiRoute()
    {
        string sourceRoot = FindProjectDirectory("StardewNpcAgent");
        string resolverSource = File.ReadAllText(Path.Combine(sourceRoot, "Game", "DialogueCandidateResolver.cs"));
        string runtimeSource = File.ReadAllText(Path.Combine(sourceRoot, "Application", "DialogueSpikeRuntime.cs"));
        string formalRuntimeSource = File.ReadAllText(
            Path.Combine(sourceRoot, "Integration", "SaveSessionRuntime.cs"));
        string bindingsSource = File.ReadAllText(
            Path.Combine(sourceRoot, "Integration", "SmapiEventBindings.cs"));

        Assert.Contains("Game1.npcDialogues", resolverSource, StringComparison.Ordinal);
        Assert.Contains("tryToRetrieveDialogue", resolverSource, StringComparison.Ordinal);
        Assert.Contains("Game1.isRaining", formalRuntimeSource, StringComparison.Ordinal);
        Assert.Contains("Game1.IsGreenRainingHere", resolverSource, StringComparison.Ordinal);
        Assert.Contains("AssetEditPriority.Late", runtimeSource, StringComparison.Ordinal);
        Assert.Contains("InvalidateCache", runtimeSource, StringComparison.Ordinal);
        Assert.Contains("resetSeasonalDialogue", runtimeSource, StringComparison.Ordinal);
        Assert.Contains("AssetEditPriority.Late", formalRuntimeSource, StringComparison.Ordinal);
        Assert.Contains("DialogueInjectionAdapter.Apply", formalRuntimeSource, StringComparison.Ordinal);
        Assert.Contains("MenuChanged", bindingsSource, StringComparison.Ordinal);
        Assert.Contains("RenderedActiveMenu", bindingsSource, StringComparison.Ordinal);
    }

    /// <summary>
    /// 中文等 locale 会把缓存键具体化为 ``asset.locale``；正式运行时必须按 base name
    /// 失效所有等价变体，不能只请求精确的无 locale key 后把 cache entry 误报为已激活。
    /// </summary>
    [Fact]
    public void SaveSessionRuntime_InvalidatesLocalizedDialogueVariantsByBaseName()
    {
        string sourceRoot = FindProjectDirectory("StardewNpcAgent");
        string runtimeSource = File.ReadAllText(
            Path.Combine(sourceRoot, "Integration", "SaveSessionRuntime.cs"));

        Assert.Contains("InvalidateDialogueAssetVariants", runtimeSource, StringComparison.Ordinal);
        Assert.Contains(
            "asset.Name.IsEquivalentTo(assetName, useBaseName: true)",
            runtimeSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "helper.GameContent.InvalidateCache(assetName);",
            runtimeSource,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// raw key 与文本必须先由纯规则解析并扫描，之后才允许构造公开 Dialogue 做一致性校验。
    /// </summary>
    [Fact]
    public void CandidateResolver_ScansRawSourceBeforePublicDialogueConstruction()
    {
        string sourceRoot = FindProjectDirectory("StardewNpcAgent");
        string resolverSource = File.ReadAllText(Path.Combine(sourceRoot, "Game", "DialogueCandidateResolver.cs"));

        int rawSelectionIndex = resolverSource.IndexOf("OrdinaryDialogueKeyResolver.Select", StringComparison.Ordinal);
        int scannerIndex = resolverSource.IndexOf("DialogueControlCommandScanner.Scan", StringComparison.Ordinal);
        int publicConstructionIndex = resolverSource.IndexOf("npc.tryToRetrieveDialogue(", StringComparison.Ordinal);

        Assert.True(rawSelectionIndex >= 0, "resolver 必须先使用纯 raw key 选择器。");
        Assert.True(scannerIndex > rawSelectionIndex, "raw source scanner 必须在纯 key 选择之后运行。");
        Assert.True(
            publicConstructionIndex > scannerIndex,
            "公开 API 构造 Dialogue 前必须已经证明 exact raw source 不含危险 DSL。");
    }

    /// <summary>
    /// 已加载候选必须由调用方显式选择；默认解析入口持续采用更保守的未加载模式。
    /// </summary>
    [Fact]
    public void CandidateResolver_LoadedModeIsExplicitAndDefaultRemainsUnloaded()
    {
        string sourceRoot = FindProjectDirectory("StardewNpcAgent");
        string resolverSource = File.ReadAllText(
            Path.Combine(sourceRoot, "Game", "DialogueCandidateResolver.cs"));

        Assert.Contains(
            "DialogueCandidateResolutionMode.AllowVerifiedLoadedStack",
            resolverSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "DialogueCandidateResolutionMode mode = DialogueCandidateResolutionMode.RequireUnloaded",
            resolverSource,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// authoritative loaded 方法必须只消费 actual snapshot 与 exact raw assets，不能调用 pure 日期链。
    /// </summary>
    [Fact]
    public void CandidateResolver_AuthoritativeMethodNeverCallsLegacyPureSelection()
    {
        string sourceRoot = FindProjectDirectory("StardewNpcAgent");
        string resolverSource = File.ReadAllText(
            Path.Combine(sourceRoot, "Game", "DialogueCandidateResolver.cs"));

        string method = ExtractMethodRegion(
            resolverSource,
            "internal DialogueCandidateResolution ResolveAuthoritativeLoaded(",
            "internal bool RevalidateAuthoritativeLoaded(");

        Assert.Contains("AuthoritativeDialogueSourceResolver.Resolve", method, StringComparison.Ordinal);
        Assert.Contains("sourceDialogueAsset", method, StringComparison.Ordinal);
        Assert.Contains("npcOrdinaryDialogueAsset", method, StringComparison.Ordinal);
        Assert.DoesNotContain("OrdinaryDialogueKeyResolver", method, StringComparison.Ordinal);
        Assert.DoesNotContain("tryToRetrieveDialogue", method, StringComparison.Ordinal);
    }

    /// <summary>
    /// runtime 必须先做顶层 route，再按同一 snapshot 完成 capture、resolve 与 bind；不能 pure-first。
    /// </summary>
    [Fact]
    public void SaveSessionRuntime_RoutesLoadedSourceBeforeAnyCandidateResolution()
    {
        string sourceRoot = FindProjectDirectory("StardewNpcAgent");
        string runtimeSource = File.ReadAllText(
            Path.Combine(sourceRoot, "Integration", "SaveSessionRuntime.cs"));
        string method = ExtractMethodRegion(
            runtimeSource,
            "private IReadOnlyList<PreparedDialogueCandidate> PrepareDayStartedCandidates(",
            "private IReadOnlyList<DialogueCandidate> ResolvePreparedCandidatesForActivation(");

        int routeIndex = method.IndexOf("DialogueCandidateRoutePolicy.Select", StringComparison.Ordinal);
        int snapshotIndex = method.IndexOf(
            "StardewLoadedDialogueStackAdapter.CaptureSourceSnapshot",
            StringComparison.Ordinal);
        int authoritativeIndex = method.IndexOf(
            "candidateResolver.ResolveAuthoritativeLoaded",
            StringComparison.Ordinal);
        int bindIndex = method.IndexOf(
            "StardewLoadedDialogueStackAdapter.BindCandidate",
            StringComparison.Ordinal);
        int legacyIndex = method.IndexOf("candidateResolver.Resolve(", StringComparison.Ordinal);

        Assert.True(routeIndex >= 0, "runtime 必须实际调用纯 route policy。");
        Assert.True(snapshotIndex > routeIndex, "loaded snapshot 必须发生在 route 决策之后。");
        Assert.True(authoritativeIndex > snapshotIndex, "loaded resolver 必须消费已捕获 snapshot。");
        Assert.True(bindIndex > authoritativeIndex, "candidate 必须绑定回同一初始 snapshot token。");
        Assert.True(legacyIndex > routeIndex, "legacy pure resolver 只能位于 route 之后的兼容分支。");
    }

    /// <summary>
    /// Provider 返回后的 loaded 候选只允许 exact source revalidation，不能重新运行 pure selector/RNG。
    /// </summary>
    [Fact]
    public void SaveSessionRuntime_LoadedRevalidationDoesNotRerunLegacyResolver()
    {
        string sourceRoot = FindProjectDirectory("StardewNpcAgent");
        string runtimeSource = File.ReadAllText(
            Path.Combine(sourceRoot, "Integration", "SaveSessionRuntime.cs"));
        string method = ExtractMethodRegion(
            runtimeSource,
            "private IReadOnlyList<DialogueCandidate> ResolvePreparedCandidatesForActivation(",
            "private static bool HasSameRawCandidateIdentity(");

        Assert.Contains("candidateResolver.RevalidateAuthoritativeLoaded", method, StringComparison.Ordinal);
        Assert.Contains("preparedCandidate.LoadedTarget is not null", method, StringComparison.Ordinal);
    }

    /// <summary>
    /// 已加载栈适配器必须只走批准的公开字段路线，并把唯一写操作限制为 DialogueLine 文本 CAS。
    /// </summary>
    [Fact]
    public void LoadedStackAdapter_UsesOnlyApprovedPublicObjectsAndTextWrite()
    {
        string sourceRoot = FindProjectDirectory("StardewNpcAgent");
        string adapterSource = File.ReadAllText(
            Path.Combine(sourceRoot, "Integration", "StardewLoadedDialogueStackAdapter.cs"));

        Assert.Contains("Game1.npcDialogues", adapterSource, StringComparison.Ordinal);
        Assert.Contains("DialogueLine.Text", adapterSource, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals", adapterSource, StringComparison.Ordinal);
        Assert.Contains("TryReplaceText", adapterSource, StringComparison.Ordinal);
        Assert.Contains("DialogueSourceClassifier.ClassifyTranslationKey", adapterSource, StringComparison.Ordinal);

        Assert.False(
            System.Text.RegularExpressions.Regex.IsMatch(
                adapterSource,
                @"\.CurrentDialogue\b(?!AlreadyLoaded)"),
            "适配器不得读取会触发原版 lazy load 的当前对话属性。");
        Assert.DoesNotContain("resetCurrentDialogue", adapterSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Harmony", adapterSource, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Reflection", adapterSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Helper.Reflection", adapterSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ButtonPressed", adapterSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Events.Input", adapterSource, StringComparison.Ordinal);
        Assert.DoesNotContain("new Dialogue(", adapterSource, StringComparison.Ordinal);
        Assert.DoesNotContain("new DialogueBox", adapterSource, StringComparison.Ordinal);
    }

    /// <summary>
    /// Save session 必须显式编排 loaded preparation、直接应用、生命周期释放与首帧完成清理。
    /// </summary>
    [Fact]
    public void SaveSessionRuntime_WiresTheCompleteLoadedDialogueLifecycle()
    {
        string sourceRoot = FindProjectDirectory("StardewNpcAgent");
        string runtimeSource = File.ReadAllText(
            Path.Combine(sourceRoot, "Integration", "SaveSessionRuntime.cs"));

        Assert.Contains("DayStartedGenerationPreparation", runtimeSource, StringComparison.Ordinal);
        Assert.Contains("StardewLoadedDialogueStackAdapter.CaptureSourceSnapshot", runtimeSource, StringComparison.Ordinal);
        Assert.Contains("StardewLoadedDialogueStackAdapter.BindCandidate", runtimeSource, StringComparison.Ordinal);
        Assert.Contains(
            "DialogueCandidateRoutePolicy.Select",
            runtimeSource,
            StringComparison.Ordinal);
        Assert.Contains("LoadedDialogueStackCoordinator.Apply", runtimeSource, StringComparison.Ordinal);
        Assert.Contains("IsTrackedDirectKey", runtimeSource, StringComparison.Ordinal);
        Assert.Contains("ReleaseUndisplayed", runtimeSource, StringComparison.Ordinal);
        Assert.Contains("MarkDisplayed", runtimeSource, StringComparison.Ordinal);
    }

    /// <summary>
    /// 构建配置必须持续禁止自动部署，但只为已批准的 gift observer 启用游戏自带 Harmony。
    /// </summary>
    [Fact]
    public void ProjectFile_KeepsDeploymentDisabledAndEnablesApprovedHarmonyReference()
    {
        string sourceRoot = FindProjectDirectory("StardewNpcAgent");
        string projectFile = File.ReadAllText(Path.Combine(sourceRoot, "StardewNpcAgent.csproj"));

        Assert.Contains("<EnableModDeploy>false</EnableModDeploy>", projectFile, StringComparison.Ordinal);
        Assert.Contains("<EnableHarmony>true</EnableHarmony>", projectFile, StringComparison.Ordinal);
    }

    /// <summary>
    /// 只有真正的新配置可以调用会生成文件的 ReadConfig；已有 object 必须使用纯读 API，且异常不得回退。
    /// </summary>
    [Fact]
    public void ModEntry_UsesGeneratingReadOnlyForNewConfiguration()
    {
        string sourceRoot = FindProjectDirectory("StardewNpcAgent");
        string source = File.ReadAllText(Path.Combine(sourceRoot, "ModEntry.cs"));
        string readCoordinatorSource = File.ReadAllText(
            Path.Combine(sourceRoot, "Configuration", "ModConfigReadCoordinator.cs"));
        string startupOrchestratorSource = File.ReadAllText(
            Path.Combine(sourceRoot, "Application", "ModStartupOrchestrator.cs"));

        Assert.Equal(
            1,
            source.Split("helper.ReadConfig<ModConfig>()", StringSplitOptions.None).Length - 1);
        Assert.Equal(
            1,
            source.Split(
                "helper.Data.ReadJsonFile<ModConfig>(\"config.json\")",
                StringSplitOptions.None).Length - 1);
        Assert.Contains(
            "fieldState == TargetNpcIdsFieldState.NewConfiguration",
            readCoordinatorSource,
            StringComparison.Ordinal);
        Assert.Contains("ModStartupOrchestrator.Execute", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ModConfigReadCoordinator.Read", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ModBootstrapOrchestrator.Execute", source, StringComparison.Ordinal);
        Assert.Contains(
            "ModConfigReadCoordinator.Read",
            startupOrchestratorSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "ModBootstrapOrchestrator.Execute",
            startupOrchestratorSource,
            StringComparison.Ordinal);
        Assert.Contains("ModConfigFileInspector.IsSnapshotCurrent", source, StringComparison.Ordinal);
        Assert.Contains(
            "readResult.EffectiveFieldState",
            startupOrchestratorSource,
            StringComparison.Ordinal);
        Assert.Contains("inspectCurrentConfiguration", source, StringComparison.Ordinal);
        Assert.Contains(
            "finalSnapshot.FieldState != TargetNpcIdsFieldState.ExplicitValue",
            readCoordinatorSource,
            StringComparison.Ordinal);
        Assert.Contains("CONFIGURATION_INSPECTION_FAILED", source, StringComparison.Ordinal);
        Assert.Contains("CONFIGURATION_READ_FAILED", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// registry 不得读取 CanBeRomanced 动态扩容；启动诊断只能报告 supported/enabled 与 warning code/count。
    /// </summary>
    [Fact]
    public void RegistryAndStartupDiagnostics_DoNotExposeDynamicOrRawConfiguredNpcIds()
    {
        string sourceRoot = FindProjectDirectory("StardewNpcAgent");
        string registrySource = File.ReadAllText(
            Path.Combine(sourceRoot, "Game", "VanillaMarriageableNpcRegistry.cs"));
        string entrySource = File.ReadAllText(Path.Combine(sourceRoot, "ModEntry.cs"));
        string spikeSource = File.ReadAllText(
            Path.Combine(sourceRoot, "Application", "DialogueSpikeRuntime.cs"));
        string saveSource = File.ReadAllText(
            Path.Combine(sourceRoot, "Integration", "SaveSessionRuntime.cs"));

        Assert.DoesNotContain(".CanBeRomanced", registrySource, StringComparison.Ordinal);
        Assert.DoesNotContain("using StardewValley", registrySource, StringComparison.Ordinal);
        Assert.Contains("supported_count=", entrySource, StringComparison.Ordinal);
        Assert.Contains("enabled_count=", entrySource, StringComparison.Ordinal);
        Assert.Contains("code={warningCode}", entrySource, StringComparison.Ordinal);
        Assert.Contains("count={warningCount}", entrySource, StringComparison.Ordinal);
        Assert.DoesNotContain("config.TargetNpcIds", entrySource, StringComparison.Ordinal);
        Assert.DoesNotContain("FormatTargetNpcIds", spikeSource, StringComparison.Ordinal);
        Assert.DoesNotContain("FormatTargetNpcIds", saveSource, StringComparison.Ordinal);
    }

    /// <summary>
    /// 从测试输出目录向上定位 solution，再返回同级 production 项目，避免依赖调用者 cwd。
    /// </summary>
    private static string FindProjectDirectory(string projectName)
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "StardewNpcAgent.sln")))
        {
            current = current.Parent;
        }

        Assert.NotNull(current);
        string projectDirectory = Path.Combine(current!.FullName, projectName);
        Assert.True(Directory.Exists(projectDirectory), $"未找到 production 项目目录：{projectDirectory}");
        return projectDirectory;
    }

    /// <summary>
    /// 按相邻方法签名截取源码区域，使边界断言只审查目标方法而不是整个文件中的 legacy fallback。
    /// </summary>
    private static string ExtractMethodRegion(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        int end = source.IndexOf(endMarker, start + Math.Max(startMarker.Length, 1), StringComparison.Ordinal);
        Assert.True(start >= 0, $"未找到方法起点：{startMarker}");
        Assert.True(end > start, $"未找到方法终点：{endMarker}");
        return source.Substring(start, end - start);
    }
}
