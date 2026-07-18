using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using StardewNpcAgent.Contracts;
using StardewNpcAgent.Game;
using StardewNpcAgent.Infrastructure.Storage;

namespace StardewNpcAgent.Application;

/// <summary>
/// 玩家点击普通对话时，应用层对当前文本应走原版还是正式 generated cache 的决策。
/// </summary>
public enum DialogueDisplayDecisionKind
{
    /// <summary>
    /// cache、复合身份、source hash 或正式生成元数据任一不满足，调用方必须完整执行原版路径。
    /// </summary>
    UseOriginal,

    /// <summary>
    /// 当前逐字符原文与正式 generated cache 完全一致，调用方可以显示增强文本。
    /// </summary>
    UseGenerated,
}

/// <summary>
/// 点击路径解析一次展示决策所需的全部当前事实。
/// </summary>
/// <param name="GameDayIndex">当前绝对游戏日；它是 cache 身份的一部分，禁止跨日命中。</param>
/// <param name="Locale">当前 SMAPI locale；必须与预生成时逐字符一致。</param>
/// <param name="NpcId">稳定 NPC 内部 ID，而不是本地化显示名称。</param>
/// <param name="AssetName">当前对话来源的规范化精确资产名。</param>
/// <param name="DialogueKey">当前来源资产中的精确字段 key。</param>
/// <param name="CurrentSourceText">点击时重新读取的逐字符原版正文；不得预先 Trim 或规范化。</param>
/// <remarks>
/// 本输入不包含网关、模型、数据库、游戏全局对象、点击坐标或 UI 引用。游戏适配层负责在
/// 调用前读取这些稳定事实，协调器只执行纯本地判定。
/// </remarks>
public sealed record DialogueDisplayContext(
    int GameDayIndex,
    string Locale,
    string NpcId,
    string AssetName,
    string DialogueKey,
    string CurrentSourceText);

/// <summary>
/// 原生 Dialogue UI 完成展示后，由调用方显式提供的实际显示事实。
/// </summary>
/// <param name="WasActuallyDisplayed">
/// 只有原生 UI 已经成功接收并展示增强文本时才允许为 true；“已经选中但尚未显示”必须为 false。
/// </param>
/// <param name="DisplayedDayIndex">实际显示发生的绝对游戏日。</param>
/// <param name="NpcId">实际显示对象的稳定 NPC 内部 ID。</param>
/// <param name="SourceHash">实际显示时仍对应的原版正文逐字符 hash。</param>
public sealed record DisplayedDialogueConfirmation(
    bool WasActuallyDisplayed,
    int DisplayedDayIndex,
    string NpcId,
    string SourceHash);

/// <summary>
/// 本地 displayed ACK 入队结果。
/// </summary>
/// <param name="Status">
/// <see cref="DisplayAckStatus.Accepted"/> 表示首次持久化；
/// <see cref="DisplayAckStatus.Duplicate"/> 表示同一确定性展示事实已存在且本次为零写 no-op。
/// </param>
/// <param name="DisplayReceiptId">由展示事实确定性派生的幂等 receipt。</param>
/// <param name="RequestId">只从 <paramref name="DisplayReceiptId"/> 再派生的请求身份。</param>
public sealed record DisplayAckEnqueueResult(
    DisplayAckStatus Status,
    string DisplayReceiptId,
    string RequestId);

/// <summary>
/// 一次不可变的点击展示决策，同时充当后续 <c>RecordDisplayed</c> 所需的 opaque token。
/// </summary>
/// <remarks>
/// 本类型没有公开构造器或可写属性。每个实例都保存创建它的 coordinator 私有 owner marker，
/// 所以调用方不能手工拼出、复制或跨 coordinator 重放授权 token。公开表面只暴露“是否使用
/// generated”及其文本；generation、day、NPC、source hash 等 ACK 身份保持为内部只读快照。
/// </remarks>
public sealed class DialogueDisplayDecision
{
    private readonly object ownerMarker;

