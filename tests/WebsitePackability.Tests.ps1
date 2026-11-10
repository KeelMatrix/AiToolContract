#requires -Version 7.0
param(
    [string] $ScratchRoot = [IO.Path]::GetTempPath(),
    [string] $Case = '*'
)

$ErrorActionPreference = 'Stop'
$originalExitCode = (Get-Variable LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue).Value
$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $repoRoot 'build/Invoke-NestedPwsh.ps1')
$originalPath = $env:PATH
$temporaryRoot = Join-Path $ScratchRoot ('website-packability-' + [Guid]::NewGuid().ToString('N'))
$manifest = '{"schemaVersion":1,"packages":{"KeelMatrix.AiToolContract":{"visibility":"public-product","role":"primary"}}}'
$releaseEnable = '<IsPackable>false</IsPackable><IsPackable Condition="''$(Configuration)'' == ''Release''">True</IsPackable>'
$releaseDisable = '<IsPackable>TRUE</IsPackable><IsPackable Condition="''$(Configuration)'' == ''Release''">FALSE</IsPackable>'
$cases = @(
    @{ Name = 'inherited-true-lower'; Props = '<IsPackable>true</IsPackable>'; Packable = $true; Kind = 'tests' },
    @{ Name = 'inherited-true-mixed'; Props = '<IsPackable>True</IsPackable>'; Packable = $true; Kind = 'smoke' },
    @{ Name = 'inherited-true-upper'; Props = '<IsPackable>TRUE</IsPackable>'; Packable = $true; Kind = 'samples' },
    @{ Name = 'inherited-false-lower'; Props = '<IsPackable>false</IsPackable>'; Packable = $false },
    @{ Name = 'inherited-false-upper'; Props = '<IsPackable>FALSE</IsPackable>'; Packable = $false; MainBoolean = 'TRUE' },
    @{ Name = 'release-enable-props'; Props = $releaseEnable; Packable = $true },
    @{ Name = 'release-disable-props'; Props = $releaseDisable; Packable = $false },
    @{ Name = 'project-disable'; Props = '<IsPackable>TRUE</IsPackable>'; Project = '<IsPackable>false</IsPackable>'; Packable = $false },
    @{ Name = 'project-enable'; Props = '<IsPackable>FALSE</IsPackable>'; Project = '<IsPackable>True</IsPackable>'; Packable = $true },
    @{ Name = 'targets-enable'; Props = '<IsPackable>true</IsPackable>'; Project = '<IsPackable>false</IsPackable>'; Targets = '<IsPackable>TRUE</IsPackable>'; Packable = $true },
    @{ Name = 'targets-disable'; Props = '<IsPackable>false</IsPackable>'; Project = '<IsPackable>True</IsPackable>'; Targets = '<IsPackable>FALSE</IsPackable>'; Packable = $false },
    @{ Name = 'release-enable-targets'; Props = '<IsPackable>FALSE</IsPackable>'; Targets = $releaseEnable; Packable = $true },
    @{ Name = 'release-disable-targets'; Props = '<IsPackable>True</IsPackable>'; Targets = $releaseDisable; Packable = $false },
    @{ Name = 'global-configuration-precedence'; Project = '<Configuration>Debug</Configuration>' + $releaseEnable; Packable = $true },
    @{ Name = 'sdk-default'; Packable = $true },
    @{ Name = 'classified-sibling'; Props = '<IsPackable>True</IsPackable>'; Packable = $true; Classified = $true },
    @{ Name = 'shipping-release-enable'; Props = '<IsPackable>FALSE</IsPackable>'; Packable = $false; MainPackability = $releaseEnable; MainBoolean = 'True' },
    @{ Name = 'shipping-missing-sentinels'; Props = '<IsPackable>false</IsPackable>'; Packable = $false; MainMetadata = '<PackageTags>ai</PackageTags>' },
    @{ Name = 'shipping-classification-drift'; Props = '<IsPackable>FALSE</IsPackable>'; Packable = $false; MainMetadata = '<PackageTags>ai;keelmatrix-internal-package;keelmatrix-primary</PackageTags>' },
    @{ Name = 'shipping-project-url-drift'; Props = '<IsPackable>false</IsPackable>'; Packable = $false; MainMetadata = '<PackageProjectUrl>https://github.com/KeelMatrix/Other</PackageProjectUrl>' },
    @{ Name = 'shipping-repository-url-drift'; Props = '<IsPackable>false</IsPackable>'; Packable = $false; MainMetadata = '<RepositoryUrl>https://github.com/KeelMatrix/Other</RepositoryUrl>' }
)

