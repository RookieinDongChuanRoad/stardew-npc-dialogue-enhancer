using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using StardewNpcAgent.Contracts;

namespace StardewNpcAgent.Infrastructure.Storage;

/// <summary>
/// 一次 displayed ACK 网络尝试的公开深拷贝。
/// </summary>
/// <param name="GenerationId">后端 generation 路径身份。</param>
/// <param name="Request">展示回执请求的可修改副本。</param>
/// <param name="Sequence">本地 FIFO 单调序号。</param>
/// <param name="AttemptCount">已经持久化的瞬态失败次数。</param>
/// <param name="CanonicalRequestJson">递归排序 object key 后的完整请求 JSON。</param>
/// <remarks>
/// 该 record 不是授权令牌。任一 mutation API 都会重新验证 request、canonical JSON、
/// generation、sequence 和 attempt，并与当前 FIFO 首项精确比较。
/// </remarks>
public sealed record PendingDisplayAck(
    string GenerationId,
    DisplayAckRequest Request,
    long Sequence,
    int AttemptCount,
    string CanonicalRequestJson);

/// <summary>
/// 已确认不可继续自动重试的展示回执快照。
/// </summary>
/// <param name="GenerationId">原 generation 路径身份。</param>
/// <param name="Request">从内部 canonical JSON 新建的请求深拷贝。</param>
/// <param name="Sequence">原 FIFO 序号。</param>
/// <param name="AttemptCount">进入 dead letter 前累计的瞬态失败次数。</param>
/// <param name="ReasonCode">稳定业务原因码，不包含异常正文或 HTTP body。</param>
public sealed record DisplayAckDeadLetter(
    string GenerationId,
    DisplayAckRequest Request,
    long Sequence,
    int AttemptCount,
    string ReasonCode);

/// <summary>
/// ACK outbox 的一次完整落盘 snapshot。
/// </summary>
/// <remarks>
/// 类型保持 internal，只服务于严格 converter、AtomicJsonFile 和测试写失败 seam。
/// snapshot 及私有 state 都只保存不可变 scalar/canonical JSON，不保留调用方 DTO 引用。
/// </remarks>
internal sealed record DisplayAckOutboxSnapshot(
    int FormatVersion,
    string SaveId,
    string PlayerId,
    long NextSequence,
    IReadOnlyList<DisplayAckOutboxSnapshotEntry> Pending,
    IReadOnlyList<DisplayAckOutboxDeadLetterEntry> DeadLetters);

/// <summary>
/// snapshot 中的一条 pending displayed ACK。
/// </summary>
internal sealed record DisplayAckOutboxSnapshotEntry(
    string GenerationId,
    long Sequence,
    int AttemptCount,
    string CanonicalRequestJson);

/// <summary>
/// snapshot 中的一条永久失败 displayed ACK。
/// </summary>
internal sealed record DisplayAckOutboxDeadLetterEntry(
    string GenerationId,
    long Sequence,
    int AttemptCount,
    string ReasonCode,
    string CanonicalRequestJson);

/// <summary>
/// 按 save/player 分区持久化 displayed ACK 的同步单项 FIFO 状态机。
/// </summary>
/// <remarks>
/// ACK endpoint 没有批量语义，因此本类一次只暴露和改变 FIFO 首项。它不发送 HTTP、不
/// 启动后台线程、不读取游戏状态。每个 mutation 都在实例锁中遵守 copy-on-write：先验证
/// 调用快照和当前首项，再构造完整新状态，AtomicJsonFile 成功落盘后才替换内存 state。
/// 实际运行边界仍是单机、受控 save-local 目录、单进程调用，不建立通用 retry 基类。
/// </remarks>
public sealed class DurableDisplayAckOutbox
{
    private const int SnapshotFormatVersion = 1;
    private const string CorruptedSnapshotMessage =
        "Display ACK outbox snapshot 不符合受支持的格式或领域不变量。";
    private readonly object synchronizationGate = new();
    private readonly string saveId;
    private readonly string playerId;
    private readonly Action<DisplayAckOutboxSnapshot> snapshotWriter;
    private DisplayAckOutboxState state;

    /// <summary>
    /// 创建已经恢复并验证过状态的实例。
    /// </summary>
    private DurableDisplayAckOutbox(
        string saveId,
        string playerId,
        Action<DisplayAckOutboxSnapshot> snapshotWriter,
        DisplayAckOutboxState state)
    {
        this.saveId = saveId;
        this.playerId = playerId;
        this.snapshotWriter = snapshotWriter;
        this.state = state;
    }

