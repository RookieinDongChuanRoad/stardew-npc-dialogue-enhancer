using StardewNpcAgent.Game;

namespace StardewNpcAgent.Tests.Game;

/// <summary>
/// 冻结 ordinary/rainy daily 来源的精确分类边界。
/// </summary>
public sealed class DialogueSourceClassifierTests
{
    /// <summary>
    /// 只有目标 NPC 自身的有限 ordinary key 与共享 rainy sheet 的 exact NPC key 才能分类。
    /// 路径规则只等价处理反斜杠，不允许 Trim、大小写折叠或 Unicode normalization。
    /// </summary>
    [Theory]
    [InlineData("Characters/Dialogue/Abigail:fall_Mon", "Abigail", "OrdinaryDaily", "Characters/Dialogue/Abigail", "fall_Mon")]
    [InlineData("Characters\\Dialogue\\Abigail:fall_Mon", "Abigail", "OrdinaryDaily", "Characters/Dialogue/Abigail", "fall_Mon")]
    [InlineData("Characters/Dialogue/rainy:Abigail", "Abigail", "RainyDaily", "Characters/Dialogue/rainy", "Abigail")]
    [InlineData("Characters\\Dialogue\\rainy:Abigail", "Abigail", "RainyDaily", "Characters/Dialogue/rainy", "Abigail")]
    [InlineData("Characters/Dialogue/rainy:Sebastian", "Abigail", null, null, null)]
    [InlineData("Characters/Dialogue/Rainy:Abigail", "Abigail", null, null, null)]
    [InlineData("Characters/Dialogue/MarriageDialogueAbigail:Mon", "Abigail", null, null, null)]
    [InlineData("Characters/Dialogue/MarriageDialogue:Mon", "Abigail", null, null, null)]
    [InlineData("Strings/StringsFromCSFiles:NPC.cs.123", "Abigail", null, null, null)]
    [InlineData("Characters/Dialogue/Abigail:divorced", "Abigail", null, null, null)]
    [InlineData("Characters/Dialogue/Abigail:EngagementDialogue", "Abigail", null, null, null)]
    [InlineData(" Characters/Dialogue/Abigail:fall_Mon", "Abigail", null, null, null)]
    [InlineData("Characters/Dialogue/Abigail:fall_Mon ", "Abigail", null, null, null)]
    [InlineData("Characters/Dialogue/Á:fall_Mon", "Á", null, null, null)]
    public void ClassifyTranslationKey_AcceptsOnlyExactSupportedDailySources(
        string translationKey,
        string expectedNpcId,
        string? expectedFamily,
        string? expectedAssetName,
        string? expectedDialogueKey)
    {
        DialogueSourceIdentity? identity = DialogueSourceClassifier.ClassifyTranslationKey(
            translationKey,
            expectedNpcId);
        if (expectedFamily is null)
        {
            Assert.Null(identity);
            return;
        }

        Assert.NotNull(identity);
        Assert.Equal(expectedFamily, identity!.Family.ToString());
        Assert.Equal(expectedNpcId, identity.NpcId);
        Assert.Equal(expectedAssetName, identity.AssetName);
        Assert.Equal(expectedDialogueKey, identity.DialogueKey);
    }
}
