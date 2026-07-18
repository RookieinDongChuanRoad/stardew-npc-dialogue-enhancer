using System.Text.Json;
using StardewNpcAgent.Configuration;
using StardewNpcAgent.Game;

namespace StardewNpcAgent.Tests.Configuration;

/// <summary>
/// 冻结字段存在性状态到运行时 enabled NPC 集合的唯一兼容策略。
/// </summary>
public sealed class ModConfigCompatibilityPolicyTests
{
    /// <summary>
    /// 真正的新配置使用十二人默认值，并保持 canonical 顺序且不产生兼容 warning。
    /// </summary>
    [Fact]
    public void Resolve_NewConfiguration_EnablesCanonicalTwelve()
    {
        ModConfigCompatibilityResult result = ModConfigCompatibilityPolicy.ResolveEnabledNpcIds(
            new ModConfig(),
            TargetNpcIdsFieldState.NewConfiguration);

        Assert.True(result.IsConfigurationUsable);
        Assert.Equal(VanillaMarriageableNpcRegistry.AllIds, result.EnabledNpcIds);
        Assert.Empty(result.WarningCodes);
        Assert.Equal(0, result.IgnoredUnsupportedNpcIdCount);
    }

    /// <summary>
    /// 显式数组按用户首次出现顺序 trim/dedupe，再与固定 supported 集合取交集；未知值不得回写删除。
    /// </summary>
    [Fact]
    public void Resolve_ExplicitArray_FiltersUnsupportedWithoutMutatingConfiguration()
    {
        ModConfig config = new()
        {
            TargetNpcIds = new List<string?>
            {
                " Shane ",
                "CustomNpc",
                null,
                "Abigail",
                "Shane",
                "customnpc",
                " ",
            },
        };
        string?[] originalValues = config.TargetNpcIds.ToArray();

        ModConfigCompatibilityResult result = ModConfigCompatibilityPolicy.ResolveEnabledNpcIds(
            config,
            TargetNpcIdsFieldState.ExplicitValue);

        Assert.True(result.IsConfigurationUsable);
        Assert.Equal(new[] { "Shane", "Abigail" }, result.EnabledNpcIds);
        Assert.Equal(
            new[] { "TARGET_NPC_IDS_UNSUPPORTED_IGNORED" },
            result.WarningCodes);
        Assert.Equal(2, result.IgnoredUnsupportedNpcIdCount);
        Assert.Equal(originalValues, config.TargetNpcIds);
    }

    /// <summary>
    /// 显式 null 与显式空数组都表达合法零目标，不能被新默认值回填。
    /// </summary>
    [Theory]
    [InlineData(TargetNpcIdsFieldState.ExplicitNull)]
    [InlineData(TargetNpcIdsFieldState.ExplicitValue)]
    public void Resolve_ExplicitNullOrEmpty_ReturnsUsableEmptySet(
        TargetNpcIdsFieldState fieldState)
    {
        ModConfig config = new()
        {
            TargetNpcIds = fieldState == TargetNpcIdsFieldState.ExplicitNull
                ? null
                : new List<string?>(),
        };

        ModConfigCompatibilityResult result = ModConfigCompatibilityPolicy.ResolveEnabledNpcIds(
            config,
            fieldState);

        Assert.True(result.IsConfigurationUsable);
        Assert.Empty(result.EnabledNpcIds);
        Assert.Empty(result.WarningCodes);
    }

    /// <summary>
    /// 旧 object 缺字段时不能读取新 initializer；每次启动都稳定使用 legacy 两人并发出固定 code。
    /// </summary>
    [Fact]
    public void Resolve_MissingField_UsesLegacyTwoEveryTime()
    {
        ModConfigCompatibilityResult first = ModConfigCompatibilityPolicy.ResolveEnabledNpcIds(
            new ModConfig(),
            TargetNpcIdsFieldState.MissingFromExistingConfiguration);
        ModConfigCompatibilityResult second = ModConfigCompatibilityPolicy.ResolveEnabledNpcIds(
            new ModConfig(),
            TargetNpcIdsFieldState.MissingFromExistingConfiguration);

        Assert.True(first.IsConfigurationUsable);
        Assert.Equal(new[] { "Abigail", "Sebastian" }, first.EnabledNpcIds);
        Assert.Equal(first.EnabledNpcIds, second.EnabledNpcIds);
        Assert.Equal(
            new[] { "TARGET_NPC_IDS_MISSING_USING_LEGACY_DEFAULT" },
            first.WarningCodes);
        Assert.Equal(first.WarningCodes, second.WarningCodes);
    }