    /// <summary>
    /// 创建一个已绑定 coordinator 实例的不可变决策。
    /// </summary>
    /// <param name="ownerMarker">协调器私有对象身份；只用引用相等判断，不进入 receipt。</param>
    /// <param name="kind">原版或 generated 终态。</param>
    /// <param name="enhancedText">generated 时的增强文本；原版时为 null。</param>
    /// <param name="generationId">generated 时的后端生成身份。</param>
    /// <param name="cacheKey">generated 时命中的完整 cache 身份快照。</param>
    /// <param name="sourceFamily">generated 时已复核的 source family。</param>
    /// <param name="sourceText">generated 时已复核的逐字符原文。</param>
    /// <param name="sourceHash">generated 时已经复核过的当前原文 hash。</param>
    private DialogueDisplayDecision(
        object ownerMarker,
        DialogueDisplayDecisionKind kind,
        string? enhancedText,
        string? generationId,
        DailyDialogueCacheKey? cacheKey,
        DialogueSourceFamily? sourceFamily,
        string? sourceText,
        string? sourceHash)
    {
        this.ownerMarker = ownerMarker;
        Kind = kind;
        EnhancedText = enhancedText;
        GenerationId = generationId;
        CacheKey = cacheKey;
        SourceFamily = sourceFamily;
        SourceText = sourceText;
        SourceHash = sourceHash;
    }

    /// <summary>
    /// 当前点击应完整走原版还是允许显示 generated 文本。
    /// </summary>
    public DialogueDisplayDecisionKind Kind { get; }

    /// <summary>
    /// 只有 <see cref="DialogueDisplayDecisionKind.UseGenerated"/> 时非 null。
    /// </summary>
    public string? EnhancedText { get; }

    /// <summary>
    /// 后续 ACK 的 generation 路径身份；只允许同文件协调器读取。
    /// </summary>
    internal string? GenerationId { get; }

    /// <summary>
    /// Resolve 命中的完整 cache key 快照；RecordDisplayed 只使用其中冻结的 day 与 NPC。
    /// </summary>
    internal DailyDialogueCacheKey? CacheKey { get; }

    /// <summary>Resolve 时已独立核对的 source family。</summary>
    internal DialogueSourceFamily? SourceFamily { get; }

    /// <summary>Resolve 时逐字符核对的 cache raw source。</summary>
    internal string? SourceText { get; }

    /// <summary>
    /// Resolve 已经逐字符复核过的当前原文 hash。
    /// </summary>
    internal string? SourceHash { get; }

    /// <summary>
    /// 创建一个不能写 ACK 的原版决策。
    /// </summary>
    internal static DialogueDisplayDecision CreateOriginal(object ownerMarker)
    {
        ArgumentNullException.ThrowIfNull(ownerMarker);
        return new DialogueDisplayDecision(
            ownerMarker,
            DialogueDisplayDecisionKind.UseOriginal,
            enhancedText: null,
            generationId: null,
            cacheKey: null,
            sourceFamily: null,
            sourceText: null,
            sourceHash: null);
    }

    /// <summary>
    /// 从已经完成全部展示 gate 的正式 cache 项创建 generated 决策快照。
    /// </summary>
    internal static DialogueDisplayDecision CreateGenerated(
        object ownerMarker,
        DailyDialogueCacheEntry entry,
        string verifiedSourceHash)
    {
        ArgumentNullException.ThrowIfNull(ownerMarker);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(verifiedSourceHash);
        return new DialogueDisplayDecision(
            ownerMarker,
            DialogueDisplayDecisionKind.UseGenerated,
            entry.EnhancedText,
            entry.GenerationId,
            entry.Key,
            entry.SourceFamily,
            entry.SourceText,
            verifiedSourceHash);
    }

    /// <summary>
    /// 使用对象引用核对 token 是否确由当前 coordinator 实例签发。
    /// </summary>
    internal bool IsOwnedBy(object expectedOwnerMarker)
    {
        return ReferenceEquals(ownerMarker, expectedOwnerMarker);
    }
}

