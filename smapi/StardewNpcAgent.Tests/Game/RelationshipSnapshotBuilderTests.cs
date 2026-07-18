using StardewNpcAgent.Game;

namespace StardewNpcAgent.Tests.Game;

/// <summary>
/// 冻结游戏 Friendship 到 Agent 四阶段关系快照的唯一映射。
/// </summary>
public sealed class RelationshipSnapshotBuilderTests
{
    [Fact]
    public void MissingOrBelowFourHearts_IsAcquaintance()
    {
        AssertSnapshot(null, 0, "acquaintance");
        AssertSnapshot(new RelationshipFacts(999, RelationshipStatus.Friendly), 999, "acquaintance");
    }

    [Fact]
    public void FourHeartsOrMoreWithoutRomanticStatus_IsFriend()
    {
        AssertSnapshot(new RelationshipFacts(1_000, RelationshipStatus.Friendly), 1_000, "friend");
        AssertSnapshot(new RelationshipFacts(2_499, RelationshipStatus.Friendly), 2_499, "friend");
    }

    [Fact]
    public void DatingAndEngaged_AreDatingWhileMarriedIsSpouse()
    {
        AssertSnapshot(
            new RelationshipFacts(2_000, RelationshipStatus.Dating),
            2_000,
            "dating");
        AssertSnapshot(
            new RelationshipFacts(2_500, RelationshipStatus.Engaged),
            2_500,
            "dating");
        AssertSnapshot(
            new RelationshipFacts(3_000, RelationshipStatus.Married),
            3_000,
            "spouse");
    }

    [Fact]
    public void DivorcedStatus_UsesConservativeAcquaintanceStage()
    {
        AssertSnapshot(
            new RelationshipFacts(2_500, RelationshipStatus.Divorced),
            2_500,
            "acquaintance");
    }

    private static void AssertSnapshot(
        RelationshipFacts? friendship,
        int expectedPoints,
        string expectedStage)
    {
        StardewNpcAgent.Contracts.RelationshipSnapshot snapshot =
            RelationshipSnapshotBuilder.Build(friendship);
        Assert.Equal(expectedPoints, snapshot.FriendshipPoints);
        Assert.Equal(expectedStage, snapshot.RelationshipStage);
    }
}
