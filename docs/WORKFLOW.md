# Capture and verification workflow

## Capture the first baseline

1. Build the application's real `AIFunction` or `AIFunctionDeclaration` catalog in the existing test or application setup.
2. Pass those framework objects to `AiToolContractCapture.Capture`.
3. Treat an unsuccessful result as a capture failure, duplicate identity, or resource-limit failure; do not serialize a partial baseline.
4. Serialize the successful baseline with `AiToolContractJson.Serialize` and write it to a caller-owned path.
5. Review the file as a source-code-like artifact before committing it.

Capture reads `Name`, `Description`, `JsonSchema`, `ReturnJsonSchema`, and the explicit `ApprovalRequiredAIFunction` wrapper marker. It never calls `InvokeAsync`, the underlying method, a model, a provider, or a network endpoint. Framework schema properties can be lazy, so the framework implementation may perform its own metadata generation when a property is read; this package does not execute the function or supply arguments.

## Verify a later catalog

```csharp
var baseline = AiToolContractJson.Parse(File.ReadAllText("ai-tools.baseline.json"));
var result = AiToolContractVerifier.Verify(baseline, tools);

if (!result.IsClean)
{
    foreach (var change in result.Diff?.Changes ?? Array.Empty<AiToolChange>())
        Console.WriteLine($"{change.Compatibility}: {change.Message}");
}
```

Verification is read-only. It does not update, overwrite, stage, or accept a baseline. A clean result requires no changes and no diagnostics. Unsupported schema semantics are review-relevant and never produce a clean result.

## Accept an update explicitly

After a human or policy review, call `AiToolContractVerifier.Accept` for a candidate with no breaking or unsupported differences, or `AcceptWithBreakingReview` after an explicit breaking-change review. Serialize the returned baseline yourself. Neither method writes a file or updates a repository.

