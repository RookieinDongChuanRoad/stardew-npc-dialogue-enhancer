using StardewNpcAgent.Contracts;
using StardewNpcAgent.Application;
using StardewNpcAgent.Game;
using StardewNpcAgent.Infrastructure.Storage;

namespace StardewNpcAgent.Tests.Application;

/// <summary>
/// 验证点击路径只做本地 cache 决策，并且只有实际显示后的显式确认才能写入 ACK outbox。
/// </summary>
/// <remarks>
/// 本组测试只使用真实内存 cache 与测试临时文件 outbox；不构造生成网关、不启动 HTTP、
/// SMAPI、游戏进程或后台任务，从依赖层面冻结“玩家点击不等待模型”的应用边界。
/// </remarks>
public sealed class DialogueDisplayCoordinatorTests : IDisposable
{
    private const string SaveId = "save-standard-farm-001";
    private const string PlayerId = "player-farmer-001";
    private readonly string testDirectory;

    /// <summary>
    /// 为每个测试实例创建唯一临时目录，避免并行测试共享 displayed ACK snapshot。
    /// </summary>
    public DialogueDisplayCoordinatorTests()
    {
        testDirectory = Path.Combine(
            Path.GetTempPath(),
            "StardewNpcAgent.Tests",
            $"DialogueDisplayCoordinator.{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
    }

    /// <summary>
    /// cache miss 是正常原版路径；Resolve 不能为了补结果访问网络，也不能提前写展示 ACK。
    /// </summary>
    [Fact]
    public void Resolve_WhenCacheMissesUsesOriginalWithoutEnqueuingAck()
    {
        string outboxPath = Path.Combine(testDirectory, "display-acks.json");
        DailyDialogueCache cache = new();
        DurableDisplayAckOutbox outbox = DurableDisplayAckOutbox.Open(
            outboxPath,
            SaveId,
            PlayerId);
        DialogueDisplayCoordinator coordinator = new(SaveId, PlayerId, cache, outbox);
        DialogueDisplayContext context = new(
            GameDayIndex: 14,
            Locale: "zh-CN",
            NpcId: "Abigail",
            AssetName: "Characters/Dialogue/Abigail",
            DialogueKey: "spring_Mon",
            CurrentSourceText: "原版台词-Abigail");

        DialogueDisplayDecision decision = coordinator.Resolve(context);

        Assert.Equal(DialogueDisplayDecisionKind.UseOriginal, decision.Kind);
        Assert.Null(decision.EnhancedText);
        Assert.Equal(0, outbox.PendingCount);
        Assert.False(File.Exists(outboxPath));
    }

    /// <summary>
    /// cache 身份必须同时绑定日期、locale、NPC、资产和 dialogue key；任一维度不同都不能
    /// 猜测“足够接近”而命中旧结果。
    /// </summary>
    [Theory]
    [InlineData(CacheKeyMutation.GameDayIndex)]
    [InlineData(CacheKeyMutation.Locale)]
    [InlineData(CacheKeyMutation.NpcId)]
    [InlineData(CacheKeyMutation.AssetName)]
    [InlineData(CacheKeyMutation.DialogueKey)]
    public void Resolve_WhenAnyCompositeCacheKeyDimensionDiffersUsesOriginal(
        CacheKeyMutation mutation)
    {
        string outboxPath = Path.Combine(testDirectory, $"display-acks-{mutation}.json");
        DialogueDisplayContext context = CreateDisplayContext();
        DailyDialogueCacheEntry entry = CreateGeneratedCacheEntry(context);
        DailyDialogueCacheKey mismatchedKey = mutation switch
        {
            CacheKeyMutation.GameDayIndex => entry.Key with
            {
                GameDayIndex = entry.Key.GameDayIndex + 1,
            },
            CacheKeyMutation.Locale => entry.Key with { Locale = "en" },
            CacheKeyMutation.NpcId => entry.Key with { NpcId = "Sebastian" },
            CacheKeyMutation.AssetName => entry.Key with
            {
                AssetName = "Characters/Dialogue/Abigail_Rainy",
            },
            CacheKeyMutation.DialogueKey => entry.Key with { DialogueKey = "spring_Tue" },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null),
        };
        DailyDialogueCache cache = new();
        cache.Store(entry with { Key = mismatchedKey });
        DurableDisplayAckOutbox outbox = OpenOutbox(outboxPath);
        DialogueDisplayCoordinator coordinator = new(SaveId, PlayerId, cache, outbox);

        DialogueDisplayDecision decision = coordinator.Resolve(context);

        Assert.Equal(DialogueDisplayDecisionKind.UseOriginal, decision.Kind);
        Assert.Null(decision.EnhancedText);
        Assert.Equal(0, outbox.PendingCount);
        Assert.False(File.Exists(outboxPath));
    }

