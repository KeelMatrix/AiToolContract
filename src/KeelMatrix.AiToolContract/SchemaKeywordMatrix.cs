namespace KeelMatrix.AiToolContract;

internal enum SchemaKeywordValueShape
{
    SchemaMap,
    Reference,
    SchemaObject,
    SchemaOrBoolean,
    SchemaOrBooleanNoTuple,
    NonEmptySchemaArray,
    EnumArray,
    NonEmptyStringArray,
    Type,
    Boolean,
    Number,
    NonNegativeInteger
}

internal enum SchemaKeywordChangeHandling
{
    Classified,
    Unsupported
}

internal sealed class SchemaKeywordDefinition
{
    internal SchemaKeywordDefinition(
        string keyword,
        SchemaKeywordValueShape valueShape,
        SchemaKeywordChangeHandling changeHandling,
        string validValueJson,
        string changedValueJson,
        string malformedValueJson)
    {
        Keyword = keyword;
        ValueShape = valueShape;
        ChangeHandling = changeHandling;
        ValidValueJson = validValueJson;
        ChangedValueJson = changedValueJson;
        MalformedValueJson = malformedValueJson;
    }

    internal string Keyword { get; }
    internal SchemaKeywordValueShape ValueShape { get; }
    internal SchemaKeywordChangeHandling ChangeHandling { get; }
    internal string ValidValueJson { get; }
    internal string ChangedValueJson { get; }
    internal string MalformedValueJson { get; }
}

internal static class SchemaKeywordMatrix
{
    internal static IReadOnlyList<SchemaKeywordDefinition> All { get; } = new[]
    {
        new SchemaKeywordDefinition("$defs", SchemaKeywordValueShape.SchemaMap, SchemaKeywordChangeHandling.Unsupported, "{\"Order\":{\"type\":\"string\"}}", "{\"Order\":{\"type\":\"integer\"}}", "[]"),
        new SchemaKeywordDefinition("$ref", SchemaKeywordValueShape.Reference, SchemaKeywordChangeHandling.Unsupported, "\"#/$defs/Order\"", "\"#/$defs/Other\"", "1"),
        new SchemaKeywordDefinition("additionalProperties", SchemaKeywordValueShape.SchemaOrBoolean, SchemaKeywordChangeHandling.Unsupported, "{\"type\":\"string\"}", "{\"type\":\"integer\"}", "1"),
        new SchemaKeywordDefinition("allOf", SchemaKeywordValueShape.NonEmptySchemaArray, SchemaKeywordChangeHandling.Unsupported, "[{\"type\":\"string\"}]", "[{\"type\":\"integer\"}]", "{}"),
        new SchemaKeywordDefinition("anyOf", SchemaKeywordValueShape.NonEmptySchemaArray, SchemaKeywordChangeHandling.Unsupported, "[{\"type\":\"string\"}]", "[{\"type\":\"integer\"}]", "{}"),
        new SchemaKeywordDefinition("contains", SchemaKeywordValueShape.SchemaOrBoolean, SchemaKeywordChangeHandling.Unsupported, "{\"type\":\"string\"}", "{\"type\":\"integer\"}", "1"),
        new SchemaKeywordDefinition("definitions", SchemaKeywordValueShape.SchemaMap, SchemaKeywordChangeHandling.Unsupported, "{\"Order\":{\"type\":\"string\"}}", "{\"Order\":{\"type\":\"integer\"}}", "[]"),
        new SchemaKeywordDefinition("enum", SchemaKeywordValueShape.EnumArray, SchemaKeywordChangeHandling.Classified, "[1,\"one\"]", "[1,\"one\",2]", "{}"),
        new SchemaKeywordDefinition("exclusiveMaximum", SchemaKeywordValueShape.Number, SchemaKeywordChangeHandling.Classified, "1", "2", "\"1\""),
        new SchemaKeywordDefinition("exclusiveMinimum", SchemaKeywordValueShape.Number, SchemaKeywordChangeHandling.Classified, "1", "2", "\"1\""),
        new SchemaKeywordDefinition("items", SchemaKeywordValueShape.SchemaOrBooleanNoTuple, SchemaKeywordChangeHandling.Classified, "{\"type\":\"string\"}", "{\"type\":\"integer\"}", "[]"),
        new SchemaKeywordDefinition("maxItems", SchemaKeywordValueShape.NonNegativeInteger, SchemaKeywordChangeHandling.Classified, "1", "2", "-1"),
        new SchemaKeywordDefinition("maxLength", SchemaKeywordValueShape.NonNegativeInteger, SchemaKeywordChangeHandling.Classified, "1", "2", "-1"),
        new SchemaKeywordDefinition("maximum", SchemaKeywordValueShape.Number, SchemaKeywordChangeHandling.Classified, "1", "2", "\"1\""),
        new SchemaKeywordDefinition("minItems", SchemaKeywordValueShape.NonNegativeInteger, SchemaKeywordChangeHandling.Classified, "1", "2", "-1"),
        new SchemaKeywordDefinition("minLength", SchemaKeywordValueShape.NonNegativeInteger, SchemaKeywordChangeHandling.Classified, "1", "2", "-1"),
        new SchemaKeywordDefinition("minimum", SchemaKeywordValueShape.Number, SchemaKeywordChangeHandling.Classified, "1", "2", "\"1\""),
        new SchemaKeywordDefinition("not", SchemaKeywordValueShape.SchemaOrBoolean, SchemaKeywordChangeHandling.Unsupported, "{\"type\":\"string\"}", "{\"type\":\"integer\"}", "1"),
        new SchemaKeywordDefinition("nullable", SchemaKeywordValueShape.Boolean, SchemaKeywordChangeHandling.Classified, "true", "false", "1"),
        new SchemaKeywordDefinition("oneOf", SchemaKeywordValueShape.NonEmptySchemaArray, SchemaKeywordChangeHandling.Unsupported, "[{\"type\":\"string\"}]", "[{\"type\":\"integer\"}]", "{}"),
        new SchemaKeywordDefinition("properties", SchemaKeywordValueShape.SchemaMap, SchemaKeywordChangeHandling.Classified, "{\"nested\":{\"type\":\"string\"}}", "{\"nested\":{\"type\":\"integer\"}}", "[]"),
        new SchemaKeywordDefinition("required", SchemaKeywordValueShape.NonEmptyStringArray, SchemaKeywordChangeHandling.Classified, "[\"nested\"]", "[\"other\"]", "[1]"),
        new SchemaKeywordDefinition("type", SchemaKeywordValueShape.Type, SchemaKeywordChangeHandling.Classified, "[\"string\",\"null\"]", "[\"integer\",\"null\"]", "[1]"),
    };

    internal static SchemaKeywordDefinition Get(string keyword) =>
        All.Single(definition => string.Equals(definition.Keyword, keyword, StringComparison.Ordinal));

    internal static bool TryGet(string keyword, out SchemaKeywordDefinition? definition)
    {
        definition = All.FirstOrDefault(candidate => string.Equals(candidate.Keyword, keyword, StringComparison.Ordinal));
        return definition is not null;
    }
}
