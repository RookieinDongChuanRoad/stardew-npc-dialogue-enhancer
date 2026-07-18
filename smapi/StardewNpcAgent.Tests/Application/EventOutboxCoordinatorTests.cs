using System.Text.Json;
using StardewNpcAgent.Application;
using StardewNpcAgent.Contracts;
using StardewNpcAgent.Infrastructure.Http;
using StardewNpcAgent.Infrastructure.Storage;

namespace StardewNpcAgent.Tests.Application;

/// <summary>
/// 验证 DayStarted/DayEnding 共用的 event outbox 同步器只提交截止日内事实并正确落盘水位。
/// </summary>
public sealed class EventOutboxCoordinatorTests : IDisposable
{
    private const string SaveId = "save-event-sync";
    private const string PlayerId = "player-event-sync";
    private readonly string testDirectory = Path.Combine(
        Path.GetTempPath(),
        "StardewNpcAgent.Tests",
        $"EventOutboxCoordinator.{Guid.NewGuid():N}");

    public EventOutboxCoordinatorTests()
    {
        Directory.CreateDirectory(testDirectory);
    }

    /// <summary>
    /// cutoff-aware batch 只能选择 FIFO 中截至昨日的前缀；当前日事件必须继续留在 durable 文件。
    /// </summary>
    [Fact]
    public void CreatePendingBatchThroughDay_ExcludesLaterDayAndPermanentFailureDeadLettersSelectedItems()
    {
        DurableEventOutbox outbox = OpenOutbox("cutoff.json");
        outbox.Enqueue(CreateProgressionEvent("event-day-9", 9));
        outbox.Enqueue(CreateProgressionEvent("event-day-10", 10));

        PendingEventBatch batch = Assert.IsType<PendingEventBatch>(
            outbox.CreatePendingBatchThroughDay("request-cutoff", throughDayIndex: 9));

        Assert.Single(batch.Request.Events);
        Assert.Equal("event-day-9", batch.Request.Events[0].EventId);
        outbox.RecordPermanentFailure(batch, "EVENT_CONTRACT_REJECTED");
        Assert.Equal(1, outbox.PendingCount);
        Assert.Equal(1, outbox.DeadLetterCount);
        Assert.Null(outbox.CreatePendingBatchThroughDay("request-none", throughDayIndex: 9));
        Assert.Equal(
            "event-day-10",
            outbox.CreatePendingBatchThroughDay("request-day-10", throughDayIndex: 10)!
                .Request.Events[0].EventId);
    }

    /// <summary>
    /// 只有 response 已经应用到 outbox snapshot 后同步器才返回新 watermark；当前日事实不上传。
    /// </summary>
    [Fact]
    public async Task FlushThroughDayAsync_AppliesResponseThenReturnsPersistedWatermark()
    {
        DurableEventOutbox outbox = OpenOutbox("success.json");
        outbox.Enqueue(CreateProgressionEvent("event-yesterday", 9));
        outbox.Enqueue(CreateProgressionEvent("event-today", 10));
        RecordingEventGateway gateway = new(request => CreateAcceptedResponse(request, 1, 9));
        EventOutboxCoordinator coordinator = new(
            outbox,
            gateway,
            requestIdFactory: () => "request-event-sync-success");

        EventOutboxWatermark watermark = await coordinator.FlushThroughDayAsync(
            9,
            CancellationToken.None);

        Assert.Equal(new EventOutboxWatermark(1, 9), watermark);
        Assert.Equal(1, outbox.PendingCount);
        Assert.Equal(new[] { "event-yesterday" }, gateway.UploadedEventIds);
        DurableEventOutbox reopened = OpenOutbox("success.json");
        Assert.Equal(1, reopened.MemoryRevision);
        Assert.Equal(9, reopened.CommittedThroughDayIndex);
    }

