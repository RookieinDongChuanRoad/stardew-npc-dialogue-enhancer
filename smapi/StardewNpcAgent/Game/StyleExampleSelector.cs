namespace StardewNpcAgent.Game;

/// <summary>
/// 风格样本选择终态 reason code。
/// </summary>
public enum StyleExampleSelectionReasonCode
{
    Selected,
    InvalidContext,
    InsufficientSafeExamples,
}

/// <summary>
/// 调用方从当前已加载 NPC dialogue asset 构建的样本选择请求。
/// </summary>
/// <remarks>
/// Selector 不读取全局 content manager，也不跨 NPC/locale 查找。<see cref="NpcId"/> 与
/// <see cref="Locale"/> 是显式来源标签；<see cref="DialogueEntries"/> 必须就是该来源的
/// 当前字典。这让纯逻辑测试可以冻结排序规则，也避免后台复制原版语料。
/// </remarks>
public sealed class DialogueStyleSelectionRequest
{
    public string NpcId { get; set; } = string.Empty;

    public string Locale { get; set; } = string.Empty;

    public string CurrentSeason { get; set; } = string.Empty;

    /// <summary>
    /// 当前玩家与 NPC 的可用心数阶段；用于排除更高心数 key，并优先接近当前阶段的样本。
    /// </summary>
    public int CurrentHeartLevel { get; set; }

    public string SourceKey { get; set; } = string.Empty;

    public string SourceText { get; set; } = string.Empty;

    public IReadOnlyDictionary<string, string>? DialogueEntries { get; set; }
}

/// <summary>
/// 确定性样本选择结果。
/// </summary>
/// <param name="IsSuccessful">是否获得合同要求的 2～5 条安全样本。</param>
/// <param name="ReasonCode">成功或失败原因。</param>
/// <param name="Examples">成功时 2～5 条；失败时始终为空。</param>
public sealed record StyleExampleSelectionResult(
    bool IsSuccessful,
    StyleExampleSelectionReasonCode ReasonCode,
    IReadOnlyList<string> Examples);

/// <summary>
/// 从同 NPC、当前 locale 的真实 dialogue 字典选择稳定风格样本。
/// </summary>
public static class StyleExampleSelector
{
    // 原版 Harvey/Penny 的双语言 ordinary sheet 在排除 source、token 与高心门槛后，
    // 数学上最多只剩两条独立样本。两条仍来自同 NPC/locale 且完整通过既有安全扫描；
    // 调整的是质量锚点数量，不放宽 DSL、关系或跨 NPC 来源边界。
    private const int MinimumExampleCount = 2;
    private const int MaximumExampleCount = 5;

    /// <summary>
    /// 过滤 source、特殊 key、重复文本和 DSL，再按季节、长度差、key 做稳定排序。
    /// </summary>
    /// <param name="request">带明确来源标签的已加载字典快照。</param>
    /// <returns>2～5 条样本；不足两条时不返回部分结果。</returns>
    public static StyleExampleSelectionResult Select(DialogueStyleSelectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.NpcId)
            || string.IsNullOrWhiteSpace(request.Locale)
            || string.IsNullOrWhiteSpace(request.CurrentSeason)
            || string.IsNullOrWhiteSpace(request.SourceKey)
            || string.IsNullOrWhiteSpace(request.SourceText)
            || request.CurrentHeartLevel is < 0 or > 10
            || request.DialogueEntries is null)
        {
            return Failed(StyleExampleSelectionReasonCode.InvalidContext);
        }

        string seasonPrefix = $"{request.CurrentSeason}_";
        HashSet<string> seenTexts = new(StringComparer.Ordinal)
        {
            request.SourceText,
        };

        List<StyleCandidate> candidates = new();
        foreach ((string key, string text) in request.DialogueEntries)
        {
            if (string.Equals(key, request.SourceKey, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(text)
                || !DialogueControlCommandScanner.Scan(text).IsSafeForStaticAppend)
            {
                continue;
            }

            if (!DialogueKeyClassifier.TryGetRequiredHeartLevel(key, out int requiredHeartLevel)
                || requiredHeartLevel > request.CurrentHeartLevel)
            {
                // 原版只在玩家达到门槛后才会选择该 key。把未解锁的高心台词当风格样本，
                // 会把亲密关系语气泄漏到低好感阶段，因此必须直接排除。
                continue;
            }

            // 只有候选通过 key 与关系门槛后才登记重复文本；否则一个被排除的高心 key
            // 可能错误地遮蔽内容相同、但当前关系阶段合法的基线 key。
            if (!seenTexts.Add(text))
            {
                continue;
            }

            candidates.Add(
                new StyleCandidate(
                    Key: key,
                    Text: text,
                    IsSameSeason: key.StartsWith(seasonPrefix, StringComparison.Ordinal),
                    RelationshipDistance: request.CurrentHeartLevel - requiredHeartLevel,
                    LengthDifference: Math.Abs(text.Length - request.SourceText.Length)));
        }

        IReadOnlyList<string> examples = candidates
            .OrderByDescending(candidate => candidate.IsSameSeason)
            .ThenBy(candidate => candidate.RelationshipDistance)
            .ThenBy(candidate => candidate.LengthDifference)
            .ThenBy(candidate => candidate.Key, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Text, StringComparer.Ordinal)
            .Take(MaximumExampleCount)
            .Select(candidate => candidate.Text)
            .ToArray();

        if (examples.Count < MinimumExampleCount)
        {
            return Failed(StyleExampleSelectionReasonCode.InsufficientSafeExamples);
        }

        return new StyleExampleSelectionResult(
            true,
            StyleExampleSelectionReasonCode.Selected,
            examples);
    }

    /// <summary>
    /// 失败结果永远不暴露不足量候选，避免调用方误把 1～2 条样本发送到后端。
    /// </summary>
    private static StyleExampleSelectionResult Failed(StyleExampleSelectionReasonCode reasonCode)
    {
        return new StyleExampleSelectionResult(false, reasonCode, Array.Empty<string>());
    }

    /// <summary>
    /// 排序专用不可变值，避免在 LINQ 链里重复计算季节和长度。
    /// </summary>
    private sealed record StyleCandidate(
        string Key,
        string Text,
        bool IsSameSeason,
        int RelationshipDistance,
        int LengthDifference);
}
