using StardewNpcAgent.Infrastructure.Storage;
using StardewNpcAgent.Integration;

namespace StardewNpcAgent.Tests.Integration;

/// <summary>
/// 验证矿洞、工具与精通 producer 都遵守“先 durable enqueue，再推进会话 baseline”。
/// </summary>
public sealed class PlayerProgressionSessionStateTests : IDisposable
{
    private readonly string testDirectory;

    public PlayerProgressionSessionStateTests()
    {
        testDirectory = Path.Combine(
            Path.GetTempPath(),
            "StardewNpcAgent.Tests",
            $"PlayerProgressionSessionState.{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
    }

    [Fact]
    public void InitializeBaselines_DoesNotBackfillExistingProgress()
    {
        DurableEventOutbox outbox = OpenOutbox("baseline.json");
        PlayerProgressionSessionState session = new(outbox);

        session.InitializeBaselines(
            deepestMineRawDepth: 50,
            pendingToolUpgrade: new PendingToolUpgrade("axe", 2),
            trashCanLevel: 1,
            masteryClaimValues: new[] { 1, 0, 0, 0, 0 });

        Assert.Equal(0, outbox.PendingCount);
        Assert.Equal(50, session.DeepestMineRawBaseline);
        Assert.Equal(new PendingToolUpgrade("axe", 2), session.PendingToolUpgrade);
        Assert.Equal(1, session.TrashCanLevelBaseline);
        Assert.Equal(new[] { 1, 0, 0, 0, 0 }, session.SnapshotMasteryClaimValues());
    }

    [Fact]
    public void ObserveMineDepth_EnqueuesHighestNewMilestoneAndSuppressesRepeatWarp()
    {
        DurableEventOutbox outbox = OpenOutbox("mine.json");
        PlayerProgressionSessionState session = new(outbox);
        session.InitializeBaselines(4, null, 0, ZeroMastery());

        Assert.True(session.ObserveMineDepth(13, isLocalPlayer: true, observedRawDepth: 12));
        Assert.False(session.ObserveMineDepth(13, isLocalPlayer: true, observedRawDepth: 12));

        Assert.Equal(12, session.DeepestMineRawBaseline);
        Assert.Equal(1, outbox.PendingCount);
        Assert.Equal(
            10,
            outbox.CreatePendingBatch("request").Request.Events[0]
                .Payload.GetProperty("milestone_depth").GetInt32());
    }

    [Fact]
    public void ObserveReceivedTools_RequiresSingleCandidateMatchingTrackedPendingUpgrade()
    {
        DurableEventOutbox outbox = OpenOutbox("tool.json");
        PlayerProgressionSessionState session = new(outbox);
        session.InitializeBaselines(0, null, 0, ZeroMastery());

        Assert.Equal(
            0,
            session.ObserveReceivedTools(
                13,
                isLocalPlayer: true,
                new[] { new ReceivedToolObservation("axe", 2) }));
        session.ObservePendingToolUpgrade(new PendingToolUpgrade("axe", 2));
        Assert.Equal(
            0,
            session.ObserveReceivedTools(
                13,
                isLocalPlayer: true,
                new[] { new ReceivedToolObservation("pickaxe", 2) }));
        Assert.NotNull(session.PendingToolUpgrade);

        Assert.Equal(
            1,
            session.ObserveReceivedTools(
                13,
                isLocalPlayer: true,
                new[] { new ReceivedToolObservation("axe", 2) }));

        Assert.Null(session.PendingToolUpgrade);
        Assert.Equal(1, outbox.PendingCount);
        Assert.Equal(
            "axe",
            outbox.CreatePendingBatch("request").Request.Events[0]
                .Payload.GetProperty("tool_id").GetString());
    }

    [Fact]
    public void ObserveReceivedTools_DuplicateCandidateOrOldChestToolDoesNotConsumePending()
    {
        DurableEventOutbox outbox = OpenOutbox("tool-ambiguous.json");
        PlayerProgressionSessionState session = new(outbox);
        session.InitializeBaselines(
            0,
            new PendingToolUpgrade("pickaxe", 3),
            0,
            ZeroMastery());

        Assert.Equal(
            0,
            session.ObserveReceivedTools(
                13,
                true,
                new[]
                {
                    new ReceivedToolObservation("pickaxe", 2),
                    new ReceivedToolObservation("fishing_rod", 3),
                }));
        Assert.Equal(
            0,
            session.ObserveReceivedTools(
                13,
                true,
                new[]
                {
                    new ReceivedToolObservation("pickaxe", 3),
                    new ReceivedToolObservation("pickaxe", 3),
                }));

        Assert.Equal(new PendingToolUpgrade("pickaxe", 3), session.PendingToolUpgrade);
        Assert.Equal(0, outbox.PendingCount);
    }

    [Fact]
    public void ObserveTrashCanLevel_RequiresTrackedPendingAndExactOneLevelGrowth()
    {
        DurableEventOutbox outbox = OpenOutbox("trash-can.json");
        PlayerProgressionSessionState session = new(outbox);
        session.InitializeBaselines(0, null, 1, ZeroMastery());

        Assert.False(session.ObserveTrashCanLevel(13, true, observedLevel: 2));
        Assert.Equal(2, session.TrashCanLevelBaseline);
        Assert.Equal(0, outbox.PendingCount);

        session.ObservePendingToolUpgrade(new PendingToolUpgrade("trash_can", 3));
        Assert.True(session.ObserveTrashCanLevel(14, true, observedLevel: 3));

        Assert.Null(session.PendingToolUpgrade);
        Assert.Equal(1, outbox.PendingCount);
        Assert.Equal(
            "trash_can",
            outbox.CreatePendingBatch("request").Request.Events[0]
                .Payload.GetProperty("tool_id").GetString());
    }

    [Fact]
    public void ReconcileMasteryClaims_OnlyZeroToOneTransitionsEnqueueOnceInIndexOrder()
    {
        DurableEventOutbox outbox = OpenOutbox("mastery.json");
        PlayerProgressionSessionState session = new(outbox);
        session.InitializeBaselines(0, null, 0, ZeroMastery());

        int committed = session.ReconcileMasteryClaims(
            13,
            isLocalPlayer: true,
            new[] { 1, 0, 1, 0, 0 });
        int repeated = session.ReconcileMasteryClaims(
            13,
            isLocalPlayer: true,
            new[] { 1, 0, 1, 0, 0 });

        Assert.Equal(2, committed);
        Assert.Equal(0, repeated);
        Assert.Equal(
            new[] { "farming", "foraging" },
            outbox.CreatePendingBatch("request").Request.Events.Select(
                item => item.Payload.GetProperty("skill_id").GetString()));
    }

    [Fact]
    public void ObserveMineDepth_WhenWriterFailsDoesNotAdvanceBaseline()
    {
        string filePath = Path.Combine(testDirectory, "mine-write-failure.json");
        DurableEventOutbox initial = DurableEventOutbox.Open(filePath, "save-a", "player-a");
        InvalidOperationException injected = new("test-only writer failure");
        DurableEventOutbox failing = DurableEventOutbox.Open(
            filePath,
            "save-a",
            "player-a",
            snapshotWriter: _ => throw injected);
        PlayerProgressionSessionState session = new(failing);
        session.InitializeBaselines(4, null, 0, ZeroMastery());

        Assert.Same(
            injected,
            Assert.Throws<InvalidOperationException>(
                () => session.ObserveMineDepth(13, true, 5)));
        Assert.Equal(4, session.DeepestMineRawBaseline);
        Assert.Equal(0, failing.PendingCount);
    }

    [Fact]
    public void Clear_RemovesAllEphemeralBaselinesWithoutTouchingOutbox()
    {
        DurableEventOutbox outbox = OpenOutbox("clear-player.json");
        PlayerProgressionSessionState session = new(outbox);
        session.InitializeBaselines(50, new PendingToolUpgrade("pan", 2), 1, ZeroMastery());

        session.Clear();

        Assert.Null(session.DeepestMineRawBaseline);
        Assert.Null(session.PendingToolUpgrade);
        Assert.Null(session.TrashCanLevelBaseline);
        Assert.Empty(session.SnapshotMasteryClaimValues());
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

    private static int[] ZeroMastery()
    {
        return new[] { 0, 0, 0, 0, 0 };
    }
}
