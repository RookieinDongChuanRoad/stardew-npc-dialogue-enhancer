using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewNpcAgent.Configuration;
using StardewNpcAgent.Game;
using StardewNpcAgent.Infrastructure.Storage;
using StardewValley;

namespace StardewNpcAgent.Application;

/// <summary>
/// 编排 Phase 2 静态普通日常对话 Spike 的 SMAPI 事件生命周期。
/// </summary>
/// <remarks>
/// 运行时只在新日或 locale 变化后预计算静态 cache，并在精确 dialogue asset 请求时做
/// Late edit。NPC 的原版交互方法仍负责礼物、任务、事件、问题、关系分支、好感奖励和
/// 原生 UI；本类不处理玩家输入，也不创建任何自定义菜单。
/// </remarks>
public sealed class DialogueSpikeRuntime
{
    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly ModConfig config;
    private readonly IReadOnlyList<string> targetNpcIds;
    private readonly DialogueCandidateResolver candidateResolver;
    private readonly DailyDialogueCache cache = new();
    private bool isInitialized;

    /// <summary>
    /// 创建运行时及纯逻辑依赖；构造本身不订阅事件、不读取游戏状态、不修改资产。
    /// </summary>
    /// <param name="helper">SMAPI helper。</param>
    /// <param name="monitor">只记录不含秘密的诊断信息。</param>
    /// <param name="config">已由 ModEntry 读取的本地配置。</param>
    public DialogueSpikeRuntime(IModHelper helper, IMonitor monitor, ModConfig config)
        : this(helper, monitor, config, GetLegacyConfiguredNpcIds(config))
    {
    }

    /// <summary>
    /// 使用 compatibility policy 已解析的 enabled ID 创建运行时；构造本身仍不订阅事件或读取世界状态。
    /// </summary>
    /// <param name="helper">SMAPI helper。</param>
    /// <param name="monitor">只记录稳定、无用户原文的诊断信息。</param>
    /// <param name="config">已通过通用 validator 的配置；静态 marker 仍从这里读取。</param>
    /// <param name="enabledNpcIds">已限定在固定支持集内、保留配置顺序的不可变语义列表。</param>
    public DialogueSpikeRuntime(
        IModHelper helper,
        IMonitor monitor,
        ModConfig config,
        IReadOnlyList<string> enabledNpcIds)
    {
        this.helper = helper ?? throw new ArgumentNullException(nameof(helper));
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        // 即使 policy 当前返回 read-only collection，runtime 仍复制快照，防止其他 public 调用方传入可变 List。
        targetNpcIds = EnabledNpcIdsSnapshot.Create(enabledNpcIds);
        candidateResolver = new DialogueCandidateResolver(helper);
    }

    /// <summary>
    /// 订阅运行时所需的四个公开事件。重复调用是编程错误，直接抛出以避免重复 edit。
    /// </summary>
    public void Initialize()
    {
        if (isInitialized)
        {
            throw new InvalidOperationException("DialogueSpikeRuntime 不能重复初始化。");
        }

        helper.Events.GameLoop.DayStarted += OnDayStarted;
        helper.Events.Content.LocaleChanged += OnLocaleChanged;
        helper.Events.Content.AssetRequested += OnAssetRequested;
        helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
        isInitialized = true;

        monitor.Log(
            $"静态普通日常对话 Spike 已启用；enabled_count={targetNpcIds.Count}。",
            LogLevel.Info);
    }

    /// <summary>
    /// 新日世界状态可读后重建 cache；所有 NPC 独立失败，不阻止其他 NPC 或游戏继续。
    /// </summary>
    private void OnDayStarted(object? sender, DayStartedEventArgs eventArgs)
    {
        RebuildDailyCache("day_started");
    }

    /// <summary>
    /// locale 改变后先失效旧语言资产，再按新语言重建，禁止跨 locale 复用文本或 hash。
    /// </summary>
    private void OnLocaleChanged(object? sender, LocaleChangedEventArgs eventArgs)
    {
        RebuildDailyCache("locale_changed");
    }

