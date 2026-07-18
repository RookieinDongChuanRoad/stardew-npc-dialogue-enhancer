using System.Text;
using StardewNpcAgent.Configuration;

namespace StardewNpcAgent.Tests.Configuration;

/// <summary>
/// 冻结已有 <c>config.json</c> 的只读字段存在性检查合同。
/// </summary>
public sealed class ModConfigFileInspectorTests : IDisposable
{
    private readonly string testDirectory = Path.Combine(
        Path.GetTempPath(),
        $"StardewNpcAgent.ConfigInspector.{Guid.NewGuid():N}");

    /// <summary>
    /// 旧配置没有 <c>TargetNpcIds</c> 字段时必须被识别为兼容分支，而且检查过程不能改写文件。
    /// </summary>
    [Fact]
    public void Inspect_ExistingObjectWithoutTargetNpcIds_ReturnsMissingAndPreservesBytes()
    {
        Directory.CreateDirectory(testDirectory);
        string configPath = Path.Combine(testDirectory, "config.json");
        byte[] originalBytes = Encoding.UTF8.GetBytes(
            "{\n  \"EnableAgentDialogue\": false\n}\n");
        File.WriteAllBytes(configPath, originalBytes);

        TargetNpcIdsFieldState state = ModConfigFileInspector.Inspect(testDirectory);

        Assert.Equal(TargetNpcIdsFieldState.MissingFromExistingConfiguration, state);
        Assert.Equal(originalBytes, File.ReadAllBytes(configPath));
    }

    /// <summary>
    /// 只有文件真实不存在时才是新配置；这个状态允许 ModEntry 使用 SMAPI 正常的首次配置生成路径。
    /// </summary>
    [Fact]
    public void Inspect_MissingConfigFile_ReturnsNewConfiguration()
    {
        Directory.CreateDirectory(testDirectory);

        TargetNpcIdsFieldState state = ModConfigFileInspector.Inspect(testDirectory);

        Assert.Equal(TargetNpcIdsFieldState.NewConfiguration, state);
        Assert.False(File.Exists(Path.Combine(testDirectory, "config.json")));
    }

    /// <summary>
    /// 显式 null 与数组都属于可解析 object，但二者的兼容语义不同，必须在反序列化前保留区别。
    /// </summary>
    [Theory]
    [InlineData("{\"TargetNpcIds\":null}", TargetNpcIdsFieldState.ExplicitNull)]
    [InlineData("{\"TargetNpcIds\":[]}", TargetNpcIdsFieldState.ExplicitValue)]
    [InlineData("{\"TargetNpcIds\":[\"Leah\"]}", TargetNpcIdsFieldState.ExplicitValue)]
    public void Inspect_ExplicitNullOrArray_ReturnsExactStateAndPreservesBytes(
        string configJson,
        TargetNpcIdsFieldState expectedState)
    {
        byte[] originalBytes = WriteConfig(configJson);

        TargetNpcIdsFieldState state = ModConfigFileInspector.Inspect(testDirectory);

        Assert.Equal(expectedState, state);
        Assert.Equal(originalBytes, ReadConfigBytes());
    }

    /// <summary>
    /// SMAPI/Newtonsoft 对属性名大小写不敏感；单个 lower-camel 字段必须保留与 PascalCase 相同的语义。
    /// </summary>
    [Theory]
    [InlineData("{\"targetNpcIds\":null}", TargetNpcIdsFieldState.ExplicitNull)]
    [InlineData("{\"targetNpcIds\":[]}", TargetNpcIdsFieldState.ExplicitValue)]
    public void Inspect_SingleCaseInsensitiveProperty_UsesSmapiCompatibleState(
        string configJson,
        TargetNpcIdsFieldState expectedState)
    {
        byte[] originalBytes = WriteConfig(configJson);

        TargetNpcIdsFieldState state = ModConfigFileInspector.Inspect(testDirectory);

        Assert.Equal(expectedState, state);
        Assert.Equal(originalBytes, ReadConfigBytes());
    }

    /// <summary>
    /// 多个大小写等价字段会让 Newtonsoft 的 last-wins 行为依赖顺序；Inspector 必须直接拒绝这种歧义。
    /// </summary>
    [Theory]
    [InlineData("{\"TargetNpcIds\":[],\"targetNpcIds\":null}")]
    [InlineData("{\"TargetNpcIds\":[],\"TargetNpcIds\":[\"Leah\"]}")]
    public void Inspect_DuplicateCaseInsensitiveProperties_ReturnsInvalidAndPreservesBytes(
        string configJson)
    {
        byte[] originalBytes = WriteConfig(configJson);

        TargetNpcIdsFieldState state = ModConfigFileInspector.Inspect(testDirectory);

        Assert.Equal(TargetNpcIdsFieldState.InvalidConfiguration, state);
        Assert.Equal(originalBytes, ReadConfigBytes());
    }

