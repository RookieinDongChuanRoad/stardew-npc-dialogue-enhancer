using System.Text.Json;
using StardewNpcAgent.Application;
using StardewNpcAgent.Contracts;
using StardewNpcAgent.Game;
using StardewNpcAgent.Infrastructure.Storage;

namespace StardewNpcAgent.Tests.Application;

/// <summary>
/// 验证 DayStarted 每日生成协调器的确定顺序、完整响应映射和 fail-closed cache 语义。
/// </summary>
/// <remarks>
/// 测试只使用 scripted 端口，不启动 HTTP、SMAPI 事件、后台线程或真实游戏。测试输入均为
/// 稳定 DTO 与 <see cref="DialogueCandidate"/>，从而把 Phase 4 的应用层边界与 Phase 7
/// 网络/游戏适配器明确分开。
/// </remarks>
public sealed class DailyGenerationCoordinatorTests
{
    /// <summary>
    /// 生成请求不得在事件刷新完成前出现；刷新完成后，必须精确使用持久化 revision，
    /// 并保持候选输入顺序及“当前日减一”的事件封账边界。
    /// </summary>
    [Fact]
    public async Task RunDayStartedAsync_AwaitsEventFlushThenBuildsOrderedRequestFromPersistedWatermark()
    {
        TaskCompletionSource<EventOutboxWatermark> flushCompletion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptedEventOutboxSynchronizer eventSynchronizer = new()
        {
            Handler = (_, _) => flushCompletion.Task,
        };
        ScriptedDialogueGenerationGateway generationGateway = new()
        {
            Handler = (request, _) => Task.FromResult(
                BuildValidResponse(
                    request,
                    DialogueGenerationStatus.Passthrough,
                    DialogueGenerationStatus.Passthrough)),
        };
        DailyDialogueCache cache = new();
        DailyGenerationCoordinator coordinator = new(eventSynchronizer, generationGateway, cache);
        DayStartedGenerationInput input = BuildInput(
            gameDayIndex: 14,
            locale: "zh-CN",
            npcIds: new[] { "Sebastian", "Abigail" });

        Task<DailyGenerationRunResult> runTask = coordinator.RunDayStartedAsync(input);

        Assert.Equal(new[] { 13 }, eventSynchronizer.ThroughDayIndexes);
        Assert.Empty(generationGateway.Requests);

        flushCompletion.SetResult(new EventOutboxWatermark(73, 13));
        DailyGenerationRunResult result = await runTask;

        DialogueGenerationBatchRequest request = Assert.Single(generationGateway.Requests);
        Assert.Equal(DailyGenerationRunStatus.Completed, result.Status);
        Assert.Equal(73, request.RequiredMemoryRevision);
        Assert.Equal(new[] { "Sebastian", "Abigail" }, request.Items.Select(item => item.NpcId));
        Assert.Equal(input.StableDayContext.Locale, request.StableDayContext.Locale);
        Assert.All(request.Items, item => Assert.False(string.IsNullOrWhiteSpace(item.TaskId)));
    }

    /// <summary>
    /// 相同业务输入和相同事件水位跨协调器实例重试时，request/task identity 必须逐字符稳定，
    /// 不能依赖随机数、时钟、对象引用或调用次数。
    /// </summary>
    [Fact]
    public async Task RunDayStartedAsync_DerivesStableRequestAndTaskIdsForIdenticalInputs()
    {
        DayStartedGenerationInput firstInput = BuildInput(
            gameDayIndex: 14,
            locale: "zh-CN",
            npcIds: new[] { "Abigail", "Sebastian" });
        DayStartedGenerationInput secondInput = BuildInput(
            gameDayIndex: 14,
            locale: "zh-CN",
            npcIds: new[] { "Abigail", "Sebastian" });

        DialogueGenerationBatchRequest firstRequest = await RunAndCaptureRequestAsync(firstInput, revision: 73);
        DialogueGenerationBatchRequest secondRequest = await RunAndCaptureRequestAsync(secondInput, revision: 73);

        Assert.Equal(firstRequest.RequestId, secondRequest.RequestId);
        Assert.Equal(
            firstRequest.Items.Select(item => item.TaskId),
            secondRequest.Items.Select(item => item.TaskId));
    }

