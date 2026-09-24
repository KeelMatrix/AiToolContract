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

static AiToolContractBaseline ParseBaseline(string schema) => AiToolContractJson.Parse("{\"schemaVersion\":1,\"tools\":[{\"name\":\"tool\",\"description\":null,\"inputSchema\":" + schema + ",\"returnSchema\":null,\"requiresApproval\":false}]}");
static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

var mixedBaseline = ParseBaseline("{\"type\":\"object\",\"properties\":{\"first\":{\"type\":\"string\"},\"second\":{\"type\":\"string\"}},\"required\":[\"first\"]}");
var mixedCandidate = ParseBaseline("{\"type\":\"object\",\"properties\":{\"first\":{\"type\":\"string\"},\"second\":{\"type\":\"string\"}},\"required\":[\"second\"]}");
var mixedDiff = AiToolContractVerifier.Compare(mixedBaseline, mixedCandidate);
Require(mixedDiff.Changes.Count == 3 && mixedDiff.Changes[0].Kind == AiToolChangeKind.Unsupported && mixedDiff.Changes.Any(static change => change.Kind == AiToolChangeKind.RequiredParameterAdded) && mixedDiff.Changes.Any(static change => change.Kind == AiToolChangeKind.OptionalParameterAdded), "The packed consumer did not preserve all mixed required changes.");
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
Require(siblingDiff.Changes.Count == 2 && siblingDiff.Changes.Any(static change => change.Path == "$.properties.Items.items") && siblingDiff.Changes.Any(static change => change.Path == "$.properties.items.items"), "The packed consumer collapsed case-distinct property paths.");

foreach (var propertyName in new[] { "Reference", "Description", "HasDefault", "DefaultValue", "Format", "Types", "Properties", "Required", "EnumValues", "Items", "Minimum", "Maximum", "ExclusiveMinimum", "ExclusiveMaximum", "MinLength", "MaxLength", "MinItems", "MaxItems" })
{
    var reservedBaseline = ParseBaseline("{\"type\":\"object\",\"properties\":{\"" + propertyName + "\":{\"type\":\"array\"}}}");
    var reservedCandidate = ParseBaseline("{\"type\":\"object\",\"properties\":{\"" + propertyName + "\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}}}");
    var reservedDiff = AiToolContractVerifier.Compare(reservedBaseline, reservedCandidate);
    Require(reservedDiff.Changes.Count == 1 && reservedDiff.Changes[0].Path == "$.properties." + propertyName + ".items", "The packed consumer rewrote reserved property path '" + propertyName + "'.");
}

Console.WriteLine("CONSUMER_SMOKE_PASS");
