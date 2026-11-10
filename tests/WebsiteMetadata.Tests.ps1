#requires -Version 7.0
param([string] $ScratchRoot = [IO.Path]::GetTempPath())

$ErrorActionPreference = 'Stop'
$originalExitCode = (Get-Variable LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue).Value
$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $repoRoot 'build/Test-WebsiteMetadata.ps1')
. (Join-Path $repoRoot 'build/Invoke-NestedPwsh.ps1')
$manifest = '{"schemaVersion":1,"packages":{"KeelMatrix.AiToolContract":{"visibility":"public-product","role":"primary"}}}'
$package = [pscustomobject]@{
    PackageId = 'KeelMatrix.AiToolContract'
    PackageTags = 'ai;testing;keelmatrix-public-product;keelmatrix-primary'
    PackageProjectUrl = 'https://github.com/KeelMatrix/AiToolContract'
    RepositoryUrl = 'https://github.com/KeelMatrix/AiToolContract'
    RepositoryType = 'git'
}
$script:checks = 0
function Reject([scriptblock] $Action, [string] $Label) {
    $failed = $false
    try { & $Action } catch { $failed = $true }
    if (-not $failed) { throw "Website metadata accepted $Label." }
    $script:checks++
}

Assert-WebsiteMetadata $manifest @($package)
foreach ($json in @(
    '', 'null', '[]', '{}',
    $manifest.Replace('"schemaVersion":1,', ''),
    $manifest.Replace('"schemaVersion":1', '"schemaVersion":2'),
    $manifest.Replace('"schemaVersion":1', '"schemaVersion":"1"'),
    $manifest.Replace('"schemaVersion":1', '"schemaVersion":true'),
    $manifest.Replace('"schemaVersion":1', '"schemaVersion":1,"schemaVersion":1'),
    $manifest.Replace('schemaVersion', 'SchemaVersion'),
    $manifest.Replace('"packages":{', '"packages":null,"extra":{'),
    $manifest.Replace('KeelMatrix.AiToolContract', 'keelmatrix.aitoolcontract'),
    $manifest.Replace('KeelMatrix.AiToolContract', 'KeelMatrix.Other'),
    '{"schemaVersion":1,"packages":{}}',
    '{"schemaVersion":1,"packages":{"KeelMatrix.AiToolContract":{"visibility":"public-product","role":"primary"},"KeelMatrix.AiToolContract":{"visibility":"public-product","role":"primary"}}}',
    $manifest.Replace('"role":"primary"', '"role":"primary","role":"primary"'),
    $manifest.Replace('"role":"primary"', '"Role":"primary"'),
    $manifest.Replace('"role":"primary"', '"role":null'),
    $manifest.Replace('"role":"primary"', '"role":{}'),
    $manifest.Replace('"role":"primary"', '"role":"Primary"'),
    $manifest.Replace('"role":"primary"', '"role":"other"'),
    $manifest.Replace('"visibility":"public-product"', '"visibility":"Public-Product"'),
    $manifest.Replace('"visibility":"public-product"', '"visibility":"other"'),
    $manifest.Replace('"visibility":"public-product"', '"visibility":[]')
)) { Reject { Assert-WebsiteMetadata $json @($package) } 'malformed manifest' }

foreach ($tags in @(
    '', 'ai', 'ai;keelmatrix-primary', 'ai;keelmatrix-public-product',
    'ai;keelmatrix-public-product;keelmatrix-primary;keelmatrix-internal-package',
    'ai;keelmatrix-public-product;keelmatrix-primary;keelmatrix-component',
    'ai;keelmatrix-public-product;keelmatrix-primary;keelmatrix-primary',
    'ai;keelmatrix-public-product;keelmatrix-public-product;keelmatrix-primary',
    'ai;KeelMatrix-public-product;keelmatrix-primary',
    'ai;keelmatrix-public-product;keelmatrix-Primary',
    'ai;keelmatrix-internal-package;keelmatrix-primary',
    'ai;keelmatrix-public-product;keelmatrix-component',
    'ai;keelmatrix-public-product;keelmatrix-primary;KEELMATRIX-PRIMARY'
)) {
    $bad = $package.PSObject.Copy(); $bad.PackageTags = $tags
    Reject { Assert-WebsiteMetadata $manifest @($bad) } 'invalid sentinels'
}
foreach ($property in @('PackageProjectUrl', 'RepositoryUrl')) {
    foreach ($url in @('', 'http://github.com/KeelMatrix/AiToolContract',
        'https://github.com/Other/AiToolContract', 'https://github.com/KeelMatrix/Other',
        'https://github.com.evil/KeelMatrix/AiToolContract', 'https://keelmatrix.com',
        'https://github.com/KeelMatrix/AiToolContract?x=1', 'https://github.com/KeelMatrix/AiToolContract/')) {
        $bad = $package.PSObject.Copy(); $bad.$property = $url
        Reject { Assert-WebsiteMetadata $manifest @($bad) } "invalid $property"
    }
}
$bad = $package.PSObject.Copy(); $bad.RepositoryType = 'Git'
Reject { Assert-WebsiteMetadata $manifest @($bad) } 'invalid repository type'
Reject { Assert-WebsiteMetadata $manifest @() } 'no packable packages'
Reject { Assert-WebsiteMetadata $manifest @($package, $package) } 'duplicate source IDs'
$component = $package.PSObject.Copy(); $component.PackageTags = $component.PackageTags.Replace('primary', 'component')
Reject { Assert-WebsiteMetadata ($manifest.Replace('primary', 'component')) @($component) } 'zero primaries'
$sibling = $package.PSObject.Copy(); $sibling.PackageId = 'KeelMatrix.AiToolContract.Sibling'
$family = $manifest | ConvertFrom-Json -AsHashtable
$family.packages[$sibling.PackageId] = @{ visibility = 'public-product'; role = 'primary' }
Reject { Assert-WebsiteMetadata ($family | ConvertTo-Json -Depth 5) @($package, $sibling) } 'two primaries'
$family.packages[$sibling.PackageId].role = 'component'
$sibling.PackageTags = $component.PackageTags
Assert-WebsiteMetadata ($family | ConvertTo-Json -Depth 5) @($package, $sibling)
Reject { Assert-WebsiteMetadata ($family | ConvertTo-Json -Depth 5) @($package) } 'extra manifest ID'
Assert-WebsiteMetadata $manifest @($package.PSObject.Copy() | ForEach-Object { $_.PackageTags = $_.PackageTags.Replace(';', ' '); $_ })