    /// <summary>
    /// 打开按 save/player 精确隔离的 durable displayed ACK outbox。
    /// </summary>
    /// <param name="absolutePath">调用方提供的 save-local 绝对文件路径。</param>
    /// <param name="saveId">稳定存档身份。</param>
    /// <param name="playerId">稳定玩家身份。</param>
    /// <returns>缺失文件对应空状态；合法 snapshot 对应恢复后的状态。</returns>
    public static DurableDisplayAckOutbox Open(
        string absolutePath,
        string saveId,
        string playerId)
    {
        return OpenCore(absolutePath, saveId, playerId, snapshotWriterOverride: null);
    }

    /// <summary>
    /// 使用真实文件读取、但注入 mutation writer 的 internal 测试入口。
    /// </summary>
    /// <remarks>
    /// seam 只用于稳定验证写失败时内存仍指向旧 state；生产入口始终使用 AtomicJsonFile。
    /// </remarks>
    internal static DurableDisplayAckOutbox Open(
        string absolutePath,
        string saveId,
        string playerId,
        Action<DisplayAckOutboxSnapshot> snapshotWriter)
    {
        ArgumentNullException.ThrowIfNull(snapshotWriter);
        return OpenCore(absolutePath, saveId, playerId, snapshotWriter);
    }

    /// <summary>
    /// 当前等待提交的 ACK 数量。
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
    /// 当前已隔离的永久失败 ACK 数量。
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
    /// 将一次已经实际展示的合法增强台词回执先持久化到本地。
    /// </summary>
    /// <param name="generationId">后端 generation 路径身份。</param>
    /// <param name="request">展示回执合同 DTO；方法不会保留该对象引用。</param>
    /// <returns>首次持久化返回 Accepted；完全相同的幂等重入返回 Duplicate。</returns>
    /// <remarks>
    /// 同 receipt、generation 与 canonical request 完全等价时为真正零写 no-op；同 receipt
    /// 对应任何不同事实都抛 identity conflict，不能用后来值覆盖原展示事实。
    /// </remarks>
    public DisplayAckStatus Enqueue(string generationId, DisplayAckRequest request)
    {
        ValidateStableString(generationId, nameof(generationId));
        CanonicalAckRequest canonicalRequest = CreateCanonicalRequestFromCaller(request);

        lock (synchronizationGate)
        {
            StoredDisplayAck? existingPending = state.Pending.FirstOrDefault(
                item => string.Equals(
                    item.DisplayReceiptId,
                    canonicalRequest.DisplayReceiptId,
                    StringComparison.Ordinal));
            StoredDisplayAckDeadLetter? existingDeadLetter = state.DeadLetters.FirstOrDefault(
                item => string.Equals(
                    item.DisplayReceiptId,
                    canonicalRequest.DisplayReceiptId,
                    StringComparison.Ordinal));

            if (existingPending is not null || existingDeadLetter is not null)
            {
                string existingGenerationId = existingPending?.GenerationId
                    ?? existingDeadLetter!.GenerationId;
                string existingCanonicalJson = existingPending?.CanonicalRequestJson
                    ?? existingDeadLetter!.CanonicalRequestJson;
                if (string.Equals(existingGenerationId, generationId, StringComparison.Ordinal)
                    && string.Equals(
                        existingCanonicalJson,
                        canonicalRequest.CanonicalJson,
                        StringComparison.Ordinal))
                {
                    return DisplayAckStatus.Duplicate;
                }

                throw new OutboxIdentityConflictException(
                    "同一 display_receipt_id 已对应不同展示事实，outbox 拒绝自动覆盖。");
            }

            if (state.NextSequence == long.MaxValue)
            {
                throw new InvalidOperationException("Display ACK outbox sequence 已达到可表示上限。");
            }

            StoredDisplayAck newItem = new(
                generationId,
                canonicalRequest.DisplayReceiptId,
                state.NextSequence,
                AttemptCount: 0,
                canonicalRequest.CanonicalJson);
            StoredDisplayAck[] nextPending = state.Pending.Append(newItem).ToArray();
            DisplayAckOutboxState nextState = new(
                state.NextSequence + 1,
                Array.AsReadOnly(nextPending),
                state.DeadLetters);
            PersistThenReplace(nextState);
            return DisplayAckStatus.Accepted;
        }
    }

    /// <summary>
    /// 返回当前 FIFO 首项的深拷贝；空 outbox 返回 null。
    /// </summary>
    public PendingDisplayAck? CreateNextAttempt()
    {
        lock (synchronizationGate)
        {
            if (state.Pending.Count == 0)
            {
                return null;
            }

            StoredDisplayAck first = state.Pending[0];
            return new PendingDisplayAck(
                first.GenerationId,
                DeserializeCanonicalRequest(first.CanonicalRequestJson),
                first.Sequence,
                first.AttemptCount,
                first.CanonicalRequestJson);
        }
    }

    /// <summary>
    /// 应用后端 accepted/duplicate 成功响应，删除一次仍匹配的 FIFO 首项。
    /// </summary>
    /// <remarks>
    /// 已经不存在的同一旧 attempt 视为成功重放并保持零写；若同 receipt 已再次出现但
    /// sequence/canonical 不同，则拒绝旧 attempt，避免删除后来追加的新事实。
    /// </remarks>
    public void MarkDelivered(PendingDisplayAck attempt, DisplayAckResponse response)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(response);

