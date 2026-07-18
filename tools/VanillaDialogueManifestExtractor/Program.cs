using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;
using StardewNpcAgent.Game;

namespace VanillaDialogueManifestExtractor;

/// <summary>
/// 只读提取本机原版 XNB 对话来源，默认仅报告结果；只有显式 <c>--output</c> 才写 fixture。
/// </summary>
public static class Program
{
    /// <summary>
    /// 解析命令行、读取安装版本与 XNB，并按 dry-run/显式输出边界结束。
    /// </summary>
    /// <param name="args">必须包含 Content root 和至少一个 locale。</param>
    /// <returns>成功为 0；参数、资源或安全样本不完整为 1。</returns>
    public static int Main(string[] args)
    {
        try
        {
            ExtractorOptions options = ExtractorOptions.Parse(args);
            GameBuildInfo build = GameInstallationLocator.ReadBuildInfo(options.GameContentRoot);
            IDialogueAssetReader reader = new MonoGameDialogueAssetReader(
                options.GameContentRoot);
            VanillaDialogueManifest manifest = DialogueManifestExtractor.Extract(
                build,
                VanillaMarriageableNpcRegistry.AllIds,
                options.Locales,
                reader);
            string json = ManifestJson.Serialize(manifest);

            if (options.IsDryRun)
            {
                Console.WriteLine(
                    $"DRY_RUN_OK game={build.GameVersion} build={build.GameBuild} "
                    + $"entries={manifest.Entries.Count} manifest_sha256={Hashing.Sha256Hex(json)}");
                return 0;
            }

            AtomicManifestWriter.Write(options.OutputPath!, json);
            Console.WriteLine(
                $"MANIFEST_WRITTEN entries={manifest.Entries.Count} "
                + $"manifest_sha256={Hashing.Sha256Hex(json)}");
            return 0;
        }
        catch (Exception error) when (
            error is ArgumentException
                or FileNotFoundException
                or DirectoryNotFoundException
                or InvalidDataException
                or UnauthorizedAccessException
                or IOException
                or ContentLoadException)
        {
            // 失败日志只保留异常类型和已经净化的稳定原因；模型密钥、存档和游戏进程
            // 从未进入本工具，因此失败也不会触发外部副作用或自动重试。
            Console.Error.WriteLine(
                $"EXTRACTION_FAILED type={error.GetType().Name} reason={error.Message}");
            return 1;
        }
    }
}

/// <summary>
/// extractor 的封闭命令行参数。
/// </summary>
/// <param name="GameContentRoot">必须是绝对的 Stardew <c>Content</c> 根。</param>
/// <param name="Locales">按用户输入顺序保留的 <c>en</c>/<c>zh-CN</c>。</param>
/// <param name="OutputPath">null 表示 dry-run；非 null 才允许写共享 fixture。</param>
public sealed record ExtractorOptions(
    string GameContentRoot,
    IReadOnlyList<string> Locales,
    string? OutputPath)
{
    private static readonly HashSet<string> SupportedLocales = new(
        new[] { "en", "zh-CN" },
        StringComparer.Ordinal);

    /// <summary>未显式提供输出路径时始终为 true。</summary>
    public bool IsDryRun => OutputPath is null;

    /// <summary>
    /// 解析重复 <c>--locale</c> 参数；未知参数、重复 locale 与相对路径全部拒绝。
    /// </summary>
    public static ExtractorOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? contentRoot = null;
        string? outputPath = null;
        List<string> locales = new();

        for (int index = 0; index < args.Count; index++)
        {
            string option = args[index];
            if (option is not ("--game-content-root" or "--locale" or "--output"))
            {
                throw new ArgumentException("只支持 --game-content-root、--locale 与 --output。", nameof(args));
            }

            if (++index >= args.Count || string.IsNullOrWhiteSpace(args[index]))
            {
                throw new ArgumentException($"参数 {option} 缺少非空值。", nameof(args));
            }

            string value = args[index];
            switch (option)
            {
                case "--game-content-root":
                    if (contentRoot is not null)
                    {
                        throw new ArgumentException("--game-content-root 只能出现一次。", nameof(args));
                    }

                    contentRoot = RequireAbsolutePath(value, "--game-content-root");
                    break;
                case "--locale":
                    if (!SupportedLocales.Contains(value) || locales.Contains(value, StringComparer.Ordinal))
                    {
                        throw new ArgumentException("locale 只允许各出现一次 en 与 zh-CN。", nameof(args));
                    }

                    locales.Add(value);
                    break;
                case "--output":
                    if (outputPath is not null)
                    {
                        throw new ArgumentException("--output 只能出现一次。", nameof(args));
                    }

                    outputPath = RequireAbsolutePath(value, "--output");
                    break;
            }
        }

        if (contentRoot is null || locales.Count == 0)
        {
            throw new ArgumentException("必须提供 Content root 和至少一个 locale。", nameof(args));
        }

        return new ExtractorOptions(contentRoot, locales.AsReadOnly(), outputPath);
    }

    private static string RequireAbsolutePath(string value, string option)
    {
        if (!Path.IsPathFullyQualified(value) || value != value.Trim())
        {
            throw new ArgumentException($"{option} 必须是无边缘空白的绝对路径。", nameof(value));
        }

        return Path.GetFullPath(value);
    }
}

