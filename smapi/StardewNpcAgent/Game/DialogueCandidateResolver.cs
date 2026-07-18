using StardewModdingAPI;
using StardewNpcAgent.Integration;
using StardewValley;

namespace StardewNpcAgent.Game;

/// <summary>
/// 游戏适配器解析候选时的阶段终态。
/// </summary>
public enum DialogueCandidateResolutionReasonCode
{
    Resolved,
    EligibilityRejected,
    UnsupportedNpc,
    NoOrdinaryDialogueFound,
    TranslationKeyRejected,
    SourceMissing,
    PublicSelectionMismatch,
    InsufficientStyleExamples,
    AuthoritativeSourceRejected,
}

/// <summary>
/// 指定候选解析器如何处理原版已经加载的普通日常台词栈。
/// </summary>
public enum DialogueCandidateResolutionMode
{
    /// <summary>
    /// 保守默认模式：一旦发现原版栈已加载就拒绝候选，保持现有资产注入路径的行为。
    /// </summary>
    RequireUnloaded,

    /// <summary>
    /// 允许继续解析已加载候选，但调用方随后必须通过专用栈策略和对象身份复核。
    /// </summary>
    AllowVerifiedLoadedStack,
}

/// <summary>
/// 已从当前游戏内容管线证明来源的普通日常对话候选。
/// </summary>
/// <param name="NpcId">稳定 NPC 内部 ID。</param>
/// <param name="SourceFamily">由 exact asset/key 独立分类的来源族。</param>
/// <param name="Locale">当前 SMAPI locale。</param>
/// <param name="AssetName">当前 NPC 的精确 dialogue asset。</param>
/// <param name="DialogueKey">原版 daily 选择链返回的精确字段 key。</param>
/// <param name="SourceText">当前已加载资产中的逐字符原文。</param>
/// <param name="SourceHash">原文的 UTF-8 SHA-256。</param>
/// <param name="StyleExamples">同 NPC、同 locale 的 2～5 条稳定安全样本。</param>
public sealed record DialogueCandidate(
    string NpcId,
    DialogueSourceFamily SourceFamily,
    string Locale,
    string AssetName,
    string DialogueKey,
    string SourceText,
    string SourceHash,
    IReadOnlyList<string> StyleExamples);

/// <summary>
/// 候选解析结果；资格失败时保留 policy reason，其他来源失败使用阶段 reason。
/// </summary>
/// <param name="IsResolved">是否可进入静态 enhancer。</param>
/// <param name="ReasonCode">候选解析阶段原因。</param>
/// <param name="EligibilityReasonCode">若由确定性 policy 拒绝，则为对应具体原因。</param>
/// <param name="Candidate">成功时非 null。</param>
public sealed record DialogueCandidateResolution(
    bool IsResolved,
    DialogueCandidateResolutionReasonCode ReasonCode,
    DialogueEligibilityReasonCode? EligibilityReasonCode,
    DialogueCandidate? Candidate);

/// <summary>
/// 使用 Stardew/SMAPI 公开 API 无副作用解析当天普通 daily 候选。
/// </summary>
/// <remarks>
/// 关键约束是绝不读取会懒加载日常对话栈的 NPC 属性。适配器只直接观察
/// <see cref="Game1.npcDialogues"/> 是否已经存在该 NPC 的非 null 栈；随后先从 raw asset
/// 以纯规则复现两个已验证 NPC 的 season/fallback key 选择并扫描 exact source。只有 raw
/// source 已证明无随机、问题、action 或多行 DSL，才调用公开
/// <see cref="NPC.tryToRetrieveDialogue"/> 做 key 一致性和解析形状校验。
/// </remarks>
public sealed class DialogueCandidateResolver
{
    private readonly IModHelper helper;

    /// <summary>
    /// 创建只依赖公开 content helper 的游戏适配器。
    /// </summary>
    /// <param name="helper">SMAPI helper，用于读取当前已应用其他 Mod 编辑的真实资产。</param>
    public DialogueCandidateResolver(IModHelper helper)
    {
        this.helper = helper ?? throw new ArgumentNullException(nameof(helper));
    }