    /// <summary>
    /// 精确 key 仍不足以授权展示：当前逐字符原文、三个 generation metadata 和增强文本
    /// 都必须保持正式 generated cache 的合法形状；静态 Spike/损坏项统一回退原版。
    /// </summary>
    [Theory]
    [InlineData(InvalidGeneratedCacheMutation.SourceFamilyMismatch)]
    [InlineData(InvalidGeneratedCacheMutation.SourceTextMismatch)]
    [InlineData(InvalidGeneratedCacheMutation.SourceHashMismatch)]
    [InlineData(InvalidGeneratedCacheMutation.GenerationIdMissing)]
    [InlineData(InvalidGeneratedCacheMutation.GenerationKeyMissing)]
    [InlineData(InvalidGeneratedCacheMutation.TraceIdMissing)]
    [InlineData(InvalidGeneratedCacheMutation.GenerationIdHasEdgeWhitespace)]
    [InlineData(InvalidGeneratedCacheMutation.EnhancedTextNull)]
    [InlineData(InvalidGeneratedCacheMutation.EnhancedTextBlank)]
    [InlineData(InvalidGeneratedCacheMutation.EnhancedTextHasEdgeWhitespace)]
    [InlineData(InvalidGeneratedCacheMutation.EnhancedTextHasSecondPlayerNameSlot)]
    [InlineData(InvalidGeneratedCacheMutation.EnhancedTextHasOtherDsl)]
    public void Resolve_WhenSourceOrFormalGeneratedFieldsAreInvalidUsesOriginal(
        InvalidGeneratedCacheMutation mutation)
    {
        string outboxPath = Path.Combine(testDirectory, $"invalid-{mutation}.json");
        DialogueDisplayContext context = CreateDisplayContext();
        DailyDialogueCacheEntry validEntry = CreateGeneratedCacheEntry(context);
        DailyDialogueCacheEntry invalidEntry = mutation switch
        {
            InvalidGeneratedCacheMutation.SourceFamilyMismatch => validEntry with
            {
                SourceFamily = DialogueSourceFamily.RainyDaily,
            },
            InvalidGeneratedCacheMutation.SourceTextMismatch => validEntry with
            {
                SourceText = context.CurrentSourceText + "!",
            },
            InvalidGeneratedCacheMutation.SourceHashMismatch => validEntry with
            {
                SourceHash = SourceDialogueHasher.Compute(context.CurrentSourceText + "!"),
            },
            InvalidGeneratedCacheMutation.GenerationIdMissing => validEntry with
            {
                GenerationId = null,
            },
            InvalidGeneratedCacheMutation.GenerationKeyMissing => validEntry with
            {
                GenerationKey = " ",
            },
            InvalidGeneratedCacheMutation.TraceIdMissing => validEntry with { TraceId = null },
            InvalidGeneratedCacheMutation.GenerationIdHasEdgeWhitespace => validEntry with
            {
                GenerationId = $" {validEntry.GenerationId}",
            },
            InvalidGeneratedCacheMutation.EnhancedTextNull => validEntry with
            {
                EnhancedText = null!,
            },
            InvalidGeneratedCacheMutation.EnhancedTextBlank => validEntry with
            {
                EnhancedText = "\t",
            },
            InvalidGeneratedCacheMutation.EnhancedTextHasEdgeWhitespace => validEntry with
            {
                EnhancedText = $"{validEntry.EnhancedText} ",
            },
            InvalidGeneratedCacheMutation.EnhancedTextHasSecondPlayerNameSlot => validEntry with
            {
                EnhancedText = "你好，@@。",
            },
            InvalidGeneratedCacheMutation.EnhancedTextHasOtherDsl => validEntry with
            {
                EnhancedText = "你好，%endearment。",
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null),
        };
        DailyDialogueCache cache = new();
        cache.Store(invalidEntry);
        DurableDisplayAckOutbox outbox = OpenOutbox(outboxPath);
        DialogueDisplayCoordinator coordinator = new(SaveId, PlayerId, cache, outbox);

        DialogueDisplayDecision decision = coordinator.Resolve(context);

        Assert.Equal(DialogueDisplayDecisionKind.UseOriginal, decision.Kind);
        Assert.Null(decision.EnhancedText);
        Assert.Equal(0, outbox.PendingCount);
        Assert.False(File.Exists(outboxPath));
    }

