using System.Text.Json;

namespace StardewNpcAgent.Tests.Integration;

/// <summary>
/// 验证仓库中实际随 Mod 发布的 manifest 公开元数据。
///
/// 该测试直接读取 production 项目里的 manifest.json，而不是复制一份测试 fixture。
/// 因此作者名、稳定 Mod ID 与运行时版本边界只要在发布源文件中被意外改动，测试都会
/// 立即失败；同时路径定位不依赖测试启动时的当前工作目录，适用于本地与 CI。
/// </summary>
public sealed class ManifestMetadataTests
{
    /// <summary>
    /// 公开发布清单必须保持项目名称、作者、稳定标识与 SMAPI/Game 兼容性合同。
    ///
    /// <para>UniqueID 是存档与 SMAPI 识别 Mod 的稳定键，不能因公开作者名变更而迁移。</para>
    /// </summary>
    [Fact]
    public void Manifest_ContainsExpectedPublicMetadata()
    {
        using JsonDocument manifest = ReadRepositoryManifest();
        JsonElement root = manifest.RootElement;

        Assert.Equal("Stardew NPC Agent", ReadRequiredString(root, "Name"));
        Assert.Equal("rookieindongchuanroad", ReadRequiredString(root, "Author"));
        Assert.Equal("Liurongfu.StardewNpcAgent", ReadRequiredString(root, "UniqueID"));
        Assert.Equal("StardewNpcAgent.dll", ReadRequiredString(root, "EntryDll"));
        Assert.Equal("4.5.2", ReadRequiredString(root, "MinimumApiVersion"));
        Assert.Equal("1.6.15", ReadRequiredString(root, "MinimumGameVersion"));
    }

    /// <summary>
    /// 读取 production 项目中作为发布源的 manifest.json，并在文件缺失或 JSON 无效时保留
    /// 原始异常上下文，避免把发布配置问题误报成断言失败。
    /// </summary>
    private static JsonDocument ReadRepositoryManifest()
    {
        string manifestPath = Path.Combine(
            FindSolutionDirectory(),
            "StardewNpcAgent",
            "manifest.json");

        return JsonDocument.Parse(File.ReadAllText(manifestPath));
    }

    /// <summary>
    /// 取得必填字符串字段。缺失、null 或非字符串值均属于 manifest 合同破坏，应给出字段名
    /// 以便维护者准确修复，而不是让 <see cref="JsonElement.GetString" /> 抛出无上下文异常。
    /// </summary>
    private static string ReadRequiredString(JsonElement manifest, string propertyName)
    {
        Assert.True(
            manifest.TryGetProperty(propertyName, out JsonElement property),
            $"manifest.json 必须包含字符串字段 {propertyName}。");
        Assert.Equal(JsonValueKind.String, property.ValueKind);

        return property.GetString()
            ?? throw new InvalidOperationException($"manifest.json 字段 {propertyName} 不得为 null。");
    }

    /// <summary>
    /// 从测试输出目录向上寻找 solution，避免在 IDE、命令行与 CI 的不同当前目录下失效。
    /// </summary>
    private static string FindSolutionDirectory()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "StardewNpcAgent.sln")))
        {
            current = current.Parent;
        }

        Assert.NotNull(current);
        return current!.FullName;
    }
}
