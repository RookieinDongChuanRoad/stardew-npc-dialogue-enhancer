using StardewNpcAgent.Contracts;
using StardewNpcAgent.Game;
using StardewNpcAgent.Infrastructure.Storage;

namespace StardewNpcAgent.Integration;

/// <summary>
/// 从目标 NPC 的 public Friendship 复制出的不可变会话观察值。
/// </summary>
/// <param name="NpcId">原版内部 NPC ID，不是本地化显示名。</param>
/// <param name="FriendshipPoints">当前公开 Friendship.Points。</param>
/// <param name="Status">当前公开 Friendship.Status 的纯逻辑映射。</param>
public sealed record NpcFriendshipObservation(
    string NpcId,
    int FriendshipPoints,
    RelationshipStatus Status);

/// <summary>
/// 保存只在当前已加载存档内有效的 producer baseline，并协调 NPC history 的 durable enqueue。
/// </summary>
/// <remarks>
/// 本类不读取 Game1，也不拥有网络任务。SaveSessionRuntime 必须在 SMAPI 主线程先冻结
/// <see cref="NpcFriendshipObservation"/>，再调用这里。会话 baseline 只用于检测 Status/Points 差分；
/// “四心是否历史首次”始终以同一 events.json 中的 durable checkpoint 为准。类按单线程生命周期设计，
/// 内部不加锁；DurableEventOutbox 自身仍保证 copy-on-write 与文件原子性。
/// </remarks>
public sealed class ProducerSessionState
{
    public const string CheckpointsUnavailableReason = "CHECKPOINTS_UNAVAILABLE";
    public const string CheckpointStatusUnsupportedReason = "CHECKPOINT_STATUS_UNSUPPORTED";
    public const string CheckpointPersistenceFailedReason = "CHECKPOINT_PERSISTENCE_FAILED";
    public const string CheckpointStateConflictReason = "CHECKPOINT_STATE_CONFLICT";

    private const string BaselineBelowStatus = "baseline_below";
    private const string BaselineExistingStatus = "baseline_existing";
    private const string SeenStatus = "seen";
    private readonly DurableEventOutbox eventOutbox;
    private readonly HashSet<string> targetNpcIds;
    private readonly Dictionary<string, NpcFriendshipObservation> npcBaselines =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> friendshipMilestoneDisableReasons =
        new(StringComparer.Ordinal);

    /// <summary>
    /// 创建一个绑定当前 save/player outbox 分区的会话状态。
    /// </summary>
    /// <param name="eventOutbox">当前存档已经严格恢复的 durable event outbox。</param>
    /// <param name="targetNpcIds">Mod 配置中已归一化的目标 NPC ID。</param>
    public ProducerSessionState(
        DurableEventOutbox eventOutbox,
        IEnumerable<string> targetNpcIds)
    {
        this.eventOutbox = eventOutbox ?? throw new ArgumentNullException(nameof(eventOutbox));
        ArgumentNullException.ThrowIfNull(targetNpcIds);

        this.targetNpcIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (string npcId in targetNpcIds)
        {
            ValidateNpcId(npcId, nameof(targetNpcIds));
            if (!this.targetNpcIds.Add(npcId))
            {
                throw new ArgumentException("targetNpcIds 不能包含重复 NPC ID。", nameof(targetNpcIds));
            }
        }
    }

    /// <summary>
    /// 用 SaveLoaded 时的真实游戏状态建立一个 NPC baseline，不回填任何历史事件。
    /// </summary>
    /// <returns>NPC 属于目标集合时为 true；非目标 NPC 被安全忽略并返回 false。</returns>
    public bool InitializeNpcBaseline(
        int observedDayIndex,
        NpcFriendshipObservation observation)
    {
        ValidateObservation(observedDayIndex, observation);
        if (!targetNpcIds.Contains(observation.NpcId))
        {
            return false;
        }

        InitializeOrReconcileFriendshipCheckpoint(observedDayIndex, observation);
        // checkpoint 已成功持久化或该 milestone producer 已被明确禁用后，才接受会话 baseline。
        npcBaselines[observation.NpcId] = observation;
        return true;
    }

