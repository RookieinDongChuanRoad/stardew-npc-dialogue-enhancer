using StardewNpcAgent.Contracts;
using StardewNpcAgent.Game;

namespace StardewNpcAgent.Tests.Game;

/// <summary>
/// 验证玩家成长 producer 只把已经冻结的公开游戏事实映射为稳定合同事件。
/// </summary>
/// <remarks>
/// 本测试类不启动 SMAPI，也不读取 Game1。运行时负责在主线程取得真实值；collector 只负责
/// 白名单、阈值、payload 与无歧义 identity，因此可以覆盖全部边界而不依赖游戏 UI 时序。
/// </remarks>
public sealed class PlayerProgressionEventCollectorTests
{
    [Theory]
    [InlineData("Farming", "farming")]
    [InlineData("Fishing", "fishing")]
    [InlineData("Foraging", "foraging")]
    [InlineData("Mining", "mining")]
    [InlineData("Combat", "combat")]
    public void CollectSkillLevelReached_FiveVanillaSkillsProduceStablePublicFacts(
        string observedSkillName,
        string expectedSkillId)
    {
        LevelChangedFact fact = new(
            IsLocalPlayer: true,
            OccurredDayIndex: 13,
            SkillName: observedSkillName,
            OldLevel: 4,
            NewLevel: 5);

        GameEvent first = Assert.IsType<GameEvent>(
            PlayerProgressionEventCollector.CollectSkillLevelReached(fact));
        GameEvent replay = Assert.IsType<GameEvent>(
            PlayerProgressionEventCollector.CollectSkillLevelReached(fact));

        Assert.Equal(first.EventId, replay.EventId);
        Assert.StartsWith("event-skill-level-reached-v1-", first.EventId, StringComparison.Ordinal);
        Assert.Equal("skill_level_reached", first.EventType);
        Assert.Equal("1", first.EventVersion);
        Assert.Equal(13, first.OccurredDayIndex);
        Assert.Equal("smapi.player.level_changed", first.Source);
        Assert.Equal(AudienceScope.Public, first.AudienceScope);
        Assert.Null(first.AudienceNpcId);
        Assert.Equal(expectedSkillId, first.Payload.GetProperty("skill_id").GetString());
        Assert.Equal(4, first.Payload.GetProperty("old_level").GetInt32());
        Assert.Equal(5, first.Payload.GetProperty("new_level").GetInt32());
        Assert.True(ContractValidator.Validate(first).IsValid);
    }

