using System.Net;
using System.Text;
using StardewNpcAgent.Contracts;
using StardewNpcAgent.Infrastructure.Http;
using StardewNpcAgent.Tests.Contracts;

namespace StardewNpcAgent.Tests.Infrastructure.Http;

/// <summary>
/// 使用纯内存 HttpMessageHandler 验证 API client 的 endpoint、timeout、状态码和严格 JSON 边界。
/// </summary>
public sealed class AgentApiClientTests
{
    /// <summary>
    /// 2xx 只有在 JSON 与 C# runtime contract 都合法时才可返回给协调器。
    /// </summary>
    [Fact]
    public async Task GenerateBatchAsync_ValidResponseUsesExpectedEndpointAndStrictContract()
    {
        DialogueGenerationBatchRequest request = CreateGenerationRequest();
        HttpRequestMessage? capturedRequest = null;
        ScriptedHandler handler = new(async (message, cancellationToken) =>
        {
            capturedRequest = message;
            string requestJson = await message.Content!.ReadAsStringAsync(cancellationToken);
            Assert.Equal(ContractJson.Serialize(request), requestJson);
            return JsonResponse(HttpStatusCode.OK, CreateGenerationResponse(request));
        });
        AgentApiClient client = CreateClient(handler);

        DialogueGenerationBatchResponse response = await client.GenerateBatchAsync(
            request,
            CancellationToken.None);

        Assert.Equal(request.RequestId, response.RequestId);
        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest!.Method);
        Assert.Equal("/api/v1/dialogue-generations/batch", capturedRequest.RequestUri!.AbsolutePath);
    }

    /// <summary>
    /// 成功状态中的未知字段或 malformed JSON 不能绕过共享反序列化边界。
    /// </summary>
    [Theory]
    [InlineData("{not-json")]
    [InlineData("{\"schema_version\":\"1.0\",\"unexpected\":true}")]
    public async Task GenerateBatchAsync_InvalidSuccessBodyIsClassifiedWithoutBodyLeak(string body)
    {
        ScriptedHandler handler = new(
            (_message, _token) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                }));
        AgentApiClient client = CreateClient(handler);

        AgentApiException error = await Assert.ThrowsAsync<AgentApiException>(
            () => client.GenerateBatchAsync(CreateGenerationRequest(), CancellationToken.None));

        Assert.Equal(AgentApiFailureKind.InvalidResponse, error.Kind);
        Assert.Equal("HTTP_RESPONSE_CONTRACT_INVALID", error.ReasonCode);
        Assert.DoesNotContain(body, error.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// 408/429/5xx 可由生命周期稍后重试；其他 4xx 是当前请求的永久业务/合同拒绝。
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout, AgentApiFailureKind.Transient)]
    [InlineData((HttpStatusCode)429, AgentApiFailureKind.Transient)]
    [InlineData(HttpStatusCode.ServiceUnavailable, AgentApiFailureKind.Transient)]
    [InlineData(HttpStatusCode.UnprocessableEntity, AgentApiFailureKind.Permanent)]
    [InlineData(HttpStatusCode.Conflict, AgentApiFailureKind.Permanent)]
    public async Task GenerateBatchAsync_ClassifiesHttpStatus(
        HttpStatusCode statusCode,
        AgentApiFailureKind expectedKind)
    {
        ScriptedHandler handler = new(
            (_message, _token) => Task.FromResult(new HttpResponseMessage(statusCode)));
        AgentApiClient client = CreateClient(handler);

        AgentApiException error = await Assert.ThrowsAsync<AgentApiException>(
            () => client.GenerateBatchAsync(CreateGenerationRequest(), CancellationToken.None));

        Assert.Equal(expectedKind, error.Kind);
        Assert.Equal((int)statusCode, error.HttpStatusCode);
    }

    /// <summary>
    /// 本地 FastAPI 未启动或连接被拒绝时没有 HTTP response；客户端必须返回稳定 transient，
    /// 让 DayStarted 清空 staging 并回退原版，而不是把 socket 异常传播到 SMAPI 主线程。
    /// </summary>
    [Fact]
    public async Task GenerateBatchAsync_BackendOfflineIsStableTransientFailure()
    {
        ScriptedHandler handler = new(
            (_message, _token) => Task.FromException<HttpResponseMessage>(
                new HttpRequestException("scripted connection refused")));
        AgentApiClient client = CreateClient(handler);

        AgentApiException error = await Assert.ThrowsAsync<AgentApiException>(
            () => client.GenerateBatchAsync(CreateGenerationRequest(), CancellationToken.None));

        Assert.Equal(AgentApiFailureKind.Transient, error.Kind);
        Assert.Equal("HTTP_NETWORK_FAILURE", error.ReasonCode);
        Assert.Null(error.HttpStatusCode);
        Assert.DoesNotContain("connection refused", error.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// 内部 timeout 必须折叠为稳定 transient；调用方取消仍保持原生取消语义。
    /// </summary>
    [Fact]
    public async Task EventUpload_InternalTimeoutIsTransientButCallerCancellationPropagates()
    {
        ScriptedHandler handler = new(async (_message, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("延迟任务不应正常完成");
        });
        AgentApiClient shortClient = CreateClient(
            handler,
            new AgentApiTimeouts(
                TimeSpan.FromMilliseconds(20),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1)));

        AgentApiException timeout = await Assert.ThrowsAsync<AgentApiException>(
            () => shortClient.IngestBatchAsync(CreateEventRequest(), CancellationToken.None));
        Assert.Equal(AgentApiFailureKind.Transient, timeout.Kind);
        Assert.Equal("HTTP_REQUEST_TIMEOUT", timeout.ReasonCode);

        using CancellationTokenSource callerCancellation = new();
        callerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => shortClient.IngestBatchAsync(CreateEventRequest(), callerCancellation.Token));
    }

    /// <summary>
    /// generation ID 属于 URL path segment，斜杠和空格不能改变 endpoint 路径结构。
    /// </summary>
    [Fact]
    public async Task AcknowledgeDisplayAsync_EscapesGenerationIdAsSinglePathSegment()
    {
        Uri? capturedUri = null;
        DisplayAckRequest request = CreateDisplayAckRequest();
        ScriptedHandler handler = new((message, _token) =>
        {
            capturedUri = message.RequestUri;
            return Task.FromResult(JsonResponse(
                HttpStatusCode.OK,
                new DisplayAckResponse
                {
                    SchemaVersion = ContractVersions.V1,
                    RequestId = request.RequestId,
                    DisplayReceiptId = request.DisplayReceiptId,
                    Status = DisplayAckStatus.Accepted,
                }));
        });
        AgentApiClient client = CreateClient(handler);

        await client.AcknowledgeDisplayAsync("generation/with space", request, CancellationToken.None);

        Assert.NotNull(capturedUri);
        Assert.Contains("generation%2Fwith%20space", capturedUri!.AbsoluteUri, StringComparison.Ordinal);
        Assert.EndsWith("/displayed", capturedUri.AbsolutePath, StringComparison.Ordinal);
    }

    private static AgentApiClient CreateClient(
        HttpMessageHandler handler,
        AgentApiTimeouts? timeouts = null)
    {
        return new AgentApiClient(
            new HttpClient(handler),
            new Uri("http://127.0.0.1:8000/"),
            timeouts ?? AgentApiTimeouts.Default);
    }

    private static HttpResponseMessage JsonResponse<T>(HttpStatusCode statusCode, T value)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(
                ContractJson.Serialize(value),
                Encoding.UTF8,
                "application/json"),
        };
    }

    private static DialogueGenerationBatchRequest CreateGenerationRequest()
    {
        return ContractJson.Deserialize<DialogueGenerationBatchRequest>(
            FixtureFile.ReadAllText("dialogue_batch.json"));
    }

    private static DialogueGenerationBatchResponse CreateGenerationResponse(
        DialogueGenerationBatchRequest request)
    {
        return new DialogueGenerationBatchResponse
        {
            SchemaVersion = request.SchemaVersion,
            RequestId = request.RequestId,
            MemoryRevision = request.RequiredMemoryRevision,
            Items = request.Items.Select(item => new DialogueGenerationItemResult
            {
                TaskId = item.TaskId,
                GenerationId = $"generation-{item.NpcId}",
                GenerationKey = $"generation-key-{item.NpcId}",
                Status = DialogueGenerationStatus.Passthrough,
                Text = null,
                SourceHash = item.SourceDialogue.SourceHash,
                ReasonCode = "NO_VALUABLE_ENHANCEMENT",
                EvidenceIds = new List<string>(),
                TraceId = $"trace-{item.NpcId}",
            }).ToList(),
        };
    }

    private static GameEventBatchRequest CreateEventRequest()
    {
        return ContractJson.Deserialize<GameEventBatchRequest>(
            FixtureFile.ReadAllText("event_batch.json"));
    }

    private static DisplayAckRequest CreateDisplayAckRequest()
    {
        return new DisplayAckRequest
        {
            SchemaVersion = ContractVersions.V1,
            RequestId = "request-display-test",
            SaveId = "save-test",
            PlayerId = "player-test",
            DisplayReceiptId = "receipt-display-test",
            DisplayedDayIndex = 14,
            NpcId = "Abigail",
            SourceHash = "sha256:display-test",
        };
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler;

        public ScriptedHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            this.handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return handler(request, cancellationToken);
        }
    }
}
