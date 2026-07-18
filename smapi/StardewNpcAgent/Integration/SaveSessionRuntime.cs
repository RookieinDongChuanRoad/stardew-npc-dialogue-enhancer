using System.Text.Json;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewNpcAgent.Application;
using StardewNpcAgent.Configuration;
using StardewNpcAgent.Contracts;
using StardewNpcAgent.Game;
using StardewNpcAgent.Infrastructure.Http;
using StardewNpcAgent.Infrastructure.Storage;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Menus;
using StardewValley.Tools;

namespace StardewNpcAgent.Integration;

/// <summary>
/// 编排正式 Agent 模式的 save session、每日后台生成与主线程结果激活。
/// </summary>
/// <remarks>
/// 所有 Game1/NPC/content 读取都发生在 SMAPI 事件主线程；后台 Task 只操作 immutable DTO、
/// HTTP client、durable outbox 与独立 staging cache。任务不会在返回标题/locale 变化时被取消，
/// 而是通过 session generation 在应用前拒绝，避免迟到结果污染新存档。
/// </remarks>
public sealed class SaveSessionRuntime
{
    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly IReadOnlyList<string> targetNpcIds;
    private readonly DialogueCandidateResolver candidateResolver;
    private readonly DailyDialogueCache liveCache = new();
    private readonly MainThreadCompletionQueue completionQueue = new();
    private readonly SessionGenerationGate sessionGate = new();
    private readonly DialogueDisplayObserver displayObserver = new();
    private readonly HttpClient httpClient;
    private readonly AgentApiClient apiClient;
    private readonly TimeSpan generationRequestDeadline;
    private readonly SmapiEventBindings eventBindings;
    private SavePartitionIdentity? identity;
    private DurableEventOutbox? eventOutbox;
    private DurableDisplayAckOutbox? displayAckOutbox;
    private EventOutboxCoordinator? eventCoordinator;
    private DisplayAckOutboxCoordinator? displayAckCoordinator;
    private DialogueDisplayCoordinator? displayCoordinator;
    private LoadedDialogueStackCoordinator? loadedDialogueCoordinator;
    private ProducerSessionState? producerSessionState;
    private PlayerProgressionSessionState? playerProgressionSessionState;
    private WorldProgressionSessionState? worldProgressionSessionState;
    private bool isApplyingOwnAssetInvalidation;

    public SaveSessionRuntime(
        IModHelper helper,
        IMonitor monitor,
        ModConfig config,
        ValidatedAgentConfiguration agentConfiguration)
        : this(
            helper,
            monitor,
            config,
            agentConfiguration,
            GetLegacyConfiguredNpcIds(config))
    {
    }

    /// <summary>
    /// 使用 compatibility policy 已解析的 enabled ID 创建正式 runtime。
    /// </summary>
    /// <remarks>
    /// 构造函数会创建 HttpClient 与事件 bindings，但不会订阅事件；因此 ModEntry 必须在调用前完成
    /// inspector、读取、compatibility、validation 与零 enabled gate。
    /// </remarks>
    /// <param name="helper">SMAPI helper。</param>
    /// <param name="monitor">稳定诊断 logger。</param>
    /// <param name="config">保留在签名中以兼容统一装配边界；必须已经非 null。</param>
    /// <param name="agentConfiguration">已验证的 loopback URL 与 timeout。</param>
    /// <param name="enabledNpcIds">已解析、保留用户顺序且限定在支持集内的 NPC ID。</param>
    public SaveSessionRuntime(
        IModHelper helper,
        IMonitor monitor,
        ModConfig config,
        ValidatedAgentConfiguration agentConfiguration,
        IReadOnlyList<string> enabledNpcIds)
    {
        this.helper = helper ?? throw new ArgumentNullException(nameof(helper));
        this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(agentConfiguration);
        // runtime 拥有自己的只读副本，避免 public overload 的调用方在构造后修改原 List。
        targetNpcIds = EnabledNpcIdsSnapshot.Create(enabledNpcIds);
        candidateResolver = new DialogueCandidateResolver(helper);
        httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        apiClient = new AgentApiClient(
            httpClient,
            agentConfiguration.BackendBaseUri,
            new AgentApiTimeouts(
                agentConfiguration.EventRequestTimeout,
                agentConfiguration.GenerationRequestTimeout,
                agentConfiguration.DisplayAckRequestTimeout));
        // cohort coordinator 与 HTTP adapter 使用同一份已验证配置。前者给两个 batch 建立
        // 一个绝对 deadline，后者继续作为单请求的防御性 timeout；任一更早触发都安全回退。
        generationRequestDeadline = agentConfiguration.GenerationRequestTimeout;
        eventBindings = new SmapiEventBindings(helper, this);
    }

    public void Initialize()
    {
        eventBindings.Initialize();
        monitor.Log(
            $"Stardew NPC Agent 正式模式已启用；enabled_count={targetNpcIds.Count}。",
            LogLevel.Info);
    }

    internal void HandleSaveLoaded()
    {
        try
        {
            InvalidateLiveCache(resetNpcCopies: Context.IsWorldReady);
            string saveFolderName = Constants.SaveFolderName
                ?? throw new InvalidOperationException("SMAPI 尚未提供当前 save folder identity。");
            identity = SaveIdentityProvider.Create(
                saveFolderName,
                Game1.player.UniqueMultiplayerID,
                helper.DirectoryPath);
            Directory.CreateDirectory(identity.PartitionDirectory);
            eventOutbox = DurableEventOutbox.Open(
                identity.EventOutboxPath,
                identity.SaveId,
                identity.PlayerId);
            displayAckOutbox = DurableDisplayAckOutbox.Open(
                identity.DisplayAckOutboxPath,
                identity.SaveId,
                identity.PlayerId);
            eventCoordinator = new EventOutboxCoordinator(eventOutbox, apiClient);
            displayAckCoordinator = new DisplayAckOutboxCoordinator(displayAckOutbox, apiClient);
            displayCoordinator = new DialogueDisplayCoordinator(
                identity.SaveId,
                identity.PlayerId,
                liveCache,
                displayAckOutbox);
            loadedDialogueCoordinator = new LoadedDialogueStackCoordinator(
                liveCache,
                displayCoordinator,
                displayObserver);
            sessionGate.StartSession(identity.SaveId, identity.PlayerId);
            InitializeProducerStates(eventOutbox);
            ScheduleDisplayAckFlush("save_loaded");
            monitor.Log("已初始化当前存档的 Agent outbox 分区。", LogLevel.Trace);
        }
        catch (Exception exception)
        {
            // snapshot 腐化或本地目录失败时保持原版；绝不覆盖/删除现有 durable 文件。
            sessionGate.EndSession();
            identity = null;
            eventOutbox = null;
            displayAckOutbox = null;
            eventCoordinator = null;
            displayAckCoordinator = null;
            displayCoordinator = null;
            loadedDialogueCoordinator = null;
            producerSessionState = null;
            playerProgressionSessionState = null;
            worldProgressionSessionState = null;
            LogStableFailure("SAVE_SESSION_INITIALIZATION_FAILED", exception);
        }
    }

