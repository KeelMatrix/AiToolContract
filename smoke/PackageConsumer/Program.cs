using System.Text.Json;
using KeelMatrix.AiToolContract;
using Microsoft.Extensions.AI;

var options = new AIFunctionFactoryOptions { Name = "search_orders", Description = "Search orders" };
var original = AIFunctionFactory.Create((string query) => query, options);
var captured = AiToolContractCapture.Capture(new AITool[] { original });
if (!captured.Succeeded)
    throw new InvalidOperationException(captured.Diagnostic!.Message);

var baselinePath = Path.Combine(Path.GetTempPath(), "ai-tool-contract-smoke-baseline.json");
File.WriteAllText(baselinePath, AiToolContractJson.Serialize(captured.Baseline!));
var parsed = AiToolContractJson.Parse(File.ReadAllText(baselinePath));
var clean = AiToolContractVerifier.Verify(parsed, new AITool[] { original });
if (!clean.IsClean)
    throw new InvalidOperationException("The package consumer did not verify its own baseline.");

var changed = AIFunctionFactory.Create((string query, string region) => query + region, options);
var breaking = AiToolContractVerifier.Verify(parsed, new AITool[] { changed });
if (breaking.IsClean || breaking.Diff is null || breaking.Diff.Compatibility != AiToolCompatibility.Breaking || !breaking.Diff.Changes.Any(static change => change.Kind == AiToolChangeKind.RequiredParameterAdded))
    throw new InvalidOperationException("The package consumer did not report the required argument as breaking.");

static AiToolContractBaseline ParseBaseline(string schema, string? output = null) => AiToolContractJson.Parse("{\"schemaVersion\":1,\"tools\":[{\"name\":\"tool\",\"description\":null,\"inputSchema\":" + schema + ",\"returnSchema\":" + (output ?? "null") + ",\"requiresApproval\":false}]}");
static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

var mixedBaseline = ParseBaseline("{\"type\":\"object\",\"properties\":{\"first\":{\"type\":\"string\"},\"second\":{\"type\":\"string\"}},\"required\":[\"first\"]}");
var mixedCandidate = ParseBaseline("{\"type\":\"object\",\"properties\":{\"first\":{\"type\":\"string\"},\"second\":{\"type\":\"string\"}},\"required\":[\"second\"]}");
var mixedDiff = AiToolContractVerifier.Compare(mixedBaseline, mixedCandidate);
Require(mixedDiff.Changes.Count == 2 && mixedDiff.Changes.Any(static change => change.Kind == AiToolChangeKind.RequiredParameterAdded) && mixedDiff.Changes.Any(static change => change.Kind == AiToolChangeKind.OptionalParameterAdded), "The packed consumer did not preserve composed required changes.");
Require(ReferenceEquals(mixedCandidate, AiToolContractVerifier.AcceptWithBreakingReview(mixedCandidate, mixedDiff)), "The composed change could not be accepted after breaking review.");
var mixedLimitRejected = false;
try
{
    AiToolContractVerifier.Compare(mixedBaseline, mixedCandidate, new AiToolContractLimits { MaxChanges = 1 });
}
catch (AiToolContractException)
{
    mixedLimitRejected = true;
}
Require(mixedLimitRejected, "The packed consumer did not enforce the mixed required change limit.");

var siblingBaseline = ParseBaseline("{\"type\":\"object\",\"properties\":{\"Items\":{\"type\":\"array\"},\"items\":{\"type\":\"array\"}}}");
var siblingCandidate = ParseBaseline("{\"type\":\"object\",\"properties\":{\"Items\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}},\"items\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"}}}}");
var siblingDiff = AiToolContractVerifier.Compare(siblingBaseline, siblingCandidate, new AiToolContractLimits { MaxChanges = 2 });
Require(siblingDiff.Changes.Count == 2 && siblingDiff.Changes.Any(static change => change.Path == "$.inputSchema.properties[\"Items\"].items") && siblingDiff.Changes.Any(static change => change.Path == "$.inputSchema.properties[\"items\"].items"), "The packed consumer collapsed case-distinct property paths.");

foreach (var propertyName in new[] { "Reference", "Description", "HasDefault", "DefaultValue", "Format", "Types", "Properties", "Required", "EnumValues", "Items", "Minimum", "Maximum", "ExclusiveMinimum", "ExclusiveMaximum", "MinLength", "MaxLength", "MinItems", "MaxItems" })
{
    var reservedBaseline = ParseBaseline("{\"type\":\"object\",\"properties\":{\"" + propertyName + "\":{\"type\":\"array\"}}}");
    var reservedCandidate = ParseBaseline("{\"type\":\"object\",\"properties\":{\"" + propertyName + "\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}}}");
    var reservedDiff = AiToolContractVerifier.Compare(reservedBaseline, reservedCandidate);
    Require(reservedDiff.Changes.Count == 1 && reservedDiff.Changes[0].Path == "$.inputSchema.properties[\"" + propertyName + "\"].items", "The packed consumer rewrote reserved property path '" + propertyName + "'.");
}

