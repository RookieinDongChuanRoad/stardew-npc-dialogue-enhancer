using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using Mono.Cecil;
using Mono.Cecil.Cil;
using StardewNpcAgent.Game;
using StardewValley;

namespace StardewNpcAgent.Integration;

/// <summary>
/// 一次只读程序集审计的稳定结果；失败原因不包含本机绝对路径或程序集正文。
/// </summary>
public sealed record GiftGivenCompatibilityResult(
    bool IsCompatible,
    string ReasonCode,
    string? AssemblyVersion,
    string? TargetMethodSignature,
    string? DirectCallerSignature,
    int DirectCallerCount,
    int CallbackInstructionOffset,
    int TasteInstructionOffset,
    string? ReceiveGiftFingerprint,
    string? GiftTasteFingerprint);

/// <summary>
/// gift postfix 安装尝试的稳定结果。
/// </summary>
public sealed record GiftGivenPatchInstallResult(
    bool IsInstalled,
    string ReasonCode,
    GiftGivenCompatibilityResult Compatibility);

/// <summary>
/// 对已安装 Stardew Valley 程序集执行精确、只读的 gift callback 兼容门禁。
/// </summary>
/// <remarks>
/// 门禁使用 Mono.Cecil 读取磁盘程序集，不加载或执行其中类型。版本、方法签名、唯一 direct caller、
/// callback→taste 顺序和两段规范化 IL fingerprint 必须同时匹配经审计的 1.6.15 build；任一漂移都
/// 返回稳定失败结果，调用方不得安装 Harmony patch。
/// </remarks>
public static class GiftGivenCompatibilityAudit
{
    private const string ExpectedAssemblyName = "Stardew Valley";
    private const string ExpectedAssemblyVersion = "1.6.15.24356";
    private const string TargetMethodSignature =
        "System.Void StardewValley.Farmer::onGiftGiven(StardewValley.NPC,StardewValley.Object)";
    private const string DirectCallerSignature =
        "System.Void StardewValley.NPC::receiveGift(StardewValley.Object,StardewValley.Farmer,System.Boolean,System.Single,System.Boolean)";
    private const string TasteMethodSignature =
        "System.Int32 StardewValley.NPC::getGiftTasteForThisItem(StardewValley.Item)";
    private const string ExpectedReceiveGiftFingerprint =
        "50d09cf20d2ef9df3899cfda29af675fa7f285931d22916e855c278d51f981a9";
    private const string ExpectedGiftTasteFingerprint =
        "635edcc394c801200b19fc961f4595502dfd29eff28136649e215a71250eed9d";