    internal void HandleDayStarted()
    {
        if (!Context.IsWorldReady
            || identity is null
            || eventCoordinator is null)
        {
            return;
        }

        try
        {
            sessionGate.InvalidatePendingWork();
            InvalidateLiveCache(resetNpcCopies: true);
            int gameDayIndex = Game1.Date.TotalDays;
            // 若上一日 DayEnding 后没有收到 Saved，旧 facility stage 不能跨日伪装成已保存事实。
            worldProgressionSessionState?.DiscardStaged();
            ReconcilePlayerAndNpcProducers(gameDayIndex, "day_started");
            string locale = GetCurrentLocale();
            GenerationSessionToken token = sessionGate.CaptureDay(gameDayIndex, locale);
            DayStartedGenerationPreparation preparation = BuildDayStartedPreparation(
                identity,
                gameDayIndex,
                locale);
            DailyDialogueCache stagingCache = new();
            DailyGenerationBatchCoordinator coordinator = new(
                eventCoordinator,
                apiClient,
                stagingCache,
                generationRequestDeadline);
            _ = RunDailyGenerationAsync(token, preparation, stagingCache, coordinator);
            ScheduleDisplayAckFlush("day_started");
        }
        catch (Exception exception)
        {
            LogStableFailure("DAY_STARTED_INPUT_FAILED", exception);
        }
    }

    internal void HandleDayEnding()
    {
        if (!Context.IsWorldReady)
        {
            return;
        }

        int gameDayIndex = Game1.Date.TotalDays;
        ReconcilePlayerAndNpcProducers(gameDayIndex, "day_ending");
        StagePublicFacilities(gameDayIndex);

        // 事件 handler 只调度，不等待网络，因此后端故障不会阻止睡觉或存档。
        ScheduleEventFlush(gameDayIndex, "day_ending");
        ScheduleDisplayAckFlush("day_ending");
    }

    /// <summary>
    /// 只有 SMAPI 确认本次保存完成后，才把 DayEnding 暂存的公共设施变化写入 outbox。
    /// </summary>
    internal void HandleSaved()
    {
        if (!Context.IsWorldReady || worldProgressionSessionState is null)
        {
            return;
        }

        try
        {
            int committed = worldProgressionSessionState.CommitSaved();
            if (committed > 0)
            {
                ScheduleEventFlush(Game1.Date.TotalDays, "saved_facility");
            }
        }
        catch (Exception exception)
        {
            // stage 保留供同一会话后续 Saved 重试；原版保存结果已经完成，不能向外抛出。
            LogStableFailure("PUBLIC_FACILITY_COMMIT_FAILED", exception);
        }
    }

    /// <summary>
    /// 以 SMAPI 的一秒公共事件对账小型 producer snapshot；不做每帧磁盘写入或网络轮询。
    /// </summary>
    internal void HandleOneSecondUpdateTicked()
    {
        if (!Context.IsWorldReady)
        {
            return;
        }

        int gameDayIndex = Game1.Date.TotalDays;
        if (ReconcilePlayerAndNpcProducers(gameDayIndex, "one_second"))
        {
            ScheduleEventFlush(gameDayIndex, "one_second_producer");
        }
    }

    internal void HandleUpdateTicked()
    {
        try
        {
            completionQueue.Drain();
        }
        catch (Exception exception)
        {
            LogStableFailure("MAIN_THREAD_COMPLETION_FAILED", exception);
        }
    }

    internal void HandleReturnedToTitle()
    {
        sessionGate.EndSession();
        InvalidateLiveCache(resetNpcCopies: false);
        producerSessionState?.Clear();
        playerProgressionSessionState?.Clear();
        worldProgressionSessionState?.DiscardStaged();
        identity = null;
        eventOutbox = null;
        displayAckOutbox = null;
        eventCoordinator = null;
        displayAckCoordinator = null;
        displayCoordinator = null;
        loadedDialogueCoordinator = null;
        producerSessionState = null;
        playerProgressionSessionState = null;
        worldProgressionSessionState = null;
    }

    internal void HandleLocaleChanged()
    {
        sessionGate.InvalidatePendingWork();
        InvalidateLiveCache(resetNpcCopies: Context.IsWorldReady);
    }

    internal void HandleAssetsInvalidated(AssetsInvalidatedEventArgs eventArgs)
    {
        if (isApplyingOwnAssetInvalidation || liveCache.Snapshot().Count == 0)
        {
            return;
        }

        IReadOnlyList<string> liveAssetNames = liveCache.Snapshot()
            .Select(entry => entry.Key.AssetName)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        bool affectsLiveDialogue = eventArgs.NamesWithoutLocale.Any(
            invalidated => liveAssetNames.Any(
                assetName => invalidated.IsEquivalentTo(assetName, useBaseName: true)));
        if (affectsLiveDialogue)
        {
            sessionGate.InvalidatePendingWork();
            InvalidateLiveCache(resetNpcCopies: Context.IsWorldReady);
        }
    }

    internal void HandleAssetRequested(AssetRequestedEventArgs eventArgs)
    {
        DialogueDisplayCoordinator? currentDisplayCoordinator = displayCoordinator;
        LoadedDialogueStackCoordinator? currentLoadedDialogueCoordinator =
            loadedDialogueCoordinator;
        if (!Context.IsWorldReady || currentDisplayCoordinator is null)
        {
            return;
        }

        int gameDayIndex = Game1.Date.TotalDays;
        string locale = GetCurrentLocale();
        IReadOnlyList<DailyDialogueCacheEntry> matchingEntries = liveCache.Snapshot()
            .Where(entry => entry.Key.GameDayIndex == gameDayIndex)
            .Where(entry => string.Equals(entry.Key.Locale, locale, StringComparison.Ordinal))
            .Where(entry => eventArgs.Name.IsEquivalentTo(entry.Key.AssetName, useBaseName: true))
            // direct entry 为等待首帧 ACK 暂留 live cache，但已经写入当前 DialogueLine；
            // 这里必须排除它，保持 direct 与 Late asset 两条注入路线互斥。
            .Where(entry => currentLoadedDialogueCoordinator?.IsTrackedDirectKey(entry.Key) != true)
            .ToArray();
        if (matchingEntries.Count == 0)
        {
            return;
        }

        eventArgs.Edit(
            asset =>
            {
                try
                {
                    if (!Context.IsWorldReady
                        || !ReferenceEquals(displayCoordinator, currentDisplayCoordinator)
                        || Game1.Date.TotalDays != gameDayIndex
                        || !string.Equals(GetCurrentLocale(), locale, StringComparison.Ordinal))
                    {
                        return;
                    }

                    IDictionary<string, string> dialogueData =
                        asset.AsDictionary<string, string>().Data;
                    DialogueInjectionResult result = DialogueInjectionAdapter.Apply(
                        dialogueData,
                        asset.Name.BaseName,
                        gameDayIndex,
                        locale,
                        matchingEntries,
                        currentDisplayCoordinator,
                        displayObserver,
                        GetFilteredLocalPlayerName());
                    monitor.Log(
                        $"正式对话资产检查 asset={asset.Name.BaseName}, applied={result.AppliedCount}。",
                        LogLevel.Trace);
                }
                catch (Exception exception)
                {
                    // Asset edit 失败必须保留当前字典；SMAPI/原版内容管线继续完成加载。
                    LogStableFailure("DIALOGUE_ASSET_EDIT_FAILED", exception);
                }
            },
            AssetEditPriority.Late);
    }