/// <summary>从游戏程序集 metadata 读取的版本 provenance。</summary>
public sealed record GameBuildInfo(string GameVersion, string GameBuild);

/// <summary>定位本机 Steam Content root，并只读核定 Stardew 程序集版本。</summary>
public static class GameInstallationLocator
{
    /// <summary>返回当前 macOS Steam 默认 Content root；不检查或创建目录。</summary>
    public static string GetDefaultContentRoot()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library",
            "Application Support",
            "Steam",
            "steamapps",
            "common",
            "Stardew Valley",
            "Contents",
            "Resources",
            "Content");
    }

    /// <summary>
    /// 读取 <c>Stardew Valley.dll</c> 的四段 AssemblyVersion；前三段为游戏版本，
    /// 第四段为 build。这里不加载游戏程序集，也不启动 Stardew/SMAPI。
    /// </summary>
    public static GameBuildInfo ReadBuildInfo(string contentRoot)
    {
        if (!Directory.Exists(contentRoot))
        {
            throw new DirectoryNotFoundException("指定的 Stardew Content root 不存在。");
        }

        string gameAssemblyPath = Path.GetFullPath(
            Path.Combine(contentRoot, "..", "..", "MacOS", "Stardew Valley.dll"));
        if (!File.Exists(gameAssemblyPath))
        {
            throw new FileNotFoundException("无法在 Content root 对应安装中找到 Stardew Valley.dll。", gameAssemblyPath);
        }

        FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(gameAssemblyPath);
        string? fileVersion = versionInfo.FileVersion;
        string[] versionParts = fileVersion?.Split('.', StringSplitOptions.None) ?? Array.Empty<string>();
        if (versionParts.Length != 4 || versionParts.Any(part => !int.TryParse(part, out _)))
        {
            throw new InvalidDataException("Stardew Valley.dll 没有可证明的四段文件版本。");
        }

        return new GameBuildInfo(
            string.Join('.', versionParts.Take(3)),
            versionParts[3]);
    }
}

/// <summary>
/// 一个已读取的 localized XNB 字典及其文件 provenance。
/// </summary>
/// <param name="AssetName">无 locale 后缀的 SMAPI canonical asset name。</param>
/// <param name="Locale">exact locale。</param>
/// <param name="LocalizedXnbPath">相对 Content root 的 POSIX 路径。</param>
/// <param name="LocalizedXnbSha256">原始 XNB bytes 的 SHA-256 hex。</param>
/// <param name="Entries">ContentManager 解压后的 exact key/text 字典。</param>
public sealed record DialogueAssetSnapshot(
    string AssetName,
    string Locale,
    string LocalizedXnbPath,
    string LocalizedXnbSha256,
    IReadOnlyDictionary<string, string> Entries);

/// <summary>隔离真实 XNB reader，供纯逻辑测试使用内存字典。</summary>
public interface IDialogueAssetReader
{
    /// <summary>读取一个 exact asset/locale；文件或格式异常时必须抛出并中止整次 manifest。</summary>
    DialogueAssetSnapshot Read(string assetName, string locale);
}

