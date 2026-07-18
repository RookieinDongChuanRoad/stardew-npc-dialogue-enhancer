using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StardewNpcAgent.Contracts;
using StardewNpcAgent.Infrastructure.Storage;

namespace StardewNpcAgent.Tests.Infrastructure.Storage;

/// <summary>
/// 以真实临时文件验证事件 outbox 的完整持久化状态机。
/// </summary>
/// <remarks>
/// 测试只模拟网络调用前后的领域输入，不启动 HTTP client、SMAPI、游戏或后台线程。
/// 每个 mutation 都通过文件字节、重启恢复和公开快照共同断言，避免只验证内存 happy path。
/// </remarks>
public sealed class DurableEventOutboxTests : IDisposable
{
    private const string SaveId = "save-a";
    private const string PlayerId = "player-a";
    private const string MissingRejectedReasonCode = "EVENT_REJECTED_WITHOUT_REASON";
    private readonly string testDirectory;

    /// <summary>
    /// 为当前测试实例创建唯一临时目录；所有文件都与真实游戏存档和 Mods 目录隔离。
    /// </summary>
    public DurableEventOutboxTests()
    {
        testDirectory = Path.Combine(
            Path.GetTempPath(),
            "StardewNpcAgent.Tests",
            $"DurableEventOutbox.{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
    }

    /// <summary>
    /// 缺失文件是正常空状态，Open 不得提前创建路径；第一次 Enqueue 才应原子落盘并可重启恢复。
    /// </summary>
    [Fact]
    public void Open_WhenSnapshotIsMissing_IsEmptyWithoutWritingAndFirstEnqueueSurvivesRestart()
    {
        string missingParent = Path.Combine(testDirectory, "missing-parent");
        string filePath = Path.Combine(missingParent, "events.json");

        DurableEventOutbox outbox = DurableEventOutbox.Open(filePath, SaveId, PlayerId);

        Assert.Equal(0, outbox.PendingCount);
        Assert.Equal(0, outbox.DeadLetterCount);
        Assert.Equal(0, outbox.MemoryRevision);
        Assert.Equal(-1, outbox.CommittedThroughDayIndex);
        Assert.False(Directory.Exists(missingParent));
        Assert.Throws<InvalidOperationException>(
            () => outbox.CreatePendingBatch("request-empty"));

        outbox.Enqueue(CreateEvent("event-001"));

        Assert.True(File.Exists(filePath));
        DurableEventOutbox reopened = DurableEventOutbox.Open(filePath, SaveId, PlayerId);
        PendingEventBatch batch = reopened.CreatePendingBatch("request-restarted");
        Assert.Equal(new[] { "event-001" }, batch.Request.Events.Select(item => item.EventId));
        Assert.Equal(new[] { 1L }, batch.Identities.Select(item => item.Sequence));
        Assert.Equal(0, reopened.MemoryRevision);
        Assert.Equal(-1, reopened.CommittedThroughDayIndex);
    }

    /// <summary>
    /// canonical JSON 必须递归忽略 object key 顺序，同时复制调用方 DTO，后续外部修改不能污染事实。
    /// </summary>
    [Fact]
    public void Enqueue_EquivalentRecursiveObjectOrderIsNoOpAndCallerMutationCannotChangeStoredEvent()
    {
        string filePath = CreateFilePath("canonical-order.json");
        GameEvent original = CreateEvent(
            "event-canonical",
            payloadJson: "{\"weather\":\"sun\",\"nested\":{\"b\":2,\"a\":1},\"array\":[{\"y\":2,\"x\":1},1]}");
        DurableEventOutbox outbox = DurableEventOutbox.Open(filePath, SaveId, PlayerId);
        outbox.Enqueue(original);
        byte[] bytesAfterFirstEnqueue = File.ReadAllBytes(filePath);

        // 调用方仍持有原 DTO；这里主动破坏它，证明 outbox 只保存 canonical immutable value。
        original.Source = "mutated-after-enqueue";
        original.Payload = ParseObject("{\"mutated\":true}");
        GameEvent equivalentWithReorderedObjects = CreateEvent(
            "event-canonical",
            payloadJson: "{\"array\":[{\"x\":1,\"y\":2},1],\"nested\":{\"a\":1,\"b\":2},\"weather\":\"sun\"}");

        outbox.Enqueue(equivalentWithReorderedObjects);

        Assert.Equal(1, outbox.PendingCount);
        Assert.Equal(bytesAfterFirstEnqueue, File.ReadAllBytes(filePath));
        PendingEventBatch batch = outbox.CreatePendingBatch("request-canonical");
        Assert.Equal("smapi", batch.Request.Events[0].Source);
        Assert.Equal("sun", batch.Request.Events[0].Payload.GetProperty("weather").GetString());

        using JsonDocument snapshot = JsonDocument.Parse(File.ReadAllText(filePath));
        JsonElement persistedEvent = snapshot.RootElement
            .GetProperty("pending")[0]
            .GetProperty("event");
        Assert.Equal(JsonValueKind.Object, persistedEvent.ValueKind);
    }

    /// <summary>
    /// payload 允许动态字段，但同一 object 的重复 key 会产生不唯一语义；必须在首次写盘前拒绝。
    /// </summary>
    [Fact]
    public void Enqueue_WhenPayloadContainsDuplicateObjectKeyRejectsWithoutCreatingSnapshot()
    {
        string filePath = CreateFilePath("duplicate-payload-key.json");
        DurableEventOutbox outbox = DurableEventOutbox.Open(filePath, SaveId, PlayerId);
        GameEvent ambiguousEvent = CreateEvent(
            "event-duplicate-payload-key",
            payloadJson: "{\"same\":1,\"same\":2}");

        Assert.Throws<ArgumentException>(() => outbox.Enqueue(ambiguousEvent));

        Assert.False(File.Exists(filePath));
        Assert.Equal(0, outbox.PendingCount);
    }

    /// <summary>
    /// event_id 是不可变事实身份；payload、source 或发生日任一变化都必须冲突且不改旧文件。
    /// </summary>
    [Fact]
    public void Enqueue_SameIdentityWithDifferentFactThrowsConflictAndPreservesSnapshotHash()
    {
        string filePath = CreateFilePath("identity-conflict.json");
        DurableEventOutbox outbox = DurableEventOutbox.Open(filePath, SaveId, PlayerId);
        GameEvent baseline = CreateEvent("event-conflict");
        outbox.Enqueue(baseline);
        string originalHash = ComputeSha256(filePath);

        GameEvent changedPayload = CloneEvent(baseline);
        changedPayload.Payload = ParseObject("{\"value\":999}");
        GameEvent changedSource = CloneEvent(baseline);
        changedSource.Source = "another-source";
        GameEvent changedDay = CloneEvent(baseline);
        changedDay.OccurredDayIndex++;

        foreach (GameEvent conflictingEvent in new[] { changedPayload, changedSource, changedDay })
        {
            Assert.Throws<OutboxIdentityConflictException>(() => outbox.Enqueue(conflictingEvent));
            Assert.Equal(originalHash, ComputeSha256(filePath));
            Assert.Equal(1, outbox.PendingCount);
        }
    }

    /// <summary>
    /// 共享合同上限是 64；第 65 条必须留到下一批，并保持单调 sequence 对应的 FIFO。
    /// </summary>
    [Fact]
    public void CreatePendingBatch_WithSixtyFiveEventsReturnsStableSixtyFourPlusOneFifo()
    {
        string filePath = CreateFilePath("batch-limit.json");
        DurableEventOutbox outbox = DurableEventOutbox.Open(filePath, SaveId, PlayerId);
        for (int index = 1; index <= 65; index++)
        {
            outbox.Enqueue(CreateEvent($"event-{index:D3}"));
        }

        Assert.Throws<ArgumentOutOfRangeException>(
            () => outbox.CreatePendingBatch("request-zero", maxItems: 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => outbox.CreatePendingBatch(
                "request-too-large",
                maxItems: ContractLimits.MaximumEventsPerBatch + 1));

        PendingEventBatch firstBatch = outbox.CreatePendingBatch("request-first");

        Assert.Equal(ContractLimits.MaximumEventsPerBatch, firstBatch.Request.Events.Count);
        Assert.Equal(
            Enumerable.Range(1, 64).Select(index => $"event-{index:D3}"),
            firstBatch.Request.Events.Select(item => item.EventId));
        Assert.Equal(
            Enumerable.Range(1, 64).Select(index => (long)index),
            firstBatch.Identities.Select(item => item.Sequence));

        outbox.ApplyResponse(firstBatch, CreateResponse(firstBatch, EventIngestionStatus.Accepted));
        PendingEventBatch secondBatch = outbox.CreatePendingBatch("request-second");

        Assert.Single(secondBatch.Request.Events);
        Assert.Equal("event-065", secondBatch.Request.Events[0].EventId);
        Assert.Equal(65L, secondBatch.Identities[0].Sequence);
    }

    /// <summary>
    /// accepted/duplicate 都完成 pending；rejected 进入 dead letter，缺失原因使用稳定内部代码。
    /// </summary>
    [Fact]
    public void ApplyResponse_MapsTerminalStatusesAndUsesStableFallbackReasonCode()
    {
        string filePath = CreateFilePath("terminal-statuses.json");
        DurableEventOutbox outbox = DurableEventOutbox.Open(filePath, SaveId, PlayerId);
        foreach (string eventId in new[] { "accepted", "duplicate", "rejected-explicit", "rejected-missing" })
        {
            outbox.Enqueue(CreateEvent(eventId));
        }

        PendingEventBatch batch = outbox.CreatePendingBatch("request-terminal");
        GameEventBatchResponse response = CreateResponse(
            batch,
            EventIngestionStatus.Accepted,
            EventIngestionStatus.Duplicate,
            EventIngestionStatus.Rejected,
            EventIngestionStatus.Rejected);
        response.Items[2].ReasonCode = "PAYLOAD_INVALID";
        response.Items[3].ReasonCode = null;

        outbox.ApplyResponse(batch, response);

        Assert.Equal(0, outbox.PendingCount);
        Assert.Equal(2, outbox.DeadLetterCount);
        IReadOnlyList<EventDeadLetter> deadLetters = outbox.SnapshotDeadLetters();
        Assert.Equal(new[] { "rejected-explicit", "rejected-missing" }, deadLetters.Select(item => item.Event.EventId));
        Assert.Equal(new[] { "PAYLOAD_INVALID", MissingRejectedReasonCode }, deadLetters.Select(item => item.ReasonCode));
    }

    /// <summary>
    /// 瞬态失败不删除事实；一次合法调用只增加一次 attempt，并且新值必须经重启恢复。
    /// </summary>
    [Fact]
    public void RecordTransientFailure_IncrementsEachCurrentItemOnceAndPersistsAcrossRestart()
    {
        string filePath = CreateFilePath("transient.json");
        DurableEventOutbox outbox = DurableEventOutbox.Open(filePath, SaveId, PlayerId);
        outbox.Enqueue(CreateEvent("event-a"));
        outbox.Enqueue(CreateEvent("event-b"));
        PendingEventBatch batch = outbox.CreatePendingBatch("request-transient");

        outbox.RecordTransientFailure(batch, "NETWORK_TIMEOUT");

        Assert.Equal(2, outbox.PendingCount);
        Assert.Equal(new[] { 1, 1 }, ReadPendingAttemptCounts(filePath));
        DurableEventOutbox reopened = DurableEventOutbox.Open(filePath, SaveId, PlayerId);
        PendingEventBatch reopenedBatch = reopened.CreatePendingBatch("request-after-restart");
        reopened.RecordTransientFailure(reopenedBatch, "SERVICE_UNAVAILABLE");
        Assert.Equal(new[] { 2, 2 }, ReadPendingAttemptCounts(filePath));
    }

    /// <summary>
    /// response 必须与批次 request_id、数量、顺序、event_id 和已声明 status 精确对应；否则整批零变化。
    /// </summary>
    [Fact]
    public void ApplyResponse_WhenEnvelopeOrItemMappingIsInvalidRejectsWholeBatchWithoutMutation()
    {
        string filePath = CreateFilePath("invalid-response.json");
        DurableEventOutbox outbox = DurableEventOutbox.Open(filePath, SaveId, PlayerId);
        outbox.Enqueue(CreateEvent("event-a"));
        outbox.Enqueue(CreateEvent("event-b"));
        PendingEventBatch batch = outbox.CreatePendingBatch("request-response-validation");
        string originalHash = ComputeSha256(filePath);
        List<Action<GameEventBatchResponse>> invalidMutations = new()
        {
            response => response.RequestId = "wrong-request",
            response => response.Items.RemoveAt(1),
            response => response.Items.Reverse(),
            response => response.Items[0].EventId = "wrong-event",
            response => response.Items[0].Status = (EventIngestionStatus)999,
        };

        foreach (Action<GameEventBatchResponse> mutate in invalidMutations)
        {
            GameEventBatchResponse invalidResponse = CreateResponse(
                batch,
                EventIngestionStatus.Accepted);
            mutate(invalidResponse);

            Assert.Throws<ArgumentException>(() => outbox.ApplyResponse(batch, invalidResponse));
            Assert.Equal(originalHash, ComputeSha256(filePath));
            Assert.Equal(2, outbox.PendingCount);
            Assert.Equal(0, outbox.DeadLetterCount);
        }
    }

    /// <summary>
    /// batch 是调用方可修改的传输快照；修改分区、顺序、identity 或 canonical 内容不得改内部状态。
    /// </summary>
    [Fact]
    public void MutatingReturnedBatchCannotChangeOrAuthorizeDifferentStoredFacts()
    {
        string filePath = CreateFilePath("mutated-batch.json");
        DurableEventOutbox outbox = DurableEventOutbox.Open(filePath, SaveId, PlayerId);
        outbox.Enqueue(CreateEvent("event-a"));
        outbox.Enqueue(CreateEvent("event-b"));
        string originalHash = ComputeSha256(filePath);

        PendingEventBatch wrongPartition = outbox.CreatePendingBatch("request-wrong-partition");
        wrongPartition.Request.SaveId = "forged-save";
        Assert.Throws<ArgumentException>(
            () => outbox.RecordTransientFailure(wrongPartition, "NETWORK_TIMEOUT"));

        PendingEventBatch wrongOrder = outbox.CreatePendingBatch("request-wrong-order");
        wrongOrder.Request.Events.Reverse();
        Assert.Throws<ArgumentException>(
            () => outbox.ApplyResponse(
                wrongOrder,
                CreateResponse(wrongOrder, EventIngestionStatus.Accepted)));

        PendingEventBatch wrongSequence = outbox.CreatePendingBatch("request-wrong-sequence");
        PendingEventIdentity[] forgedIdentities = wrongSequence.Identities.ToArray();
        forgedIdentities[0] = forgedIdentities[0] with { Sequence = forgedIdentities[0].Sequence + 100 };
        wrongSequence = wrongSequence with { Identities = forgedIdentities };
        Assert.Throws<ArgumentException>(
            () => outbox.RecordTransientFailure(wrongSequence, "NETWORK_TIMEOUT"));

        PendingEventBatch wrongCanonical = outbox.CreatePendingBatch("request-wrong-canonical");
        PendingEventIdentity[] forgedCanonical = wrongCanonical.Identities.ToArray();
        forgedCanonical[0] = forgedCanonical[0] with { CanonicalEventJson = "{}" };
        wrongCanonical = wrongCanonical with { Identities = forgedCanonical };
        Assert.Throws<ArgumentException>(
            () => outbox.RecordTransientFailure(wrongCanonical, "NETWORK_TIMEOUT"));

        Assert.Equal(originalHash, ComputeSha256(filePath));
        Assert.Equal(2, outbox.PendingCount);
        Assert.Equal(new[] { 0, 0 }, ReadPendingAttemptCounts(filePath));
        Assert.Equal(
            new[] { "event-a", "event-b" },
            outbox.CreatePendingBatch("request-fresh").Request.Events.Select(item => item.EventId));
    }

    /// <summary>
    /// 网络在途期间允许追加事件；旧响应只完成旧 identity，同一响应重放不得触碰后来追加项。
    /// </summary>
    [Fact]
    public void ApplyResponse_ReplayIsNoOpAndDoesNotAffectEventAppendedWhileBatchWasInFlight()
    {
        string filePath = CreateFilePath("response-replay.json");
        DurableEventOutbox outbox = DurableEventOutbox.Open(filePath, SaveId, PlayerId);
        outbox.Enqueue(CreateEvent("event-old-a"));
        outbox.Enqueue(CreateEvent("event-old-b"));
        PendingEventBatch inFlight = outbox.CreatePendingBatch("request-in-flight");
        outbox.Enqueue(CreateEvent("event-appended"));
        GameEventBatchResponse response = CreateResponse(
            inFlight,
            EventIngestionStatus.Accepted,
            EventIngestionStatus.Duplicate);

        outbox.ApplyResponse(inFlight, response);
        string hashAfterFirstApply = ComputeSha256(filePath);
        outbox.ApplyResponse(inFlight, response);

        Assert.Equal(hashAfterFirstApply, ComputeSha256(filePath));
        Assert.Equal(1, outbox.PendingCount);
        Assert.Equal(
            "event-appended",
            outbox.CreatePendingBatch("request-appended").Request.Events[0].EventId);
    }

    /// <summary>
    /// 后端事件 ACK 返回的 memory/day 水位是次日生成的必需输入，必须与事件终态原子落盘、
    /// 跨重启恢复；任何回退或非法组合都不能先删除 pending 或改写旧 snapshot。
    /// </summary>
    [Fact]
    public void ApplyResponse_PersistsMonotonicWatermarkAndRejectsRegressionWithoutMutation()
    {
        string filePath = CreateFilePath("watermark.json");
        DurableEventOutbox outbox = DurableEventOutbox.Open(filePath, SaveId, PlayerId);
        outbox.Enqueue(CreateEvent("event-watermark-a"));
        PendingEventBatch firstBatch = outbox.CreatePendingBatch("request-watermark-a");
        GameEventBatchResponse firstResponse = CreateResponse(
            firstBatch,
            EventIngestionStatus.Accepted);
        firstResponse.MemoryRevision = 7;
        firstResponse.CommittedThroughDayIndex = 13;

        outbox.ApplyResponse(firstBatch, firstResponse);

        Assert.Equal(7, outbox.MemoryRevision);
        Assert.Equal(13, outbox.CommittedThroughDayIndex);
        DurableEventOutbox reopened = DurableEventOutbox.Open(filePath, SaveId, PlayerId);
        Assert.Equal(7, reopened.MemoryRevision);
        Assert.Equal(13, reopened.CommittedThroughDayIndex);

        reopened.Enqueue(CreateEvent("event-watermark-b"));
        PendingEventBatch secondBatch = reopened.CreatePendingBatch("request-watermark-b");
        string originalHash = ComputeSha256(filePath);
        (int Revision, int CommittedDay)[] invalidWatermarks =
        {
            (6, 13),
            (7, 12),
            (0, -1),
            (8, -1),
        };

        foreach ((int revision, int committedDay) in invalidWatermarks)
        {
            GameEventBatchResponse invalidResponse = CreateResponse(
                secondBatch,
                EventIngestionStatus.Accepted);
            invalidResponse.MemoryRevision = revision;
            invalidResponse.CommittedThroughDayIndex = committedDay;

            Assert.Throws<ArgumentException>(
                () => reopened.ApplyResponse(secondBatch, invalidResponse));
            Assert.Equal(originalHash, ComputeSha256(filePath));
            Assert.Equal(1, reopened.PendingCount);
            Assert.Equal(7, reopened.MemoryRevision);
            Assert.Equal(13, reopened.CommittedThroughDayIndex);
        }

        reopened.RecordTransientFailure(secondBatch, "NETWORK_TIMEOUT");
        Assert.Equal(7, reopened.MemoryRevision);
        Assert.Equal(13, reopened.CommittedThroughDayIndex);
        DurableEventOutbox reopenedAfterTransient = DurableEventOutbox.Open(
            filePath,
            SaveId,
            PlayerId);
        Assert.Equal(7, reopenedAfterTransient.MemoryRevision);
        Assert.Equal(13, reopenedAfterTransient.CommittedThroughDayIndex);
    }

    /// <summary>
    /// 文件格式、分区、计数、唯一键和事件合同任一损坏都必须 fail closed，且不得重写原始字节。
    /// </summary>
    [Fact]
    public void Open_WhenSnapshotViolatesFrozenFormatRejectsEveryCaseWithoutChangingBytes()
    {
        string eventOne = ContractJson.Serialize(CreateEvent("event-1"));
        string eventTwo = ContractJson.Serialize(CreateEvent("event-2"));
        string pendingOne = PendingEntry(sequence: 1, attemptCount: 0, eventOne);
        string pendingTwo = PendingEntry(sequence: 2, attemptCount: 0, eventTwo);
        string duplicateEventId = PendingEntry(sequence: 2, attemptCount: 0, eventOne);
        string duplicateSequence = PendingEntry(sequence: 1, attemptCount: 0, eventTwo);
        string deadOne = DeadEntry(sequence: 1, attemptCount: 0, "PERMANENT", eventOne);
        string missingRequiredEventField = eventOne.Replace(
            "\"source\":\"smapi\",",
            string.Empty,
            StringComparison.Ordinal);
        string eventWithUnknownField = eventOne[..^1] + ",\"unexpected\":true}";
        List<string> corruptedSnapshots = new()
        {
            "{\"format_version\":1",
            BuildSnapshotJson(formatVersion: 3, nextSequence: 2, pendingJson: pendingOne),
            BuildSnapshotJson(saveId: "wrong-save", nextSequence: 2, pendingJson: pendingOne),
            BuildSnapshotJson(nextSequence: 2, pendingJson: pendingOne, extraTopLevel: ",\"unexpected\":true"),
            BuildSnapshotJson(nextSequenceToken: "1.0", pendingJson: pendingOne),
            BuildSnapshotJson(memoryRevisionToken: "1.0", pendingJson: pendingOne),
            BuildSnapshotJson(memoryRevision: -1, pendingJson: pendingOne),
            BuildSnapshotJson(memoryRevision: 1, committedDayIndex: -1, nextSequence: 2, pendingJson: pendingOne),
            BuildSnapshotJson(memoryRevision: 0, committedDayIndex: 0, nextSequence: 2, pendingJson: pendingOne),
            BuildSnapshotJson(nextSequence: 1, pendingJson: pendingOne),
            BuildSnapshotJson(nextSequence: 2, pendingJson: PendingEntry(0, 0, eventOne)),
            BuildSnapshotJson(nextSequence: 2, pendingJson: PendingEntry(1, -1, eventOne)),
            BuildSnapshotJson(nextSequence: 3, pendingJson: $"{pendingOne},{duplicateEventId}"),
            BuildSnapshotJson(nextSequence: 3, pendingJson: $"{pendingOne},{duplicateSequence}"),
            BuildSnapshotJson(nextSequence: 2, pendingJson: pendingOne, deadJson: deadOne),
            BuildSnapshotJson(nextSequence: 2, pendingJson: "{\"sequence\":1,\"attempt_count\":0,\"event\":\"escaped-json\"}"),
            BuildSnapshotJson(nextSequence: 2, pendingJson: PendingEntry(1, 0, missingRequiredEventField)),
            BuildSnapshotJson(nextSequence: 2, pendingJson: PendingEntry(1, 0, eventWithUnknownField)),
            BuildSnapshotJson(nextSequence: 3, pendingJson: pendingOne, deadJson: DeadEntry(2, 0, string.Empty, eventTwo)),
            BuildSnapshotJson(nextSequence: 2, pendingJson: pendingOne[..^1] + ",\"unexpected\":true}"),
            BuildSnapshotJson(nextSequence: 2, pendingJson: "null"),
            "{\"format_version\":1,\"save_id\":\"save-a\",\"player_id\":\"player-a\",\"next_sequence\":1,\"pending\":[],\"dead_letters\":[]}",
            "{\"format_version\":1,\"format_version\":1,\"save_id\":\"save-a\",\"player_id\":\"player-a\",\"memory_revision\":0,\"committed_through_day_index\":-1,\"next_sequence\":1,\"pending\":[],\"dead_letters\":[]}",
        };

        for (int index = 0; index < corruptedSnapshots.Count; index++)
        {
            string caseDirectory = Path.Combine(testDirectory, $"corrupt-{index:D2}");
            Directory.CreateDirectory(caseDirectory);
            string filePath = Path.Combine(caseDirectory, "events.json");
            byte[] originalBytes = Encoding.UTF8.GetBytes(corruptedSnapshots[index]);
            File.WriteAllBytes(filePath, originalBytes);

            Assert.Throws<OutboxCorruptedException>(
                () => DurableEventOutbox.Open(filePath, SaveId, PlayerId));
            Assert.Equal(originalBytes, File.ReadAllBytes(filePath));
        }
    }

    /// <summary>
    /// dead-letter snapshot 的集合和嵌套 Event 都必须与内部 canonical state 隔离。
    /// </summary>
    [Fact]
    public void SnapshotDeadLetters_ReturnsReadOnlyDeepCopies()
    {
        string filePath = CreateFilePath("dead-letter-copy.json");
        DurableEventOutbox outbox = DurableEventOutbox.Open(filePath, SaveId, PlayerId);
        outbox.Enqueue(CreateEvent("event-dead"));
        PendingEventBatch batch = outbox.CreatePendingBatch("request-dead");
        outbox.ApplyResponse(batch, CreateResponse(batch, EventIngestionStatus.Rejected));

        IReadOnlyList<EventDeadLetter> firstSnapshot = outbox.SnapshotDeadLetters();
        firstSnapshot[0].Event.Source = "mutated-external-copy";
        firstSnapshot[0].Event.Payload = ParseObject("{\"mutated\":true}");
        IList<EventDeadLetter> listView = Assert.IsAssignableFrom<IList<EventDeadLetter>>(firstSnapshot);
        Assert.Throws<NotSupportedException>(() => listView.Add(firstSnapshot[0]));

        EventDeadLetter fresh = Assert.Single(outbox.SnapshotDeadLetters());
        Assert.Equal("smapi", fresh.Event.Source);
        Assert.Equal(1, fresh.Event.Payload.GetProperty("value").GetInt32());
    }

    /// <summary>
    /// 持久化失败前构造的新 attempt 不能提前替换内存；连续失败应始终从旧 attempt=0 提议 1。
    /// </summary>
    [Fact]
    public void RecordTransientFailure_WhenSnapshotWriteFailsPreservesFileAndInMemoryState()
    {
        string filePath = CreateFilePath("write-failure.json");
        DurableEventOutbox initial = DurableEventOutbox.Open(filePath, SaveId, PlayerId);
        initial.Enqueue(CreateEvent("event-write-failure"));
        byte[] originalBytes = File.ReadAllBytes(filePath);
        List<int> proposedAttemptCounts = new();
        OutboxPersistenceException injectedFailure = new(
            "injected snapshot write failure",
            new IOException("test-only move failure"));
        DurableEventOutbox failing = DurableEventOutbox.Open(
            filePath,
            SaveId,
            PlayerId,
            snapshotWriter: snapshot =>
            {
                proposedAttemptCounts.Add(snapshot.Pending.Single().AttemptCount);
                throw injectedFailure;
            });
        PendingEventBatch batch = failing.CreatePendingBatch("request-write-failure");

        Assert.Same(
            injectedFailure,
            Assert.Throws<OutboxPersistenceException>(
                () => failing.RecordTransientFailure(batch, "NETWORK_TIMEOUT")));
        Assert.Same(
            injectedFailure,
            Assert.Throws<OutboxPersistenceException>(
                () => failing.RecordTransientFailure(batch, "NETWORK_TIMEOUT")));

        Assert.Equal(new[] { 1, 1 }, proposedAttemptCounts);
        Assert.Equal(originalBytes, File.ReadAllBytes(filePath));
        Assert.Equal(new[] { 0 }, ReadPendingAttemptCounts(filePath));
    }

    /// <summary>
    /// v1 文件升级到 v2 时只能新增 checkpoint 容器；pending、dead letter 与后端水位必须逐字保留。
    /// </summary>
    [Fact]
    public void InitializeProducerCheckpoint_UpgradesV1SnapshotWithoutChangingExistingOutboxState()
    {
        string filePath = CreateFilePath("checkpoint-v1-upgrade.json");
        string pendingEvent = ContractJson.Serialize(CreateEvent("event-v1-pending", occurredDayIndex: 12));
        string deadEvent = ContractJson.Serialize(CreateEvent("event-v1-dead", occurredDayIndex: 11));
        File.WriteAllText(
            filePath,
            BuildSnapshotJson(
                formatVersion: 1,
                memoryRevision: 2,
                committedDayIndex: 12,
                nextSequence: 3,
                pendingJson: PendingEntry(1, 1, pendingEvent),
                deadJson: DeadEntry(2, 2, "PERMANENT", deadEvent)));
        DurableEventOutbox outbox = DurableEventOutbox.Open(filePath, SaveId, PlayerId);

        outbox.InitializeProducerCheckpoint(
            "friendship:Abigail:friend",
            "baseline_existing",
            observedDayIndex: 12);

        Assert.Equal(1, outbox.PendingCount);
        Assert.Equal(1, outbox.DeadLetterCount);
        Assert.Equal(2, outbox.MemoryRevision);
        Assert.Equal(12, outbox.CommittedThroughDayIndex);
        ProducerCheckpoint checkpoint = Assert.Single(outbox.SnapshotProducerCheckpoints());
        Assert.Equal("friendship:Abigail:friend", checkpoint.ProducerId);
        Assert.Equal("baseline_existing", checkpoint.Status);
        Assert.Equal(12, checkpoint.ObservedDayIndex);

        using JsonDocument persisted = JsonDocument.Parse(File.ReadAllText(filePath));
        JsonElement root = persisted.RootElement;
        Assert.Equal(2, root.GetProperty("format_version").GetInt32());
        Assert.Equal(2, root.GetProperty("memory_revision").GetInt32());
        Assert.Equal(12, root.GetProperty("committed_through_day_index").GetInt32());
        Assert.Equal(3, root.GetProperty("next_sequence").GetInt64());
        JsonElement persistedPending = Assert.Single(root.GetProperty("pending").EnumerateArray());
        Assert.Equal(1, persistedPending.GetProperty("sequence").GetInt64());
        Assert.Equal(1, persistedPending.GetProperty("attempt_count").GetInt32());
        Assert.Equal(
            "event-v1-pending",
            persistedPending.GetProperty("event").GetProperty("event_id").GetString());
        JsonElement persistedDead = Assert.Single(root.GetProperty("dead_letters").EnumerateArray());
        Assert.Equal(2, persistedDead.GetProperty("sequence").GetInt64());
        Assert.Equal(2, persistedDead.GetProperty("attempt_count").GetInt32());
        Assert.Equal("PERMANENT", persistedDead.GetProperty("reason_code").GetString());
        Assert.Equal(
            "event-v1-dead",
            persistedDead.GetProperty("event").GetProperty("event_id").GetString());
        Assert.Single(root.GetProperty("producer_checkpoints").EnumerateArray());

        DurableEventOutbox reopened = DurableEventOutbox.Open(filePath, SaveId, PlayerId);
        Assert.Equal(checkpoint, Assert.Single(reopened.SnapshotProducerCheckpoints()));
    }

    /// <summary>
    /// 完全相同的 baseline 初始化是零写入；同 key 的不同事实不能静默覆盖。
    /// </summary>
    [Fact]
    public void InitializeProducerCheckpoint_IsIdempotentButRejectsConflictingFact()
    {
        string filePath = CreateFilePath("checkpoint-idempotent.json");
        DurableEventOutbox outbox = DurableEventOutbox.Open(filePath, SaveId, PlayerId);
        outbox.InitializeProducerCheckpoint("friendship:Abigail:friend", "baseline_below", 12);
        byte[] firstBytes = File.ReadAllBytes(filePath);

        outbox.InitializeProducerCheckpoint("friendship:Abigail:friend", "baseline_below", 12);

        Assert.Equal(firstBytes, File.ReadAllBytes(filePath));
        Assert.Throws<OutboxIdentityConflictException>(
            () => outbox.InitializeProducerCheckpoint(
                "friendship:Abigail:friend",
                "baseline_existing",
                12));
        Assert.Equal(firstBytes, File.ReadAllBytes(filePath));
    }

    /// <summary>
    /// checkpoint 是有界 producer 状态，不允许通过超长字符串或无限 key 把 outbox 变成通用数据库。
    /// </summary>
    [Fact]
    public void InitializeProducerCheckpoint_EnforcesStableBoundedState()
    {
        string filePath = CreateFilePath("checkpoint-bounds.json");
        DurableEventOutbox outbox = DurableEventOutbox.Open(filePath, SaveId, PlayerId);

        Assert.Throws<ArgumentException>(
            () => outbox.InitializeProducerCheckpoint(new string('p', 257), "seen", 12));
        Assert.Throws<ArgumentException>(
            () => outbox.InitializeProducerCheckpoint("friendship:Abigail:friend", new string('s', 65), 12));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => outbox.InitializeProducerCheckpoint("friendship:Abigail:friend", "seen", -1));

        for (int index = 0; index < 128; index++)
        {
            outbox.InitializeProducerCheckpoint($"producer:{index:D3}", "baseline_below", 12);
        }

        Assert.Equal(128, outbox.SnapshotProducerCheckpoints().Count);
        Assert.Throws<InvalidOperationException>(
            () => outbox.InitializeProducerCheckpoint("producer:overflow", "baseline_below", 12));
    }

    /// <summary>
    /// 无法确定历史发生日的 SaveLoaded 对账只推进 baseline，不生产事件；CAS 防止覆盖已 seen 状态。
    /// </summary>
    [Fact]
    public void ReconcileProducerCheckpointWithoutEvent_UsesCompareAndSetAndIsIdempotent()
    {
        string filePath = CreateFilePath("checkpoint-baseline-reconcile.json");
        DurableEventOutbox outbox = DurableEventOutbox.Open(filePath, SaveId, PlayerId);
        outbox.InitializeProducerCheckpoint("friendship:Abigail:friend", "baseline_below", 10);

        outbox.ReconcileProducerCheckpointWithoutEvent(
            "friendship:Abigail:friend",
            expectedStatus: "baseline_below",
            status: "baseline_existing",
            observedDayIndex: 13);

        Assert.Equal(0, outbox.PendingCount);
        Assert.Equal(
            new ProducerCheckpoint("friendship:Abigail:friend", "baseline_existing", 13),
            Assert.Single(outbox.SnapshotProducerCheckpoints()));
        byte[] reconciledBytes = File.ReadAllBytes(filePath);

        outbox.ReconcileProducerCheckpointWithoutEvent(
            "friendship:Abigail:friend",
            expectedStatus: "baseline_below",
            status: "baseline_existing",
            observedDayIndex: 13);
        Assert.Equal(reconciledBytes, File.ReadAllBytes(filePath));
        Assert.Throws<OutboxIdentityConflictException>(
            () => outbox.ReconcileProducerCheckpointWithoutEvent(
                "friendship:Abigail:friend",
                expectedStatus: "baseline_below",
                status: "seen",
                observedDayIndex: 14));
        Assert.Equal(reconciledBytes, File.ReadAllBytes(filePath));
    }

    /// <summary>
    /// 首次跨越里程碑时，事件与 seen checkpoint 必须通过一次 snapshot mutation 同时落盘。
    /// </summary>
    [Fact]
    public void EnqueueAndAdvanceCheckpoint_PersistsEventAndSeenStateAtomicallyAndSurvivesRestart()
    {
        string filePath = CreateFilePath("checkpoint-event-atomic.json");
        DurableEventOutbox outbox = DurableEventOutbox.Open(filePath, SaveId, PlayerId);
        outbox.InitializeProducerCheckpoint("friendship:Abigail:friend", "baseline_below", 12);
        GameEvent milestone = CreateEvent("event-friendship-milestone", occurredDayIndex: 13);

        outbox.EnqueueAndAdvanceCheckpoint(
            milestone,
            "friendship:Abigail:friend",
            "seen",
            observedDayIndex: 13);

        Assert.Equal(
            "event-friendship-milestone",
            outbox.CreatePendingBatch("request").Request.Events[0].EventId);
        Assert.Equal(
            new ProducerCheckpoint("friendship:Abigail:friend", "seen", 13),
            Assert.Single(outbox.SnapshotProducerCheckpoints()));

        DurableEventOutbox reopened = DurableEventOutbox.Open(filePath, SaveId, PlayerId);
        Assert.Equal(1, reopened.PendingCount);
        Assert.Equal(
            new ProducerCheckpoint("friendship:Abigail:friend", "seen", 13),
            Assert.Single(reopened.SnapshotProducerCheckpoints()));

        byte[] pendingBytes = File.ReadAllBytes(filePath);
        reopened.EnqueueAndAdvanceCheckpoint(
            milestone,
            "friendship:Abigail:friend",
            "seen",
            observedDayIndex: 13);
        Assert.Equal(pendingBytes, File.ReadAllBytes(filePath));

        PendingEventBatch completedBatch = reopened.CreatePendingBatch("request-complete-milestone");
        reopened.ApplyResponse(
            completedBatch,
            CreateResponse(completedBatch, EventIngestionStatus.Accepted));
        Assert.Equal(0, reopened.PendingCount);
        byte[] completedBytes = File.ReadAllBytes(filePath);

        // checkpoint 保留首次事实，因此即使 event 已上传删除，同一游戏回调重放也不能再次入队。
        reopened.EnqueueAndAdvanceCheckpoint(
            milestone,
            "friendship:Abigail:friend",
            "seen",
            observedDayIndex: 13);
        Assert.Equal(0, reopened.PendingCount);
        Assert.Equal(completedBytes, File.ReadAllBytes(filePath));
    }

    /// <summary>
    /// writer 失败时不能只留下 event 或只推进 checkpoint；重试必须从同一旧状态重新提议。
    /// </summary>
    [Fact]
    public void EnqueueAndAdvanceCheckpoint_WhenSnapshotWriteFailsPreservesBothOldStates()
    {
        string filePath = CreateFilePath("checkpoint-write-failure.json");
        DurableEventOutbox initial = DurableEventOutbox.Open(filePath, SaveId, PlayerId);
        initial.InitializeProducerCheckpoint("friendship:Abigail:friend", "baseline_below", 12);
        byte[] originalBytes = File.ReadAllBytes(filePath);
        List<(int Pending, string Status)> proposals = new();
        OutboxPersistenceException injectedFailure = new(
            "injected checkpoint write failure",
            new IOException("test-only move failure"));
        DurableEventOutbox failing = DurableEventOutbox.Open(
            filePath,
            SaveId,
            PlayerId,
            snapshotWriter: snapshot =>
            {
                proposals.Add(
                    (
                        snapshot.Pending.Count,
                        Assert.Single(snapshot.ProducerCheckpoints).Status));
                throw injectedFailure;
            });
        GameEvent milestone = CreateEvent("event-checkpoint-write-failure", occurredDayIndex: 13);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            Assert.Same(
                injectedFailure,
                Assert.Throws<OutboxPersistenceException>(
                    () => failing.EnqueueAndAdvanceCheckpoint(
                        milestone,
                        "friendship:Abigail:friend",
                        "seen",
                        13)));
        }

        Assert.Equal(new[] { (1, "seen"), (1, "seen") }, proposals);
        Assert.Equal(0, failing.PendingCount);
        Assert.Equal(
            new ProducerCheckpoint("friendship:Abigail:friend", "baseline_below", 12),
            Assert.Single(failing.SnapshotProducerCheckpoints()));
        Assert.Equal(originalBytes, File.ReadAllBytes(filePath));
    }

