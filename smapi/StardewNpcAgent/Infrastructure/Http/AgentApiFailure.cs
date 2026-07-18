namespace StardewNpcAgent.Infrastructure.Http;

/// <summary>
/// HTTP 边界对调用方有意义的三类失败。
/// </summary>
public enum AgentApiFailureKind
{
    /// <summary>连接、timeout、408、429 或 5xx，可由 durable outbox 稍后重试。</summary>
    Transient,

    /// <summary>4xx 业务/合同拒绝；是否 dead letter 由具体 outbox coordinator 决定。</summary>
    Permanent,

    /// <summary>2xx body 不是当前严格共享合同；保留 pending 并停止本轮。</summary>
    InvalidResponse,
}

/// <summary>
/// Agent API client 向上层暴露的稳定失败，不保存 response body、URL 或底层异常正文。
/// </summary>
public sealed class AgentApiException : Exception
{
    /// <summary>
    /// 创建一个只携带机器分类的异常。
    /// </summary>
    public AgentApiException(
        AgentApiFailureKind kind,
        string reasonCode,
        int? httpStatusCode = null)
        : base("agent api request failed")
    {
        if (string.IsNullOrWhiteSpace(reasonCode) || reasonCode != reasonCode.Trim())
        {
            throw new ArgumentException("reasonCode 必须非空且无首尾空白。", nameof(reasonCode));
        }

        Kind = kind;
        ReasonCode = reasonCode;
        HttpStatusCode = httpStatusCode;
    }

    /// <summary>瞬态、永久或响应合同失败。</summary>
    public AgentApiFailureKind Kind { get; }

    /// <summary>不含自由文本的稳定机器码。</summary>
    public string ReasonCode { get; }

    /// <summary>存在 HTTP response 时的整数状态码；连接/timeout 为 null。</summary>
    public int? HttpStatusCode { get; }
}

/// <summary>
/// 三类 endpoint 独立 timeout；避免用 HttpClient 全局 timeout 误伤 30 秒生成批次。
/// </summary>
public sealed record AgentApiTimeouts
{
    /// <summary>项目冻结的默认 3s / 35s / 3s。</summary>
    public static AgentApiTimeouts Default { get; } = new(
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(35),
        TimeSpan.FromSeconds(3));

    /// <summary>
    /// 构造并验证三个正、有界 timeout。
    /// </summary>
    public AgentApiTimeouts(
        TimeSpan eventRequest,
        TimeSpan generationRequest,
        TimeSpan displayAckRequest)
    {
        Validate(eventRequest, nameof(eventRequest));
        Validate(generationRequest, nameof(generationRequest));
        Validate(displayAckRequest, nameof(displayAckRequest));
        EventRequest = eventRequest;
        GenerationRequest = generationRequest;
        DisplayAckRequest = displayAckRequest;
    }

    public TimeSpan EventRequest { get; }

    public TimeSpan GenerationRequest { get; }

    public TimeSpan DisplayAckRequest { get; }

    private static void Validate(TimeSpan value, string parameterName)
    {
        if (value < TimeSpan.FromMilliseconds(10) || value > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(parameterName, "HTTP timeout 必须位于 10ms..2min。");
        }
    }
}