/// <summary>使用游戏随附 MonoGame ContentManager 只读加载 localized 字符串字典。</summary>
public sealed class MonoGameDialogueAssetReader : IDialogueAssetReader
{
    private readonly string contentRoot;
    private readonly Dictionary<(string AssetName, string Locale), DialogueAssetSnapshot> cache = new();

    /// <summary>保存绝对 Content root；构造阶段不打开任何 XNB。</summary>
    public MonoGameDialogueAssetReader(string contentRoot)
    {
        if (!Path.IsPathFullyQualified(contentRoot) || !Directory.Exists(contentRoot))
        {
            throw new DirectoryNotFoundException("XNB reader 需要存在的绝对 Content root。");
        }

        this.contentRoot = Path.GetFullPath(contentRoot);
    }

    /// <inheritdoc />
    public DialogueAssetSnapshot Read(string assetName, string locale)
    {
        if (cache.TryGetValue((assetName, locale), out DialogueAssetSnapshot? cached))
        {
            return cached;
        }

        if (!assetName.StartsWith("Characters/Dialogue/", StringComparison.Ordinal)
            || assetName.Contains("..", StringComparison.Ordinal)
            || assetName.Contains('\\')
            || locale is not ("en" or "zh-CN"))
        {
            throw new ArgumentException("asset 或 locale 不属于 extractor 的封闭范围。");
        }

        string localizedSuffix = locale == "en" ? ".xnb" : $".{locale}.xnb";
        string localizedAssetName = locale == "en" ? assetName : $"{assetName}.{locale}";
        string filePath = Path.Combine(
            contentRoot,
            assetName.Replace('/', Path.DirectorySeparatorChar) + localizedSuffix);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException(
                $"缺少 asset={assetName} locale={locale} 的 localized XNB。");
        }

        Dictionary<string, string> entries;
        using (ContentManager content = new(new GameServiceContainer(), contentRoot))
        {
            Dictionary<string, string> loaded = content.Load<Dictionary<string, string>>(
                localizedAssetName);
            entries = new Dictionary<string, string>(loaded, StringComparer.Ordinal);
        }

        DialogueAssetSnapshot snapshot = new(
            assetName,
            locale,
            ManifestPath.ToContentRelativePosixPath(contentRoot, filePath),
            Hashing.Sha256Hex(File.ReadAllBytes(filePath)),
            entries);
        cache.Add((assetName, locale), snapshot);
        return snapshot;
    }
}

/// <summary>共享 manifest 根对象。</summary>
public sealed record VanillaDialogueManifest
{
    [JsonPropertyName("manifest_version")]
    public string ManifestVersion { get; init; } = "vanilla-dialogue-source-manifest-v1";

    [JsonPropertyName("game_version")]
    public string GameVersion { get; init; } = string.Empty;

    [JsonPropertyName("game_build")]
    public string GameBuild { get; init; } = string.Empty;

    [JsonPropertyName("extractor_version")]
    public string ExtractorVersion { get; init; } = "vanilla-dialogue-manifest-extractor-v1";

    [JsonPropertyName("entries")]
    public IReadOnlyList<VanillaDialogueManifestEntry> Entries { get; init; } =
        Array.Empty<VanillaDialogueManifestEntry>();
}

/// <summary>一条 source 与同 NPC ordinary style examples 的完整双 provenance。</summary>
public sealed record VanillaDialogueManifestEntry
{
    [JsonPropertyName("npc_id")]
    public string NpcId { get; init; } = string.Empty;

    [JsonPropertyName("locale")]
    public string Locale { get; init; } = string.Empty;

    [JsonPropertyName("source_family")]
    public string SourceFamily { get; init; } = string.Empty;

    [JsonPropertyName("asset_name")]
    public string AssetName { get; init; } = string.Empty;

    [JsonPropertyName("dialogue_key")]
    public string DialogueKey { get; init; } = string.Empty;

    [JsonPropertyName("source_text")]
    public string SourceText { get; init; } = string.Empty;

