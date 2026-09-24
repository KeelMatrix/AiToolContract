# KeelMatrix.AiToolContract development guide

## Layout

- `src/KeelMatrix.AiToolContract/` contains the single packable library and its package README.
- `tests/KeelMatrix.AiToolContract.Tests/` contains offline unit and integration-style contract tests.
- `docs/` contains the baseline, workflow, compatibility, and developer guides.
- `PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt` belong only beside the shipping assembly.

## Source navigation

Start at `AiToolContractCapture.cs` for the framework boundary, `AiToolContractJson.cs` for the versioned file format, and `AiToolContractVerifier.cs` for classification. Keep schema canonicalization bounded and fail closed when a semantic rule is not defensible.

## Validation escalation

Use a focused test first, then the affected test project, then the Release solution test, package build and archive inspection, clean package-consumer smoke, and vulnerable-package audit. Tests must be offline and must not require provider credentials.

## Scope discipline

Do not invoke captured functions or models. Do not add provider SDKs, MCP support, a CLI, hosted storage, automatic baseline writes, or telemetry. Do not place private schemas, credentials, local feeds, or machine-specific settings in the repository.

