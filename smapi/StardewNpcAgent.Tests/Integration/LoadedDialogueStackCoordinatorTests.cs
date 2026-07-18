using StardewNpcAgent.Application;
using StardewNpcAgent.Game;
using StardewNpcAgent.Infrastructure.Storage;
using StardewNpcAgent.Integration;

namespace StardewNpcAgent.Tests.Integration;

/// <summary>
/// 验证已加载栈协调层的 compare-and-swap、观察器授权与生命周期回滚。
/// </summary>
/// <remarks>
/// 测试使用真实 cache、展示协调器、展示观察器和 durable ACK outbox；唯一替身是游戏对象
/// 载体，因为当前 arm64 测试进程不能执行 x86-64 Stardew 程序集中的对象构造代码。
/// </remarks>
public sealed class LoadedDialogueStackCoordinatorTests : IDisposable
{
    private const string SaveId = "save-loaded-stack";
    private const string PlayerId = "player-loaded-stack";
    private readonly string testDirectory = Path.Combine(
        Path.GetTempPath(),
        "StardewNpcAgent.Tests",
        $"LoadedDialogueStackCoordinator.{Guid.NewGuid():N}");

    /// <summary>
    /// 为每个测试创建独立目录，避免 durable outbox 文件相互污染。
    /// </summary>
    public LoadedDialogueStackCoordinatorTests()
    {
        Directory.CreateDirectory(testDirectory);
    }

    /// <summary>
    /// 完整 generated entry 通过当前事实、cache 决策与文本比较后，只写一次并 arm 原生观察器。
    /// </summary>
    [Fact]
    public void Apply_ValidGeneratedEntryChangesTextOnceAndArmsObserver()
    {
        TestRuntime runtime = CreateRuntime();

        LoadedDialogueApplyResult result = runtime.Coordinator.Apply(
            runtime.Target,
            runtime.Entry,
            isDialogueDisplayActive: false);

        Assert.True(result.WasApplied);
        Assert.Equal(LoadedDialogueApplyReasonCode.Applied, result.ReasonCode);
        Assert.Null(result.TargetReasonCode);
        Assert.Equal(runtime.Entry.EnhancedText, runtime.Access.Text);
        Assert.Equal(1, runtime.Access.ReplacementCount);
        Assert.Equal(1, runtime.Observer.ArmedCount);
        Assert.True(runtime.Cache.TryGet(runtime.Entry.Key, out _));
        Assert.True(runtime.Coordinator.IsTrackedDirectKey(runtime.Entry.Key));
    }

    /// <summary>
    /// direct loaded route 仍以 raw template 做最终 CAS；本地过滤后名字只供 observer 计算
    /// exact UI 文本。名字缺失时必须在写入前删除 cache 并保持原版行。
    /// </summary>
    [Fact]
    public void Apply_PlayerNameSlotUsesRawCasAndFallsBackWhenLocalNameIsUnavailable()
    {
        const string filteredPlayerName = "Sensitive Farmer 42";
        TestRuntime valid = CreateRuntime(enhancedText: "别太累，@。");

        LoadedDialogueApplyResult applied = valid.Coordinator.Apply(
            valid.Target,
            valid.Entry,
            isDialogueDisplayActive: false,
            filteredPlayerName);

        Assert.True(applied.WasApplied);
        Assert.Equal("别太累，@。", valid.Access.Text);
        Assert.Equal(1, valid.Observer.ArmedCount);
        object menu = new();
        DialogueMenuSnapshot renderedSnapshot = new(
            menu,
            valid.Entry.Key.NpcId,
            $"{valid.Entry.Key.AssetName}:{valid.Entry.Key.DialogueKey}",
            "别太累，Sensitive Farmer 42。");
        valid.Observer.ObserveMenu(
            renderedSnapshot,
            valid.Entry.Key.GameDayIndex,
            valid.Entry.Key.Locale);
        Assert.NotNull(
            valid.Observer.TryObserveRendered(
                renderedSnapshot,
                valid.Entry.Key.GameDayIndex,
                valid.Entry.Key.Locale));

        TestRuntime missingName = CreateRuntime(enhancedText: "别太累，@。");
        LoadedDialogueApplyResult rejected = missingName.Coordinator.Apply(
            missingName.Target,
            missingName.Entry,
            isDialogueDisplayActive: false,
            filteredPlayerName: null);

        Assert.False(rejected.WasApplied);
        Assert.Equal(LoadedDialogueApplyReasonCode.CacheDecisionRejected, rejected.ReasonCode);
        Assert.Equal("原版。", missingName.Access.Text);
        Assert.Equal(0, missingName.Access.ReplacementCount);
        Assert.Equal(0, missingName.Observer.ArmedCount);
        Assert.False(missingName.Cache.TryGet(missingName.Entry.Key, out _));
    }