/// <summary>
/// 协调玩家点击时的本地 cache 决策，以及实际展示后的 durable ACK 入队。
/// </summary>
/// <remarks>
/// 本类只有两条同步、有限路径：<see cref="Resolve"/> 只读 <see cref="DailyDialogueCache"/> 并
/// 使用 <see cref="SourceDialogueHasher"/> 复核当前原文；<see cref="RecordDisplayed"/> 只构造
/// 现有 <see cref="DisplayAckRequest"/> 并调用 <see cref="DurableDisplayAckOutbox.Enqueue"/>。
/// 它不读取游戏全局对象、不显示 UI、不访问网关/网络/数据库，也不等待模型或启动后台线程。
/// </remarks>
public sealed class DialogueDisplayCoordinator
{
    private const string DisplayReceiptIdentityPrefix = "display-receipt-v1-";
    private const string RequestIdentityPrefix = "request-display-ack-v1-";
    private readonly object ownerMarker = new();
    private readonly string saveId;
    private readonly string playerId;
    private readonly DailyDialogueCache cache;
    private readonly DurableDisplayAckOutbox displayAckOutbox;

    /// <summary>
    /// 创建绑定单一 save/player 分区的点击展示协调器。
    /// </summary>
    /// <param name="saveId">稳定存档身份；写入每个 ACK request。</param>
    /// <param name="playerId">稳定玩家身份；写入每个 ACK request。</param>
    /// <param name="cache">DayStarted 协调器已经写好的进程内当日 cache。</param>
    /// <param name="displayAckOutbox">同一 save/player 分区的 durable ACK outbox。</param>
    public DialogueDisplayCoordinator(
        string saveId,
        string playerId,
        DailyDialogueCache cache,
        DurableDisplayAckOutbox displayAckOutbox)
    {
        ValidateStableString(saveId, nameof(saveId));
        ValidateStableString(playerId, nameof(playerId));
        this.saveId = saveId;
        this.playerId = playerId;
        this.cache = cache ?? throw new ArgumentNullException(nameof(cache));
        this.displayAckOutbox = displayAckOutbox
            ?? throw new ArgumentNullException(nameof(displayAckOutbox));
    }

    /// <summary>
    /// 使用完整复合 key 与当前逐字符原文决定是否允许展示正式 generated cache。
    /// </summary>
    /// <param name="context">点击时由游戏适配层显式提供的全部当前事实。</param>
    /// <returns>
    /// 任一输入、cache、hash、metadata 或增强文本不合法时返回 UseOriginal；只有全部 gate
    /// 通过时返回包含增强文本的不可变 UseGenerated 决策。
    /// </returns>
    public DialogueDisplayDecision Resolve(DialogueDisplayContext context)
    {
        if (!IsValidResolveContext(context))
        {
            return DialogueDisplayDecision.CreateOriginal(ownerMarker);
        }

        DailyDialogueCacheKey cacheKey = new(
            context.GameDayIndex,
            context.Locale,
            context.NpcId,
            context.AssetName,
            context.DialogueKey);
        if (!cache.TryGet(cacheKey, out DailyDialogueCacheEntry? entry) || entry is null)
        {
            return DialogueDisplayDecision.CreateOriginal(ownerMarker);
        }

        // 这里必须重新计算点击时当前正文的 hash，不能信任输入方附带旧 hash，也不能对正文
        // Trim、统一换行或做 Unicode normalization。任何一个字符变化都会回退原版。
        string currentSourceHash = SourceDialogueHasher.Compute(context.CurrentSourceText);
        if (!DialogueSourceClassifier.MatchesIdentity(
                entry.SourceFamily,
                entry.Key.NpcId,
                entry.Key.AssetName,
                entry.Key.DialogueKey)
            || !string.Equals(entry.SourceText, context.CurrentSourceText, StringComparison.Ordinal)
            || !string.Equals(entry.SourceHash, currentSourceHash, StringComparison.Ordinal)
            || !HasValidFormalGenerationMetadata(entry)
            || !IsStableString(entry.EnhancedText)
            // cache 是内部信任边界，可能被测试 fake 或未来适配器构造。这里再次解析
            // raw template，保证第二个 @ 或其他 DSL 永远拿不到 opaque display token。
            || !DialogueTemplatePolicy.TryParse(entry.EnhancedText, out _))
        {
            return DialogueDisplayDecision.CreateOriginal(ownerMarker);
        }

        return DialogueDisplayDecision.CreateGenerated(ownerMarker, entry, currentSourceHash);
    }

