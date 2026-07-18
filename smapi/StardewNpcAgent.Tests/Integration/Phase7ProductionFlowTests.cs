using System.Net;
using System.Text;
using System.Text.Json;
using StardewNpcAgent.Application;
using StardewNpcAgent.Contracts;
using StardewNpcAgent.Game;
using StardewNpcAgent.Infrastructure.Http;
using StardewNpcAgent.Infrastructure.Storage;
using StardewNpcAgent.Integration;

namespace StardewNpcAgent.Tests.Integration;

/// <summary>
/// 验证 Phase 7 生产组件经同一个严格 HTTP adapter 组合后形成完整、可恢复的本地闭环。
/// </summary>
/// <remarks>
/// 本测试使用内存 <see cref="HttpMessageHandler"/> 代替真实端口，但其余路径均使用生产类：
/// durable event/ACK outbox、三个 HTTP endpoint、每日批量协调器、staging 激活、Late 字典
/// 注入、原生菜单首帧观察与 ACK flush。这样可以在不启动游戏、服务或模型的前提下验证
/// 跨组件合同，而不为每个基础类重复建立审核链。
/// </remarks>
public sealed class Phase7ProductionFlowTests : IDisposable
{
    private const string SaveId = "save-phase7-production-flow";
    private const string PlayerId = "player-phase7-production-flow";
    private const int GameDayIndex = 14;
    private const string Locale = "zh-CN";
    private readonly string testDirectory = Path.Combine(
        Path.GetTempPath(),
        "StardewNpcAgent.Tests",
        $"Phase7ProductionFlow.{Guid.NewGuid():N}");

    public Phase7ProductionFlowTests()
    {
        Directory.CreateDirectory(testDirectory);
    }