    /// <summary>
    /// Agent 等待期间的对象身份、下标、翻译键或最后一刻文本竞态均须拒绝并移除 live cache。
    /// </summary>
    [Theory]
    [InlineData(TargetMutation.StackIdentity, LoadedDialogueStackReasonCode.SnapshotIdentityChanged)]
    [InlineData(TargetMutation.DialogueIndex, LoadedDialogueStackReasonCode.DialogueAlreadyAdvanced)]
    [InlineData(TargetMutation.TranslationKey, LoadedDialogueStackReasonCode.TranslationKeyMismatch)]
    [InlineData(TargetMutation.CompareAndSwapRace, null)]
    public void Apply_ChangedIdentityIndexKeyOrTextRemovesLiveCacheAndDoesNotArm(
        TargetMutation mutation,
        LoadedDialogueStackReasonCode? expectedTargetReason)
    {
        TestRuntime runtime = CreateRuntime();
        ApplyMutation(runtime.Access, mutation);

        LoadedDialogueApplyResult result = runtime.Coordinator.Apply(
            runtime.Target,
            runtime.Entry,
            isDialogueDisplayActive: false);

        Assert.False(result.WasApplied);
        Assert.Equal(
            mutation == TargetMutation.CompareAndSwapRace
                ? LoadedDialogueApplyReasonCode.TextCompareFailed
                : LoadedDialogueApplyReasonCode.TargetRejected,
            result.ReasonCode);
        Assert.Equal(expectedTargetReason, result.TargetReasonCode);
        Assert.Equal(
            mutation == TargetMutation.CompareAndSwapRace ? "竞态写入。" : "原版。",
            runtime.Access.Text);
        Assert.Equal(0, runtime.Observer.ArmedCount);
        Assert.False(runtime.Cache.TryGet(runtime.Entry.Key, out _));
    }

    /// <summary>
    /// typed <c>@</c> 不改变 direct route 的最后一刻 CAS：若第三方在事实复核后改写行，
    /// 本 Mod 必须保留第三方文本、删除 cache，且不得 arm 含本地名字的观察 token。
    /// </summary>
    [Fact]
    public void Apply_PlayerNameTemplateStillRejectsThirdPartyCompareAndSwapRace()
    {
        TestRuntime runtime = CreateRuntime(enhancedText: "别太累，@。");
        runtime.Access.ReplaceTextBeforeNextCompare = "第三方刚刚改写的文本。";

        LoadedDialogueApplyResult result = runtime.Coordinator.Apply(
            runtime.Target,
            runtime.Entry,
            isDialogueDisplayActive: false,
            filteredPlayerName: "Sensitive Farmer 42");

        Assert.False(result.WasApplied);
        Assert.Equal(LoadedDialogueApplyReasonCode.TextCompareFailed, result.ReasonCode);
        Assert.Equal("第三方刚刚改写的文本。", runtime.Access.Text);
        Assert.Equal(0, runtime.Observer.ArmedCount);
        Assert.False(runtime.Cache.TryGet(runtime.Entry.Key, out _));
    }

