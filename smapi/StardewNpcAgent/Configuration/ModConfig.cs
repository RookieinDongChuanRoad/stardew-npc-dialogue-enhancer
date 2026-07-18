namespace StardewNpcAgent.Configuration;

using StardewNpcAgent.Game;

/// <summary>
/// Stardew 普通日常对话静态 Spike 与正式 Agent runtime 的用户配置。
/// </summary>
/// <remarks>
/// 默认两种模式都关闭，确保仅安装 Mod 不订阅资产编辑、不访问网络、不改变 NPC 交互。
/// API key 永远只存在 Python 后端；SMAPI 配置只保存 loopback 地址与有限 timeout。
/// </remarks>
public sealed class ModConfig
{
    /// <summary>
    /// 是否显式启用静态对话 Spike。默认 <c>false</c>，只有用户人工开启后才装配运行时。
    /// </summary>
    public bool EnableStaticDialogueSpike { get; set; }

    /// <summary>
    /// 是否启用正式 SMAPI/FastAPI Agent 链路。默认 <c>false</c>，且不能与静态 Spike 同时开启。
    /// </summary>
    public bool EnableAgentDialogue { get; set; }

    /// <summary>
    /// 本地 FastAPI 后端基地址。MVP 只允许 loopback，远程部署尚未实现认证与 TLS 配置。
    /// </summary>
    public string BackendBaseUrl { get; set; } = "http://127.0.0.1:8000/";

    /// <summary>
    /// 事件批次上传的单次 HTTP timeout（毫秒）。失败后事实保留在 durable outbox。
    /// </summary>
    public int EventRequestTimeoutMilliseconds { get; set; } = 3_000;

    /// <summary>
    /// 每日批量生成的单次 HTTP timeout（毫秒）；120 秒覆盖后端 105 秒批次预算与传输余量。
    /// </summary>
    public int GenerationRequestTimeoutMilliseconds { get; set; } = 120_000;

    /// <summary>
    /// displayed ACK 的单次 HTTP timeout（毫秒）。失败后回执保留在 durable outbox。
    /// </summary>
    public int DisplayAckRequestTimeoutMilliseconds { get; set; } = 3_000;

    /// <summary>
    /// 允许参与 Spike 的稳定 NPC 内部 ID。
    /// </summary>
    /// <remarks>
    /// 属性允许 null 是为了安全承接手工编辑或旧配置反序列化结果；业务代码必须通过
    /// <see cref="GetNormalizedTargetNpcIds"/> 获取清洗后的只读列表。
    /// </remarks>
    public List<string?>? TargetNpcIds { get; set; } = VanillaMarriageableNpcRegistry.AllIds
        .Select(npcId => (string?)npcId)
        .ToList();

    /// <summary>
    /// 静态 enhancer 追加到原文后的可见测试标记。
    /// </summary>
    /// <remarks>
    /// 默认值只包含普通 Unicode 文本，不含 Stardew 对话 DSL。运行时仍会再次调用
    /// <c>StaticDialogueEnhancer</c> 校验用户自定义值，不能因配置可写而跳过安全边界。
    /// </remarks>
    public string StaticDialogueMarker { get; set; } = "【NPC Agent 静态增强测试】";

    /// <summary>
    /// 清洗目标 NPC ID：去掉空项、修剪首尾空白，并按首次出现顺序做大小写敏感去重。
    /// </summary>
    /// <returns>可安全枚举的稳定内部 ID 列表；坏配置最多得到空列表，不抛异常。</returns>
    public IReadOnlyList<string> GetNormalizedTargetNpcIds()
    {
        if (TargetNpcIds is null)
        {
            return Array.Empty<string>();
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        List<string> normalized = new();
        foreach (string? configuredId in TargetNpcIds)
        {
            string? candidate = configuredId?.Trim();
            if (!string.IsNullOrEmpty(candidate) && seen.Add(candidate))
            {
                normalized.Add(candidate);
            }
        }

        return normalized;
    }
}
