using System.Text;
using System.Text.Json;
using Xunit;

namespace KeelMatrix.AiToolContract.Tests;

public sealed class SchemaFailureLocationTests
{
    public static IEnumerable<object[]> Counters()
    {
        yield return new object[] { "{\"description\":\"x\",\"format\":\"x\"}", 2, 0, "format" };
        yield return new object[] { "{\"properties\":{\"a\":{},\"b\":{}}}", 3, 0, "properties" };
        yield return new object[] { "{\"default\":{\"a\":1,\"b\":2}}", 3, 0, "default" };
        yield return new object[] { "{\"enum\":[{\"a\":1,\"b\":2}]}", 3, 0, "enum" };
        yield return new object[] { "{\"enum\":[1,2]}", 0, 2, "enum" };
        yield return new object[] { "{\"required\":[\"a\",\"b\"]}", 0, 2, "required" };
        yield return new object[] { "{\"type\":[\"string\",\"null\"]}", 0, 2, "type" };
        yield return new object[] { "{\"default\":[1,2]}", 0, 2, "default" };
        yield return new object[] { "{\"enum\":[[1,2]]}", 0, 3, "enum" };
    }

    [Theory]
    [InlineData("\"$ref\":\"#/x\"")]
    [InlineData("\"default\":0")]
    [InlineData("\"format\":\"date\"")]
    [InlineData("\"type\":\"string\"")]
    [InlineData("\"enum\":[0]")]
    [InlineData("\"items\":{}")]
    [InlineData("\"minimum\":0")]
    [InlineData("\"maximum\":0")]
    [InlineData("\"exclusiveMinimum\":0")]
    [InlineData("\"exclusiveMaximum\":0")]
    [InlineData("\"minLength\":0")]
    [InlineData("\"maxLength\":0")]
    [InlineData("\"minItems\":0")]
    [InlineData("\"maxItems\":0")]
    [InlineData("\"required\":[\"p\"]")]
    public void EverySchemaKeywordCounterKeepsContext(string member)
    {
        foreach (var output in new[] { false, true })
        {
            var schema = "{\"description\":\"x\"," + member + "}";
            var exact = new AiToolContractLimits { MaxProperties = 2 };
            var low = new AiToolContractLimits { MaxProperties = 1 };
            var tools = new[] { RecursiveRequiredTests.Declaration(schema, output) };
            var capture = AiToolContractCapture.Capture(tools, exact);
            Assert.True(capture.Succeeded, capture.Diagnostic?.Message);
            var baseline = capture.Baseline!;
            var json = AiToolContractJson.Serialize(baseline, exact);
            Assert.True(AiToolContractVerifier.Compare(baseline, AiToolContractJson.Parse(json, exact), exact).IsClean);
            var path = output ? "$.returnSchema" : "$.inputSchema";
            Check(AiToolContractCapture.Capture(tools, low).Diagnostic!, path, "property", AiToolDiagnosticKind.CanonicalizationOrResourceLimit);
            Check(Assert.Throws<AiToolContractException>(() => AiToolContractJson.Parse(json, low)).Diagnostic, path, "property", AiToolDiagnosticKind.CanonicalizationOrResourceLimit);
            Check(Assert.Throws<AiToolContractException>(() => AiToolContractJson.Serialize(baseline, low)).Diagnostic, path, "property", AiToolDiagnosticKind.CanonicalizationOrResourceLimit);
            Check(Assert.Throws<AiToolContractException>(() => AiToolContractVerifier.Compare(baseline, baseline, low)).Diagnostic, path, "property", AiToolDiagnosticKind.CanonicalizationOrResourceLimit);
        }
    }

