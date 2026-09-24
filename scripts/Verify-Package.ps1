param(
    [ValidateSet('Release')]
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\KeelMatrix.AiToolContract\KeelMatrix.AiToolContract.csproj'
$solution = Join-Path $repoRoot 'KeelMatrix.AiToolContract.sln'
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

Write-Host "Package gate root: $repoRoot"
Write-Host 'FOUNDER_ICON_GATE: repository-root icon.png is required by the pack configuration at src/KeelMatrix.AiToolContract/KeelMatrix.AiToolContract.csproj:12 and :22.'
$rootIconPath = Join-Path $repoRoot 'icon.png'
$rootIconPresent = Test-Path -LiteralPath $rootIconPath -PathType Leaf
if (-not $rootIconPresent) {
    Fail 'FOUNDER_ICON_GATE: repository-root icon.png is missing; the founder must place the 512x512, <=200 KB icon.'
}
else {
    $rootIconBytes = [System.IO.File]::ReadAllBytes($rootIconPath)
    Require ($rootIconBytes.Length -le 204800) 'FOUNDER_ICON_GATE: repository-root icon.png exceeds 200 KB.'
    Require ($rootIconBytes.Length -ge 24) 'FOUNDER_ICON_GATE: repository-root icon.png is too short to be a PNG.'
    $pngSignature = [byte[]](137, 80, 78, 71, 13, 10, 26, 10)
    Require (($rootIconBytes[0..7] -join ',') -ceq ($pngSignature -join ',')) 'FOUNDER_ICON_GATE: repository-root icon.png is not a PNG.'
    if ($rootIconBytes.Length -ge 24) {
        Require ((Get-PngDimension $rootIconBytes 16) -eq 512) 'FOUNDER_ICON_GATE: repository-root icon.png width is not 512 pixels.'
        Require ((Get-PngDimension $rootIconBytes 20) -eq 512) 'FOUNDER_ICON_GATE: repository-root icon.png height is not 512 pixels.'
    }
}

if (Test-Path -LiteralPath $artifactDirectory) {
    Remove-Item -LiteralPath $artifactDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $artifactDirectory -Force | Out-Null

Run-Step 'dotnet restore .\KeelMatrix.AiToolContract.sln' {
    dotnet restore $solution
}
Run-Step 'dotnet pack .\src\KeelMatrix.AiToolContract\KeelMatrix.AiToolContract.csproj -c Release --no-restore -o .\artifacts\package-gate' {
    dotnet pack $project -c $Configuration --no-restore -o $artifactDirectory
}

$version = '0.1.0-rc.1'
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
            Require ($metadata.description -ceq 'Baseline and compatibility-check the model-visible contracts of Microsoft.Extensions.AI tools.') 'Nuspec description is incorrect.'
            Require ($metadata.tags -ceq 'ai microsoft-extensions-ai aifunction function-calling tool-calling json-schema testing compatibility') 'Nuspec tags are incorrect.'
            Require ($metadata.license.type -ceq 'expression' -and $metadata.license.'#text' -ceq 'MIT') 'Nuspec license metadata is incorrect.'
            Require ($metadata.readme -ceq 'README.md') 'Nuspec readme metadata is incorrect.'
            Require ($metadata.repository.type -ceq 'git' -and $metadata.repository.url -ceq 'https://github.com/KeelMatrix/AiToolContract') 'Nuspec repository metadata is incorrect.'
            $groups = @($metadata.dependencies.group)
            Require ($groups.Count -eq 2) 'Nuspec dependency groups do not cover exactly net8.0 and netstandard2.0.'
            foreach ($group in $groups) {
                Require ($group.targetFramework -in @('net8.0', '.NETStandard2.0')) "Unexpected dependency target framework '$($group.targetFramework)'."
                $dependency = @($group.dependency)
                Require ($dependency.Count -eq 1 -and $dependency[0].id -ceq 'Microsoft.Extensions.AI.Abstractions' -and $dependency[0].version -ceq '[10.0.0, 11.0.0)') "Unexpected dependency declaration for '$($group.targetFramework)'."
            }
            if ($rootIconPresent) {
                Require ($metadata.icon -ceq 'icon.png') 'FOUNDER_ICON_GATE: nuspec icon metadata is not icon.png.'
            }
            else {
                Fail 'FOUNDER_ICON_GATE: package metadata has no icon because repository-root icon.png is absent.'
            }
        }

        Require ($null -ne $packageZip.GetEntry('README.md')) 'Package root README.md is missing.'
        Require ($null -ne $packageZip.GetEntry('LICENSE')) 'Package root LICENSE is missing.'

        foreach ($entry in @($packageZip.Entries + $symbolsZip.Entries)) {
            Require ($entry.FullName -notmatch '(?i)(^|/)(\.env|\.env\.|secrets?\.json|appsettings\.Local\.json|AGENTS\.md|paperclip|codex|internal|task|review|issue|\.user|\.suo)(/|$|\.)') "Sensitive or internal archive entry '$($entry.FullName)' is present."
            Require ($entry.FullName -notmatch '(?i)(OpenAI|Azure\.AI|Anthropic|Google\.GenAI|Ollama|MCP|provider)') "Provider SDK or unrelated integration entry '$($entry.FullName)' is present."
        }

        if ($rootIconPresent) {
            $embeddedIcon = $packageZip.GetEntry('icon.png')
            Require ($null -ne $embeddedIcon) 'FOUNDER_ICON_GATE: package root icon.png is missing.'
            if ($null -ne $embeddedIcon) {
                $embeddedIconBytes = Get-EntryBytes $embeddedIcon
                Require ((Get-Hash $embeddedIconBytes) -ceq (Get-Hash $rootIconBytes)) 'FOUNDER_ICON_GATE: embedded package icon is not byte-identical to repository-root icon.png.'
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
