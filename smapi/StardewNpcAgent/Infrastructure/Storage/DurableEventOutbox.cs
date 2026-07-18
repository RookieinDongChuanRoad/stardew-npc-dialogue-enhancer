using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using StardewNpcAgent.Contracts;

namespace StardewNpcAgent.Infrastructure.Storage;

/// <summary>
/// 一次事件上传尝试的公开、可传输快照。
/// </summary>
/// <param name="Request">将发送到后端的完整合同请求；调用方可以序列化或修改自己的副本。</param>
/// <param name="Identities">与 Request.Events 同序的稳定 outbox identity。</param>
/// <remarks>
/// 该 record 本身不是授权令牌。网络返回后，outbox 会重新核对分区、顺序、sequence 和
/// canonical JSON；调用方对 Request 或 Identities 的任何修改都不能改变内部事实。
/// </remarks>
public sealed record PendingEventBatch(
    GameEventBatchRequest Request,
    IReadOnlyList<PendingEventIdentity> Identities);

/// <summary>
/// 将公开 batch 条目绑定到 durable pending 项的不可变身份快照。
/// </summary>
/// <param name="EventId">合同事件身份。</param>
/// <param name="Sequence">本地 outbox 分配的单调 FIFO 序号。</param>
/// <param name="CanonicalEventJson">递归排序 object key 后的完整事件 JSON；数组顺序保持不变。</param>
public sealed record PendingEventIdentity(
    string EventId,
    long Sequence,
    string CanonicalEventJson);

/// <summary>
/// 已确认无法自动重试的事件事实快照。
/// </summary>
/// <param name="Event">从内部 canonical JSON 重新构造的深拷贝。</param>
/// <param name="Sequence">原 pending FIFO 序号。</param>
/// <param name="AttemptCount">进入 dead letter 前累计的瞬态失败次数。</param>
/// <param name="ReasonCode">稳定业务原因码；不保存异常文本或 HTTP body。</param>
public sealed record EventDeadLetter(
    GameEvent Event,
    long Sequence,
    int AttemptCount,
    string ReasonCode);

/// <summary>
/// 一个差分事件 producer 已持久确认的不可变状态。
/// </summary>
/// <param name="ProducerId">
/// 当前 save/player 分区内的稳定 producer 身份，例如
/// <c>friendship:Abigail:friend</c>。
/// </param>
/// <param name="Status">
/// producer 自己定义的稳定状态码；outbox 只负责非空、边界和冲突校验，不解释业务语义。
/// </param>
/// <param name="ObservedDayIndex">该状态最后一次被真实游戏状态确认的绝对游戏日。</param>
/// <remarks>
/// checkpoint 不是第二套事件数据库，也不会上传后端。它只用于防止需要“首次发生”语义的
/// producer 在重载后重复生产事件。
/// </remarks>
public sealed record ProducerCheckpoint(
    string ProducerId,
    string Status,
    int ObservedDayIndex);

/// <summary>
/// Event outbox 落盘使用的完整 snapshot。
/// </summary>
/// <remarks>
/// 类型保持 internal，只作为 <see cref="AtomicJsonFile{TSnapshot}"/> 与领域状态机之间的
/// 序列化边界。条目只含 string/number/canonical JSON 等不可变值，不持有调用方 DTO 引用。
/// </remarks>
internal sealed record EventOutboxSnapshot(
    int FormatVersion,
    string SaveId,
    string PlayerId,
    int MemoryRevision,
    int CommittedThroughDayIndex,
    long NextSequence,
    IReadOnlyList<EventOutboxSnapshotEntry> Pending,
    IReadOnlyList<EventOutboxDeadLetterEntry> DeadLetters,
    IReadOnlyList<EventOutboxProducerCheckpointEntry> ProducerCheckpoints,
    bool ProducerCheckpointsStructurallyValid,
    string? ProducerCheckpointsSourceJson);

/// <summary>
/// snapshot 中的一条 pending 事件。
/// </summary>
internal sealed record EventOutboxSnapshotEntry(
    long Sequence,
    int AttemptCount,
    string CanonicalEventJson);

/// <summary>
/// snapshot 中的一条 dead-letter 事件。
/// </summary>
internal sealed record EventOutboxDeadLetterEntry(
    long Sequence,
    int AttemptCount,
    string ReasonCode,
    string CanonicalEventJson);

/// <summary>
/// snapshot 中的一条 producer checkpoint；按 ProducerId 严格升序落盘。
/// </summary>
internal sealed record EventOutboxProducerCheckpointEntry(
    string ProducerId,
    string Status,
    int ObservedDayIndex);

/// <summary>
/// 按 save/player 分区持久化结构化游戏事件的同步状态机。
/// </summary>
/// <remarks>
/// 本类只负责本地序列化、FIFO 分批、幂等、重试计数和 dead letter；它不发送 HTTP、不启动
/// 后台线程、不读取游戏状态。实际运行假设为单机、受控 save-local 目录、单进程调用。
/// 每个 mutation 都在实例锁内遵守 copy-on-write：验证旧状态与调用快照，构造新状态，原子
/// 写盘成功后才替换内存引用。因此磁盘失败不会制造“内存已经删除、文件仍保留”的分叉。
/// </remarks>
public sealed class DurableEventOutbox
{
    private const int LegacySnapshotFormatVersion = 1;
    private const int SnapshotFormatVersion = 2;
    private const int MaximumProducerCheckpoints = 128;
    private const int MaximumProducerIdCharacters = 256;
    private const int MaximumCheckpointStatusCharacters = 64;
    private const string MissingRejectedReasonCode = "EVENT_REJECTED_WITHOUT_REASON";
    private const string CorruptedSnapshotMessage =
        "Event outbox snapshot 不符合受支持的格式或领域不变量。";
    private readonly object synchronizationGate = new();
    private readonly string saveId;
    private readonly string playerId;
    private readonly Action<EventOutboxSnapshot> snapshotWriter;
    private EventOutboxState state;

    /// <summary>
    /// 创建已经恢复并验证过状态的实例。
    /// </summary>
    private DurableEventOutbox(
        string saveId,
        string playerId,
        Action<EventOutboxSnapshot> snapshotWriter,
        EventOutboxState state)
    {
        this.saveId = saveId;
        this.playerId = playerId;
        this.snapshotWriter = snapshotWriter;
        this.state = state;
    }

    /// <summary>
    /// 打开一个按 save/player 精确隔离的 durable event outbox。
    /// </summary>
    /// <param name="absolutePath">调用方提供的 save-local 绝对文件路径。</param>
    /// <param name="saveId">稳定存档身份；不能为空、首尾不能有空白。</param>
    /// <param name="playerId">稳定玩家身份；不能为空、首尾不能有空白。</param>
    /// <returns>缺失文件对应空状态；合法文件对应完整恢复状态。</returns>
    /// <exception cref="OutboxCorruptedException">
    /// 文件格式、版本、分区、合同 DTO、唯一键或计数不变量不合法。
    /// </exception>
    /// <exception cref="OutboxPersistenceException">文件存在但无法读取。</exception>
    public static DurableEventOutbox Open(
        string absolutePath,
        string saveId,
        string playerId)
    {
        return OpenCore(absolutePath, saveId, playerId, snapshotWriterOverride: null);
    }

    /// <summary>
    /// 使用真实文件读取、但注入 mutation writer 的测试入口。
    /// </summary>
    /// <remarks>
    /// seam 仅用于稳定复现最终持久化失败并验证内存 copy-on-write，不改变公开 API，也不在
    /// 生产调用中替代 <see cref="AtomicJsonFile{TSnapshot}"/>。
    /// </remarks>
    internal static DurableEventOutbox Open(
        string absolutePath,
        string saveId,
        string playerId,
        Action<EventOutboxSnapshot> snapshotWriter)
    {
        ArgumentNullException.ThrowIfNull(snapshotWriter);
        return OpenCore(absolutePath, saveId, playerId, snapshotWriter);
    }

    /// <summary>
    /// 当前仍等待后端终态的事件数。
    /// </summary>
    public int PendingCount
    {
        get
        {
            lock (synchronizationGate)
            {
                return state.Pending.Count;
            }
        }
    }

    /// <summary>
    /// 当前已隔离的永久失败事件数。
    /// </summary>
    public int DeadLetterCount
    {
        get
        {
            lock (synchronizationGate)
            {
                return state.DeadLetters.Count;
            }
        }
    }

    /// <summary>
    /// 最近一次已成功应用的后端 memory revision 水位。
    /// </summary>
    /// <remarks>
    /// 该值与事件终态保存在同一 snapshot 中，供次日生成请求填写
    /// required_memory_revision；初始无事实分区为 0。
    /// </remarks>
    public int MemoryRevision
    {
        get
        {
            lock (synchronizationGate)
            {
                return state.MemoryRevision;
            }
        }
    }

    /// <summary>
    /// 最近一次已成功应用的后端 committed-through day 水位；初始值为 -1。
    /// </summary>
    public int CommittedThroughDayIndex
    {
        get
        {
            lock (synchronizationGate)
            {
                return state.CommittedThroughDayIndex;
            }
        }
    }

    /// <summary>
    /// 当前 snapshot 中的 producer checkpoint 区域是否可安全解释。
    /// </summary>
    /// <remarks>
    /// false 只禁用依赖“首次发生”状态的 producer；普通事件 enqueue/flush 仍继续工作，并在后续
    /// snapshot mutation 中原样保留不透明 checkpoint JSON，避免用空集合掩盖损坏。
    /// </remarks>
    public bool ProducerCheckpointsAvailable
    {
        get
        {
            lock (synchronizationGate)
            {
                return state.OpaqueProducerCheckpointsJson is null;
            }
        }
    }

