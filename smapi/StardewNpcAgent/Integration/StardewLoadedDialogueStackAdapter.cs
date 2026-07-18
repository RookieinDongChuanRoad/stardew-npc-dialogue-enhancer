using StardewNpcAgent.Game;
using StardewNpcAgent.Infrastructure.Storage;
using StardewValley;

namespace StardewNpcAgent.Integration;

/// <summary>
/// 从 Stardew 公开的已加载台词字典捕获受控栈顶目标。
/// </summary>
/// <remarks>
/// 本适配器是直接注入路线中唯一持有 Stardew 对象的模块。它只读取
/// <see cref="Game1.npcDialogues"/>、<see cref="Dialogue.dialogues"/> 和其他公开状态，
/// 不访问会触发 lazy load 的 NPC 对话属性，也不重建、推进或重置原版 Dialogue。
/// 唯一写操作由私有访问端口的 <see cref="DialogueLine.Text"/> compare-and-swap 完成。
/// </remarks>
internal static class StardewLoadedDialogueStackAdapter
{
    /// <summary>
    /// 无副作用判断原版是否已经为目标 NPC 建立非 null 日常台词栈。
    /// </summary>
    /// <param name="npc">当前 world 中按稳定内部 ID 获取的 NPC。</param>
    /// <returns>公开字典中是否存在该 NPC 的非 null 栈；空栈也算“已加载但形状异常”。</returns>
    public static bool HasLoadedStack(NPC npc)
    {
        ArgumentNullException.ThrowIfNull(npc);
        return TryGetLoadedStack(npc.Name, out _);
    }

    /// <summary>
    /// 捕获实际栈、栈顶 Dialogue 与首行对象引用，并立即通过纯策略判断安全形状。
    /// </summary>
    /// <param name="npc">候选对应的当前 NPC 对象。</param>
    /// <param name="candidate">已由 raw source、公开选择 API 与资格策略证明的候选。</param>
    /// <param name="gameDayIndex">当前绝对游戏日，写入完整 cache key。</param>
    /// <param name="locale">当前 SMAPI locale，必须与候选逐字符一致。</param>
    /// <param name="isDialogueDisplayActive">当前是否已有原生对话 UI。</param>
    /// <returns>成功时携带对象访问 token；任一不确定条件只返回稳定拒绝原因。</returns>
    public static LoadedDialogueTargetResolution Capture(
        NPC npc,
        DialogueCandidate candidate,
        int gameDayIndex,
        string locale,
        bool isDialogueDisplayActive)
    {
        ArgumentNullException.ThrowIfNull(npc);
        ArgumentNullException.ThrowIfNull(candidate);

        LoadedDialogueSourceCaptureResolution sourceCapture = CaptureSourceSnapshot(
            npc,
            candidate.NpcId,
            locale,
            isDialogueDisplayActive);
        if (!sourceCapture.IsCaptured || sourceCapture.Handle is null)
        {
            return new LoadedDialogueTargetResolution(
                IsCaptured: false,
                ReasonCode: sourceCapture.ReasonCode,
                Target: null);
        }

        return BindCandidate(sourceCapture.Handle, candidate, gameDayIndex, locale);
    }

