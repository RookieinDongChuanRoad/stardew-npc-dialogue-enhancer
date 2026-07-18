using StardewNpcAgent.Integration;

namespace StardewNpcAgent.Tests.Integration;

/// <summary>
/// 用 Mono.Cecil 对真实已安装 Stardew Valley 程序集执行 gift patch 的完整 fail-closed 门禁。
/// </summary>
public sealed class GiftGivenCompatibilityTests
{
    [Fact]
    public void AuditInstalledGame_ExactVersionSignatureCallerOrderAndFingerprintsAreAccepted()
    {
        string assemblyPath = FindInstalledGameAssemblyPath();

        GiftGivenCompatibilityResult result = GiftGivenCompatibilityAudit.Audit(assemblyPath);

        Assert.True(result.IsCompatible, result.ReasonCode);
        Assert.Equal("1.6.15.24356", result.AssemblyVersion);
        Assert.Equal(
            "System.Void StardewValley.Farmer::onGiftGiven(StardewValley.NPC,StardewValley.Object)",
            result.TargetMethodSignature);
        Assert.Equal(
            "System.Void StardewValley.NPC::receiveGift(StardewValley.Object,StardewValley.Farmer,System.Boolean,System.Single,System.Boolean)",
            result.DirectCallerSignature);
        Assert.Equal(1, result.DirectCallerCount);
        Assert.Equal(0x0078, result.CallbackInstructionOffset);
        Assert.Equal(0x019C, result.TasteInstructionOffset);
        Assert.True(result.CallbackInstructionOffset < result.TasteInstructionOffset);
        Assert.Equal(
            "50d09cf20d2ef9df3899cfda29af675fa7f285931d22916e855c278d51f981a9",
            result.ReceiveGiftFingerprint);
        Assert.Equal(
            "635edcc394c801200b19fc961f4595502dfd29eff28136649e215a71250eed9d",
            result.GiftTasteFingerprint);
    }

    [Fact]
    public void Audit_NonGameAssemblyFailsClosedWithoutThrowingOrPatchingAnything()
    {
        string testAssemblyPath = typeof(GiftGivenCompatibilityTests).Assembly.Location;

        GiftGivenCompatibilityResult result = GiftGivenCompatibilityAudit.Audit(testAssemblyPath);

        Assert.False(result.IsCompatible);
        Assert.Equal("ASSEMBLY_IDENTITY_MISMATCH", result.ReasonCode);
    }

    [Fact]
    public void Audit_MissingFileReturnsStableFailureInsteadOfLeakingAbsolutePath()
    {
        string missingPath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.dll");

        GiftGivenCompatibilityResult result = GiftGivenCompatibilityAudit.Audit(missingPath);

        Assert.False(result.IsCompatible);
        Assert.Equal("ASSEMBLY_READ_FAILED", result.ReasonCode);
        Assert.DoesNotContain(missingPath, result.ReasonCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// 当前门禁有意验证本机安装；没有游戏程序集时应失败，而不是跳过并形成假绿色。
    /// </summary>
    internal static string FindInstalledGameAssemblyPath()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] candidates =
        {
            Path.Combine(
                home,
                "Library",
                "Application Support",
                "Steam",
                "steamapps",
                "common",
                "Stardew Valley",
                "Contents",
                "MacOS",
                "Stardew Valley.dll"),
            Path.Combine("/Applications", "Stardew Valley.app", "Contents", "MacOS", "Stardew Valley.dll"),
            Path.Combine(home, ".steam", "steam", "steamapps", "common", "Stardew Valley", "Stardew Valley.dll"),
        };

        return Assert.Single(candidates, File.Exists);
    }
}
