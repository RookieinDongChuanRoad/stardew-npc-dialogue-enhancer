using StardewModdingAPI;
using StardewNpcAgent.Application;
using StardewNpcAgent.Configuration;
using StardewNpcAgent.Game;
using StardewNpcAgent.Integration;

namespace StardewNpcAgent;

/// <summary>
/// Stardew NPC Agent Mod 的 SMAPI 进程入口。
/// </summary>
/// <remarks>
/// Entry 只负责只读检查/读取配置、解析 enabled NPC、验证并装配运行时。默认模式关闭，因此普通安装
/// 不订阅资产事件、不读取世界状态、不访问网络，也不改变 NPC 交互。
/// </remarks>
public sealed class ModEntry : Mod
{
    private DialogueSpikeRuntime? dialogueSpikeRuntime;
    private SaveSessionRuntime? saveSessionRuntime;

    /// <summary>
    /// 由 SMAPI 在 Mod 加载时调用；只有配置 snapshot 稳定、校验通过且目标非空时才初始化运行时。
    /// </summary>
    /// <param name="helper">SMAPI 提供的配置、事件与内容 helper。</param>
    public override void Entry(IModHelper helper)
    {
        ModStartupOperations operations = new ModStartupOperations(
            inspectInitialConfiguration: () => ModConfigFileInspector.InspectSnapshot(helper.DirectoryPath),
            isSnapshotCurrent: snapshot => ModConfigFileInspector.IsSnapshotCurrent(
                helper.DirectoryPath,
                snapshot),
            createNewConfiguration: () =>
            {
                // 只触发 SMAPI 正常首次生成；返回的瞬时对象不会进入 policy，随后必须重新 Inspect + pure read。
                _ = helper.ReadConfig<ModConfig>();
            },
            inspectCurrentConfiguration: () => ModConfigFileInspector.InspectSnapshot(helper.DirectoryPath),
            readExistingConfiguration: () => helper.Data.ReadJsonFile<ModConfig>("config.json"),
            createBootstrapOperations: config => CreateBootstrapOperations(helper, config));
        ModStartupResult startup = ModStartupOrchestrator.Execute(operations);

        switch (startup.Status)
        {
            case ModStartupStatus.InspectionFailed:
                // 不记录路径或异常消息，避免把用户目录与配置原文带入日志。
                Monitor.Log("CONFIGURATION_INSPECTION_FAILED。", LogLevel.Error);
                return;
            case ModStartupStatus.InvalidInspection:
                Monitor.Log("CONFIGURATION_INVALID。", LogLevel.Error);
                return;
            case ModStartupStatus.SnapshotChanged:
                Monitor.Log("CONFIGURATION_CHANGED_DURING_READ。", LogLevel.Error);
                return;
            case ModStartupStatus.GeneratedConfigurationInvalid:
                Monitor.Log("CONFIGURATION_GENERATION_INVALID。", LogLevel.Error);
                return;
            case ModStartupStatus.ReadFailed:
                Monitor.Log("CONFIGURATION_READ_FAILED。", LogLevel.Error);
                return;
            case ModStartupStatus.CompatibilityInvalid:
                Monitor.Log("CONFIGURATION_COMPATIBILITY_FAILED。", LogLevel.Error);
                return;
            case ModStartupStatus.ValidationInvalid:
                IReadOnlyList<string> errorCodes = startup.BootstrapResult?.Validation?.ErrorCodes
                    ?? Array.Empty<string>();
                Monitor.Log(
                    "Stardew NPC Agent 配置无效，已保持零行为："
                        + string.Join(", ", errorCodes),
                    LogLevel.Error);
                return;
            case ModStartupStatus.ZeroEnabledNpcIds:
                Monitor.Log("TARGET_NPC_RUNTIME_DISABLED enabled_count=0。", LogLevel.Info);
                return;
            case ModStartupStatus.DisabledModes:
                Monitor.Log(
                    "Stardew NPC Agent 已加载；静态 Spike 与正式 Agent 默认均关闭，未修改资产或访问网络。",
                    LogLevel.Info);
                return;
            case ModStartupStatus.StaticRuntimeInitialized:
            case ModStartupStatus.AgentRuntimeInitialized:
                return;
            default:
                throw new InvalidOperationException($"未知 Mod startup 状态：{startup.Status}。");
        }
    }