    /// <summary>
    /// 为单个目标 NPC 解析普通 daily 候选；任一未知条件都返回失败而不改变游戏状态。
    /// </summary>
    /// <param name="npc">由游戏按稳定内部 ID 返回的 NPC 实例。</param>
    /// <param name="locale">当前内容 locale；英文应由调用方规范化为非空稳定值。</param>
    /// <param name="targetNpcIds">经配置清洗后的大小写敏感目标集合。</param>
    /// <param name="mode">
    /// 是否允许继续解析已加载的普通日常栈。默认值保持旧路径的保守拒绝行为。
    /// </param>
    /// <returns>安全候选或明确 fallback 原因。</returns>
    public DialogueCandidateResolution Resolve(
        NPC npc,
        string locale,
        IReadOnlyCollection<string> targetNpcIds,
        DialogueCandidateResolutionMode mode = DialogueCandidateResolutionMode.RequireUnloaded)
    {
        ArgumentNullException.ThrowIfNull(npc);
        ArgumentNullException.ThrowIfNull(targetNpcIds);

        string npcId = npc.Name;
        Farmer? player = Game1.player;
        GameLocation? currentLocation = npc.currentLocation;
        bool isKnownContext =
            player is not null
            && currentLocation is not null
            && !string.IsNullOrWhiteSpace(locale)
            && !string.IsNullOrWhiteSpace(Game1.currentSeason);
        bool isTargetNpc = targetNpcIds.Contains(npcId, StringComparer.Ordinal);

        // 临时台词与普通日常栈必须分别记录：显式已加载模式仅能放宽后者，绝不能覆盖前者。
        bool hasTemporaryDialogue = npc.TemporaryDialogue is not null;

        // Game1.npcDialogues 在新日流程中允许为 null。null 表示尚未加载，只有字典已存在、
        // 且该 NPC key 对应非 null 栈，才算原版选择已经发生。直接读取 public field 不会
        // 触发 NPC 上会懒加载当前对话的属性。
        bool hasLoadedDialogueStack = Game1.npcDialogues is not null
            && Game1.npcDialogues.TryGetValue(npcId, out Stack<Dialogue>? existingStack)
            && existingStack is not null;

        bool isFestivalDay = isKnownContext && Utility.isFestivalDay();
        bool isPassiveFestivalDay = isKnownContext && Utility.IsPassiveFestivalDay();
        bool isEventActive = isKnownContext && (Game1.eventUp || Game1.CurrentEvent is not null);
        // 普通雨使用原版 daily 选择链同样依赖的全局天气；不能因 NPC 此刻在室内就误判
        // 为晴天。绿雨则用公开的 location-context API，覆盖室内遮蔽下仍属于绿雨日的情况。
        bool isGreenRaining = isKnownContext && Game1.IsGreenRainingHere(currentLocation);
        Friendship? friendship = null;
        if (player is not null)
        {
            player.friendshipData.TryGetValue(npcId, out friendship);
        }

        DialogueEligibilityContext preflightContext = new()
        {
            IsKnownContext = isKnownContext,
            IsTargetNpc = isTargetNpc,
            // source 尚未解析；这两个值只让 preflight 先评估更早的世界与 NPC gate。
            IsNpcDialogueAsset = true,
            IsSupportedDailySource = true,
            IsFestivalDay = isFestivalDay,
            IsPassiveFestivalDay = isPassiveFestivalDay,
            IsEventActive = isEventActive,
            HasTemporaryDialogue = hasTemporaryDialogue,
            IsCurrentDialogueLoaded = hasLoadedDialogueStack,
            AllowVerifiedLoadedStack = mode
                == DialogueCandidateResolutionMode.AllowVerifiedLoadedStack,
            IsGreenRaining = isGreenRaining,
        };
        DialogueEligibilityDecision preflightDecision = DialogueEligibilityPolicy.Evaluate(preflightContext);
        if (!preflightDecision.IsEligible)
        {
            return RejectedByEligibility(preflightDecision.ReasonCode);
        }

        int heartLevel = friendship?.Points / 250 ?? 0;
        string dialogueAssetName = DialogueKeyClassifier.NormalizeAssetName(
            $"Characters/Dialogue/{npc.GetDialogueSheetName()}");
        Dictionary<string, string> rawDialogueAsset =
            helper.GameContent.Load<Dictionary<string, string>>(dialogueAssetName);

        OrdinaryDialogueKeySelectionResult rawSelection = OrdinaryDialogueKeyResolver.Select(
            new OrdinaryDialogueKeySelectionRequest
            {
                NpcId = npcId,
                DialogueEntries = rawDialogueAsset,
                Season = Game1.currentSeason,
                DayOfMonth = Game1.dayOfMonth,
                ShortDayName = Game1.shortDayNameFromDayOfSeason(Game1.dayOfMonth),
                Year = Game1.year,
                HeartLevel = heartLevel,
                SpouseId = player?.spouse,
                HasCurrentOrPendingRoommate = player?.hasCurrentOrPendingRoommate() == true,
            });
        if (!rawSelection.IsSelected)
        {
            DialogueCandidateResolutionReasonCode reasonCode = rawSelection.ReasonCode
                == OrdinaryDialogueKeySelectionReasonCode.UnsupportedNpc
                ? DialogueCandidateResolutionReasonCode.UnsupportedNpc
                : DialogueCandidateResolutionReasonCode.NoOrdinaryDialogueFound;
            return Rejected(reasonCode);
        }

        if (rawSelection.DialogueKey is null || rawSelection.SourceText is null)
        {
            return Rejected(DialogueCandidateResolutionReasonCode.SourceMissing);
        }

        DialogueKeyClassification keyClassification = DialogueKeyClassifier.ClassifyTranslationKey(
            $"{dialogueAssetName}:{rawSelection.DialogueKey}",
            npcId);
        if (!keyClassification.IsOrdinaryDaily || keyClassification.ParsedKey is null)
        {
            return Rejected(DialogueCandidateResolutionReasonCode.TranslationKeyRejected);
        }

        ParsedDialogueKey parsedKey = keyClassification.ParsedKey;
        string sourceText = rawSelection.SourceText;

        // 原版在普通日常栈加载后还会检查 active dialogue event。若任一 pending key 存在于
        // 当前 NPC 资产中，就证明玩家当前应先看到特殊事件文本，静态 cache 必须让路。
        bool hasPendingActiveDialogueEvent = player is not null
            && player.activeDialogueEvents.Keys.Any(rawDialogueAsset.ContainsKey);

        DialogueControlScanResult controlScan = DialogueControlCommandScanner.Scan(sourceText);
        bool isApprovedDailyTemplate = DialogueTemplatePolicy.TryParse(sourceText, out _);
        DialogueEligibilityContext rawSourceContext = new()
        {
            IsKnownContext = true,
            IsTargetNpc = true,
            IsNpcDialogueAsset = true,
            IsSupportedDailySource = true,
            IsQuestionDialogue = controlScan.HasQuestionSyntax,
            IsGiftDialogue = false,
            IsQuestDialogue = false,
            HasPendingActiveDialogueEvent = hasPendingActiveDialogueEvent,
            IsCurrentDialogueLoaded = false,
            IsGreenRaining = false,
            // 通用 scanner 仍把 @ 视为危险；只有本 exact daily source 显式通过 typed
            // template policy 时，才局部豁免 scanner 的 HasAnyControlSyntax 总位。
            HasDangerousControlCommand = !isApprovedDailyTemplate,
            HasSideEffects = controlScan.HasSideEffectSyntax,
            HasMultipleDialogueLines = controlScan.HasMultipleDialogueSyntax,
        };
        DialogueEligibilityDecision rawSourceDecision = DialogueEligibilityPolicy.Evaluate(rawSourceContext);
        if (!rawSourceDecision.IsEligible)
        {
            return RejectedByEligibility(rawSourceDecision.ReasonCode);
        }

        // 到这里 exact raw source 已证明不含随机、问题、action、物品领取或多行 DSL，才允许
        // 公开 API 构造 Dialogue。构造结果只做一致性和 shape 校验，不读取或写入日常栈。
        Dialogue? selectedDialogue = npc.tryToRetrieveDialogue($"{Game1.currentSeason}_", heartLevel)
            ?? npc.tryToRetrieveDialogue(string.Empty, heartLevel);
        if (selectedDialogue is null)
        {
            return Rejected(DialogueCandidateResolutionReasonCode.PublicSelectionMismatch);
        }

        DialogueKeyClassification publicKeyClassification = DialogueKeyClassifier.ClassifyTranslationKey(
            selectedDialogue.TranslationKey,
            npcId);
        if (!publicKeyClassification.IsOrdinaryDaily
            || publicKeyClassification.ParsedKey is null
            || !string.Equals(
                publicKeyClassification.ParsedKey.AssetName,
                parsedKey.AssetName,
                StringComparison.Ordinal)
            || !string.Equals(
                publicKeyClassification.ParsedKey.DialogueKey,
                parsedKey.DialogueKey,
                StringComparison.Ordinal))
        {
            return Rejected(DialogueCandidateResolutionReasonCode.PublicSelectionMismatch);
        }

        DialogueEligibilityContext parsedShapeContext = new()
        {
            IsKnownContext = true,
            IsTargetNpc = true,
            IsNpcDialogueAsset = true,
            IsSupportedDailySource = true,
            IsQuestionDialogue = selectedDialogue.answerQuestionBehavior is not null,
            HasSideEffects = selectedDialogue.dialogues.Any(line => line.SideEffects is not null),
            HasMultipleDialogueLines = selectedDialogue.dialogues.Count != 1,
            HasOnFinishCallback = selectedDialogue.onFinish is not null,
            RemoveOnNextMove = selectedDialogue.removeOnNextMove,
        };
        DialogueEligibilityDecision parsedShapeDecision = DialogueEligibilityPolicy.Evaluate(
            parsedShapeContext);
        if (!parsedShapeDecision.IsEligible)
        {
            return RejectedByEligibility(parsedShapeDecision.ReasonCode);
        }

        StyleExampleSelectionResult styleSelection = StyleExampleSelector.Select(
            new DialogueStyleSelectionRequest
            {
                NpcId = npcId,
                Locale = locale,
                CurrentSeason = Game1.currentSeason,
                CurrentHeartLevel = Math.Clamp(heartLevel, 0, 10),
                SourceKey = parsedKey.DialogueKey,
                SourceText = sourceText,
                DialogueEntries = rawDialogueAsset,
            });
        if (!styleSelection.IsSuccessful)
        {
            return Rejected(DialogueCandidateResolutionReasonCode.InsufficientStyleExamples);
        }

        DialogueCandidate candidate = new(
            npcId,
            DialogueSourceFamily.OrdinaryDaily,
            locale,
            parsedKey.AssetName,
            parsedKey.DialogueKey,
            sourceText,
            SourceDialogueHasher.Compute(sourceText),
            styleSelection.Examples);
        return new DialogueCandidateResolution(
            true,
            DialogueCandidateResolutionReasonCode.Resolved,
            null,
            candidate);
    }