try {
    New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
    # Keep the SDK first while exercising discovery with another matching executable.
    $sdkDirectory = Split-Path -Parent (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source
    $alternateDirectory = Join-Path $temporaryRoot 'alternate-dotnet'
    New-Item -ItemType Directory -Path $alternateDirectory | Out-Null
    if ($IsWindows) {
        Set-Content -LiteralPath (Join-Path $alternateDirectory 'dotnet.cmd') '@exit /b 47'
    }
    else {
        $alternateDotnet = Join-Path $alternateDirectory 'dotnet'
        [IO.File]::WriteAllText($alternateDotnet, "#!/bin/sh`nexit 47`n")
        [IO.File]::SetUnixFileMode($alternateDotnet, [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute)
    }
    $env:PATH = $sdkDirectory + [IO.Path]::PathSeparator + $alternateDirectory + [IO.Path]::PathSeparator + $originalPath
    if (@(Get-Command dotnet -CommandType Application).Count -lt 2) { throw 'Expected multiple dotnet executable matches.' }
    $nativeDotnet = (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source
    $selected = @($cases | Where-Object { $_.Name -clike $Case })
    if ($selected.Count -eq 0) { throw "No packability cases match '$Case'." }
    foreach ($test in $selected) {
        $fixture = Join-Path $temporaryRoot $test.Name
        $kind = if ($test.Kind) { $test.Kind } else { 'tests' }
        $siblingDirectory = Join-Path $fixture "$kind/Sibling"
        $mainDirectory = Join-Path $fixture 'src/KeelMatrix.AiToolContract'
        foreach ($directory in @('build', 'scripts', 'src/KeelMatrix.AiToolContract', "$kind/Sibling")) {
            New-Item -ItemType Directory -Path (Join-Path $fixture $directory) -Force | Out-Null
        }
        foreach ($name in @('Invoke-NestedPwsh.ps1', 'Test-NestedPwshLaunch.ps1', 'Test-SymbolMetadata.ps1', 'Test-WebsiteMetadata.ps1')) {
            Copy-Item -LiteralPath (Join-Path $repoRoot "build/$name") -Destination (Join-Path $fixture 'build')
        }
        foreach ($name in @('Verify-Package.ps1', 'Test-WebsiteMetadata.ps1')) {
            Copy-Item -LiteralPath (Join-Path $repoRoot "scripts/$name") -Destination (Join-Path $fixture 'scripts')
        }
        Copy-Item -LiteralPath (Join-Path $repoRoot 'global.json') -Destination $fixture
        Set-Content -LiteralPath (Join-Path $fixture 'NuGet.config') '<configuration><packageSources><clear /></packageSources></configuration>'
        Set-Content -LiteralPath (Join-Path $fixture 'Directory.Build.props') @'
<Project><PropertyGroup>
  <TargetFramework>net8.0</TargetFramework>
  <PackageTags>ai;keelmatrix-public-product;keelmatrix-primary</PackageTags>
  <RepositoryUrl>https://github.com/KeelMatrix/AiToolContract</RepositoryUrl>
  <PackageProjectUrl>https://github.com/KeelMatrix/AiToolContract</PackageProjectUrl>
  <RepositoryType>git</RepositoryType>
  <IncludeBuildOutput>false</IncludeBuildOutput>
  <SuppressDependenciesWhenPacking>true</SuppressDependenciesWhenPacking>
  <NoPackageAnalysis>true</NoPackageAnalysis>
</PropertyGroup></Project>
'@
        $boolean = if ($test.MainBoolean) { $test.MainBoolean } else { 'true' }
        $mainPackability = if ($test.MainPackability) { $test.MainPackability } else { '<IsPackable>true</IsPackable>' }
        Set-Content -LiteralPath (Join-Path $mainDirectory 'KeelMatrix.AiToolContract.csproj') (('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><PackageId>KeelMatrix.AiToolContract</PackageId><IsShippingProject>{0}</IsShippingProject><GenerateDocumentationFile>{0}</GenerateDocumentationFile>{1}</PropertyGroup></Project>' -f $boolean, $mainPackability))
        Set-Content -LiteralPath (Join-Path $mainDirectory 'Directory.Build.targets') ('<Project><PropertyGroup Condition="''$(Configuration)'' == ''Release''">' + $test.MainMetadata + '</PropertyGroup></Project>')
        Set-Content -LiteralPath (Join-Path $siblingDirectory 'Directory.Build.props') ('<Project><Import Project="../../Directory.Build.props"/><PropertyGroup>' + $test.Props + '</PropertyGroup></Project>')
        Set-Content -LiteralPath (Join-Path $siblingDirectory 'Directory.Build.targets') ('<Project><PropertyGroup>' + $test.Targets + '</PropertyGroup></Project>')
        $siblingPath = Join-Path $siblingDirectory 'Sibling.csproj'
        $tags = if ($test.Classified) { 'ai;keelmatrix-public-product;keelmatrix-component' } else { 'ai' }
        Set-Content -LiteralPath $siblingPath ('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><PackageId>KeelMatrix.UnclassifiedSibling</PackageId><PackageTags>' + $tags + '</PackageTags>' + $test.Project + '</PropertyGroup><ItemGroup><None Include="payload.txt" Pack="true" PackagePath=""/></ItemGroup></Project>')
        Set-Content -LiteralPath (Join-Path $siblingDirectory 'payload.txt') 'Packability fixture content.'
        $inputManifest = $manifest
        if ($test.Classified) {
            $inputManifest = $manifest.Replace('}}}', '},"KeelMatrix.UnclassifiedSibling":{"visibility":"public-product","role":"component"}}}')
        }
        Set-Content -LiteralPath (Join-Path $fixture 'keelmatrix.website.json') $inputManifest
        # Only restore is intercepted. Every MSBuild evaluation runs the actual SDK.
        Set-Content -LiteralPath (Join-Path $fixture 'probe.ps1') @'
param([string] $Mode)
$ErrorActionPreference = 'Stop'
$nativeDotnet = (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source
function dotnet {
    if ($args[0] -eq 'restore') { Write-Output 'PACKABILITY_RESTORE_REACHED'; exit 0 }
    & $nativeDotnet @args
    $global:LASTEXITCODE = $LASTEXITCODE
}
if ($Mode -eq 'metadata') { & (Join-Path $PSScriptRoot 'scripts/Test-WebsiteMetadata.ps1') -RepositoryRoot $PSScriptRoot }
else { & (Join-Path $PSScriptRoot 'scripts/Verify-Package.ps1') -AllowMissingIcon }
'@
        Push-Location $fixture
        try {
            $evaluation = & $nativeDotnet msbuild $siblingPath -nologo -p:Configuration=Release '-getProperty:IsPackable,Configuration' 2>&1
            if ($LASTEXITCODE -ne 0) { throw "Real SDK evaluation failed: $evaluation" }
            $properties = ($evaluation -join "`n" | ConvertFrom-Json).Properties
            if ($properties.Configuration -cne 'Release' -or ($properties.IsPackable -ieq 'true') -ne $test.Packable) {
                throw "Unexpected SDK evaluation for '$($test.Name)': $evaluation"
            }
            $restore = & $nativeDotnet restore $siblingPath --configfile (Join-Path $fixture 'NuGet.config') -p:Configuration=Release 2>&1
            if ($LASTEXITCODE -ne 0) { throw "Fixture restore failed: $restore" }
            $pack = & $nativeDotnet pack $siblingPath -c Release --no-build --no-restore -o (Join-Path $fixture 'packages') 2>&1
            if ($LASTEXITCODE -ne 0) { throw "Fixture pack failed: $pack" }
            $produced = Test-Path -LiteralPath (Join-Path $fixture 'packages/KeelMatrix.UnclassifiedSibling.1.0.0.nupkg')
            if ($produced -ne $test.Packable) { throw "SDK pack disagrees for '$($test.Name)': $pack" }

            $metadata = (Invoke-NestedPwsh -NoProfile -File (Join-Path $fixture 'probe.ps1') -Mode metadata 2>&1) -join "`n"
            $metadataExit = $LASTEXITCODE
            $invalidMetadata = ($test.Packable -and -not $test.Classified) -or [bool]$test.MainMetadata
            if ($invalidMetadata) {
                if ($metadataExit -eq 0 -or $metadata -notmatch 'Website metadata (has|sentinels|must use)' -or $metadata -match 'WEBSITE_METADATA_PASS') {
                    throw "Metadata enumeration accepted '$($test.Name)' (exit $metadataExit): $metadata"
                }
            }
            elseif ($metadataExit -ne 0 -or $metadata -notmatch 'WEBSITE_METADATA_PASS') {
                throw "Valid metadata rejected for '$($test.Name)': $metadata"
            }
            $gate = (Invoke-NestedPwsh -NoProfile -File (Join-Path $fixture 'probe.ps1') -Mode gate 2>&1) -join "`n"
            $gateExit = $LASTEXITCODE
            $expectedCount = if ($test.Packable) { 2 } else { 1 }
            if ($gate -notmatch "PACKABLE_PROJECT_COUNT: $expectedCount;") {
                throw "Package gate selected the wrong project set for '$($test.Name)': $gate"
            }
            if ($test.Packable -or $test.MainMetadata) {
                if ($gateExit -eq 0 -or $gate -match 'PACKABILITY_RESTORE_REACHED' -or $gate -notmatch 'Website metadata|Expected exactly one packable project') {
                    throw "Package gate failed to reject '$($test.Name)' before restore (exit $gateExit): $gate"
                }
            }
            elseif ($gateExit -ne 0 -or $gate -notmatch 'PACKABILITY_RESTORE_REACHED') {
                throw "Nonpackable sibling blocked the valid gate for '$($test.Name)': $gate"
            }
            Write-Output "PACKABILITY_CASE_PASS: $($test.Name); SDK=$($properties.IsPackable); package=$produced; metadataExit=$metadataExit; gateExit=$gateExit"
        }
        finally { Pop-Location }
    }
    Write-Output "WEBSITE_PACKABILITY_TEST_PASS: $($selected.Count) real SDK evaluation/pack and native gate cases."
}
finally {
    $env:PATH = $originalPath
    if ($null -eq $originalExitCode) { Remove-Variable LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue }
    else { $global:LASTEXITCODE = $originalExitCode }
    $resolved = [IO.Path]::GetFullPath($temporaryRoot)
    if (-not $resolved.StartsWith([IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($ScratchRoot)) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Unexpected packability test scratch path.'
    }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
