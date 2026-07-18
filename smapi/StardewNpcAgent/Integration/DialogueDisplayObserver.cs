using StardewNpcAgent.Application;
using StardewNpcAgent.Game;

namespace StardewNpcAgent.Integration;

/// <summary>
/// 从公开原生对话菜单提取、供纯观察器核对的最小菜单快照。
/// </summary>
public sealed record DialogueMenuSnapshot(
    object MenuIdentity,
    string NpcId,
    string TranslationKey,
    string DisplayedText);

/// <summary>
/// 首次 RenderedActiveMenu 已确认、但尚未完成 durable ACK 入队的一次观察。
/// </summary>
public sealed class RenderedDialogueObservation
{
    internal RenderedDialogueObservation(
        DialogueDisplayDecision decision,
        DisplayedDialogueConfirmation confirmation)
    {
        Decision = decision;
        Confirmation = confirmation;
    }

    public DialogueDisplayDecision Decision { get; }

    public DisplayedDialogueConfirmation Confirmation { get; }
}

/// <summary>
/// observer 已完成本地 template render、但尚未确认注入成功的一次短生命周期准备结果。
/// </summary>
/// <remarks>
/// 本类型只在同一主线程同步调用链中从 Prepare 传到 Commit。它不进入 cache、HTTP、outbox、
/// receipt 或日志；其中的 expected text 可能含真实玩家名，因此保持 internal 且不提供序列化。
/// </remarks>
internal sealed record PreparedDialogueArm(
    DialogueDisplayDecision Decision,
    string TranslationKey,
    Infrastructure.Storage.DailyDialogueCacheKey CacheKey,
    DialogueSourceFamily SourceFamily,
    string SourceText,
    string SourceHash,
    string RawTemplate,
    string ExpectedRenderedText);

/// <summary>
/// 将 Late patch 的 opaque decision 与之后真正打开并绘制的原生菜单关联。
/// </summary>
/// <remarks>
/// MenuChanged 只建立关联，不 ACK；RenderedActiveMenu 才返回 observation。调用方只有在
/// `RecordDisplayed` 成功或得到 duplicate 后调用 <see cref="Complete"/>，因此本地文件写失败时
/// token 会保留并可在下一帧重试，而不会阻止已经显示的 UI。
/// </remarks>
public sealed class DialogueDisplayObserver
{
    private readonly Dictionary<string, ArmedDialogue> armedByTranslationKey =
        new(StringComparer.Ordinal);
    private ActiveDialogue? active;

    public int ArmedCount => armedByTranslationKey.Count;

    /// <summary>
    /// 兼容无玩家名槽的既有调用。含槽 decision 必须使用带过滤后名字的准备路径。
    /// </summary>
    public void Arm(DialogueDisplayDecision decision)
    {
        PreparedDialogueArm? prepared = PrepareArm(decision, filteredPlayerName: null);
        if (prepared is null)
        {
            throw new ArgumentException("玩家名槽缺少可用的本地过滤后名字。", nameof(decision));
        }

        CommitArm(prepared);
    }

    /// <summary>
    /// 便捷完成一次本地 render 与 arm；主要供不需要与注入事务分离的测试/调用点使用。
    /// </summary>
    /// <param name="decision">正式 generated opaque decision。</param>
    /// <param name="filteredPlayerName">主线程经原版 Utility 过滤的短生命周期名字。</param>
    /// <returns>无槽或名字可用且已 arm 时为 true；名字缺失的含槽模板返回 false。</returns>
    public bool TryArm(DialogueDisplayDecision decision, string? filteredPlayerName)
    {
        PreparedDialogueArm? prepared = PrepareArm(decision, filteredPlayerName);
        if (prepared is null)
        {
            return false;
        }

        CommitArm(prepared);
        return true;
    }

