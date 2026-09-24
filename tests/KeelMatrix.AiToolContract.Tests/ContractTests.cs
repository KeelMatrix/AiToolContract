using System.ComponentModel;
using System.Text.Json;
using KeelMatrix.AiToolContract;
using Microsoft.Extensions.AI;
using Xunit;

namespace KeelMatrix.AiToolContract.Tests;

public sealed class ContractTests
{
    private static readonly string[] CanonicalRequiredNames = { "a", "b" };

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
            "{\"definitions\":{\"Order\":{\"type\":\"object\"}}}",
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
        var propertyMembers = new Dictionary<string, string>
        {
            ["a"] = "{\"type\":\"string\"}",
            ["b"] = "{\"type\":\"integer\"}"
        };
        var rootMembers = new[] { "type", "required", "properties" };
        var expected = string.Empty;
        var variants = 0;

        foreach (var rootOrder in Permutations(rootMembers))
            foreach (var propertyOrder in Permutations(propertyMembers.Keys.ToArray()))
                foreach (var requiredOrder in Permutations(CanonicalRequiredNames))
                {
                    var properties = "{" + string.Join(',', propertyOrder.Select(name => "\"" + name + "\":" + propertyMembers[name])) + "}";
                    var members = new Dictionary<string, string>
                    {
                        ["type"] = "\"object\"",
                        ["required"] = "[\"" + string.Join("\",\"", requiredOrder) + "\"]",
                        ["properties"] = properties
                    };
                    var schema = "{" + string.Join(',', rootOrder.Select(name => "\"" + name + "\":" + members[name])) + "}";
                    var actual = AiToolContractJson.Serialize(Capture(schema));
                    if (variants++ == 0)
                        expected = actual;
                    else
                        Assert.Equal(expected, actual);
                }

        Assert.Equal(24, variants);
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
        Assert.True(capture.Diagnostic!.Kind is AiToolDiagnosticKind.CaptureFailure or AiToolDiagnosticKind.MalformedBaseline or AiToolDiagnosticKind.UnsupportedClassification);
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

    [Fact]
    public void ModelTransitionRuleTableMatchesTheClosedNormalizedModel()
    {
        Assert.NotEmpty(SchemaTransitionRules.ModelPropertyNames);
        Assert.NotEmpty(SchemaTransitionRules.Rules);
        Assert.Empty(SchemaTransitionRules.CoverageFailures);
        Assert.Equal(SchemaTransitionRules.Rules.Count * 4, ModelDerivedTransitionMatrix.Count());
    }

    [Theory]
    [MemberData(nameof(ModelDerivedTransitionMatrix))]
    public void ModelDerivedTransitionMatrixClassifiesEveryRuleAndDepth(GeneratedTransitionCase testCase)
    {
        var schemas = SchemaTransitionSchemaGenerator.Build(testCase.Rule, testCase.Depth);
        var oldBaseline = testCase.ReturnSchema ? Capture("{\"type\":\"object\"}", schemas.OldSchema) : Capture(schemas.OldSchema);
        var newBaseline = testCase.ReturnSchema ? Capture("{\"type\":\"object\"}", schemas.NewSchema) : Capture(schemas.NewSchema);
        var diff = AiToolContractVerifier.Compare(oldBaseline, newBaseline);
        var expected = testCase.ReturnSchema ? testCase.Rule.Return : testCase.Rule.Input;

        Assert.False(diff.IsClean, testCase.Name);
        Assert.Equal(expected.Compatibility, diff.Compatibility);
        Assert.Single(diff.Changes);
        Assert.Equal(expected.Kind, diff.Changes[0].Kind);
        Assert.Equal(expected.Compatibility, diff.Changes[0].Compatibility);
        Assert.Equal(testCase.ExpectedPath, diff.Changes[0].Path);
        if (expected.Unsupported)
        {
            AssertUnsupported(diff);
            Assert.Throws<AiToolContractException>(() => AiToolContractVerifier.Accept(newBaseline, diff));
            Assert.Throws<AiToolContractException>(() => AiToolContractVerifier.AcceptWithBreakingReview(newBaseline, diff));
            return;
        }

        Assert.Contains(diff.Changes, change => change.Kind == expected.Kind && change.Compatibility == expected.Compatibility);
        if (expected.Compatibility == AiToolCompatibility.Breaking)
        {
            Assert.Throws<AiToolContractException>(() => AiToolContractVerifier.Accept(newBaseline, diff));
            Assert.Same(newBaseline, AiToolContractVerifier.AcceptWithBreakingReview(newBaseline, diff));
        }
        else
        {
            Assert.Same(newBaseline, AiToolContractVerifier.Accept(newBaseline, diff));
            Assert.Same(newBaseline, AiToolContractVerifier.AcceptWithBreakingReview(newBaseline, diff));
        }
    }

