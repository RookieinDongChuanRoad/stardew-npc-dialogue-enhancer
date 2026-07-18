using StardewNpcAgent.Application;
using StardewNpcAgent.Contracts;
using StardewNpcAgent.Game;
using StardewNpcAgent.Infrastructure.Storage;
using StardewNpcAgent.Integration;

namespace StardewNpcAgent.Tests.Integration;

/// <summary>
/// 验证正式 generated cache 只能通过 Late asset dictionary patch 注入，并在原生菜单首次绘制后 ACK。
/// </summary>
public sealed class DialogueNativeUiIntegrationTests : IDisposable
{
    private const string SaveId = "save-native-ui";
    private const string PlayerId = "player-native-ui";
    private readonly string testDirectory = Path.Combine(
        Path.GetTempPath(),
        "StardewNpcAgent.Tests",
        $"DialogueNativeUi.{Guid.NewGuid():N}");

    public DialogueNativeUiIntegrationTests()
    {
        Directory.CreateDirectory(testDirectory);
    }

    /// <summary>
    /// exact source 的正式 generated entry 才允许 patch 并 arm opaque decision；静态/坏 hash 全部原样。
    /// </summary>
    [Fact]
    public void InjectionAdapter_OnlyAppliesFormalGeneratedEntryWithCurrentSourceHash()
    {
        DailyDialogueCache live = new();
        DailyDialogueCacheEntry formal = CreateEntry();
        live.Store(formal);
        DurableDisplayAckOutbox outbox = OpenOutbox("injection.json");
        DialogueDisplayCoordinator coordinator = new(SaveId, PlayerId, live, outbox);
        DialogueDisplayObserver observer = new();
        Dictionary<string, string> dialogue = new(StringComparer.Ordinal)
        {
            [formal.Key.DialogueKey] = "原版台词。",
        };

        DialogueInjectionResult applied = DialogueInjectionAdapter.Apply(
            dialogue,
            formal.Key.AssetName,
            formal.Key.GameDayIndex,
            formal.Key.Locale,
            new[] { formal },
            coordinator,
            observer);

        Assert.Equal(1, applied.AppliedCount);
        Assert.Equal("增强台词。", dialogue[formal.Key.DialogueKey]);
        Assert.Equal(1, observer.ArmedCount);
        Assert.Equal(0, outbox.PendingCount);

        observer.Clear();
        dialogue[formal.Key.DialogueKey] = "其他 Mod 改过的原文。";
        DialogueInjectionResult changedSource = DialogueInjectionAdapter.Apply(
            dialogue,
            formal.Key.AssetName,
            formal.Key.GameDayIndex,
            formal.Key.Locale,
            new[] { formal },
            coordinator,
            observer);
        Assert.Equal(0, changedSource.AppliedCount);
        Assert.Equal("其他 Mod 改过的原文。", dialogue[formal.Key.DialogueKey]);
        Assert.Equal(0, observer.ArmedCount);

        DailyDialogueCacheEntry staticEntry = formal with
        {
            GenerationId = null,
            GenerationKey = null,
            TraceId = null,
        };
        dialogue[formal.Key.DialogueKey] = "原版台词。";
        DialogueInjectionResult staticResult = DialogueInjectionAdapter.Apply(
            dialogue,
            formal.Key.AssetName,
            formal.Key.GameDayIndex,
            formal.Key.Locale,
            new[] { staticEntry },
            coordinator,
            observer);
        Assert.Equal(0, staticResult.AppliedCount);
        Assert.Equal("原版台词。", dialogue[formal.Key.DialogueKey]);
    }

