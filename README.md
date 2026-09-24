# KeelMatrix.AiToolContract

KeelMatrix.AiToolContract baselines and compatibility-checks the model-visible contracts of Microsoft.Extensions.AI tools: names, descriptions, input JSON Schema, return JSON Schema, and the explicit approval wrapper exposed by the framework.

## Install

```sh
dotnet add package KeelMatrix.AiToolContract
```

## Quick Start

Capture the real `AIFunction` objects already used by an application or test project. Write and accept the first baseline explicitly, then verify future captures.

```csharp
using KeelMatrix.AiToolContract;
using Microsoft.Extensions.AI;

var tool = AIFunctionFactory.Create((string query, int limit) => $"{query}:{limit}");
var captured = AiToolContractCapture.Capture(new AITool[] { tool });
if (!captured.Succeeded)
    throw new InvalidOperationException(captured.Diagnostic!.Message);

var baselineText = AiToolContractJson.Serialize(captured.Baseline!);
File.WriteAllText("ai-tools.baseline.json", baselineText);

var baseline = AiToolContractJson.Parse(File.ReadAllText("ai-tools.baseline.json"));
var verification = AiToolContractVerifier.Verify(baseline, new AITool[] { tool });
if (!verification.IsClean)
    throw new InvalidOperationException("AI tool contract requires review.");
```

No model, provider, API key, or network call is required. Capture reads callable metadata and JSON Schema; it never invokes a captured function. Structural compatibility does not prove that a model will make the same tool-selection decisions after a description or provider change.

## What is classified

The verifier reports `Breaking`, `Risky`, `Additive`, or `Informational` results. It distinguishes tool add/remove, required and optional parameter changes, parameter removal, type and nullability changes, enum and supported constraint changes, return-schema changes, description changes, approval metadata changes, and unsupported schema changes. A rename is reported as a remove plus an add.

Malformed schemas and unknown or ambiguous schema semantics fail closed with an explicit diagnostic. Verification never overwrites a baseline or auto-approves a breaking change.

## Documentation

- [Capture and verification workflow](docs/WORKFLOW.md)
- [Compatibility categories and examples](docs/COMPATIBILITY.md)
- [Baseline format and versioning](docs/BASELINE.md)
- [JSON Schema support matrix](docs/JSON-SCHEMA-SUPPORT.md)
- [Development and validation](docs/DEV.md)

## Privacy

Tool names, descriptions, schemas, parameter names, enum values, and return schemas can reveal proprietary capabilities. Baselines are source-code-like artifacts: keep them under the same access controls as the application and do not publish them automatically from private applications. This package ships no telemetry, analytics, network client, or background activity.

## License

MIT. See [LICENSE](LICENSE).
