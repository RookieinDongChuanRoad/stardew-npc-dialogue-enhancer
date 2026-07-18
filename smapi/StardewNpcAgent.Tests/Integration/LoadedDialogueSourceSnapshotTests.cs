using StardewNpcAgent.Game;
using StardewNpcAgent.Integration;

namespace StardewNpcAgent.Tests.Integration;

/// <summary>
/// 冻结 authoritative loaded source 快照的原子性：失败时不得泄露对象 token。
/// </summary>
public sealed class LoadedDialogueSourceSnapshotTests
{
    /// <summary>
    /// ordinary/rainy exact source 成功时必须保留 actual key、raw line、locale 和同一组三层 token。
    /// </summary>
    [Theory]
    [InlineData("Characters/Dialogue/Abigail:fall_Mon", "OrdinaryDaily")]
    [InlineData("Characters\\Dialogue\\rainy:Abigail", "RainyDaily")]
    public void Capture_ExactSafeSourceReturnsOneAtomicSnapshot(
        string translationKey,
        string expectedFamily)
    {
        object stackToken = new();
        object dialogueToken = new();
        object lineToken = new();

        LoadedDialogueSourceSnapshotResolution resolution = Capture(
            translationKey,
            CreateEligibleFacts(),
            stackToken,
            dialogueToken,
            lineToken);

        Assert.True(resolution.IsCaptured);
        LoadedDialogueSourceSnapshot snapshot = Assert.IsType<LoadedDialogueSourceSnapshot>(
            resolution.Snapshot);
        Assert.Equal(expectedFamily, snapshot.SourceIdentity.Family.ToString());
        Assert.Equal("Abigail", snapshot.SourceIdentity.NpcId);
        Assert.Equal(translationKey, snapshot.TranslationKey);
        Assert.Equal("原版逐字符行。", snapshot.SourceText);
        Assert.Equal("zh-CN", snapshot.Locale);
        Assert.Same(stackToken, snapshot.StackToken);
        Assert.Same(dialogueToken, snapshot.DialogueToken);
        Assert.Same(lineToken, snapshot.LineToken);
    }

    /// <summary>
    /// special source 或 shape/行为风险任一存在时，结果只能携带 reason，不能暴露半可信 token。
    /// </summary>
    [Theory]
    [MemberData(nameof(GetRejectedCaptures))]
    public void Capture_UnsupportedSourceOrUnsafeShapeNeverReturnsTokens(
        string translationKey,
        Func<LoadedDialogueStackFacts, LoadedDialogueStackFacts> mutate,
        string expectedReason)
    {
        LoadedDialogueSourceSnapshotResolution resolution = Capture(
            translationKey,
            mutate(CreateEligibleFacts()),
            new object(),
            new object(),
            new object());

        Assert.False(resolution.IsCaptured);
        Assert.Equal(expectedReason, resolution.ReasonCode.ToString());
        Assert.Null(resolution.Snapshot);
    }

    public static IEnumerable<object[]> GetRejectedCaptures()
    {
        yield return Case(
            "Characters/Dialogue/MarriageDialogueAbigail:Mon",
            facts => facts,
            "UnsupportedDailySource");
        yield return Case(
            "Characters/Dialogue/Abigail:fall_Mon",
            facts => facts with { SpeakerMatches = false },
            "SpeakerMismatch");
        yield return Case(
            "Characters/Dialogue/Abigail:fall_Mon",
            facts => facts with { CurrentDialogueIndex = 1 },
            "DialogueAlreadyAdvanced");
        yield return Case(
            "Characters/Dialogue/Abigail:fall_Mon",
            facts => facts with { DialogueLineCount = 2 },
            "UnsupportedDialogueShape");
        yield return Case(
            "Characters/Dialogue/Abigail:fall_Mon",
            facts => facts with { HasQuestionBehavior = true },
            "QuestionDialogue");
        yield return Case(
            "Characters/Dialogue/Abigail:fall_Mon",
            facts => facts with { HasSideEffects = true },
            "DialogueSideEffects");
        yield return Case(
            "Characters/Dialogue/Abigail:fall_Mon",
            facts => facts with { HasOnFinishCallback = true },
            "DialogueOnFinishCallback");
        yield return Case(
            "Characters/Dialogue/Abigail:fall_Mon",
            facts => facts with { RemoveOnNextMove = true },
            "RemoveOnNextMove");
    }

