using StardewNpcAgent.Contracts;
using StardewNpcAgent.Game;
using StardewNpcAgent.Infrastructure.Storage;

namespace StardewNpcAgent.Integration;

/// <summary>
/// 运行时已从公开 pending tool 归一化出的目标领取事实。
/// </summary>
public sealed record PendingToolUpgrade(string ToolId, int ExpectedReceivedLevel);

/// <summary>
/// InventoryChanged 中一件新增 Tool 的 canonical ID 与公开 UpgradeLevel。
/// </summary>
public sealed record ReceivedToolObservation(string ToolId, int UpgradeLevel);

/// <summary>
/// 管理 mine/tool/mastery 的 ephemeral baseline，并确保事件先落盘、状态后推进。
/// </summary>
/// <remarks>
/// 本类只接收已经从 Game1/SMAPI 参数冻结的 primitive/value record，不读取游戏对象。技能 LevelChanged
/// 自带 old/new，无需 baseline，直接由 SaveSessionRuntime 调用纯 collector。
/// </remarks>
public sealed class PlayerProgressionSessionState
{
    private static readonly HashSet<string> SupportedToolIds = new(StringComparer.Ordinal)
    {
        "axe",
        "pickaxe",
        "hoe",
        "watering_can",
        "pan",
        "trash_can",
    };

    private readonly DurableEventOutbox eventOutbox;
    private int? deepestMineRawBaseline;
    private PendingToolUpgrade? pendingToolUpgrade;
    private int? trashCanLevelBaseline;
    private int[]? masteryClaimValues;

    public PlayerProgressionSessionState(DurableEventOutbox eventOutbox)
    {
        this.eventOutbox = eventOutbox ?? throw new ArgumentNullException(nameof(eventOutbox));
    }

    public int? DeepestMineRawBaseline => deepestMineRawBaseline;

    public PendingToolUpgrade? PendingToolUpgrade => pendingToolUpgrade;

    public int? TrashCanLevelBaseline => trashCanLevelBaseline;

    /// <summary>
    /// SaveLoaded 一次建立全部 baseline，不为已有进度补发事件。
    /// </summary>
    public void InitializeBaselines(
        int deepestMineRawDepth,
        PendingToolUpgrade? pendingToolUpgrade,
        int trashCanLevel,
        IReadOnlyList<int> masteryClaimValues)
    {
        if (deepestMineRawDepth < 0 || deepestMineRawDepth == 77377)
        {
            throw new ArgumentOutOfRangeException(
                nameof(deepestMineRawDepth),
                "初始 deepest mine raw depth 必须是非负的真实 high-water。");
        }

        ValidateToolUpgrade(pendingToolUpgrade);
        ValidateUpgradeLevel(trashCanLevel, nameof(trashCanLevel), allowZero: true);
        int[] mastery = ValidateAndCopyMasteryValues(masteryClaimValues);

        this.deepestMineRawBaseline = deepestMineRawDepth;
        this.pendingToolUpgrade = pendingToolUpgrade;
        trashCanLevelBaseline = trashCanLevel;
        this.masteryClaimValues = mastery;
    }

    /// <summary>
    /// Warped 后比较 raw high-water；跨阈值事件写盘成功后才推进 baseline。
    /// </summary>
    /// <returns>本次是否真正入队一个 mine milestone。</returns>
    public bool ObserveMineDepth(
        int occurredDayIndex,
        bool isLocalPlayer,
        int observedRawDepth)
    {
        int previousRawDepth = deepestMineRawBaseline
            ?? throw new InvalidOperationException("Mine baseline 尚未初始化。");
        if (!isLocalPlayer || observedRawDepth == 77377 || observedRawDepth <= previousRawDepth)
        {
            return false;
        }

        GameEvent? gameEvent = PlayerProgressionEventCollector.CollectMineDepthMilestone(
            new MineDepthMilestoneFact(
                isLocalPlayer,
                occurredDayIndex,
                previousRawDepth,
                observedRawDepth));
        if (gameEvent is not null)
        {
            eventOutbox.Enqueue(gameEvent);
        }

        // 无新阈值时也推进 high-water，避免下一次观察重复计算同一小区间。
        deepestMineRawBaseline = observedRawDepth;
        return gameEvent is not null;
    }

    /// <summary>
    /// 从公开 toolBeingUpgraded 记录当前 pending；这里只跟踪，不生产事件。
    /// </summary>
    /// <param name="pending">
    /// 当前公开 pending；null 只应在调用方已经确认没有尚待领取的升级时使用。
    /// InventoryChanged 的领取确认必须先于清空调用，避免原版先清 field 后让事件丢失。
    /// </param>
    public void ObservePendingToolUpgrade(PendingToolUpgrade? pending)
    {
        ValidateToolUpgrade(pending);
        pendingToolUpgrade = pending;
    }