    /// <summary>
    /// 在任何 pure candidate 解析之前，从 <c>Game1.npcDialogues</c> 捕获 actual source snapshot。
    /// </summary>
    /// <remarks>
    /// 方法只读取一次 stack/dialogue/line 引用，并把同一组三层对象同时交给快照和 access token。
    /// source classifier 或 shape policy 失败时 <see cref="LoadedDialogueSourceCaptureResolution.Handle"/>
    /// 为 null，调用方无法获得半可信对象引用。
    /// </remarks>
    public static LoadedDialogueSourceCaptureResolution CaptureSourceSnapshot(
        NPC npc,
        string expectedNpcId,
        string locale,
        bool isDialogueDisplayActive)
    {
        ArgumentNullException.ThrowIfNull(npc);

        bool hasLoadedStack = TryGetLoadedStack(npc.Name, out Stack<Dialogue>? stack);
        Dialogue? dialogue = PeekOrNull(stack);
        DialogueLine? line = GetFirstLineOrNull(dialogue);
        string actualTranslationKey = dialogue?.TranslationKey ?? string.Empty;
        string sourceText = line?.Text ?? string.Empty;
        DialogueSourceIdentity? sourceIdentity = DialogueSourceClassifier.ClassifyTranslationKey(
            actualTranslationKey,
            expectedNpcId);
        LoadedDialogueStackFacts captureFacts = CreateFacts(
            npc,
            expectedNpcId,
            sourceIdentity,
            sourceText,
            capturedLocale: locale,
            hasLoadedStack,
            stack,
            dialogue,
            line,
            capturedStack: stack,
            capturedDialogue: dialogue,
            capturedLine: line,
            isDialogueDisplayActive);
        LoadedDialogueSourceSnapshotResolution resolution =
            LoadedDialogueSourceSnapshotCapture.Capture(
                expectedNpcId,
                actualTranslationKey,
                sourceText,
                locale,
                captureFacts,
                stack!,
                dialogue!,
                line!);
        if (!resolution.IsCaptured || resolution.Snapshot is null || sourceIdentity is null)
        {
            return new LoadedDialogueSourceCaptureResolution(
                IsCaptured: false,
                ReasonCode: resolution.ReasonCode,
                Handle: null);
        }

        StardewLoadedDialogueTargetAccess access = new(
            npc,
            sourceIdentity,
            locale,
            sourceText,
            stack,
            dialogue,
            line);
        return new LoadedDialogueSourceCaptureResolution(
            IsCaptured: true,
            ReasonCode: LoadedDialogueStackReasonCode.Eligible,
            Handle: new LoadedDialogueSourceSnapshotHandle(resolution.Snapshot, access));
    }

    /// <summary>
    /// 把 resolver 结果绑定回同一个初始 snapshot/access token，不重新读取游戏对象。
    /// </summary>
    public static LoadedDialogueTargetResolution BindCandidate(
        LoadedDialogueSourceSnapshotHandle handle,
        DialogueCandidate candidate,
        int gameDayIndex,
        string locale)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(candidate);

        LoadedDialogueSourceSnapshot snapshot = handle.Snapshot;
        DialogueSourceIdentity sourceIdentity = snapshot.SourceIdentity;
        if (!string.Equals(candidate.NpcId, sourceIdentity.NpcId, StringComparison.Ordinal)
            || candidate.SourceFamily != sourceIdentity.Family
            || !string.Equals(candidate.Locale, snapshot.Locale, StringComparison.Ordinal)
            || !string.Equals(locale, snapshot.Locale, StringComparison.Ordinal)
            || !string.Equals(candidate.AssetName, sourceIdentity.AssetName, StringComparison.Ordinal)
            || !string.Equals(candidate.DialogueKey, sourceIdentity.DialogueKey, StringComparison.Ordinal)
            || !string.Equals(candidate.SourceText, snapshot.SourceText, StringComparison.Ordinal)
            || !string.Equals(
                candidate.SourceHash,
                SourceDialogueHasher.Compute(snapshot.SourceText),
                StringComparison.Ordinal))
        {
            return new LoadedDialogueTargetResolution(
                IsCaptured: false,
                ReasonCode: LoadedDialogueStackReasonCode.TranslationKeyMismatch,
                Target: null);
        }

