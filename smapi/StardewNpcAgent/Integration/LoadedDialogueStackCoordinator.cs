using StardewNpcAgent.Application;
using StardewNpcAgent.Game;
using StardewNpcAgent.Infrastructure.Storage;

namespace StardewNpcAgent.Integration;

/// <summary>
/// 已加载游戏对象的最小读写端口。
/// </summary>
/// <remarks>
/// 协调层只依赖不可变事实与逐字符条件替换，不知道 <c>Game1</c>、<c>Dialogue</c> 或
/// <c>DialogueLine</c>。真实适配器保证两个方法都只在 SMAPI 主线程调用；测试可用内存载体
/// 验证同一合同，而无需执行平台架构不兼容的 Stardew 程序集。
/// </remarks>
internal interface ILoadedDialogueTargetAccess
{
    /// <summary>
    /// 重新读取当前栈事实，并把调用方观察到的 UI 状态合并到快照中。
    /// </summary>
    /// <param name="isDialogueDisplayActive">当前是否已有原生对话显示。</param>
    /// <returns>不包含游戏对象引用的当前事实。</returns>
    LoadedDialogueStackFacts ReadCurrentFacts(bool isDialogueDisplayActive);

    /// <summary>
    /// 仅当当前文本逐字符等于预期值时执行替换。
    /// </summary>
    /// <param name="expectedCurrentText">调用方最后一次确认的当前文本。</param>
    /// <param name="replacementText">准备写入的替换文本。</param>
    /// <returns>比较与替换是否作为一个主线程步骤成功。</returns>
    bool TryReplaceText(string expectedCurrentText, string replacementText);
}

/// <summary>
/// 一条候选与其已加载游戏对象访问端口的不可变绑定。
/// </summary>
/// <param name="Key">日期、语言、NPC、资产与字段组成的完整 live cache 身份。</param>
/// <param name="Candidate">生成请求使用的精确 raw source 候选。</param>
/// <param name="Access">只持有捕获对象并执行当前事实复核的主线程端口。</param>
internal sealed record LoadedDialogueTarget(
    DailyDialogueCacheKey Key,
    DialogueCandidate Candidate,
    ILoadedDialogueTargetAccess Access);

/// <summary>
/// 直接写入已加载栈的稳定结果码。
/// </summary>
public enum LoadedDialogueApplyReasonCode
{
    /// <summary>全部身份、事实、cache 与文本比较通过，增强文本已写入。</summary>
    Applied,

    /// <summary>目标身份或当前栈事实不再满足捕获条件。</summary>
    TargetRejected,

    /// <summary>live cache 无法签发与候选完全一致的正式 generated 决策。</summary>
    CacheDecisionRejected,

    /// <summary>最终逐字符 compare-and-swap 失败，说明最后检查后文本又发生变化。</summary>
    TextCompareFailed,

    /// <summary>写入后发生意外异常，协调器已经尝试回滚。</summary>
    RolledBack,
}

/// <summary>
/// 一次直接注入尝试的确定性结果。
/// </summary>
/// <param name="WasApplied">增强文本是否仍作为受跟踪补丁存在于目标行。</param>
/// <param name="ReasonCode">应用阶段结果。</param>
/// <param name="TargetReasonCode">目标策略拒绝时的具体原因，其他阶段为 null。</param>
public sealed record LoadedDialogueApplyResult(
    bool WasApplied,
    LoadedDialogueApplyReasonCode ReasonCode,
    LoadedDialogueStackReasonCode? TargetReasonCode);

/// <summary>
/// 生命周期清理释放直接补丁后的汇总。
/// </summary>
/// <param name="DirectKeys">
/// 本轮曾由直接路线跟踪的完整 key；运行时必须据此跳过资产 invalidation/reset。
/// </param>
/// <param name="RestoredCount">仍可证明安全且已从增强文本恢复为原文的行数。</param>
public sealed record LoadedDialogueReleaseResult(
    IReadOnlyList<DailyDialogueCacheKey> DirectKeys,
    int RestoredCount);

/// <summary>
/// 协调已加载文本的二次事实校验、cache 授权、条件写入、展示跟踪与生命周期释放。
/// </summary>
/// <remarks>
/// 本类不读取游戏全局状态，也不启动后台任务。调用方必须在 SMAPI 主线程调用它；它把
/// <see cref="LoadedDialogueStackPolicy"/>、现有 <see cref="DialogueDisplayCoordinator"/> 和
/// <see cref="DialogueDisplayObserver"/> 串成一条 fail-closed 提交路径。
/// </remarks>
internal sealed class LoadedDialogueStackCoordinator
{
    private readonly DailyDialogueCache cache;
    private readonly DialogueDisplayCoordinator displayCoordinator;
    private readonly DialogueDisplayObserver? displayObserver;
    private readonly Action<DialogueDisplayDecision>? testArmObserver;
    private readonly Dictionary<DailyDialogueCacheKey, AppliedLoadedDialoguePatch> appliedPatches = new();