    /// <summary>
    /// 昨日事件先提交为 memory revision 1；两 NPC 批次只生成 Abigail，随后只为真正绘制的
    /// Abigail 文本写入并提交 ACK。Sebastian 的 passthrough 始终保留原版且没有展示回执。
    /// </summary>
    [Fact]
    public async Task ScriptedHttp_CompletesEventGenerationActivationNativeDisplayAndAckLoop()
    {
        DurableEventOutbox eventOutbox = DurableEventOutbox.Open(
            Path.Combine(testDirectory, "events.json"),
            SaveId,
            PlayerId);
        eventOutbox.Enqueue(CreateYesterdayProgressionEvent());
        DurableDisplayAckOutbox ackOutbox = DurableDisplayAckOutbox.Open(
            Path.Combine(testDirectory, "display-acks.json"),
            SaveId,
            PlayerId);
        ScriptedBackendHandler backend = new();
        using HttpClient httpClient = new(backend)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        AgentApiClient apiClient = new(
            httpClient,
            new Uri("http://127.0.0.1:8000/"),
            AgentApiTimeouts.Default);
        EventOutboxCoordinator eventCoordinator = new(
            eventOutbox,
            apiClient,
            requestIdFactory: () => "request-phase7-event-sync");
        DailyDialogueCache stagingCache = new();
        DailyGenerationBatchCoordinator generationCoordinator = new(
            eventCoordinator,
            apiClient,
            stagingCache,
            AgentApiTimeouts.Default.GenerationRequest);
        DialogueCandidate abigail = CreateCandidate("Abigail", "春雨让山谷安静了一些。");
        DialogueCandidate sebastian = CreateCandidate("Sebastian", "今天适合待在室内。");

        DailyGenerationRunResult generationResult = await generationCoordinator.RunDayStartedAsync(
            CreateDayStartedInput(abigail, sebastian));

        Assert.Equal(DailyGenerationRunStatus.Completed, generationResult.Status);
        Assert.Equal(1, generationResult.CachedEntryCount);
        Assert.Equal(0, eventOutbox.PendingCount);
        Assert.Equal(1, eventOutbox.MemoryRevision);
        Assert.Equal(GameDayIndex - 1, eventOutbox.CommittedThroughDayIndex);
        Assert.Equal(new[] { "Abigail", "Sebastian" }, backend.GeneratedNpcIds);
        DailyDialogueCacheEntry stagedEntry = Assert.Single(stagingCache.Snapshot());
        Assert.Equal("Abigail", stagedEntry.Key.NpcId);

        // 后台结果只能先落 staging；这里模拟 UpdateTicked 主线程重新解析仍然有效的候选后提交。
        SessionGenerationGate sessionGate = new();
        sessionGate.StartSession(SaveId, PlayerId);
        GenerationSessionToken token = sessionGate.CaptureDay(GameDayIndex, Locale);
        DailyDialogueCache liveCache = new();
        DialogueActivationResult activation = StagedDialogueActivator.Activate(
            sessionGate,
            token,
            GameDayIndex,
            Locale,
            new[] { abigail, sebastian },
            stagingCache,
            liveCache);

        DailyDialogueCacheEntry liveEntry = Assert.Single(activation.ActivatedEntries);
        Assert.Single(liveCache.Snapshot());
        Assert.Equal("Abigail", liveEntry.Key.NpcId);

        DialogueDisplayCoordinator displayCoordinator = new(
            SaveId,
            PlayerId,
            liveCache,
            ackOutbox);
        DialogueDisplayObserver observer = new();
        Dictionary<string, string> abigailDialogueAsset = new(StringComparer.Ordinal)
        {
            [abigail.DialogueKey] = abigail.SourceText,
        };
        DialogueInjectionResult injection = DialogueInjectionAdapter.Apply(
            abigailDialogueAsset,
            abigail.AssetName,
            GameDayIndex,
            Locale,
            liveCache.Snapshot(),
            displayCoordinator,
            observer);

        Assert.Equal(1, injection.AppliedCount);
        Assert.Equal("雨声让我想起你昨天在矿井里的进展。", abigailDialogueAsset[abigail.DialogueKey]);
        Assert.Equal(0, ackOutbox.PendingCount);

        // MenuChanged 只建立关联；必须等同一原生菜单进入首次 RenderedActiveMenu 才能 ACK。
        object nativeDialogueMenu = new();
        DialogueMenuSnapshot nativeSnapshot = new(
            nativeDialogueMenu,
            abigail.NpcId,
            $"{abigail.AssetName}:{abigail.DialogueKey}",
            abigailDialogueAsset[abigail.DialogueKey]);
        observer.ObserveMenu(nativeSnapshot, GameDayIndex, Locale);
        Assert.Equal(0, ackOutbox.PendingCount);
        RenderedDialogueObservation rendered = Assert.IsType<RenderedDialogueObservation>(
            observer.TryObserveRendered(nativeSnapshot, GameDayIndex, Locale));
        DisplayAckEnqueueResult enqueueResult = displayCoordinator.RecordDisplayed(
            rendered.Decision,
            rendered.Confirmation);
        observer.Complete(rendered);

        Assert.Equal(DisplayAckStatus.Accepted, enqueueResult.Status);
        Assert.Equal(1, ackOutbox.PendingCount);
        DisplayAckOutboxCoordinator ackCoordinator = new(ackOutbox, apiClient);
        await ackCoordinator.FlushAsync(CancellationToken.None);

        Assert.Equal(0, ackOutbox.PendingCount);
        Assert.Equal(0, ackOutbox.DeadLetterCount);
        Assert.Equal(1, backend.DisplayAckCount);
        Assert.Equal("Abigail", backend.AcknowledgedNpcIds.Single());
        Assert.Equal(
            new[]
            {
                "/api/v1/game-events/batches",
                "/api/v1/dialogue-generations/batch",
                "/api/v1/dialogue-generations/generation-phase7-abigail/displayed",
            },
            backend.RequestPaths);
    }