    [JsonPropertyName("source_hash")]
    public string SourceHash { get; init; } = string.Empty;

    [JsonPropertyName("localized_xnb_path")]
    public string LocalizedXnbPath { get; init; } = string.Empty;

    [JsonPropertyName("localized_xnb_sha256")]
    public string LocalizedXnbSha256 { get; init; } = string.Empty;

    [JsonPropertyName("style_asset_name")]
    public string StyleAssetName { get; init; } = string.Empty;

    [JsonPropertyName("style_localized_xnb_path")]
    public string StyleLocalizedXnbPath { get; init; } = string.Empty;

    [JsonPropertyName("style_localized_xnb_sha256")]
    public string StyleLocalizedXnbSha256 { get; init; } = string.Empty;

    [JsonPropertyName("style_context_season")]
    public string StyleContextSeason { get; init; } = string.Empty;

    [JsonPropertyName("style_context_heart_level")]
    public int StyleContextHeartLevel { get; init; }

    [JsonPropertyName("style_keys")]
    public IReadOnlyList<string> StyleKeys { get; init; } = Array.Empty<string>();

    [JsonPropertyName("style_texts")]
    public IReadOnlyList<string> StyleTexts { get; init; } = Array.Empty<string>();
}

/// <summary>组合生产规则并对任一 NPC/locale 缺口整体 fail closed。</summary>
public static class DialogueManifestExtractor
{
    private const string RainyAssetName = "Characters/Dialogue/rainy";

    /// <summary>
    /// 对每名 NPC/locale 输出至少一个 template-safe ordinary，并尽量各保留一个无槽/单
    /// <c>@</c> 样本；另逐字记录一个 exact rainy。manifest 不把 rainy 的存在冒充 eligibility，
    /// 后续 evaluator 必须重新运行 production template policy 派生正负例。
    /// </summary>
    public static VanillaDialogueManifest Extract(
        GameBuildInfo build,
        IReadOnlyList<string> npcIds,
        IReadOnlyList<string> locales,
        IDialogueAssetReader reader)
    {
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(npcIds);
        ArgumentNullException.ThrowIfNull(locales);
        ArgumentNullException.ThrowIfNull(reader);
        if (npcIds.Count == 0 || locales.Count == 0)
        {
            throw new ArgumentException("NPC 与 locale 集合均不得为空。");
        }

        List<VanillaDialogueManifestEntry> entries = new();
        foreach (string npcId in npcIds)
        {
            foreach (string locale in locales)
            {
                DialogueAssetSnapshot ordinary = reader.Read(
                    $"Characters/Dialogue/{npcId}",
                    locale);
                DialogueAssetSnapshot rainy = reader.Read(RainyAssetName, locale);
                IReadOnlyList<VanillaDialogueManifestEntry> ordinaryEntries =
                    BuildOrdinaryEntries(npcId, locale, ordinary);
                if (ordinaryEntries.Count == 0)
                {
                    throw MissingSafeSample(npcId, locale, "ordinary_daily");
                }

                entries.AddRange(ordinaryEntries);
                entries.Add(BuildRainyEntry(npcId, locale, ordinary, rainy));
            }
        }

        VanillaDialogueManifestEntry[] stableEntries = entries
            .OrderBy(entry => entry.NpcId, StringComparer.Ordinal)
            .ThenBy(entry => entry.Locale, StringComparer.Ordinal)
            .ThenBy(entry => entry.SourceFamily, StringComparer.Ordinal)
            .ThenBy(entry => entry.DialogueKey, StringComparer.Ordinal)
            .ToArray();
        return new VanillaDialogueManifest
        {
            GameVersion = build.GameVersion,
            GameBuild = build.GameBuild,
            Entries = stableEntries,
        };
    }

