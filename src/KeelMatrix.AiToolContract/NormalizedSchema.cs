using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace KeelMatrix.AiToolContract;

/// <summary>
/// The closed internal schema surface. Capture, baseline parsing, and comparison
/// all cross this boundary; comparison never operates on raw JSON text.
/// </summary>
internal sealed class NormalizedSchema
{
    internal NormalizedSchema(
        string? reference,
        string? description,
        bool hasDefault,
        NormalizedJsonValue? defaultValue,
        string? format,
        IReadOnlyList<string> types,
        IReadOnlyDictionary<string, NormalizedSchema> properties,
        IReadOnlyList<string> required,
        IReadOnlyList<NormalizedJsonValue>? enumValues,
        NormalizedSchema? items,
        JsonNumber? minimum,
        JsonNumber? maximum,
        JsonNumber? exclusiveMinimum,
        JsonNumber? exclusiveMaximum,
        JsonNumber? minLength,
        JsonNumber? maxLength,
        JsonNumber? minItems,
        JsonNumber? maxItems)
    {
        Reference = reference;
        Description = description;
        HasDefault = hasDefault;
        DefaultValue = defaultValue;
        Format = format;
        Types = types;
        Properties = properties;
        Required = required;
        EnumValues = enumValues;
        Items = items;
        Minimum = minimum;
        Maximum = maximum;
        ExclusiveMinimum = exclusiveMinimum;
        ExclusiveMaximum = exclusiveMaximum;
        MinLength = minLength;
        MaxLength = maxLength;
        MinItems = minItems;
        MaxItems = maxItems;
    }

    internal string? Reference { get; }
    internal string? Description { get; }
    internal bool HasDefault { get; }
    internal NormalizedJsonValue? DefaultValue { get; }
    internal string? Format { get; }
    internal IReadOnlyList<string> Types { get; }
    internal IReadOnlyDictionary<string, NormalizedSchema> Properties { get; }
    internal IReadOnlyList<string> Required { get; }
    internal IReadOnlyList<NormalizedJsonValue>? EnumValues { get; }
    internal NormalizedSchema? Items { get; }
    internal JsonNumber? Minimum { get; }
    internal JsonNumber? Maximum { get; }
    internal JsonNumber? ExclusiveMinimum { get; }
    internal JsonNumber? ExclusiveMaximum { get; }
    internal JsonNumber? MinLength { get; }
    internal JsonNumber? MaxLength { get; }
    internal JsonNumber? MinItems { get; }
    internal JsonNumber? MaxItems { get; }

    internal string ToCanonicalJson()
    {
        var builder = new StringBuilder();
        builder.Append('{');
        var first = true;

        WriteProperty(builder, ref first, "$ref", Reference, static (target, output) => WriteString(output, target!));
        WriteProperty(builder, ref first, "description", Description, static (value, output) => WriteString(output, value!));
        if (HasDefault)
            WriteValueProperty(builder, ref first, "default", DefaultValue!);
        WriteProperty(builder, ref first, "format", Format, static (value, output) => WriteString(output, value!));

        if (Types.Count == 1)
        {
            WritePropertyName(builder, ref first, "type");
            WriteString(builder, Types[0]);
        }
        else if (Types.Count > 1)
        {
            WritePropertyName(builder, ref first, "type");
            builder.Append('[');
            for (var index = 0; index < Types.Count; index++)
            {
                if (index > 0)
                    builder.Append(',');
                WriteString(builder, Types[index]);
            }
            builder.Append(']');
        }

        if (EnumValues is not null)
        {
            WritePropertyName(builder, ref first, "enum");
            builder.Append('[');
            for (var index = 0; index < EnumValues.Count; index++)
            {
                if (index > 0)
                    builder.Append(',');
                EnumValues[index].Write(builder);
            }
            builder.Append(']');
        }

        if (Items is not null)
        {
            WritePropertyName(builder, ref first, "items");
            builder.Append(Items.ToCanonicalJson());
        }

        WriteNumberProperty(builder, ref first, "maximum", Maximum);
        WriteNumberProperty(builder, ref first, "exclusiveMaximum", ExclusiveMaximum);
        WriteNumberProperty(builder, ref first, "minimum", Minimum);
        WriteNumberProperty(builder, ref first, "exclusiveMinimum", ExclusiveMinimum);
        WriteNumberProperty(builder, ref first, "maxItems", MaxItems);
        WriteNumberProperty(builder, ref first, "maxLength", MaxLength);
        WriteNumberProperty(builder, ref first, "minItems", MinItems);
        WriteNumberProperty(builder, ref first, "minLength", MinLength);

        if (Properties.Count > 0)
        {
            WritePropertyName(builder, ref first, "properties");
            builder.Append('{');
            var propertyIndex = 0;
            foreach (var property in Properties.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                if (propertyIndex++ > 0)
                    builder.Append(',');
                WriteString(builder, property.Key);
                builder.Append(':');
                builder.Append(property.Value.ToCanonicalJson());
            }
            builder.Append('}');
        }

        if (Required.Count > 0)
        {
            WritePropertyName(builder, ref first, "required");
            builder.Append('[');
            for (var index = 0; index < Required.Count; index++)
            {
                if (index > 0)
                    builder.Append(',');
                WriteString(builder, Required[index]);
            }
            builder.Append(']');
        }

        builder.Append('}');
        return builder.ToString();
    }

