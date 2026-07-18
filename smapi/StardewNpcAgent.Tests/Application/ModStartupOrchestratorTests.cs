using StardewNpcAgent.Application;
using StardewNpcAgent.Configuration;

namespace StardewNpcAgent.Tests.Application;

/// <summary>
/// 以生产实际使用的顶层 orchestrator 冻结配置读取到 runtime 初始化的完整顺序。
/// </summary>
public sealed class ModStartupOrchestratorTests
{
    /// <summary>
    /// Existing Agent 配置必须连续执行 Inspect → pure read 前后复核 → Resolve → Validate → runtime → Harmony
    /// → Initialize；顶层 orchestrator 不得在两个已测试的子协调器之间插入额外副作用或丢失字段状态。
    /// </summary>
    [Fact]
    public void Execute_ExistingAgentConfiguration_PreservesCompleteOrderAndEffectiveFieldState()
    {
        List<string> calls = new();
        ModConfigFileInspection inspection = new(
            TargetNpcIdsFieldState.ExplicitValue,
            ContentFingerprint: "stable-fingerprint");
        ModConfig config = new()
        {
            EnableAgentDialogue = true,
            TargetNpcIds = new List<string?> { "Abigail" },
        };
        ModStartupOperations operations = new(
            inspectInitialConfiguration: () =>
            {
                calls.Add("inspect_initial");
                return inspection;
            },
            isSnapshotCurrent: snapshot =>
            {
                calls.Add($"check:{snapshot.FieldState}");
                return true;
            },
            createNewConfiguration: () => calls.Add("create_new"),
            inspectCurrentConfiguration: () =>
                throw new InvalidOperationException("existing 分支不应重新选择 snapshot。"),
            readExistingConfiguration: () =>
            {
                calls.Add("read_existing");
                return config;
            },
            createBootstrapOperations: _ =>
            {
                calls.Add("create_bootstrap_operations");
                return CreateBootstrapOperations(calls);
            });

        ModStartupResult result = ModStartupOrchestrator.Execute(operations);

        Assert.Equal(ModStartupStatus.AgentRuntimeInitialized, result.Status);
        Assert.Equal(TargetNpcIdsFieldState.ExplicitValue, result.ReadResult.EffectiveFieldState);
        Assert.Equal(
            new[]
            {
                "inspect_initial",
                "check:ExplicitValue",
                "read_existing",
                "check:ExplicitValue",
                "create_bootstrap_operations",
                "resolve:ExplicitValue",
                "validate",
                "report",
                "construct_agent",
                "install_harmony",
                "initialize_agent",
            },
            calls);
    }

    /// <summary>
    /// NewConfiguration 必须先触发生成，再以 final ExplicitValue snapshot 纯读；空数组随后在 zero gate 停止。
    /// </summary>
    [Fact]
    public void Execute_NewEmptyConfiguration_UsesFinalFieldStateAndStopsBeforeRuntime()
    {
        List<string> calls = new();
        ModConfigFileInspection initial = new(
            TargetNpcIdsFieldState.NewConfiguration,
            ContentFingerprint: null);
        ModConfigFileInspection final = new(
            TargetNpcIdsFieldState.ExplicitValue,
            ContentFingerprint: "generated-fingerprint");
        ModConfig config = new()
        {
            EnableAgentDialogue = true,
            TargetNpcIds = new List<string?>(),
        };
        ModStartupOperations operations = new(
            inspectInitialConfiguration: () =>
            {
                calls.Add("inspect_initial");
                return initial;
            },
            isSnapshotCurrent: snapshot =>
            {
                calls.Add($"check:{snapshot.FieldState}");
                return true;
            },
            createNewConfiguration: () => calls.Add("create_new"),
            inspectCurrentConfiguration: () =>
            {
                calls.Add("inspect_final");
                return final;
            },
            readExistingConfiguration: () =>
            {
                calls.Add("read_existing");
                return config;
            },
            createBootstrapOperations: _ =>
            {
                calls.Add("create_bootstrap_operations");
                return CreateBootstrapOperations(calls);
            });

        ModStartupResult result = ModStartupOrchestrator.Execute(operations);

        Assert.Equal(ModStartupStatus.ZeroEnabledNpcIds, result.Status);
        Assert.Equal(TargetNpcIdsFieldState.ExplicitValue, result.ReadResult.EffectiveFieldState);
        Assert.Equal(
            new[]
            {
                "inspect_initial",
                "check:NewConfiguration",
                "create_new",
                "inspect_final",
                "check:ExplicitValue",
                "read_existing",
                "check:ExplicitValue",
                "create_bootstrap_operations",
                "resolve:ExplicitValue",
                "validate",
                "report",
            },
            calls);
        Assert.DoesNotContain(calls, call => call.Contains("runtime", StringComparison.Ordinal));
        Assert.DoesNotContain("install_harmony", calls);
        Assert.DoesNotContain("initialize_agent", calls);
    }

