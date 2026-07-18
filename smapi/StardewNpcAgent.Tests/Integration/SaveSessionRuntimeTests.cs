using StardewNpcAgent.Application;
using StardewNpcAgent.Configuration;
using StardewNpcAgent.Game;
using StardewNpcAgent.Infrastructure.Storage;
using StardewNpcAgent.Integration;

namespace StardewNpcAgent.Tests.Integration;

/// <summary>
/// 以纯 session token、completion queue 与 staging activation 验证后台任务不会污染游戏主线程状态。
/// </summary>
public sealed class SaveSessionRuntimeTests
{
    /// <summary>
    /// 两个 runtime 必须保留旧 config 构造函数，并新增显式接收 resolved enabled IDs 的不同参数个数重载。
    /// </summary>
    [Fact]
    public void RuntimeConstructors_PreserveLegacyApiAndAddResolvedNpcIdOverloads()
    {
        string sourceRoot = FindProjectDirectory();
        string spikeSource = File.ReadAllText(
            Path.Combine(sourceRoot, "Application", "DialogueSpikeRuntime.cs"));
        string saveSource = File.ReadAllText(
            Path.Combine(sourceRoot, "Integration", "SaveSessionRuntime.cs"));

        Assert.Equal(
            2,
            spikeSource.Split("public DialogueSpikeRuntime(", StringSplitOptions.None).Length - 1);
        Assert.Contains(
            "public DialogueSpikeRuntime(IModHelper helper, IMonitor monitor, ModConfig config)",
            spikeSource,
            StringComparison.Ordinal);
        Assert.Contains("IReadOnlyList<string> enabledNpcIds", spikeSource, StringComparison.Ordinal);

        Assert.Equal(
            2,
            saveSource.Split("public SaveSessionRuntime(", StringSplitOptions.None).Length - 1);
        Assert.Contains("ModConfig config,", saveSource, StringComparison.Ordinal);
        Assert.Contains(
            "ValidatedAgentConfiguration agentConfiguration)",
            saveSource,
            StringComparison.Ordinal);
        Assert.Contains("IReadOnlyList<string> enabledNpcIds", saveSource, StringComparison.Ordinal);
    }

