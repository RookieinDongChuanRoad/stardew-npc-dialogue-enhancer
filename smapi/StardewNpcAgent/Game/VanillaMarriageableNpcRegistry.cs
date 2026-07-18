namespace StardewNpcAgent.Game;

/// <summary>
/// 本 Mod 已完成人格、来源与安全边界审查的原版十二名可婚 NPC registry。
/// </summary>
/// <remarks>
/// 这里的“支持”是产品审核合同，不等价于游戏运行时的 <c>CanBeRomanced</c>。固定 registry 能避免
/// 第三方 Mod NPC 或未来游戏数据变化在没有 profile、测试与兼容审计时被动态扩入 Agent 路径。
/// </remarks>
public static class VanillaMarriageableNpcRegistry
{
    /// <summary>
    /// canonical 顺序同时用于新配置默认值和后续稳定分片；数组始终保持私有，防止调用方取得可变引用。
    /// </summary>
    private static readonly string[] OrderedIds =
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
    };

    /// <summary>
    /// 取得固定顺序的只读支持集合。
    /// </summary>
    public static IReadOnlyList<string> AllIds { get; } = Array.AsReadOnly(OrderedIds);

    /// <summary>
    /// 以精确 ordinal 语义判断 NPC ID 是否属于已审查支持集。
    /// </summary>
    /// <param name="npcId">Stardew 稳定内部 NPC ID；null 永远不受支持。</param>
    /// <returns>仅当 ID 与 canonical 项完全一致时为 <c>true</c>。</returns>
    public static bool Contains(string? npcId)
    {
        return npcId is not null
            && Array.IndexOf(OrderedIds, npcId) >= 0;
    }
}