    /// <summary>
    /// asset route 写入的仍是 raw <c>@</c> template，让原版在显示期展开；observer 只用
    /// 短生命周期过滤后名字计算 expected text。名字不可用时字典保持原文且不 arm。
    /// </summary>
    [Fact]
    public void InjectionAdapter_PlayerNameSlotRequiresLocalNameButKeepsRawTemplateInAsset()
    {
        const string filteredPlayerName = "Sensitive Farmer 42";
        DailyDialogueCacheEntry entry = CreateEntry() with
        {
            EnhancedText = "今天也别太累，@。",
        };
        DailyDialogueCache live = new();
        live.Store(entry);
        DialogueDisplayCoordinator coordinator = new(
            SaveId,
            PlayerId,
            live,
            OpenOutbox("asset-name-slot.json"));
        DialogueDisplayObserver observer = new();
        Dictionary<string, string> dialogue = new(StringComparer.Ordinal)
        {
            [entry.Key.DialogueKey] = entry.SourceText,
        };

        DialogueInjectionResult applied = DialogueInjectionAdapter.Apply(
            dialogue,
            entry.Key.AssetName,
            entry.Key.GameDayIndex,
            entry.Key.Locale,
            new[] { entry },
            coordinator,
            observer,
            filteredPlayerName);

        Assert.Equal(1, applied.AppliedCount);
        Assert.Equal("今天也别太累，@。", dialogue[entry.Key.DialogueKey]);
        Assert.Equal(1, observer.ArmedCount);

        observer.Clear();
        dialogue[entry.Key.DialogueKey] = entry.SourceText;
        DialogueInjectionResult missingName = DialogueInjectionAdapter.Apply(
            dialogue,
            entry.Key.AssetName,
            entry.Key.GameDayIndex,
            entry.Key.Locale,
            new[] { entry },
            coordinator,
            observer,
            filteredPlayerName: null);

        Assert.Equal(0, missingName.AppliedCount);
        Assert.Equal(entry.SourceText, dialogue[entry.Key.DialogueKey]);
        Assert.Equal(0, observer.ArmedCount);
    }

    /// <summary>
    /// MenuChanged 只关联；首次 RenderedActiveMenu 才返回确认。成功入队后 Complete 使 token one-shot。
    /// </summary>
    [Fact]
    public void DisplayObserver_AcksOnlyAfterMatchingNativeMenuIsRenderedAndOnlyOnce()
    {
        DailyDialogueCache live = new();
        DailyDialogueCacheEntry entry = CreateEntry();
        live.Store(entry);
        DurableDisplayAckOutbox outbox = OpenOutbox("observer.json");
        DialogueDisplayCoordinator coordinator = new(SaveId, PlayerId, live, outbox);
        DialogueDisplayDecision decision = coordinator.Resolve(CreateDisplayContext());
        DialogueDisplayObserver observer = new();
        observer.Arm(decision);
        object nativeMenu = new();
        // Stardew 的公开 Dialogue.TranslationKey 由 LoadedDialogueKey 构造时使用
        // 反斜杠资产路径；缓存身份则遵循 SMAPI 的正斜杠形式。成功路径必须覆盖这个
        // 真实运行时差异，否则台词虽然已经显示，观察器仍会漏掉 durable ACK。
        string nativeTranslationKey =
            $"{entry.Key.AssetName.Replace('/', '\\')}:{entry.Key.DialogueKey}";
        DialogueMenuSnapshot snapshot = new(
            nativeMenu,
            entry.Key.NpcId,
            nativeTranslationKey,
            entry.EnhancedText);

        observer.ObserveMenu(snapshot, entry.Key.GameDayIndex, entry.Key.Locale);

        Assert.Equal(0, outbox.PendingCount);
        Assert.Null(
            observer.TryObserveRendered(
                snapshot with { MenuIdentity = new object() },
                entry.Key.GameDayIndex,
                entry.Key.Locale));
        RenderedDialogueObservation rendered = Assert.IsType<RenderedDialogueObservation>(
            observer.TryObserveRendered(snapshot, entry.Key.GameDayIndex, entry.Key.Locale));
        DisplayAckEnqueueResult ack = coordinator.RecordDisplayed(
            rendered.Decision,
            rendered.Confirmation);
        observer.Complete(rendered);

        Assert.Equal(DisplayAckStatus.Accepted, ack.Status);
        Assert.Equal(1, outbox.PendingCount);
        Assert.Null(observer.TryObserveRendered(snapshot, entry.Key.GameDayIndex, entry.Key.Locale));
        Assert.Equal(0, observer.ArmedCount);
    }