    /// <summary>
    /// 使用真实原生 UI 观察器创建协调器。
    /// </summary>
    /// <param name="cache">Staged activator 已提交结果的 live cache。</param>
    /// <param name="displayCoordinator">负责复核 source hash 与正式生成元数据的展示协调器。</param>
    /// <param name="displayObserver">负责关联原生菜单并在首帧后产生 ACK 观察的实例。</param>
    public LoadedDialogueStackCoordinator(
        DailyDialogueCache cache,
        DialogueDisplayCoordinator displayCoordinator,
        DialogueDisplayObserver displayObserver)
    {
        this.cache = cache ?? throw new ArgumentNullException(nameof(cache));
        this.displayCoordinator = displayCoordinator
            ?? throw new ArgumentNullException(nameof(displayCoordinator));
        this.displayObserver = displayObserver
            ?? throw new ArgumentNullException(nameof(displayObserver));
    }

    /// <summary>
    /// 创建带可替换 arm 动作的协调器，供测试验证观察器异常后的事务式回滚。
    /// </summary>
    /// <remarks>
    /// 该构造器保持 internal，正式运行时只能使用接收真实
    /// <see cref="DialogueDisplayObserver"/> 的构造器。
    /// </remarks>
    internal LoadedDialogueStackCoordinator(
        DailyDialogueCache cache,
        DialogueDisplayCoordinator displayCoordinator,
        Action<DialogueDisplayDecision> armObserver)
    {
        this.cache = cache ?? throw new ArgumentNullException(nameof(cache));
        this.displayCoordinator = displayCoordinator
            ?? throw new ArgumentNullException(nameof(displayCoordinator));
        testArmObserver = armObserver ?? throw new ArgumentNullException(nameof(armObserver));
    }

    /// <summary>
    /// 在 Agent 返回后对同一目标执行全量复核，并将正式 generated 文本受控写入当前行。
    /// </summary>
    /// <param name="target">DayStarted 主线程捕获的候选与对象访问 token。</param>
    /// <param name="entry">Staged activator 刚提交到 live cache 的正式结果。</param>
    /// <param name="isDialogueDisplayActive">当前是否已有原生对话显示。</param>
    /// <param name="filteredPlayerName">
    /// 当前主线程经原版 Utility 过滤的本地名字；只转交 observer render，不进入 registry。
    /// </param>
    /// <returns>稳定的应用或拒绝结果；正常拒绝已经删除精确 live cache 项。</returns>
    /// <exception cref="Exception">
    /// 端口或观察器发生非业务异常时，在尝试回滚文本并清理 cache/registry 后原样传播。
    /// </exception>
    public LoadedDialogueApplyResult Apply(
        LoadedDialogueTarget target,
        DailyDialogueCacheEntry entry,
        bool isDialogueDisplayActive,
        string? filteredPlayerName = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(entry);

        if (!HasMatchingPreparedIdentity(target, entry))
        {
            return Reject(
                entry.Key,
                LoadedDialogueApplyReasonCode.TargetRejected,
                targetReasonCode: null);
        }

        LoadedDialogueStackDecision targetDecision = LoadedDialogueStackPolicy.EvaluateCurrent(
            target.Access.ReadCurrentFacts(isDialogueDisplayActive));
        if (!targetDecision.IsEligible)
        {
            return Reject(
                entry.Key,
                LoadedDialogueApplyReasonCode.TargetRejected,
                targetDecision.ReasonCode);
        }

        DialogueDisplayDecision displayDecision = displayCoordinator.Resolve(
            new DialogueDisplayContext(
                target.Key.GameDayIndex,
                target.Key.Locale,
                target.Key.NpcId,
                target.Key.AssetName,
                target.Key.DialogueKey,
                target.Candidate.SourceText));
        if (!MatchesActivatedEntry(displayDecision, entry))
        {
            return Reject(
                entry.Key,
                LoadedDialogueApplyReasonCode.CacheDecisionRejected,
                targetReasonCode: null);
        }

        PreparedDialogueArm? preparedArm = displayObserver?.PrepareArm(
            displayDecision,
            filteredPlayerName);
        if (displayObserver is not null && preparedArm is null)
        {
            // 唯一正常情况是 template 含 @，但当前本地玩家名/过滤结果不可用。
            // 必须在任何 DialogueLine 写入前精确删除该 cache 并回退原版。
            return Reject(
                entry.Key,
                LoadedDialogueApplyReasonCode.CacheDecisionRejected,
                targetReasonCode: null);
        }

        bool textWasReplaced = false;
        try
        {
            // 这是实际写入前的最后一道门。即便当前事实刚刚通过，只要其他组件在两步之间
            // 改了文本，逐字符比较也会失败并保留对方的当前值。
            if (!target.Access.TryReplaceText(
                    target.Candidate.SourceText,
                    displayDecision.EnhancedText!))
            {
                return Reject(
                    entry.Key,
                    LoadedDialogueApplyReasonCode.TextCompareFailed,
                    targetReasonCode: null);
            }

            textWasReplaced = true;
            if (displayObserver is not null)
            {
                displayObserver.CommitArm(preparedArm!);
            }
            else
            {
                testArmObserver!(displayDecision);
            }
            appliedPatches[entry.Key] = new AppliedLoadedDialoguePatch(
                target,
                target.Candidate.SourceText,
                displayDecision.EnhancedText!,
                displayDecision);

            return new LoadedDialogueApplyResult(
                WasApplied: true,
                ReasonCode: LoadedDialogueApplyReasonCode.Applied,
                TargetReasonCode: null);
        }
        catch
        {
            // 异常不是业务终态，必须继续抛给现有日志边界。不过在离开前先执行补偿：只有
            // 当前值仍等于本次写入值时才恢复，因此补偿也不会覆盖第三方竞态修改。
            if (textWasReplaced)
            {
                TryRollbackWithoutMaskingOriginalException(
                    target.Access,
                    displayDecision.EnhancedText!,
                    target.Candidate.SourceText);
            }

            cache.Remove(entry.Key);
            appliedPatches.Remove(entry.Key);
            throw;
        }
    }

