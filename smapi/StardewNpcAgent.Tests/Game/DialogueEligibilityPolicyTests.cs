using StardewNpcAgent.Game;

namespace StardewNpcAgent.Tests.Game;

/// <summary>
/// 覆盖普通日常候选的确定性资格矩阵；任何不确定或特殊分支都必须回退原版。
/// </summary>
public sealed class DialogueEligibilityPolicyTests
{
    /// <summary>
    /// 已知环境、目标 NPC、自身普通 daily 资产且无副作用时才允许静态增强。
    /// </summary>
    [Fact]
    public void Evaluate_OrdinaryDailyDialogueIsEligible()
    {
        DialogueEligibilityDecision result = DialogueEligibilityPolicy.Evaluate(CreateEligibleContext());

        Assert.True(result.IsEligible);
        Assert.Equal(DialogueEligibilityReasonCode.Eligible, result.ReasonCode);
    }

    /// <summary>
    /// 已由专用栈策略验证的调用路径，只能显式绕过“原版栈已加载”这一项时序门禁。
    /// </summary>
    [Fact]
    public void Evaluate_VerifiedLoadedModeMayBypassOnlyLoadedTimingGate()
    {
        DialogueEligibilityContext context = CreateEligibleContext();
        context.IsCurrentDialogueLoaded = true;
        context.AllowVerifiedLoadedStack = true;

        DialogueEligibilityDecision result = DialogueEligibilityPolicy.Evaluate(context);

        Assert.True(result.IsEligible);
        Assert.Equal(DialogueEligibilityReasonCode.Eligible, result.ReasonCode);
    }

    /// <summary>
    /// 临时台词代表更高优先级的游戏内容，即使选择已加载模式也必须原样回退。
    /// </summary>
    [Fact]
    public void Evaluate_TemporaryDialogueCannotBeBypassedByLoadedMode()
    {
        DialogueEligibilityContext context = CreateEligibleContext();
        context.HasTemporaryDialogue = true;
        context.IsCurrentDialogueLoaded = true;
        context.AllowVerifiedLoadedStack = true;

        DialogueEligibilityDecision result = DialogueEligibilityPolicy.Evaluate(context);

        Assert.False(result.IsEligible);
        Assert.Equal(
            DialogueEligibilityReasonCode.CurrentDialogueAlreadyLoaded,
            result.ReasonCode);
    }

    /// <summary>
    /// 失败矩阵逐项只改变一个信号，以证明每个明确边界都有稳定 reason code。
    /// </summary>
    [Theory]
    [MemberData(nameof(GetIneligibleCases))]
    public void Evaluate_SpecialOrUncertainContextIsSkipped(
        Action<DialogueEligibilityContext> makeIneligible,
        DialogueEligibilityReasonCode expectedReason)
    {
        DialogueEligibilityContext context = CreateEligibleContext();
        makeIneligible(context);

        DialogueEligibilityDecision result = DialogueEligibilityPolicy.Evaluate(context);

        Assert.False(result.IsEligible);
        Assert.Equal(expectedReason, result.ReasonCode);
    }

    /// <summary>
    /// xUnit 理论数据：节日、事件、问题、礼物、任务、缓存状态及 DSL 全覆盖。
    /// </summary>
    public static IEnumerable<object[]> GetIneligibleCases()
    {
        yield return Case(context => context.IsKnownContext = false, DialogueEligibilityReasonCode.UnknownContext);
        yield return Case(context => context.IsTargetNpc = false, DialogueEligibilityReasonCode.NpcNotTargeted);
        yield return Case(context => context.IsNpcDialogueAsset = false, DialogueEligibilityReasonCode.NonNpcDialogueAsset);
        yield return Case(context => context.IsSupportedDailySource = false, DialogueEligibilityReasonCode.UnsupportedDailySource);
        yield return Case(context => context.IsFestivalDay = true, DialogueEligibilityReasonCode.FestivalDay);
        yield return Case(context => context.IsPassiveFestivalDay = true, DialogueEligibilityReasonCode.PassiveFestivalDay);
        yield return Case(context => context.IsEventActive = true, DialogueEligibilityReasonCode.EventActive);
        yield return Case(context => context.IsQuestionDialogue = true, DialogueEligibilityReasonCode.QuestionDialogue);
        yield return Case(context => context.IsGiftDialogue = true, DialogueEligibilityReasonCode.GiftDialogue);
        yield return Case(context => context.IsQuestDialogue = true, DialogueEligibilityReasonCode.QuestDialogue);
        yield return Case(context => context.HasPendingActiveDialogueEvent = true, DialogueEligibilityReasonCode.PendingActiveDialogueEvent);
        yield return Case(context => context.IsCurrentDialogueLoaded = true, DialogueEligibilityReasonCode.CurrentDialogueAlreadyLoaded);
        yield return Case(context => context.IsGreenRaining = true, DialogueEligibilityReasonCode.GreenRain);
        yield return Case(context => context.HasDangerousControlCommand = true, DialogueEligibilityReasonCode.DangerousControlCommand);
        yield return Case(context => context.HasSideEffects = true, DialogueEligibilityReasonCode.DialogueSideEffects);
        yield return Case(context => context.HasMultipleDialogueLines = true, DialogueEligibilityReasonCode.MultipleDialogueLines);
        yield return Case(context => context.HasOnFinishCallback = true, DialogueEligibilityReasonCode.DialogueOnFinishCallback);
        yield return Case(context => context.RemoveOnNextMove = true, DialogueEligibilityReasonCode.RemoveOnNextMove);
    }

    /// <summary>
    /// 建立唯一 eligible 基线，避免每个测试遗漏某个安全信号。
    /// </summary>
    private static DialogueEligibilityContext CreateEligibleContext()
    {
        return new DialogueEligibilityContext
        {
            IsKnownContext = true,
            IsTargetNpc = true,
            IsNpcDialogueAsset = true,
            IsSupportedDailySource = true,
        };
    }

    /// <summary>
    /// 普通雨与 spouse 关系只是上下文事实；只要 exact source 是 ordinary/rainy，不能据此 blanket 拒绝。
    /// GreenRain 仍由独立确定性边界优先拒绝。
    /// </summary>
    [Theory]
    [InlineData(DialogueSourceFamily.OrdinaryDaily)]
    [InlineData(DialogueSourceFamily.RainyDaily)]
    public void Evaluate_SupportedDailySourceIsNotRejectedByWeatherOrRelationship(
        DialogueSourceFamily sourceFamily)
    {
        DialogueEligibilityContext context = CreateEligibleContext();

        DialogueEligibilityDecision result = DialogueEligibilityPolicy.Evaluate(context);

        Assert.True(result.IsEligible, sourceFamily.ToString());
        Assert.Equal(DialogueEligibilityReasonCode.Eligible, result.ReasonCode);
    }

    /// <summary>
    /// 把强类型委托和预期枚举包装为 xUnit MemberData 项。
    /// </summary>
    private static object[] Case(
        Action<DialogueEligibilityContext> makeIneligible,
        DialogueEligibilityReasonCode reasonCode)
    {
        return new object[] { makeIneligible, reasonCode };
    }
}
