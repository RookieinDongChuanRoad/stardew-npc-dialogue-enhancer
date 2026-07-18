using StardewNpcAgent.Contracts;
using StardewNpcAgent.Game;

namespace StardewNpcAgent.Tests.Game;

/// <summary>
/// 验证当前目标 NPC 私人关系历史的纯事件映射，不推断礼物、原因或未注册跳变。
/// </summary>
public sealed class NpcHistoryEventCollectorTests
{
    [Theory]
    [InlineData(RelationshipStatus.Friendly, RelationshipStatus.Dating, "friendly", "dating")]
    [InlineData(RelationshipStatus.Dating, RelationshipStatus.Engaged, "dating", "engaged")]
    [InlineData(RelationshipStatus.Engaged, RelationshipStatus.Married, "engaged", "married")]
    [InlineData(RelationshipStatus.Dating, RelationshipStatus.Friendly, "dating", "friendly")]
    [InlineData(RelationshipStatus.Engaged, RelationshipStatus.Friendly, "engaged", "friendly")]
    [InlineData(RelationshipStatus.Married, RelationshipStatus.Divorced, "married", "divorced")]
    public void CollectRelationshipStatusChanged_FrozenTransitionsProduceNpcPrivateFact(
        RelationshipStatus oldStatus,
        RelationshipStatus newStatus,
        string expectedOldStatus,
        string expectedNewStatus)
    {
        RelationshipStatusChangedFact fact = new(
            IsLocalPlayer: true,
            OccurredDayIndex: 13,
            NpcId: "Abigail",
            OldStatus: oldStatus,
            NewStatus: newStatus);

        GameEvent first = Assert.IsType<GameEvent>(
            NpcHistoryEventCollector.CollectRelationshipStatusChanged(fact));
        GameEvent replay = Assert.IsType<GameEvent>(
            NpcHistoryEventCollector.CollectRelationshipStatusChanged(fact));

        Assert.Equal(first.EventId, replay.EventId);
        Assert.StartsWith("event-relationship-status-changed-v1-", first.EventId, StringComparison.Ordinal);
        Assert.Equal("relationship_status_changed", first.EventType);
        Assert.Equal("1", first.EventVersion);
        Assert.Equal(13, first.OccurredDayIndex);
        Assert.Equal("smapi.player.friendship_snapshot", first.Source);
        Assert.Equal(AudienceScope.Npc, first.AudienceScope);
        Assert.Equal("Abigail", first.AudienceNpcId);
        Assert.Equal(expectedOldStatus, first.Payload.GetProperty("old_status").GetString());
        Assert.Equal(expectedNewStatus, first.Payload.GetProperty("new_status").GetString());
        Assert.Equal(2, first.Payload.EnumerateObject().Count());
        Assert.True(ContractValidator.Validate(first).IsValid);
    }

    [Theory]
    [InlineData(RelationshipStatus.Divorced, RelationshipStatus.Friendly)]
    [InlineData(RelationshipStatus.Friendly, RelationshipStatus.Married)]
    [InlineData(RelationshipStatus.Married, RelationshipStatus.Friendly)]
    [InlineData(RelationshipStatus.Friendly, RelationshipStatus.Friendly)]
    [InlineData(RelationshipStatus.Divorced, RelationshipStatus.Divorced)]
    public void CollectRelationshipStatusChanged_UnregisteredOrForgetfulTransitionReturnsNull(
        RelationshipStatus oldStatus,
        RelationshipStatus newStatus)
    {
        GameEvent? result = NpcHistoryEventCollector.CollectRelationshipStatusChanged(
            new RelationshipStatusChangedFact(
                true,
                13,
                "Abigail",
                oldStatus,
                newStatus));

        Assert.Null(result);
    }

    [Fact]
    public void CollectRelationshipStatusChanged_RemotePlayerReturnsNull()
    {
        Assert.Null(
            NpcHistoryEventCollector.CollectRelationshipStatusChanged(
                new RelationshipStatusChangedFact(
                    false,
                    13,
                    "Abigail",
                    RelationshipStatus.Friendly,
                    RelationshipStatus.Dating)));
    }

    [Fact]
    public void CollectFriendshipMilestone_FirstUpwardFourHeartCrossingProducesPrivateFact()
    {
        FriendshipMilestoneReachedFact fact = new(
            IsLocalPlayer: true,
            OccurredDayIndex: 13,
            NpcId: "Sebastian",
            PreviousFriendshipPoints: 999,
            ObservedFriendshipPoints: 1_001);

        GameEvent first = Assert.IsType<GameEvent>(
            NpcHistoryEventCollector.CollectFriendshipMilestoneReached(fact));
        GameEvent replay = Assert.IsType<GameEvent>(
            NpcHistoryEventCollector.CollectFriendshipMilestoneReached(fact));

        Assert.Equal(first.EventId, replay.EventId);
        Assert.StartsWith("event-friendship-milestone-reached-v1-", first.EventId, StringComparison.Ordinal);
        Assert.Equal("friendship_milestone_reached", first.EventType);
        Assert.Equal("1", first.EventVersion);
        Assert.Equal("smapi.player.friendship_snapshot", first.Source);
        Assert.Equal(AudienceScope.Npc, first.AudienceScope);
        Assert.Equal("Sebastian", first.AudienceNpcId);
        Assert.Equal("friend", first.Payload.GetProperty("milestone_id").GetString());
        Assert.Equal(1_000, first.Payload.GetProperty("threshold_points").GetInt32());
        Assert.Equal(2, first.Payload.EnumerateObject().Count());
        Assert.True(ContractValidator.Validate(first).IsValid);
    }

    [Theory]
    [InlineData(false, 999, 1_000)]
    [InlineData(true, 500, 999)]
    [InlineData(true, 1_000, 1_001)]
    [InlineData(true, 1_001, 999)]
    [InlineData(true, 999, 999)]
    public void CollectFriendshipMilestone_RemoteOrNonFirstUpwardCrossingReturnsNull(
        bool isLocalPlayer,
        int previousPoints,
        int observedPoints)
    {
        GameEvent? result = NpcHistoryEventCollector.CollectFriendshipMilestoneReached(
            new FriendshipMilestoneReachedFact(
                isLocalPlayer,
                13,
                "Sebastian",
                previousPoints,
                observedPoints));

        Assert.Null(result);
    }

    [Fact]
    public void Collectors_InvalidStableFactsFailBeforeComputingIdentity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => NpcHistoryEventCollector.CollectRelationshipStatusChanged(
                new RelationshipStatusChangedFact(
                    true,
                    -1,
                    "Abigail",
                    RelationshipStatus.Friendly,
                    RelationshipStatus.Dating)));
        Assert.Throws<ArgumentException>(
            () => NpcHistoryEventCollector.CollectRelationshipStatusChanged(
                new RelationshipStatusChangedFact(
                    true,
                    13,
                    " Abigail ",
                    RelationshipStatus.Friendly,
                    RelationshipStatus.Dating)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => NpcHistoryEventCollector.CollectFriendshipMilestoneReached(
                new FriendshipMilestoneReachedFact(true, 13, "Abigail", -1, 1_000)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => NpcHistoryEventCollector.CollectRelationshipStatusChanged(
                new RelationshipStatusChangedFact(
                    true,
                    13,
                    "Abigail",
                    (RelationshipStatus)999,
                    RelationshipStatus.Dating)));
    }
}
