using StardewNpcAgent.Game;
using StardewNpcAgent.Infrastructure.Storage;

namespace StardewNpcAgent.Tests.Game;

/// <summary>
/// 验证资产编辑只有在完整 cache identity 与当前 source hash 都一致时才发生。
/// </summary>
public sealed class DialogueAssetPatchPolicyTests
{
    /// <summary>
    /// 命中同日、同 locale、同资产、同 key 且 source hash 相符时才替换字典值。
    /// </summary>
    [Fact]
    public void Apply_MatchingEntryReplacesExactDialogueKey()
    {
        Dictionary<string, string> asset = new() { ["spring_Mon"] = "原版台词" };
        DailyDialogueCacheEntry entry = CreateEntry("原版台词");
        DialogueAssetPatchContext context = CreateContext();

        DialogueAssetPatchDecision result = DialogueAssetPatchPolicy.Apply(asset, entry, context);

        Assert.True(result.WasApplied);
        Assert.Equal(DialogueAssetPatchReasonCode.Applied, result.ReasonCode);
        Assert.Equal("增强台词", asset["spring_Mon"]);
    }

    /// <summary>
    /// 最终资产写边界只允许 typed template：一个 <c>@</c> 原样进入游戏供原版展开，
    /// 第二个槽或其他 DSL 即使绕过上游 cache 构造也不能修改字典。
    /// </summary>
    [Theory]
    [InlineData("今天也别太累，@。", true)]
    [InlineData("你好，@@。", false)]
    [InlineData("你好，%endearment。", false)]
    public void Apply_RechecksGeneratedTemplateBeforeFinalDictionaryWrite(
        string enhancedText,
        bool expectedApplied)
    {
        Dictionary<string, string> asset = new() { ["spring_Mon"] = "原版台词" };
        DailyDialogueCacheEntry entry = CreateEntry("原版台词") with
        {
            EnhancedText = enhancedText,
        };

        DialogueAssetPatchDecision result = DialogueAssetPatchPolicy.Apply(
            asset,
            entry,
            CreateContext());

        Assert.Equal(expectedApplied, result.WasApplied);
        Assert.Equal(
            expectedApplied ? enhancedText : "原版台词",
            asset["spring_Mon"]);
        Assert.Equal(
            expectedApplied
                ? DialogueAssetPatchReasonCode.Applied
                : DialogueAssetPatchReasonCode.InvalidEnhancedTemplate,
            result.ReasonCode);
    }

    /// <summary>
    /// 没有 cache 是正常 fallback，字典必须保持逐项不变。
    /// </summary>
    [Fact]
    public void Apply_NoCacheEntryLeavesDictionaryUnchanged()
    {
        Dictionary<string, string> asset = new() { ["spring_Mon"] = "原版台词" };
        Dictionary<string, string> before = new(asset);

        DialogueAssetPatchDecision result = DialogueAssetPatchPolicy.Apply(asset, null, CreateContext());

        Assert.False(result.WasApplied);
        Assert.Equal(DialogueAssetPatchReasonCode.NoCacheEntry, result.ReasonCode);
        Assert.Equal(before, asset);
    }

    /// <summary>
    /// Content Patcher 或其他 Mod 改变当前 source 后，旧增强结果必须失效。
    /// </summary>
    [Fact]
    public void Apply_SourceHashMismatchLeavesDictionaryUnchanged()
    {
        Dictionary<string, string> asset = new() { ["spring_Mon"] = "其他 Mod 修改后的台词" };
        Dictionary<string, string> before = new(asset);
        DailyDialogueCacheEntry entry = CreateEntry("原版台词") with
        {
            SourceText = "其他 Mod 修改后的台词",
        };

        DialogueAssetPatchDecision result = DialogueAssetPatchPolicy.Apply(asset, entry, CreateContext());

        Assert.False(result.WasApplied);
        Assert.Equal(DialogueAssetPatchReasonCode.SourceHashMismatch, result.ReasonCode);
        Assert.Equal(before, asset);
    }

    /// <summary>
    /// source key 已被移除时不能新建 key，因为这会覆盖其他 Mod 的删除意图。
    /// </summary>
    [Fact]
    public void Apply_SourceMissingLeavesDictionaryUnchanged()
    {
        Dictionary<string, string> asset = new() { ["spring_Tue"] = "另一天" };
        Dictionary<string, string> before = new(asset);

        DialogueAssetPatchDecision result = DialogueAssetPatchPolicy.Apply(
            asset,
            CreateEntry("原版台词"),
            CreateContext());

        Assert.False(result.WasApplied);
        Assert.Equal(DialogueAssetPatchReasonCode.SourceMissing, result.ReasonCode);
        Assert.Equal(before, asset);
    }

