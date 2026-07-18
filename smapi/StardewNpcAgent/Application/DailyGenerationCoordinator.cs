using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StardewNpcAgent.Contracts;
using StardewNpcAgent.Game;
using StardewNpcAgent.Infrastructure.Storage;

namespace StardewNpcAgent.Application;

/// <summary>
/// 事件 outbox 完成一次刷新后已经持久化的分区水位。
/// </summary>
/// <param name="MemoryRevision">后端确认并已写入本地 outbox snapshot 的 memory revision。</param>
/// <param name="CommittedThroughDayIndex">同一 snapshot 中的已提交绝对游戏日；空分区为 -1。</param>
/// <remarks>
/// 端口实现必须先把后端响应应用到 durable outbox，再返回本 record。协调器只信任这个
/// 持久化后的值，不能从在途 HTTP 响应或刷新前内存值推测 required memory revision。
/// </remarks>
public sealed record EventOutboxWatermark(
    int MemoryRevision,
    int CommittedThroughDayIndex);

/// <summary>
/// DayStarted 应用层用于刷新截至指定日期事件的最小端口。
/// </summary>
/// <remarks>
/// Phase 4 只注入 scripted fake；真实 HTTP/outbox 编排属于 Phase 7。本接口不暴露
/// <see cref="DurableEventOutbox"/> 的 mutation 细节，调用方只消费成功持久化后的水位。
/// </remarks>
public interface IEventOutboxSynchronizer
{
    /// <summary>
    /// 刷新并持久化截至指定日的事件，然后返回持久化水位。
    /// </summary>
    /// <param name="throughDayIndex">包含式截止日；首日之前允许使用 -1。</param>
    /// <param name="cancellationToken">调用方取消信号，必须原样传播。</param>
    Task<EventOutboxWatermark> FlushThroughDayAsync(
        int throughDayIndex,
        CancellationToken cancellationToken);
}

