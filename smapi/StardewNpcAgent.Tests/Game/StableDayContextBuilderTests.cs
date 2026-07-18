using System.Text.Json;
using StardewNpcAgent.Contracts;
using StardewNpcAgent.Game;

namespace StardewNpcAgent.Tests.Game;

/// <summary>
/// 验证每天生成期间冻结的天气与 progression JSON 是确定性游戏事实。
/// </summary>
public sealed class StableDayContextBuilderTests
{
    [Theory]
    [InlineData(true, true, true, true, "green_rain")]
    [InlineData(false, true, true, true, "rain")]
    [InlineData(false, false, true, true, "snow")]
    [InlineData(false, false, false, true, "wind")]
    [InlineData(false, false, false, false, "sunny")]
    public void Build_UsesStableWeatherPrecedence(
        bool greenRain,
        bool rain,
        bool snow,
        bool debris,
        string expectedWeather)
    {
        StableDayContext context = StableDayContextBuilder.Build(
            CreateFacts(greenRain, rain, snow, debris));

        Assert.Equal(expectedWeather, context.Weather);
        Assert.Equal("fall", context.Season);
        Assert.Equal("zh-CN", context.Locale);
        Assert.True(ContractValidator.Validate(CreateRequest(context)).IsValid);
    }

    [Fact]
    public void Build_ProgressionSignalsHaveExactStableShape()
    {
        StableDayContext context = StableDayContextBuilder.Build(CreateFacts());

        Assert.Equal(JsonValueKind.Object, context.ProgressionSignals.ValueKind);
        Assert.Equal(2, context.ProgressionSignals.GetProperty("year").GetInt32());
        Assert.Equal(14, context.ProgressionSignals.GetProperty("day_of_month").GetInt32());
        Assert.Equal(80, context.ProgressionSignals.GetProperty("deepest_mine_level").GetInt32());
        Assert.Equal(
            new[] { "year", "day_of_month", "deepest_mine_level" },
            context.ProgressionSignals.EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public void Build_InvalidFactsFailBeforeProducingContractDto()
    {
        Assert.Throws<ArgumentException>(
            () => StableDayContextBuilder.Build(CreateFacts() with { Locale = " zh-CN" }));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => StableDayContextBuilder.Build(CreateFacts() with { Year = 0 }));
    }

    private static StableDayFacts CreateFacts(
        bool greenRain = false,
        bool rain = false,
        bool snow = false,
        bool debris = false)
    {
        return new StableDayFacts(
            Season: "fall",
            Locale: "zh-CN",
            IsGreenRain: greenRain,
            IsRaining: rain,
            IsSnowing: snow,
            IsDebrisWeather: debris,
            Year: 2,
            DayOfMonth: 14,
            DeepestMineLevel: 80);
    }

    private static DialogueGenerationBatchRequest CreateRequest(StableDayContext context)
    {
        return new DialogueGenerationBatchRequest
        {
            SchemaVersion = ContractVersions.V1,
            RequestId = "request-stable-day",
            SaveId = "save-stable-day",
            PlayerId = "player-stable-day",
            GameDayIndex = 42,
            RequiredMemoryRevision = 0,
            StableDayContext = context,
            Items = new List<DialogueGenerationItem>
            {
                new()
                {
                    TaskId = "task-stable-day",
                    NpcId = "Abigail",
                    SourceDialogue = new SourceDialogue
                    {
                        AssetName = "Characters/Dialogue/Abigail",
                        DialogueKey = "fall_Mon",
                        Text = "原文。",
                        SourceHash = "sha256:stable-day",
                    },
                    RelationshipSnapshot = new StardewNpcAgent.Contracts.RelationshipSnapshot
                    {
                        FriendshipPoints = 1_000,
                        RelationshipStage = "friend",
                    },
                    StyleExamples = new List<string> { "一。", "二。", "三。" },
                    MemorySignals = new List<JsonElement>(),
                },
            },
        };
    }
}
