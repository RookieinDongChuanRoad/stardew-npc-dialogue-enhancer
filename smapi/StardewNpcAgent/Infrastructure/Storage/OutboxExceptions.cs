namespace StardewNpcAgent.Infrastructure.Storage;

/// <summary>
/// 表示持久化 JSON 已存在，但其内容无法恢复为受支持的 outbox snapshot。
/// </summary>
/// <remarks>
/// 这是供后续 Event/ACK outbox 复用的稳定公开异常。外层消息只描述安全逻辑类别，
/// 不包含绝对路径、用户名或文件正文，避免玩家数据和本机身份进入普通日志。
/// InnerException 仅限受控诊断，底层 serializer/OS 异常仍可能包含路径；普通日志不得直接
/// 输出完整 inner exception、完整异常链或 ToString()。
/// </remarks>
public sealed class OutboxCorruptedException : IOException
{
    /// <summary>
    /// 创建内容损坏异常。
    /// </summary>
    /// <param name="message">不包含原始文件正文的稳定诊断消息。</param>
    /// <param name="innerException">仅供受控诊断的底层 JSON 异常；可能包含路径，不得直接写入普通日志。</param>
    public OutboxCorruptedException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// 表示 outbox snapshot 因文件系统、权限、序列化或原子替换失败而未能持久化。
/// </summary>
/// <remarks>
/// 调用方可以稳定捕获此类型决定重试或降级，同时通过 InnerException 保留真实根因；
/// 本异常不承诺失败一定可重试，磁盘已满、权限不足和 serializer 错误都可能进入此边界。
/// 外层 Message 不包含绝对路径或用户名。InnerException 仅限受控诊断且可能包含 OS 路径，
/// 普通日志不得直接输出完整 inner exception、完整异常链或 ToString()。
/// </remarks>
public sealed class OutboxPersistenceException : IOException
{
    /// <summary>
    /// 创建持久化失败异常并保留底层根因。
    /// </summary>
    /// <param name="message">描述目标文件和失败阶段的稳定消息。</param>
    /// <param name="innerException">仅供受控诊断的底层文件系统或序列化异常；不得直接写入普通日志。</param>
    public OutboxPersistenceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// 表示后续 outbox 层发现同一稳定身份对应互相冲突的不可合并内容。
/// </summary>
/// <remarks>
/// 当前共享文件原语不解释业务身份，因此不会主动抛出此异常；先在基础设施层冻结公开类型，
/// 让后续 Event 与 ACK outbox 使用同一错误合同而无需再次扩张公共 API。
/// </remarks>
public sealed class OutboxIdentityConflictException : InvalidOperationException
{
    /// <summary>
    /// 创建不可自动解决的 outbox 身份冲突异常。
    /// </summary>
    /// <param name="message">描述冲突身份的稳定消息；调用方应避免写入完整 payload。</param>
    public OutboxIdentityConflictException(string message)
        : base(message)
    {
    }
}
