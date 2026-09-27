using System.Text;
using System.Text.Json;

namespace KeelMatrix.AiToolContract;

/// <summary>Reads, writes, and canonicalizes the KeelMatrix baseline format.</summary>
public static class AiToolContractJson
{
    private static readonly HashSet<string> BaselineMembers = new(StringComparer.Ordinal)
    {
        "schemaVersion",
        "tools"
    };

    private static readonly HashSet<string> ToolMembers = new(StringComparer.Ordinal)
    {
        "name",
        "description",
        "inputSchema",
        "returnSchema",
        "requiresApproval"
    };

    /// <summary>Serializes a version-one baseline deterministically.</summary>
    public static string Serialize(AiToolContractBaseline baseline, AiToolContractLimits? limits = null)
    {
        if (baseline is null)
            throw new ArgumentNullException(nameof(baseline));
        var effectiveLimits = limits ?? AiToolContractLimits.Default;
        effectiveLimits.Validate();
        if (baseline.SchemaVersion != 1)
            throw new AiToolContractException(new AiToolContractDiagnostic(AiToolDiagnosticKind.UnsupportedBaselineVersion, "Baseline schema version " + baseline.SchemaVersion + " is not supported."));
        baseline.ValidateLimits(effectiveLimits);

        var tools = baseline.Tools.OrderBy(static tool => tool.Name, StringComparer.Ordinal).ToList();
        var builder = new StringBuilder();
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
            builder.Append(tool.InputSchema.ToCanonicalJson());
            builder.Append(",\"returnSchema\":");
            builder.Append(tool.ReturnSchema is null ? "null" : tool.ReturnSchema.ToCanonicalJson());
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
            if (Encoding.UTF8.GetByteCount(json) > (long)effectiveLimits.MaxSchemaBytes * 2L)
                throw Resource("The baseline exceeds the configured byte limit.");

            RejectDuplicateProperties(json, effectiveLimits);
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = effectiveLimits.GetJsonDocumentMaxDepth(4), CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
            var root = document.RootElement;
            RequireObject(root, "The baseline root must be a JSON object.");
            RequireEnvelopeMembers(root, BaselineMembers, "baseline");

            var version = root.GetProperty("schemaVersion");
            if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var schemaVersion))
                throw Malformed("The baseline schemaVersion must be an integer.");
            if (schemaVersion != 1)
                throw new AiToolContractException(new AiToolContractDiagnostic(AiToolDiagnosticKind.UnsupportedBaselineVersion, "Baseline schema version " + schemaVersion + " is not supported."));

            var toolsElement = root.GetProperty("tools");
            RequireKind(toolsElement, JsonValueKind.Array, "The baseline tools member must be an array.");
            var tools = new List<AiToolContractTool>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var toolElement in toolsElement.EnumerateArray())
            {
                if (tools.Count >= effectiveLimits.MaxTools)
                    throw Resource("The baseline exceeds the configured tool-count limit.");
                RequireObject(toolElement, "Every tools entry must be an object.");
                RequireEnvelopeMembers(toolElement, ToolMembers, "tool");

                var name = ReadRequiredString(toolElement, "name");
                if (!names.Add(name))
                    throw new AiToolContractException(new AiToolContractDiagnostic(AiToolDiagnosticKind.DuplicateToolIdentity, "The baseline contains duplicate tool identity '" + name + "'."));
                var description = ReadNullableString(toolElement, "description");
                var inputSchemaElement = toolElement.GetProperty("inputSchema");
                RequireObject(inputSchemaElement, "Tool '" + name + "' inputSchema must be an object.");
                var inputSchema = SchemaNormalizer.Normalize(inputSchemaElement, effectiveLimits, AiToolDiagnosticKind.MalformedBaseline);

                var returnSchemaElement = toolElement.GetProperty("returnSchema");
                NormalizedSchema? returnSchema = null;
                if (returnSchemaElement.ValueKind != JsonValueKind.Null)
                {
                    RequireObject(returnSchemaElement, "Tool '" + name + "' returnSchema must be an object or null.");
                    returnSchema = SchemaNormalizer.Normalize(returnSchemaElement, effectiveLimits, AiToolDiagnosticKind.MalformedBaseline);
                }

                var approval = toolElement.GetProperty("requiresApproval");
                RequireKind(approval, JsonValueKind.True, JsonValueKind.False, "Tool '" + name + "' requiresApproval must be a boolean.");
                tools.Add(new AiToolContractTool(name, description, inputSchema, returnSchema, approval.GetBoolean()));
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
            throw Malformed("The baseline is invalid: " + ex.Message);
        }
        catch (OverflowException ex)
        {
            throw Resource("The baseline numeric representation exceeds the configured resource boundary: " + ex.Message);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw Resource("The baseline exceeds the configured resource boundary: " + ex.Message);
        }
    }

    private static string ReadRequiredString(JsonElement element, string propertyName)
    {
        var property = element.GetProperty(propertyName);
        RequireKind(property, JsonValueKind.String, "Every tool must contain a string " + propertyName + ".");
        var value = property.GetString();
        if (string.IsNullOrWhiteSpace(value))
            throw Malformed("Tool identity cannot be empty.");
        return value!;
    }

    private static string? ReadNullableString(JsonElement element, string propertyName)
    {
        var property = element.GetProperty(propertyName);
        if (property.ValueKind == JsonValueKind.Null)
            return null;
        RequireKind(property, JsonValueKind.String, "Property " + propertyName + " must be a string or null.");
        return property.GetString();
    }

    private static void RequireEnvelopeMembers(JsonElement element, HashSet<string> allowed, string context)
    {
        var actual = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            actual.Add(property.Name);
        foreach (var property in actual)
            if (!allowed.Contains(property))
                throw Malformed("Unknown " + context + " member '" + property + "'.");
        foreach (var required in allowed)
            if (!actual.Contains(required))
                throw Malformed("The " + context + " envelope is missing required member '" + required + "'.");
    }

    private static void RequireObject(JsonElement value, string message)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw Malformed(message);
    }

    private static void RequireKind(JsonElement value, JsonValueKind expected, string message)
    {
        if (value.ValueKind != expected)
            throw Malformed(message);
    }

    private static void RequireKind(JsonElement value, JsonValueKind first, JsonValueKind second, string message)
    {
        if (value.ValueKind != first && value.ValueKind != second)
            throw Malformed(message);
    }

    private static AiToolContractException Malformed(string message) =>
        new(new AiToolContractDiagnostic(AiToolDiagnosticKind.MalformedBaseline, message));

    private static AiToolContractException Resource(string message) =>
        new(new AiToolContractDiagnostic(AiToolDiagnosticKind.CanonicalizationOrResourceLimit, message));

    private static void RejectDuplicateProperties(string json, AiToolContractLimits limits)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json), new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
            MaxDepth = limits.GetJsonDocumentMaxDepth(4)
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
        exception.Message.Contains("depth", StringComparison.OrdinalIgnoreCase) || exception.Message.Contains("maximum", StringComparison.OrdinalIgnoreCase);
}