    internal bool SemanticallyEquals(NormalizedSchema other)
    {
        if (!string.Equals(Reference, other.Reference, StringComparison.Ordinal) ||
            !string.Equals(Description, other.Description, StringComparison.Ordinal) ||
            HasDefault != other.HasDefault ||
            (HasDefault && !DefaultValue!.SemanticallyEquals(other.DefaultValue!)) ||
            !string.Equals(Format, other.Format, StringComparison.Ordinal) ||
            !Types.SequenceEqual(other.Types, StringComparer.Ordinal) ||
            !Required.SequenceEqual(other.Required, StringComparer.Ordinal) ||
            !NullableValuesEqual(EnumValues, other.EnumValues) ||
            !NullableNumberEqual(Minimum, other.Minimum) ||
            !NullableNumberEqual(Maximum, other.Maximum) ||
            !NullableNumberEqual(ExclusiveMinimum, other.ExclusiveMinimum) ||
            !NullableNumberEqual(ExclusiveMaximum, other.ExclusiveMaximum) ||
            !NullableNumberEqual(MinLength, other.MinLength) ||
            !NullableNumberEqual(MaxLength, other.MaxLength) ||
            !NullableNumberEqual(MinItems, other.MinItems) ||
            !NullableNumberEqual(MaxItems, other.MaxItems))
            return false;

        if ((Items is null) != (other.Items is null) || (Items is not null && !Items.SemanticallyEquals(other.Items!)) || Properties.Count != other.Properties.Count)
            return false;
        foreach (var property in Properties)
            if (!other.Properties.TryGetValue(property.Key, out var otherProperty) || !property.Value.SemanticallyEquals(otherProperty))
                return false;
        return true;
    }

    private static bool NullableValuesEqual(IReadOnlyList<NormalizedJsonValue>? left, IReadOnlyList<NormalizedJsonValue>? right)
    {
        if (left is null || right is null)
            return left is null && right is null;
        return left.Count == right.Count && left.Zip(right, static (a, b) => a.SemanticallyEquals(b)).All(static equal => equal);
    }

    private static bool NullableNumberEqual(JsonNumber? left, JsonNumber? right) =>
        left.HasValue == right.HasValue && (!left.HasValue || left.Value.CompareTo(right!.Value) == 0);

    private static void WriteProperty<T>(StringBuilder builder, ref bool first, string name, T? value, Action<T, StringBuilder> writer)
        where T : class
    {
        if (value is null)
            return;
        WritePropertyName(builder, ref first, name);
        writer(value, builder);
    }

    private static void WriteValueProperty(StringBuilder builder, ref bool first, string name, NormalizedJsonValue value)
    {
        WritePropertyName(builder, ref first, name);
        value.Write(builder);
    }

    private static void WriteNumberProperty(StringBuilder builder, ref bool first, string name, JsonNumber? value)
    {
        if (!value.HasValue)
            return;
        WritePropertyName(builder, ref first, name);
        builder.Append(value.Value.ToCanonicalString());
    }

    private static void WritePropertyName(StringBuilder builder, ref bool first, string name)
    {
        if (!first)
            builder.Append(',');
        first = false;
        WriteString(builder, name);
        builder.Append(':');
    }