    /// <summary>
    /// 释放所有尚未通过真实首帧确认的直接补丁，并在仍可证明安全时恢复原文。
    /// </summary>
    /// <param name="canVerifyCurrentTarget">
    /// world 是否仍可安全读取；返回标题后必须为 false，此时只丢弃对象引用而不写 detached 对象。
    /// </param>
    /// <returns>全部直接 key 与成功恢复数量。</returns>
    public LoadedDialogueReleaseResult ReleaseUndisplayed(bool canVerifyCurrentTarget)
    {
        AppliedLoadedDialoguePatch[] patches = appliedPatches.Values
            .OrderBy(patch => patch.Target.Key.GameDayIndex)
            .ThenBy(patch => patch.Target.Key.Locale, StringComparer.Ordinal)
            .ThenBy(patch => patch.Target.Key.NpcId, StringComparer.Ordinal)
            .ThenBy(patch => patch.Target.Key.AssetName, StringComparer.Ordinal)
            .ThenBy(patch => patch.Target.Key.DialogueKey, StringComparer.Ordinal)
            .ToArray();

        // 先清 registry，确保即使后续真实对象访问意外抛错，也不会在下一次生命周期重复写旧对象。
        appliedPatches.Clear();
        int restoredCount = 0;
        if (canVerifyCurrentTarget)
        {
            foreach (AppliedLoadedDialoguePatch patch in patches)
            {
                LoadedDialogueStackFacts currentFacts = patch.Target.Access.ReadCurrentFacts(
                    isDialogueDisplayActive: false);
                if (LoadedDialogueStackPolicy.CanRestore(currentFacts)
                    && patch.Target.Access.TryReplaceText(
                        patch.GeneratedText,
                        patch.OriginalText))
                {
                    restoredCount++;
                }
            }
        }

        return new LoadedDialogueReleaseResult(
            patches.Select(patch => patch.Target.Key).ToArray(),
            restoredCount);
    }

    /// <summary>
    /// 标记一条直接补丁已由原生 UI 真实展示，并释放其生命周期回滚记录。
    /// </summary>
    /// <param name="decision">
    /// 必须与 registry 中保存的 opaque decision 是同一对象，而不仅是字段值相等。
    /// </param>
    /// <returns>命中的完整 direct key；非本协调器 token 或重复调用返回 null。</returns>
    public DailyDialogueCacheKey? MarkDisplayed(DialogueDisplayDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        foreach ((DailyDialogueCacheKey key, AppliedLoadedDialoguePatch patch) in appliedPatches)
        {
            if (!ReferenceEquals(patch.DisplayDecision, decision))
            {
                continue;
            }

            appliedPatches.Remove(key);
            return key;
        }

        return null;
    }

