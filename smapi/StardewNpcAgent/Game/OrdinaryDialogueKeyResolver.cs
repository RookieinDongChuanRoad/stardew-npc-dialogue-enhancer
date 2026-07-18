namespace StardewNpcAgent.Game;

/// <summary>
/// 纯 raw ordinary daily key 选择的终态。
/// </summary>
public enum OrdinaryDialogueKeySelectionReasonCode
{
    Selected,
    InvalidContext,
    UnsupportedNpc,
    NoMatchingKey,
}

/// <summary>
/// 从公开游戏事实与已加载 raw dialogue 字典构建的纯选择请求。
/// </summary>
/// <remarks>
/// 字段对应 Stardew 1.6.15 <c>NPC.tryToRetrieveDialogue</c> 的公开输入。该纯类型不构造
/// <c>Dialogue</c>，所以即使候选 raw text 含随机或 action 命令，也不会提前解析或执行。
/// </remarks>
public sealed class OrdinaryDialogueKeySelectionRequest
{
    public string NpcId { get; set; } = string.Empty;

    public IReadOnlyDictionary<string, string>? DialogueEntries { get; set; }

    public string Season { get; set; } = string.Empty;

    public int DayOfMonth { get; set; }

    public string ShortDayName { get; set; } = string.Empty;

    public int Year { get; set; }

    public int HeartLevel { get; set; }

    public string? SpouseId { get; set; }

    public bool HasCurrentOrPendingRoommate { get; set; }
}

/// <summary>
/// 纯 key 选择结果；成功时同时返回 exact raw source，供 scanner 在任何 Dialogue 构造前检查。
/// </summary>
/// <param name="IsSelected">是否按原版顺序找到一个 key。</param>
/// <param name="ReasonCode">成功或失败原因。</param>
/// <param name="DialogueKey">成功时的精确字典 key。</param>
/// <param name="SourceText">成功时的精确 raw 字典值。</param>
public sealed record OrdinaryDialogueKeySelectionResult(
    bool IsSelected,
    OrdinaryDialogueKeySelectionReasonCode ReasonCode,
    string? DialogueKey,
    string? SourceText);

/// <summary>
/// 对 Abigail 与 Sebastian 复现 Stardew 1.6.15 ordinary daily key 查找顺序。
/// </summary>
/// <remarks>
/// 当前只承诺两个 Phase 2 目标，是因为原版对 Penny 与 Caroline 存在角色专属替换分支；
/// 对未逐项验证的 NPC 直接返回 unsupported，优于构造一个可能含危险 DSL 的错误候选。
/// </remarks>
public static class OrdinaryDialogueKeyResolver
{
    private static readonly HashSet<string> SupportedNpcIds = new(
        new[] { "Abigail", "Sebastian" },
        StringComparer.Ordinal);

    private static readonly HashSet<string> Seasons = new(
        new[] { "spring", "summer", "fall", "winter" },
        StringComparer.Ordinal);

    private static readonly HashSet<string> ShortDayNames = new(
        new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" },
        StringComparer.Ordinal);

    /// <summary>
    /// 按“季节前缀，再无前缀 fallback”复现调用方的两段 ordinary daily 查找。
    /// </summary>
    /// <param name="request">当前日、关系与 raw 字典事实。</param>
    /// <returns>exact key/source；不支持或无匹配时不返回半可信候选。</returns>
    public static OrdinaryDialogueKeySelectionResult Select(
        OrdinaryDialogueKeySelectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!SupportedNpcIds.Contains(request.NpcId))
        {
            return Failed(OrdinaryDialogueKeySelectionReasonCode.UnsupportedNpc);
        }

        if (request.DialogueEntries is null
            || !Seasons.Contains(request.Season)
            || request.DayOfMonth is < 1 or > 28
            || !ShortDayNames.Contains(request.ShortDayName)
            || request.Year < 1
            || request.HeartLevel < 0)
        {
            return Failed(OrdinaryDialogueKeySelectionReasonCode.InvalidContext);
        }

        string? selectedKey = SelectForPreface(request, $"{request.Season}_");
        selectedKey ??= SelectForPreface(request, string.Empty);
        if (selectedKey is null
            || !request.DialogueEntries.TryGetValue(selectedKey, out string? sourceText))
        {
            return Failed(OrdinaryDialogueKeySelectionReasonCode.NoMatchingKey);
        }

        return new OrdinaryDialogueKeySelectionResult(
            true,
            OrdinaryDialogueKeySelectionReasonCode.Selected,
            selectedKey,
            sourceText);
    }

    /// <summary>
    /// 复现单次 preface 查找，包括玩家已有配偶时优先尝试 relationship suffix。
    /// </summary>
    private static string? SelectForPreface(
        OrdinaryDialogueKeySelectionRequest request,
        string preface)
    {
        if (!string.IsNullOrEmpty(request.SpouseId))
        {
            string relationshipSuffix = request.HasCurrentOrPendingRoommate
                ? $"_roommate_{request.SpouseId}"
                : $"_inlaw_{request.SpouseId}";
            string? relationshipKey = SelectCore(request, preface, relationshipSuffix);
            if (relationshipKey is not null)
            {
                return relationshipKey;
            }
        }

        return SelectCore(request, preface, string.Empty);
    }

    /// <summary>
    /// 复现日期、年份、wildcard、心数 weekday 和普通 weekday 的确切优先级。
    /// </summary>
    private static string? SelectCore(
        OrdinaryDialogueKeySelectionRequest request,
        string preface,
        string suffix)
    {
        IReadOnlyDictionary<string, string> entries = request.DialogueEntries!;
        int effectiveYear = Math.Min(request.Year, 2);

        if (effectiveYear == 1)
        {
            string firstYearDateKey = $"{preface}{request.DayOfMonth}{suffix}";
            if (entries.ContainsKey(firstYearDateKey))
            {
                return firstYearDateKey;
            }
        }

        string datedYearKey = $"{preface}{request.DayOfMonth}_{effectiveYear}{suffix}";
        if (entries.ContainsKey(datedYearKey))
        {
            return datedYearKey;
        }

        string datedWildcardKey = $"{preface}{request.DayOfMonth}_*{suffix}";
        if (entries.ContainsKey(datedWildcardKey))
        {
            return datedWildcardKey;
        }

        for (int requiredHearts = 10; requiredHearts >= 2; requiredHearts -= 2)
        {
            if (request.HeartLevel < requiredHearts)
            {
                continue;
            }

            string heartYearKey =
                $"{preface}{request.ShortDayName}{requiredHearts}_{effectiveYear}{suffix}";
            if (entries.ContainsKey(heartYearKey))
            {
                return heartYearKey;
            }

            string heartKey = $"{preface}{request.ShortDayName}{requiredHearts}{suffix}";
            if (entries.ContainsKey(heartKey))
            {
                return heartKey;
            }
        }

        string weekdayKey = $"{preface}{request.ShortDayName}{suffix}";
        if (!entries.ContainsKey(weekdayKey))
        {
            return null;
        }

        string weekdayYearKey =
            $"{preface}{request.ShortDayName}_{effectiveYear}{suffix}";
        return entries.ContainsKey(weekdayYearKey) ? weekdayYearKey : weekdayKey;
    }

    /// <summary>
    /// 构造不携带 key/source 的失败值。
    /// </summary>
    private static OrdinaryDialogueKeySelectionResult Failed(
        OrdinaryDialogueKeySelectionReasonCode reasonCode)
    {
        return new OrdinaryDialogueKeySelectionResult(false, reasonCode, null, null);
    }
}