    private static void WriteString(StringBuilder builder, string value) => builder.Append(JsonSerializer.Serialize(value));
}

internal enum NormalizedJsonValueKind
{
    Null,
    Boolean,
    Number,
    String,
    Array,
    Object
}

internal sealed class NormalizedJsonValue
{
    private NormalizedJsonValue(NormalizedJsonValueKind kind, bool booleanValue, JsonNumber numberValue, string? stringValue, IReadOnlyList<NormalizedJsonValue>? arrayValue, IReadOnlyDictionary<string, NormalizedJsonValue>? objectValue)
    {
        Kind = kind;
        BooleanValue = booleanValue;
        NumberValue = numberValue;
        StringValue = stringValue;
        ArrayValue = arrayValue;
        ObjectValue = objectValue;
    }

    internal NormalizedJsonValueKind Kind { get; }
    internal bool BooleanValue { get; }
    internal JsonNumber NumberValue { get; }
    internal string? StringValue { get; }
    internal IReadOnlyList<NormalizedJsonValue>? ArrayValue { get; }
    internal IReadOnlyDictionary<string, NormalizedJsonValue>? ObjectValue { get; }

    internal static NormalizedJsonValue Null() => new(NormalizedJsonValueKind.Null, false, default, null, null, null);
    internal static NormalizedJsonValue Boolean(bool value) => new(NormalizedJsonValueKind.Boolean, value, default, null, null, null);
    internal static NormalizedJsonValue Number(JsonNumber value) => new(NormalizedJsonValueKind.Number, false, value, null, null, null);
    internal static NormalizedJsonValue String(string value) => new(NormalizedJsonValueKind.String, false, default, value, null, null);
    internal static NormalizedJsonValue Array(IReadOnlyList<NormalizedJsonValue> value) => new(NormalizedJsonValueKind.Array, false, default, null, value, null);
    internal static NormalizedJsonValue Object(IReadOnlyDictionary<string, NormalizedJsonValue> value) => new(NormalizedJsonValueKind.Object, false, default, null, null, value);

    internal bool SemanticallyEquals(NormalizedJsonValue other)
    {
        if (Kind != other.Kind)
            return false;
        switch (Kind)
        {
            case NormalizedJsonValueKind.Null:
                return true;
            case NormalizedJsonValueKind.Boolean:
                return BooleanValue == other.BooleanValue;
            case NormalizedJsonValueKind.Number:
                return NumberValue.CompareTo(other.NumberValue) == 0;
            case NormalizedJsonValueKind.String:
                return string.Equals(StringValue, other.StringValue, StringComparison.Ordinal);
            case NormalizedJsonValueKind.Array:
                return ArrayValue!.Count == other.ArrayValue!.Count && ArrayValue.Zip(other.ArrayValue, static (a, b) => a.SemanticallyEquals(b)).All(static equal => equal);
            case NormalizedJsonValueKind.Object:
                if (ObjectValue!.Count != other.ObjectValue!.Count)
                    return false;
                foreach (var property in ObjectValue)
                    if (!other.ObjectValue.TryGetValue(property.Key, out var otherProperty) || !property.Value.SemanticallyEquals(otherProperty))
                        return false;
                return true;
            default:
                return false;
        }
    }

    internal string ToCanonicalJson()
    {
        var builder = new StringBuilder();
        Write(builder);
        return builder.ToString();
    }

    internal void Write(StringBuilder builder)
    {
        switch (Kind)
        {
            case NormalizedJsonValueKind.Null:
                builder.Append("null");
                break;
            case NormalizedJsonValueKind.Boolean:
                builder.Append(BooleanValue ? "true" : "false");
                break;
            case NormalizedJsonValueKind.Number:
                builder.Append(NumberValue.ToCanonicalString());
                break;
            case NormalizedJsonValueKind.String:
                builder.Append(JsonSerializer.Serialize(StringValue));
                break;
            case NormalizedJsonValueKind.Array:
                builder.Append('[');
                for (var index = 0; index < ArrayValue!.Count; index++)
                {
                    if (index > 0)
                        builder.Append(',');
                    ArrayValue[index].Write(builder);
                }
                builder.Append(']');
                break;
            case NormalizedJsonValueKind.Object:
                builder.Append('{');
                var propertyIndex = 0;
                foreach (var property in ObjectValue!.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
                {
                    if (propertyIndex++ > 0)
                        builder.Append(',');
                    builder.Append(JsonSerializer.Serialize(property.Key));
                    builder.Append(':');
                    property.Value.Write(builder);
                }
                builder.Append('}');
                break;
            default:
                throw new InvalidOperationException("Unknown normalized JSON value kind.");
        }
    }
}

internal readonly struct JsonNumber
{
    internal JsonNumber(BigInteger coefficient, BigInteger exponent)
    {
        Coefficient = coefficient;
        Exponent = exponent;
    }

    internal BigInteger Coefficient { get; }
    internal BigInteger Exponent { get; }
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
        return prefix + digits + (Exponent.IsZero ? string.Empty : "e" + Exponent.ToString(CultureInfo.InvariantCulture));
    }