    /// <summary>
    /// 合法 partial batch 中只有 generated 可进入 cache；其他三个业务终态都是正常原版路径。
    /// </summary>
    [Theory]
    [InlineData(DialogueGenerationStatus.Passthrough)]
    [InlineData(DialogueGenerationStatus.Skipped)]
    [InlineData(DialogueGenerationStatus.Failed)]
    public async Task RunDayStartedAsync_ValidPartialResponseCachesOnlyGenerated(
        DialogueGenerationStatus secondStatus)
    {
        DayStartedGenerationInput input = BuildInput(
            gameDayIndex: 14,
            locale: "zh-CN",
            npcIds: new[] { "Abigail", "Sebastian" });
        ScriptedEventOutboxSynchronizer eventSynchronizer = CreateSuccessfulSynchronizer(revision: 73);
        ScriptedDialogueGenerationGateway generationGateway = new()
        {
            Handler = (request, _) => Task.FromResult(
                BuildValidResponse(
                    request,
                    DialogueGenerationStatus.Generated,
                    secondStatus)),
        };
        DailyDialogueCache cache = new();
        DailyGenerationCoordinator coordinator = new(eventSynchronizer, generationGateway, cache);

        DailyGenerationRunResult result = await coordinator.RunDayStartedAsync(input);

        DailyDialogueCacheEntry cachedEntry = Assert.Single(cache.Snapshot());
        Assert.Equal(DailyGenerationRunStatus.Completed, result.Status);
        Assert.Equal(1, result.CachedEntryCount);
        Assert.Equal("Abigail", cachedEntry.Key.NpcId);
        Assert.Equal(DialogueSourceFamily.OrdinaryDaily, cachedEntry.SourceFamily);
        Assert.Equal(
            input.Candidates[0].Candidate.SourceText,
            cachedEntry.SourceText);
        Assert.Equal("增强台词-Abigail", cachedEntry.EnhancedText);
        Assert.Equal("generation-0", cachedEntry.GenerationId);
        Assert.Equal("generation-key-0", cachedEntry.GenerationKey);
        Assert.Equal("trace-0", cachedEntry.TraceId);
        Assert.True(cachedEntry.HasCompleteGenerationMetadata);
        Assert.False(cache.TryGet(CreateCacheKey(input, itemIndex: 1), out _));
    }

    /// <summary>
    /// HTTP contract 只把 generated text 声明为字符串；游戏侧必须在 cache 前再次执行 typed
    /// template parser。一个坏 item 只回退该 NPC，不能把第二个合法 NPC 一起丢弃。
    /// </summary>
    [Fact]
    public async Task RunDayStartedAsync_SkipsGeneratedItemThatFailsGameTemplatePolicy()
    {
        DayStartedGenerationInput input = BuildInput(
            gameDayIndex: 14,
            locale: "zh-CN",
            npcIds: new[] { "Abigail", "Sebastian" });
        ScriptedDialogueGenerationGateway generationGateway = new()
        {
            Handler = (request, _) =>
            {
                DialogueGenerationBatchResponse response = BuildValidResponse(
                    request,
                    DialogueGenerationStatus.Generated,
                    DialogueGenerationStatus.Generated);
                response.Items[0].Text = "非法的第二个槽@@";
                response.Items[1].Text = "合法称呼，@。";
                return Task.FromResult(response);
            },
        };
        DailyDialogueCache cache = new();
        DailyGenerationCoordinator coordinator = new(
            CreateSuccessfulSynchronizer(revision: 73),
            generationGateway,
            cache);

        DailyGenerationRunResult result = await coordinator.RunDayStartedAsync(input);

        DailyDialogueCacheEntry cached = Assert.Single(cache.Snapshot());
        Assert.Equal(DailyGenerationRunStatus.Completed, result.Status);
        Assert.Equal(1, result.CachedEntryCount);
        Assert.Equal("Sebastian", cached.Key.NpcId);
        Assert.Equal("合法称呼，@。", cached.EnhancedText);
    }

