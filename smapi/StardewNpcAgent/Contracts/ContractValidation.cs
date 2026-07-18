using System.Text.Json;

namespace StardewNpcAgent.Contracts;

/// <summary>
/// 单条、可定位的 C# wire contract 校验错误。
/// </summary>
/// <param name="Path">使用 snake_case 与数组下标表示的字段路径。</param>
/// <param name="Message">面向开发日志的中文原因，不包含隐私或游戏存档正文。</param>
public sealed record ContractValidationError(string Path, string Message);

/// <summary>
/// 纯合同校验结果，不抛业务异常，也不修改被校验 DTO。
/// </summary>
public sealed class ContractValidationResult
{
    /// <summary>
    /// 创建不可变错误快照。
    /// </summary>
    /// <param name="errors">本次校验发现的全部错误。</param>
    internal ContractValidationResult(IEnumerable<ContractValidationError> errors)
    {
        Errors = errors.ToArray();
    }

    /// <summary>
    /// 所有已发现错误；合法结果为空。
    /// </summary>
    public IReadOnlyList<ContractValidationError> Errors { get; }

    /// <summary>
    /// 是否可以进入后续业务流程。
    /// </summary>
    public bool IsValid => Errors.Count == 0;

    /// <summary>
    /// 生成适合测试失败和开发日志的紧凑描述。
    /// </summary>
    public override string ToString()
    {
        return IsValid
            ? "合同合法"
            : string.Join("; ", Errors.Select(error => $"{error.Path}: {error.Message}"));
    }
}

/// <summary>
/// 游戏侧轻量、确定性的 wire contract 校验入口。
/// </summary>
/// <remarks>
/// 共享 JSON Schema 仍是跨语言真值。本类只实现 C# runtime 立即需要的局部不变量，
/// 包括非空/首尾空白、集合边界、数值下限、JSON object、事件受众和响应文本终态。
/// 所有方法均无 IO、无日志、无状态且不修剪字符串，便于单元测试和后续 HTTP 复用。
/// </remarks>
public static class ContractValidator
{
    private const int MinimumStyleExampleCount = 2;
    private const int MaximumStyleExampleCount = 5;
    private const int MaximumDialogueBatchSize = 8;

    /// <summary>
    /// 校验单条结构化游戏事件。
    /// </summary>
    /// <param name="gameEvent">尚未进入 outbox 或批量请求的单条事件。</param>
    /// <returns>与批次内事件完全相同规则产生的不可变校验结果。</returns>
    /// <remarks>
    /// Durable outbox 必须在持久化单条事件时复用 wire contract 真值，但构造完整批次只为
    /// 触发校验会引入伪 request/save/player 字段。这个入口直接调用既有私有实现，避免复制
    /// event_id、payload object 与 audience 组合规则后发生长期漂移。
    /// </remarks>
    public static ContractValidationResult Validate(GameEvent gameEvent)
    {
        ArgumentNullException.ThrowIfNull(gameEvent);
        List<ContractValidationError> errors = new();

        ValidateGameEvent(gameEvent, "$", errors);

        return new ContractValidationResult(errors);
    }

    /// <summary>
    /// 校验游戏事件批次请求。
    /// </summary>
    public static ContractValidationResult Validate(GameEventBatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<ContractValidationError> errors = new();

        ValidateDto(request, "$", errors);
        ValidateSchemaVersion(request.SchemaVersion, "schema_version", errors);
        ValidateNonBlank(request.RequestId, "request_id", errors);
        ValidateNonBlank(request.SaveId, "save_id", errors);
        ValidateNonBlank(request.PlayerId, "player_id", errors);

        if (request.Events is null
            || request.Events.Count == 0
            || request.Events.Count > ContractLimits.MaximumEventsPerBatch)
        {
            Add(
                errors,
                "events",
                $"事件数量必须在 1～{ContractLimits.MaximumEventsPerBatch} 之间。");
            // 批次大小是资源保护 envelope。超限后继续扫描子项会让攻击性输入线性放大
            // CPU 和错误集合；保留上面已完成的 envelope 字段错误后立即返回。
            return new ContractValidationResult(errors);
        }

        for (int index = 0; index < request.Events.Count; index++)
        {
            ValidateGameEvent(request.Events[index], $"events[{index}]", errors);
        }

        return new ContractValidationResult(errors);
    }

