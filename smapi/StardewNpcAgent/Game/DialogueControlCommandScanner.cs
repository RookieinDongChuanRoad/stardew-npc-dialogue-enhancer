namespace StardewNpcAgent.Game;

/// <summary>
/// 对原始 Stardew 对话文本做保守的控制语法扫描结果。
/// </summary>
/// <param name="HasAnyControlSyntax">是否发现任一可能改变解析语义的 Stardew DSL。</param>
/// <param name="HasQuestionSyntax">是否发现问题、快速响应或回答命令。</param>
/// <param name="HasSideEffectSyntax">是否发现 action、事件、话题或其他副作用命令。</param>
/// <param name="HasMultipleDialogueSyntax">是否发现多屏、多分支或物理换行。</param>
public sealed record DialogueControlScanResult(
    bool HasAnyControlSyntax,
    bool HasQuestionSyntax,
    bool HasSideEffectSyntax,
    bool HasMultipleDialogueSyntax)
{
    /// <summary>
    /// 只有完全不含已知控制语法的单段文本才允许静态追加。
    /// </summary>
    public bool IsSafeForStaticAppend => !HasAnyControlSyntax;
}

/// <summary>
/// 识别可能改变 Stardew 原生对话解析、交互或副作用的文本标记。
/// </summary>
/// <remarks>
/// 这是安全扫描器，不是完整 DSL parser。无法证明无害的 token 会被拒绝，代价只是该条
/// 对话继续走原版。普通中文问号不属于控制语法；只有以特殊前缀编码的游戏命令才命中。
/// </remarks>
public static class DialogueControlCommandScanner
{
    /// <summary>
    /// 会创建回答选项或改变问题流程的命令前缀。
    /// </summary>
    private static readonly string[] QuestionTokens =
    {
        "$q",
        "$y",
        "$r",
    };

    /// <summary>
    /// 可能运行 action、改变状态、进入事件或选择动态分支的命令前缀。
    /// </summary>
    private static readonly string[] SideEffectTokens =
    {
        "$action",
        "$v",
        "$t",
        "$query",
        "$p",
        "$d",
        "$c",
        "%revealtaste",
        "%fork",
    };

    /// <summary>
    /// 扫描原始文本；null 作为未知输入处理，因此同样不允许静态追加。
    /// </summary>
    /// <param name="text">来自当前 locale 对话资产的原始值。</param>
    /// <returns>四类保守语法信号。</returns>
    public static DialogueControlScanResult Scan(string? text)
    {
        if (text is null)
        {
            return new DialogueControlScanResult(
                HasAnyControlSyntax: true,
                HasQuestionSyntax: false,
                HasSideEffectSyntax: false,
                HasMultipleDialogueSyntax: false);
        }

        bool hasQuestion = ContainsAnyExactCommand(text, QuestionTokens);
        bool hasItemGrabSyntax = text.Contains('[') || text.Contains(']');
        bool hasSideEffect = ContainsAny(text, SideEffectTokens) || hasItemGrabSyntax;
        bool hasMultipleDialogue =
            text.Contains("#$b#", StringComparison.Ordinal)
            || text.Contains("||", StringComparison.Ordinal)
            || text.Contains('\n')
            || text.Contains('\r');

        // 除上面可分类的危险命令外，任何其他 $ 命令、名字/token 占位符、性别分支、
        // 断句分隔符和特殊 continuation 标记也不由 Spike 猜测拼接语义。
        bool hasOtherDsl =
            text.Contains('$')
            || text.Contains('#')
            || text.Contains('@')
            || text.Contains('%')
            || text.Contains('^')
            || text.Contains('¦')
            || hasItemGrabSyntax
            || text.Contains('{')
            || text.Contains('}');

        return new DialogueControlScanResult(
            HasAnyControlSyntax: hasQuestion || hasSideEffect || hasMultipleDialogue || hasOtherDsl,
            HasQuestionSyntax: hasQuestion,
            HasSideEffectSyntax: hasSideEffect,
            HasMultipleDialogueSyntax: hasMultipleDialogue);
    }

    /// <summary>
    /// 使用 ordinal 比较检查任一控制 token；游戏 DSL token 本身大小写敏感。
    /// </summary>
    private static bool ContainsAny(string text, IEnumerable<string> tokens)
    {
        return tokens.Any(token => text.Contains(token, StringComparison.Ordinal));
    }

    /// <summary>
    /// 匹配完整短命令名，避免把 <c>$query</c> 错判为 <c>$q</c>。
    /// </summary>
    private static bool ContainsAnyExactCommand(string text, IEnumerable<string> commandTokens)
    {
        foreach (string token in commandTokens)
        {
            int searchIndex = 0;
            while (searchIndex < text.Length)
            {
                int tokenIndex = text.IndexOf(token, searchIndex, StringComparison.Ordinal);
                if (tokenIndex < 0)
                {
                    break;
                }

                int followingIndex = tokenIndex + token.Length;
                if (followingIndex == text.Length || !IsCommandNameCharacter(text[followingIndex]))
                {
                    return true;
                }

                searchIndex = tokenIndex + 1;
            }
        }

        return false;
    }

    /// <summary>
    /// Stardew 命令名使用 ASCII 字母、数字和下划线；其余字符表示 token 边界。
    /// </summary>
    private static bool IsCommandNameCharacter(char character)
    {
        return character is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '_';
    }
}