    /// <summary>
    /// 审计一个候选游戏程序集；任何 IO、PE 或元数据异常都折叠为无路径稳定原因码。
    /// </summary>
    public static GiftGivenCompatibilityResult Audit(string assemblyPath)
    {
        if (string.IsNullOrWhiteSpace(assemblyPath))
        {
            return Failed("ASSEMBLY_READ_FAILED");
        }

        try
        {
            using AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(
                assemblyPath,
                new ReaderParameters
                {
                    InMemory = true,
                    ReadingMode = ReadingMode.Immediate,
                    ReadSymbols = false,
                });
            string assemblyVersion = assembly.Name.Version.ToString();
            if (!string.Equals(assembly.Name.Name, ExpectedAssemblyName, StringComparison.Ordinal)
                || !string.Equals(
                    assemblyVersion,
                    ExpectedAssemblyVersion,
                    StringComparison.Ordinal))
            {
                return Failed("ASSEMBLY_IDENTITY_MISMATCH", assemblyVersion);
            }

            TypeDefinition? farmerType = assembly.MainModule.GetType("StardewValley.Farmer");
            TypeDefinition? npcType = assembly.MainModule.GetType("StardewValley.NPC");
            if (farmerType is null || npcType is null)
            {
                return Failed("TARGET_TYPE_MISSING", assemblyVersion);
            }

            MethodDefinition? target = FindUniqueMethod(farmerType, TargetMethodSignature);
            if (target is null
                || !target.IsPublic
                || target.IsVirtual
                || target.IsStatic
                || !target.HasBody)
            {
                return Failed("TARGET_SIGNATURE_MISMATCH", assemblyVersion);
            }

            MethodDefinition? receiveGift = FindUniqueMethod(npcType, DirectCallerSignature);
            MethodDefinition? giftTaste = FindUniqueMethod(npcType, TasteMethodSignature);
            if (receiveGift is null || giftTaste is null || !receiveGift.HasBody || !giftTaste.HasBody)
            {
                return Failed("SEMANTIC_METHOD_MISSING", assemblyVersion, target.FullName);
            }

            List<DirectCallSite> directCallers = FindDirectCallers(
                assembly.MainModule,
                TargetMethodSignature);
            if (directCallers.Count != 1
                || !string.Equals(
                    directCallers[0].Caller.FullName,
                    DirectCallerSignature,
                    StringComparison.Ordinal))
            {
                return Failed(
                    "DIRECT_CALLER_MISMATCH",
                    assemblyVersion,
                    target.FullName,
                    directCallerCount: directCallers.Count);
            }

            Instruction[] callbackCalls = receiveGift.Body.Instructions
                .Where(instruction => Calls(instruction, TargetMethodSignature))
                .ToArray();
            Instruction[] tasteCalls = receiveGift.Body.Instructions
                .Where(instruction => Calls(instruction, TasteMethodSignature))
                .ToArray();
            if (callbackCalls.Length != 1
                || tasteCalls.Length != 1
                || callbackCalls[0].Offset >= tasteCalls[0].Offset)
            {
                return Failed(
                    "CALL_ORDER_MISMATCH",
                    assemblyVersion,
                    target.FullName,
                    receiveGift.FullName,
                    directCallers.Count);
            }

            string receiveGiftFingerprint = ComputeMethodFingerprint(receiveGift);
            string giftTasteFingerprint = ComputeMethodFingerprint(giftTaste);
            if (!string.Equals(
                    receiveGiftFingerprint,
                    ExpectedReceiveGiftFingerprint,
                    StringComparison.Ordinal)
                || !string.Equals(
                    giftTasteFingerprint,
                    ExpectedGiftTasteFingerprint,
                    StringComparison.Ordinal))
            {
                return Failed(
                    "METHOD_FINGERPRINT_MISMATCH",
                    assemblyVersion,
                    target.FullName,
                    receiveGift.FullName,
                    directCallers.Count,
                    callbackCalls[0].Offset,
                    tasteCalls[0].Offset,
                    receiveGiftFingerprint,
                    giftTasteFingerprint);
            }

            return new GiftGivenCompatibilityResult(
                IsCompatible: true,
                ReasonCode: "COMPATIBLE",
                AssemblyVersion: assemblyVersion,
                TargetMethodSignature: target.FullName,
                DirectCallerSignature: receiveGift.FullName,
                DirectCallerCount: directCallers.Count,
                CallbackInstructionOffset: callbackCalls[0].Offset,
                TasteInstructionOffset: tasteCalls[0].Offset,
                ReceiveGiftFingerprint: receiveGiftFingerprint,
                GiftTasteFingerprint: giftTasteFingerprint);
        }
        catch (Exception)
        {
            // Mono.Cecil 对截断 PE、未知 metadata table 与 resolver 失败可能抛出不同异常类型。
            // 审计入口没有任何可恢复写操作，因此全部折叠为同一安全失败，不泄露路径或异常正文。
            return Failed("ASSEMBLY_READ_FAILED");
        }
    }