    /// <summary>
    /// 即使本日没有候选，协调器也必须先刷新昨日事件；由于 wire contract 禁止空 items，
    /// 此分支不得调用生成网关，并应把上次运行留下的 cache 清空。
    /// </summary>
    [Fact]
    public async Task RunDayStartedAsync_EmptyCandidatesStillFlushesEventsWithoutCallingGateway()
    {
        DailyDialogueCache cache = new();
        cache.Store(CreateLegacyCacheEntry(gameDayIndex: 13, locale: "zh-CN"));
        ScriptedEventOutboxSynchronizer eventSynchronizer = CreateSuccessfulSynchronizer(revision: 73);
        ScriptedDialogueGenerationGateway generationGateway = new();
        DailyGenerationCoordinator coordinator = new(eventSynchronizer, generationGateway, cache);
        DayStartedGenerationInput input = BuildInput(
            gameDayIndex: 14,
            locale: "zh-CN",
            npcIds: Array.Empty<string>());

        DailyGenerationRunResult result = await coordinator.RunDayStartedAsync(input);

        Assert.Equal(DailyGenerationRunStatus.Completed, result.Status);
        Assert.Equal(0, result.CachedEntryCount);
        Assert.Empty(result.RequestIds);
        Assert.Equal(new[] { 13 }, eventSynchronizer.ThroughDayIndexes);
        Assert.Empty(generationGateway.Requests);
        Assert.Empty(cache.Snapshot());
    }

    /// <summary>
    /// 每次 Run 都是新的日级事务边界；改变 day 或 locale 后，即使第二次合法响应全部
    /// passthrough，也不能继续命中第一次运行的 generated cache。
    /// </summary>
    [Theory]
    [InlineData(15, "zh-CN")]
    [InlineData(14, "en")]
    public async Task RunDayStartedAsync_NewDayOrLocaleClearsPreviousRunCache(
        int secondDayIndex,
        string secondLocale)
    {
        DailyDialogueCache cache = new();
        ScriptedEventOutboxSynchronizer eventSynchronizer = CreateSuccessfulSynchronizer(revision: 73);
        ScriptedDialogueGenerationGateway generationGateway = new()
        {
            Handler = (request, _) => Task.FromResult(
                BuildValidResponse(
                    request,
                    request.GameDayIndex == 14 && request.StableDayContext.Locale == "zh-CN"
                        ? DialogueGenerationStatus.Generated
                        : DialogueGenerationStatus.Passthrough)),
        };
        DailyGenerationCoordinator coordinator = new(eventSynchronizer, generationGateway, cache);
        DayStartedGenerationInput firstInput = BuildInput(14, "zh-CN", new[] { "Abigail" });
        DayStartedGenerationInput secondInput = BuildInput(
            secondDayIndex,
            secondLocale,
            new[] { "Abigail" });

        await coordinator.RunDayStartedAsync(firstInput);
        DailyDialogueCacheKey firstKey = CreateCacheKey(firstInput, itemIndex: 0);
        Assert.True(cache.TryGet(firstKey, out _));

        DailyGenerationRunResult secondResult = await coordinator.RunDayStartedAsync(secondInput);

        Assert.Equal(DailyGenerationRunStatus.Completed, secondResult.Status);
        Assert.Empty(cache.Snapshot());
        Assert.False(cache.TryGet(firstKey, out _));
    }

