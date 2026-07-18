using StardewNpcAgent.Game;

namespace StardewNpcAgent.Tests.Game;

/// <summary>
/// 冻结“受控修改已加载栈顶”方案的纯事实门禁。
/// </summary>
/// <remarks>
/// 测试只描述从公开游戏对象读取后得到的不可变事实，不直接构造 Stardew 类型。
/// 这样既能覆盖所有安全拒绝分支，也避免在 arm64 测试进程中加载 x86-64 游戏程序集。
/// </remarks>
public sealed class LoadedDialogueStackPolicyTests
{
    /// <summary>
    /// 唯一允许捕获的形状是：目标 NPC 的单元素栈、单行普通日常台词，且尚未展示或推进。
    /// </summary>
    [Fact]
    public void EvaluateCapture_ExactSingleOrdinaryLineIsEligible()
    {
        LoadedDialogueStackDecision decision = LoadedDialogueStackPolicy.EvaluateCapture(
            CreateEligibleFacts());

        Assert.True(decision.IsEligible);
        Assert.Equal(LoadedDialogueStackReasonCode.Eligible, decision.ReasonCode);
    }

    /// <summary>
    /// 每个高风险或有歧义的事实都必须独立拒绝，并返回稳定的机器可读原因。
    /// </summary>
    [Theory]
    [MemberData(nameof(GetCaptureRejections))]
    public void EvaluateCapture_UnsafeOrAmbiguousShapeIsRejected(
        Func<LoadedDialogueStackFacts, LoadedDialogueStackFacts> mutate,
        LoadedDialogueStackReasonCode expected)
    {
        LoadedDialogueStackDecision decision = LoadedDialogueStackPolicy.EvaluateCapture(
            mutate(CreateEligibleFacts()));

        Assert.False(decision.IsEligible);
        Assert.Equal(expected, decision.ReasonCode);
    }

    /// <summary>
    /// Agent 返回后重新校验时，栈、Dialogue 与 DialogueLine 三层对象都必须仍是捕获时的对象。
    /// </summary>
    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void EvaluateCurrent_RequiresAllCapturedObjectIdentities(
        bool sameStack,
        bool sameDialogue,
        bool sameLine)
    {
        LoadedDialogueStackFacts facts = CreateEligibleFacts() with
        {
            StackIdentityMatches = sameStack,
            DialogueIdentityMatches = sameDialogue,
            LineIdentityMatches = sameLine,
        };

        LoadedDialogueStackDecision decision = LoadedDialogueStackPolicy.EvaluateCurrent(facts);

        Assert.False(decision.IsEligible);
        Assert.Equal(LoadedDialogueStackReasonCode.SnapshotIdentityChanged, decision.ReasonCode);
    }

    /// <summary>
    /// 生命周期回滚只允许修改捕获时的原对象；显示状态本身不应阻止保守恢复判定。
    /// </summary>
    [Fact]
    public void CanRestore_RequiresCurrentCapturedObjectsButIgnoresDisplayEligibility()
    {
        LoadedDialogueStackFacts facts = CreateEligibleFacts() with
        {
            HasTemporaryDialogue = true,
            IsDialogueDisplayActive = true,
        };

        Assert.True(LoadedDialogueStackPolicy.CanRestore(facts));
        Assert.False(
            LoadedDialogueStackPolicy.CanRestore(
                facts with { LineIdentityMatches = false }));
    }

    /// <summary>
    /// 返回捕获阶段的全部确定性拒绝样例。每个样例只改变一个事实，便于定位回归。
    /// </summary>
    public static IEnumerable<object[]> GetCaptureRejections()
    {
        yield return Case(
            facts => facts with { HasTemporaryDialogue = true },
            LoadedDialogueStackReasonCode.TemporaryDialoguePresent);
        yield return Case(
            facts => facts with { HasLoadedStack = false },
            LoadedDialogueStackReasonCode.NoLoadedStack);
        yield return Case(
            facts => facts with { StackCount = 2 },
            LoadedDialogueStackReasonCode.UnsupportedStackShape);
        yield return Case(
            facts => facts with { HasTopDialogue = false },
            LoadedDialogueStackReasonCode.MissingTopDialogue);
        yield return Case(
            facts => facts with { SpeakerMatches = false },
            LoadedDialogueStackReasonCode.SpeakerMismatch);
        yield return Case(
            facts => facts with { TranslationKeyMatches = false },
            LoadedDialogueStackReasonCode.TranslationKeyMismatch);
        yield return Case(
            facts => facts with { IsSupportedDailySource = false },
            LoadedDialogueStackReasonCode.UnsupportedDailySource);
        yield return Case(
            facts => facts with { CurrentDialogueIndex = 1 },
            LoadedDialogueStackReasonCode.DialogueAlreadyAdvanced);
        yield return Case(
            facts => facts with { DialogueLineCount = 2 },
            LoadedDialogueStackReasonCode.UnsupportedDialogueShape);
        yield return Case(
            facts => facts with { HasCurrentLine = false },
            LoadedDialogueStackReasonCode.UnsupportedDialogueShape);
        yield return Case(
            facts => facts with { HasQuestionBehavior = true },
            LoadedDialogueStackReasonCode.QuestionDialogue);
        yield return Case(
            facts => facts with { HasSideEffects = true },
            LoadedDialogueStackReasonCode.DialogueSideEffects);
        yield return Case(
            facts => facts with { HasOnFinishCallback = true },
            LoadedDialogueStackReasonCode.DialogueOnFinishCallback);
        yield return Case(
            facts => facts with { RemoveOnNextMove = true },
            LoadedDialogueStackReasonCode.RemoveOnNextMove);
        yield return Case(
            facts => facts with { LoadedTextMatchesExpected = false },
            LoadedDialogueStackReasonCode.LoadedTextMismatch);
        yield return Case(
            facts => facts with { IsDialogueDisplayActive = true },
            LoadedDialogueStackReasonCode.DialogueDisplayActive);
    }

    /// <summary>
    /// 构造唯一安全的事实基线；各测试通过 <c>with</c> 仅改变被审查的字段。
    /// </summary>
    private static LoadedDialogueStackFacts CreateEligibleFacts() => new(
        HasTemporaryDialogue: false,
        HasLoadedStack: true,
        StackCount: 1,
        HasTopDialogue: true,
        SpeakerMatches: true,
        TranslationKeyMatches: true,
        IsSupportedDailySource: true,
        CurrentDialogueIndex: 0,
        DialogueLineCount: 1,
        HasCurrentLine: true,
        HasQuestionBehavior: false,
        HasSideEffects: false,
        HasOnFinishCallback: false,
        RemoveOnNextMove: false,
        LoadedTextMatchesExpected: true,
        IsDialogueDisplayActive: false,
        StackIdentityMatches: true,
        DialogueIdentityMatches: true,
        LineIdentityMatches: true);

    /// <summary>
    /// 将类型安全的变换与预期原因包装为 xUnit <see cref="MemberDataAttribute"/> 数据。
    /// </summary>
    private static object[] Case(
        Func<LoadedDialogueStackFacts, LoadedDialogueStackFacts> mutate,
        LoadedDialogueStackReasonCode reason) => new object[] { mutate, reason };
}
