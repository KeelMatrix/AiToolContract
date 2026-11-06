using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Xunit;

namespace KeelMatrix.AiToolContract.Tests;

public sealed class ResourceRegressionTests
{
    [Theory]
    [InlineData("{\"type\":[\"string\"],\"enum\":[\"a\"]}", 2)]
    [InlineData("{\"properties\":{},\"type\":\"string\",\"enum\":[\"a\"]}", 2)]
    [InlineData("{\"properties\":{  },\"type\":[\"string\"],\"enum\":[\"a\"]}", 2)]
    public void RepresentationOnlyContainersUseNormalizedCounters(string schema, int properties)
    {
        var limits = new AiToolContractLimits { MaxArrayItems = 1, MaxProperties = properties };
        using var document = JsonDocument.Parse(schema);
        using var emptyDocument = JsonDocument.Parse("{}");
        foreach (var output in new[] { false, true })
        {
            var captured = AiToolContractCapture.Capture(new AITool[] { AIFunctionFactory.CreateDeclaration("tool", null, output ? emptyDocument.RootElement.Clone() : document.RootElement.Clone(), output ? document.RootElement.Clone() : null) }, limits);
            Assert.True(captured.Succeeded, captured.Diagnostic?.Message);
            var json = AiToolContractJson.Serialize(captured.Baseline!, limits);
            Assert.True(AiToolContractVerifier.Compare(captured.Baseline!, AiToolContractJson.Parse(json, limits), limits).IsClean);
        }
    }

    [Fact]
    public void AggregateMetadataBudgetAcceptsExactBoundaryAndRejectsNextByte()
    {
        var limits = new AiToolContractLimits { MaxTools = 1, MaxSchemaBytes = 2 };
        using var document = JsonDocument.Parse("{}");
        AiToolContractCaptureResult CaptureDescription(string text) => AiToolContractCapture.Capture(new AITool[] { AIFunctionFactory.CreateDeclaration("tool", text, document.RootElement.Clone()) }, limits);
        var empty = CaptureDescription(string.Empty).Baseline!;
        var available = (int)limits.GetBaselineByteLimit() - Encoding.UTF8.GetByteCount(AiToolContractJson.Serialize(empty, limits));
        var exact = CaptureDescription(new string('a', available));
        Assert.True(exact.Succeeded, exact.Diagnostic?.Message);
        Assert.Equal(limits.GetBaselineByteLimit(), Encoding.UTF8.GetByteCount(AiToolContractJson.Serialize(exact.Baseline!, limits)));
        Assert.True(AiToolContractVerifier.Compare(exact.Baseline!, AiToolContractJson.Parse(AiToolContractJson.Serialize(exact.Baseline!, limits), limits), limits).IsClean);
        Assert.False(CaptureDescription(new string('a', available + 1)).Succeeded);
        var two = AiToolContractCapture.Capture(new AITool[]
        {
            AIFunctionFactory.CreateDeclaration("one", new string('a', 240), document.RootElement.Clone()),
            AIFunctionFactory.CreateDeclaration("two", new string('a', 240), document.RootElement.Clone())
        }).Baseline!;
        AssertResource(() => AiToolContractJson.Serialize(two, new AiToolContractLimits { MaxTools = 2, MaxSchemaBytes = 2 }));
    }