    /// <summary>
    /// 新构造函数必须复制 policy 结果；调用方随后修改原 List 时不能改变任一 runtime 的目标集合。
    /// </summary>
    [Fact]
    public void RuntimeResolvedNpcIdOverloads_DefensivelyCopyCallerList()
    {
        string sourceRoot = FindProjectDirectory();
        string spikeSource = File.ReadAllText(
            Path.Combine(sourceRoot, "Application", "DialogueSpikeRuntime.cs"));
        string saveSource = File.ReadAllText(
            Path.Combine(sourceRoot, "Integration", "SaveSessionRuntime.cs"));

        Assert.Contains("IReadOnlyList<string> enabledNpcIds", spikeSource, StringComparison.Ordinal);
        Assert.Contains("IReadOnlyList<string> enabledNpcIds", saveSource, StringComparison.Ordinal);
        Assert.Contains(
            "EnabledNpcIdsSnapshot.Create(enabledNpcIds)",
            spikeSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "EnabledNpcIdsSnapshot.Create(enabledNpcIds)",
            saveSource,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// existing config 在 ReadJsonFile 前后任一次指纹不一致都必须 fail closed，且绝不调用新配置 fallback。
    /// </summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void ModConfigReadCoordinator_ExistingSnapshotMismatchNeverFallsBackToCreate(
        bool currentBeforeRead,
        bool currentAfterRead)
    {
        ModConfigFileInspection snapshot = new(
            TargetNpcIdsFieldState.ExplicitValue,
            ContentFingerprint: "snapshot-fingerprint");
        Queue<bool> currentChecks = new(new[] { currentBeforeRead, currentAfterRead });
        int existingReadCount = 0;
        int newCreateCount = 0;

        ModConfigReadResult result = ModConfigReadCoordinator.Read(
            snapshot,
            isSnapshotCurrent: _ => currentChecks.Dequeue(),
            createNewConfiguration: () =>
            {
                newCreateCount++;
            },
            inspectCurrentSnapshot: () =>
                throw new InvalidOperationException("existing 分支不应重新选择 snapshot。"),
            readExistingConfiguration: () =>
            {
                existingReadCount++;
                return new ModConfig { TargetNpcIds = new List<string?>() };
            });

        Assert.Equal(ModConfigReadStatus.SnapshotChanged, result.Status);
        Assert.Null(result.Config);
        Assert.Equal(currentBeforeRead ? 1 : 0, existingReadCount);
        Assert.Equal(0, newCreateCount);
    }

    /// <summary>
    /// existing 纯读抛错或返回 null 时必须稳定失败，不能调用会生成/写回配置的 create delegate。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ModConfigReadCoordinator_ExistingReadFailureNeverFallsBackToCreate(bool throwDuringRead)
    {
        ModConfigFileInspection snapshot = new(
            TargetNpcIdsFieldState.ExplicitValue,
            ContentFingerprint: "snapshot-fingerprint");
        int newCreateCount = 0;

        ModConfigReadResult result = ModConfigReadCoordinator.Read(
            snapshot,
            isSnapshotCurrent: _ => true,
            createNewConfiguration: () =>
            {
                newCreateCount++;
            },
            inspectCurrentSnapshot: () =>
                throw new InvalidOperationException("existing 分支不应重新选择 snapshot。"),
            readExistingConfiguration: () => throwDuringRead
                ? throw new IOException("模拟 existing config 读取失败。")
                : null);

        Assert.Equal(ModConfigReadStatus.ReadFailed, result.Status);
        Assert.Null(result.Config);
        Assert.Equal(0, newCreateCount);
    }

    /// <summary>
    /// NewConfiguration 在调用生成路径前必须重新确认仍不存在；检查后出现文件时保持零读取/零写回。
    /// </summary>
    [Fact]
    public void ModConfigReadCoordinator_NewSnapshotChangedBeforeCreate_FailsClosed()
    {
        ModConfigFileInspection snapshot = new(
            TargetNpcIdsFieldState.NewConfiguration,
            ContentFingerprint: null);
        int newCreateCount = 0;
        int existingReadCount = 0;

        ModConfigReadResult result = ModConfigReadCoordinator.Read(
            snapshot,
            isSnapshotCurrent: _ => false,
            createNewConfiguration: () =>
            {
                newCreateCount++;
            },
            inspectCurrentSnapshot: () =>
                throw new InvalidOperationException("生成前 snapshot 已变化，不应继续 Inspect。"),
            readExistingConfiguration: () =>
            {
                existingReadCount++;
                return new ModConfig();
            });

        Assert.Equal(ModConfigReadStatus.SnapshotChanged, result.Status);
        Assert.Equal(0, newCreateCount);
        Assert.Equal(0, existingReadCount);
    }

    /// <summary>
    /// ReadConfig 只负责正常首次生成；返回值不可信，最终配置必须从新 snapshot 的 existing pure reader 取得。
    /// </summary>
    [Fact]
    public void ModConfigReadCoordinator_NewConfigurationUsesStableFinalSnapshotAndPureRead()
    {
        ModConfigFileInspection initial = new(
            TargetNpcIdsFieldState.NewConfiguration,
            ContentFingerprint: null);
        ModConfigFileInspection final = new(
            TargetNpcIdsFieldState.ExplicitValue,
            ContentFingerprint: "generated-fingerprint");
        List<TargetNpcIdsFieldState> verifiedStates = new();
        int createCount = 0;
        int inspectCount = 0;
        int existingReadCount = 0;
        ModConfig stableConfig = new()
        {
            TargetNpcIds = new List<string?> { "Leah" },
        };

        ModConfigReadResult result = ModConfigReadCoordinator.Read(
            initial,
            isSnapshotCurrent: snapshot =>
            {
                verifiedStates.Add(snapshot.FieldState);
                return true;
            },
            createNewConfiguration: () => createCount++,
            inspectCurrentSnapshot: () =>
            {
                inspectCount++;
                return final;
            },
            readExistingConfiguration: () =>
            {
                existingReadCount++;
                return stableConfig;
            });

        Assert.Equal(ModConfigReadStatus.Success, result.Status);
        Assert.Same(stableConfig, result.Config);
        Assert.Equal(TargetNpcIdsFieldState.ExplicitValue, result.EffectiveFieldState);
        Assert.Equal(1, createCount);
        Assert.Equal(1, inspectCount);
        Assert.Equal(1, existingReadCount);
        Assert.Equal(
            new[]
            {
                TargetNpcIdsFieldState.NewConfiguration,
                TargetNpcIdsFieldState.ExplicitValue,
                TargetNpcIdsFieldState.ExplicitValue,
            },
            verifiedStates);
    }

    /// <summary>
    /// 首次生成后的 missing/null/invalid/仍缺失都不是正常默认文件，不能直接把 ReadConfig 返回值交给 policy。
    /// </summary>
    [Theory]
    [InlineData(TargetNpcIdsFieldState.MissingFromExistingConfiguration)]
    [InlineData(TargetNpcIdsFieldState.ExplicitNull)]
    [InlineData(TargetNpcIdsFieldState.InvalidConfiguration)]
    [InlineData(TargetNpcIdsFieldState.NewConfiguration)]
    public void ModConfigReadCoordinator_NewConfigurationRejectsUnexpectedFinalState(
        TargetNpcIdsFieldState finalState)
    {
        ModConfigFileInspection initial = new(
            TargetNpcIdsFieldState.NewConfiguration,
            ContentFingerprint: null);
        int existingReadCount = 0;

        ModConfigReadResult result = ModConfigReadCoordinator.Read(
            initial,
            isSnapshotCurrent: _ => true,
            createNewConfiguration: () => { },
            inspectCurrentSnapshot: () => new ModConfigFileInspection(
                finalState,
                finalState == TargetNpcIdsFieldState.NewConfiguration ? null : "final"),
            readExistingConfiguration: () =>
            {
                existingReadCount++;
                return new ModConfig();
            });

        Assert.Equal(ModConfigReadStatus.GeneratedConfigurationInvalid, result.Status);
        Assert.Null(result.Config);
        Assert.Null(result.EffectiveFieldState);
        Assert.Equal(0, existingReadCount);
    }

    /// <summary>
    /// 最终 ExplicitValue snapshot 的 pure read 前后也必须稳定；任一次变化都丢弃读取结果。
    /// </summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void ModConfigReadCoordinator_NewFinalSnapshotMismatchFailsClosed(
        bool currentBeforePureRead,
        bool currentAfterPureRead)
    {
        ModConfigFileInspection initial = new(
            TargetNpcIdsFieldState.NewConfiguration,
            ContentFingerprint: null);
        ModConfigFileInspection final = new(
            TargetNpcIdsFieldState.ExplicitValue,
            ContentFingerprint: "generated-fingerprint");
        Queue<bool> finalChecks = new(new[] { currentBeforePureRead, currentAfterPureRead });
        int existingReadCount = 0;

        ModConfigReadResult result = ModConfigReadCoordinator.Read(
            initial,
            isSnapshotCurrent: snapshot => snapshot.FieldState == TargetNpcIdsFieldState.NewConfiguration
                || finalChecks.Dequeue(),
            createNewConfiguration: () => { },
            inspectCurrentSnapshot: () => final,
            readExistingConfiguration: () =>
            {
                existingReadCount++;
                return new ModConfig();
            });

        Assert.Equal(ModConfigReadStatus.SnapshotChanged, result.Status);
        Assert.Null(result.Config);
        Assert.Null(result.EffectiveFieldState);
        Assert.Equal(currentBeforePureRead ? 1 : 0, existingReadCount);
    }

