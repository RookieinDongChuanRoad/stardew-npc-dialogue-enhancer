using System.Text.RegularExpressions;

namespace StardewNpcAgent.Game;

/// <summary>
/// translation key 分类结果的稳定 reason code。
/// </summary>
public enum DialogueKeyReasonCode
{
    /// <summary>来源是目标 NPC 自身的保守普通日常 key。</summary>
    OrdinaryDaily,

    /// <summary>translation key 或目标 NPC ID 缺失。</summary>
    MissingInput,

    /// <summary>字符串不能无歧义拆为 <c>asset:key</c>。</summary>
    MalformedTranslationKey,

    /// <summary>资产不属于 <c>Characters/Dialogue</c>。</summary>
    NonNpcDialogueAsset,

    /// <summary>dialogue sheet 与目标 NPC 内部 ID 不一致。</summary>
    NpcSheetMismatch,

    /// <summary>资产正确，但 key 不属于保守 ordinary daily 白名单。</summary>
    NotOrdinaryDailyKey,
}

/// <summary>
/// 已证明来自目标 NPC 普通日常资产的来源标识。
/// </summary>
/// <param name="NpcId">目标 NPC 的稳定内部 ID。</param>
/// <param name="AssetName">统一使用正斜杠的精确对话资产名。</param>
/// <param name="DialogueKey">资产字典中的精确字段 key。</param>
public sealed record ParsedDialogueKey(string NpcId, string AssetName, string DialogueKey);

/// <summary>
/// translation key 的完整分类结果；失败时不返回半可信 parsed key。
/// </summary>
/// <param name="IsOrdinaryDaily">是否可以继续普通 daily 资格检查。</param>
/// <param name="ReasonCode">确定性分类原因。</param>
/// <param name="ParsedKey">只有成功时非 null。</param>
public sealed record DialogueKeyClassification(
    bool IsOrdinaryDaily,
    DialogueKeyReasonCode ReasonCode,
    ParsedDialogueKey? ParsedKey);

