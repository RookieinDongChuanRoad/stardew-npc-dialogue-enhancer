using System.Text.Json;
using StardewNpcAgent.Contracts;

namespace StardewNpcAgent.Tests.Contracts;

/// <summary>
/// 直接读取测试输出中的共享 JSON Schema，防止 C# 资源上限与跨语言真值静默漂移。
/// </summary>
public sealed class SharedSchemaAlignmentTests
{
    /// <summary>
    /// GameEventBatch 的 Schema maxItems 必须与 C# 生产常量完全一致。
    /// </summary>
    [Fact]
    public void GameEventBatchSchema_MaxItemsMatchesCSharpContractLimit()
    {
        string schemaPath = Path.Combine(
            AppContext.BaseDirectory,
            "Contracts",
            "Schemas",
            "game_event_batch.schema.json");
        Assert.True(File.Exists(schemaPath), $"测试输出缺少共享 Schema：{schemaPath}");

        using JsonDocument schema = JsonDocument.Parse(File.ReadAllText(schemaPath));
        int schemaMaximum = schema.RootElement
            .GetProperty("properties")
            .GetProperty("events")
            .GetProperty("maxItems")
            .GetInt32();

        Assert.Equal(ContractLimits.MaximumEventsPerBatch, schemaMaximum);
    }

    /// <summary>
    /// 生产常量必须精确声明 C# ``int`` 的两个端点，不能另造一个近似范围。
    /// </summary>
    [Fact]
    public void WireIntegerContractLimits_AreExactInt32Boundaries()
    {
        Assert.Equal(int.MinValue, ContractLimits.MinimumWireInteger);
        Assert.Equal(int.MaxValue, ContractLimits.MaximumWireInteger);
    }

    /// <summary>
    /// 事件发生日的 Schema 必须与生产 Int32 上限及非负下限一致。
    /// </summary>
    [Fact]
    public void GameEventSchema_DayBoundsMatchProductionWireLimits()
    {
        using JsonDocument schema = ReadSchema("game_event_batch.schema.json");
        JsonElement field = schema.RootElement
            .GetProperty("$defs")
            .GetProperty("game_event")
            .GetProperty("properties")
            .GetProperty("occurred_day_index");

        Assert.Equal(0, field.GetProperty("minimum").GetInt32());
        Assert.Equal(
            ContractLimits.MaximumWireInteger,
            field.GetProperty("maximum").GetInt32());
    }

    /// <summary>
    /// 生成日、revision 与好感点必须分别对齐非负和完整 Int32 范围。
    /// </summary>
    [Fact]
    public void DialogueSchema_IntegerBoundsMatchProductionWireLimits()
    {
        int minimum = ContractLimits.MinimumWireInteger;
        int maximum = ContractLimits.MaximumWireInteger;
        using JsonDocument schema = ReadSchema("dialogue_generation_batch.schema.json");
        JsonElement properties = schema.RootElement.GetProperty("properties");

        foreach (string fieldName in new[] { "game_day_index", "required_memory_revision" })
        {
            JsonElement field = properties.GetProperty(fieldName);
            Assert.Equal(0, field.GetProperty("minimum").GetInt32());
            Assert.Equal(maximum, field.GetProperty("maximum").GetInt32());
        }

        JsonElement friendship = schema.RootElement
            .GetProperty("$defs")
            .GetProperty("relationship_snapshot")
            .GetProperty("properties")
            .GetProperty("friendship_points");
        Assert.Equal(minimum, friendship.GetProperty("minimum").GetInt32());
        Assert.Equal(maximum, friendship.GetProperty("maximum").GetInt32());
    }

    /// <summary>
    /// 展示 ACK 的日索引必须使用与事件和生成请求相同的非负 Int32 范围。
    /// </summary>
    [Fact]
    public void DisplayAckSchema_DayBoundsMatchProductionWireLimits()
    {
        using JsonDocument schema = ReadSchema("dialogue_display_ack.schema.json");
        JsonElement field = schema.RootElement
            .GetProperty("properties")
            .GetProperty("displayed_day_index");

        Assert.Equal(0, field.GetProperty("minimum").GetInt32());
        Assert.Equal(
            ContractLimits.MaximumWireInteger,
            field.GetProperty("maximum").GetInt32());
    }

    /// <summary>
    /// 所有公开整数都必须声明项目级严格 token 注解。
    /// </summary>
    /// <remarks>
    /// Draft 2020-12 的 integer 使用数学值语义，因此会把 13.0 视为整数。共享 Schema
    /// 通过这个扩展注解声明更严格的 wire 规则；Python 与 C# raw JSON 入口负责执行它。
    /// </remarks>
    [Fact]
    public void PublicIntegerFields_DeclareStrictJsonTokenExtension()
    {
        using JsonDocument eventSchema = ReadSchema("game_event_batch.schema.json");
        JsonElement eventDay = eventSchema.RootElement
            .GetProperty("$defs")
            .GetProperty("game_event")
            .GetProperty("properties")
            .GetProperty("occurred_day_index");

        using JsonDocument dialogueSchema = ReadSchema("dialogue_generation_batch.schema.json");
        JsonElement dialogueProperties = dialogueSchema.RootElement.GetProperty("properties");
        JsonElement friendship = dialogueSchema.RootElement
            .GetProperty("$defs")
            .GetProperty("relationship_snapshot")
            .GetProperty("properties")
            .GetProperty("friendship_points");

        using JsonDocument ackSchema = ReadSchema("dialogue_display_ack.schema.json");
        JsonElement displayedDay = ackSchema.RootElement
            .GetProperty("properties")
            .GetProperty("displayed_day_index");

        JsonElement[] fields =
        {
            eventDay,
            dialogueProperties.GetProperty("game_day_index"),
            dialogueProperties.GetProperty("required_memory_revision"),
            friendship,
            displayedDay,
        };
        Assert.All(
            fields,
            field => Assert.True(
                field.GetProperty("x-stardew-json-integer-token").GetBoolean()));
    }

    /// <summary>
    /// 从测试输出读取唯一权威 Schema；缺失文件本身就是合同复制配置失败。
    /// </summary>
    private static JsonDocument ReadSchema(string schemaName)
    {
        string schemaPath = Path.Combine(
            AppContext.BaseDirectory,
            "Contracts",
            "Schemas",
            schemaName);
        Assert.True(File.Exists(schemaPath), $"测试输出缺少共享 Schema：{schemaPath}");
        return JsonDocument.Parse(File.ReadAllText(schemaPath));
    }

}
