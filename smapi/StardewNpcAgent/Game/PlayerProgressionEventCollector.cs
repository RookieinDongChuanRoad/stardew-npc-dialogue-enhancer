using System.Globalization;
using System.Text.Json;
using StardewNpcAgent.Contracts;

namespace StardewNpcAgent.Game;

/// <summary>
/// 玩家矿洞 high-water 变化在主线程冻结后的纯事实。
/// </summary>
/// <param name="IsLocalPlayer">是否属于当前本地玩家；首批不为远端 farmhand 生产事实。</param>
/// <param name="OccurredDayIndex">观察到新 high-water 的绝对游戏日。</param>
/// <param name="PreviousRawDepth">本 producer 已确认的旧 public deepestMineLevel。</param>
/// <param name="ObservedRawDepth">Warped 后读取的最新 public deepestMineLevel。</param>
public sealed record MineDepthMilestoneFact(
    bool IsLocalPlayer,
    int OccurredDayIndex,
    int PreviousRawDepth,
    int ObservedRawDepth);

/// <summary>
/// 一次“升级工具已被玩家领取”的主线程确认事实。
/// </summary>
/// <param name="IsLocalPlayer">是否属于当前本地玩家。</param>
/// <param name="OccurredDayIndex">领取发生的绝对游戏日。</param>
/// <param name="ToolId">运行时从公开类型映射出的 canonical 工具 ID。</param>
/// <param name="PendingUpgradeLevel">领取前公开 pending tool 声明的目标等级。</param>
/// <param name="ReceivedUpgradeLevel">InventoryChanged/MenuChanged 确认玩家实际拥有的等级。</param>
/// <remarks>
/// 事实必须同时包含 pending 与 received 两侧。仅仅下单、倒计时归零或从箱子取出旧工具都不能构造
/// 一个匹配事实，因此不会被 collector 误认为升级完成。
/// </remarks>
public sealed record ToolUpgradeReceivedFact(
    bool IsLocalPlayer,
    int OccurredDayIndex,
    string ToolId,
    int PendingUpgradeLevel,
    int ReceivedUpgradeLevel);

/// <summary>
/// 关闭公开 MasteryTrackerMenu 后，对单个 public mastery Stats key 做出的纯差分事实。
/// </summary>
/// <param name="IsLocalPlayer">是否属于当前本地玩家。</param>
/// <param name="OccurredDayIndex">确认领取的绝对游戏日。</param>
/// <param name="MasteryIndex">Constants.StatKeys.Mastery(index) 使用的 0～4 固定索引。</param>
/// <param name="PreviousClaimValue">打开菜单前冻结的公开 Stats 值。</param>
/// <param name="ObservedClaimValue">关闭菜单后读取的公开 Stats 值。</param>
public sealed record MasteryClaimedFact(
    bool IsLocalPlayer,
    int OccurredDayIndex,
    int MasteryIndex,
    int PreviousClaimValue,
    int ObservedClaimValue);

/// <summary>
/// 把玩家成长领域的明确公开事实映射为结构化、可持久化的共享 GameEvent。
/// </summary>
/// <remarks>
/// 本类是纯映射层：不读取 Game1、不持有 baseline、不写 outbox，也不启动网络。运行时必须先在
/// SMAPI 主线程冻结事实，再调用这里；返回非空事件后仍要由 DurableEventOutbox 同步落盘。所有
/// 白名单都使用 canonical ID，不使用本地化工具名、技能名或 UI 文本参与 payload/identity。
/// </remarks>
public static class PlayerProgressionEventCollector
{
    private const string EventVersion = "1";
    private const int MaximumVanillaSkillLevel = 10;
    private const int MaximumToolUpgradeLevel = 4;
    private const int LastRegularMineRawDepth = 120;
    private const int QuarryMineRawDepth = 77377;

    private static readonly HashSet<string> SupportedToolIds = new(StringComparer.Ordinal)
    {
        "axe",
        "pickaxe",
        "hoe",
        "watering_can",
        "pan",
        "trash_can",
    };