    /// <summary>
    /// 对一个目标 NPC 的最新快照做差分，并在推进 baseline 前同步 durable enqueue 所有合法事实。
    /// </summary>
    /// <returns>目标 NPC 被处理时为 true；非目标 NPC 返回 false 且不创建任何状态。</returns>
    public bool ReconcileNpcObservation(
        int occurredDayIndex,
        NpcFriendshipObservation observation)
    {
        ValidateObservation(occurredDayIndex, observation);
        if (!targetNpcIds.Contains(observation.NpcId))
        {
            return false;
        }

        if (!npcBaselines.TryGetValue(
            observation.NpcId,
            out NpcFriendshipObservation? previous))
        {
            // 新加入配置或生命周期漏掉 baseline 时宁可只初始化，也不把当前状态伪装成刚刚发生。
            return InitializeNpcBaseline(occurredDayIndex, observation);
        }

        GameEvent? relationshipEvent = NpcHistoryEventCollector.CollectRelationshipStatusChanged(
            new RelationshipStatusChangedFact(
                IsLocalPlayer: true,
                occurredDayIndex,
                observation.NpcId,
                previous.Status,
                observation.Status));
        if (relationshipEvent is not null)
        {
            // 普通关系事件先 durable enqueue；失败会向上传播，且不会推进该 NPC baseline。
            eventOutbox.Enqueue(relationshipEvent);
        }

        GameEvent? milestoneEvent = NpcHistoryEventCollector.CollectFriendshipMilestoneReached(
            new FriendshipMilestoneReachedFact(
                IsLocalPlayer: true,
                occurredDayIndex,
                observation.NpcId,
                previous.FriendshipPoints,
                observation.FriendshipPoints));
        if (milestoneEvent is not null && IsFriendshipMilestoneEnabled(observation.NpcId))
        {
            TryPersistFirstFriendshipMilestone(
                observation.NpcId,
                occurredDayIndex,
                milestoneEvent);
        }

        // 不受支持的 Status 跳变仍更新当前 mandatory baseline；历史白名单不能冻结当前关系状态。
        npcBaselines[observation.NpcId] = observation;
        return true;
    }

    /// <summary>
    /// DayStarted 在任何重新 baseline 之前对账过夜变化，并立即写入 outbox。
    /// </summary>
    /// <param name="occurredDayIndex">新一天的绝对游戏日；过夜变化按当前日记账。</param>
    /// <param name="observations">当前目标 NPC 的主线程冻结快照。</param>
    /// <returns>实际属于目标集合并完成对账的观察数。</returns>
    public int ReconcileDayStarted(
        int occurredDayIndex,
        IEnumerable<NpcFriendshipObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        int processed = 0;
        foreach (NpcFriendshipObservation observation in observations)
        {
            if (ReconcileNpcObservation(occurredDayIndex, observation))
            {
                processed++;
            }
        }

        return processed;
    }

    /// <summary>
    /// 返回按 NPC ID 稳定排序的会话 baseline 副本，供运行时诊断和测试核对。
    /// </summary>
    public IReadOnlyList<NpcFriendshipObservation> SnapshotNpcBaselines()
    {
        NpcFriendshipObservation[] snapshot = npcBaselines.Values
            .OrderBy(item => item.NpcId, StringComparer.Ordinal)
            .ToArray();
        return Array.AsReadOnly(snapshot);
    }

    /// <summary>
    /// 判断某个目标 NPC 的首次四心 producer 是否仍可依赖 durable checkpoint。
    /// </summary>
    public bool IsFriendshipMilestoneEnabled(string npcId)
    {
        ValidateNpcId(npcId, nameof(npcId));
        return targetNpcIds.Contains(npcId)
            && !friendshipMilestoneDisableReasons.ContainsKey(npcId);
    }

    /// <summary>
    /// 返回稳定禁用原因码；未禁用或非目标 NPC 返回 null。
    /// </summary>
    public string? GetFriendshipMilestoneDisableReason(string npcId)
    {
        ValidateNpcId(npcId, nameof(npcId));
        return friendshipMilestoneDisableReasons.TryGetValue(npcId, out string? reason)
            ? reason
            : null;
    }

    /// <summary>
    /// ReturnedToTitle 时清空所有 ephemeral baseline/失败状态；durable checkpoint 由 outbox 保留。
    /// </summary>
    public void Clear()
    {
        npcBaselines.Clear();
        friendshipMilestoneDisableReasons.Clear();
    }

