using StardewNpcAgent.Game;

namespace StardewNpcAgent.Tests.Game;

/// <summary>
/// 冻结 C# 游戏侧与 Python codec 相同的 typed 玩家名槽合同。
/// </summary>
/// <remarks>
/// 本测试只处理 raw Stardew template 与本地渲染，不访问游戏、玩家对象、HTTP 或持久化。
/// 真实玩家名只作为一次 render 调用的输入，永远不是 template identity 的组成部分。
/// </remarks>
public sealed class DialogueTemplatePolicyTests
{
    /// <summary>
    /// 零或一个 <c>@</c> 必须无损解析；parser 不 Trim、不 normalize，槽可位于首尾。
    /// </summary>
    [Theory]
    [InlineData("雨天待在屋里也不算太糟。", "雨天待在屋里也不算太糟。", DialogueAddressSlot.None, "")]
    [InlineData("@，今天还好吗？", "", DialogueAddressSlot.PlayerName, "，今天还好吗？")]
    [InlineData("回头见，@", "回头见，", DialogueAddressSlot.PlayerName, "")]
    [InlineData(" 前后空格保留 ", " 前后空格保留 ", DialogueAddressSlot.None, "")]
    public void TryParse_ZeroOrOnePlayerNameSlotRoundTripsWithoutNormalization(
        string rawText,
        string expectedPrefix,
        DialogueAddressSlot expectedSlot,
        string expectedSuffix)
    {
        bool parsed = DialogueTemplatePolicy.TryParse(rawText, out DialogueTextTemplate? template);

        Assert.True(parsed);
        Assert.NotNull(template);
        Assert.Equal(expectedPrefix, template!.Prefix);
        Assert.Equal(expectedSlot, template.AddressSlot);
        Assert.Equal(expectedSuffix, template.Suffix);
        Assert.Equal(rawText, DialogueTemplatePolicy.RenderGameTemplate(template));
    }

    /// <summary>
    /// 与 Python fixture 同义的全部未批准 DSL 必须失败，不能因为旁边有一个合法槽而逃逸。
    /// </summary>
    [Theory]
    [InlineData("你好，@@")]
    [InlineData("你好，%endearment")]
    [InlineData("你好，$")]
    [InlineData("你好，#")]
    [InlineData("你好，^")]
    [InlineData("你好，¦")]
    [InlineData("你好，[秘密]")]
    [InlineData("你好，{秘密}")]
    [InlineData("你好\r再见")]
    [InlineData("你好\n再见")]
    [InlineData("你好||再见")]
    public void TryParse_RejectsSecondSlotAndEveryOtherDialogueDsl(string rawText)
    {
        Assert.False(DialogueTemplatePolicy.TryParse(rawText, out DialogueTextTemplate? template));
        Assert.Null(template);
    }

    /// <summary>
    /// 本地渲染只替换 typed 槽；无槽正文不依赖玩家名，有槽正文则要求可用的过滤后名字。
    /// </summary>
    [Fact]
    public void TryRenderForDisplay_UsesOnlyEphemeralFilteredNameWhenSlotExists()
    {
        Assert.True(
            DialogueTemplatePolicy.TryParse(
                "今天也别太累，@。",
                out DialogueTextTemplate? addressed));
        Assert.True(
            DialogueTemplatePolicy.TryRenderForDisplay(
                addressed!,
                "Sensitive Farmer 42",
                out string? rendered));
        Assert.Equal("今天也别太累，Sensitive Farmer 42。", rendered);

        Assert.True(
            DialogueTemplatePolicy.TryParse("今天也别太累。", out DialogueTextTemplate? plain));
        Assert.True(DialogueTemplatePolicy.TryRenderForDisplay(plain!, null, out rendered));
        Assert.Equal("今天也别太累。", rendered);

        Assert.False(DialogueTemplatePolicy.TryRenderForDisplay(addressed!, null, out rendered));
        Assert.Null(rendered);
    }
}