    /// <summary>
    /// 返回当前 producer checkpoints 的稳定、只读快照。
    /// </summary>
    /// <returns>
    /// 按 <see cref="ProducerCheckpoint.ProducerId"/> ordinal 升序排列的新值对象集合；调用方
    /// 不能通过返回值修改 outbox 内部状态。
    /// </returns>
    public IReadOnlyList<ProducerCheckpoint> SnapshotProducerCheckpoints()
    {
        lock (synchronizationGate)
        {
            EnsureProducerCheckpointsAvailable();
            ProducerCheckpoint[] snapshot = state.ProducerCheckpoints
                .Select(
                    item => new ProducerCheckpoint(
                        item.ProducerId,
                        item.Status,
                        item.ObservedDayIndex))
                .ToArray();
            return Array.AsReadOnly(snapshot);
        }
    }

    /// <summary>
    /// 首次用真实游戏状态初始化一个 producer checkpoint，但不生产历史事件。
    /// </summary>
    /// <param name="producerId">当前 save/player 分区内的稳定 producer 身份。</param>
    /// <param name="status">真实 baseline 对应的稳定状态码。</param>
    /// <param name="observedDayIndex">读取 baseline 时的绝对游戏日，必须大于等于 0。</param>
    /// <remarks>
    /// 相同事实重入是零写入；同 producer 已绑定不同事实时拒绝覆盖。调用方必须先读取
    /// <see cref="SnapshotProducerCheckpoints"/> 决定是否需要初始化，不能把该方法当作通用更新入口。
    /// </remarks>
    /// <exception cref="ArgumentException">身份或状态不是稳定、有界字符串。</exception>
    /// <exception cref="ArgumentOutOfRangeException">游戏日小于 0。</exception>
    /// <exception cref="InvalidOperationException">checkpoint 容器已经达到冻结上限。</exception>
    /// <exception cref="OutboxIdentityConflictException">同 producer 已记录另一事实。</exception>
    public void InitializeProducerCheckpoint(
        string producerId,
        string status,
        int observedDayIndex)
    {
        ValidateProducerCheckpointInput(producerId, status, observedDayIndex);

        lock (synchronizationGate)
        {
            EnsureProducerCheckpointsAvailable();
            StoredProducerCheckpoint? existing = state.ProducerCheckpoints.FirstOrDefault(
                item => string.Equals(item.ProducerId, producerId, StringComparison.Ordinal));
            if (existing is not null)
            {
                if (string.Equals(existing.Status, status, StringComparison.Ordinal)
                    && existing.ObservedDayIndex == observedDayIndex)
                {
                    // SaveLoaded 等生命周期回调可能安全重入；等价 baseline 不制造磁盘写入。
                    return;
                }

                throw new OutboxIdentityConflictException(
                    "同一 producer_id 已对应不同 checkpoint 事实，outbox 拒绝自动覆盖。");
            }

            if (state.ProducerCheckpoints.Count >= MaximumProducerCheckpoints)
            {
                throw new InvalidOperationException(
                    $"Event outbox producer checkpoint 已达到 {MaximumProducerCheckpoints} 项上限。");
            }

            StoredProducerCheckpoint[] nextCheckpoints = state.ProducerCheckpoints
                .Append(new StoredProducerCheckpoint(producerId, status, observedDayIndex))
                .OrderBy(item => item.ProducerId, StringComparer.Ordinal)
                .ToArray();
            PersistThenReplace(
                state with
                {
                    ProducerCheckpoints = Array.AsReadOnly(nextCheckpoints),
                });
        }
    }

    /// <summary>
    /// 在不伪造历史事件的前提下，把一个已存在 checkpoint 从预期 baseline 推进到新 baseline。
    /// </summary>
    /// <param name="producerId">已经初始化的稳定 producer 身份。</param>
    /// <param name="expectedStatus">调用方读取后预期仍存在的旧状态，用作 compare-and-set 门禁。</param>
    /// <param name="status">由当前真实游戏状态确认的新 baseline 状态。</param>
    /// <param name="observedDayIndex">本次 baseline 观察日，不能早于旧 checkpoint。</param>
    /// <remarks>
    /// 该入口用于 SaveLoaded 发现“Mod 未运行期间事实已存在、但无法证明发生日”的保守恢复：只把
    /// <c>baseline_below</c> 标记为 <c>baseline_existing</c>，不回填事件。它不是事件完成入口；真正
    /// 观察到的首次跨越仍必须调用 <see cref="EnqueueAndAdvanceCheckpoint"/> 原子提交 event + seen。
    /// </remarks>
    public void ReconcileProducerCheckpointWithoutEvent(
        string producerId,
        string expectedStatus,
        string status,
        int observedDayIndex)
    {
        ValidateProducerCheckpointInput(producerId, status, observedDayIndex);
        ValidateStableString(expectedStatus, nameof(expectedStatus));
        if (expectedStatus.Length > MaximumCheckpointStatusCharacters)
        {
            throw new ArgumentException(
                $"expectedStatus 不能超过 {MaximumCheckpointStatusCharacters} 个字符。",
                nameof(expectedStatus));
        }

        lock (synchronizationGate)
        {
            EnsureProducerCheckpointsAvailable();
            int checkpointIndex = FindProducerCheckpointIndex(producerId);
            if (checkpointIndex < 0)
            {
                throw new InvalidOperationException("Producer checkpoint 尚未初始化，无法执行 baseline 对账。");
            }

            StoredProducerCheckpoint existing = state.ProducerCheckpoints[checkpointIndex];
            if (string.Equals(existing.Status, status, StringComparison.Ordinal)
                && existing.ObservedDayIndex == observedDayIndex)
            {
                return;
            }

            if (!string.Equals(existing.Status, expectedStatus, StringComparison.Ordinal)
                || observedDayIndex < existing.ObservedDayIndex)
            {
                throw new OutboxIdentityConflictException(
                    "Producer checkpoint 已不匹配预期 baseline，拒绝覆盖并发或更晚事实。");
            }

            StoredProducerCheckpoint[] nextCheckpoints = state.ProducerCheckpoints.ToArray();
            nextCheckpoints[checkpointIndex] = existing with
            {
                Status = status,
                ObservedDayIndex = observedDayIndex,
            };
            PersistThenReplace(
                state with
                {
                    ProducerCheckpoints = Array.AsReadOnly(nextCheckpoints),
                });
        }
    }

    /// <summary>
    /// 在一次 copy-on-write snapshot mutation 中同时入队事件并推进 producer checkpoint。
    /// </summary>
    /// <param name="gameEvent">已经由 producer 确认、且发生日等于 observedDayIndex 的合同事件。</param>
    /// <param name="producerId">此前已通过 baseline 初始化的稳定 producer 身份。</param>
    /// <param name="status">事件发生后需要持久确认的稳定状态码。</param>
    /// <param name="observedDayIndex">事件与新 checkpoint 共同对应的绝对游戏日。</param>
    /// <remarks>
    /// writer 成功前，事件与 checkpoint 都只存在于新 immutable state；写盘失败时二者都不会进入
    /// 当前内存状态。checkpoint 已经处于目标事实时视为安全重放，即使原事件已上传并从 pending
    /// 删除，也不会再次生产。若发现“事件已存在但 checkpoint 未推进”等不可能的半提交状态，则
    /// fail closed，拒绝猜测哪一侧可信。
    /// </remarks>
    /// <exception cref="ArgumentException">事件或 checkpoint 参数不合法，或发生日不一致。</exception>
    /// <exception cref="InvalidOperationException">producer 未初始化，或 sequence 已耗尽。</exception>
    /// <exception cref="OutboxIdentityConflictException">event identity 或 checkpoint 状态发生冲突。</exception>
    public void EnqueueAndAdvanceCheckpoint(
        GameEvent gameEvent,
        string producerId,
        string status,
        int observedDayIndex)
    {
        ValidateProducerCheckpointInput(producerId, status, observedDayIndex);
        CanonicalEvent canonicalEvent = CreateCanonicalEventFromCaller(gameEvent);
        if (gameEvent.OccurredDayIndex != observedDayIndex)
        {
            throw new ArgumentException(
                "原子 checkpoint mutation 要求事件发生日与 observedDayIndex 完全一致。",
                nameof(observedDayIndex));
        }

        lock (synchronizationGate)
        {
            EnsureProducerCheckpointsAvailable();
            int checkpointIndex = FindProducerCheckpointIndex(producerId);
            if (checkpointIndex < 0)
            {
                throw new InvalidOperationException(
                    "Producer checkpoint 尚未用真实游戏状态初始化，拒绝猜测 baseline。");
            }

            StoredProducerCheckpoint existingCheckpoint = state.ProducerCheckpoints[checkpointIndex];
            bool checkpointAlreadyAdvanced = string.Equals(
                existingCheckpoint.Status,
                status,
                StringComparison.Ordinal)
                && existingCheckpoint.ObservedDayIndex == observedDayIndex;

            string? existingCanonicalJson = FindStoredCanonicalEventJson(canonicalEvent.EventId);
            if (existingCanonicalJson is not null
                && !string.Equals(
                    existingCanonicalJson,
                    canonicalEvent.CanonicalJson,
                    StringComparison.Ordinal))
            {
                throw new OutboxIdentityConflictException(
                    "同一 event_id 已对应不同事件事实，outbox 拒绝自动覆盖。");
            }

            if (checkpointAlreadyAdvanced)
            {
                // 原事件仍 pending/dead-letter，或已上传删除，均代表这次首次事实已经完整提交。
                return;
            }

            if (existingCanonicalJson is not null)
            {
                // 本方法只可能同时写入两侧；发现单侧既存说明调用路径或文件状态已经分叉。
                throw new OutboxIdentityConflictException(
                    "Event 已存在但 producer checkpoint 未处于目标状态，拒绝非原子修补。");
            }

            if (observedDayIndex < existingCheckpoint.ObservedDayIndex)
            {
                throw new OutboxIdentityConflictException(
                    "Producer checkpoint 不能回退到更早的 observed day。");
            }

            if (state.NextSequence == long.MaxValue)
            {
                throw new InvalidOperationException("Event outbox sequence 已达到可表示上限。");
            }

            StoredEvent newItem = new(
                canonicalEvent.EventId,
                state.NextSequence,
                AttemptCount: 0,
                canonicalEvent.CanonicalJson);
            StoredEvent[] nextPending = state.Pending.Append(newItem).ToArray();
            StoredProducerCheckpoint[] nextCheckpoints = state.ProducerCheckpoints.ToArray();
            nextCheckpoints[checkpointIndex] = existingCheckpoint with
            {
                Status = status,
                ObservedDayIndex = observedDayIndex,
            };

            EventOutboxState nextState = state with
            {
                NextSequence = state.NextSequence + 1,
                Pending = Array.AsReadOnly(nextPending),
                ProducerCheckpoints = Array.AsReadOnly(nextCheckpoints),
            };
            PersistThenReplace(nextState);
        }
    }

