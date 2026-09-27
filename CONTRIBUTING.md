# Contributing

## Before you begin

Install the .NET SDK selected by `global.json`. Restore only from the sources in `NuGet.config`.

## Making changes

Keep the public surface focused on capturing and reviewing Microsoft.Extensions.AI tool contracts. Do not add provider SDKs, model calls, tool execution, a CLI, MCP protocol support, hosted storage, or telemetry without a product decision. Update public API baselines when an intentional shipping API changes.

## Validation

Run focused tests while developing, then the full local validation command before submitting a change:

```powershell
dotnet restore .\KeelMatrix.AiToolContract.sln
dotnet test .\KeelMatrix.AiToolContract.sln -c Release --no-restore
dotnet pack .\src\KeelMatrix.AiToolContract\KeelMatrix.AiToolContract.csproj -c Release --no-restore -o .\artifacts
pwsh .\scripts\Invoke-VulnerabilityAudit.ps1 -Solution .\KeelMatrix.AiToolContract.sln -ConfigFile .\NuGet.config
```

Changes to the baseline schema, classification rules, documentation, package metadata, or public API require corresponding tests and documentation updates. Keep generated build output and local feeds out of commits.

## Submitting a change

Explain the consumer problem, the behavioral contract, tests run, and any compatibility or privacy consequence. For security reports, follow [SECURITY.md](SECURITY.md) instead of opening a public issue.
