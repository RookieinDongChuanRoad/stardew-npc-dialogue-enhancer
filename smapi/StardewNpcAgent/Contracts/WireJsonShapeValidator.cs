using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StardewNpcAgent.Contracts;

/// <summary>
/// 在 DTO 实例化前验证 raw JSON 的对象形状与严格 integer token。
/// </summary>
/// <remarks>
/// 当前 v1 合同约定：每个 <see cref="ContractDto"/> 上带
/// <see cref="JsonPropertyNameAttribute"/> 的公开属性都是 required，并且 DTO object 不
/// 接受其他属性。该检查器直接读取类型元数据，无需维护第二份逐 DTO 字段清单。它还
/// 执行共享 Schema 的 ``x-stardew-json-integer-token`` 项目扩展：CLR int 属性只能接收
/// 不含小数点或指数的 JSON number token。它不判断 null、数值范围、字符串空白、数组
/// 长度或业务状态；这些仍由 System.Text.Json 与 <see cref="ContractValidator"/> 负责。
/// JsonElement 等显式灵活 JSON object 不会递归进入，仍可携带领域允许的动态字段。
/// </remarks>
internal static class WireJsonShapeValidator
{
    /// <summary>
    /// 缓存反射得到的对象形状。合同类型在进程生命周期内不变，避免每次 HTTP 调用重复扫描。
    /// </summary>
    private static readonly ConcurrentDictionary<Type, ContractObjectShape> ShapesByType = new();

    /// <summary>
    /// 若目标类型是 ContractDto，则从 JSON 根对象开始验证 raw wire shape。
    /// </summary>
    /// <param name="rootElement">调用方提交的原始 JSON 根值。</param>
    /// <param name="targetType">ContractJson.Deserialize 的目标 CLR 类型。</param>
    public static void Validate(JsonElement rootElement, Type targetType)
    {
        ArgumentNullException.ThrowIfNull(targetType);

        // ContractJson 也用于独立枚举等非 DTO 值；这些类型没有 DTO 对象形状元数据，
        // 应保持原来的严格 enum converter 行为，不在此处额外解释。
        if (!typeof(ContractDto).IsAssignableFrom(targetType))
        {
            return;
        }

        ValidateContractObject(rootElement, targetType, "$");
    }

    /// <summary>
    /// 验证一个 ContractDto JSON object，并递归进入嵌套 DTO 和 DTO 集合。
    /// </summary>
    private static void ValidateContractObject(
        JsonElement element,
        Type contractType,
        string path)
    {
        // null 是一种明确出现的 token。presence 已满足，是否允许 null 由 semantic
        // validator 判断，例如 public audience_npc_id 与 passthrough text 允许 null。
        if (element.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        // 非 object 的类型错误由 System.Text.Json 给出标准 JsonException。这里只负责
        // object 的字段集合，避免把类型和值校验重新实现一遍。
        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        ContractObjectShape shape = ShapesByType.GetOrAdd(contractType, BuildObjectShape);

        foreach (JsonProperty jsonProperty in element.EnumerateObject())
        {
            if (!shape.KnownWireNames.Contains(jsonProperty.Name))
            {
                throw new JsonException($"未知字段：{path}.{jsonProperty.Name}。");
            }
        }

        foreach (RequiredWireProperty requiredProperty in shape.RequiredProperties)
        {
            string propertyPath = $"{path}.{requiredProperty.WireName}";
            if (!element.TryGetProperty(requiredProperty.WireName, out JsonElement propertyValue))
            {
                throw new JsonException($"缺少必填字段：{propertyPath}。");
            }

            if (requiredProperty.RequiresIntegerToken)
            {
                ValidateIntegerToken(propertyValue, propertyPath);
            }

            if (propertyValue.ValueKind == JsonValueKind.Null || requiredProperty.NestedContractType is null)
            {
                continue;
            }

            if (requiredProperty.IsCollection)
            {
                ValidateContractCollection(
                    propertyValue,
                    requiredProperty.NestedContractType,
                    propertyPath);
            }
            else
            {
                ValidateContractObject(
                    propertyValue,
                    requiredProperty.NestedContractType,
                    propertyPath);
            }
        }
    }

    /// <summary>
    /// 对 CLR int 属性执行项目级严格 JSON token 规则。
    /// </summary>
    /// <remarks>
    /// Draft 2020-12 的 integer 是数学值概念，因此 13.0 和 1.3e1 都会通过标准 Schema。
    /// 解析后的 <see cref="JsonElement"/> 仍保留原始 token；只需拒绝小数点和指数标记，
    /// 其余 JSON 数字语法与 Int32 范围继续交给 System.Text.Json 处理。
    /// </remarks>
    private static void ValidateIntegerToken(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Number)
        {
            // string、bool、null 等类型错误继续由标准 serializer 生成 JsonException。
            return;
        }

        string rawToken = element.GetRawText();
        if (rawToken.Contains('.') || rawToken.Contains('e') || rawToken.Contains('E'))
        {
            throw new JsonException(
                $"{path} 必须使用不含小数点或指数的 JSON integer token。");
        }
    }

