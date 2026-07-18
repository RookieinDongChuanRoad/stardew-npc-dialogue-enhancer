namespace StardewNpcAgent.Game;

/// <summary>
/// 普通日常对话资格判定的确定性 reason code。
/// </summary>
/// <remarks>
/// 枚举值用于日志和测试，不授权任何 Agent 覆盖。新增条件时必须先增加失败测试，再明确
/// 在 <see cref="DialogueEligibilityPolicy.Evaluate"/> 中的优先级。
/// </remarks>
public enum DialogueEligibilityReasonCode
{
    Eligible,
    UnknownContext,
    NpcNotTargeted,
    NonNpcDialogueAsset,
    UnsupportedDailySource,
    FestivalDay,
    PassiveFestivalDay,
    EventActive,
    QuestionDialogue,
    GiftDialogue,
    QuestDialogue,
    PendingActiveDialogueEvent,
    CurrentDialogueAlreadyLoaded,
    GreenRain,
    DangerousControlCommand,
    DialogueSideEffects,
    MultipleDialogueLines,
    DialogueOnFinishCallback,
    RemoveOnNextMove,
}

/// <summary>
/// 与游戏对象解耦的资格事实快照。
/// </summary>
/// <remarks>
/// 所有属性只表示适配器已经观察到的事实；policy 不读取全局游戏状态，因此可用理论 fixture
/// 完整覆盖。布尔默认值不会被解释为“已知安全”，调用方必须显式设置
/// <see cref="IsKnownContext"/>、<see cref="IsTargetNpc"/>、
/// <see cref="IsNpcDialogueAsset"/> 与 <see cref="IsSupportedDailySource"/>。
/// </remarks>
public sealed class DialogueEligibilityContext
{
    public bool IsKnownContext { get; set; }

    public bool IsTargetNpc { get; set; }

    public bool IsNpcDialogueAsset { get; set; }

    public bool IsSupportedDailySource { get; set; }

    public bool IsFestivalDay { get; set; }

    public bool IsPassiveFestivalDay { get; set; }

    public bool IsEventActive { get; set; }

    public bool IsQuestionDialogue { get; set; }

    public bool IsGiftDialogue { get; set; }

    public bool IsQuestDialogue { get; set; }

    public bool HasPendingActiveDialogueEvent { get; set; }

    /// <summary>
    /// 目标 NPC 是否存在优先级更高的临时台词。
    /// </summary>
    /// <remarks>
    /// 该信号永远不能被已加载栈模式绕过，因为临时台词可能属于事件或其他特殊流程。
    /// </remarks>
    public bool HasTemporaryDialogue { get; set; }

    public bool IsCurrentDialogueLoaded { get; set; }

    /// <summary>
    /// 调用方是否已经准备使用更严格的已加载栈策略继续验证当前候选。
    /// </summary>
    /// <remarks>
    /// 此标志仅允许绕过普通栈的加载时序门禁，不会削弱节日、事件、天气、婚姻、
    /// 控制命令或副作用等任何其他资格条件。
    /// </remarks>
    public bool AllowVerifiedLoadedStack { get; set; }

    public bool IsGreenRaining { get; set; }

    public bool HasDangerousControlCommand { get; set; }

    public bool HasSideEffects { get; set; }

    public bool HasMultipleDialogueLines { get; set; }

    public bool HasOnFinishCallback { get; set; }

    public bool RemoveOnNextMove { get; set; }
}

/// <summary>
/// 资格 policy 的单一终态。
/// </summary>
/// <param name="IsEligible">是否可进入静态 enhancer。</param>
/// <param name="ReasonCode">首个命中的确定性边界。</param>
public sealed record DialogueEligibilityDecision(
    bool IsEligible,
    DialogueEligibilityReasonCode ReasonCode);