    private static int CompareMagnitude(JsonNumber left, JsonNumber right)
    {
        var leftDigits = BigInteger.Abs(left.Coefficient).ToString(CultureInfo.InvariantCulture).Length;
        var rightDigits = BigInteger.Abs(right.Coefficient).ToString(CultureInfo.InvariantCulture).Length;
        var leftMagnitude = new BigInteger(leftDigits) + left.Exponent;
        var rightMagnitude = new BigInteger(rightDigits) + right.Exponent;
        var magnitudeComparison = leftMagnitude.CompareTo(rightMagnitude);
        if (magnitudeComparison != 0)
            return magnitudeComparison;

        var leftValue = BigInteger.Abs(left.Coefficient);
        var rightValue = BigInteger.Abs(right.Coefficient);
        if (left.Exponent > right.Exponent)
        {
            var power = left.Exponent - right.Exponent;
            leftValue *= BigInteger.Pow(10, power <= int.MaxValue ? (int)power : throw new OverflowException("The numeric representation exceeds the supported comparison range."));
        }
        else if (right.Exponent > left.Exponent)
        {
            var power = right.Exponent - left.Exponent;
            rightValue *= BigInteger.Pow(10, power <= int.MaxValue ? (int)power : throw new OverflowException("The numeric representation exceeds the supported comparison range."));
        }
        return leftValue.CompareTo(rightValue);
    }
}