    /// <summary>
    /// invalid inspection 不得调用任何 reader；合法 existing snapshot 必须只调用 existing reader 并前后核对。
    /// </summary>
    [Theory]
    [InlineData(TargetNpcIdsFieldState.InvalidConfiguration, false, 0)]
    [InlineData(TargetNpcIdsFieldState.ExplicitValue, true, 1)]
    public void ModConfigReadCoordinator_UsesOnlyReaderAllowedByInspection(
        TargetNpcIdsFieldState fieldState,
        bool expectedSuccess,
        int expectedExistingReads)
    {
        ModConfigFileInspection snapshot = new(
            fieldState,
            fieldState == TargetNpcIdsFieldState.InvalidConfiguration ? "invalid" : "stable");
        int existingReadCount = 0;
        int newCreateCount = 0;

        ModConfigReadResult result = ModConfigReadCoordinator.Read(
            snapshot,
            isSnapshotCurrent: _ => true,
            createNewConfiguration: () =>
            {
                newCreateCount++;
            },
            inspectCurrentSnapshot: () =>
                throw new InvalidOperationException("existing 分支不应重新选择 snapshot。"),
            readExistingConfiguration: () =>
            {
                existingReadCount++;
                return new ModConfig { TargetNpcIds = new List<string?>() };
            });

        Assert.Equal(
            expectedSuccess ? ModConfigReadStatus.Success : ModConfigReadStatus.InvalidInspection,
            result.Status);
        Assert.Equal(expectedExistingReads, existingReadCount);
        Assert.Equal(0, newCreateCount);
        Assert.Equal(
            expectedSuccess ? TargetNpcIdsFieldState.ExplicitValue : null,
            result.EffectiveFieldState);
    }

