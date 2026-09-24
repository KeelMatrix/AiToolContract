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
dotnet pack .\src\KeelMatrix.AiToolContract\KeelMatrix.AiToolContract.csproj -c Release --no-restore -o .\artifacts
dotnet list .\KeelMatrix.AiToolContract.sln package --vulnerable --include-transitive
```

The dependency matrix builds the minimum supported abstraction package with `-p:AiToolContractAbstractionsVersion=10.0.0` and the current tested package with `-p:AiToolContractAbstractionsVersion=10.10.0`. The shipping assembly is the only project with Public API baselines.

Package validation must inspect both archives, confirm the package-root README and license, confirm the dependency range and absence of provider SDKs, and run a separate `PackageReference` consumer from an isolated local feed. The consumer must use a real `AIFunction`, round-trip a baseline explicitly, verify clean, add a required argument, and observe a `Breaking` result. See the repository task scripts or issue evidence for the exact candidate commands.

