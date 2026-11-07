using System.Text.Json;
using Microsoft.Extensions.AI;
using Xunit;

namespace KeelMatrix.AiToolContract.Tests;

public sealed class RequiredValueCompositionTests
{
    public static IEnumerable<object[]> Restrictions()
    {
        yield return new object[] { "{\"type\":\"string\"}", "17", "type", AiToolChangeKind.TypeChanged };
        yield return new object[] { "{\"type\":[\"string\",\"null\"]}", "17", "type", AiToolChangeKind.TypeChanged };
        yield return new object[] { "{\"enum\":[1]}", "2", "enum", AiToolChangeKind.EnumNarrowed };
        foreach (var bound in new[] { "minimum", "exclusiveMinimum", "maximum", "exclusiveMaximum", "minLength", "maxLength", "minItems", "maxItems" })
        {
            var witness = bound switch
            {
                "minimum" or "exclusiveMinimum" => "0",
                "maximum" or "exclusiveMaximum" => "2",
                "minLength" => "\"\"",
                "maxLength" => "\"ab\"",
                "minItems" => "[]",
                _ => "[0,0]"
            };
            yield return new object[] { "{\"" + bound + "\":1}", witness, bound, AiToolChangeKind.ConstraintNarrowed };
        }
        yield return new object[] { "{\"required\":[\"child\"]}", "{}", "required", AiToolChangeKind.RequiredParameterAdded };
    }

    [Theory]
    [MemberData(nameof(Restrictions))]
    public void ImplicitAndExplicitUnconstrainedMembersHaveEquivalentProtection(string restriction, string witness, string keyword, AiToolChangeKind kind)
    {
        // These values were legal for an existing required member. Its new
        // declaration rejects them even when the member itself becomes optional.
        Assert.True(Allows("{}", witness));
        Assert.False(Allows(restriction, witness));
        foreach (var output in new[] { false, true })
            foreach (var retained in new[] { false, true })
                foreach (var position in Enumerable.Range(0, 4))
                {
                    var candidateSchema = "{\"properties\":{\"id\":" + restriction + "}}";
                    if (retained)
                        candidateSchema = candidateSchema[..^1] + ",\"required\":[\"id\"]}";
                    var candidate = Capture(Wrap(candidateSchema, position), output);
                    var implicitBaseline = Capture(Wrap("{\"required\":[\"id\"]}", position), output);
                    var explicitBaseline = Capture(Wrap("{\"properties\":{\"id\":{}},\"required\":[\"id\"]}", position), output);
                    var implicitDiff = AiToolContractVerifier.Compare(implicitBaseline, candidate);
                    var explicitDiff = AiToolContractVerifier.Compare(explicitBaseline, candidate);
                    Assert.Equal(explicitDiff.Changes.Select(Tuple), implicitDiff.Changes.Select(Tuple));
                    var valuePath = Root(output, position) + ".properties[\"id\"]." + keyword;
                    Assert.Contains(implicitDiff.Changes, c => c.Path == valuePath && c.Kind == (output ? AiToolChangeKind.ReturnSchemaAdditive : kind) && c.Compatibility == (output ? AiToolCompatibility.Additive : AiToolCompatibility.Breaking));
                    Assert.Equal(retained ? 1 : 2, implicitDiff.Changes.Count);
                    Assert.Equal(!output || !retained, implicitDiff.HasBreaking);
                    CheckAcceptance(candidate, implicitDiff);
                    var serializedBaseline = AiToolContractJson.Parse(AiToolContractJson.Serialize(implicitBaseline));
                    var serializedCandidate = AiToolContractJson.Parse(AiToolContractJson.Serialize(candidate));
                    Assert.Equal(implicitDiff.Changes.Select(Tuple), AiToolContractVerifier.Compare(serializedBaseline, serializedCandidate).Changes.Select(Tuple));
                    var verification = AiToolContractVerifier.Verify(serializedBaseline, new[] { Declaration(Wrap(candidateSchema, position), output) });
                    Assert.True(verification.Capture.Succeeded);
                    Assert.Equal(implicitDiff.Changes.Select(Tuple), verification.Diff!.Changes.Select(Tuple));
                    CheckAcceptance(verification.Capture.Baseline!, verification.Diff);
                    var reverse = AiToolContractVerifier.Compare(candidate, implicitBaseline);
                    Assert.True(reverse.HasBreaking); // Declaration removal stays conservative.
                    CheckAcceptance(implicitBaseline, reverse);
                    var exact = new AiToolContractLimits { MaxChanges = implicitDiff.Changes.Count };
                    Assert.Equal(implicitDiff.Changes.Count, AiToolContractVerifier.Compare(implicitBaseline, candidate, exact).Changes.Count);
                    if (!retained)
                    {
                        var error = Assert.Throws<AiToolContractException>(() => AiToolContractVerifier.Compare(implicitBaseline, candidate, new AiToolContractLimits { MaxChanges = 1 }));
                        Assert.Equal(AiToolDiagnosticKind.CanonicalizationOrResourceLimit, error.Diagnostic.Kind);
                    }
                }
    }

