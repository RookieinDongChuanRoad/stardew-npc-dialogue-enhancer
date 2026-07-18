using StardewNpcAgent.Configuration;
using StardewNpcAgent.Game;

namespace StardewNpcAgent.Tests.Configuration;

/// <summary>
/// 冻结静态对话 Spike 的安全默认值和配置清洗边界。
/// </summary>
public sealed class ModConfigTests
{
    /// <summary>
    /// 默认安装必须是零行为；只有玩家显式开启后，运行时才可订阅并修改对话资产。
    /// </summary>
    [Fact]
    public void Defaults_AreDisabledAndTargetCanonicalTwelve()
    {
        ModConfig config = new();

        Assert.False(config.EnableStaticDialogueSpike);
        Assert.False(config.EnableAgentDialogue);
        Assert.Equal(VanillaMarriageableNpcRegistry.AllIds, config.GetNormalizedTargetNpcIds());
        Assert.False(string.IsNullOrWhiteSpace(config.StaticDialogueMarker));
        Assert.DoesNotContain('$', config.StaticDialogueMarker);
        Assert.DoesNotContain('#', config.StaticDialogueMarker);
        Assert.Equal("http://127.0.0.1:8000/", config.BackendBaseUrl);
        Assert.Equal(3_000, config.EventRequestTimeoutMilliseconds);
        Assert.Equal(120_000, config.GenerationRequestTimeoutMilliseconds);
        Assert.Equal(3_000, config.DisplayAckRequestTimeoutMilliseconds);
    }

    /// <summary>
    /// 每个配置实例都必须从 registry 复制独立 list；修改一个实例不能污染静态 registry 或下一实例。
    /// </summary>
    [Fact]
    public void DefaultTargetNpcIds_AreIndependentCopiesOfRegistry()
    {
        ModConfig first = new();
        ModConfig second = new();

        Assert.NotNull(first.TargetNpcIds);
        first.TargetNpcIds!.RemoveAt(0);

        Assert.Equal(11, first.GetNormalizedTargetNpcIds().Count);
        Assert.Equal(VanillaMarriageableNpcRegistry.AllIds, second.GetNormalizedTargetNpcIds());
        Assert.Equal("Abigail", VanillaMarriageableNpcRegistry.AllIds[0]);
    }

    /// <summary>
    /// JSON 配置可能包含空白、空项或重复 ID；运行时必须确定性清洗，不能因坏配置崩溃。
    /// </summary>
    [Fact]
    public void TargetNpcIds_AreTrimmedDeduplicatedAndBlankSafe()
    {
        ModConfig config = new()
        {
            TargetNpcIds = new List<string?>
            {
                " Sebastian ",
                null,
                "",
                "Abigail",
                "Sebastian",
                "   ",
            },
        };

        IReadOnlyList<string> normalized = config.GetNormalizedTargetNpcIds();

        Assert.Equal(new[] { "Sebastian", "Abigail" }, normalized);
    }

    /// <summary>
    /// 整个列表被反序列化为 null 时也必须安全退化为空目标集，而不是抛空引用异常。
    /// </summary>
    [Fact]
    public void NullTargetNpcIds_NormalizesToEmptyList()
    {
        ModConfig config = new() { TargetNpcIds = null };

        Assert.Empty(config.GetNormalizedTargetNpcIds());
    }
}