    /// <summary>
    /// 合法正式 generated cache 返回不可变增强决策；Resolve 本身不代表“已经显示”，因此
    /// 即使调用方读取了增强文本，也不能创建 ACK snapshot。
    /// </summary>
    [Fact]
    public void Resolve_WhenFormalGeneratedCacheMatchesReturnsEnhancedDecisionWithoutAck()
    {
        string outboxPath = Path.Combine(testDirectory, "resolve-generated.json");
        DialogueDisplayContext context = CreateDisplayContext();
        DailyDialogueCache cache = new();
        DailyDialogueCacheEntry entry = CreateGeneratedCacheEntry(context);
        cache.Store(entry);
        DurableDisplayAckOutbox outbox = OpenOutbox(outboxPath);
        DialogueDisplayCoordinator coordinator = new(SaveId, PlayerId, cache, outbox);

        DialogueDisplayDecision decision = coordinator.Resolve(context);

        Assert.Equal(DialogueDisplayDecisionKind.UseGenerated, decision.Kind);
        Assert.Equal(entry.EnhancedText, decision.EnhancedText);
        Assert.Empty(typeof(DialogueDisplayDecision).GetConstructors());
        Assert.Equal(0, outbox.PendingCount);
        Assert.False(File.Exists(outboxPath));
    }

    /// <summary>
    /// 调用方明确确认增强文本已经实际显示后，协调器才创建合同合法的 ACK。receipt 使用
    /// 独立预计算向量冻结“generation/day/NPC/source hash 的 UTF-8 字节长度前缀 + SHA-256”；
    /// request ID 只从 receipt 再派生，测试不复用生产 helper，避免同错同过。
    /// </summary>
    [Fact]
    public void RecordDisplayed_AfterExplicitConfirmationEnqueuesDeterministicContractRequest()
    {
        string outboxPath = Path.Combine(testDirectory, "record-displayed.json");
        DialogueDisplayContext context = CreateDisplayContext();
        DailyDialogueCache cache = new();
        DailyDialogueCacheEntry entry = CreateGeneratedCacheEntry(context);
        cache.Store(entry);
        DurableDisplayAckOutbox outbox = OpenOutbox(outboxPath);
        DialogueDisplayCoordinator coordinator = new(SaveId, PlayerId, cache, outbox);
        DialogueDisplayDecision decision = coordinator.Resolve(context);

        DisplayAckEnqueueResult result = coordinator.RecordDisplayed(
            decision,
            CreateDisplayedConfirmation(context));

        Assert.Equal(DisplayAckStatus.Accepted, result.Status);
        Assert.Equal(
            "display-receipt-v1-7e95b8c8ab287e3368a4ea58475e1546dce0a3c5e54e3b0f48dca2791d6fee92",
            result.DisplayReceiptId);
        Assert.Equal(
            "request-display-ack-v1-08ca298f0b9571be5f53a1fce85ab67cdb2665a37d8f5e34b0e465642ae491cc",
            result.RequestId);
        PendingDisplayAck attempt = Assert.IsType<PendingDisplayAck>(outbox.CreateNextAttempt());
        Assert.Equal(entry.GenerationId, attempt.GenerationId);
        Assert.Equal(result.DisplayReceiptId, attempt.Request.DisplayReceiptId);
        Assert.Equal(result.RequestId, attempt.Request.RequestId);
        Assert.Equal(ContractVersions.V1, attempt.Request.SchemaVersion);
        Assert.Equal(SaveId, attempt.Request.SaveId);
        Assert.Equal(PlayerId, attempt.Request.PlayerId);
        Assert.Equal(context.GameDayIndex, attempt.Request.DisplayedDayIndex);
        Assert.Equal(context.NpcId, attempt.Request.NpcId);
        Assert.Equal(SourceDialogueHasher.Compute(context.CurrentSourceText), attempt.Request.SourceHash);
        Assert.True(ContractValidator.Validate(attempt.Request).IsValid);
    }