    /// <summary>
    /// 在任何游戏文本写入前验证 decision，并以一次提供的本地名字计算 exact UI 文本。
    /// </summary>
    /// <remarks>
    /// asset/direct 注入先调用本方法，只有准备成功才执行 CAS，随后再 <see cref="CommitArm"/>。
    /// 这样名字不可用时可以原版回退，也不会留下“未实际注入却已 armed”的幽灵 token。
    /// </remarks>
    internal PreparedDialogueArm? PrepareArm(
        DialogueDisplayDecision decision,
        string? filteredPlayerName)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (decision.Kind != DialogueDisplayDecisionKind.UseGenerated
            || decision.CacheKey is null
            || decision.SourceFamily is null
            || decision.SourceText is null
            || decision.SourceHash is null
            || decision.EnhancedText is null
            || !DialogueSourceClassifier.MatchesIdentity(
                decision.SourceFamily.Value,
                decision.CacheKey.NpcId,
                decision.CacheKey.AssetName,
                decision.CacheKey.DialogueKey)
            || !string.Equals(
                SourceDialogueHasher.Compute(decision.SourceText),
                decision.SourceHash,
                StringComparison.Ordinal))
        {
            throw new ArgumentException("只有正式 generated decision 可以 arm。", nameof(decision));
        }

        if (!DialogueTemplatePolicy.TryParse(
                decision.EnhancedText,
                out DialogueTextTemplate? template))
        {
            throw new ArgumentException("generated decision 不符合 typed template policy。", nameof(decision));
        }

        if (!DialogueTemplatePolicy.TryRenderForDisplay(
                template!,
                filteredPlayerName,
                out string? expectedRenderedText))
        {
            // 这里唯一的正常失败是含 player_name slot 但当前本地名字不可用。调用方必须
            // 在写游戏文本前看到 false/null，并让该 NPC 保持原版。
            return null;
        }

