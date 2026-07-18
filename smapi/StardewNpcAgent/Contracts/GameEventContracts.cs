using System.Text.Json;
using System.Text.Json.Serialization;

namespace StardewNpcAgent.Contracts;

/// <summary>
/// 游戏事件的可见范围。
/// </summary>
public enum AudienceScope
{
    /// <summary>所有 NPC 都可共享的世界事实。</summary>
    Public,

    /// <summary>只允许指定 NPC 读取的定向事实。</summary>
    Npc,
}

/// <summary>
/// 后端对单条事件的幂等接收终态。
/// </summary>
public enum EventIngestionStatus
{
    /// <summary>首次接收并提交。</summary>
    Accepted,

    /// <summary>相同 event_id 已经提交，未创建第二条事实。</summary>
    Duplicate,

    /// <summary>事件无法通过合同或业务校验。</summary>
    Rejected,
}

/// <summary>
/// 按存档和玩家分区上传的一批结构化游戏事实。
/// </summary>
public sealed class GameEventBatchRequest : ContractDto
{
    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; set; } = string.Empty;

    [JsonPropertyName("request_id")]
    public string RequestId { get; set; } = string.Empty;

    [JsonPropertyName("save_id")]
    public string SaveId { get; set; } = string.Empty;

    [JsonPropertyName("player_id")]
    public string PlayerId { get; set; } = string.Empty;

    [JsonPropertyName("events")]
    public List<GameEvent> Events { get; set; } = new();
}

/// <summary>
/// 游戏侧已经确认发生的一条版本化事实事件。
/// </summary>
/// <remarks>
/// <see cref="Payload"/> 保持为 JSON object，以便不同 event_type/version 使用自己的
/// 字段；本层只保存 wire 数据，不解释礼物、进度等领域语义。
/// </remarks>
public sealed class GameEvent : ContractDto
{
    [JsonPropertyName("event_id")]
    public string EventId { get; set; } = string.Empty;

    [JsonPropertyName("event_type")]
    public string EventType { get; set; } = string.Empty;

    [JsonPropertyName("event_version")]
    public string EventVersion { get; set; } = string.Empty;

    [JsonPropertyName("occurred_day_index")]
    public int OccurredDayIndex { get; set; }

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("audience_scope")]
    public AudienceScope AudienceScope { get; set; }

    [JsonPropertyName("audience_npc_id")]
    public string? AudienceNpcId { get; set; }

    [JsonPropertyName("payload")]
    public JsonElement Payload { get; set; }
}

/// <summary>
/// 单条事件的接收结果。
/// </summary>
public sealed class GameEventItemResult : ContractDto
{
    [JsonPropertyName("event_id")]
    public string EventId { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public EventIngestionStatus Status { get; set; }

    [JsonPropertyName("reason_code")]
    public string? ReasonCode { get; set; }
}

/// <summary>
/// 事件批次响应及后端已经提交的记忆版本水位。
/// </summary>
public sealed class GameEventBatchResponse : ContractDto
{
    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; set; } = string.Empty;

    [JsonPropertyName("request_id")]
    public string RequestId { get; set; } = string.Empty;

    [JsonPropertyName("memory_revision")]
    public int MemoryRevision { get; set; }

    [JsonPropertyName("committed_through_day_index")]
    public int CommittedThroughDayIndex { get; set; }

    [JsonPropertyName("items")]
    public List<GameEventItemResult> Items { get; set; } = new();
}
