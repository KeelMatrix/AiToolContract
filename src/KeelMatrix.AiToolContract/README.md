# KeelMatrix.AiToolContract

Capture and review the model-visible contracts of Microsoft.Extensions.AI tools.

## Install

```sh
dotnet add package KeelMatrix.AiToolContract
```

## Quick Start

Pass the application’s real `AIFunction` instances to capture, write the returned baseline explicitly, and compare later captures with `AiToolContractVerifier`.

```csharp
using KeelMatrix.AiToolContract;
using Microsoft.Extensions.AI;

var tool = AIFunctionFactory.Create((string query, int limit) => $"{query}:{limit}");
var capture = AiToolContractCapture.Capture(new AITool[] { tool });
if (!capture.Succeeded)
    throw new InvalidOperationException(capture.Diagnostic!.Message);

var baselineJson = AiToolContractJson.Serialize(capture.Baseline!);
var baseline = AiToolContractJson.Parse(baselineJson);
var verification = AiToolContractVerifier.Verify(baseline, new AITool[] { tool });
Console.WriteLine(verification.IsClean); // True
```

This library is offline. It does not call a model, provider, network endpoint, or captured function. Structural compatibility also cannot prove that a model will make the same tool-selection decisions after a description or provider change.

## Supported frameworks

The package targets `net8.0` and `netstandard2.0` and is intended for cross-platform .NET applications on Windows, Linux, and macOS.

Malformed schemas and valid schema forms whose semantics are not represented by the closed normalized model fail closed with an explicit diagnostic. Standard JSON Schema type arrays express nullability; the OpenAPI `nullable` keyword is unsupported.

Newly required declarations retain unsupported default, format, reference, and item-schema transitions at each required child level. Both acceptance methods reject those differences. Schema counter, canonical-encoding, and malformed-string diagnostics identify the input or return schema and the affected location.

Arbitrary schema member names use quoted path segments, including failures before keyword validation; unrecognized keywords remain rejected.

Input compatibility preserves accepted calls; return compatibility preserves output guarantees. A newly nullable return, removed required return property, expanded return enum, or relaxed return bound is `Breaking`. Supported member changes compose; declaration and required-membership changes are reported independently. A declaration for an existing required name is compared against its previously unconstrained values. Acceptance requires the exact immutable candidate instance used by its comparison; compare again after recapturing or parsing a candidate. See the [compatibility guide](https://github.com/KeelMatrix/AiToolContract/blob/main/docs/COMPATIBILITY.md) for classifications and escaped diagnostic paths.

The configured `AiToolContractLimits` are enforced consistently during capture, baseline parsing, comparison, and serialization. This includes schema members, actual `properties` entries, nested schemas, every supported array-valued schema/default family, schema bytes, logical depth (including nested enum/default values), tools, and reported changes. Logical depth includes the root and has a stack-safety ceiling of 256 even when a larger limit is supplied. A derived aggregate UTF-8 budget also bounds the complete baseline envelope, tool names/descriptions, and canonical JSON in both directions of a parse/serialize round trip. Escaped output budgets are checked before constructing oversized output. Boundary values are accepted and overflows return a resource diagnostic. Oversized schema elements are rejected before copying their text. Persisted strings must contain valid Unicode: unmatched UTF-16 surrogates fail capture or baseline parsing instead of being replaced.

See the [JSON Schema support matrix](https://github.com/KeelMatrix/AiToolContract/blob/main/docs/JSON-SCHEMA-SUPPORT.md) for the version-one keyword surface. See the [repository documentation](https://github.com/KeelMatrix/AiToolContract) for baseline acceptance, compatibility categories, limits, and privacy guidance.
