using StardewNpcAgent.Game;

namespace StardewNpcAgent.Tests.Game;

/// <summary>
/// 冻结本 Mod 已审查支持的原版十二名可婚 NPC registry。
/// </summary>
public sealed class VanillaMarriageableNpcRegistryTests
{
    private static readonly string[] ExpectedIds =
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
    /// AllIds 的顺序进入配置默认值与后续稳定分片，不能依赖游戏运行时枚举顺序。
    /// </summary>
    [Fact]
    public void AllIds_ContainsExactCanonicalOrderAndCannotBeMutated()
    {
        Assert.Equal(ExpectedIds, VanillaMarriageableNpcRegistry.AllIds);

        IList<string> listView = Assert.IsAssignableFrom<IList<string>>(
            VanillaMarriageableNpcRegistry.AllIds);
        Assert.Throws<NotSupportedException>(() => listView[0] = "Changed");
        Assert.Equal(ExpectedIds, VanillaMarriageableNpcRegistry.AllIds);
    }

    /// <summary>
    /// Membership 只接受 canonical ordinal ID；大小写近似、空值和 Mod NPC 都不是已审查支持项。
    /// </summary>
    [Theory]
    [InlineData("Abigail", true)]
    [InlineData("Shane", true)]
    [InlineData("abigail", false)]
    [InlineData("Krobus", false)]
    [InlineData(null, false)]
    public void Contains_UsesExactOrdinalMembership(string? npcId, bool expected)
    {
        Assert.Equal(expected, VanillaMarriageableNpcRegistry.Contains(npcId));
    }
}
