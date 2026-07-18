using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StardewNpcAgent.Contracts;
using StardewNpcAgent.Infrastructure.Storage;

namespace StardewNpcAgent.Tests.Infrastructure.Storage;

/// <summary>
/// 使用真实临时文件验证 displayed ACK outbox 的完整单项 FIFO 状态机。
/// </summary>
/// <remarks>
/// ACK endpoint 不批量发送，因此测试始终只允许当前 FIFO 首项发生状态变化。网络在途、
/// 重启和写盘失败通过公开快照与文件字节共同验证，不启动 HTTP、SMAPI 或游戏进程。
/// </remarks>
public sealed class DurableDisplayAckOutboxTests : IDisposable
{
    private const string SaveId = "save-a";
    private const string PlayerId = "player-a";
    private readonly string testDirectory;

    /// <summary>
    /// 为每个测试实例创建唯一临时目录，避免并行测试互相共享 outbox 文件。
    /// </summary>
    public DurableDisplayAckOutboxTests()
    {
        testDirectory = Path.Combine(
            Path.GetTempPath(),
            "StardewNpcAgent.Tests",
            $"DurableDisplayAckOutbox.{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
    }

    /// <summary>
    /// 缺失文件应表现为空且零写入；首次 Enqueue 后 generation/request/attempt 必须可跨重启恢复。
    /// </summary>
    [Fact]
    public void Open_WhenSnapshotIsMissingReturnsEmptyWithoutWritingAndFirstEnqueueSurvivesRestart()
    {
        string missingParent = Path.Combine(testDirectory, "missing-parent");
        string filePath = Path.Combine(missingParent, "display-acks.json");

        DurableDisplayAckOutbox outbox = DurableDisplayAckOutbox.Open(filePath, SaveId, PlayerId);

        Assert.Equal(0, outbox.PendingCount);
        Assert.Equal(0, outbox.DeadLetterCount);
        Assert.Null(outbox.CreateNextAttempt());
        Assert.False(Directory.Exists(missingParent));

        outbox.Enqueue("generation-001", CreateRequest("receipt-001"));

        DurableDisplayAckOutbox reopened = DurableDisplayAckOutbox.Open(filePath, SaveId, PlayerId);
        PendingDisplayAck attempt = Assert.IsType<PendingDisplayAck>(reopened.CreateNextAttempt());
        Assert.Equal("generation-001", attempt.GenerationId);
        Assert.Equal("receipt-001", attempt.Request.DisplayReceiptId);
        Assert.Equal(1L, attempt.Sequence);
        Assert.Equal(0, attempt.AttemptCount);

        using JsonDocument snapshot = JsonDocument.Parse(File.ReadAllText(filePath));
        Assert.Equal(
            JsonValueKind.Object,
            snapshot.RootElement.GetProperty("pending")[0].GetProperty("request").ValueKind);
    }

    /// <summary>
    /// 同 receipt、generation 与逐字段 request 等价重入必须是真正零写 no-op，且不保留调用方 DTO。
    /// </summary>
    [Fact]
    public void Enqueue_EquivalentReceiptIsZeroWriteNoOpAndCallerMutationCannotChangeStoredRequest()
    {
        string filePath = CreateFilePath("equivalent.json");
        DisplayAckRequest original = CreateRequest("receipt-equivalent");
        DurableDisplayAckOutbox initial = DurableDisplayAckOutbox.Open(filePath, SaveId, PlayerId);
        initial.Enqueue("generation-equivalent", original);
        byte[] originalBytes = File.ReadAllBytes(filePath);
        original.NpcId = "MutatedAfterEnqueue";
        original.SourceHash = "sha256:mutated";
        int writeCount = 0;
        DurableDisplayAckOutbox reopenedWithWriterProbe = DurableDisplayAckOutbox.Open(
            filePath,
            SaveId,
            PlayerId,
            snapshotWriter: _ => writeCount++);

        reopenedWithWriterProbe.Enqueue(
            "generation-equivalent",
            CreateRequest("receipt-equivalent"));

        Assert.Equal(0, writeCount);
        Assert.Equal(originalBytes, File.ReadAllBytes(filePath));
        PendingDisplayAck stored = Assert.IsType<PendingDisplayAck>(
            reopenedWithWriterProbe.CreateNextAttempt());
        Assert.Equal("Abigail", stored.Request.NpcId);
        Assert.Equal("sha256:source", stored.Request.SourceHash);
    }

    /// <summary>
    /// receipt 是不可变身份；generation、day、NPC、hash 或任一 request 字段变化都必须冲突。
    /// </summary>
    [Fact]
    public void Enqueue_SameReceiptWithDifferentFactThrowsConflictAndPreservesOldSnapshot()
    {
        string filePath = CreateFilePath("conflicts.json");
        DisplayAckRequest baseline = CreateRequest("receipt-conflict");
        DurableDisplayAckOutbox outbox = DurableDisplayAckOutbox.Open(filePath, SaveId, PlayerId);
        outbox.Enqueue("generation-original", baseline);
        string originalHash = ComputeSha256(filePath);

        DisplayAckRequest changedDay = CloneRequest(baseline);
        changedDay.DisplayedDayIndex++;
        DisplayAckRequest changedNpc = CloneRequest(baseline);
        changedNpc.NpcId = "Leah";
        DisplayAckRequest changedHash = CloneRequest(baseline);
        changedHash.SourceHash = "sha256:different";
        DisplayAckRequest changedRequestId = CloneRequest(baseline);
        changedRequestId.RequestId = "request-different";
        (string GenerationId, DisplayAckRequest Request)[] conflicts =
        {
            ("generation-different", CloneRequest(baseline)),
            ("generation-original", changedDay),
            ("generation-original", changedNpc),
            ("generation-original", changedHash),
            ("generation-original", changedRequestId),
        };

        foreach ((string generationId, DisplayAckRequest request) in conflicts)
        {
            Assert.Throws<OutboxIdentityConflictException>(
                () => outbox.Enqueue(generationId, request));
            Assert.Equal(originalHash, ComputeSha256(filePath));
            Assert.Equal(1, outbox.PendingCount);
        }
    }

    /// <summary>
    /// 只有 FIFO 首项可被返回；公开 attempt 是深拷贝，外部修改不能改变后续读取或第二项顺序。
    /// </summary>
    [Fact]
    public void CreateNextAttempt_ReturnsFifoDeepCopyAndEmptyOutboxReturnsNull()
    {
        string filePath = CreateFilePath("fifo.json");
        DurableDisplayAckOutbox outbox = DurableDisplayAckOutbox.Open(filePath, SaveId, PlayerId);
        outbox.Enqueue("generation-first", CreateRequest("receipt-first"));
        outbox.Enqueue("generation-second", CreateRequest("receipt-second"));

        PendingDisplayAck firstCopy = Assert.IsType<PendingDisplayAck>(outbox.CreateNextAttempt());
        firstCopy.Request.NpcId = "MutatedCopy";
        PendingDisplayAck freshFirst = Assert.IsType<PendingDisplayAck>(outbox.CreateNextAttempt());

        Assert.Equal("receipt-first", freshFirst.Request.DisplayReceiptId);
        Assert.Equal("Abigail", freshFirst.Request.NpcId);
        Assert.Equal(1L, freshFirst.Sequence);

        outbox.MarkDelivered(freshFirst, CreateResponse(freshFirst, DisplayAckStatus.Accepted));
        PendingDisplayAck second = Assert.IsType<PendingDisplayAck>(outbox.CreateNextAttempt());
        Assert.Equal("receipt-second", second.Request.DisplayReceiptId);
        Assert.Equal(2L, second.Sequence);
        outbox.MarkDelivered(second, CreateResponse(second, DisplayAckStatus.Duplicate));
        Assert.Null(outbox.CreateNextAttempt());
    }

    /// <summary>
    /// accepted/duplicate 均删除一次；成功响应重放是真正零写 no-op，且不影响在途期间追加项。
    /// </summary>
    [Fact]
    public void MarkDelivered_AcceptedAndDuplicateAreReplaySafeAndPreserveAppendedItem()
    {
        foreach (DisplayAckStatus status in new[] { DisplayAckStatus.Accepted, DisplayAckStatus.Duplicate })
        {
            string filePath = CreateFilePath($"delivered-{status}.json");
            DurableDisplayAckOutbox outbox = DurableDisplayAckOutbox.Open(filePath, SaveId, PlayerId);
            outbox.Enqueue($"generation-{status}", CreateRequest($"receipt-{status}"));
            PendingDisplayAck inFlight = Assert.IsType<PendingDisplayAck>(outbox.CreateNextAttempt());
            outbox.Enqueue("generation-appended", CreateRequest($"receipt-appended-{status}"));
            DisplayAckResponse response = CreateResponse(inFlight, status);

            outbox.MarkDelivered(inFlight, response);
            byte[] bytesAfterDelivery = File.ReadAllBytes(filePath);
            int replayWriteCount = 0;
            DurableDisplayAckOutbox reopened = DurableDisplayAckOutbox.Open(
                filePath,
                SaveId,
                PlayerId,
                snapshotWriter: _ => replayWriteCount++);
            reopened.MarkDelivered(inFlight, response);

            Assert.Equal(0, replayWriteCount);
            Assert.Equal(bytesAfterDelivery, File.ReadAllBytes(filePath));
            Assert.Equal(1, reopened.PendingCount);
            Assert.Equal(
                $"receipt-appended-{status}",
                reopened.CreateNextAttempt()!.Request.DisplayReceiptId);
        }
    }

    /// <summary>
    /// 瞬态失败只递增当前首项 attempt，一次调用加一，并可跨重启继续累积。
    /// </summary>
    [Fact]
    public void RecordTransientFailure_IncrementsOnceAndPersistsAcrossRestart()
    {
        string filePath = CreateFilePath("transient.json");
        DurableDisplayAckOutbox outbox = DurableDisplayAckOutbox.Open(filePath, SaveId, PlayerId);
        outbox.Enqueue("generation-transient", CreateRequest("receipt-transient"));
        PendingDisplayAck firstAttempt = Assert.IsType<PendingDisplayAck>(outbox.CreateNextAttempt());

        outbox.RecordTransientFailure(firstAttempt, "SERVICE_UNAVAILABLE");

        Assert.Equal(1, ReadPendingAttemptCount(filePath));
        DurableDisplayAckOutbox reopened = DurableDisplayAckOutbox.Open(filePath, SaveId, PlayerId);
        PendingDisplayAck secondAttempt = Assert.IsType<PendingDisplayAck>(reopened.CreateNextAttempt());
        Assert.Equal(1, secondAttempt.AttemptCount);
        reopened.RecordTransientFailure(secondAttempt, "NETWORK_TIMEOUT");
        Assert.Equal(2, ReadPendingAttemptCount(filePath));
    }

    /// <summary>
    /// 永久失败从 pending 移入 dead letter，保留 attempt 和稳定原因；公开 dead snapshot 必须深拷贝只读。
    /// </summary>
    [Fact]
    public void RecordPermanentFailure_MovesItemToReadOnlyDeepCopiedDeadLetter()
    {
        string filePath = CreateFilePath("permanent.json");
        DurableDisplayAckOutbox outbox = DurableDisplayAckOutbox.Open(filePath, SaveId, PlayerId);
        outbox.Enqueue("generation-permanent", CreateRequest("receipt-permanent"));
        PendingDisplayAck firstAttempt = Assert.IsType<PendingDisplayAck>(outbox.CreateNextAttempt());
        outbox.RecordTransientFailure(firstAttempt, "NETWORK_TIMEOUT");
        PendingDisplayAck retry = Assert.IsType<PendingDisplayAck>(outbox.CreateNextAttempt());

        outbox.RecordPermanentFailure(retry, "DISPLAY_ACK_NOT_ALLOWED");

        Assert.Equal(0, outbox.PendingCount);
        Assert.Equal(1, outbox.DeadLetterCount);
        IReadOnlyList<DisplayAckDeadLetter> firstSnapshot = outbox.SnapshotDeadLetters();
        DisplayAckDeadLetter dead = Assert.Single(firstSnapshot);
        Assert.Equal("generation-permanent", dead.GenerationId);
        Assert.Equal(1, dead.AttemptCount);
        Assert.Equal("DISPLAY_ACK_NOT_ALLOWED", dead.ReasonCode);
        dead.Request.NpcId = "MutatedExternalCopy";
        IList<DisplayAckDeadLetter> listView = Assert.IsAssignableFrom<IList<DisplayAckDeadLetter>>(firstSnapshot);
        Assert.Throws<NotSupportedException>(() => listView.Add(dead));
        Assert.Equal("Abigail", Assert.Single(outbox.SnapshotDeadLetters()).Request.NpcId);
    }

    /// <summary>
    /// response 的 request_id、receipt_id、schema 或手工未声明 status 任一非法时整份状态不变。
    /// </summary>
    [Fact]
    public void MarkDelivered_WhenResponseIsInvalidRejectsWithoutChangingState()
    {
        string filePath = CreateFilePath("invalid-response.json");
        DurableDisplayAckOutbox outbox = DurableDisplayAckOutbox.Open(filePath, SaveId, PlayerId);
        outbox.Enqueue("generation-response", CreateRequest("receipt-response"));
        PendingDisplayAck attempt = Assert.IsType<PendingDisplayAck>(outbox.CreateNextAttempt());
        string originalHash = ComputeSha256(filePath);
        List<Action<DisplayAckResponse>> invalidMutations = new()
        {
            response => response.RequestId = "wrong-request",
            response => response.DisplayReceiptId = "wrong-receipt",
            response => response.SchemaVersion = "2.0",
            response => response.Status = (DisplayAckStatus)999,
        };

        foreach (Action<DisplayAckResponse> mutate in invalidMutations)
        {
            DisplayAckResponse response = CreateResponse(attempt, DisplayAckStatus.Accepted);
            mutate(response);

            Assert.Throws<ArgumentException>(() => outbox.MarkDelivered(attempt, response));
            Assert.Equal(originalHash, ComputeSha256(filePath));
            Assert.Equal(1, outbox.PendingCount);
        }
    }

    /// <summary>
    /// generation、request、sequence、attempt 或 canonical 任一被修改都不能授权三个 mutation API。
    /// </summary>
    [Fact]
    public void MutatingReturnedAttemptCannotChangeOrAuthorizeDifferentStoredAck()
    {
        string filePath = CreateFilePath("mutated-attempt.json");
        DurableDisplayAckOutbox outbox = DurableDisplayAckOutbox.Open(filePath, SaveId, PlayerId);
        outbox.Enqueue("generation-original", CreateRequest("receipt-original"));
        string originalHash = ComputeSha256(filePath);

        PendingDisplayAck wrongGeneration = outbox.CreateNextAttempt()! with
        {
            GenerationId = "generation-forged",
        };
        Assert.Throws<ArgumentException>(
            () => outbox.MarkDelivered(
                wrongGeneration,
                CreateResponse(wrongGeneration, DisplayAckStatus.Accepted)));

        PendingDisplayAck wrongSequence = outbox.CreateNextAttempt()! with { Sequence = 99 };
        Assert.Throws<ArgumentException>(
            () => outbox.RecordTransientFailure(wrongSequence, "NETWORK_TIMEOUT"));

        PendingDisplayAck wrongAttempt = outbox.CreateNextAttempt()! with { AttemptCount = 3 };
        Assert.Throws<ArgumentException>(
            () => outbox.RecordPermanentFailure(wrongAttempt, "PERMANENT"));

        PendingDisplayAck wrongRequest = outbox.CreateNextAttempt()!;
        wrongRequest.Request.NpcId = "Leah";
        Assert.Throws<ArgumentException>(
            () => outbox.RecordTransientFailure(wrongRequest, "NETWORK_TIMEOUT"));

        PendingDisplayAck wrongCanonical = outbox.CreateNextAttempt()! with
        {
            CanonicalRequestJson = "{}",
        };
        Assert.Throws<ArgumentException>(
            () => outbox.RecordPermanentFailure(wrongCanonical, "PERMANENT"));

        Assert.Equal(originalHash, ComputeSha256(filePath));
        Assert.Equal(1, outbox.PendingCount);
        Assert.Equal(0, ReadPendingAttemptCount(filePath));
    }

    /// <summary>
    /// 未通过合同或与 outbox 分区不一致的 Enqueue 必须在创建文件前失败。
    /// </summary>
    [Fact]
    public void Enqueue_WhenGenerationOrRequestIsInvalidRejectsBeforeWriting()
    {
        string filePath = CreateFilePath("invalid-enqueue.json");
        DurableDisplayAckOutbox outbox = DurableDisplayAckOutbox.Open(filePath, SaveId, PlayerId);
        DisplayAckRequest wrongPartition = CreateRequest("receipt-wrong-partition");
        wrongPartition.PlayerId = "another-player";
        DisplayAckRequest invalidContract = CreateRequest("receipt-invalid-contract");
        invalidContract.SourceHash = " ";

        Assert.Throws<ArgumentException>(() => outbox.Enqueue(" ", CreateRequest("receipt-generation")));
        Assert.Throws<ArgumentException>(() => outbox.Enqueue("generation-partition", wrongPartition));
        Assert.Throws<ArgumentException>(() => outbox.Enqueue("generation-contract", invalidContract));

        Assert.False(File.Exists(filePath));
        Assert.Equal(0, outbox.PendingCount);
    }

    /// <summary>
    /// snapshot 的版本、字段、整数、分区、唯一键和 request 合同任一损坏都必须 fail closed 且不改字节。
    /// </summary>
    [Fact]
    public void Open_WhenSnapshotViolatesFrozenFormatRejectsEveryCaseWithoutChangingBytes()
    {
        string requestOne = ContractJson.Serialize(CreateRequest("receipt-1"));
        string requestTwo = ContractJson.Serialize(CreateRequest("receipt-2"));
        string pendingOne = PendingEntry("generation-1", 1, 0, requestOne);
        string pendingTwo = PendingEntry("generation-2", 2, 0, requestTwo);
        string duplicateReceipt = PendingEntry("generation-other", 2, 0, requestOne);
        string duplicateSequence = PendingEntry("generation-2", 1, 0, requestTwo);
        string deadOne = DeadEntry("generation-1", 2, 0, "PERMANENT", requestOne);
        string missingRequestField = requestOne.Replace(
            ",\"source_hash\":\"sha256:source\"",
            string.Empty,
            StringComparison.Ordinal);
        string requestWithUnknownField = requestOne[..^1] + ",\"unexpected\":true}";
        string requestWrongPartition = requestOne.Replace(
            "\"player_id\":\"player-a\"",
            "\"player_id\":\"other-player\"",
            StringComparison.Ordinal);
        string requestWithDuplicateKey = requestOne[..^1]
            + ",\"source_hash\":\"sha256:source\"}";
        List<string> corruptedSnapshots = new()
        {
            "{\"format_version\":1",
            BuildSnapshotJson(formatVersion: 2, nextSequence: 2, pendingJson: pendingOne),
            BuildSnapshotJson(saveId: "wrong-save", nextSequence: 2, pendingJson: pendingOne),
            BuildSnapshotJson(nextSequence: 2, pendingJson: pendingOne, extraTopLevel: ",\"unexpected\":true"),
            BuildSnapshotJson(nextSequenceToken: "1e0", pendingJson: pendingOne),
            BuildSnapshotJson(nextSequence: 1, pendingJson: pendingOne),
            BuildSnapshotJson(nextSequence: 2, pendingJson: PendingEntry("generation-1", 0, 0, requestOne)),
            BuildSnapshotJson(nextSequence: 2, pendingJson: PendingEntry("generation-1", 1, -1, requestOne)),
            BuildSnapshotJson(nextSequence: 3, pendingJson: $"{pendingOne},{duplicateReceipt}"),
            BuildSnapshotJson(nextSequence: 3, pendingJson: $"{pendingOne},{duplicateSequence}"),
            BuildSnapshotJson(nextSequence: 3, pendingJson: pendingOne, deadJson: deadOne),
            BuildSnapshotJson(nextSequence: 2, pendingJson: "{\"generation_id\":\"generation-1\",\"sequence\":1,\"attempt_count\":0,\"request\":\"escaped\"}"),
            BuildSnapshotJson(nextSequence: 2, pendingJson: PendingEntry("generation-1", 1, 0, missingRequestField)),
            BuildSnapshotJson(nextSequence: 2, pendingJson: PendingEntry("generation-1", 1, 0, requestWithUnknownField)),
            BuildSnapshotJson(nextSequence: 2, pendingJson: PendingEntry("generation-1", 1, 0, requestWrongPartition)),
            BuildSnapshotJson(nextSequence: 2, pendingJson: PendingEntry(string.Empty, 1, 0, requestOne)),
            BuildSnapshotJson(nextSequence: 3, pendingJson: pendingOne, deadJson: DeadEntry("generation-2", 2, 0, string.Empty, requestTwo)),
            BuildSnapshotJson(nextSequence: 2, pendingJson: pendingOne[..^1] + ",\"unexpected\":true}"),
            BuildSnapshotJson(nextSequence: 2, pendingJson: "null"),
            BuildSnapshotJson().Replace("\"pending\":[]", "\"pending\":null", StringComparison.Ordinal),
            BuildSnapshotJson(nextSequence: 2, pendingJson: PendingEntry("generation-1", 1, 0, requestWithDuplicateKey)),
            "{\"format_version\":1,\"format_version\":1,\"save_id\":\"save-a\",\"player_id\":\"player-a\",\"next_sequence\":1,\"pending\":[],\"dead_letters\":[]}",
        };

        for (int index = 0; index < corruptedSnapshots.Count; index++)
        {
            string caseDirectory = Path.Combine(testDirectory, $"corrupt-{index:D2}");
            Directory.CreateDirectory(caseDirectory);
            string filePath = Path.Combine(caseDirectory, "display-acks.json");
            byte[] originalBytes = Encoding.UTF8.GetBytes(corruptedSnapshots[index]);
            File.WriteAllBytes(filePath, originalBytes);

            Assert.Throws<OutboxCorruptedException>(
                () => DurableDisplayAckOutbox.Open(filePath, SaveId, PlayerId));
            Assert.Equal(originalBytes, File.ReadAllBytes(filePath));
        }
    }

    /// <summary>
    /// writer 失败时新 attempt 不能提前进入内存；连续失败都应从旧 attempt=0 提议 1。
    /// </summary>
    [Fact]
    public void RecordTransientFailure_WhenSnapshotWriteFailsPreservesFileAndInMemoryState()
    {
        string filePath = CreateFilePath("write-failure.json");
        DurableDisplayAckOutbox initial = DurableDisplayAckOutbox.Open(filePath, SaveId, PlayerId);
        initial.Enqueue("generation-write-failure", CreateRequest("receipt-write-failure"));
        byte[] originalBytes = File.ReadAllBytes(filePath);
        List<int> proposedAttemptCounts = new();
        OutboxPersistenceException injectedFailure = new(
            "injected ACK snapshot write failure",
            new IOException("test-only move failure"));
        DurableDisplayAckOutbox failing = DurableDisplayAckOutbox.Open(
            filePath,
            SaveId,
            PlayerId,
            snapshotWriter: snapshot =>
            {
                proposedAttemptCounts.Add(snapshot.Pending.Single().AttemptCount);
                throw injectedFailure;
            });
        PendingDisplayAck attempt = Assert.IsType<PendingDisplayAck>(failing.CreateNextAttempt());

        Assert.Same(
            injectedFailure,
            Assert.Throws<OutboxPersistenceException>(
                () => failing.RecordTransientFailure(attempt, "NETWORK_TIMEOUT")));
        Assert.Same(
            injectedFailure,
            Assert.Throws<OutboxPersistenceException>(
                () => failing.RecordTransientFailure(attempt, "NETWORK_TIMEOUT")));

        Assert.Equal(new[] { 1, 1 }, proposedAttemptCounts);
        Assert.Equal(originalBytes, File.ReadAllBytes(filePath));
        Assert.Equal(0, ReadPendingAttemptCount(filePath));
    }

    /// <summary>
    /// 清理仅限当前测试生成的唯一临时目录。
    /// </summary>
    public void Dispose()
    {
        if (Directory.Exists(testDirectory))
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    /// <summary>
    /// 构造字段完整、分区正确且可单独变异的展示 ACK 请求。
    /// </summary>
    private static DisplayAckRequest CreateRequest(
        string receiptId,
        string? requestId = null,
        int displayedDayIndex = 14,
        string npcId = "Abigail",
        string sourceHash = "sha256:source")
    {
        return new DisplayAckRequest
        {
            SchemaVersion = ContractVersions.V1,
            RequestId = requestId ?? $"request-{receiptId}",
            SaveId = SaveId,
            PlayerId = PlayerId,
            DisplayReceiptId = receiptId,
            DisplayedDayIndex = displayedDayIndex,
            NpcId = npcId,
            SourceHash = sourceHash,
        };
    }

    /// <summary>
    /// 经正式合同 serializer 深拷贝请求，避免测试冲突变体共享 DTO 引用。
    /// </summary>
    private static DisplayAckRequest CloneRequest(DisplayAckRequest request)
    {
        return ContractJson.Deserialize<DisplayAckRequest>(ContractJson.Serialize(request));
    }

    /// <summary>
    /// 构造与 attempt 精确对应的后端响应。
    /// </summary>
    private static DisplayAckResponse CreateResponse(
        PendingDisplayAck attempt,
        DisplayAckStatus status)
    {
        return new DisplayAckResponse
        {
            SchemaVersion = ContractVersions.V1,
            RequestId = attempt.Request.RequestId,
            DisplayReceiptId = attempt.Request.DisplayReceiptId,
            Status = status,
        };
    }

    /// <summary>
    /// 创建当前测试目录内的目标路径，不预先创建文件。
    /// </summary>
    private string CreateFilePath(string fileName)
    {
        return Path.Combine(testDirectory, fileName);
    }

    /// <summary>
    /// 读取当前 FIFO 首项 attempt_count，直接核对落盘值。
    /// </summary>
    private static int ReadPendingAttemptCount(string filePath)
    {
        using JsonDocument snapshot = JsonDocument.Parse(File.ReadAllText(filePath));
        return snapshot.RootElement
            .GetProperty("pending")[0]
            .GetProperty("attempt_count")
            .GetInt32();
    }

    /// <summary>
    /// 计算文件 SHA-256，证明异常路径没有替换任何字节。
    /// </summary>
    private static string ComputeSha256(string filePath)
    {
        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(filePath)));
    }