    /// <summary>
    /// rainy 的 source 必须从共享 rainy asset 读取，但风格样本只能来自目标 NPC ordinary asset。
    /// </summary>
    [Fact]
    public void Resolve_RainyUsesExactSharedSourceAndNpcOrdinaryStyleAsset()
    {
        LoadedDialogueSourceSnapshot snapshot = CreateSnapshot(
            "Characters/Dialogue/rainy:Abigail",
            "雨天原文。");
        Dictionary<string, string> sourceAsset = new(StringComparer.Ordinal)
        {
            ["Abigail"] = "雨天原文。",
            ["Sebastian"] = "不能成为 Abigail 的样本。",
        };
        Dictionary<string, string> ordinaryAsset = CreateOrdinaryAsset();

        AuthoritativeDialogueSourceResolution resolution = AuthoritativeDialogueSourceResolver.Resolve(
            snapshot,
            sourceAsset,
            ordinaryAsset,
            Array.Empty<string>(),
            "fall",
            4);

        Assert.True(resolution.IsResolved);
        AuthoritativeDialogueSource source = Assert.IsType<AuthoritativeDialogueSource>(
            resolution.Source);
        Assert.Equal(DialogueSourceFamily.RainyDaily, source.Family);
        Assert.Equal("Characters/Dialogue/rainy", source.AssetName);
        Assert.Equal("Abigail", source.DialogueKey);
        IReadOnlyList<string> examples = source.StyleExamples;
        Assert.Equal(5, examples.Count);
        Assert.DoesNotContain("不能成为 Abigail 的样本。", examples);
        Assert.All(examples, example => Assert.Contains("ordinary", example, StringComparison.Ordinal));
    }

    /// <summary>
    /// authoritative ordinary/rainy source 可显式通过 typed policy 保留唯一玩家名槽；风格样本
    /// 仍由其自己的无 token scanner 单独过滤。
    /// </summary>
    [Theory]
    [InlineData("Characters/Dialogue/Abigail:fall_Mon", "fall_Mon", DialogueSourceFamily.OrdinaryDaily)]
    [InlineData("Characters/Dialogue/rainy:Abigail", "Abigail", DialogueSourceFamily.RainyDaily)]
    public void Resolve_ApprovedDailySourceAllowsOnePlayerNameSlot(
        string translationKey,
        string sourceKey,
        DialogueSourceFamily expectedFamily)
    {
        const string rawSource = "今天也别太累，@。";
        LoadedDialogueSourceSnapshot snapshot = CreateSnapshot(translationKey, rawSource);
        Dictionary<string, string> sourceAsset = expectedFamily == DialogueSourceFamily.OrdinaryDaily
            ? CreateOrdinaryAsset()
            : new Dictionary<string, string>(StringComparer.Ordinal);
        sourceAsset[sourceKey] = rawSource;

        AuthoritativeDialogueSourceResolution resolution = AuthoritativeDialogueSourceResolver.Resolve(
            snapshot,
            sourceAsset,
            CreateOrdinaryAsset(),
            Array.Empty<string>(),
            "fall",
            4);

        Assert.True(resolution.IsResolved, resolution.ReasonCode.ToString());
        AuthoritativeDialogueSource source = Assert.IsType<AuthoritativeDialogueSource>(
            resolution.Source);
        Assert.Equal(expectedFamily, source.Family);
        Assert.Equal(rawSource, source.SourceText);
    }

    /// <summary>
    /// 其他 Mod 编辑后的 exact asset raw value 必须与捕获行逐字符相同，否则 source 不再 authoritative。
    /// </summary>
    [Fact]
    public void Resolve_SourceAssetAndLoadedLineMismatchIsRejected()
    {
        LoadedDialogueSourceSnapshot snapshot = CreateSnapshot(
            "Characters/Dialogue/Abigail:fall_Mon",
            "捕获行。");
        Dictionary<string, string> sourceAsset = CreateOrdinaryAsset();
        sourceAsset["fall_Mon"] = "资产已漂移。";

        AuthoritativeDialogueSourceResolution resolution = AuthoritativeDialogueSourceResolver.Resolve(
            snapshot,
            sourceAsset,
            sourceAsset,
            Array.Empty<string>(),
            "fall",
            4);

        Assert.False(resolution.IsResolved);
        Assert.Equal(AuthoritativeDialogueSourceReasonCode.SourceTextMismatch, resolution.ReasonCode);
        Assert.Null(resolution.Source);
    }

