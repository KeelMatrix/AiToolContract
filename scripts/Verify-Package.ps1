param(
    [ValidateSet('Release')]
    [string] $Configuration = 'Release',
    [string] $Version = '0.1.0-rc.1',
    [string] $PackageReleaseNotes = 'Initial release candidate.',
    [switch] $AllowMissingIcon
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\KeelMatrix.AiToolContract\KeelMatrix.AiToolContract.csproj'
$solution = Join-Path $repoRoot 'KeelMatrix.AiToolContract.sln'
$packageReadmePath = Join-Path $repoRoot 'src\KeelMatrix.AiToolContract\README.md'
$licensePath = Join-Path $repoRoot 'LICENSE'
$artifactDirectory = Join-Path $repoRoot 'artifacts\package-gate'
$failures = [System.Collections.Generic.List[string]]::new()
$totalTimer = [System.Diagnostics.Stopwatch]::StartNew()

function Fail([string] $message) {
    [void] $failures.Add($message)
    Write-Host "FAIL: $message" -ForegroundColor Red
}

function Run-Step([string] $name, [scriptblock] $command) {
    $timer = [System.Diagnostics.Stopwatch]::StartNew()
    Write-Host "RUN: $name"
    & $command
    if ($LASTEXITCODE -ne 0) {
        Fail("$name exited with code $LASTEXITCODE.")
    }
    $timer.Stop()
    Write-Host ("DONE: {0} ({1:N0} ms)" -f $name, $timer.Elapsed.TotalMilliseconds)
}

function Require([bool] $condition, [string] $message) {
    if (-not $condition) {
        Fail($message)
    }
}

function Read-EntryText($entry) {
    $reader = [System.IO.StreamReader]::new($entry.Open())
    try {
        return $reader.ReadToEnd()
    }
    finally {
        $reader.Dispose()
    }
}

function Get-EntryBytes($entry) {
    $memory = [System.IO.MemoryStream]::new()
    try {
        $stream = $entry.Open()
        try {
            $stream.CopyTo($memory)
        }
        finally {
            $stream.Dispose()
        }
        return $memory.ToArray()
    }
    finally {
        $memory.Dispose()
    }
}

function Require-EntryFileContent($entry, [string] $sourcePath, [string] $label) {
    $sourceBytes = [System.IO.File]::ReadAllBytes($sourcePath)
    $entryBytes = Get-EntryBytes $entry
    $sameLength = $sourceBytes.Length -eq $entryBytes.Length
    $sameContent = $sameLength
    if ($sameContent) {
        for ($index = 0; $index -lt $sourceBytes.Length; $index++) {
            if ($sourceBytes[$index] -ne $entryBytes[$index]) {
                $sameContent = $false
                break
            }
        }
    }
    Require $sameContent "$label content does not match '$sourcePath'."
}

function Get-PngDimension([byte[]] $bytes, [int] $offset) {
    return ([int]$bytes[$offset] -shl 24) -bor ([int]$bytes[$offset + 1] -shl 16) -bor ([int]$bytes[$offset + 2] -shl 8) -bor [int]$bytes[$offset + 3]
}

function Get-Hash([byte[]] $bytes) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([System.BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

function Contains-Bytes([byte[]] $bytes, [byte[]] $needle) {
    for ($offset = 0; $offset -le $bytes.Length - $needle.Length; $offset++) {
        $match = $true
        for ($index = 0; $index -lt $needle.Length; $index++) {
            if ($bytes[$offset + $index] -ne $needle[$index]) {
                $match = $false
                break
            }
        }
        if ($match) { return $true }
    }
    return $false
}

function Test-SourceLink($entry, [string] $entryName) {
    $pdbBytes = Get-EntryBytes $entry
    $commit = (& git -C $repoRoot rev-parse HEAD).Trim()
    $repoMap = [System.Text.Encoding]::UTF8.GetBytes('raw.githubusercontent.com/KeelMatrix/AiToolContract/')
    $commitBytes = [System.Text.Encoding]::UTF8.GetBytes($commit)
    Require (Contains-Bytes $pdbBytes $repoMap) "SourceLink repository mapping was not found in '$entryName'."
    Require (Contains-Bytes $pdbBytes $commitBytes) "SourceLink commit mapping was not found in '$entryName'."
}

function Assert-ExactEntries([string[]] $actual, [string[]] $expected, [string] $label) {
    $actualSorted = @($actual | Sort-Object)
    $expectedSorted = @($expected | Sort-Object)
    $message = "${label} entries are not the explicit expected set. Actual: $($actualSorted -join ', '); Expected: $($expectedSorted -join ', ')"
    Require (($actualSorted -join "`n") -ceq ($expectedSorted -join "`n")) $message
}

$projectPaths = @(Get-ChildItem -LiteralPath $repoRoot -Recurse -Filter '*.csproj' -File | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | Sort-Object FullName)
$projectEvaluations = [System.Collections.Generic.List[object]]::new()
foreach ($projectPath in $projectPaths) {
    $evaluationOutput = & dotnet msbuild $projectPath.FullName -getProperty:IsPackable -getProperty:IsShippingProject 2>&1
    if ($LASTEXITCODE -ne 0) {
        Fail "MSBuild packability evaluation failed for '$($projectPath.FullName)'."
        continue
    }

    try {
        $evaluation = (($evaluationOutput | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine) | ConvertFrom-Json
        $isPackable = [string]$evaluation.Properties.IsPackable
        $isShippingProject = [string]$evaluation.Properties.IsShippingProject
        Write-Host ("PROJECT_PACKABILITY: {0}; IsPackable={1}; IsShippingProject={2}" -f $projectPath.FullName.Substring($repoRoot.Length + 1), $isPackable, $isShippingProject)
        $projectEvaluations.Add([pscustomobject]@{
                Path = $projectPath.FullName
                IsPackable = $isPackable
                IsShippingProject = $isShippingProject
            })
    }
    catch {
        Fail "MSBuild packability evaluation for '$($projectPath.FullName)' was not valid JSON: $($_.Exception.Message)"
    }
}

$packableProjects = @($projectEvaluations | Where-Object { $_.IsPackable -ceq 'true' })
$packableShippingProjects = @($packableProjects | Where-Object { $_.IsShippingProject -ceq 'true' })
Require ($projectEvaluations.Count -eq $projectPaths.Count) 'Packability evaluation did not produce one result for every project.'
Require ($packableProjects.Count -eq 1) "Expected exactly one packable project, found $($packableProjects.Count)."
Require ($packableShippingProjects.Count -eq 1 -and $packableShippingProjects[0].Path -eq $project) 'Expected exactly one packable shipping project, and it must be the shipping library.'
Write-Host ("PACKABLE_PROJECT_COUNT: {0}; PACKABLE_SHIPPING_PROJECT_COUNT: {1}" -f $packableProjects.Count, $packableShippingProjects.Count)

Write-Host "Package gate root: $repoRoot"
Write-Host 'PACKAGE_ICON_CHECK: repository-root icon.png is required by the pack configuration at src/KeelMatrix.AiToolContract/KeelMatrix.AiToolContract.csproj:12 and :26.'
$rootIconPath = Join-Path $repoRoot 'icon.png'
$rootIconPresent = Test-Path -LiteralPath $rootIconPath -PathType Leaf
if (-not $rootIconPresent) {
    if ($AllowMissingIcon) {
        Write-Host 'PACKAGE_ICON_CHECK: repository-root icon.png is absent; explicit allow-missing-icon mode continues with archive checks.' -ForegroundColor Yellow
    }
    else {
        Fail 'MISSING_PACKAGE_ICON: repository-root icon.png is missing; provide a 512x512, <=200 KB package icon.'
    }
}
else {
    $rootIconBytes = [System.IO.File]::ReadAllBytes($rootIconPath)
    Require ($rootIconBytes.Length -le 204800) 'PACKAGE_ICON_CHECK: repository-root icon.png exceeds 200 KB.'
    Require ($rootIconBytes.Length -ge 24) 'PACKAGE_ICON_CHECK: repository-root icon.png is too short to be a PNG.'
    $pngSignature = [byte[]](137, 80, 78, 71, 13, 10, 26, 10)
    Require (($rootIconBytes[0..7] -join ',') -ceq ($pngSignature -join ',')) 'PACKAGE_ICON_CHECK: repository-root icon.png is not a PNG.'
    if ($rootIconBytes.Length -ge 24) {
        Require ((Get-PngDimension $rootIconBytes 16) -eq 512) 'PACKAGE_ICON_CHECK: repository-root icon.png width is not 512 pixels.'
        Require ((Get-PngDimension $rootIconBytes 20) -eq 512) 'PACKAGE_ICON_CHECK: repository-root icon.png height is not 512 pixels.'
    }
}

if (Test-Path -LiteralPath $artifactDirectory) {
    Remove-Item -LiteralPath $artifactDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $artifactDirectory -Force | Out-Null

Run-Step 'dotnet restore .\KeelMatrix.AiToolContract.sln --configfile .\NuGet.config' {
    dotnet restore $solution --configfile (Join-Path $repoRoot 'NuGet.config') --no-cache --force
}
Run-Step 'dotnet pack .\src\KeelMatrix.AiToolContract\KeelMatrix.AiToolContract.csproj -c Release --no-restore -o .\artifacts\package-gate' {
    dotnet pack $project -c $Configuration --no-restore -p:Version=$Version -p:PackageReleaseNotes=$PackageReleaseNotes -o $artifactDirectory
}

$version = $Version
$packageId = 'KeelMatrix.AiToolContract'
$nupkgName = "$packageId.$version.nupkg"
$snupkgName = "$packageId.$version.snupkg"
$nupkgPath = Join-Path $artifactDirectory $nupkgName
$snupkgPath = Join-Path $artifactDirectory $snupkgName
Require (Test-Path -LiteralPath $nupkgPath -PathType Leaf) "Expected package '$nupkgName' was not produced."
Require (Test-Path -LiteralPath $snupkgPath -PathType Leaf) "Expected symbol package '$snupkgName' was not produced."

if ((Test-Path -LiteralPath $nupkgPath -PathType Leaf) -and (Test-Path -LiteralPath $snupkgPath -PathType Leaf)) {
    $artifactNames = @(Get-ChildItem -LiteralPath $artifactDirectory -File | Select-Object -ExpandProperty Name)
    Assert-ExactEntries $artifactNames @($nupkgName, $snupkgName) 'Artifact set'

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $packageZip = [System.IO.Compression.ZipFile]::OpenRead($nupkgPath)
    $symbolsZip = [System.IO.Compression.ZipFile]::OpenRead($snupkgPath)
    try {
        $packageEntries = @($packageZip.Entries | Select-Object -ExpandProperty FullName)
        $symbolEntries = @($symbolsZip.Entries | Select-Object -ExpandProperty FullName)
        $expectedPackageEntries = @(
            '_rels/.rels',
            "$packageId.nuspec",
            'LICENSE',
            'README.md',
            'lib/net8.0/KeelMatrix.AiToolContract.dll',
            'lib/netstandard2.0/KeelMatrix.AiToolContract.dll',
            '[Content_Types].xml',
            'package/services/metadata/core-properties/nuget.psmdcp'
        )
        if ($rootIconPresent) {
            $expectedPackageEntries += 'icon.png'
        }
        $expectedSymbolEntries = @(
            '_rels/.rels',
            "$packageId.nuspec",
            'lib/net8.0/KeelMatrix.AiToolContract.pdb',
            'lib/netstandard2.0/KeelMatrix.AiToolContract.pdb',
            '[Content_Types].xml',
            'package/services/metadata/core-properties/nuget.psmdcp'
        )
        Assert-ExactEntries $packageEntries $expectedPackageEntries 'Package archive'
        Assert-ExactEntries $symbolEntries $expectedSymbolEntries 'Symbol archive'

        $nuspecEntry = $packageZip.GetEntry("$packageId.nuspec")
        Require ($null -ne $nuspecEntry) 'Package archive is missing its nuspec.'
        if ($null -ne $nuspecEntry) {
            [xml]$nuspec = Read-EntryText $nuspecEntry
            $metadata = $nuspec.package.metadata
            Require ($metadata.id -ceq $packageId) 'Nuspec id is incorrect.'
            Require ($metadata.version -ceq $version) 'Nuspec version is incorrect.'
            Require ($metadata.authors -ceq 'KeelMatrix') 'Nuspec authors metadata is incorrect.'
            Require ($metadata.description -ceq 'Baseline and compatibility-check the model-visible contracts of Microsoft.Extensions.AI tools.') 'Nuspec description is incorrect.'
            Require ($metadata.tags -ceq 'ai microsoft-extensions-ai aifunction function-calling tool-calling json-schema testing compatibility') 'Nuspec tags are incorrect.'
            Require ($metadata.license.type -ceq 'expression' -and $metadata.license.'#text' -ceq 'MIT') 'Nuspec license metadata is incorrect.'
            Require ($metadata.readme -ceq 'README.md') 'Nuspec readme metadata is incorrect.'
            Require ($metadata.projectUrl -ceq 'https://github.com/KeelMatrix/AiToolContract') 'Nuspec project URL metadata is incorrect.'
            Require ($metadata.releaseNotes -ceq $PackageReleaseNotes) 'Nuspec release notes metadata is incorrect.'
            Require ($metadata.repository.type -ceq 'git' -and $metadata.repository.url -ceq 'https://github.com/KeelMatrix/AiToolContract') 'Nuspec repository metadata is incorrect.'
            Require ($metadata.repository.branch -ceq 'refs/heads/main') 'Nuspec repository branch metadata is incorrect.'
            Require ($metadata.repository.commit -ceq (& git -C $repoRoot rev-parse HEAD).Trim()) 'Nuspec repository commit metadata is incorrect.'
            $groups = @($metadata.dependencies.group)
            Require ($groups.Count -eq 2) 'Nuspec dependency groups do not cover exactly net8.0 and netstandard2.0.'
            foreach ($group in $groups) {
                Require ($group.targetFramework -in @('net8.0', '.NETStandard2.0')) "Unexpected dependency target framework '$($group.targetFramework)'."
                $dependency = @($group.dependency)
                Require ($dependency.Count -eq 1 -and $dependency[0].id -ceq 'Microsoft.Extensions.AI.Abstractions' -and $dependency[0].version -ceq '[10.0.0, 11.0.0)') "Unexpected dependency declaration for '$($group.targetFramework)'."
            }
            if ($rootIconPresent) {
                Require ($metadata.icon -ceq 'icon.png') 'PACKAGE_ICON_CHECK: nuspec icon metadata is not icon.png.'
            }
            elseif (-not $AllowMissingIcon) {
                Fail 'MISSING_PACKAGE_ICON: package metadata has no icon because repository-root icon.png is absent.'
            }
        }

        $readmeEntry = $packageZip.GetEntry('README.md')
        $licenseEntry = $packageZip.GetEntry('LICENSE')
        Require ($null -ne $readmeEntry) 'Package root README.md is missing.'
        Require ($null -ne $licenseEntry) 'Package root LICENSE is missing.'
        if ($null -ne $readmeEntry) {
            Require-EntryFileContent $readmeEntry $packageReadmePath 'Package README'
            $readmeText = Read-EntryText $readmeEntry
            Require ($readmeText.Contains('# KeelMatrix.AiToolContract')) 'Packed README is missing the package heading.'
            Require ($readmeText.Contains('dotnet add package KeelMatrix.AiToolContract')) 'Packed README is missing the install command.'
            Require ($readmeText.Contains('## Quick Start')) 'Packed README is missing the quick-start marker.'
        }
        if ($null -ne $licenseEntry) {
            Require-EntryFileContent $licenseEntry $licensePath 'Package LICENSE'
            $licenseText = Read-EntryText $licenseEntry
            Require ($licenseText.Contains('MIT License')) 'Packed LICENSE is missing the MIT marker.'
            Require ($licenseText.Contains('Copyright (c) 2026 KeelMatrix')) 'Packed LICENSE is missing the copyright marker.'
        }

        foreach ($entry in @($packageZip.Entries + $symbolsZip.Entries)) {
            Require ($entry.FullName -notmatch '(?i)(^|/)(\.env|\.env\.|secrets?\.json|appsettings\.Local\.json|\x41\x47\x45\x4e\x54\x53\.md|private\.config|\.user|\.suo)(/|$|\.)') "Restricted archive entry '$($entry.FullName)' is present."
            Require ($entry.FullName -notmatch '(?i)(OpenAI|Azure\.AI|Anthropic|Google\.GenAI|Ollama|MCP|provider)') "Provider SDK or unrelated integration entry '$($entry.FullName)' is present."
        }

        if ($rootIconPresent) {
            $embeddedIcon = $packageZip.GetEntry('icon.png')
            Require ($null -ne $embeddedIcon) 'PACKAGE_ICON_CHECK: package root icon.png is missing.'
            if ($null -ne $embeddedIcon) {
                $embeddedIconBytes = Get-EntryBytes $embeddedIcon
                Require ((Get-Hash $embeddedIconBytes) -ceq (Get-Hash $rootIconBytes)) 'PACKAGE_ICON_CHECK: embedded package icon is not byte-identical to repository-root icon.png.'
            }
        }

        foreach ($entryName in @('lib/net8.0/KeelMatrix.AiToolContract.pdb', 'lib/netstandard2.0/KeelMatrix.AiToolContract.pdb')) {
            $symbolEntry = $symbolsZip.GetEntry($entryName)
            Require ($null -ne $symbolEntry) "Symbol archive is missing '$entryName'."
            if ($null -ne $symbolEntry) {
                Test-SourceLink $symbolEntry $entryName
            }
        }
    }
    finally {
        $packageZip.Dispose()
        $symbolsZip.Dispose()
    }
}

$totalTimer.Stop()
Write-Host ("Package gate elapsed: {0:N0} ms" -f $totalTimer.Elapsed.TotalMilliseconds)
if ($failures.Count -gt 0) {
    Write-Host "Package gate failed with $($failures.Count) finding(s)." -ForegroundColor Red
    exit 1
}
Write-Host 'PACKAGE_GATE_PASS' -ForegroundColor Green
