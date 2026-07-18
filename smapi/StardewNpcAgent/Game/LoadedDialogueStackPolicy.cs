namespace StardewNpcAgent.Game;

/// <summary>
/// 描述“受控修改已加载栈顶”门禁的稳定结果码。
/// </summary>
/// <remarks>
/// 这些结果码用于诊断与测试，不携带游戏对象，也不授权任何写操作。
/// 顺序体现策略的审查优先级：先排除临时/缺失/歧义状态，再核对语义与显示状态。
/// </remarks>
public enum LoadedDialogueStackReasonCode
{
    /// <summary>全部条件满足，可以继续下一层确定性校验。</summary>
    Eligible,

    /// <summary>NPC 正在使用临时台词；临时台词永远不能被日常增强绕过。</summary>
    TemporaryDialoguePresent,

    /// <summary>游戏尚未为目标 NPC 加载当日台词栈。</summary>
    NoLoadedStack,

    /// <summary>台词栈不是唯一可解释的单元素形状。</summary>
    UnsupportedStackShape,

    /// <summary>栈存在但没有可读取的栈顶 Dialogue。</summary>
    MissingTopDialogue,

    /// <summary>栈顶 Dialogue 的说话者不是请求中的目标 NPC。</summary>
    SpeakerMismatch,

    /// <summary>栈顶 Dialogue 的翻译键与候选来源不一致。</summary>
    TranslationKeyMismatch,

    /// <summary>actual TranslationKey 不属于受支持的 ordinary/rainy daily source。</summary>
    UnsupportedDailySource,

    /// <summary>Dialogue 已经推进，不再是尚未展示的首行状态。</summary>
    DialogueAlreadyAdvanced,

    /// <summary>Dialogue 不是唯一单行，或当前行不存在。</summary>
    UnsupportedDialogueShape,

    /// <summary>Dialogue 包含问题行为，不能作为纯文本替换。</summary>
    QuestionDialogue,

    /// <summary>Dialogue 包含命令或其他副作用。</summary>
    DialogueSideEffects,

    /// <summary>Dialogue 带有结束回调，替换可能改变游戏流程。</summary>
    DialogueOnFinishCallback,

    /// <summary>Dialogue 会在下一次移动时被移除，生命周期不稳定。</summary>
    RemoveOnNextMove,

    /// <summary>已加载文本与候选解析时的原文不一致。</summary>
    LoadedTextMismatch,

    /// <summary>当前已有台词框显示，不能证明该行尚未被玩家看到。</summary>
    DialogueDisplayActive,

    /// <summary>Agent 等待期间，栈、Dialogue 或 DialogueLine 对象身份已经变化。</summary>
    SnapshotIdentityChanged,
}