    /// <summary>
    /// raw <c>@</c> 必须只在 observer 的本地 render 边界展开；cache 保持 raw template，ACK
    /// 只含 generation/day/NPC/source hash，序列化文件不得出现真实玩家名。
    /// </summary>
    [Fact]
    public void DisplayObserver_PlayerNameSlotMatchesRenderedTextWithoutPersistingName()
    {
        const string filteredPlayerName = "Sensitive Farmer 42";
        DailyDialogueCacheEntry entry = CreateEntry() with
        {
            EnhancedText = "今天也别太累，@。",
        };
        DailyDialogueCache live = new();
        live.Store(entry);
        string outboxPath = Path.Combine(testDirectory, "private-name.json");
        DurableDisplayAckOutbox outbox = DurableDisplayAckOutbox.Open(
            outboxPath,
            SaveId,
            PlayerId);
        DialogueDisplayCoordinator coordinator = new(SaveId, PlayerId, live, outbox);
        DialogueDisplayDecision decision = coordinator.Resolve(CreateDisplayContext());
        DialogueDisplayObserver observer = new();

        Assert.True(observer.TryArm(decision, filteredPlayerName));
        object menu = new();
        DialogueMenuSnapshot exact = new(
            menu,
            entry.Key.NpcId,
            $"{entry.Key.AssetName}:{entry.Key.DialogueKey}",
            "今天也别太累，Sensitive Farmer 42。");
        observer.ObserveMenu(exact, entry.Key.GameDayIndex, entry.Key.Locale);
        RenderedDialogueObservation rendered = Assert.IsType<RenderedDialogueObservation>(
            observer.TryObserveRendered(exact, entry.Key.GameDayIndex, entry.Key.Locale));

        coordinator.RecordDisplayed(rendered.Decision, rendered.Confirmation);
        observer.Complete(rendered);

        Assert.Equal("今天也别太累，@。", entry.EnhancedText);
        Assert.DoesNotContain(filteredPlayerName, File.ReadAllText(outboxPath), StringComparison.Ordinal);
        Assert.Equal(1, outbox.PendingCount);
    }

    /// <summary>
    /// 同一目标 NPC/key 的错误文本已证明这次菜单没有展示 generated 内容，必须永久消费 token；
    /// 之后同 key 偶然出现正确文本也不能补 ACK。完全无关菜单则不消费。
    /// </summary>
    [Fact]
    public void DisplayObserver_TargetTextMismatchConsumesTokenButUnrelatedMenuDoesNot()
    {
        DailyDialogueCache live = new();
        DailyDialogueCacheEntry entry = CreateEntry();
        live.Store(entry);
        DialogueDisplayCoordinator coordinator = new(
            SaveId,
            PlayerId,
            live,
            OpenOutbox("mismatch-consumption.json"));
        DialogueDisplayObserver observer = new();
        observer.Arm(coordinator.Resolve(CreateDisplayContext()));

        DialogueMenuSnapshot unrelated = new(
            new object(),
            "Sebastian",
            "Characters/Dialogue/Sebastian:fall_Mon",
            "别人的台词。");
        observer.ObserveMenu(unrelated, entry.Key.GameDayIndex, entry.Key.Locale);
        Assert.Equal(1, observer.ArmedCount);

        DialogueMenuSnapshot wrongTargetText = new(
            new object(),
            entry.Key.NpcId,
            $"{entry.Key.AssetName}:{entry.Key.DialogueKey}",
            "错误玩家名或原始 @。");
        observer.ObserveMenu(wrongTargetText, entry.Key.GameDayIndex, entry.Key.Locale);
        Assert.Equal(0, observer.ArmedCount);

        DialogueMenuSnapshot laterExact = wrongTargetText with
        {
            MenuIdentity = new object(),
            DisplayedText = entry.EnhancedText,
        };
        observer.ObserveMenu(laterExact, entry.Key.GameDayIndex, entry.Key.Locale);
        Assert.Null(
            observer.TryObserveRendered(
                laterExact,
                entry.Key.GameDayIndex,
                entry.Key.Locale));
    }

