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
Require(siblingDiff.Changes.Count == 2 && siblingDiff.Changes.Any(static change => change.Path == "$.properties[\"Items\"].items") && siblingDiff.Changes.Any(static change => change.Path == "$.properties[\"items\"].items"), "The packed consumer collapsed case-distinct property paths.");

foreach (var propertyName in new[] { "Reference", "Description", "HasDefault", "DefaultValue", "Format", "Types", "Properties", "Required", "EnumValues", "Items", "Minimum", "Maximum", "ExclusiveMinimum", "ExclusiveMaximum", "MinLength", "MaxLength", "MinItems", "MaxItems" })
{
    var reservedBaseline = ParseBaseline("{\"type\":\"object\",\"properties\":{\"" + propertyName + "\":{\"type\":\"array\"}}}");
    var reservedCandidate = ParseBaseline("{\"type\":\"object\",\"properties\":{\"" + propertyName + "\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}}}");
    var reservedDiff = AiToolContractVerifier.Compare(reservedBaseline, reservedCandidate);
    Require(reservedDiff.Changes.Count == 1 && reservedDiff.Changes[0].Path == "$.properties[\"" + propertyName + "\"].items", "The packed consumer rewrote reserved property path '" + propertyName + "'.");
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

Console.WriteLine("CONSUMER_SMOKE_PASS");
