using StardewNpcAgent.Contracts;
using StardewNpcAgent.Infrastructure.Http;
using StardewNpcAgent.Infrastructure.Storage;

namespace StardewNpcAgent.Application;

/// <summary>
/// displayed ACK HTTP 端口；生产由 AgentApiClient 实现。
/// </summary>
public interface IDisplayAckGateway
{
    Task<DisplayAckResponse> AcknowledgeDisplayAsync(
        string generationId,
        DisplayAckRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// 本轮 ACK 刷新遇到瞬态或无效响应；UI 已显示的文本不受影响，pending 保留。
/// </summary>
public sealed class DisplayAckSyncException : Exception
{
    public DisplayAckSyncException()
        : base("display ack synchronization failed")
    {
    }

    public string ReasonCode => "DISPLAY_ACK_SYNC_FAILED";
}

/// <summary>
/// 单线程刷新 displayed ACK FIFO；成功删除、永久 4xx 隔离、瞬态失败保留。
/// </summary>
public sealed class DisplayAckOutboxCoordinator
{
    private readonly DurableDisplayAckOutbox outbox;
    private readonly IDisplayAckGateway gateway;
    private readonly SemaphoreSlim flushGate = new(1, 1);

    public DisplayAckOutboxCoordinator(
        DurableDisplayAckOutbox outbox,
        IDisplayAckGateway gateway)
    {
        this.outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        this.gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
    }

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        await flushGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PendingDisplayAck? attempt = outbox.CreateNextAttempt();
                if (attempt is null)
                {
                    return;
                }

                try
                {
                    DisplayAckResponse response = await gateway.AcknowledgeDisplayAsync(
                        attempt.GenerationId,
                        attempt.Request,
                        cancellationToken).ConfigureAwait(false);
                    outbox.MarkDelivered(attempt, response);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (AgentApiException error)
                {
                    if (error.Kind == AgentApiFailureKind.Permanent)
                    {
                        string status = error.HttpStatusCode?.ToString(
                            System.Globalization.CultureInfo.InvariantCulture) ?? "UNKNOWN";
                        outbox.RecordPermanentFailure(attempt, $"DISPLAY_ACK_HTTP_{status}");
                        continue;
                    }

                    outbox.RecordTransientFailure(
                        attempt,
                        error.Kind == AgentApiFailureKind.InvalidResponse
                            ? "DISPLAY_ACK_RESPONSE_INVALID"
                            : "DISPLAY_ACK_API_TRANSIENT_FAILURE");
                    throw new DisplayAckSyncException();
                }
                catch (Exception)
                {
                    outbox.RecordTransientFailure(attempt, "DISPLAY_ACK_SYNC_UNEXPECTED_FAILURE");
                    throw new DisplayAckSyncException();
                }
            }
        }
        finally
        {
            flushGate.Release();
        }
    }
}