    /// <summary>
    /// 在调用方确认增强文本已经由原生 UI 实际显示后，写入确定性 displayed ACK。
    /// </summary>
    /// <param name="decision">必须是当前 coordinator 的 <see cref="Resolve"/> 返回的 generated 决策。</param>
    /// <param name="confirmation">实际显示的 day、NPC 与 source hash 事实。</param>
    /// <returns>本次 receipt 是首次入队 Accepted，还是已存在的 Duplicate。</returns>
    /// <exception cref="InvalidOperationException">
    /// 决策为原版、调用方声明尚未实际显示，或内部构造的 request 不满足共享合同时抛出。
    /// </exception>
    /// <exception cref="ArgumentException">
    /// 决策来自其他 coordinator，或实际显示身份与决策快照不一致时抛出。
    /// </exception>
    /// <remarks>
    /// outbox 持久化异常故意原样传播给上层日志边界。方法从不修改 decision 或 cache，因此
    /// ACK 写失败不会改变已经选定/已经显示的文本，也不会把异常正文变成 NPC 台词。
    /// </remarks>
    public DisplayAckEnqueueResult RecordDisplayed(
        DialogueDisplayDecision decision,
        DisplayedDialogueConfirmation confirmation)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(confirmation);

        if (!decision.IsOwnedBy(ownerMarker))
        {
            throw new ArgumentException(
                "展示决策不是由当前 DialogueDisplayCoordinator 实例签发。",
                nameof(decision));
        }

        if (decision.Kind != DialogueDisplayDecisionKind.UseGenerated
            || decision.GenerationId is null
            || decision.CacheKey is null
            || decision.SourceHash is null
            || decision.EnhancedText is null)
        {
            throw new InvalidOperationException("只有合法 generated 展示决策可以记录 displayed ACK。");
        }

        if (!confirmation.WasActuallyDisplayed)
        {
            throw new InvalidOperationException("增强文本尚未实际显示，不能记录 displayed ACK。");
        }

        ValidateDisplayedIdentity(decision, confirmation);
        string displayReceiptId = CreateDisplayReceiptId(
            decision.GenerationId,
            confirmation.DisplayedDayIndex,
            confirmation.NpcId,
            confirmation.SourceHash);
        string requestId = CreateRequestId(displayReceiptId);
        DisplayAckRequest request = new()
        {
            SchemaVersion = ContractVersions.V1,
            RequestId = requestId,
            SaveId = saveId,
            PlayerId = playerId,
            DisplayReceiptId = displayReceiptId,
            DisplayedDayIndex = confirmation.DisplayedDayIndex,
            NpcId = confirmation.NpcId,
            SourceHash = confirmation.SourceHash,
        };
        ContractValidationResult validation = ContractValidator.Validate(request);
        if (!validation.IsValid)
        {
            // 输入已先与不可变 token 精确核对，正常路径不应触发；保留这一层是为了让共享
            // wire contract 继续作为 ACK request 的最终真值，而不是在协调器复制合同规则。
            throw new InvalidOperationException("内部构造的 DisplayAckRequest 不满足共享合同。");
        }