    [Theory]
    [MemberData(nameof(RestoredTransitionInventory))]
    public void RestoredTransitionInventoryHasOneExactChange(RestoredTransitionCase testCase)
    {
        var oldBaseline = testCase.ReturnSchema ? Capture("{\"type\":\"object\"}", testCase.OldSchema) : Capture(testCase.OldSchema);
        var newBaseline = testCase.ReturnSchema ? Capture("{\"type\":\"object\"}", testCase.NewSchema) : Capture(testCase.NewSchema);
        var diff = AiToolContractVerifier.Compare(oldBaseline, newBaseline);

        Assert.False(diff.IsClean, testCase.Name);
        Assert.Equal(testCase.Compatibility, diff.Compatibility);
        Assert.Single(diff.Changes);
        Assert.Equal(testCase.Kind, diff.Changes[0].Kind);
        Assert.Equal(testCase.Compatibility, diff.Changes[0].Compatibility);
        var expectedPath = testCase.ReturnSchema ? "$.returnSchema" + testCase.Path[1..] : testCase.Path;
        Assert.Equal(expectedPath, diff.Changes[0].Path);
    }

    public static IEnumerable<object[]> ModelDerivedTransitionMatrix
    {
        get
        {
            foreach (var rule in SchemaTransitionRules.Rules)
            {
                foreach (var returnSchema in new[] { false, true })
                {
                    yield return new object[] { new GeneratedTransitionCase(rule, returnSchema, 0) };
                    yield return new object[] { new GeneratedTransitionCase(rule, returnSchema, 2) };
                }
            }
        }
    }

    public static IEnumerable<object[]> RestoredTransitionInventory
    {
        get
        {
            yield return new object[] { new RestoredTransitionCase("required-undeclared-added", "{\"type\":\"object\"}", "{\"type\":\"object\",\"required\":[\"ghost\"]}", false, AiToolChangeKind.RequiredParameterAdded, AiToolCompatibility.Breaking, "$.required") };
            yield return new object[] { new RestoredTransitionCase("required-undeclared-removed", "{\"type\":\"object\",\"required\":[\"ghost\"]}", "{\"type\":\"object\"}", false, AiToolChangeKind.OptionalParameterAdded, AiToolCompatibility.Additive, "$.required") };
            yield return new object[] { new RestoredTransitionCase("return-required-undeclared-added", "{\"type\":\"object\"}", "{\"type\":\"object\",\"required\":[\"ghost\"]}", true, AiToolChangeKind.ReturnSchemaBreaking, AiToolCompatibility.Breaking, "$.required") };
            yield return new object[] { new RestoredTransitionCase("return-required-undeclared-removed", "{\"type\":\"object\",\"required\":[\"ghost\"]}", "{\"type\":\"object\"}", true, AiToolChangeKind.ReturnSchemaAdditive, AiToolCompatibility.Additive, "$.required") };

            yield return new object[] { new RestoredTransitionCase("type-primitive-to-object", "{\"type\":\"string\"}", "{\"type\":\"object\"}", false, AiToolChangeKind.TypeChanged, AiToolCompatibility.Breaking, "$.type") };
            yield return new object[] { new RestoredTransitionCase("type-object-to-array", "{\"type\":\"object\"}", "{\"type\":\"array\"}", false, AiToolChangeKind.TypeChanged, AiToolCompatibility.Breaking, "$.type") };
            yield return new object[] { new RestoredTransitionCase("return-type-primitive-to-object", "{\"type\":\"string\"}", "{\"type\":\"object\"}", true, AiToolChangeKind.ReturnSchemaBreaking, AiToolCompatibility.Breaking, "$.type") };
            yield return new object[] { new RestoredTransitionCase("return-type-object-to-array", "{\"type\":\"object\"}", "{\"type\":\"array\"}", true, AiToolChangeKind.ReturnSchemaBreaking, AiToolCompatibility.Breaking, "$.type") };

            yield return new object[] { new RestoredTransitionCase("nested-items-union-widened", "{\"type\":\"array\",\"items\":{\"type\":\"string\"}}", "{\"type\":\"array\",\"items\":{\"type\":[\"string\",\"null\"]}}", false, AiToolChangeKind.TypeChanged, AiToolCompatibility.Additive, "$.items.type") };
            yield return new object[] { new RestoredTransitionCase("return-nested-items-union-widened", "{\"type\":\"array\",\"items\":{\"type\":\"string\"}}", "{\"type\":\"array\",\"items\":{\"type\":[\"string\",\"null\"]}}", true, AiToolChangeKind.ReturnSchemaAdditive, AiToolCompatibility.Additive, "$.items.type") };
        }
    }