    [Theory]
    [InlineData("{\"enum\":[[1,2]]}", 1, 3, 3)]
    [InlineData("{\"default\":[[1,2]]}", 1, 3, 4)]
    [InlineData("{\"enum\":[{\"a\":{\"b\":1}}]}", 3, 1, 4)]
    [InlineData("{\"default\":{\"a\":{\"b\":1}}}", 3, 1, 4)]
    public void RecursiveValuesRespectTightenedLimitsAcrossEntryPoints(string schema, int properties, int items, int depth)
    {
        foreach (var output in new[] { false, true })
        {
            var baseline = CompatibilityRegressionTests.Capture(output ? "{}" : schema, output ? schema : null);
            var exact = new AiToolContractLimits { MaxProperties = properties, MaxArrayItems = items, MaxSchemaDepth = depth };
            var serialized = AiToolContractJson.Serialize(baseline, exact);
            Assert.True(AiToolContractVerifier.Compare(baseline, AiToolContractJson.Parse(serialized, exact), exact).IsClean);
            var tightened = new List<AiToolContractLimits> { new() { MaxSchemaDepth = depth - 1 } };
            if (properties > 1)
                tightened.Add(new() { MaxProperties = properties - 1 });
            if (items > 1)
                tightened.Add(new() { MaxArrayItems = items - 1 });
            foreach (var limits in tightened)
            {
                using var schemaDocument = JsonDocument.Parse(schema);
                using var emptyDocument = JsonDocument.Parse("{}");
                var declaration = AIFunctionFactory.CreateDeclaration("tool", null, output ? emptyDocument.RootElement.Clone() : schemaDocument.RootElement.Clone(), output ? schemaDocument.RootElement.Clone() : null);
                Assert.False(AiToolContractCapture.Capture(new AITool[] { declaration }, limits).Succeeded);
                AssertResource(() => AiToolContractVerifier.Compare(baseline, baseline, limits));
                AssertResource(() => AiToolContractJson.Serialize(baseline, limits));
                AssertResource(() => AiToolContractJson.Parse(serialized, limits));
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(12)]
    public void LogicalDepthRoundTripsAtBoundaryAndRejectsFirstOverflow(int depth)
    {
        foreach (var output in new[] { false, true })
            foreach (var mode in new[] { "properties", "items", "mixed" })
            {
                var schema = "{}";
                for (var level = 1; level < depth; level++)
                    schema = mode == "items" || mode == "mixed" && level % 2 == 0 ? "{\"items\":" + schema + "}" : "{\"properties\":{\"a\":" + schema + "}}";
                var baseline = CompatibilityRegressionTests.Capture(output ? "{}" : schema, output ? schema : null);
                var limits = new AiToolContractLimits { MaxSchemaDepth = depth };
                var json = AiToolContractJson.Serialize(baseline, limits);
                Assert.True(AiToolContractVerifier.Compare(baseline, AiToolContractJson.Parse(json, limits), limits).IsClean);
                if (depth > 1)
                    AssertResource(() => AiToolContractJson.Parse(json, new AiToolContractLimits { MaxSchemaDepth = depth - 1 }));
            }
    }

    [Theory]
    [InlineData("\u0001")]
    [InlineData("é")]
    [InlineData("\"\\\n")]
    [InlineData("😀")]
    public void EscapedMetadataIsRejectedBeforeProportionalAllocation(string unit)
    {
        var text = string.Concat(Enumerable.Repeat(unit, 100_000));
        using var document = JsonDocument.Parse("{}");
        foreach (var name in new[] { false, true })
        {
            var declaration = AIFunctionFactory.CreateDeclaration(name ? text : "tool", name ? null : text, document.RootElement.Clone());
            var limits = new AiToolContractLimits { MaxTools = 1, MaxSchemaBytes = 2 };
            var baseline = AiToolContractCapture.Capture(new AITool[] { declaration }).Baseline!;
            Assert.NotNull(baseline);
            Action[] operations =
            {
                () => Assert.False(AiToolContractCapture.Capture(new AITool[] { declaration }, limits).Succeeded),
                () => AssertResource(() => AiToolContractVerifier.Compare(baseline, baseline, limits)),
                () => AssertResource(() => AiToolContractJson.Serialize(baseline, limits))
            };
            foreach (var operation in operations)
            {
                operation();
                var before = GC.GetAllocatedBytesForCurrentThread();
                operation();
                Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 128_000);
            }
        }
    }

    [Fact]
    public void MeasurementUsesTheActualJsonEncoderAtEveryBoundary()
    {
        foreach (var text in new[] { "", "plain", "\"\\\b\t\n\f\r", "\u0000<>+&'", "é中文😀", "\ud800", "\udfff" })
        {
            var encoded = JsonSerializer.Serialize(text);
            var bytes = Encoding.UTF8.GetByteCount(encoded);
            var writer = new CanonicalJsonWriter(bytes);
            writer.AppendString(text);
            Assert.Equal(encoded, writer.ToString());
            AssertResource(() => new CanonicalJsonWriter(bytes - 1, countOnly: true).AppendString(text));
        }
    }

    [Fact]
    public void LongTrailingZeroCoefficientsNormalizeBeforeIntegerConstruction()
    {
        var zeros = new string('0', 100_000);
        var watch = Stopwatch.StartNew();
        var baseline = CompatibilityRegressionTests.Capture("{\"minimum\":1" + zeros + "}");
        var equivalent = CompatibilityRegressionTests.Capture("{\"minimum\":1e100000}");
        Assert.True(AiToolContractVerifier.Compare(baseline, equivalent).IsClean);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), watch.Elapsed.ToString());
        foreach (var pair in new[] { ("-12000.000e-2", "-12e1"), ("0.0000e99999", "0"), ("12e+000003", "12000"), ("1.2000e-9999", "12e-10000") })
            Assert.True(AiToolContractVerifier.Compare(CompatibilityRegressionTests.Capture("{\"minimum\":" + pair.Item1 + "}"), CompatibilityRegressionTests.Capture("{\"minimum\":" + pair.Item2 + "}")).IsClean);
    }

    private static void AssertResource(Action operation)
    {
        var exception = Assert.Throws<AiToolContractException>(operation);
        Assert.Equal(AiToolDiagnosticKind.CanonicalizationOrResourceLimit, exception.Diagnostic.Kind);
    }
}
