namespace StardewNpcAgent.Tests.Contracts;

/// <summary>
/// 定位由测试项目链接进输出目录的共享 JSON fixture。
/// </summary>
/// <remarks>
/// fixture 的源文件始终位于仓库根目录 <c>contracts/fixtures</c>；本类只读取构建时
/// 复制的文件，避免测试依赖调用者的当前工作目录，也避免在 C# 项目维护副本。
/// </remarks>
internal static class FixtureFile
{
    /// <summary>
    /// 读取指定 fixture 的完整 UTF-8 文本。
    /// </summary>
    /// <param name="fileName">只包含文件名与扩展名，例如 <c>event_batch.json</c>。</param>
    /// <returns>fixture 的原始 JSON 文本。</returns>
    public static string ReadAllText(string fileName)
    {
        string fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "Contracts",
            "Fixtures",
            fileName);

        return File.ReadAllText(fixturePath);
    }
}