/// <summary>
/// 保守解析 Stardew <c>Dialogue.TranslationKey</c>，不根据相似名称猜测来源。
/// </summary>
public static class DialogueKeyClassifier
{
    /// <summary>
    /// NPC 日常选择链实际会尝试的 key 形状：可选季节 + 日期或星期/心级 + 可选年份。
    /// </summary>
    /// <remarks>
    /// 日期只允许 1～28；心级只允许 2、4、6、8、10；年份只允许原版选择链中的 1、2
    /// 或日期 wildcard。婚姻、姻亲、任务、礼物、问题和自定义事件后缀不会匹配。
    /// </remarks>
    private static readonly Regex OrdinaryDailyKeyPattern = new(
        @"\A(?:(?:spring|summer|fall|winter)_)?(?:"
            + @"(?:[1-9]|1[0-9]|2[0-8])(?:_(?:1|2|\*))?"
            + @"|(?:Mon|Tue|Wed|Thu|Fri|Sat|Sun)(?:2|4|6|8|10)?(?:_(?:1|2))?"
            + @")\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// 从 ordinary weekday key 提取原版选择链中的最低心数门槛；日期 key 没有心数门槛。
    /// </summary>
    private static readonly Regex WeekdayHeartKeyPattern = new(
        @"\A(?:(?:spring|summer|fall|winter)_)?"
            + @"(?:Mon|Tue|Wed|Thu|Fri|Sat|Sun)(?<hearts>10|2|4|6|8)?(?:_(?:1|2))?\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// 解析 translation key，并同时证明资产 sheet、NPC 和 ordinary key 三者一致。
    /// </summary>
    /// <param name="translationKey">游戏公开 API 返回的 <c>asset:key</c>。</param>
    /// <param name="expectedNpcId">当前正在处理的稳定 NPC 内部 ID。</param>
    /// <returns>成功时携带规范化资产名；任一不确定条件都返回失败 reason。</returns>
    public static DialogueKeyClassification ClassifyTranslationKey(
        string? translationKey,
        string? expectedNpcId)
    {
        if (string.IsNullOrWhiteSpace(translationKey) || string.IsNullOrWhiteSpace(expectedNpcId))
        {
            return Rejected(DialogueKeyReasonCode.MissingInput);
        }

        // 首尾空白可能让内容管线和字典查找看到不同名字，不能静默 Trim 后继续。
        if (!string.Equals(translationKey, translationKey.Trim(), StringComparison.Ordinal)
            || !string.Equals(expectedNpcId, expectedNpcId.Trim(), StringComparison.Ordinal))
        {
            return Rejected(DialogueKeyReasonCode.MalformedTranslationKey);
        }

        int separatorIndex = translationKey.IndexOf(':');
        if (separatorIndex <= 0
            || separatorIndex != translationKey.LastIndexOf(':')
            || separatorIndex == translationKey.Length - 1)
        {
            return Rejected(DialogueKeyReasonCode.MalformedTranslationKey);
        }

        string rawAssetName = translationKey[..separatorIndex];
        string dialogueKey = translationKey[(separatorIndex + 1)..];
        string assetName = NormalizeAssetName(rawAssetName);

        const string npcDialoguePrefix = "Characters/Dialogue/";
        if (!assetName.StartsWith(npcDialoguePrefix, StringComparison.Ordinal)
            || assetName.Contains("//", StringComparison.Ordinal))
        {
            return Rejected(DialogueKeyReasonCode.NonNpcDialogueAsset);
        }

        string sheetName = assetName[npcDialoguePrefix.Length..];
        if (sheetName.Length == 0
            || sheetName.Contains('/', StringComparison.Ordinal)
            || sheetName is "." or "..")
        {
            return Rejected(DialogueKeyReasonCode.NonNpcDialogueAsset);
        }

        if (!string.Equals(sheetName, expectedNpcId, StringComparison.Ordinal))
        {
            return Rejected(DialogueKeyReasonCode.NpcSheetMismatch);
        }

        if (!IsOrdinaryDailyKey(dialogueKey))
        {
            return Rejected(DialogueKeyReasonCode.NotOrdinaryDailyKey);
        }

        return new DialogueKeyClassification(
            IsOrdinaryDaily: true,
            DialogueKeyReasonCode.OrdinaryDaily,
            new ParsedDialogueKey(expectedNpcId, assetName, dialogueKey));
    }

    /// <summary>
    /// 比较一个原版 TranslationKey 与候选 ordinary daily 身份，只规范化资产路径分隔符。
    /// </summary>
    /// <param name="actualTranslationKey">
    /// 从公开 <c>Dialogue.TranslationKey</c> 读取的原值；原版通常使用反斜杠资产路径。
    /// </param>
    /// <param name="expectedNpcId">候选目标 NPC 的稳定内部 ID。</param>
    /// <param name="expectedAssetName">候选解析器保存的 dialogue asset 名；通常已使用正斜杠。</param>
    /// <param name="expectedDialogueKey">候选字典中的 exact ordinary daily key。</param>
    /// <returns>
    /// 只有两端都独立通过 ordinary 分类，且解析后的 NPC、规范化资产名和 key 逐项相同时为 true。
    /// </returns>
    /// <remarks>
    /// 不能直接比较原始字符串：Stardew 的 <c>NPC.LoadedDialogueKey</c> 使用
    /// <c>Characters\Dialogue\...</c>，而 SMAPI/Mod identity 统一使用正斜杠。本方法不会 Trim、
    /// 猜测近似 key 或放宽 ordinary 白名单；错误 NPC、特殊后缀、不同日期和畸形路径仍 fail closed。
    /// </remarks>
    public static bool MatchesOrdinaryDailyIdentity(
        string? actualTranslationKey,
        string? expectedNpcId,
        string? expectedAssetName,
        string? expectedDialogueKey)
    {
        if (string.IsNullOrEmpty(expectedAssetName) || string.IsNullOrEmpty(expectedDialogueKey))
        {
            return false;
        }

        DialogueKeyClassification actual = ClassifyTranslationKey(
            actualTranslationKey,
            expectedNpcId);
        DialogueKeyClassification expected = ClassifyTranslationKey(
            $"{expectedAssetName}:{expectedDialogueKey}",
            expectedNpcId);

        return actual.IsOrdinaryDaily
            && expected.IsOrdinaryDaily
            && actual.ParsedKey is not null
            && expected.ParsedKey is not null
            && actual.ParsedKey == expected.ParsedKey;
    }

    /// <summary>
    /// 判断字典 key 是否属于 ordinary daily 的有限白名单。
    /// </summary>
    /// <param name="dialogueKey">不含资产名前缀的字段 key。</param>
    /// <returns>只有完整匹配有限正则时为 true。</returns>
    public static bool IsOrdinaryDailyKey(string? dialogueKey)
    {
        return !string.IsNullOrEmpty(dialogueKey)
            && OrdinaryDailyKeyPattern.IsMatch(dialogueKey);
    }

    /// <summary>
    /// 读取 ordinary key 的最低心数门槛，供风格样本排除尚未解锁的关系语气。
    /// </summary>
    /// <param name="dialogueKey">已经或即将通过 ordinary 白名单的字段 key。</param>
    /// <param name="requiredHeartLevel">
    /// 成功时为 0、2、4、6、8 或 10；日期 key 与无心数 weekday key 返回 0。
    /// </param>
    /// <returns>key 属于 ordinary daily 白名单时为 true，否则为 false。</returns>
    public static bool TryGetRequiredHeartLevel(
        string? dialogueKey,
        out int requiredHeartLevel)
    {
        requiredHeartLevel = 0;
        if (!IsOrdinaryDailyKey(dialogueKey))
        {
            return false;
        }

        Match weekdayMatch = WeekdayHeartKeyPattern.Match(dialogueKey!);
        if (!weekdayMatch.Success)
        {
            // 日期 key 由 day/year 决定，不含 friendship gate，因此属于基线关系阶段。
            return true;
        }

        Group heartsGroup = weekdayMatch.Groups["hearts"];
        if (heartsGroup.Success)
        {
            requiredHeartLevel = int.Parse(heartsGroup.Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        return true;
    }

    /// <summary>
    /// 只做 SMAPI 资产名常见的分隔符归一化，不折叠路径段、不修改大小写。
    /// </summary>
    /// <param name="assetName">translation key 中的原始资产部分。</param>
    /// <returns>使用正斜杠的名字。</returns>
    public static string NormalizeAssetName(string assetName)
    {
        ArgumentNullException.ThrowIfNull(assetName);
        return assetName.Replace('\\', '/');
    }

    /// <summary>
    /// 创建不携带半可信解析结果的失败值。
    /// </summary>
    private static DialogueKeyClassification Rejected(DialogueKeyReasonCode reasonCode)
    {
        return new DialogueKeyClassification(false, reasonCode, null);
    }
}
