using System.Globalization;
using System.Numerics;
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

        try
        {
            using var document = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = limits.MaxSchemaDepth, CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
            SchemaSemantics.Validate(document.RootElement);
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
            throw new AiToolContractException(new AiToolContractDiagnostic(
                AiToolContractJson.IsDepthLimit(ex) ? AiToolDiagnosticKind.CanonicalizationOrResourceLimit : AiToolDiagnosticKind.CaptureFailure,
                "The JSON Schema is " + (AiToolContractJson.IsDepthLimit(ex) ? "too deep" : "malformed") + ": " + ex.Message));
        }
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
            SchemaSemantics.Validate(document.RootElement);
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
            throw new AiToolContractException(new AiToolContractDiagnostic(
                AiToolContractJson.IsDepthLimit(ex) ? AiToolDiagnosticKind.CanonicalizationOrResourceLimit : AiToolDiagnosticKind.MalformedBaseline,
                "The JSON Schema is " + (AiToolContractJson.IsDepthLimit(ex) ? "too deep" : "malformed") + ": " + ex.Message));
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
                if (string.Equals(propertyName, "required", StringComparison.Ordinal) || string.Equals(propertyName, "enum", StringComparison.Ordinal) || string.Equals(propertyName, "type", StringComparison.Ordinal))
                    items.Sort(static (left, right) => StringComparer.Ordinal.Compare(CanonicalizeValue(left), CanonicalizeValue(right)));
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
                builder.Append(CanonicalizeNumber(element.GetRawText()));
                break;
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

    internal static string CanonicalizeValue(JsonElement element)
    {
        var builder = new System.Text.StringBuilder();
        WriteValue(element, builder);
        return builder.ToString();
    }

    internal static bool TryParseNumber(string raw, out JsonNumber number)
    {
        number = default;
        if (string.IsNullOrEmpty(raw))
            return false;

        var end = raw.Length;
        var exponentIndex = raw.IndexOf('e');
        if (exponentIndex < 0)
            exponentIndex = raw.IndexOf('E');
        var significandEnd = exponentIndex >= 0 ? exponentIndex : end;
        long exponent = 0;
        if (exponentIndex >= 0 && !TryParseExponent(raw, exponentIndex + 1, out exponent))
            return false;

        var significand = raw.Substring(0, significandEnd);
        var negative = significand[0] == '-';
        var digitsStart = negative ? 1 : 0;
        var decimalIndex = significand.IndexOf('.');
        var fractionLength = decimalIndex >= 0 ? significand.Length - decimalIndex - 1 : 0;
        var digitsBuilder = new System.Text.StringBuilder();
        if (decimalIndex >= 0)
        {
            digitsBuilder.Append(significand, digitsStart, decimalIndex - digitsStart);
            digitsBuilder.Append(significand, decimalIndex + 1, significand.Length - decimalIndex - 1);
        }
        else
        {
            digitsBuilder.Append(significand, digitsStart, significand.Length - digitsStart);
        }
        var digits = digitsBuilder.ToString();
        if (digits.Length == 0)
            return false;

        try
        {
            exponent = checked(exponent - fractionLength);
            var coefficient = BigInteger.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
            if (negative)
                coefficient = -coefficient;
            while (!coefficient.IsZero && coefficient % 10 == 0)
            {
                coefficient /= 10;
                exponent = checked(exponent + 1);
            }

            number = new JsonNumber(coefficient, coefficient.IsZero ? 0 : exponent);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    internal static bool NumbersEqual(JsonElement left, JsonElement right) =>
        left.ValueKind == JsonValueKind.Number &&
        right.ValueKind == JsonValueKind.Number &&
        TryParseNumber(left.GetRawText(), out var leftNumber) &&
        TryParseNumber(right.GetRawText(), out var rightNumber) &&
        leftNumber.CompareTo(rightNumber) == 0;

    private static bool TryParseExponent(string raw, int start, out long exponent)
    {
        exponent = 0;
        if (start >= raw.Length)
            return false;
        var negative = raw[start] == '-';
        var index = negative || raw[start] == '+' ? start + 1 : start;
        if (index >= raw.Length)
            return false;
        ulong value = 0;
        for (; index < raw.Length; index++)
        {
            var digit = raw[index] - '0';
            if (digit < 0 || digit > 9)
                return false;
            if (value > (ulong.MaxValue - (uint)digit) / 10)
                return false;
            value = value * 10 + (uint)digit;
        }

        if (negative)
        {
            if (value > 9223372036854775808UL)
                return false;
            exponent = value == 9223372036854775808UL ? long.MinValue : -(long)value;
        }
        else
        {
            if (value > long.MaxValue)
                return false;
            exponent = (long)value;
        }
        return true;
    }

    private static string CanonicalizeNumber(string raw)
    {
        if (!TryParseNumber(raw, out var number))
            throw new AiToolContractException(new AiToolContractDiagnostic(AiToolDiagnosticKind.MalformedBaseline, "The JSON Schema contains an invalid number."));
        return number.ToCanonicalString();
    }

    private static void WriteValue(JsonElement element, System.Text.StringBuilder builder)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                builder.Append('{');
                var properties = element.EnumerateObject().OrderBy(static property => property.Name, StringComparer.Ordinal).ToList();
                for (var index = 0; index < properties.Count; index++)
                {
                    if (index > 0)
                        builder.Append(',');
                    builder.Append(JsonSerializer.Serialize(properties[index].Name));
                    builder.Append(':');
                    WriteValue(properties[index].Value, builder);
                }
                builder.Append('}');
                break;
            case JsonValueKind.Array:
                builder.Append('[');
                var items = element.EnumerateArray().ToList();
                for (var index = 0; index < items.Count; index++)
                {
                    if (index > 0)
                        builder.Append(',');
                    WriteValue(items[index], builder);
                }
                builder.Append(']');
                break;
            case JsonValueKind.String:
                builder.Append(JsonSerializer.Serialize(element.GetString()));
                break;
            case JsonValueKind.Number:
                builder.Append(CanonicalizeNumber(element.GetRawText()));
                break;
            case JsonValueKind.True:
                builder.Append("true");
                break;
            case JsonValueKind.False:
                builder.Append("false");
                break;
            case JsonValueKind.Null:
                builder.Append("null");
                break;
            default:
                throw new AiToolContractException(new AiToolContractDiagnostic(AiToolDiagnosticKind.MalformedBaseline, "The JSON value contains an unsupported token."));
        }
    }

    internal readonly struct JsonNumber
    {
        internal JsonNumber(BigInteger coefficient, long exponent)
        {
            Coefficient = coefficient;
            Exponent = exponent;
        }

        internal BigInteger Coefficient { get; }
        internal long Exponent { get; }
        internal bool IsNegative => Coefficient.Sign < 0;
        internal bool IsInteger => Coefficient.IsZero || Exponent >= 0;

        internal int CompareTo(JsonNumber other)
        {
            if (Coefficient.IsZero || other.Coefficient.IsZero)
                return Coefficient.IsZero ? (other.Coefficient.IsZero ? 0 : -other.Coefficient.Sign) : Coefficient.Sign;
            if (IsNegative != other.IsNegative)
                return IsNegative ? -1 : 1;

            var result = CompareMagnitude(this, other);
            return IsNegative ? -result : result;
        }

        internal string ToCanonicalString()
        {
            if (Coefficient.IsZero)
                return "0";
            var prefix = IsNegative ? "-" : string.Empty;
            var digits = BigInteger.Abs(Coefficient).ToString(CultureInfo.InvariantCulture);
            return prefix + digits + (Exponent == 0 ? string.Empty : "e" + Exponent.ToString(CultureInfo.InvariantCulture));
        }

        private static int CompareMagnitude(JsonNumber left, JsonNumber right)
        {
            var leftDigits = BigInteger.Abs(left.Coefficient).ToString(CultureInfo.InvariantCulture).Length;
            var rightDigits = BigInteger.Abs(right.Coefficient).ToString(CultureInfo.InvariantCulture).Length;
            var leftMagnitude = leftDigits + left.Exponent;
            var rightMagnitude = rightDigits + right.Exponent;
            if (leftMagnitude != rightMagnitude)
                return leftMagnitude > rightMagnitude ? 1 : -1;

            var leftValue = BigInteger.Abs(left.Coefficient);
            var rightValue = BigInteger.Abs(right.Coefficient);
            if (left.Exponent > right.Exponent)
                leftValue *= BigInteger.Pow(10, checked((int)(left.Exponent - right.Exponent)));
            else if (right.Exponent > left.Exponent)
                rightValue *= BigInteger.Pow(10, checked((int)(right.Exponent - left.Exponent)));
            return leftValue.CompareTo(rightValue);
        }
    }
}
