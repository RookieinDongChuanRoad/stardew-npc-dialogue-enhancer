using System.Globalization;
using System.Text.Json;
using StardewNpcAgent.Contracts;

namespace StardewNpcAgent.Game;

/// <summary>
/// 一个路线中立公共设施的 canonical 注册项。
/// </summary>
/// <param name="FacilityId">内部领域 ID。</param>
/// <param name="MailFlag">MasterPlayer.hasOrWillReceiveMail 使用的公开 canonical flag。</param>
/// <param name="Milestone">现有 world_progression v1 payload 的精确 milestone。</param>
public sealed record PublicFacilityDefinition(
    string FacilityId,
    string MailFlag,
    string Milestone);

/// <summary>
/// Saved 前后由会话状态机确认的一条 facility false→true 事实。
/// </summary>
public sealed record PublicFacilityRestoredFact(
    int OccurredDayIndex,
    string FacilityId,
    bool WasRestored,
    bool IsRestored);

/// <summary>
/// 把五类公共设施恢复结果映射为路线中立的 world_progression v1 事件。
/// </summary>
/// <remarks>
/// registry 是首批唯一白名单；不按 cc* 前缀或 milestone 字符串兜底。collector 不读取
/// MasterPlayer，也不决定保存是否成功；WorldProgressionSessionState 只有在 Saved 后才调用并入队。
/// </remarks>
public static class WorldProgressionEventCollector
{
    private const string EventVersion = "1";
    private static readonly IReadOnlyList<PublicFacilityDefinition> FacilityRegistry =
        Array.AsReadOnly(
            new[]
            {
                new PublicFacilityDefinition(
                    "greenhouse",
                    "ccPantry",
                    "public_facility_greenhouse_restored"),
                new PublicFacilityDefinition(
                    "minecarts",
                    "ccBoilerRoom",
                    "public_facility_minecarts_restored"),
                new PublicFacilityDefinition(
                    "bus_service",
                    "ccVault",
                    "public_facility_bus_service_restored"),
                new PublicFacilityDefinition(
                    "quarry_bridge",
                    "ccCraftsRoom",
                    "public_facility_quarry_bridge_restored"),
                new PublicFacilityDefinition(
                    "glittering_boulder",
                    "ccFishTank",
                    "public_facility_glittering_boulder_removed"),
            });

    /// <summary>
    /// 返回冻结顺序的 immutable registry；同日多设施必须按此顺序入队。
    /// </summary>
    public static IReadOnlyList<PublicFacilityDefinition> Facilities => FacilityRegistry;

    /// <summary>
    /// 只为已注册 facility 的 false→true transition 创建 public 事件。
    /// </summary>
    public static GameEvent? CollectPublicFacilityRestored(PublicFacilityRestoredFact fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        if (fact.OccurredDayIndex < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fact.OccurredDayIndex),
                fact.OccurredDayIndex,
                "OccurredDayIndex 必须大于等于 0。");
        }

        ValidateStableIdentifier(fact.FacilityId);
        PublicFacilityDefinition? definition = FacilityRegistry.SingleOrDefault(
            item => string.Equals(item.FacilityId, fact.FacilityId, StringComparison.Ordinal));
        if (definition is null || fact.WasRestored || !fact.IsRestored)
        {
            return null;
        }

        const string eventType = "world_progression";
        string eventId = "event-public-facility-restored-v1-" + GameEventCollector.ComputeIdentity(
            eventType,
            EventVersion,
            fact.OccurredDayIndex.ToString(CultureInfo.InvariantCulture),
            definition.FacilityId);
        return new GameEvent
        {
            EventId = eventId,
            EventType = eventType,
            EventVersion = EventVersion,
            OccurredDayIndex = fact.OccurredDayIndex,
            Source = "smapi.world.public_facility_restored",
            AudienceScope = AudienceScope.Public,
            AudienceNpcId = null,
            Payload = JsonSerializer.SerializeToElement(
                new Dictionary<string, string>
                {
                    ["milestone"] = definition.Milestone,
                }),
        };
    }

    private static void ValidateStableIdentifier(string facilityId)
    {
        ArgumentNullException.ThrowIfNull(facilityId);
        if (string.IsNullOrWhiteSpace(facilityId)
            || !string.Equals(facilityId, facilityId.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "FacilityId 必须非空且不能包含首尾空白。",
                nameof(facilityId));
        }
    }
}