    /// <summary>
    /// direct target 的 family 或 cache raw text 漂移必须在任何文本 CAS 前拒绝。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Apply_FamilyOrCacheRawTextDriftRejectsPreparedIdentity(bool changeFamily)
    {
        TestRuntime runtime = CreateRuntime();
        LoadedDialogueTarget target = changeFamily
            ? runtime.Target with
            {
                Candidate = runtime.Target.Candidate with
                {
                    SourceFamily = DialogueSourceFamily.RainyDaily,
                },
            }
            : runtime.Target;
        DailyDialogueCacheEntry entry = changeFamily
            ? runtime.Entry
            : runtime.Entry with { SourceText = "缓存绑定了另一段原文。" };

        LoadedDialogueApplyResult result = runtime.Coordinator.Apply(target, entry, false);

        Assert.False(result.WasApplied);
        Assert.Equal(LoadedDialogueApplyReasonCode.TargetRejected, result.ReasonCode);
        Assert.Equal("原版。", runtime.Access.Text);
        Assert.Equal(0, runtime.Access.ReplacementCount);
    }

    /// <summary>
    /// 尚未展示的直接补丁在生命周期失效时恢复原文，并报告需要跳过资产 reset 的完整 key。
    /// </summary>
    [Fact]
    public void ReleaseUndisplayed_RestoresOnlyUnchangedTrackedPatch()
    {
        TestRuntime runtime = CreateRuntime();
        Assert.True(
            runtime.Coordinator.Apply(runtime.Target, runtime.Entry, false).WasApplied);

        LoadedDialogueReleaseResult released = runtime.Coordinator.ReleaseUndisplayed(
            canVerifyCurrentTarget: true);

        Assert.Equal("原版。", runtime.Access.Text);
        Assert.Equal(2, runtime.Access.ReplacementCount);
        Assert.Equal(1, released.RestoredCount);
        Assert.Equal(new[] { runtime.Entry.Key }, released.DirectKeys);
        Assert.Empty(
            runtime.Coordinator.ReleaseUndisplayed(canVerifyCurrentTarget: true).DirectKeys);
    }

    /// <summary>
    /// 若其他 Mod 在直接注入后改写了文本，清理只能丢弃记录，绝不能覆盖第三方结果。
    /// </summary>
    [Fact]
    public void ReleaseUndisplayed_DoesNotOverwriteThirdPartyText()
    {
        TestRuntime runtime = CreateRuntime();
        Assert.True(
            runtime.Coordinator.Apply(runtime.Target, runtime.Entry, false).WasApplied);
        runtime.Access.ReplaceTextExternally("其他 Mod 的文本。");

        LoadedDialogueReleaseResult released = runtime.Coordinator.ReleaseUndisplayed(
            canVerifyCurrentTarget: true);

        Assert.Equal("其他 Mod 的文本。", runtime.Access.Text);
        Assert.Equal(1, runtime.Access.ReplacementCount);
        Assert.Equal(0, released.RestoredCount);
        Assert.Equal(new[] { runtime.Entry.Key }, released.DirectKeys);
    }

    /// <summary>
    /// 展示完成只能用 registry 中同一个 opaque decision 释放，等价但由其他协调器签发的 token 无效。
    /// </summary>
    [Fact]
    public void MarkDisplayed_RequiresTheExactOpaqueDecisionAndReleasesDirectKey()
    {
        TestRuntime runtime = CreateRuntime();
        Assert.True(
            runtime.Coordinator.Apply(runtime.Target, runtime.Entry, false).WasApplied);
        RenderedDialogueObservation actualObservation = ObserveRendered(runtime);

        DialogueDisplayCoordinator foreignDisplayCoordinator = new(
            SaveId,
            PlayerId,
            runtime.Cache,
            OpenOutbox("foreign.json"));
        DialogueDisplayDecision foreignDecision = foreignDisplayCoordinator.Resolve(
            CreateDisplayContext(runtime.Entry));

        Assert.Null(runtime.Coordinator.MarkDisplayed(foreignDecision));
        Assert.Equal(
            runtime.Entry.Key,
            runtime.Coordinator.MarkDisplayed(actualObservation.Decision));
        Assert.False(runtime.Coordinator.IsTrackedDirectKey(runtime.Entry.Key));
        Assert.Null(runtime.Coordinator.MarkDisplayed(actualObservation.Decision));
        Assert.Empty(
            runtime.Coordinator.ReleaseUndisplayed(canVerifyCurrentTarget: true).DirectKeys);
        Assert.Equal(runtime.Entry.EnhancedText, runtime.Access.Text);
    }

