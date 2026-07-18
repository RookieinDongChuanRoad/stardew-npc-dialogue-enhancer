using System.Text.Json;
using System.Text.Json.Nodes;
using StardewNpcAgent.Contracts;

namespace StardewNpcAgent.Tests.Contracts;

/// <summary>
/// 从 raw JSON 入口验证所有公开 Int32 wire 字段，不能只测试手工构造 DTO。
/// </summary>
/// <remarks>
/// CLR 属性使用 <see cref="int"/>，因此恰好位于 Int32 边界的 JSON number 必须可
/// 解析，而越过任一边界一个单位时必须由 <see cref="ContractJson"/> 抛出
/// <see cref="JsonException"/>，不能截断、回绕或延迟到业务校验。
/// </remarks>
public sealed class WireIntegerBoundaryTests
{
    /// <summary>
    /// 事件日接受 Int32 最大值，并拒绝 max+1。
    /// </summary>
    [Fact]
    public void EventOccurredDay_UsesExactInt32JsonRange()
    {
        JsonObject legal = ReadObjectFixture("event_batch.json");
        legal["events"]![0]!["occurred_day_index"] = int.MaxValue;
        GameEventBatchRequest parsed =
            ContractJson.Deserialize<GameEventBatchRequest>(legal.ToJsonString());

        JsonObject overflow = ReadObjectFixture("event_batch.json");
        overflow["events"]![0]!["occurred_day_index"] = (long)int.MaxValue + 1;

        Assert.Equal(int.MaxValue, parsed.Events[0].OccurredDayIndex);
        Assert.Throws<JsonException>(
            () => ContractJson.Deserialize<GameEventBatchRequest>(overflow.ToJsonString()));
    }

    /// <summary>
    /// 生成日和 required revision 都接受 Int32 最大值，并拒绝 max+1。
    /// </summary>
    [Fact]
    public void DialogueDayAndRevision_UseExactInt32JsonRange()
    {
        JsonObject legal = ReadObjectFixture("dialogue_batch.json");
        legal["game_day_index"] = int.MaxValue;
        legal["required_memory_revision"] = int.MaxValue;
        DialogueGenerationBatchRequest parsed =
            ContractJson.Deserialize<DialogueGenerationBatchRequest>(legal.ToJsonString());

        Assert.Equal(int.MaxValue, parsed.GameDayIndex);
        Assert.Equal(int.MaxValue, parsed.RequiredMemoryRevision);

        foreach (string fieldName in new[] { "game_day_index", "required_memory_revision" })
        {
            JsonObject overflow = ReadObjectFixture("dialogue_batch.json");
            overflow[fieldName] = (long)int.MaxValue + 1;
            Assert.Throws<JsonException>(
                () => ContractJson.Deserialize<DialogueGenerationBatchRequest>(overflow.ToJsonString()));
        }
    }

    /// <summary>
    /// 好感点使用完整 Int32 范围，两个合法端点都必须保持原值。
    /// </summary>
    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void FriendshipPoints_AcceptExactInt32JsonBoundaries(int boundary)
    {
        JsonObject root = ReadObjectFixture("dialogue_batch.json");
        root["items"]![0]!["relationship_snapshot"]!["friendship_points"] = boundary;

        DialogueGenerationBatchRequest parsed =
            ContractJson.Deserialize<DialogueGenerationBatchRequest>(root.ToJsonString());

        Assert.Equal(boundary, parsed.Items[0].RelationshipSnapshot.FriendshipPoints);
    }

    /// <summary>
    /// 好感点比 Int32 下限更小或比上限更大时都必须在 raw JSON 解析阶段失败。
    /// </summary>
    [Theory]
    [InlineData((long)int.MinValue - 1)]
    [InlineData((long)int.MaxValue + 1)]
    public void FriendshipPoints_RejectValuesOutsideInt32JsonRange(long invalidValue)
    {
        JsonObject root = ReadObjectFixture("dialogue_batch.json");
        root["items"]![0]!["relationship_snapshot"]!["friendship_points"] = invalidValue;

        Assert.Throws<JsonException>(
            () => ContractJson.Deserialize<DialogueGenerationBatchRequest>(root.ToJsonString()));
    }

    /// <summary>
    /// 展示日接受 Int32 最大值，并拒绝 max+1。
    /// </summary>
    [Fact]
    public void DisplayedDay_UsesExactInt32JsonRange()
    {
        DisplayAckRequest parsed = ContractJson.Deserialize<DisplayAckRequest>(
            BuildDisplayAckJson(int.MaxValue));

        Assert.Equal(int.MaxValue, parsed.DisplayedDayIndex);
        Assert.Throws<JsonException>(
            () => ContractJson.Deserialize<DisplayAckRequest>(
                BuildDisplayAckJson((long)int.MaxValue + 1)));
    }

