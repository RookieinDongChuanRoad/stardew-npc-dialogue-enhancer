namespace StardewNpcAgent.Configuration;

/// <summary>
/// 已经通过配置校验、可直接交给正式 runtime 的不可变后端参数。
/// </summary>
/// <param name="BackendBaseUri">只允许 loopback 的绝对 HTTP(S) 基地址。</param>
/// <param name="EventRequestTimeout">事件上传的单次等待边界。</param>
/// <param name="GenerationRequestTimeout">每日生成的单次等待边界。</param>
/// <param name="DisplayAckRequestTimeout">展示 ACK 的单次等待边界。</param>
public sealed record ValidatedAgentConfiguration(
    Uri BackendBaseUri,
    TimeSpan EventRequestTimeout,
    TimeSpan GenerationRequestTimeout,
    TimeSpan DisplayAckRequestTimeout);

/// <summary>
/// 配置校验终态；错误只使用稳定机器码，不回显用户输入 URL。
/// </summary>
/// <param name="IsValid">是否允许 ModEntry 继续装配所选模式。</param>
/// <param name="ErrorCodes">稳定、去重且保持发现顺序的错误码。</param>
/// <param name="AgentConfiguration">正式模式合法时的不可变参数；其他情况为 null。</param>
public sealed record ModConfigValidationResult(
    bool IsValid,
    IReadOnlyList<string> ErrorCodes,
    ValidatedAgentConfiguration? AgentConfiguration);

/// <summary>
/// 在订阅任何 SMAPI 事件或创建 HttpClient 前验证用户配置。
/// </summary>
/// <remarks>
/// <c>TargetNpcIds</c> 必须在调用本类前由 <see cref="ModConfigCompatibilityPolicy"/> 解析；validator
/// 只负责模式互斥、loopback URL 与 timeout，不重复过滤 NPC，也不把合法 null/empty 误判成配置错误。
/// </remarks>
public static class ModConfigValidator
{
    private const int MinimumTimeoutMilliseconds = 100;
    private const int MaximumTimeoutMilliseconds = 120_000;

    /// <summary>
    /// 验证模式互斥、loopback URL 与三个有界 timeout；不访问网络或文件系统。
    /// </summary>
    public static ModConfigValidationResult Validate(ModConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        List<string> errors = new();

        if (config.EnableStaticDialogueSpike && config.EnableAgentDialogue)
        {
            errors.Add("MUTUALLY_EXCLUSIVE_DIALOGUE_MODES");
        }

        ValidatedAgentConfiguration? agentConfiguration = null;
        if (config.EnableAgentDialogue)
        {
            Uri? backendUri = ParseLoopbackUri(config.BackendBaseUrl);
            if (backendUri is null)
            {
                errors.Add("INVALID_LOOPBACK_BACKEND_URL");
            }

            ValidateTimeout(
                config.EventRequestTimeoutMilliseconds,
                "INVALID_EVENT_REQUEST_TIMEOUT",
                errors);
            ValidateTimeout(
                config.GenerationRequestTimeoutMilliseconds,
                "INVALID_GENERATION_REQUEST_TIMEOUT",
                errors);
            ValidateTimeout(
                config.DisplayAckRequestTimeoutMilliseconds,
                "INVALID_DISPLAY_ACK_REQUEST_TIMEOUT",
                errors);

            if (errors.Count == 0 && backendUri is not null)
            {
                agentConfiguration = new ValidatedAgentConfiguration(
                    backendUri,
                    TimeSpan.FromMilliseconds(config.EventRequestTimeoutMilliseconds),
                    TimeSpan.FromMilliseconds(config.GenerationRequestTimeoutMilliseconds),
                    TimeSpan.FromMilliseconds(config.DisplayAckRequestTimeoutMilliseconds));
            }
        }

        string[] stableErrors = errors.Distinct(StringComparer.Ordinal).ToArray();
        return new ModConfigValidationResult(
            stableErrors.Length == 0,
            Array.AsReadOnly(stableErrors),
            agentConfiguration);
    }

    /// <summary>
    /// 只接受无 user-info/query/fragment 的 loopback HTTP(S) 基地址，并规范化末尾斜杠。
    /// </summary>
    private static Uri? ParseLoopbackUri(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue)
            || rawValue != rawValue.Trim()
            || !Uri.TryCreate(rawValue, UriKind.Absolute, out Uri? parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            || !parsed.IsLoopback
            || !string.IsNullOrEmpty(parsed.UserInfo)
            || !string.IsNullOrEmpty(parsed.Query)
            || !string.IsNullOrEmpty(parsed.Fragment))
        {
            return null;
        }

        string normalized = parsed.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? parsed.AbsoluteUri
            : parsed.AbsoluteUri + "/";
        return new Uri(normalized, UriKind.Absolute);
    }

    /// <summary>
    /// timeout 必须足够执行一次本地 HTTP 往返，同时不能允许分钟级无界卡住。
    /// </summary>
    private static void ValidateTimeout(
        int value,
        string errorCode,
        ICollection<string> errors)
    {
        if (value < MinimumTimeoutMilliseconds || value > MaximumTimeoutMilliseconds)
        {
            errors.Add(errorCode);
        }
    }
}