    /// <summary>
    /// transient/坏 2xx 必须保留事件并令 DayStarted fallback，不能带旧 revision 继续生成。
    /// </summary>
    [Theory]
    [InlineData(AgentApiFailureKind.Transient, "HTTP_STATUS_503")]
    [InlineData(AgentApiFailureKind.InvalidResponse, "HTTP_RESPONSE_CONTRACT_INVALID")]
    public async Task FlushThroughDayAsync_TransientOrInvalidResponsePreservesPendingAndThrows(
        AgentApiFailureKind kind,
        string reasonCode)
    {
        DurableEventOutbox outbox = OpenOutbox($"failure-{kind}.json");
        outbox.Enqueue(CreateProgressionEvent("event-yesterday", 9));
        RecordingEventGateway gateway = new(
            _ => throw new AgentApiException(kind, reasonCode, httpStatusCode: 503));
        EventOutboxCoordinator coordinator = new(outbox, gateway, () => "request-failure");

        EventOutboxSyncException error = await Assert.ThrowsAsync<EventOutboxSyncException>(
            () => coordinator.FlushThroughDayAsync(9, CancellationToken.None));

        Assert.Equal("EVENT_OUTBOX_SYNC_FAILED", error.ReasonCode);
        Assert.Equal(1, outbox.PendingCount);
        Assert.Equal(0, outbox.DeadLetterCount);
    }

    /// <summary>
    /// event 422 证明 producer payload 永久无效；同步器应隔离后继续返回，而不是无限重试。
    /// </summary>
    [Fact]
    public async Task FlushThroughDayAsync_UnprocessableBatchMovesItToDeadLetter()
    {
        DurableEventOutbox outbox = OpenOutbox("permanent.json");
        outbox.Enqueue(CreateProgressionEvent("event-invalid", 9));
        RecordingEventGateway gateway = new(
            _ => throw new AgentApiException(
                AgentApiFailureKind.Permanent,
                "HTTP_STATUS_422",
                httpStatusCode: 422));
        EventOutboxCoordinator coordinator = new(outbox, gateway, () => "request-permanent");

        EventOutboxWatermark watermark = await coordinator.FlushThroughDayAsync(
            9,
            CancellationToken.None);

        Assert.Equal(new EventOutboxWatermark(0, -1), watermark);
        Assert.Equal(0, outbox.PendingCount);
        Assert.Equal(1, outbox.DeadLetterCount);
        Assert.Equal(
            "EVENT_CONTRACT_REJECTED",
            outbox.SnapshotDeadLetters().Single().ReasonCode);
    }

    public void Dispose()
    {
        if (Directory.Exists(testDirectory))
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    private DurableEventOutbox OpenOutbox(string fileName)
    {
        return DurableEventOutbox.Open(Path.Combine(testDirectory, fileName), SaveId, PlayerId);
    }

    private static GameEvent CreateProgressionEvent(string eventId, int occurredDayIndex)
    {
        return new GameEvent
        {
            EventId = eventId,
            EventType = "world_progression",
            EventVersion = "1",
            OccurredDayIndex = occurredDayIndex,
            Source = "smapi.player.level_changed",
            AudienceScope = AudienceScope.Public,
            AudienceNpcId = null,
            Payload = JsonSerializer.SerializeToElement(
                new Dictionary<string, string> { ["milestone"] = $"level-{occurredDayIndex}" }),
        };
    }

    private static GameEventBatchResponse CreateAcceptedResponse(
        GameEventBatchRequest request,
        int memoryRevision,
        int committedDay)
    {
        return new GameEventBatchResponse
        {
            SchemaVersion = request.SchemaVersion,
            RequestId = request.RequestId,
            MemoryRevision = memoryRevision,
            CommittedThroughDayIndex = committedDay,
            Items = request.Events.Select(item => new GameEventItemResult
            {
                EventId = item.EventId,
                Status = EventIngestionStatus.Accepted,
                ReasonCode = null,
            }).ToList(),
        };
    }

    private sealed class RecordingEventGateway : IGameEventGateway
    {
        private readonly Func<GameEventBatchRequest, GameEventBatchResponse> handler;

        public RecordingEventGateway(Func<GameEventBatchRequest, GameEventBatchResponse> handler)
        {
            this.handler = handler;
        }

        public List<string> UploadedEventIds { get; } = new();

        public Task<GameEventBatchResponse> IngestBatchAsync(
            GameEventBatchRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UploadedEventIds.AddRange(request.Events.Select(item => item.EventId));
            return Task.FromResult(handler(request));
        }
    }
}
