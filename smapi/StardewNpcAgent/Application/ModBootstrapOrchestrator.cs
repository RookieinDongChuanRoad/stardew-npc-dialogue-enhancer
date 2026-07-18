using StardewNpcAgent.Configuration;

namespace StardewNpcAgent.Application;

/// <summary>
/// 可执行 bootstrap orchestration 的稳定终态。
/// </summary>
internal enum ModBootstrapStatus
{
    CompatibilityInvalid,
    ValidationInvalid,
    ZeroEnabledNpcIds,
    DisabledModes,
    StaticRuntimeInitialized,
    AgentRuntimeInitialized,
}

/// <summary>
/// bootstrap 结果保留纯策略与 validation 证据，供 ModEntry 记录稳定诊断。
/// </summary>
internal sealed record ModBootstrapResult(
    ModBootstrapStatus Status,
    ModConfigCompatibilityResult Compatibility,
    ModConfigValidationResult? Validation);

/// <summary>
/// Bootstrap 唯一允许的纯步骤与副作用 delegate 集合。
/// </summary>
internal sealed class ModBootstrapOperations
{
    /// <summary>
    /// 创建 operations；每个 delegate 都是必需边界，避免测试或生产静默跳过某一步。
    /// </summary>
    internal ModBootstrapOperations(
        Func<ModConfig?, TargetNpcIdsFieldState, ModConfigCompatibilityResult> resolveEnabledNpcIds,
        Func<ModConfig, ModConfigValidationResult> validateConfig,
        Action<ModConfigCompatibilityResult> reportResolvedConfiguration,
        Action<IReadOnlyList<string>> constructStaticRuntime,
        Action initializeStaticRuntime,
        Action<IReadOnlyList<string>, ValidatedAgentConfiguration> constructAgentRuntime,
        Action installAgentHarmony,
        Action initializeAgentRuntime)
    {
        ResolveEnabledNpcIds = resolveEnabledNpcIds
            ?? throw new ArgumentNullException(nameof(resolveEnabledNpcIds));
        ValidateConfig = validateConfig ?? throw new ArgumentNullException(nameof(validateConfig));
        ReportResolvedConfiguration = reportResolvedConfiguration
            ?? throw new ArgumentNullException(nameof(reportResolvedConfiguration));
        ConstructStaticRuntime = constructStaticRuntime
            ?? throw new ArgumentNullException(nameof(constructStaticRuntime));
        InitializeStaticRuntime = initializeStaticRuntime
            ?? throw new ArgumentNullException(nameof(initializeStaticRuntime));
        ConstructAgentRuntime = constructAgentRuntime
            ?? throw new ArgumentNullException(nameof(constructAgentRuntime));
        InstallAgentHarmony = installAgentHarmony
            ?? throw new ArgumentNullException(nameof(installAgentHarmony));
        InitializeAgentRuntime = initializeAgentRuntime
            ?? throw new ArgumentNullException(nameof(initializeAgentRuntime));
    }

    internal Func<ModConfig?, TargetNpcIdsFieldState, ModConfigCompatibilityResult> ResolveEnabledNpcIds { get; }

    internal Func<ModConfig, ModConfigValidationResult> ValidateConfig { get; }

    internal Action<ModConfigCompatibilityResult> ReportResolvedConfiguration { get; }

    internal Action<IReadOnlyList<string>> ConstructStaticRuntime { get; }

    internal Action InitializeStaticRuntime { get; }

    internal Action<IReadOnlyList<string>, ValidatedAgentConfiguration> ConstructAgentRuntime { get; }

    internal Action InstallAgentHarmony { get; }

    internal Action InitializeAgentRuntime { get; }
}

/// <summary>
/// 生产 ModEntry 与测试共用的唯一 bootstrap 状态机。
/// </summary>
internal static class ModBootstrapOrchestrator
{
    /// <summary>
    /// 严格执行 Resolve → Validate → report → zero gate → runtime → Harmony → Initialize。
    /// </summary>
    internal static ModBootstrapResult Execute(
        ModConfig config,
        TargetNpcIdsFieldState fieldState,
        ModBootstrapOperations operations)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(operations);

        ModConfigCompatibilityResult compatibility = operations.ResolveEnabledNpcIds(config, fieldState);
        if (!compatibility.IsConfigurationUsable)
        {
            return new ModBootstrapResult(
                ModBootstrapStatus.CompatibilityInvalid,
                compatibility,
                Validation: null);
        }

        ModConfigValidationResult validation = operations.ValidateConfig(config);
        if (!validation.IsValid)
        {
            return new ModBootstrapResult(
                ModBootstrapStatus.ValidationInvalid,
                compatibility,
                validation);
        }

        operations.ReportResolvedConfiguration(compatibility);
        if (compatibility.EnabledNpcIds.Count == 0)
        {
            return new ModBootstrapResult(
                ModBootstrapStatus.ZeroEnabledNpcIds,
                compatibility,
                validation);
        }

        if (config.EnableStaticDialogueSpike)
        {
            operations.ConstructStaticRuntime(compatibility.EnabledNpcIds);
            operations.InitializeStaticRuntime();
            return new ModBootstrapResult(
                ModBootstrapStatus.StaticRuntimeInitialized,
                compatibility,
                validation);
        }

        if (config.EnableAgentDialogue)
        {
            ValidatedAgentConfiguration agentConfiguration = validation.AgentConfiguration
                ?? throw new InvalidOperationException("正式模式缺少已验证后端配置。");
            operations.ConstructAgentRuntime(compatibility.EnabledNpcIds, agentConfiguration);
            operations.InstallAgentHarmony();
            operations.InitializeAgentRuntime();
            return new ModBootstrapResult(
                ModBootstrapStatus.AgentRuntimeInitialized,
                compatibility,
                validation);
        }

        return new ModBootstrapResult(
            ModBootstrapStatus.DisabledModes,
            compatibility,
            validation);
    }
}