        string translationKey = CreateTranslationKey(
            decision.CacheKey.AssetName,
            decision.CacheKey.DialogueKey);
        return new PreparedDialogueArm(
            decision,
            translationKey,
            decision.CacheKey,
            decision.SourceFamily.Value,
            decision.SourceText,
            decision.SourceHash,
            decision.EnhancedText,
            expectedRenderedText!);
    }

    /// <summary>
    /// 只有调用方已经成功 patch/CAS 游戏文本后，才把准备结果加入菜单观察表。
    /// </summary>
    internal void CommitArm(PreparedDialogueArm prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        armedByTranslationKey[prepared.TranslationKey] = new ArmedDialogue(
            prepared.Decision,
            prepared.TranslationKey,
            prepared.CacheKey,
            prepared.SourceFamily,
            prepared.SourceText,
            prepared.SourceHash,
            prepared.RawTemplate,
            prepared.ExpectedRenderedText);
    }

    public void ObserveMenu(
        DialogueMenuSnapshot? snapshot,
        int currentDayIndex,
        string currentLocale)
    {
        active = null;
        if (snapshot is null
            || currentDayIndex < 0
            || string.IsNullOrWhiteSpace(currentLocale))
        {
            return;
        }

        // 缓存资产名使用 SMAPI 的正斜杠，而 Stardew 原生 Dialogue.TranslationKey
        // 通常使用反斜杠。这里先经过 ordinary-daily 分类器解析，再以规范化身份查找；
        // 不能直接 Replace 后查找，否则畸形路径、特殊 key 或错误 NPC 可能被意外放宽。
        DialogueSourceIdentity? currentSourceIdentity = DialogueSourceClassifier.ClassifyTranslationKey(
            snapshot.TranslationKey,
            snapshot.NpcId);
        if (currentSourceIdentity is null)
        {
            return;
        }

        string translationKey = CreateTranslationKey(
            currentSourceIdentity.AssetName,
            currentSourceIdentity.DialogueKey);
        if (!armedByTranslationKey.TryGetValue(translationKey, out ArmedDialogue? armed))
        {
            // 不同 NPC/key 的菜单与本 token 无关，不能消费之后仍可能正确显示的结果。
            return;
        }

        if (armed.CacheKey.GameDayIndex != currentDayIndex
            || !string.Equals(armed.CacheKey.Locale, currentLocale, StringComparison.Ordinal)
            || !MatchesArmedDialogue(snapshot, armed))
        {
            // exact target key 已出现却不是预期 day/locale/text，已经足以证明本次 token
            // 没有驱动该菜单。永久消费，避免未来同 key 文本偶然相等时误 ACK。
            armedByTranslationKey.Remove(translationKey);
            return;
        }

        active = new ActiveDialogue(snapshot.MenuIdentity, armed);
    }

    public RenderedDialogueObservation? TryObserveRendered(
        DialogueMenuSnapshot currentSnapshot,
        int currentDayIndex,
        string currentLocale)
    {
        ArgumentNullException.ThrowIfNull(currentSnapshot);
        if (active is null || !ReferenceEquals(active.MenuIdentity, currentSnapshot.MenuIdentity))
        {
            return null;
        }

        if (active.Observation is not null)
        {
            // 首个 exact frame 已证明“至少展示一次”。后续帧变化不能撤销该事实；尤其
            // durable outbox 首次写失败时，下一帧仍应能重试同一个 observation。
            return active.Observation;
        }

        if (active.Armed.CacheKey.GameDayIndex != currentDayIndex
            || !string.Equals(
                active.Armed.CacheKey.Locale,
                currentLocale,
                StringComparison.Ordinal)
            || !MatchesArmedDialogue(currentSnapshot, active.Armed))
        {
            // 同一个目标菜单的首帧已不再匹配，永久消费 token。新的同 key 菜单不能把
            // 这次失败重新解释成成功，也不能产生 ACK。
            armedByTranslationKey.Remove(active.Armed.TranslationKey);
            active = null;
            return null;
        }

        active.Observation ??= new RenderedDialogueObservation(
            active.Armed.Decision,
            new DisplayedDialogueConfirmation(
                WasActuallyDisplayed: true,
                DisplayedDayIndex: active.Armed.CacheKey.GameDayIndex,
                NpcId: active.Armed.CacheKey.NpcId,
                SourceHash: active.Armed.SourceHash));
        return active.Observation;
    }

    public void Complete(RenderedDialogueObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (active?.Observation is null || !ReferenceEquals(active.Observation, observation))
        {
            // 真正的 decision owner 授权由 RecordDisplayed 再检查；这里仅拒绝旧菜单观察。
            throw new ArgumentException("rendered observation 不属于当前 active token。", nameof(observation));
        }

        armedByTranslationKey.Remove(active.Armed.TranslationKey);
        active = null;
    }

    public void Clear()
    {
        active = null;
        armedByTranslationKey.Clear();
    }

    private static string CreateTranslationKey(string assetName, string dialogueKey)
    {
        return $"{assetName}:{dialogueKey}";
    }

    /// <summary>
    /// MenuChanged 与首次 RenderedActiveMenu 必须使用同一组 speaker/key/text 身份。
    /// </summary>
    private static bool MatchesArmedDialogue(
        DialogueMenuSnapshot snapshot,
        ArmedDialogue armed)
    {
        DialogueSourceIdentity? current = DialogueSourceClassifier.ClassifyTranslationKey(
            snapshot.TranslationKey,
            snapshot.NpcId);
        return current is not null
            && current.Family == armed.SourceFamily
            && string.Equals(armed.CacheKey.NpcId, snapshot.NpcId, StringComparison.Ordinal)
            && string.Equals(current.AssetName, armed.CacheKey.AssetName, StringComparison.Ordinal)
            && string.Equals(current.DialogueKey, armed.CacheKey.DialogueKey, StringComparison.Ordinal)
            && string.Equals(
                armed.ExpectedRenderedText,
                snapshot.DisplayedText,
                StringComparison.Ordinal);
    }

    private sealed record ArmedDialogue(
        DialogueDisplayDecision Decision,
        string TranslationKey,
        Infrastructure.Storage.DailyDialogueCacheKey CacheKey,
        DialogueSourceFamily SourceFamily,
        string SourceText,
        string SourceHash,
        string RawTemplate,
        string ExpectedRenderedText);

    private sealed class ActiveDialogue
    {
        public ActiveDialogue(object menuIdentity, ArmedDialogue armed)
        {
            MenuIdentity = menuIdentity;
            Armed = armed;
        }

        public object MenuIdentity { get; }

        public ArmedDialogue Armed { get; }

        public RenderedDialogueObservation? Observation { get; set; }
    }
}
