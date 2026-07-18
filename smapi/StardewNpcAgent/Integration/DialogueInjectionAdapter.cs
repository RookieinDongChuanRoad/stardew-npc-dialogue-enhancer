using StardewNpcAgent.Application;
using StardewNpcAgent.Game;
using StardewNpcAgent.Infrastructure.Storage;

namespace StardewNpcAgent.Integration;

/// <summary>
/// 一次 asset dictionary 注入的逐项决策与应用数量。
/// </summary>
public sealed record DialogueInjectionResult(
    int AppliedCount,
    IReadOnlyList<DialogueAssetPatchDecision> Decisions);

/// <summary>
/// 在 SMAPI Late asset edit 内把正式 live cache 精确 patch 到原版 dialogue dictionary。
/// </summary>
/// <remarks>
/// Adapter 不显示 UI、不拦截输入。它先用 <see cref="DialogueDisplayCoordinator.Resolve"/>
/// 对当前原文重算 hash 并取得 opaque decision，再复用 <see cref="DialogueAssetPatchPolicy"/>
/// 做最终字典写入；只有真实 applied 后才 arm displayed observer。
/// </remarks>
public static class DialogueInjectionAdapter
{
    public static DialogueInjectionResult Apply(
        IDictionary<string, string> dialogueAsset,
        string assetName,
        int gameDayIndex,
        string locale,
        IReadOnlyList<DailyDialogueCacheEntry> entries,
        DialogueDisplayCoordinator displayCoordinator,
        DialogueDisplayObserver displayObserver,
        string? filteredPlayerName = null)
    {
        ArgumentNullException.ThrowIfNull(dialogueAsset);
        ArgumentNullException.ThrowIfNull(assetName);
        ArgumentNullException.ThrowIfNull(locale);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(displayCoordinator);
        ArgumentNullException.ThrowIfNull(displayObserver);

        string normalizedAssetName = DialogueKeyClassifier.NormalizeAssetName(assetName);
        DialogueAssetPatchContext patchContext = new(
            gameDayIndex,
            locale,
            normalizedAssetName);
        List<DialogueAssetPatchDecision> decisions = new();
        int appliedCount = 0;
        foreach (DailyDialogueCacheEntry entry in entries)
        {
            if (!entry.HasCompleteGenerationMetadata)
            {
                decisions.Add(
                    new DialogueAssetPatchDecision(
                        false,
                        DialogueAssetPatchReasonCode.InvalidCacheIdentity));
                continue;
            }

            if (!dialogueAsset.TryGetValue(entry.Key.DialogueKey, out string? currentSource))
            {
                decisions.Add(
                    new DialogueAssetPatchDecision(
                        false,
                        DialogueAssetPatchReasonCode.SourceMissing));
                continue;
            }

            DialogueDisplayDecision displayDecision = displayCoordinator.Resolve(
                new DialogueDisplayContext(
                    gameDayIndex,
                    locale,
                    entry.Key.NpcId,
                    normalizedAssetName,
                    entry.Key.DialogueKey,
                    currentSource));
            if (displayDecision.Kind != DialogueDisplayDecisionKind.UseGenerated)
            {
                decisions.Add(
                    new DialogueAssetPatchDecision(
                        false,
                        DialogueAssetPatchReasonCode.NoCacheEntry));
                continue;
            }


            // observer 是唯一把 raw @ 与本地名字组合成 expected UI 文本的边界。
            // 名字不可用时在字典写入前回退，绝不暂时 patch 后再猜测是否可观察。
            PreparedDialogueArm? preparedArm = displayObserver.PrepareArm(
                displayDecision,
                filteredPlayerName);
            if (preparedArm is null)
            {
                decisions.Add(
                    new DialogueAssetPatchDecision(
                        false,
                        DialogueAssetPatchReasonCode.PlayerNameUnavailable));
                continue;
            }

            DialogueAssetPatchDecision patchDecision = DialogueAssetPatchPolicy.Apply(
                dialogueAsset,
                entry,
                patchContext);
            decisions.Add(patchDecision);
            if (patchDecision.WasApplied)
            {
                displayObserver.CommitArm(preparedArm);
                appliedCount++;
            }
        }

        return new DialogueInjectionResult(
            appliedCount,
            Array.AsReadOnly(decisions.ToArray()));
    }
}
