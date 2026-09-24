using System.Text;
using System.Text.Json;

namespace KeelMatrix.AiToolContract;

/// <summary>Reads, writes, and canonicalizes the KeelMatrix baseline format.</summary>
public static class AiToolContractJson
{
    /// <summary>Serializes a version-one baseline deterministically.</summary>
    public static string Serialize(AiToolContractBaseline baseline, AiToolContractLimits? limits = null)
    {
        if (baseline is null)
            throw new ArgumentNullException(nameof(baseline));
        var effectiveLimits = limits ?? AiToolContractLimits.Default;
        effectiveLimits.Validate();
        if (baseline.SchemaVersion != 1)
            throw new AiToolContractException(new AiToolContractDiagnostic(AiToolDiagnosticKind.UnsupportedBaselineVersion, "Baseline schema version " + baseline.SchemaVersion + " is not supported."));
        if (baseline.Tools.Count > effectiveLimits.MaxTools)
            throw new AiToolContractException(new AiToolContractDiagnostic(AiToolDiagnosticKind.CanonicalizationOrResourceLimit, "The baseline exceeds the configured tool-count limit."));

        var tools = baseline.Tools.OrderBy(static tool => tool.Name, StringComparer.Ordinal).ToList();
        var builder = new System.Text.StringBuilder();
        builder.Append("{\"schemaVersion\":1,\"tools\":[");
        for (var index = 0; index < tools.Count; index++)
        {
            if (index > 0)
                builder.Append(',');
            var tool = tools[index];
            builder.Append("{\"name\":");
            builder.Append(JsonSerializer.Serialize(tool.Name));
            builder.Append(",\"description\":");
            builder.Append(tool.Description is null ? "null" : JsonSerializer.Serialize(tool.Description));
            builder.Append(",\"inputSchema\":");
            builder.Append(CanonicalJson.Canonicalize(tool.InputSchemaJson, effectiveLimits));
            builder.Append(",\"returnSchema\":");
            builder.Append(tool.ReturnSchemaJson is null ? "null" : CanonicalJson.Canonicalize(tool.ReturnSchemaJson, effectiveLimits));
            builder.Append(",\"requiresApproval\":");
            builder.Append(tool.RequiresApproval ? "true" : "false");
            builder.Append('}');
        }
        builder.Append("]}");
        return builder.ToString();
    }

    /// <summary>Parses and validates a version-one baseline, failing closed on malformed or unknown versions.</summary>
    public static AiToolContractBaseline Parse(string json, AiToolContractLimits? limits = null)
    {
        if (json is null)
            throw new ArgumentNullException(nameof(json));
        var effectiveLimits = limits ?? AiToolContractLimits.Default;
        try
        {
            effectiveLimits.Validate();
            if (System.Text.Encoding.UTF8.GetByteCount(json) > effectiveLimits.MaxSchemaBytes * 2L)
                throw new AiToolContractException(new AiToolContractDiagnostic(AiToolDiagnosticKind.CanonicalizationOrResourceLimit, "The baseline exceeds the configured byte limit."));

            RejectDuplicateProperties(json, effectiveLimits);
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = effectiveLimits.MaxSchemaDepth + 4, CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw Malformed("The baseline root must be a JSON object.");
            if (!root.TryGetProperty("schemaVersion", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var schemaVersion))
                throw Malformed("The baseline must contain an integer schemaVersion.");
            if (schemaVersion != 1)
                throw new AiToolContractException(new AiToolContractDiagnostic(AiToolDiagnosticKind.UnsupportedBaselineVersion, "Baseline schema version " + schemaVersion + " is not supported."));
            if (!root.TryGetProperty("tools", out var toolsElement) || toolsElement.ValueKind != JsonValueKind.Array)
                throw Malformed("The baseline must contain a tools array.");

            var tools = new List<AiToolContractTool>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var toolElement in toolsElement.EnumerateArray())
            {
                if (tools.Count >= effectiveLimits.MaxTools)
                    throw new AiToolContractException(new AiToolContractDiagnostic(AiToolDiagnosticKind.CanonicalizationOrResourceLimit, "The baseline exceeds the configured tool-count limit."));
                if (toolElement.ValueKind != JsonValueKind.Object)
                    throw Malformed("Every tools entry must be an object.");
                var name = ReadRequiredString(toolElement, "name");
                if (!names.Add(name))
                    throw new AiToolContractException(new AiToolContractDiagnostic(AiToolDiagnosticKind.DuplicateToolIdentity, "The baseline contains duplicate tool identity '" + name + "'."));
                var description = ReadNullableString(toolElement, "description");
                if (!toolElement.TryGetProperty("inputSchema", out var inputSchema))
                    throw Malformed("Tool '" + name + "' is missing inputSchema.");
                var inputJson = CanonicalJson.Canonicalize(inputSchema.GetRawText(), effectiveLimits);
                string? returnJson = null;
                if (toolElement.TryGetProperty("returnSchema", out var returnSchema) && returnSchema.ValueKind != JsonValueKind.Null)
                    returnJson = CanonicalJson.Canonicalize(returnSchema.GetRawText(), effectiveLimits);
                var requiresApproval = false;
                if (toolElement.TryGetProperty("requiresApproval", out var approval))
                {
                    if (approval.ValueKind != JsonValueKind.True && approval.ValueKind != JsonValueKind.False)
                        throw Malformed("Tool '" + name + "' has a non-boolean requiresApproval value.");
                    requiresApproval = approval.GetBoolean();
                }
                tools.Add(new AiToolContractTool(name, description, inputJson, returnJson, requiresApproval));
            }

            tools.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
            return new AiToolContractBaseline(tools);
        }
        catch (AiToolContractException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw new AiToolContractException(new AiToolContractDiagnostic(IsDepthLimit(ex) ? AiToolDiagnosticKind.CanonicalizationOrResourceLimit : AiToolDiagnosticKind.MalformedBaseline, "The baseline JSON is " + (IsDepthLimit(ex) ? "too deep" : "malformed") + ": " + ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            throw new AiToolContractException(new AiToolContractDiagnostic(AiToolDiagnosticKind.MalformedBaseline, "The baseline is invalid: " + ex.Message));
        }
    }

    private static string ReadRequiredString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
            throw Malformed("Every tool must contain a string " + propertyName + ".");
        var value = property.GetString();
        if (string.IsNullOrWhiteSpace(value))
            throw Malformed("Tool identity cannot be empty.");
        return value!;
    }

