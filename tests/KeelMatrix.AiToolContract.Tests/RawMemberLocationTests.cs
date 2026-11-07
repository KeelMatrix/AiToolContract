using System.Text.Json;
using Xunit;

namespace KeelMatrix.AiToolContract.Tests;

public sealed class RawMemberLocationTests
{
    public static IEnumerable<object[]> Locations()
    {
        // Expected segments are specified independently of the production encoder.
        var names = new (string Name, string Segment)[]
        {
            ("", """[""]"""),
            ("a.b", """["a.b"]"""),
            ("properties[\"x\"].items", """["properties[\u0022x\u0022].items"]"""),
            ("a\"b", """["a\u0022b"]"""),
            ("a[b]", """["a[b]"]"""),
            ("a\nb", """["a\nb"]"""),
            ("é😀", """["\u00E9\uD83D\uDE00"]"""),
            ("a\\/b~c", """["a\\/b~c"]""")
        };
        foreach (var (name, segment) in names)
            foreach (var output in new[] { false, true })
                for (var placement = 0; placement < 4; placement++)
                    yield return new object[] { name, segment, output, placement };
    }

    [Theory]
    [MemberData(nameof(Locations))]
    public void RawMemberCountersAndUnsupportedRejectionQuoteArbitraryNames(string name, string segment, bool output, int placement)
    {
        foreach (var preceding in new[] { 1, 2 })
        {
            var schema = Wrap("{\"description\":\"ok\"," + (preceding == 2 ? "\"format\":\"x\"," : "") + JsonSerializer.Serialize(name) + ":0}", placement);
            var count = Overhead(placement) + preceding;
            var path = Root(output, placement) + segment;
            CheckEntryPoints(schema, output, new AiToolContractLimits { MaxProperties = count }, path, AiToolDiagnosticKind.CanonicalizationOrResourceLimit);
            // At the exact count and above, the same arbitrary keyword is still unsupported.
            foreach (var limit in new[] { count + 1, count + 2 })
                CheckEntryPoints(schema, output, new AiToolContractLimits { MaxProperties = limit }, path, AiToolDiagnosticKind.UnsupportedClassification);
            if (preceding == 2)
                CheckEntryPoints(schema, output, new AiToolContractLimits { MaxProperties = count - 1 }, Root(output, placement) + ".format", AiToolDiagnosticKind.CanonicalizationOrResourceLimit);
        }
        var key = JsonSerializer.Serialize(name);
        var duplicate = Wrap("{" + key + ":0," + key + ":1}", placement);
        foreach (var entryPoint in new[] { "capture", "parse", "verify" })
            Check(Failure(duplicate, output, new AiToolContractLimits(), entryPoint), Root(output, placement) + segment, entryPoint == "parse" ? AiToolDiagnosticKind.MalformedBaseline : AiToolDiagnosticKind.CaptureFailure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FixedKeywordsKeepDottedPathsAndUnknownIdentifiersAreQuoted(bool output)
    {
        var root = output ? "$.returnSchema" : "$.inputSchema";
        var keywords = new[] { "$ref", "description", "default", "format", "type", "enum", "items", "properties", "required", "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "minLength", "maxLength", "minItems", "maxItems" };
        foreach (var keyword in keywords)
        {
            var key = JsonSerializer.Serialize(keyword);
            var schema = "{" + key + ":null," + key + ":null}";
            foreach (var entryPoint in new[] { "capture", "parse", "verify" })
                Check(Failure(schema, output, new AiToolContractLimits(), entryPoint), root + "." + keyword, entryPoint == "parse" ? AiToolDiagnosticKind.MalformedBaseline : AiToolDiagnosticKind.CaptureFailure);
        }
        foreach (var keyword in new[] { "unknown", "Items", "TYPE" })
            CheckEntryPoints("{\"" + keyword + "\":0}", output, new AiToolContractLimits(), root + "[\"" + keyword + "\"]", AiToolDiagnosticKind.UnsupportedClassification);
        // Decoded spellings have the same fixed-key location and duplicate protection.
        foreach (var entryPoint in new[] { "capture", "parse", "verify" })
            Check(Failure("{\"type\":\"string\",\"\\u0074ype\":\"number\"}", output, new AiToolContractLimits(), entryPoint), root + ".type", entryPoint == "parse" ? AiToolDiagnosticKind.MalformedBaseline : AiToolDiagnosticKind.CaptureFailure);
    }

    [Theory]
    [MemberData(nameof(Locations))]
    public void LaterAndNormalizedCountersRetainEncodedNames(string name, string segment, bool output, int placement)
    {
        foreach (var keyword in new[] { "properties", "default", "enum" })
        {
            var member = JsonSerializer.Serialize(name) + (keyword == "properties" ? ":{}" : ":0");
            var value = "{" + member + "}";
            var schema = Wrap("{\"" + keyword + "\":" + (keyword == "enum" ? "[" + value + "]" : value) + "}", placement);
            var count = Overhead(placement) + 2;
            var exact = new AiToolContractLimits { MaxProperties = count };
            var low = new AiToolContractLimits { MaxProperties = count - 1 };
            var path = Root(output, placement) + "." + keyword + (keyword == "enum" ? "[0]" : "") + segment;
            var tools = new[] { RecursiveRequiredTests.Declaration(schema, output) };
            var capture = AiToolContractCapture.Capture(tools, exact);
            Assert.True(capture.Succeeded, capture.Diagnostic?.Message);
            var baseline = capture.Baseline!;
            var json = AiToolContractJson.Serialize(baseline, exact);
            Assert.True(AiToolContractVerifier.Compare(baseline, AiToolContractJson.Parse(json, exact), exact).IsClean);
            CheckEntryPoints(schema, output, low, path, AiToolDiagnosticKind.CanonicalizationOrResourceLimit);
            Check(Assert.Throws<AiToolContractException>(() => AiToolContractJson.Serialize(baseline, low)).Diagnostic, path, AiToolDiagnosticKind.CanonicalizationOrResourceLimit);
            Check(Assert.Throws<AiToolContractException>(() => AiToolContractVerifier.Compare(baseline, baseline, low)).Diagnostic, path, AiToolDiagnosticKind.CanonicalizationOrResourceLimit);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FlatPathShapedNamesDoNotAliasNestedKeywords(bool output)
    {
        var flat = "{\"description\":\"x\",\"properties[\\\"x\\\"].items\":0}";
        var nested = "{\"properties\":{\"x\":{\"items\":{}}}}";
        var root = output ? "$.returnSchema" : "$.inputSchema";
        foreach (var entryPoint in new[] { "capture", "parse", "verify" })
        {
            var flatFailure = Failure(flat, output, new AiToolContractLimits { MaxProperties = 1 }, entryPoint);
            var nestedFailure = Failure(nested, output, new AiToolContractLimits { MaxProperties = 2 }, entryPoint);
            Check(flatFailure, root + """["properties[\u0022x\u0022].items"]""", AiToolDiagnosticKind.CanonicalizationOrResourceLimit);
            Check(nestedFailure, root + ".properties[\"x\"].items", AiToolDiagnosticKind.CanonicalizationOrResourceLimit);
            Assert.NotEqual(flatFailure.Message, nestedFailure.Message);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UndecodableNamesIdentifyTheNearestValidContainer(bool output)
    {
        for (var placement = 0; placement < 4; placement++)
            foreach (var container in new[] { "", "properties", "default", "enum" })
                foreach (var surrogate in new[] { "\\ud800", "\\udfff" })
                {
                    var value = "{\"" + surrogate + "\":0}";
                    var schema = Wrap(container.Length == 0 ? value : "{\"" + container + "\":" + (container == "enum" ? "[" + value + "]" : value) + "}", placement);
                    var path = Root(output, placement) + (container.Length == 0 ? "" : "." + container) + (container == "enum" ? "[0]" : "");
                    foreach (var entryPoint in new[] { "capture", "parse", "verify" })
                        Check(Failure(schema, output, new AiToolContractLimits(), entryPoint), path, entryPoint == "parse" ? AiToolDiagnosticKind.MalformedBaseline : AiToolDiagnosticKind.CaptureFailure);
                }
    }

    private static void CheckEntryPoints(string schema, bool output, AiToolContractLimits limits, string path, AiToolDiagnosticKind kind)
    {
        foreach (var entryPoint in new[] { "capture", "parse", "verify" })
            Check(Failure(schema, output, limits, entryPoint), path, kind);
    }

    private static AiToolContractDiagnostic Failure(string schema, bool output, AiToolContractLimits limits, string entryPoint)
    {
        if (entryPoint == "parse")
        {
            var json = "{\"schemaVersion\":1,\"tools\":[{\"name\":\"tool\",\"description\":null,\"inputSchema\":" + (output ? "{}" : schema) + ",\"returnSchema\":" + (output ? schema : "null") + ",\"requiresApproval\":false}]}";
            return Assert.Throws<AiToolContractException>(() => AiToolContractJson.Parse(json, limits)).Diagnostic;
        }
        var tools = new[] { RecursiveRequiredTests.Declaration(schema, output) };
        var capture = entryPoint == "capture"
            ? AiToolContractCapture.Capture(tools, limits)
            : AiToolContractVerifier.Verify(RecursiveRequiredTests.Capture("{}", output), tools, limits).Capture;
        Assert.False(capture.Succeeded);
        Assert.Null(capture.Baseline);
        return Assert.IsType<AiToolContractDiagnostic>(capture.Diagnostic);
    }

    private static void Check(AiToolContractDiagnostic diagnostic, string path, AiToolDiagnosticKind kind)
    {
        Assert.Equal(kind, diagnostic.Kind);
        Assert.Contains("at " + path, diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', diagnostic.Message);
    }

    private static int Overhead(int placement) => placement switch { 0 => 0, 1 => 2, 2 => 1, _ => 3 };
    private static string Root(bool output, int placement) => (output ? "$.returnSchema" : "$.inputSchema") + (placement switch { 0 => "", 1 => ".properties[\"a.b\"]", 2 => ".items", _ => ".properties[\"a.b\"].items" });
    private static string Wrap(string schema, int placement) => placement switch { 0 => schema, 1 => "{\"properties\":{\"a.b\":" + schema + "}}", 2 => "{\"items\":" + schema + "}", _ => "{\"properties\":{\"a.b\":{\"items\":" + schema + "}}}" };
}
