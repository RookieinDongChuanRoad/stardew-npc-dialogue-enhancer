namespace StardewNpcAgent.Configuration;

/// <summary>
/// 配置读取协调器的稳定终态。
/// </summary>
internal enum ModConfigReadStatus
{
    Success,
    InvalidInspection,
    SnapshotChanged,
    GeneratedConfigurationInvalid,
    ReadFailed,
}

/// <summary>
/// 配置读取结果；只有 <see cref="ModConfigReadStatus.Success"/> 才携带非 null 配置与有效字段状态。
/// </summary>
/// <param name="Status">稳定读取终态。</param>
/// <param name="Config">来自稳定 existing pure reader 的配置；失败时为 null。</param>
/// <param name="EffectiveFieldState">
/// 与 <paramref name="Config"/> 同一稳定 snapshot 的字段状态；首次生成后是最终 ExplicitValue，而不是初始 New。
/// </param>
internal sealed record ModConfigReadResult(
    ModConfigReadStatus Status,
    ModConfig? Config,
    TargetNpcIdsFieldState? EffectiveFieldState);

/// <summary>
/// 在不依赖庞大 SMAPI fake 的前提下冻结 config snapshot、首次生成与纯读路径选择。
/// </summary>
internal static class ModConfigReadCoordinator
{
    /// <summary>
    /// 依据同一 bytes inspection 选择唯一读取流程，并在每次 existing pure read 前后复核稳定指纹。
    /// </summary>
    /// <remarks>
    /// Existing 分支绝不调用 create；New 分支只用 create 触发 SMAPI 正常首次生成，随后重新 Inspect，并只接受
    /// 稳定 ExplicitValue 的 final snapshot。任一核对失败、reader 抛错或返回 null 都 fail closed，且不重试。
    /// </remarks>
    internal static ModConfigReadResult Read(
        ModConfigFileInspection inspection,
        Func<ModConfigFileInspection, bool> isSnapshotCurrent,
        Action createNewConfiguration,
        Func<ModConfigFileInspection> inspectCurrentSnapshot,
        Func<ModConfig?> readExistingConfiguration)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        ArgumentNullException.ThrowIfNull(isSnapshotCurrent);
        ArgumentNullException.ThrowIfNull(createNewConfiguration);
        ArgumentNullException.ThrowIfNull(inspectCurrentSnapshot);
        ArgumentNullException.ThrowIfNull(readExistingConfiguration);

        TargetNpcIdsFieldState fieldState = inspection.FieldState;
        if (fieldState == TargetNpcIdsFieldState.InvalidConfiguration)
        {
            return Failure(ModConfigReadStatus.InvalidInspection);
        }

        try
        {
            // New 与 existing 都先复核；这能拒绝 inspection 后新出现、消失或已被替换的文件。
            if (!isSnapshotCurrent(inspection))
            {
                return Failure(ModConfigReadStatus.SnapshotChanged);
            }

            if (fieldState == TargetNpcIdsFieldState.NewConfiguration)
            {
                createNewConfiguration();
                ModConfigFileInspection finalSnapshot = inspectCurrentSnapshot();

                // 正常 SMAPI 首次生成会显式写入十二人数组。其他状态表示并发文件或生成异常，不能猜测。
                if (finalSnapshot.FieldState != TargetNpcIdsFieldState.ExplicitValue)
                {
                    return Failure(ModConfigReadStatus.GeneratedConfigurationInvalid);
                }

                return ReadStableExistingSnapshot(
                    finalSnapshot,
                    isSnapshotCurrent,
                    readExistingConfiguration,
                    verifyBeforeRead: true);
            }

            return ReadStableExistingSnapshot(
                inspection,
                isSnapshotCurrent,
                readExistingConfiguration,
                verifyBeforeRead: false);
        }
        catch (Exception)
        {
            // 不泄露异常消息或路径，也不回退另一 reader；ModEntry 只记录稳定状态码。
            return Failure(ModConfigReadStatus.ReadFailed);
        }
    }

    /// <summary>
    /// 对一个已确认 existing 的 snapshot 执行 pure read，并在调用前后比较同一稳定指纹。
    /// </summary>
    private static ModConfigReadResult ReadStableExistingSnapshot(
        ModConfigFileInspection snapshot,
        Func<ModConfigFileInspection, bool> isSnapshotCurrent,
        Func<ModConfig?> readExistingConfiguration,
        bool verifyBeforeRead)
    {
        // 初始 existing snapshot 已在 Read 入口核对；New 生成的 final snapshot 则需要自己的读前核对。
        if (verifyBeforeRead && !isSnapshotCurrent(snapshot))
        {
            return Failure(ModConfigReadStatus.SnapshotChanged);
        }

        ModConfig? config = readExistingConfiguration();
        if (config is null)
        {
            return Failure(ModConfigReadStatus.ReadFailed);
        }

        if (!isSnapshotCurrent(snapshot))
        {
            return Failure(ModConfigReadStatus.SnapshotChanged);
        }

        return new ModConfigReadResult(
            ModConfigReadStatus.Success,
            config,
            snapshot.FieldState);
    }

    /// <summary>
    /// 所有失败都清空配置与 effective state，防止调用方误用读取窗口中的瞬时对象。
    /// </summary>
    private static ModConfigReadResult Failure(ModConfigReadStatus status)
    {
        return new ModConfigReadResult(status, Config: null, EffectiveFieldState: null);
    }
}