    internal void HandleMenuChanged(MenuChangedEventArgs eventArgs)
    {
        ReconcilePlayerProgressionAfterMenuChange(eventArgs);

        DialogueMenuSnapshot? snapshot = Context.IsWorldReady
            ? CaptureDialogueMenuSnapshot(eventArgs.NewMenu)
            : null;
        if (snapshot is null)
        {
            displayObserver.ObserveMenu(null, 0, "en");
            return;
        }

        displayObserver.ObserveMenu(
            snapshot,
            Game1.Date.TotalDays,
            GetCurrentLocale());
    }

    internal void HandleRenderedActiveMenu()
    {
        DialogueDisplayCoordinator? currentCoordinator = displayCoordinator;
        LoadedDialogueStackCoordinator? currentLoadedCoordinator = loadedDialogueCoordinator;
        if (!Context.IsWorldReady || currentCoordinator is null)
        {
            return;
        }

        DialogueMenuSnapshot? currentSnapshot = CaptureDialogueMenuSnapshot(
            Game1.activeClickableMenu);
        if (currentSnapshot is null)
        {
            return;
        }

        RenderedDialogueObservation? observation = displayObserver.TryObserveRendered(
            currentSnapshot,
            Game1.Date.TotalDays,
            GetCurrentLocale());
        if (observation is null)
        {
            return;
        }

        try
        {
            DisplayAckEnqueueResult ackResult = currentCoordinator.RecordDisplayed(
                observation.Decision,
                observation.Confirmation);
            if (ackResult.Status is DisplayAckStatus.Accepted or DisplayAckStatus.Duplicate)
            {
                DailyDialogueCacheKey? directKey = currentLoadedCoordinator?.MarkDisplayed(
                    observation.Decision);
                if (directKey is not null)
                {
                    liveCache.Remove(directKey);
                }
            }

            displayObserver.Complete(observation);
            ScheduleDisplayAckFlush("rendered_dialogue");
        }
        catch (Exception exception)
        {
            // 原生菜单已经绘制；本地 ACK 写失败只能记录并在后续帧/生命周期重试，
            // 绝不能关闭菜单或把玩家已经看到的文本改回异常说明。
            LogStableFailure("DISPLAY_ACK_ENQUEUE_FAILED", exception);
        }
    }

    internal void HandleLevelChanged(LevelChangedEventArgs eventArgs)
    {
        if (!Context.IsWorldReady || eventOutbox is null)
        {
            return;
        }

        try
        {
            GameEvent? gameEvent = PlayerProgressionEventCollector.CollectSkillLevelReached(
                new LevelChangedFact(
                    eventArgs.IsLocalPlayer,
                    Game1.Date.TotalDays,
                    eventArgs.Skill.ToString(),
                    eventArgs.OldLevel,
                    eventArgs.NewLevel));
            if (gameEvent is null)
            {
                return;
            }

            eventOutbox.Enqueue(gameEvent);
            ScheduleEventFlush(gameEvent.OccurredDayIndex, "level_changed");
        }
        catch (Exception exception)
        {
            // 本地 persistence 失败不改变升级结果或玩家流程；事实不会被伪装成已上传。
            LogStableFailure("LEVEL_EVENT_ENQUEUE_FAILED", exception);
        }
    }

    /// <summary>
    /// 进入矿井后读取本地玩家的公开 high-water，并只提交 collector 认可的新里程碑。
    /// </summary>
    internal void HandleWarped(WarpedEventArgs eventArgs)
    {
        if (!Context.IsWorldReady
            || playerProgressionSessionState is null
            || !eventArgs.IsLocalPlayer
            || eventArgs.NewLocation is not MineShaft mineShaft
            || mineShaft.mineLevel == 77377)
        {
            return;
        }

        try
        {
            bool enqueued = playerProgressionSessionState.ObserveMineDepth(
                Game1.Date.TotalDays,
                isLocalPlayer: true,
                eventArgs.Player.deepestMineLevel);
            if (enqueued)
            {
                ScheduleEventFlush(Game1.Date.TotalDays, "mine_depth");
            }
        }
        catch (Exception exception)
        {
            // 矿井 producer 失败不影响 warp；durable 写失败时 session state 不推进。
            LogStableFailure("MINE_DEPTH_OBSERVATION_FAILED", exception);
        }
    }

    /// <summary>
    /// 用 InventoryChanged 的 Added 工具确认领取；先匹配旧 pending，再读取原版当前 field。
    /// </summary>
    internal void HandleInventoryChanged(InventoryChangedEventArgs eventArgs)
    {
        PlayerProgressionSessionState? currentState = playerProgressionSessionState;
        if (!Context.IsWorldReady || currentState is null || !eventArgs.IsLocalPlayer)
        {
            return;
        }

        try
        {
            ReceivedToolObservation[] receivedTools = eventArgs.Added
                .OfType<Tool>()
                .Select(TryCreateReceivedToolObservation)
                .Where(item => item is not null)
                .Cast<ReceivedToolObservation>()
                .ToArray();
            int enqueued = currentState.ObserveReceivedTools(
                Game1.Date.TotalDays,
                isLocalPlayer: true,
                receivedTools);

            // 领取代码会在 Added 回调前清空 toolBeingUpgraded。必须在上面的匹配完成后才同步
            // null，否则会丢失这次领取；若匹配失败则清除过期 pending，避免日后箱中旧工具误报。
            currentState.ObservePendingToolUpgrade(CapturePendingToolUpgrade(eventArgs.Player));
            if (enqueued > 0)
            {
                ScheduleEventFlush(Game1.Date.TotalDays, "tool_received");
            }
        }
        catch (Exception exception)
        {
            LogStableFailure("TOOL_RECEIPT_OBSERVATION_FAILED", exception);
        }
    }