    /// <summary>
    /// InventoryChanged 只在恰好一件 Added tool 匹配 pending ID/level 时确认领取。
    /// </summary>
    /// <returns>成功入队为 1，否则为 0。</returns>
    public int ObserveReceivedTools(
        int occurredDayIndex,
        bool isLocalPlayer,
        IEnumerable<ReceivedToolObservation> addedTools)
    {
        ArgumentNullException.ThrowIfNull(addedTools);
        PendingToolUpgrade? pending = pendingToolUpgrade;
        if (!isLocalPlayer || pending is null || string.Equals(pending.ToolId, "trash_can", StringComparison.Ordinal))
        {
            return 0;
        }

        ReceivedToolObservation[] matches = addedTools
            .Where(item => item is not null)
            .Where(
                item => string.Equals(item.ToolId, pending.ToolId, StringComparison.Ordinal)
                    && item.UpgradeLevel == pending.ExpectedReceivedLevel)
            .ToArray();
        if (matches.Length != 1)
        {
            return 0;
        }

        GameEvent? gameEvent = PlayerProgressionEventCollector.CollectToolUpgradeReceived(
            new ToolUpgradeReceivedFact(
                isLocalPlayer,
                occurredDayIndex,
                pending.ToolId,
                pending.ExpectedReceivedLevel,
                matches[0].UpgradeLevel));
        if (gameEvent is null)
        {
            return 0;
        }

        eventOutbox.Enqueue(gameEvent);
        pendingToolUpgrade = null;
        return 1;
    }

    /// <summary>
    /// Menu/定时观察 trashCanLevel；只有匹配 pending 且恰好增长一级才是领取事件。
    /// </summary>
    public bool ObserveTrashCanLevel(
        int occurredDayIndex,
        bool isLocalPlayer,
        int observedLevel)
    {
        int previousLevel = trashCanLevelBaseline
            ?? throw new InvalidOperationException("Trash can baseline 尚未初始化。");
        ValidateUpgradeLevel(observedLevel, nameof(observedLevel), allowZero: true);
        if (!isLocalPlayer || observedLevel == previousLevel)
        {
            return false;
        }

        PendingToolUpgrade? pending = pendingToolUpgrade;
        bool isConfirmedReceipt = pending is not null
            && string.Equals(pending.ToolId, "trash_can", StringComparison.Ordinal)
            && pending.ExpectedReceivedLevel == observedLevel
            && observedLevel == previousLevel + 1;
        if (isConfirmedReceipt)
        {
            GameEvent gameEvent = PlayerProgressionEventCollector.CollectToolUpgradeReceived(
                    new ToolUpgradeReceivedFact(
                        isLocalPlayer,
                        occurredDayIndex,
                        "trash_can",
                        pending!.ExpectedReceivedLevel,
                        observedLevel))
                ?? throw new InvalidOperationException("已确认 trash can 领取未通过冻结 collector。");
            eventOutbox.Enqueue(gameEvent);
            trashCanLevelBaseline = observedLevel;
            pendingToolUpgrade = null;
            return true;
        }

        // 未跟踪到可靠 pending 时只接受当前 baseline，绝不把历史或 Mod 写入伪装成领取事件。
        trashCanLevelBaseline = observedLevel;
        if (pending is not null && string.Equals(pending.ToolId, "trash_can", StringComparison.Ordinal))
        {
            pendingToolUpgrade = null;
        }

        return false;
    }

    /// <summary>
    /// 关闭 MasteryTrackerMenu 后比较五个 public Stats key，按 index 顺序提交 0→1。
    /// </summary>
    public int ReconcileMasteryClaims(
        int occurredDayIndex,
        bool isLocalPlayer,
        IReadOnlyList<int> observedValues)
    {
        int[] current = ValidateAndCopyMasteryValues(observedValues);
        int[] previous = masteryClaimValues
            ?? throw new InvalidOperationException("Mastery baseline 尚未初始化。");
        if (!isLocalPlayer)
        {
            return 0;
        }

        int committed = 0;
        for (int index = 0; index < current.Length; index++)
        {
            GameEvent? gameEvent = PlayerProgressionEventCollector.CollectMasteryClaimed(
                new MasteryClaimedFact(
                    isLocalPlayer,
                    occurredDayIndex,
                    index,
                    previous[index],
                    current[index]));
            if (gameEvent is not null)
            {
                eventOutbox.Enqueue(gameEvent);
                committed++;
            }

            // 每个 key 的 baseline 只在对应 event durable 后推进；中途失败保留后续旧值供重试。
            previous[index] = current[index];
        }

        return committed;
    }

    public IReadOnlyList<int> SnapshotMasteryClaimValues()
    {
        int[] snapshot = masteryClaimValues?.ToArray() ?? Array.Empty<int>();
        return Array.AsReadOnly(snapshot);
    }

    /// <summary>
    /// ReturnedToTitle 清除 ephemeral observation state，不触碰 durable outbox。
    /// </summary>
    public void Clear()
    {
        deepestMineRawBaseline = null;
        pendingToolUpgrade = null;
        trashCanLevelBaseline = null;
        masteryClaimValues = null;
    }

    private static void ValidateToolUpgrade(PendingToolUpgrade? pending)
    {
        if (pending is null)
        {
            return;
        }

        if (!SupportedToolIds.Contains(pending.ToolId))
        {
            throw new ArgumentException("Pending tool ID 不在首批白名单。", nameof(pending));
        }

        ValidateUpgradeLevel(
            pending.ExpectedReceivedLevel,
            nameof(pending),
            allowZero: false);
    }

    private static int[] ValidateAndCopyMasteryValues(IReadOnlyList<int> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count != 5 || values.Any(value => value is < 0 or > 1))
        {
            throw new ArgumentException("Mastery snapshot 必须精确包含五个 0/1 值。", nameof(values));
        }

        return values.ToArray();
    }

    private static void ValidateUpgradeLevel(
        int level,
        string parameterName,
        bool allowZero)
    {
        int minimum = allowZero ? 0 : 1;
        if (level < minimum || level > 4)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                level,
                $"升级等级必须在 {minimum}～4 之间。");
        }
    }
}
