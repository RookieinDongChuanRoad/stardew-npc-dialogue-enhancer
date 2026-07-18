using StardewNpcAgent.Game;

namespace StardewNpcAgent.Tests.Game;

/// <summary>
/// 验证 Stardew 对话 DSL 扫描器采用保守的 fail-closed 行为。
/// </summary>
public sealed class DialogueControlCommandScannerTests
{
    /// <summary>
    /// 普通自然语言标点不是游戏命令，可以进入静态追加和风格样本候选。
    /// </summary>
    [Fact]
    public void Scan_PlainTextHasNoControlSyntax()
    {
        DialogueControlScanResult result = DialogueControlCommandScanner.Scan("今天看起来会是很平静的一天。要去湖边吗？");

        Assert.False(result.HasAnyControlSyntax);
        Assert.False(result.HasQuestionSyntax);
        Assert.False(result.HasSideEffectSyntax);
        Assert.False(result.HasMultipleDialogueSyntax);
        Assert.True(result.IsSafeForStaticAppend);
    }

    /// <summary>
    /// 问题、快速响应和回答命令会改变原生交互流程，必须单独标记并拒绝。
    /// </summary>
    [Theory]
    [InlineData("要一起走吗？$q 100/Yes_好/No_不了")]
    [InlineData("$y 'Yes_好/No_不了'")]
    [InlineData("$r 100 10 answer")]
    public void Scan_RecognizesQuestionCommands(string text)
    {
        DialogueControlScanResult result = DialogueControlCommandScanner.Scan(text);

        Assert.True(result.HasAnyControlSyntax);
        Assert.True(result.HasQuestionSyntax);
        Assert.False(result.IsSafeForStaticAppend);
    }

    /// <summary>
    /// action、事件、话题和 taste reveal 都可能产生游戏副作用，不能被静态增强改写。
    /// </summary>
    [Theory]
    [InlineData("你好。$action AddMail test")]
    [InlineData("$v 100")]
    [InlineData("$t conversation_topic 7")]
    [InlineData("好吃。%revealtaste:Abigail:66")]
    [InlineData("$query PLAYER_HAS_MAIL Current test")]
    [InlineData("$c 0.5#随机分支")]
    public void Scan_RecognizesSideEffectCommands(string text)
    {
        DialogueControlScanResult result = DialogueControlCommandScanner.Scan(text);

        Assert.True(result.HasAnyControlSyntax);
        Assert.True(result.HasSideEffectSyntax);
        Assert.False(result.IsSafeForStaticAppend);
    }

    /// <summary>
    /// `$query` 是动态状态分支，不是 `$q` 问题命令；reason 分类不能靠前缀误判。
    /// </summary>
    [Fact]
    public void Scan_DoesNotMisclassifyQueryAsQuestion()
    {
        DialogueControlScanResult result = DialogueControlCommandScanner.Scan(
            "$query PLAYER_HAS_MAIL Current test");

        Assert.True(result.HasSideEffectSyntax);
        Assert.False(result.HasQuestionSyntax);
    }

    /// <summary>
    /// 多屏和随机多分支语法会改变行数与选择，必须显式归类为 multi-line。
    /// </summary>
    [Theory]
    [InlineData("第一屏#$b#第二屏")]
    [InlineData("第一种||第二种")]
    [InlineData("第一行\n第二行")]
    [InlineData("第一行\r\n第二行")]
    public void Scan_RecognizesMultipleDialogueSyntax(string text)
    {
        DialogueControlScanResult result = DialogueControlCommandScanner.Scan(text);

        Assert.True(result.HasAnyControlSyntax);
        Assert.True(result.HasMultipleDialogueSyntax);
        Assert.False(result.IsSafeForStaticAppend);
    }

    /// <summary>
    /// 情绪、token、性别分支等其他 DSL 即使不执行 action，也不应由 Spike 猜测其拼接语义。
    /// </summary>
    [Theory]
    [InlineData("今天不错。$h")]
    [InlineData("你好，@。")]
    [InlineData("农场 %farm 很漂亮。")]
    [InlineData("男声^女声")]
    [InlineData("一段¦另一段")]
    public void Scan_ConservativelyRejectsOtherStardewDsl(string text)
    {
        DialogueControlScanResult result = DialogueControlCommandScanner.Scan(text);

        Assert.True(result.HasAnyControlSyntax);
        Assert.False(result.IsSafeForStaticAppend);
    }

    /// <summary>
    /// typed template policy 的局部豁免不能改变通用 scanner：其他自由文本入口仍把 <c>@</c>
    /// 当作未知 DSL，只有明确 daily source/output 边界才可解析它。
    /// </summary>
    [Fact]
    public void Scan_PlayerNameTokenRemainsDangerousOutsideTypedTemplatePolicy()
    {
        DialogueControlScanResult result = DialogueControlCommandScanner.Scan("你好，@");

        Assert.True(result.HasAnyControlSyntax);
        Assert.False(result.IsSafeForStaticAppend);
    }

    /// <summary>
    /// 方括号会被游戏解释为物品领取对话；即使没有美元命令，也必须视为危险语义。
    /// </summary>
    [Theory]
    [InlineData("给你。[Object 74]")]
    [InlineData("[74]")]
    public void Scan_RejectsItemGrabDialogueSyntax(string text)
    {
        DialogueControlScanResult result = DialogueControlCommandScanner.Scan(text);

        Assert.True(result.HasAnyControlSyntax);
        Assert.True(result.HasSideEffectSyntax);
        Assert.False(result.IsSafeForStaticAppend);
    }
}
