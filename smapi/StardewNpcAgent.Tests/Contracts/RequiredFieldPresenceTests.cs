using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using StardewNpcAgent.Contracts;

namespace StardewNpcAgent.Tests.Contracts;

/// <summary>
/// 验证 JSON wire 上“字段真实出现”与“字段值是否合法”是两个独立边界。
/// </summary>
/// <remarks>
/// System.Text.Json 在缺字段时会保留 DTO 默认值：整数变成 0、枚举变成第一个成员、
/// 列表保持空集合、nullable 字段保持 null。这会让 ContractValidator 无法区分“调用方
/// 明确发送了合法的 0/null/空数组”和“调用方根本没有发送 required 字段”。因此本组
/// 测试要求 ContractJson.Deserialize 先验证 presence；字段显式为 null 时仍算已出现，
/// 再交给 ContractValidator 判断该字段在当前语义下是否允许 null。
/// </remarks>
public sealed class RequiredFieldPresenceTests
{
    private const string DisplayAckRequestJson =
        "{"
        + "\"schema_version\":\"1.0\","
        + "\"request_id\":\"request-display-ack-001\","
        + "\"save_id\":\"save-standard-farm-001\","
        + "\"player_id\":\"player-farmer-001\","
        + "\"display_receipt_id\":\"receipt-001\","
        + "\"displayed_day_index\":14,"
        + "\"npc_id\":\"Abigail\","
        + "\"source_hash\":\"sha256:abigail-spring-mon-zh-cn\""
        + "}";

    private const string GameEventBatchResponseJson =
        "{"
        + "\"schema_version\":\"1.0\","
        + "\"request_id\":\"request-events-spring-15\","
        + "\"memory_revision\":42,"
        + "\"committed_through_day_index\":13,"
        + "\"items\":[{"
        + "\"event_id\":\"event-gift-abigail-spring-14\","
        + "\"status\":\"accepted\","
        + "\"reason_code\":null"
        + "}]"
        + "}";

    private const string DisplayAckResponseJson =
        "{"
        + "\"schema_version\":\"1.0\","
        + "\"request_id\":\"request-display-ack-001\","
        + "\"display_receipt_id\":\"receipt-001\","
        + "\"status\":\"accepted\""
        + "}";

    /// <summary>
    /// displayed_day_index=0 是合法值，所以缺字段不能靠 DTO 默认值 0 蒙混过关。
    /// </summary>
    [Fact]
    public void DisplayAckRequest_RejectsMissingDisplayedDayIndexAtWireBoundary()
    {
        string json = RemoveRequiredProperty(DisplayAckRequestJson, "displayed_day_index");

        JsonException exception = Assert.Throws<JsonException>(
            () => ContractJson.Deserialize<DisplayAckRequest>(json));

        Assert.Contains("$.displayed_day_index", exception.Message);
    }

    /// <summary>
    /// audience_scope 缢失时枚举默认值 Public 看似合法，但 wire contract 仍应拒绝。
    /// </summary>
    [Fact]
    public void GameEvent_RejectsMissingAudienceScopeInsteadOfUsingEnumDefault()
    {
        string json = RemoveRequiredProperty(
            FixtureFile.ReadAllText("event_batch.json"),
            "events",
            "1",
            "audience_scope");

        JsonException exception = Assert.Throws<JsonException>(
            () => ContractJson.Deserialize<GameEventBatchRequest>(json));

        Assert.Contains("$.events[1].audience_scope", exception.Message);
    }

    /// <summary>
    /// public 事件要求 audience_npc_id 字段真实出现且值为 null；完全缺字段不等价。
    /// </summary>
    [Fact]
    public void PublicGameEvent_RejectsMissingRequiredButNullableAudienceNpcId()
    {
        string json = RemoveRequiredProperty(
            FixtureFile.ReadAllText("event_batch.json"),
            "events",
            "1",
            "audience_npc_id");

        JsonException exception = Assert.Throws<JsonException>(
            () => ContractJson.Deserialize<GameEventBatchRequest>(json));

        Assert.Contains("$.events[1].audience_npc_id", exception.Message);
    }