    [Theory]
    [InlineData("{\"default\":1}", "default")]
    [InlineData("{\"format\":\"date\"}", "format")]
    [InlineData("{\"$ref\":\"#/other\"}", "$ref")]
    [InlineData("{\"items\":{\"type\":\"string\"}}", "items")]
    [InlineData("{\"properties\":{\"child\":{\"default\":1}},\"required\":[\"child\"]}", "default")]
    public void UnsupportedSubtreesCannotBypassEitherAcceptanceMethod(string subtree, string keyword)
    {
        foreach (var output in new[] { false, true })
            foreach (var retained in new[] { false, true })
                foreach (var position in Enumerable.Range(0, 4))
                {
                    var oldSchema = "{\"required\":[\"id\"]}";
                    var newSchema = "{\"properties\":{\"id\":" + subtree + "}}";
                    if (retained)
                        newSchema = newSchema[..^1] + ",\"required\":[\"id\"]}";
                    var baseline = Capture(Wrap(oldSchema, position), output);
                    var candidate = Capture(Wrap(newSchema, position), output);
                    var diff = AiToolContractVerifier.Compare(baseline, candidate);
                    Assert.True(diff.HasUnsupported);
                    Assert.Contains(diff.Changes, c => c.Kind == AiToolChangeKind.Unsupported && c.Path.EndsWith("." + keyword, StringComparison.Ordinal));
                    Assert.Throws<AiToolContractException>(() => AiToolContractVerifier.Accept(candidate, diff));
                    Assert.Throws<AiToolContractException>(() => AiToolContractVerifier.AcceptWithBreakingReview(candidate, diff));
                }
    }

    [Theory]
    [InlineData("{}", false)]
    [InlineData("{\"description\":\"documented\"}", true)]
    public void UnconstrainedDeclarationDoesNotInventRequiredObligation(string declaration, bool description)
    {
        foreach (var output in new[] { false, true })
            foreach (var retained in new[] { false, true })
            {
                var baseline = Capture("{\"required\":[\"id\"]}", output);
                var candidate = Capture("{\"properties\":{\"id\":" + declaration + "}" + (retained ? ",\"required\":[\"id\"]}" : "}"), output);
                var diff = AiToolContractVerifier.Compare(baseline, candidate);
                Assert.Equal((description ? 1 : 0) + (retained ? 0 : 1), diff.Changes.Count);
                Assert.DoesNotContain(diff.Changes, c => c.Kind == AiToolChangeKind.RequiredParameterAdded);
                CheckAcceptance(candidate, diff);
            }
    }

    [Fact]
    public void MultipleExistingMembersComposeWithGenuinelyNewOptionalParameters()
    {
        var baseline = Capture("{\"required\":[\"a\",\"b\"]}", false);
        var candidate = Capture("{\"properties\":{\"a\":{\"type\":\"string\"},\"b\":{\"enum\":[1]},\"new\":{\"type\":\"string\"}}}", false);
        var diff = AiToolContractVerifier.Compare(baseline, candidate);
        Assert.Equal(5, diff.Changes.Count);
        Assert.Equal(2, diff.Changes.Count(c => c.Compatibility == AiToolCompatibility.Breaking));
        CheckAcceptance(candidate, diff);
        var optional = Capture("{\"properties\":{\"new\":{\"type\":\"string\"}}}", false);
        var addition = AiToolContractVerifier.Compare(Capture("{}", false), optional);
        Assert.Equal(AiToolChangeKind.OptionalParameterAdded, Assert.Single(addition.Changes).Kind);
        Assert.Same(optional, AiToolContractVerifier.Accept(optional, addition));
    }

