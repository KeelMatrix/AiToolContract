using System.Text.Json;
using Microsoft.Extensions.AI;

namespace KeelMatrix.AiToolContract;

/// <summary>Captures model-visible metadata from real Microsoft.Extensions.AI tools.</summary>
public static class AiToolContractCapture
{
    /// <summary>
    /// Captures a catalog of AIFunction declarations without invoking any function or provider.
    /// Non-function AITool instances fail closed because they do not expose the callable schema required by this package.
    /// </summary>
    public static AiToolContractCaptureResult Capture(IEnumerable<AITool> tools, AiToolContractLimits? limits = null)
    {
        if (tools is null)
            throw new ArgumentNullException(nameof(tools));

        var effectiveLimits = limits ?? AiToolContractLimits.Default;
        try
        {
            effectiveLimits.Validate();
            var captured = new List<AiToolContractTool>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            var count = 0;

            foreach (var tool in tools)
            {
                if (++count > effectiveLimits.MaxTools)
                    return Failure(AiToolDiagnosticKind.CanonicalizationOrResourceLimit, "The tool catalog exceeds the configured tool-count limit.");

                if (tool is not AIFunctionDeclaration declaration)
                    return Failure(AiToolDiagnosticKind.CaptureFailure, "Every captured tool must be an AIFunctionDeclaration with callable JSON Schema metadata.");

                string name;
                try
                {
                    name = declaration.Name;
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
                {
                    return Failure(AiToolDiagnosticKind.CaptureFailure, "The tool name could not be read: " + ex.Message);
                }

                if (string.IsNullOrWhiteSpace(name))
                    return Failure(AiToolDiagnosticKind.CaptureFailure, "A tool has an empty or whitespace-only identity.");

                if (!names.Add(name))
                    return Failure(AiToolDiagnosticKind.DuplicateToolIdentity, "The catalog contains duplicate tool identity '" + name + "'.");

                try
                {
                    var inputSchema = CanonicalJson.Canonicalize(declaration.JsonSchema, effectiveLimits);
                    var returnSchema = declaration.ReturnJsonSchema is JsonElement returnElement
                        ? CanonicalJson.Canonicalize(returnElement, effectiveLimits)
                        : null;
                    var description = declaration.Description;
                    var requiresApproval = IsApprovalRequired(declaration);
                    captured.Add(new AiToolContractTool(name, description, inputSchema, returnSchema, requiresApproval));
                }
                catch (AiToolContractException ex)
                {
                    return Failure(ex.Diagnostic.Kind, "Tool '" + name + "': " + ex.Diagnostic.Message);
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
                {
                    return Failure(AiToolDiagnosticKind.CaptureFailure, "Tool '" + name + "' metadata could not be captured: " + ex.Message);
                }
            }

            captured.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
            return new AiToolContractCaptureResult(new AiToolContractBaseline(captured), null);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return Failure(AiToolDiagnosticKind.CanonicalizationOrResourceLimit, ex.Message);
        }
    }

    /// <summary>Captures a catalog of AIFunction instances.</summary>
    public static AiToolContractCaptureResult Capture(IEnumerable<AIFunction> functions, AiToolContractLimits? limits = null)
    {
        if (functions is null)
            throw new ArgumentNullException(nameof(functions));
        return Capture(functions.Cast<AITool>(), limits);
    }

    private static AiToolContractCaptureResult Failure(AiToolDiagnosticKind kind, string message) =>
        new(null, new AiToolContractDiagnostic(kind, message));

    private static bool IsApprovalRequired(AIFunctionDeclaration declaration)
    {
        // ApprovalRequiredAIFunction is currently an explicitly documented framework wrapper,
        // but it carries MEAI001. Reflection keeps this package usable without making that
        // experimental diagnostic part of every consumer's compilation.
        for (var type = declaration.GetType(); type is not null; type = type.BaseType)
        {
            if (string.Equals(type.FullName, "Microsoft.Extensions.AI.ApprovalRequiredAIFunction", StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}

internal static class CanonicalJson
{
    public static string Canonicalize(JsonElement element, AiToolContractLimits limits)
    {
        var raw = element.GetRawText();
        if (System.Text.Encoding.UTF8.GetByteCount(raw) > limits.MaxSchemaBytes)
            throw new AiToolContractException(new AiToolContractDiagnostic(AiToolDiagnosticKind.CanonicalizationOrResourceLimit, "The JSON Schema exceeds the configured byte limit."));

        using var document = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = limits.MaxSchemaDepth, CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
        var builder = new System.Text.StringBuilder(raw.Length);
        var state = new CanonicalState(limits);
        Write(document.RootElement, builder, state, 0);
        return builder.ToString();
    }

    public static string Canonicalize(string raw, AiToolContractLimits limits)
    {
        if (raw is null)
            throw new AiToolContractException(new AiToolContractDiagnostic(AiToolDiagnosticKind.MalformedBaseline, "A schema value is null."));
        if (System.Text.Encoding.UTF8.GetByteCount(raw) > limits.MaxSchemaBytes)
            throw new AiToolContractException(new AiToolContractDiagnostic(AiToolDiagnosticKind.CanonicalizationOrResourceLimit, "The JSON Schema exceeds the configured byte limit."));

        try
        {
            using var document = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = limits.MaxSchemaDepth, CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
            var builder = new System.Text.StringBuilder(raw.Length);
            var state = new CanonicalState(limits);
            Write(document.RootElement, builder, state, 0);
            return builder.ToString();
        }
        catch (AiToolContractException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw new AiToolContractException(new AiToolContractDiagnostic(AiToolDiagnosticKind.MalformedBaseline, "The JSON Schema is malformed: " + ex.Message));
        }
    }

    private static void Write(JsonElement element, System.Text.StringBuilder builder, CanonicalState state, int depth, string? propertyName = null)
    {
        if (++state.NodeCount > state.Limits.MaxProperties + state.Limits.MaxArrayItems + 1_000_000)
            throw new AiToolContractException(new AiToolContractDiagnostic(AiToolDiagnosticKind.CanonicalizationOrResourceLimit, "The JSON Schema exceeds the configured node limit."));
        if (depth > state.Limits.MaxSchemaDepth)
            throw new AiToolContractException(new AiToolContractDiagnostic(AiToolDiagnosticKind.CanonicalizationOrResourceLimit, "The JSON Schema exceeds the configured depth limit."));

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                builder.Append('{');
                var properties = element.EnumerateObject().ToList();
                state.PropertyCount += properties.Count;
                if (state.PropertyCount > state.Limits.MaxProperties)
                    throw new AiToolContractException(new AiToolContractDiagnostic(AiToolDiagnosticKind.CanonicalizationOrResourceLimit, "The JSON Schema exceeds the configured property limit."));
                var propertyNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in properties)
                    if (!propertyNames.Add(property.Name))
                        throw new AiToolContractException(new AiToolContractDiagnostic(AiToolDiagnosticKind.MalformedBaseline, "The JSON Schema contains duplicate object property '" + property.Name + "'."));
                properties.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
                for (var index = 0; index < properties.Count; index++)
                {
                    if (index > 0)
                        builder.Append(',');
                    builder.Append(JsonSerializer.Serialize(properties[index].Name));
                    builder.Append(':');
                    Write(properties[index].Value, builder, state, depth + 1, properties[index].Name);
                }
                builder.Append('}');
                break;
            case JsonValueKind.Array:
                builder.Append('[');
                var items = element.EnumerateArray().ToList();
                if (string.Equals(propertyName, "required", StringComparison.Ordinal) || string.Equals(propertyName, "enum", StringComparison.Ordinal))
                    items.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.GetRawText(), right.GetRawText()));
                var itemIndex = 0;
                foreach (var item in items)
                {
                    if (++state.ArrayItemCount > state.Limits.MaxArrayItems)
                        throw new AiToolContractException(new AiToolContractDiagnostic(AiToolDiagnosticKind.CanonicalizationOrResourceLimit, "The JSON Schema exceeds the configured array-item limit."));
                    if (itemIndex++ > 0)
                        builder.Append(',');
                    Write(item, builder, state, depth + 1);
                }
                builder.Append(']');
                break;
            case JsonValueKind.String:
                builder.Append(JsonSerializer.Serialize(element.GetString()));
                break;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                builder.Append(element.GetRawText());
                break;
            default:
                throw new AiToolContractException(new AiToolContractDiagnostic(AiToolDiagnosticKind.MalformedBaseline, "The JSON Schema contains an unsupported JSON token."));
        }
    }

    private sealed class CanonicalState
    {
        public CanonicalState(AiToolContractLimits limits) => Limits = limits;
        public AiToolContractLimits Limits { get; }
        public int NodeCount { get; set; }
        public int PropertyCount { get; set; }
        public int ArrayItemCount { get; set; }
    }
}