    /// <summary>
    /// 合法零 enabled 必须真实执行 Resolve→Validate→report 后停止，任何 runtime/Harmony/Initialize delegate 均不调用。
    /// </summary>
    [Theory]
    [InlineData(TargetNpcIdsFieldState.ExplicitNull)]
    [InlineData(TargetNpcIdsFieldState.ExplicitValue)]
    public void ModBootstrapOrchestrator_ZeroEnabledStopsBeforeAllRuntimeSideEffects(
        TargetNpcIdsFieldState fieldState)
    {
        List<string> calls = new();
        ModConfig config = new()
        {
            EnableAgentDialogue = true,
            TargetNpcIds = fieldState == TargetNpcIdsFieldState.ExplicitNull
                ? null
                : new List<string?>(),
        };

        ModBootstrapResult result = ModBootstrapOrchestrator.Execute(
            config,
            fieldState,
            CreateBootstrapOperations(calls));

        Assert.Equal(ModBootstrapStatus.ZeroEnabledNpcIds, result.Status);
        Assert.Equal(new[] { "resolve", "validate", "report" }, calls);
    }

    /// <summary>
    /// invalid compatibility 与通用 validation 失败都必须在 report/runtime 之前停止，且状态码彼此不同。
    /// </summary>
    [Fact]
    public void ModBootstrapOrchestrator_InvalidStagesStopAtExactBoundary()
    {
        List<string> invalidInspectionCalls = new();
        ModBootstrapResult invalidInspection = ModBootstrapOrchestrator.Execute(
            new ModConfig { EnableAgentDialogue = true },
            TargetNpcIdsFieldState.InvalidConfiguration,
            CreateBootstrapOperations(invalidInspectionCalls));

        List<string> invalidValidationCalls = new();
        ModBootstrapResult invalidValidation = ModBootstrapOrchestrator.Execute(
            new ModConfig
            {
                EnableStaticDialogueSpike = true,
                EnableAgentDialogue = true,
                TargetNpcIds = new List<string?> { "Abigail" },
            },
            TargetNpcIdsFieldState.ExplicitValue,
            CreateBootstrapOperations(invalidValidationCalls));

        Assert.Equal(ModBootstrapStatus.CompatibilityInvalid, invalidInspection.Status);
        Assert.Equal(new[] { "resolve" }, invalidInspectionCalls);
        Assert.Equal(ModBootstrapStatus.ValidationInvalid, invalidValidation.Status);
        Assert.Equal(new[] { "resolve", "validate" }, invalidValidationCalls);
    }

    /// <summary>
    /// 正式模式必须严格执行 Resolve→Validate→report→runtime constructor→Harmony→Initialize。
    /// </summary>
    [Fact]
    public void ModBootstrapOrchestrator_AgentModeExecutesFrozenSideEffectOrder()
    {
        List<string> calls = new();
        ModConfig config = new()
        {
            EnableAgentDialogue = true,
            TargetNpcIds = new List<string?> { "Abigail" },
        };

        ModBootstrapResult result = ModBootstrapOrchestrator.Execute(
            config,
            TargetNpcIdsFieldState.ExplicitValue,
            CreateBootstrapOperations(calls));

        Assert.Equal(ModBootstrapStatus.AgentRuntimeInitialized, result.Status);
        Assert.Equal(
            new[]
            {
                "resolve",
                "validate",
                "report",
                "construct_agent",
                "install_harmony",
                "initialize_agent",
            },
            calls);
    }