    /// <summary>
    /// 将 SMAPI LevelChanged 的本地五技能升级映射为新的明确 wire event。
    /// </summary>
    /// <param name="fact">主线程复制出的 LevelChanged 最小事实。</param>
    /// <returns>合法升级事件；远端、非增长、Luck 或 Mod 技能返回 null。</returns>
    public static GameEvent? CollectSkillLevelReached(LevelChangedFact fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        ValidateOccurredDay(fact.OccurredDayIndex);
        if (!fact.IsLocalPlayer)
        {
            return null;
        }

        if (fact.OldLevel < 0
            || fact.OldLevel > MaximumVanillaSkillLevel
            || fact.NewLevel < 0
            || fact.NewLevel > MaximumVanillaSkillLevel)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fact),
                $"原版技能等级必须在 0～{MaximumVanillaSkillLevel} 之间。");
        }

        if (fact.NewLevel <= fact.OldLevel)
        {
            return null;
        }

        string? skillId = TryMapVanillaSkillName(fact.SkillName);
        if (skillId is null)
        {
            // Luck 与未注册 Mod 技能没有完成端到端 producer 验收，首批明确忽略。
            return null;
        }

        string eventType = "skill_level_reached";
        string eventId = "event-skill-level-reached-v1-" + GameEventCollector.ComputeIdentity(
            eventType,
            EventVersion,
            FormatInteger(fact.OccurredDayIndex),
            skillId,
            FormatInteger(fact.OldLevel),
            FormatInteger(fact.NewLevel));
        return CreatePublicEvent(
            eventId,
            eventType,
            fact.OccurredDayIndex,
            source: "smapi.player.level_changed",
            JsonSerializer.SerializeToElement(
                new
                {
                    skill_id = skillId,
                    old_level = fact.OldLevel,
                    new_level = fact.NewLevel,
                }));
    }

    /// <summary>
    /// 将 public deepestMineLevel 的真正 high-water 增长压缩为一个最高新里程碑。
    /// </summary>
    /// <param name="fact">Warped 后冻结的旧、新 raw high-water。</param>
    /// <returns>
    /// 普通矿井每五层或骷髅洞穴 25/50/100/100n 的最高新阈值；未跨阈值、重复、回退和 Quarry 返回 null。
    /// </returns>
    public static GameEvent? CollectMineDepthMilestone(MineDepthMilestoneFact fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        ValidateOccurredDay(fact.OccurredDayIndex);
        if (!fact.IsLocalPlayer)
        {
            return null;
        }

        if (fact.PreviousRawDepth < 0 || fact.ObservedRawDepth < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fact), "矿洞 raw depth 不能为负。");
        }

        if (fact.ObservedRawDepth <= fact.PreviousRawDepth
            || fact.ObservedRawDepth == QuarryMineRawDepth
            || fact.PreviousRawDepth == QuarryMineRawDepth)
        {
            // Quarry 是原版 side-branch sentinel，不是普通矿井或骷髅洞穴进度。
            return null;
        }

        MineMilestone? milestone = TryCreateMineMilestone(
            fact.PreviousRawDepth,
            fact.ObservedRawDepth);
        if (milestone is null)
        {
            return null;
        }

        string eventType = "mine_depth_milestone_reached";
        string eventId = "event-mine-depth-milestone-v1-" + GameEventCollector.ComputeIdentity(
            eventType,
            EventVersion,
            FormatInteger(fact.OccurredDayIndex),
            milestone.MineId,
            FormatInteger(milestone.MilestoneDepth),
            FormatInteger(milestone.ObservedDepth));
        return CreatePublicEvent(
            eventId,
            eventType,
            fact.OccurredDayIndex,
            source: "smapi.player.warped",
            JsonSerializer.SerializeToElement(
                new
                {
                    mine_id = milestone.MineId,
                    milestone_depth = milestone.MilestoneDepth,
                    observed_depth = milestone.ObservedDepth,
                }));
    }

    /// <summary>
    /// 只在 canonical pending tool 与玩家实际收到的升级等级完全相同时生产领取事实。
    /// </summary>
    public static GameEvent? CollectToolUpgradeReceived(ToolUpgradeReceivedFact fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        ValidateOccurredDay(fact.OccurredDayIndex);
        if (!fact.IsLocalPlayer)
        {
            return null;
        }

        ValidateStableIdentifier(fact.ToolId, nameof(fact.ToolId));
        if (fact.PendingUpgradeLevel < 0
            || fact.PendingUpgradeLevel > MaximumToolUpgradeLevel
            || fact.ReceivedUpgradeLevel < 0
            || fact.ReceivedUpgradeLevel > MaximumToolUpgradeLevel)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fact),
                $"工具升级等级必须在 0～{MaximumToolUpgradeLevel} 之间。");
        }

        if (!SupportedToolIds.Contains(fact.ToolId)
            || fact.ReceivedUpgradeLevel == 0
            || fact.PendingUpgradeLevel != fact.ReceivedUpgradeLevel)
        {
            return null;
        }

        string eventType = "tool_upgrade_received";
        string eventId = "event-tool-upgrade-received-v1-" + GameEventCollector.ComputeIdentity(
            eventType,
            EventVersion,
            FormatInteger(fact.OccurredDayIndex),
            fact.ToolId,
            FormatInteger(fact.ReceivedUpgradeLevel));
        return CreatePublicEvent(
            eventId,
            eventType,
            fact.OccurredDayIndex,
            source: "smapi.player.tool_upgrade_observed",
            JsonSerializer.SerializeToElement(
                new
                {
                    tool_id = fact.ToolId,
                    upgrade_level = fact.ReceivedUpgradeLevel,
                }));
    }

    /// <summary>
    /// 将五个 public mastery Stats key 的精确 0→1 变化映射为领取事实。
    /// </summary>
    public static GameEvent? CollectMasteryClaimed(MasteryClaimedFact fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        ValidateOccurredDay(fact.OccurredDayIndex);
        if (!fact.IsLocalPlayer)
        {
            return null;
        }

        if (fact.PreviousClaimValue < 0
            || fact.PreviousClaimValue > 1
            || fact.ObservedClaimValue < 0
            || fact.ObservedClaimValue > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fact),
                "Mastery public Stats 值只允许 0 或 1。");
        }

        string? skillId = TryMapMasteryIndex(fact.MasteryIndex);
        if (skillId is null
            || fact.PreviousClaimValue != 0
            || fact.ObservedClaimValue != 1)
        {
            return null;
        }

        string eventType = "mastery_claimed";
        string eventId = "event-mastery-claimed-v1-" + GameEventCollector.ComputeIdentity(
            eventType,
            EventVersion,
            FormatInteger(fact.OccurredDayIndex),
            skillId);
        return CreatePublicEvent(
            eventId,
            eventType,
            fact.OccurredDayIndex,
            source: "smapi.player.mastery_snapshot",
            JsonSerializer.SerializeToElement(new { skill_id = skillId }));
    }

    /// <summary>
    /// 根据 raw high-water 所属矿区计算一个最高新阈值，不回填所有中间层。
    /// </summary>
    private static MineMilestone? TryCreateMineMilestone(
        int previousRawDepth,
        int observedRawDepth)
    {
        if (observedRawDepth <= LastRegularMineRawDepth)
        {
            int milestoneDepth = (observedRawDepth / 5) * 5;
            int previousMilestoneDepth = (previousRawDepth / 5) * 5;
            return milestoneDepth >= 5 && milestoneDepth > previousMilestoneDepth
                ? new MineMilestone("the_mines", milestoneDepth, observedRawDepth)
                : null;
        }

        int observedDisplayDepth = observedRawDepth - LastRegularMineRawDepth;
        int previousDisplayDepth = previousRawDepth > LastRegularMineRawDepth
            ? previousRawDepth - LastRegularMineRawDepth
            : 0;
        int milestone = GetSkullCavernMilestone(observedDisplayDepth);
        int previousMilestone = GetSkullCavernMilestone(previousDisplayDepth);
        return milestone > previousMilestone
            ? new MineMilestone("skull_cavern", milestone, observedDisplayDepth)
            : null;
    }

    /// <summary>
    /// 骷髅洞穴首批阈值为 25、50、100，之后每 100 层一个节点。
    /// </summary>
    private static int GetSkullCavernMilestone(int displayDepth)
    {
        if (displayDepth < 25)
        {
            return 0;
        }

        if (displayDepth < 50)
        {
            return 25;
        }

        if (displayDepth < 100)
        {
            return 50;
        }

        return (displayDepth / 100) * 100;
    }

    /// <summary>
    /// SMAPI 的英文技能名只映射五个已验收原版技能；比较不依赖当前游戏语言。
    /// </summary>
    private static string? TryMapVanillaSkillName(string skillName)
    {
        ValidateStableIdentifier(skillName, nameof(skillName));
        return skillName.ToLowerInvariant() switch
        {
            "farming" => "farming",
            "fishing" => "fishing",
            "foraging" => "foraging",
            "mining" => "mining",
            "combat" => "combat",
            _ => null,
        };
    }

    /// <summary>
    /// 冻结 Stardew Valley 1.6.15 的 public mastery Stats 索引映射。
    /// </summary>
    private static string? TryMapMasteryIndex(int masteryIndex)
    {
        return masteryIndex switch
        {
            0 => "farming",
            1 => "fishing",
            2 => "foraging",
            3 => "mining",
            4 => "combat",
            _ => null,
        };
    }

    /// <summary>
    /// 统一构造 public audience 事件，避免四个 producer 漂移基础 envelope 字段。
    /// </summary>
    private static GameEvent CreatePublicEvent(
        string eventId,
        string eventType,
        int occurredDayIndex,
        string source,
        JsonElement payload)
    {
        return new GameEvent
        {
            EventId = eventId,
            EventType = eventType,
            EventVersion = EventVersion,
            OccurredDayIndex = occurredDayIndex,
            Source = source,
            AudienceScope = AudienceScope.Public,
            AudienceNpcId = null,
            Payload = payload,
        };
    }

    /// <summary>
    /// 所有事实在计算 identity 前都必须具备有效的绝对游戏日。
    /// </summary>
    private static void ValidateOccurredDay(int occurredDayIndex)
    {
        if (occurredDayIndex < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(occurredDayIndex),
                occurredDayIndex,
                "occurredDayIndex 必须大于等于 0。");
        }
    }

    /// <summary>
    /// 验证运行时传入的 canonical identifier 非空且没有首尾空白；不做静默 Trim。
    /// </summary>
    private static void ValidateStableIdentifier(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("标识必须非空且不能包含首尾空白。", parameterName);
        }
    }

    /// <summary>
    /// 所有 identity 数值都使用 invariant 十进制，避免本机区域设置改变 hash。
    /// </summary>
    private static string FormatInteger(int value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// collector 内部已经换算为展示层数的矿洞里程碑。
    /// </summary>
    private sealed record MineMilestone(
        string MineId,
        int MilestoneDepth,
        int ObservedDepth);
}
