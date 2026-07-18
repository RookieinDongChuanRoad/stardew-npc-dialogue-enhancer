using StardewNpcAgent.Configuration;

namespace StardewNpcAgent.Application;

/// <summary>
/// 从初始配置检查到 runtime 初始化的顶层稳定终态。
/// </summary>
internal enum ModStartupStatus
{
    InspectionFailed,
    InvalidInspection,
    SnapshotChanged,
    GeneratedConfigurationInvalid,
    ReadFailed,
    CompatibilityInvalid,
    ValidationInvalid,
    ZeroEnabledNpcIds,
    DisabledModes,
    StaticRuntimeInitialized,
    AgentRuntimeInitialized,
}

/// <summary>
/// 顶层启动结果；保留实际 read/bootstrap 子结果，供 ModEntry 记录稳定且不含原文的诊断。
/// </summary>
internal sealed record ModStartupResult(
    ModStartupStatus Status,
    ModConfigReadResult ReadResult,
    ModBootstrapResult? BootstrapResult);

/// <summary>
/// 顶层 orchestrator 所需的最低层只读配置操作与 bootstrap factory。
/// </summary>
internal sealed class ModStartupOperations
{
    /// <summary>
    /// 创建完整操作集合；所有 delegate 都是必需边界，测试可记录真实生产顺序而无需伪造 IModHelper。
    /// </summary>
    internal ModStartupOperations(
        Func<ModConfigFileInspection> inspectInitialConfiguration,
        Func<ModConfigFileInspection, bool> isSnapshotCurrent,
        Action createNewConfiguration,
        Func<ModConfigFileInspection> inspectCurrentConfiguration,
        Func<ModConfig?> readExistingConfiguration,
        Func<ModConfig, ModBootstrapOperations> createBootstrapOperations)
    {
        InspectInitialConfiguration = inspectInitialConfiguration
            ?? throw new ArgumentNullException(nameof(inspectInitialConfiguration));
        IsSnapshotCurrent = isSnapshotCurrent
            ?? throw new ArgumentNullException(nameof(isSnapshotCurrent));
        CreateNewConfiguration = createNewConfiguration
            ?? throw new ArgumentNullException(nameof(createNewConfiguration));
        InspectCurrentConfiguration = inspectCurrentConfiguration
            ?? throw new ArgumentNullException(nameof(inspectCurrentConfiguration));
        ReadExistingConfiguration = readExistingConfiguration
            ?? throw new ArgumentNullException(nameof(readExistingConfiguration));
        CreateBootstrapOperations = createBootstrapOperations
            ?? throw new ArgumentNullException(nameof(createBootstrapOperations));
    }

    internal Func<ModConfigFileInspection> InspectInitialConfiguration { get; }

    internal Func<ModConfigFileInspection, bool> IsSnapshotCurrent { get; }

    internal Action CreateNewConfiguration { get; }

    internal Func<ModConfigFileInspection> InspectCurrentConfiguration { get; }

    internal Func<ModConfig?> ReadExistingConfiguration { get; }

    internal Func<ModConfig, ModBootstrapOperations> CreateBootstrapOperations { get; }
}

/// <summary>
/// 组合生产实际使用的 config-read 与 bootstrap 状态机，消除二者之间未经执行测试覆盖的接线空隙。
/// </summary>
internal static class ModStartupOrchestrator
{
    /// <summary>
    /// 执行 Inspect → Read/Create → Resolve → Validate → zero gate → runtime → Harmony → Initialize。
    /// </summary>
    internal static ModStartupResult Execute(ModStartupOperations operations)
    {
        ArgumentNullException.ThrowIfNull(operations);

        ModConfigFileInspection inspection;
        try
        {
            inspection = operations.InspectInitialConfiguration();
        }
        catch (Exception)
        {
            return new ModStartupResult(
                ModStartupStatus.InspectionFailed,
                new ModConfigReadResult(
                    ModConfigReadStatus.ReadFailed,
                    Config: null,
                    EffectiveFieldState: null),
                BootstrapResult: null);
        }

        ModConfigReadResult readResult = ModConfigReadCoordinator.Read(
            inspection,
            operations.IsSnapshotCurrent,
            operations.CreateNewConfiguration,
            operations.InspectCurrentConfiguration,
            operations.ReadExistingConfiguration);
        if (readResult.Status != ModConfigReadStatus.Success
            || readResult.Config is null
            || readResult.EffectiveFieldState is null)
        {
            return new ModStartupResult(
                MapReadStatus(readResult.Status),
                readResult,
                BootstrapResult: null);
        }

        ModBootstrapOperations bootstrapOperations =
            operations.CreateBootstrapOperations(readResult.Config);
        ModBootstrapResult bootstrapResult = ModBootstrapOrchestrator.Execute(
            readResult.Config,
            readResult.EffectiveFieldState.Value,
            bootstrapOperations);
        return new ModStartupResult(
            MapBootstrapStatus(bootstrapResult.Status),
            readResult,
            bootstrapResult);
    }

    /// <summary>
    /// 把 config-read 子状态一对一提升为顶层稳定状态；成功状态只能继续 bootstrap，不能在这里返回。
    /// </summary>
    private static ModStartupStatus MapReadStatus(ModConfigReadStatus status)
    {
        return status switch
        {
            ModConfigReadStatus.InvalidInspection => ModStartupStatus.InvalidInspection,
            ModConfigReadStatus.SnapshotChanged => ModStartupStatus.SnapshotChanged,
            ModConfigReadStatus.GeneratedConfigurationInvalid =>
                ModStartupStatus.GeneratedConfigurationInvalid,
            ModConfigReadStatus.ReadFailed => ModStartupStatus.ReadFailed,
            ModConfigReadStatus.Success => throw new InvalidOperationException(
                "成功配置读取不能映射为失败 startup 状态。"),
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "未知配置读取状态。"),
        };
    }

    /// <summary>
    /// 把 bootstrap 子状态一对一提升，保留 zero、模式关闭与两种 runtime 成功终态的区别。
    /// </summary>
    private static ModStartupStatus MapBootstrapStatus(ModBootstrapStatus status)
    {
        return status switch
        {
            ModBootstrapStatus.CompatibilityInvalid => ModStartupStatus.CompatibilityInvalid,
            ModBootstrapStatus.ValidationInvalid => ModStartupStatus.ValidationInvalid,
            ModBootstrapStatus.ZeroEnabledNpcIds => ModStartupStatus.ZeroEnabledNpcIds,
            ModBootstrapStatus.DisabledModes => ModStartupStatus.DisabledModes,
            ModBootstrapStatus.StaticRuntimeInitialized => ModStartupStatus.StaticRuntimeInitialized,
            ModBootstrapStatus.AgentRuntimeInitialized => ModStartupStatus.AgentRuntimeInitialized,
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "未知 bootstrap 状态。"),
        };
    }
}