    /// <summary>
    /// 从游戏已选择的 authoritative loaded snapshot 解析 ordinary/rainy 候选。
    /// </summary>
    /// <remarks>
    /// 本方法绝不调用 <see cref="OrdinaryDialogueKeyResolver"/> 或
    /// <see cref="NPC.tryToRetrieveDialogue(string, int)"/>。actual TranslationKey 已由原版选择，
    /// 这里仅加载已应用其他 Mod 编辑后的 exact source asset，并用目标 NPC ordinary asset
    /// 检查 pending event 与 style samples。
    /// </remarks>
    internal DialogueCandidateResolution ResolveAuthoritativeLoaded(
        NPC npc,
        string locale,
        IReadOnlyCollection<string> targetNpcIds,
        LoadedDialogueSourceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(npc);
        ArgumentNullException.ThrowIfNull(targetNpcIds);
        ArgumentNullException.ThrowIfNull(snapshot);

        string npcId = npc.Name;
        Farmer? player = Game1.player;
        GameLocation? currentLocation = npc.currentLocation;
        bool isKnownContext = player is not null
            && currentLocation is not null
            && !string.IsNullOrEmpty(locale)
            && !string.IsNullOrEmpty(Game1.currentSeason);
        DialogueEligibilityDecision preflight = DialogueEligibilityPolicy.Evaluate(
            new DialogueEligibilityContext
            {
                IsKnownContext = isKnownContext,
                IsTargetNpc = targetNpcIds.Contains(npcId, StringComparer.Ordinal),
                IsNpcDialogueAsset = true,
                IsSupportedDailySource = snapshot.SourceIdentity.NpcId == npcId,
                IsFestivalDay = isKnownContext && Utility.isFestivalDay(),
                IsPassiveFestivalDay = isKnownContext && Utility.IsPassiveFestivalDay(),
                IsEventActive = isKnownContext && (Game1.eventUp || Game1.CurrentEvent is not null),
                HasTemporaryDialogue = npc.TemporaryDialogue is not null,
                IsCurrentDialogueLoaded = true,
                AllowVerifiedLoadedStack = true,
                IsGreenRaining = isKnownContext && Game1.IsGreenRainingHere(currentLocation),
            });
        if (!preflight.IsEligible)
        {
            return RejectedByEligibility(preflight.ReasonCode);
        }

        if (!string.Equals(snapshot.Locale, locale, StringComparison.Ordinal))
        {
            return Rejected(DialogueCandidateResolutionReasonCode.AuthoritativeSourceRejected);
        }

        IReadOnlyDictionary<string, string> sourceDialogueAsset =
            helper.GameContent.Load<Dictionary<string, string>>(
                snapshot.SourceIdentity.AssetName);
        string npcOrdinaryAssetName = $"Characters/Dialogue/{npcId}";
        IReadOnlyDictionary<string, string> npcOrdinaryDialogueAsset =
            helper.GameContent.Load<Dictionary<string, string>>(npcOrdinaryAssetName);
        IReadOnlyCollection<string> pendingDialogueEventKeys =
            player!.activeDialogueEvents.Keys.ToArray();
        int heartLevel = 0;
        if (player.friendshipData.TryGetValue(npcId, out Friendship? friendship))
        {
            heartLevel = friendship.Points / 250;
        }

        AuthoritativeDialogueSourceResolution sourceResolution =
            AuthoritativeDialogueSourceResolver.Resolve(
                snapshot,
                sourceDialogueAsset,
                npcOrdinaryDialogueAsset,
                pendingDialogueEventKeys,
                Game1.currentSeason,
                heartLevel);
        if (!sourceResolution.IsResolved || sourceResolution.Source is null)
        {
            return sourceResolution.ReasonCode switch
            {
                AuthoritativeDialogueSourceReasonCode.PendingActiveDialogueEvent =>
                    RejectedByEligibility(
                        DialogueEligibilityReasonCode.PendingActiveDialogueEvent),
                AuthoritativeDialogueSourceReasonCode.InsufficientStyleExamples =>
                    Rejected(
                        DialogueCandidateResolutionReasonCode.InsufficientStyleExamples),
                _ => Rejected(
                    DialogueCandidateResolutionReasonCode.AuthoritativeSourceRejected),
            };
        }

        AuthoritativeDialogueSource source = sourceResolution.Source;
        DialogueCandidate candidate = new(
            source.NpcId,
            source.Family,
            source.Locale,
            source.AssetName,
            source.DialogueKey,
            source.SourceText,
            source.SourceHash,
            source.StyleExamples);
        return new DialogueCandidateResolution(
            IsResolved: true,
            DialogueCandidateResolutionReasonCode.Resolved,
            EligibilityReasonCode: null,
            candidate);
    }