    /// <summary>
    /// 校验事件接收响应，供后续 HTTP client 在写入 outbox ACK 前使用。
    /// </summary>
    public static ContractValidationResult Validate(GameEventBatchResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        List<ContractValidationError> errors = new();

        ValidateDto(response, "$", errors);
        ValidateSchemaVersion(response.SchemaVersion, "schema_version", errors);
        ValidateNonBlank(response.RequestId, "request_id", errors);
        ValidateNonNegative(response.MemoryRevision, "memory_revision", errors);
        ValidateAtLeastMinusOne(
            response.CommittedThroughDayIndex,
            "committed_through_day_index",
            errors);
        if (response.Items is null || response.Items.Count == 0)
        {
            Add(errors, "items", "至少需要一个事件结果。");
        }
        else
        {
            for (int index = 0; index < response.Items.Count; index++)
            {
                string path = $"items[{index}]";
                GameEventItemResult? item = response.Items[index];
                if (item is null)
                {
                    // System.Text.Json 允许集合元素显式为 null；合同校验必须像其他 DTO
                    // 集合路径一样返回索引级错误，不能把不可信 wire 数据变成 NRE。
                    Add(errors, path, "事件结果不能为 null。");
                    continue;
                }

                ValidateDto(item, path, errors);
                ValidateNonBlank(item.EventId, $"{path}.event_id", errors);
                ValidateOptionalNonBlank(item.ReasonCode, $"{path}.reason_code", errors);
            }
        }

        return new ContractValidationResult(errors);
    }

    /// <summary>
    /// 校验每日台词生成请求，包括每项 2～5 条风格样本。
    /// </summary>
    public static ContractValidationResult Validate(DialogueGenerationBatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<ContractValidationError> errors = new();

        ValidateDto(request, "$", errors);
        ValidateSchemaVersion(request.SchemaVersion, "schema_version", errors);
        ValidateNonBlank(request.RequestId, "request_id", errors);
        ValidateNonBlank(request.SaveId, "save_id", errors);
        ValidateNonBlank(request.PlayerId, "player_id", errors);
        ValidateNonNegative(request.GameDayIndex, "game_day_index", errors);
        ValidateNonNegative(request.RequiredMemoryRevision, "required_memory_revision", errors);
        ValidateStableDayContext(request.StableDayContext, "stable_day_context", errors);

        if (request.Items is null || request.Items.Count == 0 || request.Items.Count > MaximumDialogueBatchSize)
        {
            Add(errors, "items", $"条目数量必须在 1～{MaximumDialogueBatchSize} 之间。");
        }

        if (request.Items is not null)
        {
            HashSet<string> taskIds = new(StringComparer.Ordinal);
            HashSet<string> npcIds = new(StringComparer.Ordinal);
            for (int index = 0; index < request.Items.Count; index++)
            {
                DialogueGenerationItem? item = request.Items[index];
                string path = $"items[{index}]";
                ValidateDialogueItem(item!, path, errors);
                if (item is null)
                {
                    continue;
                }

                // 后端以 task_id 和 npc_id 分别作为批次映射与每 NPC 隔离边界。重复值会让
                // partial response 无法唯一映射回候选，因此属于 envelope 业务错误。
                if (!string.IsNullOrWhiteSpace(item.TaskId) && !taskIds.Add(item.TaskId))
                {
                    Add(errors, $"{path}.task_id", "同一批次内 task_id 必须唯一。");
                }

                if (!string.IsNullOrWhiteSpace(item.NpcId) && !npcIds.Add(item.NpcId))
                {
                    Add(errors, $"{path}.npc_id", "同一批次内 npc_id 必须唯一。");
                }
            }
        }

        return new ContractValidationResult(errors);
    }

