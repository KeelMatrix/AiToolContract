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
                    var inputSchema = SchemaNormalizer.Normalize(declaration.JsonSchema, effectiveLimits);
                    var returnSchema = declaration.ReturnJsonSchema is JsonElement returnElement
                        ? SchemaNormalizer.Normalize(returnElement, effectiveLimits)
                        : null;
                    var description = declaration.Description;
                    var requiresApproval = IsApprovalRequired(declaration);
                    captured.Add(new AiToolContractTool(name, description, inputSchema, returnSchema, requiresApproval));
                }
                catch (AiToolContractException ex)
                {
                    return Failure(ex.Diagnostic.Kind, "Tool '" + name + "': " + ex.Diagnostic.Message);
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or OverflowException)
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