    /// <summary>
    /// 建立首次 checkpoint；若加载时已越过阈值，只写 baseline_existing，绝不回填 event。
    /// </summary>
    private void InitializeOrReconcileFriendshipCheckpoint(
        int observedDayIndex,
        NpcFriendshipObservation observation)
    {
        if (!eventOutbox.ProducerCheckpointsAvailable)
        {
            DisableFriendshipMilestone(observation.NpcId, CheckpointsUnavailableReason);
            return;
        }

        try
        {
            string producerId = CreateFriendshipCheckpointId(observation.NpcId);
            ProducerCheckpoint? checkpoint = eventOutbox.SnapshotProducerCheckpoints()
                .SingleOrDefault(
                    item => string.Equals(
                        item.ProducerId,
                        producerId,
                        StringComparison.Ordinal));
            if (checkpoint is null)
            {
                eventOutbox.InitializeProducerCheckpoint(
                    producerId,
                    observation.FriendshipPoints
                        >= NpcHistoryEventCollector.FriendMilestoneThresholdPoints
                        ? BaselineExistingStatus
                        : BaselineBelowStatus,
                    observedDayIndex);
                return;
            }

            if (!IsKnownCheckpointStatus(checkpoint.Status))
            {
                DisableFriendshipMilestone(
                    observation.NpcId,
                    CheckpointStatusUnsupportedReason);
                return;
            }

            if (string.Equals(checkpoint.Status, BaselineBelowStatus, StringComparison.Ordinal)
                && observation.FriendshipPoints
                    >= NpcHistoryEventCollector.FriendMilestoneThresholdPoints)
            {
                // Mod 未运行期间可能已跨越，但发生日不可证明：只持久化“加载时既有”。
                eventOutbox.ReconcileProducerCheckpointWithoutEvent(
                    producerId,
                    expectedStatus: BaselineBelowStatus,
                    status: BaselineExistingStatus,
                    observedDayIndex);
            }
        }
        catch (OutboxPersistenceException)
        {
            DisableFriendshipMilestone(
                observation.NpcId,
                CheckpointPersistenceFailedReason);
        }
        catch (OutboxIdentityConflictException)
        {
            DisableFriendshipMilestone(observation.NpcId, CheckpointStateConflictReason);
        }
        catch (InvalidOperationException)
        {
            DisableFriendshipMilestone(observation.NpcId, CheckpointStateConflictReason);
        }
    }

    /// <summary>
    /// 对真正的会话内首次跨越执行 event + seen 原子提交；任何失败只禁用这一 NPC 的 milestone。
    /// </summary>
    private void TryPersistFirstFriendshipMilestone(
        string npcId,
        int occurredDayIndex,
        GameEvent milestoneEvent)
    {
        try
        {
            string producerId = CreateFriendshipCheckpointId(npcId);
            ProducerCheckpoint? checkpoint = eventOutbox.SnapshotProducerCheckpoints()
                .SingleOrDefault(
                    item => string.Equals(
                        item.ProducerId,
                        producerId,
                        StringComparison.Ordinal));
            if (checkpoint is null)
            {
                DisableFriendshipMilestone(npcId, CheckpointStateConflictReason);
                return;
            }

            if (string.Equals(checkpoint.Status, BaselineBelowStatus, StringComparison.Ordinal))
            {
                eventOutbox.EnqueueAndAdvanceCheckpoint(
                    milestoneEvent,
                    producerId,
                    SeenStatus,
                    occurredDayIndex);
                return;
            }

            if (!string.Equals(checkpoint.Status, BaselineExistingStatus, StringComparison.Ordinal)
                && !string.Equals(checkpoint.Status, SeenStatus, StringComparison.Ordinal))
            {
                DisableFriendshipMilestone(npcId, CheckpointStatusUnsupportedReason);
            }
            // baseline_existing/seen 都已证明“不是新的首次”，因此正常 suppress 而不是失败。
        }
        catch (OutboxPersistenceException)
        {
            DisableFriendshipMilestone(npcId, CheckpointPersistenceFailedReason);
        }
        catch (OutboxIdentityConflictException)
        {
            DisableFriendshipMilestone(npcId, CheckpointStateConflictReason);
        }
        catch (InvalidOperationException)
        {
            DisableFriendshipMilestone(npcId, CheckpointStateConflictReason);
        }
    }

    private static bool IsKnownCheckpointStatus(string status)
    {
        return string.Equals(status, BaselineBelowStatus, StringComparison.Ordinal)
            || string.Equals(status, BaselineExistingStatus, StringComparison.Ordinal)
            || string.Equals(status, SeenStatus, StringComparison.Ordinal);
    }

    private static string CreateFriendshipCheckpointId(string npcId)
    {
        return $"friendship:{npcId}:{NpcHistoryEventCollector.FriendMilestoneId}";
    }

    private void DisableFriendshipMilestone(string npcId, string reasonCode)
    {
        friendshipMilestoneDisableReasons.TryAdd(npcId, reasonCode);
    }

    private static void ValidateObservation(
        int observedDayIndex,
        NpcFriendshipObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (observedDayIndex < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(observedDayIndex),
                observedDayIndex,
                "observedDayIndex 必须大于等于 0。");
        }

        ValidateNpcId(observation.NpcId, nameof(observation));
        if (observation.FriendshipPoints < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(observation), "Friendship.Points 不能为负。");
        }

        if (!Enum.IsDefined(typeof(RelationshipStatus), observation.Status))
        {
            throw new ArgumentOutOfRangeException(nameof(observation), "未知 Friendship.Status。");
        }
    }

    private static void ValidateNpcId(string npcId, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(npcId, parameterName);
        if (string.IsNullOrWhiteSpace(npcId)
            || !string.Equals(npcId, npcId.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("NPC ID 必须非空且不能包含首尾空白。", parameterName);
        }
    }
}
