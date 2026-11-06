using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Xunit;

namespace KeelMatrix.AiToolContract.Tests;

public sealed class RequiredCompositionTests
{
    private static readonly string[] IdName = { "id" };
    private static readonly string[] PairNames = { "id", "other" };
    private static readonly string[] CaseNames = { "Id", "id" };
    private static readonly int[] Absent = { 0 };
    private static readonly int[] Declared = { 1 };
    private static readonly int[] Required = { 2 };
    private static readonly int[] DeclaredRequired = { 3 };
    private static readonly int[] AbsentPair = { 0, 0 };
    private static readonly int[] RequiredPair = { 2, 0 };
    private static readonly int[] DeclaredPair = { 1, 0 };
    private static readonly int[] SwappedPair = { 1, 3 };
    private static readonly int[] FirstBoth = { 3, 0 };
    private static readonly int[] SecondBoth = { 0, 3 };
    // Each name has four independent states: absent, declared, required, or both.
    // Expectations below express the public component policy without consulting
    // the normalized model or the production transition rules.
    public static IEnumerable<object[]> SingleNameStates()
    {
        foreach (var output in new[] { false, true })
            for (var position = 0; position < 3; position++)
                foreach (var name in new[] { "id", "a.properties.b", "" })
                    for (var before = 0; before < 4; before++)
                        for (var after = 0; after < 4; after++)
                            yield return new object[] { output, position, name, before, after };
    }

    public static IEnumerable<object[]> TwoNameStates()
    {
        foreach (var output in new[] { false, true })
            for (var position = 0; position < 3; position++)
                for (var before = 0; before < 16; before++)
                    for (var after = 0; after < 16; after++)
                        yield return new object[] { output, position, before, after };
    }

    [Theory]
    [MemberData(nameof(SingleNameStates))]
    public void RequiredChangesRemainIndependent(bool output, int position, string name, int before, int after) =>
        CheckStates(output, position, new[] { name }, new[] { before }, new[] { after });

    [Theory]
    [MemberData(nameof(TwoNameStates))]
    public void TwoNameChangesPreserveEveryComponent(bool output, int position, int before, int after) =>
        CheckStates(output, position, PairNames, new[] { before & 3, before >> 2 }, new[] { after & 3, after >> 2 });