    /// <summary>
    /// JSON ``true`` 不能被任何目标 Int32 字段解释为数字 1。
    /// </summary>
    [Fact]
    public void PublicWireIntegerFields_RejectBooleanJsonTokens()
    {
        JsonObject eventRoot = ReadObjectFixture("event_batch.json");
        eventRoot["events"]![0]!["occurred_day_index"] = true;
        Assert.Throws<JsonException>(
            () => ContractJson.Deserialize<GameEventBatchRequest>(eventRoot.ToJsonString()));

        foreach (string fieldName in new[] { "game_day_index", "required_memory_revision" })
        {
            JsonObject dialogueRoot = ReadObjectFixture("dialogue_batch.json");
            dialogueRoot[fieldName] = true;
            Assert.Throws<JsonException>(
                () => ContractJson.Deserialize<DialogueGenerationBatchRequest>(
                    dialogueRoot.ToJsonString()));
        }

        JsonObject friendshipRoot = ReadObjectFixture("dialogue_batch.json");
        friendshipRoot["items"]![0]!["relationship_snapshot"]!["friendship_points"] = true;
        Assert.Throws<JsonException>(
            () => ContractJson.Deserialize<DialogueGenerationBatchRequest>(
                friendshipRoot.ToJsonString()));

        const string booleanAck =
            "{"
            + "\"schema_version\":\"1.0\","
            + "\"request_id\":\"request-wire-bool\","
            + "\"save_id\":\"save-wire\","
            + "\"player_id\":\"player-wire\","
            + "\"display_receipt_id\":\"receipt-wire-bool\","
            + "\"displayed_day_index\":true,"
            + "\"npc_id\":\"Abigail\","
            + "\"source_hash\":\"sha256:wire-bool\""
            + "}";
        Assert.Throws<JsonException>(
            () => ContractJson.Deserialize<DisplayAckRequest>(booleanAck));
    }

    /// <summary>
    /// 数学上等于整数的 decimal/exponent token 仍不是 v1 严格 integer token。
    /// </summary>
    /// <remarks>
    /// 标准 JSON Schema 会把 13.0 视为 integer；游戏侧必须在 DTO 实例化前执行共享
    /// Schema 的项目扩展语义，并给出可定位字段路径，而不是依赖 serializer 偶然行为。
    /// </remarks>
    [Theory]
    [InlineData("13.0")]
    [InlineData("1.3e1")]
    public void EventOccurredDay_RejectsIntegralDecimalAndExponentTokens(string invalidToken)
    {
        string json = FixtureFile.ReadAllText("event_batch.json").Replace(
            "\"occurred_day_index\": 13",
            $"\"occurred_day_index\": {invalidToken}",
            StringComparison.Ordinal);

        JsonException exception = Assert.Throws<JsonException>(
            () => ContractJson.Deserialize<GameEventBatchRequest>(json));

        Assert.Contains("$.events[0].occurred_day_index", exception.Message);
        Assert.Contains("JSON integer token", exception.Message);
    }

    /// <summary>
    /// 每日生成请求中的日、revision 与好感点使用同一个严格整数词法规则。
    /// </summary>
    [Theory]
    [InlineData("\"game_day_index\": 14", "\"game_day_index\": 14.0", "$.game_day_index")]
    [InlineData("\"game_day_index\": 14", "\"game_day_index\": 1.4e1", "$.game_day_index")]
    [InlineData(
        "\"required_memory_revision\": 42",
        "\"required_memory_revision\": 42.0",
        "$.required_memory_revision")]
    [InlineData(
        "\"required_memory_revision\": 42",
        "\"required_memory_revision\": 4.2e1",
        "$.required_memory_revision")]
    [InlineData(
        "\"friendship_points\": 750",
        "\"friendship_points\": 750.0",
        "$.items[0].relationship_snapshot.friendship_points")]
    [InlineData(
        "\"friendship_points\": 750",
        "\"friendship_points\": 7.5e2",
        "$.items[0].relationship_snapshot.friendship_points")]
    public void DialogueIntegers_RejectIntegralDecimalAndExponentTokens(
        string originalFragment,
        string invalidFragment,
        string expectedPath)
    {
        string json = FixtureFile.ReadAllText("dialogue_batch.json").Replace(
            originalFragment,
            invalidFragment,
            StringComparison.Ordinal);

        JsonException exception = Assert.Throws<JsonException>(
            () => ContractJson.Deserialize<DialogueGenerationBatchRequest>(json));

        Assert.Contains(expectedPath, exception.Message);
        Assert.Contains("JSON integer token", exception.Message);
    }

