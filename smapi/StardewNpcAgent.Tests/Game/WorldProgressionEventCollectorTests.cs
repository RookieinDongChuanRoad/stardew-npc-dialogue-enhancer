using StardewNpcAgent.Contracts;
using StardewNpcAgent.Game;

namespace StardewNpcAgent.Tests.Game;

/// <summary>
/// 验证五类路线中立公共设施结果的纯映射，不把公告栏、完整路线或任意 flag 当作兜底事实。
/// </summary>
public sealed class WorldProgressionEventCollectorTests
{
    [Theory]
    [InlineData("greenhouse", "public_facility_greenhouse_restored")]
    [InlineData("minecarts", "public_facility_minecarts_restored")]
    [InlineData("bus_service", "public_facility_bus_service_restored")]
    [InlineData("quarry_bridge", "public_facility_quarry_bridge_restored")]
    [InlineData("glittering_boulder", "public_facility_glittering_boulder_removed")]
    public void CollectPublicFacilityRestored_FiveCanonicalTransitionsProducePublicFact(
        string facilityId,
        string expectedMilestone)
    {
        PublicFacilityRestoredFact fact = new(
            OccurredDayIndex: 13,
            FacilityId: facilityId,
            WasRestored: false,
            IsRestored: true);

        GameEvent first = Assert.IsType<GameEvent>(
            WorldProgressionEventCollector.CollectPublicFacilityRestored(fact));
        GameEvent replay = Assert.IsType<GameEvent>(
            WorldProgressionEventCollector.CollectPublicFacilityRestored(fact));

        Assert.Equal(first.EventId, replay.EventId);
        Assert.StartsWith("event-public-facility-restored-v1-", first.EventId, StringComparison.Ordinal);
        Assert.Equal("world_progression", first.EventType);
        Assert.Equal("1", first.EventVersion);
        Assert.Equal(13, first.OccurredDayIndex);
        Assert.Equal("smapi.world.public_facility_restored", first.Source);
        Assert.Equal(AudienceScope.Public, first.AudienceScope);
        Assert.Null(first.AudienceNpcId);
        Assert.Equal(expectedMilestone, first.Payload.GetProperty("milestone").GetString());
        Assert.Single(first.Payload.EnumerateObject());
        Assert.True(ContractValidator.Validate(first).IsValid);
    }

    [Theory]
    [InlineData("greenhouse", false, false)]
    [InlineData("greenhouse", true, true)]
    [InlineData("greenhouse", true, false)]
    [InlineData("bulletin_board", false, true)]
    [InlineData("ccBulletin", false, true)]
    [InlineData("movie_theater", false, true)]
    public void CollectPublicFacilityRestored_NoUpwardTransitionOrUnknownFacilityReturnsNull(
        string facilityId,
        bool wasRestored,
        bool isRestored)
    {
        Assert.Null(
            WorldProgressionEventCollector.CollectPublicFacilityRestored(
                new PublicFacilityRestoredFact(
                    13,
                    facilityId,
                    wasRestored,
                    isRestored)));
    }

    [Fact]
    public void Registry_FreezesCanonicalFlagAndStableEnqueueOrder()
    {
        Assert.Equal(
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
            },
            WorldProgressionEventCollector.Facilities);
    }

    [Fact]
    public void CollectPublicFacilityRestored_InvalidDayOrIdentifierFailsClosed()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => WorldProgressionEventCollector.CollectPublicFacilityRestored(
                new PublicFacilityRestoredFact(-1, "greenhouse", false, true)));
        Assert.Throws<ArgumentException>(
            () => WorldProgressionEventCollector.CollectPublicFacilityRestored(
                new PublicFacilityRestoredFact(13, " greenhouse ", false, true)));
    }
}
