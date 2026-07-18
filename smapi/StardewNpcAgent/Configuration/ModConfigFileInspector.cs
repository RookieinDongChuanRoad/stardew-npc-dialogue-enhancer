using System.Security.Cryptography;
using System.Text.Json;

namespace StardewNpcAgent.Configuration;

/// <summary>
/// 描述 <c>config.json</c> 顶层 <c>TargetNpcIds</c> 字段的兼容性状态。
/// </summary>
/// <remarks>
/// 该状态只表达文件结构事实，不承载 NPC allowlist、去重或业务启用决策；这些解释必须由独立的
/// compatibility policy 完成，避免只读检查器在启动早期意外改变用户配置语义。
/// </remarks>
public enum TargetNpcIdsFieldState
{
    /// <summary>Mod 目录内尚不存在 <c>config.json</c>。</summary>
    NewConfiguration,

    /// <summary>已有配置是 JSON object，但没有目标字段，表示旧版本配置。</summary>
    MissingFromExistingConfiguration,

    /// <summary>已有配置显式把目标字段设置为 JSON <c>null</c>。</summary>
    ExplicitNull,

    /// <summary>已有配置显式提供了结构合法的目标字段值。</summary>
    ExplicitValue,

    /// <summary>已有配置无法被安全解释，启动必须 fail closed。</summary>
    InvalidConfiguration,
}

/// <summary>
/// 从单一、不可变 bytes snapshot 得到的配置字段状态与稳定内容指纹。
/// </summary>
/// <param name="FieldState">同一 bytes snapshot 的顶层目标字段状态。</param>
/// <param name="ContentFingerprint">
/// 已有普通文件的 SHA-256；真正缺失或在读取前失败时为 null。指纹只用于启动期一致性比较，不写日志。
/// </param>
internal sealed record ModConfigFileInspection(
    TargetNpcIdsFieldState FieldState,
    string? ContentFingerprint);

/// <summary>
/// 以纯读取方式检查 Mod 目录内 <c>config.json</c> 的顶层目标字段。
/// </summary>
/// <remarks>
/// 检查器只打开并解析用户文件，从不创建、截断或重写文件。把“字段是否存在”和“字段值如何启用 NPC”
/// 分开，是为了让旧配置兼容与运行时 allowlist 都能被独立测试，并确保异常输入不会触发 SMAPI 的写回路径。
/// </remarks>
public static class ModConfigFileInspector
{
    private const string ConfigFileName = "config.json";
    private const string TargetNpcIdsPropertyName = "TargetNpcIds";

    /// <summary>
    /// 检查指定 Mod 目录下配置文件的目标字段状态。
    /// </summary>
    /// <param name="modDirectoryPath">包含 <c>config.json</c> 的 Mod 根目录绝对或相对路径。</param>
    /// <returns>只基于顶层 JSON 结构得到的字段状态。</returns>
    public static TargetNpcIdsFieldState Inspect(string modDirectoryPath)
    {
        return InspectSnapshot(modDirectoryPath).FieldState;
    }

    /// <summary>
    /// 使用显式只读文件操作检查配置；内部重载让“存在性检查后文件消失”的竞态可以确定性验证。
    /// </summary>
    /// <param name="modDirectoryPath">包含配置文件的 Mod 根目录。</param>
    /// <param name="getAttributes">只读存在性与文件类型检查。</param>
    /// <param name="openRead">只读打开操作；不得创建或截断文件。</param>
    /// <returns>字段状态；任何已有文件读取/解析异常都返回 InvalidConfiguration。</returns>
    internal static TargetNpcIdsFieldState Inspect(
        string modDirectoryPath,
        Func<string, FileAttributes> getAttributes,
        Func<string, Stream> openRead)
    {
        return InspectSnapshot(modDirectoryPath, getAttributes, openRead).FieldState;
    }

    /// <summary>
    /// 读取一次配置 bytes，同时派生字段状态与稳定指纹；绝不把原文或指纹写入日志。
    /// </summary>
    /// <param name="modDirectoryPath">包含配置文件的 Mod 根目录。</param>
    /// <returns>同一 bytes snapshot 的检查结果。</returns>
    internal static ModConfigFileInspection InspectSnapshot(string modDirectoryPath)
    {
        return InspectSnapshot(modDirectoryPath, File.GetAttributes, OpenConfigReadStream);
    }

