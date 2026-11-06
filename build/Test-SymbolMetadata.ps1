#requires -Version 7.0
#requires -PSEdition Core

function Assert-NormalizedSourcePath([string] $path) {
    if (-not $path.StartsWith('/_/', [StringComparison]::Ordinal) -or
        $path.Contains('\') -or $path.Contains(':') -or $path.Contains('//') -or
        $path -match '(^|/)\.{1,2}(/|$)') {
        throw 'Symbol metadata contains a source path outside the normalized /_/ root.'
    }
}

function Test-SymbolMetadata([byte[]] $pdbBytes, [byte[]] $assemblyBytes, [string] $commit) {
    $pdbStream = [IO.MemoryStream]::new($pdbBytes, $false)
    $assemblyStream = [IO.MemoryStream]::new($assemblyBytes, $false)
    $provider = $null
    $pe = $null
    try {
        $provider = [System.Reflection.Metadata.MetadataReaderProvider]::FromPortablePdbStream($pdbStream)
        $reader = $provider.GetMetadataReader()
        if ($reader.Documents.Count -eq 0) { throw 'Symbol metadata contains no source documents.' }
        foreach ($handle in $reader.Documents) {
            Assert-NormalizedSourcePath ($reader.GetString($reader.GetDocument($handle).Name))
        }

        $sourceLinkRecords = @($reader.CustomDebugInformation | Where-Object {
            $reader.GetGuid($reader.GetCustomDebugInformation($_).Kind) -eq [guid]'cc110556-a091-4d38-9fec-25ab9a351a6a'
        })
        if ($sourceLinkRecords.Count -ne 1) { throw 'Symbols must contain exactly one SourceLink record.' }
        $record = $reader.GetCustomDebugInformation($sourceLinkRecords[0])
        if ($record.Parent.Kind -ne 'ModuleDefinition') { throw 'SourceLink must describe the symbol module.' }
        $json = [System.Text.Json.JsonDocument]::Parse([Text.Encoding]::UTF8.GetString($reader.GetBlobBytes($record.Value)))
        try {
            $properties = @($json.RootElement.EnumerateObject())
            if ($properties.Count -ne 1 -or $properties[0].Name -cne 'documents' -or
                $properties[0].Value.ValueKind -ne 'Object') { throw 'Invalid SourceLink documents map.' }
            $maps = @($properties[0].Value.EnumerateObject())
            $expectedUrl = "https://raw.githubusercontent.com/KeelMatrix/AiToolContract/$commit/*"
            if ($maps.Count -ne 1 -or $maps[0].Name -cne '/_/*' -or
                $maps[0].Value.ValueKind -ne 'String' -or $maps[0].Value.GetString() -cne $expectedUrl) {
                throw 'SourceLink must map /_/* to the exact repository commit.'
            }
        }
        finally { $json.Dispose() }

        $pe = [System.Reflection.PortableExecutable.PEReader]::new($assemblyStream)
        $codeViews = @($pe.ReadDebugDirectory() | Where-Object Type -eq 'CodeView')
        if ($codeViews.Count -ne 1) { throw 'Assembly must contain exactly one CodeView record.' }
        $path = $pe.ReadCodeViewDebugDirectoryData($codeViews[0]).Path
        if ($path -cne 'KeelMatrix.AiToolContract.pdb') {
            Assert-NormalizedSourcePath $path
            if (-not $path.EndsWith('/KeelMatrix.AiToolContract.pdb', [StringComparison]::Ordinal)) {
                throw 'Assembly debug path does not name the package symbols.'
            }
        }
    }
    finally {
        if ($null -ne $pe) { $pe.Dispose() }
        if ($null -ne $provider) { $provider.Dispose() }
        $assemblyStream.Dispose()
        $pdbStream.Dispose()
    }
}