    private static void CheckStates(bool output, int position, string[] names, int[] before, int[] after)
    {
        var oldSchema = Wrap(Schema(names, before), position);
        var newSchema = Wrap(Schema(names, after), position);
        var baseline = Capture(oldSchema, output);
        var candidate = Capture(newSchema, output);
        var expected = Components(names, before, after, output, Path(output, position));
        CheckDiff(baseline, candidate, expected);
        CheckDiff(RoundTrip(baseline), RoundTrip(candidate), expected);
        var verified = AiToolContractVerifier.Verify(baseline, new[] { Declaration(newSchema, output) });
        Assert.True(verified.Capture.Succeeded);
        CheckReport(verified.Capture.Baseline!, verified.Diff!, expected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyRepresentationsAndOrdinalNamesPreserveComponents(bool output)
    {
        foreach (var position in Enumerable.Range(0, 3))
        {
            var absent = Capture(Wrap("{\"type\":\"object\"}", position), output);
            var empty = Capture(Wrap("{\"type\":\"object\",\"properties\":{}}", position), output);
            CheckDiff(absent, empty, new List<Component>());
            CheckDiff(empty, absent, new List<Component>());
            foreach (var name in new[] { "Id", "id", "[]\"\\/\u000aé" })
            {
                CheckStates(output, position, new[] { name }, Required, Declared);
                CheckStates(output, position, new[] { name }, Declared, Required);
            }
            CheckStates(output, position, CaseNames, FirstBoth, SecondBoth);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void LostPresenceHasConcreteNewlyValidOutputWitnesses(int position)
    {
        foreach (var pair in new[]
        {
            ("{\"type\":\"object\",\"required\":[\"id\"]}", "{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\"}}}", "{}", "{\"id\":\"x\"}"),
            ("{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\"}},\"required\":[\"id\"]}", "{\"type\":\"object\"}", "{\"other\":\"x\"}", "{\"id\":\"x\"}"),
            ("{\"type\":\"object\",\"required\":[\"id\",\"stable\"]}", "{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\"},\"other\":{\"type\":\"string\"}},\"required\":[\"other\",\"stable\"]}", "{\"other\":\"x\",\"stable\":\"x\"}", "{\"id\":\"x\",\"stable\":\"x\"}")
        })
        {
            var oldSchema = Wrap(pair.Item1, position);
            var newSchema = Wrap(pair.Item2, position);
            Assert.False(Allows(oldSchema, WrapValue(pair.Item3, position)));
            Assert.True(Allows(newSchema, WrapValue(pair.Item3, position)));
            Assert.True(Allows(oldSchema, WrapValue(pair.Item4, position)));
            var baseline = Capture(oldSchema, true);
            var candidate = Capture(newSchema, true);
            var diff = AiToolContractVerifier.Compare(baseline, candidate);
            Assert.True(diff.HasBreaking);
            Assert.Contains(diff.Changes, c => c.Path == Path(true, position) + ".required" && c.Kind == AiToolChangeKind.ReturnSchemaBreaking);
            Assert.Throws<AiToolContractException>(() => AiToolContractVerifier.Accept(candidate, diff));
            Assert.Same(candidate, AiToolContractVerifier.AcceptWithBreakingReview(candidate, diff));
            // Swaps admit different witnesses in each direction.
            if (pair.Item3.Contains("stable", StringComparison.Ordinal))
            {
                Assert.False(Allows(newSchema, WrapValue(pair.Item4, position)));
                Assert.True(AiToolContractVerifier.Compare(candidate, baseline).HasBreaking);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RequiredCompositionRetainsSiblingTypeEnumAndBoundChanges(bool output)
    {
        foreach (var position in Enumerable.Range(0, 4))
        {
            const string before = "{\"type\":\"object\",\"properties\":{\"typed\":{\"type\":\"string\"},\"choice\":{\"enum\":[1]},\"bounded\":{\"maximum\":1}},\"required\":[\"id\"]}";
            const string after = "{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\"},\"other\":{\"type\":\"string\"},\"typed\":{\"type\":[\"string\",\"null\"]},\"choice\":{\"enum\":[1,2]},\"bounded\":{\"maximum\":2}},\"required\":[\"other\"]}";
            foreach (var reverse in new[] { false, true })
            {
                var path = Path(output, position);
                var expected = Components(PairNames, reverse ? SwappedPair : RequiredPair, reverse ? RequiredPair : SwappedPair, output, path);
                var severity = output != reverse ? AiToolCompatibility.Breaking : AiToolCompatibility.Additive;
                var returnKind = severity == AiToolCompatibility.Breaking ? AiToolChangeKind.ReturnSchemaBreaking : AiToolChangeKind.ReturnSchemaAdditive;
                expected.Add(new(output ? returnKind : AiToolChangeKind.TypeChanged, severity, path + ".properties[\"typed\"].type", null));
                expected.Add(new(output ? returnKind : reverse ? AiToolChangeKind.EnumNarrowed : AiToolChangeKind.EnumExpanded, severity, path + ".properties[\"choice\"].enum", null));
                expected.Add(new(output ? returnKind : reverse ? AiToolChangeKind.ConstraintNarrowed : AiToolChangeKind.ConstraintExpanded, severity, path + ".properties[\"bounded\"].maximum", null));
                CheckDiff(Capture(Wrap(reverse ? after : before, position), output), Capture(Wrap(reverse ? before : after, position), output), expected);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompositionKeepsAcceptanceAssociationAndUnsupportedSiblings(bool output)
    {
        var baseline = Capture(Schema(IdName, Required), output);
        var candidate = Capture(Schema(IdName, Declared), output);
        var diff = AiToolContractVerifier.Compare(baseline, candidate);
        foreach (var unrelated in new[] { baseline, Capture(Schema(IdName, Declared), output), RoundTrip(candidate) })
        {
            Assert.Throws<AiToolContractException>(() => AiToolContractVerifier.Accept(unrelated, diff));
            Assert.Throws<AiToolContractException>(() => AiToolContractVerifier.AcceptWithBreakingReview(unrelated, diff));
        }
        foreach (var other in new[] { AiToolContractVerifier.Compare(baseline, baseline), AiToolContractVerifier.Compare(candidate, baseline) })
        {
            Assert.Throws<AiToolContractException>(() => AiToolContractVerifier.Accept(candidate, other));
            Assert.Throws<AiToolContractException>(() => AiToolContractVerifier.AcceptWithBreakingReview(candidate, other));
        }
        var unsupported = Capture("{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\"},\"sibling\":{\"$ref\":\"#/other\"}}}", output);
        var withSibling = Capture("{\"type\":\"object\",\"properties\":{\"sibling\":{}},\"required\":[\"id\"]}", output);
        var unsupportedDiff = AiToolContractVerifier.Compare(withSibling, unsupported);
        Assert.Equal(3, unsupportedDiff.Changes.Count);
        Assert.True(unsupportedDiff.HasUnsupported);
        Assert.Contains(unsupportedDiff.Changes, c => c.Path.EndsWith(".required", StringComparison.Ordinal));
        Assert.Throws<AiToolContractException>(() => AiToolContractVerifier.Accept(unsupported, unsupportedDiff));
        Assert.Throws<AiToolContractException>(() => AiToolContractVerifier.AcceptWithBreakingReview(unsupported, unsupportedDiff));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangeLimitsCountAllIndependentComponents(bool output)
    {
        foreach (var position in Enumerable.Range(0, 3))
            foreach (var states in new[] { (RequiredPair, DeclaredPair, 2), (RequiredPair, SwappedPair, 4), (AbsentPair, RequiredPair, 1) })
                foreach (var reverse in new[] { false, true })
                {
                    var baseline = Capture(Wrap(Schema(PairNames, reverse ? states.Item2 : states.Item1), position), output);
                    var candidate = Capture(Wrap(Schema(PairNames, reverse ? states.Item1 : states.Item2), position), output);
                    foreach (var capacity in new[] { states.Item3, states.Item3 + 1 })
                        Assert.Equal(states.Item3, AiToolContractVerifier.Compare(baseline, candidate, new AiToolContractLimits { MaxChanges = capacity }).Changes.Count);
                    Assert.True(AiToolContractVerifier.Compare(baseline, baseline, new AiToolContractLimits { MaxChanges = 1 }).IsClean);
                    if (states.Item3 > 1)
                    {
                        var error = Assert.Throws<AiToolContractException>(() => AiToolContractVerifier.Compare(baseline, candidate, new AiToolContractLimits { MaxChanges = states.Item3 - 1 }));
                        Assert.Equal(AiToolDiagnosticKind.CanonicalizationOrResourceLimit, error.Diagnostic.Kind);
                    }
                    var extraTool = AiToolContractJson.Parse(AiToolContractJson.Serialize(candidate).Replace("\"tools\":[", "\"tools\":[{\"name\":\"extra\",\"description\":null,\"inputSchema\":{},\"returnSchema\":null,\"requiresApproval\":false},", StringComparison.Ordinal));
                    Assert.Equal(states.Item3 + 1, AiToolContractVerifier.Compare(baseline, extraTool, new AiToolContractLimits { MaxChanges = states.Item3 + 1 }).Changes.Count);
                    Assert.Throws<AiToolContractException>(() => AiToolContractVerifier.Compare(baseline, extraTool, new AiToolContractLimits { MaxChanges = states.Item3 }));
                }
    }

    [Fact]
    public void FactoryCaptureAndVerifyKeepRemovedDeclarationAndRequirement()
    {
        var function = AIFunctionFactory.Create((string id) => id, new AIFunctionFactoryOptions { Name = "tool" });
        var baseline = AiToolContractCapture.Capture(new AITool[] { function }).Baseline!;
        var input = JsonNode.Parse(function.JsonSchema.GetRawText())!.AsObject();
        input["properties"]!.AsObject().Remove("id");
        input.Remove("required");
        using var schema = JsonDocument.Parse(input.ToJsonString());
        var declaration = AIFunctionFactory.CreateDeclaration(function.Name, function.Description, schema.RootElement.Clone(), function.ReturnJsonSchema);
        var result = AiToolContractVerifier.Verify(RoundTrip(baseline), new AITool[] { declaration });
        Assert.True(result.Capture.Succeeded);
        CheckReport(result.Capture.Baseline!, result.Diff!, Components(IdName, DeclaredRequired, Absent, false, "$"));
    }

    private static List<Component> Components(string[] names, int[] before, int[] after, bool output, string path)
    {
        var result = new List<Component>();
        for (var i = 0; i < names.Length; i++)
        {
            var oldDeclared = (before[i] & 1) != 0;
            var newDeclared = (after[i] & 1) != 0;
            var oldRequired = (before[i] & 2) != 0;
            var newRequired = (after[i] & 2) != 0;
            if (oldDeclared && !newDeclared)
                result.Add(new(output ? AiToolChangeKind.ReturnSchemaBreaking : AiToolChangeKind.ParameterRemoved, AiToolCompatibility.Breaking, path + ".properties[" + JsonSerializer.Serialize(names[i]) + "]", names[i]));
            if (!oldDeclared && newDeclared)
                result.Add(new(output ? AiToolChangeKind.ReturnSchemaAdditive : newRequired ? AiToolChangeKind.RequiredParameterAdded : AiToolChangeKind.OptionalParameterAdded, !output && newRequired ? AiToolCompatibility.Breaking : AiToolCompatibility.Additive, path + ".properties[" + JsonSerializer.Serialize(names[i]) + "]", names[i]));
            if (!oldRequired && newRequired)
                result.Add(new(output ? AiToolChangeKind.ReturnSchemaAdditive : AiToolChangeKind.RequiredParameterAdded, output ? AiToolCompatibility.Additive : AiToolCompatibility.Breaking, path + ".required", names[i]));
            if (oldRequired && !newRequired)
                result.Add(new(output ? AiToolChangeKind.ReturnSchemaBreaking : AiToolChangeKind.OptionalParameterAdded, output ? AiToolCompatibility.Breaking : AiToolCompatibility.Additive, path + ".required", names[i]));
        }
        return result;
    }

    private static void CheckDiff(AiToolContractBaseline baseline, AiToolContractBaseline candidate, List<Component> expected) =>
        CheckReport(candidate, AiToolContractVerifier.Compare(baseline, candidate), expected);

    private static void CheckReport(AiToolContractBaseline candidate, AiToolContractDiff diff, List<Component> expected)
    {
        Assert.Equal(expected.Count, diff.Changes.Count);
        var remaining = diff.Changes.ToList();
        foreach (var component in expected)
        {
            var match = remaining.FindIndex(c => c.ToolName == "tool" && c.Kind == component.Kind && c.Compatibility == component.Severity && c.Path == component.Path && (component.Name is null || c.Message.Contains("'" + component.Name + "'", StringComparison.Ordinal)));
            Assert.True(match >= 0, component.ToString());
            remaining.RemoveAt(match);
        }
        Assert.Equal(expected.Count == 0, diff.IsClean);
        Assert.Equal(expected.Any(c => c.Severity == AiToolCompatibility.Breaking), diff.HasBreaking);
        Assert.False(diff.HasUnsupported);
        Assert.DoesNotContain(diff.Diagnostics, d => d.Kind == AiToolDiagnosticKind.UnsupportedClassification);
        Assert.Equal(expected.Count == 0 ? AiToolCompatibility.Informational : expected.Max(c => c.Severity), diff.Compatibility);
        if (diff.HasBreaking)
            Assert.Throws<AiToolContractException>(() => AiToolContractVerifier.Accept(candidate, diff));
        else
            Assert.Same(candidate, AiToolContractVerifier.Accept(candidate, diff));
        Assert.Same(candidate, AiToolContractVerifier.AcceptWithBreakingReview(candidate, diff));
    }

    private static string Schema(string[] names, int[] states)
    {
        var schema = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["type"] = "object",
            ["properties"] = names.Where((_, i) => (states[i] & 1) != 0).ToDictionary(n => n, _ => new { type = "string" }, StringComparer.Ordinal)
        };
        var required = names.Where((_, i) => (states[i] & 2) != 0).ToArray();
        if (required.Length > 0)
            schema["required"] = required;
        return JsonSerializer.Serialize(schema);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[\"id\",\"id\"]")]
    [InlineData("[1]")]
    [InlineData("null")]
    public void MalformedRequiredArraysRemainRejected(string required)
    {
        foreach (var output in new[] { false, true })
        {
            var schema = "{\"type\":\"object\",\"required\":" + required + "}";
            Assert.False(AiToolContractCapture.Capture(new[] { Declaration(schema, output) }).Succeeded);
            var json = "{\"schemaVersion\":1,\"tools\":[{\"name\":\"tool\",\"description\":null,\"inputSchema\":" + (output ? "{}" : schema) + ",\"returnSchema\":" + (output ? schema : "null") + ",\"requiresApproval\":false}]}";
            Assert.Throws<AiToolContractException>(() => AiToolContractJson.Parse(json));
        }
    }

    private static string Wrap(string schema, int position) => position switch
    {
        0 => schema,
        1 => "{\"type\":\"object\",\"properties\":{\"result\":" + schema + "},\"required\":[\"result\"]}",
        2 => "{\"type\":\"array\",\"items\":" + schema + "}",
        3 => Wrap(Wrap(Wrap(schema, 1), 2), 1),
        _ => throw new ArgumentOutOfRangeException(nameof(position))
    };

    private static string WrapValue(string value, int position) => position switch
    {
        0 => value,
        1 => "{\"result\":" + value + "}",
        2 => "[" + value + "]",
        3 => WrapValue(WrapValue(WrapValue(value, 1), 2), 1),
        _ => throw new ArgumentOutOfRangeException(nameof(position))
    };

    private static string Path(bool output, int position) => (output ? "$.returnSchema" : "$") + (position switch
    {
        0 => "",
        1 => ".properties[\"result\"]",
        2 => ".items",
        3 => ".properties[\"result\"].items.properties[\"result\"]",
        _ => throw new ArgumentOutOfRangeException(nameof(position))
    });

    private static AIFunctionDeclaration Declaration(string schema, bool output)
    {
        using var document = JsonDocument.Parse(schema);
        using var empty = JsonDocument.Parse("{}");
        return AIFunctionFactory.CreateDeclaration("tool", null, output ? empty.RootElement.Clone() : document.RootElement.Clone(), output ? document.RootElement.Clone() : null);
    }

    private static AiToolContractBaseline Capture(string schema, bool output)
    {
        var captured = AiToolContractCapture.Capture(new[] { Declaration(schema, output) });
        Assert.True(captured.Succeeded, captured.Diagnostic?.Message);
        return captured.Baseline!;
    }

    private static AiToolContractBaseline RoundTrip(AiToolContractBaseline baseline) => AiToolContractJson.Parse(AiToolContractJson.Serialize(baseline));

    // Finite witness evaluator: only object/string/array presence and wrapping.
    private static bool Allows(string schema, string value)
    {
        using var schemaDocument = JsonDocument.Parse(schema);
        using var valueDocument = JsonDocument.Parse(value);
        return Allows(schemaDocument.RootElement, valueDocument.RootElement);
    }

    private static bool Allows(JsonElement schema, JsonElement value)
    {
        if (schema.TryGetProperty("type", out var type) && type.GetString() switch
        {
            "object" => value.ValueKind != JsonValueKind.Object,
            "array" => value.ValueKind != JsonValueKind.Array,
            "string" => value.ValueKind != JsonValueKind.String,
            _ => throw new InvalidOperationException("Unknown witness type.")
        }) return false;
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out var required) && required.EnumerateArray().Any(n => !value.TryGetProperty(n.GetString()!, out _))) return false;
            if (schema.TryGetProperty("properties", out var properties))
                foreach (var property in properties.EnumerateObject())
                    if (value.TryGetProperty(property.Name, out var member) && !Allows(property.Value, member)) return false;
        }
        if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
            return value.EnumerateArray().All(item => Allows(items, item));
        return true;
    }

    private sealed record Component(AiToolChangeKind Kind, AiToolCompatibility Severity, string Path, string? Name);
}
