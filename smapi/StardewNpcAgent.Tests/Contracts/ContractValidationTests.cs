using System.Text.Json;
using System.Globalization;
using StardewNpcAgent.Contracts;

namespace StardewNpcAgent.Tests.Contracts;

/// <summary>
/// 覆盖 JSON Schema 之外、C# 运行时必须主动执行的轻量合同校验。
/// </summary>
/// <remarks>
/// 这些测试刻意不实现或模拟完整 JSON Schema 引擎。它们只冻结游戏侧最容易出错、
/// 且后续业务逻辑必须依赖的边界：样本数量、事件受众、空白与严格枚举 token。
/// </remarks>
public sealed class ContractValidationTests
{
    /// <summary>
    /// 2 与 5 都是合法的风格样本边界。
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    public void DialogueRequest_AcceptsTwoToFiveStyleExamplesAtBoundaries(int exampleCount)
    {
        DialogueGenerationBatchRequest request = ReadDialogueRequest();
        request.Items[0].StyleExamples = BuildStyleExamples(exampleCount);

        ContractValidationResult result = ContractValidator.Validate(request);

        Assert.True(result.IsValid, result.ToString());
    }

    /// <summary>
    /// 1 与 6 分别越过下限和上限，必须被纯合同校验拒绝。
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    public void DialogueRequest_RejectsStyleExampleCountsOutsideContract(int exampleCount)
    {
        DialogueGenerationBatchRequest request = ReadDialogueRequest();
        request.Items[0].StyleExamples = BuildStyleExamples(exampleCount);

        ContractValidationResult result = ContractValidator.Validate(request);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Path == "items[0].style_examples");
    }

    /// <summary>
    /// fixture 中的 npc/id 与 public/null 两种组合都必须合法。
    /// </summary>
    [Fact]
    public void EventBatch_AcceptsNpcIdAndPublicNullAudienceCombinations()
    {
        GameEventBatchRequest request = ReadEventRequest();

        ContractValidationResult result = ContractValidator.Validate(request);

        Assert.True(result.IsValid, result.ToString());
    }

    /// <summary>
    /// DTO 与 ContractJson round-trip 组合链都必须接受恰好达到生产上限的合法事件。
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EventBatch_AcceptsMaximumEventsAtHardLimit(bool roundTripThroughContractJson)
    {
        GameEventBatchRequest request = BuildEventRequest(ContractLimits.MaximumEventsPerBatch);
        if (roundTripThroughContractJson)
        {
            request = ContractJson.Deserialize<GameEventBatchRequest>(ContractJson.Serialize(request));
        }

        ContractValidationResult result = ContractValidator.Validate(request);

        Assert.True(result.IsValid, result.ToString());
    }

    /// <summary>
    /// DTO 与 ContractJson round-trip 组合链都必须在业务执行前拒绝第一条越界事件。
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EventBatch_RejectsFirstEventAboveHardLimit(bool roundTripThroughContractJson)
    {
        GameEventBatchRequest request = BuildEventRequest(ContractLimits.MaximumEventsPerBatch + 1);
        if (roundTripThroughContractJson)
        {
            request = ContractJson.Deserialize<GameEventBatchRequest>(ContractJson.Serialize(request));
        }

        ContractValidationResult result = ContractValidator.Validate(request);

        Assert.False(result.IsValid);
        ContractValidationError error = Assert.Single(
            result.Errors,
            candidate => candidate.Path == "events");
        Assert.Contains(
            ContractLimits.MaximumEventsPerBatch.ToString(CultureInfo.InvariantCulture),
            error.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// 超限 envelope 必须在批次级 fail-fast；首项与末项的无效字段都不能被继续扫描，
    /// 否则攻击性输入仍可能放大为最多数百条错误和 CPU 工作。
    /// </summary>
    [Fact]
    public void EventBatch_OverflowFailsFastWithoutValidatingInvalidChildEvents()
    {
        GameEventBatchRequest request = BuildEventRequest(ContractLimits.MaximumEventsPerBatch + 1);
        request.Events[0].EventId = string.Empty;
        request.Events[^1].Payload = default;

        ContractValidationResult result = ContractValidator.Validate(request);

        ContractValidationError error = Assert.Single(result.Errors);
        Assert.Equal("events", error.Path);
        Assert.DoesNotContain(result.Errors, candidate => candidate.Path.StartsWith("events[", StringComparison.Ordinal));
    }

    /// <summary>
    /// public 事件不得携带 NPC ID，否则会把全局事实错误地缩窄到单个角色。
    /// </summary>
    [Fact]
    public void EventBatch_RejectsPublicAudienceWithNpcId()
    {
        GameEventBatchRequest request = ReadEventRequest();
        request.Events[1].AudienceNpcId = "Abigail";

        ContractValidationResult result = ContractValidator.Validate(request);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Path == "events[1].audience_npc_id");
    }

    /// <summary>
    /// NPC 定向事件必须携带稳定内部 ID，不能以 null 表示未知角色。
    /// </summary>
    [Fact]
    public void EventBatch_RejectsNpcAudienceWithoutNpcId()
    {
        GameEventBatchRequest request = ReadEventRequest();
        request.Events[0].AudienceNpcId = null;

        ContractValidationResult result = ContractValidator.Validate(request);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Path == "events[0].audience_npc_id");
    }

    /// <summary>
    /// 首部或尾部空白会改变幂等键和稳定 ID 的含义，必须直接拒绝而不是静默 Trim。
    /// </summary>
    [Theory]
    [InlineData(" request-events-spring-15")]
    [InlineData("request-events-spring-15 ")]
    public void EventBatch_RejectsLeadingOrTrailingWhitespaceWithoutNormalizing(string requestId)
    {
        GameEventBatchRequest request = ReadEventRequest();
        request.RequestId = requestId;

        ContractValidationResult result = ContractValidator.Validate(request);

        Assert.False(result.IsValid);
        Assert.Equal(requestId, request.RequestId);
        Assert.Contains(result.Errors, error => error.Path == "request_id");
    }

    /// <summary>
    /// 台词正文中的内部 CRLF 是合法内容，校验和序列化不得改写换行格式。
    /// </summary>
    [Fact]
    public void DialogueRequest_PreservesInternalCrLfExactly()
    {
        const string textWithCrLf = "第一行保留。\r\n第二行也保留。";
        DialogueGenerationBatchRequest request = ReadDialogueRequest();
        request.Items[0].SourceDialogue.Text = textWithCrLf;

        ContractValidationResult result = ContractValidator.Validate(request);
        string serialized = ContractJson.Serialize(request);
        DialogueGenerationBatchRequest roundTripped = ContractJson.Deserialize<DialogueGenerationBatchRequest>(serialized);

        Assert.True(result.IsValid, result.ToString());
        Assert.Equal(textWithCrLf, request.Items[0].SourceDialogue.Text);
        Assert.Equal(textWithCrLf, roundTripped.Items[0].SourceDialogue.Text);
    }

    /// <summary>
    /// 事件响应列表允许反序列化显式 null，但语义校验必须返回可定位错误而不是抛 NRE。
    /// </summary>
    [Fact]
    public void GameEventResponse_NullItemReturnsContractErrorWithoutThrowing()
    {
        const string responseJson =
            "{"
            + "\"schema_version\":\"1.0\","
            + "\"request_id\":\"request-events-spring-15\","
            + "\"memory_revision\":42,"
            + "\"committed_through_day_index\":13,"
            + "\"items\":[null]"
            + "}";
        GameEventBatchResponse response = ContractJson.Deserialize<GameEventBatchResponse>(responseJson);
        ContractValidationResult? result = null;

        Exception? exception = Record.Exception(
            () => result = ContractValidator.Validate(response));

        Assert.Null(exception);
        ContractValidationResult validation = Assert.IsType<ContractValidationResult>(result);
        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error => error.Path == "items[0]");
    }

    /// <summary>
    /// 空分区哨兵 -1、首个合法日 0 与 Int32 最大日都必须通过响应语义校验。
    /// </summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(int.MaxValue)]
    public void GameEventResponse_AcceptsCommittedDayWireBoundaries(int committedDay)
    {
        GameEventBatchResponse response = BuildEventResponse();
        response.CommittedThroughDayIndex = committedDay;

        ContractValidationResult result = ContractValidator.Validate(response);

        Assert.True(result.IsValid, result.ToString());
    }

    /// <summary>
    /// -2 不是合法空分区哨兵，也不是非负游戏日，必须返回可定位合同错误。
    /// </summary>
    [Fact]
    public void GameEventResponse_RejectsCommittedDayBelowMinusOne()
    {
        GameEventBatchResponse response = BuildEventResponse();
        response.CommittedThroughDayIndex = -2;

        ContractValidationResult result = ContractValidator.Validate(response);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error.Path == "committed_through_day_index");
    }

    /// <summary>
    /// 其余 ContractDto 集合路径也必须把显式 null 变成索引级合同错误，保持行为一致。
    /// </summary>
    [Fact]
    public void OtherContractCollections_ReportIndexedErrorsForNullItems()
    {
        GameEventBatchRequest eventRequest = ReadEventRequest();
        DialogueGenerationBatchRequest dialogueRequest = ReadDialogueRequest();
        DialogueGenerationBatchResponse dialogueResponse = ReadDialogueResponse();
        eventRequest.Events[0] = null!;
        dialogueRequest.Items[0] = null!;
        dialogueResponse.Items[0] = null!;

        ContractValidationResult eventValidation = ContractValidator.Validate(eventRequest);
        ContractValidationResult requestValidation = ContractValidator.Validate(dialogueRequest);
        ContractValidationResult responseValidation = ContractValidator.Validate(dialogueResponse);

        Assert.Contains(eventValidation.Errors, error => error.Path == "events[0]");
        Assert.Contains(requestValidation.Errors, error => error.Path == "items[0]");
        Assert.Contains(responseValidation.Errors, error => error.Path == "items[0]");
    }

    /// <summary>
    /// generated 文本若含首尾空白必须直接拒绝，并保留原值供调用方诊断，不能静默 Trim。
    /// </summary>
    [Fact]
    public void GeneratedDialogue_RejectsEdgeWhitespaceWithoutNormalizing()
    {
        const string paddedText = " 需要拒绝但不能改写的台词 ";
        DialogueGenerationBatchResponse response = ReadDialogueResponse();
        response.Items[0].Text = paddedText;

        ContractValidationResult validation = ContractValidator.Validate(response);

        Assert.False(validation.IsValid);
        Assert.Equal(paddedText, response.Items[0].Text);
        Assert.Contains(validation.Errors, error => error.Path == "items[0].text");
    }

    /// <summary>
    /// generated 文本内部的 CRLF 合法，校验及 JSON round-trip 必须逐字符保留。
    /// </summary>
    [Fact]
    public void GeneratedDialogue_PreservesInternalCrLfExactly()
    {
        const string textWithCrLf = "第一行增强台词。\r\n第二行保持原样。";
        DialogueGenerationBatchResponse response = ReadDialogueResponse();
        response.Items[0].Text = textWithCrLf;

        ContractValidationResult validation = ContractValidator.Validate(response);
        string serialized = ContractJson.Serialize(response);
        DialogueGenerationBatchResponse roundTripped =
            ContractJson.Deserialize<DialogueGenerationBatchResponse>(serialized);

        Assert.True(validation.IsValid, validation.ToString());
        Assert.Equal(textWithCrLf, response.Items[0].Text);
        Assert.Equal(textWithCrLf, roundTripped.Items[0].Text);
    }

    /// <summary>
    /// 所有公开枚举都必须输出约定的单词 token，而不是 C# 名称或整数。
    /// </summary>
    [Fact]
    public void ContractJson_SerializesEveryEnumAsExactLowerCamelWireToken()
    {
        Assert.Equal("\"accepted\"", ContractJson.Serialize(EventIngestionStatus.Accepted));
        Assert.Equal("\"duplicate\"", ContractJson.Serialize(EventIngestionStatus.Duplicate));
        Assert.Equal("\"rejected\"", ContractJson.Serialize(EventIngestionStatus.Rejected));
        Assert.Equal("\"generated\"", ContractJson.Serialize(DialogueGenerationStatus.Generated));
        Assert.Equal("\"passthrough\"", ContractJson.Serialize(DialogueGenerationStatus.Passthrough));
        Assert.Equal("\"skipped\"", ContractJson.Serialize(DialogueGenerationStatus.Skipped));
        Assert.Equal("\"failed\"", ContractJson.Serialize(DialogueGenerationStatus.Failed));
        Assert.Equal("\"public\"", ContractJson.Serialize(AudienceScope.Public));
        Assert.Equal("\"npc\"", ContractJson.Serialize(AudienceScope.Npc));
    }

    /// <summary>
    /// 未知字符串、错误大小写和整数都不是合法 wire enum，必须在反序列化边界失败。
    /// </summary>
    [Theory]
    [InlineData("\"surprised\"")]
    [InlineData("\"Generated\"")]
    [InlineData("0")]
    public void ContractJson_RejectsUnknownNonCanonicalOrIntegerEnumTokens(string enumJson)
    {
        Assert.Throws<JsonException>(
            () => ContractJson.Deserialize<DialogueGenerationStatus>(enumJson));
    }

    /// <summary>
    /// 读取有效的共享事件请求，为每个反例只改变一个变量。
    /// </summary>
    private static GameEventBatchRequest ReadEventRequest()
    {
        return ContractJson.Deserialize<GameEventBatchRequest>(
            FixtureFile.ReadAllText("event_batch.json"));
    }

    /// <summary>
    /// 从共享 fixture 的第一条合法事件生成指定大小的批次，只有 event_id 与数组长度变化。
    /// </summary>
    private static GameEventBatchRequest BuildEventRequest(int eventCount)
    {
        GameEventBatchRequest request = ReadEventRequest();
        GameEvent template = request.Events[0];
        request.Events = Enumerable.Range(0, eventCount)
            .Select(
                index => new GameEvent
                {
                    EventId = $"event-contract-limit-{index:D2}",
                    EventType = template.EventType,
                    EventVersion = template.EventVersion,
                    OccurredDayIndex = template.OccurredDayIndex,
                    Source = template.Source,
                    AudienceScope = template.AudienceScope,
                    AudienceNpcId = template.AudienceNpcId,
                    Payload = template.Payload.Clone(),
                })
            .ToList();
        return request;
    }

    /// <summary>
    /// 读取有效的共享生成请求，为样本与空白测试提供稳定基线。
    /// </summary>
    private static DialogueGenerationBatchRequest ReadDialogueRequest()
    {
        return ContractJson.Deserialize<DialogueGenerationBatchRequest>(
            FixtureFile.ReadAllText("dialogue_batch.json"));
    }

    /// <summary>
    /// 读取有效共享生成响应，为终态文本与 null 集合项测试提供稳定基线。
    /// </summary>
    private static DialogueGenerationBatchResponse ReadDialogueResponse()
    {
        return ContractJson.Deserialize<DialogueGenerationBatchResponse>(
            FixtureFile.ReadAllText("dialogue_batch_response.json"));
    }

    /// <summary>
    /// 构造一个字段完整的事件响应，供水位语义测试只替换目标日索引。
    /// </summary>
    private static GameEventBatchResponse BuildEventResponse()
    {
        return new GameEventBatchResponse
        {
            SchemaVersion = ContractVersions.V1,
            RequestId = "request-event-response-boundary",
            MemoryRevision = 1,
            CommittedThroughDayIndex = 0,
            Items = new List<GameEventItemResult>
            {
                new()
                {
                    EventId = "event-response-boundary",
                    Status = EventIngestionStatus.Accepted,
                    ReasonCode = null,
                },
            },
        };
    }

    /// <summary>
    /// 生成指定数量、内容均合法的风格样本。
    /// </summary>
    private static List<string> BuildStyleExamples(int count)
    {
        return Enumerable.Range(1, count)
            .Select(index => $"风格样本 {index}")
            .ToList();
    }
}