        // Enqueue 在 outbox 自己的锁内原子返回 Accepted/Duplicate。不能用调用前后
        // PendingCount 推断：Phase 7 后台 flusher 可能在两次计数之间删除 FIFO 首项。
        DisplayAckStatus status = displayAckOutbox.Enqueue(decision.GenerationId, request);
        return new DisplayAckEnqueueResult(status, displayReceiptId, requestId);
    }

    /// <summary>
    /// 点击输入不合法时 fail closed。身份字段沿用合同的“非空、无首尾空白”规则；当前
    /// source 正文也必须是正式生成请求可接受的稳定非空文本。
    /// </summary>
    private static bool IsValidResolveContext(DialogueDisplayContext? context)
    {
        return context is not null
            && context.GameDayIndex >= 0
            && IsStableString(context.Locale)
            && IsStableString(context.NpcId)
            && IsStableString(context.AssetName)
            && IsStableString(context.DialogueKey)
            && IsStableString(context.CurrentSourceText);
    }

    /// <summary>
    /// 正式 generated cache 必须保留 DayStarted 映射阶段验证过的全部 generation 身份。
    /// 静态 Spike 的兼容三参数 entry 没有这些字段，所以永远只能走原版 ACK 边界。
    /// </summary>
    private static bool HasValidFormalGenerationMetadata(DailyDialogueCacheEntry entry)
    {
        return entry.HasCompleteGenerationMetadata
            && IsStableString(entry.GenerationId)
            && IsStableString(entry.GenerationKey)
            && IsStableString(entry.TraceId);
    }

    /// <summary>
    /// 复核实际显示事实与 Resolve 决策快照完全一致，不接受跨日、跨 NPC 或换源确认。
    /// </summary>
    private static void ValidateDisplayedIdentity(
        DialogueDisplayDecision decision,
        DisplayedDialogueConfirmation confirmation)
    {
        if (confirmation.DisplayedDayIndex < 0
            || !IsStableString(confirmation.NpcId)
            || !IsStableString(confirmation.SourceHash)
            || confirmation.DisplayedDayIndex != decision.CacheKey!.GameDayIndex
            || !string.Equals(
                confirmation.NpcId,
                decision.CacheKey.NpcId,
                StringComparison.Ordinal)
            || !string.Equals(
                confirmation.SourceHash,
                decision.SourceHash,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "实际显示的 day、NPC 或 source hash 与展示决策不一致。",
                nameof(confirmation));
        }
    }

    /// <summary>
    /// receipt 只绑定 generation ID、实际显示日、稳定 NPC ID 与 source hash。
    /// </summary>
    private static string CreateDisplayReceiptId(
        string generationId,
        int displayedDayIndex,
        string npcId,
        string sourceHash)
    {
        return ComputeLengthPrefixedIdentifier(
            DisplayReceiptIdentityPrefix,
            new[]
            {
                generationId,
                displayedDayIndex.ToString(CultureInfo.InvariantCulture),
                npcId,
                sourceHash,
            });
    }

    /// <summary>
    /// request ID 只从已经确定的 receipt UTF-8 bytes 再做一次 SHA-256，不混入时钟或随机数。
    /// </summary>
    private static string CreateRequestId(string displayReceiptId)
    {
        byte[] receiptBytes = Encoding.UTF8.GetBytes(displayReceiptId);
        byte[] digest = SHA256.HashData(receiptBytes);
        return RequestIdentityPrefix + Convert.ToHexString(digest).ToLowerInvariant();
    }

    /// <summary>
    /// 对每个组件写入“UTF-8 byte length + ':' + UTF-8 bytes”，再对完整 canonical bytes
    /// 计算 SHA-256。字节长度而不是 UTF-16 字符数可避免未来 Unicode identity 的边界歧义。
    /// </summary>
    private static string ComputeLengthPrefixedIdentifier(
        string outputPrefix,
        IEnumerable<string> components)
    {
        using MemoryStream canonicalBytes = new();
        foreach (string component in components)
        {
            ArgumentNullException.ThrowIfNull(component);
            byte[] componentBytes = Encoding.UTF8.GetBytes(component);
            byte[] lengthBytes = Encoding.ASCII.GetBytes(
                componentBytes.Length.ToString(CultureInfo.InvariantCulture));
            canonicalBytes.Write(lengthBytes, 0, lengthBytes.Length);
            canonicalBytes.WriteByte((byte)':');
            canonicalBytes.Write(componentBytes, 0, componentBytes.Length);
        }

        byte[] digest = SHA256.HashData(canonicalBytes.ToArray());
        return outputPrefix + Convert.ToHexString(digest).ToLowerInvariant();
    }

    /// <summary>
    /// 判断 identity/text 是否非空且没有首尾空白；不自动 Trim，内部空白和换行保持原样。
    /// </summary>
    private static bool IsStableString(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && string.Equals(value, value.Trim(), StringComparison.Ordinal);
    }

    /// <summary>
    /// 对构造函数公开分区参数给出可定位异常；运行期 cache 内容则通过 Resolve 安全回退。
    /// </summary>
    private static void ValidateStableString(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (!IsStableString(value))
        {
            throw new ArgumentException("值必须非空且不能包含首尾空白。", parameterName);
        }
    }
}
