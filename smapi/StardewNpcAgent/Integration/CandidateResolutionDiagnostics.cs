using StardewNpcAgent.Game;

namespace StardewNpcAgent.Integration;

/// <summary>
/// 为正式模式候选解析生成不含台词、Prompt 或凭据的稳定诊断消息。
/// </summary>
/// <remarks>
/// `cached=0` 是合法 fallback，但单独记录计数无法判断是婚姻、天气、特殊事件还是内容形状
/// 门禁生效。该 formatter 只输出 NPC 稳定 ID 与两个枚举 reason，既能支持真实游戏验收，
/// 又不会把原文或模型审计内容写入 SMAPI 日志。
/// </remarks>
internal static class CandidateResolutionDiagnostics
{
    /// <summary>
    /// 把单个目标 NPC 的解析终态格式化为稳定、低敏感度的 Trace 日志。
    /// </summary>
    /// <param name="npcId">游戏提供的稳定 NPC 内部 ID。</param>
    /// <param name="resolution">候选解析器返回的阶段终态与可选资格原因。</param>
    /// <returns>不包含自由文本的单行诊断消息。</returns>
    public static string Format(
        string npcId,
        DialogueCandidateResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(npcId);
        ArgumentNullException.ThrowIfNull(resolution);

        return $"正式候选解析 npc={npcId}, result={resolution.ReasonCode}, "
            + $"eligibility={resolution.EligibilityReasonCode?.ToString() ?? "n/a"}。";
    }
}
