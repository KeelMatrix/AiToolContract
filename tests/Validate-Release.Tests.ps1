$ErrorActionPreference = 'Stop'

. (Join-Path (Split-Path -Parent $PSScriptRoot) 'build/Invoke-NestedPwsh.ps1')

$scriptPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts/Validate-Release.ps1'
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "ai-tool-contract-release-tests-$([Guid]::NewGuid().ToString('N'))"
$packageDirectory = Join-Path $temporaryRoot 'artifacts/package-gate'
$projectPath = Join-Path $temporaryRoot 'src/KeelMatrix.AiToolContract/KeelMatrix.AiToolContract.csproj'

function Write-Package([string] $Extension, [string] $Id = 'KeelMatrix.AiToolContract', [string] $Version = '0.1.0') {
    $path = Join-Path $packageDirectory "KeelMatrix.AiToolContract.0.1.0.$Extension"
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
    $archive = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $entry = $archive.CreateEntry('KeelMatrix.AiToolContract.nuspec')
        $writer = [IO.StreamWriter]::new($entry.Open())
        try { $writer.Write("<package><metadata><id>$Id</id><version>$Version</version></metadata></package>") }
        finally { $writer.Dispose() }
    }
    finally { $archive.Dispose() }
}

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
New-Item -ItemType Directory -Path (Split-Path -Parent $projectPath) -Force | Out-Null
Set-Content -LiteralPath $projectPath -Value '<Project><PropertyGroup><PackageId>KeelMatrix.AiToolContract</PackageId><Version>0.1.0</Version></PropertyGroup></Project>'
Write-Package 'nupkg'
Write-Package 'snupkg'

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
    $finalizedChangelog = Get-Content -LiteralPath (Join-Path $temporaryRoot 'CHANGELOG.md') -Raw

    foreach ($extension in @('nupkg', 'snupkg')) {
        foreach ($identity in @(
                @{ Id = 'KeelMatrix.AiToolContract'; Version = '0.1.1' },
                @{ Id = 'KeelMatrix.AiToolContract'; Version = '0.1.0-rc.1' },
                @{ Id = 'Other.Package'; Version = '0.1.0' })) {
            Write-Package $extension @identity
            Assert-Condition (Invoke-Validator @{ Version = '0.1.0'; Tag = 'v0.1.0'; RepositoryRoot = $temporaryRoot; PackageDirectory = $packageDirectory; RequireFinalizedChangelog = $true }) "A mismatched embedded $extension identity was accepted."
        }
        Set-Content -LiteralPath (Join-Path $packageDirectory "KeelMatrix.AiToolContract.0.1.0.$extension") -Value 'not a package'
        Assert-Condition (Invoke-Validator @{ Version = '0.1.0'; Tag = 'v0.1.0'; RepositoryRoot = $temporaryRoot; PackageDirectory = $packageDirectory }) "An invalid $extension archive was accepted."
        Write-Package $extension

        foreach ($invalidMetadata in @(
                '<id>Other.Package</id><id>KeelMatrix.AiToolContract</id><version>0.1.0</version>',
                '<id>KeelMatrix.AiToolContract</id><version>0.1.1</version><version>0.1.0</version>',
                '<id>KeelMatrix.AiToolContract</id>',
                '<version>0.1.0</version>')) {
            $archive = [IO.Compression.ZipFile]::Open((Join-Path $packageDirectory "KeelMatrix.AiToolContract.0.1.0.$extension"), [IO.Compression.ZipArchiveMode]::Update)
            try {
                $archive.GetEntry('KeelMatrix.AiToolContract.nuspec').Delete()
                $writer = [IO.StreamWriter]::new($archive.CreateEntry('KeelMatrix.AiToolContract.nuspec').Open())
                try { $writer.Write("<package><metadata>$invalidMetadata</metadata></package>") }
                finally { $writer.Dispose() }
            }
            finally { $archive.Dispose() }
            Assert-Condition (Invoke-Validator @{ Version = '0.1.0'; Tag = 'v0.1.0'; RepositoryRoot = $temporaryRoot; PackageDirectory = $packageDirectory }) "Ambiguous or missing $extension identity metadata was accepted."
        }
        Write-Package $extension
    }

    foreach ($configuredVersion in @('0.1.1', '0.1.0-rc.1')) {
        Set-Content -LiteralPath $projectPath -Value "<Project><PropertyGroup><PackageId>KeelMatrix.AiToolContract</PackageId><Version>$configuredVersion</Version></PropertyGroup></Project>"
        Assert-Condition (Invoke-Validator @{ Version = '0.1.0'; Tag = 'v0.1.0'; RepositoryRoot = $temporaryRoot; PackageDirectory = $packageDirectory; RequireFinalizedChangelog = $true }) 'A mismatched project version was accepted.'
    }
    Set-Content -LiteralPath $projectPath -Value '<Project><PropertyGroup><PackageId>KeelMatrix.AiToolContract</PackageId><Version>0.1.0</Version></PropertyGroup></Project>'

    foreach ($invalidChangelog in @(
            "# Changelog`n`n## [Unreleased]`n`n### 0.1.0`n`n- Provides tool contracts.",
            $finalizedChangelog.Replace('[0.1.0]', '[0.1.1]'),
            $finalizedChangelog.Replace('[0.1.0] - 2026-09-26', '[0.1.0] - Planned'),
            $finalizedChangelog.Replace('2026-09-26', '2026-02-30'),
            $finalizedChangelog.Replace('2026-09-26', '2999-01-01'),
            $finalizedChangelog.Replace('### Added', '### Fixed'))) {
        Set-Content -LiteralPath (Join-Path $temporaryRoot 'CHANGELOG.md') -Value $invalidChangelog
        Assert-Condition (Invoke-Validator @{ Version = '0.1.0'; Tag = 'v0.1.0'; RepositoryRoot = $temporaryRoot; PackageDirectory = $packageDirectory; RequireFinalizedChangelog = $true }) 'An unfinalized or mismatched changelog was accepted.'
    }
    Set-Content -LiteralPath (Join-Path $temporaryRoot 'CHANGELOG.md') -Value $finalizedChangelog

    $markers = @(
        'Planned',
        'Unreleased',
        'not yet published',
        'TBD',
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
    foreach ($marker in $markers) {
        Set-Content -LiteralPath (Join-Path $temporaryRoot 'CHANGELOG.md') -Value @"
# Changelog

## [0.1.0] - 2026-09-26

### Added

- $marker the initial package contract.
"@
        Assert-Condition (Invoke-Validator @{ Version = '0.1.0'; Tag = 'v0.1.0'; RepositoryRoot = $temporaryRoot; PackageDirectory = $packageDirectory; RequireFinalizedChangelog = $true }) "Remediation marker '$marker' was accepted."
    }

    Set-Content -LiteralPath (Join-Path $temporaryRoot 'CHANGELOG.md') -Value @'
# Changelog

## [0.1.0] - 2026-09-26

### Added

- Planned initial release.
'@
    Assert-Condition (Invoke-Validator @{ Version = '0.1.0'; Tag = 'v0.1.0'; RepositoryRoot = $temporaryRoot; PackageDirectory = $packageDirectory; RequireFinalizedChangelog = $true }) 'Pre-release changelog wording was accepted.'

    Set-Content -LiteralPath (Join-Path $temporaryRoot 'CHANGELOG.md') -Value @'
# Changelog

## [0.1.0] - 2026-09-26

### Added

- Provides deterministic tool contract verification.
'@
    Assert-Condition (-not (Invoke-Validator @{ Version = '0.1.0'; Tag = 'v0.1.0'; RepositoryRoot = $temporaryRoot; PackageDirectory = $packageDirectory; RequireFinalizedChangelog = $true })) 'A valid consumer-facing first-release entry was rejected.'

    $packageGatePath = Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts/Verify-Package.ps1'
    $packageGate = Get-Content -LiteralPath $packageGatePath -Raw
    $projectPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'src/KeelMatrix.AiToolContract/KeelMatrix.AiToolContract.csproj'
    $project = Get-Content -LiteralPath $projectPath -Raw
    Assert-Condition ($project -match '<GenerateDocumentationFile>true</GenerateDocumentationFile>') 'The shipping project does not enable XML documentation generation.'
    Assert-Condition ($packageGate -match 'lib/net8\.0/KeelMatrix\.AiToolContract\.xml' -and $packageGate -match 'lib/netstandard2\.0/KeelMatrix\.AiToolContract\.xml') 'The package gate does not require XML documentation for both target frameworks.'
    Assert-Condition ($packageGate -match 'MISSING_PACKAGE_ICON') 'The package gate does not fail closed for a missing icon.'
    Assert-Condition ($packageGate -match 'Get-PngDimension' -and $packageGate -match '512') 'The package gate does not validate icon dimensions.'
    Assert-Condition ($packageGate -match 'nuspec icon metadata' -and $packageGate -match 'byte-identical') 'The package gate does not validate icon metadata and byte identity.'

    $releaseWorkflowPath = Join-Path (Split-Path -Parent $PSScriptRoot) '.github/workflows/release.yml'
    $releaseWorkflow = Get-Content -LiteralPath $releaseWorkflowPath -Raw
    $validationStep = [regex]::Match($releaseWorkflow, '(?ms)^      - name: Validate release contract\r?\n(?<body>.*?)(?=^      - name:|\z)')
    Assert-Condition $validationStep.Success 'The release workflow has no release validation step.'
    $validationBody = $validationStep.Groups['body'].Value
    Assert-Condition ($validationBody -match 'Validate-Release\.ps1.*-RequireFinalizedChangelog' -and $validationBody -notmatch '(?m)^\s*(if|continue-on-error):') 'The finalized release contract can be skipped or ignored.'
    Assert-Condition ($validationStep.Index -lt $releaseWorkflow.IndexOf('      - name: Authenticate to NuGet.org') -and $validationStep.Index -lt $releaseWorkflow.IndexOf('      - name: Push package to NuGet.org')) 'Release validation must precede authentication and publication.'

    $validationCommand = [regex]::Match($validationBody, '(?m)^\s*run:\s*(?<command>[^\r\n]+)').Groups['command'].Value
    New-Item -ItemType Directory -Path (Join-Path $temporaryRoot 'scripts') -Force | Out-Null
    Copy-Item -LiteralPath $scriptPath -Destination (Join-Path $temporaryRoot 'scripts/Validate-Release.ps1')
    $workflowProbe = Join-Path $temporaryRoot 'release-step.ps1'
    Set-Content -LiteralPath $workflowProbe -Value @'
$ErrorActionPreference = 'Stop'
$env:RELEASE_VERSION = '0.1.0'
$env:RELEASE_TAG = 'v0.1.0'
Set-Location -LiteralPath $PSScriptRoot
'@
    Add-Content -LiteralPath $workflowProbe -Value $validationCommand
    Add-Content -LiteralPath $workflowProbe -Value "Write-Output 'PUBLICATION_REACHED'"
    foreach ($case in @('valid', 'planned', 'package-mismatch')) {
        Set-Content -LiteralPath (Join-Path $temporaryRoot 'CHANGELOG.md') -Value $(if ($case -eq 'planned') { $finalizedChangelog.Replace('Provides', 'Planned') } else { $finalizedChangelog })
        Write-Package 'nupkg' -Version $(if ($case -eq 'package-mismatch') { '0.1.1' } else { '0.1.0' })
        $probeOutput = @(Invoke-NestedPwsh -ArgumentList @('-NoProfile', '-NonInteractive', '-File', $workflowProbe) 2>&1)
        $probeExitCode = $LASTEXITCODE
        if ($case -eq 'valid') {
            Assert-Condition ($probeExitCode -eq 0 -and $probeOutput -contains 'PUBLICATION_REACHED') 'The workflow validation step rejected consistent finalized input.'
        }
        else {
            Assert-Condition ($probeExitCode -ne 0 -and $probeOutput -notcontains 'PUBLICATION_REACHED') "The workflow validation step did not stop publication for '$case'."
        }
    }

    $ciWorkflowPath = Join-Path (Split-Path -Parent $PSScriptRoot) '.github/workflows/ci.yml'
    $ciWorkflow = Get-Content -LiteralPath $ciWorkflowPath -Raw
    foreach ($jobName in @('build-test', 'quality', 'package', 'consumer-smoke')) {
        $jobMatch = [regex]::Match($ciWorkflow, "(?ms)^  $([regex]::Escape($jobName)):\r?\n(?<body>.*?)(?=^  \S|\z)")
        Assert-Condition $jobMatch.Success "CI job '$jobName' is missing from the workflow."
        $timeoutMatch = [regex]::Match($jobMatch.Groups['body'].Value, '(?m)^    timeout-minutes:\s*(?<minutes>\d+)\s*$')
        Assert-Condition $timeoutMatch.Success "CI job '$jobName' has no job-level timeout-minutes bound."
        Assert-Condition ([int]$timeoutMatch.Groups['minutes'].Value -gt 0) "CI job '$jobName' has a non-positive timeout-minutes bound."
    }
    Assert-Condition ($ciWorkflow -notmatch 'Verify-Package\.ps1\s+-AllowMissingIcon') 'CI allows a missing package icon.'
    Assert-Condition ($releaseWorkflow -notmatch 'Verify-Package\.ps1\s+-AllowMissingIcon') 'Release allows a missing package icon.'
    $unsafeRefLines = @($releaseWorkflow -split "`r?`n" | Where-Object {
            $_ -match '\$\{\{\s*github\.ref_name\s*\}\}' -and $_ -notmatch '^\s*RELEASE_TAG:\s*'
        })
    $maliciousRef = "v1.2.3'; Write-Output('PWNED') #"
    Assert-Condition ($unsafeRefLines.Count -eq 0) "The release workflow embeds an untrusted ref directly outside its environment boundary: $($unsafeRefLines -join ' | ')"
    Assert-Condition ($releaseWorkflow -match '(?m)^\s*RELEASE_TAG:\s*\$\{\{\s*github\.ref_name\s*\}\}\s*$') 'The release workflow does not pass the tag through an environment boundary.'
    Assert-Condition ($releaseWorkflow -match '(?m)^\s*\$tag\s*=\s*\$env:RELEASE_TAG\s*$') "The release workflow could execute a legal malicious ref such as '$maliciousRef'."
    $publishLines = @($releaseWorkflow -split "`r?`n" | Where-Object { $_ -match 'dotnet nuget push' })
    Assert-Condition ($publishLines.Count -eq 2) "The release workflow must push exactly the nupkg and snupkg artifacts, found $($publishLines.Count) push command(s)."
    Assert-Condition ($releaseWorkflow -notmatch '(?i)--skip-duplicate') 'The release workflow suppresses duplicate package failures.'
    Assert-Condition ($publishLines -match '\$nupkg' -and $publishLines -match '\$snupkg') 'The release workflow does not fail closed on both package artifact pushes.'
    $nupkgPublishLines = @($publishLines | Where-Object { $_ -match '\$nupkg' })
    $snupkgPublishLines = @($publishLines | Where-Object { $_ -match '\$snupkg' })
    Assert-Condition ($nupkgPublishLines.Count -eq 1 -and $nupkgPublishLines[0] -match '(?i)--no-symbols') 'The primary package push does not suppress implicit symbol submission.'
    Assert-Condition ($snupkgPublishLines.Count -eq 1 -and $snupkgPublishLines[0] -notmatch '\$nupkg') 'The explicit symbol push is not isolated to the symbol artifact.'
    $previousReleaseTag = $env:RELEASE_TAG
    try {
        $env:RELEASE_TAG = $maliciousRef
        $probeOutput = @(Invoke-NestedPwsh -ArgumentList @('-NoProfile', '-NonInteractive', '-Command', "`$tag = `$env:RELEASE_TAG; if (`$tag -notmatch '^v(?<version>\d+\.\d+\.\d+)$') { 'REJECTED' } else { 'ACCEPTED' }") 2>&1)
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
