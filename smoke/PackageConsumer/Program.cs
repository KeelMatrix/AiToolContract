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

Console.WriteLine("CONSUMER_SMOKE_PASS");