        DailyDialogueCacheKey key = new(
            gameDayIndex,
            locale,
            candidate.NpcId,
            candidate.AssetName,
            candidate.DialogueKey);
        return new LoadedDialogueTargetResolution(
            IsCaptured: true,
            ReasonCode: LoadedDialogueStackReasonCode.Eligible,
            Target: new LoadedDialogueTarget(key, candidate, handle.Access));
    }

    /// <summary>
    /// 直接读取公开字典；不通过 NPC getter 触发原版选择或修改加载时机。
    /// </summary>
    private static bool TryGetLoadedStack(string npcId, out Stack<Dialogue>? stack)
    {
        stack = null;
        return Game1.npcDialogues is not null
            && Game1.npcDialogues.TryGetValue(npcId, out stack)
            && stack is not null;
    }

    /// <summary>
    /// 安全读取栈顶；空栈返回 null，由策略产生明确形状拒绝而不是抛出异常。
    /// </summary>
    private static Dialogue? PeekOrNull(Stack<Dialogue>? stack)
    {
        return stack is not null && stack.Count > 0
            ? stack.Peek()
            : null;
    }

    /// <summary>
    /// 安全读取首行引用；是否恰为单行以及当前下标是否仍为零交给纯策略判断。
    /// </summary>
    private static DialogueLine? GetFirstLineOrNull(Dialogue? dialogue)
    {
        return dialogue is not null && dialogue.dialogues.Count > 0
            ? dialogue.dialogues[0]
            : null;
    }

    /// <summary>
    /// 从同一次对象读取构造 source-oriented facts；用于 capture 与后续 current CAS 复核。
    /// </summary>
    private static LoadedDialogueStackFacts CreateFacts(
        NPC npc,
        string expectedNpcId,
        DialogueSourceIdentity? expectedSourceIdentity,
        string expectedSourceText,
        string capturedLocale,
        bool hasLoadedStack,
        Stack<Dialogue>? currentStack,
        Dialogue? currentDialogue,
        DialogueLine? currentLine,
        Stack<Dialogue>? capturedStack,
        Dialogue? capturedDialogue,
        DialogueLine? capturedLine,
        bool isDialogueDisplayActive)
    {
        DialogueSourceIdentity? currentSourceIdentity =
            DialogueSourceClassifier.ClassifyTranslationKey(
                currentDialogue?.TranslationKey,
                expectedNpcId);
        return new LoadedDialogueStackFacts(
            HasTemporaryDialogue: npc.TemporaryDialogue is not null,
            HasLoadedStack: hasLoadedStack,
            StackCount: currentStack?.Count ?? 0,
            HasTopDialogue: currentDialogue is not null,
            SpeakerMatches: string.Equals(
                currentDialogue?.speaker?.Name,
                expectedNpcId,
                StringComparison.Ordinal),
            TranslationKeyMatches: expectedSourceIdentity is not null
                && currentSourceIdentity == expectedSourceIdentity
                && !string.IsNullOrEmpty(capturedLocale),
            IsSupportedDailySource: currentSourceIdentity is not null,
            CurrentDialogueIndex: currentDialogue?.currentDialogueIndex ?? -1,
            DialogueLineCount: currentDialogue?.dialogues.Count ?? 0,
            HasCurrentLine: currentLine is not null,
            HasQuestionBehavior: currentDialogue?.answerQuestionBehavior is not null,
            HasSideEffects: currentDialogue?.dialogues.Any(
                dialogueLine => dialogueLine.SideEffects is not null) == true,
            HasOnFinishCallback: currentDialogue?.onFinish is not null,
            RemoveOnNextMove: currentDialogue?.removeOnNextMove == true,
            LoadedTextMatchesExpected: string.Equals(
                currentLine?.Text,
                expectedSourceText,
                StringComparison.Ordinal),
            IsDialogueDisplayActive: isDialogueDisplayActive,
            StackIdentityMatches: ReferenceEquals(currentStack, capturedStack),
            DialogueIdentityMatches: ReferenceEquals(currentDialogue, capturedDialogue),
            LineIdentityMatches: ReferenceEquals(currentLine, capturedLine));
    }

    /// <summary>
    /// 持有捕获对象引用，并在每次提交/恢复前从公开字典重新构建事实。
    /// </summary>
    /// <remarks>
    /// 三层 <see cref="ReferenceEquals(object?, object?)"/> 检查防止“字段值碰巧相同的新对象”
    /// 被误认为原 Agent 请求对应的对象。该实例不离开 SMAPI 进程，也不会被序列化到后端。
    /// </remarks>
    private sealed class StardewLoadedDialogueTargetAccess : ILoadedDialogueTargetAccess
    {
        private readonly NPC npc;
        private readonly DialogueSourceIdentity sourceIdentity;
        private readonly string capturedLocale;
        private readonly string capturedSourceText;
        private readonly Stack<Dialogue>? capturedStack;
        private readonly Dialogue? capturedDialogue;
        private readonly DialogueLine? capturedLine;

        /// <summary>
        /// 保存 DayStarted 捕获的对象引用与 raw candidate 身份。
        /// </summary>
        public StardewLoadedDialogueTargetAccess(
            NPC npc,
            DialogueSourceIdentity sourceIdentity,
            string capturedLocale,
            string capturedSourceText,
            Stack<Dialogue>? capturedStack,
            Dialogue? capturedDialogue,
            DialogueLine? capturedLine)
        {
            this.npc = npc;
            this.sourceIdentity = sourceIdentity;
            this.capturedLocale = capturedLocale;
            this.capturedSourceText = capturedSourceText;
            this.capturedStack = capturedStack;
            this.capturedDialogue = capturedDialogue;
            this.capturedLine = capturedLine;
        }

        /// <summary>
        /// 从当前公开字典重读栈、栈顶和首行，并与捕获引用逐层比较。
        /// </summary>
        /// <param name="isDialogueDisplayActive">调用方观察到的当前 UI 状态。</param>
        /// <returns>供纯策略判断的完整事实快照。</returns>
        public LoadedDialogueStackFacts ReadCurrentFacts(bool isDialogueDisplayActive)
        {
            bool hasLoadedStack = TryGetLoadedStack(npc.Name, out Stack<Dialogue>? currentStack);
            Dialogue? currentDialogue = PeekOrNull(currentStack);
            DialogueLine? currentLine = GetFirstLineOrNull(currentDialogue);
            return CreateFacts(
                npc,
                sourceIdentity.NpcId,
                sourceIdentity,
                capturedSourceText,
                capturedLocale,
                hasLoadedStack,
                currentStack,
                currentDialogue,
                currentLine,
                capturedStack,
                capturedDialogue,
                capturedLine,
                isDialogueDisplayActive);
        }

        /// <summary>
        /// 对捕获行执行逐字符条件替换；不改变栈、Dialogue、speaker、key、下标或回调。
        /// </summary>
        /// <param name="expectedCurrentText">只有当前 Text 精确等于该值才允许写入。</param>
        /// <param name="replacementText">通过全部上层门禁后的原文或增强文本。</param>
        /// <returns>捕获行仍存在且文本比较成功时为 true；否则不写并返回 false。</returns>
        public bool TryReplaceText(string expectedCurrentText, string replacementText)
        {
            if (capturedLine is null
                || !string.Equals(capturedLine.Text, expectedCurrentText, StringComparison.Ordinal))
            {
                return false;
            }

            capturedLine.Text = replacementText;
            return true;
        }
    }
}

