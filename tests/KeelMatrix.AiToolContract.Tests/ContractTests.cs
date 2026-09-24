using System.Text.Json;
using KeelMatrix.AiToolContract;
using Microsoft.Extensions.AI;
using Xunit;

namespace KeelMatrix.AiToolContract.Tests;

public sealed class ContractTests
{
    private static readonly string[] PropertyNames = { "a", "b", "c", "d" };
    private static readonly string[] EnumValues = { "new", "open", "closed" };

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
        Assert.Equal("query", result.Baseline!.Tools[0].InputSchemaJson.Contains("query", StringComparison.Ordinal) ? "query" : null);
        Assert.NotNull(result.Baseline.Tools[0].ReturnSchemaJson);
    }

    [Fact]
    public void CanonicalizationIgnoresObjectPropertyAndWhitespaceOrder()
    {
        var left = Capture("{\"type\":\"object\",\"properties\":{\"b\":{\"type\":\"integer\"},\"a\":{\"type\":\"string\"}},\"required\":[\"b\",\"a\"]}");
        var right = Capture("{ \"required\": [ \"a\", \"b\" ], \"properties\": { \"a\": { \"type\": \"string\" }, \"b\": { \"type\": \"integer\" } }, \"type\": \"object\" }");

        var diff = AiToolContractVerifier.Compare(left, right);

        Assert.True(diff.IsClean, string.Join("; ", diff.Diagnostics.Select(static d => d.Message)));
        Assert.Equal(AiToolContractJson.Serialize(left), AiToolContractJson.Serialize(right));
    }

    [Fact]
    public void CanonicalizationPropertyTestStabilizesSemanticSetArraysAndProperties()
    {
        var random = new Random(1354);
        var expected = string.Empty;
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var propertyOrder = PropertyNames.OrderBy(_ => random.Next()).ToArray();
            var requiredOrder = PropertyNames.OrderBy(_ => random.Next()).ToArray();
            var enumOrder = EnumValues.OrderBy(_ => random.Next()).ToArray();
            var schema = "{\"type\":\"object\",\"required\":[" + string.Join(",", requiredOrder.Select(static value => JsonSerializer.Serialize(value))) + "],\"properties\":{" + string.Join(",", propertyOrder.Select(static name => JsonSerializer.Serialize(name) + ":{\"type\":\"string\"}")) + "},\"enum\":[" + string.Join(",", enumOrder.Select(static value => JsonSerializer.Serialize(value))) + "]}";
            var serialized = AiToolContractJson.Serialize(Capture(schema));
            if (iteration == 0)
                expected = serialized;
            Assert.Equal(expected, serialized);
        }
    }

    [Fact]
    public void TypeArrayOrderIsCanonicalizedAsASet()
    {
        var left = Capture("{\"type\":[\"string\",\"null\"]}");
        var right = Capture("{\"type\":[\"null\",\"string\"]}");

        var diff = AiToolContractVerifier.Compare(left, right);

        Assert.True(diff.IsClean, string.Join("; ", diff.Diagnostics.Select(static d => d.Message)));
        Assert.Equal(AiToolContractJson.Serialize(left), AiToolContractJson.Serialize(right));
    }

    [Fact]
    public void AllOfOrderRemainsReviewRelevant()
    {
        var left = Capture("{\"allOf\":[{\"type\":\"string\"},{\"minLength\":1}]}");
        var right = Capture("{\"allOf\":[{\"minLength\":1},{\"type\":\"string\"}]}");

        var diff = AiToolContractVerifier.Compare(left, right);

        Assert.False(diff.IsClean);
        Assert.Contains(diff.Changes, static change => change.Kind == AiToolChangeKind.Unsupported);
        Assert.Contains(diff.Diagnostics, static diagnostic => diagnostic.Kind == AiToolDiagnosticKind.UnsupportedClassification);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"type\":\"object\",\"properties\":[]}")]
    [InlineData("{\"type\":\"object\",\"required\":1}")]
    [InlineData("{\"type\":1}")]
    public void InvalidSchemaShapesFailClosed(string schema)
    {
        var baseline = BaselineWithInput(schema);

        var diff = AiToolContractVerifier.Compare(baseline, baseline);

        Assert.False(diff.IsClean);
        Assert.Contains(diff.Changes, static change => change.Kind == AiToolChangeKind.Unsupported);
        Assert.Contains(diff.Diagnostics, static diagnostic => diagnostic.Kind == AiToolDiagnosticKind.MalformedBaseline);
        var capture = CaptureWithLimits(schema, AiToolContractLimits.Default);
        Assert.False(capture.Succeeded);
        Assert.Equal(AiToolDiagnosticKind.MalformedBaseline, capture.Diagnostic!.Kind);
        var exception = Assert.Throws<AiToolContractException>(() => AiToolContractJson.Parse("{\"schemaVersion\":1,\"tools\":[{\"name\":\"tool\",\"description\":null,\"inputSchema\":" + schema + ",\"returnSchema\":null,\"requiresApproval\":false}]}"));
        Assert.Equal(AiToolDiagnosticKind.MalformedBaseline, exception.Diagnostic.Kind);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("{\"type\":\"array\",\"items\":[{\"type\":\"string\"}]}")]
    public void ValidButUnsupportedSchemaFormsFailClosed(string schema)
    {
        var baseline = BaselineWithInput(schema);

        var diff = AiToolContractVerifier.Compare(baseline, baseline);

        Assert.False(diff.IsClean);
        Assert.Contains(diff.Changes, static change => change.Kind == AiToolChangeKind.Unsupported);
        Assert.Contains(diff.Diagnostics, static diagnostic => diagnostic.Kind == AiToolDiagnosticKind.UnsupportedClassification);
        var exception = Assert.Throws<AiToolContractException>(() => AiToolContractJson.Parse("{\"schemaVersion\":1,\"tools\":[{\"name\":\"tool\",\"description\":null,\"inputSchema\":" + schema + ",\"returnSchema\":null,\"requiresApproval\":false}]}"));
        Assert.Equal(AiToolDiagnosticKind.UnsupportedClassification, exception.Diagnostic.Kind);
    }

    [Fact]
    public void ReferenceAndDefinitionsShapeIsStable()
    {
        var left = Capture("{\"$ref\":\"#/$defs/Order\",\"$defs\":{\"Order\":{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\"}}}}}");
        var right = Capture("{\"$defs\":{\"Order\":{\"properties\":{\"id\":{\"type\":\"string\"}},\"type\":\"object\"}},\"$ref\":\"#/$defs/Order\"}");

        Assert.True(AiToolContractVerifier.Compare(left, right).IsClean);
    }

    [Fact]
    public void ToolAddAndRemoveAreClassified()
    {
        var baseline = Capture("{\"type\":\"object\"}");
        var added = AiToolContractVerifier.Compare(baseline, CaptureTwo("first", "second"));
        var removed = AiToolContractVerifier.Compare(CaptureTwo("first", "second"), baseline);

        Assert.Contains(added.Changes, static change => change.Kind == AiToolChangeKind.ToolAdded && change.Compatibility == AiToolCompatibility.Additive);
        Assert.Contains(removed.Changes, static change => change.Kind == AiToolChangeKind.ToolRemoved && change.Compatibility == AiToolCompatibility.Breaking);
    }

    [Fact]
    public void RequiredAndOptionalParameterChangesAreClassified()
    {
        var baseline = Capture("{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"}}}");
        var required = Capture("{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"},\"region\":{\"type\":\"string\"}},\"required\":[\"region\"]}");
        var optional = Capture("{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"},\"limit\":{\"type\":\"integer\"}}}");

        Assert.Contains(AiToolContractVerifier.Compare(baseline, required).Changes, static change => change.Kind == AiToolChangeKind.RequiredParameterAdded && change.Compatibility == AiToolCompatibility.Breaking);
        Assert.Contains(AiToolContractVerifier.Compare(baseline, optional).Changes, static change => change.Kind == AiToolChangeKind.OptionalParameterAdded && change.Compatibility == AiToolCompatibility.Additive);
    }

    [Fact]
    public void ParameterRemovalAndNullabilityChangeAreClassified()
    {
        var baseline = Capture("{\"type\":\"object\",\"properties\":{\"query\":{\"type\": [\"string\",\"null\"]}}}");
        var removed = Capture("{\"type\":\"object\"}");
        var nonNullable = Capture("{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"}}}");

        Assert.Contains(AiToolContractVerifier.Compare(baseline, removed).Changes, static change => change.Kind == AiToolChangeKind.ParameterRemoved);
        Assert.Contains(AiToolContractVerifier.Compare(baseline, nonNullable).Changes, static change => change.Kind == AiToolChangeKind.ConstraintNarrowed && change.Compatibility == AiToolCompatibility.Breaking);
    }

    [Fact]
    public void PrimitiveObjectAndArrayTypeChangesAreBreaking()
    {
        var cases = new[]
        {
            ("string", "integer"),
            ("object", "array"),
            ("array", "object")
        };
        foreach (var (oldType, newType) in cases)
        {
            var diff = AiToolContractVerifier.Compare(Capture("{\"type\":\"" + oldType + "\"}"), Capture("{\"type\":\"" + newType + "\"}"));
            Assert.Contains(diff.Changes, static change => change.Kind == AiToolChangeKind.TypeChanged && change.Compatibility == AiToolCompatibility.Breaking);
        }
    }

    [Fact]
    public void EnumExpansionAndNarrowingAreClassified()
    {
        var baseline = Capture("{\"type\":\"object\",\"properties\":{\"state\":{\"enum\":[\"new\",\"open\"]}}}");
        var expanded = Capture("{\"type\":\"object\",\"properties\":{\"state\":{\"enum\":[\"new\",\"open\",\"closed\"]}}}");
        var narrowed = Capture("{\"type\":\"object\",\"properties\":{\"state\":{\"enum\":[\"open\"]}}}");

        Assert.Contains(AiToolContractVerifier.Compare(baseline, expanded).Changes, static change => change.Kind == AiToolChangeKind.EnumExpanded && change.Compatibility == AiToolCompatibility.Additive);
        Assert.Contains(AiToolContractVerifier.Compare(baseline, narrowed).Changes, static change => change.Kind == AiToolChangeKind.EnumNarrowed && change.Compatibility == AiToolCompatibility.Breaking);
    }

    [Fact]
    public void ConstraintWideningAndNarrowingAreClassified()
    {
        var baseline = Capture("{\"type\":\"object\",\"properties\":{\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":100}}}");
        var narrowed = Capture("{\"type\":\"object\",\"properties\":{\"limit\":{\"type\":\"integer\",\"minimum\":10,\"maximum\":50}}}");
        var widened = Capture("{\"type\":\"object\",\"properties\":{\"limit\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":1000}}}");

        Assert.Equal(2, AiToolContractVerifier.Compare(baseline, narrowed).Changes.Count(static change => change.Kind == AiToolChangeKind.ConstraintNarrowed));
        Assert.Equal(2, AiToolContractVerifier.Compare(baseline, widened).Changes.Count(static change => change.Kind == AiToolChangeKind.ConstraintExpanded));
    }

    [Fact]
    public void ReturnSchemaAndDescriptionChangesAreClassified()
    {
        var baseline = Capture("{\"type\":\"object\"}", "{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\"}}}", "Search orders");
        var additive = Capture("{\"type\":\"object\"}", "{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\"},\"count\":{\"type\":\"integer\"}}}", "Search orders");
        var risky = Capture("{\"type\":\"object\"}", "{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\"}}}", "Search invoices");
        var breaking = Capture("{\"type\":\"object\"}", "{\"type\":\"object\"}", "Search orders");

        Assert.Contains(AiToolContractVerifier.Compare(baseline, additive).Changes, static change => change.Kind == AiToolChangeKind.ReturnSchemaAdditive);
        Assert.Contains(AiToolContractVerifier.Compare(baseline, risky).Changes, static change => change.Kind == AiToolChangeKind.DescriptionChanged && change.Compatibility == AiToolCompatibility.Risky);
        Assert.Contains(AiToolContractVerifier.Compare(baseline, breaking).Changes, static change => change.Kind == AiToolChangeKind.ReturnSchemaBreaking && change.Compatibility == AiToolCompatibility.Breaking);
    }

    [Fact]
    public void ApprovalMetadataChangeIsReviewRelevant()
    {
        var function = AIFunctionFactory.Create((string value) => value);
#pragma warning disable MEAI001
        var approval = new ApprovalRequiredAIFunction(function);
#pragma warning restore MEAI001
        var baseline = AiToolContractCapture.Capture(new AITool[] { function }).Baseline!;
        var candidate = AiToolContractCapture.Capture(new AITool[] { approval }).Baseline!;

        var diff = AiToolContractVerifier.Compare(baseline, candidate);

        Assert.Contains(diff.Changes, static change => change.Kind == AiToolChangeKind.ApprovalSafetyMetadataChanged);
        Assert.Equal(AiToolCompatibility.Risky, diff.Compatibility);
    }

    [Fact]
    public void UnknownSchemaChangeFailsClosed()
    {
        var baseline = BaselineWithInput("{\"type\":\"object\",\"x-vendor-policy\":\"one\"}");
        var candidate = BaselineWithInput("{\"type\":\"object\",\"x-vendor-policy\":\"two\"}");

        var diff = AiToolContractVerifier.Compare(baseline, candidate);

        Assert.False(diff.IsClean);
        Assert.Contains(diff.Changes, static change => change.Kind == AiToolChangeKind.Unsupported);
        Assert.Contains(diff.Diagnostics, static diagnostic => diagnostic.Kind == AiToolDiagnosticKind.UnsupportedClassification);
    }

    [Fact]
    public void UnchangedUnsupportedSchemaSemanticsFailClosedOnCaptureParseAndCompare()
    {
        var schema = "{\"type\":\"object\",\"x-vendor-policy\":\"one\"}";

        var capture = CaptureWithLimits(schema, AiToolContractLimits.Default);
        Assert.False(capture.Succeeded);
        Assert.Equal(AiToolDiagnosticKind.UnsupportedClassification, capture.Diagnostic!.Kind);

        var parse = Assert.Throws<AiToolContractException>(() => AiToolContractJson.Parse("{\"schemaVersion\":1,\"tools\":[{\"name\":\"tool\",\"description\":\"A tool\",\"inputSchema\":" + schema + ",\"returnSchema\":null,\"requiresApproval\":false}]}"));
        Assert.Equal(AiToolDiagnosticKind.UnsupportedClassification, parse.Diagnostic.Kind);

        var diff = AiToolContractVerifier.Compare(BaselineWithInput(schema), BaselineWithInput(schema));
        Assert.False(diff.IsClean);
        Assert.Contains(diff.Changes, static change => change.Kind == AiToolChangeKind.Unsupported);
        Assert.Contains(diff.Diagnostics, static diagnostic => diagnostic.Kind == AiToolDiagnosticKind.UnsupportedClassification);
    }

    [Fact]
    public void DuplicateBaselineMembersFailAsMalformedForEveryEnvelopeObject()
    {
        var duplicateDocuments = new[]
        {
            "{\"schemaVersion\":1,\"schemaVersion\":1,\"tools\":[]}",
            "{\"schemaVersion\":1,\"tools\":[],\"tools\":[]}",
            "{\"schemaVersion\":1,\"tools\":[{\"name\":\"tool\",\"name\":\"tool\",\"description\":null,\"inputSchema\":{\"type\":\"object\"},\"returnSchema\":null,\"requiresApproval\":false}]}",
            "{\"schemaVersion\":1,\"tools\":[{\"name\":\"tool\",\"description\":null,\"description\":null,\"inputSchema\":{\"type\":\"object\"},\"returnSchema\":null,\"requiresApproval\":false}]}",
            "{\"schemaVersion\":1,\"tools\":[{\"name\":\"tool\",\"description\":null,\"inputSchema\":{\"type\":\"object\"},\"inputSchema\":{\"type\":\"object\"},\"returnSchema\":null,\"requiresApproval\":false}]}",
            "{\"schemaVersion\":1,\"tools\":[{\"name\":\"tool\",\"description\":null,\"inputSchema\":{\"type\":\"object\"},\"returnSchema\":null,\"returnSchema\":null,\"requiresApproval\":false}]}",
            "{\"schemaVersion\":1,\"tools\":[{\"name\":\"tool\",\"description\":null,\"inputSchema\":{\"type\":\"object\"},\"returnSchema\":null,\"requiresApproval\":false,\"requiresApproval\":false}]}",
            "{\"schemaVersion\":1,\"tools\":[{\"name\":\"tool\",\"description\":null,\"inputSchema\":{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"string\",\"type\":\"string\"}}},\"returnSchema\":null,\"requiresApproval\":false}]}",
        };

        foreach (var json in duplicateDocuments)
        {
            var exception = Assert.Throws<AiToolContractException>(() => AiToolContractJson.Parse(json));
            Assert.Equal(AiToolDiagnosticKind.MalformedBaseline, exception.Diagnostic.Kind);
        }
    }

    [Fact]
    public void DuplicateIdentityIsDistinctFailure()
    {
        var first = Declaration("duplicate", "{\"type\":\"object\"}");
        var second = Declaration("duplicate", "{\"type\":\"object\"}");

        var result = AiToolContractCapture.Capture(new AITool[] { first, second });

        Assert.False(result.Succeeded);
        Assert.Equal(AiToolDiagnosticKind.DuplicateToolIdentity, result.Diagnostic!.Kind);
    }

    [Fact]
    public void BaselineVersionAndMalformedInputFailClosed()
    {
        var unsupported = Assert.Throws<AiToolContractException>(() => AiToolContractJson.Parse("{\"schemaVersion\":99,\"tools\":[]}"));
        var malformed = Assert.Throws<AiToolContractException>(() => AiToolContractJson.Parse("{\"schemaVersion\":1,\"tools\":[}"));

        Assert.Equal(AiToolDiagnosticKind.UnsupportedBaselineVersion, unsupported.Diagnostic.Kind);
        Assert.Equal(AiToolDiagnosticKind.MalformedBaseline, malformed.Diagnostic.Kind);
    }

    [Fact]
    public void ResourceLimitsFailSafely()
    {
        var limits = new AiToolContractLimits { MaxSchemaDepth = 3, MaxSchemaBytes = 64 };
        var deep = Capture("{\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"object\",\"properties\":{\"b\":{\"type\":\"object\",\"properties\":{\"c\":{\"type\":\"string\"}}}}}}}");
        var oversized = CaptureWithLimits("{\"description\":\"" + new string('x', 100) + "\"}", new AiToolContractLimits { MaxSchemaBytes = 32 });

        Assert.False(AiToolContractCapture.Capture(new AITool[] { Declaration("deep", deep.Tools[0].InputSchemaJson) }, limits).Succeeded);
        Assert.Equal(AiToolDiagnosticKind.CanonicalizationOrResourceLimit, oversized.Diagnostic!.Kind);
    }

    [Fact]
    public void ResourceLimitCategoriesAreExplicit()
    {
        var propertyLimited = CaptureWithLimits("{\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"string\"},\"b\":{\"type\":\"string\"}}}", new AiToolContractLimits { MaxProperties = 1 });
        var arrayLimited = CaptureWithLimits("{\"enum\":[1,2]}", new AiToolContractLimits { MaxArrayItems = 1 });
        var toolsLimited = AiToolContractCapture.Capture(new AITool[]
        {
            Declaration("first", "{\"type\":\"object\"}"),
            Declaration("second", "{\"type\":\"object\"}")
        }, new AiToolContractLimits { MaxTools = 1 });

        Assert.Equal(AiToolDiagnosticKind.CanonicalizationOrResourceLimit, propertyLimited.Diagnostic!.Kind);
        Assert.Equal(AiToolDiagnosticKind.CanonicalizationOrResourceLimit, arrayLimited.Diagnostic!.Kind);
        Assert.Equal(AiToolDiagnosticKind.CanonicalizationOrResourceLimit, toolsLimited.Diagnostic!.Kind);

        var changeLimited = Assert.Throws<AiToolContractException>(() => AiToolContractVerifier.Compare(
            Capture("{\"type\":\"integer\",\"minimum\":1,\"maximum\":100}"),
            Capture("{\"type\":\"integer\",\"minimum\":10,\"maximum\":50}"),
            new AiToolContractLimits { MaxChanges = 1 }));
        Assert.Equal(AiToolDiagnosticKind.CanonicalizationOrResourceLimit, changeLimited.Diagnostic.Kind);

        var depthLimitedCapture = CaptureWithLimits("{\"a\":{\"b\":{\"c\":{\"type\":\"string\"}}}}", new AiToolContractLimits { MaxSchemaDepth = 3 });
        Assert.Equal(AiToolDiagnosticKind.CanonicalizationOrResourceLimit, depthLimitedCapture.Diagnostic!.Kind);

        var depthLimitedParse = Assert.Throws<AiToolContractException>(() => AiToolContractJson.Parse(
            "{\"schemaVersion\":1,\"tools\":[{\"name\":\"tool\",\"description\":null,\"inputSchema\":{\"a\":{\"b\":{\"c\":{\"type\":\"string\"}}}},\"returnSchema\":null,\"requiresApproval\":false}]}",
            new AiToolContractLimits { MaxSchemaDepth = 3 }));
        Assert.Equal(AiToolDiagnosticKind.CanonicalizationOrResourceLimit, depthLimitedParse.Diagnostic.Kind);
    }

    [Fact]
    public void VerificationNeverWritesOrAcceptsBreakingChangesAutomatically()
    {
        var baseline = Capture("{\"type\":\"object\"}");
        var candidate = Capture("{\"type\":\"object\",\"required\":[\"query\"],\"properties\":{\"query\":{\"type\":\"string\"}}}");
        var diff = AiToolContractVerifier.Compare(baseline, candidate);

        Assert.False(diff.IsClean);
        Assert.Throws<AiToolContractException>(() => AiToolContractVerifier.Accept(candidate, diff));
        Assert.Same(candidate, AiToolContractVerifier.AcceptWithBreakingReview(candidate, diff));
    }

    [Fact]
    public void ConstraintBoundsClassifyUnboundedTransitionsInBothDirections()
    {
        var unbounded = Capture("{\"type\":\"integer\"}");
        var bounded = Capture("{\"type\":\"integer\",\"maximum\":100}");
        var lowerBounded = Capture("{\"type\":\"integer\",\"minimum\":1}");

        Assert.Contains(AiToolContractVerifier.Compare(unbounded, bounded).Changes, static change => change.Kind == AiToolChangeKind.ConstraintNarrowed && change.Compatibility == AiToolCompatibility.Breaking);
        Assert.Contains(AiToolContractVerifier.Compare(bounded, unbounded).Changes, static change => change.Kind == AiToolChangeKind.ConstraintExpanded && change.Compatibility == AiToolCompatibility.Additive);
        Assert.Contains(AiToolContractVerifier.Compare(unbounded, lowerBounded).Changes, static change => change.Kind == AiToolChangeKind.ConstraintNarrowed && change.Compatibility == AiToolCompatibility.Breaking);
        Assert.Contains(AiToolContractVerifier.Compare(lowerBounded, unbounded).Changes, static change => change.Kind == AiToolChangeKind.ConstraintExpanded && change.Compatibility == AiToolCompatibility.Additive);
    }

    [Fact]
    public void RequiredAndOptionalReturnPropertiesHaveDifferentCompatibility()
    {
        var baseline = Capture("{\"type\":\"object\"}", "{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\"}}}");
        var optional = Capture("{\"type\":\"object\"}", "{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\"},\"count\":{\"type\":\"integer\"}}}");
        var required = Capture("{\"type\":\"object\"}", "{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\"},\"count\":{\"type\":\"integer\"}},\"required\":[\"count\"]}");

        Assert.Contains(AiToolContractVerifier.Compare(baseline, optional).Changes, static change => change.Kind == AiToolChangeKind.ReturnSchemaAdditive && change.Compatibility == AiToolCompatibility.Additive);
        Assert.Contains(AiToolContractVerifier.Compare(baseline, required).Changes, static change => change.Kind == AiToolChangeKind.ReturnSchemaBreaking && change.Compatibility == AiToolCompatibility.Breaking);
    }

    private static AiToolContractBaseline Capture(string inputSchema, string? returnSchema = null, string? description = "A tool") =>
        AiToolContractCapture.Capture(new AITool[] { Declaration("tool", inputSchema, returnSchema, description) }).Baseline!;

    private static AiToolContractBaseline BaselineWithInput(string inputSchema) =>
        new(new[] { new AiToolContractTool("tool", "A tool", inputSchema, null, false) });

    private static AiToolContractCaptureResult CaptureWithLimits(string inputSchema, AiToolContractLimits limits) =>
        AiToolContractCapture.Capture(new AITool[] { Declaration("tool", inputSchema) }, limits);

    private static AiToolContractBaseline CaptureTwo(string first, string second) =>
        AiToolContractCapture.Capture(new AITool[]
        {
            Declaration(first, "{\"type\":\"object\"}"),
            Declaration(second, "{\"type\":\"object\"}")
        }).Baseline!;

    private static AIFunctionDeclaration Declaration(string name, string inputSchema, string? returnSchema = null, string? description = "A tool")
    {
        using var input = JsonDocument.Parse(inputSchema);
        JsonElement? output = null;
        if (returnSchema is not null)
        {
            using var returnDocument = JsonDocument.Parse(returnSchema);
            output = returnDocument.RootElement.Clone();
        }
        return AIFunctionFactory.CreateDeclaration(name, description, input.RootElement.Clone(), output);
    }
}
