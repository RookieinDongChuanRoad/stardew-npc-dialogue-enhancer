namespace StardewNpcAgent.Contracts;

/// <summary>
/// C# 游戏适配层与共享 wire contract 对齐的资源保护上限。
/// </summary>
/// <remarks>
/// 这些值属于协议边界，而不是可调运行时配置。事件上限用于限制 SMAPI 离线 outbox
/// 的单次刷新规模，并缩短 SQLite 单写者事务的占用时间。修改时必须同时更新根目录
/// JSON Schema、Python 合同和 C# 跨语言边界测试，不能只放宽某一端。
/// </remarks>
public static class ContractLimits
{
    /// <summary>
    /// 公开 wire integer 的最小值，即 C# <see cref="int.MinValue"/>。
    /// </summary>
    /// <remarks>
    /// Python 整数没有固定宽度，但游戏侧 DTO 使用 Int32；合同采用两端最低共同范围，
    /// 不能让 Python/Schema 接受 C# 无法解析的数字。
    /// </remarks>
    public const int MinimumWireInteger = int.MinValue;

    /// <summary>
    /// 公开 wire integer 的最大值，即 C# <see cref="int.MaxValue"/>。
    /// </summary>
    public const int MaximumWireInteger = int.MaxValue;

    /// <summary>
    /// 单个 <see cref="GameEventBatchRequest"/> 允许携带的最大事件数。
    /// </summary>
    public const int MaximumEventsPerBatch = 64;
}