$temporaryRoot = Join-Path $ScratchRoot ('website-metadata-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
try {
    $archivePath = Join-Path $temporaryRoot 'fixture.nupkg'
    function Write-Archive([string] $Xml) {
        $zip = [IO.Compression.ZipFile]::Open($archivePath, [IO.Compression.ZipArchiveMode]::Create)
        try {
            $writer = [IO.StreamWriter]::new($zip.CreateEntry('fixture.nuspec').Open())
            try { $writer.Write($Xml) } finally { $writer.Dispose() }
        } finally { $zip.Dispose() }
    }
    $xml = '<package><metadata><id>KeelMatrix.AiToolContract</id><tags>ai testing keelmatrix-public-product keelmatrix-primary</tags><projectUrl>https://github.com/KeelMatrix/AiToolContract</projectUrl><repository type="git" url="https://github.com/KeelMatrix/AiToolContract" /></metadata></package>'
    Write-Archive $xml
    Assert-WebsitePackage $archivePath $manifest @($package)
    Remove-Item -LiteralPath $archivePath
    foreach ($invalid in @($xml.Replace('keelmatrix-primary', 'keelmatrix-component'),
        $xml.Replace('ai testing ', ''), $xml.Replace('<id>KeelMatrix.AiToolContract', '<id>Other'),
        $xml.Replace('https://github.com/KeelMatrix/AiToolContract', 'https://example.com'))) {
        Write-Archive $invalid
        Reject { Assert-WebsitePackage $archivePath $manifest @($package) } 'built/source drift'
        Remove-Item -LiteralPath $archivePath
    }

    # Exercise the real package gate: valid evaluated source reaches restore; invalid metadata stops it.
    foreach ($directory in @('build', 'scripts', 'src/KeelMatrix.AiToolContract')) {
        New-Item -ItemType Directory -Path (Join-Path $temporaryRoot $directory) -Force | Out-Null
    }
    foreach ($name in @('Invoke-NestedPwsh.ps1', 'Test-NestedPwshLaunch.ps1', 'Test-SymbolMetadata.ps1', 'Test-WebsiteMetadata.ps1')) {
        Copy-Item -LiteralPath (Join-Path $repoRoot "build/$name") -Destination (Join-Path $temporaryRoot 'build')
    }
    foreach ($name in @('Verify-Package.ps1', 'Test-WebsiteMetadata.ps1')) {
        Copy-Item -LiteralPath (Join-Path $repoRoot "scripts/$name") -Destination (Join-Path $temporaryRoot 'scripts')
    }
    Set-Content -LiteralPath (Join-Path $temporaryRoot 'src/KeelMatrix.AiToolContract/KeelMatrix.AiToolContract.csproj') '<Project />'
    function dotnet {
        if ($args[0] -eq 'restore') { throw 'METADATA_RESTORE_REACHED' }
        $global:LASTEXITCODE = 0
        @{ Properties = @{
            IsPackable = 'true'; IsShippingProject = 'true'; GenerateDocumentationFile = 'true'
            PackageId = $package.PackageId; PackageTags = $package.PackageTags
            PackageProjectUrl = $package.PackageProjectUrl; RepositoryUrl = $package.RepositoryUrl
            RepositoryType = $package.RepositoryType
        } } | ConvertTo-Json
    }
    foreach ($valid in @($false, $true)) {
        $inputJson = if ($valid) { $manifest } else { $manifest.Replace('schemaVersion', 'SchemaVersion') }
        Set-Content -LiteralPath (Join-Path $temporaryRoot 'keelmatrix.website.json') $inputJson
        $message = ''
        try { & (Join-Path $temporaryRoot 'scripts/Verify-Package.ps1') -AllowMissingIcon *> $null }
        catch { $message = $_.Exception.Message }
        if (($valid -and $message -cne 'METADATA_RESTORE_REACHED') -or
            (-not $valid -and $message -notlike 'Website metadata*')) {
            throw "Package gate metadata failure propagation is incorrect: $message"
        }
    }
    Set-Content -LiteralPath (Join-Path $temporaryRoot 'keelmatrix.website.json') '{}'
    $childOutput = Invoke-NestedPwsh -NoProfile -File (Join-Path $temporaryRoot 'scripts/Test-WebsiteMetadata.ps1') -RepositoryRoot $temporaryRoot 2>&1
    if ($LASTEXITCODE -eq 0 -or ($childOutput -join "`n") -notmatch 'Website metadata') {
        throw 'Metadata validation did not propagate failure to a native process exit code.'
    }
    Write-Output "WEBSITE_METADATA_TEST_PASS: $script:checks negative cases and real package gate propagation."
}
finally {
    if ($null -eq $originalExitCode) {
        Remove-Variable LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue
    }
    else {
        $global:LASTEXITCODE = $originalExitCode
    }
    $resolved = [IO.Path]::GetFullPath($temporaryRoot)
    if (-not $resolved.StartsWith([IO.Path]::GetFullPath($ScratchRoot), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Unexpected metadata test scratch path.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
