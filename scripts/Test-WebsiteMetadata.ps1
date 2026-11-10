#requires -Version 7.0
param(
    [string] $RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [string] $PackagePath,
    [ValidateSet('Release')]
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../build/Test-WebsiteMetadata.ps1')
$manifest = [IO.File]::ReadAllText((Join-Path $RepositoryRoot 'keelmatrix.website.json'))
$projects = @(Get-ChildItem -LiteralPath $RepositoryRoot -Recurse -File -Filter '*.csproj' |
    Where-Object { $_.FullName -notmatch '[/\\](bin|obj)[/\\]' })
$packages = @(
    foreach ($project in $projects) {
        $output = & dotnet msbuild $project.FullName -nologo "-p:Configuration=$Configuration" '-getProperty:IsPackable,PackageId,PackageTags,PackageProjectUrl,RepositoryUrl,RepositoryType'
        if ($LASTEXITCODE -ne 0) { throw 'Website metadata MSBuild evaluation failed.' }
        $properties = ($output -join "`n" | ConvertFrom-Json).Properties
        if ($properties.IsPackable -ieq 'true') { $properties }
    }
)
Assert-WebsiteMetadata $manifest $packages
if ($PackagePath) { Assert-WebsitePackage $PackagePath $manifest $packages }
Write-Output 'WEBSITE_METADATA_PASS'
