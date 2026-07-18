using System.Text.Json;
using System.Text.Json.Serialization;

namespace StardewNpcAgent.Contracts;

/// <summary>
/// 所有公开合同 DTO 的共同基类，用于捕获未声明 JSON 字段。
/// </summary>
/// <remarks>
/// <see cref="ContractJson"/> 会在 raw JSON 边界立即拒绝未知字段。本基类仍通过
/// <see cref="JsonExtensionDataAttribute"/> 捕获绕过 wrapper 的反序列化结果，随后由
/// <see cref="ContractValidator"/> 作为纵深防御返回合同错误。这里不解释字段含义，
/// 也不实现完整 JSON Schema 引擎。
/// </remarks>
public abstract class ContractDto
{
    /// <summary>
    /// 反序列化时遇到的未知字段；正常、已验证 DTO 应为 null 或空集合。
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
