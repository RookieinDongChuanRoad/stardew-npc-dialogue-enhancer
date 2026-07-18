using StardewNpcAgent.Game;

namespace StardewNpcAgent.Tests.Game;

/// <summary>
/// 验证风格样本只来自调用方提供的同 NPC、当前 locale 已加载字典，并稳定选 2～5 条。
/// </summary>
public sealed class StyleExampleSelectorTests
{
    /// <summary>
    /// 同季节优先，其次按长度差和 key 的 ordinal 顺序；字典插入顺序不能影响结果。
    /// </summary>
    [Fact]
    public void Select_IsDeterministicAndPrioritizesSeasonThenLength()
    {
        DialogueStyleSelectionRequest request = CreateRequest(
            new Dictionary<string, string>
            {
                ["summer_Mon"] = "ZZZZZZZZZZ",
                ["spring_Wed"] = "BBBBBBBB",
                ["spring_Thu"] = "CCCCCCCCCCCC",
                ["spring_Tue"] = "AAAAAAAAAA",
                ["Mon"] = "YYYYYYYYYY",
                ["Tue"] = "XXXXXXXXXX",
            });

        StyleExampleSelectionResult first = StyleExampleSelector.Select(request);
        StyleExampleSelectionResult second = StyleExampleSelector.Select(request);

        Assert.True(first.IsSuccessful, first.ReasonCode.ToString());
        Assert.Equal(StyleExampleSelectionReasonCode.Selected, first.ReasonCode);
        Assert.Equal(
            new[] { "CCCCCCCCCCCC", "AAAAAAAAAA", "BBBBBBBB", "YYYYYYYYYY", "XXXXXXXXXX" },
            first.Examples);
        Assert.Equal(first.Examples, second.Examples);
    }

    /// <summary>
    /// source、自身重复文本、特殊 key、控制命令与多行文本都不能污染风格样本。
    /// </summary>
    [Fact]
    public void Select_ExcludesSourceSpecialKeysControlSyntaxAndDuplicates()
    {
        DialogueStyleSelectionRequest request = CreateRequest(
            new Dictionary<string, string>
            {
                ["spring_Mon"] = "SOURCE_TEXT",
                ["spring_Tue"] = "SOURCE_TEXT",
                ["divorced"] = "special",
                ["spring_Wed"] = "question$q 100/Yes_yes",
                ["spring_Thu"] = "line one\nline two",
                ["summer_Mon"] = "safe one",
                ["summer_Tue"] = "safe two",
                ["fall_Wed"] = "safe three",
                ["winter_Thu"] = "safe four",
            });

        StyleExampleSelectionResult result = StyleExampleSelector.Select(request);

        Assert.True(result.IsSuccessful, result.ReasonCode.ToString());
        Assert.Equal(new[] { "safe three", "safe four", "safe one", "safe two" }, result.Examples);
        Assert.DoesNotContain("SOURCE_TEXT", result.Examples);
    }

    /// <summary>
    /// 玩家名槽只允许出现在 approved source/output；风格样本仍是完全无 token 的纯文本。
    /// </summary>
    [Fact]
    public void Select_ExcludesPlayerNameSlotFromStyleExamples()
    {
        DialogueStyleSelectionRequest request = CreateRequest(
            new Dictionary<string, string>
            {
                ["spring_Tue"] = "你好，@。",
                ["summer_Mon"] = "safe one",
                ["summer_Tue"] = "safe two",
                ["fall_Wed"] = "safe three",
            });

        StyleExampleSelectionResult result = StyleExampleSelector.Select(request);

        Assert.True(result.IsSuccessful, result.ReasonCode.ToString());
        Assert.DoesNotContain("你好，@。", result.Examples);
        Assert.Equal(3, result.Examples.Count);
    }

