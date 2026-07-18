namespace StardewNpcAgent.Game;

/// <summary>
/// 静态 enhancer 的明确终态。
/// </summary>
public enum StaticDialogueEnhancementReasonCode
{
    Enhanced,
    EmptySource,
    InvalidMarker,
    UnsafeControlSyntax,
}

/// <summary>
/// 静态文本增强结果；失败时不携带可展示文本。
/// </summary>
/// <param name="IsSuccessful">是否生成了安全静态文本。</param>
/// <param name="ReasonCode">成功或拒绝原因。</param>
/// <param name="EnhancedText">成功时为逐字符原文加标记，失败时为 null。</param>
public sealed record StaticDialogueEnhancementResult(
    bool IsSuccessful,
    StaticDialogueEnhancementReasonCode ReasonCode,
    string? EnhancedText);

/// <summary>
/// Phase 2 专用静态 enhancer，只证明缓存与原生显示链路，不接入模型。
/// </summary>
public static class StaticDialogueEnhancer
{
    /// <summary>
    /// 保留原文并追加可见标记；任何空值、歧义空白或 Stardew DSL 都 fail closed。
    /// </summary>
    /// <param name="sourceText">当前 locale 资产中的精确原文。</param>
    /// <param name="marker">用户配置的无 DSL 测试标记。</param>
    /// <returns>安全增强或确定性失败原因。</returns>
    public static StaticDialogueEnhancementResult TryEnhance(string? sourceText, string? marker)
    {
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            return Failed(StaticDialogueEnhancementReasonCode.EmptySource);
        }

        if (string.IsNullOrWhiteSpace(marker)
            || !string.Equals(marker, marker.Trim(), StringComparison.Ordinal))
        {
            return Failed(StaticDialogueEnhancementReasonCode.InvalidMarker);
        }

        if (!DialogueControlCommandScanner.Scan(sourceText).IsSafeForStaticAppend
            || !DialogueControlCommandScanner.Scan(marker).IsSafeForStaticAppend)
        {
            return Failed(StaticDialogueEnhancementReasonCode.UnsafeControlSyntax);
        }

        return new StaticDialogueEnhancementResult(
            true,
            StaticDialogueEnhancementReasonCode.Enhanced,
            $"{sourceText} {marker}");
    }

    /// <summary>
    /// 构造不携带文本的失败值，避免误把不安全原文写入 cache。
    /// </summary>
    private static StaticDialogueEnhancementResult Failed(
        StaticDialogueEnhancementReasonCode reasonCode)
    {
        return new StaticDialogueEnhancementResult(false, reasonCode, null);
    }
}
