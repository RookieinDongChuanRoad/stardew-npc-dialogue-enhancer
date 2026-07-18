using StardewNpcAgent.Game;

namespace StardewNpcAgent.Tests.Game;

/// <summary>
/// 验证 translation key 解析只接受目标 NPC 自己的保守普通日常 key。
/// </summary>
public sealed class DialogueKeyClassifierTests
{
    /// <summary>
    /// 两个 MVP NPC 的季节、日期、星期和好感变体都属于原版普通 daily 选择链。
    /// </summary>
    [Theory]
    [InlineData("Characters\\Dialogue\\Abigail:spring_Mon", "Abigail", "Characters/Dialogue/Abigail", "spring_Mon")]
    [InlineData("Characters/Dialogue/Abigail:15_2", "Abigail", "Characters/Dialogue/Abigail", "15_2")]
    [InlineData("Characters/Dialogue/Sebastian:winter_Fri10_2", "Sebastian", "Characters/Dialogue/Sebastian", "winter_Fri10_2")]
    [InlineData("Characters/Dialogue/Sebastian:summer_28_*", "Sebastian", "Characters/Dialogue/Sebastian", "summer_28_*")]
    public void ClassifyTranslationKey_AcceptsConservativeOrdinaryDailyKeys(
        string translationKey,
        string npcId,
        string expectedAssetName,
        string expectedDialogueKey)
    {
        DialogueKeyClassification result = DialogueKeyClassifier.ClassifyTranslationKey(translationKey, npcId);

        Assert.True(result.IsOrdinaryDaily, result.ReasonCode.ToString());
        Assert.Equal(DialogueKeyReasonCode.OrdinaryDaily, result.ReasonCode);
        ParsedDialogueKey parsed = Assert.IsType<ParsedDialogueKey>(result.ParsedKey);
        Assert.Equal(expectedAssetName, parsed.AssetName);
        Assert.Equal(expectedDialogueKey, parsed.DialogueKey);
        Assert.Equal(npcId, parsed.NpcId);
    }

    /// <summary>
    /// 原版 NPC 通过 <c>LoadedDialogueKey</c> 构造的 TranslationKey 使用反斜杠，而 Mod 的
    /// candidate identity 使用规范化正斜杠。身份匹配只能等价处理路径分隔符；NPC、sheet、
    /// ordinary key 及 key 全文仍必须完全一致，特殊对话或相邻日期绝不能借此放行。
    /// </summary>
    [Theory]
    [InlineData("Characters\\Dialogue\\Sebastian:fall_Mon", "Characters/Dialogue/Sebastian", "fall_Mon", true)]
    [InlineData("Characters/Dialogue/Sebastian:fall_Mon", "Characters\\Dialogue\\Sebastian", "fall_Mon", true)]
    [InlineData("Characters/Dialogue/Abigail:fall_Mon", "Characters/Dialogue/Sebastian", "fall_Mon", false)]
    [InlineData("Characters/Dialogue/Sebastian:fall_Tue", "Characters/Dialogue/Sebastian", "fall_Mon", false)]
    [InlineData("Characters/Dialogue/Sebastian:divorced", "Characters/Dialogue/Sebastian", "divorced", false)]
    [InlineData("Characters//Dialogue/Sebastian:fall_Mon", "Characters/Dialogue/Sebastian", "fall_Mon", false)]
    [InlineData(null, "Characters/Dialogue/Sebastian", "fall_Mon", false)]
    public void MatchesOrdinaryDailyIdentity_NormalizesOnlyAssetSeparators(
        string? actualTranslationKey,
        string expectedAssetName,
        string expectedDialogueKey,
        bool expectedMatch)
    {
        bool matches = DialogueKeyClassifier.MatchesOrdinaryDailyIdentity(
            actualTranslationKey,
            "Sebastian",
            expectedAssetName,
            expectedDialogueKey);

        Assert.Equal(expectedMatch, matches);
    }