    /// <summary>
    /// v2 checkpoint 腐化只禁用依赖 checkpoint 的 producer；普通事件 outbox 必须继续工作。
    /// </summary>
    [Fact]
    public void Open_WhenProducerCheckpointIsCorruptIsolatesCheckpointStateAndPreservesOpaqueValue()
    {
        string first = CheckpointEntry("friendship:Abigail:friend", "seen", 13);
        string duplicate = CheckpointEntry("friendship:Abigail:friend", "baseline_below", 12);
        string[] corruptedSnapshots =
        {
            BuildSnapshotJson(formatVersion: 2, checkpointJson: "null"),
            BuildSnapshotJson(formatVersion: 2, checkpointJson: first + "," + duplicate),
            BuildSnapshotJson(
                formatVersion: 2,
                checkpointJson: "{\"producer_id\":\"friendship:Abigail:friend\",\"status\":\"seen\",\"observed_day_index\":-1}"),
            BuildSnapshotJson(
                formatVersion: 2,
                checkpointJson: first[..^1] + ",\"unexpected\":true}"),
        };

        for (int index = 0; index < corruptedSnapshots.Length; index++)
        {
            string filePath = CreateFilePath($"checkpoint-corrupt-{index}.json");
            File.WriteAllText(filePath, corruptedSnapshots[index]);
            byte[] original = File.ReadAllBytes(filePath);
            using JsonDocument originalDocument = JsonDocument.Parse(corruptedSnapshots[index]);
            string originalCheckpointJson = originalDocument.RootElement
                .GetProperty("producer_checkpoints")
                .GetRawText();

            DurableEventOutbox outbox = DurableEventOutbox.Open(filePath, SaveId, PlayerId);

            Assert.False(outbox.ProducerCheckpointsAvailable);
            Assert.Throws<InvalidOperationException>(() => outbox.SnapshotProducerCheckpoints());
            Assert.Throws<InvalidOperationException>(
                () => outbox.InitializeProducerCheckpoint(
                    "friendship:Abigail:friend",
                    "baseline_below",
                    13));
            Assert.Equal(original, File.ReadAllBytes(filePath));

            // 非 checkpoint producer 仍可 durable enqueue；重写 snapshot 时必须保留未知状态，
            // 不能把腐化值偷偷替换成空数组并让 friendship producer 重新猜 baseline。
            string eventId = $"event-after-corrupt-checkpoint-{index}";
            outbox.Enqueue(CreateEvent(eventId, occurredDayIndex: 13));
            using JsonDocument rewritten = JsonDocument.Parse(File.ReadAllText(filePath));
            Assert.Equal(
                originalCheckpointJson,
                rewritten.RootElement.GetProperty("producer_checkpoints").GetRawText());

            DurableEventOutbox reopened = DurableEventOutbox.Open(filePath, SaveId, PlayerId);
            Assert.False(reopened.ProducerCheckpointsAvailable);
            Assert.Equal(eventId, reopened.CreatePendingBatch("request").Request.Events[0].EventId);
        }
    }