/// <summary>
/// 已加载目标捕获的确定性结果。
/// </summary>
/// <param name="IsCaptured">是否已经捕获唯一安全的栈顶行。</param>
/// <param name="ReasonCode">成功或拒绝的稳定栈策略原因。</param>
/// <param name="Target">成功时非 null；失败时绝不暴露半可信对象 token。</param>
internal sealed record LoadedDialogueTargetResolution(
    bool IsCaptured,
    LoadedDialogueStackReasonCode ReasonCode,
    LoadedDialogueTarget? Target);

/// <summary>
/// 同一初始 source snapshot 与其三层对象访问端口的不可变绑定。
/// </summary>
/// <remarks>
/// resolver 只能读取 <see cref="Snapshot"/> 的值事实；最终 bind 复用这里的
/// <see cref="Access"/>，不会重新从 <c>Game1.npcDialogues</c> 捕获对象。
/// </remarks>
internal sealed record LoadedDialogueSourceSnapshotHandle(
    LoadedDialogueSourceSnapshot Snapshot,
    ILoadedDialogueTargetAccess Access);

/// <summary>
/// source-first 捕获结果；失败时不返回 snapshot 或对象访问 token。
/// </summary>
internal sealed record LoadedDialogueSourceCaptureResolution(
    bool IsCaptured,
    LoadedDialogueStackReasonCode ReasonCode,
    LoadedDialogueSourceSnapshotHandle? Handle);