    /// <summary>
    /// 为已稳定读取的单一配置创建 bootstrap delegates；本方法只装配依赖，不执行状态机步骤。
    /// </summary>
    private ModBootstrapOperations CreateBootstrapOperations(IModHelper helper, ModConfig config)
    {
        return new ModBootstrapOperations(
            resolveEnabledNpcIds: ModConfigCompatibilityPolicy.ResolveEnabledNpcIds,
            validateConfig: ModConfigValidator.Validate,
            reportResolvedConfiguration: ReportResolvedConfiguration,
            constructStaticRuntime: enabledNpcIds =>
            {
                dialogueSpikeRuntime = new DialogueSpikeRuntime(
                    helper,
                    Monitor,
                    config,
                    enabledNpcIds);
            },
            initializeStaticRuntime: () =>
                (dialogueSpikeRuntime
                    ?? throw new InvalidOperationException("静态模式 runtime 尚未构造。"))
                .Initialize(),
            constructAgentRuntime: (enabledNpcIds, agentConfiguration) =>
            {
                saveSessionRuntime = new SaveSessionRuntime(
                    helper,
                    Monitor,
                    config,
                    agentConfiguration,
                    enabledNpcIds);
            },
            installAgentHarmony: InstallGiftObserver,
            initializeAgentRuntime: () =>
                (saveSessionRuntime
                    ?? throw new InvalidOperationException("正式模式 runtime 尚未构造。"))
                .Initialize());
    }

    /// <summary>
    /// 在 validation 成功后记录稳定 warning 与计数；不回显用户配置的 NPC ID、路径或台词。
    /// </summary>
    private void ReportResolvedConfiguration(ModConfigCompatibilityResult compatibility)
    {
        foreach (string warningCode in compatibility.WarningCodes)
        {
            int warningCount = warningCode == ModConfigCompatibilityPolicy.UnsupportedIgnoredWarningCode
                ? compatibility.IgnoredUnsupportedNpcIdCount
                : compatibility.EnabledNpcIds.Count;
            Monitor.Log(
                $"TARGET_NPC_CONFIGURATION_WARNING code={warningCode}, count={warningCount}。",
                LogLevel.Warn);
        }

        Monitor.Log(
            $"TARGET_NPC_REGISTRY_RESOLVED supported_count={VanillaMarriageableNpcRegistry.AllIds.Count}, "
                + $"enabled_count={compatibility.EnabledNpcIds.Count}。",
            LogLevel.Info);
    }

    /// <summary>
    /// 安装唯一批准的 accepted-gift observer；必须只由 Agent bootstrap 在 runtime 构造后调用。
    /// </summary>
    private void InstallGiftObserver()
    {
        SaveSessionRuntime runtime = saveSessionRuntime
            ?? throw new InvalidOperationException("正式模式 runtime 尚未构造。");
        GiftGivenPatchInstallResult giftObserver = GiftGivenHarmonyPatch.TryInstall(
            typeof(StardewValley.Game1).Assembly.Location,
            runtime.HandleGiftGiven,
            reasonCode => Monitor.Log(
                $"GIFT_OBSERVER_RUNTIME_FAILURE reason={reasonCode}。",
                LogLevel.Error));
        if (!giftObserver.IsInstalled)
        {
            // Harmony 兼容审计失败时只关闭 gift producer；其余正式 Agent 事件与对话仍可运行。
            Monitor.Log(
                $"GIFT_OBSERVER_DISABLED reason={giftObserver.ReasonCode}。",
                LogLevel.Warn);
        }
    }
}
