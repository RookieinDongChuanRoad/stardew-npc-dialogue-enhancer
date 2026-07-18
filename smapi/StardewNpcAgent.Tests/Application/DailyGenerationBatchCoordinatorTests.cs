using System.Text.Json;
using StardewNpcAgent.Application;
using StardewNpcAgent.Contracts;
using StardewNpcAgent.Game;
using StardewNpcAgent.Infrastructure.Storage;

namespace StardewNpcAgent.Tests.Application;

/// <summary>
/// 验证一次 DayStarted cohort 的 flush、并发、共享 deadline 与失败隔离。
/// </summary>
/// <remarks>
/// 全部 gateway 都是进程内脚本，不访问网络或 Provider。测试只观察稳定 DTO、计数和 cache，
/// 不依赖游戏对象或真实存档。
/// </remarks>
public sealed class DailyGenerationBatchCoordinatorTests
{
    private static readonly string[] VanillaTwelve =
    {
        "Abigail",
        "Alex",
        "Elliott",
        "Emily",
        "Haley",
        "Harvey",
        "Leah",
        "Maru",
        "Penny",
        "Sam",
        "Sebastian",
        "Shane",
    };

    /// <summary>
    /// 十二项必须只 flush 一次，并以同一 revision 同时提交两个稳定 6 项请求。
    /// </summary>
    [Fact]
    public async Task RunDayStartedAsync_FlushesOnceAndStartsStableSixPlusSixTogether()
    {
        ScriptedEventSynchronizer synchronizer = new(revision: 41);
        ConcurrentGateway gateway = new();
        DailyDialogueCache cache = new();
        DailyGenerationBatchCoordinator coordinator = new(
            synchronizer,
            gateway,
            cache,
            TimeSpan.FromSeconds(5));

        DailyGenerationRunResult result = await coordinator.RunDayStartedAsync(
            BuildInput(VanillaTwelve));

        Assert.Equal(DailyGenerationRunStatus.Completed, result.Status);
        Assert.Equal(2, result.BatchCount);
        Assert.Equal(2, result.SuccessfulBatchCount);
        Assert.Equal(0, result.FailedBatchCount);
        Assert.Equal(2, result.RequestIds.Count);
        Assert.Equal(2, result.RequestIds.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(new[] { 6, 6 }, gateway.Requests.Select(request => request.Items.Count));
        Assert.Equal(VanillaTwelve, gateway.Requests.SelectMany(request => request.Items).Select(item => item.NpcId));
        Assert.All(gateway.Requests, request => Assert.Equal(41, request.RequiredMemoryRevision));
        Assert.Equal(1, synchronizer.FlushCount);
        Assert.Equal(13, synchronizer.ThroughDayIndex);
        Assert.True(gateway.StartedTogether);
        Assert.Equal(gateway.ObservedTokens[0], gateway.ObservedTokens[1]);
    }

    /// <summary>
    /// 一个 batch 的网络失败只能让该半组回退；合法 sibling 的 generated staging 必须保留。
    /// </summary>
    [Fact]
    public async Task RunDayStartedAsync_OneBatchFailurePreservesSuccessfulSiblingStaging()
    {
        ScriptedEventSynchronizer synchronizer = new(revision: 52);
        PartialFailureGateway gateway = new();
        DailyDialogueCache cache = new();
        DailyGenerationBatchCoordinator coordinator = new(
            synchronizer,
            gateway,
            cache,
            TimeSpan.FromSeconds(5));

        DailyGenerationRunResult result = await coordinator.RunDayStartedAsync(
            BuildInput(VanillaTwelve));

        Assert.Equal(DailyGenerationRunStatus.Completed, result.Status);
        Assert.Equal(2, result.BatchCount);
        Assert.Equal(1, result.SuccessfulBatchCount);
        Assert.Equal(1, result.FailedBatchCount);
        Assert.Equal(6, result.CachedEntryCount);
        Assert.Equal(VanillaTwelve.Skip(6), cache.Snapshot().Select(entry => entry.Key.NpcId));
        Assert.Equal(2, gateway.Requests.Count);
        Assert.Equal(1, synchronizer.FlushCount);
    }

    /// <summary>
    /// 一个 batch 的 response envelope 映射错误与网络失败采用同一局部隔离边界。
    /// </summary>
    [Fact]
    public async Task RunDayStartedAsync_InvalidEnvelopeDoesNotClearValidSibling()
    {
        ScriptedEventSynchronizer synchronizer = new(revision: 53);
        InvalidFirstEnvelopeGateway gateway = new();
        DailyDialogueCache cache = new();
        DailyGenerationBatchCoordinator coordinator = new(
            synchronizer,
            gateway,
            cache,
            TimeSpan.FromSeconds(5));

        DailyGenerationRunResult result = await coordinator.RunDayStartedAsync(
            BuildInput(VanillaTwelve));

        Assert.Equal(DailyGenerationRunStatus.Completed, result.Status);
        Assert.Equal(1, result.SuccessfulBatchCount);
        Assert.Equal(1, result.FailedBatchCount);
        Assert.Equal(VanillaTwelve.Skip(6), cache.Snapshot().Select(entry => entry.Key.NpcId));
        Assert.Equal(2, gateway.Requests.Count);
    }

    /// <summary>
    /// 两个 request 必须在任何 gateway 调用前都完成深拷贝；第一批 adapter 的同步行为
    /// 不能改变第二批已经冻结的 weather/context。
    /// </summary>
    [Fact]
    public async Task RunDayStartedAsync_FreezesBothRequestsBeforeFirstGatewaySubmission()
    {
        ScriptedEventSynchronizer synchronizer = new(revision: 57);
        DayStartedGenerationInput input = BuildInput(VanillaTwelve);
        MutatingFirstCallGateway gateway = new(
            () => input.StableDayContext.Weather = "snow");
        DailyGenerationBatchCoordinator coordinator = new(
            synchronizer,
            gateway,
            new DailyDialogueCache(),
            TimeSpan.FromSeconds(5));

        DailyGenerationRunResult result = await coordinator.RunDayStartedAsync(input);

        Assert.Equal(DailyGenerationRunStatus.Completed, result.Status);
        Assert.Equal(2, gateway.Requests.Count);
        Assert.All(gateway.Requests, request => Assert.Equal("rain", request.StableDayContext.Weather));
    }

    /// <summary>
    /// 共享 deadline 到期时，已完成 sibling 仍可提交；尾部 batch 只计一次失败且不重试。
    /// </summary>
    [Fact]
    public async Task RunDayStartedAsync_SharedDeadlineKeepsCompletedSiblingAndDoesNotRetryTail()
    {
        ScriptedEventSynchronizer synchronizer = new(revision: 58);
        TailTimeoutGateway gateway = new();
        DailyDialogueCache cache = new();
        DailyGenerationBatchCoordinator coordinator = new(
            synchronizer,
            gateway,
            cache,
            TimeSpan.FromMilliseconds(100));

        DailyGenerationRunResult result = await coordinator.RunDayStartedAsync(
            BuildInput(VanillaTwelve));

        Assert.Equal(DailyGenerationRunStatus.Completed, result.Status);
        Assert.Equal(1, result.SuccessfulBatchCount);
        Assert.Equal(1, result.FailedBatchCount);
        Assert.Equal(6, result.CachedEntryCount);
        Assert.Equal(2, gateway.Requests.Count);
        Assert.Equal(VanillaTwelve.Take(6), cache.Snapshot().Select(entry => entry.Key.NpcId));
    }

    /// <summary>
    /// 第十三项必须在 flush 后 fail closed，且不得提交被截断的请求。
    /// </summary>
    [Fact]
    public async Task RunDayStartedAsync_ThirteenItemsFailsClosedWithoutGatewayCall()
    {
        ScriptedEventSynchronizer synchronizer = new(revision: 63);
        RecordingGateway gateway = new();
        DailyDialogueCache cache = new();
        cache.Store(CreateLegacyEntry());
        DailyGenerationBatchCoordinator coordinator = new(
            synchronizer,
            gateway,
            cache,
            TimeSpan.FromSeconds(5));
        string[] thirteen = VanillaTwelve.Append("UnexpectedThirteenthNpc").ToArray();

        DailyGenerationRunResult result = await coordinator.RunDayStartedAsync(
            BuildInput(thirteen));

        Assert.Equal(DailyGenerationRunStatus.Fallback, result.Status);
        Assert.Equal(0, result.BatchCount);
        Assert.Empty(result.RequestIds);
        Assert.Empty(gateway.Requests);
        Assert.Empty(cache.Snapshot());
        Assert.Equal(1, synchronizer.FlushCount);
    }

    /// <summary>
    /// 调用方取消必须原样传播并清空整个 cohort，不能保留先完成的 sibling。
    /// </summary>
    [Fact]
    public async Task RunDayStartedAsync_CallerCancellationPropagatesAndClearsCohort()
    {
        using CancellationTokenSource callerCancellation = new();
        ScriptedEventSynchronizer synchronizer = new(revision: 74);
        CancellingGateway gateway = new(callerCancellation);
        DailyDialogueCache cache = new();
        cache.Store(CreateLegacyEntry());
        DailyGenerationBatchCoordinator coordinator = new(
            synchronizer,
            gateway,
            cache,
            TimeSpan.FromSeconds(5));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.RunDayStartedAsync(
                BuildInput(VanillaTwelve),
                callerCancellation.Token));

        Assert.Empty(cache.Snapshot());
        Assert.Equal(1, synchronizer.FlushCount);
        Assert.Equal(2, gateway.Requests.Count);
    }

