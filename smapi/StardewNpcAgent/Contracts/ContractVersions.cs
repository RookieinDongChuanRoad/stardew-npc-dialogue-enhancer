namespace StardewNpcAgent.Contracts;

/// <summary>
/// 集中保存游戏 Mod 与后端共同支持的 wire contract 版本。
/// </summary>
/// <remarks>
/// 版本常量只描述传输协议，不等同于 Mod 或程序集版本。若未来升级协议，必须先更新
/// 根目录 JSON Schema、共享 fixture 与两端 DTO，再新增对应常量，不能静默改写 V1。
/// </remarks>
public static class ContractVersions
{
    /// <summary>
    /// 当前冻结的第一版 JSON wire contract。
    /// </summary>
    public const string V1 = "1.0";
}
