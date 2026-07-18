using StardewNpcAgent.Game;

namespace StardewNpcAgent.Infrastructure.Storage;

/// <summary>
/// 单条增强对话在游戏侧内存 cache 中的完整复合身份。
/// </summary>
/// <param name="GameDayIndex">绝对游戏日；禁止跨日命中。</param>
/// <param name="Locale">SMAPI 当前 locale；禁止跨语言命中。</param>
/// <param name="NpcId">稳定 NPC 内部 ID。</param>
/// <param name="AssetName">规范化后的精确对话资产名。</param>
/// <param name="DialogueKey">资产字典中的精确字段 key。</param>
public sealed record DailyDialogueCacheKey(
    int GameDayIndex,
    string Locale,
    string NpcId,
    string AssetName,
    string DialogueKey);

/// <summary>
/// 当日增强缓存项。
/// </summary>
/// <param name="Key">完整日期、语言与来源身份。</param>
/// <param name="SourceFamily">独立分类后的 ordinary/rainy family。</param>
/// <param name="SourceText">生成前逐字符原文；每个消费边界都必须同时比较正文与 hash。</param>
/// <param name="SourceHash">生成前原文的逐字符 SHA-256。</param>
/// <param name="EnhancedText">仅在所有 gate 通过后允许写入资产的文本。</param>
/// <param name="GenerationId">后端生成记录身份；静态 Spike 兼容路径允许为 null。</param>
/// <param name="GenerationKey">后端幂等生成键；静态 Spike 兼容路径允许为 null。</param>
/// <param name="TraceId">后端审计 trace 身份；静态 Spike 兼容路径允许为 null。</param>
public sealed record DailyDialogueCacheEntry(
    DailyDialogueCacheKey Key,
    DialogueSourceFamily SourceFamily,
    string SourceText,
    string SourceHash,
    string EnhancedText,
    string? GenerationId = null,
    string? GenerationKey = null,
    string? TraceId = null)
{
    /// <summary>
    /// 是否同时具备展示确认所需的 generation、key 与 trace 身份。
    /// </summary>
    public bool HasCompleteGenerationMetadata =>
        !string.IsNullOrWhiteSpace(GenerationId)
        && !string.IsNullOrWhiteSpace(GenerationKey)
        && !string.IsNullOrWhiteSpace(TraceId);
}

/// <summary>
/// 进程内当日对话缓存。
/// </summary>
/// <remarks>
/// 本类不做磁盘持久化，只冻结完整复合 key、覆盖语义、snapshot 与 clear。Phase 2 静态
/// Spike 和 Phase 4 正式 generated 结果共用该容器；是否允许展示由上层根据来源 hash 与
/// generation metadata 决定，容器本身不猜测业务终态。
/// </remarks>
public sealed class DailyDialogueCache
{
    private readonly Dictionary<DailyDialogueCacheKey, DailyDialogueCacheEntry> entries = new();

    /// <summary>
    /// 保存或覆盖同一复合身份的结果。
    /// </summary>
    /// <param name="entry">已通过 resolver、policy 与静态 enhancer 的缓存项。</param>
    public void Store(DailyDialogueCacheEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        entries[entry.Key] = entry;
    }

    /// <summary>
    /// 只按完整 key 查找；调用方不能省略日期或 locale。
    /// </summary>
    /// <param name="key">当前展示上下文的完整身份。</param>
    /// <param name="entry">命中时返回缓存项，否则为 null。</param>
    /// <returns>是否精确命中。</returns>
    public bool TryGet(DailyDialogueCacheKey key, out DailyDialogueCacheEntry? entry)
    {
        ArgumentNullException.ThrowIfNull(key);
        return entries.TryGetValue(key, out entry);
    }

    /// <summary>
    /// 只删除完整匹配的 day/locale/NPC/asset/key 缓存项。
    /// </summary>
    /// <param name="key">必须完整匹配的缓存复合身份。</param>
    /// <returns>删除前是否确实存在该项；重复删除返回 <see langword="false"/>。</returns>
    /// <remarks>
    /// 直接栈注入在拒绝、回滚和成功展示后都需要释放单个结果。提供精确删除避免调用方
    /// 为清理一条记录而清空同日其他 NPC 或其他 locale 的合法缓存。
    /// </remarks>
    public bool Remove(DailyDialogueCacheKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return entries.Remove(key);
    }

    /// <summary>
    /// 返回稳定排序的独立列表，供清空前失效旧资产和诊断使用。
    /// </summary>
    public IReadOnlyList<DailyDialogueCacheEntry> Snapshot()
    {
        return entries.Values
            .OrderBy(entry => entry.Key.GameDayIndex)
            .ThenBy(entry => entry.Key.Locale, StringComparer.Ordinal)
            .ThenBy(entry => entry.Key.NpcId, StringComparer.Ordinal)
            .ThenBy(entry => entry.Key.AssetName, StringComparer.Ordinal)
            .ThenBy(entry => entry.Key.DialogueKey, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// 删除所有缓存项；不会自行访问游戏 content cache，资产失效由运行时显式编排。
    /// </summary>
    public void Clear()
    {
        entries.Clear();
    }
}
