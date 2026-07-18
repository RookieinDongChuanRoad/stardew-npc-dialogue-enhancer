using System.Collections.Concurrent;

namespace StardewNpcAgent.Integration;

/// <summary>
/// 后台任务向 SMAPI 主线程提交有限完成动作的进程内队列。
/// </summary>
/// <remarks>
/// 后台 continuation 只能 Enqueue，不得直接读取/写入 Game1、NPC、content asset 或 live cache；
/// `UpdateTicked` 在主线程调用 <see cref="Drain"/> 后才执行这些动作。
/// </remarks>
public sealed class MainThreadCompletionQueue
{
    private readonly ConcurrentQueue<Action> pendingActions = new();

    public int PendingCount => pendingActions.Count;

    public void Enqueue(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        pendingActions.Enqueue(action);
    }

    /// <summary>
    /// 按 FIFO 执行当前及执行期间新入队的动作，每项至多一次。
    /// </summary>
    /// <returns>本次实际执行的 action 数。</returns>
    public int Drain()
    {
        int drained = 0;
        while (pendingActions.TryDequeue(out Action? action))
        {
            action();
            drained++;
        }

        return drained;
    }
}
