using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Xunit;
using Xunit.Abstractions;

namespace KeelMatrix.AiToolContract.Tests;

public sealed class EnumResourceTests
{
    private readonly ITestOutputHelper _output;

    public EnumResourceTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RejectionAllocationDoesNotGrowWithCallerElement(bool output, bool escaped)
    {
        foreach (var length in new[] { 10_000, 100_000, 1_000_000 })
        {
            var text = string.Concat(Enumerable.Repeat(escaped ? "\\u0001" : "a", length));
            using var document = JsonDocument.Parse("{\"description\":\"" + text + "\"}");
            using var empty = JsonDocument.Parse("{}");
            var tools = new AITool[] { AIFunctionFactory.CreateDeclaration("tool", null, output ? empty.RootElement : document.RootElement, output ? document.RootElement : null) };
            var limits = new AiToolContractLimits { MaxSchemaBytes = 2 };
            Action operation = () =>
            {
                var result = AiToolContractCapture.Capture(tools, limits);
                Assert.False(result.Succeeded);
                Assert.Equal(AiToolDiagnosticKind.CanonicalizationOrResourceLimit, result.Diagnostic!.Kind);
            };
            var allocation = Measure(operation);
            _output.WriteLine("rejection output={0} escaped={1} length={2} allocated={3}", output, escaped, length, allocation);
            Assert.InRange(allocation, 0, 32_000);
        }
    }

    [Theory]
    [InlineData("{\"description\":\"plain\"}")]
    [InlineData("{ \"description\" : \"\\u0001\\u00e9\" }")]
    [InlineData("{\"description\":\"é😀\"}")]
    public void RawAndCanonicalByteBoundariesAreBothEnforced(string schema)
    {
        using var document = JsonDocument.Parse(schema);
        foreach (var output in new[] { false, true })
        {
            var baseline = CompatibilityRegressionTests.Capture(output ? "{}" : schema, output ? schema : null);
            var canonical = output ? baseline.Tools[0].ReturnSchemaJson! : baseline.Tools[0].InputSchemaJson;
            var bytes = Math.Max(Encoding.UTF8.GetByteCount(schema), Encoding.UTF8.GetByteCount(canonical));
            using var empty = JsonDocument.Parse("{}");
            var tools = new AITool[] { AIFunctionFactory.CreateDeclaration("tool", null, output ? empty.RootElement : document.RootElement, output ? document.RootElement : null) };
            var exact = new AiToolContractLimits { MaxSchemaBytes = bytes };
            Assert.True(AiToolContractCapture.Capture(tools, exact).Succeeded);
            var json = AiToolContractJson.Serialize(baseline, exact);
            Assert.True(AiToolContractVerifier.Compare(baseline, AiToolContractJson.Parse(json, exact), exact).IsClean);
            Assert.False(AiToolContractCapture.Capture(tools, new AiToolContractLimits { MaxSchemaBytes = bytes - 1 }).Succeeded);
        }
    }

    [Theory]
    [InlineData(false, 1024)]
    [InlineData(true, 1024)]
    [InlineData(false, 10000)]
    [InlineData(true, 10000)]
    public void LargeEnumsUseBoundedAllocationsAcrossCaptureParseAndComparison(bool output, int count)
    {
        var values = Enumerable.Range(100000, 20000).Where(n => n % 10 != 0).Take(count).Select(n => n.ToString(CultureInfo.InvariantCulture)).ToArray();
        var schema = "{\"enum\":[" + string.Join(",", values) + "]}";
        using var document = JsonDocument.Parse(schema);
        using var empty = JsonDocument.Parse("{}");
        var tools = new AITool[] { AIFunctionFactory.CreateDeclaration("tool", null, output ? empty.RootElement : document.RootElement, output ? document.RootElement : null) };
        var baseline = AiToolContractCapture.Capture(tools).Baseline!;
        var json = AiToolContractJson.Serialize(baseline);
        var candidateSchema = schema[..^1] + ",\"description\":\"changed\"}";
        var candidate = CompatibilityRegressionTests.Capture(output ? "{}" : candidateSchema, output ? candidateSchema : null);
        var captureBytes = Measure(() => Assert.True(AiToolContractCapture.Capture(tools).Succeeded));
        var parseBytes = Measure(() => Assert.NotNull(AiToolContractJson.Parse(json)));
        var compareBytes = Measure(() => Assert.Equal(AiToolChangeKind.DescriptionChanged, Assert.Single(AiToolContractVerifier.Compare(baseline, candidate, new AiToolContractLimits { MaxChanges = 1 }).Changes).Kind));
        _output.WriteLine("enum output={0} count={1} capture={2} parse={3} compare={4}", output, count, captureBytes, parseBytes, compareBytes);
        Assert.InRange(captureBytes, 0, count * 4000L + 1_000_000);
        Assert.InRange(parseBytes, 0, count * 6000L + 1_000_000);
        Assert.InRange(compareBytes, 0, count * 1000L + 1_000_000);
        Assert.True(AiToolContractVerifier.Compare(baseline, AiToolContractJson.Parse(json)).IsClean);
        var tightened = new AiToolContractLimits { MaxArrayItems = count - 1 };
        AssertResource(() => AiToolContractVerifier.Compare(baseline, candidate, tightened));
        AssertResource(() => AiToolContractJson.Serialize(baseline, tightened));
        AssertResource(() => AiToolContractJson.Parse(json, tightened));
    }