/// <summary>
/// 从 Stardew 公共对象读取出的已加载栈事实快照。
/// </summary>
/// <param name="HasTemporaryDialogue">目标 NPC 当前是否有优先级更高的临时台词。</param>
/// <param name="HasLoadedStack">目标 NPC 是否在 <c>Game1.npcDialogues</c> 中拥有已加载栈。</param>
/// <param name="StackCount">已加载栈中的 Dialogue 数量。</param>
/// <param name="HasTopDialogue">是否能取得非空栈顶 Dialogue。</param>
/// <param name="SpeakerMatches">栈顶说话者是否仍是目标 NPC。</param>
/// <param name="TranslationKeyMatches">栈顶翻译键是否与捕获候选完全一致。</param>
/// <param name="IsSupportedDailySource">翻译键是否被精确分类为 ordinary/rainy daily source。</param>
/// <param name="CurrentDialogueIndex">Dialogue 当前行下标；安全形状必须仍为零。</param>
/// <param name="DialogueLineCount">Dialogue 包含的总行数。</param>
/// <param name="HasCurrentLine">安全下标处是否有非空 DialogueLine。</param>
/// <param name="HasQuestionBehavior">Dialogue 是否包含问题/回答行为。</param>
/// <param name="HasSideEffects">Dialogue 是否包含命令或其他副作用。</param>
/// <param name="HasOnFinishCallback">Dialogue 是否注册完成回调。</param>
/// <param name="RemoveOnNextMove">Dialogue 是否将在 NPC 下一次移动时移除。</param>
/// <param name="LoadedTextMatchesExpected">当前行文本是否与预期的比较文本完全一致。</param>
/// <param name="IsDialogueDisplayActive">游戏当前是否正在显示台词框。</param>
/// <param name="StackIdentityMatches">重新检查时，栈是否仍为捕获时的对象。</param>
/// <param name="DialogueIdentityMatches">重新检查时，Dialogue 是否仍为捕获时的对象。</param>
/// <param name="LineIdentityMatches">重新检查时，DialogueLine 是否仍为捕获时的对象。</param>
/// <remarks>
/// 该类型只携带布尔值和计数，不持有任何游戏对象引用，因此可以在纯单元测试中使用。
/// 对象引用仅由主线程适配层保存，并在重新读取事实时转换成三项身份比较结果。
/// </remarks>
public sealed record LoadedDialogueStackFacts(
    bool HasTemporaryDialogue,
    bool HasLoadedStack,
    int StackCount,
    bool HasTopDialogue,
    bool SpeakerMatches,
    bool TranslationKeyMatches,
    bool IsSupportedDailySource,
    int CurrentDialogueIndex,
    int DialogueLineCount,
    bool HasCurrentLine,
    bool HasQuestionBehavior,
    bool HasSideEffects,
    bool HasOnFinishCallback,
    bool RemoveOnNextMove,
    bool LoadedTextMatchesExpected,
    bool IsDialogueDisplayActive,
    bool StackIdentityMatches,
    bool DialogueIdentityMatches,
    bool LineIdentityMatches);

/// <summary>
/// 已加载栈门禁的确定性判断结果。
/// </summary>
/// <param name="IsEligible">是否允许调用方继续执行下一层比较或写入。</param>
/// <param name="ReasonCode">稳定的成功或拒绝原因。</param>
public sealed record LoadedDialogueStackDecision(
    bool IsEligible,
    LoadedDialogueStackReasonCode ReasonCode);

/// <summary>
/// 对已加载台词栈执行纯事实校验，不读取或修改任何 Stardew 状态。
/// </summary>
/// <remarks>
/// 安全边界必须由确定性代码掌控，不能由 Agent 决定。调用方应先在主线程采集事实，
/// 再使用本策略判断；即使返回 <see cref="LoadedDialogueStackReasonCode.Eligible"/>，
/// 写入前仍必须使用 <see cref="EvaluateCurrent"/> 对同一批对象与文本做第二次检查。
/// </remarks>
public static class LoadedDialogueStackPolicy
{
    /// <summary>
    /// 判断初次捕获的已加载栈是否属于唯一允许增强的安全形状。
    /// </summary>
    /// <param name="facts">主线程从公开游戏对象采集的事实。</param>
    /// <returns>确定性的允许或拒绝结果。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="facts"/> 为 <see langword="null"/>。</exception>
    public static LoadedDialogueStackDecision EvaluateCapture(LoadedDialogueStackFacts facts)
    {
        return Evaluate(facts, requireCapturedIdentity: false);
    }

    /// <summary>
    /// Agent 返回后重新判断当前栈，并额外要求三层对象身份仍与捕获时相同。
    /// </summary>
    /// <param name="facts">包含对象身份比较结果的当前事实。</param>
    /// <returns>确定性的允许或拒绝结果。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="facts"/> 为 <see langword="null"/>。</exception>
    public static LoadedDialogueStackDecision EvaluateCurrent(LoadedDialogueStackFacts facts)
    {
        return Evaluate(facts, requireCapturedIdentity: true);
    }

