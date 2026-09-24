using System.ComponentModel;
using System.Text.Json;
using KeelMatrix.AiToolContract;
using Microsoft.Extensions.AI;
using Xunit;

namespace KeelMatrix.AiToolContract.Tests;

public sealed class ContractTests
{
    [Fact]
    public void CaptureReadsRealAIFunctionMetadataWithoutInvokingIt()
    {
        var invoked = false;
        var function = AIFunctionFactory.Create((string query, int limit) =>
        {
            invoked = true;
            return query + limit;
        });

        var result = AiToolContractCapture.Capture(new AITool[] { function });

        Assert.True(result.Succeeded, result.Diagnostic?.Message);
        Assert.False(invoked);
        Assert.Contains("query", result.Baseline!.Tools[0].InputSchemaJson, StringComparison.Ordinal);
        Assert.NotNull(result.Baseline.Tools[0].ReturnSchemaJson);
    }

    [Fact]
    public void RealFrameworkFixtureWithDescriptionsDefaultsFormatsAndNestedValuesRoundTrips()
    {
        var function = AIFunctionFactory.Create((Func<string, int, DateTime?, Uri?, Guid?, SearchStatus, SearchOptions?, string[]?, string>)Search);

        var capture = AiToolContractCapture.Capture(new AITool[] { function });

        Assert.True(capture.Succeeded, capture.Diagnostic?.Message);
        var schema = capture.Baseline!.Tools[0].InputSchemaJson;
        Assert.Contains("description", schema, StringComparison.Ordinal);
        Assert.Contains("default", schema, StringComparison.Ordinal);
        Assert.Contains("format", schema, StringComparison.Ordinal);
        Assert.Contains("date-time", schema, StringComparison.Ordinal);
        Assert.Contains("uri", schema, StringComparison.Ordinal);
        Assert.Contains("uuid", schema, StringComparison.Ordinal);
        Assert.Contains("enum", schema, StringComparison.Ordinal);
        Assert.Contains("items", schema, StringComparison.Ordinal);
        Assert.Contains("properties", schema, StringComparison.Ordinal);

        var parsed = AiToolContractJson.Parse(AiToolContractJson.Serialize(capture.Baseline));
        var diff = AiToolContractVerifier.Compare(capture.Baseline, parsed);

        Assert.True(diff.IsClean, string.Join("; ", diff.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void ClosedSubsetHappyAndFailClosedCasesAreEnumerated()
    {
        var supported = new[]
        {
            "{\"$ref\":\"#/$defs/Order\"}",
            "{\"description\":\"A value\",\"default\":null,\"format\":\"date-time\",\"type\":[\"string\",\"null\"]}",
            "{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"string\"}},\"required\":[\"value\"]}",
            "{\"type\":\"array\",\"items\":{\"type\":\"integer\"},\"minItems\":1,\"maxItems\":10}",
            "{\"type\":\"number\",\"minimum\":1,\"maximum\":10,\"exclusiveMinimum\":0,\"exclusiveMaximum\":11}",
            "{\"type\":\"string\",\"minLength\":1,\"maxLength\":100,\"enum\":[\"one\",\"two\"]}"
        };
        var unsupported = new[]
        {
            "{\"allOf\":[{\"type\":\"string\"}]}",
            "{\"$defs\":{\"Order\":{\"type\":\"object\"}}}",
            "{\"additionalProperties\":false}",
            "{\"nullable\":true}",
            "{\"x-vendor-policy\":\"review\"}"
        };

        foreach (var schema in supported)
        {
            var capture = CaptureWithLimits(schema);
            Assert.True(capture.Succeeded, schema + ": " + capture.Diagnostic?.Message);
            var parsed = AiToolContractJson.Parse(AiToolContractJson.Serialize(capture.Baseline!));
            Assert.True(AiToolContractVerifier.Compare(capture.Baseline!, parsed).IsClean, schema);
        }
        foreach (var schema in unsupported)
        {
            var capture = CaptureWithLimits(schema);
            Assert.False(capture.Succeeded, schema);
            Assert.Equal(AiToolDiagnosticKind.UnsupportedClassification, capture.Diagnostic!.Kind);
        }
    }

    [Fact]
    public void CanonicalizationUsesTheNormalizedModelForOrderAndNumericLexicalForms()
    {
        var left = Capture("{\"type\":\"object\",\"properties\":{\"b\":{\"type\":\"integer\",\"minimum\":1.0},\"a\":{\"type\":\"string\"}},\"required\":[\"b\",\"a\"]}");
        var right = Capture("{ \"required\": [ \"a\", \"b\" ], \"properties\": { \"a\": { \"type\": \"string\" }, \"b\": { \"minimum\": 1e0, \"type\": \"integer\" } }, \"type\": \"object\" }");

        var diff = AiToolContractVerifier.Compare(left, right);

        Assert.True(diff.IsClean, string.Join("; ", diff.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.Equal(AiToolContractJson.Serialize(left), AiToolContractJson.Serialize(right));
    }

    [Fact]
    public void CanonicalizationOrderStabilityPropertyHolds()
    {
        var schemas = new[]
        {
            "{\"type\":\"object\",\"required\":[\"a\",\"b\"],\"properties\":{\"a\":{\"type\":\"string\"},\"b\":{\"type\":\"integer\"}}}",
            "{\"properties\":{\"b\":{\"type\":\"integer\"},\"a\":{\"type\":\"string\"}},\"type\":\"object\",\"required\":[\"b\",\"a\"]}",
            "{\"required\":[\"b\",\"a\"],\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"string\"},\"b\":{\"type\":\"integer\"}}}"
        };

        var expected = AiToolContractJson.Serialize(Capture(schemas[0]));
        foreach (var schema in schemas.Skip(1))
            Assert.Equal(expected, AiToolContractJson.Serialize(Capture(schema)));
    }

    [Fact]
    public void RequiredOptionalTypeEnumAndConstraintChangesAreClassified()
    {
        var baseline = Capture("{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"},\"state\":{\"enum\":[\"new\",\"open\"]},\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":100}}}");
        var candidate = Capture("{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"},\"region\":{\"type\":\"string\"},\"note\":{\"type\":\"string\"},\"state\":{\"enum\":[\"open\"]},\"limit\":{\"type\":\"integer\",\"minimum\":10,\"maximum\":50}},\"required\":[\"region\"]}");

        var diff = AiToolContractVerifier.Compare(baseline, candidate);

        Assert.Contains(diff.Changes, static change => change.Kind == AiToolChangeKind.RequiredParameterAdded && change.Compatibility == AiToolCompatibility.Breaking);
        Assert.Contains(diff.Changes, static change => change.Kind == AiToolChangeKind.OptionalParameterAdded && change.Compatibility == AiToolCompatibility.Additive);
        Assert.Contains(diff.Changes, static change => change.Kind == AiToolChangeKind.EnumNarrowed && change.Compatibility == AiToolCompatibility.Breaking);
        Assert.Equal(2, diff.Changes.Count(static change => change.Kind == AiToolChangeKind.ConstraintNarrowed));
    }

    [Fact]
    public void NullableUsesStandardTypeArraysAndOpenApiNullableFailsClosed()
    {
        var nullable = Capture("{\"type\":[\"string\",\"null\"]}");
        var nonNullable = Capture("{\"type\":\"string\"}");

        var diff = AiToolContractVerifier.Compare(nullable, nonNullable);

        Assert.Contains(diff.Changes, static change => change.Kind == AiToolChangeKind.TypeChanged && change.Compatibility == AiToolCompatibility.Breaking);
        var capture = CaptureWithLimits("{\"type\":\"string\",\"nullable\":true}");
        Assert.False(capture.Succeeded);
        Assert.Equal(AiToolDiagnosticKind.UnsupportedClassification, capture.Diagnostic!.Kind);
    }

    [Fact]
    public void NumericExponentBeyondInt64IsRepresentableAndNotMalformed()
    {
        const string schema = "{\"type\":\"number\",\"minimum\":1e9223372036854775808}";

        var capture = CaptureWithLimits(schema);

        Assert.True(capture.Succeeded, capture.Diagnostic?.Message);
        var parsed = AiToolContractJson.Parse(AiToolContractJson.Serialize(capture.Baseline!));
        Assert.True(AiToolContractVerifier.Compare(capture.Baseline!, parsed).IsClean);
    }

    [Fact]
    public void ValidRefIsCheckedAsUriReferenceAndTargetOrSiblingChangesAreUnsupported()
    {
        var baseline = Capture("{\"$ref\":\"#/$defs/Order\"}");
        var same = Capture("{\"$ref\":\"#/$defs/Order\"}");
        var targetChanged = Capture("{\"$ref\":\"#/$defs/Invoice\"}");
        var siblingChanged = Capture("{\"$ref\":\"#/$defs/Order\",\"description\":\"Order\"}");

        Assert.True(AiToolContractVerifier.Compare(baseline, same).IsClean);
        AssertUnsupported(AiToolContractVerifier.Compare(baseline, targetChanged));
        AssertUnsupported(AiToolContractVerifier.Compare(baseline, siblingChanged));

        var malformed = CaptureWithLimits("{\"$ref\":\"http://[invalid\"}");
        Assert.False(malformed.Succeeded);
        Assert.Equal(AiToolDiagnosticKind.CaptureFailure, malformed.Diagnostic!.Kind);
        var malformedBaseline = Assert.Throws<AiToolContractException>(() => AiToolContractJson.Parse(Envelope("{\"$ref\":\"http://[invalid\"}")));
        Assert.Equal(AiToolDiagnosticKind.MalformedBaseline, malformedBaseline.Diagnostic.Kind);
    }

    [Theory]
    [InlineData("{\"x-vendor-policy\":\"root\"}")]
    [InlineData("{\"type\":\"object\",\"properties\":{\"nested\":{\"x-vendor-policy\":\"deep\"}}}")]
    [InlineData("{\"type\":\"array\",\"items\":{\"properties\":{\"nested\":{\"unknown\":true}}}}")]
    public void UnknownKeywordsAtSeveralDepthsFailAtNormalization(string schema)
    {
        var capture = CaptureWithLimits(schema);

        Assert.False(capture.Succeeded);
        Assert.Equal(AiToolDiagnosticKind.UnsupportedClassification, capture.Diagnostic!.Kind);
    }

    [Theory]
    [InlineData("{\"type\":1}")]
    [InlineData("{\"type\":\"object\",\"properties\":[]}")]
    [InlineData("{\"type\":\"object\",\"required\":1}")]
    [InlineData("{\"type\":\"array\",\"items\":[]}")]
    public void WrongSchemaValueKindsFailClosed(string schema)
    {
        var capture = CaptureWithLimits(schema);

        Assert.False(capture.Succeeded);
        Assert.Contains(capture.Diagnostic!.Kind, new[] { AiToolDiagnosticKind.CaptureFailure, AiToolDiagnosticKind.MalformedBaseline, AiToolDiagnosticKind.UnsupportedClassification });
    }

    [Fact]
    public void EnvelopeRequiresEveryMemberAndRejectsUnknownMembers()
    {
        var valid = Envelope("{\"type\":\"object\"}");
        var mutations = new[]
        {
            valid.Replace("\"schemaVersion\":1,", string.Empty, StringComparison.Ordinal),
            valid.Replace("\"tools\":", "\"extra\":true,\"tools\":", StringComparison.Ordinal),
            valid.Replace("\"description\":null,", string.Empty, StringComparison.Ordinal),
            valid.Replace("\"inputSchema\":{\"type\":\"object\"},", string.Empty, StringComparison.Ordinal),
            valid.Replace("\"returnSchema\":null,", string.Empty, StringComparison.Ordinal),
            valid.Replace("\"requiresApproval\":false", string.Empty, StringComparison.Ordinal),
            valid.Replace("\"requiresApproval\":false", "\"requiresApproval\":\"false\"", StringComparison.Ordinal)
        };

        foreach (var mutation in mutations)
        {
            var exception = Assert.Throws<AiToolContractException>(() => AiToolContractJson.Parse(mutation));
            Assert.Equal(AiToolDiagnosticKind.MalformedBaseline, exception.Diagnostic.Kind);
        }
    }

    [Fact]
    public void RootAndToolDuplicateMembersShareEnvelopeDiagnosticFamily()
    {
        var duplicateRoot = "{\"schemaVersion\":1,\"schemaVersion\":1,\"tools\":[]}";
        var duplicateTool = Envelope("{\"type\":\"object\"}").Replace("\"name\":\"tool\",", "\"name\":\"tool\",\"name\":\"tool\",", StringComparison.Ordinal);

        Assert.Equal(AiToolDiagnosticKind.MalformedBaseline, Assert.Throws<AiToolContractException>(() => AiToolContractJson.Parse(duplicateRoot)).Diagnostic.Kind);
        Assert.Equal(AiToolDiagnosticKind.MalformedBaseline, Assert.Throws<AiToolContractException>(() => AiToolContractJson.Parse(duplicateTool)).Diagnostic.Kind);
    }

    [Fact]
    public void UnsupportedSchemaChangesBlockBothAcceptanceMethods()
    {
        var baseline = Capture("{\"type\":\"string\",\"format\":\"date-time\"}");
        var candidate = Capture("{\"type\":\"string\",\"format\":\"uri\"}");
        var diff = AiToolContractVerifier.Compare(baseline, candidate);

        AssertUnsupported(diff);
        Assert.Throws<AiToolContractException>(() => AiToolContractVerifier.Accept(candidate, diff));
        Assert.Throws<AiToolContractException>(() => AiToolContractVerifier.AcceptWithBreakingReview(candidate, diff));
    }

    [Fact]
    public void ResourceLimitsAreExplicit()
    {
        var depth = CaptureWithLimits("{\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"object\",\"properties\":{\"b\":{\"type\":\"object\",\"properties\":{\"c\":{\"type\":\"string\"}}}}}}}", new AiToolContractLimits { MaxSchemaDepth = 3 });
        var bytes = CaptureWithLimits("{\"description\":\"" + new string('x', 100) + "\"}", new AiToolContractLimits { MaxSchemaBytes = 32 });
        var array = CaptureWithLimits("{\"enum\":[1,2]}", new AiToolContractLimits { MaxArrayItems = 1 });

        Assert.Equal(AiToolDiagnosticKind.CanonicalizationOrResourceLimit, depth.Diagnostic!.Kind);
        Assert.Equal(AiToolDiagnosticKind.CanonicalizationOrResourceLimit, bytes.Diagnostic!.Kind);
        Assert.Equal(AiToolDiagnosticKind.CanonicalizationOrResourceLimit, array.Diagnostic!.Kind);
    }

    [Fact]
    public void BreakingChangesAreNeverAutomaticallyAccepted()
    {
        var baseline = Capture("{\"type\":\"object\"}");
        var candidate = Capture("{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"}},\"required\":[\"query\"]}");
        var diff = AiToolContractVerifier.Compare(baseline, candidate);