    private static IReadOnlyList<VanillaDialogueManifestEntry> BuildOrdinaryEntries(
        string npcId,
        string locale,
        DialogueAssetSnapshot ordinary)
    {
        List<VanillaDialogueManifestEntry> safe = new();
        foreach ((string key, string text) in ordinary.Entries.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            DialogueSourceIdentity? source = DialogueSourceClassifier.ClassifyTranslationKey(
                $"{ordinary.AssetName}:{key}",
                npcId);
            if (source?.Family != DialogueSourceFamily.OrdinaryDaily
                || !DialogueKeyClassifier.TryGetRequiredHeartLevel(key, out int requiredHearts)
                || !DialogueTemplatePolicy.TryParse(text, out _))
            {
                continue;
            }

            StyleEvidence? style = SelectStyleEvidence(
                npcId,
                locale,
                key,
                text,
                SeasonForKey(key),
                requiredHearts,
                ordinary);
            if (style is null)
            {
                continue;
            }

            safe.Add(CreateEntry(source, locale, text, ordinary, ordinary, style));
            if (safe.Any(entry => entry.SourceText.Contains('@')) && safe.Any(entry => !entry.SourceText.Contains('@')))
            {
                break;
            }

            // 每类只需一个稳定样本。若当前已取得无槽或有槽，继续扫描只寻找另一类；
            // 不把整张受版权保护的 NPC sheet 复制进仓库 fixture。
            if (safe.Count(entry => entry.SourceText.Contains('@')) > 1
                || safe.Count(entry => !entry.SourceText.Contains('@')) > 1)
            {
                safe.RemoveAt(safe.Count - 1);
            }
        }

        return safe;
    }

    private static VanillaDialogueManifestEntry BuildRainyEntry(
        string npcId,
        string locale,
        DialogueAssetSnapshot ordinary,
        DialogueAssetSnapshot rainy)
    {
        if (!rainy.Entries.TryGetValue(npcId, out string? text)
            || string.IsNullOrWhiteSpace(text))
        {
            throw MissingSafeSample(npcId, locale, "rainy_daily");
        }

        DialogueSourceIdentity? source = DialogueSourceClassifier.ClassifyTranslationKey(
            $"{rainy.AssetName}:{npcId}",
            npcId);
        StyleEvidence? style = SelectStyleEvidence(
            npcId,
            locale,
            npcId,
            text,
            "spring",
            0,
            ordinary);
        if (source?.Family != DialogueSourceFamily.RainyDaily || style is null)
        {
            throw MissingSafeSample(npcId, locale, "rainy_daily");
        }

        return CreateEntry(source, locale, text, rainy, ordinary, style);
    }

    private static StyleEvidence? SelectStyleEvidence(
        string npcId,
        string locale,
        string sourceKey,
        string sourceText,
        string season,
        int currentHeartLevel,
        DialogueAssetSnapshot ordinary)
    {
        int[] allowedHeartLevels = { 0, 2, 4, 6, 8, 10 };
        foreach (int candidateHeartLevel in allowedHeartLevels.Where(
                     heartLevel => heartLevel >= currentHeartLevel))
        {
            StyleExampleSelectionResult selection = StyleExampleSelector.Select(
                new DialogueStyleSelectionRequest
                {
                    NpcId = npcId,
                    Locale = locale,
                    CurrentSeason = season,
                    CurrentHeartLevel = candidateHeartLevel,
                    SourceKey = sourceKey,
                    SourceText = sourceText,
                    DialogueEntries = ordinary.Entries,
                });
            if (!selection.IsSuccessful)
            {
                continue;
            }

            List<string> keys = new();
            foreach (string selectedText in selection.Examples)
            {
                string? selectedKey = ordinary.Entries
                    .Where(pair => string.Equals(pair.Value, selectedText, StringComparison.Ordinal))
                    .Where(
                        pair => DialogueKeyClassifier.TryGetRequiredHeartLevel(
                            pair.Key,
                            out int hearts)
                            && hearts <= candidateHeartLevel)
                    .Where(pair => DialogueControlCommandScanner.Scan(pair.Value).IsSafeForStaticAppend)
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => pair.Key)
                    .FirstOrDefault();
                if (selectedKey is null)
                {
                    throw new InvalidDataException("StyleExampleSelector 返回了无法映射回 ordinary key 的文本。");
                }

                keys.Add(selectedKey);
            }

            return new StyleEvidence(
                keys,
                selection.Examples,
                season,
                candidateHeartLevel);
        }

