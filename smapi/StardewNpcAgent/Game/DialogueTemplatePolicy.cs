namespace StardewNpcAgent.Game;

/// <summary>
/// 游戏侧对话模板允许的封闭称呼槽集合。
/// </summary>
/// <remarks>
/// <see cref="PlayerName"/> 只代表原版 <c>@</c>；它不携带真实玩家名，也不授予任何
/// 关系或记忆事实。其余 Stardew token 不在本枚举中，因此无法被 typed 路径表达。
/// </remarks>
public enum DialogueAddressSlot
{
    None,
    PlayerName,
}

/// <summary>
/// 一条已验证的 Stardew daily 对话模板。
/// </summary>
/// <param name="Prefix">槽前逐字符字面量；允许为空。</param>
/// <param name="AddressSlot">无槽或唯一玩家名槽。</param>
/// <param name="Suffix">槽后逐字符字面量；无槽模板固定为空。</param>
public sealed record DialogueTextTemplate(
    string Prefix,
    DialogueAddressSlot AddressSlot,
    string Suffix);

/// <summary>
/// 仅在 approved daily source/output 边界解析唯一允许的 <c>@</c> 玩家名槽。
/// </summary>
/// <remarks>
/// 通用 <see cref="DialogueControlCommandScanner"/> 仍把 <c>@</c> 当作危险 DSL。本 policy
/// 是一个更窄的显式入口：只允许零或一个 <c>@</c>，拒绝全部其他 Stardew 控制字符、
/// 物理换行与 <c>||</c>。解析和渲染均不 Trim、不做 Unicode normalization。
/// </remarks>
public static class DialogueTemplatePolicy
{
    private static readonly char[] RejectedDslCharacters =
    {
        '$',
        '#',
        '%',
        '^',
        '¦',
        '[',
        ']',
        '{',
        '}',
        '\r',
        '\n',
    };

    /// <summary>
    /// 将 raw 游戏文本无损解析为零或一个 typed 玩家名槽。
    /// </summary>
    /// <param name="rawText">来自 exact source asset 或已校验生成响应的逐字符文本。</param>
    /// <param name="template">成功时返回不可变模板；失败时为 null，且不回显输入。</param>
    /// <returns>文本是否只包含普通字面量和至多一个 <c>@</c>。</returns>
    public static bool TryParse(string? rawText, out DialogueTextTemplate? template)
    {
        template = null;
        if (rawText is null
            || rawText.Contains("||", StringComparison.Ordinal)
            || rawText.IndexOfAny(RejectedDslCharacters) >= 0)
        {
            return false;
        }

        int firstSlotIndex = rawText.IndexOf('@');
        if (firstSlotIndex < 0)
        {
            template = new DialogueTextTemplate(rawText, DialogueAddressSlot.None, string.Empty);
            return true;
        }

        if (rawText.IndexOf('@', firstSlotIndex + 1) >= 0)
        {
            return false;
        }

        template = new DialogueTextTemplate(
            rawText[..firstSlotIndex],
            DialogueAddressSlot.PlayerName,
            rawText[(firstSlotIndex + 1)..]);
        return true;
    }

    /// <summary>
    /// 把可信 typed template 还原成仍含原版 <c>@</c> 的 raw 游戏文本。
    /// </summary>
    /// <param name="template">必须来自 <see cref="TryParse"/> 或等价的可信 typed 构造。</param>
    /// <returns>未展开玩家名的逐字符游戏模板。</returns>
    /// <exception cref="ArgumentException">字段含 raw DSL，或枚举/无槽 shape 非法。</exception>
    public static string RenderGameTemplate(DialogueTextTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        string rawText = template.AddressSlot switch
        {
            DialogueAddressSlot.None when template.Suffix.Length == 0 => template.Prefix,
            DialogueAddressSlot.PlayerName => $"{template.Prefix}@{template.Suffix}",
            _ => throw new ArgumentException("对话模板的称呼槽 shape 不合法。", nameof(template)),
        };

        if (!TryParse(rawText, out DialogueTextTemplate? reparsed) || reparsed != template)
        {
            throw new ArgumentException("对话模板包含未批准的游戏 token。", nameof(template));
        }

        return rawText;
    }

    /// <summary>
    /// 使用一次调用期间提供的过滤后玩家名生成 UI 应显示的 exact 文本。
    /// </summary>
    /// <param name="template">已通过本 policy 的 raw template。</param>
    /// <param name="filteredPlayerName">
    /// 调用方在游戏主线程经 <c>Utility.FilterUserName</c> 得到的短生命周期值；无槽时忽略。
    /// </param>
    /// <param name="renderedText">成功时为 exact UI 文本；失败时为 null。</param>
    /// <returns>无槽，或有槽且过滤后玩家名可用时为 true。</returns>
    public static bool TryRenderForDisplay(
        DialogueTextTemplate template,
        string? filteredPlayerName,
        out string? renderedText)
    {
        ArgumentNullException.ThrowIfNull(template);
        renderedText = null;
        try
        {
            _ = RenderGameTemplate(template);
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (template.AddressSlot == DialogueAddressSlot.None)
        {
            renderedText = template.Prefix;
            return true;
        }

        if (string.IsNullOrEmpty(filteredPlayerName))
        {
            return false;
        }

        renderedText = string.Concat(template.Prefix, filteredPlayerName, template.Suffix);
        return true;
    }
}
