using StardewNpcAgent.Application;
using StardewNpcAgent.Contracts;
using StardewNpcAgent.Infrastructure.Http;
using StardewNpcAgent.Infrastructure.Storage;

namespace StardewNpcAgent.Tests.Application;

/// <summary>
/// 验证 displayed ACK 的原子入队结果和 single-flight durable flush。
/// </summary>
public sealed class DisplayAckOutboxCoordinatorTests : IDisposable
{
    private const string SaveId = "save-ack-sync";
    private const string PlayerId = "player-ack-sync";
    private readonly string testDirectory = Path.Combine(
        Path.GetTempPath(),
        "StardewNpcAgent.Tests",
        $"DisplayAckOutboxCoordinator.{Guid.NewGuid():N}");

    public DisplayAckOutboxCoordinatorTests()
    {
        Directory.CreateDirectory(testDirectory);
    }

    /// <summary>
    /// Enqueue 必须在同一 outbox 锁内直接返回 accepted/duplicate，不能依赖外部前后计数。
    /// </summary>
    [Fact]
    public void Enqueue_ReturnsAtomicAcceptedThenDuplicate()
    {
        DurableDisplayAckOutbox outbox = OpenOutbox("enqueue.json");
        DisplayAckRequest request = CreateRequest("receipt-atomic");

        DisplayAckStatus first = outbox.Enqueue("generation-atomic", request);
        DisplayAckStatus second = outbox.Enqueue("generation-atomic", request);

        Assert.Equal(DisplayAckStatus.Accepted, first);
        Assert.Equal(DisplayAckStatus.Duplicate, second);
        Assert.Equal(1, outbox.PendingCount);
    }

    /// <summary>
    /// accepted/duplicate 响应都删除当前 FIFO 首项；重复调用 Flush 不产生第二个网络请求。
    /// </summary>
    [Theory]
    [InlineData(DisplayAckStatus.Accepted)]
    [InlineData(DisplayAckStatus.Duplicate)]
    public async Task FlushAsync_SuccessRemovesPending(DisplayAckStatus responseStatus)
    {
        DurableDisplayAckOutbox outbox = OpenOutbox($"success-{responseStatus}.json");
        outbox.Enqueue("generation-success", CreateRequest("receipt-success"));
        RecordingDisplayAckGateway gateway = new((generationId, request) => new DisplayAckResponse
        {
            SchemaVersion = request.SchemaVersion,
            RequestId = request.RequestId,
            DisplayReceiptId = request.DisplayReceiptId,
            Status = responseStatus,
        });
        DisplayAckOutboxCoordinator coordinator = new(outbox, gateway);

        await coordinator.FlushAsync(CancellationToken.None);
        await coordinator.FlushAsync(CancellationToken.None);

        Assert.Equal(0, outbox.PendingCount);
        Assert.Equal(1, gateway.CallCount);
        Assert.Equal("generation-success", gateway.GenerationIds.Single());
    }

    /// <summary>
    /// timeout/503/坏 2xx 保留 pending 并持久化 attempt，供下一生命周期重试。
    /// </summary>
    [Fact]
    public async Task FlushAsync_TransientFailurePreservesPending()
    {
        DurableDisplayAckOutbox outbox = OpenOutbox("transient.json");
        outbox.Enqueue("generation-transient", CreateRequest("receipt-transient"));
        RecordingDisplayAckGateway gateway = new(
            (_generationId, _request) => throw new AgentApiException(
                AgentApiFailureKind.Transient,
                "HTTP_STATUS_503",
                httpStatusCode: 503));
        DisplayAckOutboxCoordinator coordinator = new(outbox, gateway);

        DisplayAckSyncException error = await Assert.ThrowsAsync<DisplayAckSyncException>(
            () => coordinator.FlushAsync(CancellationToken.None));

        Assert.Equal("DISPLAY_ACK_SYNC_FAILED", error.ReasonCode);
        Assert.Equal(1, outbox.PendingCount);
        Assert.Equal(0, outbox.DeadLetterCount);
        Assert.Equal(1, outbox.CreateNextAttempt()!.AttemptCount);
    }

    /// <summary>
    /// ACK 404/409/422 无法通过重试修复，应隔离该 receipt 后继续处理后续项。
    /// </summary>
    [Theory]
    [InlineData(404)]
    [InlineData(409)]
    [InlineData(422)]
    public async Task FlushAsync_PermanentAckFailureMovesItemToDeadLetter(int statusCode)
    {
        DurableDisplayAckOutbox outbox = OpenOutbox($"permanent-{statusCode}.json");
        outbox.Enqueue("generation-permanent", CreateRequest($"receipt-{statusCode}"));
        RecordingDisplayAckGateway gateway = new(
            (_generationId, _request) => throw new AgentApiException(
                AgentApiFailureKind.Permanent,
                $"HTTP_STATUS_{statusCode}",
                statusCode));
        DisplayAckOutboxCoordinator coordinator = new(outbox, gateway);

        await coordinator.FlushAsync(CancellationToken.None);

        Assert.Equal(0, outbox.PendingCount);
        Assert.Equal(1, outbox.DeadLetterCount);
        Assert.Equal(
            $"DISPLAY_ACK_HTTP_{statusCode}",
            outbox.SnapshotDeadLetters().Single().ReasonCode);
    }

    public void Dispose()
    {
        if (Directory.Exists(testDirectory))
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    private DurableDisplayAckOutbox OpenOutbox(string fileName)
    {
        return DurableDisplayAckOutbox.Open(
            Path.Combine(testDirectory, fileName),
            SaveId,
            PlayerId);
    }

    private static DisplayAckRequest CreateRequest(string receiptId)
    {
        return new DisplayAckRequest
        {
            SchemaVersion = ContractVersions.V1,
            RequestId = $"request-{receiptId}",
            SaveId = SaveId,
            PlayerId = PlayerId,
            DisplayReceiptId = receiptId,
            DisplayedDayIndex = 14,
            NpcId = "Abigail",
            SourceHash = "sha256:display-ack-sync",
        };
    }

    private sealed class RecordingDisplayAckGateway : IDisplayAckGateway
    {
        private readonly Func<string, DisplayAckRequest, DisplayAckResponse> handler;

        public RecordingDisplayAckGateway(
            Func<string, DisplayAckRequest, DisplayAckResponse> handler)
        {
            this.handler = handler;
        }

        public int CallCount { get; private set; }

        public List<string> GenerationIds { get; } = new();

        public Task<DisplayAckResponse> AcknowledgeDisplayAsync(
            string generationId,
            DisplayAckRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            GenerationIds.Add(generationId);
            return Task.FromResult(handler(generationId, request));
        }
    }
}
