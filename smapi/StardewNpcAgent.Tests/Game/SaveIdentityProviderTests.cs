using System.Globalization;
using StardewNpcAgent.Game;

namespace StardewNpcAgent.Tests.Game;

/// <summary>
/// 验证存档/玩家身份与本地 outbox 路径稳定、隔离且不直接拼接外部字符串。
/// </summary>
public sealed class SaveIdentityProviderTests
{
    /// <summary>
    /// 相同游戏身份必须得到逐字相同 API identity 与路径；不同玩家必须分区。
    /// </summary>
    [Fact]
    public void Create_IsStableAndPlayerSensitive()
    {
        string modDirectory = Path.Combine(Path.GetTempPath(), "StardewNpcAgent.ModRoot");

        SavePartitionIdentity first = SaveIdentityProvider.Create(
            "Farm_123456789",
            99887766L,
            modDirectory);
        SavePartitionIdentity repeated = SaveIdentityProvider.Create(
            "Farm_123456789",
            99887766L,
            modDirectory);
        SavePartitionIdentity anotherPlayer = SaveIdentityProvider.Create(
            "Farm_123456789",
            99887767L,
            modDirectory);

        Assert.Equal(first, repeated);
        Assert.NotEqual(first.PlayerId, anotherPlayer.PlayerId);
        Assert.NotEqual(first.PartitionDirectory, anotherPlayer.PartitionDirectory);
        Assert.StartsWith("save-v1-", first.SaveId, StringComparison.Ordinal);
        Assert.StartsWith("player-v1-", first.PlayerId, StringComparison.Ordinal);
    }

    /// <summary>
    /// Stardew 的旧存档可能把玩家身份保存为负的有符号 64 位整数。
    /// 该数值是游戏提供的不透明稳定身份，不是数量或数组下标，因此负号不表示输入非法。
    /// Provider 必须直接对 invariant 字符串做 hash；不能取绝对值，否则正负身份会碰撞，
    /// 且 <see cref="long.MinValue"/> 无法安全取绝对值。
    /// </summary>
    [Fact]
    public void Create_NegativeLegacyMultiplayerIdProducesStableOpaquePartition()
    {
        string modDirectory = Path.Combine(Path.GetTempPath(), "StardewNpcAgent.ModRoot");
        const string saveFolderName = "LegacyFarm_123456789";
        const long legacyPlayerId = -1234567890123456789L;

        SavePartitionIdentity first = SaveIdentityProvider.Create(
            saveFolderName,
            legacyPlayerId,
            modDirectory);
        SavePartitionIdentity repeated = SaveIdentityProvider.Create(
            saveFolderName,
            legacyPlayerId,
            modDirectory);
        SavePartitionIdentity positiveCounterpart = SaveIdentityProvider.Create(
            saveFolderName,
            1234567890123456789L,
            modDirectory);
        SavePartitionIdentity minimumSignedValue = SaveIdentityProvider.Create(
            saveFolderName,
            long.MinValue,
            modDirectory);

        Assert.Equal(first, repeated);
        Assert.NotEqual(first.PlayerId, positiveCounterpart.PlayerId);
        Assert.NotEqual(first.PlayerId, minimumSignedValue.PlayerId);
        Assert.NotEqual(first.PartitionDirectory, positiveCounterpart.PartitionDirectory);
        Assert.DoesNotContain(saveFolderName, first.SaveId, StringComparison.Ordinal);
        Assert.DoesNotContain(
            legacyPlayerId.ToString(CultureInfo.InvariantCulture),
            first.PlayerId,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            legacyPlayerId.ToString(CultureInfo.InvariantCulture),
            first.PartitionDirectory,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// 原始 save folder 可能包含空格或路径字符；磁盘路径只能使用固定 hash segment。
    /// </summary>
    [Fact]
    public void Create_PathsStayUnderModDataAndDoNotContainRawSaveName()
    {
        string modDirectory = Path.Combine(Path.GetTempPath(), "StardewNpcAgent Mod");
        string rawSaveName = "Farm Name/../private";

        SavePartitionIdentity identity = SaveIdentityProvider.Create(
            rawSaveName,
            123L,
            modDirectory);

        string expectedRoot = Path.GetFullPath(Path.Combine(modDirectory, "data"));
        Assert.StartsWith(expectedRoot + Path.DirectorySeparatorChar, identity.PartitionDirectory);
        Assert.DoesNotContain("Farm Name", identity.PartitionDirectory, StringComparison.Ordinal);
        Assert.Equal(
            Path.Combine(identity.PartitionDirectory, "events.json"),
            identity.EventOutboxPath);
        Assert.Equal(
            Path.Combine(identity.PartitionDirectory, "display-acks.json"),
            identity.DisplayAckOutboxPath);
    }

    /// <summary>
    /// 缺失世界身份或非绝对 Mod 根目录必须在创建文件前 fail closed。
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_InvalidSaveIdentityIsRejected(string saveFolderName)
    {
        Assert.Throws<ArgumentException>(
            () => SaveIdentityProvider.Create(
                saveFolderName,
                1L,
                Path.GetTempPath()));
    }
}