    /// <summary>
    /// 每日生成请求中的默认整数、空列表和嵌套默认对象都不能替代 required presence。
    /// </summary>
    /// <param name="expectedPath">错误消息应定位到的 wire 路径。</param>
    /// <param name="pathSegments">从根对象到待删除字段的对象键或数组下标。</param>
    [Theory]
    [MemberData(nameof(DialogueRequestMissingFields))]
    public void DialogueRequest_RejectsMissingRequiredFieldsRecursively(
        string expectedPath,
        string[] pathSegments)
    {
        string json = RemoveRequiredProperty(
            FixtureFile.ReadAllText("dialogue_batch.json"),
            pathSegments);

        JsonException exception = Assert.Throws<JsonException>(
            () => ContractJson.Deserialize<DialogueGenerationBatchRequest>(json));

        Assert.Contains(expectedPath, exception.Message);
    }

    /// <summary>
    /// 生成响应必须区分缺 status/text/evidence_ids 与显式默认值或 null。
    /// </summary>
    /// <param name="expectedPath">错误消息应定位到的 wire 路径。</param>
    /// <param name="pathSegments">从根对象到待删除字段的对象键或数组下标。</param>
    [Theory]
    [MemberData(nameof(DialogueResponseMissingFields))]
    public void DialogueResponse_RejectsMissingRequiredFieldsRecursively(
        string expectedPath,
        string[] pathSegments)
    {
        string json = RemoveRequiredProperty(
            FixtureFile.ReadAllText("dialogue_batch_response.json"),
            pathSegments);

        JsonException exception = Assert.Throws<JsonException>(
            () => ContractJson.Deserialize<DialogueGenerationBatchResponse>(json));

        Assert.Contains(expectedPath, exception.Message);
    }

    /// <summary>
    /// 无共享 fixture 的响应 DTO 也必须遵守同一 presence 机制，不能成为旁路。
    /// </summary>
    [Fact]
    public void GameEventResponse_RejectsMissingCommittedDayIndex()
    {
        string json = RemoveRequiredProperty(
            GameEventBatchResponseJson,
            "committed_through_day_index");

        JsonException exception = Assert.Throws<JsonException>(
            () => ContractJson.Deserialize<GameEventBatchResponse>(json));

        Assert.Contains("$.committed_through_day_index", exception.Message);
    }

    /// <summary>
    /// DisplayAckResponse 的 status 不能由枚举默认值 Accepted 隐式补齐。
    /// </summary>
    [Fact]
    public void DisplayAckResponse_RejectsMissingStatusInsteadOfUsingEnumDefault()
    {
        string json = RemoveRequiredProperty(DisplayAckResponseJson, "status");

        JsonException exception = Assert.Throws<JsonException>(
            () => ContractJson.Deserialize<DisplayAckResponse>(json));

        Assert.Contains("$.status", exception.Message);
    }

    /// <summary>
    /// 显式 null 仍代表 required 字段已出现；现有 semantic validator 决定它是否合法。
    /// </summary>
    [Fact]
    public void ExplicitNullRequiredFields_RemainAvailableForSemanticValidation()
    {
        GameEventBatchRequest eventRequest = ContractJson.Deserialize<GameEventBatchRequest>(
            FixtureFile.ReadAllText("event_batch.json"));
        DialogueGenerationBatchResponse dialogueResponse =
            ContractJson.Deserialize<DialogueGenerationBatchResponse>(
                FixtureFile.ReadAllText("dialogue_batch_response.json"));

        ContractValidationResult eventValidation = ContractValidator.Validate(eventRequest);
        ContractValidationResult responseValidation = ContractValidator.Validate(dialogueResponse);

        Assert.True(eventValidation.IsValid, eventValidation.ToString());
        Assert.Null(eventRequest.Events[1].AudienceNpcId);
        Assert.True(responseValidation.IsValid, responseValidation.ToString());
        Assert.Null(dialogueResponse.Items[1].Text);
    }

    /// <summary>
    /// ContractJson 是唯一 raw JSON wrapper；根对象未知字段必须在同一 shape 边界立即拒绝。
    /// </summary>
    [Fact]
    public void ContractJson_RejectsUnknownRootFieldImmediately()
    {
        JsonObject root = Assert.IsType<JsonObject>(
            JsonNode.Parse(FixtureFile.ReadAllText("event_batch.json")));
        root["future_wire_field"] = "unsupported";

        JsonException exception = Assert.Throws<JsonException>(
            () => ContractJson.Deserialize<GameEventBatchRequest>(root.ToJsonString()));

        Assert.Contains("$.future_wire_field", exception.Message);
    }

