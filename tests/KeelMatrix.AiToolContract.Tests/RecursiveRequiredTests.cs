using System.Text.Json;
using Microsoft.Extensions.AI;
using Xunit;

namespace KeelMatrix.AiToolContract.Tests;

public sealed class RecursiveRequiredTests
{
    [Theory]
    [InlineData("{\"default\":1}", "default")]
    [InlineData("{\"format\":\"date\"}", "format")]
    [InlineData("{\"$ref\":\"#/x\"}", "$ref")]
    [InlineData("{\"items\":{}}", "items")]
    public void NewlyRequiredChildrenPreserveUnsupportedComponents(string declaration, string keyword)
    {
        foreach (var output in new[] { false, true })
            foreach (var retained in new[] { false, true })
                foreach (var position in Enumerable.Range(0, 4))
                    foreach (var childState in Enumerable.Range(0, 4))
                    {
                        var oldValue = childState switch
                        {
                            0 => "{}",
                            1 => "{\"properties\":{\"child\":{}}}",
                            2 => "{\"required\":[\"child\"]}",
                            _ => "{\"properties\":{\"child\":{}},\"required\":[\"child\"]}"
                        };
                        var oldSchemas = new List<string> { "{\"properties\":{\"id\":" + oldValue + "},\"required\":[\"id\"]}" };
                        if (childState == 0)
                            oldSchemas.Add("{\"required\":[\"id\"]}");
                        foreach (var oldSchema in oldSchemas)
                        {
                            var next = "{\"properties\":{\"id\":{\"properties\":{\"child\":" + declaration + "},\"required\":[\"child\"]}}" + (retained ? ",\"required\":[\"id\"]}" : "}");
                            // Presence is observable independently of the unsupported
                            // keyword: {"id":{}} loses validity when child is required.
                            using var oldDocument = JsonDocument.Parse(oldSchema);
                            using var newDocument = JsonDocument.Parse(next);
                            using var witness = JsonDocument.Parse("{\"id\":{}}");
                            Assert.Equal(childState < 2, RequiredNamesPresent(oldDocument.RootElement, witness.RootElement));
                            Assert.False(RequiredNamesPresent(newDocument.RootElement, witness.RootElement));
                            var baseline = Capture(Wrap(oldSchema, position), output);
                            var candidate = Capture(Wrap(next, position), output);
                            var expectedPath = Root(output, position) + ".properties[\"id\"].properties[\"child\"]." + keyword;
                            Check(baseline, candidate, expectedPath);
                            Check(AiToolContractJson.Parse(AiToolContractJson.Serialize(baseline)), AiToolContractJson.Parse(AiToolContractJson.Serialize(candidate)), expectedPath);
                            var verification = AiToolContractVerifier.Verify(baseline, new[] { Declaration(Wrap(next, position), output) });
                            Assert.True(verification.Capture.Succeeded);
                            Reject(verification.Capture.Baseline!, verification.Diff!, expectedPath);
                            var reverse = AiToolContractVerifier.Compare(candidate, baseline);
                            Assert.True(reverse.HasBreaking || reverse.Changes.Any(c => c.Kind == AiToolChangeKind.Unsupported));
                        }
                    }
    }

