using System.IO.Compression;

namespace StardewNpcAgent.Tests.Integration;

/// <summary>
/// 验证 ModBuildConfig 生成的真实 Release zip 可以在 Unix/macOS 上直接部署。
///
/// 这组测试刻意读取构建产物，而不是重新模拟一套打包逻辑。发布包即使文件名和内容
/// 都正确，只要 ZIP 中的 Unix 权限被写成 000，解压后的 manifest 与 DLL 仍不可读，
/// SMAPI 就无法加载。因此“条目可读”属于发布产物合同，而不是部署脚本的补救职责。
/// </summary>
public sealed class ReleasePackageTests
{
    /// <summary>
    /// Harmony 与 Mono.Cecil 均由已安装的 SMAPI 提供，发布包不得夹带第二份程序集覆盖运行时。
    /// </summary>
    [Fact]
    public void ReleaseZip_ContainsOnlyManifestAndModAssembly()
    {
        using ZipArchive archive = ZipFile.OpenRead(FindReleaseZipPath());

        Assert.Equal(
            new[]
            {
                "StardewNpcAgent/StardewNpcAgent.dll",
                "StardewNpcAgent/manifest.json",
            },
            archive.Entries
                .Where(entry => !entry.FullName.EndsWith("/", StringComparison.Ordinal))
                .Select(entry => entry.FullName)
                .OrderBy(name => name, StringComparer.Ordinal));
    }

    /// <summary>
    /// Release zip 中的每个文件条目都必须至少允许文件所有者读取。
    ///
    /// ZIP 的 Unix mode 位位于 external attributes 的高 16 位。直接检查归档元数据，
    /// 可以避免测试进程以高权限运行时绕过 000 权限而产生假阳性。
    /// </summary>
    [Fact]
    public void ReleaseZip_FileEntriesGrantOwnerReadPermission()
    {
        string releaseZipPath = FindReleaseZipPath();

        using ZipArchive archive = ZipFile.OpenRead(releaseZipPath);
        ZipArchiveEntry[] fileEntries = archive.Entries
            .Where(entry => !entry.FullName.EndsWith("/", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(fileEntries);
        foreach (ZipArchiveEntry entry in fileEntries)
        {
            uint unixMode = unchecked((uint)entry.ExternalAttributes) >> 16;
            const uint ownerReadPermission = 0x0100;

            Assert.True(
                (unixMode & ownerReadPermission) != 0,
                $"发布包条目必须允许所有者读取，但 {entry.FullName} 的 Unix mode 是 " +
                $"0{Convert.ToString((int)unixMode, 8)}。");
        }
    }

    /// <summary>
    /// 根据当前测试配置定位 production 项目的同配置 zip。
    ///
    /// 测试既可在 Debug 也可在 Release 下运行，因此不硬编码配置目录；版本号同样不
    /// 写死，而是要求该输出目录恰好存在一个 ModBuildConfig 生成的发布包。若残留多个
    /// 版本，测试会明确失败，防止误验旧包。
    /// </summary>
    private static string FindReleaseZipPath()
    {
        string solutionDirectory = FindSolutionDirectory();
        DirectoryInfo testOutputDirectory = new(AppContext.BaseDirectory);
        string buildConfiguration = testOutputDirectory.Parent?.Name
            ?? throw new InvalidOperationException("无法从测试输出路径确定构建配置。");
        string productionOutputDirectory = Path.Combine(
            solutionDirectory,
            "StardewNpcAgent",
            "bin",
            buildConfiguration,
            "net6.0");

        string[] zipPaths = Directory.Exists(productionOutputDirectory)
            ? Directory.GetFiles(productionOutputDirectory, "StardewNpcAgent *.zip", SearchOption.TopDirectoryOnly)
            : Array.Empty<string>();

        return Assert.Single(zipPaths);
    }

    /// <summary>
    /// 从测试输出目录向上寻找 solution，避免测试依赖调用者的当前工作目录。
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
