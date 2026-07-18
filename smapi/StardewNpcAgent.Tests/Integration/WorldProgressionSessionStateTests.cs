using StardewNpcAgent.Contracts;
using StardewNpcAgent.Game;
using StardewNpcAgent.Infrastructure.Storage;
using StardewNpcAgent.Integration;

namespace StardewNpcAgent.Tests.Integration;

/// <summary>
/// 验证公共设施只在 Saved 后 durable enqueue，并保持 registry 顺序与保守失败语义。
/// </summary>
public sealed class WorldProgressionSessionStateTests : IDisposable
{
    private readonly string testDirectory;

    public WorldProgressionSessionStateTests()
    {
        testDirectory = Path.Combine(
            Path.GetTempPath(),
            "StardewNpcAgent.Tests",
            $"WorldProgressionSessionState.{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
    }

    [Fact]
    public void InitializeBaseline_ExistingFacilitiesDoNotBackfill()
    {
        DurableEventOutbox outbox = OpenOutbox("existing.json");
        WorldProgressionSessionState session = new(outbox);

        session.InitializeBaseline(
            CreateSnapshot(greenhouse: true, minecarts: false, bus: true));

        Assert.Equal(0, outbox.PendingCount);
        Assert.True(session.SnapshotBaseline()["greenhouse"]);
        Assert.True(session.SnapshotBaseline()["bus_service"]);
    }

    [Fact]
    public void StageDayEnding_DoesNotEnqueueUntilSavedAndCommitsMultipleFacilitiesInRegistryOrder()
    {
        DurableEventOutbox outbox = OpenOutbox("saved.json");
        WorldProgressionSessionState session = new(outbox);
        session.InitializeBaseline(CreateSnapshot());

        session.StageDayEnding(
            occurredDayIndex: 13,
            CreateSnapshot(greenhouse: true, minecarts: true, quarryBridge: true));

        Assert.Equal(0, outbox.PendingCount);
        Assert.Equal(3, session.StagedTransitionCount);

        int committed = session.CommitSaved();

        Assert.Equal(3, committed);
        PendingEventBatch batch = outbox.CreatePendingBatch("request");
        Assert.Equal(
            new[]
            {
                "public_facility_greenhouse_restored",
                "public_facility_minecarts_restored",
                "public_facility_quarry_bridge_restored",
            },
            batch.Request.Events.Select(
                item => item.Payload.GetProperty("milestone").GetString()));
        Assert.Equal(new[] { 1L, 2L, 3L }, batch.Identities.Select(item => item.Sequence));
        Assert.Equal(0, session.StagedTransitionCount);
        Assert.True(session.SnapshotBaseline()["quarry_bridge"]);
    }

    [Fact]
    public void DiscardStaged_SaveFailureOrReturnToTitleCreatesNoWorldMemory()
    {
        DurableEventOutbox outbox = OpenOutbox("discard.json");
        WorldProgressionSessionState session = new(outbox);
        session.InitializeBaseline(CreateSnapshot());
        session.StageDayEnding(13, CreateSnapshot(glitteringBoulder: true));

        session.DiscardStaged();

        Assert.Equal(0, session.StagedTransitionCount);
        Assert.Equal(0, outbox.PendingCount);
        Assert.False(session.SnapshotBaseline()["glittering_boulder"]);
    }

    [Fact]
    public void CommitSaved_IsIdempotentAndDoesNotRepeatAlreadyRestoredFacility()
    {
        DurableEventOutbox outbox = OpenOutbox("idempotent.json");
        WorldProgressionSessionState session = new(outbox);
        session.InitializeBaseline(CreateSnapshot());
        session.StageDayEnding(13, CreateSnapshot(bus: true));

        Assert.Equal(1, session.CommitSaved());
        Assert.Equal(0, session.CommitSaved());
        session.StageDayEnding(14, CreateSnapshot(bus: true));
        Assert.Equal(0, session.CommitSaved());

        Assert.Equal(1, outbox.PendingCount);
    }

    [Fact]
    public void Snapshot_MissingUnknownOrDuplicateFacilityFailsClosed()
    {
        DurableEventOutbox outbox = OpenOutbox("invalid.json");
        WorldProgressionSessionState session = new(outbox);
        Dictionary<string, bool> missing = CreateSnapshot();
        missing.Remove("greenhouse");
        Dictionary<string, bool> unknown = CreateSnapshot();
        unknown["bulletin_board"] = true;

        Assert.Throws<ArgumentException>(() => session.InitializeBaseline(missing));
        Assert.Throws<ArgumentException>(() => session.InitializeBaseline(unknown));
        Assert.Equal(0, outbox.PendingCount);
    }

    public void Dispose()
    {
        if (Directory.Exists(testDirectory))
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    private DurableEventOutbox OpenOutbox(string fileName)
    {
        return DurableEventOutbox.Open(
            Path.Combine(testDirectory, fileName),
            "save-a",
            "player-a");
    }

    private static Dictionary<string, bool> CreateSnapshot(
        bool greenhouse = false,
        bool minecarts = false,
        bool bus = false,
        bool quarryBridge = false,
        bool glitteringBoulder = false)
    {
        return new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["greenhouse"] = greenhouse,
            ["minecarts"] = minecarts,
            ["bus_service"] = bus,
            ["quarry_bridge"] = quarryBridge,
            ["glittering_boulder"] = glitteringBoulder,
        };
    }
}