    /// <summary>
    /// 同一显示事实重复确认必须得到同 receipt/request；第二次由 durable outbox 的等价
    /// Enqueue 判为 duplicate，既不新增 pending，也不重写 snapshot 字节。
    /// </summary>
    [Fact]
    public void RecordDisplayed_WhenSameFactRepeatsReturnsDuplicateAndKeepsOnePending()
    {
        string outboxPath = Path.Combine(testDirectory, "record-duplicate.json");
        DialogueDisplayContext context = CreateDisplayContext();
        DailyDialogueCache cache = new();
        cache.Store(CreateGeneratedCacheEntry(context));
        DurableDisplayAckOutbox outbox = OpenOutbox(outboxPath);
        DialogueDisplayCoordinator coordinator = new(SaveId, PlayerId, cache, outbox);
        DialogueDisplayDecision decision = coordinator.Resolve(context);
        DisplayedDialogueConfirmation confirmation = CreateDisplayedConfirmation(context);

        DisplayAckEnqueueResult first = coordinator.RecordDisplayed(decision, confirmation);
        byte[] firstSnapshotBytes = File.ReadAllBytes(outboxPath);
        DisplayAckEnqueueResult second = coordinator.RecordDisplayed(decision, confirmation);

        Assert.Equal(DisplayAckStatus.Accepted, first.Status);
        Assert.Equal(DisplayAckStatus.Duplicate, second.Status);
        Assert.Equal(first.DisplayReceiptId, second.DisplayReceiptId);
        Assert.Equal(first.RequestId, second.RequestId);
        Assert.Equal(1, outbox.PendingCount);
        Assert.Equal(firstSnapshotBytes, File.ReadAllBytes(outboxPath));
    }

    /// <summary>
    /// UseOriginal 决策从未授权增强展示；即使调用方错误声称“已显示”，也必须在 outbox
    /// 之前拒绝，不能把原版台词伪装成 generated 消费记忆冷却。
    /// </summary>
    [Fact]
    public void RecordDisplayed_WhenDecisionUsesOriginalRejectsWithoutWriting()
    {
        string outboxPath = Path.Combine(testDirectory, "record-original.json");
        DialogueDisplayContext context = CreateDisplayContext();
        DailyDialogueCache cache = new();
        DurableDisplayAckOutbox outbox = OpenOutbox(outboxPath);
        DialogueDisplayCoordinator coordinator = new(SaveId, PlayerId, cache, outbox);
        DialogueDisplayDecision originalDecision = coordinator.Resolve(context);

        Assert.Throws<InvalidOperationException>(
            () => coordinator.RecordDisplayed(
                originalDecision,
                CreateDisplayedConfirmation(context)));

        Assert.Equal(0, outbox.PendingCount);
        Assert.False(File.Exists(outboxPath));
    }

    /// <summary>
    /// 仅仅选中了增强文本仍不等于实际展示；调用方显式传入 false 时必须零写拒绝。
    /// </summary>
    [Fact]
    public void RecordDisplayed_WhenCallerSaysTextWasNotDisplayedRejectsWithoutWriting()
    {
        string outboxPath = Path.Combine(testDirectory, "record-not-displayed.json");
        DialogueDisplayContext context = CreateDisplayContext();
        DailyDialogueCache cache = new();
        cache.Store(CreateGeneratedCacheEntry(context));
        DurableDisplayAckOutbox outbox = OpenOutbox(outboxPath);
        DialogueDisplayCoordinator coordinator = new(SaveId, PlayerId, cache, outbox);
        DialogueDisplayDecision decision = coordinator.Resolve(context);
        DisplayedDialogueConfirmation confirmation = CreateDisplayedConfirmation(context) with
        {
            WasActuallyDisplayed = false,
        };

        Assert.Throws<InvalidOperationException>(
            () => coordinator.RecordDisplayed(decision, confirmation));

        Assert.Equal(0, outbox.PendingCount);
        Assert.False(File.Exists(outboxPath));
    }

