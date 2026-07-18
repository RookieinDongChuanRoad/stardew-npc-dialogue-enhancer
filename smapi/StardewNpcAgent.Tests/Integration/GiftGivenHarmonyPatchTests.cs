using StardewNpcAgent.Contracts;
using StardewNpcAgent.Game;
using StardewNpcAgent.Integration;

namespace StardewNpcAgent.Tests.Integration;

/// <summary>
/// 验证 accepted-gift 纯映射与唯一 Harmony 文件的只观察实现边界。
/// </summary>
public sealed class GiftGivenHarmonyPatchTests
{
    [Theory]
    [InlineData("love")]
    [InlineData("like")]
    [InlineData("neutral")]
    [InlineData("dislike")]
    [InlineData("hate")]
    [InlineData("stardrop_tea")]
    public void CollectGiftGiven_SixFrozenTastesProduceNpcPrivateV2Fact(string taste)
    {
        GiftGivenFact fact = new(
            IsLocalPlayer: true,
            OccurredDayIndex: 13,
            NpcId: "Abigail",
            QualifiedItemId: "(O)74",
            Taste: taste,
            DailyGiftOrdinal: 1);

        GameEvent first = Assert.IsType<GameEvent>(NpcHistoryEventCollector.CollectGiftGiven(fact));
        GameEvent replay = Assert.IsType<GameEvent>(NpcHistoryEventCollector.CollectGiftGiven(fact));

        Assert.Equal(first.EventId, replay.EventId);
        Assert.StartsWith("event-gift-given-v2-", first.EventId, StringComparison.Ordinal);
        Assert.Equal("gift_given", first.EventType);
        Assert.Equal("2", first.EventVersion);
        Assert.Equal("harmony.farmer.on_gift_given", first.Source);
        Assert.Equal(AudienceScope.Npc, first.AudienceScope);
        Assert.Equal("Abigail", first.AudienceNpcId);
        Assert.Equal("(O)74", first.Payload.GetProperty("item_id").GetString());
        Assert.Equal(taste, first.Payload.GetProperty("taste").GetString());
        Assert.Equal(2, first.Payload.EnumerateObject().Count());
        Assert.True(ContractValidator.Validate(first).IsValid);
    }

    [Fact]
    public void CollectGiftGiven_DailyOrdinalSeparatesSameNpcAndItemWithoutDelimiterIdentity()
    {
        GiftGivenFact firstFact = new(true, 13, "Abigail", "(O)74", "love", 1);
        GiftGivenFact secondFact = firstFact with { DailyGiftOrdinal = 2 };

        GameEvent first = Assert.IsType<GameEvent>(NpcHistoryEventCollector.CollectGiftGiven(firstFact));
        GameEvent second = Assert.IsType<GameEvent>(NpcHistoryEventCollector.CollectGiftGiven(secondFact));

        Assert.NotEqual(first.EventId, second.EventId);
    }

    [Theory]
    [InlineData(false, "love", 1)]
    [InlineData(true, "unknown", 1)]
    public void CollectGiftGiven_RemoteOrUnknownTasteReturnsNull(
        bool isLocalPlayer,
        string taste,
        int ordinal)
    {
        Assert.Null(
            NpcHistoryEventCollector.CollectGiftGiven(
                new GiftGivenFact(
                    isLocalPlayer,
                    13,
                    "Abigail",
                    "(O)74",
                    taste,
                    ordinal)));
    }

    [Fact]
    public void CollectGiftGiven_InvalidDayItemOrOrdinalFailsClosed()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => NpcHistoryEventCollector.CollectGiftGiven(
                new GiftGivenFact(true, -1, "Abigail", "(O)74", "love", 1)));
        Assert.Throws<ArgumentException>(
            () => NpcHistoryEventCollector.CollectGiftGiven(
                new GiftGivenFact(true, 13, "Abigail", " ", "love", 1)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => NpcHistoryEventCollector.CollectGiftGiven(
                new GiftGivenFact(true, 13, "Abigail", "(O)74", "love", 0)));
    }

    [Fact]
    public void PatchSource_AuditsBeforeInstallAndContainsOnlyReadOnlyObservationCalls()
    {
        string sourcePath = Path.Combine(
            FindProjectDirectory(),
            "Integration",
            "GiftGivenHarmonyPatch.cs");
        string source = File.ReadAllText(sourcePath);

        Assert.Contains("[HarmonyPostfix]", source, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(source, "[HarmonyPostfix]"));
        Assert.Contains("Farmer.onGiftGiven", source, StringComparison.Ordinal);
        Assert.Contains("getGiftTasteForThisItem", source, StringComparison.Ordinal);
        Assert.Contains("QualifiedItemId", source, StringComparison.Ordinal);
        Assert.Contains("GiftsToday + 1", source, StringComparison.Ordinal);
        Assert.Contains("try", source, StringComparison.Ordinal);
        Assert.Contains("catch", source, StringComparison.Ordinal);
        Assert.True(
            source.IndexOf("GiftGivenCompatibilityAudit.Audit", StringComparison.Ordinal)
                < source.IndexOf("CreateClassProcessor", StringComparison.Ordinal),
            "必须先通过完整程序集审计，之后才允许 Harmony 安装 patch。");

        Assert.DoesNotContain("changeFriendship", source, StringComparison.Ordinal);
        Assert.DoesNotContain("removeItemFromInventory", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".receiveGift(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Dialogue", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Random", source, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string source, string value)
    {
        return source.Split(value, StringSplitOptions.None).Length - 1;
    }

    private static string FindProjectDirectory()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "StardewNpcAgent.sln")))
        {
            current = current.Parent;
        }

        Assert.NotNull(current);
        return Path.Combine(current!.FullName, "StardewNpcAgent");
    }
}