        return null;
    }

    private static VanillaDialogueManifestEntry CreateEntry(
        DialogueSourceIdentity source,
        string locale,
        string sourceText,
        DialogueAssetSnapshot sourceAsset,
        DialogueAssetSnapshot styleAsset,
        StyleEvidence style)
    {
        return new VanillaDialogueManifestEntry
        {
            NpcId = source.NpcId,
            Locale = locale,
            SourceFamily = source.Family == DialogueSourceFamily.OrdinaryDaily
                ? "ordinary_daily"
                : "rainy_daily",
            AssetName = source.AssetName,
            DialogueKey = source.DialogueKey,
            SourceText = sourceText,
            SourceHash = "sha256:" + Hashing.Sha256Hex(sourceText),
            LocalizedXnbPath = sourceAsset.LocalizedXnbPath,
            LocalizedXnbSha256 = sourceAsset.LocalizedXnbSha256,
            StyleAssetName = styleAsset.AssetName,
            StyleLocalizedXnbPath = styleAsset.LocalizedXnbPath,
            StyleLocalizedXnbSha256 = styleAsset.LocalizedXnbSha256,
            StyleContextSeason = style.Season,
            StyleContextHeartLevel = style.HeartLevel,
            StyleKeys = style.Keys,
            StyleTexts = style.Texts,
        };
    }

    private static string SeasonForKey(string key)
    {
        string? season = new[] { "spring", "summer", "fall", "winter" }
            .FirstOrDefault(value => key.StartsWith(value + "_", StringComparison.Ordinal));
        return season ?? "spring";
    }

    private static InvalidDataException MissingSafeSample(
        string npcId,
        string locale,
        string family)
    {
        return new InvalidDataException(
            $"npc={npcId} locale={locale} source_family={family} 缺少最少安全样本。");
    }

    private sealed record StyleEvidence(
        IReadOnlyList<string> Keys,
        IReadOnlyList<string> Texts,
        string Season,
        int HeartLevel);
}

/// <summary>把本机路径约束为不含绝对用户目录的 Content-relative POSIX path。</summary>
public static class ManifestPath
{
    /// <summary>文件必须位于 root 内；否则拒绝而不是写入 <c>..</c>。</summary>
    public static string ToContentRelativePosixPath(string contentRoot, string filePath)
    {
        string fullRoot = Path.GetFullPath(contentRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string fullFile = Path.GetFullPath(filePath);
        string relative = Path.GetRelativePath(fullRoot, fullFile);
        if (Path.IsPathFullyQualified(relative)
            || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException("XNB 文件不在指定 Content root 内。", nameof(filePath));
        }

        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }
}

/// <summary>共享 fixture 的唯一稳定 JSON serializer。</summary>
public static class ManifestJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    /// <summary>序列化为 UTF-8 友好的稳定缩进 JSON，并固定一个结尾换行。</summary>
    public static string Serialize(VanillaDialogueManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return JsonSerializer.Serialize(manifest, Options) + "\n";
    }
}

/// <summary>同目录临时文件 + replace 的显式输出边界。</summary>
public static class AtomicManifestWriter
{
    /// <summary>
    /// 写入 UTF-8 no-BOM；任一异常尽力清理临时文件，不触碰原有目标内容。
    /// </summary>
    public static void Write(string outputPath, string json)
    {
        if (string.IsNullOrEmpty(outputPath))
        {
            throw new ArgumentException("输出路径不得为空。", nameof(outputPath));
        }
        ArgumentNullException.ThrowIfNull(json);
        string fullOutputPath = Path.GetFullPath(outputPath);
        string? directory = Path.GetDirectoryName(fullOutputPath);
        if (directory is null || !Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException("--output 的父目录必须预先存在。");
        }

        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullOutputPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, fullOutputPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}

/// <summary>正文、XNB bytes 与完整 manifest 共用的 SHA-256 hex 实现。</summary>
public static class Hashing
{
    public static string Sha256Hex(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Sha256Hex(Encoding.UTF8.GetBytes(value));
    }

    public static string Sha256Hex(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    }
}
