using StardewNpcAgent.Configuration;

namespace StardewNpcAgent.Tests.Configuration;

/// <summary>
/// 验证正式模式只允许显式、互斥、loopback 且有界的后端配置。
/// </summary>
public sealed class ModConfigValidatorTests
{
    /// <summary>
    /// 默认安装两种行为模式都关闭，因此即使后端不存在也应是合法零行为配置。
    /// </summary>
    [Fact]
    public void Defaults_AreValidAndDoNotCreateAgentConfiguration()
    {
        ModConfigValidationResult result = ModConfigValidator.Validate(new ModConfig());

        Assert.True(result.IsValid);
        Assert.Empty(result.ErrorCodes);
        Assert.Null(result.AgentConfiguration);
    }

    /// <summary>
    /// TargetNpcIds 的 null/empty 由 compatibility policy 解释为合法零目标；validator 不得二次解释为错误。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExplicitNullOrEmptyTargetNpcIds_RemainsValidZeroTargetConfiguration(bool useNull)
    {
        ModConfig config = new()
        {
            TargetNpcIds = useNull ? null : new List<string?>(),
        };

        ModConfigValidationResult result = ModConfigValidator.Validate(config);

        Assert.True(result.IsValid);
        Assert.Empty(result.ErrorCodes);
        Assert.Null(result.AgentConfiguration);
    }

    /// <summary>
    /// 正式模式必须把 URL 与三个 timeout 转成不可变运行配置，不能在运行中反复解释字符串。
    /// </summary>
    [Fact]
    public void EnabledAgentMode_ReturnsValidatedLoopbackConfiguration()
    {
        ModConfig config = new() { EnableAgentDialogue = true };

        ModConfigValidationResult result = ModConfigValidator.Validate(config);

        Assert.True(result.IsValid);
        ValidatedAgentConfiguration validated = Assert.IsType<ValidatedAgentConfiguration>(
            result.AgentConfiguration);
        Assert.Equal(new Uri("http://127.0.0.1:8000/"), validated.BackendBaseUri);
        Assert.Equal(TimeSpan.FromSeconds(3), validated.EventRequestTimeout);
        Assert.Equal(TimeSpan.FromSeconds(120), validated.GenerationRequestTimeout);
        Assert.Equal(TimeSpan.FromSeconds(3), validated.DisplayAckRequestTimeout);
    }

    /// <summary>
    /// Static spike 与正式 Agent 同时 patch 同一资产会产生不确定顺序，必须整体保持零行为。
    /// </summary>
    [Fact]
    public void MutuallyExclusiveModes_AreRejected()
    {
        ModConfigValidationResult result = ModConfigValidator.Validate(
            new ModConfig
            {
                EnableStaticDialogueSpike = true,
                EnableAgentDialogue = true,
            });

        Assert.False(result.IsValid);
        Assert.Contains("MUTUALLY_EXCLUSIVE_DIALOGUE_MODES", result.ErrorCodes);
        Assert.Null(result.AgentConfiguration);
    }

    /// <summary>
    /// MVP 没有远程认证/TLS 配置，因此非 loopback 地址不能被误当成受支持部署。
    /// </summary>
    [Theory]
    [InlineData("http://example.com:8000/")]
    [InlineData("not-a-uri")]
    public void NonLoopbackOrMalformedBackend_IsRejected(string backendBaseUrl)
    {
        ModConfigValidationResult result = ModConfigValidator.Validate(
            new ModConfig
            {
                EnableAgentDialogue = true,
                BackendBaseUrl = backendBaseUrl,
            });

        Assert.False(result.IsValid);
        Assert.Contains("INVALID_LOOPBACK_BACKEND_URL", result.ErrorCodes);
    }

    /// <summary>
    /// timeout 是产品级等待边界，零值、负值或异常长等待都必须在订阅 SMAPI 事件前拒绝。
    /// </summary>
    [Fact]
    public void InvalidTimeout_IsRejectedWithStableCode()
    {
        ModConfigValidationResult result = ModConfigValidator.Validate(
            new ModConfig
            {
                EnableAgentDialogue = true,
                EventRequestTimeoutMilliseconds = 0,
            });

        Assert.False(result.IsValid);
        Assert.Contains("INVALID_EVENT_REQUEST_TIMEOUT", result.ErrorCodes);
    }

    /// <summary>
    /// generation HTTP 可以使用批准的 120 秒默认值，但不能借本次放宽取消既有产品上限。
    /// </summary>
    [Fact]
    public void GenerationTimeout_AboveApprovedMaximumIsRejected()
    {
        ModConfigValidationResult result = ModConfigValidator.Validate(
            new ModConfig
            {
                EnableAgentDialogue = true,
                GenerationRequestTimeoutMilliseconds = 120_001,
            });

        Assert.False(result.IsValid);
        Assert.Contains("INVALID_GENERATION_REQUEST_TIMEOUT", result.ErrorCodes);
        Assert.Null(result.AgentConfiguration);
    }
}
