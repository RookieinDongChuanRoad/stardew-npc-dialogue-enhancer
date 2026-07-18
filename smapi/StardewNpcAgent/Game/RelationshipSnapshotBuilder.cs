using StardewNpcAgent.Contracts;

namespace StardewNpcAgent.Game;

/// <summary>
/// 从游戏 Friendship.Status 提取的最小、纯逻辑关系状态。
/// </summary>
public enum RelationshipStatus
{
    Friendly,
    Dating,
    Engaged,
    Married,
    Divorced,
}

/// <summary>
/// 游戏适配层在主线程读取的关系事实；builder 不直接访问 Game1 或 mutable Friendship。
/// </summary>
public sealed record RelationshipFacts(int FriendshipPoints, RelationshipStatus Status);

/// <summary>
/// 把游戏已有 points/status 确定性映射到 Agent profile 的四个阶段。
/// </summary>
public static class RelationshipSnapshotBuilder
{
    private const int FriendThresholdPoints = 1_000;

    public static RelationshipSnapshot Build(RelationshipFacts? facts)
    {
        int friendshipPoints = facts?.FriendshipPoints ?? 0;
        if (friendshipPoints < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(facts), "friendship points 不能为负。");
        }

        string stage = facts?.Status switch
        {
            RelationshipStatus.Married => "spouse",
            RelationshipStatus.Dating or RelationshipStatus.Engaged => "dating",
            RelationshipStatus.Divorced => "acquaintance",
            _ when friendshipPoints >= FriendThresholdPoints => "friend",
            _ => "acquaintance",
        };
        return new RelationshipSnapshot
        {
            FriendshipPoints = friendshipPoints,
            RelationshipStage = stage,
        };
    }
}
