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
        var unsupported = FindUnsupported(schema).FirstOrDefault();
        if (!string.IsNullOrEmpty(unsupported.Keyword))
            throw new AiToolContractException(new AiToolContractDiagnostic(AiToolDiagnosticKind.UnsupportedClassification, "Schema keyword '" + unsupported.Keyword + "' at " + unsupported.Path + " has unsupported semantics and requires review."));
    }

    internal static IEnumerable<(string Path, string Keyword)> FindUnsupported(JsonElement schema)
    {
        var results = new List<(string Path, string Keyword)>();
        Visit(schema, "$", results);
        return results;
    }

    private static void Visit(JsonElement schema, string path, ICollection<(string Path, string Keyword)> results)
    {
        if (schema.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in schema.EnumerateObject())
        {
            var propertyPath = path + "." + property.Name;
            if (!SupportedKeywords.Contains(property.Name))
            {
                results.Add((propertyPath, property.Name));
                continue;
            }

            switch (property.Name)
            {
                case "properties":
                case "$defs":
                case "definitions":
                    if (property.Value.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var child in property.Value.EnumerateObject())
                            Visit(child.Value, propertyPath + "." + child.Name, results);
                    }
                    break;
                case "items":
                case "additionalProperties":
                case "contains":
                case "not":
                    Visit(property.Value, propertyPath, results);
                    break;
                case "oneOf":
                case "anyOf":
                case "allOf":
                    if (property.Value.ValueKind == JsonValueKind.Array)
                    {
                        var index = 0;
                        foreach (var child in property.Value.EnumerateArray())
                            Visit(child, propertyPath + "[" + index++ + "]", results);
                    }
                    break;
            }
        }
    }
}
