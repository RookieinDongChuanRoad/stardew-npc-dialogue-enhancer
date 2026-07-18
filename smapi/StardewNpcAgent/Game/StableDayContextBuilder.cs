using System.Text.Json;
using System.Text.Json.Serialization;
using StardewNpcAgent.Contracts;

namespace StardewNpcAgent.Game;

/// <summary>
/// 主线程在 DayStarted 冻结的、不会由后台任务再次读取的游戏事实。
/// </summary>
public sealed record StableDayFacts(
    string Season,
    string Locale,
    bool IsGreenRain,
    bool IsRaining,
    bool IsSnowing,
    bool IsDebrisWeather,
    int Year,
    int DayOfMonth,
    int DeepestMineLevel);

/// <summary>
/// 将明确游戏事实映射为共享 StableDayContext，不读取 Game1 或网络。
/// </summary>
public static class StableDayContextBuilder
{
    public static StableDayContext Build(StableDayFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ValidateStableString(facts.Season, nameof(facts.Season));
        ValidateStableString(facts.Locale, nameof(facts.Locale));
        if (facts.Year < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(facts.Year));
        }

        if (facts.DayOfMonth < 1 || facts.DayOfMonth > 28)
        {
            throw new ArgumentOutOfRangeException(nameof(facts.DayOfMonth));
        }

        if (facts.DeepestMineLevel < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(facts.DeepestMineLevel));
        }

        string weather = facts switch
        {
            { IsGreenRain: true } => "green_rain",
            { IsRaining: true } => "rain",
            { IsSnowing: true } => "snow",
            { IsDebrisWeather: true } => "wind",
            _ => "sunny",
        };
        JsonElement progressionSignals = JsonSerializer.SerializeToElement(
            new StableProgressionSignals(
                facts.Year,
                facts.DayOfMonth,
                facts.DeepestMineLevel));
        return new StableDayContext
        {
            Season = facts.Season,
            Weather = weather,
            Locale = facts.Locale,
            ProgressionSignals = progressionSignals,
        };
    }

    private static void ValidateStableString(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim())
        {
            throw new ArgumentException("值必须非空且无首尾空白。", parameterName);
        }
    }

    /// <summary>
    /// 显式属性名和声明顺序冻结 progression_signals wire shape。
    /// </summary>
    private sealed record StableProgressionSignals(
        [property: JsonPropertyName("year")] int Year,
        [property: JsonPropertyName("day_of_month")] int DayOfMonth,
        [property: JsonPropertyName("deepest_mine_level")] int DeepestMineLevel);
}
