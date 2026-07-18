using StardewNpcAgent.Infrastructure.Storage;

namespace StardewNpcAgent.Game;

/// <summary>
/// 资产应用阶段的确定性 reason code。
/// </summary>
public enum DialogueAssetPatchReasonCode
{
    Applied,
    NoCacheEntry,
    InvalidCacheIdentity,
    CacheContextMismatch,
    SourceMissing,
    SourceTextMismatch,
    SourceHashMismatch,
    InvalidEnhancedTemplate,
    PlayerNameUnavailable,
}

/// <summary>
/// <c>AssetRequested</c> 发生时可确认的稳定上下文。
/// </summary>
/// <param name="GameDayIndex">当前绝对游戏日。</param>
/// <param name="Locale">当前 SMAPI locale。</param>
/// <param name="AssetName">当前被请求的精确资产名。</param>
public sealed record DialogueAssetPatchContext(
    int GameDayIndex,
    string Locale,
    string AssetName);

/// <summary>
/// 资产字典是否被本 policy 修改及其原因。
/// </summary>
/// <param name="WasApplied">是否恰好替换了目标 key。</param>
/// <param name="ReasonCode">应用或 fallback 原因。</param>
public sealed record DialogueAssetPatchDecision(
    bool WasApplied,
    DialogueAssetPatchReasonCode ReasonCode);

/// <summary>
/// 在 Late asset edit 中执行最终 source hash gate。
/// </summary>
/// <remarks>
/// Policy 在所有检查完成前不写字典。没有 cache、context 不符、source 被移除或被其他 Mod
/// 修改都是正常原版回退，而不是异常；尤其不会为了命中而创建已被删除的 key。
/// </remarks>
public static class DialogueAssetPatchPolicy
{
    /// <summary>
    /// 仅在完整 cache identity 与当前原文哈希都相符时替换精确字典项。
    /// </summary>
    /// <param name="dialogueAsset">SMAPI Late edit 提供的当前最终字典。</param>
    /// <param name="cacheEntry">与当前资产候选匹配的缓存项；允许为 null 表示无缓存。</param>
    /// <param name="context">当前日、locale 和资产。</param>
    /// <returns>明确的 applied/fallback 决策。</returns>
    public static DialogueAssetPatchDecision Apply(
        IDictionary<string, string> dialogueAsset,
        DailyDialogueCacheEntry? cacheEntry,
        DialogueAssetPatchContext context)
    {
        ArgumentNullException.ThrowIfNull(dialogueAsset);
        ArgumentNullException.ThrowIfNull(context);

        if (cacheEntry is null)
        {
            return Rejected(DialogueAssetPatchReasonCode.NoCacheEntry);
        }

        DailyDialogueCacheKey key = cacheEntry.Key;
        if (!DialogueSourceClassifier.MatchesIdentity(
                cacheEntry.SourceFamily,
                key.NpcId,
                key.AssetName,
                key.DialogueKey))
        {
            // Cache 是未来后端和内部编排都会写入的信任边界。即使 source hash 恰好匹配，
            // NPC ID、dialogue sheet 与 ordinary key 不能互相证明时也绝不允许 patch。
            return Rejected(DialogueAssetPatchReasonCode.InvalidCacheIdentity);
        }

        string normalizedContextAsset = DialogueKeyClassifier.NormalizeAssetName(context.AssetName);
        if (key.GameDayIndex != context.GameDayIndex
            || !string.Equals(key.Locale, context.Locale, StringComparison.Ordinal)
            || !string.Equals(key.AssetName, normalizedContextAsset, StringComparison.Ordinal))
        {
            return Rejected(DialogueAssetPatchReasonCode.CacheContextMismatch);
        }

        if (!dialogueAsset.TryGetValue(key.DialogueKey, out string? currentSource))
        {
            return Rejected(DialogueAssetPatchReasonCode.SourceMissing);
        }

        if (!string.Equals(currentSource, cacheEntry.SourceText, StringComparison.Ordinal))
        {
            return Rejected(DialogueAssetPatchReasonCode.SourceTextMismatch);
        }

        string currentHash = SourceDialogueHasher.Compute(currentSource);
        if (!string.Equals(currentHash, cacheEntry.SourceHash, StringComparison.Ordinal))
        {
            return Rejected(DialogueAssetPatchReasonCode.SourceHashMismatch);
        }

        if (!DialogueTemplatePolicy.TryParse(cacheEntry.EnhancedText, out _))
        {
            // 防止未来调用方绕过展示协调器直接使用 patch policy；正式生成文本必须在
            // 最后写字典前仍可表达为零或一个 typed 玩家名槽。
            return Rejected(DialogueAssetPatchReasonCode.InvalidEnhancedTemplate);
        }

        // 所有只读 gate 已完成后才执行唯一一次写入，确保任何失败路径都保持原字典。
        dialogueAsset[key.DialogueKey] = cacheEntry.EnhancedText;
        return new DialogueAssetPatchDecision(true, DialogueAssetPatchReasonCode.Applied);
    }

    /// <summary>
    /// 构造不修改资产的 fallback 结果。
    /// </summary>
    private static DialogueAssetPatchDecision Rejected(DialogueAssetPatchReasonCode reasonCode)
    {
        return new DialogueAssetPatchDecision(false, reasonCode);
    }
}