    [Theory]
    [InlineData(false, "Farming", 4, 5)]
    [InlineData(true, "Farming", 5, 5)]
    [InlineData(true, "Farming", 6, 5)]
    [InlineData(true, "Luck", 0, 1)]
    [InlineData(true, "Magic", 0, 1)]
    public void CollectSkillLevelReached_RemoteNonIncreaseOrUnregisteredSkillReturnsNull(
        bool isLocalPlayer,
        string skillName,
        int oldLevel,
        int newLevel)
    {
        GameEvent? result = PlayerProgressionEventCollector.CollectSkillLevelReached(
            new LevelChangedFact(isLocalPlayer, 13, skillName, oldLevel, newLevel));

        Assert.Null(result);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 11)]
    [InlineData(11, 12)]
    public void CollectSkillLevelReached_LevelOutsideVanillaRangeFailsClosed(
        int oldLevel,
        int newLevel)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PlayerProgressionEventCollector.CollectSkillLevelReached(
                new LevelChangedFact(true, 13, "Farming", oldLevel, newLevel)));
    }

    [Theory]
    [InlineData(0, 4, null, null, null)]
    [InlineData(4, 5, "the_mines", 5, 5)]
    [InlineData(5, 12, "the_mines", 10, 12)]
    [InlineData(0, 120, "the_mines", 120, 120)]
    [InlineData(120, 144, null, null, null)]
    [InlineData(120, 145, "skull_cavern", 25, 25)]
    [InlineData(145, 180, "skull_cavern", 50, 60)]
    [InlineData(180, 220, "skull_cavern", 100, 100)]
    [InlineData(220, 370, "skull_cavern", 200, 250)]
    [InlineData(370, 420, "skull_cavern", 300, 300)]
    public void CollectMineDepthMilestone_MapsRawHighWaterAndEmitsOnlyHighestCrossedThreshold(
        int previousRawDepth,
        int observedRawDepth,
        string? expectedMineId,
        int? expectedMilestoneDepth,
        int? expectedObservedDepth)
    {
        MineDepthMilestoneFact fact = new(
            IsLocalPlayer: true,
            OccurredDayIndex: 13,
            PreviousRawDepth: previousRawDepth,
            ObservedRawDepth: observedRawDepth);

        GameEvent? result = PlayerProgressionEventCollector.CollectMineDepthMilestone(fact);

        if (expectedMineId is null)
        {
            Assert.Null(result);
            return;
        }

        GameEvent gameEvent = Assert.IsType<GameEvent>(result);
        Assert.StartsWith("event-mine-depth-milestone-v1-", gameEvent.EventId, StringComparison.Ordinal);
        Assert.Equal("mine_depth_milestone_reached", gameEvent.EventType);
        Assert.Equal("1", gameEvent.EventVersion);
        Assert.Equal("smapi.player.warped", gameEvent.Source);
        Assert.Equal(AudienceScope.Public, gameEvent.AudienceScope);
        Assert.Equal(expectedMineId, gameEvent.Payload.GetProperty("mine_id").GetString());
        Assert.Equal(
            expectedMilestoneDepth,
            gameEvent.Payload.GetProperty("milestone_depth").GetInt32());
        Assert.Equal(
            expectedObservedDepth,
            gameEvent.Payload.GetProperty("observed_depth").GetInt32());
        Assert.True(ContractValidator.Validate(gameEvent).IsValid);
        Assert.Equal(
            gameEvent.EventId,
            PlayerProgressionEventCollector.CollectMineDepthMilestone(fact)?.EventId);
    }

    [Theory]
    [InlineData(0, 77377)]
    [InlineData(50, 50)]
    [InlineData(50, 49)]
    public void CollectMineDepthMilestone_QuarryRepeatOrDecreaseReturnsNull(
        int previousRawDepth,
        int observedRawDepth)
    {
        GameEvent? result = PlayerProgressionEventCollector.CollectMineDepthMilestone(
            new MineDepthMilestoneFact(true, 13, previousRawDepth, observedRawDepth));

        Assert.Null(result);
    }

    [Fact]
    public void CollectMineDepthMilestone_RemotePlayerDoesNotProducePublicFact()
    {
        Assert.Null(
            PlayerProgressionEventCollector.CollectMineDepthMilestone(
                new MineDepthMilestoneFact(false, 13, 4, 5)));
    }

    [Theory]
    [InlineData("axe")]
    [InlineData("pickaxe")]
    [InlineData("hoe")]
    [InlineData("watering_can")]
    [InlineData("pan")]
    [InlineData("trash_can")]
    public void CollectToolUpgradeReceived_SixCanonicalToolsRequireMatchingReceivedLevel(
        string toolId)
    {
        ToolUpgradeReceivedFact fact = new(
            IsLocalPlayer: true,
            OccurredDayIndex: 13,
            ToolId: toolId,
            PendingUpgradeLevel: 2,
            ReceivedUpgradeLevel: 2);

        GameEvent gameEvent = Assert.IsType<GameEvent>(
            PlayerProgressionEventCollector.CollectToolUpgradeReceived(fact));

        Assert.StartsWith("event-tool-upgrade-received-v1-", gameEvent.EventId, StringComparison.Ordinal);
        Assert.Equal("tool_upgrade_received", gameEvent.EventType);
        Assert.Equal("1", gameEvent.EventVersion);
        Assert.Equal("smapi.player.tool_upgrade_observed", gameEvent.Source);
        Assert.Equal(AudienceScope.Public, gameEvent.AudienceScope);
        Assert.Equal(toolId, gameEvent.Payload.GetProperty("tool_id").GetString());
        Assert.Equal(2, gameEvent.Payload.GetProperty("upgrade_level").GetInt32());
        Assert.True(ContractValidator.Validate(gameEvent).IsValid);
        Assert.Equal(
            gameEvent.EventId,
            PlayerProgressionEventCollector.CollectToolUpgradeReceived(fact)?.EventId);
    }

    [Theory]
    [InlineData(false, "axe", 2, 2)]
    [InlineData(true, "axe", 1, 2)]
    [InlineData(true, "fishing_rod", 2, 2)]
    [InlineData(true, "Axe", 2, 2)]
    [InlineData(true, "axe", 0, 0)]
    public void CollectToolUpgradeReceived_RemoteMismatchOrUnregisteredToolReturnsNull(
        bool isLocalPlayer,
        string toolId,
        int pendingUpgradeLevel,
        int receivedUpgradeLevel)
    {
        GameEvent? result = PlayerProgressionEventCollector.CollectToolUpgradeReceived(
            new ToolUpgradeReceivedFact(
                isLocalPlayer,
                13,
                toolId,
                pendingUpgradeLevel,
                receivedUpgradeLevel));

        Assert.Null(result);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(1, 5)]
    [InlineData(5, 5)]
    public void CollectToolUpgradeReceived_LevelOutsideFrozenRangeFailsClosed(
        int pendingUpgradeLevel,
        int receivedUpgradeLevel)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PlayerProgressionEventCollector.CollectToolUpgradeReceived(
                new ToolUpgradeReceivedFact(
                    true,
                    13,
                    "axe",
                    pendingUpgradeLevel,
                    receivedUpgradeLevel)));
    }

    [Theory]
    [InlineData(0, "farming")]
    [InlineData(1, "fishing")]
    [InlineData(2, "foraging")]
    [InlineData(3, "mining")]
    [InlineData(4, "combat")]
    public void CollectMasteryClaimed_FivePublicStatsTransitionsMapToCanonicalSkills(
        int masteryIndex,
        string expectedSkillId)
    {
        MasteryClaimedFact fact = new(
            IsLocalPlayer: true,
            OccurredDayIndex: 13,
            MasteryIndex: masteryIndex,
            PreviousClaimValue: 0,
            ObservedClaimValue: 1);

        GameEvent gameEvent = Assert.IsType<GameEvent>(
            PlayerProgressionEventCollector.CollectMasteryClaimed(fact));

        Assert.StartsWith("event-mastery-claimed-v1-", gameEvent.EventId, StringComparison.Ordinal);
        Assert.Equal("mastery_claimed", gameEvent.EventType);
        Assert.Equal("1", gameEvent.EventVersion);
        Assert.Equal("smapi.player.mastery_snapshot", gameEvent.Source);
        Assert.Equal(AudienceScope.Public, gameEvent.AudienceScope);
        Assert.Equal(expectedSkillId, gameEvent.Payload.GetProperty("skill_id").GetString());
        Assert.True(ContractValidator.Validate(gameEvent).IsValid);
        Assert.Equal(
            gameEvent.EventId,
            PlayerProgressionEventCollector.CollectMasteryClaimed(fact)?.EventId);
    }

    [Theory]
    [InlineData(false, 0, 0, 1)]
    [InlineData(true, -1, 0, 1)]
    [InlineData(true, 5, 0, 1)]
    [InlineData(true, 0, 0, 0)]
    [InlineData(true, 0, 1, 1)]
    [InlineData(true, 0, 1, 0)]
    public void CollectMasteryClaimed_RemoteUnknownOrNonClaimTransitionReturnsNull(
        bool isLocalPlayer,
        int masteryIndex,
        int previousClaimValue,
        int observedClaimValue)
    {
        GameEvent? result = PlayerProgressionEventCollector.CollectMasteryClaimed(
            new MasteryClaimedFact(
                isLocalPlayer,
                13,
                masteryIndex,
                previousClaimValue,
                observedClaimValue));

        Assert.Null(result);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 2)]
    [InlineData(2, 2)]
    public void CollectMasteryClaimed_InvalidPublicStatValueFailsClosed(
        int previousClaimValue,
        int observedClaimValue)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PlayerProgressionEventCollector.CollectMasteryClaimed(
                new MasteryClaimedFact(
                    true,
                    13,
                    0,
                    previousClaimValue,
                    observedClaimValue)));
    }

    [Fact]
    public void Collectors_InvalidOccurredDayFailsBeforeCreatingAnyIdentity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PlayerProgressionEventCollector.CollectSkillLevelReached(
                new LevelChangedFact(true, -1, "Farming", 0, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PlayerProgressionEventCollector.CollectMineDepthMilestone(
                new MineDepthMilestoneFact(true, -1, 0, 5)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PlayerProgressionEventCollector.CollectToolUpgradeReceived(
                new ToolUpgradeReceivedFact(true, -1, "axe", 1, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PlayerProgressionEventCollector.CollectMasteryClaimed(
                new MasteryClaimedFact(true, -1, 0, 0, 1)));
    }
}