    /// <summary>
    /// Provider 返回后复核 authoritative candidate 的 exact raw asset identity。
    /// </summary>
    /// <remarks>
    /// 对象/line 身份由 captured target access 同步复核；此处只重读 exact asset/key/text/hash
    /// 与 ordinary pending event gate，且不会重新运行日期、RNG 或 public Dialogue 选择链。
    /// </remarks>
    internal bool RevalidateAuthoritativeLoaded(NPC npc, DialogueCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(npc);
        ArgumentNullException.ThrowIfNull(candidate);

        if (!string.Equals(npc.Name, candidate.NpcId, StringComparison.Ordinal)
            || !DialogueSourceClassifier.MatchesIdentity(
                candidate.SourceFamily,
                candidate.NpcId,
                candidate.AssetName,
                candidate.DialogueKey))
        {
            return false;
        }

        IReadOnlyDictionary<string, string> sourceDialogueAsset =
            helper.GameContent.Load<Dictionary<string, string>>(candidate.AssetName);
        if (!sourceDialogueAsset.TryGetValue(candidate.DialogueKey, out string? currentSource)
            || !string.Equals(currentSource, candidate.SourceText, StringComparison.Ordinal)
            || !string.Equals(
                SourceDialogueHasher.Compute(currentSource),
                candidate.SourceHash,
                StringComparison.Ordinal)
            || !DialogueTemplatePolicy.TryParse(currentSource, out _))
        {
            return false;
        }

        Farmer? player = Game1.player;
        IReadOnlyDictionary<string, string> npcOrdinaryDialogueAsset =
            helper.GameContent.Load<Dictionary<string, string>>(
                $"Characters/Dialogue/{candidate.NpcId}");
        return player is not null
            && !player.activeDialogueEvents.Keys.Any(npcOrdinaryDialogueAsset.ContainsKey);
    }

    /// <summary>
    /// 包装确定性资格拒绝，保留 policy 的具体 reason 供日志和手工测试观察。
    /// </summary>
    private static DialogueCandidateResolution RejectedByEligibility(
        DialogueEligibilityReasonCode reasonCode)
    {
        return new DialogueCandidateResolution(
            false,
            DialogueCandidateResolutionReasonCode.EligibilityRejected,
            reasonCode,
            null);
    }

    /// <summary>
    /// 包装来源解析失败；这类失败没有伪造 eligibility reason。
    /// </summary>
    private static DialogueCandidateResolution Rejected(
        DialogueCandidateResolutionReasonCode reasonCode)
    {
        return new DialogueCandidateResolution(false, reasonCode, null, null);
    }
}