    /// <summary>
    /// Harmony postfix 只交付不可变 fact；运行时再次执行目标 NPC 与当前 save session 边界。
    /// </summary>
    internal void HandleGiftGiven(GiftGivenFact fact)
    {
        if (!Context.IsWorldReady
            || eventOutbox is null
            || !targetNpcIds.Contains(fact.NpcId, StringComparer.Ordinal))
        {
            return;
        }

        try
        {
            GameEvent? gameEvent = NpcHistoryEventCollector.CollectGiftGiven(fact);
            if (gameEvent is null)
            {
                return;
            }

            eventOutbox.Enqueue(gameEvent);
            ScheduleEventFlush(gameEvent.OccurredDayIndex, "gift_given");
        }
        catch (Exception exception)
        {
            // Postfix sink 不得把任何异常传播回原版 onGiftGiven 调用路径。
            LogStableFailure("GIFT_EVENT_ENQUEUE_FAILED", exception);
        }
    }

    /// <summary>
    /// 分别初始化三个 producer 领域；一个领域的公开状态不合法时只禁用该领域。
    /// </summary>
    /// <param name="currentEventOutbox">当前 save/player 分区的 durable outbox。</param>
    private void InitializeProducerStates(DurableEventOutbox currentEventOutbox)
    {
        producerSessionState = null;
        playerProgressionSessionState = null;
        worldProgressionSessionState = null;
        int gameDayIndex = Game1.Date.TotalDays;

        try
        {
            ProducerSessionState npcState = new(currentEventOutbox, targetNpcIds);
            foreach (string npcId in targetNpcIds)
            {
                try
                {
                    npcState.InitializeNpcBaseline(
                        gameDayIndex,
                        CaptureNpcFriendshipObservation(npcId));
                    string? disabledReason = npcState.GetFriendshipMilestoneDisableReason(npcId);
                    if (disabledReason is not null)
                    {
                        monitor.Log(
                            $"NPC_MILESTONE_PRODUCER_DISABLED npc={npcId}, reason={disabledReason}。",
                            LogLevel.Warn);
                    }
                }
                catch (Exception exception)
                {
                    // 单个目标 NPC 状态异常不能阻止其他目标的关系 producer 建立 baseline。
                    LogStableFailure($"NPC_BASELINE_INITIALIZATION_FAILED_{npcId}", exception);
                }
            }

            producerSessionState = npcState;
        }
        catch (Exception exception)
        {
            LogStableFailure("NPC_PRODUCER_INITIALIZATION_FAILED", exception);
        }

        try
        {
            PlayerProgressionSessionState playerState = new(currentEventOutbox);
            playerState.InitializeBaselines(
                Game1.player.deepestMineLevel,
                CapturePendingToolUpgrade(Game1.player),
                Game1.player.trashCanLevel,
                CaptureMasteryClaimValues(Game1.player));
            playerProgressionSessionState = playerState;
        }
        catch (Exception exception)
        {
            LogStableFailure("PLAYER_PROGRESSION_INITIALIZATION_FAILED", exception);
        }

        try
        {
            WorldProgressionSessionState worldState = new(currentEventOutbox);
            worldState.InitializeBaseline(CapturePublicFacilitySnapshot());
            worldProgressionSessionState = worldState;
        }
        catch (Exception exception)
        {
            LogStableFailure("WORLD_PROGRESSION_INITIALIZATION_FAILED", exception);
        }
    }

    /// <summary>
    /// 对账不会主动 flush；调用方按生命周期决定是否立即调度网络。
    /// </summary>
    /// <returns>本次是否新增至少一条 durable pending event。</returns>
    private bool ReconcilePlayerAndNpcProducers(int occurredDayIndex, string trigger)
    {
        int pendingBefore = eventOutbox?.PendingCount ?? 0;
        PlayerProgressionSessionState? playerState = playerProgressionSessionState;
        if (playerState is not null)
        {
            try
            {
                PendingToolUpgrade? pending = CapturePendingToolUpgrade(Game1.player);
                if (pending is not null)
                {
                    // 一秒/日边界只刷新非空 pending。原版领取会先清 field，真正的 null 清理应由
                    // InventoryChanged/MenuChanged 在完成领取确认后执行。
                    playerState.ObservePendingToolUpgrade(pending);
                }
            }
            catch (Exception exception)
            {
                LogStableFailure($"PENDING_TOOL_RECONCILIATION_FAILED_{trigger}", exception);
            }

            try
            {
                playerState.ObserveTrashCanLevel(
                    occurredDayIndex,
                    isLocalPlayer: true,
                    Game1.player.trashCanLevel);
            }
            catch (Exception exception)
            {
                LogStableFailure($"TRASH_CAN_RECONCILIATION_FAILED_{trigger}", exception);
            }
        }

        ProducerSessionState? npcState = producerSessionState;
        if (npcState is not null)
        {
            foreach (string npcId in targetNpcIds)
            {
                try
                {
                    npcState.ReconcileNpcObservation(
                        occurredDayIndex,
                        CaptureNpcFriendshipObservation(npcId));
                }
                catch (Exception exception)
                {
                    LogStableFailure($"NPC_RECONCILIATION_FAILED_{trigger}_{npcId}", exception);
                }
            }
        }

        return (eventOutbox?.PendingCount ?? 0) > pendingBefore;
    }

    /// <summary>
    /// 菜单切换是工具下单/垃圾桶领取/精通领取的低频确认点；各子 producer 独立失败。
    /// </summary>
    private void ReconcilePlayerProgressionAfterMenuChange(MenuChangedEventArgs eventArgs)
    {
        PlayerProgressionSessionState? currentState = playerProgressionSessionState;
        if (!Context.IsWorldReady || currentState is null)
        {
            return;
        }

        int gameDayIndex = Game1.Date.TotalDays;
        bool enqueued = false;
        if (eventArgs.OldMenu is MasteryTrackerMenu
            && eventArgs.NewMenu is not MasteryTrackerMenu)
        {
            try
            {
                enqueued = currentState.ReconcileMasteryClaims(
                    gameDayIndex,
                    isLocalPlayer: true,
                    CaptureMasteryClaimValues(Game1.player)) > 0;
            }
            catch (Exception exception)
            {
                LogStableFailure("MASTERY_RECONCILIATION_FAILED", exception);
            }
        }

        try
        {
            enqueued |= currentState.ObserveTrashCanLevel(
                gameDayIndex,
                isLocalPlayer: true,
                Game1.player.trashCanLevel);
        }
        catch (Exception exception)
        {
            LogStableFailure("TRASH_CAN_MENU_RECONCILIATION_FAILED", exception);
        }

        try
        {
            // 先确认上面的 trash level，再接受当前 null/non-null pending，防止领取瞬间丢事实。
            currentState.ObservePendingToolUpgrade(CapturePendingToolUpgrade(Game1.player));
        }
        catch (Exception exception)
        {
            LogStableFailure("PENDING_TOOL_MENU_RECONCILIATION_FAILED", exception);
        }

        if (enqueued)
        {
            ScheduleEventFlush(gameDayIndex, "menu_progression");
        }
    }