    /// <summary>
    /// 校验逐 NPC 的生成响应及 status/text 一致性。
    /// </summary>
    public static ContractValidationResult Validate(DialogueGenerationBatchResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        List<ContractValidationError> errors = new();

        ValidateDto(response, "$", errors);
        ValidateSchemaVersion(response.SchemaVersion, "schema_version", errors);
        ValidateNonBlank(response.RequestId, "request_id", errors);
        ValidateNonNegative(response.MemoryRevision, "memory_revision", errors);

        if (response.Items is null || response.Items.Count == 0 || response.Items.Count > MaximumDialogueBatchSize)
        {
            Add(errors, "items", $"条目数量必须在 1～{MaximumDialogueBatchSize} 之间。");
        }

        if (response.Items is not null)
        {
            for (int index = 0; index < response.Items.Count; index++)
            {
                ValidateDialogueResult(response.Items[index], $"items[{index}]", errors);
            }
        }

        return new ContractValidationResult(errors);
    }

    /// <summary>
    /// 校验每日生成响应是否与其原始请求逐项、逐字段精确对应。
    /// </summary>
    /// <param name="request">协调器实际提交给生成网关的请求。</param>
    /// <param name="response">网关返回、尚未写入游戏 cache 的响应。</param>
    /// <returns>
    /// 同时包含两侧 wire 错误、envelope 不一致、逐项错序和展示身份重复的不可变结果。
    /// </returns>
    /// <remarks>
    /// 该重载只服务 DayStarted request/response 映射，不承担 HTTP、重试或工作流编排。
    /// cache 写入前必须整批通过；任何错误都意味着无法可靠证明 generated 文本属于当前
    /// source/task/memory snapshot，因此调用方应整批回退原版。
    /// </remarks>
    public static ContractValidationResult Validate(
        DialogueGenerationBatchRequest request,
        DialogueGenerationBatchResponse response)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);

        List<ContractValidationError> errors = new();
        errors.AddRange(Validate(request).Errors);
        errors.AddRange(Validate(response).Errors);

        if (!string.Equals(response.SchemaVersion, request.SchemaVersion, StringComparison.Ordinal))
        {
            Add(errors, "response.schema_version", "必须与原始请求 schema_version 精确一致。");
        }

        if (!string.Equals(response.RequestId, request.RequestId, StringComparison.Ordinal))
        {
            Add(errors, "response.request_id", "必须与原始请求 request_id 精确一致。");
        }

        if (response.MemoryRevision != request.RequiredMemoryRevision)
        {
            Add(
                errors,
                "response.memory_revision",
                "必须等于请求冻结的 required_memory_revision。");
        }

        if (request.Items is null || response.Items is null)
        {
            return new ContractValidationResult(errors);
        }

        if (response.Items.Count != request.Items.Count)
        {
            Add(errors, "response.items", "条目数量必须与原始请求完全一致。");
            return new ContractValidationResult(errors);
        }

        HashSet<string> generationIds = new(StringComparer.Ordinal);
        HashSet<string> generationKeys = new(StringComparer.Ordinal);
        HashSet<string> traceIds = new(StringComparer.Ordinal);
        for (int index = 0; index < request.Items.Count; index++)
        {
            DialogueGenerationItem? requestItem = request.Items[index];
            DialogueGenerationItemResult? responseItem = response.Items[index];
            string responsePath = $"response.items[{index}]";
            if (requestItem is null || responseItem is null)
            {
                // 单侧 wire 校验已经报告精确 null 路径；映射层不能继续解引用或猜测身份。
                continue;
            }

            if (!string.Equals(responseItem.TaskId, requestItem.TaskId, StringComparison.Ordinal))
            {
                Add(errors, $"{responsePath}.task_id", "必须与同序请求条目的 task_id 一致。");
            }

            if (!string.Equals(
                    responseItem.SourceHash,
                    requestItem.SourceDialogue.SourceHash,
                    StringComparison.Ordinal))
            {
                Add(
                    errors,
                    $"{responsePath}.source_hash",
                    "必须与同序请求条目的 source_hash 一致。");
            }

            ValidateUniqueResponseIdentity(
                responseItem.GenerationId,
                generationIds,
                $"{responsePath}.generation_id",
                "generation_id",
                errors);
            ValidateUniqueResponseIdentity(
                responseItem.GenerationKey,
                generationKeys,
                $"{responsePath}.generation_key",
                "generation_key",
                errors);
            ValidateUniqueResponseIdentity(
                responseItem.TraceId,
                traceIds,
                $"{responsePath}.trace_id",
                "trace_id",
                errors);
        }

        return new ContractValidationResult(errors);
    }

    /// <summary>
    /// 校验增强台词展示回执请求。
    /// </summary>
    public static ContractValidationResult Validate(DisplayAckRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<ContractValidationError> errors = new();

        ValidateDto(request, "$", errors);
        ValidateSchemaVersion(request.SchemaVersion, "schema_version", errors);
        ValidateNonBlank(request.RequestId, "request_id", errors);
        ValidateNonBlank(request.SaveId, "save_id", errors);
        ValidateNonBlank(request.PlayerId, "player_id", errors);
        ValidateNonBlank(request.DisplayReceiptId, "display_receipt_id", errors);
        ValidateNonNegative(request.DisplayedDayIndex, "displayed_day_index", errors);
        ValidateNonBlank(request.NpcId, "npc_id", errors);
        ValidateNonBlank(request.SourceHash, "source_hash", errors);

        return new ContractValidationResult(errors);
    }

    /// <summary>
    /// 校验展示回执响应。
    /// </summary>
    public static ContractValidationResult Validate(DisplayAckResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        List<ContractValidationError> errors = new();

        ValidateDto(response, "$", errors);
        ValidateSchemaVersion(response.SchemaVersion, "schema_version", errors);
        ValidateNonBlank(response.RequestId, "request_id", errors);
        ValidateNonBlank(response.DisplayReceiptId, "display_receipt_id", errors);

        return new ContractValidationResult(errors);
    }

    /// <summary>
    /// 校验单条结构化事件及 audience_scope/audience_npc_id 的唯一合法组合。
    /// </summary>
    private static void ValidateGameEvent(GameEvent gameEvent, string path, ICollection<ContractValidationError> errors)
    {
        if (gameEvent is null)
        {
            Add(errors, path, "事件不能为 null。");
            return;
        }

        ValidateDto(gameEvent, path, errors);
        ValidateNonBlank(gameEvent.EventId, $"{path}.event_id", errors);
        ValidateNonBlank(gameEvent.EventType, $"{path}.event_type", errors);
        ValidateNonBlank(gameEvent.EventVersion, $"{path}.event_version", errors);
        ValidateNonNegative(gameEvent.OccurredDayIndex, $"{path}.occurred_day_index", errors);
        ValidateNonBlank(gameEvent.Source, $"{path}.source", errors);
        ValidateJsonObject(gameEvent.Payload, $"{path}.payload", errors);

        switch (gameEvent.AudienceScope)
        {
            case AudienceScope.Public:
                if (gameEvent.AudienceNpcId is not null)
                {
                    Add(errors, $"{path}.audience_npc_id", "public 事件必须使用 null。");
                }

                break;

            case AudienceScope.Npc:
                ValidateNonBlank(gameEvent.AudienceNpcId, $"{path}.audience_npc_id", errors);
                break;

            default:
                // 正常 JSON 入口已由严格 enum converter 拒绝未知值；该分支保护手工构造 DTO。
                Add(errors, $"{path}.audience_scope", "不是已声明的受众范围。");
                break;
        }
    }

    /// <summary>
    /// 校验稳定日上下文与可扩展 progression_signals object。
    /// </summary>
    private static void ValidateStableDayContext(
        StableDayContext context,
        string path,
        ICollection<ContractValidationError> errors)
    {
        if (context is null)
        {
            Add(errors, path, "稳定日上下文不能为 null。");
            return;
        }

        ValidateDto(context, path, errors);
        ValidateNonBlank(context.Season, $"{path}.season", errors);
        ValidateNonBlank(context.Weather, $"{path}.weather", errors);
        ValidateNonBlank(context.Locale, $"{path}.locale", errors);
        ValidateJsonObject(context.ProgressionSignals, $"{path}.progression_signals", errors);
    }

    /// <summary>
    /// 校验一项生成输入；不执行任何记忆检索或 NPC 资格判断。
    /// </summary>
    private static void ValidateDialogueItem(
        DialogueGenerationItem item,
        string path,
        ICollection<ContractValidationError> errors)
    {
        if (item is null)
        {
            Add(errors, path, "生成条目不能为 null。");
            return;
        }

        ValidateDto(item, path, errors);
        ValidateNonBlank(item.TaskId, $"{path}.task_id", errors);
        ValidateNonBlank(item.NpcId, $"{path}.npc_id", errors);
        ValidateSourceDialogue(item.SourceDialogue, $"{path}.source_dialogue", errors);
        ValidateRelationship(item.RelationshipSnapshot, $"{path}.relationship_snapshot", errors);

        if (item.StyleExamples is null
            || item.StyleExamples.Count < MinimumStyleExampleCount
            || item.StyleExamples.Count > MaximumStyleExampleCount)
        {
            Add(
                errors,
                $"{path}.style_examples",
                $"风格样本数量必须在 {MinimumStyleExampleCount}～{MaximumStyleExampleCount} 之间。");
        }

        if (item.StyleExamples is not null)
        {
            for (int index = 0; index < item.StyleExamples.Count; index++)
            {
                ValidateNonBlank(item.StyleExamples[index], $"{path}.style_examples[{index}]", errors);
            }
        }

        if (item.MemorySignals is null)
        {
            Add(errors, $"{path}.memory_signals", "记忆线索数组不能为 null。");
        }
        else
        {
            for (int index = 0; index < item.MemorySignals.Count; index++)
            {
                ValidateJsonObject(item.MemorySignals[index], $"{path}.memory_signals[{index}]", errors);
            }
        }
    }

    /// <summary>
    /// 校验原台词来源信息；只比较内容，不执行 Trim 或 hash 计算。
    /// </summary>
    private static void ValidateSourceDialogue(
        SourceDialogue source,
        string path,
        ICollection<ContractValidationError> errors)
    {
        if (source is null)
        {
            Add(errors, path, "原台词来源不能为 null。");
            return;
        }

        ValidateDto(source, path, errors);
        ValidateNonBlank(source.AssetName, $"{path}.asset_name", errors);
        ValidateNonBlank(source.DialogueKey, $"{path}.dialogue_key", errors);
        ValidateNonBlank(source.Text, $"{path}.text", errors);
        ValidateNonBlank(source.SourceHash, $"{path}.source_hash", errors);
    }

    /// <summary>
    /// 校验游戏提供的关系快照。
    /// </summary>
    private static void ValidateRelationship(
        RelationshipSnapshot relationship,
        string path,
        ICollection<ContractValidationError> errors)
    {
        if (relationship is null)
        {
            Add(errors, path, "关系快照不能为 null。");
            return;
        }

        ValidateDto(relationship, path, errors);
        ValidateNonBlank(relationship.RelationshipStage, $"{path}.relationship_stage", errors);
    }

    /// <summary>
    /// 校验生成终态及其可展示文本不变量。
    /// </summary>
    private static void ValidateDialogueResult(
        DialogueGenerationItemResult item,
        string path,
        ICollection<ContractValidationError> errors)
    {
        if (item is null)
        {
            Add(errors, path, "生成结果不能为 null。");
            return;
        }

        ValidateDto(item, path, errors);
        ValidateNonBlank(item.TaskId, $"{path}.task_id", errors);
        ValidateNonBlank(item.GenerationId, $"{path}.generation_id", errors);
        ValidateNonBlank(item.GenerationKey, $"{path}.generation_key", errors);
        ValidateNonBlank(item.SourceHash, $"{path}.source_hash", errors);
        ValidateNonBlank(item.ReasonCode, $"{path}.reason_code", errors);
        ValidateNonBlank(item.TraceId, $"{path}.trace_id", errors);

        bool isDeclaredStatus = item.Status is DialogueGenerationStatus.Generated
            or DialogueGenerationStatus.Passthrough
            or DialogueGenerationStatus.Skipped
            or DialogueGenerationStatus.Failed;
        if (!isDeclaredStatus)
        {
            // 手工构造 DTO 可绕过严格 JSON enum converter；未知 status 不能被误当成
            // “非 generated 且 text=null”的合法原版路径。
            Add(errors, $"{path}.status", "不是已声明的生成终态。");
        }
        else if (item.Status == DialogueGenerationStatus.Generated)
        {
            ValidateNonBlank(item.Text, $"{path}.text", errors);
        }
        else if (item.Text is not null)
        {
            Add(errors, $"{path}.text", $"{item.Status} 状态必须使用 null 文本。");
        }

        if (item.EvidenceIds is null)
        {
            Add(errors, $"{path}.evidence_ids", "evidence_ids 不能为 null。");
        }
        else
        {
            for (int index = 0; index < item.EvidenceIds.Count; index++)
            {
                ValidateNonBlank(item.EvidenceIds[index], $"{path}.evidence_ids[{index}]", errors);
            }
        }
    }

    /// <summary>
    /// 非空展示身份在同一 batch 内必须唯一，防止两个 NPC 共用 generation/trace 身份。
    /// </summary>
    private static void ValidateUniqueResponseIdentity(
        string? value,
        ISet<string> seenValues,
        string path,
        string fieldName,
        ICollection<ContractValidationError> errors)
    {
        // 必填与边缘空白由单 DTO 校验报告；这里仅补跨项重复，避免同一坏值产生噪声错误。
        if (!string.IsNullOrWhiteSpace(value) && !seenValues.Add(value))
        {
            Add(errors, path, $"同一响应内 {fieldName} 必须唯一。");
        }
    }

    /// <summary>
    /// 验证协议版本精确等于 V1。
    /// </summary>
    private static void ValidateSchemaVersion(
        string? value,
        string path,
        ICollection<ContractValidationError> errors)
    {
        ValidateNonBlank(value, path, errors);
        if (value is not null && value != ContractVersions.V1)
        {
            Add(errors, path, $"当前只支持 {ContractVersions.V1}。");
        }
    }

    /// <summary>
    /// 拒绝空字符串、纯空白以及首尾空白；内部空白和 CRLF 保持原样。
    /// </summary>
    private static void ValidateNonBlank(
        string? value,
        string path,
        ICollection<ContractValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Add(errors, path, "必须是非空字符串。");
            return;
        }

        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            Add(errors, path, "首尾不能包含空白字符；校验器不会自动修剪输入。");
        }
    }

    /// <summary>
    /// null 合法；非 null 时使用与必填字符串相同的边缘空白规则。
    /// </summary>
    private static void ValidateOptionalNonBlank(
        string? value,
        string path,
        ICollection<ContractValidationError> errors)
    {
        if (value is not null)
        {
            ValidateNonBlank(value, path, errors);
        }
    }

    /// <summary>
    /// 验证整数不能小于零。
    /// </summary>
    private static void ValidateNonNegative(
        int value,
        string path,
        ICollection<ContractValidationError> errors)
    {
        if (value < 0)
        {
            Add(errors, path, "不能小于 0。");
        }
    }

    /// <summary>
    /// 验证分区已提交日允许空分区哨兵 -1，其余值必须为非负游戏日。
    /// </summary>
    private static void ValidateAtLeastMinusOne(
        int value,
        string path,
        ICollection<ContractValidationError> errors)
    {
        if (value < -1)
        {
            Add(errors, path, "不能小于空分区哨兵 -1。");
        }
    }

    /// <summary>
    /// 验证可扩展 JSON 值在 wire 上确实是 object，而不是 null、数组或标量。
    /// </summary>
    private static void ValidateJsonObject(
        JsonElement value,
        string path,
        ICollection<ContractValidationError> errors)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            Add(errors, path, "必须是 JSON object。");
        }
    }

    /// <summary>
    /// 将未声明字段转为合同错误，保护两端版本协商。
    /// </summary>
    private static void ValidateDto(
        ContractDto dto,
        string path,
        ICollection<ContractValidationError> errors)
    {
        if (dto.ExtensionData is not { Count: > 0 })
        {
            return;
        }

        foreach (string propertyName in dto.ExtensionData.Keys.OrderBy(name => name, StringComparer.Ordinal))
        {
            string propertyPath = path == "$" ? propertyName : $"{path}.{propertyName}";
            Add(errors, propertyPath, "当前合同版本不允许该未知字段。");
        }
    }

    /// <summary>
    /// 统一追加错误，确保路径和消息结构稳定。
    /// </summary>
    private static void Add(
        ICollection<ContractValidationError> errors,
        string path,
        string message)
    {
        errors.Add(new ContractValidationError(path, message));
    }
}
