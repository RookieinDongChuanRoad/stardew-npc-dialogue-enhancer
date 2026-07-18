using System.Globalization;
using System.Text.Json;
using StardewNpcAgent.Contracts;

namespace StardewNpcAgent.Game;

/// <summary>
/// 两次公开 Friendship.Status 快照之间的最小关系迁移事实。
/// </summary>
public sealed record RelationshipStatusChangedFact(
    bool IsLocalPlayer,
    int OccurredDayIndex,
    string NpcId,
    RelationshipStatus OldStatus,
    RelationshipStatus NewStatus);

/// <summary>
/// 两次公开 Friendship.Points 快照之间的四心阈值差分事实。
/// </summary>
public sealed record FriendshipMilestoneReachedFact(
    bool IsLocalPlayer,
    int OccurredDayIndex,
    string NpcId,
    int PreviousFriendshipPoints,
    int ObservedFriendshipPoints);

/// <summary>
/// Farmer.onGiftGiven postfix 从公开参数与状态复制出的 accepted-gift 事实。
/// </summary>
/// <param name="IsLocalPlayer">是否由当前本地玩家送出。</param>
/// <param name="OccurredDayIndex">callback 所在绝对游戏日。</param>
/// <param name="NpcId">实际接收礼物的原版 NPC ID。</param>
/// <param name="QualifiedItemId">公开 Item.QualifiedItemId，不使用本地化物品名。</param>
/// <param name="Taste">六值 canonical taste。</param>
/// <param name="DailyGiftOrdinal">callback 时公开 GiftsToday 加一得到的当日序号。</param>
public sealed record GiftGivenFact(
    bool IsLocalPlayer,
    int OccurredDayIndex,
    string NpcId,
    string QualifiedItemId,
    string Taste,
    int DailyGiftOrdinal);

/// <summary>
/// 把当前目标 NPC 的明确私人关系历史映射为 npc audience GameEvent。
/// </summary>
/// <remarks>
/// collector 只陈述 public Status/Points 变化，不推断“为什么变化”，也不把当前 relationship
/// mandatory context 复刻成历史。四心 collector 只证明一次向上跨越；“首次”与跨会话去重由
/// DurableEventOutbox 中同 snapshot 的 checkpoint 保证。
/// </remarks>
public static class NpcHistoryEventCollector
{
    public const int FriendMilestoneThresholdPoints = 1_000;
    public const string FriendMilestoneId = "friend";
    private const string EventVersion = "1";
    private const string FriendshipSnapshotSource = "smapi.player.friendship_snapshot";
    private static readonly HashSet<string> SupportedGiftTastes = new(StringComparer.Ordinal)
    {
        "love",
        "like",
        "neutral",
        "dislike",
        "hate",
        "stardrop_tea",
    };

