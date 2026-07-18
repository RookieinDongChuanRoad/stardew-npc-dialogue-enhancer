namespace StardewNpcAgent.Game;

/// <summary>
/// 当前版本允许增强的原版日常台词来源族。
/// </summary>
/// <remarks>
/// family 是 Mod 内部 trust identity，不进入公共 v1 wire。MarriageDialogue、事件、礼物、
/// 问题等来源不应通过增加枚举成员被顺带放开；它们需要独立设计和安全证明。
/// </remarks>
public enum DialogueSourceFamily
{
    /// <summary>目标 NPC 自身 ordinary dialogue sheet 中的有限 daily key。</summary>
    OrdinaryDaily,

    /// <summary>原版共享 rainy sheet 中以目标 NPC ID 为 exact key 的台词。</summary>
    RainyDaily,
}

/// <summary>
/// 已由本地规则独立证明的 exact dialogue source identity。
/// </summary>
/// <param name="Family">ordinary 或 rainy daily 来源族。</param>
/// <param name="NpcId">目标 NPC 的大小写敏感内部 ID。</param>
/// <param name="AssetName">只把反斜杠换成正斜杠后的 exact 资产名。</param>
/// <param name="DialogueKey">资产字典中的 exact key。</param>
public sealed record DialogueSourceIdentity(
    DialogueSourceFamily Family,
    string NpcId,
    string AssetName,
    string DialogueKey);

/// <summary>
/// 从游戏实际 <c>Dialogue.TranslationKey</c> 分类受支持的 ordinary/rainy daily 来源。
/// </summary>
/// <remarks>
/// 本分类器只允许 SMAPI/Stardew 资产路径的 <c>\</c> 到 <c>/</c> 等价；不会 Trim、
/// case-fold、Unicode normalize 或猜测相似 sheet/key。失败时返回 null，避免调用方持有
/// 半可信 asset/key 并在后续阶段误用。
/// </remarks>
public static class DialogueSourceClassifier
{
    private const string RainyDialogueAssetName = "Characters/Dialogue/rainy";

    /// <summary>
    /// 分类一个 exact TranslationKey。
    /// </summary>
    /// <param name="translationKey">游戏栈顶 Dialogue 暴露的 <c>asset:key</c> 原值。</param>
    /// <param name="expectedNpcId">当前处理目标的 exact NPC 内部 ID。</param>
    /// <returns>成功时返回完整 identity；任一不确定条件返回 null。</returns>
    public static DialogueSourceIdentity? ClassifyTranslationKey(
        string? translationKey,
        string? expectedNpcId)
    {
        if (string.IsNullOrEmpty(translationKey) || string.IsNullOrEmpty(expectedNpcId))
        {
            return null;
        }

        // 只接受一个分隔 asset/key 的冒号；多冒号来源无法无歧义映射回资产字典。
        int separatorIndex = translationKey.IndexOf(':');
        if (separatorIndex <= 0
            || separatorIndex != translationKey.LastIndexOf(':')
            || separatorIndex == translationKey.Length - 1)
        {
            return null;
        }

        string assetName = DialogueKeyClassifier.NormalizeAssetName(translationKey[..separatorIndex]);
        string dialogueKey = translationKey[(separatorIndex + 1)..];

        // rainy 是共享 sheet，只有 exact NPC key 才能证明该行属于目标 NPC。
        if (string.Equals(assetName, RainyDialogueAssetName, StringComparison.Ordinal))
        {
            return string.Equals(dialogueKey, expectedNpcId, StringComparison.Ordinal)
                ? new DialogueSourceIdentity(
                    DialogueSourceFamily.RainyDaily,
                    expectedNpcId,
                    assetName,
                    dialogueKey)
                : null;
        }

        // ordinary 继续复用已经审计的有限 key 白名单，避免在这里复制并漂移正则语义。
        DialogueKeyClassification ordinary = DialogueKeyClassifier.ClassifyTranslationKey(
            translationKey,
            expectedNpcId);
        if (!ordinary.IsOrdinaryDaily || ordinary.ParsedKey is null)
        {
            return null;
        }

        return new DialogueSourceIdentity(
            DialogueSourceFamily.OrdinaryDaily,
            ordinary.ParsedKey.NpcId,
            ordinary.ParsedKey.AssetName,
            ordinary.ParsedKey.DialogueKey);
    }

    /// <summary>
    /// 在独立 trust boundary 重新分类并逐字段核对已存储 source identity。
    /// </summary>
    /// <remarks>
    /// family 必须显式存储并比较，不能仅由 asset/key 临时推导后丢弃；同时要求 asset 已是
    /// classifier 返回的 canonical slash 形式，防止不同边界对路径字符串采用不同身份语义。
    /// </remarks>
    public static bool MatchesIdentity(
        DialogueSourceFamily family,
        string npcId,
        string assetName,
        string dialogueKey)
    {
        DialogueSourceIdentity? classified = ClassifyTranslationKey(
            $"{assetName}:{dialogueKey}",
            npcId);
        return classified is not null
            && classified.Family == family
            && string.Equals(classified.NpcId, npcId, StringComparison.Ordinal)
            && string.Equals(classified.AssetName, assetName, StringComparison.Ordinal)
            && string.Equals(classified.DialogueKey, dialogueKey, StringComparison.Ordinal);
    }
}