    /// <summary>
    /// 日期、locale 或资产名不一致都属于 cache context mismatch，禁止跨上下文应用。
    /// </summary>
    [Theory]
    [InlineData(43, "zh-CN", "Characters/Dialogue/Abigail")]
    [InlineData(42, "en", "Characters/Dialogue/Abigail")]
    [InlineData(42, "zh-CN", "Characters/Dialogue/Sebastian")]
    public void Apply_ContextMismatchLeavesDictionaryUnchanged(
        int dayIndex,
        string locale,
        string assetName)
    {
        Dictionary<string, string> asset = new() { ["spring_Mon"] = "原版台词" };
        Dictionary<string, string> before = new(asset);
        DialogueAssetPatchContext context = new(dayIndex, locale, assetName);

        DialogueAssetPatchDecision result = DialogueAssetPatchPolicy.Apply(
            asset,
            CreateEntry("原版台词"),
            context);

        Assert.False(result.WasApplied);
        Assert.Equal(DialogueAssetPatchReasonCode.CacheContextMismatch, result.ReasonCode);
        Assert.Equal(before, asset);
    }

    /// <summary>
    /// cache 内部 NPC ID 必须与 asset sheet 一致；不能仅凭 day/locale/asset/hash 就跨 NPC 应用。
    /// </summary>
    [Fact]
    public void Apply_NpcIdAndDialogueSheetMismatchLeavesDictionaryUnchanged()
    {
        Dictionary<string, string> asset = new() { ["spring_Mon"] = "原版台词" };
        Dictionary<string, string> before = new(asset);
        DailyDialogueCacheEntry validEntry = CreateEntry("原版台词");
        DailyDialogueCacheEntry entry = validEntry with
        {
            Key = validEntry.Key with { NpcId = "Sebastian" },
        };

        DialogueAssetPatchDecision result = DialogueAssetPatchPolicy.Apply(
            asset,
            entry,
            CreateContext());

        Assert.False(result.WasApplied);
        Assert.Equal(DialogueAssetPatchReasonCode.InvalidCacheIdentity, result.ReasonCode);
        Assert.Equal(before, asset);
    }

    /// <summary>
    /// cache family 与 exact asset/key 分类不一致时，即使 raw text/hash 相同也不能授权 patch。
    /// </summary>
    [Fact]
    public void Apply_SourceFamilyDriftLeavesDictionaryUnchanged()
    {
        Dictionary<string, string> asset = new() { ["spring_Mon"] = "原版台词" };
        DailyDialogueCacheEntry entry = CreateEntry("原版台词") with
        {
            SourceFamily = DialogueSourceFamily.RainyDaily,
        };

        DialogueAssetPatchDecision result = DialogueAssetPatchPolicy.Apply(
            asset,
            entry,
            CreateContext());

        Assert.False(result.WasApplied);
        Assert.Equal("原版台词", asset["spring_Mon"]);
    }

    /// <summary>
    /// cache 必须同时保存并比较 raw text 与 hash；只让 hash 看似匹配不能掩盖正文身份漂移。
    /// </summary>
    [Fact]
    public void Apply_SourceTextDriftWithMatchingHashLeavesDictionaryUnchanged()
    {
        Dictionary<string, string> asset = new() { ["spring_Mon"] = "当前原版台词" };
        DailyDialogueCacheEntry entry = CreateEntry("当前原版台词") with
        {
            SourceText = "缓存绑定了另一段正文",
        };

        DialogueAssetPatchDecision result = DialogueAssetPatchPolicy.Apply(
            asset,
            entry,
            CreateContext());

        Assert.False(result.WasApplied);
        Assert.Equal("当前原版台词", asset["spring_Mon"]);
    }

    /// <summary>
    /// rainy cache 的 exact identity 是共享 asset + NPC key，不能强行套 ordinary sheet 规则。
    /// </summary>
    [Fact]
    public void Apply_ExactRainyIdentityReplacesNpcKeyInSharedAsset()
    {
        const string sourceText = "雨天原版台词";
        Dictionary<string, string> asset = new() { ["Abigail"] = sourceText };
        DailyDialogueCacheEntry entry = new(
            new DailyDialogueCacheKey(
                42,
                "zh-CN",
                "Abigail",
                "Characters/Dialogue/rainy",
                "Abigail"),
            DialogueSourceFamily.RainyDaily,
            sourceText,
            SourceDialogueHasher.Compute(sourceText),
            "雨天增强台词");

        DialogueAssetPatchDecision result = DialogueAssetPatchPolicy.Apply(
            asset,
            entry,
            new DialogueAssetPatchContext(42, "zh-CN", "Characters/Dialogue/rainy"));

        Assert.True(result.WasApplied);
        Assert.Equal("雨天增强台词", asset["Abigail"]);
    }

    /// <summary>
    /// 生成与当前 source 精确绑定的缓存项。
    /// </summary>
    private static DailyDialogueCacheEntry CreateEntry(string sourceText)
    {
        DailyDialogueCacheKey key = new(
            42,
            "zh-CN",
            "Abigail",
            "Characters/Dialogue/Abigail",
            "spring_Mon");
        return new DailyDialogueCacheEntry(
            key,
            DialogueSourceFamily.OrdinaryDaily,
            sourceText,
            SourceDialogueHasher.Compute(sourceText),
            "增强台词");
    }

    /// <summary>
    /// 资产事件的即时上下文，与缓存 key 分开传入以便 policy 做完整比较。
    /// </summary>
    private static DialogueAssetPatchContext CreateContext()
    {
        return new DialogueAssetPatchContext(
            42,
            "zh-CN",
            "Characters/Dialogue/Abigail");
    }
}