    [Theory]
    [MemberData(nameof(Counters))]
    public void AllCounterExitsKeepSchemaRoleAcrossEntryPoints(string schema, int properties, int items, string keyword)
    {
        foreach (var output in new[] { false, true })
            foreach (var nested in new[] { false, true })
            {
                var text = nested ? "{\"properties\":{\"a.b\":{\"items\":" + schema + "}}}" : schema;
                var exact = new AiToolContractLimits { MaxProperties = properties == 0 ? 100 : properties + (nested ? 3 : 0), MaxArrayItems = items == 0 ? 100 : items };
                var low = new AiToolContractLimits { MaxProperties = exact.MaxProperties - (properties == 0 ? 0 : 1), MaxArrayItems = exact.MaxArrayItems - (items == 0 ? 0 : 1) };
                var tools = new[] { RecursiveRequiredTests.Declaration(text, output) };
                var capture = AiToolContractCapture.Capture(tools, exact);
                Assert.True(capture.Succeeded, capture.Diagnostic?.Message);
                var baseline = capture.Baseline!;
                var json = AiToolContractJson.Serialize(baseline, exact);
                Assert.True(AiToolContractVerifier.Compare(baseline, AiToolContractJson.Parse(json, exact), exact).IsClean);
                var path = (output ? "$.returnSchema" : "$.inputSchema") + (nested ? ".properties[\"a.b\"].items" : "");
                var failure = AiToolContractCapture.Capture(tools, low);
                Assert.False(failure.Succeeded);
                Check(failure.Diagnostic!, path, keyword, AiToolDiagnosticKind.CanonicalizationOrResourceLimit);
                Check(Assert.Throws<AiToolContractException>(() => AiToolContractJson.Parse(json, low)).Diagnostic, path, keyword, AiToolDiagnosticKind.CanonicalizationOrResourceLimit);
                Check(Assert.Throws<AiToolContractException>(() => AiToolContractJson.Serialize(baseline, low)).Diagnostic, path, keyword, AiToolDiagnosticKind.CanonicalizationOrResourceLimit);
                Check(Assert.Throws<AiToolContractException>(() => AiToolContractVerifier.Compare(baseline, baseline, low)).Diagnostic, path, keyword, AiToolDiagnosticKind.CanonicalizationOrResourceLimit);
                var verification = AiToolContractVerifier.Verify(baseline, tools, low);
                Assert.False(verification.Capture.Succeeded);
                Check(verification.Capture.Diagnostic!, path, keyword, AiToolDiagnosticKind.CanonicalizationOrResourceLimit);
            }
    }

