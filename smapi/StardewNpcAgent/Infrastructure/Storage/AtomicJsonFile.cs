using System.Security;
using System.Text.Json;

namespace StardewNpcAgent.Infrastructure.Storage;

/// <summary>
/// 为单个 class snapshot 提供同步 JSON 读取和同目录 copy-on-write 替换。
/// </summary>
/// <typeparam name="TSnapshot">由上层 outbox 定义的完整、可 JSON 序列化 snapshot 类型。</typeparam>
/// <remarks>
/// 写入先落到目标同目录的唯一隐藏临时文件，显式 flush 到磁盘后再通过 File.Move 覆盖 target。
/// 因此序列化或 flush 在 rename 前失败时不会先截断合法旧文件，同目录也避免跨文件系统 move。
///
/// final target 若为 symbolic link 会被拒绝，避免透明读写 outbox 根目录之外的文件。该边界仍不提供
/// 进程间锁、并发写者仲裁或历史版本保留，也不声称 flush + rename 在所有操作系统、文件系统和
/// 硬件缓存组合下都绝对抗断电。
/// </remarks>
internal sealed class AtomicJsonFile<TSnapshot>
    where TSnapshot : class
{
    private const int FileStreamBufferSize = 4096;
    private const string CorruptedContentMessage = "Outbox snapshot JSON 内容已损坏或不符合合同。";
    private const string NullRootMessage = "Outbox snapshot JSON 根值不能为 null。";
    private const string ReadFailureMessage = "无法读取 outbox snapshot。";
    private const string WriteFailureMessage = "无法持久化 outbox snapshot。";
    private readonly JsonSerializerOptions serializerOptions;
    private readonly Func<Guid> temporaryFileIdFactory;
    private readonly Func<string, FileStream> readStreamFactory;
    private readonly Action<string, string> moveReplace;

    /// <summary>
    /// 创建绑定到一个稳定绝对路径的 JSON 文件原语。
    /// </summary>
    /// <param name="absolutePath">目标 JSON 文件的绝对路径；相对路径会因 cwd 漂移而被拒绝。</param>
    /// <param name="serializerOptions">
    /// 上层合同使用的 JSON 配置。构造函数会复制配置，调用方之后的修改不会改变持久化格式。
    /// </param>
    /// <exception cref="ArgumentNullException">路径或 serializer options 为 null。</exception>
    /// <exception cref="ArgumentException">路径为空白或不是绝对路径。</exception>
    public AtomicJsonFile(string absolutePath, JsonSerializerOptions serializerOptions)
        : this(
            absolutePath,
            serializerOptions,
            Guid.NewGuid,
            OpenReadStream,
            MoveReplace)
    {
    }

    /// <summary>
    /// 创建可注入临时文件 ID 来源的实例，供同程序集测试确定性复现极低概率路径碰撞。
    /// </summary>
    /// <param name="absolutePath">目标 JSON 文件的绝对路径。</param>
    /// <param name="serializerOptions">会被复制的 JSON 配置。</param>
    /// <param name="temporaryFileIdFactory">每次 Write 用于生成唯一隐藏 temp 名的 GUID factory。</param>
    /// <remarks>
    /// 该重载保持 internal，不向 Mod 消费者扩张 API。正常生产构造函数始终使用 Guid.NewGuid；
    /// 注入 seam 只让测试能预创建精确碰撞文件，从而验证 CreateNew 失败时的所有权边界。
    /// </remarks>
    internal AtomicJsonFile(
        string absolutePath,
        JsonSerializerOptions serializerOptions,
        Func<Guid> temporaryFileIdFactory)
        : this(
            absolutePath,
            serializerOptions,
            temporaryFileIdFactory,
            OpenReadStream,
            MoveReplace)
    {
    }

    /// <summary>
    /// 创建可确定性注入读取失败和最终替换失败的 internal 测试实例。
    /// </summary>
    /// <param name="absolutePath">目标 JSON 文件绝对路径。</param>
    /// <param name="serializerOptions">会被复制的 JSON 配置。</param>
    /// <param name="temporaryFileIdFactory">temp 文件 GUID 来源。</param>
    /// <param name="readStreamFactory">只负责打开目标读取流；正常构造使用真实 FileStream。</param>
    /// <param name="moveReplace">只负责最终覆盖 move；正常构造使用 File.Move(overwrite: true)。</param>
    /// <remarks>
    /// seam 只覆盖两个难以稳定制造的 OS 故障点，不抽象目录创建、temp 写入、flush 或删除，
    /// 因此测试仍会执行真实 copy-on-write 文件流程，而不是退化成通用虚拟文件系统。
    /// </remarks>
    internal AtomicJsonFile(
        string absolutePath,
        JsonSerializerOptions serializerOptions,
        Func<Guid> temporaryFileIdFactory,
        Func<string, FileStream> readStreamFactory,
        Action<string, string> moveReplace)
    {
        ArgumentNullException.ThrowIfNull(absolutePath);
        if (string.IsNullOrWhiteSpace(absolutePath))
        {
            throw new ArgumentException("Atomic JSON 文件路径不能为空或纯空白。", nameof(absolutePath));
        }

        if (!Path.IsPathFullyQualified(absolutePath))
        {
            throw new ArgumentException("Atomic JSON 文件路径必须是绝对路径。", nameof(absolutePath));
        }

        ArgumentNullException.ThrowIfNull(serializerOptions);
        ArgumentNullException.ThrowIfNull(temporaryFileIdFactory);
        ArgumentNullException.ThrowIfNull(readStreamFactory);
        ArgumentNullException.ThrowIfNull(moveReplace);

        AbsolutePath = Path.GetFullPath(absolutePath);
        this.serializerOptions = new JsonSerializerOptions(serializerOptions);
        this.temporaryFileIdFactory = temporaryFileIdFactory;
        this.readStreamFactory = readStreamFactory;
        this.moveReplace = moveReplace;
    }

    /// <summary>
    /// 获取经 Path.GetFullPath 规范化后的稳定目标路径。
    /// </summary>
    public string AbsolutePath { get; }

    /// <summary>
    /// 尝试读取并反序列化完整 snapshot。
    /// </summary>
    /// <param name="snapshot">成功时为非 null snapshot；文件不存在时为 null。</param>
    /// <returns>文件存在且成功恢复 snapshot 时为 true；文件不存在时为 false。</returns>
    /// <exception cref="OutboxCorruptedException">JSON 截断、字段类型错误或根值为 null。</exception>
    /// <exception cref="OutboxPersistenceException">读取遭遇 I/O、权限或安全策略错误。</exception>
    public bool TryRead(out TSnapshot? snapshot)
    {
        snapshot = null;

        FileStream stream;
        try
        {
            ThrowIfFinalTargetIsSymbolicLink();

            // 直接尝试打开而不是先依赖 File.Exists。File.Exists 会把权限错误也折叠成 false，
            // 那会把“无法访问”误报成正常 miss；这里仅把明确的 missing 异常映射为 false。
            stream = readStreamFactory(AbsolutePath);
        }
        catch (FileNotFoundException)
        {
            // 文件在调用前或打开竞态中消失都等价于当前没有 snapshot；不创建任何路径。
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            // 父目录尚未初始化同样是正常 miss；目录只由 Write 按需创建。
            return false;
        }
        catch (UnauthorizedAccessException exception)
        {
            throw CreateReadPersistenceException(exception);
        }
        catch (SecurityException exception)
        {
            throw CreateReadPersistenceException(exception);
        }
        catch (IOException exception)
        {
            throw CreateReadPersistenceException(exception);
        }

        try
        {
            using (stream)
            {
                TSnapshot? deserialized = JsonSerializer.Deserialize<TSnapshot>(stream, serializerOptions);
                if (deserialized is null)
                {
                    throw new OutboxCorruptedException(NullRootMessage);
                }

                snapshot = deserialized;
                return true;
            }
        }
        catch (OutboxCorruptedException)
        {
            // null root 已经映射为稳定异常，不应再被下方通用错误边界改写。
            throw;
        }
        catch (JsonException exception)
        {
            // JsonException 可能包含 byte offset，但自定义外层消息绝不拼接文件正文。
            throw new OutboxCorruptedException(
                CorruptedContentMessage,
                exception);
        }
        catch (NotSupportedException exception)
        {
            throw new OutboxCorruptedException(
                CorruptedContentMessage,
                exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw CreateReadPersistenceException(exception);
        }
        catch (SecurityException exception)
        {
            throw CreateReadPersistenceException(exception);
        }
        catch (IOException exception)
        {
            // 流已成功打开后，任何 missing 派生异常都属于读取/converter 失败，不能再返回 miss。
            throw CreateReadPersistenceException(exception);
        }
    }

    /// <summary>
    /// 将完整 snapshot 写到同目录临时文件，flush 后原子替换 target。
    /// </summary>
    /// <param name="snapshot">待持久化的非 null 完整 snapshot。</param>
    /// <exception cref="ArgumentNullException">snapshot 为 null。</exception>
    /// <exception cref="OutboxPersistenceException">
    /// 创建目录、创建临时文件、序列化、flush 或最终替换中的任一阶段失败。
    /// </exception>
    public void Write(TSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        string? temporaryPath = null;
        bool temporaryFileOwned = false;
        try
        {
            string parentDirectory = GetRequiredParentDirectory();
            ThrowIfFinalTargetIsSymbolicLink();
            Directory.CreateDirectory(parentDirectory);

            temporaryPath = CreateUniqueTemporaryPath(parentDirectory);
            using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                FileStreamBufferSize,
                FileOptions.WriteThrough))
            {
                // 只有 CreateNew 构造成功返回，才证明该路径由本次 Write 创建并拥有。
                // 若路径预先存在，构造函数会先抛出，ownership 仍为 false，catch 绝不能删除它。
                temporaryFileOwned = true;

                // 同步序列化确保方法返回前所有 JSON 都已进入 FileStream；若 getter/converter 中途失败，
                // target 尚未被触碰，catch 会 best-effort 删除这个可能只写了一部分的 temp。
                JsonSerializer.Serialize(stream, snapshot, serializerOptions);
                stream.Flush(flushToDisk: true);
            }

            // 临时文件与 target 位于同一目录，因此不会发生普通的跨卷 move。overwrite 只在
            // temp 已完整序列化并 flush 后执行；绝不先打开或截断现有 target。
            // move 紧前再次检查，覆盖序列化期间 final target 被换成 symlink 的常见竞态窗口。
            ThrowIfFinalTargetIsSymbolicLink();
            moveReplace(temporaryPath, AbsolutePath);
            temporaryFileOwned = false;
            temporaryPath = null;
        }
        catch (Exception exception) when (IsFatalOrCancellation(exception))
        {
            // 取消和致命异常不能被伪装为可重试的持久化失败；仍先清理本次 owned temp，
            // 然后用 bare throw 保留原异常类型、实例和堆栈。
            if (temporaryFileOwned)
            {
                BestEffortDeleteTemporaryFile(temporaryPath);
            }

            throw;
        }
        catch (Exception exception)
        {
            // 清理失败不能覆盖真正的持久化根因；原异常始终保存在稳定异常的 InnerException。
            if (temporaryFileOwned)
            {
                BestEffortDeleteTemporaryFile(temporaryPath);
            }

            throw new OutboxPersistenceException(
                WriteFailureMessage,
                exception);
        }
    }

    /// <summary>
    /// 判断异常是否必须在清理 owned temp 后原样逸出，禁止包装为持久化异常。
    /// </summary>
    /// <param name="exception">Write 捕获到的原始异常。</param>
    /// <returns>取消信号或不应被普通恢复逻辑吞掉的致命异常返回 true。</returns>
    private static bool IsFatalOrCancellation(Exception exception)
    {
        return exception is OperationCanceledException
            or OutOfMemoryException
            or AccessViolationException;
    }

    /// <summary>
    /// 拒绝 final target symbolic link，避免读取链接目标或用 rename 替换链接本身。
    /// </summary>
    /// <exception cref="IOException">final target 是文件或目录 symbolic link。</exception>
    private void ThrowIfFinalTargetIsSymbolicLink()
    {
        FileInfo targetInfo = new(AbsolutePath);
        if (targetInfo.LinkTarget is not null)
        {
            throw new IOException("Final outbox snapshot target 不能是 symbolic link。");
        }
    }

    /// <summary>
    /// 正常构造使用的真实目标读取函数。
    /// </summary>
    /// <param name="path">目标 JSON 文件绝对路径。</param>
    /// <returns>允许并发读取和原子替换的 FileStream。</returns>
    private static FileStream OpenReadStream(string path)
    {
        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete);
    }

    /// <summary>
    /// 正常构造使用的真实最终替换函数。
    /// </summary>
    /// <param name="source">已经完整写入并 flush 的 owned temp。</param>
    /// <param name="destination">最终 target。</param>
    private static void MoveReplace(string source, string destination)
    {
        File.Move(source, destination, overwrite: true);
    }

    /// <summary>
    /// 获取目标父目录；根目录本身不是合法文件 target，因此以 I/O 根因进入统一写失败边界。
    /// </summary>
    /// <returns>用于创建临时文件和 target 的绝对父目录。</returns>
    private string GetRequiredParentDirectory()
    {
        string? parentDirectory = Path.GetDirectoryName(AbsolutePath);
        if (string.IsNullOrEmpty(parentDirectory) || string.IsNullOrEmpty(Path.GetFileName(AbsolutePath)))
        {
            throw new IOException($"Atomic JSON 目标路径必须指向文件：{AbsolutePath}");
        }

        return parentDirectory;
    }

    /// <summary>
    /// 生成目标同目录、以点开头且带 GUID 的唯一临时路径。
    /// </summary>
    /// <param name="parentDirectory">已经创建的 target 父目录。</param>
    /// <returns>尚未创建的临时文件绝对路径；FileMode.CreateNew 最终强制不覆盖任何碰撞文件。</returns>
    private string CreateUniqueTemporaryPath(string parentDirectory)
    {
        string targetFileName = Path.GetFileName(AbsolutePath);
        string temporaryFileName = $".{targetFileName}.{temporaryFileIdFactory():N}.tmp";
        return Path.Combine(parentDirectory, temporaryFileName);
    }

    /// <summary>
    /// 构造稳定读取异常，同时保留真实 I/O 或权限异常。
    /// </summary>
    /// <param name="innerException">打开或读取文件时的原始异常。</param>
    /// <returns>供上层统一捕获的持久化异常。</returns>
    private OutboxPersistenceException CreateReadPersistenceException(Exception innerException)
    {
        return new OutboxPersistenceException(
            ReadFailureMessage,
            innerException);
    }

    /// <summary>
    /// 删除本次 Write 创建的临时文件；任何清理错误都不得掩盖原始写失败。
    /// </summary>
    /// <param name="temporaryPath">本次写入的临时路径；尚未创建或 move 已成功时为 null。</param>
    private static void BestEffortDeleteTemporaryFile(string? temporaryPath)
    {
        if (temporaryPath is null)
        {
            return;
        }

        try
        {
            File.Delete(temporaryPath);
        }
        catch (IOException)
        {
            // best-effort 清理失败不能掩盖原始 Write 异常。
        }
        catch (UnauthorizedAccessException)
        {
            // 权限变化可能阻止删除；保留原始 Write 异常供上层诊断。
        }
        catch (SecurityException)
        {
            // 安全策略拒绝删除时同样只保留原始 Write 异常。
        }
    }
}
