using System.Text.Json.Serialization;

namespace StardewNpcAgent.Contracts;

/// <summary>
/// 展示回执的幂等接收状态。
/// </summary>
public enum DisplayAckStatus
{
    /// <summary>首次接收该 display_receipt_id。</summary>
    Accepted,

    /// <summary>相同回执已经处理，不重复消费冷却。</summary>
    Duplicate,
}

/// <summary>
/// 游戏确认一条合法增强台词已经实际展示的回执。
/// </summary>
/// <remarks>
/// generation_id 位于 HTTP 路径中；请求体保留 save_id/player_id，防止同一 NPC 与
/// source hash 在不同存档之间错误归属。Phase 1 只冻结 DTO，不发送网络请求。
/// </remarks>
public sealed class DisplayAckRequest : ContractDto
{
    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; set; } = string.Empty;

    [JsonPropertyName("request_id")]
    public string RequestId { get; set; } = string.Empty;

    [JsonPropertyName("save_id")]
    public string SaveId { get; set; } = string.Empty;

    [JsonPropertyName("player_id")]
    public string PlayerId { get; set; } = string.Empty;

    [JsonPropertyName("display_receipt_id")]
    public string DisplayReceiptId { get; set; } = string.Empty;

    [JsonPropertyName("displayed_day_index")]
    public int DisplayedDayIndex { get; set; }

    [JsonPropertyName("npc_id")]
    public string NpcId { get; set; } = string.Empty;

    [JsonPropertyName("source_hash")]
    public string SourceHash { get; set; } = string.Empty;
}

/// <summary>
/// 后端对展示回执的幂等确认。
/// </summary>
public sealed class DisplayAckResponse : ContractDto
{
    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; set; } = string.Empty;

    [JsonPropertyName("request_id")]
    public string RequestId { get; set; } = string.Empty;

    [JsonPropertyName("display_receipt_id")]
    public string DisplayReceiptId { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public DisplayAckStatus Status { get; set; }
}