    /// <summary>
    /// 将一条已确认发生的合法事件先持久化到本地 outbox。
    /// </summary>
    /// <param name="gameEvent">合同合法的单条事件；方法不会保留该 DTO 引用。</param>
    /// <remarks>
    /// 同 event_id 且 canonical 事实等价时是正常 no-op；同 identity 对应不同事实时拒绝合并。
    /// </remarks>
    /// <exception cref="ArgumentException">事件不符合共享合同。</exception>
    /// <exception cref="OutboxIdentityConflictException">同 event_id 已绑定到不同事实。</exception>
    public void Enqueue(GameEvent gameEvent)
    {
        CanonicalEvent canonicalEvent = CreateCanonicalEventFromCaller(gameEvent);

        lock (synchronizationGate)
        {
            StoredEvent? existingPending = state.Pending.FirstOrDefault(
                item => string.Equals(item.EventId, canonicalEvent.EventId, StringComparison.Ordinal));
            StoredDeadLetter? existingDeadLetter = state.DeadLetters.FirstOrDefault(
                item => string.Equals(item.EventId, canonicalEvent.EventId, StringComparison.Ordinal));
            string? existingCanonicalJson = existingPending?.CanonicalEventJson
                ?? existingDeadLetter?.CanonicalEventJson;

            if (existingCanonicalJson is not null)
            {
                if (string.Equals(
                    existingCanonicalJson,
                    canonicalEvent.CanonicalJson,
                    StringComparison.Ordinal))
                {
                    // 等价重入不产生新 sequence，也不做无意义磁盘写入。
                    return;
                }

                throw new OutboxIdentityConflictException(
                    "同一 event_id 已对应不同事件事实，outbox 拒绝自动覆盖。");
            }

            if (state.NextSequence == long.MaxValue)
            {
                // 不能回绕 sequence；虽然单机 Mod 实际无法达到该数量，显式失败比破坏 FIFO 更安全。
                throw new InvalidOperationException("Event outbox sequence 已达到可表示上限。");
            }

            StoredEvent newItem = new(
                canonicalEvent.EventId,
                state.NextSequence,
                AttemptCount: 0,
                canonicalEvent.CanonicalJson);
            StoredEvent[] nextPending = state.Pending.Append(newItem).ToArray();
            EventOutboxState nextState = new(
                state.NextSequence + 1,
                state.MemoryRevision,
                state.CommittedThroughDayIndex,
                Array.AsReadOnly(nextPending),
                state.DeadLetters,
                state.ProducerCheckpoints,
                state.OpaqueProducerCheckpointsJson);

            PersistThenReplace(nextState);
        }
    }

    /// <summary>
    /// 创建当前 FIFO 前缀的上传快照，最多包含共享合同允许的 64 条事件。
    /// </summary>
    /// <param name="requestId">本次 HTTP 请求身份。</param>
    /// <param name="maxItems">1～64 的批次上限。</param>
    /// <returns>与当前内部状态隔离、可由调用方序列化和修改的深拷贝。</returns>
    /// <exception cref="InvalidOperationException">当前没有 pending 事件。</exception>
    public PendingEventBatch CreatePendingBatch(
        string requestId,
        int maxItems = ContractLimits.MaximumEventsPerBatch)
    {
        return CreatePendingBatchThroughDay(requestId, int.MaxValue, maxItems)
            ?? throw new InvalidOperationException("Event outbox 当前没有待提交事件。");
    }

    /// <summary>
    /// 创建 FIFO 中截至指定绝对游戏日的前缀；用于兑现 DayStarted 的“只刷新昨日”合同。
    /// </summary>
    /// <param name="requestId">本次 HTTP 请求身份。</param>
    /// <param name="throughDayIndex">包含式截止日；首日之前允许 -1。</param>
    /// <param name="maxItems">1～64 的批次上限。</param>
    /// <returns>存在 eligible 前缀时返回深拷贝；无 pending 或首项晚于 cutoff 时返回 null。</returns>
    /// <remarks>
    /// 只取 FIFO 前缀而不越过未来事件。正常游戏按发生顺序入队，不会出现“未来日在前、旧日在后”；
    /// 即使 snapshot 来自合法但异常的晚补顺序，等待 cutoff 追上也比跳过 FIFO 更可解释。
    /// </remarks>
    public PendingEventBatch? CreatePendingBatchThroughDay(
        string requestId,
        int throughDayIndex,
        int maxItems = ContractLimits.MaximumEventsPerBatch)
    {
        ValidateStableString(requestId, nameof(requestId));
        if (throughDayIndex < -1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(throughDayIndex),
                throughDayIndex,
                "throughDayIndex 必须大于等于 -1。");
        }

