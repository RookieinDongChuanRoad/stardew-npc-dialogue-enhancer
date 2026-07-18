using StardewNpcAgent.Game;

namespace StardewNpcAgent.Integration;

/// <summary>
/// 游戏已选择栈顶的 authoritative source 与三层对象引用的原子只读快照。
/// </summary>
/// <param name="SourceIdentity">由 actual TranslationKey 分类得到的 ordinary/rainy identity。</param>
/// <param name="TranslationKey">捕获时 Dialogue 暴露的未修改 actual TranslationKey。</param>
/// <param name="SourceText">捕获时当前 DialogueLine 的逐字符 raw 文本。</param>
/// <param name="Locale">捕获时的 exact SMAPI locale。</param>
/// <param name="CaptureFacts">同一次读取产生的 shape 与行为事实。</param>
/// <param name="StackToken">捕获时的 Stack 对象引用。</param>
/// <param name="DialogueToken">捕获时的 Dialogue 对象引用。</param>
/// <param name="LineToken">捕获时的 DialogueLine 对象引用。</param>
/// <remarks>
/// 三个 token 只存在于本地 SMAPI 进程，绝不进入 HTTP/SQLite。调用方必须把这个快照
/// 贯穿 source resolve 与 candidate bind，不能再次 capture 一个“字段碰巧相同”的新对象。
/// </remarks>
internal sealed record LoadedDialogueSourceSnapshot(
    DialogueSourceIdentity SourceIdentity,
    string TranslationKey,
    string SourceText,
    string Locale,
    LoadedDialogueStackFacts CaptureFacts,
    object StackToken,
    object DialogueToken,
    object LineToken);

/// <summary>
/// authoritative 快照捕获的单一终态。失败时 <see cref="Snapshot"/> 永远为 null。
/// </summary>
internal sealed record LoadedDialogueSourceSnapshotResolution(
    bool IsCaptured,
    LoadedDialogueStackReasonCode ReasonCode,
    LoadedDialogueSourceSnapshot? Snapshot);

/// <summary>
/// 将同一次游戏对象读取原子化为可信 source snapshot。
/// </summary>
internal static class LoadedDialogueSourceSnapshotCapture
{
    /// <summary>
    /// 只有 exact source 与全部 shape gate 同时通过时才暴露三层对象 token。
    /// </summary>
    /// <param name="expectedNpcId">当前目标 NPC 的 exact 内部 ID。</param>
    /// <param name="actualTranslationKey">栈顶 Dialogue 的 actual TranslationKey。</param>
    /// <param name="sourceText">栈顶当前行的逐字符文本。</param>
    /// <param name="locale">捕获时 locale。</param>
    /// <param name="facts">与三个 token 同一次读取产生的不可变事实。</param>
    /// <param name="stackToken">实际 Stack 对象。</param>
    /// <param name="dialogueToken">实际 Dialogue 对象。</param>
    /// <param name="lineToken">实际 DialogueLine 对象。</param>
    public static LoadedDialogueSourceSnapshotResolution Capture(
        string expectedNpcId,
        string actualTranslationKey,
        string sourceText,
        string locale,
        LoadedDialogueStackFacts facts,
        object stackToken,
        object dialogueToken,
        object lineToken)
    {
        ArgumentNullException.ThrowIfNull(facts);

        DialogueSourceIdentity? sourceIdentity = DialogueSourceClassifier.ClassifyTranslationKey(
            actualTranslationKey,
            expectedNpcId);
        if (sourceIdentity is null)
        {
            return Rejected(LoadedDialogueStackReasonCode.UnsupportedDailySource);
        }

        // token 与原始字段任一缺失都会让后续 CAS 无法证明对象归属。不能返回部分 snapshot。
        if (string.IsNullOrEmpty(sourceText)
            || string.IsNullOrEmpty(locale)
            || stackToken is null
            || dialogueToken is null
            || lineToken is null)
        {
            return Rejected(LoadedDialogueStackReasonCode.UnsupportedDialogueShape);
        }

        LoadedDialogueStackDecision shapeDecision = LoadedDialogueStackPolicy.EvaluateCapture(facts);
        if (!shapeDecision.IsEligible)
        {
            return Rejected(shapeDecision.ReasonCode);
        }

        return new LoadedDialogueSourceSnapshotResolution(
            IsCaptured: true,
            ReasonCode: LoadedDialogueStackReasonCode.Eligible,
            Snapshot: new LoadedDialogueSourceSnapshot(
                sourceIdentity,
                actualTranslationKey,
                sourceText,
                locale,
                facts,
                stackToken,
                dialogueToken,
                lineToken));
    }

    private static LoadedDialogueSourceSnapshotResolution Rejected(
        LoadedDialogueStackReasonCode reasonCode)
    {
        return new LoadedDialogueSourceSnapshotResolution(
            IsCaptured: false,
            ReasonCode: reasonCode,
            Snapshot: null);
    }
}

/// <summary>
/// authoritative source 解析的稳定原因。
/// </summary>
internal enum AuthoritativeDialogueSourceReasonCode
{
    Resolved,
    SourceMissing,
    SourceTextMismatch,
    UnsafeSource,
    PendingActiveDialogueEvent,
    InsufficientStyleExamples,
}

