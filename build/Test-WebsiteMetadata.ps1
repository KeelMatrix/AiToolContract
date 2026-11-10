function Assert-WebsiteMetadata {
    param([string] $ManifestJson, [object[]] $Packages)

    function Assert-ObjectKeys($Element, [string[]] $Keys) {
        if ($Element.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
            throw 'Website metadata must contain JSON objects.'
        }
        $actual = @($Element.EnumerateObject() | ForEach-Object { $_.Name })
        if ($actual.Count -ne $Keys.Count) {
            throw 'Website metadata has missing, duplicate, or unknown members.'
        }
        foreach ($key in $Keys) {
            if (@($actual | Where-Object { $_ -ceq $key }).Count -ne 1) {
                throw 'Website metadata has missing, duplicate, or wrong-case members.'
            }
        }
    }

    $document = [System.Text.Json.JsonDocument]::Parse($ManifestJson)
    try {
        $root = $document.RootElement
        Assert-ObjectKeys $root @('schemaVersion', 'packages')
        $schema = $root.GetProperty('schemaVersion')
        if ($schema.ValueKind -ne [System.Text.Json.JsonValueKind]::Number -or $schema.GetRawText() -cne '1') {
            throw 'Website metadata requires schemaVersion 1.'
        }
        $ids = @($Packages | ForEach-Object { [string]$_.PackageId })
        if ($ids.Count -eq 0 -or @($ids | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count -gt 0 -or
            @($ids | Sort-Object -Unique).Count -ne $ids.Count) {
            throw 'Website metadata requires unique, nonempty packable package IDs.'
        }
        $entries = $root.GetProperty('packages')
        Assert-ObjectKeys $entries $ids
        $primaryCount = 0
        $hasPublicProduct = $false
        foreach ($package in $Packages) {
            $entry = $entries.GetProperty([string]$package.PackageId)
            Assert-ObjectKeys $entry @('visibility', 'role')
            $visibility = $entry.GetProperty('visibility').GetString()
            $role = $entry.GetProperty('role').GetString()
            if (@('public-product', 'internal-package') -cnotcontains $visibility -or
                @('primary', 'component') -cnotcontains $role) {
                throw 'Website metadata visibility or role is invalid.'
            }
            if ($role -ceq 'primary') { $primaryCount++ }
            if ($visibility -ceq 'public-product') { $hasPublicProduct = $true }
            $tags = @(([string]$package.PackageTags -split '[;\s]+') | Where-Object { $_ })
            $sentinels = @($tags | Where-Object { $_ -imatch '^keelmatrix-' })
            if ($sentinels.Count -ne 2 -or
                @($sentinels | Where-Object { $_ -ceq "keelmatrix-$visibility" }).Count -ne 1 -or
                @($sentinels | Where-Object { $_ -ceq "keelmatrix-$role" }).Count -ne 1) {
                throw 'Website metadata sentinels are missing, duplicate, conflicting, wrong-case, or disagree with the manifest.'
            }
            $canonicalUrl = 'https://github.com/KeelMatrix/AiToolContract'
            if ($package.PackageProjectUrl -cne $canonicalUrl -or $package.RepositoryUrl -cne $canonicalUrl -or
                $package.RepositoryType -cne 'git') {
                throw 'Website metadata must use the canonical project and git repository URLs.'
            }
        }
        if ($primaryCount -gt 1 -or ($hasPublicProduct -and $primaryCount -ne 1)) {
            throw 'Website metadata requires exactly one primary per public product family.'
        }
    }
    finally { $document.Dispose() }
}

function Assert-WebsitePackage {
    param([string] $PackagePath, [string] $ManifestJson, [object[]] $Packages)

    $zip = [IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $nuspecs = @($zip.Entries | Where-Object { $_.FullName -clike '*.nuspec' })
        if ($nuspecs.Count -ne 1) { throw 'Website metadata requires exactly one package nuspec.' }
        $reader = [IO.StreamReader]::new($nuspecs[0].Open())
        try { [xml]$nuspec = $reader.ReadToEnd() }
        finally { $reader.Dispose() }
        $metadata = $nuspec.package.metadata
        $source = @($Packages | Where-Object { $_.PackageId -ceq $metadata.id })
        if ($source.Count -ne 1) { throw 'Website metadata built package ID does not match the source.' }
        $built = [pscustomobject]@{
            PackageId = [string]$metadata.id
            PackageTags = [string]$metadata.tags
            PackageProjectUrl = [string]$metadata.projectUrl
            RepositoryUrl = [string]$metadata.repository.url
            RepositoryType = [string]$metadata.repository.type
        }
        $allPackages = @($Packages | Where-Object { $_.PackageId -cne $built.PackageId }) + @($built)
        Assert-WebsiteMetadata $ManifestJson $allPackages
        if ((@($source[0].PackageTags -split '[;\s]+' | Where-Object { $_ }) -join ' ') -cne
            (@($built.PackageTags -split '[;\s]+' | Where-Object { $_ }) -join ' ')) {
            throw 'Website metadata built package tags do not match the source.'
        }
    }
    finally { $zip.Dispose() }
}