    /// <summary>
    /// 新公开的单事件入口必须复用批次的相同规则，而不是形成第二份漂移校验器。
    /// </summary>
    [Fact]
    public void ContractValidator_SingleEventEntryPointUsesExistingEventRules()
    {
        GameEvent valid = CreateEvent("event-valid");
        GameEvent invalid = CloneEvent(valid);
        invalid.Payload = JsonDocument.Parse("[]").RootElement.Clone();

        ContractValidationResult validResult = ContractValidator.Validate(valid);
        ContractValidationResult invalidResult = ContractValidator.Validate(invalid);

        Assert.True(validResult.IsValid, validResult.ToString());
        Assert.False(invalidResult.IsValid);
        Assert.Contains(invalidResult.Errors, error => error.Path.EndsWith("payload", StringComparison.Ordinal));
    }

    /// <summary>
    /// 删除当前测试实例创建的唯一临时目录；不会触碰任何真实存档、Mods 或其他测试目录。
    /// </summary>
    public void Dispose()
    {
        if (Directory.Exists(testDirectory))
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    /// <summary>
    /// 构造一个合同合法、字段可读且 payload 可按测试替换的事件。
    /// </summary>
    private static GameEvent CreateEvent(
        string eventId,
        int occurredDayIndex = 13,
        string source = "smapi",
        string payloadJson = "{\"value\":1}")
    {
        return new GameEvent
        {
            EventId = eventId,
            EventType = "npc_interaction",
            EventVersion = "1.0",
            OccurredDayIndex = occurredDayIndex,
            Source = source,
            AudienceScope = AudienceScope.Public,
            AudienceNpcId = null,
            Payload = ParseObject(payloadJson),
        };
    }

    /// <summary>
    /// 经正式合同 serializer 深拷贝事件，避免测试变体共享 JsonElement 或 DTO 引用。
    /// </summary>
    private static GameEvent CloneEvent(GameEvent gameEvent)
    {
        return ContractJson.Deserialize<GameEvent>(ContractJson.Serialize(gameEvent));
    }

    /// <summary>
    /// 解析测试 payload 并 clone，确保 JsonDocument 释放后 JsonElement 仍可使用。
    /// </summary>
    private static JsonElement ParseObject(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// 按 batch 顺序构造字段完整的后端响应；单个 status 参数会广播到所有条目。
    /// </summary>
    private static GameEventBatchResponse CreateResponse(
        PendingEventBatch batch,
        params EventIngestionStatus[] statuses)
    {
        if (statuses.Length == 1 && batch.Request.Events.Count > 1)
        {
            statuses = Enumerable.Repeat(statuses[0], batch.Request.Events.Count).ToArray();
        }

        if (statuses.Length != batch.Request.Events.Count)
        {
            throw new ArgumentException("测试响应 status 数必须等于 batch 事件数。", nameof(statuses));
        }

        return new GameEventBatchResponse
        {
            SchemaVersion = ContractVersions.V1,
            RequestId = batch.Request.RequestId,
            MemoryRevision = 1,
            CommittedThroughDayIndex = 13,
            Items = batch.Request.Events
                .Select(
                    (gameEvent, index) => new GameEventItemResult
                    {
                        EventId = gameEvent.EventId,
                        Status = statuses[index],
                        ReasonCode = null,
                    })
                .ToList(),
        };
    }

    /// <summary>
    /// 创建当前测试目录内的 outbox 文件路径，不预先创建目标文件。
    /// </summary>
    private string CreateFilePath(string fileName)
    {
        return Path.Combine(testDirectory, fileName);
    }

    /// <summary>
    /// 读取落盘 pending attempt，直接证明重启恢复值而非只看当前对象计数。
    /// </summary>
    private static int[] ReadPendingAttemptCounts(string filePath)
    {
        using JsonDocument snapshot = JsonDocument.Parse(File.ReadAllText(filePath));
        return snapshot.RootElement
            .GetProperty("pending")
            .EnumerateArray()
            .Select(item => item.GetProperty("attempt_count").GetInt32())
            .ToArray();
    }

    /// <summary>
    /// 对当前 target 计算 SHA-256，便于断言失败路径没有发生任何字节替换。
    /// </summary>
    private static string ComputeSha256(string filePath)
    {
        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(filePath)));
    }