/// <summary>
/// 已由 actual loaded snapshot、exact raw asset 与目标 NPC ordinary asset 共同证明的来源。
/// </summary>
internal sealed record AuthoritativeDialogueSource(
    DialogueSourceFamily Family,
    string NpcId,
    string Locale,
    string AssetName,
    string DialogueKey,
    string SourceText,
    string SourceHash,
    IReadOnlyList<string> StyleExamples);

/// <summary>
/// source resolve 的单一终态；失败不返回部分 source 或不足量样本。
/// </summary>
internal sealed record AuthoritativeDialogueSourceResolution(
    bool IsResolved,
    AuthoritativeDialogueSourceReasonCode ReasonCode,
    AuthoritativeDialogueSource? Source);

/// <summary>
/// 以同一 loaded snapshot 为起点解析 exact source，并始终从 NPC ordinary asset 取风格事实。
/// </summary>
internal static class AuthoritativeDialogueSourceResolver
{
    /// <summary>
    /// 解析已加载 ordinary/rainy source，不调用原版 RNG、日期链或 Dialogue 构造 API。
    /// </summary>
    /// <param name="snapshot">适配器捕获并通过 shape policy 的同一 authoritative snapshot。</param>
    /// <param name="sourceDialogueAsset">
    /// snapshot identity 指向的 exact asset；rainy 时是共享 rainy sheet。
    /// </param>
    /// <param name="npcOrdinaryDialogueAsset">
    /// 始终是 <c>Characters/Dialogue/&lt;NpcId&gt;</c>，只用于 pending event 与 style samples。
    /// </param>
    /// <param name="pendingDialogueEventKeys">玩家当前 pending active dialogue event keys。</param>
    /// <param name="currentSeason">捕获日的稳定季节。</param>
    /// <param name="currentHeartLevel">目标 NPC 当前 0～10 心关系阶段。</param>
    public static AuthoritativeDialogueSourceResolution Resolve(
        LoadedDialogueSourceSnapshot snapshot,
        IReadOnlyDictionary<string, string> sourceDialogueAsset,
        IReadOnlyDictionary<string, string> npcOrdinaryDialogueAsset,
        IReadOnlyCollection<string> pendingDialogueEventKeys,
        string currentSeason,
        int currentHeartLevel)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(sourceDialogueAsset);
        ArgumentNullException.ThrowIfNull(npcOrdinaryDialogueAsset);
        ArgumentNullException.ThrowIfNull(pendingDialogueEventKeys);

        if (!sourceDialogueAsset.TryGetValue(
                snapshot.SourceIdentity.DialogueKey,
                out string? currentRawSource))
        {
            return Rejected(AuthoritativeDialogueSourceReasonCode.SourceMissing);
        }

        // 逐字符正文比较与 hash 各有职责：正文比较防止仅 hash 身份掩盖错误绑定，hash 则进入
        // wire/cache identity。source asset 与 loaded line 只要不同就说明快照已不是当前权威值。
        if (!string.Equals(currentRawSource, snapshot.SourceText, StringComparison.Ordinal))
        {
            return Rejected(AuthoritativeDialogueSourceReasonCode.SourceTextMismatch);
        }

        if (!DialogueTemplatePolicy.TryParse(currentRawSource, out _))
        {
            return Rejected(AuthoritativeDialogueSourceReasonCode.UnsafeSource);
        }

        // 原版 active dialogue events 挂在角色自身 ordinary sheet；rainy 共享 asset 不能替代这个 gate。
        if (pendingDialogueEventKeys.Any(npcOrdinaryDialogueAsset.ContainsKey))
        {
            return Rejected(AuthoritativeDialogueSourceReasonCode.PendingActiveDialogueEvent);
        }

        StyleExampleSelectionResult styleSelection = StyleExampleSelector.Select(
            new DialogueStyleSelectionRequest
            {
                NpcId = snapshot.SourceIdentity.NpcId,
                Locale = snapshot.Locale,
                CurrentSeason = currentSeason,
                CurrentHeartLevel = Math.Clamp(currentHeartLevel, 0, 10),
                SourceKey = snapshot.SourceIdentity.DialogueKey,
                SourceText = currentRawSource,
                DialogueEntries = npcOrdinaryDialogueAsset,
            });
        if (!styleSelection.IsSuccessful)
        {
            return Rejected(AuthoritativeDialogueSourceReasonCode.InsufficientStyleExamples);
        }

        return new AuthoritativeDialogueSourceResolution(
            IsResolved: true,
            ReasonCode: AuthoritativeDialogueSourceReasonCode.Resolved,
            Source: new AuthoritativeDialogueSource(
                snapshot.SourceIdentity.Family,
                snapshot.SourceIdentity.NpcId,
                snapshot.Locale,
                snapshot.SourceIdentity.AssetName,
                snapshot.SourceIdentity.DialogueKey,
                currentRawSource,
                SourceDialogueHasher.Compute(currentRawSource),
                styleSelection.Examples));
    }

    private static AuthoritativeDialogueSourceResolution Rejected(
        AuthoritativeDialogueSourceReasonCode reasonCode)
    {
        return new AuthoritativeDialogueSourceResolution(
            IsResolved: false,
            ReasonCode: reasonCode,
            Source: null);
    }
}
