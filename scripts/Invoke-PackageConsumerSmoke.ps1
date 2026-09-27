[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PackagePath,

    [Parameter(Mandatory = $true)]
    [string] $Version,

    [string] $AbstractionsVersion = '10.10.0'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$packageConsumer = Join-Path $repoRoot 'smoke/PackageConsumer/PackageConsumer.csproj'
$netStandardConsumer = Join-Path $repoRoot 'smoke/PackageConsumer.NetStandard/PackageConsumer.NetStandard.csproj'
$nugetConfig = Join-Path $repoRoot 'smoke/PackageConsumer/NuGet.config'
$feed = Join-Path $repoRoot 'smoke/feed'
$packageCache = Join-Path ([IO.Path]::GetTempPath()) ('ai-tool-contract-smoke-packages-' + [Guid]::NewGuid().ToString('N'))

function Invoke-Dotnet([string[]] $Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') exited with code $LASTEXITCODE."
    }
}

try {
    if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) {
        throw "Package artifact '$PackagePath' is missing."
    }
    New-Item -ItemType Directory -Path $feed -Force | Out-Null
    New-Item -ItemType Directory -Path $packageCache -Force | Out-Null
    Copy-Item -LiteralPath $PackagePath -Destination (Join-Path $feed (Split-Path -Leaf $PackagePath)) -Force

    $restoreProperties = @(
        "-p:AiToolContractConsumerVersion=$Version",
        "-p:AiToolContractConsumerAbstractionsVersion=$AbstractionsVersion"
    )
    Invoke-Dotnet (@('restore', $packageConsumer, '--configfile', $nugetConfig, '--packages', $packageCache, '--no-cache') + $restoreProperties)
    Invoke-Dotnet @('build', $packageConsumer, '--configuration', 'Release', '--no-restore', '--packages', $packageCache)
    Invoke-Dotnet @('run', '--project', $packageConsumer, '--configuration', 'Release', '--no-restore', '--no-build')

    Invoke-Dotnet (@('restore', $netStandardConsumer, '--configfile', $nugetConfig, '--packages', $packageCache, '--no-cache') + $restoreProperties)
    Invoke-Dotnet @('build', $netStandardConsumer, '--configuration', 'Release', '--no-restore', '--packages', $packageCache)

    Write-Output 'PACKAGE_CONSUMER_SMOKE_PASS: net8.0 executed and netstandard2.0 referenced the exact package artifact.'
}
finally {
    if (Test-Path -LiteralPath $feed) {
        Remove-Item -LiteralPath $feed -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $packageCache) {
        Remove-Item -LiteralPath $packageCache -Recurse -Force -ErrorAction SilentlyContinue
    }
}