    /// <summary>
    /// 使用可注入的只读文件操作创建 snapshot，供读取竞态测试确定性模拟文件消失。
    /// </summary>
    internal static ModConfigFileInspection InspectSnapshot(
        string modDirectoryPath,
        Func<string, FileAttributes> getAttributes,
        Func<string, Stream> openRead)
    {
        if (string.IsNullOrWhiteSpace(modDirectoryPath))
        {
            throw new ArgumentException("Mod 目录路径不能为空。", nameof(modDirectoryPath));
        }

        ArgumentNullException.ThrowIfNull(getAttributes);
        ArgumentNullException.ThrowIfNull(openRead);
        string configPath = Path.Combine(modDirectoryPath, ConfigFileName);

        try
        {
            FileAttributes attributes = getAttributes(configPath);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                return new ModConfigFileInspection(
                    TargetNpcIdsFieldState.InvalidConfiguration,
                    ContentFingerprint: null);
            }
        }
        catch (FileNotFoundException)
        {
            return new ModConfigFileInspection(
                TargetNpcIdsFieldState.NewConfiguration,
                ContentFingerprint: null);
        }
        catch (DirectoryNotFoundException)
        {
            return new ModConfigFileInspection(
                TargetNpcIdsFieldState.NewConfiguration,
                ContentFingerprint: null);
        }
        catch (Exception exception) when (IsExpectedFileFailure(exception))
        {
            // 无权限、路径或其他 IO 问题不等同于“文件不存在”，否则后续 ReadConfig 可能覆盖用户文件。
            return new ModConfigFileInspection(
                TargetNpcIdsFieldState.InvalidConfiguration,
                ContentFingerprint: null);
        }

        try
        {
            using Stream stream = openRead(configPath);
            using MemoryStream snapshotBuffer = new();
            stream.CopyTo(snapshotBuffer);
            byte[] snapshotBytes = snapshotBuffer.ToArray();
            string fingerprint = Convert.ToHexString(SHA256.HashData(snapshotBytes));

            using JsonDocument document = JsonDocument.Parse(snapshotBytes);
            return new ModConfigFileInspection(
                InspectRoot(document.RootElement),
                fingerprint);
        }
        catch (JsonException)
        {
            // malformed JSON 仍属于已有无效配置；指纹无需保留，因为 ModEntry 会在业务读取前直接返回。
            return new ModConfigFileInspection(
                TargetNpcIdsFieldState.InvalidConfiguration,
                ContentFingerprint: null);
        }
        catch (Exception exception) when (IsExpectedFileFailure(exception))
        {
            // 文件已被确认存在；此后任何读取竞态或 JSON 错误都必须 fail closed，绝不能转成 NewConfiguration。
            return new ModConfigFileInspection(
                TargetNpcIdsFieldState.InvalidConfiguration,
                ContentFingerprint: null);
        }
    }

    /// <summary>
    /// 重新读取当前 bytes 并与先前 snapshot 做固定语义比较；任一读取失败、消失或状态变化都返回 false。
    /// </summary>
    /// <remarks>
    /// 该方法不自动重试，也不暴露内容指纹。ModEntry 在 existing-config 纯读前后各调用一次，确保
    /// compatibility state 与 SMAPI 反序列化对象没有跨版本组合。
    /// </remarks>
    internal static bool IsSnapshotCurrent(
        string modDirectoryPath,
        ModConfigFileInspection expectedSnapshot)
    {
        ArgumentNullException.ThrowIfNull(expectedSnapshot);
        ModConfigFileInspection currentSnapshot = InspectSnapshot(modDirectoryPath);
        return currentSnapshot.FieldState == expectedSnapshot.FieldState
            && string.Equals(
                currentSnapshot.ContentFingerprint,
                expectedSnapshot.ContentFingerprint,
                StringComparison.Ordinal);
    }

    /// <summary>
    /// 只解释顶层 object 与目标字段的 JSON value kind，不读取 NPC 内容或执行 allowlist 业务规则。
    /// </summary>
    private static TargetNpcIdsFieldState InspectRoot(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return TargetNpcIdsFieldState.InvalidConfiguration;
        }

        JsonElement? targetNpcIds = null;
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!string.Equals(
                property.Name,
                TargetNpcIdsPropertyName,
                StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (targetNpcIds.HasValue)
            {
                // Newtonsoft 对大小写等价字段采用顺序相关的赋值；拒绝歧义比依赖 last-wins 更安全。
                return TargetNpcIdsFieldState.InvalidConfiguration;
            }

            targetNpcIds = property.Value;
        }

        if (!targetNpcIds.HasValue)
        {
            return TargetNpcIdsFieldState.MissingFromExistingConfiguration;
        }

        return targetNpcIds.Value.ValueKind switch
        {
            JsonValueKind.Null => TargetNpcIdsFieldState.ExplicitNull,
            JsonValueKind.Array => TargetNpcIdsFieldState.ExplicitValue,
            _ => TargetNpcIdsFieldState.InvalidConfiguration,
        };
    }

    /// <summary>
    /// 打开允许其他进程继续读取/替换的只读 stream；本 Mod 不持有写锁，也不创建文件。
    /// </summary>
    private static Stream OpenConfigReadStream(string configPath)
    {
        return new FileStream(
            configPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
    }

    /// <summary>
    /// 只折叠预期的文件系统边界错误；程序逻辑错误仍应暴露给测试和开发者。
    /// </summary>
    private static bool IsExpectedFileFailure(Exception exception)
    {
        return exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException;
    }
}