    /// <summary>
    /// 判断完整 cache key 是否仍由尚未首帧确认的 direct patch 跟踪。
    /// </summary>
    /// <param name="key">待检查的日期、语言、NPC、资产与字段完整身份。</param>
    /// <returns>该 key 是否属于直接已加载路线，而非等待 Late asset patch 的路线。</returns>
    /// <remarks>
    /// 运行时的 AssetRequested 过滤使用此查询，避免 direct entry 为等待 ACK 暂留 live cache
    /// 期间又被资产路线二次注入。查询不暴露 target 或 opaque decision。
    /// </remarks>
    public bool IsTrackedDirectKey(DailyDialogueCacheKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return appliedPatches.ContainsKey(key);
    }

    /// <summary>
    /// 核对 prepared target、raw candidate 与 activated entry 的完整复合身份。
    /// </summary>
    private static bool HasMatchingPreparedIdentity(
        LoadedDialogueTarget target,
        DailyDialogueCacheEntry entry)
    {
        return target.Candidate is not null
            && target.Access is not null
            && target.Key == entry.Key
            && DialogueSourceClassifier.MatchesIdentity(
                entry.SourceFamily,
                entry.Key.NpcId,
                entry.Key.AssetName,
                entry.Key.DialogueKey)
            && target.Candidate.SourceFamily == entry.SourceFamily
            && string.Equals(target.Candidate.NpcId, target.Key.NpcId, StringComparison.Ordinal)
            && string.Equals(target.Candidate.Locale, target.Key.Locale, StringComparison.Ordinal)
            && string.Equals(target.Candidate.AssetName, target.Key.AssetName, StringComparison.Ordinal)
            && string.Equals(target.Candidate.DialogueKey, target.Key.DialogueKey, StringComparison.Ordinal)
            && string.Equals(target.Candidate.SourceText, entry.SourceText, StringComparison.Ordinal)
            && string.Equals(target.Candidate.SourceHash, entry.SourceHash, StringComparison.Ordinal)
            && !string.IsNullOrEmpty(target.Candidate.SourceText);
    }

    /// <summary>
    /// 确认展示协调器签发的 token 确实对应本次 activator entry，而非同 key 的旧缓存值。
    /// </summary>
    private static bool MatchesActivatedEntry(
        DialogueDisplayDecision decision,
        DailyDialogueCacheEntry entry)
    {
        return decision.Kind == DialogueDisplayDecisionKind.UseGenerated
            && decision.CacheKey == entry.Key
            && decision.SourceFamily == entry.SourceFamily
            && string.Equals(decision.SourceText, entry.SourceText, StringComparison.Ordinal)
            && string.Equals(decision.EnhancedText, entry.EnhancedText, StringComparison.Ordinal)
            && string.Equals(decision.GenerationId, entry.GenerationId, StringComparison.Ordinal)
            && string.Equals(decision.SourceHash, entry.SourceHash, StringComparison.Ordinal);
    }

    /// <summary>
    /// 完成普通拒绝的统一精确清理。
    /// </summary>
    private LoadedDialogueApplyResult Reject(
        DailyDialogueCacheKey key,
        LoadedDialogueApplyReasonCode reasonCode,
        LoadedDialogueStackReasonCode? targetReasonCode)
    {
        cache.Remove(key);
        appliedPatches.Remove(key);
        return new LoadedDialogueApplyResult(
            WasApplied: false,
            ReasonCode: reasonCode,
            TargetReasonCode: targetReasonCode);
    }

    /// <summary>
    /// 尽力执行异常补偿；补偿端口异常不能掩盖触发补偿的原始异常。
    /// </summary>
    private static void TryRollbackWithoutMaskingOriginalException(
        ILoadedDialogueTargetAccess access,
        string generatedText,
        string originalText)
    {
        try
        {
            access.TryReplaceText(generatedText, originalText);
        }
        catch
        {
            // 端口合同正常不应抛出。此处刻意保留原异常给运行时稳定日志边界；registry/cache
            // 仍会在外层 catch 中清理，且不再尝试写可能已脱离当前世界的对象。
        }
    }

    /// <summary>
    /// 一条已写入但尚未首帧确认的进程内补丁记录。
    /// </summary>
    private sealed record AppliedLoadedDialoguePatch(
        LoadedDialogueTarget Target,
        string OriginalText,
        string GeneratedText,
        DialogueDisplayDecision DisplayDecision);
}