    /// <summary>
    /// wire、envelope、逐项映射或展示身份任一字段不合法时必须整批 fail closed；
    /// 即使前一轮有缓存、响应中的第一项看似 generated，也不能留下部分结果。
    /// </summary>
    [Theory]
    [InlineData(InvalidResponseMutation.SchemaVersion)]
    [InlineData(InvalidResponseMutation.RequestId)]
    [InlineData(InvalidResponseMutation.MemoryRevision)]
    [InlineData(InvalidResponseMutation.NullItems)]
    [InlineData(InvalidResponseMutation.MissingItem)]
    [InlineData(InvalidResponseMutation.NullItem)]
    [InlineData(InvalidResponseMutation.ReversedOrder)]
    [InlineData(InvalidResponseMutation.TaskId)]
    [InlineData(InvalidResponseMutation.SourceHash)]
    [InlineData(InvalidResponseMutation.UnknownStatus)]
    [InlineData(InvalidResponseMutation.GeneratedTextMissing)]
    [InlineData(InvalidResponseMutation.NonGeneratedTextPresent)]
    [InlineData(InvalidResponseMutation.GenerationIdMissing)]
    [InlineData(InvalidResponseMutation.GenerationKeyMissing)]
    [InlineData(InvalidResponseMutation.TraceIdMissing)]
    [InlineData(InvalidResponseMutation.ReasonCodeMissing)]
    [InlineData(InvalidResponseMutation.EvidenceListNull)]
    [InlineData(InvalidResponseMutation.EvidenceIdMissing)]
    [InlineData(InvalidResponseMutation.DuplicateGenerationId)]
    [InlineData(InvalidResponseMutation.DuplicateGenerationKey)]
    [InlineData(InvalidResponseMutation.DuplicateTraceId)]
    public async Task RunDayStartedAsync_InvalidResponseReturnsFallbackAndLeavesWholeCacheEmpty(
        InvalidResponseMutation mutation)
    {
        DayStartedGenerationInput input = BuildInput(
            gameDayIndex: 14,
            locale: "zh-CN",
            npcIds: new[] { "Abigail", "Sebastian" });
        DailyDialogueCache cache = new();
        cache.Store(CreateLegacyCacheEntry(gameDayIndex: 13, locale: "zh-CN"));
        ScriptedDialogueGenerationGateway generationGateway = new()
        {
            Handler = (request, _) =>
            {
                DialogueGenerationBatchResponse response = BuildValidResponse(
                    request,
                    DialogueGenerationStatus.Generated,
                    DialogueGenerationStatus.Generated);
                ApplyInvalidMutation(response, mutation);
                return Task.FromResult(response);
            },
        };
        DailyGenerationCoordinator coordinator = new(
            CreateSuccessfulSynchronizer(revision: 73),
            generationGateway,
            cache);

        DailyGenerationRunResult result = await coordinator.RunDayStartedAsync(input);

        Assert.Equal(DailyGenerationRunStatus.Fallback, result.Status);
        Assert.Equal(0, result.CachedEntryCount);
        Assert.Empty(cache.Snapshot());
    }

    /// <summary>
    /// 事件同步、网关、非调用方取消型 timeout 与本地 request contract 失败都属于游戏侧
    /// 可降级错误：方法返回 fallback，且任何旧 cache 或半成品都必须为空。
    /// </summary>
    [Theory]
    [InlineData(RunFailureMode.EventSynchronizationException)]
    [InlineData(RunFailureMode.EventSynchronizationTimeout)]
    [InlineData(RunFailureMode.GenerationGatewayException)]
    [InlineData(RunFailureMode.GenerationGatewayTimeout)]
    [InlineData(RunFailureMode.InvalidRequestContract)]
    public async Task RunDayStartedAsync_OperationalOrContractFailureReturnsFallback(
        RunFailureMode failureMode)
    {
        DayStartedGenerationInput input = BuildInput(14, "zh-CN", new[] { "Abigail" });
        ScriptedEventOutboxSynchronizer eventSynchronizer = CreateSuccessfulSynchronizer(revision: 73);
        ScriptedDialogueGenerationGateway generationGateway = new()
        {
            Handler = (request, _) => Task.FromResult(
                BuildValidResponse(request, DialogueGenerationStatus.Generated)),
        };

        switch (failureMode)
        {
            case RunFailureMode.EventSynchronizationException:
                eventSynchronizer.Handler = (_, _) =>
                    Task.FromException<EventOutboxWatermark>(new InvalidOperationException("scripted event failure"));
                break;

            case RunFailureMode.EventSynchronizationTimeout:
                eventSynchronizer.Handler = (_, _) =>
                    Task.FromException<EventOutboxWatermark>(new OperationCanceledException("scripted timeout"));
                break;

            case RunFailureMode.GenerationGatewayException:
                generationGateway.Handler = (_, _) =>
                    Task.FromException<DialogueGenerationBatchResponse>(new InvalidOperationException("scripted gateway failure"));
                break;

            case RunFailureMode.GenerationGatewayTimeout:
                generationGateway.Handler = (_, _) =>
                    Task.FromException<DialogueGenerationBatchResponse>(new TimeoutException("scripted timeout"));
                break;

            case RunFailureMode.InvalidRequestContract:
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(failureMode), failureMode, null);
        }

        if (failureMode == RunFailureMode.InvalidRequestContract)
        {
            input = input with
            {
                StableDayContext = new StableDayContext
                {
                    Season = " ",
                    Weather = input.StableDayContext.Weather,
                    Locale = input.StableDayContext.Locale,
                    ProgressionSignals = input.StableDayContext.ProgressionSignals.Clone(),
                },
            };
        }

        DailyDialogueCache cache = new();
        cache.Store(CreateLegacyCacheEntry(gameDayIndex: 13, locale: "zh-CN"));
        DailyGenerationCoordinator coordinator = new(eventSynchronizer, generationGateway, cache);