    /// <summary>
    /// 在一个 type 内按 Cecil FullName 查找唯一方法；重载重复或缺失都 fail closed。
    /// </summary>
    private static MethodDefinition? FindUniqueMethod(TypeDefinition type, string fullName)
    {
        MethodDefinition[] matches = type.Methods
            .Where(method => string.Equals(method.FullName, fullName, StringComparison.Ordinal))
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    /// <summary>
    /// 扫描模块内全部嵌套 type，统计直接 call/callvirt target 的位置。
    /// </summary>
    private static List<DirectCallSite> FindDirectCallers(ModuleDefinition module, string targetSignature)
    {
        List<DirectCallSite> callers = new();
        foreach (TypeDefinition type in EnumerateTypes(module.Types))
        {
            foreach (MethodDefinition method in type.Methods.Where(method => method.HasBody))
            {
                foreach (Instruction instruction in method.Body.Instructions)
                {
                    if (Calls(instruction, targetSignature))
                    {
                        callers.Add(new DirectCallSite(method, instruction.Offset));
                    }
                }
            }
        }

        return callers;
    }

    private static bool Calls(Instruction instruction, string targetSignature)
    {
        return (instruction.OpCode.Code == Mono.Cecil.Cil.Code.Call
                || instruction.OpCode.Code == Mono.Cecil.Cil.Code.Callvirt)
            && instruction.Operand is MethodReference method
            && string.Equals(method.FullName, targetSignature, StringComparison.Ordinal);
    }

    /// <summary>
    /// 对 offset、opcode 和规范化 operand 逐行编码，得到可复现的方法体 SHA-256。
    /// </summary>
    internal static string ComputeMethodFingerprint(MethodDefinition method)
    {
        ArgumentNullException.ThrowIfNull(method);
        if (!method.HasBody)
        {
            throw new ArgumentException("Method 必须包含 IL body。", nameof(method));
        }

        StringBuilder canonical = new();
        foreach (Instruction instruction in method.Body.Instructions)
        {
            canonical.Append(instruction.Offset.ToString("X4", CultureInfo.InvariantCulture));
            canonical.Append('|');
            canonical.Append(instruction.OpCode.Code);
            canonical.Append('|');
            canonical.Append(FormatOperand(instruction.Operand));
            canonical.Append('\n');
        }

        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }

    private static string FormatOperand(object? operand)
    {
        return operand switch
        {
            null => string.Empty,
            MethodReference method => method.FullName,
            FieldReference field => field.FullName,
            TypeReference type => type.FullName,
            ParameterDefinition parameter => $"parameter:{parameter.Index}",
            VariableDefinition variable => $"variable:{variable.Index}",
            Instruction target => $"target:{target.Offset:X4}",
            Instruction[] targets => "targets:"
                + string.Join(",", targets.Select(item => $"{item.Offset:X4}")),
            string text => "string:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(text)),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => operand.ToString() ?? string.Empty,
        };
    }

    private static IEnumerable<TypeDefinition> EnumerateTypes(IEnumerable<TypeDefinition> roots)
    {
        foreach (TypeDefinition type in roots)
        {
            yield return type;
            foreach (TypeDefinition nested in EnumerateTypes(type.NestedTypes))
            {
                yield return nested;
            }
        }
    }

    private static GiftGivenCompatibilityResult Failed(
        string reasonCode,
        string? assemblyVersion = null,
        string? targetMethodSignature = null,
        string? directCallerSignature = null,
        int directCallerCount = 0,
        int callbackInstructionOffset = -1,
        int tasteInstructionOffset = -1,
        string? receiveGiftFingerprint = null,
        string? giftTasteFingerprint = null)
    {
        return new GiftGivenCompatibilityResult(
            IsCompatible: false,
            ReasonCode: reasonCode,
            AssemblyVersion: assemblyVersion,
            TargetMethodSignature: targetMethodSignature,
            DirectCallerSignature: directCallerSignature,
            DirectCallerCount: directCallerCount,
            CallbackInstructionOffset: callbackInstructionOffset,
            TasteInstructionOffset: tasteInstructionOffset,
            ReceiveGiftFingerprint: receiveGiftFingerprint,
            GiftTasteFingerprint: giftTasteFingerprint);
    }

    private sealed record DirectCallSite(MethodDefinition Caller, int InstructionOffset);
}

/// <summary>
/// 唯一获准的 Harmony patch：观察 public Farmer.onGiftGiven(NPC,Object) 完成后的参数。
/// </summary>
/// <remarks>
/// postfix 没有返回值，不接收 __result，不修改参数。它只读取本地玩家、NPC ID、QualifiedItemId、
/// public GiftsToday 与 gift taste，并把不可变 fact 交给运行时 sink；任何异常都在 patch 内吞并转成
/// 稳定诊断回调，绝不能改变原版送礼结果。
/// </remarks>
[HarmonyPatch(
    typeof(Farmer),
    nameof(Farmer.onGiftGiven),
    new Type[] { typeof(NPC), typeof(StardewValley.Object) })]
public static class GiftGivenHarmonyPatch
{
    private const string HarmonyId = "liurongfu.stardew-npc-agent.gift-observer";
    private static readonly object InstallationGate = new();
    private static Action<GiftGivenFact>? observationSink;
    private static Action<string>? failureSink;
    private static bool installed;

