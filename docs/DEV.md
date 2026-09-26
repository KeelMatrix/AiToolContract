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
dotnet list .\KeelMatrix.AiToolContract.sln package --vulnerable --include-transitive
```

## Package and archive gate

Run the repository-controlled gate from the repository root. It restores the solution, builds the packable project, creates the exact `.nupkg` and `.snupkg`, and inspects their metadata and contents.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Verify-Package.ps1
```

The gate reports a missing repository-root `icon.png` as `MISSING_PACKAGE_ICON` and continues with the other archive checks when that file is absent. The required path is resolved by the `PackageIcon` and pack item entries at lines 12 and 26 in `src/KeelMatrix.AiToolContract/KeelMatrix.AiToolContract.csproj`; no project-local icon copy is required.

The default gate requires the founder-owned icon. Public CI may use `-AllowMissingIcon` only for the staged pre-founder candidate; when the icon is present, the same gate validates its dimensions, metadata, package entry, and byte identity.

## Dependency matrix

Build the minimum supported abstraction package and the current tested package with the repository's central version override:

```powershell
dotnet build .\src\KeelMatrix.AiToolContract\KeelMatrix.AiToolContract.csproj -c Release -p:AiToolContractAbstractionsVersion=10.0.0
dotnet build .\src\KeelMatrix.AiToolContract\KeelMatrix.AiToolContract.csproj -c Release -p:AiToolContractAbstractionsVersion=10.10.0
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
```

The consumer captures a real `AIFunction`, round-trips a baseline, verifies clean, adds a required argument, and observes a `Breaking` result.

Clean up the isolated feed and package cache after the smoke run:

```powershell
Remove-Item -LiteralPath .\smoke\feed -Recurse -Force
Remove-Item -LiteralPath $smokePackages -Recurse -Force -ErrorAction SilentlyContinue
```