    /// <summary>
    /// 顶层必须是 object；null、数组、空文件或 malformed JSON 都不能退化成首次安装并覆盖用户文件。
    /// </summary>
    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("")]
    [InlineData("{\"TargetNpcIds\": [")]
    public void Inspect_InvalidTopLevelJson_ReturnsInvalidAndPreservesBytes(string configJson)
    {
        byte[] originalBytes = WriteConfig(configJson);

        TargetNpcIdsFieldState state = ModConfigFileInspector.Inspect(testDirectory);

        Assert.Equal(TargetNpcIdsFieldState.InvalidConfiguration, state);
        Assert.Equal(originalBytes, ReadConfigBytes());
    }

    /// <summary>
    /// 目标字段只有 array 或 null 两种合法 JSON 类型；其他类型必须在 SMAPI 业务反序列化前 fail closed。
    /// </summary>
    [Theory]
    [InlineData("\"Abigail\"")]
    [InlineData("{}")]
    [InlineData("42")]
    [InlineData("true")]
    public void Inspect_TargetNpcIdsWithWrongJsonType_ReturnsInvalidAndPreservesBytes(
        string invalidFieldJson)
    {
        byte[] originalBytes = WriteConfig($"{{\"TargetNpcIds\":{invalidFieldJson}}}");

        TargetNpcIdsFieldState state = ModConfigFileInspector.Inspect(testDirectory);

        Assert.Equal(TargetNpcIdsFieldState.InvalidConfiguration, state);
        Assert.Equal(originalBytes, ReadConfigBytes());
    }

    /// <summary>
    /// 文件在存在性检查后、只读打开前消失属于竞态错误，不能伪装为 NewConfiguration 后触发写回。
    /// </summary>
    [Fact]
    public void Inspect_FileDisappearsDuringRead_ReturnsInvalidAndPreservesOriginalBytes()
    {
        byte[] originalBytes = WriteConfig("{\"TargetNpcIds\":[\"Abigail\"]}");

        TargetNpcIdsFieldState state = ModConfigFileInspector.Inspect(
            testDirectory,
            File.GetAttributes,
            _ => throw new FileNotFoundException("模拟存在性检查后的删除竞态。"));

        Assert.Equal(TargetNpcIdsFieldState.InvalidConfiguration, state);
        Assert.Equal(originalBytes, ReadConfigBytes());
    }

    /// <summary>
    /// 字段状态与稳定指纹必须来自同一 bytes snapshot；即使语义相同，读取前后 bytes 改变也要拒绝。
    /// </summary>
    [Fact]
    public void InspectSnapshot_WhenExistingBytesChange_IsNoLongerCurrent()
    {
        WriteConfig("{\"TargetNpcIds\":[\"Abigail\"]}");
        ModConfigFileInspection snapshot = ModConfigFileInspector.InspectSnapshot(testDirectory);

        Assert.Equal(TargetNpcIdsFieldState.ExplicitValue, snapshot.FieldState);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.ContentFingerprint));
        Assert.True(ModConfigFileInspector.IsSnapshotCurrent(testDirectory, snapshot));

        WriteConfig("{ \"TargetNpcIds\": [\"Abigail\"] }");

        Assert.False(ModConfigFileInspector.IsSnapshotCurrent(testDirectory, snapshot));
    }

    /// <summary>
    /// 已有文件 snapshot 之后消失，以及新配置 snapshot 之后出现，都不能继续使用原状态。
    /// </summary>
    [Fact]
    public void InspectSnapshot_WhenFilePresenceChanges_IsNoLongerCurrent()
    {
        WriteConfig("{\"TargetNpcIds\":[]}");
        ModConfigFileInspection existing = ModConfigFileInspector.InspectSnapshot(testDirectory);
        File.Delete(Path.Combine(testDirectory, "config.json"));

        Assert.False(ModConfigFileInspector.IsSnapshotCurrent(testDirectory, existing));

        ModConfigFileInspection missing = ModConfigFileInspector.InspectSnapshot(testDirectory);
        Assert.Equal(TargetNpcIdsFieldState.NewConfiguration, missing.FieldState);
        Assert.True(ModConfigFileInspector.IsSnapshotCurrent(testDirectory, missing));

        WriteConfig("{\"TargetNpcIds\":[]}");

        Assert.False(ModConfigFileInspector.IsSnapshotCurrent(testDirectory, missing));
    }

    /// <summary>
    /// 写入本测试自己的配置文件并返回原始 bytes，供每个已有文件分支证明零写入。
    /// </summary>
    private byte[] WriteConfig(string configJson)
    {
        Directory.CreateDirectory(testDirectory);
        byte[] bytes = Encoding.UTF8.GetBytes(configJson);
        File.WriteAllBytes(Path.Combine(testDirectory, "config.json"), bytes);
        return bytes;
    }

    /// <summary>
    /// 读取测试配置的当前 bytes；不做文本规范化，确保连空白与换行都未改变。
    /// </summary>
    private byte[] ReadConfigBytes()
    {
        return File.ReadAllBytes(Path.Combine(testDirectory, "config.json"));
    }

    /// <summary>
    /// 每个测试使用独立目录；清理只触及测试自己创建的临时路径。
    /// </summary>
    public void Dispose()
    {
        if (Directory.Exists(testDirectory))
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }
}
