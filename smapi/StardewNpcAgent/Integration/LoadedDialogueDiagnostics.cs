using StardewNpcAgent.Game;

namespace StardewNpcAgent.Integration;

/// <summary>
/// 为已加载栈路径生成不含自由文本和敏感运行时身份的稳定诊断消息。
/// </summary>
/// <remarks>
/// 输出只允许 NPC 内部 ID 和枚举原因。不得加入台词原文、增强文本、Prompt、工具参数、
/// save/player ID、文件路径或异常消息，避免日志成为隐式的数据泄露通道。
/// </remarks>
public static class LoadedDialogueDiagnostics
{
    /// <summary>
    /// 格式化 DayStarted 捕获结果。
    /// </summary>
    /// <param name="npcId">配置中的稳定 NPC 内部 ID。</param>
    /// <param name="reasonCode">栈捕获策略的稳定结果码。</param>
    /// <returns>单行、无自由正文的诊断字符串。</returns>
    public static string FormatCapture(
        string npcId,
        LoadedDialogueStackReasonCode reasonCode)
    {
        return $"loaded_dialogue_capture npc={npcId}, result={reasonCode}.";
    }

    /// <summary>
    /// 格式化 Agent 结果回到主线程后的应用结果。
    /// </summary>
    /// <param name="npcId">配置中的稳定 NPC 内部 ID。</param>
    /// <param name="result">直接注入协调器的确定性结果。</param>
    /// <returns>单行、无 source/generated 文本的诊断字符串。</returns>
    public static string FormatApply(
        string npcId,
        LoadedDialogueApplyResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return $"loaded_dialogue_apply npc={npcId}, result={result.ReasonCode}, "
            + $"target={result.TargetReasonCode?.ToString() ?? "n/a"}.";
    }
}