    [Theory]
    [InlineData("")]
    [InlineData("a.b")]
    [InlineData("[]\"\\\n")]
    [InlineData("é😀")]
    public void DynamicCounterLocationsEscapeNames(string name)
    {
        var key = JsonSerializer.Serialize(name);
        var schema = "{\"properties\":{" + key + ":{\"default\":{\"a\":1,\"b\":2}}}}";
        foreach (var output in new[] { false, true })
        {
            var tools = new[] { RecursiveRequiredTests.Declaration(schema, output) };
            var baseline = AiToolContractCapture.Capture(tools).Baseline!;
            var limits = new AiToolContractLimits { MaxProperties = 4 };
            var path = (output ? "$.returnSchema" : "$.inputSchema") + ".properties[" + key + "].default";
            Check(AiToolContractCapture.Capture(tools, limits).Diagnostic!, path, "default", AiToolDiagnosticKind.CanonicalizationOrResourceLimit);
            Check(Assert.Throws<AiToolContractException>(() => AiToolContractJson.Serialize(baseline, limits)).Diagnostic, path, "default", AiToolDiagnosticKind.CanonicalizationOrResourceLimit);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EnumKeyExpansionKeepsItsOriginalLocation(bool output)
    {
        var schema = "{\"properties\":{\"a.b\":{\"enum\":[\"" + new string('é', 20) + "\"]}}}";
        var limits = new AiToolContractLimits { MaxSchemaBytes = Encoding.UTF8.GetByteCount(schema) };
        var path = (output ? "$.returnSchema" : "$.inputSchema") + ".properties[\"a.b\"].enum[0]";
        var tools = new[] { RecursiveRequiredTests.Declaration(schema, output) };
        Check(AiToolContractCapture.Capture(tools, limits).Diagnostic!, path, "enum", AiToolDiagnosticKind.CanonicalizationOrResourceLimit);
        Check(Assert.Throws<AiToolContractException>(() => AiToolContractJson.Parse(Envelope(schema, output), limits)).Diagnostic, path, "enum", AiToolDiagnosticKind.CanonicalizationOrResourceLimit);
        var baseline = AiToolContractCapture.Capture(tools).Baseline!;
        // Once normalized, byte-budget rejection belongs to the schema root.
        var role = output ? "$.returnSchema" : "$.inputSchema";
        Check(Assert.Throws<AiToolContractException>(() => AiToolContractJson.Serialize(baseline, limits)).Diagnostic, role, "byte", AiToolDiagnosticKind.CanonicalizationOrResourceLimit);
        Check(Assert.Throws<AiToolContractException>(() => AiToolContractVerifier.Compare(baseline, baseline, limits)).Diagnostic, role, "byte", AiToolDiagnosticKind.CanonicalizationOrResourceLimit);
        var canonicalBytes = Encoding.UTF8.GetByteCount(output ? baseline.Tools[0].ReturnSchemaJson! : baseline.Tools[0].InputSchemaJson);
        var exact = new AiToolContractLimits { MaxSchemaBytes = canonicalBytes };
        Assert.True(AiToolContractCapture.Capture(tools, exact).Succeeded);
        Assert.True(AiToolContractVerifier.Compare(baseline, AiToolContractJson.Parse(AiToolContractJson.Serialize(baseline, exact), exact), exact).IsClean);
        var overflow = new AiToolContractLimits { MaxSchemaBytes = canonicalBytes - 1 };
        Check(AiToolContractCapture.Capture(tools, overflow).Diagnostic!, role, "byte", AiToolDiagnosticKind.CanonicalizationOrResourceLimit);
        Check(Assert.Throws<AiToolContractException>(() => AiToolContractJson.Parse(Envelope(schema, output), overflow)).Diagnostic, role, "byte", AiToolDiagnosticKind.CanonicalizationOrResourceLimit);
        Check(Assert.Throws<AiToolContractException>(() => AiToolContractJson.Serialize(baseline, overflow)).Diagnostic, role, "byte", AiToolDiagnosticKind.CanonicalizationOrResourceLimit);
        Check(Assert.Throws<AiToolContractException>(() => AiToolContractVerifier.Compare(baseline, baseline, overflow)).Diagnostic, role, "byte", AiToolDiagnosticKind.CanonicalizationOrResourceLimit);
    }

    [Theory]
    [InlineData("{\"\\ud800\":0}", "")]
    [InlineData("{\"description\":\"\\ud800\"}", ".description")]
    [InlineData("{\"format\":\"\\udfff\"}", ".format")]
    [InlineData("{\"$ref\":\"\\ud800\"}", ".$ref")]
    [InlineData("{\"properties\":{\"\\ud800\":{}}}", ".properties")]
    [InlineData("{\"required\":[\"\\ud800\"]}", ".required[0]")]
    [InlineData("{\"type\":[\"string\",\"\\ud800\"]}", ".type[1]")]
    [InlineData("{\"default\":{\"\\ud800\":0}}", ".default")]
    [InlineData("{\"default\":[\"\\ud800\"]}", ".default[0]")]
    [InlineData("{\"enum\":[{\"\\ud800\":0}]}", ".enum[0]")]
    [InlineData("{\"enum\":[\"\\ud800\"]}", ".enum[0]")]
    public void MalformedSchemaDecodingKeepsRoleAndNearestValidLocation(string schema, string suffix)
    {
        foreach (var output in new[] { false, true })
        {
            var text = "{\"properties\":{\"a.b\":{\"items\":" + schema + "}}}";
            var path = (output ? "$.returnSchema" : "$.inputSchema") + ".properties[\"a.b\"].items" + suffix;
            var capture = AiToolContractCapture.Capture(new[] { RecursiveRequiredTests.Declaration(text, output) });
            Assert.False(capture.Succeeded);
            Assert.Null(capture.Baseline);
            Check(capture.Diagnostic!, path, "", AiToolDiagnosticKind.CaptureFailure);
            Check(Assert.Throws<AiToolContractException>(() => AiToolContractJson.Parse(Envelope(text, output))).Diagnostic, path, "", AiToolDiagnosticKind.MalformedBaseline);
        }
    }

    private static string Envelope(string schema, bool output) => "{\"schemaVersion\":1,\"tools\":[{\"name\":\"tool\",\"description\":null,\"inputSchema\":" + (output ? "{}" : schema) + ",\"returnSchema\":" + (output ? schema : "null") + ",\"requiresApproval\":false}]}";

    private static void Check(AiToolContractDiagnostic diagnostic, string path, string keyword, AiToolDiagnosticKind kind)
    {
        Assert.NotNull(diagnostic);
        Assert.Equal(kind, diagnostic.Kind);
        Assert.Contains(path, diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains(keyword, diagnostic.Message, StringComparison.Ordinal);
    }
}
