using StardewNpcAgent.Contracts;
using StardewNpcAgent.Infrastructure.Storage;

namespace StardewNpcAgent.Application;

/// <summary>
/// 单个 HTTP batch 的局部执行结果。
/// </summary>
/// <param name="IsSuccessful">请求、响应映射与局部 staging 是否全部合法。</param>
/// <param name="RequestId">成功构造 request 后的稳定 ID；构造前失败时为 null。</param>
/// <param name="Entries">只属于本 batch 的 generated entries；失败时恒为空。</param>
internal sealed record DailyGenerationBatchExecutionResult(
    bool IsSuccessful,
    string? RequestId,
    IReadOnlyList<DailyDialogueCacheEntry> Entries);

/// <summary>
/// 编排一次 DayStarted cohort 的单次事件刷新、稳定分片、并发请求和局部结果合并。
/// </summary>
/// <remarks>
/// 协调器是 cohort 的唯一事务边界：live staging cache 只清一次，event outbox 只刷新一次，
/// 两个 batch 共用同一个取消源和绝对 deadline。普通 batch 失败只丢弃自己的局部结果；
/// 调用方取消则清空整个 cohort 并原样传播。
/// </remarks>
public sealed class DailyGenerationBatchCoordinator
{
    /// <summary>
    /// 兼容旧 <see cref="DailyGenerationCoordinator"/> facade 的默认生成预算。
    /// 正式 runtime 会显式传入已经校验的 Mod 配置值。
    /// </summary>
    internal static readonly TimeSpan DefaultGenerationDeadline = TimeSpan.FromSeconds(120);

    private readonly IEventOutboxSynchronizer eventOutboxSynchronizer;
    private readonly IDialogueGenerationGateway dialogueGenerationGateway;
    private readonly DailyDialogueCache cache;
    private readonly TimeSpan generationDeadline;

    /// <summary>
    /// 创建一个 cohort coordinator。
    /// </summary>
    /// <param name="eventOutboxSynchronizer">只执行一次、返回已持久化 revision 的事件端口。</param>
    /// <param name="dialogueGenerationGateway">由两个并发 batch 共享的生成端口。</param>
    /// <param name="cache">本 cohort 独占写入的 staging cache。</param>
    /// <param name="generationDeadline">两个 batch 共用的总等待预算，必须位于 0～120 秒。</param>
    public DailyGenerationBatchCoordinator(
        IEventOutboxSynchronizer eventOutboxSynchronizer,
        IDialogueGenerationGateway dialogueGenerationGateway,
        DailyDialogueCache cache,
        TimeSpan generationDeadline)
    {
        this.eventOutboxSynchronizer = eventOutboxSynchronizer
            ?? throw new ArgumentNullException(nameof(eventOutboxSynchronizer));
        this.dialogueGenerationGateway = dialogueGenerationGateway
            ?? throw new ArgumentNullException(nameof(dialogueGenerationGateway));
        this.cache = cache ?? throw new ArgumentNullException(nameof(cache));
        if (generationDeadline <= TimeSpan.Zero
            || generationDeadline > DefaultGenerationDeadline)
        {
            throw new ArgumentOutOfRangeException(
                nameof(generationDeadline),
                generationDeadline,
                "每日生成 deadline 必须位于 0～120 秒。");
        }

        this.generationDeadline = generationDeadline;
    }

    /// <summary>
    /// 清一次 cache、刷新一次昨日事件，并在共享 deadline 内并发执行全部计划批次。
    /// </summary>
    /// <param name="input">主线程已经冻结的存档、日期、locale、context 与有序候选。</param>
    /// <param name="cancellationToken">调用方生命周期取消；触发时清 cohort 并原样传播。</param>
    /// <returns>批次计数、所有已构造 request ID 与最终合并 cache 数量。</returns>
    public async Task<DailyGenerationRunResult> RunDayStartedAsync(
        DayStartedGenerationInput input,
        CancellationToken cancellationToken = default)
    {
        // DayStarted 是 staging cache 的事务边界。必须在任何验证或 await 前清旧值，
        // 才不会让跨日、跨 locale 或失败运行继续展示上一轮文本。
        cache.Clear();

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (input is null || input.GameDayIndex < 0)
            {
                return CreatePreBatchFallback();
            }

            EventOutboxWatermark watermark = await eventOutboxSynchronizer
                .FlushThroughDayAsync(input.GameDayIndex - 1, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsValidPersistedWatermark(watermark) || input.Candidates is null)
            {
                return CreatePreBatchFallback();
            }

            IReadOnlyList<IReadOnlyList<DailyDialogueGenerationInput>> plannedBatches;
            try
            {
                plannedBatches = DailyGenerationBatchPlanner.Plan(input.Candidates);
            }
            catch (ArgumentOutOfRangeException)
            {
                // 超过十二项是 configuration/runtime contract 漂移；不得截断后继续生成。
                return CreatePreBatchFallback();
            }

            if (plannedBatches.Count == 0)
            {
                return new DailyGenerationRunResult(
                    DailyGenerationRunStatus.Completed,
                    CachedEntryCount: 0,
                    BatchCount: 0,
                    SuccessfulBatchCount: 0,
                    FailedBatchCount: 0,
                    RequestIds: Array.Empty<string>());
            }

            // 在调用第一个 gateway 前同步构造全部 request。BuildRequest 会深拷贝 context、关系、
            // memory signals 与 style examples，因此 adapter 的同步代码也无法改变 sibling 快照。
            DialogueGenerationBatchRequest?[] preparedRequests = new DialogueGenerationBatchRequest?[
                plannedBatches.Count];
            for (int index = 0; index < plannedBatches.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    preparedRequests[index] = DailyGenerationCoordinator.BuildRequest(
                        CreateBatchInput(input, plannedBatches[index]),
                        watermark.MemoryRevision);
                }
                catch (Exception)
                {
                    // 一个局部输入不合法只标记该 batch；其他批次仍使用同一已持久化 revision。
                    preparedRequests[index] = null;
                }
            }