        Assert.False(diff.IsClean);
        Assert.Throws<AiToolContractException>(() => AiToolContractVerifier.Accept(candidate, diff));
        Assert.Same(candidate, AiToolContractVerifier.AcceptWithBreakingReview(candidate, diff));
    }

    private static string Search([Description("Search text.")] string query, [Description("Maximum results.")] int limit = 10, DateTime? since = null, Uri? callback = null, Guid? requestId = null, SearchStatus status = SearchStatus.Open, SearchOptions? options = null, string[]? tags = null) => query;

    private enum SearchStatus
    {
        Open,
        Closed
    }

    private sealed class SearchOptions
    {
        public string? Region { get; set; }
    }

    private static AiToolContractBaseline Capture(string inputSchema, string? returnSchema = null, string? description = "A tool")
    {
        var result = CaptureWithLimits(inputSchema, AiToolContractLimits.Default, returnSchema, description);
        Assert.True(result.Succeeded, result.Diagnostic?.Message);
        return result.Baseline!;
    }

    private static AiToolContractCaptureResult CaptureWithLimits(string inputSchema, AiToolContractLimits? limits = null, string? returnSchema = null, string? description = "A tool")
    {
        using var input = JsonDocument.Parse(inputSchema);
        JsonElement? output = null;
        if (returnSchema is not null)
        {
            using var returnDocument = JsonDocument.Parse(returnSchema);
            output = returnDocument.RootElement.Clone();
        }
        var declaration = AIFunctionFactory.CreateDeclaration("tool", description, input.RootElement.Clone(), output);
        return AiToolContractCapture.Capture(new AITool[] { declaration }, limits ?? AiToolContractLimits.Default);
    }

    private static string Envelope(string schema) =>
        "{\"schemaVersion\":1,\"tools\":[{\"name\":\"tool\",\"description\":null,\"inputSchema\":" + schema + ",\"returnSchema\":null,\"requiresApproval\":false}]}";

    private static void AssertUnsupported(AiToolContractDiff diff)
    {
        Assert.False(diff.IsClean);
        Assert.Contains(diff.Changes, static change => change.Kind == AiToolChangeKind.Unsupported);
        Assert.Contains(diff.Diagnostics, static diagnostic => diagnostic.Kind == AiToolDiagnosticKind.UnsupportedClassification);
    }
}
