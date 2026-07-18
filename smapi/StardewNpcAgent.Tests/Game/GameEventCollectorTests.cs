using StardewNpcAgent.Contracts;
using StardewNpcAgent.Game;

namespace StardewNpcAgent.Tests.Game;

/// <summary>
/// 验证首个正式事件只使用 SMAPI LevelChanged 的明确语义，不从 inventory/friendship 猜礼物。
/// </summary>
public sealed class GameEventCollectorTests
{
    [Fact]
    public void CollectLevelChanged_LocalIncreaseCreatesStablePublicProgressionEvent()
    {
        LevelChangedFact fact = new(
            IsLocalPlayer: true,
            OccurredDayIndex: 13,
            SkillName: "Mining",
            OldLevel: 4,
            NewLevel: 5);

        GameEvent first = Assert.IsType<GameEvent>(GameEventCollector.CollectLevelChanged(fact));
        GameEvent repeated = Assert.IsType<GameEvent>(GameEventCollector.CollectLevelChanged(fact));

        Assert.Equal(first.EventId, repeated.EventId);
        Assert.StartsWith("event-level-v1-", first.EventId, StringComparison.Ordinal);
        Assert.Equal("world_progression", first.EventType);
        Assert.Equal("1", first.EventVersion);
        Assert.Equal(13, first.OccurredDayIndex);
        Assert.Equal("smapi.player.level_changed", first.Source);
        Assert.Equal(AudienceScope.Public, first.AudienceScope);
        Assert.Null(first.AudienceNpcId);
        Assert.Equal("skill_mining_level_5", first.Payload.GetProperty("milestone").GetString());
        Assert.True(ContractValidator.Validate(first).IsValid);
    }

    [Theory]
    [InlineData(false, 4, 5)]
    [InlineData(true, 5, 5)]
    [InlineData(true, 6, 5)]
    public void CollectLevelChanged_RemoteOrNonIncreaseReturnsNull(
        bool isLocalPlayer,
        int oldLevel,
        int newLevel)
    {
        GameEvent? result = GameEventCollector.CollectLevelChanged(
            new LevelChangedFact(
                isLocalPlayer,
                OccurredDayIndex: 13,
                "Farming",
                oldLevel,
                newLevel));

        Assert.Null(result);
    }

    [Fact]
    public void CollectLevelChanged_InvalidDayFailsClosed()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => GameEventCollector.CollectLevelChanged(
                new LevelChangedFact(true, -1, "Fishing", 1, 2)));
    }
}