    /// <summary>
    /// pending active event 始终在目标 NPC ordinary asset 上检查，即使 actual source 来自 rainy sheet。
    /// </summary>
    [Fact]
    public void Resolve_RainyStillChecksPendingEventAgainstNpcOrdinaryAsset()
    {
        LoadedDialogueSourceSnapshot snapshot = CreateSnapshot(
            "Characters/Dialogue/rainy:Abigail",
            "雨天原文。");
        Dictionary<string, string> ordinaryAsset = CreateOrdinaryAsset();
        ordinaryAsset["event-special"] = "事件文本。";

        AuthoritativeDialogueSourceResolution resolution = AuthoritativeDialogueSourceResolver.Resolve(
            snapshot,
            new Dictionary<string, string> { ["Abigail"] = "雨天原文。" },
            ordinaryAsset,
            new[] { "event-special" },
            "fall",
            4);

        Assert.False(resolution.IsResolved);
        Assert.Equal(
            AuthoritativeDialogueSourceReasonCode.PendingActiveDialogueEvent,
            resolution.ReasonCode);
        Assert.Null(resolution.Source);
    }

    /// <summary>
    /// candidate bind 必须复用初始 snapshot handle 中的同一 access token，不能重新 capture 对象。
    /// </summary>
    [Fact]
    public void BindCandidate_ReusesInitialSnapshotAccessToken()
    {
        LoadedDialogueSourceSnapshot snapshot = CreateSnapshot(
            "Characters/Dialogue/rainy:Abigail",
            "雨天原文。");
        InMemoryTargetAccess access = new();
        LoadedDialogueSourceSnapshotHandle handle = new(snapshot, access);
        DialogueCandidate candidate = new(
            "Abigail",
            DialogueSourceFamily.RainyDaily,
            "zh-CN",
            "Characters/Dialogue/rainy",
            "Abigail",
            "雨天原文。",
            SourceDialogueHasher.Compute("雨天原文。"),
            new[] { "样例一。", "样例二。", "样例三。" });

        LoadedDialogueTargetResolution resolution =
            StardewLoadedDialogueStackAdapter.BindCandidate(handle, candidate, 42, "zh-CN");

        Assert.True(resolution.IsCaptured);
        LoadedDialogueTarget target = Assert.IsType<LoadedDialogueTarget>(resolution.Target);
        Assert.Same(access, target.Access);
        Assert.Same(candidate, target.Candidate);
    }

    private static LoadedDialogueSourceSnapshotResolution Capture(
        string translationKey,
        LoadedDialogueStackFacts facts,
        object stackToken,
        object dialogueToken,
        object lineToken)
    {
        return LoadedDialogueSourceSnapshotCapture.Capture(
            "Abigail",
            translationKey,
            "原版逐字符行。",
            "zh-CN",
            facts,
            stackToken,
            dialogueToken,
            lineToken);
    }

    private static LoadedDialogueSourceSnapshot CreateSnapshot(
        string translationKey,
        string sourceText)
    {
        LoadedDialogueSourceSnapshotResolution resolution =
            LoadedDialogueSourceSnapshotCapture.Capture(
                "Abigail",
                translationKey,
                sourceText,
                "zh-CN",
                CreateEligibleFacts(),
                new object(),
                new object(),
                new object());
        return Assert.IsType<LoadedDialogueSourceSnapshot>(resolution.Snapshot);
    }

    private static Dictionary<string, string> CreateOrdinaryAsset()
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["fall_Mon"] = "ordinary source。",
            ["fall_Tue"] = "ordinary one。",
            ["fall_Wed"] = "ordinary two。",
            ["summer_Thu"] = "ordinary three。",
            ["winter_Fri"] = "ordinary four。",
        };
    }

    private static object[] Case(
        string translationKey,
        Func<LoadedDialogueStackFacts, LoadedDialogueStackFacts> mutate,
        string expectedReason) => new object[] { translationKey, mutate, expectedReason };

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

    private sealed class InMemoryTargetAccess : ILoadedDialogueTargetAccess
    {
        public LoadedDialogueStackFacts ReadCurrentFacts(bool isDialogueDisplayActive)
        {
            return CreateEligibleFacts() with
            {
                IsDialogueDisplayActive = isDialogueDisplayActive,
            };
        }

        public bool TryReplaceText(string expectedCurrentText, string replacementText)
        {
            return string.Equals(expectedCurrentText, replacementText, StringComparison.Ordinal);
        }
    }
}