    /// <summary>
    /// 连续两次启动读取同一旧文件时都必须保持 legacy 两人，而且第一次读取不能把十二人 initializer 写回磁盘。
    /// </summary>
    [Fact]
    public void Resolve_TwoReadsOfLegacyFile_PreserveBytesAndRemainLegacyTwo()
    {
        string testDirectory = Path.Combine(
            Path.GetTempPath(),
            $"StardewNpcAgent.LegacyConfig.{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        string configPath = Path.Combine(testDirectory, "config.json");
        byte[] originalBytes = System.Text.Encoding.UTF8.GetBytes(
            "{\n  \"EnableAgentDialogue\": false\n}\n");
        File.WriteAllBytes(configPath, originalBytes);

        try
        {
            ModConfigCompatibilityResult first = InspectReadAndResolve(testDirectory, configPath);
            ModConfigCompatibilityResult second = InspectReadAndResolve(testDirectory, configPath);

            Assert.Equal(new[] { "Abigail", "Sebastian" }, first.EnabledNpcIds);
            Assert.Equal(first.EnabledNpcIds, second.EnabledNpcIds);
            Assert.Equal(originalBytes, File.ReadAllBytes(configPath));
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    /// <summary>
    /// Inspector 已判 invalid 或已有文件反序列化返回 null 时，策略必须保持不可用空集合。
    /// </summary>
    [Theory]
    [InlineData(TargetNpcIdsFieldState.InvalidConfiguration, true)]
    [InlineData(TargetNpcIdsFieldState.ExplicitValue, false)]
    public void Resolve_InvalidStateOrMissingDeserializedConfig_FailsClosed(
        TargetNpcIdsFieldState fieldState,
        bool supplyConfig)
    {
        ModConfigCompatibilityResult result = ModConfigCompatibilityPolicy.ResolveEnabledNpcIds(
            supplyConfig ? new ModConfig() : null,
            fieldState);

        Assert.False(result.IsConfigurationUsable);
        Assert.Empty(result.EnabledNpcIds);
        Assert.Empty(result.WarningCodes);
    }

    /// <summary>
    /// 策略返回值必须是只读快照，调用方不能在 runtime 构造前篡改 enabled 或 warning 集合。
    /// </summary>
    [Fact]
    public void Resolve_ReturnsImmutableSnapshots()
    {
        ModConfigCompatibilityResult result = ModConfigCompatibilityPolicy.ResolveEnabledNpcIds(
            new ModConfig(),
            TargetNpcIdsFieldState.NewConfiguration);

        IList<string> enabled = Assert.IsAssignableFrom<IList<string>>(result.EnabledNpcIds);
        IList<string> warnings = Assert.IsAssignableFrom<IList<string>>(result.WarningCodes);

        Assert.Throws<NotSupportedException>(() => enabled[0] = "Changed");
        Assert.Throws<NotSupportedException>(() => warnings.Add("CHANGED"));
    }

    /// <summary>
    /// 两个 runtime 共用的 snapshot helper 必须实际复制输入，不能只把可变 IReadOnlyList 引用换个类型保存。
    /// </summary>
    [Fact]
    public void EnabledNpcIdsSnapshot_CopiesCallerListAndReturnsReadOnlyView()
    {
        List<string> callerList = new() { "Shane" };

        IReadOnlyList<string> snapshot = EnabledNpcIdsSnapshot.Create(callerList);
        callerList[0] = "Changed";
        callerList.Add("Abigail");

        Assert.Equal(new[] { "Shane" }, snapshot);
        IList<string> listView = Assert.IsAssignableFrom<IList<string>>(snapshot);
        Assert.Throws<NotSupportedException>(() => listView[0] = "ChangedAgain");
    }

    /// <summary>
    /// 模拟 ModEntry 的 existing-config 纯读分支；只反序列化，不调用任何会生成配置的 helper。
    /// </summary>
    private static ModConfigCompatibilityResult InspectReadAndResolve(
        string testDirectory,
        string configPath)
    {
        TargetNpcIdsFieldState fieldState = ModConfigFileInspector.Inspect(testDirectory);
        string json = File.ReadAllText(configPath);
        ModConfig? config = JsonSerializer.Deserialize<ModConfig>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return ModConfigCompatibilityPolicy.ResolveEnabledNpcIds(config, fieldState);
    }
}
