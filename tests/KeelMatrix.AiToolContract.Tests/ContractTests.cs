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

    [Theory]
    [MemberData(nameof(TransitionMatrix))]
    public void ClosedModelTransitionMatrixClassifiesEveryEntry(string name, string oldSchema, string newSchema, bool returnSchema, AiToolChangeKind expectedKind, AiToolCompatibility expectedCompatibility, bool unsupported)
    {
        var oldBaseline = returnSchema ? Capture("{\"type\":\"object\"}", oldSchema) : Capture(oldSchema);
        var newBaseline = returnSchema ? Capture("{\"type\":\"object\"}", newSchema) : Capture(newSchema);
        var diff = AiToolContractVerifier.Compare(oldBaseline, newBaseline);

        Assert.False(diff.IsClean, name);
        Assert.Equal(expectedCompatibility, diff.Compatibility);
        Assert.Single(diff.Changes);
        Assert.Equal(expectedKind, diff.Changes[0].Kind);
        Assert.Equal(expectedCompatibility, diff.Changes[0].Compatibility);
        if (unsupported)
        {
            AssertUnsupported(diff);
            Assert.Throws<AiToolContractException>(() => AiToolContractVerifier.Accept(newBaseline, diff));
            Assert.Throws<AiToolContractException>(() => AiToolContractVerifier.AcceptWithBreakingReview(newBaseline, diff));
        }
        else if (expectedCompatibility == AiToolCompatibility.Breaking)
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
    public void PublicChangeKindsAndCompatibilityLevelsAreEnumerated()
    {
        var observedKinds = new HashSet<AiToolChangeKind>();
        var observedCompatibility = new HashSet<AiToolCompatibility>();
        foreach (var row in TransitionMatrix)
        {
            var oldSchema = (string)row[1];
            var newSchema = (string)row[2];
            var returnSchema = (bool)row[3];
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

    public static IEnumerable<object[]> TransitionMatrix
    {
        get
        {
            yield return Matrix("reference-added", "{}", "{\"$ref\":\"#/$defs/Order\"}", false, AiToolChangeKind.Unsupported, AiToolCompatibility.Risky, true);
            yield return Matrix("reference-removed", "{\"$ref\":\"#/$defs/Order\"}", "{}", false, AiToolChangeKind.Unsupported, AiToolCompatibility.Risky, true);
            yield return Matrix("reference-target-changed", "{\"$ref\":\"#/$defs/Order\"}", "{\"$ref\":\"#/$defs/Invoice\"}", false, AiToolChangeKind.Unsupported, AiToolCompatibility.Risky, true);
            yield return Matrix("description-added", "{}", "{\"description\":\"A value\"}", false, AiToolChangeKind.DescriptionChanged, AiToolCompatibility.Risky, false);
            yield return Matrix("description-removed", "{\"description\":\"A value\"}", "{}", false, AiToolChangeKind.DescriptionChanged, AiToolCompatibility.Risky, false);
            yield return Matrix("default-added", "{}", "{\"default\":null}", false, AiToolChangeKind.Unsupported, AiToolCompatibility.Risky, true);
            yield return Matrix("default-removed", "{\"default\":null}", "{}", false, AiToolChangeKind.Unsupported, AiToolCompatibility.Risky, true);
            yield return Matrix("format-added", "{}", "{\"format\":\"date-time\"}", false, AiToolChangeKind.Unsupported, AiToolCompatibility.Risky, true);
            yield return Matrix("format-removed", "{\"format\":\"date-time\"}", "{}", false, AiToolChangeKind.Unsupported, AiToolCompatibility.Risky, true);

            yield return Matrix("type-added", "{}", "{\"type\":\"string\"}", false, AiToolChangeKind.TypeChanged, AiToolCompatibility.Breaking, false);
            yield return Matrix("type-removed", "{\"type\":\"string\"}", "{}", false, AiToolChangeKind.TypeChanged, AiToolCompatibility.Additive, false);
            yield return Matrix("type-union-widened", "{\"type\":\"string\"}", "{\"type\":[\"string\",\"null\"]}", false, AiToolChangeKind.TypeChanged, AiToolCompatibility.Additive, false);
            yield return Matrix("type-union-narrowed", "{\"type\":[\"string\",\"null\"]}", "{\"type\":\"string\"}", false, AiToolChangeKind.TypeChanged, AiToolCompatibility.Breaking, false);
            yield return Matrix("type-primitive-to-object", "{\"type\":\"string\"}", "{\"type\":\"object\"}", false, AiToolChangeKind.TypeChanged, AiToolCompatibility.Breaking, false);
            yield return Matrix("type-object-to-array", "{\"type\":\"object\"}", "{\"type\":\"array\"}", false, AiToolChangeKind.TypeChanged, AiToolCompatibility.Breaking, false);
            yield return Matrix("return-type-union-widened", "{\"type\":\"string\"}", "{\"type\":[\"string\",\"null\"]}", true, AiToolChangeKind.ReturnSchemaAdditive, AiToolCompatibility.Additive, false);
            yield return Matrix("return-type-union-narrowed", "{\"type\":[\"string\",\"null\"]}", "{\"type\":\"string\"}", true, AiToolChangeKind.ReturnSchemaBreaking, AiToolCompatibility.Breaking, false);

            yield return Matrix("enum-added", "{\"type\":\"string\"}", "{\"type\":\"string\",\"enum\":[\"open\",\"closed\"]}", false, AiToolChangeKind.EnumNarrowed, AiToolCompatibility.Breaking, false);
            yield return Matrix("enum-removed", "{\"type\":\"string\",\"enum\":[\"open\",\"closed\"]}", "{\"type\":\"string\"}", false, AiToolChangeKind.EnumExpanded, AiToolCompatibility.Additive, false);
            yield return Matrix("enum-expanded", "{\"type\":\"string\",\"enum\":[\"open\"]}", "{\"type\":\"string\",\"enum\":[\"open\",\"closed\"]}", false, AiToolChangeKind.EnumExpanded, AiToolCompatibility.Additive, false);
            yield return Matrix("enum-narrowed", "{\"type\":\"string\",\"enum\":[\"open\",\"closed\"]}", "{\"type\":\"string\",\"enum\":[\"open\"]}", false, AiToolChangeKind.EnumNarrowed, AiToolCompatibility.Breaking, false);
            yield return Matrix("return-enum-expanded", "{\"type\":\"string\",\"enum\":[\"open\"]}", "{\"type\":\"string\",\"enum\":[\"open\",\"closed\"]}", true, AiToolChangeKind.ReturnSchemaAdditive, AiToolCompatibility.Additive, false);
            yield return Matrix("return-enum-narrowed", "{\"type\":\"string\",\"enum\":[\"open\",\"closed\"]}", "{\"type\":\"string\",\"enum\":[\"open\"]}", true, AiToolChangeKind.ReturnSchemaBreaking, AiToolCompatibility.Breaking, false);
            yield return Matrix("enum-mixed", "{\"type\":\"string\",\"enum\":[\"open\",\"closed\"]}", "{\"type\":\"string\",\"enum\":[\"open\",\"new\"]}", false, AiToolChangeKind.Unsupported, AiToolCompatibility.Risky, true);

            yield return Matrix("items-added", "{\"type\":\"array\"}", "{\"type\":\"array\",\"items\":{\"type\":\"string\"}}", false, AiToolChangeKind.Unsupported, AiToolCompatibility.Risky, true);
            yield return Matrix("items-removed", "{\"type\":\"array\",\"items\":{\"type\":\"string\"}}", "{\"type\":\"array\"}", false, AiToolChangeKind.Unsupported, AiToolCompatibility.Risky, true);
            yield return Matrix("nested-items-union-widened", "{\"type\":\"array\",\"items\":{\"type\":\"string\"}}", "{\"type\":\"array\",\"items\":{\"type\":[\"string\",\"null\"]}}", false, AiToolChangeKind.TypeChanged, AiToolCompatibility.Additive, false);

            yield return Matrix("optional-property-added", "{\"type\":\"object\"}", "{\"type\":\"object\",\"properties\":{\"note\":{\"type\":\"string\"}}}", false, AiToolChangeKind.OptionalParameterAdded, AiToolCompatibility.Additive, false);
            yield return Matrix("required-property-added", "{\"type\":\"object\"}", "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"}},\"required\":[\"query\"]}", false, AiToolChangeKind.RequiredParameterAdded, AiToolCompatibility.Breaking, false);
            yield return Matrix("property-removed", "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"}}}", "{\"type\":\"object\"}", false, AiToolChangeKind.ParameterRemoved, AiToolCompatibility.Breaking, false);
            yield return Matrix("nested-property-type-changed", "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"}}}", "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"integer\"}}}", false, AiToolChangeKind.TypeChanged, AiToolCompatibility.Breaking, false);
            yield return Matrix("return-optional-property-added", "{\"type\":\"object\"}", "{\"type\":\"object\",\"properties\":{\"result\":{\"type\":\"string\"}}}", true, AiToolChangeKind.ReturnSchemaAdditive, AiToolCompatibility.Additive, false);
            yield return Matrix("return-required-property-added", "{\"type\":\"object\"}", "{\"type\":\"object\",\"properties\":{\"result\":{\"type\":\"string\"}},\"required\":[\"result\"]}", true, AiToolChangeKind.ReturnSchemaBreaking, AiToolCompatibility.Breaking, false);
            yield return Matrix("return-property-removed", "{\"type\":\"object\",\"properties\":{\"result\":{\"type\":\"string\"}}}", "{\"type\":\"object\"}", true, AiToolChangeKind.ReturnSchemaBreaking, AiToolCompatibility.Breaking, false);

            yield return Matrix("required-declared-added", "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"}}}", "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"}},\"required\":[\"query\"]}", false, AiToolChangeKind.RequiredParameterAdded, AiToolCompatibility.Breaking, false);
            yield return Matrix("required-declared-removed", "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"}},\"required\":[\"query\"]}", "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"}}}", false, AiToolChangeKind.OptionalParameterAdded, AiToolCompatibility.Additive, false);
            yield return Matrix("required-undeclared-added", "{\"type\":\"object\"}", "{\"type\":\"object\",\"required\":[\"ghost\"]}", false, AiToolChangeKind.RequiredParameterAdded, AiToolCompatibility.Breaking, false);
            yield return Matrix("required-undeclared-removed", "{\"type\":\"object\",\"required\":[\"ghost\"]}", "{\"type\":\"object\"}", false, AiToolChangeKind.OptionalParameterAdded, AiToolCompatibility.Additive, false);
            yield return Matrix("return-required-declared-added", "{\"type\":\"object\",\"properties\":{\"result\":{\"type\":\"string\"}}}", "{\"type\":\"object\",\"properties\":{\"result\":{\"type\":\"string\"}},\"required\":[\"result\"]}", true, AiToolChangeKind.ReturnSchemaBreaking, AiToolCompatibility.Breaking, false);
            yield return Matrix("return-required-declared-removed", "{\"type\":\"object\",\"properties\":{\"result\":{\"type\":\"string\"}},\"required\":[\"result\"]}", "{\"type\":\"object\",\"properties\":{\"result\":{\"type\":\"string\"}}}", true, AiToolChangeKind.ReturnSchemaAdditive, AiToolCompatibility.Additive, false);

            foreach (var memberName in new[] { "minimum", "exclusiveMinimum", "minLength", "minItems", "maximum", "exclusiveMaximum", "maxLength", "maxItems" })
            {
                var lowerBound = memberName is "minimum" or "exclusiveMinimum" or "minLength" or "minItems";
                var narrowOld = lowerBound ? "1" : "2";
                var narrowNew = lowerBound ? "2" : "1";
                yield return Matrix("input-" + memberName + "-added", "{}", SchemaWithMember(memberName, "1"), false, AiToolChangeKind.ConstraintNarrowed, AiToolCompatibility.Breaking, false);
                yield return Matrix("input-" + memberName + "-removed", SchemaWithMember(memberName, "1"), "{}", false, AiToolChangeKind.ConstraintExpanded, AiToolCompatibility.Additive, false);
                yield return Matrix("input-" + memberName + "-narrowed", SchemaWithMember(memberName, narrowOld), SchemaWithMember(memberName, narrowNew), false, AiToolChangeKind.ConstraintNarrowed, AiToolCompatibility.Breaking, false);
                yield return Matrix("input-" + memberName + "-widened", SchemaWithMember(memberName, narrowNew), SchemaWithMember(memberName, narrowOld), false, AiToolChangeKind.ConstraintExpanded, AiToolCompatibility.Additive, false);
                yield return Matrix("return-" + memberName + "-narrowed", SchemaWithMember(memberName, narrowOld), SchemaWithMember(memberName, narrowNew), true, AiToolChangeKind.ReturnSchemaBreaking, AiToolCompatibility.Breaking, false);
                yield return Matrix("return-" + memberName + "-widened", SchemaWithMember(memberName, narrowNew), SchemaWithMember(memberName, narrowOld), true, AiToolChangeKind.ReturnSchemaAdditive, AiToolCompatibility.Additive, false);
            }
        }
    }

    private static object[] Matrix(string name, string oldSchema, string newSchema, bool returnSchema, AiToolChangeKind kind, AiToolCompatibility compatibility, bool unsupported) =>
        new object[] { name, oldSchema, newSchema, returnSchema, kind, compatibility, unsupported };

    private static string SchemaWithMember(string name, string value) => "{\"" + name + "\":" + value + "}";

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