        DailyGenerationRunResult result = await coordinator.RunDayStartedAsync(input);

        Assert.Equal(DailyGenerationRunStatus.Fallback, result.Status);
        Assert.Empty(cache.Snapshot());
        if (failureMode == RunFailureMode.InvalidRequestContract)
        {
            Assert.Empty(generationGateway.Requests);
            Assert.Single(eventSynchronizer.ThroughDayIndexes);
        }
    }

    /// <summary>
    /// 调用方显式取消不是普通后端 timeout；协调器必须传播取消语义，不能把它伪装为 fallback。
    /// </summary>
    [Fact]
    public async Task RunDayStartedAsync_PropagatesCallerCancellation()
    {
        using CancellationTokenSource cancellationSource = new();
        ScriptedDialogueGenerationGateway generationGateway = new()
        {
            Handler = (_, cancellationToken) =>
            {
                cancellationSource.Cancel();
                return Task.FromCanceled<DialogueGenerationBatchResponse>(cancellationToken);
            },
        };
        DailyDialogueCache cache = new();
        cache.Store(CreateLegacyCacheEntry(gameDayIndex: 13, locale: "zh-CN"));
        DailyGenerationCoordinator coordinator = new(
            CreateSuccessfulSynchronizer(revision: 73),
            generationGateway,
            cache);
        DayStartedGenerationInput input = BuildInput(14, "zh-CN", new[] { "Abigail" });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.RunDayStartedAsync(input, cancellationSource.Token));

        Assert.Empty(cache.Snapshot());
        Assert.Single(generationGateway.Requests);
    }

    /// <summary>
    /// 运行一个独立协调器并截获它发给 gateway 的真实请求，供稳定 identity 测试复用。
    /// </summary>
    private static async Task<DialogueGenerationBatchRequest> RunAndCaptureRequestAsync(
        DayStartedGenerationInput input,
        int revision)
    {
        ScriptedDialogueGenerationGateway gateway = new()
        {
            Handler = (request, _) => Task.FromResult(
                BuildValidResponse(
                    request,
                    DialogueGenerationStatus.Passthrough,
                    DialogueGenerationStatus.Passthrough)),
        };
        DailyGenerationCoordinator coordinator = new(
            CreateSuccessfulSynchronizer(revision),
            gateway,
            new DailyDialogueCache());

        DailyGenerationRunResult result = await coordinator.RunDayStartedAsync(input);

        Assert.Equal(DailyGenerationRunStatus.Completed, result.Status);
        return Assert.Single(gateway.Requests);
    }

    /// <summary>
    /// 创建返回合法、已持久化水位的事件同步 fake。
    /// </summary>
    private static ScriptedEventOutboxSynchronizer CreateSuccessfulSynchronizer(int revision)
    {
        return new ScriptedEventOutboxSynchronizer
        {
            Handler = (throughDayIndex, _) => Task.FromResult(
                new EventOutboxWatermark(revision, throughDayIndex)),
        };
    }

    /// <summary>
    /// 构造稳定的 DayStarted 输入。候选顺序完全由 <paramref name="npcIds"/> 决定，
    /// 便于明确验证 coordinator 不排序、不按 NPC 名重排。
    /// </summary>
    private static DayStartedGenerationInput BuildInput(
        int gameDayIndex,
        string locale,
        IReadOnlyList<string> npcIds)
    {
        StableDayContext stableDayContext = new()
        {
            Season = "spring",
            Weather = "rain",
            Locale = locale,
            ProgressionSignals = ParseJsonObject(
                "{\"mine_level\":40,\"community_center\":\"pantry_completed\"}"),
        };
        DailyDialogueGenerationInput[] candidates = npcIds
            .Select(
                (npcId, index) =>
                {
                    string sourceText = $"原版台词-{npcId}";
                    DialogueCandidate candidate = new(
                        npcId,
                        DialogueSourceFamily.OrdinaryDaily,
                        locale,
                        $"Characters/Dialogue/{npcId}",
                        "spring_Mon",
                        sourceText,
                        SourceDialogueHasher.Compute(sourceText),
                        new[]
                        {
                            $"{npcId} 风格样本一",
                            $"{npcId} 风格样本二",
                            $"{npcId} 风格样本三",
                        });
                    RelationshipSnapshot relationship = new()
                    {
                        FriendshipPoints = 500 + index,
                        RelationshipStage = index == 0 ? "friend" : "acquaintance",
                    };
                    IReadOnlyList<JsonElement> memorySignals = index == 0
                        ? new[]
                        {
                            ParseJsonObject(
                                $"{{\"signal_id\":\"signal-{npcId}\",\"occurred_day_index\":13}}"),
                        }
                        : Array.Empty<JsonElement>();
                    return new DailyDialogueGenerationInput(candidate, relationship, memorySignals);
                })
            .ToArray();

        return new DayStartedGenerationInput(
            "save-standard-farm-001",
            "player-farmer-001",
            gameDayIndex,
            stableDayContext,
            candidates);
    }

    /// <summary>
    /// 按真实 request 的 task/source identity 构造合法响应；状态数组必须与 items 同长。
    /// </summary>
    private static DialogueGenerationBatchResponse BuildValidResponse(
        DialogueGenerationBatchRequest request,
        params DialogueGenerationStatus[] statuses)
    {
        Assert.Equal(request.Items.Count, statuses.Length);
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
                        GenerationId = $"generation-{index}",
                        GenerationKey = $"generation-key-{index}",
                        Status = statuses[index],
                        Text = statuses[index] == DialogueGenerationStatus.Generated
                            ? $"增强台词-{item.NpcId}"
                            : null,
                        SourceHash = item.SourceDialogue.SourceHash,
                        ReasonCode = statuses[index] == DialogueGenerationStatus.Generated
                            ? "SCRIPTED_GENERATED"
                            : $"SCRIPTED_{statuses[index].ToString().ToUpperInvariant()}",
                        EvidenceIds = statuses[index] == DialogueGenerationStatus.Generated
                            ? new List<string> { $"evidence-{index}" }
                            : new List<string>(),
                        TraceId = $"trace-{index}",
                    })
                .ToList(),
        };
    }

    /// <summary>
    /// 对一个原本合法的响应只破坏一个维度，使每个 RED 都能定位到明确不变量。
    /// </summary>
    private static void ApplyInvalidMutation(
        DialogueGenerationBatchResponse response,
        InvalidResponseMutation mutation)
    {
        switch (mutation)
        {
            case InvalidResponseMutation.SchemaVersion:
                response.SchemaVersion = "2.0";
                break;
            case InvalidResponseMutation.RequestId:
                response.RequestId += "-other";
                break;
            case InvalidResponseMutation.MemoryRevision:
                response.MemoryRevision++;
                break;
            case InvalidResponseMutation.NullItems:
                response.Items = null!;
                break;
            case InvalidResponseMutation.MissingItem:
                response.Items.RemoveAt(1);
                break;
            case InvalidResponseMutation.NullItem:
                response.Items[0] = null!;
                break;
            case InvalidResponseMutation.ReversedOrder:
                response.Items.Reverse();
                break;
            case InvalidResponseMutation.TaskId:
                response.Items[0].TaskId += "-other";
                break;
            case InvalidResponseMutation.SourceHash:
                response.Items[0].SourceHash += "-other";
                break;
            case InvalidResponseMutation.UnknownStatus:
                response.Items[0].Status = (DialogueGenerationStatus)999;
                response.Items[0].Text = null;
                break;
            case InvalidResponseMutation.GeneratedTextMissing:
                response.Items[0].Text = null;
                break;
            case InvalidResponseMutation.NonGeneratedTextPresent:
                response.Items[1].Status = DialogueGenerationStatus.Passthrough;
                response.Items[1].Text = "不应存在的文本";
                break;
            case InvalidResponseMutation.GenerationIdMissing:
                response.Items[0].GenerationId = " ";
                break;
            case InvalidResponseMutation.GenerationKeyMissing:
                response.Items[0].GenerationKey = " ";
                break;
            case InvalidResponseMutation.TraceIdMissing:
                response.Items[0].TraceId = " ";
                break;
            case InvalidResponseMutation.ReasonCodeMissing:
                response.Items[0].ReasonCode = " ";
                break;
            case InvalidResponseMutation.EvidenceListNull:
                response.Items[0].EvidenceIds = null!;
                break;
            case InvalidResponseMutation.EvidenceIdMissing:
                response.Items[0].EvidenceIds[0] = " ";
                break;
            case InvalidResponseMutation.DuplicateGenerationId:
                response.Items[1].GenerationId = response.Items[0].GenerationId;
                break;
            case InvalidResponseMutation.DuplicateGenerationKey:
                response.Items[1].GenerationKey = response.Items[0].GenerationKey;
                break;
            case InvalidResponseMutation.DuplicateTraceId:
                response.Items[1].TraceId = response.Items[0].TraceId;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
        }
    }

    /// <summary>
    /// 从输入中的指定候选构造与 coordinator 相同的 cache 复合 key。
    /// </summary>
    private static DailyDialogueCacheKey CreateCacheKey(
        DayStartedGenerationInput input,
        int itemIndex)
    {
        DialogueCandidate candidate = input.Candidates[itemIndex].Candidate;
        return new DailyDialogueCacheKey(
            input.GameDayIndex,
            input.StableDayContext.Locale,
            candidate.NpcId,
            candidate.AssetName,
            candidate.DialogueKey);
    }

    /// <summary>
    /// 创建旧静态 Spike 风格的三参数 cache，验证新运行总会先删除旧状态。
    /// </summary>
    private static DailyDialogueCacheEntry CreateLegacyCacheEntry(int gameDayIndex, string locale)
    {
        return new DailyDialogueCacheEntry(
            new DailyDialogueCacheKey(
                gameDayIndex,
                locale,
                "Abigail",
                "Characters/Dialogue/Abigail",
                "spring_Mon"),
            DialogueSourceFamily.OrdinaryDaily,
            "旧原版台词",
            SourceDialogueHasher.Compute("旧原版台词"),
            "旧静态增强台词");
    }

    /// <summary>
    /// 解析并脱离 JsonDocument 生命周期返回 JSON object。
    /// </summary>
    private static JsonElement ParseJsonObject(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// 事件同步 scripted fake；默认 handler 故意失败，避免测试漏配时静默通过。
    /// </summary>
    private sealed class ScriptedEventOutboxSynchronizer : IEventOutboxSynchronizer
    {
        public Func<int, CancellationToken, Task<EventOutboxWatermark>> Handler { get; set; } =
            (_, _) => Task.FromException<EventOutboxWatermark>(
                new InvalidOperationException("测试未配置事件同步脚本。"));

        public List<int> ThroughDayIndexes { get; } = new();

        public Task<EventOutboxWatermark> FlushThroughDayAsync(
            int throughDayIndex,
            CancellationToken cancellationToken)
        {
            ThroughDayIndexes.Add(throughDayIndex);
            return Handler(throughDayIndex, cancellationToken);
        }
    }

    /// <summary>
    /// 生成网关 scripted fake；保存真实请求快照，便于验证顺序、水位和稳定 identity。
    /// </summary>
    private sealed class ScriptedDialogueGenerationGateway : IDialogueGenerationGateway
    {
        public Func<DialogueGenerationBatchRequest, CancellationToken, Task<DialogueGenerationBatchResponse>>
            Handler
        { get; set; } = (_, _) =>
                Task.FromException<DialogueGenerationBatchResponse>(
                    new InvalidOperationException("测试不应调用生成网关。"));

        public List<DialogueGenerationBatchRequest> Requests { get; } = new();

        public Task<DialogueGenerationBatchResponse> GenerateBatchAsync(
            DialogueGenerationBatchRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Handler(request, cancellationToken);
        }
    }

    /// <summary>
    /// 每个枚举值只破坏一个响应字段或跨项不变量。
    /// </summary>
    public enum InvalidResponseMutation
    {
        SchemaVersion,
        RequestId,
        MemoryRevision,
        NullItems,
        MissingItem,
        NullItem,
        ReversedOrder,
        TaskId,
        SourceHash,
        UnknownStatus,
        GeneratedTextMissing,
        NonGeneratedTextPresent,
        GenerationIdMissing,
        GenerationKeyMissing,
        TraceIdMissing,
        ReasonCodeMissing,
        EvidenceListNull,
        EvidenceIdMissing,
        DuplicateGenerationId,
        DuplicateGenerationKey,
        DuplicateTraceId,
    }

    /// <summary>
    /// 运行阶段失败分类，仅供 theory 选择 scripted fake 行为。
    /// </summary>
    public enum RunFailureMode
    {
        EventSynchronizationException,
        EventSynchronizationTimeout,
        GenerationGatewayException,
        GenerationGatewayTimeout,
        InvalidRequestContract,
    }
}
