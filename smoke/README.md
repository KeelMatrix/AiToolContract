# Package consumer smoke

This non-packable project proves the first-success path from the built NuGet package rather than a project reference. The local feed contains only the package under test; framework dependencies restore from NuGet.org through the checked-in `NuGet.config`.

From the repository root, build the package first, copy the resulting `.nupkg` to `smoke/feed/`, then run:

```powershell
dotnet restore .\smoke\PackageConsumer\PackageConsumer.csproj --configfile .\smoke\PackageConsumer\NuGet.config --packages "$env:TEMP\ai-tool-contract-smoke-packages" --no-cache
dotnet build .\smoke\PackageConsumer\PackageConsumer.csproj -c Release --no-restore
dotnet run --project .\smoke\PackageConsumer\PackageConsumer.csproj -c Release --no-restore --no-build
```

The program captures a real `AIFunction`, writes and parses the baseline explicitly, verifies clean, then adds a required argument and requires a `Breaking` diff. It does not call a provider or require an API key.