    private static string? ReadNullableString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
            return null;
        if (property.ValueKind != JsonValueKind.String)
            throw Malformed("Property " + propertyName + " must be a string or null.");
        return property.GetString();
    }

    private static AiToolContractException Malformed(string message) =>
        new(new AiToolContractDiagnostic(AiToolDiagnosticKind.MalformedBaseline, message));

    private static void RejectDuplicateProperties(string json, AiToolContractLimits limits)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json), new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
            MaxDepth = limits.MaxSchemaDepth + 4
        });
        var objects = new Stack<HashSet<string>>();
        var sawToken = false;

        try
        {
            while (reader.Read())
            {
                sawToken = true;
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        objects.Push(new HashSet<string>(StringComparer.Ordinal));
                        break;
                    case JsonTokenType.PropertyName:
                        if (objects.Count == 0 || !objects.Peek().Add(reader.GetString()!))
                            throw Malformed("The baseline contains a duplicate JSON object member.");
                        break;
                    case JsonTokenType.EndObject:
                        if (objects.Count == 0)
                            throw Malformed("The baseline JSON is malformed.");
                        objects.Pop();
                        break;
                }
            }
        }
        catch (AiToolContractException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw new AiToolContractException(new AiToolContractDiagnostic(IsDepthLimit(ex) ? AiToolDiagnosticKind.CanonicalizationOrResourceLimit : AiToolDiagnosticKind.MalformedBaseline, "The baseline JSON is " + (IsDepthLimit(ex) ? "too deep" : "malformed") + ": " + ex.Message));
        }

        if (!sawToken || objects.Count != 0)
            throw Malformed("The baseline JSON is malformed.");
    }

    internal static bool IsDepthLimit(JsonException exception) =>
        exception.Message.Contains("depth", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("maximum", StringComparison.OrdinalIgnoreCase);
}

internal static class SchemaSemantics
{
    private static readonly HashSet<string> JsonSchemaTypes = new(StringComparer.Ordinal)
    {
        "array",
        "boolean",
        "integer",
        "null",
        "number",
        "object",
        "string"
    };

    private static readonly HashSet<string> SupportedKeywords = new(StringComparer.Ordinal)
    {
        "$defs",
        "$ref",
        "additionalProperties",
        "allOf",
        "anyOf",
        "contains",
        "definitions",
        "enum",
        "exclusiveMaximum",
        "exclusiveMinimum",
        "items",
        "maxItems",
        "maxLength",
        "maximum",
        "minItems",
        "minLength",
        "minimum",
        "not",
        "nullable",
        "oneOf",
        "properties",
        "required",
        "type"
    };

    internal static void Validate(JsonElement schema)
    {
        ValidateSchema(schema, "$");
    }

