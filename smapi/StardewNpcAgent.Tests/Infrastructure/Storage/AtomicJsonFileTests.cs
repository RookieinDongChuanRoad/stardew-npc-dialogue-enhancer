using System.Text.Json;
using System.Text.Json.Serialization;
using StardewNpcAgent.Infrastructure.Storage;

namespace StardewNpcAgent.Tests.Infrastructure.Storage;

/// <summary>
/// 使用真实临时目录验证共享 JSON 文件原语的持久化边界。
/// </summary>
/// <remarks>
/// 这些测试刻意不 mock 文件系统：copy-on-write 是否保留旧 target、是否清理本次临时文件，
/// 只有通过真实文件字节和目录内容才能可靠验证。每个测试实例使用独立目录，避免并行执行时互相污染。
/// </remarks>
public sealed class AtomicJsonFileTests : IDisposable
{
    private readonly string testDirectory;

    /// <summary>
    /// 为当前测试创建唯一目录；目录位于系统临时区，不会接触真实游戏 Mods。
    /// </summary>
    public AtomicJsonFileTests()
    {
        testDirectory = Path.Combine(
            Path.GetTempPath(),
            "StardewNpcAgent.Tests",
            $"AtomicJsonFile.{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
    }

    /// <summary>
    /// 不存在的文件属于正常 miss：返回 false/null，并且不得顺手创建父目录或目标文件。
    /// </summary>
    [Fact]
    public void TryRead_WhenFileIsMissing_ReturnsFalseWithoutCreatingAnything()
    {
        string missingParent = Path.Combine(testDirectory, "missing-parent");
        string targetPath = Path.Combine(missingParent, "snapshot.json");
        AtomicJsonFile<TestSnapshot> file = CreateFile<TestSnapshot>(targetPath);

        bool found = file.TryRead(out TestSnapshot? snapshot);

        Assert.False(found);
        Assert.Null(snapshot);
        Assert.False(Directory.Exists(missingParent));
        Assert.False(File.Exists(targetPath));
    }

    /// <summary>
    /// 合法 snapshot 必须经真实 JSON 文件完整 round-trip，证明读写使用同一 serializer 配置。
    /// </summary>
    [Fact]
    public void WriteAndTryRead_WithValidSnapshot_RoundTripsJson()
    {
        string targetPath = Path.Combine(testDirectory, "snapshot.json");
        AtomicJsonFile<TestSnapshot> file = CreateFile<TestSnapshot>(targetPath);
        TestSnapshot expected = new("Abigail", 42);

        file.Write(expected);
        bool found = file.TryRead(out TestSnapshot? actual);

        Assert.True(found);
        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// Write 应负责创建缺失父目录，让上层 outbox 不必重复处理目录初始化竞态。
    /// </summary>
    [Fact]
    public void Write_WhenParentDirectoryIsMissing_CreatesParentDirectory()
    {
        string targetPath = Path.Combine(testDirectory, "nested", "storage", "snapshot.json");
        AtomicJsonFile<TestSnapshot> file = CreateFile<TestSnapshot>(targetPath);

        file.Write(new TestSnapshot("Leah", 7));

        Assert.True(File.Exists(targetPath));
        Assert.True(file.TryRead(out TestSnapshot? snapshot));
        Assert.Equal(new TestSnapshot("Leah", 7), snapshot);
    }

    /// <summary>
    /// 第二次写入必须整体替换旧 JSON，而不是追加、原地截断后重写或保留旧字段字节。
    /// </summary>
    [Fact]
    public void Write_WhenTargetAlreadyExists_ReplacesWholeSnapshot()
    {
        string targetPath = Path.Combine(testDirectory, "snapshot.json");
        AtomicJsonFile<TestSnapshot> file = CreateFile<TestSnapshot>(targetPath);
        file.Write(new TestSnapshot("FirstValueMustDisappear", 1));

        TestSnapshot replacement = new("Sebastian", 2);
        file.Write(replacement);

        Assert.True(file.TryRead(out TestSnapshot? actual));
        Assert.Equal(replacement, actual);
        Assert.DoesNotContain("FirstValueMustDisappear", File.ReadAllText(targetPath), StringComparison.Ordinal);
    }

    /// <summary>
    /// 成功 rename 后本次唯一隐藏临时文件必须消失，避免长期运行积累垃圾文件。
    /// </summary>
    [Fact]
    public void Write_WhenSuccessful_LeavesNoTemporaryFile()
    {
        string targetPath = Path.Combine(testDirectory, "snapshot.json");
        AtomicJsonFile<TestSnapshot> file = CreateFile<TestSnapshot>(targetPath);

        file.Write(new TestSnapshot("Penny", 3));

        Assert.Empty(FindTemporaryFiles(targetPath));
    }

    /// <summary>
    /// CreateNew 命中预存在的同名 temp 时，本次 Write 从未取得该文件所有权，绝不能把它当垃圾删除。
    /// </summary>
    [Fact]
    public void Write_WhenTemporaryPathCollides_PreservesUnownedFileAndDoesNotCreateTarget()
    {
        Guid knownTemporaryId = Guid.Parse("b2ffb0b7-d273-41ca-af75-58978f52de21");
        string targetPath = Path.Combine(testDirectory, "snapshot.json");
        string collidingTemporaryPath = Path.Combine(
            testDirectory,
            $".snapshot.json.{knownTemporaryId:N}.tmp");
        byte[] originalCollisionBytes = System.Text.Encoding.UTF8.GetBytes("pre-existing-unowned-temp");
        File.WriteAllBytes(collidingTemporaryPath, originalCollisionBytes);
        AtomicJsonFile<TestSnapshot> file = new(
            targetPath,
            CreateSerializerOptions(),
            temporaryFileIdFactory: () => knownTemporaryId);

        OutboxPersistenceException exception = Assert.Throws<OutboxPersistenceException>(
            () => file.Write(new TestSnapshot("Emily", 11)));

        Assert.IsAssignableFrom<IOException>(exception.InnerException);
        Assert.True(File.Exists(collidingTemporaryPath));
        Assert.Equal(originalCollisionBytes, File.ReadAllBytes(collidingTemporaryPath));
        Assert.False(File.Exists(targetPath));
    }

    /// <summary>
    /// 截断 JSON 不能伪装成 miss；应映射为稳定的损坏异常，且消息不得泄漏文件正文。
    /// </summary>
    [Fact]
    public void TryRead_WhenJsonIsTruncated_ThrowsCorruptedExceptionWithoutFileContents()
    {
        const string secretBodyFragment = "SECRET_BODY_MUST_NOT_APPEAR";
        const string secretPathFragment = "SECRET_USER_PATH";
        string secretParent = Path.Combine(testDirectory, secretPathFragment);
        Directory.CreateDirectory(secretParent);
        string targetPath = Path.Combine(secretParent, "snapshot.json");
        File.WriteAllText(targetPath, $"{{\"Name\":\"{secretBodyFragment}\"");
        AtomicJsonFile<TestSnapshot> file = CreateFile<TestSnapshot>(targetPath);

        OutboxCorruptedException exception = Assert.Throws<OutboxCorruptedException>(
            () => file.TryRead(out _));

        Assert.IsType<JsonException>(exception.InnerException);
        Assert.DoesNotContain(secretBodyFragment, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secretPathFragment, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(testDirectory, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// JSON 根值为 null 不满足 class snapshot 合同，必须作为持久化内容损坏处理。
    /// </summary>
    [Fact]
    public void TryRead_WhenJsonRootIsNull_ThrowsCorruptedException()
    {
        string targetPath = Path.Combine(testDirectory, "snapshot.json");
        File.WriteAllText(targetPath, "null");
        AtomicJsonFile<TestSnapshot> file = CreateFile<TestSnapshot>(targetPath);

        Assert.Throws<OutboxCorruptedException>(() => file.TryRead(out _));
    }

    /// <summary>
    /// JSON 字段类型不符合 snapshot 合同时同样是内容损坏，而不是普通 I/O 故障。
    /// </summary>
    [Fact]
    public void TryRead_WhenJsonValueHasWrongType_ThrowsCorruptedException()
    {
        string targetPath = Path.Combine(testDirectory, "snapshot.json");
        File.WriteAllText(targetPath, "{\"name\":\"Harvey\",\"count\":\"not-an-integer\"}");
        AtomicJsonFile<TestSnapshot> file = CreateFile<TestSnapshot>(targetPath);

        OutboxCorruptedException exception = Assert.Throws<OutboxCorruptedException>(
            () => file.TryRead(out _));

        Assert.IsType<JsonException>(exception.InnerException);
    }

    /// <summary>
    /// 目标流已经成功打开后，converter 再抛 missing 异常属于读取/转换失败，不能伪装成文件 miss。
    /// </summary>
    /// <param name="throwDirectoryNotFound">true 验证 DirectoryNotFound；false 验证 FileNotFound。</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TryRead_WhenOpenedConverterThrowsMissingException_ThrowsPersistenceException(
        bool throwDirectoryNotFound)
    {
        string targetPath = Path.Combine(testDirectory, "snapshot.json");
        File.WriteAllText(targetPath, "{}");
        JsonSerializerOptions options = CreateSerializerOptions();
        options.Converters.Add(new ThrowingReadConverter(
            () => throwDirectoryNotFound
                ? new DirectoryNotFoundException("converter dependency directory missing")
                : new FileNotFoundException("converter dependency file missing")));
        AtomicJsonFile<TestSnapshot> file = new(targetPath, options);

        OutboxPersistenceException exception = Assert.Throws<OutboxPersistenceException>(
            () => file.TryRead(out _));

        if (throwDirectoryNotFound)
        {
            Assert.IsType<DirectoryNotFoundException>(exception.InnerException);
        }
        else
        {
            Assert.IsType<FileNotFoundException>(exception.InnerException);
        }
    }

    /// <summary>
    /// converter 明确表示 snapshot 类型不受支持时，文件无法按当前合同恢复，应稳定映射为 Corrupted。
    /// </summary>
    [Fact]
    public void TryRead_WhenConverterThrowsNotSupportedException_ThrowsCorruptedException()
    {
        string targetPath = Path.Combine(testDirectory, "snapshot.json");
        File.WriteAllText(targetPath, "{}");
        JsonSerializerOptions options = CreateSerializerOptions();
        options.Converters.Add(new ThrowingReadConverter(
            () => new NotSupportedException("snapshot type is not supported")));
        AtomicJsonFile<TestSnapshot> file = new(targetPath, options);

        OutboxCorruptedException exception = Assert.Throws<OutboxCorruptedException>(
            () => file.TryRead(out _));

        Assert.IsType<NotSupportedException>(exception.InnerException);
    }

    /// <summary>
    /// 目标路径若实际是目录，不得被当作“文件不存在”；这是稳定的持久化访问失败。
    /// </summary>
    [Fact]
    public void TryRead_WhenTargetIsDirectory_ThrowsPersistenceException()
    {
        string targetPath = Path.Combine(testDirectory, "snapshot.json");
        Directory.CreateDirectory(targetPath);
        AtomicJsonFile<TestSnapshot> file = CreateFile<TestSnapshot>(targetPath);

        OutboxPersistenceException exception = Assert.Throws<OutboxPersistenceException>(
            () => file.TryRead(out _));

        Assert.NotNull(exception.InnerException);
    }

    /// <summary>
    /// final target 是真实文件 symlink 时必须拒绝读取，避免透明跟随到 outbox 根目录之外。
    /// </summary>
    [Fact]
    public void TryRead_WhenFinalTargetIsSymbolicLink_ThrowsPersistenceExceptionWithoutFollowingLink()
    {
        const string secretPathFragment = "SECRET_USER_PATH";
        string secretParent = Path.Combine(testDirectory, secretPathFragment);
        Directory.CreateDirectory(secretParent);
        string realTargetPath = Path.Combine(testDirectory, "real-snapshot.json");
        string symbolicLinkPath = Path.Combine(secretParent, "snapshot.json");
        File.WriteAllText(realTargetPath, "{\"name\":\"Linus\",\"count\":18}");
        if (!TryCreateFileSymbolicLink(symbolicLinkPath, realTargetPath))
        {
            return;
        }

        AtomicJsonFile<TestSnapshot> file = CreateFile<TestSnapshot>(symbolicLinkPath);

        OutboxPersistenceException exception = Assert.Throws<OutboxPersistenceException>(
            () => file.TryRead(out _));

        Assert.NotNull(new FileInfo(symbolicLinkPath).LinkTarget);
        Assert.DoesNotContain(secretPathFragment, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 真实文件访问被操作系统拒绝时不能降级为 miss；必须保留底层权限/I/O 根因并映射为稳定异常。
    /// </summary>
    [Fact]
    public void TryRead_WhenReadStreamFactoryDeniesAccess_ThrowsSafePersistenceException()
    {
        const string secretPathFragment = "SECRET_USER_PATH";
        string targetPath = Path.Combine(testDirectory, secretPathFragment, "snapshot.json");
        UnauthorizedAccessException denied = new("read denied for diagnostic inner exception");
        AtomicJsonFile<TestSnapshot> file = new(
            targetPath,
            CreateSerializerOptions(),
            temporaryFileIdFactory: Guid.NewGuid,
            readStreamFactory: _ => throw denied,
            moveReplace: static (source, destination) => File.Move(source, destination, overwrite: true));

        OutboxPersistenceException exception = Assert.Throws<OutboxPersistenceException>(
            () => file.TryRead(out _));

        Assert.Same(denied, exception.InnerException);
        Assert.DoesNotContain(secretPathFragment, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(testDirectory, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 相对路径依赖进程 cwd，可能把 outbox 写到不可预测位置，因此构造阶段必须立即拒绝。
    /// </summary>
    [Fact]
    public void Constructor_WhenPathIsRelative_ThrowsArgumentException()
    {
        string relativeParent = $"AtomicJsonFile.RelativePath.{Guid.NewGuid():N}";
        string relativeTargetPath = Path.Combine(relativeParent, "snapshot.json");
        string absoluteParent = Path.Combine(Directory.GetCurrentDirectory(), relativeParent);
        string absoluteTargetPath = Path.Combine(absoluteParent, "snapshot.json");

        try
        {
            Assert.Throws<ArgumentException>(
                () => new AtomicJsonFile<TestSnapshot>(relativeTargetPath, CreateSerializerOptions()));

            Assert.False(Directory.Exists(absoluteParent));
            Assert.False(File.Exists(absoluteTargetPath));
        }
        finally
        {
            // 清理仅限本测试生成的唯一 cwd 子路径，防止回归实现产生副作用后污染工作区。
            if (File.Exists(absoluteTargetPath))
            {
                File.Delete(absoluteTargetPath);
            }

            if (Directory.Exists(absoluteParent))
            {
                Directory.Delete(absoluteParent, recursive: true);
            }
        }
    }

    /// <summary>
    /// null、空字符串和纯空白都不能表示稳定文件身份，应在任何文件系统副作用前失败。
    /// </summary>
    /// <param name="invalidPath">待验证的无效路径。</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_WhenPathIsNullOrWhiteSpace_ThrowsArgumentException(string? invalidPath)
    {
        Assert.ThrowsAny<ArgumentException>(
            () => new AtomicJsonFile<TestSnapshot>(invalidPath!, CreateSerializerOptions()));
    }

    /// <summary>
    /// null serializer options 是调用合同错误，必须在任何目录或文件副作用前明确拒绝。
    /// </summary>
    [Fact]
    public void Constructor_WhenSerializerOptionsIsNull_ThrowsBeforeFileSystemSideEffects()
    {
        string missingParent = Path.Combine(testDirectory, "must-not-be-created");
        string targetPath = Path.Combine(missingParent, "snapshot.json");

        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => new AtomicJsonFile<TestSnapshot>(targetPath, serializerOptions: null!));

        Assert.Equal("serializerOptions", exception.ParamName);
        Assert.False(Directory.Exists(missingParent));
        Assert.False(File.Exists(targetPath));
    }

    /// <summary>
    /// internal 测试 seam 的 GUID factory 也必须在构造阶段校验，不能把 null 延迟到 Write 后再产生目录。
    /// </summary>
    [Fact]
    public void Constructor_WhenTemporaryFileIdFactoryIsNull_ThrowsBeforeFileSystemSideEffects()
    {
        string missingParent = Path.Combine(testDirectory, "must-not-be-created");
        string targetPath = Path.Combine(missingParent, "snapshot.json");

        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => new AtomicJsonFile<TestSnapshot>(
                targetPath,
                CreateSerializerOptions(),
                temporaryFileIdFactory: null!));

        Assert.Equal("temporaryFileIdFactory", exception.ParamName);
        Assert.False(Directory.Exists(missingParent));
        Assert.False(File.Exists(targetPath));
    }

    /// <summary>
    /// 对外暴露的 AbsolutePath 必须是 Path.GetFullPath 规范化结果，供日志和锁身份稳定复用。
    /// </summary>
    [Fact]
    public void Constructor_WithAbsolutePath_NormalizesStoredPath()
    {
        string inputPath = Path.Combine(testDirectory, "nested", "..", "snapshot.json");

        AtomicJsonFile<TestSnapshot> file = CreateFile<TestSnapshot>(inputPath);

        Assert.Equal(Path.GetFullPath(inputPath), file.AbsolutePath);
    }

    /// <summary>
    /// serializer options 必须在构造时复制；调用方之后修改原对象不能改变落盘 JSON 形状。
    /// </summary>
    [Fact]
    public void Constructor_CopiesSerializerOptions()
    {
        string targetPath = Path.Combine(testDirectory, "snapshot.json");
        JsonSerializerOptions callerOptions = CreateSerializerOptions();
        AtomicJsonFile<TestSnapshot> file = new(targetPath, callerOptions);

        callerOptions.PropertyNamingPolicy = null;
        file.Write(new TestSnapshot("Robin", 9));

        string json = File.ReadAllText(targetPath);
        Assert.Contains("\"name\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Name\"", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// null snapshot 是调用合同错误，必须在创建父目录、temp 或 target 之前失败。
    /// </summary>
    [Fact]
    public void Write_WhenSnapshotIsNull_ThrowsBeforeFileSystemSideEffects()
    {
        string missingParent = Path.Combine(testDirectory, "must-not-be-created");
        string targetPath = Path.Combine(missingParent, "snapshot.json");
        AtomicJsonFile<TestSnapshot> file = CreateFile<TestSnapshot>(targetPath);

        Exception? exception = Record.Exception(() => file.Write(snapshot: null!));

        Assert.False(Directory.Exists(missingParent));
        Assert.False(File.Exists(targetPath));
        ArgumentNullException argumentException = Assert.IsType<ArgumentNullException>(exception);
        Assert.Equal("snapshot", argumentException.ParamName);
    }

    /// <summary>
    /// target 是目录时最终 move 必须失败为稳定持久化异常，并清理已经 flush 的临时文件。
    /// </summary>
    [Fact]
    public void Write_WhenTargetIsDirectory_ThrowsPersistenceExceptionAndCleansTemporaryFile()
    {
        string targetPath = Path.Combine(testDirectory, "snapshot.json");
        Directory.CreateDirectory(targetPath);
        AtomicJsonFile<TestSnapshot> file = CreateFile<TestSnapshot>(targetPath);

        OutboxPersistenceException exception = Assert.Throws<OutboxPersistenceException>(
            () => file.Write(new TestSnapshot("Maru", 5)));

        Assert.NotNull(exception.InnerException);
        Assert.True(Directory.Exists(targetPath));
        Assert.Empty(FindTemporaryFiles(targetPath));
    }

    /// <summary>
    /// final target 是 symlink 时 Write 必须在替换前拒绝，不能跟随或把链接本身替换成普通文件。
    /// </summary>
    [Fact]
    public void Write_WhenFinalTargetIsSymbolicLink_ThrowsPersistenceAndPreservesLinkAndTarget()
    {
        string realTargetPath = Path.Combine(testDirectory, "real-snapshot.json");
        string symbolicLinkPath = Path.Combine(testDirectory, "snapshot.json");
        byte[] originalTargetBytes = System.Text.Encoding.UTF8.GetBytes("real-target-must-remain-unchanged");
        File.WriteAllBytes(realTargetPath, originalTargetBytes);
        if (!TryCreateFileSymbolicLink(symbolicLinkPath, realTargetPath))
        {
            return;
        }

        AtomicJsonFile<TestSnapshot> file = CreateFile<TestSnapshot>(symbolicLinkPath);

        Assert.Throws<OutboxPersistenceException>(
            () => file.Write(new TestSnapshot("Krobus", 19)));

        Assert.NotNull(new FileInfo(symbolicLinkPath).LinkTarget);
        Assert.Equal(originalTargetBytes, File.ReadAllBytes(realTargetPath));
        Assert.Empty(FindTemporaryFiles(symbolicLinkPath));
    }

    /// <summary>
    /// target 在序列化期间才被换成 symlink 时，最终 move 前必须再次检查并拒绝替换链接。
    /// </summary>
    [Fact]
    public void Write_WhenTargetBecomesSymbolicLinkDuringSerialization_RejectsBeforeMove()
    {
        string probeTargetPath = Path.Combine(testDirectory, "probe-target.json");
        string probeLinkPath = Path.Combine(testDirectory, "probe-link.json");
        File.WriteAllText(probeTargetPath, "probe");
        if (!TryCreateFileSymbolicLink(probeLinkPath, probeTargetPath))
        {
            return;
        }

        File.Delete(probeLinkPath);
        string realTargetPath = Path.Combine(testDirectory, "real-snapshot.json");
        string symbolicLinkPath = Path.Combine(testDirectory, "snapshot.json");
        byte[] originalTargetBytes = System.Text.Encoding.UTF8.GetBytes("real-target-must-remain-unchanged");
        File.WriteAllBytes(realTargetPath, originalTargetBytes);
        AtomicJsonFile<SymlinkCreatingSnapshot> file = CreateFile<SymlinkCreatingSnapshot>(symbolicLinkPath);
        SymlinkCreatingSnapshot snapshot = new(
            "Elliott",
            () => File.CreateSymbolicLink(symbolicLinkPath, realTargetPath));

        Assert.Throws<OutboxPersistenceException>(() => file.Write(snapshot));

        Assert.NotNull(new FileInfo(symbolicLinkPath).LinkTarget);
        Assert.Equal(originalTargetBytes, File.ReadAllBytes(realTargetPath));
        Assert.Empty(FindTemporaryFiles(symbolicLinkPath));
    }

    /// <summary>
    /// 序列化在写到一半后失败时，旧 target 字节必须完全不变，本次 partial temp 也必须删除。
    /// </summary>
    [Fact]
    public void Write_WhenSerializationFails_PreservesOldBytesAndCleansTemporaryFile()
    {
        string targetPath = Path.Combine(testDirectory, "snapshot.json");
        AtomicJsonFile<ConditionallyThrowingSnapshot> file =
            CreateFile<ConditionallyThrowingSnapshot>(targetPath);
        file.Write(new ConditionallyThrowingSnapshot("known-good", throwFromGetter: false));
        byte[] originalBytes = File.ReadAllBytes(targetPath);

        OutboxPersistenceException exception = Assert.Throws<OutboxPersistenceException>(
            () => file.Write(new ConditionallyThrowingSnapshot("cannot-complete", throwFromGetter: true)));

        Assert.NotNull(exception.InnerException);
        Assert.Equal(originalBytes, File.ReadAllBytes(targetPath));
        Assert.Empty(FindTemporaryFiles(targetPath));
    }

    /// <summary>
    /// temp 已完整序列化并 flush 后若最终替换失败，旧 target 必须逐字节不变且 owned temp 被删除。
    /// </summary>
    [Fact]
    public void Write_WhenMoveReplaceFailsAfterFlush_PreservesTargetAndCleansOwnedTemporaryFile()
    {
        const string secretPathFragment = "SECRET_USER_PATH";
        string secretParent = Path.Combine(testDirectory, secretPathFragment);
        Directory.CreateDirectory(secretParent);
        string targetPath = Path.Combine(secretParent, "snapshot.json");
        byte[] originalBytes = System.Text.Encoding.UTF8.GetBytes("known-good-target-bytes");
        File.WriteAllBytes(targetPath, originalBytes);
        Guid knownTemporaryId = Guid.Parse("6cb25dd8-6e21-4daf-ac6c-17f569d336ad");
        string expectedTemporaryPath = Path.Combine(
            secretParent,
            $".snapshot.json.{knownTemporaryId:N}.tmp");
        TestSnapshot replacement = new("Wizard", 77);
        JsonSerializerOptions options = CreateSerializerOptions();
        bool moveWasCalled = false;
        TestSnapshot? flushedTemporarySnapshot = null;
        IOException moveFailure = new("injected move failure with diagnostic details");
        AtomicJsonFile<TestSnapshot> file = new(
            targetPath,
            options,
            temporaryFileIdFactory: () => knownTemporaryId,
            readStreamFactory: OpenRealReadStream,
            moveReplace: (source, destination) =>
            {
                moveWasCalled = true;
                using FileStream temporaryStream = OpenRealReadStream(source);
                flushedTemporarySnapshot = JsonSerializer.Deserialize<TestSnapshot>(temporaryStream, options);
                throw moveFailure;
            });

        OutboxPersistenceException exception = Assert.Throws<OutboxPersistenceException>(
            () => file.Write(replacement));

        Assert.True(moveWasCalled);
        Assert.Equal(replacement, flushedTemporarySnapshot);
        Assert.Same(moveFailure, exception.InnerException);
        Assert.Equal(originalBytes, File.ReadAllBytes(targetPath));
        Assert.False(File.Exists(expectedTemporaryPath));
        Assert.DoesNotContain(secretPathFragment, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(testDirectory, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 取消信号不是持久化故障；清理 owned temp 后必须保留原 OperationCanceledException 实例。
    /// </summary>
    [Fact]
    public void Write_WhenSnapshotGetterCancels_RethrowsOriginalAndPreservesTarget()
    {
        AssertExceptionalSerializationFailureIsRethrown(
            new OperationCanceledException("intentional cancellation"));
    }

    /// <summary>
    /// OutOfMemoryException 属于致命资源失败，不能被包装成可重试的 PersistenceException。
    /// </summary>
    [Fact]
    public void Write_WhenSnapshotGetterThrowsOutOfMemory_RethrowsOriginalAndPreservesTarget()
    {
        AssertExceptionalSerializationFailureIsRethrown(
            new OutOfMemoryException("intentional fatal test exception"));
    }

    /// <summary>
    /// AccessViolationException 同样属于致命异常；测试只抛安全构造实例，不进行真实非法内存访问。
    /// </summary>
    [Fact]
    public void Write_WhenSnapshotGetterThrowsAccessViolation_RethrowsOriginalAndPreservesTarget()
    {
        AssertExceptionalSerializationFailureIsRethrown(
            new AccessViolationException("intentional fatal test exception"));
    }

    /// <summary>
    /// 三个稳定异常必须公开并保留指定基类；共享文件实现本身仍保持 internal。
    /// </summary>
    [Fact]
    public void PublicSurface_ExposesStableExceptionsButKeepsAtomicJsonFileInternal()
    {
        IOException innerException = new("disk failure");
        OutboxCorruptedException corrupted = new("corrupted", innerException);
        OutboxPersistenceException persistence = new("persistence", innerException);
        OutboxIdentityConflictException conflict = new("conflict");

        Assert.True(typeof(OutboxCorruptedException).IsPublic);
        Assert.True(typeof(OutboxPersistenceException).IsPublic);
        Assert.True(typeof(OutboxIdentityConflictException).IsPublic);
        Assert.IsAssignableFrom<IOException>(corrupted);
        Assert.IsAssignableFrom<IOException>(persistence);
        Assert.IsAssignableFrom<InvalidOperationException>(conflict);
        Assert.Same(innerException, corrupted.InnerException);
        Assert.Same(innerException, persistence.InnerException);
        Assert.Equal("conflict", conflict.Message);
        Assert.True(typeof(AtomicJsonFile<TestSnapshot>).IsNotPublic);
    }

    /// <summary>
    /// 删除本测试创建的唯一临时目录；测试逻辑从不持有真实 Mods 路径。
    /// </summary>
    public void Dispose()
    {
        if (Directory.Exists(testDirectory))
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    /// <summary>
    /// 创建使用 camelCase 的测试实例，同时复用 production 构造参数形状。
    /// </summary>
    /// <typeparam name="TSnapshot">待持久化的 class snapshot 类型。</typeparam>
    /// <param name="targetPath">当前测试唯一的绝对目标路径。</param>
    /// <returns>指向真实测试文件系统的内部原子文件实例。</returns>
    private static AtomicJsonFile<TSnapshot> CreateFile<TSnapshot>(string targetPath)
        where TSnapshot : class
    {
        return new AtomicJsonFile<TSnapshot>(targetPath, CreateSerializerOptions());
    }

    /// <summary>
    /// 返回独立 options，便于测试调用方后续修改与 production 内部副本互不影响。
    /// </summary>
    private static JsonSerializerOptions CreateSerializerOptions()
    {
        return new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
    }

    /// <summary>
    /// 按 production 临时文件命名合同查找 target 同目录的残留文件。
    /// </summary>
    /// <param name="targetPath">用于派生父目录和目标文件名的绝对路径。</param>
    /// <returns>当前仍存在的本 target 临时文件路径。</returns>
    private static IReadOnlyList<string> FindTemporaryFiles(string targetPath)
    {
        string? parentDirectory = Path.GetDirectoryName(targetPath);
        if (parentDirectory is null || !Directory.Exists(parentDirectory))
        {
            return Array.Empty<string>();
        }

        string targetFileName = Path.GetFileName(targetPath);
        return Directory
            .EnumerateFiles(parentDirectory, $".{targetFileName}.*.tmp", SearchOption.TopDirectoryOnly)
            .ToArray();
    }

    /// <summary>
    /// 验证取消/致命 serializer 异常的共同安全边界：原样逸出、旧 target 不变、owned temp 删除。
    /// </summary>
    /// <typeparam name="TException">必须原样逸出的异常类型。</typeparam>
    /// <param name="expectedException">getter 将抛出的同一异常实例。</param>
    private void AssertExceptionalSerializationFailureIsRethrown<TException>(TException expectedException)
        where TException : Exception
    {
        string targetPath = Path.Combine(testDirectory, "snapshot.json");
        AtomicJsonFile<ExceptionThrowingSnapshot> file = CreateFile<ExceptionThrowingSnapshot>(targetPath);
        file.Write(new ExceptionThrowingSnapshot("known-good", exceptionFromGetter: null));
        byte[] originalBytes = File.ReadAllBytes(targetPath);

        TException actualException = Assert.Throws<TException>(
            () => file.Write(new ExceptionThrowingSnapshot("cannot-complete", expectedException)));

        Assert.Same(expectedException, actualException);
        Assert.Equal(originalBytes, File.ReadAllBytes(targetPath));
        Assert.Empty(FindTemporaryFiles(targetPath));
    }

    /// <summary>
    /// 以 production 相同的共享模式打开真实读取流，供只替换 move 行为的测试构造重载使用。
    /// </summary>
    /// <param name="path">目标 JSON 文件绝对路径。</param>
    /// <returns>允许并发读取和原子替换的真实 FileStream。</returns>
    private static FileStream OpenRealReadStream(string path)
    {
        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete);
    }

    /// <summary>
    /// 创建指向真实文件的 final-target symlink；只在明确不支持或 Windows 权限不足时跳过行为断言。
    /// </summary>
    /// <param name="linkPath">待创建的链接路径。</param>
    /// <param name="targetPath">链接指向的真实文件。</param>
    /// <returns>链接成功创建时为 true；当前平台明确不可用时为 false。</returns>
    private static bool TryCreateFileSymbolicLink(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
        {
            // 部分 Windows 环境未启用 Developer Mode 且测试进程没有创建 symlink 的权限。
            return false;
        }
        catch (IOException) when (OperatingSystem.IsWindows())
        {
            // Windows 文件系统或策略可能明确拒绝 symlink；Unix 上的 IOException 不应被隐藏。
            return false;
        }
    }

    /// <summary>
    /// 代表后续 Event/ACK outbox 可以复用的普通 class snapshot；本 Task 不实现具体 outbox。
    /// </summary>
    /// <param name="Name">可读业务标识。</param>
    /// <param name="Count">用于验证数值类型错误和完整 round-trip 的计数。</param>
    private sealed record TestSnapshot(string Name, int Count);

    /// <summary>
    /// 通过真实 getter 异常模拟 serializer 写出部分 JSON 后失败，不引入文件系统 mock。
    /// </summary>
    private sealed class ConditionallyThrowingSnapshot
    {
        private readonly string payload;
        private readonly bool throwFromGetter;

        /// <summary>
        /// 创建可成功或故意失败的 snapshot。
        /// </summary>
        /// <param name="payload">成功时写入 JSON 的内容。</param>
        /// <param name="throwFromGetter">为 true 时在 serializer 读取属性时抛出异常。</param>
        public ConditionallyThrowingSnapshot(string payload, bool throwFromGetter)
        {
            this.payload = payload;
            this.throwFromGetter = throwFromGetter;
        }

        /// <summary>
        /// serializer 访问的公开属性；故意在指定实例上失败以验证 copy-on-write 回滚边界。
        /// </summary>
        public string Payload
        {
            get
            {
                if (throwFromGetter)
                {
                    throw new InvalidOperationException("Intentional serialization failure for atomicity test.");
                }

                return payload;
            }
        }
    }

    /// <summary>
    /// 让 serializer 在读取公开属性时抛出调用方指定异常，用于测试异常透传与 temp 清理。
    /// </summary>
    private sealed class ExceptionThrowingSnapshot
    {
        private readonly string payload;
        private readonly Exception? exceptionFromGetter;

        /// <summary>
        /// 创建正常或异常 snapshot。
        /// </summary>
        /// <param name="payload">无异常时返回的 JSON 字符串值。</param>
        /// <param name="exceptionFromGetter">非 null 时由 Payload getter 原样抛出。</param>
        public ExceptionThrowingSnapshot(string payload, Exception? exceptionFromGetter)
        {
            this.payload = payload;
            this.exceptionFromGetter = exceptionFromGetter;
        }

        /// <summary>
        /// serializer 读取的唯一公开属性。
        /// </summary>
        public string Payload
        {
            get
            {
                if (exceptionFromGetter is not null)
                {
                    throw exceptionFromGetter;
                }

                return payload;
            }
        }
    }

    /// <summary>
    /// serializer 首次读取 Payload 时执行文件系统动作，用于复现写入中的 final-target symlink 竞态。
    /// </summary>
    private sealed class SymlinkCreatingSnapshot
    {
        private readonly string payload;
        private readonly Action createSymbolicLink;

        /// <summary>
        /// 创建会在属性读取时执行一次 symlink 动作的 snapshot。
        /// </summary>
        /// <param name="payload">序列化字符串值。</param>
        /// <param name="createSymbolicLink">在 final move 前创建 target symlink 的动作。</param>
        public SymlinkCreatingSnapshot(string payload, Action createSymbolicLink)
        {
            this.payload = payload;
            this.createSymbolicLink = createSymbolicLink;
        }

        /// <summary>
        /// serializer 读取时先创建 symlink，再返回普通 payload。
        /// </summary>
        public string Payload
        {
            get
            {
                createSymbolicLink();
                return payload;
            }
        }
    }

    /// <summary>
    /// 在真实目标流已经打开后，从 converter 内确定性抛出指定异常，用于冻结读取阶段分类。
    /// </summary>
    private sealed class ThrowingReadConverter : JsonConverter<TestSnapshot>
    {
        private readonly Func<Exception> exceptionFactory;

        /// <summary>
        /// 创建每次读取时生成新异常实例的 converter，避免复用已带堆栈的异常。
        /// </summary>
        /// <param name="exceptionFactory">构造本次 Read 应抛异常的 factory。</param>
        public ThrowingReadConverter(Func<Exception> exceptionFactory)
        {
            this.exceptionFactory = exceptionFactory;
        }

        /// <inheritdoc />
        public override TestSnapshot? Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            throw exceptionFactory();
        }

        /// <inheritdoc />
        public override void Write(
            Utf8JsonWriter writer,
            TestSnapshot value,
            JsonSerializerOptions options)
        {
            throw new NotSupportedException("ThrowingReadConverter 仅用于读取异常分类测试。");
        }
    }
}
