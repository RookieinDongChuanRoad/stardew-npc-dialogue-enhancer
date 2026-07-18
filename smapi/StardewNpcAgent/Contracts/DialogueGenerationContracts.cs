using System.Text.Json;
using System.Text.Json.Serialization;

namespace StardewNpcAgent.Contracts;

/// <summary>
/// 单 NPC 每日生成任务的业务终态。
/// </summary>
public enum DialogueGenerationStatus
{
    /// <summary>生成了通过 Guard、允许展示的增强文本。</summary>
    Generated,

    /// <summary>Agent 判断没有有价值的增强，正常保留原台词。</summary>
    Passthrough,

    /// <summary>确定性资格检查认为该项不应增强。</summary>
    Skipped,

    /// <summary>生成、工具或 Guard 流程失败，游戏必须回退原台词。</summary>
    Failed,
}

/// <summary>
/// 每日批量预生成请求。
/// </summary>
public sealed class DialogueGenerationBatchRequest : ContractDto
{
    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; set; } = string.Empty;

    [JsonPropertyName("request_id")]
    public string RequestId { get; set; } = string.Empty;

    [JsonPropertyName("save_id")]
    public string SaveId { get; set; } = string.Empty;

    [JsonPropertyName("player_id")]
    public string PlayerId { get; set; } = string.Empty;

    [JsonPropertyName("game_day_index")]
    public int GameDayIndex { get; set; }

    [JsonPropertyName("required_memory_revision")]
    public int RequiredMemoryRevision { get; set; }

    [JsonPropertyName("stable_day_context")]
    public StableDayContext StableDayContext { get; set; } = new();

    [JsonPropertyName("items")]
    public List<DialogueGenerationItem> Items { get; set; } = new();
}

/// <summary>
/// 每日预生成期间保持稳定的游戏世界快照。
/// </summary>
public sealed class StableDayContext : ContractDto
{
    [JsonPropertyName("season")]
    public string Season { get; set; } = string.Empty;

    [JsonPropertyName("weather")]
    public string Weather { get; set; } = string.Empty;

    [JsonPropertyName("locale")]
    public string Locale { get; set; } = string.Empty;

    /// <summary>
    /// 可扩展的世界进度 JSON object；游戏侧是这些事实的权威来源。
    /// </summary>
    [JsonPropertyName("progression_signals")]
    public JsonElement ProgressionSignals { get; set; }
}

/// <summary>
/// 批次中的单 NPC 生成任务。
/// </summary>
public sealed class DialogueGenerationItem : ContractDto
{
    [JsonPropertyName("task_id")]
    public string TaskId { get; set; } = string.Empty;

    [JsonPropertyName("npc_id")]
    public string NpcId { get; set; } = string.Empty;

    [JsonPropertyName("source_dialogue")]
    public SourceDialogue SourceDialogue { get; set; } = new();

    [JsonPropertyName("relationship_snapshot")]
    public RelationshipSnapshot RelationshipSnapshot { get; set; } = new();

    /// <summary>
    /// 同 NPC、同 locale 的 2～5 条确定性原版台词，只约束语言风格。
    /// </summary>
    [JsonPropertyName("style_examples")]
    public List<string> StyleExamples { get; set; } = new();

    /// <summary>
    /// 轻量结构化记忆线索；每项必须是 JSON object，不等同于完整记忆正文。
    /// </summary>
    [JsonPropertyName("memory_signals")]
    public List<JsonElement> MemorySignals { get; set; } = new();
}

/// <summary>
/// 原始台词语义锚点及展示前一致性指纹。
/// </summary>
public sealed class SourceDialogue : ContractDto
{
    [JsonPropertyName("asset_name")]
    public string AssetName { get; set; } = string.Empty;

    [JsonPropertyName("dialogue_key")]
    public string DialogueKey { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("source_hash")]
    public string SourceHash { get; set; } = string.Empty;
}

/// <summary>
/// 由游戏侧读取的 NPC 关系快照，后端只能消费、不能修改。
/// </summary>
public sealed class RelationshipSnapshot : ContractDto
{
    [JsonPropertyName("friendship_points")]
    public int FriendshipPoints { get; set; }

    [JsonPropertyName("relationship_stage")]
    public string RelationshipStage { get; set; } = string.Empty;
}

/// <summary>
/// 每日批量生成响应；各 NPC 项彼此独立进入终态。
/// </summary>
public sealed class DialogueGenerationBatchResponse : ContractDto
{
    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; set; } = string.Empty;

    [JsonPropertyName("request_id")]
    public string RequestId { get; set; } = string.Empty;

    [JsonPropertyName("memory_revision")]
    public int MemoryRevision { get; set; }

    [JsonPropertyName("items")]
    public List<DialogueGenerationItemResult> Items { get; set; } = new();
}

/// <summary>
/// 单 NPC 生成任务的终态结果。
/// </summary>
public sealed class DialogueGenerationItemResult : ContractDto
{
    [JsonPropertyName("task_id")]
    public string TaskId { get; set; } = string.Empty;

    [JsonPropertyName("generation_id")]
    public string GenerationId { get; set; } = string.Empty;

    [JsonPropertyName("generation_key")]
    public string GenerationKey { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public DialogueGenerationStatus Status { get; set; }

    /// <summary>
    /// 只有 generated 可携带非空文本；其他终态必须为 null。
    /// </summary>
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("source_hash")]
    public string SourceHash { get; set; } = string.Empty;

    [JsonPropertyName("reason_code")]
    public string ReasonCode { get; set; } = string.Empty;

    [JsonPropertyName("evidence_ids")]
    public List<string> EvidenceIds { get; set; } = new();

    [JsonPropertyName("trace_id")]
    public string TraceId { get; set; } = string.Empty;
}