    /// <summary>
    /// 将已通过唯一 accepted-gift callback 观察到的事实映射为当前 NPC 私有 v2 事件。
    /// </summary>
    /// <remarks>
    /// 本方法不接受 friendship delta、InventoryChanged 猜测或本地化显示名。生日与品质不会改变
    /// taste 类别，因此 payload 只包含 qualified item ID 和 taste；当日 ordinal 只用于区分合法重复
    /// occurrence identity，不进入叙事 payload。
    /// </remarks>
    public static GameEvent? CollectGiftGiven(GiftGivenFact fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        ValidateOccurredDay(fact.OccurredDayIndex);
        if (!fact.IsLocalPlayer)
        {
            return null;
        }

        ValidateNpcId(fact.NpcId);
        ValidateStableValue(fact.QualifiedItemId, nameof(fact.QualifiedItemId));
        ValidateStableValue(fact.Taste, nameof(fact.Taste));
        if (fact.DailyGiftOrdinal < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fact.DailyGiftOrdinal),
                fact.DailyGiftOrdinal,
                "DailyGiftOrdinal 必须大于等于 1。");
        }

        if (!SupportedGiftTastes.Contains(fact.Taste))
        {
            return null;
        }

        const string eventType = "gift_given";
        const string eventVersion = "2";
        string eventId = "event-gift-given-v2-" + GameEventCollector.ComputeIdentity(
            eventType,
            eventVersion,
            FormatInteger(fact.OccurredDayIndex),
            fact.NpcId,
            fact.QualifiedItemId,
            FormatInteger(fact.DailyGiftOrdinal));
        return CreateNpcEvent(
            eventId,
            eventType,
            eventVersion,
            fact.OccurredDayIndex,
            fact.NpcId,
            source: "harmony.farmer.on_gift_given",
            JsonSerializer.SerializeToElement(
                new
                {
                    item_id = fact.QualifiedItemId,
                    taste = fact.Taste,
                }));
    }

    /// <summary>
    /// 只接受产品规格冻结的六种原版关系迁移。
    /// </summary>
    /// <returns>
    /// Dating/Engaged/Married、Dating|Engaged→Friendly、Married→Divorced 对应事件；其他跳变返回 null。
    /// 特别地，Divorced→Friendly 可能表示原版抹除记忆，禁止生成可回忆的历史。
    /// </returns>
    public static GameEvent? CollectRelationshipStatusChanged(RelationshipStatusChangedFact fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        ValidateOccurredDay(fact.OccurredDayIndex);
        if (!fact.IsLocalPlayer)
        {
            return null;
        }

        ValidateNpcId(fact.NpcId);
        ValidateRelationshipStatus(fact.OldStatus, nameof(fact.OldStatus));
        ValidateRelationshipStatus(fact.NewStatus, nameof(fact.NewStatus));
        if (!IsSupportedRelationshipTransition(fact.OldStatus, fact.NewStatus))
        {
            return null;
        }

        string oldStatus = ToWireStatus(fact.OldStatus);
        string newStatus = ToWireStatus(fact.NewStatus);
        string eventType = "relationship_status_changed";
        string eventId = "event-relationship-status-changed-v1-" + GameEventCollector.ComputeIdentity(
            eventType,
            EventVersion,
            FormatInteger(fact.OccurredDayIndex),
            fact.NpcId,
            oldStatus,
            newStatus);
        return CreateNpcEvent(
            eventId,
            eventType,
            EventVersion,
            fact.OccurredDayIndex,
            fact.NpcId,
            FriendshipSnapshotSource,
            JsonSerializer.SerializeToElement(
                new
                {
                    old_status = oldStatus,
                    new_status = newStatus,
                }));
    }

    /// <summary>
    /// 将本次会话内 1000 点以下到 1000 点及以上的向上跨越映射为四心朋友里程碑。
    /// </summary>
    /// <remarks>
    /// 本方法故意不根据 observed points 判断是否“历史首次”；调用方必须同时检查 durable checkpoint，
    /// 并用 EnqueueAndAdvanceCheckpoint 把事件与 seen 状态原子提交。
    /// </remarks>
    public static GameEvent? CollectFriendshipMilestoneReached(FriendshipMilestoneReachedFact fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        ValidateOccurredDay(fact.OccurredDayIndex);
        if (!fact.IsLocalPlayer)
        {
            return null;
        }

        ValidateNpcId(fact.NpcId);
        if (fact.PreviousFriendshipPoints < 0 || fact.ObservedFriendshipPoints < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fact), "Friendship.Points 不能为负。");
        }

        if (fact.PreviousFriendshipPoints >= FriendMilestoneThresholdPoints
            || fact.ObservedFriendshipPoints < FriendMilestoneThresholdPoints
            || fact.ObservedFriendshipPoints <= fact.PreviousFriendshipPoints)
        {
            return null;
        }

        string eventType = "friendship_milestone_reached";
        string eventId = "event-friendship-milestone-reached-v1-"
            + GameEventCollector.ComputeIdentity(
                eventType,
                EventVersion,
                FormatInteger(fact.OccurredDayIndex),
                fact.NpcId,
                FriendMilestoneId,
                FormatInteger(FriendMilestoneThresholdPoints));
        return CreateNpcEvent(
            eventId,
            eventType,
            EventVersion,
            fact.OccurredDayIndex,
            fact.NpcId,
            FriendshipSnapshotSource,
            JsonSerializer.SerializeToElement(
                new
                {
                    milestone_id = FriendMilestoneId,
                    threshold_points = FriendMilestoneThresholdPoints,
                }));
    }

    /// <summary>
    /// 冻结可叙述的 Status 边。未列出的跳变仍可更新当前状态，但不产生历史事件。
    /// </summary>
    private static bool IsSupportedRelationshipTransition(
        RelationshipStatus oldStatus,
        RelationshipStatus newStatus)
    {
        return (oldStatus, newStatus) switch
        {
            (RelationshipStatus.Friendly, RelationshipStatus.Dating) => true,
            (RelationshipStatus.Dating, RelationshipStatus.Engaged) => true,
            (RelationshipStatus.Engaged, RelationshipStatus.Married) => true,
            (RelationshipStatus.Dating, RelationshipStatus.Friendly) => true,
            (RelationshipStatus.Engaged, RelationshipStatus.Friendly) => true,
            (RelationshipStatus.Married, RelationshipStatus.Divorced) => true,
            _ => false,
        };
    }

    /// <summary>
    /// 将原版 enum 映射为后端严格白名单使用的小写 wire status。
    /// </summary>
    private static string ToWireStatus(RelationshipStatus status)
    {
        return status switch
        {
            RelationshipStatus.Friendly => "friendly",
            RelationshipStatus.Dating => "dating",
            RelationshipStatus.Engaged => "engaged",
            RelationshipStatus.Married => "married",
            RelationshipStatus.Divorced => "divorced",
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "未知关系状态。"),
        };
    }

    /// <summary>
    /// 统一构造只允许当前 NPC 读取的私人事件 envelope。
    /// </summary>
    private static GameEvent CreateNpcEvent(
        string eventId,
        string eventType,
        string eventVersion,
        int occurredDayIndex,
        string npcId,
        string source,
        JsonElement payload)
    {
        return new GameEvent
        {
            EventId = eventId,
            EventType = eventType,
            EventVersion = eventVersion,
            OccurredDayIndex = occurredDayIndex,
            Source = source,
            AudienceScope = AudienceScope.Npc,
            AudienceNpcId = npcId,
            Payload = payload,
        };
    }

    private static void ValidateOccurredDay(int occurredDayIndex)
    {
        if (occurredDayIndex < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(occurredDayIndex),
                occurredDayIndex,
                "occurredDayIndex 必须大于等于 0。");
        }
    }

    private static void ValidateNpcId(string npcId)
    {
        ValidateStableValue(npcId, nameof(npcId));
    }

    private static void ValidateStableValue(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("标识值必须非空且不能包含首尾空白。", parameterName);
        }
    }

    private static void ValidateRelationshipStatus(RelationshipStatus status, string parameterName)
    {
        if (!Enum.IsDefined(typeof(RelationshipStatus), status))
        {
            throw new ArgumentOutOfRangeException(parameterName, status, "未知关系状态。");
        }
    }

    private static string FormatInteger(int value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }
}