    /// <summary>
    /// 实际显示确认必须与决策快照的 day、稳定 NPC ID 和 source hash 精确一致；任一篡改
    /// 都会在创建 request 前失败。
    /// </summary>
    [Theory]
    [InlineData(DisplayConfirmationMutation.DisplayedDayIndex)]
    [InlineData(DisplayConfirmationMutation.NpcId)]
    [InlineData(DisplayConfirmationMutation.SourceHash)]
    public void RecordDisplayed_WhenConfirmationIdentityDiffersRejectsWithoutWriting(
        DisplayConfirmationMutation mutation)
    {
        string outboxPath = Path.Combine(testDirectory, $"record-mismatch-{mutation}.json");
        DialogueDisplayContext context = CreateDisplayContext();
        DailyDialogueCache cache = new();
        cache.Store(CreateGeneratedCacheEntry(context));
        DurableDisplayAckOutbox outbox = OpenOutbox(outboxPath);
        DialogueDisplayCoordinator coordinator = new(SaveId, PlayerId, cache, outbox);
        DialogueDisplayDecision decision = coordinator.Resolve(context);
        DisplayedDialogueConfirmation valid = CreateDisplayedConfirmation(context);
        DisplayedDialogueConfirmation invalid = mutation switch
        {
            DisplayConfirmationMutation.DisplayedDayIndex => valid with
            {
                DisplayedDayIndex = valid.DisplayedDayIndex + 1,
            },
            DisplayConfirmationMutation.NpcId => valid with { NpcId = "Sebastian" },
            DisplayConfirmationMutation.SourceHash => valid with
            {
                SourceHash = SourceDialogueHasher.Compute(context.CurrentSourceText + "!"),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null),
        };

        Assert.Throws<ArgumentException>(() => coordinator.RecordDisplayed(decision, invalid));

        Assert.Equal(0, outbox.PendingCount);
        Assert.False(File.Exists(outboxPath));
    }

    /// <summary>
    /// 决策本身是不可公开构造的 opaque token，并绑定产生它的 coordinator 实例；即使另一个
    /// coordinator 读取了完全相同的 cache，跨实例 token 也不能授权当前实例写 ACK。
    /// </summary>
    [Fact]
    public void RecordDisplayed_WhenDecisionComesFromAnotherCoordinatorRejectsForgedToken()
    {
        string outboxPath = Path.Combine(testDirectory, "record-forged-token.json");
        DialogueDisplayContext context = CreateDisplayContext();
        DailyDialogueCache cache = new();
        cache.Store(CreateGeneratedCacheEntry(context));
        DurableDisplayAckOutbox outbox = OpenOutbox(outboxPath);
        DialogueDisplayCoordinator issuingCoordinator = new(SaveId, PlayerId, cache, outbox);
        DialogueDisplayCoordinator receivingCoordinator = new(SaveId, PlayerId, cache, outbox);
        DialogueDisplayDecision foreignDecision = issuingCoordinator.Resolve(context);

        Assert.Throws<ArgumentException>(
            () => receivingCoordinator.RecordDisplayed(
                foreignDecision,
                CreateDisplayedConfirmation(context)));

        Assert.Equal(0, outbox.PendingCount);
        Assert.False(File.Exists(outboxPath));
    }

    /// <summary>
    /// outbox 写盘失败必须原样抛给上层记录，但已选定的增强文本和 cache 快照不能被修改，
    /// 也不能出现只在内存里成功的 pending ACK。
    /// </summary>
    [Fact]
    public void RecordDisplayed_WhenOutboxWriteFailsPreservesDecisionAndCache()
    {
        string outboxPath = Path.Combine(testDirectory, "record-write-failure.json");
        DialogueDisplayContext context = CreateDisplayContext();
        DailyDialogueCache cache = new();
        DailyDialogueCacheEntry entry = CreateGeneratedCacheEntry(context);
        cache.Store(entry);
        OutboxPersistenceException injectedFailure = new(
            "injected display ACK write failure",
            new IOException("test-only move failure"));
        DurableDisplayAckOutbox outbox = DurableDisplayAckOutbox.Open(
            outboxPath,
            SaveId,
            PlayerId,
            snapshotWriter: _ => throw injectedFailure);
        DialogueDisplayCoordinator coordinator = new(SaveId, PlayerId, cache, outbox);
        DialogueDisplayDecision decision = coordinator.Resolve(context);

        OutboxPersistenceException actual = Assert.Throws<OutboxPersistenceException>(
            () => coordinator.RecordDisplayed(
                decision,
                CreateDisplayedConfirmation(context)));

        Assert.Same(injectedFailure, actual);
        Assert.Equal(DialogueDisplayDecisionKind.UseGenerated, decision.Kind);
        Assert.Equal(entry.EnhancedText, decision.EnhancedText);
        Assert.True(cache.TryGet(entry.Key, out DailyDialogueCacheEntry? cachedAfterFailure));
        Assert.Equal(entry, cachedAfterFailure);
        Assert.Equal(0, outbox.PendingCount);
        Assert.False(File.Exists(outboxPath));
    }