var outputBaseline = ParseBaseline("{}");
var oldOutput = AiToolContractJson.Parse(AiToolContractJson.Serialize(outputBaseline).Replace("\"returnSchema\":null", "\"returnSchema\":{\"type\":\"string\"}", StringComparison.Ordinal));
var newOutput = AiToolContractJson.Parse(AiToolContractJson.Serialize(outputBaseline).Replace("\"returnSchema\":null", "\"returnSchema\":{\"type\":[\"string\",\"null\"]}", StringComparison.Ordinal));
var outputDiff = AiToolContractVerifier.Compare(oldOutput, newOutput);
Require(outputDiff.Compatibility == AiToolCompatibility.Breaking, "A newly nullable output lost its breaking classification.");
var unrelatedRejected = false;
try { AiToolContractVerifier.Accept(newOutput, AiToolContractVerifier.Compare(oldOutput, oldOutput)); }
catch (AiToolContractException) { unrelatedRejected = true; }
Require(unrelatedRejected, "An unrelated comparison bypassed acceptance.");
foreach (var composition in new[]
{
    ("{\"type\":\"object\",\"required\":[\"id\"]}", "{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\"}}}", 2),
    ("{\"type\":\"object\",\"required\":[\"id\",\"stable\"]}", "{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\"},\"other\":{\"type\":\"string\"}},\"required\":[\"other\",\"stable\"]}", 4)
})
{
    var previous = ParseBaseline("{}", composition.Item1);
    var candidate = ParseBaseline("{}", composition.Item2);
    var diff = AiToolContractVerifier.Compare(previous, candidate, new AiToolContractLimits { MaxChanges = composition.Item3 });
    Require(diff.Changes.Count == composition.Item3 && diff.Compatibility == AiToolCompatibility.Breaking && !diff.Changes.Any(static change => change.Kind == AiToolChangeKind.Unsupported), "The packed consumer lost a required output component during declaration changes.");
    Require(diff.Changes.Any(static change => change.Kind == AiToolChangeKind.ReturnSchemaBreaking && change.Path == "$.returnSchema.required"), "The packed consumer lost the required output guarantee.");
    var rejected = false;
    try { AiToolContractVerifier.Accept(candidate, diff); }
    catch (AiToolContractException) { rejected = true; }
    Require(rejected, "The packed consumer accepted a lost required output guarantee without review.");
    Require(ReferenceEquals(candidate, AiToolContractVerifier.AcceptWithBreakingReview(candidate, diff)), "The packed consumer rejected a supported composition after review.");
    var limitRejected = false;
    try { AiToolContractVerifier.Compare(previous, candidate, new AiToolContractLimits { MaxChanges = composition.Item3 - 1 }); }
    catch (AiToolContractException) { limitRejected = true; }
    Require(limitRejected, "The packed consumer did not count independent declaration and required components.");
}
var deepBaseline = ParseBaseline("{\"properties\":{\"a\":{\"properties\":{\"b\":{}}}}}");
var depthLimits = new AiToolContractLimits { MaxSchemaDepth = 3 };
Require(AiToolContractVerifier.Compare(deepBaseline, AiToolContractJson.Parse(AiToolContractJson.Serialize(deepBaseline, depthLimits), depthLimits), depthLimits).IsClean, "Logical schema depth did not round trip.");

var existingMember = ParseBaseline("{\"required\":[\"id\"]}");
var restrictedMember = ParseBaseline("{\"properties\":{\"id\":{\"type\":\"string\"}}}");
var restrictedDiff = AiToolContractVerifier.Compare(existingMember, restrictedMember);
Require(restrictedDiff.Compatibility == AiToolCompatibility.Breaking && restrictedDiff.Changes.Any(static c => c.Kind == AiToolChangeKind.TypeChanged && c.Path == "$.inputSchema.properties[\"id\"].type"), "An existing required member lost value protection.");
var restrictionRejected = false;
try { AiToolContractVerifier.Accept(restrictedMember, restrictedDiff); }
catch (AiToolContractException) { restrictionRejected = true; }
Require(restrictionRejected, "The packed consumer accepted an unreviewed value restriction.");

