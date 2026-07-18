using VanillaDialogueManifestExtractor;

namespace VanillaDialogueManifestExtractor.Tests;

/// <summary>
/// 用内存字典验证 manifest 组装；测试不会读写 Stardew Content。
/// </summary>
public sealed class DialogueManifestExtractorTests
{
    [Fact]
    public void Extract_UsesApprovedDailyRulesAndSeparatesRainyStyleProvenance()
    {
        InMemoryAssetReader reader = new(
            new Dictionary<(string Asset, string Locale), DialogueAssetSnapshot>
            {
                [("Characters/Dialogue/Abigail", "zh-CN")] = Snapshot(
                    "Characters/Dialogue/Abigail",
                    "zh-CN",
                    "Characters/Dialogue/Abigail.zh-CN.xnb",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["spring_Mon"] = "普通来源。",
                        ["spring_Tue"] = "你好，@。",
                        ["summer_Wed"] = "夏天的样本。",
                        ["fall_Thu"] = "秋天的样本。",
                        ["winter_Fri"] = "冬天的样本。",
                        ["spring_Sat2"] = "高心关系语气。",
                        ["Introduction"] = "非日常 key。",
                    }),
                [("Characters/Dialogue/rainy", "zh-CN")] = Snapshot(
                    "Characters/Dialogue/rainy",
                    "zh-CN",
                    "Characters/Dialogue/rainy.zh-CN.xnb",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["Abigail"] = "今天下雨了。",
                        ["Sebastian"] = "不属于目标 NPC。",
                    }),
            });

        VanillaDialogueManifest manifest = DialogueManifestExtractor.Extract(
            new GameBuildInfo("1.6.15", "24356"),
            new[] { "Abigail" },
            new[] { "zh-CN" },
            reader);

        Assert.Equal(3, manifest.Entries.Count);
        Assert.Contains(
            manifest.Entries,
            entry => entry.SourceFamily == "ordinary_daily" && !entry.SourceText.Contains('@'));
        Assert.Contains(
            manifest.Entries,
            entry => entry.SourceFamily == "ordinary_daily" && entry.SourceText == "你好，@。");
        VanillaDialogueManifestEntry rainy = Assert.Single(
            manifest.Entries,
            entry => entry.SourceFamily == "rainy_daily");
        Assert.Equal("Characters/Dialogue/rainy.zh-CN.xnb", rainy.LocalizedXnbPath);
        Assert.Equal("Characters/Dialogue/Abigail", rainy.StyleAssetName);
        Assert.Equal(
            "Characters/Dialogue/Abigail.zh-CN.xnb",
            rainy.StyleLocalizedXnbPath);
        Assert.DoesNotContain("高心关系语气。", rainy.StyleTexts);
        Assert.All(manifest.Entries, entry => Assert.InRange(entry.StyleTexts.Count, 2, 5));
    }

    [Fact]
    public void Extract_FailsClosedWhenAnyNpcLocaleHasTooFewSafeStyleExamples()
    {
        InMemoryAssetReader reader = new(
            new Dictionary<(string Asset, string Locale), DialogueAssetSnapshot>
            {
                [("Characters/Dialogue/Abigail", "en")] = Snapshot(
                    "Characters/Dialogue/Abigail",
                    "en",
                    "Characters/Dialogue/Abigail.xnb",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["Mon"] = "Only one safe line.",
                    }),
                [("Characters/Dialogue/rainy", "en")] = Snapshot(
                    "Characters/Dialogue/rainy",
                    "en",
                    "Characters/Dialogue/rainy.xnb",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["Abigail"] = "Rain.",
                    }),
            });

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => DialogueManifestExtractor.Extract(
                new GameBuildInfo("1.6.15", "24356"),
                new[] { "Abigail" },
                new[] { "en" },
                reader));

        Assert.Contains("Abigail", error.Message, StringComparison.Ordinal);
        Assert.Contains("en", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_OrdinaryStartsAtSourceHeartAndUsesTheFirstSufficientLevel()
    {
        InMemoryAssetReader reader = new(
            new Dictionary<(string Asset, string Locale), DialogueAssetSnapshot>
            {
                [("Characters/Dialogue/Alex", "en")] = Snapshot(
                    "Characters/Dialogue/Alex",
                    "en",
                    "Characters/Dialogue/Alex.xnb",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["fall_Mon2"] = "Hey, @.",
                        ["fall_Tue4"] = "First four-heart style.",
                        ["fall_Wed4"] = "Second four-heart style.",
                        ["spring_Wed"] = "Spring baseline.",
                    }),
                [("Characters/Dialogue/rainy", "en")] = Snapshot(
                    "Characters/Dialogue/rainy",
                    "en",
                    "Characters/Dialogue/rainy.xnb",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["Alex"] = "Rain.",
                    }),
            });

        VanillaDialogueManifest manifest = DialogueManifestExtractor.Extract(
            new GameBuildInfo("1.6.15", "24356"),
            new[] { "Alex" },
            new[] { "en" },
            reader);

        VanillaDialogueManifestEntry addressed = Assert.Single(
            manifest.Entries,
            entry => entry.SourceText.Contains('@'));
        Assert.Contains("fall_Tue4", addressed.StyleKeys);
        Assert.Contains("First four-heart style.", addressed.StyleTexts);
        Assert.Equal("fall", addressed.StyleContextSeason);
        Assert.Equal(4, addressed.StyleContextHeartLevel);
    }

    [Fact]
    public void Extract_RainyUsesTheLowestHeartLevelThatProvidesTwoSafeStyles()
    {
        InMemoryAssetReader reader = new(
            new Dictionary<(string Asset, string Locale), DialogueAssetSnapshot>
            {
                [("Characters/Dialogue/Harvey", "en")] = Snapshot(
                    "Characters/Dialogue/Harvey",
                    "en",
                    "Characters/Dialogue/Harvey.xnb",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["spring_Mon"] = "One baseline style.",
                        ["spring_Tue4"] = "First four-heart style.",
                        ["spring_Wed4"] = "Second four-heart style.",
                    }),
                [("Characters/Dialogue/rainy", "en")] = Snapshot(
                    "Characters/Dialogue/rainy",
                    "en",
                    "Characters/Dialogue/rainy.xnb",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["Harvey"] = "Rainy provenance.$b#Still special.",
                    }),
            });

        VanillaDialogueManifest manifest = DialogueManifestExtractor.Extract(
            new GameBuildInfo("1.6.15", "24356"),
            new[] { "Harvey" },
            new[] { "en" },
            reader);

        VanillaDialogueManifestEntry rainy = Assert.Single(
            manifest.Entries,
            entry => entry.SourceFamily == "rainy_daily");
        Assert.Equal("spring", rainy.StyleContextSeason);
        Assert.Equal(4, rainy.StyleContextHeartLevel);
        Assert.Contains("spring_Mon", rainy.StyleKeys);
        Assert.Contains("spring_Tue4", rainy.StyleKeys);
    }

    [Fact]
    public void ToContentRelativePosixPath_RejectsFilesOutsideRoot()
    {
        string root = Path.GetFullPath("/tmp/game-content");
        string inside = Path.Combine(root, "Characters", "Dialogue", "Abigail.xnb");

        Assert.Equal(
            "Characters/Dialogue/Abigail.xnb",
            ManifestPath.ToContentRelativePosixPath(root, inside));
        Assert.Throws<ArgumentException>(
            () => ManifestPath.ToContentRelativePosixPath(root, "/tmp/other/Abigail.xnb"));
    }

    private static DialogueAssetSnapshot Snapshot(
        string assetName,
        string locale,
        string relativePath,
        IReadOnlyDictionary<string, string> entries)
    {
        return new DialogueAssetSnapshot(
            assetName,
            locale,
            relativePath,
            new string('a', 64),
            entries);
    }

    private sealed class InMemoryAssetReader : IDialogueAssetReader
    {
        private readonly IReadOnlyDictionary<(string Asset, string Locale), DialogueAssetSnapshot>
            snapshots;

        public InMemoryAssetReader(
            IReadOnlyDictionary<(string Asset, string Locale), DialogueAssetSnapshot> snapshots)
        {
            this.snapshots = snapshots;
        }

        public DialogueAssetSnapshot Read(string assetName, string locale)
        {
            return snapshots.TryGetValue((assetName, locale), out DialogueAssetSnapshot? snapshot)
                ? snapshot
                : throw new FileNotFoundException("测试资产不存在。");
        }
    }
}
