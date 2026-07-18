namespace StardewNpcAgent.Application;

/// <summary>
/// 把 compatibility policy 已解析的有序 NPC 输入切成最多两个稳定生成批次。
/// </summary>
/// <remarks>
/// 本类只依据输入位置分片，不读取关系、记忆、天气或预计生成价值。这样同一份用户配置
/// 每天都会得到相同的 batch 边界，也不会因为 HashSet 枚举顺序改变 request identity。
/// </remarks>
public static class DailyGenerationBatchPlanner
{
    /// <summary>
    /// 当前原版可婚 NPC 总数，也是本阶段允许进入一个 cohort 的硬上限。
    /// </summary>
    public const int MaximumCohortSize = 12;

    /// <summary>
    /// 复制输入并按冻结规则返回零个、一个或两个批次。
    /// </summary>
    /// <typeparam name="T">被分片的值类型；planner 不检查或解释值内容。</typeparam>
    /// <param name="orderedItems">兼容策略已经解析并保留用户顺序的只读输入。</param>
    /// <returns>
    /// 0 项返回空；1～8 项返回单批；9～12 项按前半组不小于后半组的方式平衡切分。
    /// 每个返回批次都是独立数组，不受调用方随后修改原集合影响。
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="orderedItems"/> 为 null。</exception>
    /// <exception cref="ArgumentOutOfRangeException">输入超过原版十二人边界。</exception>
    public static IReadOnlyList<IReadOnlyList<T>> Plan<T>(IReadOnlyList<T> orderedItems)
    {
        ArgumentNullException.ThrowIfNull(orderedItems);
        if (orderedItems.Count > MaximumCohortSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(orderedItems),
                orderedItems.Count,
                "每日生成 cohort 不能超过原版十二名可婚 NPC。");
        }

        if (orderedItems.Count == 0)
        {
            return Array.Empty<IReadOnlyList<T>>();
        }

        if (orderedItems.Count <= 8)
        {
            return new IReadOnlyList<T>[] { orderedItems.ToArray() };
        }

        int firstBatchSize = (orderedItems.Count + 1) / 2;
        T[] firstBatch = orderedItems.Take(firstBatchSize).ToArray();
        T[] secondBatch = orderedItems.Skip(firstBatchSize).ToArray();
        return new IReadOnlyList<T>[] { firstBatch, secondBatch };
    }
}