    [Fact]
    public void ToolCatalogMetadataAndAcceptancePathsCoverAddRemoveDescriptionAndApprovalChanges()
    {
        var oldCatalog = CaptureCatalog(Function("kept", "Before"), Function("removed"));
        var addedCatalog = CaptureCatalog(Function("kept", "After"), Function("added"));
        var descriptionDiff = AiToolContractVerifier.Compare(oldCatalog, addedCatalog);

        Assert.Contains(descriptionDiff.Changes, static change => change.Kind == AiToolChangeKind.ToolRemoved && change.Compatibility == AiToolCompatibility.Breaking);
        Assert.Contains(descriptionDiff.Changes, static change => change.Kind == AiToolChangeKind.ToolAdded && change.Compatibility == AiToolCompatibility.Additive);
        Assert.Contains(descriptionDiff.Changes, static change => change.Kind == AiToolChangeKind.DescriptionChanged && change.Compatibility == AiToolCompatibility.Risky);
        Assert.Throws<AiToolContractException>(() => AiToolContractVerifier.Accept(addedCatalog, descriptionDiff));
        Assert.Same(addedCatalog, AiToolContractVerifier.AcceptWithBreakingReview(addedCatalog, descriptionDiff));

        var plain = Function("approval");
        var approval = new ApprovalRequiredAIFunction(plain);
        var plainBaseline = CaptureCatalog(plain);
        var approvalBaseline = CaptureCatalog(approval);
        var approvalAdded = AiToolContractVerifier.Compare(plainBaseline, approvalBaseline);
        Assert.Contains(approvalAdded.Changes, static change => change.Kind == AiToolChangeKind.ApprovalSafetyMetadataChanged && change.Compatibility == AiToolCompatibility.Risky);
        Assert.Same(approvalBaseline, AiToolContractVerifier.Accept(approvalBaseline, approvalAdded));

        var approvalRemoved = AiToolContractVerifier.Compare(approvalBaseline, plainBaseline);
        Assert.Contains(approvalRemoved.Changes, static change => change.Kind == AiToolChangeKind.ApprovalSafetyMetadataChanged && change.Compatibility == AiToolCompatibility.Breaking);
        Assert.Throws<AiToolContractException>(() => AiToolContractVerifier.Accept(plainBaseline, approvalRemoved));
        Assert.Same(plainBaseline, AiToolContractVerifier.AcceptWithBreakingReview(plainBaseline, approvalRemoved));
    }

    [Fact]
    public void OneUnsupportedItemsTransitionFitsWithinOneChangeLimit()
    {
        var baseline = Capture("{\"type\":\"array\"}");
        var candidate = Capture("{\"type\":\"array\",\"items\":{\"type\":\"string\"}}");

        var diff = AiToolContractVerifier.Compare(baseline, candidate, new AiToolContractLimits { MaxChanges = 1 });

        Assert.False(diff.IsClean);
        Assert.Equal(AiToolCompatibility.Risky, diff.Compatibility);
        Assert.Single(diff.Changes);
        Assert.Equal(AiToolChangeKind.Unsupported, diff.Changes[0].Kind);
        Assert.Equal(AiToolCompatibility.Risky, diff.Changes[0].Compatibility);
        Assert.Equal("$.items", diff.Changes[0].Path);
        Assert.Contains(diff.Diagnostics, static diagnostic => diagnostic.Kind == AiToolDiagnosticKind.UnsupportedClassification);
    }