    /// <summary>
    /// 已加载路线复用 staging activator、正式展示决策和原生首帧 ACK；成功后精确释放 direct cache。
    /// </summary>
    [Fact]
    public void DirectLoadedRoute_ActivatesAppliesRendersAcknowledgesAndReleasesCache()
    {
        DialogueCandidate candidate = CreateCandidate("Sebastian", "原版已加载台词。");
        SessionGenerationGate sessionGate = new();
        sessionGate.StartSession(SaveId, PlayerId);
        GenerationSessionToken token = sessionGate.CaptureDay(GameDayIndex, Locale);
        DailyDialogueCache stagingCache = new();
        DailyDialogueCacheEntry stagedEntry = new(
            new DailyDialogueCacheKey(
                GameDayIndex,
                Locale,
                candidate.NpcId,
                candidate.AssetName,
                candidate.DialogueKey),
            candidate.SourceFamily,
            candidate.SourceText,
            candidate.SourceHash,
            "Agent 增强后的已加载台词。",
            "generation-phase7-direct-sebastian",
            "generation-key-phase7-direct-sebastian",
            "trace-phase7-direct-sebastian");
        stagingCache.Store(stagedEntry);
        DailyDialogueCache liveCache = new();

        DialogueActivationResult activation = StagedDialogueActivator.Activate(
            sessionGate,
            token,
            GameDayIndex,
            Locale,
            new[] { candidate },
            stagingCache,
            liveCache);
        DailyDialogueCacheEntry liveEntry = Assert.Single(activation.ActivatedEntries);

        DurableDisplayAckOutbox ackOutbox = DurableDisplayAckOutbox.Open(
            Path.Combine(testDirectory, "direct-display-acks.json"),
            SaveId,
            PlayerId);
        DialogueDisplayCoordinator displayCoordinator = new(
            SaveId,
            PlayerId,
            liveCache,
            ackOutbox);
        DialogueDisplayObserver observer = new();
        LoadedDialogueStackCoordinator loadedCoordinator = new(
            liveCache,
            displayCoordinator,
            observer);
        InMemoryLoadedDialogueTargetAccess access = new(candidate.SourceText);
        LoadedDialogueTarget target = new(liveEntry.Key, candidate, access);

        LoadedDialogueApplyResult applied = loadedCoordinator.Apply(
            target,
            liveEntry,
            isDialogueDisplayActive: false);

        Assert.True(applied.WasApplied);
        Assert.Equal(liveEntry.EnhancedText, access.Text);
        Assert.Equal(1, observer.ArmedCount);
        Assert.Equal(0, ackOutbox.PendingCount);

        object nativeMenu = new();
        DialogueMenuSnapshot snapshot = new(
            nativeMenu,
            candidate.NpcId,
            $"{candidate.AssetName}:{candidate.DialogueKey}",
            liveEntry.EnhancedText);
        observer.ObserveMenu(snapshot, GameDayIndex, Locale);
        Assert.Equal(0, ackOutbox.PendingCount);
        RenderedDialogueObservation rendered = Assert.IsType<RenderedDialogueObservation>(
            observer.TryObserveRendered(snapshot, GameDayIndex, Locale));
        DisplayAckEnqueueResult ackResult = displayCoordinator.RecordDisplayed(
            rendered.Decision,
            rendered.Confirmation);
        DailyDialogueCacheKey? directKey = loadedCoordinator.MarkDisplayed(rendered.Decision);
        Assert.NotNull(directKey);
        Assert.True(liveCache.Remove(directKey!));
        observer.Complete(rendered);

        Assert.Equal(DisplayAckStatus.Accepted, ackResult.Status);
        Assert.Equal(1, ackOutbox.PendingCount);
        Assert.Empty(liveCache.Snapshot());
        Assert.Equal(0, observer.ArmedCount);
        Assert.Empty(
            loadedCoordinator.ReleaseUndisplayed(canVerifyCurrentTarget: true).DirectKeys);
        Assert.Equal(liveEntry.EnhancedText, access.Text);
    }

    /// <summary>
    /// 构造由真实 LevelChanged 投影语义代表的昨日 public progression 事实。
    /// </summary>
    private static GameEvent CreateYesterdayProgressionEvent()
    {
        GameEvent gameEvent = Assert.IsType<GameEvent>(
            GameEventCollector.CollectLevelChanged(
                new LevelChangedFact(
                    IsLocalPlayer: true,
                    OccurredDayIndex: GameDayIndex - 1,
                    SkillName: "Mining",
                    OldLevel: 4,
                    NewLevel: 5)));
        Assert.Equal("skill_mining_level_5", gameEvent.Payload.GetProperty("milestone").GetString());
        return gameEvent;
    }

