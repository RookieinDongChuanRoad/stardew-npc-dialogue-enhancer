using StardewNpcAgent.Contracts;
using StardewNpcAgent.Game;
using StardewNpcAgent.Infrastructure.Storage;

namespace StardewNpcAgent.Integration;

/// <summary>
/// 管理公共设施的 save-commit baseline：DayEnding 只 stage，Saved 才 durable enqueue。
/// </summary>
/// <remarks>
/// 设施状态来自 MasterPlayer 的公开世界真值，但事件写入当前本地 save/player outbox 分区。首次加载
/// 只建立 baseline；保存失败或 ReturnedToTitle 调用 <see cref="DiscardStaged"/> 后不产生 memory。
/// </remarks>
public sealed class WorldProgressionSessionState
{
    private readonly DurableEventOutbox eventOutbox;
    private Dictionary<string, bool>? baseline;
    private StagedFacilitySnapshot? staged;

    public WorldProgressionSessionState(DurableEventOutbox eventOutbox)
    {
        this.eventOutbox = eventOutbox ?? throw new ArgumentNullException(nameof(eventOutbox));
    }

    /// <summary>
    /// 当前等待 Saved 确认的 false→true 数量。
    /// </summary>
    public int StagedTransitionCount => staged?.Transitions.Count ?? 0;

    /// <summary>
    /// SaveLoaded 用完整五项公开状态建立 baseline，不回填已经为 true 的设施。
    /// </summary>
    public void InitializeBaseline(IReadOnlyDictionary<string, bool> currentSnapshot)
    {
        baseline = ValidateAndCopySnapshot(currentSnapshot);
        staged = null;
    }

    /// <summary>
    /// DayEnding 冻结本日 save 候选；这里不写 outbox，也不推进 baseline。
    /// </summary>
    public void StageDayEnding(
        int occurredDayIndex,
        IReadOnlyDictionary<string, bool> currentSnapshot)
    {
        if (occurredDayIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(occurredDayIndex));
        }

        Dictionary<string, bool> current = ValidateAndCopySnapshot(currentSnapshot);
        Dictionary<string, bool> currentBaseline = baseline
            ?? throw new InvalidOperationException("Facility baseline 尚未初始化。");
        List<PublicFacilityRestoredFact> transitions = new();
        foreach (PublicFacilityDefinition definition in WorldProgressionEventCollector.Facilities)
        {
            bool wasRestored = currentBaseline[definition.FacilityId];
            bool isRestored = current[definition.FacilityId];
            if (!wasRestored && isRestored)
            {
                transitions.Add(
                    new PublicFacilityRestoredFact(
                        occurredDayIndex,
                        definition.FacilityId,
                        WasRestored: false,
                        IsRestored: true));
            }
        }

        staged = new StagedFacilitySnapshot(
            occurredDayIndex,
            current,
            Array.AsReadOnly(transitions.ToArray()));
    }

    /// <summary>
    /// SMAPI Saved 确认成功后，按 registry 顺序 durable enqueue，并在全部调用返回后推进 baseline。
    /// </summary>
    /// <returns>本次提交的设施事件数；无 stage 或无 transition 返回 0。</returns>
    public int CommitSaved()
    {
        StagedFacilitySnapshot? currentStage = staged;
        if (currentStage is null)
        {
            return 0;
        }

        int committed = 0;
        foreach (PublicFacilityRestoredFact transition in currentStage.Transitions)
        {
            GameEvent gameEvent = WorldProgressionEventCollector.CollectPublicFacilityRestored(transition)
                ?? throw new InvalidOperationException("Staged facility transition 未通过冻结 collector。");
            eventOutbox.Enqueue(gameEvent);
            committed++;
        }

        // 若中途写失败，staged 与 baseline 都保持旧值；已写入的前缀可在 Saved 重试时幂等重放。
        baseline = new Dictionary<string, bool>(currentStage.CurrentSnapshot, StringComparer.Ordinal);
        staged = null;
        return committed;
    }

    /// <summary>
    /// 保存未确认或返回标题时丢弃 stage；durable baseline 保持最近一次 Saved 状态。
    /// </summary>
    public void DiscardStaged()
    {
        staged = null;
    }

    /// <summary>
    /// 返回当前 baseline 的只读副本。
    /// </summary>
    public IReadOnlyDictionary<string, bool> SnapshotBaseline()
    {
        Dictionary<string, bool> current = baseline
            ?? throw new InvalidOperationException("Facility baseline 尚未初始化。");
        return new System.Collections.ObjectModel.ReadOnlyDictionary<string, bool>(
            new Dictionary<string, bool>(current, StringComparer.Ordinal));
    }

    /// <summary>
    /// snapshot 必须逐字包含 registry 的五个 key，禁止 missing/unknown/duplicate 的模糊恢复。
    /// </summary>
    private static Dictionary<string, bool> ValidateAndCopySnapshot(
        IReadOnlyDictionary<string, bool> snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        string[] expected = WorldProgressionEventCollector.Facilities
            .Select(item => item.FacilityId)
            .ToArray();
        if (snapshot.Count != expected.Length
            || expected.Any(facilityId => !snapshot.ContainsKey(facilityId))
            || snapshot.Keys.Any(
                key => !expected.Contains(key, StringComparer.Ordinal)))
        {
            throw new ArgumentException(
                "Facility snapshot 必须精确包含五个 canonical facility ID。",
                nameof(snapshot));
        }

        return expected.ToDictionary(
            facilityId => facilityId,
            facilityId => snapshot[facilityId],
            StringComparer.Ordinal);
    }

    private sealed record StagedFacilitySnapshot(
        int OccurredDayIndex,
        IReadOnlyDictionary<string, bool> CurrentSnapshot,
        IReadOnlyList<PublicFacilityRestoredFact> Transitions);
}