    /// <summary>
    /// displayed ACK 的日索引同样拒绝 decimal/exponent 词法。
    /// </summary>
    [Theory]
    [InlineData("14.0")]
    [InlineData("1.4e1")]
    public void DisplayedDay_RejectsIntegralDecimalAndExponentTokens(string invalidToken)
    {
        const string template =
            "{"
            + "\"schema_version\":\"1.0\","
            + "\"request_id\":\"request-wire-token\","
            + "\"save_id\":\"save-wire\","
            + "\"player_id\":\"player-wire\","
            + "\"display_receipt_id\":\"receipt-wire-token\","
            + "\"displayed_day_index\":TOKEN,"
            + "\"npc_id\":\"Abigail\","
            + "\"source_hash\":\"sha256:wire-token\""
            + "}";
        string json = template.Replace("TOKEN", invalidToken, StringComparison.Ordinal);

        JsonException exception = Assert.Throws<JsonException>(
            () => ContractJson.Deserialize<DisplayAckRequest>(json));

        Assert.Contains("$.displayed_day_index", exception.Message);
        Assert.Contains("JSON integer token", exception.Message);
    }

    /// <summary>
    /// 后端响应中的 revision 与 committed day 也必须遵守相同整数 token 规则。
    /// </summary>
    [Theory]
    [InlineData(
        "\"memory_revision\":1",
        "\"memory_revision\":1.0",
        "$.memory_revision")]
    [InlineData(
        "\"memory_revision\":1",
        "\"memory_revision\":1e0",
        "$.memory_revision")]
    [InlineData(
        "\"committed_through_day_index\":14",
        "\"committed_through_day_index\":14.0",
        "$.committed_through_day_index")]
    [InlineData(
        "\"committed_through_day_index\":14",
        "\"committed_through_day_index\":1.4e1",
        "$.committed_through_day_index")]
    public void EventResponseWatermarks_RejectIntegralDecimalAndExponentTokens(
        string originalFragment,
        string invalidFragment,
        string expectedPath)
    {
        string json = BuildEventResponseJson(14).Replace(
            originalFragment,
            invalidFragment,
            StringComparison.Ordinal);

        JsonException exception = Assert.Throws<JsonException>(
            () => ContractJson.Deserialize<GameEventBatchResponse>(json));

        Assert.Contains(expectedPath, exception.Message);
        Assert.Contains("JSON integer token", exception.Message);
    }

    /// <summary>
    /// 响应水位 -1/max 可由 raw JSON 解析；越过 Int32 任一端点必须立即失败。
    /// </summary>
    [Fact]
    public void CommittedThroughDay_UsesExactInt32JsonRange()
    {
        GameEventBatchResponse emptyPartition = ContractJson.Deserialize<GameEventBatchResponse>(
            BuildEventResponseJson(-1));
        GameEventBatchResponse maximum = ContractJson.Deserialize<GameEventBatchResponse>(
            BuildEventResponseJson(int.MaxValue));

        Assert.Equal(-1, emptyPartition.CommittedThroughDayIndex);
        Assert.Equal(int.MaxValue, maximum.CommittedThroughDayIndex);
        Assert.Throws<JsonException>(
            () => ContractJson.Deserialize<GameEventBatchResponse>(
                BuildEventResponseJson((long)int.MinValue - 1)));
        Assert.Throws<JsonException>(
            () => ContractJson.Deserialize<GameEventBatchResponse>(
                BuildEventResponseJson((long)int.MaxValue + 1)));
    }

    /// <summary>
    /// 读取共享 fixture 的 object 根；测试只替换一个目标整数。
    /// </summary>
    private static JsonObject ReadObjectFixture(string fixtureName)
    {
        JsonNode? node = JsonNode.Parse(FixtureFile.ReadAllText(fixtureName));
        return Assert.IsType<JsonObject>(node);
    }

    /// <summary>
    /// 构造没有独立 fixture 的最小展示 ACK raw JSON。
    /// </summary>
    private static string BuildDisplayAckJson(long displayedDayIndex)
    {
        JsonObject root = new()
        {
            ["schema_version"] = "1.0",
            ["request_id"] = "request-wire-ack",
            ["save_id"] = "save-wire",
            ["player_id"] = "player-wire",
            ["display_receipt_id"] = "receipt-wire-ack",
            ["displayed_day_index"] = displayedDayIndex,
            ["npc_id"] = "Abigail",
            ["source_hash"] = "sha256:wire-ack",
        };
        return root.ToJsonString();
    }

    /// <summary>
    /// 构造带一个合法 item 的事件响应，仅将 committed day 作为 long 写入 raw JSON。
    /// </summary>
    private static string BuildEventResponseJson(long committedDayIndex)
    {
        JsonObject root = new()
        {
            ["schema_version"] = "1.0",
            ["request_id"] = "request-wire-response",
            ["memory_revision"] = 1,
            ["committed_through_day_index"] = committedDayIndex,
            ["items"] = new JsonArray(
                new JsonObject
                {
                    ["event_id"] = "event-wire-response",
                    ["status"] = "accepted",
                    ["reason_code"] = null,
                }),
        };
        return root.ToJsonString();
    }
}