    [Fact]
    public void PublicChangeKindsAndCompatibilityLevelsAreEnumerated()
    {
        var observedKinds = new HashSet<AiToolChangeKind>();
        var observedCompatibility = new HashSet<AiToolCompatibility>();
        foreach (var row in ModelDerivedTransitionMatrix)
        {
            var testCase = (GeneratedTransitionCase)row[0];
            var schemas = SchemaTransitionSchemaGenerator.Build(testCase.Rule, testCase.Depth);
            var oldSchema = schemas.OldSchema;
            var newSchema = schemas.NewSchema;
            var returnSchema = testCase.ReturnSchema;
            var oldBaseline = returnSchema ? Capture("{\"type\":\"object\"}", oldSchema) : Capture(oldSchema);
            var newBaseline = returnSchema ? Capture("{\"type\":\"object\"}", newSchema) : Capture(newSchema);
            var diff = AiToolContractVerifier.Compare(oldBaseline, newBaseline);
            observedKinds.UnionWith(diff.Changes.Select(static change => change.Kind));
            observedCompatibility.Add(diff.Compatibility);
        }

        var catalogDiff = AiToolContractVerifier.Compare(CaptureCatalog(Function("old")), CaptureCatalog(Function("new")));
        observedKinds.UnionWith(catalogDiff.Changes.Select(static change => change.Kind));
        observedCompatibility.Add(catalogDiff.Compatibility);

        var approval = new ApprovalRequiredAIFunction(Function("approval"));
        var approvalDiff = AiToolContractVerifier.Compare(CaptureCatalog(Function("approval")), CaptureCatalog(approval));
        observedKinds.UnionWith(approvalDiff.Changes.Select(static change => change.Kind));
        observedCompatibility.Add(approvalDiff.Compatibility);

        Assert.True(new HashSet<AiToolChangeKind>(Enum.GetValues<AiToolChangeKind>()).SetEquals(observedKinds), string.Join(", ", Enum.GetValues<AiToolChangeKind>().Except(observedKinds)));
        var clean = AiToolContractVerifier.Compare(Capture("{}"), Capture("{}"));
        observedCompatibility.Add(clean.Compatibility);
        Assert.True(new HashSet<AiToolCompatibility>(Enum.GetValues<AiToolCompatibility>()).SetEquals(observedCompatibility), string.Join(", ", Enum.GetValues<AiToolCompatibility>().Except(observedCompatibility)));
    }

    [Fact]
    public void MalformedBaselineVersionsLimitsAndDuplicateIdentitiesFailClosed()
    {
        var malformed = Assert.Throws<AiToolContractException>(() => AiToolContractJson.Parse("not-json"));
        Assert.Equal(AiToolDiagnosticKind.MalformedBaseline, malformed.Diagnostic.Kind);

        var unsupportedVersion = Assert.Throws<AiToolContractException>(() => AiToolContractJson.Parse(Envelope("{\"type\":\"object\"}").Replace("\"schemaVersion\":1", "\"schemaVersion\":2", StringComparison.Ordinal)));
        Assert.Equal(AiToolDiagnosticKind.UnsupportedBaselineVersion, unsupportedVersion.Diagnostic.Kind);

        var duplicate = AiToolContractCapture.Capture(new AITool[] { Function("duplicate"), Function("duplicate") });
        Assert.False(duplicate.Succeeded);
        Assert.Equal(AiToolDiagnosticKind.DuplicateToolIdentity, duplicate.Diagnostic!.Kind);

        var catalog = AiToolContractJson.Serialize(CaptureCatalog(Function("one"), Function("two")));
        var toolLimit = Assert.Throws<AiToolContractException>(() => AiToolContractJson.Parse(catalog, new AiToolContractLimits { MaxTools = 1 }));
        Assert.Equal(AiToolDiagnosticKind.CanonicalizationOrResourceLimit, toolLimit.Diagnostic.Kind);

        var propertyLimit = CaptureWithLimits("{\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"string\"},\"b\":{\"type\":\"string\"}}}", new AiToolContractLimits { MaxProperties = 1 });
        Assert.Equal(AiToolDiagnosticKind.CanonicalizationOrResourceLimit, propertyLimit.Diagnostic!.Kind);

        var changeLimit = Assert.Throws<AiToolContractException>(() => AiToolContractVerifier.Compare(Capture("{}"), Capture("{\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"string\"},\"b\":{\"type\":\"string\"}}}"), new AiToolContractLimits { MaxChanges = 1 }));
        Assert.Equal(AiToolDiagnosticKind.CanonicalizationOrResourceLimit, changeLimit.Diagnostic.Kind);
    }