    /// <summary>
    /// 对象 shape 检查必须递归进入 ContractDto 列表，不能只保护请求 envelope。
    /// </summary>
    [Fact]
    public void ContractJson_RejectsUnknownNestedFieldImmediately()
    {
        JsonObject root = Assert.IsType<JsonObject>(
            JsonNode.Parse(FixtureFile.ReadAllText("event_batch.json")));
        JsonArray events = Assert.IsType<JsonArray>(root["events"]);
        JsonObject firstEvent = Assert.IsType<JsonObject>(events[0]);
        firstEvent["future_nested_field"] = true;

        JsonException exception = Assert.Throws<JsonException>(
            () => ContractJson.Deserialize<GameEventBatchRequest>(root.ToJsonString()));

        Assert.Contains("$.events[0].future_nested_field", exception.Message);
    }

    /// <summary>
    /// JsonSerializerOptions 不能成为公开旁路；未来 HTTP client 必须使用 wrapper 才能
    /// 同时获得 required、unknown-field 与严格枚举检查。
    /// </summary>
    [Fact]
    public void ContractJson_DoesNotExposePublicSerializerOptions()
    {
        const BindingFlags publicStatic = BindingFlags.Public | BindingFlags.Static;
        Assert.DoesNotContain(
            typeof(ContractJson).GetProperties(publicStatic),
            property => property.PropertyType == typeof(JsonSerializerOptions));
        Assert.DoesNotContain(
            typeof(ContractJson).GetFields(publicStatic),
            field => field.FieldType == typeof(JsonSerializerOptions));
        Assert.DoesNotContain(
            typeof(ContractJson).GetMethods(publicStatic),
            method => method.ReturnType == typeof(JsonSerializerOptions)
                || method.GetParameters().Any(parameter => parameter.ParameterType == typeof(JsonSerializerOptions)));
    }

    /// <summary>
    /// 对当前及未来所有 ContractDto 做元数据回归：每个 JsonPropertyName 属性都必须
    /// 自动进入 required presence 检查，不允许维护另一份容易漂移的字段清单。
    /// </summary>
    [Fact]
    public void EveryAttributedWireProperty_IsAutomaticallyRequired()
    {
        Type[] contractTypes = GetConcreteContractTypes();

        Assert.NotEmpty(contractTypes);
        foreach (Type contractType in contractTypes)
        {
            PropertyInfo[] requiredProperties = GetRequiredWireProperties(contractType);
            Assert.NotEmpty(requiredProperties);

            foreach (PropertyInfo requiredProperty in requiredProperties)
            {
                JsonPropertyNameAttribute attribute = requiredProperty.GetCustomAttribute<JsonPropertyNameAttribute>()!;
                JsonObject incompleteObject = BuildCompleteContractObject(contractType);
                Assert.True(incompleteObject.Remove(attribute.Name));

                AssertDynamicDeserializationRejectsMissingProperty(
                    contractType,
                    incompleteObject.ToJsonString(),
                    attribute.Name);
            }
        }
    }

    /// <summary>
    /// 暴露在 DTO 上的业务属性必须显式声明 wire 名，避免新增属性绕过元数据检查。
    /// </summary>
    [Fact]
    public void EveryPublicContractProperty_DeclaresAnExplicitWireName()
    {
        Type[] contractTypes = GetConcreteContractTypes();

        foreach (Type contractType in contractTypes)
        {
            PropertyInfo[] undeclaredProperties = contractType
                .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(property => property.GetCustomAttribute<JsonPropertyNameAttribute>() is null)
                .ToArray();

            Assert.True(
                undeclaredProperties.Length == 0,
                $"{contractType.Name} 存在未声明 JsonPropertyName 的公开属性："
                + string.Join(", ", undeclaredProperties.Select(property => property.Name)));
        }
    }

    /// <summary>
    /// 每日生成请求的系统性缺字段样例，覆盖根对象、列表元素和更深层嵌套对象。
    /// </summary>
    public static IEnumerable<object[]> DialogueRequestMissingFields()
    {
        yield return new object[] { "$.game_day_index", new[] { "game_day_index" } };
        yield return new object[] { "$.required_memory_revision", new[] { "required_memory_revision" } };
        yield return new object[] { "$.items[0].memory_signals", new[] { "items", "0", "memory_signals" } };
        yield return new object[]
        {
            "$.items[0].relationship_snapshot.friendship_points",
            new[] { "items", "0", "relationship_snapshot", "friendship_points" },
        };
    }