    private static object Tuple(AiToolChange change) => (change.ToolName, change.Kind, change.Compatibility, change.Path, change.Message);

    private static void CheckAcceptance(AiToolContractBaseline candidate, AiToolContractDiff diff)
    {
        Assert.False(diff.HasUnsupported);
        if (diff.HasBreaking)
            Assert.Throws<AiToolContractException>(() => AiToolContractVerifier.Accept(candidate, diff));
        else
            Assert.Same(candidate, AiToolContractVerifier.Accept(candidate, diff));
        Assert.Same(candidate, AiToolContractVerifier.AcceptWithBreakingReview(candidate, diff));
    }

    private static AIFunctionDeclaration Declaration(string schema, bool output)
    {
        using var document = JsonDocument.Parse(schema);
        using var empty = JsonDocument.Parse("{}");
        return AIFunctionFactory.CreateDeclaration("tool", null, output ? empty.RootElement.Clone() : document.RootElement.Clone(), output ? document.RootElement.Clone() : null);
    }

    private static AiToolContractBaseline Capture(string schema, bool output)
    {
        var result = AiToolContractCapture.Capture(new[] { Declaration(schema, output) });
        Assert.True(result.Succeeded, result.Diagnostic?.Message);
        return result.Baseline!;
    }

    private static string Wrap(string schema, int position) => position switch
    {
        0 => schema,
        1 => "{\"properties\":{\"a.b\":" + schema + "}}",
        2 => "{\"items\":" + schema + "}",
        3 => "{\"properties\":{\"a.b\":{\"items\":" + schema + "}}}",
        _ => throw new ArgumentOutOfRangeException(nameof(position))
    };

    private static string Root(bool output, int position) => (output ? "$.returnSchema" : "$.inputSchema") + (position switch { 0 => "", 1 => ".properties[\"a.b\"]", 2 => ".items", 3 => ".properties[\"a.b\"].items", _ => throw new ArgumentOutOfRangeException(nameof(position)) });

    // Finite instance witnesses, independent of compatibility classification.
    private static bool Allows(string schema, string value)
    {
        using var document = JsonDocument.Parse(schema);
        using var instance = JsonDocument.Parse(value);
        var item = instance.RootElement;
        foreach (var property in document.RootElement.EnumerateObject())
        {
            var bound = property.Value;
            switch (property.Name)
            {
                case "type":
                    var types = bound.ValueKind == JsonValueKind.Array ? bound.EnumerateArray().Select(t => t.GetString()) : new[] { bound.GetString() };
                    if (!types.Any(t => t == "string" && item.ValueKind == JsonValueKind.String || t == "null" && item.ValueKind == JsonValueKind.Null)) return false;
                    break;
                case "enum": if (!bound.EnumerateArray().Any(v => v.GetRawText() == item.GetRawText())) return false; break;
                case "minimum": if (item.GetDecimal() < bound.GetDecimal()) return false; break;
                case "exclusiveMinimum": if (item.GetDecimal() <= bound.GetDecimal()) return false; break;
                case "maximum": if (item.GetDecimal() > bound.GetDecimal()) return false; break;
                case "exclusiveMaximum": if (item.GetDecimal() >= bound.GetDecimal()) return false; break;
                case "minLength": if (item.GetString()!.Length < bound.GetInt32()) return false; break;
                case "maxLength": if (item.GetString()!.Length > bound.GetInt32()) return false; break;
                case "minItems": if (item.GetArrayLength() < bound.GetInt32()) return false; break;
                case "maxItems": if (item.GetArrayLength() > bound.GetInt32()) return false; break;
                case "required": if (bound.EnumerateArray().Any(n => !item.TryGetProperty(n.GetString()!, out _))) return false; break;
                default: throw new InvalidOperationException("Unknown witness keyword.");
            }
        }
        return true;
    }
}