    /// <summary>
    /// 首个 exact rendered frame 已形成“至少展示一次”的事实；后续同菜单文本变化不能撤销
    /// observation，否则一次本地 outbox 写失败可能永远丢失已经发生的真实展示。
    /// </summary>
    [Fact]
    public void DisplayObserver_AfterFirstExactRenderKeepsObservationAcrossLaterFrameChange()
    {
        DailyDialogueCache live = new();
        DailyDialogueCacheEntry entry = CreateEntry();
        live.Store(entry);
        DialogueDisplayCoordinator coordinator = new(
            SaveId,
            PlayerId,
            live,
            OpenOutbox("later-frame-change.json"));
        DialogueDisplayObserver observer = new();
        observer.Arm(coordinator.Resolve(CreateDisplayContext()));
        object menu = new();
        DialogueMenuSnapshot exact = new(
            menu,
            entry.Key.NpcId,
            $"{entry.Key.AssetName}:{entry.Key.DialogueKey}",
            entry.EnhancedText);
        observer.ObserveMenu(exact, entry.Key.GameDayIndex, entry.Key.Locale);
        RenderedDialogueObservation first = Assert.IsType<RenderedDialogueObservation>(
            observer.TryObserveRendered(exact, entry.Key.GameDayIndex, entry.Key.Locale));

        RenderedDialogueObservation later = Assert.IsType<RenderedDialogueObservation>(
            observer.TryObserveRendered(
                exact with { DisplayedText = "第二帧已经变化。" },
                entry.Key.GameDayIndex,
                entry.Key.Locale));

        Assert.Same(first, later);
        Assert.Equal(1, observer.ArmedCount);
    }

    /// <summary>
    /// speaker/key/text/day/locale 任一不符都不能把其他原生菜单关联到 generated token。
    /// </summary>
    [Theory]
    [InlineData("Sebastian", "Characters/Dialogue/Abigail:fall_Mon", "增强台词。", 14, "zh-CN", 1)]
    [InlineData("Abigail", "Characters/Dialogue/Abigail:fall_Tue", "增强台词。", 14, "zh-CN", 1)]
    [InlineData("Abigail", "Characters/Dialogue/Abigail:fall_Mon", "别的文本。", 14, "zh-CN", 0)]
    [InlineData("Abigail", "Characters/Dialogue/Abigail:fall_Mon", "增强台词。", 15, "zh-CN", 0)]
    [InlineData("Abigail", "Characters/Dialogue/Abigail:fall_Mon", "增强台词。", 14, "en", 0)]
    public void DisplayObserver_MismatchedNativeMenuNeverConfirms(
        string npcId,
        string translationKey,
        string displayedText,
        int day,
        string locale,
        int expectedArmedCount)
    {
        DailyDialogueCache live = new();
        DailyDialogueCacheEntry entry = CreateEntry();
        live.Store(entry);
        DialogueDisplayCoordinator coordinator = new(
            SaveId,
            PlayerId,
            live,
            OpenOutbox($"mismatch-{Guid.NewGuid():N}.json"));
        DialogueDisplayObserver observer = new();
        observer.Arm(coordinator.Resolve(CreateDisplayContext()));
        object nativeMenu = new();

        DialogueMenuSnapshot snapshot = new(
            nativeMenu,
            npcId,
            translationKey,
            displayedText);
        observer.ObserveMenu(snapshot, day, locale);

        Assert.Null(observer.TryObserveRendered(snapshot, day, locale));
        Assert.Equal(expectedArmedCount, observer.ArmedCount);
    }

    /// <summary>
    /// rainy 菜单使用共享 asset + NPC key；observer 必须按 exact source family 关联并完成首帧确认。
    /// </summary>
    [Fact]
    public void DisplayObserver_ExactRainySourceAssociatesAndConfirms()
    {
        const string sourceText = "雨天原版。";
        DailyDialogueCacheEntry entry = new(
            new DailyDialogueCacheKey(
                14,
                "zh-CN",
                "Abigail",
                "Characters/Dialogue/rainy",
                "Abigail"),
            DialogueSourceFamily.RainyDaily,
            sourceText,
            SourceDialogueHasher.Compute(sourceText),
            "雨天增强。",
            "generation-rainy",
            "generation-key-rainy",
            "trace-rainy");
        DailyDialogueCache cache = new();
        cache.Store(entry);
        DialogueDisplayCoordinator coordinator = new(
            SaveId,
            PlayerId,
            cache,
            OpenOutbox("rainy-observer.json"));
        DialogueDisplayDecision decision = coordinator.Resolve(
            new DialogueDisplayContext(
                entry.Key.GameDayIndex,
                entry.Key.Locale,
                entry.Key.NpcId,
                entry.Key.AssetName,
                entry.Key.DialogueKey,
                entry.SourceText));
        Assert.Equal(DialogueDisplayDecisionKind.UseGenerated, decision.Kind);
        DialogueDisplayObserver observer = new();
        observer.Arm(decision);
        object menu = new();
        DialogueMenuSnapshot snapshot = new(
            menu,
            "Abigail",
            "Characters\\Dialogue\\rainy:Abigail",
            entry.EnhancedText);

        observer.ObserveMenu(snapshot, 14, "zh-CN");
        RenderedDialogueObservation? rendered = observer.TryObserveRendered(
            snapshot,
            14,
            "zh-CN");

        Assert.NotNull(rendered);
    }