    [Theory]
    [InlineData("1", "1.0")]
    [InlineData("1", "1e0")]
    [InlineData("0", "-0e999999")]
    [InlineData("12e99999", "1.20e100000")]
    [InlineData("[1,{\"a\":2,\"b\":3}]", "[1.0,{\"b\":3e0,\"a\":2.0}]")]
    [InlineData("{\"x\":[null,true,\"é\",1]}", "{\"x\":[null,true,\"\\u00e9\",1.0]}")]
    public void SemanticDuplicatesAreRejectedEvenAtTheEnd(string first, string duplicate)
    {
        var unique = string.Join(",", Enumerable.Range(100000, 2000));
        var schema = "{\"enum\":[" + first + "," + unique + "," + duplicate + "]}";
        using var document = JsonDocument.Parse(schema);
        using var empty = JsonDocument.Parse("{}");
        foreach (var output in new[] { false, true })
        {
            var capture = AiToolContractCapture.Capture(new AITool[] { AIFunctionFactory.CreateDeclaration("tool", null, output ? empty.RootElement : document.RootElement, output ? document.RootElement : null) });
            Assert.False(capture.Succeeded);
            Assert.Equal(AiToolDiagnosticKind.CaptureFailure, capture.Diagnostic!.Kind);
            var baselineJson = "{\"schemaVersion\":1,\"tools\":[{\"name\":\"tool\",\"description\":null,\"inputSchema\":" + (output ? "{}" : schema) + ",\"returnSchema\":" + (output ? schema : "null") + ",\"requiresApproval\":false}]}";
            Assert.Equal(AiToolDiagnosticKind.MalformedBaseline, Assert.Throws<AiToolContractException>(() => AiToolContractJson.Parse(baselineJson)).Diagnostic.Kind);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SetRelationsRemainExactForNestedNumbersAndSiblingChanges(bool output)
    {
        var values = Enumerable.Range(1, 2000).Select(n => "{\"n\":[" + n.ToString(CultureInfo.InvariantCulture) + "]}").ToArray();
        var oldSchema = "{\"enum\":[" + string.Join(",", values) + "]}";
        var baseline = CompatibilityRegressionTests.Capture(output ? "{}" : oldSchema, output ? oldSchema : null);
        foreach (var mode in new[] { "identical", "subset", "superset", "replacement", "disjoint" })
        {
            IEnumerable<string> nextValues = mode switch { "identical" => values.AsEnumerable().Reverse(), "subset" => values.Take(values.Length - 1), "superset" => values.Append("{\"n\":[2001]}"), "replacement" => values.Skip(1).Append("{\"n\":[2001]}"), _ => new[] { "{\"n\":[-1]}" } };
            foreach (var sibling in new[] { "", ",\"description\":\"changed\"", ",\"maximum\":3", ",\"default\":0" })
            {
                var schema = "{\"enum\":[" + string.Join(",", nextValues) + "]" + sibling + "}";
                var candidate = CompatibilityRegressionTests.Capture(output ? "{}" : schema, output ? schema : null);
                foreach (var reverse in new[] { false, true })
                {
                    var diff = AiToolContractVerifier.Compare(reverse ? candidate : baseline, reverse ? baseline : candidate);
                    var enumChanges = diff.Changes.Where(c => c.Path.EndsWith(".enum", StringComparison.Ordinal)).ToArray();
                    if (mode == "identical")
                        Assert.Empty(enumChanges);
                    else
                    {
                        var change = Assert.Single(enumChanges);
                        if (mode is "replacement" or "disjoint")
                            Assert.Equal(AiToolChangeKind.Unsupported, change.Kind);
                        else
                            Assert.Equal((mode == "subset") != reverse != output ? AiToolCompatibility.Breaking : AiToolCompatibility.Additive, change.Compatibility);
                    }
                    var target = reverse ? baseline : candidate;
                    if (diff.HasUnsupported)
                        Assert.Throws<AiToolContractException>(() => AiToolContractVerifier.AcceptWithBreakingReview(target, diff));
                    else
                        Assert.Same(target, AiToolContractVerifier.AcceptWithBreakingReview(target, diff));
                    if (diff.HasBreaking || diff.HasUnsupported)
                        Assert.Throws<AiToolContractException>(() => AiToolContractVerifier.Accept(target, diff));
                    else
                        Assert.Same(target, AiToolContractVerifier.Accept(target, diff));
                }
            }
        }
    }

    private static long Measure(Action operation)
    {
        operation();
        var before = GC.GetAllocatedBytesForCurrentThread();
        operation();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static void AssertResource(Action operation) => Assert.Equal(AiToolDiagnosticKind.CanonicalizationOrResourceLimit, Assert.Throws<AiToolContractException>(operation).Diagnostic.Kind);
}