    /// <summary>
    /// DayEnding 只 stage 当前五项 world snapshot；失败不会影响既有 event/ACK flush。
    /// </summary>
    private void StagePublicFacilities(int occurredDayIndex)
    {
        try
        {
            worldProgressionSessionState?.StageDayEnding(
                occurredDayIndex,
                CapturePublicFacilitySnapshot());
        }
        catch (Exception exception)
        {
            worldProgressionSessionState?.DiscardStaged();
            LogStableFailure("PUBLIC_FACILITY_STAGING_FAILED", exception);
        }
    }

    /// <summary>
    /// 将玩家与目标 NPC 的公开 Friendship 状态冻结为纯值，不把游戏对象交给 producer。
    /// </summary>
    private static NpcFriendshipObservation CaptureNpcFriendshipObservation(string npcId)
    {
        Friendship? friendship = null;
        Game1.player.friendshipData.TryGetValue(npcId, out friendship);
        RelationshipFacts? facts = ToRelationshipFacts(friendship);
        return new NpcFriendshipObservation(
            npcId,
            facts?.FriendshipPoints ?? 0,
            facts?.Status ?? RelationshipStatus.Friendly);
    }

    /// <summary>
    /// 普通工具的 pending 对象已带目标等级；垃圾桶只在领取时递增 Farmer.trashCanLevel。
    /// </summary>
    private static PendingToolUpgrade? CapturePendingToolUpgrade(Farmer player)
    {
        Tool? pendingTool = player.toolBeingUpgraded.Value;
        if (pendingTool is null)
        {
            return null;
        }

        if (pendingTool is GenericTool)
        {
            return string.Equals(pendingTool.ItemId, "TrashCan", StringComparison.Ordinal)
                ? new PendingToolUpgrade(
                    "trash_can",
                    checked(player.trashCanLevel + 1))
                : null;
        }

        string? toolId = TryMapSupportedRegularToolId(pendingTool);
        return toolId is null
            ? null
            : new PendingToolUpgrade(toolId, pendingTool.UpgradeLevel);
    }

    /// <summary>
    /// InventoryChanged 只接受五个原版精确运行时类型；旧箱子物品与 Mod GenericTool 不扩权。
    /// </summary>
    private static ReceivedToolObservation? TryCreateReceivedToolObservation(Tool tool)
    {
        string? toolId = TryMapSupportedRegularToolId(tool);
        return toolId is null ? null : new ReceivedToolObservation(toolId, tool.UpgradeLevel);
    }

    private static string? TryMapSupportedRegularToolId(Tool tool)
    {
        Type toolType = tool.GetType();
        if (toolType == typeof(Axe))
        {
            return "axe";
        }

        if (toolType == typeof(Pickaxe))
        {
            return "pickaxe";
        }

        if (toolType == typeof(Hoe))
        {
            return "hoe";
        }

        if (toolType == typeof(WateringCan))
        {
            return "watering_can";
        }

        return toolType == typeof(Pan) ? "pan" : null;
    }

    /// <summary>
    /// 只读取公开 Stats key；不访问 MasteryTrackerMenu 的私有选择或 canClaim 字段。
    /// </summary>
    private static IReadOnlyList<int> CaptureMasteryClaimValues(Farmer player)
    {
        int[] values = Enumerable.Range(0, 5)
            .Select(
                index => checked(
                    (int)player.stats.Get(StardewValley.Constants.StatKeys.Mastery(index))))
            .ToArray();
        return Array.AsReadOnly(values);
    }

    /// <summary>
    /// 使用 MasterPlayer 的路线中立 mail flags 构造精确五项设施 snapshot。
    /// </summary>
    private static IReadOnlyDictionary<string, bool> CapturePublicFacilitySnapshot()
    {
        return WorldProgressionEventCollector.Facilities.ToDictionary(
            definition => definition.FacilityId,
            definition => Game1.MasterPlayer.hasOrWillReceiveMail(definition.MailFlag),
            StringComparer.Ordinal);
    }

    private async Task RunDailyGenerationAsync(
        GenerationSessionToken token,
        DayStartedGenerationPreparation preparation,
        DailyDialogueCache stagingCache,
        DailyGenerationBatchCoordinator coordinator)
    {
        try
        {
            // 后台协调器只接收纯 DTO。preparation 中的 Stardew target token 只被闭包保留，
            // 不在后台读取；真正的对象复核和写入仍由 completion queue 回到主线程执行。
            DailyGenerationRunResult result = await coordinator.RunDayStartedAsync(preparation.Input)
                .ConfigureAwait(false);
            completionQueue.Enqueue(
                () => ApplyDailyGenerationResult(token, preparation, stagingCache, result));
        }
        catch (Exception exception)
        {
            completionQueue.Enqueue(
                () => LogStableFailure("DAILY_GENERATION_BACKGROUND_FAILED", exception));
        }
    }

