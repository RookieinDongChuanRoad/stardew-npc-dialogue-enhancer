using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StardewNpcAgent.Contracts;

namespace StardewNpcAgent.Game;

/// <summary>
/// SMAPI LevelChanged handler 在主线程提取的最小事实。
/// </summary>
public sealed record LevelChangedFact(
    bool IsLocalPlayer,
    int OccurredDayIndex,
    string SkillName,
    int OldLevel,
    int NewLevel);

/// <summary>
/// 保留既有 LevelChanged legacy 映射，并提供所有确定性 producer 共用的 identity 编码。
/// </summary>
/// <remarks>
/// <see cref="CollectLevelChanged"/> 只用于旧合同回归与既有数据兼容；新的正式生产路径使用
/// <see cref="PlayerProgressionEventCollector.CollectSkillLevelReached"/>。共享 identity helper 留在
/// 本类，是为了确保新旧 producer 都使用同一套 length-prefix 编码，而不是复制容易漂移的 hash 逻辑。
/// </remarks>
public static class GameEventCollector
{
    public static GameEvent? CollectLevelChanged(LevelChangedFact fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        if (fact.OccurredDayIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fact.OccurredDayIndex));
        }

        if (!fact.IsLocalPlayer || fact.NewLevel <= fact.OldLevel)
        {
            return null;
        }

        if (fact.OldLevel < 0 || fact.NewLevel < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fact), "skill level 不能为负。");
        }

        string skill = NormalizeSkillName(fact.SkillName);
        string milestone = $"skill_{skill}_level_{fact.NewLevel.ToString(CultureInfo.InvariantCulture)}";
        string eventId = "event-level-v1-" + ComputeIdentity(
            fact.OccurredDayIndex.ToString(CultureInfo.InvariantCulture),
            skill,
            fact.OldLevel.ToString(CultureInfo.InvariantCulture),
            fact.NewLevel.ToString(CultureInfo.InvariantCulture));
        return new GameEvent
        {
            EventId = eventId,
            EventType = "world_progression",
            EventVersion = "1",
            OccurredDayIndex = fact.OccurredDayIndex,
            Source = "smapi.player.level_changed",
            AudienceScope = AudienceScope.Public,
            AudienceNpcId = null,
            Payload = JsonSerializer.SerializeToElement(
                new Dictionary<string, string> { ["milestone"] = milestone }),
        };
    }

    private static string NormalizeSkillName(string skillName)
    {
        if (string.IsNullOrWhiteSpace(skillName) || skillName != skillName.Trim())
        {
            throw new ArgumentException("skillName 必须非空且无首尾空白。", nameof(skillName));
        }

        string normalized = skillName.ToLowerInvariant();
        if (normalized.Any(
                character => !((character >= 'a' && character <= 'z')
                    || (character >= '0' && character <= '9')
                    || character == '_')))
        {
            throw new ArgumentException("skillName 只能包含 ASCII 字母、数字或下划线。", nameof(skillName));
        }

        return normalized;
    }

    /// <summary>
    /// 对一组有顺序的稳定结构字段做无歧义 length-prefix 编码，再计算小写 SHA-256。
    /// </summary>
    /// <param name="components">
    /// 已由具体 producer 校验的 canonical 字段。调用方必须显式包含 event type/version，除非是在
    /// 保持既有 v1 identity 的 legacy 路径中。
    /// </param>
    /// <returns>64 字符小写十六进制摘要，不含任何本地化显示名。</returns>
    /// <remarks>
    /// 长度使用 UTF-8 byte count，因此 <c>["a:b", "c"]</c> 与 <c>["a", "b:c"]</c> 不会像
    /// delimiter join 那样产生歧义。方法保持 internal，只允许同程序集内的受控 collector 使用。
    /// </remarks>
    internal static string ComputeIdentity(params string[] components)
    {
        ArgumentNullException.ThrowIfNull(components);
        using MemoryStream canonical = new();
        foreach (string component in components)
        {
            ArgumentNullException.ThrowIfNull(component);
            byte[] value = Encoding.UTF8.GetBytes(component);
            byte[] length = Encoding.ASCII.GetBytes(
                value.Length.ToString(CultureInfo.InvariantCulture));
            canonical.Write(length, 0, length.Length);
            canonical.WriteByte((byte)':');
            canonical.Write(value, 0, value.Length);
        }

        return Convert.ToHexString(SHA256.HashData(canonical.ToArray())).ToLowerInvariant();
    }
}