    /// <summary>
    /// 观察器 arm 发生意外异常时，协调器必须先恢复原文、删除 cache/registry，再把异常交给日志边界。
    /// </summary>
    [Fact]
    public void Apply_ObserverFailureRollsBackTextAndCacheBeforeRethrowing()
    {
        TestRuntime runtime = CreateRuntime(
            armObserver: _ => throw new InvalidOperationException("测试注入的观察器故障。"));

        Assert.Throws<InvalidOperationException>(
            () => runtime.Coordinator.Apply(runtime.Target, runtime.Entry, false));

        Assert.Equal("原版。", runtime.Access.Text);
        Assert.Equal(2, runtime.Access.ReplacementCount);
        Assert.False(runtime.Cache.TryGet(runtime.Entry.Key, out _));
        Assert.Empty(
            runtime.Coordinator.ReleaseUndisplayed(canVerifyCurrentTarget: true).DirectKeys);
    }

    /// <summary>
    /// 删除每个测试创建的临时 outbox；测试结束时其中没有需要保留的用户数据。
    /// </summary>
    public void Dispose()
    {
        if (Directory.Exists(testDirectory))
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    /// <summary>
    /// 创建一套生产组件和单个内存目标，并预先将正式 generated entry 放入 live cache。
    /// </summary>
    private TestRuntime CreateRuntime(
        Action<DialogueDisplayDecision>? armObserver = null,
        string enhancedText = "增强。")
    {
        DailyDialogueCache cache = new();
        DailyDialogueCacheEntry entry = CreateEntry() with { EnhancedText = enhancedText };
        cache.Store(entry);
        DialogueDisplayCoordinator displayCoordinator = new(
            SaveId,
            PlayerId,
            cache,
            OpenOutbox($"display-{Guid.NewGuid():N}.json"));
        DialogueDisplayObserver observer = new();
        LoadedDialogueStackCoordinator coordinator = armObserver is null
            ? new LoadedDialogueStackCoordinator(cache, displayCoordinator, observer)
            : new LoadedDialogueStackCoordinator(cache, displayCoordinator, armObserver);
        InMemoryLoadedDialogueTargetAccess access = new();
        DialogueCandidate candidate = new(
            entry.Key.NpcId,
            entry.SourceFamily,
            entry.Key.Locale,
            entry.Key.AssetName,
            entry.Key.DialogueKey,
            "原版。",
            entry.SourceHash,
            new[] { "样例一。", "样例二。", "样例三。" });
        LoadedDialogueTarget target = new(entry.Key, candidate, access);
        return new TestRuntime(cache, entry, displayCoordinator, observer, coordinator, access, target);
    }

    /// <summary>
    /// 模拟 MenuChanged 与首次 RenderedActiveMenu，取得观察器返回的真实 opaque decision。
    /// </summary>
    private static RenderedDialogueObservation ObserveRendered(TestRuntime runtime)
    {
        object menuIdentity = new();
        DialogueMenuSnapshot snapshot = new(
            menuIdentity,
            runtime.Entry.Key.NpcId,
            $"{runtime.Entry.Key.AssetName}:{runtime.Entry.Key.DialogueKey}",
            runtime.Entry.EnhancedText);
        runtime.Observer.ObserveMenu(
            snapshot,
            runtime.Entry.Key.GameDayIndex,
            runtime.Entry.Key.Locale);
        return Assert.IsType<RenderedDialogueObservation>(
            runtime.Observer.TryObserveRendered(
                snapshot,
                runtime.Entry.Key.GameDayIndex,
                runtime.Entry.Key.Locale));
    }

    /// <summary>
    /// 对内存目标施加单一变化；CAS 竞态发生在事实读取之后、文本替换之前。
    /// </summary>
    private static void ApplyMutation(
        InMemoryLoadedDialogueTargetAccess access,
        TargetMutation mutation)
    {
        switch (mutation)
        {
            case TargetMutation.StackIdentity:
                access.Facts = access.Facts with { StackIdentityMatches = false };
                break;
            case TargetMutation.DialogueIndex:
                access.Facts = access.Facts with { CurrentDialogueIndex = 1 };
                break;
            case TargetMutation.TranslationKey:
                access.Facts = access.Facts with { TranslationKeyMatches = false };
                break;
            case TargetMutation.CompareAndSwapRace:
                access.ReplaceTextBeforeNextCompare = "竞态写入。";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
        }
    }

    /// <summary>
    /// 创建具备全部展示元数据的正式 generated cache entry。
    /// </summary>
    private static DailyDialogueCacheEntry CreateEntry()
    {
        DailyDialogueCacheKey key = new(
            42,
            "zh-CN",
            "Sebastian",
            "Characters/Dialogue/Sebastian",
            "fall_Mon");
        return new DailyDialogueCacheEntry(
            key,
            DialogueSourceFamily.OrdinaryDaily,
            "原版。",
            SourceDialogueHasher.Compute("原版。"),
            "增强。",
            "generation-loaded-stack",
            "generation-key-loaded-stack",
            "trace-loaded-stack");
    }

    /// <summary>
    /// 将 raw source 与完整复合 key 组合成展示协调器的输入。
    /// </summary>
    private static DialogueDisplayContext CreateDisplayContext(DailyDialogueCacheEntry entry)
    {
        return new DialogueDisplayContext(
            entry.Key.GameDayIndex,
            entry.Key.Locale,
            entry.Key.NpcId,
            entry.Key.AssetName,
            entry.Key.DialogueKey,
            "原版。");
    }

    /// <summary>
    /// 打开绑定当前测试分区的真实 durable ACK outbox。
    /// </summary>
    private DurableDisplayAckOutbox OpenOutbox(string fileName)
    {
        return DurableDisplayAckOutbox.Open(
            Path.Combine(testDirectory, fileName),
            SaveId,
            PlayerId);
    }

    /// <summary>
    /// 返回唯一安全的当前栈事实；测试通过 record copy 精确改变一个字段。
    /// </summary>
    private static LoadedDialogueStackFacts CreateEligibleFacts()
    {
        return new LoadedDialogueStackFacts(
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
    }

    /// <summary>
    /// 模拟主线程游戏对象端口，并保持与生产端口相同的逐字符条件替换合同。
    /// </summary>
    private sealed class InMemoryLoadedDialogueTargetAccess : ILoadedDialogueTargetAccess
    {
        public LoadedDialogueStackFacts Facts { get; set; } = CreateEligibleFacts();

        public string Text { get; private set; } = "原版。";

        public int ReplacementCount { get; private set; }

        public string? ReplaceTextBeforeNextCompare { get; set; }

        public LoadedDialogueStackFacts ReadCurrentFacts(bool isDialogueDisplayActive)
        {
            return Facts with { IsDialogueDisplayActive = isDialogueDisplayActive };
        }

        public bool TryReplaceText(string expectedCurrentText, string replacementText)
        {
            if (ReplaceTextBeforeNextCompare is not null)
            {
                Text = ReplaceTextBeforeNextCompare;
                ReplaceTextBeforeNextCompare = null;
            }

            if (!string.Equals(Text, expectedCurrentText, StringComparison.Ordinal))
            {
                return false;
            }

            Text = replacementText;
            ReplacementCount++;
            return true;
        }

        public void ReplaceTextExternally(string replacementText)
        {
            Text = replacementText;
        }
    }

    public enum TargetMutation
    {
        StackIdentity,
        DialogueIndex,
        TranslationKey,
        CompareAndSwapRace,
    }

    private sealed record TestRuntime(
        DailyDialogueCache Cache,
        DailyDialogueCacheEntry Entry,
        DialogueDisplayCoordinator DisplayCoordinator,
        DialogueDisplayObserver Observer,
        LoadedDialogueStackCoordinator Coordinator,
        InMemoryLoadedDialogueTargetAccess Access,
        LoadedDialogueTarget Target);
}
