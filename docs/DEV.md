# Development and validation

The repository pins the SDK in `global.json`, restore sources in `NuGet.config`, and package versions in `Directory.Packages.props`.

## Focused loop

```powershell
dotnet test .\tests\KeelMatrix.AiToolContract.Tests\KeelMatrix.AiToolContract.Tests.csproj -c Release --filter FullyQualifiedName~ContractTests
```

## Full local pass

```powershell
dotnet restore .\KeelMatrix.AiToolContract.sln
dotnet build .\KeelMatrix.AiToolContract.sln -c Release --no-restore
dotnet test .\KeelMatrix.AiToolContract.sln -c Release --no-restore
dotnet format .\KeelMatrix.AiToolContract.sln --verify-no-changes --no-restore
dotnet pack .\src\KeelMatrix.AiToolContract\KeelMatrix.AiToolContract.csproj -c Release --no-restore -o .\artifacts
pwsh .\scripts\Invoke-VulnerabilityAudit.ps1 -Solution .\KeelMatrix.AiToolContract.sln -ConfigFile .\NuGet.config
```

The vulnerability audit validates the scanner's JSON structure before walking findings. A missing, wrong-type, truncated, or structurally malformed top-level, project, framework, package, or vulnerability node fails closed instead of being treated as a clean audit.

## Package and archive gate

Run the repository-controlled gate from the repository root. It restores the solution, builds the packable project, creates the exact `.nupkg` and `.snupkg`, and inspects their metadata and contents.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Verify-Package.ps1
```

The default gate fails closed with `MISSING_PACKAGE_ICON` when the repository-root `icon.png` is absent or invalid. The required path is resolved by the `PackageIcon` and pack item entries in `src/KeelMatrix.AiToolContract/KeelMatrix.AiToolContract.csproj`; no project-local icon copy is required.

The optional `-AllowMissingIcon` switch is reserved for local pre-icon diagnostics and is not used by CI or release workflows. When the icon is present, the gate validates its 512x512 dimensions, metadata, package entry, and byte identity.

## Dependency matrix

Build the minimum supported abstraction package and the current tested package with the repository's central version override:

```powershell
dotnet build .\src\KeelMatrix.AiToolContract\KeelMatrix.AiToolContract.csproj -c Release -p:AiToolContractAbstractionsVersion=10.0.0
dotnet build .\src\KeelMatrix.AiToolContract\KeelMatrix.AiToolContract.csproj -c Release -p:AiToolContractAbstractionsVersion=10.10.1
```

The shipping assembly is the only project with Public API baselines. The package gate is the canonical local archive inspection command.

## Package-reference consumer smoke

After the package gate has produced the archive, create the isolated smoke feed, copy the exact package into it, and run the consumer without a project reference:

```powershell
New-Item -ItemType Directory -Path .\smoke\feed -Force | Out-Null
Copy-Item .\artifacts\package-gate\KeelMatrix.AiToolContract.0.1.0-rc.1.nupkg .\smoke\feed\KeelMatrix.AiToolContract.0.1.0-rc.1.nupkg -Force
$smokePackages = Join-Path $env:TEMP 'ai-tool-contract-smoke-packages'
dotnet restore .\smoke\PackageConsumer\PackageConsumer.csproj --configfile .\smoke\PackageConsumer\NuGet.config --packages $smokePackages --no-cache
dotnet build .\smoke\PackageConsumer\PackageConsumer.csproj -c Release --no-restore
dotnet run --project .\smoke\PackageConsumer\PackageConsumer.csproj -c Release --no-restore --no-build
dotnet restore .\smoke\PackageConsumer.NetStandard\PackageConsumer.NetStandard.csproj --configfile .\smoke\PackageConsumer\NuGet.config --packages $smokePackages --no-cache
dotnet build .\smoke\PackageConsumer.NetStandard\PackageConsumer.NetStandard.csproj -c Release --no-restore --packages $smokePackages
```

The consumer captures a real `AIFunction`, round-trips a baseline, verifies clean, adds a required argument, and observes a `Breaking` result.

The CI/release-equivalent command is `pwsh .\scripts\Invoke-PackageConsumerSmoke.ps1 -PackagePath <exact-nupkg> -Version <package-version> -AbstractionsVersion 10.10.1`; it runs the net8.0 consumer and compiles a separate netstandard2.0 consumer against that exact archive.

Clean up the isolated feed and package cache after the smoke run:

```powershell
Remove-Item -LiteralPath .\smoke\feed -Recurse -Force
Remove-Item -LiteralPath $smokePackages -Recurse -Force -ErrorAction SilentlyContinue
```
