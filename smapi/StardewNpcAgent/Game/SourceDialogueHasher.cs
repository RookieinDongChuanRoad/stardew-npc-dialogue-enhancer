using System.Security.Cryptography;
using System.Text;

namespace StardewNpcAgent.Game;

/// <summary>
/// 为原始对话正文计算逐字符稳定指纹。
/// </summary>
/// <remarks>
/// 指纹只用于判断“生成时看到的原文”是否仍等于“资产应用时的当前原文”。实现不会
/// Trim、统一换行或做 Unicode normalization，因为任何字符变化都应触发原版回退。
/// </remarks>
public static class SourceDialogueHasher
{
    /// <summary>
    /// 以 UTF-8 编码计算 SHA-256，并返回带算法前缀的小写十六进制字符串。
    /// </summary>
    /// <param name="sourceText">需要逐字符绑定的原始对话正文，不可为 null。</param>
    /// <returns><c>sha256:</c> 加 64 位小写十六进制摘要。</returns>
    public static string Compute(string sourceText)
    {
        ArgumentNullException.ThrowIfNull(sourceText);

        byte[] utf8Bytes = Encoding.UTF8.GetBytes(sourceText);
        byte[] digest = SHA256.HashData(utf8Bytes);
        return $"sha256:{Convert.ToHexString(digest).ToLowerInvariant()}";
    }
}
