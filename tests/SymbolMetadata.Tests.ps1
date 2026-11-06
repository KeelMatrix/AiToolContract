#requires -Version 7.0
#requires -PSEdition Core
param([switch] $VerifyBuildOutput)

$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'build/Test-SymbolMetadata.ps1')

# Real portable PDB and PE metadata, generated in memory without source or icon files.
Add-Type -TypeDefinition @'
using System;
using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;

public static class SymbolFixture
{
    public static byte[] Pdb(string document, string sourceLink, int records = 1)
    {
        var metadata = new MetadataBuilder();
        if (!string.IsNullOrEmpty(document))
            metadata.AddDocument(metadata.GetOrAddDocumentName(document), default, default, default);
        for (int i = 0; i < records; i++)
            metadata.AddCustomDebugInformation(MetadataTokens.EntityHandle(1),
                metadata.GetOrAddGuid(new Guid("cc110556-a091-4d38-9fec-25ab9a351a6a")),
                metadata.GetOrAddBlob(Encoding.UTF8.GetBytes(sourceLink)));
        var rows = new int[64];
        rows[0] = 1;
        var pdb = new PortablePdbBuilder(metadata, ImmutableArray.Create(rows), default);
        var blob = new BlobBuilder();
        pdb.Serialize(blob);
        return blob.ToArray();
    }

    public static byte[] Assembly(string path)
    {
        var metadata = new MetadataBuilder();
        metadata.AddModule(0, metadata.GetOrAddString("Fixture.dll"),
            metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        var debug = new DebugDirectoryBuilder();
        if (!string.IsNullOrEmpty(path))
            debug.AddCodeViewEntry(path, new BlobContentId(Guid.NewGuid(), 0), 0x0100);
        var pe = new ManagedPEBuilder(new PEHeaderBuilder(), new MetadataRootBuilder(metadata),
            new BlobBuilder(), debugDirectoryBuilder: debug);
        var blob = new BlobBuilder();
        pe.Serialize(blob);
        return blob.ToArray();
    }
}
'@

$commit = '0123456789abcdef0123456789abcdef01234567'
$url = "https://raw.githubusercontent.com/KeelMatrix/AiToolContract/$commit/*"
$validMap = @{ documents = @{ '/_/*' = $url } } | ConvertTo-Json -Compress
$validDocument = '/_/src/KeelMatrix.AiToolContract/Contract.cs'
$validPdb = [SymbolFixture]::Pdb($validDocument, $validMap)
$validAssembly = [SymbolFixture]::Assembly('/_/src/obj/Release/KeelMatrix.AiToolContract.pdb')
$caseCount = 0

function Check([string] $name, [byte[]] $pdb, [byte[]] $assembly, [bool] $shouldPass) {
    $passed = $true
    try { Test-SymbolMetadata $pdb $assembly $commit }
    catch { $passed = $false }
    if ($passed -ne $shouldPass) { throw "Unexpected symbol validation result: $name (accepted=$passed)." }
    $script:caseCount++
}

Check 'normalized metadata' $validPdb $validAssembly $true
Check 'filename-only CodeView' $validPdb ([SymbolFixture]::Assembly('KeelMatrix.AiToolContract.pdb')) $true
foreach ($path in @('C:\Users\developer\repo\Contract.cs', 'C:/Users/developer/repo/Contract.cs',
    '\\server\workspace\Contract.cs', '/home/developer/repo/Contract.cs', '/Users/developer/repo/Contract.cs',
    '/home/runner/work/repo/Contract.cs', 'src/Contract.cs', '/_/../private/Contract.cs',
    '/_/src/../../private/Contract.cs', '/_/src\Contract.cs')) {
    Check "document $path" ([SymbolFixture]::Pdb($path, $validMap)) $validAssembly $false
    $map = @{ documents = @{ ($path + '/*') = $url } } | ConvertTo-Json -Compress
    Check "map $path" ([SymbolFixture]::Pdb($validDocument, $map)) $validAssembly $false
    Check "CodeView $path" $validPdb ([SymbolFixture]::Assembly($path)) $false
}
foreach ($map in @('{}', '{"Documents":{}}', '{"documents":null}', '{"documents":[]}', '{',
    '{"documents":{},"documents":{}}',
    ($validMap.Replace($commit, ('f' * 40))),
    ($validMap.Replace('KeelMatrix/AiToolContract', 'other/repository')),
    ($validMap.Replace('https:', 'http:')),
    ('{"documents":{"/_/*":"' + $url + '","/home/developer/*":"' + $url + '"}}'))) {
    Check 'invalid SourceLink' ([SymbolFixture]::Pdb($validDocument, $map)) $validAssembly $false
}
Check 'missing documents' ([SymbolFixture]::Pdb($null, $validMap)) $validAssembly $false
Check 'missing SourceLink' ([SymbolFixture]::Pdb($validDocument, $validMap, 0)) $validAssembly $false
Check 'duplicate SourceLink' ([SymbolFixture]::Pdb($validDocument, $validMap, 2)) $validAssembly $false
Check 'missing CodeView' $validPdb ([SymbolFixture]::Assembly($null)) $false
Check 'wrong symbol filename' $validPdb ([SymbolFixture]::Assembly('/_/other.pdb')) $false
Check 'invalid PDB' ([byte[]](1, 2, 3)) $validAssembly $false
Check 'invalid assembly' $validPdb ([byte[]](1, 2, 3)) $false
if ($VerifyBuildOutput) {
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $head = (& git -C $repoRoot rev-parse HEAD).Trim()
    foreach ($tfm in @('net8.0', 'netstandard2.0')) {
        $basePath = Join-Path $repoRoot "src/KeelMatrix.AiToolContract/bin/Release/$tfm/KeelMatrix.AiToolContract"
        Test-SymbolMetadata ([IO.File]::ReadAllBytes("$basePath.pdb")) ([IO.File]::ReadAllBytes("$basePath.dll")) $head
        $caseCount++
    }
}
Write-Output "SYMBOL_METADATA_TEST_PASS: $caseCount cases"