    [Fact]
    public void ReferenceAndDefinitionsCombinedShapeFailsAtTheClosedModelBoundary()
    {
        var capture = CaptureWithLimits("{\"$ref\":\"#/$defs/Order\",\"$defs\":{\"Order\":{\"type\":\"object\"}}}");

        Assert.False(capture.Succeeded);
        Assert.Equal(AiToolDiagnosticKind.UnsupportedClassification, capture.Diagnostic!.Kind);
    }

    [Fact]
    public void ReturnSchemaPresenceIsClassifiedAndUsesBothAcceptancePaths()
    {
        var absent = Capture("{}");
        var present = Capture("{}", "{\"type\":\"object\"}");
        var added = AiToolContractVerifier.Compare(absent, present);

        Assert.Single(added.Changes);
        Assert.Equal(AiToolChangeKind.ReturnSchemaAdditive, added.Changes[0].Kind);
        Assert.Equal(AiToolCompatibility.Additive, added.Compatibility);
        Assert.Same(present, AiToolContractVerifier.Accept(present, added));
        Assert.Same(present, AiToolContractVerifier.AcceptWithBreakingReview(present, added));

        var removed = AiToolContractVerifier.Compare(present, absent);
        Assert.Single(removed.Changes);
        Assert.Equal(AiToolChangeKind.ReturnSchemaBreaking, removed.Changes[0].Kind);
        Assert.Equal(AiToolCompatibility.Breaking, removed.Compatibility);
        Assert.Throws<AiToolContractException>(() => AiToolContractVerifier.Accept(absent, removed));
        Assert.Same(absent, AiToolContractVerifier.AcceptWithBreakingReview(absent, removed));
    }

    public sealed class GeneratedTransitionCase
    {
        internal GeneratedTransitionCase(SchemaTransitionRule rule, bool returnSchema, int depth)
        {
            Rule = rule;
            ReturnSchema = returnSchema;
            Depth = depth;
        }

        internal SchemaTransitionRule Rule { get; }
        internal bool ReturnSchema { get; }
        internal int Depth { get; }
        internal string Name => "matrix-" + (ReturnSchema ? "return" : "input") + "-depth" + Depth + "-" + Rule.PropertyName + "-" + Rule.Direction;
        internal string ExpectedPath => SchemaTransitionSchemaGenerator.ExpectedPath(Rule, Depth, ReturnSchema);
        public override string ToString() => Name;
    }

    public sealed class RestoredTransitionCase
    {
        public RestoredTransitionCase(string name, string oldSchema, string newSchema, bool returnSchema, AiToolChangeKind kind, AiToolCompatibility compatibility, string path)
        {
            Name = name;
            OldSchema = oldSchema;
            NewSchema = newSchema;
            ReturnSchema = returnSchema;
            Kind = kind;
            Compatibility = compatibility;
            Path = path;
        }

        public string Name { get; }
        public string OldSchema { get; }
        public string NewSchema { get; }
        public bool ReturnSchema { get; }
        public AiToolChangeKind Kind { get; }
        public AiToolCompatibility Compatibility { get; }
        public string Path { get; }
        public override string ToString() => Name;
    }

    private static class SchemaTransitionSchemaGenerator
    {
        internal static (string OldSchema, string NewSchema) Build(SchemaTransitionRule rule, int depth)
        {
            var target = BuildTarget(rule.PropertyName, rule.Direction);
            return (Wrap(target.OldSchema, depth), Wrap(target.NewSchema, depth));
        }