    /// <summary>
    /// 返回标题时移除本存档的内存结果；不会访问已卸载的 NPC 世界对象。
    /// </summary>
    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs eventArgs)
    {
        InvalidateAndClearExistingCache(resetNpcCopies: false);
        monitor.Log("已清理静态普通日常对话 cache。", LogLevel.Trace);
    }

    /// <summary>
    /// 对精确命中的 NPC dialogue asset 注册 Late edit，并在回调内重新计算当前 source hash。
    /// </summary>
    private void OnAssetRequested(object? sender, AssetRequestedEventArgs eventArgs)
    {
        if (!Context.IsWorldReady)
        {
            return;
        }

        int gameDayIndex = Game1.Date.TotalDays;
        string locale = GetCurrentLocale();
        IReadOnlyList<DailyDialogueCacheEntry> matchingEntries = cache.Snapshot()
            .Where(entry => entry.Key.GameDayIndex == gameDayIndex)
            .Where(entry => string.Equals(entry.Key.Locale, locale, StringComparison.Ordinal))
            .Where(entry => eventArgs.Name.IsEquivalentTo(entry.Key.AssetName, useBaseName: true))
            .ToArray();
        if (matchingEntries.Count == 0)
        {
            return;
        }

        eventArgs.Edit(
            asset => ApplyCachedDialogueEdits(asset, matchingEntries, gameDayIndex, locale),
            AssetEditPriority.Late);
    }

    /// <summary>
    /// 在 SMAPI 已应用更早编辑后取得最终字典，并逐项执行纯 patch policy。
    /// </summary>
    /// <param name="asset">当前 asset edit 数据。</param>
    /// <param name="entries">同日、同 locale、同 base asset 的候选项。</param>
    /// <param name="gameDayIndex">事件发生时的绝对游戏日快照。</param>
    /// <param name="locale">事件发生时的 locale 快照。</param>
    private void ApplyCachedDialogueEdits(
        IAssetData asset,
        IReadOnlyList<DailyDialogueCacheEntry> entries,
        int gameDayIndex,
        string locale)
    {
        try
        {
            IDictionary<string, string> dialogueData = asset.AsDictionary<string, string>().Data;
            string normalizedAssetName = DialogueKeyClassifier.NormalizeAssetName(asset.Name.BaseName);
            DialogueAssetPatchContext context = new(gameDayIndex, locale, normalizedAssetName);

            foreach (DailyDialogueCacheEntry entry in entries)
            {
                try
                {
                    DialogueAssetPatchDecision decision = DialogueAssetPatchPolicy.Apply(
                        dialogueData,
                        entry,
                        context);
                    monitor.Log(
                        $"静态对话资产检查 npc={entry.Key.NpcId}, key={entry.Key.DialogueKey}, result={decision.ReasonCode}。",
                        LogLevel.Trace);
                }
                catch (Exception exception)
                {
                    // 单个 entry 的意外错误不能中断资产加载；保留当前字典即是完整原版 fallback。
                    monitor.Log(
                        $"静态对话资产项处理失败 npc={entry.Key.NpcId}, key={entry.Key.DialogueKey}: "
                            + $"{exception.GetType().Name}: {exception.Message}",
                        LogLevel.Error);
                }
            }
        }
        catch (Exception exception)
        {
            // 资产类型或其他边界异常只影响本次增强，绝不能阻止游戏内容管线返回原资产。
            monitor.Log(
                $"静态对话资产编辑失败：{exception.GetType().Name}: {exception.Message}",
                LogLevel.Error);
        }
    }

    /// <summary>
    /// 先失效并清空旧 cache，再逐 NPC 解析、静态增强、保存并准备下一次原版 lazy load。
    /// </summary>
    /// <param name="trigger">仅用于无秘密诊断的触发来源。</param>
    private void RebuildDailyCache(string trigger)
    {
        InvalidateAndClearExistingCache(resetNpcCopies: Context.IsWorldReady);
        if (!Context.IsWorldReady)
        {
            monitor.Log($"跳过静态对话重建 trigger={trigger}：世界尚未就绪。", LogLevel.Trace);
            return;
        }

        int gameDayIndex = Game1.Date.TotalDays;
        string locale = GetCurrentLocale();
        foreach (string npcId in targetNpcIds)
        {
            try
            {
                NPC? npc = Game1.getCharacterFromName(npcId);
                if (npc is null)
                {
                    monitor.Log($"跳过静态对话 npc={npcId}：当前世界未找到 NPC。", LogLevel.Trace);
                    continue;
                }

                DialogueCandidateResolution resolution = candidateResolver.Resolve(
                    npc,
                    locale,
                    targetNpcIds);
                if (!resolution.IsResolved || resolution.Candidate is null)
                {
                    monitor.Log(
                        $"跳过静态对话 npc={npcId}, result={resolution.ReasonCode}, "
                            + $"eligibility={resolution.EligibilityReasonCode?.ToString() ?? "n/a"}。",
                        LogLevel.Trace);
                    continue;
                }

                DialogueCandidate candidate = resolution.Candidate;
                StaticDialogueEnhancementResult enhancement = StaticDialogueEnhancer.TryEnhance(
                    candidate.SourceText,
                    config.StaticDialogueMarker);
                if (!enhancement.IsSuccessful || enhancement.EnhancedText is null)
                {
                    monitor.Log(
                        $"跳过静态对话 npc={npcId}, enhancer={enhancement.ReasonCode}。",
                        LogLevel.Trace);
                    continue;
                }

                DailyDialogueCacheKey cacheKey = new(
                    gameDayIndex,
                    locale,
                    candidate.NpcId,
                    candidate.AssetName,
                    candidate.DialogueKey);
                cache.Store(
                    new DailyDialogueCacheEntry(
                        cacheKey,
                        candidate.SourceFamily,
                        candidate.SourceText,
                        candidate.SourceHash,
                        enhancement.EnhancedText));

                // Resolver 读取过 NPC 的 seasonal dialogue 副本。存入 cache 后必须先失效
                // 精确 content asset，再只清该副本；原版下一次需要台词时会重新请求资产，
                // 触发上面的 Late edit。这里不清日常对话栈，也不主动显示任何文本。
                helper.GameContent.InvalidateCache(candidate.AssetName);
                npc.resetSeasonalDialogue();

                monitor.Log(
                    $"已准备静态对话 npc={npcId}, asset={candidate.AssetName}, key={candidate.DialogueKey}, "
                        + $"styles={candidate.StyleExamples.Count}。",
                    LogLevel.Trace);
            }
            catch (Exception exception)
            {
                // 每个 NPC 独立隔离；内容读取或公开 API 异常只让该 NPC 走原版。
                monitor.Log(
                    $"静态对话预生成失败 npc={npcId}: {exception.GetType().Name}: {exception.Message}",
                    LogLevel.Error);
            }
        }
    }

    /// <summary>
    /// 严格按“先失效旧资产，再清 cache”顺序处理跨日、locale 和返回标题边界。
    /// </summary>
    /// <param name="resetNpcCopies">世界仍可读时，是否同时清对应 NPC 的 seasonal asset 副本。</param>
    private void InvalidateAndClearExistingCache(bool resetNpcCopies)
    {
        IReadOnlyList<DailyDialogueCacheEntry> oldEntries = cache.Snapshot();

        foreach (string assetName in oldEntries
                     .Select(entry => entry.Key.AssetName)
                     .Distinct(StringComparer.Ordinal))
        {
            try
            {
                helper.GameContent.InvalidateCache(assetName);
            }
            catch (Exception exception)
            {
                monitor.Log(
                    $"旧静态对话资产失效失败 asset={assetName}: "
                        + $"{exception.GetType().Name}: {exception.Message}",
                    LogLevel.Error);
            }
        }

        if (resetNpcCopies)
        {
            foreach (string npcId in oldEntries
                         .Select(entry => entry.Key.NpcId)
                         .Distinct(StringComparer.Ordinal))
            {
                try
                {
                    Game1.getCharacterFromName(npcId)?.resetSeasonalDialogue();
                }
                catch (Exception exception)
                {
                    monitor.Log(
                        $"旧 seasonal dialogue 副本清理失败 npc={npcId}: "
                            + $"{exception.GetType().Name}: {exception.Message}",
                        LogLevel.Error);
                }
            }
        }

        cache.Clear();
    }

    /// <summary>
    /// 把 SMAPI 用空字符串表示的英文规范化为稳定非空 cache key。
    /// </summary>
    private string GetCurrentLocale()
    {
        string locale = helper.GameContent.CurrentLocale;
        return string.IsNullOrEmpty(locale) ? "en" : locale;
    }

    /// <summary>
    /// 保留旧 public 构造函数的行为：只对旧入口执行既有 trim/dedupe，不引入新的 supported 过滤语义。
    /// </summary>
    private static IReadOnlyList<string> GetLegacyConfiguredNpcIds(ModConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.GetNormalizedTargetNpcIds();
    }
}