        lock (synchronizationGate)
        {
            ValidatedDisplayAckAttempt validatedAttempt = ValidateAttemptSnapshot(attempt);
            ValidateResponseSnapshot(validatedAttempt, response);
            StoredDisplayAck? current = ResolveCurrentFirstItem(validatedAttempt);
            if (current is null)
            {
                return;
            }

            DisplayAckOutboxState nextState = new(
                state.NextSequence,
                Array.AsReadOnly(state.Pending.Skip(1).ToArray()),
                state.DeadLetters);
            PersistThenReplace(nextState);
        }
    }

    /// <summary>
    /// 为仍匹配的 FIFO 首项记录一次瞬态失败。
    /// </summary>
    /// <param name="attempt">失败请求对应的原始 attempt 快照。</param>
    /// <param name="reasonCode">稳定失败分类；v1 不保存瞬态异常或 HTTP body。</param>
    public void RecordTransientFailure(PendingDisplayAck attempt, string reasonCode)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ValidateStableString(reasonCode, nameof(reasonCode));

        lock (synchronizationGate)
        {
            ValidatedDisplayAckAttempt validatedAttempt = ValidateAttemptSnapshot(attempt);
            StoredDisplayAck? current = ResolveCurrentFirstItem(validatedAttempt);
            if (current is null)
            {
                return;
            }

            if (current.AttemptCount == int.MaxValue)
            {
                throw new InvalidOperationException("Display ACK attempt_count 已达到可表示上限。");
            }

            StoredDisplayAck updated = current with
            {
                AttemptCount = current.AttemptCount + 1,
            };
            StoredDisplayAck[] nextPending = state.Pending.ToArray();
            nextPending[0] = updated;
            DisplayAckOutboxState nextState = new(
                state.NextSequence,
                Array.AsReadOnly(nextPending),
                state.DeadLetters);
            PersistThenReplace(nextState);
        }
    }

    /// <summary>
    /// 将仍匹配的 FIFO 首项移入 dead letter，并保存稳定永久失败原因。
    /// </summary>
    public void RecordPermanentFailure(PendingDisplayAck attempt, string reasonCode)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ValidateStableString(reasonCode, nameof(reasonCode));

        lock (synchronizationGate)
        {
            ValidatedDisplayAckAttempt validatedAttempt = ValidateAttemptSnapshot(attempt);
            StoredDisplayAck? current = ResolveCurrentFirstItem(validatedAttempt);
            if (current is null)
            {
                return;
            }

            StoredDisplayAckDeadLetter deadLetter = new(
                current.GenerationId,
                current.DisplayReceiptId,
                current.Sequence,
                current.AttemptCount,
                reasonCode,
                current.CanonicalRequestJson);
            DisplayAckOutboxState nextState = new(
                state.NextSequence,
                Array.AsReadOnly(state.Pending.Skip(1).ToArray()),
                Array.AsReadOnly(state.DeadLetters.Append(deadLetter).ToArray()));
            PersistThenReplace(nextState);
        }
    }

    /// <summary>
    /// 返回 dead letters 的只读深拷贝。
    /// </summary>
    public IReadOnlyList<DisplayAckDeadLetter> SnapshotDeadLetters()
    {
        lock (synchronizationGate)
        {
            DisplayAckDeadLetter[] snapshot = state.DeadLetters
                .Select(
                    item => new DisplayAckDeadLetter(
                        item.GenerationId,
                        DeserializeCanonicalRequest(item.CanonicalRequestJson),
                        item.Sequence,
                        item.AttemptCount,
                        item.ReasonCode))
                .ToArray();
            return Array.AsReadOnly(snapshot);
        }
    }

    /// <summary>
    /// 验证分区、读取 snapshot，并绑定生产或测试 writer。
    /// </summary>
    private static DurableDisplayAckOutbox OpenCore(
        string absolutePath,
        string saveId,
        string playerId,
        Action<DisplayAckOutboxSnapshot>? snapshotWriterOverride)
    {
        ValidateStableString(saveId, nameof(saveId));
        ValidateStableString(playerId, nameof(playerId));

        AtomicJsonFile<DisplayAckOutboxSnapshot> snapshotFile = new(
            absolutePath,
            DisplayAckOutboxSnapshotJsonConverter.CreateSerializerOptions());
        DisplayAckOutboxState recoveredState = snapshotFile.TryRead(
            out DisplayAckOutboxSnapshot? snapshot)
                ? RestoreAndValidateSnapshot(snapshot!, saveId, playerId)
                : DisplayAckOutboxState.Empty;

        return new DurableDisplayAckOutbox(
            saveId,
            playerId,
            snapshotWriterOverride ?? snapshotFile.Write,
            recoveredState);
    }

    /// <summary>
    /// 只有完整 snapshot 成功落盘后才替换内存引用。
    /// </summary>
    private void PersistThenReplace(DisplayAckOutboxState nextState)
    {
        DisplayAckOutboxSnapshot snapshot = CreateSnapshot(nextState);
        snapshotWriter(snapshot);
        state = nextState;
    }

    /// <summary>
    /// 将私有 immutable state 投影为一次性序列化 snapshot。
    /// </summary>
    private DisplayAckOutboxSnapshot CreateSnapshot(DisplayAckOutboxState sourceState)
    {
        DisplayAckOutboxSnapshotEntry[] pending = sourceState.Pending
            .Select(
                item => new DisplayAckOutboxSnapshotEntry(
                    item.GenerationId,
                    item.Sequence,
                    item.AttemptCount,
                    item.CanonicalRequestJson))
            .ToArray();
        DisplayAckOutboxDeadLetterEntry[] deadLetters = sourceState.DeadLetters
            .Select(
                item => new DisplayAckOutboxDeadLetterEntry(
                    item.GenerationId,
                    item.Sequence,
                    item.AttemptCount,
                    item.ReasonCode,
                    item.CanonicalRequestJson))
            .ToArray();

        return new DisplayAckOutboxSnapshot(
            SnapshotFormatVersion,
            saveId,
            playerId,
            sourceState.NextSequence,
            Array.AsReadOnly(pending),
            Array.AsReadOnly(deadLetters));
    }

    /// <summary>
    /// 恢复严格 snapshot，并验证 request 合同、分区、sequence 与 receipt 全局唯一性。
    /// </summary>
    private static DisplayAckOutboxState RestoreAndValidateSnapshot(
        DisplayAckOutboxSnapshot snapshot,
        string expectedSaveId,
        string expectedPlayerId)
    {
        try
        {
            if (snapshot.FormatVersion != SnapshotFormatVersion
                || !string.Equals(snapshot.SaveId, expectedSaveId, StringComparison.Ordinal)
                || !string.Equals(snapshot.PlayerId, expectedPlayerId, StringComparison.Ordinal)
                || snapshot.NextSequence < 1)
            {
                throw new SnapshotInvariantException();
            }

            List<StoredDisplayAck> pending = new(snapshot.Pending.Count);
            List<StoredDisplayAckDeadLetter> deadLetters = new(snapshot.DeadLetters.Count);
            HashSet<string> receiptIds = new(StringComparer.Ordinal);
            HashSet<long> sequences = new();
            long maximumSequence = 0;
            long previousPendingSequence = 0;
            foreach (DisplayAckOutboxSnapshotEntry entry in snapshot.Pending)
            {
                if (entry is null
                    || !IsStableString(entry.GenerationId)
                    || entry.Sequence < 1
                    || entry.Sequence <= previousPendingSequence
                    || entry.AttemptCount < 0)
                {
                    throw new SnapshotInvariantException();
                }

                CanonicalAckRequest canonicalRequest = CreateCanonicalRequestFromSnapshot(
                    entry.CanonicalRequestJson,
                    expectedSaveId,
                    expectedPlayerId);
                if (!receiptIds.Add(canonicalRequest.DisplayReceiptId)
                    || !sequences.Add(entry.Sequence))
                {
                    throw new SnapshotInvariantException();
                }

                pending.Add(
                    new StoredDisplayAck(
                        entry.GenerationId,
                        canonicalRequest.DisplayReceiptId,
                        entry.Sequence,
                        entry.AttemptCount,
                        canonicalRequest.CanonicalJson));
                previousPendingSequence = entry.Sequence;
                maximumSequence = Math.Max(maximumSequence, entry.Sequence);
            }

            long previousDeadLetterSequence = 0;
            foreach (DisplayAckOutboxDeadLetterEntry entry in snapshot.DeadLetters)
            {
                if (entry is null
                    || !IsStableString(entry.GenerationId)
                    || !IsStableString(entry.ReasonCode)
                    || entry.Sequence < 1
                    || entry.Sequence <= previousDeadLetterSequence
                    || entry.AttemptCount < 0)
                {
                    throw new SnapshotInvariantException();
                }

                CanonicalAckRequest canonicalRequest = CreateCanonicalRequestFromSnapshot(
                    entry.CanonicalRequestJson,
                    expectedSaveId,
                    expectedPlayerId);
                if (!receiptIds.Add(canonicalRequest.DisplayReceiptId)
                    || !sequences.Add(entry.Sequence))
                {
                    throw new SnapshotInvariantException();
                }

                deadLetters.Add(
                    new StoredDisplayAckDeadLetter(
                        entry.GenerationId,
                        canonicalRequest.DisplayReceiptId,
                        entry.Sequence,
                        entry.AttemptCount,
                        entry.ReasonCode,
                        canonicalRequest.CanonicalJson));
                previousDeadLetterSequence = entry.Sequence;
                maximumSequence = Math.Max(maximumSequence, entry.Sequence);
            }

            if (snapshot.NextSequence <= maximumSequence)
            {
                throw new SnapshotInvariantException();
            }

            return new DisplayAckOutboxState(
                snapshot.NextSequence,
                Array.AsReadOnly(pending.ToArray()),
                Array.AsReadOnly(deadLetters.ToArray()));
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
    /// 验证调用方持有的 attempt 自身完整且 canonical request 未被修改。
    /// </summary>
    private ValidatedDisplayAckAttempt ValidateAttemptSnapshot(PendingDisplayAck attempt)
    {
        if (!IsStableString(attempt.GenerationId)
            || attempt.Request is null
            || attempt.Sequence < 1
            || attempt.AttemptCount < 0
            || !IsStableString(attempt.CanonicalRequestJson))
        {
            throw new ArgumentException("Pending display ACK attempt 结构非法。", nameof(attempt));
        }

        CanonicalAckRequest canonicalRequest;
        try
        {
            canonicalRequest = CreateCanonicalRequestFromCaller(attempt.Request);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException("Pending display ACK attempt 包含非法 request。", nameof(attempt), exception);
        }

        if (!string.Equals(
            attempt.CanonicalRequestJson,
            canonicalRequest.CanonicalJson,
            StringComparison.Ordinal))
        {
            throw new ArgumentException("Pending display ACK canonical request 已被修改。", nameof(attempt));
        }

        return new ValidatedDisplayAckAttempt(
            attempt.GenerationId,
            canonicalRequest.DisplayReceiptId,
            attempt.Sequence,
            attempt.AttemptCount,
            attempt.CanonicalRequestJson,
            attempt.Request.RequestId);
    }

    /// <summary>
    /// 验证成功响应合同以及与 attempt 的 request/receipt 映射。
    /// </summary>
    private static void ValidateResponseSnapshot(
        ValidatedDisplayAckAttempt attempt,
        DisplayAckResponse response)
    {
        ContractValidationResult validation = ContractValidator.Validate(response);
        bool declaredStatus = response.Status is DisplayAckStatus.Accepted
            or DisplayAckStatus.Duplicate;
        if (!validation.IsValid
            || !declaredStatus
            || !string.Equals(response.RequestId, attempt.RequestId, StringComparison.Ordinal)
            || !string.Equals(
                response.DisplayReceiptId,
                attempt.DisplayReceiptId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException("Display ACK response 不符合合同或 attempt identity。", nameof(response));
        }
    }

    /// <summary>
    /// 若 attempt 对应当前 FIFO 首项则返回它；已处理的旧 attempt 返回 null；冲突/越过 FIFO 则拒绝。
    /// </summary>
    private StoredDisplayAck? ResolveCurrentFirstItem(ValidatedDisplayAckAttempt attempt)
    {
        if (state.Pending.Count > 0)
        {
            StoredDisplayAck first = state.Pending[0];
            if (string.Equals(
                first.DisplayReceiptId,
                attempt.DisplayReceiptId,
                StringComparison.Ordinal))
            {
                EnsureStoredItemMatchesAttempt(first, attempt);
                return first;
            }

            StoredDisplayAck? later = state.Pending.Skip(1).FirstOrDefault(
                item => string.Equals(
                    item.DisplayReceiptId,
                    attempt.DisplayReceiptId,
                    StringComparison.Ordinal));
            if (later is not null)
            {
                // 调用方不能用第二项 attempt 越过仍未处理的 FIFO 首项。
                throw new ArgumentException("Pending display ACK attempt 不是当前 FIFO 首项。", nameof(attempt));
            }
        }

        StoredDisplayAckDeadLetter? deadLetter = state.DeadLetters.FirstOrDefault(
            item => string.Equals(
                item.DisplayReceiptId,
                attempt.DisplayReceiptId,
                StringComparison.Ordinal));
        if (deadLetter is not null)
        {
            EnsureStoredDeadLetterMatchesAttempt(deadLetter, attempt);
        }

        // receipt 已被成功删除或已经以同一事实进入 dead letter；晚到结果不得碰后来首项。
        return null;
    }

    /// <summary>
    /// 精确核对仍 pending 的首项；任一 scalar/canonical 漂移都属于调用快照冲突。
    /// </summary>
    private static void EnsureStoredItemMatchesAttempt(
        StoredDisplayAck stored,
        ValidatedDisplayAckAttempt attempt)
    {
        if (!string.Equals(stored.GenerationId, attempt.GenerationId, StringComparison.Ordinal)
            || stored.Sequence != attempt.Sequence
            || stored.AttemptCount != attempt.AttemptCount
            || !string.Equals(
                stored.CanonicalRequestJson,
                attempt.CanonicalRequestJson,
                StringComparison.Ordinal))
        {
            throw new ArgumentException("Pending display ACK attempt 不再匹配当前状态。", nameof(attempt));
        }
    }

    /// <summary>
    /// 对 dead-letter replay 也核对原事实，防止同 receipt 的伪造 attempt 被误当成正常 no-op。
    /// </summary>
    private static void EnsureStoredDeadLetterMatchesAttempt(
        StoredDisplayAckDeadLetter stored,
        ValidatedDisplayAckAttempt attempt)
    {
        if (!string.Equals(stored.GenerationId, attempt.GenerationId, StringComparison.Ordinal)
            || stored.Sequence != attempt.Sequence
            || stored.AttemptCount != attempt.AttemptCount
            || !string.Equals(
                stored.CanonicalRequestJson,
                attempt.CanonicalRequestJson,
                StringComparison.Ordinal))
        {
            throw new ArgumentException("Pending display ACK attempt 与现有 dead letter 冲突。", nameof(attempt));
        }
    }

    /// <summary>
    /// 验证调用方 request、分区并生成不持有 DTO 引用的 canonical 值。
    /// </summary>
    private CanonicalAckRequest CreateCanonicalRequestFromCaller(DisplayAckRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ContractValidationResult validation = ContractValidator.Validate(request);
        if (!validation.IsValid
            || !string.Equals(request.SaveId, saveId, StringComparison.Ordinal)
            || !string.Equals(request.PlayerId, playerId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "DisplayAckRequest 不符合共享合同或当前 outbox 分区。",
                nameof(request));
        }

        try
        {
            string canonicalJson = CanonicalizeJsonObject(ContractJson.Serialize(request));
            DisplayAckRequest immutableCopy = ContractJson.Deserialize<DisplayAckRequest>(canonicalJson);
            return new CanonicalAckRequest(immutableCopy.DisplayReceiptId, canonicalJson);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException(
                "DisplayAckRequest 无法序列化为严格合同 JSON。",
                nameof(request),
                exception);
        }
    }

    /// <summary>
    /// 加载 snapshot request 时强制经过正式 ContractJson、ContractValidator 与分区核对。
    /// </summary>
    private static CanonicalAckRequest CreateCanonicalRequestFromSnapshot(
        string requestJson,
        string expectedSaveId,
        string expectedPlayerId)
    {
        DisplayAckRequest request = ContractJson.Deserialize<DisplayAckRequest>(requestJson);
        ContractValidationResult validation = ContractValidator.Validate(request);
        if (!validation.IsValid
            || !string.Equals(request.SaveId, expectedSaveId, StringComparison.Ordinal)
            || !string.Equals(request.PlayerId, expectedPlayerId, StringComparison.Ordinal))
        {
            throw new SnapshotInvariantException();
        }

        string canonicalJson = CanonicalizeJsonObject(ContractJson.Serialize(request));
        return new CanonicalAckRequest(request.DisplayReceiptId, canonicalJson);
    }

    /// <summary>
    /// 从内部 canonical JSON 创建公开 request 深拷贝。
    /// </summary>
    private static DisplayAckRequest DeserializeCanonicalRequest(string canonicalRequestJson)
    {
        return ContractJson.Deserialize<DisplayAckRequest>(canonicalRequestJson);
    }

    /// <summary>
    /// 递归排序 object key；ACK 当前只有标量，但统一 canonical 规则便于格式长期稳定。
    /// </summary>
    private static string CanonicalizeJsonObject(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Canonical DisplayAckRequest 根值必须是 JSON object。");
        }

        EnsureNoDuplicateObjectKeys(document.RootElement, "$request");
        using MemoryStream output = new();
        using (Utf8JsonWriter writer = new(output, new JsonWriterOptions { Indented = false }))
        {
            WriteCanonicalElement(writer, document.RootElement);
        }

        return Encoding.UTF8.GetString(output.ToArray());
    }

    /// <summary>
    /// canonical writer 递归核心；只排序 object，不改变 scalar 或数组顺序。
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
                throw new JsonException("Canonical DisplayAckRequest 包含未定义 JSON 值。");
        }
    }

    /// <summary>
    /// 递归拒绝同一 object 内重复 key，避免读取器 first/last-wins 差异。
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
    /// 校验公开 string 参数非空且没有首尾空白；从不静默 Trim。
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
    /// 判断持久化身份或 reason code 是否为稳定字符串。
    /// </summary>
    private static bool IsStableString(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && string.Equals(value, value.Trim(), StringComparison.Ordinal);
    }

    /// <summary>
    /// 私有 immutable 状态；集合只在构造新 state 时创建，不原地修改。
    /// </summary>
    private sealed record DisplayAckOutboxState(
        long NextSequence,
        IReadOnlyList<StoredDisplayAck> Pending,
        IReadOnlyList<StoredDisplayAckDeadLetter> DeadLetters)
    {
        public static DisplayAckOutboxState Empty { get; } = new(
            NextSequence: 1,
            Array.Empty<StoredDisplayAck>(),
            Array.Empty<StoredDisplayAckDeadLetter>());
    }

    /// <summary>
    /// 私有 pending ACK 值对象。
    /// </summary>
    private sealed record StoredDisplayAck(
        string GenerationId,
        string DisplayReceiptId,
        long Sequence,
        int AttemptCount,
        string CanonicalRequestJson);

    /// <summary>
    /// 私有 dead-letter ACK 值对象。
    /// </summary>
    private sealed record StoredDisplayAckDeadLetter(
        string GenerationId,
        string DisplayReceiptId,
        long Sequence,
        int AttemptCount,
        string ReasonCode,
        string CanonicalRequestJson);

    /// <summary>
    /// 完成验证后的 immutable request identity。
    /// </summary>
    private sealed record CanonicalAckRequest(
        string DisplayReceiptId,
        string CanonicalJson);

    /// <summary>
    /// 完成自校验、与调用方 record 隔离的 attempt identity。
    /// </summary>
    private sealed record ValidatedDisplayAckAttempt(
        string GenerationId,
        string DisplayReceiptId,
        long Sequence,
        int AttemptCount,
        string CanonicalRequestJson,
        string RequestId);

    /// <summary>
    /// 仅用于把恢复期领域不变量统一映射为 OutboxCorruptedException。
    /// </summary>
    private sealed class SnapshotInvariantException : Exception
    {
    }
}