        internal static string ExpectedPath(SchemaTransitionRule rule, int depth, bool returnSchema)
        {
            var path = returnSchema ? "$.returnSchema" : "$";
            for (var level = depth - 1; level >= 0; level--)
                path += ".properties.nested" + level;

            var propertyPath = rule.PropertyName switch
            {
                nameof(NormalizedSchema.Reference) => ".$ref",
                nameof(NormalizedSchema.Description) => ".description",
                nameof(NormalizedSchema.HasDefault) or nameof(NormalizedSchema.DefaultValue) => ".default",
                nameof(NormalizedSchema.Format) => ".format",
                nameof(NormalizedSchema.Types) => ".type",
                nameof(NormalizedSchema.Required) => ".required",
                nameof(NormalizedSchema.EnumValues) => ".enum",
                nameof(NormalizedSchema.Items) => ".items",
                nameof(NormalizedSchema.Properties) => rule.Direction == SchemaTransitionDirection.Removed ? ".properties.result" : rule.Direction == SchemaTransitionDirection.RequiredPropertyAdded ? ".properties.query" : ".properties.note",
                _ => "." + char.ToLowerInvariant(rule.PropertyName[0]) + rule.PropertyName.Substring(1)
            };
            return path + propertyPath;
        }

        private static (string OldSchema, string NewSchema) BuildTarget(string propertyName, SchemaTransitionDirection direction)
        {
            if (propertyName == nameof(NormalizedSchema.Reference))
                return Pair(direction, "{}", "{\"$ref\":\"#/$defs/Order\"}", "{\"$ref\":\"#/$defs/Order\"}", "{\"$ref\":\"#/$defs/Invoice\"}");
            if (propertyName == nameof(NormalizedSchema.Description))
                return Pair(direction, "{}", "{\"description\":\"A value\"}", "{\"description\":\"Before\"}", "{\"description\":\"After\"}");
            if (propertyName == nameof(NormalizedSchema.HasDefault) || propertyName == nameof(NormalizedSchema.DefaultValue))
                return Pair(direction, "{}", "{\"default\":null}", "{\"default\":1}", "{\"default\":2}");
            if (propertyName == nameof(NormalizedSchema.Format))
                return Pair(direction, "{}", "{\"format\":\"date-time\"}", "{\"format\":\"date-time\"}", "{\"format\":\"uri\"}");
            if (propertyName == nameof(NormalizedSchema.Types))
                return direction switch
                {
                    SchemaTransitionDirection.Added => ("{}", "{\"type\":\"string\"}"),
                    SchemaTransitionDirection.Removed => ("{\"type\":\"string\"}", "{}"),
                    SchemaTransitionDirection.Expanded => ("{\"type\":\"string\"}", "{\"type\":[\"string\",\"null\"]}"),
                    SchemaTransitionDirection.Narrowed => ("{\"type\":[\"string\",\"null\"]}", "{\"type\":\"string\"}"),
                    _ => ("{\"type\":\"string\"}", "{\"type\":\"integer\"}")
                };
            if (propertyName == nameof(NormalizedSchema.Properties))
                return direction switch
                {
                    SchemaTransitionDirection.OptionalPropertyAdded => ("{\"type\":\"object\"}", "{\"type\":\"object\",\"properties\":{\"note\":{\"type\":\"string\"}}}"),
                    SchemaTransitionDirection.RequiredPropertyAdded => ("{\"type\":\"object\"}", "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"}},\"required\":[\"query\"]}"),
                    _ => ("{\"type\":\"object\",\"properties\":{\"result\":{\"type\":\"string\"}}}", "{\"type\":\"object\"}")
                };
            if (propertyName == nameof(NormalizedSchema.Required))
                return direction switch
                {
                    SchemaTransitionDirection.Added => ("{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"string\"}}}", "{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"string\"}},\"required\":[\"value\"]}"),
                    SchemaTransitionDirection.Removed => ("{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"string\"}},\"required\":[\"value\"]}", "{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"string\"}}}"),
                    _ => ("{\"type\":\"object\",\"required\":[\"old\"]}", "{\"type\":\"object\",\"required\":[\"new\"]}")
                };
            if (propertyName == nameof(NormalizedSchema.EnumValues))
                return direction switch
                {
                    SchemaTransitionDirection.Added => ("{\"type\":\"string\"}", "{\"type\":\"string\",\"enum\":[\"open\",\"closed\"]}"),
                    SchemaTransitionDirection.Removed => ("{\"type\":\"string\",\"enum\":[\"open\",\"closed\"]}", "{\"type\":\"string\"}"),
                    SchemaTransitionDirection.Expanded => ("{\"type\":\"string\",\"enum\":[\"open\"]}", "{\"type\":\"string\",\"enum\":[\"open\",\"closed\"]}"),
                    SchemaTransitionDirection.Narrowed => ("{\"type\":\"string\",\"enum\":[\"open\",\"closed\"]}", "{\"type\":\"string\",\"enum\":[\"open\"]}"),
                    _ => ("{\"type\":\"string\",\"enum\":[\"open\",\"closed\"]}", "{\"type\":\"string\",\"enum\":[\"open\",\"new\"]}")
                };
            if (propertyName == nameof(NormalizedSchema.Items))
                return Pair(direction, "{\"type\":\"array\"}", "{\"type\":\"array\",\"items\":{\"type\":\"string\"}}", "", "");

