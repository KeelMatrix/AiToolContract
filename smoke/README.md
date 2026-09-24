# Package consumer smoke

This non-packable project proves the first-success path from the built NuGet package rather than a project reference. The local feed contains only the package under test; framework dependencies restore from NuGet.org through the checked-in `NuGet.config`.

From the repository root, build the package first, create the isolated feed, copy the resulting `.nupkg` to `smoke/feed/`, then run:

```powershell
New-Item -ItemType Directory -Path .\smoke\feed -Force | Out-Null
Copy-Item .\artifacts\package-gate\KeelMatrix.AiToolContract.0.1.0-rc.1.nupkg .\smoke\feed\KeelMatrix.AiToolContract.0.1.0-rc.1.nupkg -Force
dotnet restore .\smoke\PackageConsumer\PackageConsumer.csproj --configfile .\smoke\PackageConsumer\NuGet.config --packages "$env:TEMP\ai-tool-contract-smoke-packages" --no-cache
dotnet build .\smoke\PackageConsumer\PackageConsumer.csproj -c Release --no-restore
dotnet run --project .\smoke\PackageConsumer\PackageConsumer.csproj -c Release --no-restore --no-build
```

The program captures a real `AIFunction`, writes and parses the baseline explicitly, verifies clean, then adds a required argument and requires a `Breaking` diff. It does not call a provider or require an API key.

After the smoke run, remove the isolated feed and package cache:

```powershell
Remove-Item -LiteralPath .\smoke\feed -Recurse -Force
Remove-Item -LiteralPath "$env:TEMP\ai-tool-contract-smoke-packages" -Recurse -Force -ErrorAction SilentlyContinue
```
