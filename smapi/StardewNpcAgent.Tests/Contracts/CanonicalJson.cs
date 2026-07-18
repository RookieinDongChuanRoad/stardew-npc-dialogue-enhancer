using System.Text;
using System.Text.Json;

namespace StardewNpcAgent.Tests.Contracts;

/// <summary>
/// 为 round-trip 测试生成与对象属性顺序无关的 JSON 表示。
/// </summary>
/// <remarks>
/// System.Text.Json 可能按 DTO 属性声明顺序输出字段，而共享 fixture 的字段顺序不属于
/// wire contract。该帮助类只在测试中递归排序对象键；数组顺序、字符串内容、布尔值、
/// null 和数字文本保持不变，从而比较真正有语义的差异。
/// </remarks>
internal static class CanonicalJson
{
    /// <summary>
    /// 将 JSON 文本规范化为可稳定比较的单行字符串。
    /// </summary>
    /// <param name="json">需要规范化的有效 JSON。</param>
    /// <returns>对象键已排序、其余语义保持不变的 JSON。</returns>
    public static string Normalize(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        StringBuilder builder = new();
        AppendElement(builder, document.RootElement);
        return builder.ToString();
    }

    /// <summary>
    /// 按 JSON 值类型递归追加规范化内容。
    /// </summary>
    private static void AppendElement(StringBuilder builder, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                builder.Append('{');
                bool isFirstProperty = true;
                foreach (JsonProperty property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    if (!isFirstProperty)
                    {
                        builder.Append(',');
                    }

                    builder.Append(JsonSerializer.Serialize(property.Name));
                    builder.Append(':');
                    AppendElement(builder, property.Value);
                    isFirstProperty = false;
                }

                builder.Append('}');
                break;

            case JsonValueKind.Array:
                builder.Append('[');
                bool isFirstItem = true;
                foreach (JsonElement item in element.EnumerateArray())
                {
                    if (!isFirstItem)
                    {
                        builder.Append(',');
                    }

                    AppendElement(builder, item);
                    isFirstItem = false;
                }

                builder.Append(']');
                break;

            case JsonValueKind.String:
                builder.Append(JsonSerializer.Serialize(element.GetString()));
                break;

            default:
                // 数字、true、false 与 null 的原始表示已经是稳定且无损的 JSON。
                builder.Append(element.GetRawText());
                break;
        }
    }
}