    /// <summary>
    /// 创建已经通过游戏侧普通日常资格检查的候选；测试刻意让两个 NPC 使用不同资产。
    /// </summary>
    private static DialogueCandidate CreateCandidate(string npcId, string sourceText)
    {
        return new DialogueCandidate(
            npcId,
            DialogueSourceFamily.OrdinaryDaily,
            Locale,
            $"Characters/Dialogue/{npcId}",
            "spring_Tue",
            sourceText,
            SourceDialogueHasher.Compute(sourceText),
            new[] { "样例一。", "样例二。", "样例三。" });
    }

    /// <summary>
    /// 把两个候选冻结为同一天的生成输入；关系与世界进度仍由游戏侧提供，后端只能消费。
    /// </summary>
    private static DayStartedGenerationInput CreateDayStartedInput(
        DialogueCandidate abigail,
        DialogueCandidate sebastian)
    {
        return new DayStartedGenerationInput(
            SaveId,
            PlayerId,
            GameDayIndex,
            new StableDayContext
            {
                Season = "spring",
                Weather = "rain",
                Locale = Locale,
                ProgressionSignals = JsonSerializer.SerializeToElement(
                    new Dictionary<string, int> { ["mine_level"] = 5 }),
            },
            new[]
            {
                new DailyDialogueGenerationInput(
                    abigail,
                    new RelationshipSnapshot
                    {
                        FriendshipPoints = 1250,
                        RelationshipStage = "friend",
                    },
                    Array.Empty<JsonElement>()),
                new DailyDialogueGenerationInput(
                    sebastian,
                    new RelationshipSnapshot
                    {
                        FriendshipPoints = 500,
                        RelationshipStage = "acquaintance",
                    },
                    Array.Empty<JsonElement>()),
            });
    }

    /// <summary>
    /// 删除测试专属临时 outbox，不触碰仓库数据、真实 Mods 或游戏存档。
    /// </summary>
    public void Dispose()
    {
        if (Directory.Exists(testDirectory))
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    /// <summary>
    /// 模拟已捕获的游戏行，只提供事实重读和逐字符条件替换，不绕过生产栈策略。
    /// </summary>
    private sealed class InMemoryLoadedDialogueTargetAccess : ILoadedDialogueTargetAccess
    {
        public InMemoryLoadedDialogueTargetAccess(string originalText)
        {
            Text = originalText;
        }

        public string Text { get; private set; }

        public LoadedDialogueStackFacts ReadCurrentFacts(bool isDialogueDisplayActive)
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
                IsDialogueDisplayActive: isDialogueDisplayActive,
                StackIdentityMatches: true,
                DialogueIdentityMatches: true,
                LineIdentityMatches: true);
        }

