using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace StardewNpcAgent.Game;

/// <summary>
/// 一个已验证存档/玩家分区的 API identity 与本地 durable 文件路径。
/// </summary>
public sealed record SavePartitionIdentity(
    string SaveId,
    string PlayerId,
    string PartitionDirectory,
    string EventOutboxPath,
    string DisplayAckOutboxPath);

/// <summary>
/// 将 Stardew save folder 与玩家的 multiplayer ID 映射为稳定、不泄露原值的分区。
///
/// <para>
/// <c>UniqueMultiplayerID</c> 是 Stardew 提供的不透明有符号 64 位身份。部分旧存档中的
/// 合法值为负数，所以这里不能把它当作“必须非负”的数量，也不能取绝对值。取绝对值既会让
/// <c>-n</c> 与 <c>n</c> 发生身份碰撞，也无法覆盖 <see cref="long.MinValue"/>。
/// </para>
/// </summary>
public static class SaveIdentityProvider
{
    /// <summary>
    /// 生成版本化 API identity 与只使用 hash segment 的 Mod-local 路径；不创建目录或文件。
    /// </summary>
    /// <param name="saveFolderName">SMAPI 报告的稳定存档目录名；允许包含路径字符，但不能为空或带首尾空白。</param>
    /// <param name="uniqueMultiplayerId">
    /// 游戏提供的玩家身份原值。完整 <see cref="long"/> 域均合法，数值仅参与规范化 hash。
    /// </param>
    /// <param name="modDirectory">Mod 的绝对根目录；生成的数据路径始终位于其 <c>data</c> 子目录。</param>
    /// <returns>不暴露存档名和玩家原始 ID 的 API identity、分区目录及 durable outbox 路径。</returns>
    /// <exception cref="ArgumentException">存档身份不稳定，或 Mod 根目录不是绝对路径。</exception>
    public static SavePartitionIdentity Create(
        string saveFolderName,
        long uniqueMultiplayerId,
        string modDirectory)
    {
        ValidateStableIdentity(saveFolderName, nameof(saveFolderName));
        if (string.IsNullOrWhiteSpace(modDirectory) || !Path.IsPathFullyQualified(modDirectory))
        {
            throw new ArgumentException("modDirectory 必须是绝对路径。", nameof(modDirectory));
        }

        // 负号是稳定身份的一部分，必须保留在规范化输入中；hash 后不会泄露到 API 或磁盘路径。
        string playerIdentity = uniqueMultiplayerId.ToString(CultureInfo.InvariantCulture);
        string saveId = "save-v1-" + HashComponents(saveFolderName);
        string playerId = "player-v1-" + HashComponents(playerIdentity);
        string partitionSegment = "partition-v1-" + HashComponents(saveId, playerId);
        string dataRoot = Path.GetFullPath(Path.Combine(modDirectory, "data"));
        string partitionDirectory = Path.Combine(dataRoot, partitionSegment);
        return new SavePartitionIdentity(
            saveId,
            playerId,
            partitionDirectory,
            Path.Combine(partitionDirectory, "events.json"),
            Path.Combine(partitionDirectory, "display-acks.json"));
    }

    /// <summary>
    /// length-prefix UTF-8 components before SHA-256，避免内部 delimiter 造成身份碰撞。
    /// </summary>
    private static string HashComponents(params string[] components)
    {
        using MemoryStream canonical = new();
        foreach (string component in components)
        {
            byte[] value = Encoding.UTF8.GetBytes(component);
            byte[] length = Encoding.ASCII.GetBytes(
                value.Length.ToString(CultureInfo.InvariantCulture));
            canonical.Write(length, 0, length.Length);
            canonical.WriteByte((byte)':');
            canonical.Write(value, 0, value.Length);
        }

        return Convert.ToHexString(SHA256.HashData(canonical.ToArray())).ToLowerInvariant();
    }

    private static void ValidateStableIdentity(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value != value.Trim()
            || value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("身份必须非空、无首尾空白且不含 NUL。", parameterName);
        }
    }
}