    /// <summary>
    /// Invalid inspection 必须在 snapshot check/read/bootstrap 之前停止，不能触发任何运行时副作用。
    /// </summary>
    [Fact]
    public void Execute_InvalidInspection_StopsBeforeAllReadersAndBootstrap()
    {
        List<string> calls = new();
        ModStartupOperations operations = new(
            inspectInitialConfiguration: () =>
            {
                calls.Add("inspect_initial");
                return new ModConfigFileInspection(
                    TargetNpcIdsFieldState.InvalidConfiguration,
                    ContentFingerprint: null);
            },
            isSnapshotCurrent: _ =>
            {
                calls.Add("check_snapshot");
                return true;
            },
            createNewConfiguration: () => calls.Add("create_new"),
            inspectCurrentConfiguration: () =>
            {
                calls.Add("inspect_final");
                throw new InvalidOperationException();
            },
            readExistingConfiguration: () =>
            {
                calls.Add("read_existing");
                return new ModConfig();
            },
            createBootstrapOperations: _ =>
            {
                calls.Add("create_bootstrap_operations");
                return CreateBootstrapOperations(calls);
            });

        ModStartupResult result = ModStartupOrchestrator.Execute(operations);

        Assert.Equal(ModStartupStatus.InvalidInspection, result.Status);
        Assert.Equal(new[] { "inspect_initial" }, calls);
        Assert.Null(result.BootstrapResult);
    }

    /// <summary>
    /// Existing pure read 抛错时必须在 bootstrap factory 之前返回，runtime/Harmony/Initialize 均不可见。
    /// </summary>
    [Fact]
    public void Execute_ExistingReadFailure_StopsBeforeBootstrapAndRuntime()
    {
        List<string> calls = new();
        ModConfigFileInspection inspection = new(
            TargetNpcIdsFieldState.ExplicitValue,
            ContentFingerprint: "stable-fingerprint");
        ModStartupOperations operations = new(
            inspectInitialConfiguration: () =>
            {
                calls.Add("inspect_initial");
                return inspection;
            },
            isSnapshotCurrent: _ =>
            {
                calls.Add("check_snapshot");
                return true;
            },
            createNewConfiguration: () => calls.Add("create_new"),
            inspectCurrentConfiguration: () =>
                throw new InvalidOperationException("existing 分支不应重新 Inspect。"),
            readExistingConfiguration: () =>
            {
                calls.Add("read_existing");
                throw new IOException("模拟 pure read 失败。");
            },
            createBootstrapOperations: _ =>
            {
                calls.Add("create_bootstrap_operations");
                return CreateBootstrapOperations(calls);
            });

        ModStartupResult result = ModStartupOrchestrator.Execute(operations);

        Assert.Equal(ModStartupStatus.ReadFailed, result.Status);
        Assert.Equal(new[] { "inspect_initial", "check_snapshot", "read_existing" }, calls);
        Assert.Null(result.BootstrapResult);
    }

    /// <summary>
    /// ModEntry 必须只装配 low-level delegates 并调用顶层 seam，不能继续直接串联两个子协调器。
    /// </summary>
    [Fact]
    public void ModEntry_UsesTopLevelStartupOrchestratorAsOnlyCompositionBoundary()
    {
        string sourceRoot = FindProjectDirectory();
        string entrySource = File.ReadAllText(Path.Combine(sourceRoot, "ModEntry.cs"));

        Assert.Contains("new ModStartupOperations", entrySource, StringComparison.Ordinal);
        Assert.Contains("ModStartupOrchestrator.Execute", entrySource, StringComparison.Ordinal);
        Assert.DoesNotContain("ModConfigReadCoordinator.Read", entrySource, StringComparison.Ordinal);
        Assert.DoesNotContain("ModBootstrapOrchestrator.Execute", entrySource, StringComparison.Ordinal);
    }

    /// <summary>
    /// 使用真实 compatibility policy 与 validator；副作用 delegate 只记录调用，不复制 production 分支逻辑。
    /// </summary>
    private static ModBootstrapOperations CreateBootstrapOperations(ICollection<string> calls)
    {
        return new ModBootstrapOperations(
            resolveEnabledNpcIds: (config, fieldState) =>
            {
                calls.Add($"resolve:{fieldState}");
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
    /// 从测试输出定位 production 项目，避免依赖调用者 cwd。
    /// </summary>
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