    private static DayStartedGenerationInput BuildInput(IReadOnlyList<string> npcIds)
    {
        StableDayContext context = new()
        {
            Season = "fall",
            Weather = "rain",
            Locale = "zh-CN",
            ProgressionSignals = ParseJsonObject("{\"community_center\":\"complete\"}"),
        };
        DailyDialogueGenerationInput[] candidates = npcIds
            .Select(
                (npcId, index) =>
                {
                    string sourceText = $"原版台词-{npcId}";
                    DialogueCandidate candidate = new(
                        npcId,
                        DialogueSourceFamily.OrdinaryDaily,
                        "zh-CN",
                        $"Characters/Dialogue/{npcId}",
                        "fall_Mon",
                        sourceText,
                        SourceDialogueHasher.Compute(sourceText),
                        new[] { "样例一。", "样例二。", "样例三。" });
                    RelationshipSnapshot relationship = new()
                    {
                        FriendshipPoints = 500 + index,
                        RelationshipStage = "friend",
                    };
                    return new DailyDialogueGenerationInput(
                        candidate,
                        relationship,
                        Array.Empty<JsonElement>());
                })
            .ToArray();
        return new DayStartedGenerationInput(
            "save-batch-cohort",
            "player-batch-cohort",
            14,
            context,
            candidates);
    }

