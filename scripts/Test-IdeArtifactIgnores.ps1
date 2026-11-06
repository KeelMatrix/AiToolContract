[CmdletBinding()]
param([string] $RepositoryRoot = (Split-Path -Parent $PSScriptRoot))

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$paths = @(
    '.vs/state', 'src/.vs/state', '.idea/workspace.xml', 'src/.idea/workspace.xml',
    'project.user', 'src/project.user', 'project.suo', 'src/project.suo',
    'project.userprefs', 'src/project.userprefs', 'TestResults/result.xml', 'tests/TestResults/result.xml',
    'build.log', 'src/build.log', '.cache/state', 'src/.cache/state',
    '.DS_Store', 'src/.DS_Store', 'Thumbs.db', 'src/Thumbs.db',
    'bin/assembly.dll', 'src/bin/assembly.dll', 'obj/project.assets.json', 'src/obj/project.assets.json'
)

# -v identifies the actual rule source, so personal/global excludes cannot make
# a missing repository rule pass. No files need to be created to ask Git.
$output = @(& git -C $RepositoryRoot -c core.quotepath=false check-ignore --no-index --verbose --non-matching -- @paths)
if ($LASTEXITCODE -notin @(0, 1)) { throw 'IDE_IGNORE_FAIL: git check-ignore failed.' }
if ($output.Count -ne $paths.Count) { throw 'IDE_IGNORE_FAIL: incomplete ignore evidence.' }
for ($index = 0; $index -lt $paths.Count; $index++) {
    if ($output[$index] -notmatch '^\.gitignore:\d+:(?!\!)[^\t]+\t') {
        throw "IDE_IGNORE_FAIL: root .gitignore does not ignore '$($paths[$index])'."
    }
}
Write-Output 'IDE_IGNORE_PASS'