using var emptySchema = JsonDocument.Parse("{}");
var malformed = AiToolContractCapture.Capture(new AITool[] { AIFunctionFactory.CreateDeclaration("\ud800", null, emptySchema.RootElement) });
Require(!malformed.Succeeded && malformed.Diagnostic!.Kind == AiToolDiagnosticKind.CaptureFailure, "Malformed Unicode was accepted for persistence.");
var validUnicode = AiToolContractCapture.Capture(new AITool[] { AIFunctionFactory.CreateDeclaration("\ufffd😀", "valid😀", emptySchema.RootElement) });
Require(validUnicode.Succeeded && AiToolContractVerifier.Compare(validUnicode.Baseline!, AiToolContractJson.Parse(AiToolContractJson.Serialize(validUnicode.Baseline!))).IsClean, "Valid Unicode did not persist losslessly.");

var descriptionsBefore = ParseBaseline("{\"description\":\"old\"}", "{\"description\":\"old\"}");
var descriptionsAfter = AiToolContractJson.Parse(AiToolContractJson.Serialize(ParseBaseline("{\"description\":\"new\"}", "{\"description\":\"new\"}")).Replace("\"description\":null", "\"description\":\"new\"", StringComparison.Ordinal));
var descriptionDiff = AiToolContractVerifier.Compare(descriptionsBefore, descriptionsAfter);
Require(descriptionDiff.Changes.Count == 3 && descriptionDiff.Changes.Select(static c => c.Path).Distinct(StringComparer.Ordinal).Count() == 3, "Tool and schema descriptions share a structured path.");
var enumValues = string.Join(",", Enumerable.Range(1, 1000));
var enumBefore = ParseBaseline("{\"enum\":[" + enumValues + "]}");
var enumAfter = ParseBaseline("{\"enum\":[" + enumValues + "],\"description\":\"changed\"}");
Require(AiToolContractVerifier.Compare(enumBefore, enumAfter, new AiToolContractLimits { MaxChanges = 1 }).Changes.Count == 1, "Unchanged enum membership generated a difference.");

foreach (var output in new[] { false, true })
{
    const string oldSchema = "{\"properties\":{\"id\":{}},\"required\":[\"id\"]}";
    const string nextSchema = "{\"properties\":{\"id\":{\"properties\":{\"child\":{\"default\":1}},\"required\":[\"child\"]}},\"required\":[\"id\"]}";
    var previous = ParseBaseline(output ? "{}" : oldSchema, output ? oldSchema : null);
    var candidate = ParseBaseline(output ? "{}" : nextSchema, output ? nextSchema : null);
    var diff = AiToolContractVerifier.Compare(previous, candidate);
    var role = output ? "$.returnSchema" : "$.inputSchema";
    Require(diff.Changes.Any(c => c.Kind == AiToolChangeKind.Unsupported && c.Path == role + ".properties[\"id\"].properties[\"child\"].default"), "A newly required child lost its unsupported component.");
    foreach (var breakingReview in new[] { false, true })
    {
        var rejected = false;
        try
        {
            if (breakingReview) AiToolContractVerifier.AcceptWithBreakingReview(candidate, diff);
            else AiToolContractVerifier.Accept(candidate, diff);
        }
        catch (AiToolContractException ex) { rejected = ex.Diagnostic.Kind == AiToolDiagnosticKind.UnsupportedClassification; }
        Require(rejected, "An unsupported required child bypassed acceptance.");
    }
    var counted = ParseBaseline(output ? "{}" : "{\"enum\":[1,2]}", output ? "{\"enum\":[1,2]}" : null);
    var located = false;
    try { AiToolContractJson.Serialize(counted, new AiToolContractLimits { MaxArrayItems = 1 }); }
    catch (AiToolContractException ex) { located = ex.Diagnostic.Kind == AiToolDiagnosticKind.CanonicalizationOrResourceLimit && ex.Diagnostic.Message.Contains(role + ".enum", StringComparison.Ordinal); }
    Require(located, "A counter failure lost its schema location.");
    var malformedLocated = false;
    try { ParseBaseline(output ? "{}" : "{\"description\":\"\\ud800\"}", output ? "{\"description\":\"\\ud800\"}" : null); }
    catch (AiToolContractException ex) { malformedLocated = ex.Diagnostic.Kind == AiToolDiagnosticKind.MalformedBaseline && ex.Diagnostic.Message.Contains(role + ".description", StringComparison.Ordinal); }
    Require(malformedLocated, "A malformed schema string lost its location.");
}

Console.WriteLine("CONSUMER_SMOKE_PASS");