/// <summary>
/// 只允许可证明安全的普通日常首条对话进入增强缓存。
/// </summary>
public static class DialogueEligibilityPolicy
{
    /// <summary>
    /// 按固定优先级评估事实快照；任一不确定或特殊信号都返回 skipped reason。
    /// </summary>
    /// <param name="context">由游戏适配器构建的不可推测事实集合。</param>
    /// <returns>eligible 或第一个明确拒绝原因。</returns>
    public static DialogueEligibilityDecision Evaluate(DialogueEligibilityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.IsKnownContext)
        {
            return Rejected(DialogueEligibilityReasonCode.UnknownContext);
        }

        if (!context.IsTargetNpc)
        {
            return Rejected(DialogueEligibilityReasonCode.NpcNotTargeted);
        }

        if (!context.IsNpcDialogueAsset)
        {
            return Rejected(DialogueEligibilityReasonCode.NonNpcDialogueAsset);
        }

        if (!context.IsSupportedDailySource)
        {
            return Rejected(DialogueEligibilityReasonCode.UnsupportedDailySource);
        }

        if (context.IsFestivalDay)
        {
            return Rejected(DialogueEligibilityReasonCode.FestivalDay);
        }

        if (context.IsPassiveFestivalDay)
        {
            return Rejected(DialogueEligibilityReasonCode.PassiveFestivalDay);
        }

        if (context.IsEventActive)
        {
            return Rejected(DialogueEligibilityReasonCode.EventActive);
        }

        if (context.IsQuestionDialogue)
        {
            return Rejected(DialogueEligibilityReasonCode.QuestionDialogue);
        }

        if (context.IsGiftDialogue)
        {
            return Rejected(DialogueEligibilityReasonCode.GiftDialogue);
        }

        if (context.IsQuestDialogue)
        {
            return Rejected(DialogueEligibilityReasonCode.QuestDialogue);
        }

        if (context.HasPendingActiveDialogueEvent)
        {
            return Rejected(DialogueEligibilityReasonCode.PendingActiveDialogueEvent);
        }

        // 临时台词属于更高优先级内容，不能因调用方选择已加载栈模式而被日常增强覆盖。
        if (context.HasTemporaryDialogue)
        {
            return Rejected(DialogueEligibilityReasonCode.CurrentDialogueAlreadyLoaded);
        }

        // 已加载模式只放宽普通栈的时间点限制。真正的栈形状、对象身份与原文比较由
        // LoadedDialogueStackPolicy 和主线程 compare-and-swap 协调层继续负责。
        if (context.IsCurrentDialogueLoaded && !context.AllowVerifiedLoadedStack)
        {
            return Rejected(DialogueEligibilityReasonCode.CurrentDialogueAlreadyLoaded);
        }

        // 绿雨是比普通雨更具体的条件，必须先返回具体 reason，便于手工验收定位。
        if (context.IsGreenRaining)
        {
            return Rejected(DialogueEligibilityReasonCode.GreenRain);
        }

        if (context.HasDangerousControlCommand)
        {
            return Rejected(DialogueEligibilityReasonCode.DangerousControlCommand);
        }

        if (context.HasSideEffects)
        {
            return Rejected(DialogueEligibilityReasonCode.DialogueSideEffects);
        }

        if (context.HasMultipleDialogueLines)
        {
            return Rejected(DialogueEligibilityReasonCode.MultipleDialogueLines);
        }

        if (context.HasOnFinishCallback)
        {
            return Rejected(DialogueEligibilityReasonCode.DialogueOnFinishCallback);
        }

        if (context.RemoveOnNextMove)
        {
            return Rejected(DialogueEligibilityReasonCode.RemoveOnNextMove);
        }

        return new DialogueEligibilityDecision(true, DialogueEligibilityReasonCode.Eligible);
    }

    /// <summary>
    /// 构造统一拒绝结果，避免每个分支重复布尔含义。
    /// </summary>
    private static DialogueEligibilityDecision Rejected(DialogueEligibilityReasonCode reasonCode)
    {
        return new DialogueEligibilityDecision(false, reasonCode);
    }
}