    /// <summary>
    /// 静态模式只允许构造并 Initialize 静态 runtime；默认关闭模式完成纯步骤后不得调用任一 runtime delegate。
    /// </summary>
    [Fact]
    public void ModBootstrapOrchestrator_StaticAndDisabledModesStayInsideOwnSideEffectBoundary()
    {
        List<string> staticCalls = new();
        ModBootstrapResult staticResult = ModBootstrapOrchestrator.Execute(
            new ModConfig
            {
                EnableStaticDialogueSpike = true,
                TargetNpcIds = new List<string?> { "Abigail" },
            },
            TargetNpcIdsFieldState.ExplicitValue,
            CreateBootstrapOperations(staticCalls));

        List<string> disabledCalls = new();
        ModBootstrapResult disabledResult = ModBootstrapOrchestrator.Execute(
            new ModConfig { TargetNpcIds = new List<string?> { "Abigail" } },
            TargetNpcIdsFieldState.ExplicitValue,
            CreateBootstrapOperations(disabledCalls));

        Assert.Equal(ModBootstrapStatus.StaticRuntimeInitialized, staticResult.Status);
        Assert.Equal(
            new[] { "resolve", "validate", "report", "construct_static", "initialize_static" },
            staticCalls);
        Assert.Equal(ModBootstrapStatus.DisabledModes, disabledResult.Status);
        Assert.Equal(new[] { "resolve", "validate", "report" }, disabledCalls);
    }

    /// <summary>
    /// 构造使用真实 policy/validator 的 operation 集；其余 delegate 只计数，不复制 production 分支逻辑。
    /// </summary>
    private static ModBootstrapOperations CreateBootstrapOperations(ICollection<string> calls)
    {
        return new ModBootstrapOperations(
            resolveEnabledNpcIds: (config, fieldState) =>
            {
                calls.Add("resolve");
                return ModConfigCompatibilityPolicy.ResolveEnabledNpcIds(config, fieldState);
            },
            validateConfig: config =>
            {
                calls.Add("validate");
                return ModConfigValidator.Validate(config);
            },
            reportResolvedConfiguration: _ => calls.Add("report"),
            constructStaticRuntime: _ => calls.Add("construct_static"),
            initializeStaticRuntime: () => calls.Add("initialize_static"),
            constructAgentRuntime: (_, _) => calls.Add("construct_agent"),
            installAgentHarmony: () => calls.Add("install_harmony"),
            initializeAgentRuntime: () => calls.Add("initialize_agent"));
    }

