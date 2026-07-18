using VanillaDialogueManifestExtractor;

namespace VanillaDialogueManifestExtractor.Tests;

/// <summary>
/// 冻结只读 extractor 的命令行边界。测试只解析字符串，不接触真实游戏目录。
/// </summary>
public sealed class ExtractorOptionsTests
{
    [Fact]
    public void Parse_WithoutOutput_RemainsDryRunAndKeepsRequestedLocaleOrder()
    {
        string contentRoot = Path.GetFullPath("/tmp/stardew-content");

        ExtractorOptions options = ExtractorOptions.Parse(
            new[]
            {
                "--game-content-root",
                contentRoot,
                "--locale",
                "en",
                "--locale",
                "zh-CN",
            });

        Assert.Equal(contentRoot, options.GameContentRoot);
        Assert.Equal(new[] { "en", "zh-CN" }, options.Locales);
        Assert.Null(options.OutputPath);
        Assert.True(options.IsDryRun);
    }

    [Theory]
    [InlineData("--game-content-root", "/tmp/content", "--locale")]
    [InlineData("--locale", "en")]
    [InlineData("--game-content-root", "/tmp/content", "--locale", "fr")]
    public void Parse_RejectsMissingValuesRootOrUnsupportedLocale(params string[] args)
    {
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => ExtractorOptions.Parse(args));

        Assert.DoesNotContain("/Users/", error.Message, StringComparison.Ordinal);
    }
}
