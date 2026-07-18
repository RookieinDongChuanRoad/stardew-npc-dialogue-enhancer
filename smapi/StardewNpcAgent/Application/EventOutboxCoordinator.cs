using StardewNpcAgent.Contracts;
using StardewNpcAgent.Infrastructure.Http;
using StardewNpcAgent.Infrastructure.Storage;

namespace StardewNpcAgent.Application;

/// <summary>
/// 事件批次 HTTP 端口；生产由 AgentApiClient 实现，测试可注入零网络 fake。
/// </summary>
public interface IGameEventGateway
{
    Task<GameEventBatchResponse> IngestBatchAsync(
        GameEventBatchRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// 本轮事件刷新未能证明截至 cutoff 的事实已提交，DayStarted 必须整体 fallback。
/// </summary>
public sealed class EventOutboxSyncException : Exception
{
    public EventOutboxSyncException()
        : base("event outbox synchronization failed")
    {
    }

    public string ReasonCode => "EVENT_OUTBOX_SYNC_FAILED";
}

/// <summary>
/// 把 cutoff-aware durable event outbox 映射到本地 FastAPI 批次端点。
/// </summary>
/// <remarks>
/// 同一实例所有 flush single-flight，避免 DayEnding 与 DayStarted 同时应用两个旧快照。
/// 任一瞬态/未知失败都会先保留并增加 attempt，再抛稳定同步异常，禁止 DayStarted 带旧
/// memory revision 继续生成。只有明确 422 producer 拒绝会 dead letter 后继续。
/// </remarks>
public sealed class EventOutboxCoordinator : IEventOutboxSynchronizer
{
    private readonly DurableEventOutbox outbox;
    private readonly IGameEventGateway gateway;
    private readonly Func<string> requestIdFactory;
    private readonly SemaphoreSlim flushGate = new(1, 1);

    public EventOutboxCoordinator(
        DurableEventOutbox outbox,
        IGameEventGateway gateway,
        Func<string>? requestIdFactory = null)
    {
        this.outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        this.gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        this.requestIdFactory = requestIdFactory
            ?? (() => $"request-event-outbox-v1-{Guid.NewGuid():N}");
    }

    public async Task<EventOutboxWatermark> FlushThroughDayAsync(
        int throughDayIndex,
        CancellationToken cancellationToken)
    {
        if (throughDayIndex < -1)
        {
            throw new ArgumentOutOfRangeException(nameof(throughDayIndex));
        }

        await flushGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PendingEventBatch? batch = outbox.CreatePendingBatchThroughDay(
                    requestIdFactory(),
                    throughDayIndex);
                if (batch is null)
                {
                    return CurrentWatermark();
                }

                try
                {
                    GameEventBatchResponse response = await gateway.IngestBatchAsync(
                        batch.Request,
                        cancellationToken).ConfigureAwait(false);
                    // ApplyResponse 会再次核对 request、顺序、identity、status 和单调水位；
                    // 只有原子 snapshot 写成功后，下一轮/最终返回才可观察到新 watermark。
                    outbox.ApplyResponse(batch, response);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (AgentApiException error)
                {
                    if (error.Kind == AgentApiFailureKind.Permanent
                        && error.HttpStatusCode == 422)
                    {
                        outbox.RecordPermanentFailure(batch, "EVENT_CONTRACT_REJECTED");
                        continue;
                    }

                    outbox.RecordTransientFailure(batch, StableTransientReason(error));
                    throw new EventOutboxSyncException();
                }
                catch (Exception)
                {
                    // 不记录自由异常正文；事件仍在 outbox，下一次生命周期可安全重试。
                    outbox.RecordTransientFailure(batch, "EVENT_SYNC_UNEXPECTED_FAILURE");
                    throw new EventOutboxSyncException();
                }
            }
        }
        finally
        {
            flushGate.Release();
        }
    }

    private EventOutboxWatermark CurrentWatermark()
    {
        return new EventOutboxWatermark(
            outbox.MemoryRevision,
            outbox.CommittedThroughDayIndex);
    }

    private static string StableTransientReason(AgentApiException error)
    {
        return error.Kind == AgentApiFailureKind.InvalidResponse
            ? "EVENT_RESPONSE_INVALID"
            : "EVENT_API_TRANSIENT_FAILURE";
    }
}
