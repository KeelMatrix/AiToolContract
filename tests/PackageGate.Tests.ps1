param([string] $ScratchRoot = [IO.Path]::GetTempPath())

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$temporaryRoot = Join-Path $ScratchRoot ('package-gate-tests-' + [Guid]::NewGuid().ToString('N'))
$originalExitCode = Get-Variable LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue

# Stop at the first build operation; these tests exercise the real gate and guard.
function dotnet { throw 'PACKAGE_GATE_BUILD_REACHED' }

try {
    New-Item -ItemType Directory -Path (Join-Path $temporaryRoot 'scripts') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $temporaryRoot 'build') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts/Verify-Package.ps1') -Destination (Join-Path $temporaryRoot 'scripts')
    foreach ($name in @('Invoke-NestedPwsh.ps1', 'Test-NestedPwshLaunch.ps1')) {
        Copy-Item -LiteralPath (Join-Path $repoRoot "build/$name") -Destination (Join-Path $temporaryRoot 'build')
    }

    $unsafePath = Join-Path $temporaryRoot 'unsafe.ps1'
    foreach ($unsafeLaunch in @($false, $true)) {
        if ($unsafeLaunch) {
            [IO.File]::WriteAllText($unsafePath, [string]::Concat('& pw', 'sh -NoProfile'))
        }
        foreach ($priorExitCode in @($null, 0, 23)) {
            if ($null -eq $priorExitCode) {
                Remove-Variable LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue
            }
            else {
                $global:LASTEXITCODE = $priorExitCode
            }

            $failureMessage = ''
            try {
                & (Join-Path $temporaryRoot 'scripts/Verify-Package.ps1') -AllowMissingIcon *> $null
            }
            catch {
                $failureMessage = $_.Exception.Message
            }

            $expectedMessage = if ($unsafeLaunch) {
                'Visible child process launch sites must use the shared containment helper.'
            }
            else {
                'PACKAGE_GATE_BUILD_REACHED'
            }
            if ($failureMessage -cne $expectedMessage) {
                throw "Package gate failed for prior exit code '$priorExitCode', unsafe launch '$unsafeLaunch': expected '$expectedMessage', got '$failureMessage'."
            }
        }
    }

    Write-Output 'PACKAGE_GATE_CONTROL_FLOW_TEST_PASS'
}
finally {
    if ($null -eq $originalExitCode) {
        Remove-Variable LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue
    }
    else {
        $global:LASTEXITCODE = $originalExitCode.Value
    }
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
