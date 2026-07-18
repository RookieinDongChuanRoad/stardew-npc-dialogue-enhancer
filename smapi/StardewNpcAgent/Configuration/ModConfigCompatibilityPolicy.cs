using StardewNpcAgent.Game;

namespace StardewNpcAgent.Configuration;

/// <summary>
/// 为 runtime 创建独立、不可变的 enabled NPC ID 快照。
/// </summary>
/// <remarks>
/// 该 helper 不执行 trim、去重或 supported 过滤；这些业务规则只属于
/// <see cref="ModConfigCompatibilityPolicy"/>。它唯一负责切断 public runtime overload 与调用方可变 List
/// 之间的引用共享，让两个 runtime 使用同一项可执行、可测试的防御性复制合同。
/// </remarks>
internal static class EnabledNpcIdsSnapshot
{
    /// <summary>
    /// 复制调用方列表并返回不能通过 <see cref="IList{T}"/> 修改的只读视图。
    /// </summary>
    internal static IReadOnlyList<string> Create(IReadOnlyList<string> enabledNpcIds)
    {
        ArgumentNullException.ThrowIfNull(enabledNpcIds);
        return Array.AsReadOnly(enabledNpcIds.ToArray());
    }
}

/// <summary>
/// 配置字段兼容解析的不可变结果。
/// </summary>
public sealed class ModConfigCompatibilityResult
{
    /// <summary>
    /// 创建结果快照；构造时再次复制集合，防止策略内部或未来调用方持有的数组泄漏可变性。
    /// </summary>
    /// <param name="isConfigurationUsable">配置是否可继续进入通用 validation。</param>
    /// <param name="enabledNpcIds">已按配置顺序解析且限定在支持集内的 NPC ID。</param>
    /// <param name="warningCodes">稳定、无用户原文的 warning code。</param>
    /// <param name="ignoredUnsupportedNpcIdCount">被支持集过滤的去重后未知 ID 数量。</param>
    internal ModConfigCompatibilityResult(
        bool isConfigurationUsable,
        IEnumerable<string> enabledNpcIds,
        IEnumerable<string> warningCodes,
        int ignoredUnsupportedNpcIdCount)
    {
        IsConfigurationUsable = isConfigurationUsable;
        EnabledNpcIds = Array.AsReadOnly(enabledNpcIds.ToArray());
        WarningCodes = Array.AsReadOnly(warningCodes.ToArray());
        IgnoredUnsupportedNpcIdCount = ignoredUnsupportedNpcIdCount;
    }

    /// <summary>配置是否可继续进入 validator 与运行时装配决策。</summary>
    public bool IsConfigurationUsable { get; }

    /// <summary>不可变、保留配置顺序的运行时 enabled NPC ID。</summary>
    public IReadOnlyList<string> EnabledNpcIds { get; }

    /// <summary>稳定、去重且不包含用户原始 ID 的 warning code。</summary>
    public IReadOnlyList<string> WarningCodes { get; }

    /// <summary>去重后被忽略的 unsupported ID 数量；日志只能输出该计数，不能输出原值。</summary>
    public int IgnoredUnsupportedNpcIdCount { get; }
}

/// <summary>
/// 把字段存在性与反序列化配置解析成唯一的运行时 enabled NPC 集合。
/// </summary>
/// <remarks>
/// 本类是 <c>TargetNpcIds</c> 的唯一业务解释器。Inspector 只报告 JSON 结构，validator 只验证模式、URL
/// 与 timeout，两个 runtime 只接收这里产出的快照；这样不会在多个层次形成不一致的旧配置兼容规则。
/// </remarks>
public static class ModConfigCompatibilityPolicy
{
    /// <summary>旧配置缺少目标字段时使用的稳定诊断码。</summary>
    public const string MissingUsingLegacyDefaultWarningCode =
        "TARGET_NPC_IDS_MISSING_USING_LEGACY_DEFAULT";

    /// <summary>显式配置包含 unsupported ID 时使用的稳定诊断码。</summary>
    public const string UnsupportedIgnoredWarningCode =
        "TARGET_NPC_IDS_UNSUPPORTED_IGNORED";

    private static readonly string[] LegacyDefaultIds =
    {
        "Abigail",
        "Sebastian",
    };

    /// <summary>
    /// 按字段状态解析运行时 enabled NPC ID，并返回不可变快照。
    /// </summary>
    /// <param name="config">
    /// SMAPI 反序列化结果；已有文件返回 null 时不得回退到新配置生成，而是返回不可用结果。
    /// </param>
    /// <param name="fieldState">Inspector 在读取前冻结的顶层字段状态。</param>
    /// <returns>包含 enabled ID、稳定 warning code 与 unsupported 计数的不可变结果。</returns>
    public static ModConfigCompatibilityResult ResolveEnabledNpcIds(
        ModConfig? config,
        TargetNpcIdsFieldState fieldState)
    {
        if (fieldState == TargetNpcIdsFieldState.InvalidConfiguration || config is null)
        {
            return CreateUnusableResult();
        }

        if (fieldState == TargetNpcIdsFieldState.MissingFromExistingConfiguration)
        {
            return new ModConfigCompatibilityResult(
                isConfigurationUsable: true,
                LegacyDefaultIds,
                new[] { MissingUsingLegacyDefaultWarningCode },
                ignoredUnsupportedNpcIdCount: 0);
        }

        if (fieldState == TargetNpcIdsFieldState.ExplicitNull)
        {
            return new ModConfigCompatibilityResult(
                isConfigurationUsable: true,
                Array.Empty<string>(),
                Array.Empty<string>(),
                ignoredUnsupportedNpcIdCount: 0);
        }

        if (fieldState is not (TargetNpcIdsFieldState.NewConfiguration
            or TargetNpcIdsFieldState.ExplicitValue)
            || config.TargetNpcIds is null)
        {
            // 状态与反序列化结果互相矛盾时不猜测用户意图，保持零行为。
            return CreateUnusableResult();
        }

        IReadOnlyList<string> normalizedIds = config.GetNormalizedTargetNpcIds();
        string[] enabledIds = normalizedIds
            .Where(VanillaMarriageableNpcRegistry.Contains)
            .ToArray();
        int ignoredCount = normalizedIds.Count - enabledIds.Length;
        string[] warnings = ignoredCount == 0
            ? Array.Empty<string>()
            : new[] { UnsupportedIgnoredWarningCode };

        return new ModConfigCompatibilityResult(
            isConfigurationUsable: true,
            enabledIds,
            warnings,
            ignoredCount);
    }

    /// <summary>
    /// 构造统一的不可用空结果；错误诊断由知道失败阶段的 ModEntry 记录，不能混入 warning 列表。
    /// </summary>
    private static ModConfigCompatibilityResult CreateUnusableResult()
    {
        return new ModConfigCompatibilityResult(
            isConfigurationUsable: false,
            Array.Empty<string>(),
            Array.Empty<string>(),
            ignoredUnsupportedNpcIdCount: 0);
    }
}
