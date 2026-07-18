using StardewNpcAgent.Game;
using StardewNpcAgent.Infrastructure.Storage;

namespace StardewNpcAgent.Integration;

/// <summary>
/// 主线程从 staging cache 成功提交到 live cache 的不可变结果。
/// </summary>
public sealed record DialogueActivationResult(
    IReadOnlyList<DailyDialogueCacheEntry> ActivatedEntries);

/// <summary>
/// 在后台生成完成后，用当前重新解析候选二次验证并提交 live cache。
/// </summary>
public static class StagedDialogueActivator
{
    public static DialogueActivationResult Activate(
        SessionGenerationGate sessionGate,
        GenerationSessionToken token,
        int currentDayIndex,
        string currentLocale,
        IReadOnlyList<DialogueCandidate> currentCandidates,
        DailyDialogueCache stagingCache,
        DailyDialogueCache liveCache)
    {
        ArgumentNullException.ThrowIfNull(sessionGate);
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(currentCandidates);
        ArgumentNullException.ThrowIfNull(stagingCache);
        ArgumentNullException.ThrowIfNull(liveCache);
        if (!sessionGate.IsCurrent(token, currentDayIndex, currentLocale))
        {
            return new DialogueActivationResult(Array.Empty<DailyDialogueCacheEntry>());
        }

        Dictionary<string, DialogueCandidate> candidatesByNpc = new(StringComparer.Ordinal);
        HashSet<string> duplicateNpcIds = new(StringComparer.Ordinal);
        foreach (DialogueCandidate candidate in currentCandidates)
        {
            if (!candidatesByNpc.TryAdd(candidate.NpcId, candidate))
            {
                duplicateNpcIds.Add(candidate.NpcId);
            }
        }

        List<DailyDialogueCacheEntry> activated = new();
        foreach (DailyDialogueCacheEntry entry in stagingCache.Snapshot())
        {
            DailyDialogueCacheKey key = entry.Key;
            if (key.GameDayIndex != token.GameDayIndex
                || !string.Equals(key.Locale, token.Locale, StringComparison.Ordinal)
                || duplicateNpcIds.Contains(key.NpcId)
                || !candidatesByNpc.TryGetValue(key.NpcId, out DialogueCandidate? candidate)
                || !entry.HasCompleteGenerationMetadata
                || !DialogueSourceClassifier.MatchesIdentity(
                    entry.SourceFamily,
                    key.NpcId,
                    key.AssetName,
                    key.DialogueKey)
                || candidate.SourceFamily != entry.SourceFamily
                || !string.Equals(candidate.Locale, key.Locale, StringComparison.Ordinal)
                || !string.Equals(candidate.AssetName, key.AssetName, StringComparison.Ordinal)
                || !string.Equals(candidate.DialogueKey, key.DialogueKey, StringComparison.Ordinal)
                || !string.Equals(candidate.SourceText, entry.SourceText, StringComparison.Ordinal)
                || !string.Equals(candidate.SourceHash, entry.SourceHash, StringComparison.Ordinal))
            {
                continue;
            }

            liveCache.Store(entry);
            activated.Add(entry);
        }

        return new DialogueActivationResult(Array.AsReadOnly(activated.ToArray()));
    }
}