    [Fact]
    public void SeveralRequiredLevelsAndSiblingsKeepEveryUnsupportedPath()
    {
        const string next = "{\"properties\":{\"id\":{\"properties\":{\"child\":{\"properties\":{\"leaf\":{\"default\":1,\"format\":\"date\"}},\"required\":[\"leaf\"]},\"sibling\":{\"$ref\":\"#/x\"}},\"required\":[\"child\",\"sibling\"]}},\"required\":[\"id\"]}";
        foreach (var output in new[] { false, true })
        {
            var baseline = Capture("{\"required\":[\"id\"]}", output);
            var candidate = Capture(next, output);
            var diff = AiToolContractVerifier.Compare(baseline, candidate);
            var root = Root(output, 0) + ".properties[\"id\"].properties";
            Reject(candidate, diff, root + "[\"child\"].properties[\"leaf\"].default");
            Assert.Contains(diff.Changes, c => c.Kind == AiToolChangeKind.Unsupported && c.Path == root + "[\"child\"].properties[\"leaf\"].format");
            Assert.Contains(diff.Changes, c => c.Kind == AiToolChangeKind.Unsupported && c.Path == root + "[\"sibling\"].$ref");
            Assert.Equal(3, diff.Changes.Count(c => c.Kind == AiToolChangeKind.Unsupported));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GenuinelyNewOptionalChildrenKeepTheirAdditivePolicy(bool output)
    {
        foreach (var requiredParent in new[] { false, true })
        {
            var baseline = Capture(requiredParent ? "{\"required\":[\"id\"]}" : "{}", output);
            var candidate = Capture(requiredParent
                ? "{\"required\":[\"id\"],\"properties\":{\"id\":{\"properties\":{\"optional\":{\"default\":1}}}}}"
                : "{\"properties\":{\"optional\":{\"required\":[\"child\"],\"properties\":{\"child\":{\"default\":1}}}}}", output);
            var diff = AiToolContractVerifier.Compare(baseline, candidate);
            Assert.False(diff.HasUnsupported);
            Assert.False(diff.HasBreaking);
            Assert.Same(candidate, AiToolContractVerifier.Accept(candidate, diff));
            Assert.Same(candidate, AiToolContractVerifier.AcceptWithBreakingReview(candidate, diff));
        }
    }

    private static void Check(AiToolContractBaseline baseline, AiToolContractBaseline candidate, string path)
    {
        var diff = AiToolContractVerifier.Compare(baseline, candidate);
        Reject(candidate, diff, path);
        Assert.Equal(diff.Changes.Count, AiToolContractVerifier.Compare(baseline, candidate, new AiToolContractLimits { MaxChanges = diff.Changes.Count }).Changes.Count);
        if (diff.Changes.Count > 1)
        {
            var error = Assert.Throws<AiToolContractException>(() => AiToolContractVerifier.Compare(baseline, candidate, new AiToolContractLimits { MaxChanges = diff.Changes.Count - 1 }));
            Assert.Equal(AiToolDiagnosticKind.CanonicalizationOrResourceLimit, error.Diagnostic.Kind);
        }
    }

    private static void Reject(AiToolContractBaseline candidate, AiToolContractDiff diff, string path)
    {
        Assert.Contains(diff.Changes, c => c.Kind == AiToolChangeKind.Unsupported && c.Path == path);
        Assert.Contains(diff.Diagnostics, d => d.Kind == AiToolDiagnosticKind.UnsupportedClassification && d.Message.Contains(path, StringComparison.Ordinal));
        Assert.Equal(AiToolDiagnosticKind.UnsupportedClassification, Assert.Throws<AiToolContractException>(() => AiToolContractVerifier.Accept(candidate, diff)).Diagnostic.Kind);
        Assert.Equal(AiToolDiagnosticKind.UnsupportedClassification, Assert.Throws<AiToolContractException>(() => AiToolContractVerifier.AcceptWithBreakingReview(candidate, diff)).Diagnostic.Kind);
    }

    // Only the supported presence constraint is evaluated. No compatibility
    // interpretation of default, format, references or item presence is assumed.
    private static bool RequiredNamesPresent(JsonElement schema, JsonElement instance)
    {
        if (schema.TryGetProperty("required", out var required))
            foreach (var name in required.EnumerateArray())
                if (!instance.TryGetProperty(name.GetString()!, out _))
                    return false;
        if (schema.TryGetProperty("properties", out var properties))
            foreach (var property in properties.EnumerateObject())
                if (instance.TryGetProperty(property.Name, out var value) && !RequiredNamesPresent(property.Value, value))
                    return false;
        return true;
    }

    internal static AIFunctionDeclaration Declaration(string schema, bool output)
    {
        using var document = JsonDocument.Parse(schema);
        using var empty = JsonDocument.Parse("{}");
        return AIFunctionFactory.CreateDeclaration("tool", null, output ? empty.RootElement.Clone() : document.RootElement.Clone(), output ? document.RootElement.Clone() : null);
    }

    internal static AiToolContractBaseline Capture(string schema, bool output) => AiToolContractCapture.Capture(new[] { Declaration(schema, output) }).Baseline!;

    private static string Wrap(string schema, int position) => position switch
    {
        0 => schema,
        1 => "{\"properties\":{\"a.b\":" + schema + "}}",
        2 => "{\"items\":" + schema + "}",
        _ => "{\"properties\":{\"a.b\":{\"items\":" + schema + "}}}"
    };

    private static string Root(bool output, int position) => (output ? "$.returnSchema" : "$.inputSchema") + (position switch { 0 => "", 1 => ".properties[\"a.b\"]", 2 => ".items", _ => ".properties[\"a.b\"].items" });
}
