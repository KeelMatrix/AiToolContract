$ErrorActionPreference = 'Stop'

$scriptPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts/Validate-Release.ps1'
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "ai-tool-contract-release-tests-$([Guid]::NewGuid().ToString('N'))"
$packageDirectory = Join-Path $temporaryRoot 'packages'

function Assert-Condition([bool] $Condition, [string] $Message) {
    if (-not $Condition) {
        throw "Release validator test failed: $Message"
    }
}

function Invoke-Validator([hashtable] $Parameters) {
    $failed = $false
    try {
        & $scriptPath @Parameters | Out-Host
    }
    catch {
        $failed = $true
    }
    return $failed
}

New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
New-Item -ItemType File -Path (Join-Path $packageDirectory 'KeelMatrix.AiToolContract.0.1.0.nupkg') | Out-Null
New-Item -ItemType File -Path (Join-Path $packageDirectory 'KeelMatrix.AiToolContract.0.1.0.snupkg') | Out-Null

try {
    Set-Content -LiteralPath (Join-Path $temporaryRoot 'CHANGELOG.md') -Value @'
# Changelog

## [Unreleased]

No unreleased changes.

## [0.1.0] - 2026-09-26

### Added

- Provides deterministic tool contract verification.
'@

    Assert-Condition (Invoke-Validator @{ Version = '0.1.0'; Tag = 'v0.1.1'; RepositoryRoot = $temporaryRoot; PackageDirectory = $packageDirectory }) 'A mismatched tag was accepted.'
    Assert-Condition (-not (Invoke-Validator @{ Version = '0.1.0'; Tag = 'v0.1.0'; RepositoryRoot = $temporaryRoot; PackageDirectory = $packageDirectory; RequireFinalizedChangelog = $true })) 'A finalized changelog was rejected.'

    Set-Content -LiteralPath (Join-Path $temporaryRoot 'CHANGELOG.md') -Value @'
# Changelog

## [0.1.0] - 2026-09-26

### Added

- Planned initial release.
'@
    Assert-Condition (Invoke-Validator @{ Version = '0.1.0'; Tag = 'v0.1.0'; RepositoryRoot = $temporaryRoot; PackageDirectory = $packageDirectory; RequireFinalizedChangelog = $true }) 'Pre-release changelog wording was accepted.'

    $releaseWorkflowPath = Join-Path (Split-Path -Parent $PSScriptRoot) '.github/workflows/release.yml'
    $releaseWorkflow = Get-Content -LiteralPath $releaseWorkflowPath -Raw
    $unsafeRefLines = @($releaseWorkflow -split "`r?`n" | Where-Object {
            $_ -match '\$\{\{\s*github\.ref_name\s*\}\}' -and $_ -notmatch '^\s*RELEASE_TAG:\s*'
        })
    $maliciousRef = "v1.2.3'; Write-Output('PWNED') #"
    Assert-Condition ($unsafeRefLines.Count -eq 0) "The release workflow embeds an untrusted ref directly outside its environment boundary: $($unsafeRefLines -join ' | ')"
    Assert-Condition ($releaseWorkflow -match '(?m)^\s*RELEASE_TAG:\s*\$\{\{\s*github\.ref_name\s*\}\}\s*$') 'The release workflow does not pass the tag through an environment boundary.'
    Assert-Condition ($releaseWorkflow -match '(?m)^\s*\$tag\s*=\s*\$env:RELEASE_TAG\s*$') "The release workflow could execute a legal malicious ref such as '$maliciousRef'."
    $previousReleaseTag = $env:RELEASE_TAG
    try {
        $env:RELEASE_TAG = $maliciousRef
        $probeOutput = @(& pwsh -NoProfile -NonInteractive -Command "`$tag = `$env:RELEASE_TAG; if (`$tag -notmatch '^v(?<version>\d+\.\d+\.\d+)$') { 'REJECTED' } else { 'ACCEPTED' }" 2>&1)
        $probeExitCode = $LASTEXITCODE
        Assert-Condition ($probeExitCode -eq 0 -and ($probeOutput -join "`n") -ceq 'REJECTED') "A malicious tag value was not rejected as data: '$maliciousRef'."
    }
    finally {
        $env:RELEASE_TAG = $previousReleaseTag
    }

    Write-Output 'RELEASE_VALIDATOR_TEST_PASS'
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