    /// <summary>
    /// 礼物、任务、问题、婚姻和事件 key 不在普通 daily 白名单中，不能靠名称猜测放行。
    /// </summary>
    [Theory]
    [InlineData("Characters/Dialogue/Abigail:AcceptGift_(O)66")]
    [InlineData("Characters/Dialogue/Abigail:questComplete")]
    [InlineData("Characters/Dialogue/Abigail:Question")]
    [InlineData("Characters/Dialogue/Abigail:divorced")]
    [InlineData("Characters/Dialogue/Abigail:spring_Mon_inlaw_Sebastian")]
    [InlineData("Characters/Dialogue/Abigail:dating_Abigail")]
    [InlineData("Characters/Dialogue/Abigail:GreenRain")]
    public void ClassifyTranslationKey_RejectsSpecialDialogueKeys(string translationKey)
    {
        DialogueKeyClassification result = DialogueKeyClassifier.ClassifyTranslationKey(translationKey, "Abigail");

        Assert.False(result.IsOrdinaryDaily);
        Assert.Equal(DialogueKeyReasonCode.NotOrdinaryDailyKey, result.ReasonCode);
        Assert.Null(result.ParsedKey);
    }

    /// <summary>
    /// 即使 key 形状像 daily，只要资产不是目标 NPC 自己的 dialogue sheet 就必须拒绝。
    /// </summary>
    [Theory]
    [InlineData("Data/ExtraDialogue:spring_Mon", DialogueKeyReasonCode.NonNpcDialogueAsset)]
    [InlineData("Characters/Dialogue/Sebastian:spring_Mon", DialogueKeyReasonCode.NpcSheetMismatch)]
    [InlineData("Characters/Dialogue/Abigail", DialogueKeyReasonCode.MalformedTranslationKey)]
    [InlineData("Characters/Dialogue/../Abigail:spring_Mon", DialogueKeyReasonCode.NonNpcDialogueAsset)]
    [InlineData(" Characters/Dialogue/Abigail:spring_Mon", DialogueKeyReasonCode.MalformedTranslationKey)]
    public void ClassifyTranslationKey_RejectsWrongAssetSheetOrMalformedPath(
        string translationKey,
        DialogueKeyReasonCode expectedReason)
    {
        DialogueKeyClassification result = DialogueKeyClassifier.ClassifyTranslationKey(translationKey, "Abigail");

        Assert.False(result.IsOrdinaryDaily);
        Assert.Equal(expectedReason, result.ReasonCode);
        Assert.Null(result.ParsedKey);
    }

    /// <summary>
    /// 目标 ID 或 translation key 缺失属于未知来源，必须 fail closed。
    /// </summary>
    [Theory]
    [InlineData(null, "Abigail")]
    [InlineData("", "Abigail")]
    [InlineData("Characters/Dialogue/Abigail:spring_Mon", null)]
    [InlineData("Characters/Dialogue/Abigail:spring_Mon", "")]
    public void ClassifyTranslationKey_RejectsMissingInputs(string? translationKey, string? npcId)
    {
        DialogueKeyClassification result = DialogueKeyClassifier.ClassifyTranslationKey(translationKey, npcId);

        Assert.False(result.IsOrdinaryDaily);
        Assert.Equal(DialogueKeyReasonCode.MissingInput, result.ReasonCode);
    }

    /// <summary>
    /// Dictionary key 的首尾空白和终止换行不能利用正则 `$` 的宽松语义伪装成完整 daily key。
    /// </summary>
    [Theory]
    [InlineData("spring_Mon\n")]
    [InlineData("spring_Mon\r\n")]
    [InlineData(" spring_Mon")]
    [InlineData("spring_Mon ")]
    public void IsOrdinaryDailyKey_RejectsLeadingTrailingWhitespaceAndNewlines(string dialogueKey)
    {
        Assert.False(DialogueKeyClassifier.IsOrdinaryDailyKey(dialogueKey));
        Assert.False(DialogueKeyClassifier.TryGetRequiredHeartLevel(dialogueKey, out _));
    }
}
