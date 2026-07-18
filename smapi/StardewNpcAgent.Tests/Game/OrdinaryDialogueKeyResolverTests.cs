using StardewNpcAgent.Game;

namespace StardewNpcAgent.Tests.Game;

/// <summary>
/// 冻结 Abigail/Sebastian 原版 ordinary daily key 查找顺序，避免为预检而构造 Dialogue。
/// </summary>
public sealed class OrdinaryDialogueKeyResolverTests
{
    /// <summary>
    /// 原版同季节前缀中，指定日期/年份优先于 weekday 与 heart fallback。
    /// </summary>
    [Fact]
    public void Select_PrefersSeasonalDateAndYearBeforeWeekday()
    {
        OrdinaryDialogueKeySelectionRequest request = CreateRequest(
            new Dictionary<string, string>
            {
                ["spring_Mon10_2"] = "weekday heart",
                ["spring_Mon"] = "weekday",
                ["spring_15_2"] = "exact date and year",
                ["Mon"] = "unprefixed fallback",
            });

        OrdinaryDialogueKeySelectionResult result = OrdinaryDialogueKeyResolver.Select(request);

        Assert.True(result.IsSelected, result.ReasonCode.ToString());
        Assert.Equal("spring_15_2", result.DialogueKey);
        Assert.Equal("exact date and year", result.SourceText);
    }

    /// <summary>
    /// 没有日期 key 时，从不高于当前心数的最高偶数门槛向下查找，并优先年份变体。
    /// </summary>
    [Fact]
    public void Select_UsesHighestUnlockedHeartAndYearOverride()
    {
        OrdinaryDialogueKeySelectionRequest request = CreateRequest(
            new Dictionary<string, string>
            {
                ["spring_Mon2"] = "two hearts",
                ["spring_Mon4"] = "four hearts",
                ["spring_Mon4_2"] = "four hearts year two",
                ["spring_Mon6"] = "locked six hearts",
            });
        request.HeartLevel = 5;

        OrdinaryDialogueKeySelectionResult result = OrdinaryDialogueKeyResolver.Select(request);

        Assert.True(result.IsSelected, result.ReasonCode.ToString());
        Assert.Equal("spring_Mon4_2", result.DialogueKey);
        Assert.Equal("four hearts year two", result.SourceText);
    }

    /// <summary>
    /// 玩家已有配偶时，原版先尝试 in-law/roommate 后缀；返回该 special key 让上层拒绝。
    /// </summary>
    [Theory]
    [InlineData(false, "spring_Mon4_inlaw_Leah")]
    [InlineData(true, "spring_Mon4_roommate_Krobus")]
    public void Select_PreservesSpouseSuffixPriorityForFailClosedClassification(
        bool hasRoommate,
        string expectedKey)
    {
        OrdinaryDialogueKeySelectionRequest request = CreateRequest(
            new Dictionary<string, string>
            {
                ["spring_Mon4"] = "ordinary",
                [expectedKey] = "relationship special",
            });
        request.SpouseId = hasRoommate ? "Krobus" : "Leah";
        request.HasCurrentOrPendingRoommate = hasRoommate;
        request.HeartLevel = 4;

        OrdinaryDialogueKeySelectionResult result = OrdinaryDialogueKeyResolver.Select(request);

        Assert.True(result.IsSelected, result.ReasonCode.ToString());
        Assert.Equal(expectedKey, result.DialogueKey);
        Assert.False(DialogueKeyClassifier.IsOrdinaryDailyKey(result.DialogueKey));
    }

    /// <summary>
    /// 季节前缀完全无匹配时才进入无前缀 fallback。
    /// </summary>
    [Fact]
    public void Select_FallsBackToUnprefixedDailyKey()
    {
        OrdinaryDialogueKeySelectionRequest request = CreateRequest(
            new Dictionary<string, string> { ["Mon"] = "unprefixed" });

        OrdinaryDialogueKeySelectionResult result = OrdinaryDialogueKeyResolver.Select(request);

        Assert.True(result.IsSelected, result.ReasonCode.ToString());
        Assert.Equal("Mon", result.DialogueKey);
    }

    /// <summary>
    /// 当前纯规则只对本 Spike 要验收的两个 NPC 承诺与 1.6.15 特例一致；其他 NPC 不猜。
    /// </summary>
    [Fact]
    public void Select_RejectsNpcOutsideValidatedSpikeScope()
    {
        OrdinaryDialogueKeySelectionRequest request = CreateRequest(
            new Dictionary<string, string> { ["spring_Mon"] = "Penny special risk" });
        request.NpcId = "Penny";

        OrdinaryDialogueKeySelectionResult result = OrdinaryDialogueKeyResolver.Select(request);

        Assert.False(result.IsSelected);
        Assert.Equal(OrdinaryDialogueKeySelectionReasonCode.UnsupportedNpc, result.ReasonCode);
    }

    /// <summary>
    /// 构建与反编译的 Y2 春 15 周一状态一致的默认请求。
    /// </summary>
    private static OrdinaryDialogueKeySelectionRequest CreateRequest(
        IReadOnlyDictionary<string, string> entries)
    {
        return new OrdinaryDialogueKeySelectionRequest
        {
            NpcId = "Abigail",
            DialogueEntries = entries,
            Season = "spring",
            DayOfMonth = 15,
            ShortDayName = "Mon",
            Year = 2,
            HeartLevel = 10,
        };
    }
}