    /// <summary>
    /// 逐项验证 ContractDto 集合；非数组类型错误仍交给 System.Text.Json。
    /// </summary>
    private static void ValidateContractCollection(
        JsonElement element,
        Type elementContractType,
        string path)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        int index = 0;
        foreach (JsonElement item in element.EnumerateArray())
        {
            ValidateContractObject(item, elementContractType, $"{path}[{index}]");
            index++;
        }
    }

    /// <summary>
    /// 从 JsonPropertyName 元数据一次性构建 required 顺序与允许字段集合。
    /// </summary>
    private static ContractObjectShape BuildObjectShape(Type contractType)
    {
        RequiredWireProperty[] requiredProperties = contractType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(CreateRequiredWireProperty)
            .Where(property => property is not null)
            .Cast<RequiredWireProperty>()
            .OrderBy(property => property.MetadataOrder)
            .ToArray();
        HashSet<string> knownWireNames = requiredProperties
            .Select(property => property.WireName)
            .ToHashSet(StringComparer.Ordinal);

        if (knownWireNames.Count != requiredProperties.Length)
        {
            throw new InvalidOperationException($"{contractType.Name} 包含重复 JsonPropertyName。");
        }

        return new ContractObjectShape(requiredProperties, knownWireNames);
    }

    /// <summary>
    /// 将一个显式 wire 属性转换成缓存形状；ExtensionData 等非 wire 属性返回 null。
    /// </summary>
    private static RequiredWireProperty? CreateRequiredWireProperty(PropertyInfo property)
    {
        JsonPropertyNameAttribute? attribute = property.GetCustomAttribute<JsonPropertyNameAttribute>();
        if (attribute is null)
        {
            return null;
        }

        if (typeof(ContractDto).IsAssignableFrom(property.PropertyType))
        {
            return new RequiredWireProperty(
                attribute.Name,
                property.PropertyType,
                IsCollection: false,
                RequiresIntegerToken: false,
                property.MetadataToken);
        }

        Type? collectionElementType = GetEnumerableElementType(property.PropertyType);
        Type? nestedContractType = collectionElementType is not null
            && typeof(ContractDto).IsAssignableFrom(collectionElementType)
                ? collectionElementType
                : null;

        return new RequiredWireProperty(
            attribute.Name,
            nestedContractType,
            IsCollection: nestedContractType is not null,
            RequiresIntegerToken: property.PropertyType == typeof(int),
            property.MetadataToken);
    }

    /// <summary>
    /// 取得数组或 IEnumerable&lt;T&gt; 的元素类型；普通标量返回 null。
    /// </summary>
    private static Type? GetEnumerableElementType(Type type)
    {
        if (type == typeof(string))
        {
            return null;
        }

        if (type.IsArray)
        {
            return type.GetElementType();
        }

        Type? enumerableInterface = type
            .GetInterfaces()
            .Append(type)
            .FirstOrDefault(
                candidate => candidate.IsGenericType
                    && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>));

        return enumerableInterface?.GetGenericArguments()[0];
    }

    /// <summary>
    /// 一个 ContractDto 的缓存对象形状。
    /// </summary>
    private sealed record ContractObjectShape(
        RequiredWireProperty[] RequiredProperties,
        HashSet<string> KnownWireNames);

    /// <summary>
    /// 一项 required wire 属性的缓存描述。
    /// </summary>
    private sealed record RequiredWireProperty(
        string WireName,
        Type? NestedContractType,
        bool IsCollection,
        bool RequiresIntegerToken,
        int MetadataOrder);
}
