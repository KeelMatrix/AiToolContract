# KeelMatrix.AiToolContract

KeelMatrix.AiToolContract baselines and compatibility-checks the model-visible contracts of Microsoft.Extensions.AI tools: names, descriptions, input JSON Schema, return JSON Schema, and the explicit approval wrapper exposed by the framework.

## Install

```sh
dotnet add package KeelMatrix.AiToolContract --version 0.1.0
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

## Supported frameworks

The package targets `net8.0` and `netstandard2.0` and is intended for cross-platform .NET applications on Windows, Linux, and macOS.

## What is classified

The verifier reports `Breaking`, `Risky`, `Additive`, or `Informational` results. It distinguishes tool add/remove, required and optional parameter changes, parameter removal, standard JSON Schema type-union changes, enum and supported constraint changes, return-schema changes, description changes, approval metadata changes, and unsupported schema changes. A rename is reported as a remove plus an add. The normalized schema model is a closed surface: the supported framework-emitted fields are listed in the [JSON Schema support matrix](docs/JSON-SCHEMA-SUPPORT.md), and unlisted keywords fail closed.

Malformed schemas and unknown or ambiguous schema semantics fail closed with an explicit diagnostic. Verification never overwrites a baseline or auto-approves a breaking change.

Newly required declarations retain unsupported default, format, reference, and item-schema transitions at each required child level. Both acceptance methods reject those differences. Schema counter, canonical-encoding, and malformed-string diagnostics identify the input or return schema and the affected location.

Arbitrary schema member names use quoted path segments, including failures before keyword validation; unrecognized keywords remain rejected.

Input compatibility preserves accepted calls; return compatibility preserves output guarantees. A newly nullable return, removed required return property, expanded return enum, or relaxed return bound is `Breaking`. Supported member changes compose; declaration and required-membership changes are reported independently. A declaration for an existing required name is compared against its previously unconstrained values. Acceptance requires the exact immutable candidate instance used by its comparison; compare again after recapturing or parsing a candidate. See the [compatibility guide](docs/COMPATIBILITY.md) for classifications and escaped diagnostic paths.

The configured `AiToolContractLimits` are enforced consistently during capture, baseline parsing, comparison, and serialization. This includes schema members, actual `properties` entries, nested schemas, every supported array-valued schema/default family, schema bytes, logical depth (including nested enum/default values), tools, and reported changes. Logical depth includes the root and has a stack-safety ceiling of 256 even when a larger limit is supplied. A derived aggregate UTF-8 budget also bounds the complete baseline envelope, tool names/descriptions, and canonical JSON in both directions of a parse/serialize round trip. Escaped output budgets are checked before constructing oversized output. Boundary values are accepted and overflows return a resource diagnostic. Oversized schema elements are rejected before copying their text. Persisted strings must contain valid Unicode: unmatched UTF-16 surrogates fail capture or baseline parsing instead of being replaced.

## Documentation

The root [website classification manifest](keelmatrix.website.json) declares this library as the public product family's primary package. It supports discovery of existing published versions; future versions also carry matching classification tags.

- [Capture and verification workflow](docs/WORKFLOW.md)
- [Compatibility categories and examples](docs/COMPATIBILITY.md)
- [Baseline format and versioning](docs/BASELINE.md)
- [JSON Schema support matrix](docs/JSON-SCHEMA-SUPPORT.md)
- [Development and validation](docs/DEV.md)

## Privacy

Tool names, descriptions, schemas, parameter names, enum values, and return schemas can reveal proprietary capabilities. Baselines are source-code-like artifacts: keep them under the same access controls as the application and do not publish them automatically from private applications. This package ships no telemetry, analytics, network client, or background activity.

## License

MIT. See [LICENSE](LICENSE).