    /// <summary>
    /// 每日生成响应的系统性缺字段样例，包含 required-but-null 的 passthrough text。
    /// </summary>
    public static IEnumerable<object[]> DialogueResponseMissingFields()
    {
        yield return new object[] { "$.items[0].status", new[] { "items", "0", "status" } };
        yield return new object[] { "$.items[1].text", new[] { "items", "1", "text" } };
        yield return new object[] { "$.items[0].evidence_ids", new[] { "items", "0", "evidence_ids" } };
    }

    /// <summary>
    /// 删除指定 wire 路径的最后一个对象属性，并保留其他 JSON 内容不变。
    /// </summary>
    /// <param name="json">作为合法基线的 JSON。</param>
    /// <param name="pathSegments">对象键或数组下标组成的路径。</param>
    /// <returns>恰好缺少一个 required 字段的 JSON。</returns>
    private static string RemoveRequiredProperty(string json, params string[] pathSegments)
    {
        Assert.NotEmpty(pathSegments);
        JsonNode root = JsonNode.Parse(json) ?? throw new InvalidOperationException("测试基线 JSON 不能为 null。");
        JsonNode current = root;

        for (int index = 0; index < pathSegments.Length - 1; index++)
        {
            string segment = pathSegments[index];
            current = current switch
            {
                JsonObject jsonObject => jsonObject[segment]
                    ?? throw new InvalidOperationException($"测试路径不存在对象属性：{segment}。"),
                JsonArray jsonArray => jsonArray[int.Parse(segment, System.Globalization.CultureInfo.InvariantCulture)]
                    ?? throw new InvalidOperationException($"测试路径不存在数组元素：{segment}。"),
                _ => throw new InvalidOperationException($"测试路径经过非容器 JSON 值：{segment}。"),
            };
        }

        string propertyName = pathSegments[^1];
        JsonObject parent = Assert.IsType<JsonObject>(current);
        Assert.True(parent.Remove(propertyName), $"待删除的 required 字段不存在：{propertyName}。");
        return root.ToJsonString();
    }

    /// <summary>
    /// 为元数据测试构造“所有 required 属性都真实出现”的最小 JSON object。
    /// </summary>
    private static JsonObject BuildCompleteContractObject(Type contractType)
    {
        JsonObject jsonObject = new();
        foreach (PropertyInfo property in GetRequiredWireProperties(contractType))
        {
            JsonPropertyNameAttribute attribute = property.GetCustomAttribute<JsonPropertyNameAttribute>()!;
            jsonObject[attribute.Name] = BuildPlaceholderValue(property.PropertyType);
        }

        return jsonObject;
    }

    /// <summary>
    /// 按 CLR 类型生成可被 System.Text.Json 解析的占位值；这里只测试字段出现，不测试值。
    /// </summary>
    private static JsonNode? BuildPlaceholderValue(Type propertyType)
    {
        if (propertyType == typeof(string))
        {
            return JsonValue.Create("placeholder");
        }

        if (propertyType == typeof(int))
        {
            return JsonValue.Create(0);
        }

        if (propertyType == typeof(JsonElement))
        {
            return new JsonObject();
        }

        if (propertyType.IsEnum)
        {
            object enumValue = Enum.GetValues(propertyType).GetValue(0)
                ?? throw new InvalidOperationException($"枚举没有成员：{propertyType.Name}。");
            string enumJson = SerializeContractValueDynamically(propertyType, enumValue);
            return JsonNode.Parse(enumJson);
        }

        if (typeof(ContractDto).IsAssignableFrom(propertyType))
        {
            return BuildCompleteContractObject(propertyType);
        }

        if (TryGetEnumerableElementType(propertyType, out _))
        {
            return new JsonArray();
        }

        throw new InvalidOperationException($"元数据测试尚未支持属性类型：{propertyType.FullName}。");
    }

    /// <summary>
    /// 动态调用泛型 Deserialize，使新增 DTO 自动进入同一元数据测试。
    /// </summary>
    private static void AssertDynamicDeserializationRejectsMissingProperty(
        Type contractType,
        string json,
        string propertyName)
    {
        MethodInfo deserializeMethod = typeof(ContractJson)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method => method.Name == nameof(ContractJson.Deserialize) && method.IsGenericMethodDefinition)
            .MakeGenericMethod(contractType);