            // 单个 linked source 在此刻冻结绝对 deadline；所有合法 request task 在 await 前立即创建，
            // 因而没有“第二批等第一批结束后再获得完整预算”的串行窗口。
            using CancellationTokenSource deadlineSource =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadlineSource.CancelAfter(generationDeadline);
            Task<DailyGenerationBatchExecutionResult>[] batchTasks = preparedRequests
                .Select(
                    request => request is null
                        ? Task.FromResult(
                            new DailyGenerationBatchExecutionResult(
                                IsSuccessful: false,
                                RequestId: null,
                                Array.Empty<DailyDialogueCacheEntry>()))
                        : RunPreparedBatchIsolatedAsync(
                            request,
                            deadlineSource.Token,
                            cancellationToken))
                .ToArray();
            DailyGenerationBatchExecutionResult[] batchResults = await Task.WhenAll(batchTasks)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            DailyDialogueCacheEntry[] mergedEntries = batchResults
                .Where(result => result.IsSuccessful)
                .SelectMany(result => result.Entries)
                .ToArray();
            foreach (DailyDialogueCacheEntry entry in mergedEntries)
            {
                // 所有局部结果均已完成验证后才合并；一个失败 sibling 永远不会调用 Store。
                cache.Store(entry);
            }

            int successfulBatchCount = batchResults.Count(result => result.IsSuccessful);
            int failedBatchCount = batchResults.Length - successfulBatchCount;
            string[] requestIds = batchResults
                .Select(result => result.RequestId)
                .Where(requestId => requestId is not null)
                .Select(requestId => requestId!)
                .ToArray();
            DailyGenerationRunStatus status = successfulBatchCount > 0
                ? DailyGenerationRunStatus.Completed
                : DailyGenerationRunStatus.Fallback;
            return new DailyGenerationRunResult(
                status,
                mergedEntries.Length,
                batchResults.Length,
                successfulBatchCount,
                failedBatchCount,
                Array.AsReadOnly(requestIds));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cache.Clear();
            throw;
        }
        catch (Exception)
        {
            // flush、输入冻结或最终 cache 合并的 cohort 级错误仍整体回退。异常正文不进入日志。
            cache.Clear();
            return CreatePreBatchFallback();
        }
    }

    /// <summary>
    /// 执行一个最多八项的局部 batch；普通失败被收敛为局部结果，调用方取消保持异常语义。
    /// </summary>
    private async Task<DailyGenerationBatchExecutionResult> RunPreparedBatchIsolatedAsync(
        DialogueGenerationBatchRequest request,
        CancellationToken deadlineToken,
        CancellationToken callerToken)
    {
        try
        {
            callerToken.ThrowIfCancellationRequested();
            IReadOnlyList<DailyDialogueCacheEntry> entries = await DailyGenerationCoordinator
                .ExecutePreparedBatchAsync(
                    dialogueGenerationGateway,
                    request,
                    deadlineToken)
                .ConfigureAwait(false);
            return new DailyGenerationBatchExecutionResult(
                IsSuccessful: true,
                request.RequestId,
                entries);
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // deadline、HTTP、JSON/contract 与 mapping 失败只丢弃本 batch。不得自动重试整批。
            return new DailyGenerationBatchExecutionResult(
                IsSuccessful: false,
                request.RequestId,
                Array.Empty<DailyDialogueCacheEntry>());
        }
    }

    /// <summary>
    /// 为一个计划批次复用同一 cohort 的标量快照，并替换为已复制的局部候选数组。
    /// </summary>
    private static DayStartedGenerationInput CreateBatchInput(
        DayStartedGenerationInput cohortInput,
        IReadOnlyList<DailyDialogueGenerationInput> batch)
    {
        return new DayStartedGenerationInput(
            cohortInput.SaveId,
            cohortInput.PlayerId,
            cohortInput.GameDayIndex,
            cohortInput.StableDayContext,
            batch.ToArray());
    }

    private static bool IsValidPersistedWatermark(EventOutboxWatermark? watermark)
    {
        return watermark is not null
            && ((watermark.MemoryRevision == 0 && watermark.CommittedThroughDayIndex == -1)
                || (watermark.MemoryRevision > 0 && watermark.CommittedThroughDayIndex >= 0));
    }

    private static DailyGenerationRunResult CreatePreBatchFallback()
    {
        return new DailyGenerationRunResult(
            DailyGenerationRunStatus.Fallback,
            CachedEntryCount: 0,
            BatchCount: 0,
            SuccessfulBatchCount: 0,
            FailedBatchCount: 0,
            RequestIds: Array.Empty<string>());
    }
}
