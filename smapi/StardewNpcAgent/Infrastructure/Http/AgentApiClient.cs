using System.Net;
using System.Text;
using System.Text.Json;
using StardewNpcAgent.Application;
using StardewNpcAgent.Contracts;

namespace StardewNpcAgent.Infrastructure.Http;

/// <summary>
/// 本地 FastAPI 的严格、有限 HTTP adapter。
/// </summary>
/// <remarks>
/// 本类不重试整段工作流、不读取游戏状态、不记录 response body，也不拥有传入的
/// <see cref="HttpClient"/>。event/ACK 的长期重试由 durable outbox coordinator 完成。
/// </remarks>
public sealed class AgentApiClient :
    IGameEventGateway,
    IDialogueGenerationGateway,
    IDisplayAckGateway
{
    private readonly HttpClient httpClient;
    private readonly Uri backendBaseUri;
    private readonly AgentApiTimeouts timeouts;

    /// <summary>
    /// 创建绑定单一 loopback 后端的 client；调用方负责 HttpClient 生命周期。
    /// </summary>
    public AgentApiClient(
        HttpClient httpClient,
        Uri backendBaseUri,
        AgentApiTimeouts timeouts)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.backendBaseUri = backendBaseUri ?? throw new ArgumentNullException(nameof(backendBaseUri));
        this.timeouts = timeouts ?? throw new ArgumentNullException(nameof(timeouts));
        if (!backendBaseUri.IsAbsoluteUri
            || !backendBaseUri.IsLoopback
            || (backendBaseUri.Scheme != Uri.UriSchemeHttp
                && backendBaseUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("AgentApiClient 只接受 loopback HTTP(S) 基地址。", nameof(backendBaseUri));
        }
    }

    public Task<GameEventBatchResponse> IngestBatchAsync(
        GameEventBatchRequest request,
        CancellationToken cancellationToken)
    {
        return SendAsync<GameEventBatchRequest, GameEventBatchResponse>(
            "api/v1/game-events/batches",
            request,
            timeouts.EventRequest,
            cancellationToken);
    }

    public Task<DialogueGenerationBatchResponse> GenerateBatchAsync(
        DialogueGenerationBatchRequest request,
        CancellationToken cancellationToken)
    {
        return SendAsync<DialogueGenerationBatchRequest, DialogueGenerationBatchResponse>(
            "api/v1/dialogue-generations/batch",
            request,
            timeouts.GenerationRequest,
            cancellationToken);
    }

    public Task<DisplayAckResponse> AcknowledgeDisplayAsync(
        string generationId,
        DisplayAckRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(generationId) || generationId != generationId.Trim())
        {
            throw new ArgumentException("generationId 必须非空且无首尾空白。", nameof(generationId));
        }

        string escapedGenerationId = Uri.EscapeDataString(generationId);
        return SendAsync<DisplayAckRequest, DisplayAckResponse>(
            $"api/v1/dialogue-generations/{escapedGenerationId}/displayed",
            request,
            timeouts.DisplayAckRequest,
            cancellationToken);
    }

    /// <summary>
    /// 序列化请求、应用独立 timeout、分类 status，并执行严格 JSON + runtime contract 校验。
    /// </summary>
    private async Task<TResponse> SendAsync<TRequest, TResponse>(
        string relativePath,
        TRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
        where TRequest : ContractDto
        where TResponse : ContractDto
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutSource.CancelAfter(timeout);
        using HttpRequestMessage message = new(
            HttpMethod.Post,
            new Uri(backendBaseUri, relativePath))
        {
            Content = new StringContent(
                ContractJson.Serialize(request),
                Encoding.UTF8,
                "application/json"),
        };

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AgentApiException(
                AgentApiFailureKind.Transient,
                "HTTP_REQUEST_TIMEOUT");
        }
        catch (HttpRequestException)
        {
            throw new AgentApiException(
                AgentApiFailureKind.Transient,
                "HTTP_NETWORK_FAILURE");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw CreateStatusFailure(response.StatusCode);
            }

            string responseJson;
            try
            {
                responseJson = await response.Content.ReadAsStringAsync(timeoutSource.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new AgentApiException(
                    AgentApiFailureKind.Transient,
                    "HTTP_RESPONSE_TIMEOUT");
            }

            try
            {
                TResponse parsed = ContractJson.Deserialize<TResponse>(responseJson);
                ContractValidationResult validation = ValidateResponse(parsed);
                if (!validation.IsValid)
                {
                    throw new AgentApiException(
                        AgentApiFailureKind.InvalidResponse,
                        "HTTP_RESPONSE_CONTRACT_INVALID");
                }

                return parsed;
            }
            catch (AgentApiException)
            {
                throw;
            }
            catch (JsonException)
            {
                throw new AgentApiException(
                    AgentApiFailureKind.InvalidResponse,
                    "HTTP_RESPONSE_CONTRACT_INVALID");
            }
        }
    }

    /// <summary>
    /// 408/429/5xx 属于可重试；其余 4xx 是永久业务/合同拒绝。
    /// </summary>
    private static AgentApiException CreateStatusFailure(HttpStatusCode statusCode)
    {
        int numericStatus = (int)statusCode;
        AgentApiFailureKind kind = statusCode == HttpStatusCode.RequestTimeout
            || numericStatus == 429
            || numericStatus >= 500
                ? AgentApiFailureKind.Transient
                : AgentApiFailureKind.Permanent;
        return new AgentApiException(kind, $"HTTP_STATUS_{numericStatus}", numericStatus);
    }

    /// <summary>
    /// 泛型反序列化后仍按具体 DTO 执行现有 runtime contract，禁止只验证 JSON shape。
    /// </summary>
    private static ContractValidationResult ValidateResponse(ContractDto response)
    {
        return response switch
        {
            GameEventBatchResponse value => ContractValidator.Validate(value),
            DialogueGenerationBatchResponse value => ContractValidator.Validate(value),
            DisplayAckResponse value => ContractValidator.Validate(value),
            _ => throw new InvalidOperationException("AgentApiClient 收到未注册的 response DTO。"),
        };
    }
}