internal static class SchemaNormalizer
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

    internal static NormalizedSchema Normalize(JsonElement schema, AiToolContractLimits limits, AiToolDiagnosticKind malformedKind = AiToolDiagnosticKind.CaptureFailure)
    {
        if (Encoding.UTF8.GetByteCount(schema.GetRawText()) > limits.MaxSchemaBytes)
            throw Resource("The JSON Schema exceeds the configured byte limit.");
        return new Normalizer(limits, malformedKind).NormalizeSchema(schema, "$", 0);
    }

    internal static NormalizedSchema Normalize(string raw, AiToolContractLimits limits)
    {
        if (raw is null)
            throw Malformed("A schema value is null.");
        if (Encoding.UTF8.GetByteCount(raw) > limits.MaxSchemaBytes)
            throw Resource("The JSON Schema exceeds the configured byte limit.");
        try
        {
            using var document = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = limits.MaxSchemaDepth, CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
            return new Normalizer(limits, AiToolDiagnosticKind.MalformedBaseline).NormalizeSchema(document.RootElement, "$", 0);
        }
        catch (AiToolContractException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw new AiToolContractException(new AiToolContractDiagnostic(IsDepthLimit(ex) ? AiToolDiagnosticKind.CanonicalizationOrResourceLimit : AiToolDiagnosticKind.MalformedBaseline, "The JSON Schema is " + (IsDepthLimit(ex) ? "too deep" : "malformed") + ": " + ex.Message));
        }
        catch (OverflowException ex)
        {
            throw Resource("The JSON Schema numeric representation exceeds the configured resource boundary: " + ex.Message);
        }
    }

    private static AiToolContractException Malformed(string message) =>
        new(new AiToolContractDiagnostic(AiToolDiagnosticKind.MalformedBaseline, message));

    private static AiToolContractException Resource(string message) =>
        new(new AiToolContractDiagnostic(AiToolDiagnosticKind.CanonicalizationOrResourceLimit, message));

    private static AiToolContractException Unsupported(string message) =>
        new(new AiToolContractDiagnostic(AiToolDiagnosticKind.UnsupportedClassification, message));

    private static bool IsDepthLimit(JsonException exception) =>
        exception.Message.Contains("depth", StringComparison.OrdinalIgnoreCase) || exception.Message.Contains("maximum", StringComparison.OrdinalIgnoreCase);

    private sealed class Normalizer
    {
        private readonly AiToolContractLimits _limits;
        private readonly AiToolDiagnosticKind _malformedKind;
        private int _propertyCount;
        private int _arrayItemCount;

        internal Normalizer(AiToolContractLimits limits, AiToolDiagnosticKind malformedKind)
        {
            _limits = limits;
            _malformedKind = malformedKind;
        }

        internal NormalizedSchema NormalizeSchema(JsonElement schema, string path, int depth)
        {
            EnsureDepth(depth, path);
            if (schema.ValueKind is JsonValueKind.True or JsonValueKind.False)
                throw Unsupported("Boolean JSON Schema at " + path + " is valid but is not represented by the closed normalized model.");
            if (schema.ValueKind != JsonValueKind.Object)
                throw MalformedAt(path + " must be a schema object.");

            var members = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in schema.EnumerateObject())
            {
                if (members.ContainsKey(property.Name))
                    throw MalformedAt("The JSON Schema contains duplicate object property '" + property.Name + "' at " + path + ".");
                members.Add(property.Name, property.Value);
                if (++_propertyCount > _limits.MaxProperties)
                    throw Resource("The JSON Schema exceeds the configured property limit.");
            }

            string? reference = null;
            string? description = null;
            var hasDefault = false;
            NormalizedJsonValue? defaultValue = null;
            string? format = null;
            var types = new List<string>();
            var properties = new Dictionary<string, NormalizedSchema>(StringComparer.Ordinal);
            var required = new List<string>();
            IReadOnlyList<NormalizedJsonValue>? enumValues = null;
            NormalizedSchema? items = null;
            JsonNumber? minimum = null;
            JsonNumber? maximum = null;
            JsonNumber? exclusiveMinimum = null;
            JsonNumber? exclusiveMaximum = null;
            JsonNumber? minLength = null;
            JsonNumber? maxLength = null;
            JsonNumber? minItems = null;
            JsonNumber? maxItems = null;

            foreach (var member in members)
            {
                var memberPath = path + "." + member.Key;
                switch (member.Key)
                {
                    case "$ref":
                        Require(member.Value, JsonValueKind.String, memberPath);
                        reference = member.Value.GetString();
                        if (!Uri.TryCreate(reference, UriKind.RelativeOrAbsolute, out _))
                            throw MalformedAt("JSON Schema $ref at " + memberPath + " is not a valid URI-reference.");
                        break;
                    case "description":
                        Require(member.Value, JsonValueKind.String, memberPath);
                        description = member.Value.GetString();
                        break;
                    case "default":
                        hasDefault = true;
                        defaultValue = NormalizeValue(member.Value, memberPath, depth + 1);
                        break;
                    case "format":
                        Require(member.Value, JsonValueKind.String, memberPath);
                        format = member.Value.GetString();
                        break;
                    case "type":
                        types = NormalizeTypes(member.Value, memberPath);
                        break;
                    case "enum":
                        enumValues = NormalizeEnum(member.Value, memberPath, depth + 1);
                        break;
                    case "items":
                        if (member.Value.ValueKind == JsonValueKind.Array)
                            throw Unsupported("Tuple-form items at " + memberPath + " is not represented by the closed normalized model.");
                        if (member.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                            throw Unsupported("Boolean items at " + memberPath + " is not represented by the closed normalized model.");
                        items = NormalizeSchema(member.Value, memberPath, depth + 1);
                        break;
                    case "properties":
                        Require(member.Value, JsonValueKind.Object, memberPath);
                        foreach (var property in member.Value.EnumerateObject())
                        {
                            if (properties.ContainsKey(property.Name))
                                throw MalformedAt("The JSON Schema contains duplicate property '" + property.Name + "' at " + memberPath + ".");
                            properties.Add(property.Name, NormalizeSchema(property.Value, memberPath + "." + property.Name, depth + 1));
                        }
                        break;
                    case "required":
                        required = NormalizeStringArray(member.Value, memberPath, "required");
                        break;
                    case "minimum":
                        minimum = NormalizeNumber(member.Value, memberPath);
                        break;
                    case "maximum":
                        maximum = NormalizeNumber(member.Value, memberPath);
                        break;
                    case "exclusiveMinimum":
                        exclusiveMinimum = NormalizeNumber(member.Value, memberPath);
                        break;
                    case "exclusiveMaximum":
                        exclusiveMaximum = NormalizeNumber(member.Value, memberPath);
                        break;
                    case "minLength":
                        minLength = NormalizeNonNegativeInteger(member.Value, memberPath);
                        break;
                    case "maxLength":
                        maxLength = NormalizeNonNegativeInteger(member.Value, memberPath);
                        break;
                    case "minItems":
                        minItems = NormalizeNonNegativeInteger(member.Value, memberPath);
                        break;
                    case "maxItems":
                        maxItems = NormalizeNonNegativeInteger(member.Value, memberPath);
                        break;
                    default:
                        throw Unsupported("Schema keyword '" + member.Key + "' at " + memberPath + " is outside the closed normalized model and requires review.");
                }
            }

            types.Sort(StringComparer.Ordinal);
            required.Sort(StringComparer.Ordinal);
            if (enumValues is List<NormalizedJsonValue> values)
                values.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.ToCanonicalJson(), right.ToCanonicalJson()));
            return new NormalizedSchema(reference, description, hasDefault, defaultValue, format, types, properties, required, enumValues, items, minimum, maximum, exclusiveMinimum, exclusiveMaximum, minLength, maxLength, minItems, maxItems);
        }

        private List<NormalizedJsonValue> NormalizeEnum(JsonElement value, string path, int depth)
        {
            Require(value, JsonValueKind.Array, path);
            if (value.GetArrayLength() == 0)
                throw MalformedAt("JSON Schema enum at " + path + " must contain at least one value.");
            var values = new List<NormalizedJsonValue>();
            foreach (var item in value.EnumerateArray())
            {
                if (++_arrayItemCount > _limits.MaxArrayItems)
                    throw Resource("The JSON Schema exceeds the configured array-item limit.");
                values.Add(NormalizeValue(item, path + "[]", depth));
            }
            for (var left = 0; left < values.Count; left++)
                for (var right = left + 1; right < values.Count; right++)
                    if (values[left].SemanticallyEquals(values[right]))
                        throw MalformedAt("JSON Schema enum at " + path + " contains duplicate data-model values.");
            return values;
        }

        private List<string> NormalizeTypes(JsonElement value, string path)
        {
            var values = new List<string>();
            if (value.ValueKind == JsonValueKind.String)
            {
                AddType(value.GetString()!, path, values);
                return values;
            }
            Require(value, JsonValueKind.Array, path);
            if (value.GetArrayLength() == 0)
                throw MalformedAt("JSON Schema type at " + path + " must contain at least one type.");
            foreach (var item in value.EnumerateArray())
            {
                if (++_arrayItemCount > _limits.MaxArrayItems)
                    throw Resource("The JSON Schema exceeds the configured array-item limit.");
                Require(item, JsonValueKind.String, path + "[]");
                AddType(item.GetString()!, path, values);
            }
            return values;
        }

        private void AddType(string value, string path, List<string> values)
        {
            if (!JsonSchemaTypes.Contains(value))
                throw MalformedAt("JSON Schema type '" + value + "' at " + path + " is not recognized.");
            if (values.Contains(value, StringComparer.Ordinal))
                throw MalformedAt("JSON Schema type at " + path + " contains duplicate values.");
            values.Add(value);
        }

        private List<string> NormalizeStringArray(JsonElement value, string path, string name)
        {
            Require(value, JsonValueKind.Array, path);
            if (value.GetArrayLength() == 0)
                throw MalformedAt("JSON Schema " + name + " at " + path + " must contain at least one value.");
            var values = new List<string>();
            foreach (var item in value.EnumerateArray())
            {
                if (++_arrayItemCount > _limits.MaxArrayItems)
                    throw Resource("The JSON Schema exceeds the configured array-item limit.");
                Require(item, JsonValueKind.String, path + "[]");
                var stringValue = item.GetString()!;
                if (!values.Contains(stringValue, StringComparer.Ordinal))
                    values.Add(stringValue);
                else
                    throw MalformedAt("JSON Schema array at " + path + " contains duplicate values.");
            }
            return values;
        }

        private JsonNumber NormalizeNumber(JsonElement value, string path)
        {
            Require(value, JsonValueKind.Number, path);
            if (!TryParseNumber(value.GetRawText(), out var number))
                throw Resource("JSON Schema number at " + path + " exceeds the supported numeric representation boundary.");
            return number;
        }

        private JsonNumber NormalizeNonNegativeInteger(JsonElement value, string path)
        {
            var number = NormalizeNumber(value, path);
            if (!number.IsInteger || number.IsNegative)
                throw MalformedAt("JSON Schema value at " + path + " must be a non-negative integer.");
            return number;
        }

        private NormalizedJsonValue NormalizeValue(JsonElement value, string path, int depth)
        {
            EnsureDepth(depth, path);
            switch (value.ValueKind)
            {
                case JsonValueKind.Null:
                    return NormalizedJsonValue.Null();
                case JsonValueKind.True:
                    return NormalizedJsonValue.Boolean(true);
                case JsonValueKind.False:
                    return NormalizedJsonValue.Boolean(false);
                case JsonValueKind.Number:
                    return NormalizedJsonValue.Number(NormalizeNumber(value, path));
                case JsonValueKind.String:
                    return NormalizedJsonValue.String(value.GetString()!);
                case JsonValueKind.Array:
                    var array = new List<NormalizedJsonValue>();
                    foreach (var item in value.EnumerateArray())
                    {
                        if (++_arrayItemCount > _limits.MaxArrayItems)
                            throw Resource("The JSON Schema exceeds the configured array-item limit.");
                        array.Add(NormalizeValue(item, path + "[]", depth + 1));
                    }
                    return NormalizedJsonValue.Array(array);
                case JsonValueKind.Object:
                    var properties = new Dictionary<string, NormalizedJsonValue>(StringComparer.Ordinal);
                    foreach (var property in value.EnumerateObject())
                    {
                        if (++_propertyCount > _limits.MaxProperties)
                            throw Resource("The JSON Schema exceeds the configured property limit.");
                        if (properties.ContainsKey(property.Name))
                            throw MalformedAt("The JSON Schema contains duplicate object property '" + property.Name + "' at " + path + ".");
                        properties.Add(property.Name, NormalizeValue(property.Value, path + "." + property.Name, depth + 1));
                    }
                    return NormalizedJsonValue.Object(properties);
                default:
                    throw MalformedAt("JSON value at " + path + " is not supported.");
            }
        }

        private void EnsureDepth(int depth, string path)
        {
            if (depth >= _limits.MaxSchemaDepth)
                throw Resource("The JSON Schema exceeds the configured depth limit at " + path + ".");
        }

        private void Require(JsonElement value, JsonValueKind expected, string path)
        {
            if (value.ValueKind != expected)
                throw MalformedAt("JSON Schema value at " + path + " must be " + expected.ToString().ToLowerInvariant() + ".");
        }

        private AiToolContractException MalformedAt(string message) =>
            new(new AiToolContractDiagnostic(_malformedKind, message));
    }

    private static bool TryParseNumber(string raw, out JsonNumber number)
    {
        number = default;
        if (string.IsNullOrEmpty(raw))
            return false;

        var exponentIndex = raw.IndexOf('e');
        if (exponentIndex < 0)
            exponentIndex = raw.IndexOf('E');
        var significandEnd = exponentIndex >= 0 ? exponentIndex : raw.Length;
        var exponent = BigInteger.Zero;
        if (exponentIndex >= 0)
        {
            var exponentText = raw.Substring(exponentIndex + 1);
            if (!BigInteger.TryParse(exponentText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent))
                return false;
        }

        var significand = raw.Substring(0, significandEnd);
        var negative = significand[0] == '-';
        var digitsStart = negative ? 1 : 0;
        var decimalIndex = significand.IndexOf('.');
        var fractionLength = decimalIndex >= 0 ? significand.Length - decimalIndex - 1 : 0;
        var digitsBuilder = new StringBuilder();
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
        if (digits.Length == 0 || !BigInteger.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var coefficient))
            return false;
        if (negative)
            coefficient = -coefficient;

        exponent -= fractionLength;
        while (!coefficient.IsZero && coefficient % 10 == 0)
        {
            coefficient /= 10;
            exponent += 1;
        }
        number = new JsonNumber(coefficient, coefficient.IsZero ? BigInteger.Zero : exponent);
        return true;
    }
}
