using StardewNpcAgent.Contracts;
using StardewNpcAgent.Game;
using StardewNpcAgent.Infrastructure.Storage;
using StardewNpcAgent.Integration;

namespace StardewNpcAgent.Tests.Integration;

/// <summary>
/// 验证 NPC 差分 producer 的会话 baseline、持久 checkpoint 与 durable enqueue 顺序。
/// </summary>
/// <remarks>
/// 测试使用真实临时 events.json，但不启动游戏或网络。这样既能证明跨实例恢复，也能精确断言
/// 首次加载不回填、DayStarted 当场入队，以及四心 decay/re-cross 不会制造第二条 memory。
/// </remarks>
public sealed class ProducerSessionStateTests : IDisposable
{
    private const string SaveId = "save-a";
    private const string PlayerId = "player-a";
    private readonly string testDirectory;

    public ProducerSessionStateTests()
    {
        testDirectory = Path.Combine(
            Path.GetTempPath(),
            "StardewNpcAgent.Tests",
            $"ProducerSessionState.{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
    }

    [Fact]
    public void InitializeNpcBaseline_FirstLoadCreatesDurableMilestoneBaselinesWithoutBackfill()
    {
        DurableEventOutbox outbox = OpenOutbox("first-load.json");
        ProducerSessionState session = CreateSession(outbox);

        Assert.True(
            session.InitializeNpcBaseline(
                12,
                new NpcFriendshipObservation("Abigail", 1_200, RelationshipStatus.Friendly)));
        Assert.True(
            session.InitializeNpcBaseline(
                12,
                new NpcFriendshipObservation("Sebastian", 500, RelationshipStatus.Friendly)));

        Assert.Equal(0, outbox.PendingCount);
        Assert.Equal(
            new[]
            {
                new ProducerCheckpoint("friendship:Abigail:friend", "baseline_existing", 12),
                new ProducerCheckpoint("friendship:Sebastian:friend", "baseline_below", 12),
            },
            outbox.SnapshotProducerCheckpoints());
        Assert.Equal(
            new[] { "Abigail", "Sebastian" },
            session.SnapshotNpcBaselines().Select(item => item.NpcId));
    }

    [Fact]
    public void InitializeNpcBaseline_ExistingBelowCheckpointAndLoadedAboveAdvancesWithoutBackfill()
    {
        DurableEventOutbox outbox = OpenOutbox("missed-while-unloaded.json");
        outbox.InitializeProducerCheckpoint(
            "friendship:Abigail:friend",
            "baseline_below",
            observedDayIndex: 10);
        ProducerSessionState session = CreateSession(outbox);

        session.InitializeNpcBaseline(
            13,
            new NpcFriendshipObservation("Abigail", 1_200, RelationshipStatus.Friendly));

        Assert.Equal(0, outbox.PendingCount);
        Assert.Equal(
            new ProducerCheckpoint("friendship:Abigail:friend", "baseline_existing", 13),
            Assert.Single(outbox.SnapshotProducerCheckpoints()));

        // 已保守记为“加载时既有”，之后先衰减再跨回也不能伪造首次四心日期。
        session.ReconcileNpcObservation(
            14,
            new NpcFriendshipObservation("Abigail", 900, RelationshipStatus.Friendly));
        session.ReconcileNpcObservation(
            15,
            new NpcFriendshipObservation("Abigail", 1_100, RelationshipStatus.Friendly));
        Assert.Equal(0, outbox.PendingCount);
    }

    [Fact]
    public void ReconcileNpcObservation_FirstFourHeartCrossingAtomicallyPersistsEventAndSeenCheckpoint()
    {
        string filePath = CreateFilePath("first-crossing.json");
        DurableEventOutbox outbox = DurableEventOutbox.Open(filePath, SaveId, PlayerId);
        ProducerSessionState session = CreateSession(outbox);
        session.InitializeNpcBaseline(
            12,
            new NpcFriendshipObservation("Abigail", 999, RelationshipStatus.Friendly));

        session.ReconcileNpcObservation(
            13,
            new NpcFriendshipObservation("Abigail", 1_001, RelationshipStatus.Friendly));

        GameEvent milestone = Assert.Single(outbox.CreatePendingBatch("request").Request.Events);
        Assert.Equal("friendship_milestone_reached", milestone.EventType);
        Assert.Equal("Abigail", milestone.AudienceNpcId);
        Assert.Equal(
            new ProducerCheckpoint("friendship:Abigail:friend", "seen", 13),
            Assert.Single(outbox.SnapshotProducerCheckpoints()));

        DurableEventOutbox reopened = DurableEventOutbox.Open(filePath, SaveId, PlayerId);
        Assert.Equal(1, reopened.PendingCount);
        Assert.Equal("seen", Assert.Single(reopened.SnapshotProducerCheckpoints()).Status);
    }

    [Fact]
    public void ReconcileNpcObservation_DecayRecrossAndRestartNeverRepeatFirstMilestone()
    {
        string filePath = CreateFilePath("decay-recross.json");
        DurableEventOutbox outbox = DurableEventOutbox.Open(filePath, SaveId, PlayerId);
        ProducerSessionState firstSession = CreateSession(outbox);
        firstSession.InitializeNpcBaseline(
            12,
            new NpcFriendshipObservation("Sebastian", 999, RelationshipStatus.Friendly));
        firstSession.ReconcileNpcObservation(
            13,
            new NpcFriendshipObservation("Sebastian", 1_001, RelationshipStatus.Friendly));
        firstSession.ReconcileNpcObservation(
            14,
            new NpcFriendshipObservation("Sebastian", 900, RelationshipStatus.Friendly));
        firstSession.ReconcileNpcObservation(
            15,
            new NpcFriendshipObservation("Sebastian", 1_100, RelationshipStatus.Friendly));
        Assert.Equal(1, outbox.PendingCount);

        DurableEventOutbox reopened = DurableEventOutbox.Open(filePath, SaveId, PlayerId);
        ProducerSessionState secondSession = CreateSession(reopened);
        secondSession.InitializeNpcBaseline(
            16,
            new NpcFriendshipObservation("Sebastian", 900, RelationshipStatus.Friendly));
        secondSession.ReconcileNpcObservation(
            17,
            new NpcFriendshipObservation("Sebastian", 1_200, RelationshipStatus.Friendly));

        Assert.Equal(1, reopened.PendingCount);
        Assert.Equal("seen", Assert.Single(reopened.SnapshotProducerCheckpoints()).Status);
    }

    [Fact]
    public void ReconcileDayStarted_OvernightRelationshipChangeIsDurableBeforeDayEnding()
    {
        string filePath = CreateFilePath("day-started-relationship.json");
        DurableEventOutbox outbox = DurableEventOutbox.Open(filePath, SaveId, PlayerId);
        ProducerSessionState session = CreateSession(outbox);
        session.InitializeNpcBaseline(
            12,
            new NpcFriendshipObservation("Abigail", 2_000, RelationshipStatus.Dating));

        int processed = session.ReconcileDayStarted(
            13,
            new[]
            {
                new NpcFriendshipObservation("Abigail", 2_500, RelationshipStatus.Engaged),
            });

        Assert.Equal(1, processed);
        GameEvent relationship = Assert.Single(outbox.CreatePendingBatch("request").Request.Events);
        Assert.Equal("relationship_status_changed", relationship.EventType);
        Assert.Equal("dating", relationship.Payload.GetProperty("old_status").GetString());
        Assert.Equal("engaged", relationship.Payload.GetProperty("new_status").GetString());

        // 不调用 DayEnding，直接重开文件，证明 DayStarted handler 已同步 durable enqueue。
        DurableEventOutbox reopened = DurableEventOutbox.Open(filePath, SaveId, PlayerId);
        Assert.Equal(1, reopened.PendingCount);
    }

    [Fact]
    public void ReconcileNpcObservation_UnregisteredJumpStillUpdatesCurrentBaseline()
    {
        DurableEventOutbox outbox = OpenOutbox("current-state-separate.json");
        ProducerSessionState session = CreateSession(outbox);
        session.InitializeNpcBaseline(
            12,
            new NpcFriendshipObservation("Abigail", 2_000, RelationshipStatus.Friendly));

        session.ReconcileNpcObservation(
            13,
            new NpcFriendshipObservation("Abigail", 2_500, RelationshipStatus.Married));
        Assert.Equal(0, outbox.PendingCount);

        session.ReconcileNpcObservation(
            14,
            new NpcFriendshipObservation("Abigail", 2_000, RelationshipStatus.Divorced));

        GameEvent divorce = Assert.Single(outbox.CreatePendingBatch("request").Request.Events);
        Assert.Equal("married", divorce.Payload.GetProperty("old_status").GetString());
        Assert.Equal("divorced", divorce.Payload.GetProperty("new_status").GetString());
    }

    [Fact]
    public void ReconcileNpcObservation_EachTargetKeepsIndependentBaselineAudienceAndCheckpoint()
    {
        DurableEventOutbox outbox = OpenOutbox("target-isolation.json");
        ProducerSessionState session = CreateSession(outbox);
        session.InitializeNpcBaseline(
            12,
            new NpcFriendshipObservation("Abigail", 999, RelationshipStatus.Friendly));
        session.InitializeNpcBaseline(
            12,
            new NpcFriendshipObservation("Sebastian", 500, RelationshipStatus.Friendly));

        session.ReconcileNpcObservation(
            13,
            new NpcFriendshipObservation("Abigail", 1_100, RelationshipStatus.Dating));
        Assert.False(
            session.ReconcileNpcObservation(
                13,
                new NpcFriendshipObservation("Leah", 1_500, RelationshipStatus.Dating)));

        PendingEventBatch batch = outbox.CreatePendingBatch("request");
        Assert.Equal(2, batch.Request.Events.Count);
        Assert.All(batch.Request.Events, item => Assert.Equal("Abigail", item.AudienceNpcId));
        Assert.Equal(
            500,
            session.SnapshotNpcBaselines().Single(item => item.NpcId == "Sebastian").FriendshipPoints);
        Assert.Equal(
            "baseline_below",
            outbox.SnapshotProducerCheckpoints()
                .Single(item => item.ProducerId == "friendship:Sebastian:friend")
                .Status);
    }

    [Fact]
    public void InitializeNpcBaseline_CorruptCheckpointDisablesOnlyMilestoneWhileRelationshipContinues()
    {
        string filePath = CreateFilePath("checkpoint-unavailable.json");
        File.WriteAllText(
            filePath,
            "{\"format_version\":2,\"save_id\":\"save-a\",\"player_id\":\"player-a\","
                + "\"memory_revision\":0,\"committed_through_day_index\":-1,\"next_sequence\":1,"
                + "\"pending\":[],\"dead_letters\":[],\"producer_checkpoints\":null}");
        DurableEventOutbox outbox = DurableEventOutbox.Open(filePath, SaveId, PlayerId);
        ProducerSessionState session = CreateSession(outbox);

        session.InitializeNpcBaseline(
            12,
            new NpcFriendshipObservation("Abigail", 999, RelationshipStatus.Friendly));
        session.ReconcileNpcObservation(
            13,
            new NpcFriendshipObservation("Abigail", 1_100, RelationshipStatus.Dating));

        Assert.False(session.IsFriendshipMilestoneEnabled("Abigail"));
        Assert.Equal(
            "CHECKPOINTS_UNAVAILABLE",
            session.GetFriendshipMilestoneDisableReason("Abigail"));
        GameEvent relationship = Assert.Single(outbox.CreatePendingBatch("request").Request.Events);
        Assert.Equal("relationship_status_changed", relationship.EventType);
        Assert.Equal("Abigail", relationship.AudienceNpcId);
    }

    [Fact]
    public void Clear_RemovesOnlyEphemeralSessionStateAndKeepsDurableCheckpoints()
    {
        DurableEventOutbox outbox = OpenOutbox("clear.json");
        ProducerSessionState session = CreateSession(outbox);
        session.InitializeNpcBaseline(
            12,
            new NpcFriendshipObservation("Abigail", 1_200, RelationshipStatus.Friendly));

        session.Clear();

        Assert.Empty(session.SnapshotNpcBaselines());
        Assert.Single(outbox.SnapshotProducerCheckpoints());
    }

    public void Dispose()
    {
        if (Directory.Exists(testDirectory))
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    private ProducerSessionState CreateSession(DurableEventOutbox outbox)
    {
        return new ProducerSessionState(outbox, new[] { "Abigail", "Sebastian" });
    }

    private DurableEventOutbox OpenOutbox(string fileName)
    {
        return DurableEventOutbox.Open(CreateFilePath(fileName), SaveId, PlayerId);
    }

    private string CreateFilePath(string fileName)
    {
        return Path.Combine(testDirectory, fileName);
    }
}