/// <summary>
/// DayStarted 应用层提交每日批量生成请求的最小端口。
/// </summary>
/// <remarks>
/// 接口只收发稳定合同 DTO，不规定 HTTP、序列化、重试或后台线程。Phase 4 测试通过
/// scripted fake 验证顺序与失败语义，真实客户端留给 Phase 7。
/// </remarks>
public interface IDialogueGenerationGateway
{
    /// <summary>
    /// 提交一个已经通过本地 contract 校验的每日生成批次。
    /// </summary>
    /// <param name="request">按输入顺序构造的完整 v1 请求。</param>
    /// <param name="cancellationToken">调用方取消信号，必须原样传播。</param>
    Task<DialogueGenerationBatchResponse> GenerateBatchAsync(
        DialogueGenerationBatchRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// 单个 <see cref="DialogueCandidate"/> 进入每日生成请求时附带的稳定业务输入。
/// </summary>
/// <param name="Candidate">游戏侧已经解析并通过资格检查的原台词候选。</param>
/// <param name="RelationshipSnapshot">游戏侧权威关系快照；协调器只复制和传输。</param>
/// <param name="MemorySignals">轻量结构化记忆线索；数组顺序属于生成上下文。</param>
public sealed record DailyDialogueGenerationInput(
    DialogueCandidate Candidate,
    RelationshipSnapshot RelationshipSnapshot,
    IReadOnlyList<JsonElement> MemorySignals);

/// <summary>
/// 一次 DayStarted 每日生成运行的全部稳定输入。
/// </summary>
/// <param name="SaveId">稳定存档身份。</param>
/// <param name="PlayerId">稳定玩家身份。</param>
/// <param name="GameDayIndex">当前绝对游戏日。</param>
/// <param name="StableDayContext">当天生成期间不会改变的季节、天气、locale 与进度快照。</param>
/// <param name="Candidates">按目标 NPC 配置顺序排列的候选；协调器不得重排。</param>
public sealed record DayStartedGenerationInput(
    string SaveId,
    string PlayerId,
    int GameDayIndex,
    StableDayContext StableDayContext,
    IReadOnlyList<DailyDialogueGenerationInput> Candidates);

/// <summary>
/// 一次每日生成运行的应用层终态。
/// </summary>
public enum DailyGenerationRunStatus
{
    /// <summary>
    /// 事件刷新完成且至少一个 batch 通过；可能因全是 passthrough 而缓存零项。
    /// </summary>
    Completed,

    /// <summary>
    /// cohort 级准备失败，或所有 batch 均失败；游戏应完整使用原版路径。
    /// </summary>
    Fallback,
}

/// <summary>
/// DayStarted 协调器返回给游戏适配层的最小、无异常业务结果。
/// </summary>
/// <param name="Status">本次运行至少有一个合法 batch，还是在进入/完成所有 batch 前 fallback。</param>
/// <param name="CachedEntryCount">最终写入的 generated cache 数量。</param>
/// <param name="BatchCount">本 cohort 实际计划并等待的批次数。</param>
/// <param name="SuccessfulBatchCount">完整通过 request/response/mapping 的批次数。</param>
/// <param name="FailedBatchCount">普通失败或共同 deadline 超时的批次数。</param>
/// <param name="RequestIds">按计划顺序排列的已构造 request ID；构造前失败项没有 ID。</param>
public sealed record DailyGenerationRunResult(
    DailyGenerationRunStatus Status,
    int CachedEntryCount,
    int BatchCount,
    int SuccessfulBatchCount,
    int FailedBatchCount,
    IReadOnlyList<string> RequestIds);

/// <summary>
/// 保留既有调用面的每日生成 facade，并提供不修改全局 cache 的单批纯执行单元。
/// </summary>
/// <remarks>
/// 本类不读取 <c>Game1</c> 或其他全局对象，不订阅 SMAPI，不发送 HTTP，也不创建后台线程。
/// 所有游戏事实由 <see cref="DayStartedGenerationInput"/> 提供。新的正式 runtime 直接使用
/// <see cref="DailyGenerationBatchCoordinator"/>；本 facade 继续支持既有内部调用，并把一次 flush、
/// 分片和失败隔离委托给同一 production coordinator，避免维护两套语义。
/// </remarks>
public sealed class DailyGenerationCoordinator
{
    private const string RequestIdentityPrefix = "request-daily-generation-v1-";
    private const string TaskIdentityPrefix = "task-daily-generation-v1-";
    private readonly IEventOutboxSynchronizer eventOutboxSynchronizer;
    private readonly IDialogueGenerationGateway dialogueGenerationGateway;
    private readonly DailyDialogueCache cache;

    /// <summary>
    /// 创建只依赖应用端口和进程内 cache 的协调器。
    /// </summary>
    /// <param name="eventOutboxSynchronizer">先刷新昨日事件并返回持久化水位的端口。</param>
    /// <param name="dialogueGenerationGateway">提交稳定合同 DTO 的生成端口。</param>
    /// <param name="cache">本次运行独占更新的当日 cache。</param>
    public DailyGenerationCoordinator(
        IEventOutboxSynchronizer eventOutboxSynchronizer,
        IDialogueGenerationGateway dialogueGenerationGateway,
        DailyDialogueCache cache)
    {
        this.eventOutboxSynchronizer = eventOutboxSynchronizer
            ?? throw new ArgumentNullException(nameof(eventOutboxSynchronizer));
        this.dialogueGenerationGateway = dialogueGenerationGateway
            ?? throw new ArgumentNullException(nameof(dialogueGenerationGateway));
        this.cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    /// <summary>
    /// 严格按“清旧 cache → await 昨日事件刷新 → 构造请求 → 调用 gateway → 整批校验 → 写 cache”运行。
    /// </summary>
    /// <param name="input">游戏适配层已经准备好的稳定日输入与有序候选。</param>
    /// <param name="cancellationToken">调用方取消信号；调用方取消会抛出而不是伪装成 fallback。</param>
    /// <returns>
    /// 合法响应返回 Completed 并只缓存 generated；业务/基础设施失败返回 Fallback 且 cache 为空。
    /// </returns>
    public async Task<DailyGenerationRunResult> RunDayStartedAsync(
        DayStartedGenerationInput input,
        CancellationToken cancellationToken = default)
    {
        DailyGenerationBatchCoordinator coordinator = new(
            eventOutboxSynchronizer,
            dialogueGenerationGateway,
            cache,
            DailyGenerationBatchCoordinator.DefaultGenerationDeadline);
        return await coordinator.RunDayStartedAsync(input, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 使用刷新后的 revision 深拷贝全部稳定输入，并保持候选数组原顺序。
    /// </summary>
    internal static DialogueGenerationBatchRequest BuildRequest(
        DayStartedGenerationInput input,
        int requiredMemoryRevision)
    {
        StableDayContext stableDayContext = CloneStableDayContext(input.StableDayContext);
        List<DialogueGenerationItem> items = new(input.Candidates.Count);

        foreach (DailyDialogueGenerationInput generationInput in input.Candidates)
        {
            DialogueCandidate candidate = generationInput.Candidate;
            if (!string.Equals(candidate.Locale, stableDayContext.Locale, StringComparison.Ordinal))
            {
                throw new ArgumentException("候选 locale 必须与 stable_day_context.locale 一致。", nameof(input));
            }

            // Resolver 正常会提供该 hash；这里再次计算，防止手工应用输入让 cache identity
            // 绑定到与正文不一致的指纹。
            string computedSourceHash = SourceDialogueHasher.Compute(candidate.SourceText);
            if (!DialogueSourceClassifier.MatchesIdentity(
                    candidate.SourceFamily,
                    candidate.NpcId,
                    candidate.AssetName,
                    candidate.DialogueKey)
                || !string.Equals(candidate.SourceHash, computedSourceHash, StringComparison.Ordinal))
            {
                throw new ArgumentException("候选 source_hash 与 source text 不一致。", nameof(input));
            }

            RelationshipSnapshot relationshipSnapshot = CloneRelationshipSnapshot(
                generationInput.RelationshipSnapshot);
            List<JsonElement> memorySignals = generationInput.MemorySignals
                .Select(signal => signal.Clone())
                .ToList();
            DialogueGenerationItem item = new()
            {
                NpcId = candidate.NpcId,
                SourceDialogue = new SourceDialogue
                {
                    AssetName = candidate.AssetName,
                    DialogueKey = candidate.DialogueKey,
                    Text = candidate.SourceText,
                    SourceHash = candidate.SourceHash,
                },
                RelationshipSnapshot = relationshipSnapshot,
                StyleExamples = candidate.StyleExamples.ToList(),
                MemorySignals = memorySignals,
            };
            item.TaskId = CreateTaskId(
                input,
                stableDayContext,
                item,
                candidate.Locale,
                requiredMemoryRevision);
            items.Add(item);
        }

        DialogueGenerationBatchRequest request = new()
        {
            SchemaVersion = ContractVersions.V1,
            SaveId = input.SaveId,
            PlayerId = input.PlayerId,
            GameDayIndex = input.GameDayIndex,
            RequiredMemoryRevision = requiredMemoryRevision,
            StableDayContext = stableDayContext,
            Items = items,
        };
        request.RequestId = CreateRequestId(request);
        return request;
    }

    /// <summary>
    /// 在响应整批通过映射校验后，仅把 generated 项转换为正式 cache entry。
    /// </summary>
    private static DailyDialogueCacheEntry[] BuildGeneratedEntries(
        DialogueGenerationBatchRequest request,
        DialogueGenerationBatchResponse response)
    {
        List<DailyDialogueCacheEntry> entries = new();
        for (int index = 0; index < response.Items.Count; index++)
        {
            DialogueGenerationItemResult responseItem = response.Items[index];
            if (responseItem.Status != DialogueGenerationStatus.Generated)
            {
                continue;
            }

            // v1 wire 为兼容既有后端仍传字符串；进入游戏 cache 前必须由游戏侧独立
            // 解析一次。单个恶意或损坏 item 只让该 NPC 回退，其他已验证结果仍可使用。
            if (!DialogueTemplatePolicy.TryParse(
                    responseItem.Text,
                    out DialogueTextTemplate? generatedTemplate))
            {
                continue;
            }

            string rawGeneratedTemplate = DialogueTemplatePolicy.RenderGameTemplate(
                generatedTemplate!);

            DialogueGenerationItem requestItem = request.Items[index];
            DialogueSourceIdentity? sourceIdentity = DialogueSourceClassifier.ClassifyTranslationKey(
                $"{requestItem.SourceDialogue.AssetName}:{requestItem.SourceDialogue.DialogueKey}",
                requestItem.NpcId);
            if (sourceIdentity is null)
            {
                throw new InvalidOperationException("响应来源无法重新分类为受支持 daily source。");
            }

            DailyDialogueCacheKey cacheKey = new(
                request.GameDayIndex,
                request.StableDayContext.Locale,
                requestItem.NpcId,
                requestItem.SourceDialogue.AssetName,
                requestItem.SourceDialogue.DialogueKey);
            entries.Add(
                new DailyDialogueCacheEntry(
                    cacheKey,
                    sourceIdentity.Family,
                    requestItem.SourceDialogue.Text,
                    responseItem.SourceHash,
                    rawGeneratedTemplate,
                    responseItem.GenerationId,
                    responseItem.GenerationKey,
                    responseItem.TraceId));
        }

        return entries.ToArray();
    }

    /// <summary>
    /// 执行一个已经冻结且通过本地 request 构造的单批请求，不清 cache、不刷新 outbox。
    /// </summary>
    /// <param name="gateway">共享生成端口；调用方决定是否并发及 deadline。</param>
    /// <param name="request">只包含一个 1～8 项 batch 的稳定 v1 DTO。</param>
    /// <param name="cancellationToken">共同 deadline 或调用方取消信号。</param>
    /// <returns>完整响应通过 mapping 后生成的局部 entries；可能因全 passthrough 而为空。</returns>
    /// <exception cref="InvalidOperationException">request 或 response mapping 不合法。</exception>
    internal static async Task<IReadOnlyList<DailyDialogueCacheEntry>> ExecutePreparedBatchAsync(
        IDialogueGenerationGateway gateway,
        DialogueGenerationBatchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(request);
        ContractValidationResult requestValidation = ContractValidator.Validate(request);
        if (!requestValidation.IsValid || request.Items.Count is < 1 or > 8)
        {
            throw new InvalidOperationException("每日生成单批 request 不合法。");
        }

        DialogueGenerationBatchResponse response = await gateway
            .GenerateBatchAsync(request, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        ContractValidationResult responseMappingValidation = ContractValidator.Validate(
            request,
            response);
        if (!responseMappingValidation.IsValid)
        {
            throw new InvalidOperationException("每日生成单批 response mapping 不合法。");
        }

        return BuildGeneratedEntries(request, response);
    }

    /// <summary>
    /// 深拷贝稳定日 DTO，避免调用方在 await 期间或 fake gateway 内修改协调器的快照。
    /// </summary>
    private static StableDayContext CloneStableDayContext(StableDayContext source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new StableDayContext
        {
            Season = source.Season,
            Weather = source.Weather,
            Locale = source.Locale,
            ProgressionSignals = source.ProgressionSignals.Clone(),
            ExtensionData = CloneExtensionData(source.ExtensionData),
        };
    }

    /// <summary>
    /// 深拷贝关系 DTO，并保留未知字段让 <see cref="ContractValidator"/> fail closed。
    /// </summary>
    private static RelationshipSnapshot CloneRelationshipSnapshot(RelationshipSnapshot source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new RelationshipSnapshot
        {
            FriendshipPoints = source.FriendshipPoints,
            RelationshipStage = source.RelationshipStage,
            ExtensionData = CloneExtensionData(source.ExtensionData),
        };
    }

    /// <summary>
    /// 克隆 extension data 而不静默丢弃未知字段；后续 contract 校验负责拒绝。
    /// </summary>
    private static Dictionary<string, JsonElement>? CloneExtensionData(
        IReadOnlyDictionary<string, JsonElement>? extensionData)
    {
        return extensionData?.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Clone(),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// 为单 NPC 任务计算稳定 identity；所有生成相关输入都进入长度前缀 SHA-256。
    /// </summary>
    private static string CreateTaskId(
        DayStartedGenerationInput input,
        StableDayContext stableDayContext,
        DialogueGenerationItem item,
        string candidateLocale,
        int requiredMemoryRevision)
    {
        List<string> components = new()
        {
            ContractVersions.V1,
            input.SaveId,
            input.PlayerId,
            input.GameDayIndex.ToString(CultureInfo.InvariantCulture),
            requiredMemoryRevision.ToString(CultureInfo.InvariantCulture),
            stableDayContext.Season,
            stableDayContext.Weather,
            stableDayContext.Locale,
            CanonicalizeJson(stableDayContext.ProgressionSignals),
            item.NpcId,
            candidateLocale,
            item.SourceDialogue.AssetName,
            item.SourceDialogue.DialogueKey,
            item.SourceDialogue.Text,
            item.SourceDialogue.SourceHash,
            item.RelationshipSnapshot.FriendshipPoints.ToString(CultureInfo.InvariantCulture),
            item.RelationshipSnapshot.RelationshipStage,
            "style_examples",
            item.StyleExamples.Count.ToString(CultureInfo.InvariantCulture),
        };
        components.AddRange(item.StyleExamples);
        components.Add("memory_signals");
        components.Add(item.MemorySignals.Count.ToString(CultureInfo.InvariantCulture));
        components.AddRange(item.MemorySignals.Select(CanonicalizeJson));
        return ComputeStableIdentifier(TaskIdentityPrefix, components);
    }

    /// <summary>
    /// 为完整有序 batch 计算稳定 request identity；item 顺序变化会有意改变 identity。
    /// </summary>
    private static string CreateRequestId(DialogueGenerationBatchRequest request)
    {
        List<string> components = new()
        {
            request.SchemaVersion,
            request.SaveId,
            request.PlayerId,
            request.GameDayIndex.ToString(CultureInfo.InvariantCulture),
            request.RequiredMemoryRevision.ToString(CultureInfo.InvariantCulture),
            request.StableDayContext.Season,
            request.StableDayContext.Weather,
            request.StableDayContext.Locale,
            CanonicalizeJson(request.StableDayContext.ProgressionSignals),
            "ordered_task_ids",
            request.Items.Count.ToString(CultureInfo.InvariantCulture),
        };
        components.AddRange(request.Items.Select(item => item.TaskId));
        return ComputeStableIdentifier(RequestIdentityPrefix, components);
    }

    /// <summary>
    /// 使用字符长度前缀消除组件边界歧义，再以 UTF-8 SHA-256 派生可重试 identity。
    /// </summary>
    private static string ComputeStableIdentifier(
        string prefix,
        IEnumerable<string> components)
    {
        StringBuilder canonicalInput = new();
        foreach (string component in components)
        {
            ArgumentNullException.ThrowIfNull(component);
            canonicalInput
                .Append(component.Length.ToString(CultureInfo.InvariantCulture))
                .Append(':')
                .Append(component);
        }

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalInput.ToString()));
        return prefix + Convert.ToHexString(digest).ToLowerInvariant();
    }

    /// <summary>
    /// 把 JsonElement 规范化为 object key 递归排序、array 顺序保留的紧凑 JSON。
    /// </summary>
    /// <remarks>
    /// progression/memory signal 是可扩展 object；直接使用 raw JSON 会让同一语义因属性顺序
    /// 改变 request/task identity。本实现只用于本协调器的稳定 ID，不扩张成通用 workflow SDK。
    /// </remarks>
    private static string CanonicalizeJson(JsonElement value)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            WriteCanonicalJson(writer, value);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// 递归写入 deterministic JSON；数值 token 原样保留，避免无谓的精度或表示改写。
    /// </summary>
    private static void WriteCanonicalJson(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in value.EnumerateObject()
                             .OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(writer, property.Value);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in value.EnumerateArray())
                {
                    WriteCanonicalJson(writer, item);
                }

                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;

            case JsonValueKind.Number:
                writer.WriteRawValue(value.GetRawText(), skipInputValidation: false);
                break;

            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;

            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;

            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;

            default:
                throw new ArgumentException("稳定 JSON 输入不能是 Undefined。", nameof(value));
        }
    }
}