        TargetInvocationException invocationException = Assert.Throws<TargetInvocationException>(
            () => deserializeMethod.Invoke(null, new object[] { json }));
        JsonException jsonException = Assert.IsType<JsonException>(invocationException.InnerException);
        Assert.Contains(propertyName, jsonException.Message);
    }

    /// <summary>
    /// 通过 ContractJson 的公开泛型 wrapper 动态序列化枚举，不暴露底层 options 旁路。
    /// </summary>
    private static string SerializeContractValueDynamically(Type contractType, object value)
    {
        MethodInfo serializeMethod = typeof(ContractJson)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method => method.Name == nameof(ContractJson.Serialize) && method.IsGenericMethodDefinition)
            .MakeGenericMethod(contractType);

        object? result = serializeMethod.Invoke(null, new[] { value });
        return Assert.IsType<string>(result);
    }

    /// <summary>
    /// 取得当前合同约定为 required 的全部公开 wire 属性。
    /// </summary>
    private static PropertyInfo[] GetRequiredWireProperties(Type contractType)
    {
        return contractType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetCustomAttribute<JsonPropertyNameAttribute>() is not null)
            .OrderBy(property => property.MetadataToken)
            .ToArray();
    }

    /// <summary>
    /// 取得产品程序集里所有具体 ContractDto，同时隔离不属于合同测试的 SMAPI 入口类型。
    /// </summary>
    /// <remarks>
    /// ModBuildConfig 正确地把游戏/SMAPI 引用标为 Private=false，因此纯合同测试输出目录
    /// 不复制 StardewModdingAPI.dll。Assembly.GetTypes 会在尝试加载 ModEntry 基类时抛
    /// ReflectionTypeLoadException，但异常的 Types 仍包含不依赖 SMAPI 的全部 Contracts
    /// 类型。恢复前会逐个断言 LoaderException 只来自故意未复制的游戏运行时依赖；任何
    /// 其他程序集加载失败都会让测试失败，不能静默漏掉合同类型。
    /// </remarks>
    private static Type[] GetConcreteContractTypes()
    {
        Assembly productAssembly = typeof(ContractDto).Assembly;
        Type[] loadableTypes;

        try
        {
            loadableTypes = productAssembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            Exception[] loaderExceptions = exception.LoaderExceptions.OfType<Exception>().ToArray();
            Assert.NotEmpty(loaderExceptions);
            Assert.All(loaderExceptions, AssertExpectedMissingGameRuntimeDependency);
            loadableTypes = exception.Types.OfType<Type>().ToArray();
        }

        return loadableTypes
            .Where(type => !type.IsAbstract && typeof(ContractDto).IsAssignableFrom(type))
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// 只允许测试输出刻意不复制的 SMAPI/游戏程序集缺失，拒绝吞掉其他加载异常。
    /// </summary>
    private static void AssertExpectedMissingGameRuntimeDependency(Exception loaderException)
    {
        FileNotFoundException missingAssembly = Assert.IsType<FileNotFoundException>(loaderException);
        string assemblyDisplayName = Assert.IsType<string>(missingAssembly.FileName);
        string assemblyName = assemblyDisplayName.Split(',')[0];
        string[] expectedRuntimeAssemblies =
        {
            "StardewModdingAPI",
            "Stardew Valley",
            "StardewValley.GameData",
            "MonoGame.Framework",
            "xTile",
            "SMAPI.Toolkit.CoreInterfaces",
            "0Harmony",
        };

        Assert.Contains(assemblyName, expectedRuntimeAssemblies);
    }

    /// <summary>
    /// 识别数组或实现 IEnumerable&lt;T&gt; 的集合元素类型。
    /// </summary>
    private static bool TryGetEnumerableElementType(Type type, out Type? elementType)
    {
        if (type.IsArray)
        {
            elementType = type.GetElementType();
            return elementType is not null;
        }

        Type? enumerableInterface = type
            .GetInterfaces()
            .Append(type)
            .FirstOrDefault(
                candidate => candidate.IsGenericType
                    && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>));

        elementType = enumerableInterface?.GetGenericArguments()[0];
        return elementType is not null;
    }
}