    /// <summary>
    /// 两条独立安全样本已经能提供人物语气锚点；source 仍必须排除，不能重复充数。
    /// </summary>
    [Fact]
    public void Select_AcceptsTwoIndependentExamplesAndStillExcludesSource()
    {
        DialogueStyleSelectionRequest request = CreateRequest(
            new Dictionary<string, string>
            {
                ["spring_Mon"] = "SOURCE_TEXT",
                ["summer_Mon"] = "safe one",
                ["summer_Tue"] = "safe two",
                ["summer_Wed"] = "unsafe$h",
            });

        StyleExampleSelectionResult result = StyleExampleSelector.Select(request);

        Assert.True(result.IsSuccessful, result.ReasonCode.ToString());
        Assert.Equal(StyleExampleSelectionReasonCode.Selected, result.ReasonCode);
        Assert.Equal(new[] { "safe one", "safe two" }, result.Examples);
        Assert.DoesNotContain("SOURCE_TEXT", result.Examples);
    }

    /// <summary>
    /// 一条安全样本仍不足以形成独立风格锚点，必须返回空失败而不是部分结果。
    /// </summary>
    [Fact]
    public void Select_ReturnsNoExamplesWhenOnlyOneIsSafe()
    {
        DialogueStyleSelectionRequest request = CreateRequest(
            new Dictionary<string, string>
            {
                ["spring_Mon"] = "SOURCE_TEXT",
                ["summer_Mon"] = "safe one",
                ["summer_Tue"] = "unsafe$h",
            });

        StyleExampleSelectionResult result = StyleExampleSelector.Select(request);

        Assert.False(result.IsSuccessful);
        Assert.Equal(StyleExampleSelectionReasonCode.InsufficientSafeExamples, result.ReasonCode);
        Assert.Empty(result.Examples);
    }

    /// <summary>
    /// 原版 weekday-heart key 的数字表示最低心数；低关系阶段不能借用尚未解锁的高心语气。
    /// 同季节候选中，应先选与当前可用关系阶段更接近的 key，再比较文本长度。
    /// </summary>
    [Fact]
    public void Select_ExcludesLockedHeartKeysAndPrioritizesClosestAvailableStage()
    {
        DialogueStyleSelectionRequest request = CreateRequest(
            new Dictionary<string, string>
            {
                ["spring_Mon2"] = "SOURCE_TEXT",
                ["spring_Tue10"] = "locked ten-heart tone",
                ["spring_Wed4"] = "locked four-heart tone",
                ["spring_Thu2"] = "same season exact stage but much longer",
                ["spring_Fri"] = "same season baseline",
                ["summer_Mon2"] = "other season exact stage",
                ["winter_Tue"] = "other season baseline",
            });
        request.SourceKey = "spring_Mon2";
        request.CurrentHeartLevel = 2;

        StyleExampleSelectionResult first = StyleExampleSelector.Select(request);
        StyleExampleSelectionResult second = StyleExampleSelector.Select(request);

        Assert.True(first.IsSuccessful, first.ReasonCode.ToString());
        Assert.Equal(
            new[]
            {
                "same season exact stage but much longer",
                "same season baseline",
                "other season exact stage",
                "other season baseline",
            },
            first.Examples);
        Assert.DoesNotContain("locked ten-heart tone", first.Examples);
        Assert.DoesNotContain("locked four-heart tone", first.Examples);
        Assert.Equal(first.Examples, second.Examples);
    }

    /// <summary>
    /// NPC、locale、season 或字典缺失意味着调用方没有证明“同 NPC、当前 locale”。
    /// </summary>
    [Fact]
    public void Select_RejectsIncompleteSelectionContext()
    {
        DialogueStyleSelectionRequest request = CreateRequest(new Dictionary<string, string>());
        request.Locale = "";

        StyleExampleSelectionResult result = StyleExampleSelector.Select(request);

        Assert.False(result.IsSuccessful);
        Assert.Equal(StyleExampleSelectionReasonCode.InvalidContext, result.ReasonCode);
        Assert.Empty(result.Examples);
    }

    /// <summary>
    /// 建立固定 source；调用方明确提供 NPC 与 locale，而不是由 selector 猜测全局状态。
    /// </summary>
    private static DialogueStyleSelectionRequest CreateRequest(IReadOnlyDictionary<string, string> entries)
    {
        return new DialogueStyleSelectionRequest
        {
            NpcId = "Abigail",
            Locale = "zh-CN",
            CurrentSeason = "spring",
            CurrentHeartLevel = 4,
            SourceKey = "spring_Mon",
            SourceText = "SOURCE_TEXT",
            DialogueEntries = entries,
        };
    }
}