    private static DialogueGenerationBatchResponse BuildGeneratedResponse(
        DialogueGenerationBatchRequest request)
    {
        return new DialogueGenerationBatchResponse
        {
            SchemaVersion = request.SchemaVersion,
            RequestId = request.RequestId,
            MemoryRevision = request.RequiredMemoryRevision,
            Items = request.Items
                .Select(
                    (item, index) => new DialogueGenerationItemResult
                    {
                        TaskId = item.TaskId,
                        GenerationId = $"generation-{request.RequestId}-{index}",
                        GenerationKey = $"generation-key-{request.RequestId}-{index}",
                        Status = DialogueGenerationStatus.Generated,
                        Text = $"增强台词-{index}",
                        SourceHash = item.SourceDialogue.SourceHash,
                        ReasonCode = "SCRIPTED_GENERATED",
                        EvidenceIds = new List<string> { $"evidence-{index}" },
                        TraceId = $"trace-{request.RequestId}-{index}",
                    })
                .ToList(),
        };
    }

    private static DailyDialogueCacheEntry CreateLegacyEntry()
    {
        const string source = "旧原文";
        return new DailyDialogueCacheEntry(
            new DailyDialogueCacheKey(
                13,
                "zh-CN",
                "Abigail",
                "Characters/Dialogue/Abigail",
                "fall_Mon"),
            DialogueSourceFamily.OrdinaryDaily,
            source,
            SourceDialogueHasher.Compute(source),
            "旧增强文本");
    }

