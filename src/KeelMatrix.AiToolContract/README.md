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

The configured `AiToolContractLimits` are enforced consistently during capture, baseline parsing, comparison, and serialization. This includes schema members, actual `properties` entries, nested schemas, every supported array-valued schema/default family, schema bytes, depth, tools, and reported changes; boundary values are accepted and overflows return a resource diagnostic.

See the [JSON Schema support matrix](https://github.com/KeelMatrix/AiToolContract/blob/main/docs/JSON-SCHEMA-SUPPORT.md) for the version-one keyword surface. See the [repository documentation](https://github.com/KeelMatrix/AiToolContract) for baseline acceptance, compatibility categories, limits, and privacy guidance.