    /// <summary>
    /// 判断生命周期清理是否仍可安全尝试恢复捕获对象中的原文。
    /// </summary>
    /// <param name="facts">针对捕获对象重新读取的当前事实。</param>
    /// <returns>仅当栈、Dialogue 与行都仍是捕获对象时返回 <see langword="true"/>。</returns>
    /// <remarks>
    /// 恢复判定故意不复用增强门禁：清理可能发生在临时台词或 UI 已打开之后。
    /// 调用方仍必须通过比较并交换，只在当前文本恰好等于本 Mod 写入的增强文本时恢复原文，
    /// 从而不覆盖游戏或其他 Mod 在此之后做出的修改。
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="facts"/> 为 <see langword="null"/>。</exception>
    public static bool CanRestore(LoadedDialogueStackFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        return facts.HasLoadedStack
            && facts.StackCount == 1
            && facts.HasTopDialogue
            && facts.HasCurrentLine
            && facts.StackIdentityMatches
            && facts.DialogueIdentityMatches
            && facts.LineIdentityMatches;
    }

    /// <summary>
    /// 以稳定顺序执行共享门禁；先返回的原因代表最基础、最可操作的拒绝原因。
    /// </summary>
    private static LoadedDialogueStackDecision Evaluate(
        LoadedDialogueStackFacts facts,
        bool requireCapturedIdentity)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (facts.HasTemporaryDialogue)
        {
            return Rejected(LoadedDialogueStackReasonCode.TemporaryDialoguePresent);
        }

        if (!facts.HasLoadedStack)
        {
            return Rejected(LoadedDialogueStackReasonCode.NoLoadedStack);
        }

        if (facts.StackCount != 1)
        {
            return Rejected(LoadedDialogueStackReasonCode.UnsupportedStackShape);
        }

        if (!facts.HasTopDialogue)
        {
            return Rejected(LoadedDialogueStackReasonCode.MissingTopDialogue);
        }

        // 身份检查必须先于语义检查。对象已被替换时，后续字段即使碰巧相同也不能证明
        // 它仍是 Agent 请求对应的那一行。
        if (requireCapturedIdentity
            && (!facts.StackIdentityMatches
                || !facts.DialogueIdentityMatches
                || !facts.LineIdentityMatches))
        {
            return Rejected(LoadedDialogueStackReasonCode.SnapshotIdentityChanged);
        }

        if (!facts.SpeakerMatches)
        {
            return Rejected(LoadedDialogueStackReasonCode.SpeakerMismatch);
        }

        if (!facts.TranslationKeyMatches)
        {
            return Rejected(LoadedDialogueStackReasonCode.TranslationKeyMismatch);
        }

        if (!facts.IsSupportedDailySource)
        {
            return Rejected(LoadedDialogueStackReasonCode.UnsupportedDailySource);
        }

        if (facts.CurrentDialogueIndex != 0)
        {
            return Rejected(LoadedDialogueStackReasonCode.DialogueAlreadyAdvanced);
        }

        if (facts.DialogueLineCount != 1 || !facts.HasCurrentLine)
        {
            return Rejected(LoadedDialogueStackReasonCode.UnsupportedDialogueShape);
        }

        if (facts.HasQuestionBehavior)
        {
            return Rejected(LoadedDialogueStackReasonCode.QuestionDialogue);
        }

        if (facts.HasSideEffects)
        {
            return Rejected(LoadedDialogueStackReasonCode.DialogueSideEffects);
        }

        if (facts.HasOnFinishCallback)
        {
            return Rejected(LoadedDialogueStackReasonCode.DialogueOnFinishCallback);
        }

        if (facts.RemoveOnNextMove)
        {
            return Rejected(LoadedDialogueStackReasonCode.RemoveOnNextMove);
        }

        if (!facts.LoadedTextMatchesExpected)
        {
            return Rejected(LoadedDialogueStackReasonCode.LoadedTextMismatch);
        }

        if (facts.IsDialogueDisplayActive)
        {
            return Rejected(LoadedDialogueStackReasonCode.DialogueDisplayActive);
        }

        return new LoadedDialogueStackDecision(
            IsEligible: true,
            ReasonCode: LoadedDialogueStackReasonCode.Eligible);
    }

    /// <summary>
    /// 创建统一的拒绝结果，避免每个分支重复布尔语义。
    /// </summary>
    private static LoadedDialogueStackDecision Rejected(
        LoadedDialogueStackReasonCode reasonCode)
    {
        return new LoadedDialogueStackDecision(
            IsEligible: false,
            ReasonCode: reasonCode);
    }
}