    private static JsonElement ParseJsonObject(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class ScriptedEventSynchronizer : IEventOutboxSynchronizer
    {
        private readonly int revision;

        public ScriptedEventSynchronizer(int revision)
        {
            this.revision = revision;
        }

        public int FlushCount { get; private set; }

        public int ThroughDayIndex { get; private set; }

        public Task<EventOutboxWatermark> FlushThroughDayAsync(
            int throughDayIndex,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FlushCount++;
            ThroughDayIndex = throughDayIndex;
            return Task.FromResult(new EventOutboxWatermark(revision, throughDayIndex));
        }
    }

    private class RecordingGateway : IDialogueGenerationGateway
    {
        public List<DialogueGenerationBatchRequest> Requests { get; } = new();

        public virtual Task<DialogueGenerationBatchResponse> GenerateBatchAsync(
            DialogueGenerationBatchRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(BuildGeneratedResponse(request));
        }
    }

    private sealed class ConcurrentGateway : RecordingGateway
    {
        private readonly TaskCompletionSource bothStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int startedCount;

        public bool StartedTogether { get; private set; }

        public List<CancellationToken> ObservedTokens { get; } = new();

        public override async Task<DialogueGenerationBatchResponse> GenerateBatchAsync(
            DialogueGenerationBatchRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            ObservedTokens.Add(cancellationToken);
            if (Interlocked.Increment(ref startedCount) == 2)
            {
                StartedTogether = true;
                bothStarted.TrySetResult();
            }

            await bothStarted.Task.WaitAsync(cancellationToken);
            return BuildGeneratedResponse(request);
        }
    }

    private sealed class PartialFailureGateway : RecordingGateway
    {
        public override Task<DialogueGenerationBatchResponse> GenerateBatchAsync(
            DialogueGenerationBatchRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (Requests.Count == 1)
            {
                return Task.FromException<DialogueGenerationBatchResponse>(
                    new InvalidOperationException("scripted first batch failure"));
            }

            return Task.FromResult(BuildGeneratedResponse(request));
        }
    }

    private sealed class MutatingFirstCallGateway : RecordingGateway
    {
        private readonly Action mutateOriginalInput;

        public MutatingFirstCallGateway(Action mutateOriginalInput)
        {
            this.mutateOriginalInput = mutateOriginalInput;
        }

        public override Task<DialogueGenerationBatchResponse> GenerateBatchAsync(
            DialogueGenerationBatchRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (Requests.Count == 1)
            {
                mutateOriginalInput();
            }

            return Task.FromResult(BuildGeneratedResponse(request));
        }
    }

    private sealed class InvalidFirstEnvelopeGateway : RecordingGateway
    {
        public override Task<DialogueGenerationBatchResponse> GenerateBatchAsync(
            DialogueGenerationBatchRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            DialogueGenerationBatchResponse response = BuildGeneratedResponse(request);
            if (Requests.Count == 1)
            {
                response.RequestId += "-mismatch";
            }

            return Task.FromResult(response);
        }
    }

    private sealed class TailTimeoutGateway : RecordingGateway
    {
        public override async Task<DialogueGenerationBatchResponse> GenerateBatchAsync(
            DialogueGenerationBatchRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (Requests.Count == 1)
            {
                return BuildGeneratedResponse(request);
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("deadline 后不可到达。");
        }
    }

    private sealed class CancellingGateway : RecordingGateway
    {
        private readonly CancellationTokenSource callerCancellation;
        private int requestCount;

        public CancellingGateway(CancellationTokenSource callerCancellation)
        {
            this.callerCancellation = callerCancellation;
        }

        public override async Task<DialogueGenerationBatchResponse> GenerateBatchAsync(
            DialogueGenerationBatchRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (Interlocked.Increment(ref requestCount) == 2)
            {
                callerCancellation.Cancel();
            }

            // 第一批先停在同一调用方 token 上；第二批启动并触发取消后，两批都应自然观察到取消。
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("取消后不可到达。 ");
        }
    }
}