    private static void ValidateSchema(JsonElement schema, string path)
    {
        if (schema.ValueKind is JsonValueKind.True or JsonValueKind.False)
            throw Unsupported("Boolean JSON Schema at " + path + " is valid but is not supported for contract comparison.");
        if (schema.ValueKind != JsonValueKind.Object)
            throw Malformed("JSON Schema at " + path + " must be an object.");

        foreach (var property in schema.EnumerateObject())
        {
            var propertyPath = path + "." + property.Name;
            if (!SupportedKeywords.Contains(property.Name))
                throw Unsupported("Schema keyword '" + property.Name + "' at " + propertyPath + " has unsupported semantics and requires review.");

            switch (property.Name)
            {
                case "$defs":
                case "definitions":
                case "properties":
                    ValidateSchemaMap(property.Value, propertyPath);
                    break;
                case "$ref":
                    RequireKind(property.Value, JsonValueKind.String, propertyPath);
                    break;
                case "additionalProperties":
                    ValidateSchemaOrBoolean(property.Value, propertyPath);
                    break;
                case "allOf":
                case "anyOf":
                case "oneOf":
                    ValidateSchemaArray(property.Value, propertyPath);
                    break;
                case "contains":
                case "not":
                    ValidateSchemaOrBoolean(property.Value, propertyPath);
                    break;
                case "items":
                    if (property.Value.ValueKind == JsonValueKind.Array)
                        throw Unsupported("Tuple-form items at " + propertyPath + " is valid JSON Schema but is not supported for contract comparison.");
                    ValidateSchemaOrBoolean(property.Value, propertyPath);
                    break;
                case "enum":
                    RequireKind(property.Value, JsonValueKind.Array, propertyPath);
                    break;
                case "required":
                    ValidateStringArray(property.Value, propertyPath);
                    break;
                case "type":
                    ValidateType(property.Value, propertyPath);
                    break;
                case "nullable":
                    RequireKind(property.Value, JsonValueKind.True, JsonValueKind.False, propertyPath);
                    break;
                case "exclusiveMaximum":
                case "exclusiveMinimum":
                case "maximum":
                case "minimum":
                    RequireKind(property.Value, JsonValueKind.Number, propertyPath);
                    break;
                case "maxItems":
                case "maxLength":
                case "minItems":
                case "minLength":
                    ValidateNonNegativeInteger(property.Value, propertyPath);
                    break;
            }
        }
    }

    private static void ValidateSchemaMap(JsonElement value, string path)
    {
        RequireKind(value, JsonValueKind.Object, path);
        foreach (var child in value.EnumerateObject())
            ValidateSchemaOrBoolean(child.Value, path + "." + child.Name);
    }

    private static void ValidateSchemaArray(JsonElement value, string path)
    {
        RequireKind(value, JsonValueKind.Array, path);
        var index = 0;
        foreach (var child in value.EnumerateArray())
            ValidateSchemaOrBoolean(child, path + "[" + index++ + "]");
    }

    private static void ValidateSchemaOrBoolean(JsonElement value, string path)
    {
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            throw Unsupported("Boolean JSON Schema at " + path + " is valid but is not supported for contract comparison.");
        ValidateSchema(value, path);
    }

    private static void ValidateStringArray(JsonElement value, string path)
    {
        RequireKind(value, JsonValueKind.Array, path);
        var values = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            var itemPath = path + "[" + index++ + "]";
            RequireKind(item, JsonValueKind.String, itemPath);
            if (!values.Add(item.GetString()!))
                throw Malformed("JSON Schema array at " + path + " contains duplicate values.");
        }
    }

    private static void ValidateType(JsonElement value, string path)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            ValidateTypeName(value.GetString()!, path);
            return;
        }
        RequireKind(value, JsonValueKind.Array, path);
        var values = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            var itemPath = path + "[" + index++ + "]";
            RequireKind(item, JsonValueKind.String, itemPath);
            var typeName = item.GetString()!;
            ValidateTypeName(typeName, itemPath);
            if (!values.Add(typeName))
                throw Malformed("JSON Schema type at " + path + " contains duplicate values.");
        }
        if (values.Count == 0)
            throw Malformed("JSON Schema type at " + path + " must contain at least one type.");
    }

    private static void ValidateTypeName(string value, string path)
    {
        if (!JsonSchemaTypes.Contains(value))
            throw Malformed("JSON Schema type '" + value + "' at " + path + " is not a recognized JSON Schema type.");
    }

    private static void ValidateNonNegativeInteger(JsonElement value, string path)
    {
        RequireKind(value, JsonValueKind.Number, path);
        if (!value.TryGetInt64(out var integer) || integer < 0)
            throw Malformed("JSON Schema value at " + path + " must be a non-negative integer.");
    }

    private static void RequireKind(JsonElement value, JsonValueKind expected, string path) =>
        RequireKind(value, new[] { expected }, path);

    private static void RequireKind(JsonElement value, JsonValueKind expectedFirst, JsonValueKind expectedSecond, string path) =>
        RequireKind(value, new[] { expectedFirst, expectedSecond }, path);

    private static void RequireKind(JsonElement value, IReadOnlyCollection<JsonValueKind> expected, string path)
    {
        if (!expected.Contains(value.ValueKind))
            throw Malformed("JSON Schema value at " + path + " must be " + string.Join(" or ", expected.Select(static kind => kind.ToString().ToLowerInvariant())) + ".");
    }

    private static AiToolContractException Malformed(string message) =>
        new(new AiToolContractDiagnostic(AiToolDiagnosticKind.MalformedBaseline, message));

private static AiToolContractException Unsupported(string message) =>
        new(new AiToolContractDiagnostic(AiToolDiagnosticKind.UnsupportedClassification, message));
}
