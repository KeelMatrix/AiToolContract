using System.Text.Json;
using Microsoft.Extensions.AI;
using Xunit;

namespace KeelMatrix.AiToolContract.Tests;

public sealed class ContractIntegrityTests
{
    private static readonly string[] DescriptionPaths = { "$.description", "$.inputSchema.description", "$.returnSchema.description" };
    [Fact]
    public void ExistingRequiredMemberCannotHideValueRestriction()
    {
        var candidate = CompatibilityRegressionTests.Capture("{\"properties\":{\"id\":{\"type\":\"string\"}}}");
        foreach (var schema in new[] { "{\"required\":[\"id\"]}", "{\"properties\":{\"id\":{}},\"required\":[\"id\"]}" })
        {
            var diff = AiToolContractVerifier.Compare(CompatibilityRegressionTests.Capture(schema), candidate);
            Assert.True(diff.HasBreaking);
            Assert.Throws<AiToolContractException>(() => AiToolContractVerifier.Accept(candidate, diff));
        }
    }

    [Fact]
    public void MalformedUnicodeNeverCapturesOrEscapesAsArgumentException()
    {
        using var schema = JsonDocument.Parse("{}");
        var capture = AiToolContractCapture.Capture(new AITool[] { AIFunctionFactory.CreateDeclaration("\ud800", null, schema.RootElement) });
        Assert.False(capture.Succeeded);
        var error = Assert.Throws<AiToolContractException>(() => AiToolContractJson.Parse("{\"schemaVersion\":1,\"tools\":[],\"\ud800\":0}"));
        Assert.Equal(AiToolDiagnosticKind.MalformedBaseline, error.Diagnostic.Kind);
    }

    [Fact]
    public void OversizedElementIsRejectedBeforeCopying()
    {
        using var schema = JsonDocument.Parse("{\"description\":\"" + new string('a', 1_000_000) + "\"}");
        var tools = new AITool[] { AIFunctionFactory.CreateDeclaration("tool", null, schema.RootElement) };
        var limits = new AiToolContractLimits { MaxSchemaBytes = 2 };
        AiToolContractCapture.Capture(tools, limits);
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.False(AiToolContractCapture.Capture(tools, limits).Succeeded);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 32_000);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void DescriptionLocationsIdentifyTheirRole(int changed)
    {
        using var oldSchema = JsonDocument.Parse("{\"description\":\"old\"}");
        using var newSchema = JsonDocument.Parse("{\"description\":\"new\"}");
        var baseline = AiToolContractCapture.Capture(new AITool[] { AIFunctionFactory.CreateDeclaration("tool", "old", oldSchema.RootElement, oldSchema.RootElement) }).Baseline!;
        var candidate = AiToolContractCapture.Capture(new AITool[] { AIFunctionFactory.CreateDeclaration("tool", (changed & 1) != 0 ? "new" : "old", (changed & 2) != 0 ? newSchema.RootElement : oldSchema.RootElement, (changed & 4) != 0 ? newSchema.RootElement : oldSchema.RootElement) }).Baseline!;
        var diff = AiToolContractVerifier.Compare(baseline, candidate);
        Assert.Equal(DescriptionPaths.Where((_, index) => (changed & (1 << index)) != 0), diff.Changes.Select(c => c.Path).OrderBy(p => p, StringComparer.Ordinal));
        Assert.Equal(diff.Changes.Count, diff.Changes.Select(c => (c.ToolName, c.Kind, c.Compatibility, c.Path)).Distinct().Count());
        Assert.All(diff.Changes, c => { Assert.Equal("tool", c.ToolName); Assert.Equal(AiToolChangeKind.DescriptionChanged, c.Kind); Assert.Equal(AiToolCompatibility.Risky, c.Compatibility); });
    }

    [Theory]
    [InlineData("")]
    [InlineData("description")]
    [InlineData("a.properties.b")]
    [InlineData("[]\"\\/~\n")]
    [InlineData("é😀")]
    public void NestedDescriptionLocationsKeepRoleAndEscapedName(string name)
    {
        var quoted = JsonSerializer.Serialize(name);
        var oldText = "{\"properties\":{" + quoted + ":{\"items\":{\"description\":\"old\"}}}}";
        var newText = oldText.Replace("old", "new", StringComparison.Ordinal);
        var baseline = CompatibilityRegressionTests.Capture(oldText, oldText);
        var candidate = CompatibilityRegressionTests.Capture(newText, newText);
        var diff = AiToolContractVerifier.Compare(baseline, candidate);
        var suffix = ".properties[" + quoted + "].items.description";
        Assert.Equal(2, diff.Changes.Count);
        Assert.Contains(diff.Changes, c => c.Path == "$.inputSchema" + suffix);
        Assert.Contains(diff.Changes, c => c.Path == "$.returnSchema" + suffix);
        Assert.Equal(2, diff.Changes.Select(c => (c.ToolName, c.Kind, c.Compatibility, c.Path)).Distinct().Count());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ByteLimitDiagnosticsIdentifySchemaRole(bool output)
    {
        using var schema = JsonDocument.Parse("{\"description\":\"long\"}");
        using var empty = JsonDocument.Parse("{}");
        var tools = new AITool[] { AIFunctionFactory.CreateDeclaration("tool", null, output ? empty.RootElement : schema.RootElement, output ? schema.RootElement : null) };
        var limits = new AiToolContractLimits { MaxSchemaBytes = 2 };
        var role = output ? "$.returnSchema" : "$.inputSchema";
        var capture = AiToolContractCapture.Capture(tools, limits);
        Assert.Contains(role, capture.Diagnostic!.Message, StringComparison.Ordinal);
        var baseline = AiToolContractCapture.Capture(tools).Baseline!;
        var error = Assert.Throws<AiToolContractException>(() => AiToolContractVerifier.Compare(baseline, baseline, limits));
        Assert.Contains(role, error.Diagnostic.Message, StringComparison.Ordinal);
    }
}