    /// <summary>
    /// 打开绑定本测试 save/player 分区的真实 durable outbox。
    /// </summary>
    private static DurableDisplayAckOutbox OpenOutbox(string absolutePath)
    {
        return DurableDisplayAckOutbox.Open(absolutePath, SaveId, PlayerId);
    }

    /// <summary>
    /// 创建点击时必须显式提供的完整当前上下文；不包含本地化 NPC 显示名或点击坐标。
    /// </summary>
    private static DialogueDisplayContext CreateDisplayContext()
    {
        return new DialogueDisplayContext(
            GameDayIndex: 14,
            Locale: "zh-CN",
            NpcId: "Abigail",
            AssetName: "Characters/Dialogue/Abigail",
            DialogueKey: "spring_Mon",
            CurrentSourceText: "原版台词-Abigail");
    }

    /// <summary>
    /// 创建只可能由已完整校验 generated 响应写入的正式 cache 形状。
    /// </summary>
    private static DailyDialogueCacheEntry CreateGeneratedCacheEntry(
        DialogueDisplayContext context)
    {
        DailyDialogueCacheKey key = new(
            context.GameDayIndex,
            context.Locale,
            context.NpcId,
            context.AssetName,
            context.DialogueKey);
        return new DailyDialogueCacheEntry(
            key,
            DialogueSourceFamily.OrdinaryDaily,
            context.CurrentSourceText,
            SourceDialogueHasher.Compute(context.CurrentSourceText),
            "增强台词-Abigail",
            GenerationId: "generation-abigail-14",
            GenerationKey: "generation-key-abigail-14",
            TraceId: "trace-abigail-14");
    }

    /// <summary>
    /// 模拟游戏适配层在原生 Dialogue UI 已成功显示增强文本之后提供的事实确认。
    /// </summary>
    private static DisplayedDialogueConfirmation CreateDisplayedConfirmation(
        DialogueDisplayContext context)
    {
        return new DisplayedDialogueConfirmation(
            WasActuallyDisplayed: true,
            DisplayedDayIndex: context.GameDayIndex,
            NpcId: context.NpcId,
            SourceHash: SourceDialogueHasher.Compute(context.CurrentSourceText));
    }

    /// <summary>
    /// 只删除当前测试创建的唯一目录，不触碰仓库或真实 Mods 文件。
    /// </summary>
    public void Dispose()
    {
        if (Directory.Exists(testDirectory))
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    /// <summary>
    /// 每个值只改变 cache 复合身份中的一个维度。
    /// </summary>
    public enum CacheKeyMutation
    {
        GameDayIndex,
        Locale,
        NpcId,
        AssetName,
        DialogueKey,
    }

    /// <summary>
    /// 每个值只破坏正式 generated cache 的一个展示 gate。
    /// </summary>
    public enum InvalidGeneratedCacheMutation
    {
        SourceFamilyMismatch,
        SourceTextMismatch,
        SourceHashMismatch,
        GenerationIdMissing,
        GenerationKeyMissing,
        TraceIdMissing,
        GenerationIdHasEdgeWhitespace,
        EnhancedTextNull,
        EnhancedTextBlank,
        EnhancedTextHasEdgeWhitespace,
        EnhancedTextHasSecondPlayerNameSlot,
        EnhancedTextHasOtherDsl,
    }

    /// <summary>
    /// 每个值只篡改实际显示确认中的一个身份字段。
    /// </summary>
    public enum DisplayConfirmationMutation
    {
        DisplayedDayIndex,
        NpcId,
        SourceHash,
    }
}
