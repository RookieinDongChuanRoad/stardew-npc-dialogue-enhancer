using StardewNpcAgent.Game;
using StardewNpcAgent.Infrastructure.Storage;

namespace StardewNpcAgent.Tests.Infrastructure.Storage;

/// <summary>
/// 验证内存 cache 以日期、locale、NPC、资产和 key 组成完整身份，不发生串日或串语言。
/// </summary>
public sealed class DailyDialogueCacheTests
{
    /// <summary>
    /// candidate 与 cache 必须都显式保存 family/raw/hash；不能只靠 asset/key 或 hash 反推。
    /// </summary>
    [Fact]
    public void SourceIdentityCarriers_ExposeFamilyRawTextAndHashAsStoredFields()
    {
        Assert.NotNull(typeof(DialogueCandidate).GetProperty("SourceFamily"));
        Assert.NotNull(typeof(DialogueCandidate).GetProperty("SourceText"));
        Assert.NotNull(typeof(DialogueCandidate).GetProperty("SourceHash"));
        Assert.NotNull(typeof(DailyDialogueCacheEntry).GetProperty("SourceFamily"));
        Assert.NotNull(typeof(DailyDialogueCacheEntry).GetProperty("SourceText"));
        Assert.NotNull(typeof(DailyDialogueCacheEntry).GetProperty("SourceHash"));
    }

    /// <summary>
    /// 静态 Spike 也必须保存显式 source identity，但不会伪造正式 generation metadata。
    /// </summary>
    [Fact]
    public void StaticEntry_PreservesSourceIdentityAndLeavesGenerationMetadataEmpty()
    {
        DailyDialogueCacheEntry entry = CreateEntry(dayIndex: 42, locale: "zh-CN");

        Assert.Null(entry.GenerationId);
        Assert.Null(entry.GenerationKey);
        Assert.Null(entry.TraceId);
        Assert.False(entry.HasCompleteGenerationMetadata);
    }

    /// <summary>
    /// 正式 generated cache 必须完整保存三个展示身份字段，供点击路径后续复核与 ACK 使用。
    /// </summary>
    [Fact]
    public void ExtendedConstructor_PreservesCompleteGenerationMetadata()
    {
        DailyDialogueCacheEntry legacyEntry = CreateEntry(dayIndex: 42, locale: "zh-CN");
        DailyDialogueCacheEntry generatedEntry = new(
            legacyEntry.Key,
            legacyEntry.SourceFamily,
            legacyEntry.SourceText,
            legacyEntry.SourceHash,
            legacyEntry.EnhancedText,
            "generation-abigail-42",
            "generation-key-abigail-42",
            "trace-abigail-42");

        Assert.Equal("generation-abigail-42", generatedEntry.GenerationId);
        Assert.Equal("generation-key-abigail-42", generatedEntry.GenerationKey);
        Assert.Equal("trace-abigail-42", generatedEntry.TraceId);
        Assert.True(generatedEntry.HasCompleteGenerationMetadata);
    }

    /// <summary>
    /// 完整 key 命中；只改变 day 或 locale 都必须 miss。
    /// </summary>
    [Fact]
    public void TryGet_DoesNotCrossDayOrLocale()
    {
        DailyDialogueCache cache = new();
        DailyDialogueCacheEntry entry = CreateEntry(dayIndex: 42, locale: "zh-CN");
        cache.Store(entry);

        bool exactHit = cache.TryGet(entry.Key, out DailyDialogueCacheEntry? exact);
        bool nextDayHit = cache.TryGet(entry.Key with { GameDayIndex = 43 }, out _);
        bool englishHit = cache.TryGet(entry.Key with { Locale = "en" }, out _);

        Assert.True(exactHit);
        Assert.Same(entry, exact);
        Assert.False(nextDayHit);
        Assert.False(englishHit);
    }

    /// <summary>
    /// 同一身份的新结果覆盖旧值，避免同一资产 edit 收到两个相互冲突的候选。
    /// </summary>
    [Fact]
    public void Store_ReplacesEntryWithSameCompositeKey()
    {
        DailyDialogueCache cache = new();
        DailyDialogueCacheEntry first = CreateEntry(42, "zh-CN");
        DailyDialogueCacheEntry second = first with { EnhancedText = "replacement" };

        cache.Store(first);
        cache.Store(second);

        Assert.Single(cache.Snapshot());
        Assert.True(cache.TryGet(first.Key, out DailyDialogueCacheEntry? stored));
        Assert.Equal("replacement", stored!.EnhancedText);
    }

    /// <summary>
    /// 精确删除只能影响完整匹配的 day/locale/NPC/asset/key，不能误删其他语言的结果。
    /// </summary>
    [Fact]
    public void Remove_DeletesOnlyTheExactCompositeKey()
    {
        DailyDialogueCache cache = new();
        DailyDialogueCacheEntry target = CreateEntry(42, "zh-CN");
        DailyDialogueCacheEntry otherLocale = CreateEntry(42, "en");
        cache.Store(target);
        cache.Store(otherLocale);

        bool removed = cache.Remove(target.Key);
        bool removedAgain = cache.Remove(target.Key);

        Assert.True(removed);
        Assert.False(removedAgain);
        Assert.False(cache.TryGet(target.Key, out _));
        Assert.True(cache.TryGet(otherLocale.Key, out _));
    }

    /// <summary>
    /// Snapshot 是稳定副本，Clear 后旧 snapshot 可用于安全失效旧资产。
    /// </summary>
    [Fact]
    public void Snapshot_IsStableCopyAndClearRemovesAllEntries()
    {
        DailyDialogueCache cache = new();
        cache.Store(CreateEntry(42, "zh-CN"));

        IReadOnlyList<DailyDialogueCacheEntry> snapshot = cache.Snapshot();
        cache.Clear();

        Assert.Single(snapshot);
        Assert.Empty(cache.Snapshot());
    }

    /// <summary>
    /// 创建一个合法缓存项；source hash 用可读占位符即可，哈希语义由专门测试冻结。
    /// </summary>
    private static DailyDialogueCacheEntry CreateEntry(int dayIndex, string locale)
    {
        DailyDialogueCacheKey key = new(
            dayIndex,
            locale,
            "Abigail",
            "Characters/Dialogue/Abigail",
            "spring_Mon");
        return new DailyDialogueCacheEntry(
            key,
            DialogueSourceFamily.OrdinaryDaily,
            "原版正文",
            SourceDialogueHasher.Compute("原版正文"),
            "原文 【静态增强】");
    }
}
