namespace StardewNpcAgent.Integration;

/// <summary>
/// 一次 DayStarted 后台生成绑定的 save/player/day/locale 与单调 session generation。
/// </summary>
public sealed record GenerationSessionToken(
    long Generation,
    string SaveId,
    string PlayerId,
    int GameDayIndex,
    string Locale);

/// <summary>
/// 不取消后台 Task，而是在主线程应用前拒绝迟到、跨存档或已失效结果。
/// </summary>
public sealed class SessionGenerationGate
{
    private readonly object synchronizationGate = new();
    private long generation;
    private string? saveId;
    private string? playerId;

    public void StartSession(string saveId, string playerId)
    {
        ValidateStableString(saveId, nameof(saveId));
        ValidateStableString(playerId, nameof(playerId));
        lock (synchronizationGate)
        {
            AdvanceGeneration();
            this.saveId = saveId;
            this.playerId = playerId;
        }
    }

    public GenerationSessionToken CaptureDay(int gameDayIndex, string locale)
    {
        if (gameDayIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(gameDayIndex));
        }

        ValidateStableString(locale, nameof(locale));
        lock (synchronizationGate)
        {
            if (saveId is null || playerId is null)
            {
                throw new InvalidOperationException("尚未建立 save session。");
            }

            return new GenerationSessionToken(
                generation,
                saveId,
                playerId,
                gameDayIndex,
                locale);
        }
    }

    public bool IsCurrent(
        GenerationSessionToken token,
        int currentDayIndex,
        string currentLocale)
    {
        ArgumentNullException.ThrowIfNull(token);
        lock (synchronizationGate)
        {
            return saveId is not null
                && playerId is not null
                && generation == token.Generation
                && currentDayIndex == token.GameDayIndex
                && string.Equals(currentLocale, token.Locale, StringComparison.Ordinal)
                && string.Equals(saveId, token.SaveId, StringComparison.Ordinal)
                && string.Equals(playerId, token.PlayerId, StringComparison.Ordinal);
        }
    }

    public void InvalidatePendingWork()
    {
        lock (synchronizationGate)
        {
            if (saveId is not null)
            {
                AdvanceGeneration();
            }
        }
    }

    public void EndSession()
    {
        lock (synchronizationGate)
        {
            AdvanceGeneration();
            saveId = null;
            playerId = null;
        }
    }

    private void AdvanceGeneration()
    {
        if (generation == long.MaxValue)
        {
            throw new InvalidOperationException("session generation 已达到可表示上限。");
        }

        generation++;
    }

    private static void ValidateStableString(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim())
        {
            throw new ArgumentException("值必须非空且无首尾空白。", parameterName);
        }
    }
}
