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
    Assert-Condition ($entry -notmatch '(?i)\b(planned|unreleased|not yet published|tbd)\b') "The '$Version' changelog entry still contains pre-release wording."

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

Write-Output "Release validation passed for KeelMatrix.AiToolContract $Version."
