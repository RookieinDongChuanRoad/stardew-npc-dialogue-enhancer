using System.Text.Json;
using System.Text.Json.Serialization;

namespace StardewNpcAgent.Contracts;

/// <summary>
/// C# 游戏适配层唯一的 wire JSON 配置与序列化入口。
/// </summary>
/// <remarks>
/// 所有 DTO 属性通过 <see cref="JsonPropertyNameAttribute"/> 显式声明 snake_case 名称；
/// 本类集中冻结大小写、空值和枚举行为。枚举使用严格 converter，确保未知字符串、
/// 错误大小写和整数不会被静默接受。反序列化 DTO 时还会先检查 required 字段是否在
/// 原始 JSON 中真实出现，递归拒绝未知字段，并执行严格 integer token 规则，避免 CLR
/// 默认值、DTO ExtensionData 或标准 JSON Schema 的数学整数宽语义把错误 wire 输入延迟
/// 到业务层。
/// </remarks>
public static class ContractJson
{
    /// <summary>
    /// 全部合同序列化共享的私有配置实例。
    /// </summary>
    /// <remarks>
    /// 不公开 JsonSerializerOptions 是有意的 API 边界：后续 HTTP client 必须调用
    /// Serialize/Deserialize wrapper，不能直接用同一 options 绕过 required 与未知字段检查。
    /// </remarks>
    private static readonly JsonSerializerOptions SerializerOptions = CreateOptions();

    /// <summary>
    /// 将 DTO 或合同枚举序列化为 JSON。
    /// </summary>
    /// <typeparam name="T">待序列化的合同类型。</typeparam>
    /// <param name="value">不可为 null 的合同值。</param>
    /// <returns>符合当前 C# wire 配置的 JSON 文本。</returns>
    public static string Serialize<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return JsonSerializer.Serialize(value, SerializerOptions);
    }

    /// <summary>
    /// 将 JSON 反序列化为指定合同类型。
    /// </summary>
    /// <typeparam name="T">目标 DTO 或枚举类型。</typeparam>
    /// <param name="json">来自共享 fixture 或后端响应的 JSON 文本。</param>
    /// <returns>反序列化后的合同值；业务使用前仍需调用 <see cref="ContractValidator"/>。</returns>
    /// <exception cref="JsonException">
    /// JSON 无效、required 字段缺失、枚举 token 非法或根值为 null 时抛出。
    /// </exception>
    public static T Deserialize<T>(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        // 必须在 DTO 实例化前读取原始 JSON 结构；实例化之后已无法区分“字段缺失”和
        // “调用方显式发送默认值”。显式 null 在这里仍算字段已出现，其合法性留给
        // ContractValidator，避免 presence 检查错误承担领域值校验职责。
        using JsonDocument document = JsonDocument.Parse(
            json,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
        WireJsonShapeValidator.Validate(document.RootElement, typeof(T));

        T? value = JsonSerializer.Deserialize<T>(document.RootElement, SerializerOptions);
        if (value is null)
        {
            throw new JsonException($"合同根值不能为 null：{typeof(T).Name}。");
        }

        return value;
    }

    /// <summary>
    /// 创建一次性共享配置，并显式注册每一种公开 wire enum。
    /// </summary>
    private static JsonSerializerOptions CreateOptions()
    {
        JsonSerializerOptions options = new()
        {
            AllowTrailingCommas = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            PropertyNameCaseInsensitive = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            WriteIndented = false,
        };

        options.Converters.Add(
            new StrictStringEnumConverter<AudienceScope>(
                (AudienceScope.Public, "public"),
                (AudienceScope.Npc, "npc")));
        options.Converters.Add(
            new StrictStringEnumConverter<EventIngestionStatus>(
                (EventIngestionStatus.Accepted, "accepted"),
                (EventIngestionStatus.Duplicate, "duplicate"),
                (EventIngestionStatus.Rejected, "rejected")));
        options.Converters.Add(
            new StrictStringEnumConverter<DialogueGenerationStatus>(
                (DialogueGenerationStatus.Generated, "generated"),
                (DialogueGenerationStatus.Passthrough, "passthrough"),
                (DialogueGenerationStatus.Skipped, "skipped"),
                (DialogueGenerationStatus.Failed, "failed")));
        options.Converters.Add(
            new StrictStringEnumConverter<DisplayAckStatus>(
                (DisplayAckStatus.Accepted, "accepted"),
                (DisplayAckStatus.Duplicate, "duplicate")));

        return options;
    }
}

/// <summary>
/// 只接受显式映射 token 的严格字符串枚举 converter。
/// </summary>
/// <typeparam name="TEnum">需要映射的 C# 枚举类型。</typeparam>
/// <remarks>
/// 标准 JsonStringEnumConverter 在读取时可能接受不规范大小写；本合同要求 token 精确
/// 匹配，因此使用双向字典。构造时还会验证所有枚举成员都有映射，防止新增成员后意外
/// 以整数或 C# 名称泄漏到 wire 上。
/// </remarks>
internal sealed class StrictStringEnumConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    private readonly IReadOnlyDictionary<string, TEnum> valuesByToken;
    private readonly IReadOnlyDictionary<TEnum, string> tokensByValue;

    /// <summary>
    /// 以枚举值与 wire token 对创建严格映射。
    /// </summary>
    /// <param name="mappings">必须覆盖该枚举全部成员，且值和 token 均不得重复。</param>
    public StrictStringEnumConverter(params (TEnum Value, string Token)[] mappings)
    {
        ArgumentNullException.ThrowIfNull(mappings);

        Dictionary<string, TEnum> byToken = new(StringComparer.Ordinal);
        Dictionary<TEnum, string> byValue = new();
        foreach ((TEnum value, string token) in mappings)
        {
            if (string.IsNullOrWhiteSpace(token) || token != token.Trim())
            {
                throw new ArgumentException("枚举 wire token 必须非空且不含首尾空白。", nameof(mappings));
            }

            if (!byToken.TryAdd(token, value) || !byValue.TryAdd(value, token))
            {
                throw new ArgumentException("枚举 wire 映射包含重复值或重复 token。", nameof(mappings));
            }
        }

        TEnum[] declaredValues = Enum.GetValues<TEnum>();
        if (declaredValues.Length != byValue.Count || declaredValues.Any(value => !byValue.ContainsKey(value)))
        {
            throw new ArgumentException("枚举 wire 映射必须覆盖全部已声明成员。", nameof(mappings));
        }

        valuesByToken = byToken;
        tokensByValue = byValue;
    }

    /// <summary>
    /// 读取精确字符串 token；任何非字符串或未映射值均直接失败。
    /// </summary>
    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"{typeof(TEnum).Name} 必须使用字符串 token，禁止整数枚举值。");
        }

        string? token = reader.GetString();
        if (token is null || !valuesByToken.TryGetValue(token, out TEnum value))
        {
            throw new JsonException($"未知或大小写不规范的 {typeof(TEnum).Name} token：{token ?? "null"}。");
        }

        return value;
    }

    /// <summary>
    /// 只输出预先冻结的 wire token。
    /// </summary>
    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        if (!tokensByValue.TryGetValue(value, out string? token))
        {
            throw new JsonException($"未映射的 {typeof(TEnum).Name} 枚举值：{value}。");
        }

        writer.WriteStringValue(token);
    }
}