            var lowerBound = propertyName is nameof(NormalizedSchema.Minimum) or nameof(NormalizedSchema.ExclusiveMinimum) or nameof(NormalizedSchema.MinLength) or nameof(NormalizedSchema.MinItems);
            var keyword = propertyName switch
            {
                nameof(NormalizedSchema.Minimum) => "minimum",
                nameof(NormalizedSchema.Maximum) => "maximum",
                nameof(NormalizedSchema.ExclusiveMinimum) => "exclusiveMinimum",
                nameof(NormalizedSchema.ExclusiveMaximum) => "exclusiveMaximum",
                nameof(NormalizedSchema.MinLength) => "minLength",
                nameof(NormalizedSchema.MaxLength) => "maxLength",
                nameof(NormalizedSchema.MinItems) => "minItems",
                _ => "maxItems"
            };
            if (direction == SchemaTransitionDirection.Added)
                return ("{}", "{\"" + keyword + "\":1}");
            if (direction == SchemaTransitionDirection.Removed)
                return ("{\"" + keyword + "\":1}", "{}");
            if (direction == SchemaTransitionDirection.Narrowed)
                return ("{\"" + keyword + "\":" + (lowerBound ? "1" : "2") + "}", "{\"" + keyword + "\":" + (lowerBound ? "2" : "1") + "}");
            return ("{\"" + keyword + "\":" + (lowerBound ? "2" : "1") + "}", "{\"" + keyword + "\":" + (lowerBound ? "1" : "2") + "}");
        }

        private static (string OldSchema, string NewSchema) Pair(SchemaTransitionDirection direction, string addedOld, string addedNew, string changedOld, string changedNew) =>
            direction == SchemaTransitionDirection.Added ? (addedOld, addedNew) : direction == SchemaTransitionDirection.Removed ? (addedNew, addedOld) : (changedOld, changedNew);

        private static string Wrap(string schema, int depth)
        {
            for (var level = 0; level < depth; level++)
                schema = "{\"type\":\"object\",\"properties\":{\"nested" + level + "\":" + schema + "}}";
            return schema;
        }
    }

    private static AIFunction Function(string name, string description = "A tool") =>
        AIFunctionFactory.Create((string value) => value, new AIFunctionFactoryOptions { Name = name, Description = description });

    private static AiToolContractBaseline CaptureCatalog(params AITool[] tools)
    {
        var result = AiToolContractCapture.Capture(tools);
        Assert.True(result.Succeeded, result.Diagnostic?.Message);
        return result.Baseline!;
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

    private static IEnumerable<string[]> Permutations(string[] values)
    {
        if (values.Length == 0)
        {
            yield return Array.Empty<string>();
            yield break;
        }

        for (var index = 0; index < values.Length; index++)
        {
            var remaining = values.Where((_, remainingIndex) => remainingIndex != index).ToArray();
            foreach (var tail in Permutations(remaining))
                yield return new[] { values[index] }.Concat(tail).ToArray();
        }
    }
}