        public bool TryReplaceText(string expectedCurrentText, string replacementText)
        {
            if (!string.Equals(Text, expectedCurrentText, StringComparison.Ordinal))
            {
                return false;
            }

            Text = replacementText;
            return true;
        }
    }

    /// <summary>
    /// 用一个 handler 模拟 FastAPI 的三个窄端点，并对每次生产 DTO 做反序列化与关键事实核对。
    /// </summary>
    private sealed class ScriptedBackendHandler : HttpMessageHandler
    {
        public List<string> RequestPaths { get; } = new();

        public List<string> GeneratedNpcIds { get; } = new();

        public List<string> AcknowledgedNpcIds { get; } = new();

        public int DisplayAckCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string path = request.RequestUri?.AbsolutePath
                ?? throw new InvalidOperationException("测试请求缺少绝对 URI。");
            RequestPaths.Add(path);
            string body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return path switch
            {
                "/api/v1/game-events/batches" => HandleEventBatch(body),
                "/api/v1/dialogue-generations/batch" => HandleGenerationBatch(body),
                "/api/v1/dialogue-generations/generation-phase7-abigail/displayed" =>
                    HandleDisplayAck(body),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        }

        private static HttpResponseMessage HandleEventBatch(string body)
        {
            GameEventBatchRequest request = ContractJson.Deserialize<GameEventBatchRequest>(body);
            GameEvent gameEvent = Assert.Single(request.Events);
            Assert.Equal(GameDayIndex - 1, gameEvent.OccurredDayIndex);
            Assert.Equal(AudienceScope.Public, gameEvent.AudienceScope);
            return JsonResponse(
                new GameEventBatchResponse
                {
                    SchemaVersion = request.SchemaVersion,
                    RequestId = request.RequestId,
                    MemoryRevision = 1,
                    CommittedThroughDayIndex = GameDayIndex - 1,
                    Items = new List<GameEventItemResult>
                    {
                        new()
                        {
                            EventId = gameEvent.EventId,
                            Status = EventIngestionStatus.Accepted,
                            ReasonCode = null,
                        },
                    },
                });
        }

        private HttpResponseMessage HandleGenerationBatch(string body)
        {
            DialogueGenerationBatchRequest request =
                ContractJson.Deserialize<DialogueGenerationBatchRequest>(body);
            Assert.Equal(1, request.RequiredMemoryRevision);
            Assert.Equal(GameDayIndex, request.GameDayIndex);
            Assert.Equal(2, request.Items.Count);
            GeneratedNpcIds.AddRange(request.Items.Select(item => item.NpcId));
            DialogueGenerationItem abigail = request.Items[0];
            DialogueGenerationItem sebastian = request.Items[1];
            return JsonResponse(
                new DialogueGenerationBatchResponse
                {
                    SchemaVersion = request.SchemaVersion,
                    RequestId = request.RequestId,
                    MemoryRevision = request.RequiredMemoryRevision,
                    Items = new List<DialogueGenerationItemResult>
                    {
                        new()
                        {
                            TaskId = abigail.TaskId,
                            GenerationId = "generation-phase7-abigail",
                            GenerationKey = "generation-key-phase7-abigail",
                            Status = DialogueGenerationStatus.Generated,
                            Text = "雨声让我想起你昨天在矿井里的进展。",
                            SourceHash = abigail.SourceDialogue.SourceHash,
                            ReasonCode = "AGENT_MEMORY_ENHANCED",
                            // C# 只透传后端 opaque evidence ID；handler 用稳定占位值模拟该字段。
                            EvidenceIds = new List<string> { "memory-phase7-mining-level-5" },
                            TraceId = "trace-phase7-abigail",
                        },
                        new()
                        {
                            TaskId = sebastian.TaskId,
                            GenerationId = "generation-phase7-sebastian",
                            GenerationKey = "generation-key-phase7-sebastian",
                            Status = DialogueGenerationStatus.Passthrough,
                            Text = null,
                            SourceHash = sebastian.SourceDialogue.SourceHash,
                            ReasonCode = "NO_VALUABLE_ENHANCEMENT",
                            EvidenceIds = new List<string>(),
                            TraceId = "trace-phase7-sebastian",
                        },
                    },
                });
        }

        private HttpResponseMessage HandleDisplayAck(string body)
        {
            DisplayAckRequest request = ContractJson.Deserialize<DisplayAckRequest>(body);
            DisplayAckCount++;
            AcknowledgedNpcIds.Add(request.NpcId);
            Assert.Equal(GameDayIndex, request.DisplayedDayIndex);
            return JsonResponse(
                new DisplayAckResponse
                {
                    SchemaVersion = request.SchemaVersion,
                    RequestId = request.RequestId,
                    DisplayReceiptId = request.DisplayReceiptId,
                    Status = DisplayAckStatus.Accepted,
                });
        }

        /// <summary>
        /// 使用生产 canonical serializer 返回严格 JSON，避免测试 handler 绕开 wire contract。
        /// </summary>
        private static HttpResponseMessage JsonResponse<TResponse>(TResponse response)
            where TResponse : ContractDto
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    ContractJson.Serialize(response),
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }
}
