using System.Text.Json;
using StardewNpcAgent.Application;
using StardewNpcAgent.Contracts;
using StardewNpcAgent.Game;
using StardewNpcAgent.Infrastructure.Storage;

namespace StardewNpcAgent.Tests.Application;

/// <summary>
/// 跨越 Phase 4 纯应用组件的确定性闭环验收。
/// </summary>
/// <remarks>
/// 测试使用 scripted 事件同步器和生成网关、真实内存 cache 与临时 durable ACK outbox。
/// 它不启动 SMAPI、HTTP、游戏、后台线程或模型，只证明组件接口组合后仍保持原版 fallback
/// 与“实际展示后才 ACK”的完整语义。
/// </remarks>
public sealed class Phase4DeterministicFlowTests : IDisposable
{
    private const string SaveId = "save-phase4-flow";
    private const string PlayerId = "player-phase4-flow";
    private readonly string testDirectory;

    /// <summary>
    /// 为真实 ACK snapshot 创建唯一临时目录，避免并行测试共享文件。
    /// </summary>
    public Phase4DeterministicFlowTests()
    {
        testDirectory = Path.Combine(
            Path.GetTempPath(),
            "StardewNpcAgent.Tests",
            $"Phase4DeterministicFlow.{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
    }

    /// <summary>
    /// 昨日事件水位驱动 mixed generation；只缓存 generated，点击显示后才产生 durable ACK。
    /// </summary>
    [Fact]
    public async Task YesterdayWatermarkToMixedCacheToDisplayedAck_FormsDeterministicLocalLoop()
    {
        DialogueCandidate abigail = CreateCandidate("Abigail", "原版台词-Abigail");
        DialogueCandidate sebastian = CreateCandidate("Sebastian", "原版台词-Sebastian");
        DayStartedGenerationInput input = new(
            SaveId,
            PlayerId,
            GameDayIndex: 14,
            StableDayContext: new StableDayContext
            {
                Season = "spring",
                Weather = "rain",
                Locale = "zh-CN",
                ProgressionSignals = ParseJsonObject("{\"mine_level\":40}"),
            },
            Candidates: new[]
            {
                CreateGenerationInput(abigail, friendshipPoints: 750, "friend"),
                CreateGenerationInput(sebastian, friendshipPoints: 500, "acquaintance"),
            });
        ScriptedEventSynchronizer eventSynchronizer = new(
            new EventOutboxWatermark(MemoryRevision: 1, CommittedThroughDayIndex: 13));
        ScriptedMixedGenerationGateway generationGateway = new();
        DailyDialogueCache cache = new();
        DailyGenerationCoordinator dailyCoordinator = new(
            eventSynchronizer,
            generationGateway,
            cache);

        DailyGenerationRunResult dailyResult = await dailyCoordinator.RunDayStartedAsync(input);

        Assert.Equal(DailyGenerationRunStatus.Completed, dailyResult.Status);
        Assert.Equal(1, dailyResult.CachedEntryCount);
        Assert.Equal(new[] { 13 }, eventSynchronizer.ThroughDayIndexes);
        DialogueGenerationBatchRequest request = Assert.Single(generationGateway.Requests);
        Assert.Equal(1, request.RequiredMemoryRevision);
        Assert.Equal(new[] { "Abigail", "Sebastian" }, request.Items.Select(item => item.NpcId));
        DailyDialogueCacheEntry generatedEntry = Assert.Single(cache.Snapshot());
        Assert.Equal("Abigail", generatedEntry.Key.NpcId);
        Assert.Equal("增强台词-Abigail", generatedEntry.EnhancedText);

        string outboxPath = Path.Combine(testDirectory, "display-acks.json");
        DurableDisplayAckOutbox ackOutbox = DurableDisplayAckOutbox.Open(
            outboxPath,
            SaveId,
            PlayerId);
        DialogueDisplayCoordinator displayCoordinator = new(
            SaveId,
            PlayerId,
            cache,
            ackOutbox);
        DialogueDisplayContext abigailContext = CreateDisplayContext(input, abigail);
        DialogueDisplayDecision generatedDecision = displayCoordinator.Resolve(abigailContext);
        DialogueDisplayDecision originalDecision = displayCoordinator.Resolve(
            CreateDisplayContext(input, sebastian));

        Assert.Equal(DialogueDisplayDecisionKind.UseGenerated, generatedDecision.Kind);
        Assert.Equal("增强台词-Abigail", generatedDecision.EnhancedText);
        Assert.Equal(DialogueDisplayDecisionKind.UseOriginal, originalDecision.Kind);
        Assert.Equal(0, ackOutbox.PendingCount);

        DisplayAckEnqueueResult ackResult = displayCoordinator.RecordDisplayed(
            generatedDecision,
            new DisplayedDialogueConfirmation(
                WasActuallyDisplayed: true,
                DisplayedDayIndex: input.GameDayIndex,
                NpcId: abigail.NpcId,
                SourceHash: abigail.SourceHash));

        Assert.Equal(DisplayAckStatus.Accepted, ackResult.Status);
        PendingDisplayAck pendingAck = Assert.IsType<PendingDisplayAck>(
            ackOutbox.CreateNextAttempt());
        Assert.Equal(generatedEntry.GenerationId, pendingAck.GenerationId);
        Assert.Equal(input.GameDayIndex, pendingAck.Request.DisplayedDayIndex);
        Assert.Equal(abigail.NpcId, pendingAck.Request.NpcId);
        Assert.Equal(abigail.SourceHash, pendingAck.Request.SourceHash);
        Assert.True(ContractValidator.Validate(pendingAck.Request).IsValid);
    }

    /// <summary>
    /// 创建已由游戏适配器证明安全的普通日常候选。
    /// </summary>
    private static DialogueCandidate CreateCandidate(string npcId, string sourceText)
    {
        return new DialogueCandidate(
            npcId,
            DialogueSourceFamily.OrdinaryDaily,
            "zh-CN",
            $"Characters/Dialogue/{npcId}",
            "spring_Mon",
            sourceText,
            SourceDialogueHasher.Compute(sourceText),
            new[] { "样例一。", "样例二。", "样例三。" });
    }

    /// <summary>
    /// 给候选附加权威关系快照；Phase 4 不在游戏侧查询或改写关系。
    /// </summary>
    private static DailyDialogueGenerationInput CreateGenerationInput(
        DialogueCandidate candidate,
        int friendshipPoints,
        string relationshipStage)
    {
        return new DailyDialogueGenerationInput(
            candidate,
            new RelationshipSnapshot
            {
                FriendshipPoints = friendshipPoints,
                RelationshipStage = relationshipStage,
            },
            Array.Empty<JsonElement>());
    }

    /// <summary>
    /// 把同一候选转换为点击时重新读取的当前来源事实。
    /// </summary>
    private static DialogueDisplayContext CreateDisplayContext(
        DayStartedGenerationInput input,
        DialogueCandidate candidate)
    {
        return new DialogueDisplayContext(
            input.GameDayIndex,
            input.StableDayContext.Locale,
            candidate.NpcId,
            candidate.AssetName,
            candidate.DialogueKey,
            candidate.SourceText);
    }

    /// <summary>
    /// 返回脱离 JsonDocument 生命周期的 object JsonElement。
    /// </summary>
    private static JsonElement ParseJsonObject(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// 清理本测试唯一临时目录，不触碰仓库或真实 Mods。
    /// </summary>
    public void Dispose()
    {
        if (Directory.Exists(testDirectory))
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    /// <summary>
    /// 返回固定、已持久化水位并记录调用边界的事件同步 fake。
    /// </summary>
    private sealed class ScriptedEventSynchronizer : IEventOutboxSynchronizer
    {
        private readonly EventOutboxWatermark watermark;

        public ScriptedEventSynchronizer(EventOutboxWatermark watermark)
        {
            this.watermark = watermark;
        }

        public List<int> ThroughDayIndexes { get; } = new();

        public Task<EventOutboxWatermark> FlushThroughDayAsync(
            int throughDayIndex,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThroughDayIndexes.Add(throughDayIndex);
            return Task.FromResult(watermark);
        }
    }

    /// <summary>
    /// 第一项 generated、第二项 passthrough 的确定性网关 fake。
    /// </summary>
    private sealed class ScriptedMixedGenerationGateway : IDialogueGenerationGateway
    {
        public List<DialogueGenerationBatchRequest> Requests { get; } = new();

        public Task<DialogueGenerationBatchResponse> GenerateBatchAsync(
            DialogueGenerationBatchRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            DialogueGenerationItem first = request.Items[0];
            DialogueGenerationItem second = request.Items[1];
            return Task.FromResult(
                new DialogueGenerationBatchResponse
                {
                    SchemaVersion = request.SchemaVersion,
                    RequestId = request.RequestId,
                    MemoryRevision = request.RequiredMemoryRevision,
                    Items = new List<DialogueGenerationItemResult>
                    {
                        new()
                        {
                            TaskId = first.TaskId,
                            GenerationId = "generation-phase4-abigail",
                            GenerationKey = "generation-key-phase4-abigail",
                            Status = DialogueGenerationStatus.Generated,
                            Text = "增强台词-Abigail",
                            SourceHash = first.SourceDialogue.SourceHash,
                            ReasonCode = "TRUSTED_PHASE4_GENERATED",
                            EvidenceIds = new List<string>(),
                            TraceId = "trace-phase4-abigail",
                        },
                        new()
                        {
                            TaskId = second.TaskId,
                            GenerationId = "generation-phase4-sebastian",
                            GenerationKey = "generation-key-phase4-sebastian",
                            Status = DialogueGenerationStatus.Passthrough,
                            Text = null,
                            SourceHash = second.SourceDialogue.SourceHash,
                            ReasonCode = "SCRIPTED_PASSTHROUGH",
                            EvidenceIds = new List<string>(),
                            TraceId = "trace-phase4-sebastian",
                        },
                    },
                });
        }
    }
}
