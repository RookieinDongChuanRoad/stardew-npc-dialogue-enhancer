using StardewNpcAgent.Application;

namespace StardewNpcAgent.Tests.Application;

/// <summary>
/// 冻结每日生成 cohort 的稳定分片算法。
/// </summary>
/// <remarks>
/// 分片只由已经完成兼容策略解析的 enabled NPC 顺序决定。本测试刻意使用普通字符串，
/// 证明 planner 不读取好感、memory 或其他会把同一配置重排的游戏状态。
/// </remarks>
public sealed class DailyGenerationBatchPlannerTests
{
    /// <summary>
    /// 0～12 项的输出形状属于已批准的运行合同；13 项必须 fail closed。
    /// </summary>
    [Theory]
    [InlineData(0, new int[0])]
    [InlineData(1, new[] { 1 })]
    [InlineData(2, new[] { 2 })]
    [InlineData(3, new[] { 3 })]
    [InlineData(4, new[] { 4 })]
    [InlineData(5, new[] { 5 })]
    [InlineData(6, new[] { 6 })]
    [InlineData(7, new[] { 7 })]
    [InlineData(8, new[] { 8 })]
    [InlineData(9, new[] { 5, 4 })]
    [InlineData(10, new[] { 5, 5 })]
    [InlineData(11, new[] { 6, 5 })]
    [InlineData(12, new[] { 6, 6 })]
    public void Plan_UsesFrozenBalancedShapes(int itemCount, int[] expectedBatchSizes)
    {
        string[] orderedIds = Enumerable.Range(0, itemCount)
            .Select(index => $"npc-{index:00}")
            .ToArray();

        IReadOnlyList<IReadOnlyList<string>> batches = DailyGenerationBatchPlanner.Plan(orderedIds);

        Assert.Equal(expectedBatchSizes, batches.Select(batch => batch.Count));
        Assert.Equal(orderedIds, batches.SelectMany(batch => batch));
    }

    /// <summary>
    /// planner 必须复制输入，避免调用方在批次启动后修改原 List 并改变已冻结顺序。
    /// </summary>
    [Fact]
    public void Plan_CopiesResolvedEnabledOrderBeforeReturning()
    {
        List<string> resolvedEnabledIds = Enumerable.Range(0, 12)
            .Select(index => $"npc-{index:00}")
            .ToList();

        IReadOnlyList<IReadOnlyList<string>> batches = DailyGenerationBatchPlanner.Plan(
            resolvedEnabledIds);
        resolvedEnabledIds.Reverse();

        Assert.Equal(
            Enumerable.Range(0, 12).Select(index => $"npc-{index:00}"),
            batches.SelectMany(batch => batch));
    }

    /// <summary>
    /// 当前产品只支持原版十二名可婚 NPC；意外传入第十三项不能被静默截断。
    /// </summary>
    [Fact]
    public void Plan_RejectsThirteenItemsWithoutPartialPlan()
    {
        string[] ids = Enumerable.Range(0, 13).Select(index => $"npc-{index:00}").ToArray();

        Assert.Throws<ArgumentOutOfRangeException>(() => DailyGenerationBatchPlanner.Plan(ids));
    }
}