        if (maxItems < 1 || maxItems > ContractLimits.MaximumEventsPerBatch)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxItems),
                maxItems,
                $"maxItems 必须在 1～{ContractLimits.MaximumEventsPerBatch} 之间。");
        }

        lock (synchronizationGate)
        {
            if (state.Pending.Count == 0)
            {
                return null;
            }

            List<StoredEvent> selectedItems = new();
            foreach (StoredEvent item in state.Pending)
            {
                GameEvent gameEvent = DeserializeCanonicalEvent(item.CanonicalEventJson);
                if (gameEvent.OccurredDayIndex > throughDayIndex)
                {
                    break;
                }

                selectedItems.Add(item);
                if (selectedItems.Count == maxItems)
                {
                    break;
                }
            }

            if (selectedItems.Count == 0)
            {
                return null;
            }

            StoredEvent[] selected = selectedItems.ToArray();
            GameEventBatchRequest request = new()
            {
                SchemaVersion = ContractVersions.V1,
                RequestId = requestId,
                SaveId = saveId,
                PlayerId = playerId,
                Events = selected
                    .Select(item => DeserializeCanonicalEvent(item.CanonicalEventJson))
                    .ToList(),
            };
            PendingEventIdentity[] identities = selected
                .Select(
                    item => new PendingEventIdentity(
                        item.EventId,
                        item.Sequence,
                        item.CanonicalEventJson))
                .ToArray();

            return new PendingEventBatch(request, Array.AsReadOnly(identities));
        }
    }

    /// <summary>
    /// 应用后端对一个既有 batch 的逐项终态。
    /// </summary>
    /// <param name="batch">此前由本实例创建、且可能在网络在途期间被调用方持有的快照。</param>
    /// <param name="response">必须与 batch request_id、数量、顺序和 event_id 精确匹配。</param>
    /// <remarks>
    /// accepted/duplicate 删除当前仍 pending 的对应项；rejected 将其移入 dead letter。
    /// 已经不存在的旧 batch 项视为已处理，因此同一 response 可安全重放；后来追加的项不在
    /// batch identity 中，绝不会被本方法删除。
    /// </remarks>
    public void ApplyResponse(PendingEventBatch batch, GameEventBatchResponse response)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(response);

        lock (synchronizationGate)
        {
            ValidatedBatch validatedBatch = ValidateBatchSnapshot(batch);
            ValidateResponseSnapshot(validatedBatch, response);
            ValidateResponseWatermark(response);
            IReadOnlyDictionary<string, StoredEvent> currentItems =
                ValidateBatchAgainstCurrentState(validatedBatch);

            if (currentItems.Count == 0)
            {
                if (response.MemoryRevision == state.MemoryRevision
                    && response.CommittedThroughDayIndex == state.CommittedThroughDayIndex)
                {
                    // 完整 response 重放且水位相同：没有业务变化，保持真正零写入。
                    return;
                }

                // 旧 batch 的 item 可能已由另一合法响应完成，但本响应携带更新的全局后端水位。
                // 只推进水位，不触碰后来追加的 pending/dead-letter 项。
                PersistThenReplace(
                    state with
                    {
                        MemoryRevision = response.MemoryRevision,
                        CommittedThroughDayIndex = response.CommittedThroughDayIndex,
                    });
                return;
            }

            HashSet<string> completedEventIds = currentItems.Keys.ToHashSet(StringComparer.Ordinal);
            StoredEvent[] nextPending = state.Pending
                .Where(item => !completedEventIds.Contains(item.EventId))
                .ToArray();
            List<StoredDeadLetter> nextDeadLetters = state.DeadLetters.ToList();

            for (int index = 0; index < validatedBatch.Items.Count; index++)
            {
                ValidatedBatchItem batchItem = validatedBatch.Items[index];
                if (!currentItems.TryGetValue(batchItem.EventId, out StoredEvent? storedItem))
                {
                    continue;
                }

                GameEventItemResult responseItem = response.Items[index];
                if (responseItem.Status == EventIngestionStatus.Rejected)
                {
                    string stableReasonCode = responseItem.ReasonCode ?? MissingRejectedReasonCode;
                    nextDeadLetters.Add(
                        new StoredDeadLetter(
                            storedItem.EventId,
                            storedItem.Sequence,
                            storedItem.AttemptCount,
                            stableReasonCode,
                            storedItem.CanonicalEventJson));
                }
            }

            EventOutboxState nextState = new(
                state.NextSequence,
                response.MemoryRevision,
                response.CommittedThroughDayIndex,
                Array.AsReadOnly(nextPending),
                Array.AsReadOnly(nextDeadLetters.ToArray()),
                state.ProducerCheckpoints,
                state.OpaqueProducerCheckpointsJson);
            PersistThenReplace(nextState);
        }
    }

    /// <summary>
    /// 记录一次网络、超时、429 或 5xx 等瞬态失败。
    /// </summary>
    /// <param name="batch">失败请求对应的原始 batch 快照。</param>
    /// <param name="reasonCode">稳定分类码；当前 v1 不把它写入 snapshot，更不会保存异常正文。</param>
    /// <remarks>
    /// 每次合法调用只给当前仍 pending 的 batch 项加一；已处理的旧项跳过。写盘失败时内存
    /// state 仍引用旧对象，调用方可在下一次刷新时安全重试。
    /// </remarks>
    public void RecordTransientFailure(PendingEventBatch batch, string reasonCode)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ValidateStableString(reasonCode, nameof(reasonCode));

        lock (synchronizationGate)
        {
            ValidatedBatch validatedBatch = ValidateBatchSnapshot(batch);
            IReadOnlyDictionary<string, StoredEvent> currentItems =
                ValidateBatchAgainstCurrentState(validatedBatch);
            if (currentItems.Count == 0)
            {
                return;
            }

            if (currentItems.Values.Any(item => item.AttemptCount == int.MaxValue))
            {
                throw new InvalidOperationException("Event outbox attempt_count 已达到可表示上限。");
            }

            StoredEvent[] nextPending = state.Pending
                .Select(
                    item => currentItems.ContainsKey(item.EventId)
                        ? item with { AttemptCount = item.AttemptCount + 1 }
                        : item)
                .ToArray();
            EventOutboxState nextState = new(
                state.NextSequence,
                state.MemoryRevision,
                state.CommittedThroughDayIndex,
                Array.AsReadOnly(nextPending),
                state.DeadLetters,
                state.ProducerCheckpoints,
                state.OpaqueProducerCheckpointsJson);

            PersistThenReplace(nextState);
        }
    }

    /// <summary>
    /// 将一个仍匹配的 producer-invalid batch 原子移入 dead letter，不改变后端水位。
    /// </summary>
    /// <remarks>
    /// 本入口只供 HTTP envelope 被 422 等永久拒绝时使用；409 revision exhausted 不是事件事实
    /// 本身错误，coordinator 必须保留 pending，不能调用本方法丢弃事实。
    /// </remarks>
    public void RecordPermanentFailure(PendingEventBatch batch, string reasonCode)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ValidateStableString(reasonCode, nameof(reasonCode));

        lock (synchronizationGate)
        {
            ValidatedBatch validatedBatch = ValidateBatchSnapshot(batch);
            IReadOnlyDictionary<string, StoredEvent> currentItems =
                ValidateBatchAgainstCurrentState(validatedBatch);
            if (currentItems.Count == 0)
            {
                return;
            }

            HashSet<string> failedEventIds = currentItems.Keys.ToHashSet(StringComparer.Ordinal);
            StoredEvent[] nextPending = state.Pending
                .Where(item => !failedEventIds.Contains(item.EventId))
                .ToArray();
            StoredDeadLetter[] nextDeadLetters = state.DeadLetters
                .Concat(
                    currentItems.Values.Select(
                        item => new StoredDeadLetter(
                            item.EventId,
                            item.Sequence,
                            item.AttemptCount,
                            reasonCode,
                            item.CanonicalEventJson)))
                .OrderBy(item => item.Sequence)
                .ToArray();
            EventOutboxState nextState = new(
                state.NextSequence,
                state.MemoryRevision,
                state.CommittedThroughDayIndex,
                Array.AsReadOnly(nextPending),
                Array.AsReadOnly(nextDeadLetters),
                state.ProducerCheckpoints,
                state.OpaqueProducerCheckpointsJson);
            PersistThenReplace(nextState);
        }
    }

    /// <summary>
    /// 返回当前 dead letters 的只读深拷贝。
    /// </summary>
    /// <returns>集合不能增删；其中每个 GameEvent 也从 canonical JSON 新建，不与内部状态共享引用。</returns>
    public IReadOnlyList<EventDeadLetter> SnapshotDeadLetters()
    {
        lock (synchronizationGate)
        {
            EventDeadLetter[] snapshot = state.DeadLetters
                .Select(
                    item => new EventDeadLetter(
                        DeserializeCanonicalEvent(item.CanonicalEventJson),
                        item.Sequence,
                        item.AttemptCount,
                        item.ReasonCode))
                .ToArray();
            return Array.AsReadOnly(snapshot);
        }
    }

    /// <summary>
    /// 校验构造参数、读取 snapshot 并绑定真实或测试 writer。
    /// </summary>
    private static DurableEventOutbox OpenCore(
        string absolutePath,
        string saveId,
        string playerId,
        Action<EventOutboxSnapshot>? snapshotWriterOverride)
    {
        ValidateStableString(saveId, nameof(saveId));
        ValidateStableString(playerId, nameof(playerId));

        AtomicJsonFile<EventOutboxSnapshot> snapshotFile = new(
            absolutePath,
            EventOutboxSnapshotJsonConverter.CreateSerializerOptions());
        EventOutboxState recoveredState;
        if (snapshotFile.TryRead(out EventOutboxSnapshot? snapshot))
        {
            recoveredState = RestoreAndValidateSnapshot(snapshot!, saveId, playerId);
        }
        else
        {
            recoveredState = EventOutboxState.Empty;
        }

        return new DurableEventOutbox(
            saveId,
            playerId,
            snapshotWriterOverride ?? snapshotFile.Write,
            recoveredState);
    }

    /// <summary>
    /// 把完整新状态写盘，只有 writer 成功返回后才替换内存引用。
    /// </summary>
    private void PersistThenReplace(EventOutboxState nextState)
    {
        EventOutboxSnapshot snapshot = CreateSnapshot(nextState);
        snapshotWriter(snapshot);
        state = nextState;
    }

    /// <summary>
    /// 将私有 immutable state 投影为一次性序列化 snapshot。
    /// </summary>
    private EventOutboxSnapshot CreateSnapshot(EventOutboxState sourceState)
    {
        EventOutboxSnapshotEntry[] pending = sourceState.Pending
            .Select(
                item => new EventOutboxSnapshotEntry(
                    item.Sequence,
                    item.AttemptCount,
                    item.CanonicalEventJson))
            .ToArray();
        EventOutboxDeadLetterEntry[] deadLetters = sourceState.DeadLetters
            .Select(
                item => new EventOutboxDeadLetterEntry(
                    item.Sequence,
                    item.AttemptCount,
                    item.ReasonCode,
                    item.CanonicalEventJson))
            .ToArray();
        EventOutboxProducerCheckpointEntry[] producerCheckpoints = sourceState.ProducerCheckpoints
            .Select(
                item => new EventOutboxProducerCheckpointEntry(
                    item.ProducerId,
                    item.Status,
                    item.ObservedDayIndex))
            .ToArray();

        return new EventOutboxSnapshot(
            SnapshotFormatVersion,
            saveId,
            playerId,
            sourceState.MemoryRevision,
            sourceState.CommittedThroughDayIndex,
            sourceState.NextSequence,
            Array.AsReadOnly(pending),
            Array.AsReadOnly(deadLetters),
            Array.AsReadOnly(producerCheckpoints),
            ProducerCheckpointsStructurallyValid: sourceState.OpaqueProducerCheckpointsJson is null,
            ProducerCheckpointsSourceJson: sourceState.OpaqueProducerCheckpointsJson);
    }

    /// <summary>
    /// 从严格 JSON snapshot 恢复私有状态，并验证所有跨条目领域不变量。
    /// </summary>
    private static EventOutboxState RestoreAndValidateSnapshot(
        EventOutboxSnapshot snapshot,
        string expectedSaveId,
        string expectedPlayerId)
    {
        try
        {
            if ((snapshot.FormatVersion != LegacySnapshotFormatVersion
                    && snapshot.FormatVersion != SnapshotFormatVersion)
                || !string.Equals(snapshot.SaveId, expectedSaveId, StringComparison.Ordinal)
                || !string.Equals(snapshot.PlayerId, expectedPlayerId, StringComparison.Ordinal)
                || !IsValidWatermark(
                    snapshot.MemoryRevision,
                    snapshot.CommittedThroughDayIndex)
                || snapshot.NextSequence < 1
                || snapshot.Pending is null
                || snapshot.DeadLetters is null
                || snapshot.ProducerCheckpoints is null
                || (snapshot.FormatVersion == LegacySnapshotFormatVersion
                    && (!snapshot.ProducerCheckpointsStructurallyValid
                        || snapshot.ProducerCheckpoints.Count != 0
                        || snapshot.ProducerCheckpointsSourceJson is not null))
                || (snapshot.FormatVersion == SnapshotFormatVersion
                    && snapshot.ProducerCheckpointsSourceJson is null))
            {
                throw new SnapshotInvariantException();
            }

            List<StoredEvent> pending = new(snapshot.Pending.Count);
            List<StoredDeadLetter> deadLetters = new(snapshot.DeadLetters.Count);
            HashSet<string> eventIds = new(StringComparer.Ordinal);
            HashSet<long> sequences = new();
            long maximumSequence = 0;
            long previousPendingSequence = 0;
            foreach (EventOutboxSnapshotEntry entry in snapshot.Pending)
            {
                if (entry is null
                    || entry.Sequence < 1
                    || entry.AttemptCount < 0
                    || entry.Sequence <= previousPendingSequence)
                {
                    throw new SnapshotInvariantException();
                }

                CanonicalEvent canonicalEvent = CreateCanonicalEventFromSnapshot(entry.CanonicalEventJson);
                if (!eventIds.Add(canonicalEvent.EventId) || !sequences.Add(entry.Sequence))
                {
                    throw new SnapshotInvariantException();
                }

                pending.Add(
                    new StoredEvent(
                        canonicalEvent.EventId,
                        entry.Sequence,
                        entry.AttemptCount,
                        canonicalEvent.CanonicalJson));
                previousPendingSequence = entry.Sequence;
                maximumSequence = Math.Max(maximumSequence, entry.Sequence);
            }

            long previousDeadLetterSequence = 0;
            foreach (EventOutboxDeadLetterEntry entry in snapshot.DeadLetters)
            {
                if (entry is null
                    || entry.Sequence < 1
                    || entry.AttemptCount < 0
                    || entry.Sequence <= previousDeadLetterSequence
                    || !IsStableString(entry.ReasonCode))
                {
                    throw new SnapshotInvariantException();
                }

                CanonicalEvent canonicalEvent = CreateCanonicalEventFromSnapshot(entry.CanonicalEventJson);
                if (!eventIds.Add(canonicalEvent.EventId) || !sequences.Add(entry.Sequence))
                {
                    // 同 event_id/sequence 跨 pending 与 dead letter 也属于冲突，不允许自动选择一侧。
                    throw new SnapshotInvariantException();
                }

                deadLetters.Add(
                    new StoredDeadLetter(
                        canonicalEvent.EventId,
                        entry.Sequence,
                        entry.AttemptCount,
                        entry.ReasonCode,
                        canonicalEvent.CanonicalJson));
                previousDeadLetterSequence = entry.Sequence;
                maximumSequence = Math.Max(maximumSequence, entry.Sequence);
            }

            if (snapshot.NextSequence <= maximumSequence)
            {
                throw new SnapshotInvariantException();
            }

            bool producerCheckpointsAvailable = TryRestoreProducerCheckpoints(
                snapshot,
                out IReadOnlyList<StoredProducerCheckpoint> producerCheckpoints);
            string? opaqueProducerCheckpointsJson = null;
            if (!producerCheckpointsAvailable)
            {
                if (snapshot.FormatVersion != SnapshotFormatVersion
                    || snapshot.ProducerCheckpointsSourceJson is null)
                {
                    throw new SnapshotInvariantException();
                }

                // 只隔离 checkpoint 子域。后续普通 outbox mutation 会原样重写这段 JSON，
                // 不会用空数组覆盖损坏，从而避免 friendship producer 误判为“从未发生”。
                opaqueProducerCheckpointsJson = snapshot.ProducerCheckpointsSourceJson;
            }

            return new EventOutboxState(
                snapshot.NextSequence,
                snapshot.MemoryRevision,
                snapshot.CommittedThroughDayIndex,
                Array.AsReadOnly(pending.ToArray()),
                Array.AsReadOnly(deadLetters.ToArray()),
                producerCheckpoints,
                opaqueProducerCheckpointsJson);
        }
        catch (OutboxCorruptedException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or SnapshotInvariantException)
        {
            throw new OutboxCorruptedException(CorruptedSnapshotMessage, exception);
        }
    }

    /// <summary>
    /// 尝试恢复 checkpoint 子域；失败只返回 unavailable，不污染已经验证的事件队列。
    /// </summary>
    private static bool TryRestoreProducerCheckpoints(
        EventOutboxSnapshot snapshot,
        out IReadOnlyList<StoredProducerCheckpoint> restored)
    {
        restored = Array.Empty<StoredProducerCheckpoint>();
        if (!snapshot.ProducerCheckpointsStructurallyValid
            || snapshot.ProducerCheckpoints.Count > MaximumProducerCheckpoints)
        {
            return false;
        }

        List<StoredProducerCheckpoint> checkpoints = new(snapshot.ProducerCheckpoints.Count);
        string? previousProducerId = null;
        foreach (EventOutboxProducerCheckpointEntry entry in snapshot.ProducerCheckpoints)
        {
            if (entry is null
                || !IsValidProducerCheckpointString(
                    entry.ProducerId,
                    MaximumProducerIdCharacters)
                || !IsValidProducerCheckpointString(
                    entry.Status,
                    MaximumCheckpointStatusCharacters)
                || entry.ObservedDayIndex < 0
                || (previousProducerId is not null
                    && string.CompareOrdinal(previousProducerId, entry.ProducerId) >= 0))
            {
                // 严格升序同时排除重复 key，避免静默选择两个冲突状态中的一个。
                return false;
            }

            checkpoints.Add(
                new StoredProducerCheckpoint(
                    entry.ProducerId,
                    entry.Status,
                    entry.ObservedDayIndex));
            previousProducerId = entry.ProducerId;
        }

        restored = Array.AsReadOnly(checkpoints.ToArray());
        return true;
    }

    /// <summary>
    /// 验证公开 batch 自身仍是 CreatePendingBatch 产生的合法结构，并生成 immutable 核对值。
    /// </summary>
    private ValidatedBatch ValidateBatchSnapshot(PendingEventBatch batch)
    {
        if (batch.Request is null || batch.Identities is null)
        {
            throw new ArgumentException("Pending event batch 缺少 request 或 identities。", nameof(batch));
        }

        ContractValidationResult requestValidation = ContractValidator.Validate(batch.Request);
        if (!requestValidation.IsValid
            || !string.Equals(batch.Request.SaveId, saveId, StringComparison.Ordinal)
            || !string.Equals(batch.Request.PlayerId, playerId, StringComparison.Ordinal)
            || batch.Identities.Count != batch.Request.Events.Count)
        {
            throw new ArgumentException("Pending event batch 不符合当前 outbox 分区或合同。", nameof(batch));
        }

        List<ValidatedBatchItem> validatedItems = new(batch.Request.Events.Count);
        HashSet<string> eventIds = new(StringComparer.Ordinal);
        HashSet<long> sequences = new();
        long previousSequence = 0;
        for (int index = 0; index < batch.Request.Events.Count; index++)
        {
            GameEvent gameEvent = batch.Request.Events[index];
            PendingEventIdentity? identity = batch.Identities[index];
            if (identity is null
                || identity.Sequence < 1
                || identity.Sequence <= previousSequence
                || !IsStableString(identity.EventId)
                || !IsStableString(identity.CanonicalEventJson))
            {
                throw new ArgumentException("Pending event batch identity 非法。", nameof(batch));
            }

            CanonicalEvent canonicalEvent;
            try
            {
                canonicalEvent = CreateCanonicalEventFromCaller(gameEvent);
            }
            catch (ArgumentException exception)
            {
                throw new ArgumentException("Pending event batch 包含非法事件。", nameof(batch), exception);
            }

            if (!string.Equals(identity.EventId, canonicalEvent.EventId, StringComparison.Ordinal)
                || !string.Equals(
                    identity.CanonicalEventJson,
                    canonicalEvent.CanonicalJson,
                    StringComparison.Ordinal)
                || !eventIds.Add(identity.EventId)
                || !sequences.Add(identity.Sequence))
            {
                throw new ArgumentException("Pending event batch identity 与事件内容不一致。", nameof(batch));
            }

            validatedItems.Add(
                new ValidatedBatchItem(
                    identity.EventId,
                    identity.Sequence,
                    identity.CanonicalEventJson));
            previousSequence = identity.Sequence;
        }

        return new ValidatedBatch(
            batch.Request.RequestId,
            Array.AsReadOnly(validatedItems.ToArray()));
    }

    /// <summary>
    /// 对仍存在的 batch identity 核对当前 pending sequence、canonical JSON 与相对顺序。
    /// </summary>
    /// <returns>只含当前仍 pending 的旧 batch 项；全部已处理时为空。</returns>
    private IReadOnlyDictionary<string, StoredEvent> ValidateBatchAgainstCurrentState(
        ValidatedBatch validatedBatch)
    {
        Dictionary<string, (StoredEvent Item, int Index)> pendingByEventId = state.Pending
            .Select((item, index) => (item, index))
            .ToDictionary(pair => pair.item.EventId, pair => (pair.item, pair.index), StringComparer.Ordinal);
        Dictionary<string, StoredEvent> currentItems = new(StringComparer.Ordinal);
        int previousCurrentIndex = -1;
        foreach (ValidatedBatchItem identity in validatedBatch.Items)
        {
            if (!pendingByEventId.TryGetValue(identity.EventId, out (StoredEvent Item, int Index) current))
            {
                // 旧 response 重放时 identity 已不存在属于正常已处理状态。
                continue;
            }

            if (current.Index <= previousCurrentIndex
                || current.Item.Sequence != identity.Sequence
                || !string.Equals(
                    current.Item.CanonicalEventJson,
                    identity.CanonicalEventJson,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException("Pending event batch 已被修改或不再匹配当前状态。", nameof(validatedBatch));
            }

            currentItems.Add(identity.EventId, current.Item);
            previousCurrentIndex = current.Index;
        }

        return currentItems;
    }

    /// <summary>
    /// 验证后端响应合同及其与请求的逐项映射。
    /// </summary>
    private static void ValidateResponseSnapshot(
        ValidatedBatch batch,
        GameEventBatchResponse response)
    {
        ContractValidationResult responseValidation = ContractValidator.Validate(response);
        if (!responseValidation.IsValid
            || !string.Equals(response.RequestId, batch.RequestId, StringComparison.Ordinal)
            || response.Items.Count != batch.Items.Count)
        {
            throw new ArgumentException("事件响应不符合合同或 batch envelope。", nameof(response));
        }

        for (int index = 0; index < batch.Items.Count; index++)
        {
            GameEventItemResult responseItem = response.Items[index];
            bool declaredStatus = responseItem.Status is EventIngestionStatus.Accepted
                or EventIngestionStatus.Duplicate
                or EventIngestionStatus.Rejected;
            if (!declaredStatus
                || !string.Equals(
                    responseItem.EventId,
                    batch.Items[index].EventId,
                    StringComparison.Ordinal))
            {
                // 手工构造的未声明 enum 不会经过 JSON converter，因此必须在这里显式拒绝。
                throw new ArgumentException("事件响应条目顺序、身份或 status 非法。", nameof(response));
            }
        }
    }

    /// <summary>
    /// 验证后端全局水位是合法状态且不回退当前已持久化进度。
    /// </summary>
    /// <remarks>
    /// response item 与水位必须在同一次 copy-on-write 中应用；先做本检查，才能保证坏响应
    /// 不会删除 pending 后才发现次日生成所需 revision 已倒退。
    /// </remarks>
    private void ValidateResponseWatermark(GameEventBatchResponse response)
    {
        if (!IsValidWatermark(
                response.MemoryRevision,
                response.CommittedThroughDayIndex)
            || response.MemoryRevision < state.MemoryRevision
            || response.CommittedThroughDayIndex < state.CommittedThroughDayIndex)
        {
            throw new ArgumentException("事件响应 memory/day 水位非法或发生回退。", nameof(response));
        }
    }

    /// <summary>
    /// 判断 memory revision 与 committed day 是否组成后端允许的完整分区状态。
    /// </summary>
    private static bool IsValidWatermark(int memoryRevision, int committedThroughDayIndex)
    {
        return (memoryRevision == 0 && committedThroughDayIndex == -1)
            || (memoryRevision > 0 && committedThroughDayIndex >= 0);
    }

    /// <summary>
    /// 将调用方事件验证、序列化并 canonicalize；返回值不保留 DTO 或 JsonDocument 引用。
    /// </summary>
    private static CanonicalEvent CreateCanonicalEventFromCaller(GameEvent gameEvent)
    {
        ArgumentNullException.ThrowIfNull(gameEvent);
        ContractValidationResult validation = ContractValidator.Validate(gameEvent);
        if (!validation.IsValid)
        {
            throw new ArgumentException(
                $"gameEvent 不符合共享合同：{validation}",
                nameof(gameEvent));
        }

        try
        {
            string canonicalJson = CanonicalizeJsonObject(ContractJson.Serialize(gameEvent));
            GameEvent immutableCopy = ContractJson.Deserialize<GameEvent>(canonicalJson);
            return new CanonicalEvent(immutableCopy.EventId, canonicalJson);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("gameEvent 无法序列化为严格合同 JSON。", nameof(gameEvent), exception);
        }
    }

    /// <summary>
    /// 将 snapshot 内的 event object 交给正式 ContractJson 和单事件 validator，再规范化顺序。
    /// </summary>
    private static CanonicalEvent CreateCanonicalEventFromSnapshot(string eventJson)
    {
        GameEvent gameEvent = ContractJson.Deserialize<GameEvent>(eventJson);
        ContractValidationResult validation = ContractValidator.Validate(gameEvent);
        if (!validation.IsValid)
        {
            throw new SnapshotInvariantException();
        }

        string canonicalJson = CanonicalizeJsonObject(ContractJson.Serialize(gameEvent));
        return new CanonicalEvent(gameEvent.EventId, canonicalJson);
    }

    /// <summary>
    /// 从内部 canonical JSON 创建一个全新 DTO，确保公开快照是深拷贝。
    /// </summary>
    private static GameEvent DeserializeCanonicalEvent(string canonicalEventJson)
    {
        return ContractJson.Deserialize<GameEvent>(canonicalEventJson);
    }

    /// <summary>
    /// 递归按 ordinal 排序 object property；数组元素顺序和 JSON scalar 值保持原样。
    /// </summary>
    private static string CanonicalizeJsonObject(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Canonical event 根值必须是 JSON object。");
        }

        // Contract payload 允许动态 object，但重复 key 会让“同一事实”取决于具体 JSON
        // 读取器采用 first-wins 还是 last-wins。首次 Enqueue 就拒绝，避免写出下次重启
        // 必然被严格 snapshot reader 判为损坏的自相矛盾文件。
        EnsureNoDuplicateObjectKeys(document.RootElement, "$event");

        using MemoryStream output = new();
        using (Utf8JsonWriter writer = new(output, new JsonWriterOptions { Indented = false }))
        {
            WriteCanonicalElement(writer, document.RootElement);
        }

        return Encoding.UTF8.GetString(output.ToArray());
    }

    /// <summary>
    /// 递归拒绝 event 及动态 payload 中语义不唯一的重复 object key。
    /// </summary>
    private static void EnsureNoDuplicateObjectKeys(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new JsonException($"{path} 包含重复 object key {property.Name}。");
                }

                EnsureNoDuplicateObjectKeys(property.Value, $"{path}.{property.Name}");
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (JsonElement item in element.EnumerateArray())
            {
                EnsureNoDuplicateObjectKeys(item, $"{path}[{index}]");
                index++;
            }
        }
    }

    /// <summary>
    /// canonical writer 的递归核心；只对 object key 排序，不重排数组或改变 number token。
    /// </summary>
    private static void WriteCanonicalElement(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in element
                    .EnumerateObject()
                    .OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalElement(writer, property.Value);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    WriteCanonicalElement(writer, item);
                }

                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;

            case JsonValueKind.Number:
                // JsonElement.WriteTo 对单个 number 保留其合法 token，不把 1.0 偷换成 1。
                element.WriteTo(writer);
                break;

            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;

            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;

            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;

            default:
                throw new JsonException("Canonical event 包含未定义 JSON 值。");
        }
    }

    /// <summary>
    /// 校验公开 string 参数非空且没有首尾空白；不做静默 Trim。
    /// </summary>
    private static void ValidateStableString(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (!IsStableString(value))
        {
            throw new ArgumentException("值必须非空且不能包含首尾空白。", parameterName);
        }
    }

    /// <summary>
    /// 判断持久化身份或 reason code 是否满足稳定字符串规则。
    /// </summary>
    private static bool IsStableString(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && string.Equals(value, value.Trim(), StringComparison.Ordinal);
    }

    /// <summary>
    /// 校验公开 checkpoint mutation 的输入边界；不允许静默 Trim 或截断持久身份。
    /// </summary>
    private static void ValidateProducerCheckpointInput(
        string producerId,
        string status,
        int observedDayIndex)
    {
        ValidateStableString(producerId, nameof(producerId));
        ValidateStableString(status, nameof(status));
        if (producerId.Length > MaximumProducerIdCharacters)
        {
            throw new ArgumentException(
                $"producerId 不能超过 {MaximumProducerIdCharacters} 个字符。",
                nameof(producerId));
        }

        if (status.Length > MaximumCheckpointStatusCharacters)
        {
            throw new ArgumentException(
                $"status 不能超过 {MaximumCheckpointStatusCharacters} 个字符。",
                nameof(status));
        }

        if (observedDayIndex < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(observedDayIndex),
                observedDayIndex,
                "observedDayIndex 必须大于等于 0。");
        }
    }

    /// <summary>
    /// 恢复路径使用的无异常布尔校验，和公开 mutation 保持完全相同的字符串合同。
    /// </summary>
    private static bool IsValidProducerCheckpointString(string? value, int maximumCharacters)
    {
        return IsStableString(value) && value!.Length <= maximumCharacters;
    }

    /// <summary>
    /// 防止 checkpoint 腐化被调用方误解成“从未初始化”的空集合。
    /// </summary>
    /// <remarks>调用方必须持有 synchronizationGate。</remarks>
    private void EnsureProducerCheckpointsAvailable()
    {
        if (state.OpaqueProducerCheckpointsJson is not null)
        {
            throw new InvalidOperationException(
                "Producer checkpoint 区域已损坏；相关 producer 必须停用，不能从当前游戏状态猜测修复。");
        }
    }

    /// <summary>
    /// 在已按 ProducerId 排序、最多 128 项的小集合中定位 checkpoint。
    /// </summary>
    /// <remarks>
    /// 这里保留线性扫描以维持单一 immutable list 表示；当前冻结上限下无需再维护一份会漂移的 map。
    /// 调用方必须持有 synchronizationGate。
    /// </remarks>
    private int FindProducerCheckpointIndex(string producerId)
    {
        for (int index = 0; index < state.ProducerCheckpoints.Count; index++)
        {
            if (string.Equals(
                state.ProducerCheckpoints[index].ProducerId,
                producerId,
                StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// 在 pending 与 dead letter 中查找一个 event identity 已绑定的 canonical 事实。
    /// </summary>
    /// <remarks>调用方必须持有 synchronizationGate；恢复阶段已保证两个集合不会重复 event_id。</remarks>
    private string? FindStoredCanonicalEventJson(string eventId)
    {
        StoredEvent? existingPending = state.Pending.FirstOrDefault(
            item => string.Equals(item.EventId, eventId, StringComparison.Ordinal));
        StoredDeadLetter? existingDeadLetter = state.DeadLetters.FirstOrDefault(
            item => string.Equals(item.EventId, eventId, StringComparison.Ordinal));
        return existingPending?.CanonicalEventJson ?? existingDeadLetter?.CanonicalEventJson;
    }

    /// <summary>
    /// 私有 immutable 状态；集合只在构造新 state 时创建，之后从不原地修改。
    /// </summary>
    private sealed record EventOutboxState(
        long NextSequence,
        int MemoryRevision,
        int CommittedThroughDayIndex,
        IReadOnlyList<StoredEvent> Pending,
        IReadOnlyList<StoredDeadLetter> DeadLetters,
        IReadOnlyList<StoredProducerCheckpoint> ProducerCheckpoints,
        string? OpaqueProducerCheckpointsJson)
    {
        public static EventOutboxState Empty { get; } = new(
            NextSequence: 1,
            MemoryRevision: 0,
            CommittedThroughDayIndex: -1,
            Array.Empty<StoredEvent>(),
            Array.Empty<StoredDeadLetter>(),
            Array.Empty<StoredProducerCheckpoint>(),
            OpaqueProducerCheckpointsJson: null);
    }

    /// <summary>
    /// 私有 pending 值对象。
    /// </summary>
    private sealed record StoredEvent(
        string EventId,
        long Sequence,
        int AttemptCount,
        string CanonicalEventJson);

    /// <summary>
    /// 私有 dead-letter 值对象。
    /// </summary>
    private sealed record StoredDeadLetter(
        string EventId,
        long Sequence,
        int AttemptCount,
        string ReasonCode,
        string CanonicalEventJson);

    /// <summary>
    /// 私有 producer checkpoint 值对象；集合始终按 ProducerId ordinal 升序。
    /// </summary>
    private sealed record StoredProducerCheckpoint(
        string ProducerId,
        string Status,
        int ObservedDayIndex);

    /// <summary>
    /// 完成验证后的 immutable event identity。
    /// </summary>
    private sealed record CanonicalEvent(string EventId, string CanonicalJson);

    /// <summary>
    /// 完成自校验的 batch，不保留调用方 DTO/集合引用。
    /// </summary>
    private sealed record ValidatedBatch(
        string RequestId,
        IReadOnlyList<ValidatedBatchItem> Items);

    /// <summary>
    /// 完成自校验的一条 batch identity。
    /// </summary>
    private sealed record ValidatedBatchItem(
        string EventId,
        long Sequence,
        string CanonicalEventJson);

    /// <summary>
    /// 仅用于把 snapshot 领域不变量失败统一映射为 OutboxCorruptedException。
    /// </summary>
    private sealed class SnapshotInvariantException : Exception
    {
    }
}

/// <summary>
/// Event outbox v1/v2 snapshot 的严格 JSON converter。
/// </summary>
/// <remarks>
/// System.Text.Json 默认会接受未知字段并让重复 key 后值覆盖前值；对崩溃恢复文件，这会把
/// 模糊状态静默解释成合法状态。因此 converter 先读取 raw JsonElement，逐层冻结 required/
/// unknown/duplicate key 与 integer token，再把 event object 原文交给领域恢复逻辑。
/// </remarks>
internal sealed class EventOutboxSnapshotJsonConverter : JsonConverter<EventOutboxSnapshot>
{
    private static readonly string[] SnapshotPropertiesV1 =
    {
        "format_version",
        "save_id",
        "player_id",
        "memory_revision",
        "committed_through_day_index",
        "next_sequence",
        "pending",
        "dead_letters",
    };

    private static readonly string[] SnapshotPropertiesV2 =
    {
        "format_version",
        "save_id",
        "player_id",
        "memory_revision",
        "committed_through_day_index",
        "next_sequence",
        "pending",
        "dead_letters",
        "producer_checkpoints",
    };

    private static readonly string[] PendingProperties =
    {
        "sequence",
        "attempt_count",
        "event",
    };

    private static readonly string[] DeadLetterProperties =
    {
        "sequence",
        "attempt_count",
        "reason_code",
        "event",
    };

    private static readonly string[] ProducerCheckpointProperties =
    {
        "producer_id",
        "status",
        "observed_day_index",
    };

    /// <summary>
    /// 创建 AtomicJsonFile 专用的冻结 serializer 配置。
    /// </summary>
    internal static JsonSerializerOptions CreateSerializerOptions()
    {
        JsonSerializerOptions options = new()
        {
            AllowTrailingCommas = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            PropertyNameCaseInsensitive = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            WriteIndented = false,
        };
        options.Converters.Add(new EventOutboxSnapshotJsonConverter());
        return options;
    }

    /// <summary>
    /// 严格读取完整 snapshot，并保留 event 为 object 原文而非转义字符串。
    /// </summary>
    public override EventOutboxSnapshot Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        IReadOnlyDictionary<string, JsonElement> properties = ReadAllowedObject(
            document.RootElement,
            "$",
            SnapshotPropertiesV2);
        if (!properties.TryGetValue("format_version", out JsonElement formatVersionElement))
        {
            throw new JsonException("$ 缺少字段 format_version。");
        }

        int formatVersion = ReadInt32(formatVersionElement, "$.format_version");
        // v1 是唯一没有 checkpoint 字段的受支持历史 shape；其他版本先按最新 shape 做严格
        // JSON 校验，再由领域恢复明确拒绝未知 format version。
        EnsureExactProperties(
            properties,
            "$",
            formatVersion == 1 ? SnapshotPropertiesV1 : SnapshotPropertiesV2);
        string saveId = ReadString(properties["save_id"], "$.save_id");
        string playerId = ReadString(properties["player_id"], "$.player_id");
        int memoryRevision = ReadInt32(properties["memory_revision"], "$.memory_revision");
        int committedThroughDayIndex = ReadInt32(
            properties["committed_through_day_index"],
            "$.committed_through_day_index");
        long nextSequence = ReadInt64(properties["next_sequence"], "$.next_sequence");
        IReadOnlyList<EventOutboxSnapshotEntry> pending = ReadPending(
            properties["pending"],
            "$.pending");
        IReadOnlyList<EventOutboxDeadLetterEntry> deadLetters = ReadDeadLetters(
            properties["dead_letters"],
            "$.dead_letters");
        IReadOnlyList<EventOutboxProducerCheckpointEntry> producerCheckpoints =
            Array.Empty<EventOutboxProducerCheckpointEntry>();
        bool producerCheckpointsStructurallyValid = true;
        string? producerCheckpointsSourceJson = null;
        if (formatVersion != 1)
        {
            JsonElement producerCheckpointElement = properties["producer_checkpoints"];
            producerCheckpointsSourceJson = producerCheckpointElement.GetRawText();
            try
            {
                producerCheckpoints = ReadProducerCheckpoints(
                    producerCheckpointElement,
                    "$.producer_checkpoints");
            }
            catch (JsonException)
            {
                // checkpoint 是可隔离子域：保留原始 JSON，留给领域恢复标记 unavailable；
                // pending/dead-letter/watermark 仍按严格合同继续恢复。
                producerCheckpointsStructurallyValid = false;
            }
        }

        return new EventOutboxSnapshot(
            formatVersion,
            saveId,
            playerId,
            memoryRevision,
            committedThroughDayIndex,
            nextSequence,
            pending,
            deadLetters,
            producerCheckpoints,
            producerCheckpointsStructurallyValid,
            producerCheckpointsSourceJson);
    }

    /// <summary>
    /// 以冻结字段顺序写 snapshot；event JSON 直接写 object token，不执行字符串二次编码。
    /// </summary>
    public override void Write(
        Utf8JsonWriter writer,
        EventOutboxSnapshot value,
        JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Pending is null
            || value.DeadLetters is null
            || value.ProducerCheckpoints is null)
        {
            throw new JsonException("Event outbox snapshot 集合不能为 null。");
        }

        writer.WriteStartObject();
        writer.WriteNumber("format_version", value.FormatVersion);
        writer.WriteString("save_id", value.SaveId);
        writer.WriteString("player_id", value.PlayerId);
        writer.WriteNumber("memory_revision", value.MemoryRevision);
        writer.WriteNumber("committed_through_day_index", value.CommittedThroughDayIndex);
        writer.WriteNumber("next_sequence", value.NextSequence);

        writer.WritePropertyName("pending");
        writer.WriteStartArray();
        foreach (EventOutboxSnapshotEntry entry in value.Pending)
        {
            if (entry is null)
            {
                throw new JsonException("Event outbox pending entry 不能为 null。");
            }

            writer.WriteStartObject();
            writer.WriteNumber("sequence", entry.Sequence);
            writer.WriteNumber("attempt_count", entry.AttemptCount);
            writer.WritePropertyName("event");
            WriteEventObject(writer, entry.CanonicalEventJson);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WritePropertyName("dead_letters");
        writer.WriteStartArray();
        foreach (EventOutboxDeadLetterEntry entry in value.DeadLetters)
        {
            if (entry is null)
            {
                throw new JsonException("Event outbox dead-letter entry 不能为 null。");
            }

            writer.WriteStartObject();
            writer.WriteNumber("sequence", entry.Sequence);
            writer.WriteNumber("attempt_count", entry.AttemptCount);
            writer.WriteString("reason_code", entry.ReasonCode);
            writer.WritePropertyName("event");
            WriteEventObject(writer, entry.CanonicalEventJson);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WritePropertyName("producer_checkpoints");
        if (!value.ProducerCheckpointsStructurallyValid)
        {
            if (value.ProducerCheckpoints.Count != 0
                || value.ProducerCheckpointsSourceJson is null)
            {
                throw new JsonException("不可用 checkpoint snapshot 缺少唯一的不透明 JSON 来源。");
            }

            WriteOpaqueJsonValue(writer, value.ProducerCheckpointsSourceJson);
        }
        else
        {
            writer.WriteStartArray();
            foreach (EventOutboxProducerCheckpointEntry entry in value.ProducerCheckpoints)
            {
                if (entry is null)
                {
                    throw new JsonException("Event outbox producer checkpoint entry 不能为 null。");
                }

                writer.WriteStartObject();
                writer.WriteString("producer_id", entry.ProducerId);
                writer.WriteString("status", entry.Status);
                writer.WriteNumber("observed_day_index", entry.ObservedDayIndex);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// 读取 pending 数组；每个 entry 只接受冻结字段集合。
    /// </summary>
    private static IReadOnlyList<EventOutboxSnapshotEntry> ReadPending(
        JsonElement element,
        string path)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException($"{path} 必须是 JSON array。");
        }

        List<EventOutboxSnapshotEntry> entries = new();
        int index = 0;
        foreach (JsonElement item in element.EnumerateArray())
        {
            string itemPath = $"{path}[{index}]";
            IReadOnlyDictionary<string, JsonElement> properties = ReadExactObject(
                item,
                itemPath,
                PendingProperties);
            entries.Add(
                new EventOutboxSnapshotEntry(
                    ReadInt64(properties["sequence"], $"{itemPath}.sequence"),
                    ReadInt32(properties["attempt_count"], $"{itemPath}.attempt_count"),
                    ReadEventObjectJson(properties["event"], $"{itemPath}.event")));
            index++;
        }

        return Array.AsReadOnly(entries.ToArray());
    }

    /// <summary>
    /// 读取 dead_letters 数组；reason code 只读取字符串，非空语义由领域恢复统一判断。
    /// </summary>
    private static IReadOnlyList<EventOutboxDeadLetterEntry> ReadDeadLetters(
        JsonElement element,
        string path)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException($"{path} 必须是 JSON array。");
        }

        List<EventOutboxDeadLetterEntry> entries = new();
        int index = 0;
        foreach (JsonElement item in element.EnumerateArray())
        {
            string itemPath = $"{path}[{index}]";
            IReadOnlyDictionary<string, JsonElement> properties = ReadExactObject(
                item,
                itemPath,
                DeadLetterProperties);
            entries.Add(
                new EventOutboxDeadLetterEntry(
                    ReadInt64(properties["sequence"], $"{itemPath}.sequence"),
                    ReadInt32(properties["attempt_count"], $"{itemPath}.attempt_count"),
                    ReadString(properties["reason_code"], $"{itemPath}.reason_code"),
                    ReadEventObjectJson(properties["event"], $"{itemPath}.event")));
            index++;
        }

        return Array.AsReadOnly(entries.ToArray());
    }

    /// <summary>
    /// 读取 v2 producer_checkpoints；字符串边界、排序和唯一性由领域恢复统一校验。
    /// </summary>
    private static IReadOnlyList<EventOutboxProducerCheckpointEntry> ReadProducerCheckpoints(
        JsonElement element,
        string path)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException($"{path} 必须是 JSON array。");
        }

        List<EventOutboxProducerCheckpointEntry> entries = new();
        int index = 0;
        foreach (JsonElement item in element.EnumerateArray())
        {
            string itemPath = $"{path}[{index}]";
            IReadOnlyDictionary<string, JsonElement> properties = ReadExactObject(
                item,
                itemPath,
                ProducerCheckpointProperties);
            entries.Add(
                new EventOutboxProducerCheckpointEntry(
                    ReadString(properties["producer_id"], $"{itemPath}.producer_id"),
                    ReadString(properties["status"], $"{itemPath}.status"),
                    ReadInt32(
                        properties["observed_day_index"],
                        $"{itemPath}.observed_day_index")));
            index++;
        }

        return Array.AsReadOnly(entries.ToArray());
    }

    /// <summary>
    /// 读取 required-only object，同时拒绝 unknown、missing 和重复 JSON key。
    /// </summary>
    private static IReadOnlyDictionary<string, JsonElement> ReadExactObject(
        JsonElement element,
        string path,
        IReadOnlyList<string> requiredProperties)
    {
        IReadOnlyDictionary<string, JsonElement> values = ReadAllowedObject(
            element,
            path,
            requiredProperties);
        EnsureExactProperties(values, path, requiredProperties);
        return values;
    }

    /// <summary>
    /// 读取 object 并拒绝允许集合之外或重复的 key；版本化 root 会在随后选择 exact shape。
    /// </summary>
    private static IReadOnlyDictionary<string, JsonElement> ReadAllowedObject(
        JsonElement element,
        string path,
        IReadOnlyList<string> allowedProperties)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"{path} 必须是 JSON object。");
        }

        HashSet<string> allowed = allowedProperties.ToHashSet(StringComparer.Ordinal);
        Dictionary<string, JsonElement> values = new(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw new JsonException($"{path} 包含未知字段 {property.Name}。");
            }

            if (!values.TryAdd(property.Name, property.Value))
            {
                throw new JsonException($"{path} 包含重复字段 {property.Name}。");
            }
        }

        return values;
    }

    /// <summary>
    /// 在已经拒绝重复 key 后，进一步冻结 required-only shape。
    /// </summary>
    private static void EnsureExactProperties(
        IReadOnlyDictionary<string, JsonElement> values,
        string path,
        IReadOnlyList<string> requiredProperties)
    {
        HashSet<string> required = requiredProperties.ToHashSet(StringComparer.Ordinal);
        foreach (string actualProperty in values.Keys)
        {
            if (!required.Contains(actualProperty))
            {
                throw new JsonException($"{path} 包含未知字段 {actualProperty}。");
            }
        }

        foreach (string requiredProperty in requiredProperties)
        {
            if (!values.ContainsKey(requiredProperty))
            {
                throw new JsonException($"{path} 缺少字段 {requiredProperty}。");
            }
        }
    }

    /// <summary>
    /// 读取严格 JSON string；显式 null 不会被折叠为 C# null 后继续运行。
    /// </summary>
    private static string ReadString(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            throw new JsonException($"{path} 必须是 JSON string。");
        }

        return element.GetString()
            ?? throw new JsonException($"{path} 不能为 null。");
    }

    /// <summary>
    /// 读取不含小数点/指数的 Int32 token。
    /// </summary>
    private static int ReadInt32(JsonElement element, string path)
    {
        ValidateIntegerToken(element, path);
        if (!element.TryGetInt32(out int value))
        {
            throw new JsonException($"{path} 超出 Int32 范围。");
        }

        return value;
    }

    /// <summary>
    /// 读取不含小数点/指数的 Int64 token。
    /// </summary>
    private static long ReadInt64(JsonElement element, string path)
    {
        ValidateIntegerToken(element, path);
        if (!element.TryGetInt64(out long value))
        {
            throw new JsonException($"{path} 超出 Int64 范围。");
        }

        return value;
    }

    /// <summary>
    /// snapshot 本地计数也只接受 JSON integer lexical token，拒绝 1.0 和 1e0。
    /// </summary>
    private static void ValidateIntegerToken(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Number)
        {
            throw new JsonException($"{path} 必须是 JSON number。");
        }

        string raw = element.GetRawText();
        if (raw.Contains('.') || raw.Contains('e') || raw.Contains('E'))
        {
            throw new JsonException($"{path} 必须使用 JSON integer token。");
        }
    }

    /// <summary>
    /// 读取 event object 原文，并递归拒绝其内部 duplicate object key。
    /// </summary>
    private static string ReadEventObjectJson(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"{path} 必须是 JSON object，不能是转义字符串。");
        }

        EnsureNoDuplicateObjectKeys(element, path);
        return element.GetRawText();
    }

    /// <summary>
    /// 动态 payload 不限制字段名，但同一 object 内重复字段会导致语义取值不唯一，因此拒绝。
    /// </summary>
    private static void EnsureNoDuplicateObjectKeys(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new JsonException($"{path} 包含重复 object key {property.Name}。");
                }

                EnsureNoDuplicateObjectKeys(property.Value, $"{path}.{property.Name}");
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (JsonElement item in element.EnumerateArray())
            {
                EnsureNoDuplicateObjectKeys(item, $"{path}[{index}]");
                index++;
            }
        }
    }

    /// <summary>
    /// 把 canonical event JSON 作为 object token 写入 snapshot。
    /// </summary>
    private static void WriteEventObject(Utf8JsonWriter writer, string canonicalEventJson)
    {
        using JsonDocument document = JsonDocument.Parse(canonicalEventJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Event outbox event 必须是 JSON object。");
        }

        document.RootElement.WriteTo(writer);
    }

    /// <summary>
    /// 重写已隔离的 checkpoint JSON value，不解释、修复或替换其中内容。
    /// </summary>
    /// <remarks>
    /// JsonDocument 会去除无意义空白，但保留 value kind、重复 object key 与原始标量值；这足以
    /// 防止后续普通事件 mutation 把不可解释状态偷换成“空 checkpoint”。
    /// </remarks>
    private static void WriteOpaqueJsonValue(Utf8JsonWriter writer, string sourceJson)
    {
        using JsonDocument document = JsonDocument.Parse(sourceJson);
        document.RootElement.WriteTo(writer);
    }
}