    /// <summary>
    /// 构造一个 pending entry JSON；eventJson 必须是 object 文本而非转义字符串。
    /// </summary>
    private static string PendingEntry(long sequence, int attemptCount, string eventJson)
    {
        return "{"
            + $"\"sequence\":{sequence},"
            + $"\"attempt_count\":{attemptCount},"
            + $"\"event\":{eventJson}"
            + "}";
    }

    /// <summary>
    /// 构造一个 dead-letter entry JSON，供腐化矩阵精确改变单一不变量。
    /// </summary>
    private static string DeadEntry(
        long sequence,
        int attemptCount,
        string reasonCode,
        string eventJson)
    {
        return "{"
            + $"\"sequence\":{sequence},"
            + $"\"attempt_count\":{attemptCount},"
            + $"\"reason_code\":{JsonSerializer.Serialize(reasonCode)},"
            + $"\"event\":{eventJson}"
            + "}";
    }

    /// <summary>
    /// 构造一个 producer checkpoint JSON，供 v2 快照兼容性与腐化矩阵复用。
    /// </summary>
    private static string CheckpointEntry(
        string producerId,
        string status,
        int observedDayIndex)
    {
        return "{"
            + $"\"producer_id\":{JsonSerializer.Serialize(producerId)},"
            + $"\"status\":{JsonSerializer.Serialize(status)},"
            + $"\"observed_day_index\":{observedDayIndex}"
            + "}";
    }