    /// <summary>
    /// MenuChanged 关联成功后，同一个菜单在首帧前若换成其他 speaker/key/text，不能沿用旧 token ACK。
    /// </summary>
    [Theory]
    [InlineData("Sebastian", "Characters/Dialogue/Abigail:fall_Mon", "增强台词。")]
    [InlineData("Abigail", "Characters/Dialogue/Abigail:fall_Tue", "增强台词。")]
    [InlineData("Abigail", "Characters/Dialogue/Abigail:fall_Mon", "首帧实际显示了其他文本。")]
    public void DisplayObserver_FirstRenderedFrameRechecksCurrentDialogueSnapshot(
        string renderedNpcId,
        string renderedTranslationKey,
        string renderedText)
    {
        DailyDialogueCache live = new();
        DailyDialogueCacheEntry entry = CreateEntry();
        live.Store(entry);
        DurableDisplayAckOutbox outbox = OpenOutbox($"render-recheck-{Guid.NewGuid():N}.json");
        DialogueDisplayCoordinator coordinator = new(SaveId, PlayerId, live, outbox);
        DialogueDisplayObserver observer = new();
        observer.Arm(coordinator.Resolve(CreateDisplayContext()));
        object nativeMenu = new();
        observer.ObserveMenu(
            new DialogueMenuSnapshot(
                nativeMenu,
                entry.Key.NpcId,
                $"{entry.Key.AssetName}:{entry.Key.DialogueKey}",
                entry.EnhancedText),
            entry.Key.GameDayIndex,
            entry.Key.Locale);

        RenderedDialogueObservation? rendered = observer.TryObserveRendered(
            currentSnapshot: new DialogueMenuSnapshot(
                nativeMenu,
                renderedNpcId,
                renderedTranslationKey,
                renderedText),
            currentDayIndex: entry.Key.GameDayIndex,
            currentLocale: entry.Key.Locale);

        Assert.Null(rendered);
        Assert.Equal(0, outbox.PendingCount);
        Assert.Equal(0, observer.ArmedCount);
    }

    public void Dispose()
    {
        if (Directory.Exists(testDirectory))
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    private DurableDisplayAckOutbox OpenOutbox(string fileName)
    {
        return DurableDisplayAckOutbox.Open(
            Path.Combine(testDirectory, fileName),
            SaveId,
            PlayerId);
    }

    private static DailyDialogueCacheEntry CreateEntry()
    {
        string sourceText = "原版台词。";
        return new DailyDialogueCacheEntry(
            new DailyDialogueCacheKey(
                14,
                "zh-CN",
                "Abigail",
                "Characters/Dialogue/Abigail",
                "fall_Mon"),
            DialogueSourceFamily.OrdinaryDaily,
            sourceText,
            SourceDialogueHasher.Compute(sourceText),
            "增强台词。",
            "generation-native-ui",
            "generation-key-native-ui",
            "trace-native-ui");
    }

    private static DialogueDisplayContext CreateDisplayContext()
    {
        DailyDialogueCacheEntry entry = CreateEntry();
        return new DialogueDisplayContext(
            entry.Key.GameDayIndex,
            entry.Key.Locale,
            entry.Key.NpcId,
            entry.Key.AssetName,
            entry.Key.DialogueKey,
            "原版台词。");
    }
}