    private void ApplyDailyGenerationResult(
        GenerationSessionToken token,
        DayStartedGenerationPreparation preparation,
        DailyDialogueCache stagingCache,
        DailyGenerationRunResult result)
    {
        if (result.Status != DailyGenerationRunStatus.Completed
            || !Context.IsWorldReady
            || !sessionGate.IsCurrent(token, Game1.Date.TotalDays, GetCurrentLocale()))
        {
            return;
        }

        IReadOnlyList<DialogueCandidate> currentCandidates =
            ResolvePreparedCandidatesForActivation(preparation.Candidates, token.Locale);
        DialogueActivationResult activation = StagedDialogueActivator.Activate(
            sessionGate,
            token,
            Game1.Date.TotalDays,
            GetCurrentLocale(),
            currentCandidates,
            stagingCache,
            liveCache);
        Dictionary<string, PreparedDialogueCandidate> preparedByNpc = preparation.Candidates
            .GroupBy(candidate => candidate.Candidate.NpcId, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(
                group => group.Key,
                group => group.Single(),
                StringComparer.Ordinal);
        LoadedDialogueStackCoordinator? currentLoadedCoordinator = loadedDialogueCoordinator;
        foreach (DailyDialogueCacheEntry entry in activation.ActivatedEntries)
        {
            if (!preparedByNpc.TryGetValue(
                    entry.Key.NpcId,
                    out PreparedDialogueCandidate? preparedCandidate))
            {
                // 正常情况下 activator 已拒绝未知 NPC；此防御分支避免未来重构让未准备项进入任一路线。
                liveCache.Remove(entry.Key);
                continue;
            }

            if (preparedCandidate.LoadedTarget is null)
            {
                // DayStarted 时尚未加载的 NPC 保留原有 Late asset 注入与 seasonal copy reset 路线。
                InvalidateAssetAndResetNpc(entry.Key.AssetName, entry.Key.NpcId);
                continue;
            }

            if (currentLoadedCoordinator is null)
            {
                liveCache.Remove(entry.Key);
                continue;
            }

            try
            {
                // LoadedDialogueStackCoordinator.Apply 在同一主线程回调中完成当前事实复核、
                // cache 决策、文本 CAS、observer arm 与 registry 登记。
                LoadedDialogueApplyResult applyResult = currentLoadedCoordinator.Apply(
                    preparedCandidate.LoadedTarget,
                    entry,
                    IsDialogueDisplayActive(),
                    GetFilteredLocalPlayerName());
                monitor.Log(
                    LoadedDialogueDiagnostics.FormatApply(entry.Key.NpcId, applyResult),
                    LogLevel.Trace);
            }
            catch (Exception exception)
            {
                // 协调器在抛出前已经尝试恢复文本并删除精确 cache/registry；这里仅记录稳定类型。
                LogStableFailure($"LOADED_DIALOGUE_APPLY_FAILED_{entry.Key.NpcId}", exception);
            }
        }

        monitor.Log(
            $"每日 Agent 生成完成；activated={activation.ActivatedEntries.Count}, "
                + $"cached={result.CachedEntryCount}, batches={result.BatchCount}, "
                + $"successful_batches={result.SuccessfulBatchCount}, "
                + $"failed_batches={result.FailedBatchCount}, "
                + $"request_ids={string.Join(",", result.RequestIds)}。",
            LogLevel.Trace);
    }

    /// <summary>
    /// 在主线程构造后端 DTO，并并行保存每个候选对应的 direct target token（若存在）。
    /// </summary>
    private DayStartedGenerationPreparation BuildDayStartedPreparation(
        SavePartitionIdentity currentIdentity,
        int gameDayIndex,
        string locale)
    {
        StableDayContext stableContext = StableDayContextBuilder.Build(
            new StableDayFacts(
                Game1.currentSeason,
                locale,
                Game1.player.currentLocation is not null
                    && Game1.IsGreenRainingHere(Game1.player.currentLocation),
                Game1.isRaining,
                Game1.isSnowing,
                Game1.isDebrisWeather,
                Game1.year,
                Game1.dayOfMonth,
                Game1.player.deepestMineLevel));
        IReadOnlyList<PreparedDialogueCandidate> preparedCandidates = PrepareDayStartedCandidates(
            gameDayIndex,
            locale,
            logDecisions: true);
        List<DailyDialogueGenerationInput> generationCandidates = new();
        foreach (PreparedDialogueCandidate preparedCandidate in preparedCandidates)
        {
            DialogueCandidate candidate = preparedCandidate.Candidate;
            Friendship? friendship = null;
            Game1.player.friendshipData.TryGetValue(candidate.NpcId, out friendship);
            generationCandidates.Add(
                new DailyDialogueGenerationInput(
                    candidate,
                    RelationshipSnapshotBuilder.Build(ToRelationshipFacts(friendship)),
                    Array.Empty<JsonElement>()));
        }

        DayStartedGenerationInput input = new(
            currentIdentity.SaveId,
            currentIdentity.PlayerId,
            gameDayIndex,
            stableContext,
            generationCandidates);
        return new DayStartedGenerationPreparation(input, preparedCandidates);
    }

    /// <summary>
    /// 在 DayStarted 主线程解析配置目标；已加载栈必须额外捕获成功才允许进入生成请求。
    /// </summary>
    /// <param name="gameDayIndex">当前绝对游戏日，写入 direct target 完整 key。</param>
    /// <param name="locale">当前 SMAPI locale。</param>
    /// <param name="logDecisions">
    /// 是否为本轮初始解析记录稳定 reason；激活阶段的二次校验传 false，避免同一日重复日志。
    /// </param>
    /// <returns>按配置顺序排列、可安全发送后端的候选及可选 direct target。</returns>
    private IReadOnlyList<PreparedDialogueCandidate> PrepareDayStartedCandidates(
        int gameDayIndex,
        string locale,
        bool logDecisions)
    {
        List<PreparedDialogueCandidate> candidates = new();
        foreach (string npcId in targetNpcIds)
        {
            try
            {
                NPC? npc = Game1.getCharacterFromName(npcId);
                if (npc is null)
                {
                    continue;
                }

                bool hasLoadedStack = StardewLoadedDialogueStackAdapter.HasLoadedStack(npc);
                bool isGreenRaining = npc.currentLocation is not null
                    && Game1.IsGreenRainingHere(npc.currentLocation);
                DialogueCandidateRouteDecision route = DialogueCandidateRoutePolicy.Select(
                    npcId,
                    hasLoadedStack,
                    Game1.isRaining,
                    isGreenRaining);
                if (logDecisions)
                {
                    monitor.Log(
                        $"正式候选路由 npc={npcId}, route={route.Route}, reason={route.ReasonCode}。",
                        LogLevel.Trace);
                }

                if (route.Route == DialogueCandidateRoute.OriginalDialogueFallback)
                {
                    continue;
                }

                DialogueCandidateResolution resolution;
                LoadedDialogueTarget? loadedTarget = null;
                if (route.Route == DialogueCandidateRoute.AuthoritativeLoadedSource)
                {
                    LoadedDialogueSourceCaptureResolution sourceCapture =
                        StardewLoadedDialogueStackAdapter.CaptureSourceSnapshot(
                            npc,
                            npcId,
                            locale,
                            IsDialogueDisplayActive());
                    if (logDecisions)
                    {
                        monitor.Log(
                            LoadedDialogueDiagnostics.FormatCapture(
                                npcId,
                                sourceCapture.ReasonCode),
                            LogLevel.Trace);
                    }

                    if (!sourceCapture.IsCaptured || sourceCapture.Handle is null)
                    {
                        continue;
                    }

                    resolution = candidateResolver.ResolveAuthoritativeLoaded(
                        npc,
                        locale,
                        targetNpcIds,
                        sourceCapture.Handle.Snapshot);
                    if (!resolution.IsResolved || resolution.Candidate is null)
                    {
                        if (logDecisions)
                        {
                            monitor.Log(
                                CandidateResolutionDiagnostics.Format(npcId, resolution),
                                LogLevel.Trace);
                        }

                        continue;
                    }

                    LoadedDialogueTargetResolution bind =
                        StardewLoadedDialogueStackAdapter.BindCandidate(
                            sourceCapture.Handle,
                            resolution.Candidate,
                            gameDayIndex,
                            locale);
                    if (!bind.IsCaptured || bind.Target is null)
                    {
                        continue;
                    }

                    loadedTarget = bind.Target;
                }
                else
                {
                    // 兼容路线只能由纯 policy 为 dry、无栈的 Abigail/Sebastian 选择。
                    resolution = candidateResolver.Resolve(
                        npc,
                        locale,
                        targetNpcIds,
                        DialogueCandidateResolutionMode.RequireUnloaded);
                }

                if (logDecisions)
                {
                    monitor.Log(
                        CandidateResolutionDiagnostics.Format(npcId, resolution),
                        LogLevel.Trace);
                }

                if (!resolution.IsResolved || resolution.Candidate is null)
                {
                    continue;
                }

                candidates.Add(new PreparedDialogueCandidate(resolution.Candidate, loadedTarget));
            }
            catch (Exception exception)
            {
                LogStableFailure($"CANDIDATE_RESOLUTION_FAILED_{npcId}", exception);
            }
        }

        return candidates;
    }

    /// <summary>
    /// Agent 返回后只重新解析最初进入请求的 NPC，并保持每个候选原先的 loaded/unloaded 模式。
    /// </summary>
    /// <param name="preparedCandidates">DayStarted 已冻结的候选与可选 target。</param>
    /// <param name="locale">token 中的原始 locale；session gate 已先证明仍是当前 locale。</param>
    /// <returns>与原始 raw identity 逐字段一致的当前候选。</returns>
    private IReadOnlyList<DialogueCandidate> ResolvePreparedCandidatesForActivation(
        IReadOnlyList<PreparedDialogueCandidate> preparedCandidates,
        string locale)
    {
        List<DialogueCandidate> currentCandidates = new();
        foreach (PreparedDialogueCandidate preparedCandidate in preparedCandidates)
        {
            string npcId = preparedCandidate.Candidate.NpcId;
            try
            {
                NPC? npc = Game1.getCharacterFromName(npcId);
                if (npc is null)
                {
                    continue;
                }

                if (preparedCandidate.LoadedTarget is not null)
                {
                    // loaded candidate 保留初次对象 token；这里只重读 exact raw asset，绝不运行 pure selector。
                    if (candidateResolver.RevalidateAuthoritativeLoaded(
                            npc,
                            preparedCandidate.Candidate))
                    {
                        currentCandidates.Add(preparedCandidate.Candidate);
                    }

                    continue;
                }

                DialogueCandidateResolution resolution = candidateResolver.Resolve(
                    npc,
                    locale,
                    targetNpcIds,
                    DialogueCandidateResolutionMode.RequireUnloaded);
                if (resolution.IsResolved
                    && resolution.Candidate is not null
                    && HasSameRawCandidateIdentity(
                        preparedCandidate.Candidate,
                        resolution.Candidate))
                {
                    currentCandidates.Add(resolution.Candidate);
                }
            }
            catch (Exception exception)
            {
                LogStableFailure($"CANDIDATE_REVALIDATION_FAILED_{npcId}", exception);
            }
        }

        return currentCandidates;
    }

    /// <summary>
    /// 复核生成前后 raw source 身份；source text 与 hash 均要求逐字符一致，不能只依赖哈希。
    /// </summary>
    private static bool HasSameRawCandidateIdentity(
        DialogueCandidate prepared,
        DialogueCandidate current)
    {
        return string.Equals(prepared.NpcId, current.NpcId, StringComparison.Ordinal)
            && prepared.SourceFamily == current.SourceFamily
            && string.Equals(prepared.Locale, current.Locale, StringComparison.Ordinal)
            && string.Equals(prepared.AssetName, current.AssetName, StringComparison.Ordinal)
            && string.Equals(prepared.DialogueKey, current.DialogueKey, StringComparison.Ordinal)
            && string.Equals(prepared.SourceText, current.SourceText, StringComparison.Ordinal)
            && string.Equals(prepared.SourceHash, current.SourceHash, StringComparison.Ordinal);
    }

    private void ScheduleEventFlush(int throughDayIndex, string trigger)
    {
        EventOutboxCoordinator? coordinator = eventCoordinator;
        if (coordinator is null)
        {
            return;
        }

        _ = RunEventFlushAsync(coordinator, throughDayIndex, trigger);
    }

    private async Task RunEventFlushAsync(
        EventOutboxCoordinator coordinator,
        int throughDayIndex,
        string trigger)
    {
        try
        {
            await coordinator.FlushThroughDayAsync(throughDayIndex, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            completionQueue.Enqueue(
                () => LogStableFailure($"EVENT_FLUSH_FAILED_{trigger}", exception));
        }
    }

    private void ScheduleDisplayAckFlush(string trigger)
    {
        DisplayAckOutboxCoordinator? coordinator = displayAckCoordinator;
        if (coordinator is null)
        {
            return;
        }

        _ = RunDisplayAckFlushAsync(coordinator, trigger);
    }

    private async Task RunDisplayAckFlushAsync(
        DisplayAckOutboxCoordinator coordinator,
        string trigger)
    {
        try
        {
            await coordinator.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            completionQueue.Enqueue(
                () => LogStableFailure($"DISPLAY_ACK_FLUSH_FAILED_{trigger}", exception));
        }
    }

    private void InvalidateLiveCache(bool resetNpcCopies)
    {
        IReadOnlyList<DailyDialogueCacheEntry> oldEntries = liveCache.Snapshot();
        LoadedDialogueReleaseResult directRelease = loadedDialogueCoordinator?.ReleaseUndisplayed(
                canVerifyCurrentTarget: Context.IsWorldReady)
            ?? new LoadedDialogueReleaseResult(
                Array.Empty<DailyDialogueCacheKey>(),
                RestoredCount: 0);
        HashSet<DailyDialogueCacheKey> directKeys = directRelease.DirectKeys.ToHashSet();

        // direct patch 必须先尝试生成文→原文 CAS，再清 observer/cache；这样 MenuChanged 与首帧
        // 之间发生 locale/asset/session 失效时不会继续保留可错误 ACK 的 token。
        liveCache.Clear();
        displayObserver.Clear();
        foreach (DailyDialogueCacheEntry entry in oldEntries)
        {
            if (directKeys.Contains(entry.Key))
            {
                // 该对象已由 direct registry 处理。无论是否成功恢复，都不能再 invalidate asset
                // 或 reset NPC，否则会把直接路线误当成尚未加载的资产路线。
                continue;
            }

            try
            {
                isApplyingOwnAssetInvalidation = true;
                InvalidateDialogueAssetVariants(entry.Key.AssetName);
            }
            catch (Exception exception)
            {
                LogStableFailure("LIVE_CACHE_ASSET_INVALIDATION_FAILED", exception);
            }
            finally
            {
                isApplyingOwnAssetInvalidation = false;
            }

            if (resetNpcCopies)
            {
                try
                {
                    Game1.getCharacterFromName(entry.Key.NpcId)?.resetSeasonalDialogue();
                }
                catch (Exception exception)
                {
                    LogStableFailure("LIVE_CACHE_NPC_RESET_FAILED", exception);
                }
            }
        }
    }

    private void InvalidateAssetAndResetNpc(string assetName, string npcId)
    {
        try
        {
            isApplyingOwnAssetInvalidation = true;
            bool invalidated = InvalidateDialogueAssetVariants(assetName);
            Game1.getCharacterFromName(npcId)?.resetSeasonalDialogue();
            monitor.Log(
                $"activated_asset_invalidation npc={npcId}, matched={invalidated}.",
                LogLevel.Trace);
        }
        catch (Exception exception)
        {
            LogStableFailure("ACTIVATED_ASSET_INVALIDATION_FAILED", exception);
        }
        finally
        {
            isApplyingOwnAssetInvalidation = false;
        }
    }

    /// <summary>
    /// 按无 locale 的 dialogue base name 失效所有等价缓存项。
    /// </summary>
    /// <param name="assetName">候选使用的规范化无 locale asset name。</param>
    /// <returns>SMAPI 是否实际命中至少一个已缓存的 locale 变体。</returns>
    /// <remarks>
    /// 中文内容会以 ``Characters/Dialogue/Npc.zh-CN`` 作为真实 cache key。精确调用
    /// ``InvalidateCache("Characters/Dialogue/Npc")`` 会返回 false，随后原版 lazy load
    /// 继续复用旧字典，Late edit 永远没有执行机会。这里复用 SMAPI 自身的 base-name
    /// 等价判断，既覆盖当前 locale，也避免手写或猜测 locale 后缀。
    /// </remarks>
    private bool InvalidateDialogueAssetVariants(string assetName)
    {
        if (string.IsNullOrWhiteSpace(assetName))
        {
            throw new ArgumentException("dialogue asset name 不能为空。", nameof(assetName));
        }

        return helper.GameContent.InvalidateCache(
            asset => asset.Name.IsEquivalentTo(assetName, useBaseName: true));
    }

    /// <summary>
    /// 判断当前是否已有对话正在展示；任何 true 都禁止在可见 UI 中途替换文本。
    /// </summary>
    private static bool IsDialogueDisplayActive()
    {
        return Game1.dialogueUp || Game1.activeClickableMenu is DialogueBox;
    }

    /// <summary>
    /// DayStarted 单 NPC 的业务候选，以及仅供主线程完成阶段使用的可选已加载 target。
    /// </summary>
    /// <param name="Candidate">发送后端的不可变 raw source 候选。</param>
    /// <param name="LoadedTarget">
    /// 已加载路线的进程内对象 token；null 表示继续原有 unloaded asset 路线。
    /// </param>
    private sealed record PreparedDialogueCandidate(
        DialogueCandidate Candidate,
        LoadedDialogueTarget? LoadedTarget);

    /// <summary>
    /// 把纯后端 DTO 与主线程 prepared token 放在同一日生命周期中，但不把 token 序列化。
    /// </summary>
    /// <param name="Input">后台协调器唯一可以读取的纯生成输入。</param>
    /// <param name="Candidates">完成回调重新验证和选择注入路线所需的原始准备记录。</param>
    private sealed record DayStartedGenerationPreparation(
        DayStartedGenerationInput Input,
        IReadOnlyList<PreparedDialogueCandidate> Candidates);

    private static RelationshipFacts? ToRelationshipFacts(Friendship? friendship)
    {
        if (friendship is null)
        {
            return null;
        }

        RelationshipStatus status = friendship.Status switch
        {
            FriendshipStatus.Dating => RelationshipStatus.Dating,
            FriendshipStatus.Engaged => RelationshipStatus.Engaged,
            FriendshipStatus.Married => RelationshipStatus.Married,
            FriendshipStatus.Divorced => RelationshipStatus.Divorced,
            _ => RelationshipStatus.Friendly,
        };
        return new RelationshipFacts(friendship.Points, status);
    }

    /// <summary>
    /// 从当前公开原生菜单重新读取 speaker、TranslationKey 与实际文本。
    /// </summary>
    /// <remarks>
    /// 本方法同时用于 MenuChanged 和首次 RenderedActiveMenu。不能只保留第一次菜单快照：
    /// 同一 DialogueBox 在两事件之间仍可能被游戏或其他同进程 Mod 改写；ACK 必须绑定首帧
    /// 实际读取到的内容，而不是关联时曾经存在的内容。
    /// </remarks>
    private static DialogueMenuSnapshot? CaptureDialogueMenuSnapshot(IClickableMenu? menu)
    {
        if (menu is not DialogueBox dialogueBox
            || dialogueBox.characterDialogue is null
            || dialogueBox.characterDialogue.speaker is null)
        {
            return null;
        }

        Dialogue dialogue = dialogueBox.characterDialogue;
        return new DialogueMenuSnapshot(
            dialogueBox,
            dialogue.speaker.Name,
            dialogue.TranslationKey,
            dialogue.getCurrentDialogue());
    }

    private string GetCurrentLocale()
    {
        string locale = helper.GameContent.CurrentLocale;
        return string.IsNullOrEmpty(locale) ? "en" : locale;
    }

    /// <summary>
    /// 在 SMAPI 主线程取得与原版 Dialogue 显示期相同的过滤后本地玩家名。
    /// </summary>
    /// <remarks>
    /// 返回值只作为当前同步注入调用的临时参数传给 observer render，不进入 DTO、cache、
    /// outbox、receipt 或日志。玩家对象、原名或过滤结果不可用时返回 null，含槽结果回退；
    /// 无槽 template 仍可正常应用。
    /// </remarks>
    private static string? GetFilteredLocalPlayerName()
    {
        Farmer? player = Game1.player;
        if (player is null || string.IsNullOrEmpty(player.Name))
        {
            return null;
        }

        string filteredPlayerName = Utility.FilterUserName(player.Name);
        return string.IsNullOrEmpty(filteredPlayerName) ? null : filteredPlayerName;
    }

    /// <summary>
    /// 保留旧 public 构造函数的既有语义；生产 ModEntry 使用显式 resolved IDs overload。
    /// </summary>
    private static IReadOnlyList<string> GetLegacyConfiguredNpcIds(ModConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.GetNormalizedTargetNpcIds();
    }

    private void LogStableFailure(string reasonCode, Exception exception)
    {
        monitor.Log(
            $"{reasonCode}: {exception.GetType().Name}。",
            LogLevel.Error);
    }
}
