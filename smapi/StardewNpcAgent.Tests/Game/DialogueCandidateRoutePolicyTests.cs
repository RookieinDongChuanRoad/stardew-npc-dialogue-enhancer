using StardewNpcAgent.Game;

namespace StardewNpcAgent.Tests.Game;

/// <summary>
/// 冻结 DayStarted 候选入口的顶层路由，避免先用 pure resolver 猜来源再验证 loaded stack。
/// </summary>
public sealed class DialogueCandidateRoutePolicyTests
{
    /// <summary>
    /// loaded stack 是 ordinary/rainy 的唯一主路径；无栈时只保留两个旧 NPC 的 dry fallback。
    /// GreenRain 比 loaded 与普通雨优先，始终原版回退。
    /// </summary>
    [Theory]
    [InlineData("Alex", true, false, false, "AuthoritativeLoadedSource", "AuthoritativeLoadedSource")]
    [InlineData("Alex", true, true, false, "AuthoritativeLoadedSource", "AuthoritativeLoadedSource")]
    [InlineData("Abigail", false, false, false, "LegacyPureOrdinaryFallback", "LegacyPureOrdinaryFallback")]
    [InlineData("Sebastian", false, false, false, "LegacyPureOrdinaryFallback", "LegacyPureOrdinaryFallback")]
    [InlineData("Abigail", false, true, false, "OriginalDialogueFallback", "NoAuthoritativeDailySource")]
    [InlineData("Sebastian", false, true, false, "OriginalDialogueFallback", "NoAuthoritativeDailySource")]
    [InlineData("Alex", false, false, false, "OriginalDialogueFallback", "NoLegacyPureFallback")]
    [InlineData("Shane", false, false, false, "OriginalDialogueFallback", "NoLegacyPureFallback")]
    [InlineData("Abigail", true, true, true, "OriginalDialogueFallback", "GreenRain")]
    [InlineData("Alex", true, false, true, "OriginalDialogueFallback", "GreenRain")]
    public void Select_UsesLoadedFirstAndNeverGuessesRainyWithoutAStack(
        string npcId,
        bool hasLoadedStack,
        bool isRaining,
        bool isGreenRaining,
        string expectedRoute,
        string expectedReason)
    {
        DialogueCandidateRouteDecision decision = DialogueCandidateRoutePolicy.Select(
            npcId,
            hasLoadedStack,
            isRaining,
            isGreenRaining);

        Assert.Equal(expectedRoute, decision.Route.ToString());
        Assert.Equal(expectedReason, decision.ReasonCode.ToString());
    }
}
