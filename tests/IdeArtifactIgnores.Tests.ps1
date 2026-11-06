$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'build/Invoke-NestedPwsh.ps1')
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$gate = Join-Path $repositoryRoot 'scripts/Test-IdeArtifactIgnores.ps1'
$baseDirectory = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$testRoot = Join-Path $baseDirectory ('ai-tool-contract-ignores-' + [Guid]::NewGuid().ToString('N'))
$rules = [IO.File]::ReadAllLines((Join-Path $repositoryRoot '.gitignore'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    & git -C $testRoot init --quiet
    if ($LASTEXITCODE -ne 0) { throw 'Could not initialize ignore fixture.' }
    function Invoke-Gate {
        $script:gateOutput = @(Invoke-NestedPwsh -ArgumentList @('-NoProfile', '-NonInteractive', '-File', $gate, '-RepositoryRoot', $testRoot) 2>&1)
        return $LASTEXITCODE
    }
    [IO.File]::WriteAllLines((Join-Path $testRoot '.gitignore'), $rules)
    if ((Invoke-Gate) -ne 0) { throw "Normal ignore configuration failed: $gateOutput" }
    foreach ($requiredRule in @('.vs/', '.idea/', '*.user', '*.suo', '*.userprefs', 'TestResults/', '*.log', '.cache/', '.DS_Store', 'Thumbs.db', 'bin/', 'obj/')) {
        [IO.File]::WriteAllLines((Join-Path $testRoot '.gitignore'), @($rules | Where-Object { $_ -cne $requiredRule }))
        if ((Invoke-Gate) -eq 0) { throw "Removing '$requiredRule' did not fail the gate." }
    }
    [IO.File]::WriteAllLines((Join-Path $testRoot '.gitignore'), $rules)
    $trackedCandidates = @('README.md', 'src/Product.cs', 'project.csproj', 'project.userprefs.cs', 'TestResults.cs', 'catalog.json')
    $ignored = @(& git -C $testRoot check-ignore --no-index -- @trackedCandidates)
    if ($LASTEXITCODE -ne 1 -or $ignored.Count -ne 0) { throw 'Ignore rules hide product source or configuration.' }
    Write-Output 'IDE_IGNORE_TEST_PASS: normal rules, twelve removed-rule controls, and source-path controls.'
}
finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    if (-not $resolved.StartsWith($baseDirectory, [StringComparison]::OrdinalIgnoreCase) -or (Split-Path -Leaf $resolved) -notlike 'ai-tool-contract-ignores-*') { throw 'Unsafe cleanup target.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}

exit 0
