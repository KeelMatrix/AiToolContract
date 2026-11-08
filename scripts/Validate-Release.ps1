[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Version,

    [string] $Tag,

    [string] $RepositoryRoot = (Split-Path -Parent $PSScriptRoot),

    [string] $PackageDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/package-gate'),

    [switch] $RequireFinalizedChangelog
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-Condition([bool] $Condition, [string] $Message) {
    if (-not $Condition) {
        throw "Release validation failed: $Message"
    }
}

Assert-Condition ($Version -match '^\d+\.\d+\.\d+$') "Version '$Version' is not a stable X.Y.Z version."
if ($Tag) {
    Assert-Condition ($Tag -ceq "v$Version") "Tag '$Tag' does not match version '$Version'."
}

$root = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$packageRoot = (Resolve-Path -LiteralPath $PackageDirectory).Path
$packageId = 'KeelMatrix.AiToolContract'
$projectPath = Join-Path $root 'src/KeelMatrix.AiToolContract/KeelMatrix.AiToolContract.csproj'
[xml] $project = [IO.File]::ReadAllText($projectPath)
$configuredIds = @($project.SelectNodes('/Project/PropertyGroup/PackageId'))
$configuredVersions = @($project.SelectNodes('/Project/PropertyGroup/Version'))
Assert-Condition ($configuredIds.Count -eq 1 -and $configuredIds[0].InnerText -ceq $packageId) 'The configured package ID does not match the release package.'
Assert-Condition ($configuredVersions.Count -eq 1 -and $configuredVersions[0].InnerText -ceq $Version) 'The configured package version does not match the release version.'

if ($RequireFinalizedChangelog) {
    $changelogPath = Join-Path $root 'CHANGELOG.md'
    Assert-Condition (Test-Path -LiteralPath $changelogPath -PathType Leaf) 'CHANGELOG.md is missing.'

    $changelog = [IO.File]::ReadAllText($changelogPath)
    $escapedVersion = [regex]::Escape($Version)
    $releaseHeader = [regex]::Match($changelog, "(?m)^## \[$escapedVersion\] - (?<date>\d{4}-\d{2}-\d{2})\s*$")
    Assert-Condition $releaseHeader.Success "CHANGELOG.md has no finalized '$Version' release entry."

    $releaseDate = [DateTime]::ParseExact(
        $releaseHeader.Groups['date'].Value,
        'yyyy-MM-dd',
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::AssumeUniversal)
    Assert-Condition ($releaseDate.Date -le [DateTime]::UtcNow.Date) 'CHANGELOG.md release date cannot be in the future.'

    $entryStart = $releaseHeader.Index + $releaseHeader.Length
    $remaining = $changelog.Substring($entryStart)
    $nextHeader = [regex]::Match($remaining, '(?m)^## \[')
    $entry = if ($nextHeader.Success) { $remaining.Substring(0, $nextHeader.Index) } else { $remaining }
    $remediationMarkers = @(
        'planned',
        'unreleased',
        'not yet published',
        'tbd',
        'now',
        'no longer',
        'previously',
        'formerly',
        'used to',
        'fixed',
        'fixes',
        'corrected',
        'resolved',
        'addressed',
        'this removes',
        'this fixes',
        'changed from'
    )
    $markerPattern = '(?i)(?<!\w)(?:' + (($remediationMarkers | ForEach-Object { [regex]::Escape($_) }) -join '|') + ')(?!\w)'
    Assert-Condition ($entry -notmatch $markerPattern) "The '$Version' changelog entry contains pre-release remediation wording."

    $categories = @([regex]::Matches($entry, '(?m)^###\s+(.+?)\s*$') | ForEach-Object { $_.Groups[1].Value.Trim() })
    $otherStableEntries = @([regex]::Matches($changelog, '(?m)^## \[\d+\.\d+\.\d+\] - \d{4}-\d{2}-\d{2}\s*$') | Where-Object { $_.Value -notmatch "^## \[$escapedVersion\]" })
    if ($otherStableEntries.Count -eq 0) {
        Assert-Condition ($categories.Count -gt 0 -and $categories -contains 'Added') "The first release entry for '$Version' must contain an Added section."
        Assert-Condition (@($categories | Where-Object { $_ -cne 'Added' }).Count -eq 0) "The first release entry for '$Version' may contain only an Added section."
    }
}

$expectedPackages = @(
    "KeelMatrix.AiToolContract.$Version.nupkg",
    "KeelMatrix.AiToolContract.$Version.snupkg"
)
$actualEntries = @(Get-ChildItem -LiteralPath $packageRoot -Force -Recurse | ForEach-Object {
        [IO.Path]::GetRelativePath($packageRoot, $_.FullName).Replace('\', '/')
    })
$unexpectedEntries = @($actualEntries | Where-Object { $_ -notin $expectedPackages })
Assert-Condition ($unexpectedEntries.Count -eq 0) "Unexpected files or directories in the package directory: $($unexpectedEntries -join ', ')."

$actualPackages = @(Get-ChildItem -LiteralPath $packageRoot -File | Where-Object { $_.Name -like '*.nupkg' -or $_.Name -like '*.snupkg' } | Select-Object -ExpandProperty Name)
$unexpected = @($actualPackages | Where-Object { $_ -notin $expectedPackages })
$missing = @($expectedPackages | Where-Object { $_ -notin $actualPackages })
Assert-Condition ($unexpected.Count -eq 0) "Unexpected package artifacts: $($unexpected -join ', ')."
Assert-Condition ($missing.Count -eq 0) "Missing package artifacts: $($missing -join ', ')."
Assert-Condition ($actualPackages.Count -eq $expectedPackages.Count) "Expected exactly $($expectedPackages.Count) package artifacts, found $($actualPackages.Count)."

foreach ($name in $expectedPackages) {
    $archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $packageRoot $name))
    try {
        $nuspecEntries = @($archive.Entries | Where-Object { $_.FullName -like '*.nuspec' })
        Assert-Condition ($nuspecEntries.Count -eq 1 -and $nuspecEntries[0].FullName -ceq "$packageId.nuspec") "'$name' must contain exactly the expected nuspec."
        $reader = [IO.StreamReader]::new($nuspecEntries[0].Open())
        try { [xml] $nuspec = $reader.ReadToEnd() }
        finally { $reader.Dispose() }
        $ids = @($nuspec.SelectNodes('/*[local-name()="package"]/*[local-name()="metadata"]/*[local-name()="id"]'))
        $versions = @($nuspec.SelectNodes('/*[local-name()="package"]/*[local-name()="metadata"]/*[local-name()="version"]'))
        Assert-Condition ($ids.Count -eq 1 -and $ids[0].InnerText -ceq $packageId) "'$name' embeds a different package ID."
        Assert-Condition ($versions.Count -eq 1 -and $versions[0].InnerText -ceq $Version) "'$name' embeds a different package version."
    }
    finally { $archive.Dispose() }
}

Write-Output "Release validation passed for KeelMatrix.AiToolContract $Version."