    /// <summary>
    /// 构造完整 snapshot JSON；参数只服务于损坏矩阵，不参与生产序列化。
    /// </summary>
    private static string BuildSnapshotJson(
        int formatVersion = 1,
        string saveId = SaveId,
        string playerId = PlayerId,
        int memoryRevision = 0,
        int committedDayIndex = -1,
        long nextSequence = 1,
        string? memoryRevisionToken = null,
        string? nextSequenceToken = null,
        string pendingJson = "",
        string deadJson = "",
        string checkpointJson = "",
        string extraTopLevel = "")
    {
        string revisionToken = memoryRevisionToken
            ?? memoryRevision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string sequenceToken = nextSequenceToken
            ?? nextSequence.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string producerCheckpoints = formatVersion >= 2
            ? $",\"producer_checkpoints\":[{checkpointJson}]"
            : string.Empty;
        return "{"
            + $"\"format_version\":{formatVersion},"
            + $"\"save_id\":{JsonSerializer.Serialize(saveId)},"
            + $"\"player_id\":{JsonSerializer.Serialize(playerId)},"
            + $"\"memory_revision\":{revisionToken},"
            + $"\"committed_through_day_index\":{committedDayIndex},"
            + $"\"next_sequence\":{sequenceToken},"
            + $"\"pending\":[{pendingJson}],"
            + $"\"dead_letters\":[{deadJson}]"
            + producerCheckpoints
            + extraTopLevel
            + "}";
    }
}