    /// <summary>
    /// 构造 pending entry JSON；requestJson 必须直接嵌入 object token。
    /// </summary>
    private static string PendingEntry(
        string generationId,
        long sequence,
        int attemptCount,
        string requestJson)
    {
        return "{"
            + $"\"generation_id\":{JsonSerializer.Serialize(generationId)},"
            + $"\"sequence\":{sequence},"
            + $"\"attempt_count\":{attemptCount},"
            + $"\"request\":{requestJson}"
            + "}";
    }

    /// <summary>
    /// 构造 dead-letter entry JSON，供腐化矩阵只改变目标不变量。
    /// </summary>
    private static string DeadEntry(
        string generationId,
        long sequence,
        int attemptCount,
        string reasonCode,
        string requestJson)
    {
        return "{"
            + $"\"generation_id\":{JsonSerializer.Serialize(generationId)},"
            + $"\"sequence\":{sequence},"
            + $"\"attempt_count\":{attemptCount},"
            + $"\"reason_code\":{JsonSerializer.Serialize(reasonCode)},"
            + $"\"request\":{requestJson}"
            + "}";
    }

    /// <summary>
    /// 构造完整 snapshot JSON，仅用于严格恢复格式的反例矩阵。
    /// </summary>
    private static string BuildSnapshotJson(
        int formatVersion = 1,
        string saveId = SaveId,
        string playerId = PlayerId,
        long nextSequence = 1,
        string? nextSequenceToken = null,
        string pendingJson = "",
        string deadJson = "",
        string extraTopLevel = "")
    {
        string sequenceToken = nextSequenceToken
            ?? nextSequence.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return "{"
            + $"\"format_version\":{formatVersion},"
            + $"\"save_id\":{JsonSerializer.Serialize(saveId)},"
            + $"\"player_id\":{JsonSerializer.Serialize(playerId)},"
            + $"\"next_sequence\":{sequenceToken},"
            + $"\"pending\":[{pendingJson}],"
            + $"\"dead_letters\":[{deadJson}]"
            + extraTopLevel
            + "}";
    }
}
