using StardewNpcAgent.Game;

namespace StardewNpcAgent.Tests.Game;

/// <summary>
/// 验证静态 Spike enhancer 只做无 DSL 的可见追加，不改写原始语义锚点。
/// </summary>
public sealed class StaticDialogueEnhancerTests
{
    /// <summary>
    /// 合法输入逐字符保留原文，并只追加一个配置标记。
    /// </summary>
    [Fact]
    public void TryEnhance_PreservesSourceAndAppendsVisibleMarker()
    {
        StaticDialogueEnhancementResult result = StaticDialogueEnhancer.TryEnhance(
            "今天也许会去湖边。",
            "【NPC Agent 静态增强测试】");

        Assert.True(result.IsSuccessful, result.ReasonCode.ToString());
        Assert.Equal(StaticDialogueEnhancementReasonCode.Enhanced, result.ReasonCode);
        Assert.Equal("今天也许会去湖边。 【NPC Agent 静态增强测试】", result.EnhancedText);
        Assert.StartsWith("今天也许会去湖边。", result.EnhancedText, StringComparison.Ordinal);
    }

    /// <summary>
    /// 空源、空标记和首尾空白标记都不是可审计的静态增强输入。
    /// </summary>
    [Theory]
    [InlineData("", "【标记】", StaticDialogueEnhancementReasonCode.EmptySource)]
    [InlineData("   ", "【标记】", StaticDialogueEnhancementReasonCode.EmptySource)]
    [InlineData("原文", "", StaticDialogueEnhancementReasonCode.InvalidMarker)]
    [InlineData("原文", " 标记", StaticDialogueEnhancementReasonCode.InvalidMarker)]
    [InlineData("原文", "标记 ", StaticDialogueEnhancementReasonCode.InvalidMarker)]
    public void TryEnhance_RejectsEmptyOrAmbiguousInputs(
        string source,
        string marker,
        StaticDialogueEnhancementReasonCode expectedReason)
    {
        StaticDialogueEnhancementResult result = StaticDialogueEnhancer.TryEnhance(source, marker);

        Assert.False(result.IsSuccessful);
        Assert.Equal(expectedReason, result.ReasonCode);
        Assert.Null(result.EnhancedText);
    }

    /// <summary>
    /// 原文或标记含 Stardew DSL 时，追加位置可能改变解析语义，必须拒绝而不是猜测。
    /// </summary>
    [Theory]
    [InlineData("原文$h", "【标记】")]
    [InlineData("原文", "【标记$q】")]
    [InlineData("第一行\n第二行", "【标记】")]
    public void TryEnhance_RejectsDangerousDslInSourceOrMarker(string source, string marker)
    {
        StaticDialogueEnhancementResult result = StaticDialogueEnhancer.TryEnhance(source, marker);

        Assert.False(result.IsSuccessful);
        Assert.Equal(StaticDialogueEnhancementReasonCode.UnsafeControlSyntax, result.ReasonCode);
        Assert.Null(result.EnhancedText);
    }
}