    /// <summary>
    /// 只有完整兼容审计通过后才安装 class processor；失败时不会保留半安装 patch。
    /// </summary>
    public static GiftGivenPatchInstallResult TryInstall(
        string gameAssemblyPath,
        Action<GiftGivenFact> onObserved,
        Action<string>? onFailure = null)
    {
        ArgumentNullException.ThrowIfNull(onObserved);
        GiftGivenCompatibilityResult compatibility = GiftGivenCompatibilityAudit.Audit(
            gameAssemblyPath);
        if (!compatibility.IsCompatible)
        {
            return new GiftGivenPatchInstallResult(
                IsInstalled: false,
                compatibility.ReasonCode,
                compatibility);
        }

        lock (InstallationGate)
        {
            if (installed)
            {
                return new GiftGivenPatchInstallResult(
                    IsInstalled: true,
                    ReasonCode: "ALREADY_INSTALLED",
                    compatibility);
            }

            Harmony harmony = new(HarmonyId);
            try
            {
                harmony.CreateClassProcessor(typeof(GiftGivenHarmonyPatch)).Patch();
                observationSink = onObserved;
                failureSink = onFailure;
                installed = true;
                return new GiftGivenPatchInstallResult(
                    IsInstalled: true,
                    ReasonCode: "INSTALLED",
                    compatibility);
            }
            catch
            {
                // CreateClassProcessor 只有一个 postfix，但仍主动清理同 ID，避免未来 Harmony 行为变化
                // 时留下无法确认的半安装状态。清理失败也不能向原版调用路径抛出。
                try
                {
                    harmony.UnpatchAll(HarmonyId);
                }
                catch
                {
                    // 最外层结果仍保持 PATCH_INSTALL_FAILED；不得让诊断清理异常覆盖原始失败类别。
                }

                observationSink = null;
                failureSink = null;
                installed = false;
                return new GiftGivenPatchInstallResult(
                    IsInstalled: false,
                    ReasonCode: "PATCH_INSTALL_FAILED",
                    compatibility);
            }
        }
    }

    /// <summary>
    /// Harmony 只观察已经完成的 onGiftGiven；所有读取和 sink 调用都在保护边界内。
    /// </summary>
    [HarmonyPostfix]
    private static void Postfix(Farmer __instance, NPC __0, StardewValley.Object __1)
    {
        try
        {
            Action<GiftGivenFact>? sink = observationSink;
            if (sink is null
                || !__instance.IsLocalPlayer
                || !__instance.friendshipData.TryGetValue(__0.Name, out Friendship? friendship)
                || friendship.GiftsToday == int.MaxValue)
            {
                return;
            }

            string? taste = MapTaste(__0.getGiftTasteForThisItem(__1));
            if (taste is null)
            {
                ReportFailure("GIFT_TASTE_UNSUPPORTED");
                return;
            }

            sink(
                new GiftGivenFact(
                    IsLocalPlayer: true,
                    OccurredDayIndex: Game1.Date.TotalDays,
                    NpcId: __0.Name,
                    QualifiedItemId: __1.QualifiedItemId,
                    Taste: taste,
                    DailyGiftOrdinal: friendship.GiftsToday + 1));
        }
        catch
        {
            ReportFailure("GIFT_POSTFIX_OBSERVATION_FAILED");
        }
    }

    /// <summary>
    /// 将原版 public gift_taste_* 常量逐值映射为冻结 wire enum。
    /// </summary>
    private static string? MapTaste(int taste)
    {
        return taste switch
        {
            NPC.gift_taste_love => "love",
            NPC.gift_taste_like => "like",
            NPC.gift_taste_neutral => "neutral",
            NPC.gift_taste_dislike => "dislike",
            NPC.gift_taste_hate => "hate",
            NPC.gift_taste_stardroptea => "stardrop_tea",
            _ => null,
        };
    }

    /// <summary>
    /// 诊断 sink 同样不能影响原版路径；它只接收稳定代码，不接收 exception 或本机路径。
    /// </summary>
    private static void ReportFailure(string reasonCode)
    {
        try
        {
            failureSink?.Invoke(reasonCode);
        }
        catch
        {
            // 观察诊断本身失败时保持静默，原版行为优先。
        }
    }
}
