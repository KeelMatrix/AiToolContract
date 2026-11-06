using System.Text.Json;
using Microsoft.Extensions.AI;
using Xunit;

namespace KeelMatrix.AiToolContract.Tests;

public sealed class CompatibilityRegressionTests
{
    private static readonly int[] OldEnum = { 1 };
    private static readonly int[] NewEnum = { 1, 2 };
    // Each witness is a newly permitted output that an existing consumer cannot handle.
    public static IEnumerable<object[]> LostGuarantees()
    {
        yield return new object[] { "{\"type\":\"string\"}", "{\"type\":[\"string\",\"null\"]}", "null" };
        yield return new object[] { "{\"type\":\"integer\"}", "{\"type\":\"number\"}", "1.5" };
        yield return new object[] { "{\"type\":\"string\"}", "{}", "false" };
        yield return new object[] { "{\"required\":[\"id\"]}", "{}", "{}" };
        yield return new object[] { "{\"enum\":[\"ok\"]}", "{\"enum\":[\"ok\",\"error\"]}", "\"error\"" };
        yield return new object[] { "{\"enum\":[\"ok\"]}", "{}", "42" };
        foreach (var bound in new[] { "maximum", "exclusiveMaximum", "maxLength", "maxItems" })
            yield return new object[] { "{\"" + bound + "\":10}", "{\"" + bound + "\":20}", bound.Contains("Length", StringComparison.Ordinal) ? "\"abcdefghijk\"" : bound.Contains("Items", StringComparison.Ordinal) ? "[0,0,0,0,0,0,0,0,0,0,0]" : "11" };
        foreach (var bound in new[] { "minimum", "exclusiveMinimum", "minLength", "minItems" })
            yield return new object[] { "{\"" + bound + "\":10}", "{\"" + bound + "\":2}", bound.Contains("Length", StringComparison.Ordinal) ? "\"abc\"" : bound.Contains("Items", StringComparison.Ordinal) ? "[0,0,0]" : "3" };
        foreach (var bound in new[] { "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "minLength", "maxLength", "minItems", "maxItems" })
            yield return new object[] { "{\"" + bound + "\":10}", "{}", bound == "minLength" ? "\"\"" : bound == "maxLength" ? "\"abcdefghijk\"" : bound == "minItems" ? "[]" : bound == "maxItems" ? "[0,0,0,0,0,0,0,0,0,0,0]" : bound is "minimum" or "exclusiveMinimum" ? "0" : "11" };
    }

    [Theory]
    [MemberData(nameof(LostGuarantees))]
    public void OutputGuaranteesAndInputAcceptanceHaveOppositeDirections(string oldSchema, string newSchema, string witness)
    {
        Assert.False(Allows(oldSchema, witness));
        Assert.True(Allows(newSchema, witness));
        foreach (var wrap in new Func<string, string>[] { static s => s, static s => "{\"properties\":{\"result\":" + s + "}}", static s => "{\"items\":" + s + "}" })
        {
            var previous = Capture("{}", wrap(oldSchema));
            var candidate = Capture("{}", wrap(newSchema));
            var diff = AiToolContractVerifier.Compare(previous, candidate);
            Assert.False(diff.IsClean, witness);
            Assert.Equal(AiToolCompatibility.Breaking, diff.Compatibility);
            Assert.All(diff.Changes, c => Assert.Equal(AiToolChangeKind.ReturnSchemaBreaking, c.Kind));
            Assert.Throws<AiToolContractException>(() => AiToolContractVerifier.Accept(candidate, diff));
            Assert.Same(candidate, AiToolContractVerifier.AcceptWithBreakingReview(candidate, diff));
            var reverse = AiToolContractVerifier.Compare(candidate, previous);
            Assert.Equal(AiToolCompatibility.Additive, reverse.Compatibility);
            Assert.Same(previous, AiToolContractVerifier.Accept(previous, reverse));

            var inputDiff = AiToolContractVerifier.Compare(Capture(wrap(oldSchema)), Capture(wrap(newSchema)));
            Assert.Equal(AiToolCompatibility.Additive, inputDiff.Compatibility);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReturnPropertyGuaranteesComposeWithNestedWidening(bool required)
    {
        var previous = Capture("{}", "{\"properties\":{\"id\":{\"type\":\"integer\"}},\"required\":[\"id\"]}");
        var nextSchema = "{\"properties\":{\"id\":{\"type\":\"integer\"},\"name\":{\"type\":\"string\"}}" + (required ? ",\"required\":[\"id\",\"name\"]" : ",\"required\":[\"id\"]") + "}";
        var candidate = Capture("{}", nextSchema);
        var diff = AiToolContractVerifier.Compare(previous, candidate);
        Assert.Equal(AiToolCompatibility.Additive, diff.Compatibility);
        Assert.Same(candidate, AiToolContractVerifier.Accept(candidate, diff));
        Assert.Equal(AiToolCompatibility.Breaking, AiToolContractVerifier.Compare(candidate, previous).Compatibility);
        var widened = Capture("{}", nextSchema.Replace("\"integer\"", "\"number\"", StringComparison.Ordinal));
        Assert.Equal(AiToolCompatibility.Breaking, AiToolContractVerifier.Compare(previous, widened).Compatibility);
    }

    // Independent instance checks for the finite witness vocabulary above. These
    // expectations do not read the normalized model or the production rule table.
    private static bool Allows(string schemaJson, string instanceJson)
    {
        using var schemaDocument = JsonDocument.Parse(schemaJson);
        using var instanceDocument = JsonDocument.Parse(instanceJson);
        var value = instanceDocument.RootElement;
        foreach (var keyword in schemaDocument.RootElement.EnumerateObject())
        {
            var constraint = keyword.Value;
            switch (keyword.Name)
            {
                case "type":
                    var types = constraint.ValueKind == JsonValueKind.Array ? constraint.EnumerateArray().Select(t => t.GetString()).ToArray() : new[] { constraint.GetString() };
                    if (!types.Any(t => t switch
                    {
                        "null" => value.ValueKind == JsonValueKind.Null,
                        "string" => value.ValueKind == JsonValueKind.String,
                        "number" => value.ValueKind == JsonValueKind.Number,
                        "integer" => value.ValueKind == JsonValueKind.Number && value.GetDecimal() % 1 == 0,
                        _ => throw new InvalidOperationException("Unknown witness type.")
                    })) return false;
                    break;
                case "enum":
                    if (!constraint.EnumerateArray().Any(v => v.GetRawText() == value.GetRawText())) return false;
                    break;
                case "required":
                    if (value.ValueKind == JsonValueKind.Object && constraint.EnumerateArray().Any(n => !value.TryGetProperty(n.GetString()!, out _))) return false;
                    break;
                case "minimum": if (value.ValueKind == JsonValueKind.Number && value.GetDecimal() < constraint.GetDecimal()) return false; break;
                case "maximum": if (value.ValueKind == JsonValueKind.Number && value.GetDecimal() > constraint.GetDecimal()) return false; break;
                case "exclusiveMinimum": if (value.ValueKind == JsonValueKind.Number && value.GetDecimal() <= constraint.GetDecimal()) return false; break;
                case "exclusiveMaximum": if (value.ValueKind == JsonValueKind.Number && value.GetDecimal() >= constraint.GetDecimal()) return false; break;
                case "minLength": if (value.ValueKind == JsonValueKind.String && value.GetString()!.Length < constraint.GetInt32()) return false; break;
                case "maxLength": if (value.ValueKind == JsonValueKind.String && value.GetString()!.Length > constraint.GetInt32()) return false; break;
                case "minItems": if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() < constraint.GetInt32()) return false; break;
                case "maxItems": if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > constraint.GetInt32()) return false; break;
                default: throw new InvalidOperationException("Unknown witness keyword.");
            }
        }
        return true;
    }

    public static IEnumerable<object[]> Compositions()
    {
        yield return new object[] { "{}", "{\"properties\":{\"a\":{},\"b\":{}}}" };
        yield return new object[] { "{\"properties\":{\"a\":{},\"b\":{}}}", "{}" };
        yield return new object[] { "{\"properties\":{\"a\":{\"type\":\"string\"}}}", "{\"properties\":{\"a\":{\"type\":[\"string\",\"null\"]},\"b\":{},\"c\":{}},\"required\":[\"b\"]}" };
        yield return new object[] { "{\"properties\":{\"a\":{},\"b\":{\"enum\":[1]}}}", "{\"properties\":{\"b\":{\"enum\":[1,2]}}}" };
        yield return new object[] { "{\"required\":[\"a\",\"b\"]}", "{\"required\":[\"c\",\"d\"]}" };
    }

    [Theory]
    [MemberData(nameof(Compositions))]
    public void SupportedMemberChangesCompose(string oldSchema, string newSchema)
    {
        foreach (var output in new[] { false, true })
            foreach (var wrap in new Func<string, string>[] { static s => s, static s => "{\"properties\":{\"nested\":" + s + "}}", static s => "{\"items\":" + s + "}" })
            {
                var baseline = output ? Capture("{}", wrap(oldSchema)) : Capture(wrap(oldSchema));
                var candidate = output ? Capture("{}", wrap(newSchema)) : Capture(wrap(newSchema));
                var diff = AiToolContractVerifier.Compare(baseline, candidate);
                Assert.DoesNotContain(diff.Changes, c => c.Kind == AiToolChangeKind.Unsupported);
                Assert.DoesNotContain(diff.Diagnostics, d => d.Kind == AiToolDiagnosticKind.UnsupportedClassification);
                Assert.Same(candidate, AiToolContractVerifier.AcceptWithBreakingReview(candidate, diff));
                foreach (var schemaPair in new[] { (baseline.Tools[0].InputSchema, candidate.Tools[0].InputSchema), (baseline.Tools[0].ReturnSchema, candidate.Tools[0].ReturnSchema) })
                    if (schemaPair.Item1 is not null && schemaPair.Item2 is not null)
                        AssertCovered(schemaPair.Item1, schemaPair.Item2);
            }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcceptanceRejectsUnrelatedStaleReversedAndReparsedEvidence(bool breakingReview)
    {
        var baseline = Capture("{}");
        var breaking = Capture("{\"required\":[\"id\"]}");
        var unsupported = Capture("{\"$ref\":\"#/other\"}");
        foreach (var candidate in new[] { breaking, unsupported, Capture("{}"), AiToolContractJson.Parse(AiToolContractJson.Serialize(baseline)) })
        {
            var clean = AiToolContractVerifier.Compare(baseline, baseline);
            Assert.Throws<AiToolContractException>(() => Accept(candidate, clean, breakingReview));
        }
        var reversed = AiToolContractVerifier.Compare(breaking, baseline);
        Assert.Throws<AiToolContractException>(() => Accept(breaking, reversed, breakingReview));
        var matched = AiToolContractVerifier.Compare(baseline, breaking);
        if (breakingReview)
            Assert.Same(breaking, Accept(breaking, matched, true));
        else
            Assert.Throws<AiToolContractException>(() => Accept(breaking, matched, false));
        Assert.Same(baseline, Accept(baseline, AiToolContractVerifier.Compare(baseline, baseline), breakingReview));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DynamicPropertyPathsAreUnambiguous(bool output)
    {
        var names = new[] { "a.properties.b", "a", "", "[]", "\"", "\\", "/", "~", "\n", "é" };
        var properties = names.ToDictionary(n => n, _ => new { @enum = OldEnum });
        var nextProperties = names.ToDictionary(n => n, _ => new { @enum = NewEnum });
        var oldSchema = JsonSerializer.Serialize(new { properties });
        var newSchema = JsonSerializer.Serialize(new { properties = nextProperties });
        var baseline = output ? Capture("{}", oldSchema) : Capture(oldSchema);
        var candidate = output ? Capture("{}", newSchema) : Capture(newSchema);
        var diff = AiToolContractVerifier.Compare(baseline, candidate);
        Assert.Equal(names.Length, diff.Changes.Select(c => c.Path).Distinct(StringComparer.Ordinal).Count());
        foreach (var name in names)
            Assert.Contains(diff.Changes, c => c.Path == (output ? "$.returnSchema" : "$") + ".properties[" + JsonSerializer.Serialize(name) + "].enum");
    }

    private static void AssertCovered(NormalizedSchema before, NormalizedSchema after)
    {
        foreach (var transition in SchemaTransitionRules.FindTransitions(before, after))
            Assert.True(SchemaTransitionRules.TryGetRule(transition.PropertyName, transition.Direction, out _), transition.PropertyName + ":" + transition.Direction);
        foreach (var property in before.Properties)
            if (after.Properties.TryGetValue(property.Key, out var next))
                AssertCovered(property.Value, next);
        if (before.Items is not null && after.Items is not null)
            AssertCovered(before.Items, after.Items);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LiteralAndNestedPropertyLocationsHaveDistinctPaths(bool output)
    {
        const string before = "{\"properties\":{\"a.properties.b\":{\"enum\":[1]},\"a\":{\"properties\":{\"b\":{\"enum\":[1]}}}}}";
        var after = before.Replace("[1]", "[1,2]", StringComparison.Ordinal);
        var baseline = output ? Capture("{}", before) : Capture(before);
        var candidate = output ? Capture("{}", after) : Capture(after);
        var changes = AiToolContractVerifier.Compare(baseline, candidate).Changes;
        Assert.Equal(2, changes.Count);
        Assert.Equal(2, changes.Select(c => c.Path).Distinct(StringComparer.Ordinal).Count());
        var root = output ? "$.returnSchema" : "$";
        Assert.Contains(changes, c => c.Path == root + ".properties[\"a.properties.b\"].enum");
        Assert.Contains(changes, c => c.Path == root + ".properties[\"a\"].properties[\"b\"].enum");
    }

    [Theory]
    [InlineData("type", "\"string\"", "[\"string\",\"null\"]")]
    [InlineData("enum", "[1]", "[1,2]")]
    [InlineData("minimum", "1", "2")]
    [InlineData("description", "\"before\"", "\"after\"")]
    [InlineData("default", "1", "2")]
    [InlineData("format", "\"date\"", "\"time\"")]
    [InlineData("$ref", "\"#/before\"", "\"#/after\"")]
    public void AllChangeFamiliesUseQuotedDynamicSegments(string keyword, string before, string after)
    {
        const string name = "a.[]\"/\\~\n";
        foreach (var output in new[] { false, true })
        {
            string Schema(string value) => "{\"properties\":{" + JsonSerializer.Serialize(name) + ":{\"" + keyword + "\":" + value + "}}}";
            var baseline = output ? Capture("{}", Schema(before)) : Capture(Schema(before));
            var candidate = output ? Capture("{}", Schema(after)) : Capture(Schema(after));
            var change = Assert.Single(AiToolContractVerifier.Compare(baseline, candidate).Changes);
            Assert.Equal((output ? "$.returnSchema" : "$") + ".properties[" + JsonSerializer.Serialize(name) + "]." + keyword, change.Path);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CaptureAndParseDiagnosticsPreserveQuotedPathsAndSchemaRole(bool output)
    {
        const string schema = "{\"properties\":{\"a.b\":{\"default\":{\"[]\":[[0]]}}}}";
        using var inputDocument = JsonDocument.Parse(output ? "{}" : schema);
        using var outputDocument = output ? JsonDocument.Parse(schema) : null;
        var limits = new AiToolContractLimits { MaxSchemaDepth = 5 };
        var result = AiToolContractCapture.Capture(new AITool[] { AIFunctionFactory.CreateDeclaration("tool", null, inputDocument.RootElement.Clone(), outputDocument?.RootElement.Clone()) }, limits);
        Assert.False(result.Succeeded);
        var expected = (output ? "$.returnSchema" : "$") + ".properties[\"a.b\"].default[\"[]\"][0][0]";
        Assert.Contains(expected, result.Diagnostic!.Message, StringComparison.Ordinal);
        var baseline = output ? Capture("{}", schema) : Capture(schema);
        var parseError = Assert.Throws<AiToolContractException>(() => AiToolContractJson.Parse(AiToolContractJson.Serialize(baseline), limits));
        Assert.Contains(expected, parseError.Message, StringComparison.Ordinal);
    }

    private static AiToolContractBaseline Accept(AiToolContractBaseline candidate, AiToolContractDiff diff, bool review) =>
        review ? AiToolContractVerifier.AcceptWithBreakingReview(candidate, diff) : AiToolContractVerifier.Accept(candidate, diff);

    internal static AiToolContractBaseline Capture(string input, string? output = null)
    {
        using var inputDocument = JsonDocument.Parse(input);
        using var outputDocument = output is null ? null : JsonDocument.Parse(output);
        var result = AiToolContractCapture.Capture(new AITool[] { AIFunctionFactory.CreateDeclaration("tool", null, inputDocument.RootElement.Clone(), outputDocument?.RootElement.Clone()) });
        Assert.True(result.Succeeded, result.Diagnostic?.Message);
        return result.Baseline!;
    }
}