    /// <summary>
    /// 正式 bindings 必须覆盖所有 producer 所需的公开 SMAPI 生命周期，不允许靠每帧轮询替代事件。
    /// </summary>
    [Fact]
    public void SmapiEventBindings_WiresAllProducerLifecycleEvents()
    {
        string sourceRoot = FindProjectDirectory();
        string bindings = File.ReadAllText(
            Path.Combine(sourceRoot, "Integration", "SmapiEventBindings.cs"));

        foreach (string requiredBinding in new[]
                 {
                     "GameLoop.Saved += OnSaved",
                     "GameLoop.OneSecondUpdateTicked += OnOneSecondUpdateTicked",
                     "Player.Warped += OnWarped",
                     "Player.InventoryChanged += OnInventoryChanged",
                     "runtime.HandleSaved()",
                     "runtime.HandleOneSecondUpdateTicked()",
                     "runtime.HandleWarped(eventArgs)",
                     "runtime.HandleInventoryChanged(eventArgs)",
                 })
        {
            Assert.Contains(requiredBinding, bindings, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// DayEnding 必须先完成差分与 facility staging，再调度网络；Saved 才能提交设施事件。
    /// </summary>
    [Fact]
    public void SaveSessionRuntime_OrdersProducerReconciliationBeforeFlushAndCommitsFacilitiesOnlyOnSaved()
    {
        string source = File.ReadAllText(
            Path.Combine(FindProjectDirectory(), "Integration", "SaveSessionRuntime.cs"));
        int dayEndingStart = source.IndexOf("internal void HandleDayEnding()", StringComparison.Ordinal);
        int reconcileIndex = source.IndexOf("ReconcilePlayerAndNpcProducers", dayEndingStart, StringComparison.Ordinal);
        int stageIndex = source.IndexOf("StagePublicFacilities", dayEndingStart, StringComparison.Ordinal);
        int flushIndex = source.IndexOf("ScheduleEventFlush", dayEndingStart, StringComparison.Ordinal);
        int savedStart = source.IndexOf("internal void HandleSaved()", StringComparison.Ordinal);

        Assert.True(dayEndingStart >= 0);
        Assert.True(savedStart >= 0);
        int commitIndex = source.IndexOf("CommitSaved", savedStart, StringComparison.Ordinal);
        Assert.True(reconcileIndex > dayEndingStart);
        Assert.True(stageIndex > reconcileIndex);
        Assert.True(flushIndex > stageIndex);
        Assert.True(commitIndex > savedStart);
        Assert.DoesNotContain(
            "CommitSaved",
            source.Substring(dayEndingStart, savedStart - dayEndingStart),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// 返回标题只清 ephemeral producer 状态，不能取消或终止已经开始的后台 HTTP 工作。
    /// </summary>
    [Fact]
    public void SaveSessionRuntime_ReturnedToTitleClearsProducersWithoutCancellation()
    {
        string source = File.ReadAllText(
            Path.Combine(FindProjectDirectory(), "Integration", "SaveSessionRuntime.cs"));
        int methodStart = source.IndexOf("internal void HandleReturnedToTitle()", StringComparison.Ordinal);
        int nextMethod = source.IndexOf("internal void HandleLocaleChanged()", methodStart, StringComparison.Ordinal);
        string method = source.Substring(methodStart, nextMethod - methodStart);

        Assert.Contains("producerSessionState?.Clear()", method, StringComparison.Ordinal);
        Assert.Contains("playerProgressionSessionState?.Clear()", method, StringComparison.Ordinal);
        Assert.Contains("worldProgressionSessionState?.DiscardStaged()", method, StringComparison.Ordinal);
        Assert.DoesNotContain("Cancel", method, StringComparison.Ordinal);
        Assert.DoesNotContain("Dispose", method, StringComparison.Ordinal);
    }

    /// <summary>
    /// 已审计 gift patch 只在正式 Agent 模式安装，并把 immutable fact 交给 save runtime。
    /// </summary>
    [Fact]
    public void ModEntry_InstallsGiftObserverOnlyForFormalAgentRuntime()
    {
        string source = File.ReadAllText(Path.Combine(FindProjectDirectory(), "ModEntry.cs"));
        string bootstrapSource = File.ReadAllText(
            Path.Combine(FindProjectDirectory(), "Application", "ModBootstrapOrchestrator.cs"));
        int agentBranch = bootstrapSource.IndexOf(
            "if (config.EnableAgentDialogue)",
            StringComparison.Ordinal);
        int construct = bootstrapSource.IndexOf(
            "operations.ConstructAgentRuntime",
            agentBranch,
            StringComparison.Ordinal);
        int install = bootstrapSource.IndexOf(
            "operations.InstallAgentHarmony",
            agentBranch,
            StringComparison.Ordinal);
        int initialize = bootstrapSource.IndexOf(
            "operations.InitializeAgentRuntime",
            agentBranch,
            StringComparison.Ordinal);

        Assert.True(agentBranch >= 0);
        Assert.True(construct > agentBranch);
        Assert.True(install > construct);
        Assert.True(initialize > install);
        Assert.Contains("installAgentHarmony: InstallGiftObserver", source, StringComparison.Ordinal);
        Assert.Contains("runtime.HandleGiftGiven", source, StringComparison.Ordinal);
        Assert.Contains("typeof(StardewValley.Game1).Assembly.Location", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// 正式模式的候选拒绝日志必须同时保留阶段原因与确定性资格原因，且只包含稳定枚举；
    /// 否则 live 验收看到 cached=0 时无法区分婚姻、天气、特殊事件或内容形状回退。
    /// </summary>
    [Fact]
    public void CandidateResolutionDiagnostics_ReportsStableStageAndEligibilityReasons()
    {
        DialogueCandidateResolution sourceRejection = new(
            IsResolved: false,
            DialogueCandidateResolutionReasonCode.EligibilityRejected,
            DialogueEligibilityReasonCode.UnsupportedDailySource,
            Candidate: null);
        DialogueCandidateResolution styleRejection = new(
            IsResolved: false,
            DialogueCandidateResolutionReasonCode.InsufficientStyleExamples,
            EligibilityReasonCode: null,
            Candidate: null);

        string sourceLog = CandidateResolutionDiagnostics.Format(
            "Abigail",
            sourceRejection);
        string styleLog = CandidateResolutionDiagnostics.Format(
            "Sebastian",
            styleRejection);

        Assert.Equal(
            "正式候选解析 npc=Abigail, result=EligibilityRejected, eligibility=UnsupportedDailySource。",
            sourceLog);
        Assert.Equal(
            "正式候选解析 npc=Sebastian, result=InsufficientStyleExamples, eligibility=n/a。",
            styleLog);
    }

    /// <summary>
    /// 后台 continuation 只能排队；只有 UpdateTicked 对应的主线程 Drain 才执行副作用。
    /// </summary>
    [Fact]
    public void MainThreadCompletionQueue_DefersWorkUntilDrainAndRunsEachActionOnce()
    {
        MainThreadCompletionQueue queue = new();
        List<int> observed = new();
        queue.Enqueue(() => observed.Add(1));
        queue.Enqueue(() => observed.Add(2));

        Assert.Empty(observed);
        Assert.Equal(2, queue.PendingCount);

        int drained = queue.Drain();
        int drainedAgain = queue.Drain();

        Assert.Equal(2, drained);
        Assert.Equal(0, drainedAgain);
        Assert.Equal(new[] { 1, 2 }, observed);
        Assert.Equal(0, queue.PendingCount);
    }

    /// <summary>
    /// 正式 runtime 必须把同一个已校验 generation timeout 交给 cohort coordinator，
    /// 不能为两个 HTTP batch 各自串行创建一轮完整等待预算。
    /// </summary>
    [Fact]
    public void SaveSessionRuntime_UsesBatchCoordinatorWithValidatedSharedDeadline()
    {
        string source = File.ReadAllText(
            Path.Combine(FindProjectDirectory(), "Integration", "SaveSessionRuntime.cs"));

        Assert.Contains(
            "generationRequestDeadline = agentConfiguration.GenerationRequestTimeout;",
            source,
            StringComparison.Ordinal);
        Assert.Contains("DailyGenerationBatchCoordinator coordinator = new(", source, StringComparison.Ordinal);
        Assert.Contains("generationRequestDeadline);", source, StringComparison.Ordinal);
        Assert.Contains(
            "DailyGenerationBatchCoordinator coordinator)",
            source,
            StringComparison.Ordinal);
        Assert.Contains("batches={result.BatchCount}", source, StringComparison.Ordinal);
        Assert.Contains("request_ids={string.Join", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "DailyGenerationCoordinator coordinator = new(",
            source,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Save A 的 token 在切换 Save B、locale invalidation 或返回标题后都必须失效。
    /// </summary>
    [Fact]
    public void SessionGenerationGate_RejectsLateResultsAcrossSaveDayLocaleAndInvalidation()
    {
        SessionGenerationGate gate = new();
        gate.StartSession("save-a", "player-a");
        GenerationSessionToken saveAToken = gate.CaptureDay(14, "zh-CN");

        Assert.True(gate.IsCurrent(saveAToken, 14, "zh-CN"));
        Assert.False(gate.IsCurrent(saveAToken, 15, "zh-CN"));
        Assert.False(gate.IsCurrent(saveAToken, 14, "en"));

        gate.InvalidatePendingWork();
        Assert.False(gate.IsCurrent(saveAToken, 14, "zh-CN"));

        GenerationSessionToken refreshed = gate.CaptureDay(14, "zh-CN");
        gate.StartSession("save-b", "player-b");
        Assert.False(gate.IsCurrent(refreshed, 14, "zh-CN"));

        GenerationSessionToken saveBToken = gate.CaptureDay(20, "en");
        gate.EndSession();
        Assert.False(gate.IsCurrent(saveBToken, 20, "en"));
    }

    /// <summary>
    /// 两 NPC staging 结果只有在当前重新解析候选的 asset/key/hash 仍一致时才提交到 live cache。
    /// </summary>
    [Fact]
    public void StagedDialogueActivator_ActivatesTwoValidNpcsAndDropsChangedOrMissingCandidate()
    {
        SessionGenerationGate gate = new();
        gate.StartSession("save-a", "player-a");
        GenerationSessionToken token = gate.CaptureDay(14, "zh-CN");
        DialogueCandidate abigail = CreateCandidate("Abigail", "原文 A。", "hash-a");
        DialogueCandidate sebastian = CreateCandidate("Sebastian", "原文 S。", "hash-s");
        DailyDialogueCache staging = new();
        staging.Store(CreateGeneratedEntry(token, abigail, "增强 A。"));
        staging.Store(CreateGeneratedEntry(token, sebastian, "增强 S。"));
        DailyDialogueCache live = new();

        DialogueActivationResult both = StagedDialogueActivator.Activate(
            gate,
            token,
            currentDayIndex: 14,
            currentLocale: "zh-CN",
            currentCandidates: new[] { abigail, sebastian },
            stagingCache: staging,
            liveCache: live);

        Assert.Equal(2, both.ActivatedEntries.Count);
        Assert.Equal(2, live.Snapshot().Count);

        live.Clear();
        DialogueCandidate changedAbigail = abigail with { SourceHash = "changed-hash" };
        DialogueActivationResult partial = StagedDialogueActivator.Activate(
            gate,
            token,
            14,
            "zh-CN",
            new[] { changedAbigail },
            staging,
            live);

        Assert.Empty(partial.ActivatedEntries);
        Assert.Empty(live.Snapshot());
    }

    /// <summary>
    /// staging 提交必须逐项比较 family 与 raw text，不能只依赖复合 key 和 hash。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StagedDialogueActivator_DropsFamilyOrRawTextDrift(bool changeFamily)
    {
        SessionGenerationGate gate = new();
        gate.StartSession("save-a", "player-a");
        GenerationSessionToken token = gate.CaptureDay(14, "zh-CN");
        DialogueCandidate candidate = CreateCandidate("Abigail", "原文。", "hash");
        DailyDialogueCache staging = new();
        staging.Store(CreateGeneratedEntry(token, candidate, "增强。"));
        DialogueCandidate drifted = changeFamily
            ? candidate with { SourceFamily = DialogueSourceFamily.RainyDaily }
            : candidate with { SourceText = "另一段原文。" };
        DailyDialogueCache live = new();

        DialogueActivationResult result = StagedDialogueActivator.Activate(
            gate,
            token,
            14,
            "zh-CN",
            new[] { drifted },
            staging,
            live);

        Assert.Empty(result.ActivatedEntries);
        Assert.Empty(live.Snapshot());
    }

    /// <summary>
    /// session 已失效时不得清理或写入现有 live cache；迟到任务必须是真正 no-op。
    /// </summary>
    [Fact]
    public void StagedDialogueActivator_ExpiredSessionDoesNotMutateLiveCache()
    {
        SessionGenerationGate gate = new();
        gate.StartSession("save-a", "player-a");
        GenerationSessionToken expired = gate.CaptureDay(14, "zh-CN");
        DialogueCandidate candidate = CreateCandidate("Abigail", "原文。", "hash");
        DailyDialogueCache staging = new();
        staging.Store(CreateGeneratedEntry(expired, candidate, "增强。"));
        DailyDialogueCache live = new();
        live.Store(CreateGeneratedEntry(expired, candidate, "已有结果。"));
        gate.InvalidatePendingWork();

        DialogueActivationResult result = StagedDialogueActivator.Activate(
            gate,
            expired,
            14,
            "zh-CN",
            new[] { candidate },
            staging,
            live);

        Assert.Empty(result.ActivatedEntries);
        Assert.Single(live.Snapshot());
        Assert.Equal("已有结果。", live.Snapshot()[0].EnhancedText);
    }

    private static DialogueCandidate CreateCandidate(
        string npcId,
        string sourceText,
        string sourceHash)
    {
        return new DialogueCandidate(
            npcId,
            DialogueSourceFamily.OrdinaryDaily,
            "zh-CN",
            $"Characters/Dialogue/{npcId}",
            "fall_Mon",
            sourceText,
            sourceHash,
            new[] { "样本一。", "样本二。", "样本三。" });
    }

    private static DailyDialogueCacheEntry CreateGeneratedEntry(
        GenerationSessionToken token,
        DialogueCandidate candidate,
        string enhancedText)
    {
        return new DailyDialogueCacheEntry(
            new DailyDialogueCacheKey(
                token.GameDayIndex,
                token.Locale,
                candidate.NpcId,
                candidate.AssetName,
                candidate.DialogueKey),
            candidate.SourceFamily,
            candidate.SourceText,
            candidate.SourceHash,
            enhancedText,
            GenerationId: $"generation-{candidate.NpcId}",
            GenerationKey: $"key-{candidate.NpcId}",
            TraceId: $"trace-{candidate.NpcId}");
    }

    private static string FindProjectDirectory()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "StardewNpcAgent.sln")))
        {
            current = current.Parent;
        }

        Assert.NotNull(current);
        return Path.Combine(current!.FullName, "StardewNpcAgent");
    }
}
