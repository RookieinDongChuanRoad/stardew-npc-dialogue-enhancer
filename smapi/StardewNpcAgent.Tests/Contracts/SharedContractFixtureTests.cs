using System.Text.Json;
using StardewNpcAgent.Contracts;
using StardewNpcAgent.Game;

namespace StardewNpcAgent.Tests.Contracts;

/// <summary>
/// 证明 C# 能消费仓库根目录的共享 fixture，并无损保持 registry 与 wire 语义。
/// </summary>
public sealed class SharedContractFixtureTests
{
    /// <summary>
    /// 十二人 fixture 是跨语言 registry 的唯一有序合同；名称和顺序变化都必须显式更新版本。
    /// </summary>
    [Fact]
    public void VanillaMarriageableNpcFixture_ContainsExactVersionedOrder()
    {
        string fixtureJson = FixtureFile.ReadAllText("vanilla_marriageable_npcs.json");
        using JsonDocument document = JsonDocument.Parse(fixtureJson);

        JsonElement root = document.RootElement;
        Assert.Equal(
            "vanilla-marriageable-npcs-v1",
            root.GetProperty("schema_version").GetString());
        string[] fixtureIds = root.GetProperty("npc_ids")
            .EnumerateArray()
            .Select(element => element.GetString()
                ?? throw new InvalidOperationException("十二人 fixture 的 npc_ids 不允许 null。"))
            .ToArray();
        Assert.Equal(
            new[]
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
            },
            fixtureIds);
        Assert.Equal(VanillaMarriageableNpcRegistry.AllIds, fixtureIds);
    }

    /// <summary>
    /// 事件批次必须保留 NPC 定向事件与 public 事件的受众语义。
    /// </summary>
    [Fact]
    public void EventBatchFixture_DeserializesAndRoundTripsSemantically()
    {
        string fixtureJson = FixtureFile.ReadAllText("event_batch.json");

        GameEventBatchRequest request = ContractJson.Deserialize<GameEventBatchRequest>(fixtureJson);
        ContractValidationResult validation = ContractValidator.Validate(request);
        string roundTrippedJson = ContractJson.Serialize(request);

        Assert.True(validation.IsValid, validation.ToString());
        Assert.Equal("Abigail", request.Events[0].AudienceNpcId);
        Assert.Equal(AudienceScope.Npc, request.Events[0].AudienceScope);
        Assert.Equal(AudienceScope.Public, request.Events[1].AudienceScope);
        Assert.Null(request.Events[1].AudienceNpcId);
        Assert.Equal(CanonicalJson.Normalize(fixtureJson), CanonicalJson.Normalize(roundTrippedJson));
    }

    /// <summary>
    /// 每日生成请求必须保留两个稳定 NPC ID、locale 以及动态上下文字段。
    /// </summary>
    [Fact]
    public void DialogueBatchFixture_DeserializesAndRoundTripsSemantically()
    {
        string fixtureJson = FixtureFile.ReadAllText("dialogue_batch.json");

        DialogueGenerationBatchRequest request = ContractJson.Deserialize<DialogueGenerationBatchRequest>(fixtureJson);
        ContractValidationResult validation = ContractValidator.Validate(request);
        string roundTrippedJson = ContractJson.Serialize(request);

        Assert.True(validation.IsValid, validation.ToString());
        Assert.Equal("zh-CN", request.StableDayContext.Locale);
        Assert.Collection(
            request.Items,
            item => Assert.Equal("Abigail", item.NpcId),
            item => Assert.Equal("Sebastian", item.NpcId));
        Assert.Equal(CanonicalJson.Normalize(fixtureJson), CanonicalJson.Normalize(roundTrippedJson));
    }

    /// <summary>
    /// 响应 fixture 必须显式覆盖 generated 与正常成功态 passthrough。
    /// </summary>
    [Fact]
    public void DialogueBatchResponseFixture_DeserializesTerminalStatusesAndRoundTripsSemantically()
    {
        string fixtureJson = FixtureFile.ReadAllText("dialogue_batch_response.json");

        DialogueGenerationBatchResponse response = ContractJson.Deserialize<DialogueGenerationBatchResponse>(fixtureJson);
        ContractValidationResult validation = ContractValidator.Validate(response);
        string roundTrippedJson = ContractJson.Serialize(response);

        Assert.True(validation.IsValid, validation.ToString());
        Assert.Collection(
            response.Items,
            item => Assert.Equal(DialogueGenerationStatus.Generated, item.Status),
            item => Assert.Equal(DialogueGenerationStatus.Passthrough, item.Status));
        Assert.Equal(CanonicalJson.Normalize(fixtureJson), CanonicalJson.Normalize(roundTrippedJson));
    }
}