/// <summary>
/// Display ACK outbox v1 snapshot 的严格 JSON converter。
/// </summary>
/// <remarks>
/// 默认 System.Text.Json 会忽略未知字段并让重复 key 后值覆盖前值；崩溃恢复文件不能接受
/// 这种歧义。converter 因此逐层冻结 required/unknown/duplicate key、integer token，并
/// 保证 request 始终以 JSON object 落盘和读取。
/// </remarks>
internal sealed class DisplayAckOutboxSnapshotJsonConverter : JsonConverter<DisplayAckOutboxSnapshot>
{
    private static readonly string[] SnapshotProperties =
    {
        "format_version",
        "save_id",
        "player_id",
        "next_sequence",
        "pending",
        "dead_letters",
    };

    private static readonly string[] PendingProperties =
    {
        "generation_id",
        "sequence",
        "attempt_count",
        "request",
    };

    private static readonly string[] DeadLetterProperties =
    {
        "generation_id",
        "sequence",
        "attempt_count",
        "reason_code",
        "request",
    };

    /// <summary>
    /// 创建 AtomicJsonFile 使用的冻结 serializer 配置。
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
        options.Converters.Add(new DisplayAckOutboxSnapshotJsonConverter());
        return options;
    }

    /// <summary>
    /// 严格读取完整 snapshot。
    /// </summary>
    public override DisplayAckOutboxSnapshot Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        IReadOnlyDictionary<string, JsonElement> properties = ReadExactObject(
            document.RootElement,
            "$",
            SnapshotProperties);
        return new DisplayAckOutboxSnapshot(
            ReadInt32(properties["format_version"], "$.format_version"),
            ReadString(properties["save_id"], "$.save_id"),
            ReadString(properties["player_id"], "$.player_id"),
            ReadInt64(properties["next_sequence"], "$.next_sequence"),
            ReadPending(properties["pending"], "$.pending"),
            ReadDeadLetters(properties["dead_letters"], "$.dead_letters"));
    }

    /// <summary>
    /// 以冻结字段顺序写 snapshot，request 直接写 object token。
    /// </summary>
    public override void Write(
        Utf8JsonWriter writer,
        DisplayAckOutboxSnapshot value,
        JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Pending is null || value.DeadLetters is null)
        {
            throw new JsonException("Display ACK outbox snapshot 集合不能为 null。");
        }

        writer.WriteStartObject();
        writer.WriteNumber("format_version", value.FormatVersion);
        writer.WriteString("save_id", value.SaveId);
        writer.WriteString("player_id", value.PlayerId);
        writer.WriteNumber("next_sequence", value.NextSequence);
        writer.WritePropertyName("pending");
        writer.WriteStartArray();
        foreach (DisplayAckOutboxSnapshotEntry entry in value.Pending)
        {
            if (entry is null)
            {
                throw new JsonException("Display ACK pending entry 不能为 null。");
            }

            writer.WriteStartObject();
            writer.WriteString("generation_id", entry.GenerationId);
            writer.WriteNumber("sequence", entry.Sequence);
            writer.WriteNumber("attempt_count", entry.AttemptCount);
            writer.WritePropertyName("request");
            WriteRequestObject(writer, entry.CanonicalRequestJson);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WritePropertyName("dead_letters");
        writer.WriteStartArray();
        foreach (DisplayAckOutboxDeadLetterEntry entry in value.DeadLetters)
        {
            if (entry is null)
            {
                throw new JsonException("Display ACK dead-letter entry 不能为 null。");
            }

            writer.WriteStartObject();
            writer.WriteString("generation_id", entry.GenerationId);
            writer.WriteNumber("sequence", entry.Sequence);
            writer.WriteNumber("attempt_count", entry.AttemptCount);
            writer.WriteString("reason_code", entry.ReasonCode);
            writer.WritePropertyName("request");
            WriteRequestObject(writer, entry.CanonicalRequestJson);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <summary>
    /// 读取 pending 数组及其 exact entry shape。
    /// </summary>
    private static IReadOnlyList<DisplayAckOutboxSnapshotEntry> ReadPending(
        JsonElement element,
        string path)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException($"{path} 必须是 JSON array。");
        }

        List<DisplayAckOutboxSnapshotEntry> entries = new();
        int index = 0;
        foreach (JsonElement item in element.EnumerateArray())
        {
            string itemPath = $"{path}[{index}]";
            IReadOnlyDictionary<string, JsonElement> properties = ReadExactObject(
                item,
                itemPath,
                PendingProperties);
            entries.Add(
                new DisplayAckOutboxSnapshotEntry(
                    ReadString(properties["generation_id"], $"{itemPath}.generation_id"),
                    ReadInt64(properties["sequence"], $"{itemPath}.sequence"),
                    ReadInt32(properties["attempt_count"], $"{itemPath}.attempt_count"),
                    ReadRequestObjectJson(properties["request"], $"{itemPath}.request")));
            index++;
        }

        return Array.AsReadOnly(entries.ToArray());
    }

    /// <summary>
    /// 读取 dead_letters 数组及稳定 reason 字符串。
    /// </summary>
    private static IReadOnlyList<DisplayAckOutboxDeadLetterEntry> ReadDeadLetters(
        JsonElement element,
        string path)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException($"{path} 必须是 JSON array。");
        }

        List<DisplayAckOutboxDeadLetterEntry> entries = new();
        int index = 0;
        foreach (JsonElement item in element.EnumerateArray())
        {
            string itemPath = $"{path}[{index}]";
            IReadOnlyDictionary<string, JsonElement> properties = ReadExactObject(
                item,
                itemPath,
                DeadLetterProperties);
            entries.Add(
                new DisplayAckOutboxDeadLetterEntry(
                    ReadString(properties["generation_id"], $"{itemPath}.generation_id"),
                    ReadInt64(properties["sequence"], $"{itemPath}.sequence"),
                    ReadInt32(properties["attempt_count"], $"{itemPath}.attempt_count"),
                    ReadString(properties["reason_code"], $"{itemPath}.reason_code"),
                    ReadRequestObjectJson(properties["request"], $"{itemPath}.request")));
            index++;
        }

        return Array.AsReadOnly(entries.ToArray());
    }

    /// <summary>
    /// 读取 required-only object，同时拒绝 unknown、missing 与重复字段。
    /// </summary>
    private static IReadOnlyDictionary<string, JsonElement> ReadExactObject(
        JsonElement element,
        string path,
        IReadOnlyList<string> requiredProperties)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"{path} 必须是 JSON object。");
        }

        HashSet<string> allowed = requiredProperties.ToHashSet(StringComparer.Ordinal);
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

        foreach (string requiredProperty in requiredProperties)
        {
            if (!values.ContainsKey(requiredProperty))
            {
                throw new JsonException($"{path} 缺少字段 {requiredProperty}。");
            }
        }

        return values;
    }

    /// <summary>
    /// 读取严格 JSON string，显式 null 不会被折叠成默认值。
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
    /// 读取不含 decimal/exponent 的 Int32 token。
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
    /// 读取不含 decimal/exponent 的 Int64 token。
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
    /// snapshot 本地计数只接受 JSON integer lexical token。
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
    /// 读取 request object 原文，并递归拒绝内部 duplicate key。
    /// </summary>
    private static string ReadRequestObjectJson(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"{path} 必须是 JSON object，不能是转义字符串。");
        }

        EnsureNoDuplicateObjectKeys(element, path);
        return element.GetRawText();
    }

    /// <summary>
    /// request 合同无动态字段；递归检查仍可明确关闭未来嵌套对象的 duplicate key 歧义。
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
    /// 将 canonical request JSON 直接写为 object token。
    /// </summary>
    private static void WriteRequestObject(Utf8JsonWriter writer, string canonicalRequestJson)
    {
        using JsonDocument document = JsonDocument.Parse(canonicalRequestJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Display ACK request 必须是 JSON object。");
        }

        document.RootElement.WriteTo(writer);
    }
}
