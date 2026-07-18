namespace StardewNpcAgent.Game;

/// <summary>
/// DayStarted 为单 NPC 选择的候选解析路线。
/// </summary>
public enum DialogueCandidateRoute
{
    /// <summary>从游戏已经选中的栈顶 source 捕获 authoritative snapshot。</summary>
    AuthoritativeLoadedSource,

    /// <summary>仅为旧 Abigail/Sebastian dry 无栈路径保留的 pure ordinary fallback。</summary>
    LegacyPureOrdinaryFallback,

    /// <summary>不生成候选，完整保留原版台词。</summary>
    OriginalDialogueFallback,
}

/// <summary>
/// 顶层路由的稳定诊断原因。
/// </summary>
public enum DialogueCandidateRouteReasonCode
{
    AuthoritativeLoadedSource,
    LegacyPureOrdinaryFallback,
    NoAuthoritativeDailySource,
    NoLegacyPureFallback,
    GreenRain,
}

/// <summary>
/// 纯路由 policy 的单一终态。
/// </summary>
/// <param name="Route">调用方下一步必须采用的路线。</param>
/// <param name="ReasonCode">用于日志与回归测试的稳定原因。</param>
public sealed record DialogueCandidateRouteDecision(
    DialogueCandidateRoute Route,
    DialogueCandidateRouteReasonCode ReasonCode);

/// <summary>
/// 在任何候选解析前决定 authoritative loaded、legacy pure 或原版回退路线。
/// </summary>
/// <remarks>
/// 该 policy 是“不得先猜 ordinary 再验证 loaded”的架构边界：只要原版栈已经加载，
/// 调用方就必须从该栈的 actual TranslationKey 开始；雨天无栈时不得复制或猜测原版
/// rainy/ordinary 选择链。GreenRain 尚未进入受支持来源族，因此拥有最高拒绝优先级。
/// </remarks>
public static class DialogueCandidateRoutePolicy
{
    /// <summary>
    /// 选择单 NPC 的候选解析路线。
    /// </summary>
    /// <param name="npcId">目标 NPC 的 exact 内部 ID。</param>
    /// <param name="hasLoadedStack">游戏是否已经为目标 NPC 选择并加载栈。</param>
    /// <param name="isRaining">当前是否为普通雨天。</param>
    /// <param name="isGreenRaining">当前是否为 GreenRain。</param>
    /// <returns>无副作用、稳定的路由结果。</returns>
    public static DialogueCandidateRouteDecision Select(
        string npcId,
        bool hasLoadedStack,
        bool isRaining,
        bool isGreenRaining)
    {
        if (isGreenRaining)
        {
            return new DialogueCandidateRouteDecision(
                DialogueCandidateRoute.OriginalDialogueFallback,
                DialogueCandidateRouteReasonCode.GreenRain);
        }

        if (hasLoadedStack)
        {
            return new DialogueCandidateRouteDecision(
                DialogueCandidateRoute.AuthoritativeLoadedSource,
                DialogueCandidateRouteReasonCode.AuthoritativeLoadedSource);
        }

        if (isRaining)
        {
            return new DialogueCandidateRouteDecision(
                DialogueCandidateRoute.OriginalDialogueFallback,
                DialogueCandidateRouteReasonCode.NoAuthoritativeDailySource);
        }

        if (npcId is "Abigail" or "Sebastian")
        {
            return new DialogueCandidateRouteDecision(
                DialogueCandidateRoute.LegacyPureOrdinaryFallback,
                DialogueCandidateRouteReasonCode.LegacyPureOrdinaryFallback);
        }

        return new DialogueCandidateRouteDecision(
            DialogueCandidateRoute.OriginalDialogueFallback,
            DialogueCandidateRouteReasonCode.NoLegacyPureFallback);
    }
}
