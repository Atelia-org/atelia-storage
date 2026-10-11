# Allowed consumer graphs are acceptance contracts; Pack.ps1 owns the production package list.
function Copy-PackageSmokeSources([string]$SourceDirectory, [string]$WorkDirectory) {
    # These small consumers and the metadata helper keep their source files at the top level.
    # Never import repository bin/obj output into the fresh isolated build.
    $destination = Join-Path $WorkDirectory (Split-Path $SourceDirectory -Leaf)
    New-Item -ItemType Directory -Path $destination | Out-Null
    Get-ChildItem -LiteralPath $SourceDirectory -File | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $destination
    }
}

function Get-PackageSmokeContracts {
    @(
        [pscustomobject]@{ Name = 'RbfPackageSmoke'; DirectId = 'Atelia.Rbf'; Closure = @('Atelia.Primitives', 'Atelia.Data', 'Atelia.Rbf'); LogPrefix = 'primary'; AssetsLog = 'rbf-assets.log'; DataDirectory = 'rbf-files' },
        [pscustomobject]@{ Name = 'BinaryPackageSmoke'; DirectId = 'Atelia.Binary'; Closure = @('Atelia.Binary'); LogPrefix = 'binary'; AssetsLog = 'binary-assets.log'; DataDirectory = '' },
        [pscustomobject]@{ Name = 'FrameStorePackageSmoke'; DirectId = 'Atelia.FrameStore'; Closure = @('Atelia.Primitives', 'Atelia.Data', 'Atelia.Rbf', 'Atelia.Binary', 'Atelia.FrameStore'); LogPrefix = 'frame-store'; AssetsLog = 'frame-store-assets.log'; DataDirectory = 'frame-store-files' },
        [pscustomobject]@{ Name = 'VersionStorePackageSmoke'; DirectId = 'Atelia.VersionStore'; Closure = @('Atelia.Primitives', 'Atelia.Data', 'Atelia.Rbf', 'Atelia.Binary', 'Atelia.FrameStore', 'Atelia.VersionStore'); LogPrefix = 'version-store'; AssetsLog = 'version-store-assets.log'; DataDirectory = 'version-store-files' }
    )
}

function Assert-PackageSmokeManifest($Manifest, [string]$Version, [string]$FeedDirectory) {
    $ids = @(Get-PackageSmokeContracts | ForEach-Object { $_.Closure } | Select-Object -Unique)
    if ($Manifest.schemaVersion -ne 1 -or $Manifest.version -cne $Version -or @($Manifest.packages).Count -ne $ids.Count -or
        $Manifest.repositoryUrl -cne 'https://github.com/Atelia-org/atelia-storage' -or $Manifest.sourceRevision -cnotmatch '^[0-9a-f]{40}$') {
        throw 'All manifest must describe the requested six-package schema 1 candidate and source.'
    }
    foreach ($id in $ids) {
        $records = @($Manifest.packages | Where-Object id -CEQ $id)
        if ($records.Count -ne 1) { throw "All manifest does not contain the expected candidate: $id/$Version" }
        foreach ($pair in @(@('file', 'sha256', 'nupkg'), @('symbolsFile', 'symbolsSha256', 'snupkg'))) {
            $filename = "$id.$Version.$($pair[2])"
            if ($records[0].($pair[0]) -cne $filename -or $records[0].($pair[1]) -cnotmatch '^[0-9a-f]{64}$') {
                throw "All manifest does not contain the expected candidate artifact: $filename"
            }
            $path = Join-Path $FeedDirectory $filename
            if (!(Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $records[0].($pair[1])) {
                throw "All manifest frozen candidate missing or changed: $filename"
            }
        }
    }
    return $ids
}

function Assert-PackageSmokeAssets($Contract, [string]$WorkDirectory, [string]$Version) {
    $name = $Contract.Name
    $assets = Get-Content -LiteralPath (Join-Path $WorkDirectory "$name/obj/project.assets.json") -Raw | ConvertFrom-Json -AsHashtable
    if (@($assets.libraries.Values | Where-Object { $_.type -cne 'package' }).Count) {
        throw "$name consumer assets contain a non-package reference."
    }
    $expected = @($Contract.Closure | ForEach-Object { "$_/$Version" })
    if ($Contract.Closure -ccontains 'Atelia.Binary') { $expected += 'K4os.Compression.LZ4/1.3.8' }
    $expected = @($expected | Sort-Object)
    $actual = @($assets.libraries.Keys | Sort-Object)
    if (($actual -join '|') -cne ($expected -join '|')) { throw "$name restored package closure differs from the approved closure." }
    if (!$assets.targets.Contains('net10.0') -or $assets.targets.Count -ne 1) { throw "$name consumer assets must have one net10.0 target." }
    $target = @($assets.targets['net10.0'].Keys | Sort-Object)
    if (($target -join '|') -cne ($expected -join '|')) { throw "$name target closure differs from the approved closure." }
    $frameworks = @($assets.project.frameworks.Keys)
    if ($frameworks.Count -ne 1 -or $frameworks[0] -cne 'net10.0') { throw "$name smoke must have one net10.0 target framework." }
    $dependencies = $assets.project.frameworks['net10.0'].dependencies
    $direct = @($dependencies.Keys)
    if ($direct.Count -ne 1 -or $direct[0] -cne $Contract.DirectId) { throw "$name smoke must PackageReference only $($Contract.DirectId)." }
    $reference = $dependencies[$Contract.DirectId]
    if ($reference.target -cne 'Package' -or $reference.version -cnotin @("[$Version]", "[$Version, $Version]", "[$Version,$Version]")) {
        throw "$name direct PackageReference must use exact candidate version $Version."
    }
    $folders = @($assets.packageFolders.Keys)
    $privateCache = [IO.Path]::GetFullPath((Join-Path $WorkDirectory 'packages')).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    if ($folders.Count -ne 1 -or [IO.Path]::GetFullPath($folders[0]).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) -cne $privateCache) {
        throw "$name consumer must use the isolated private NuGet cache."
    }
    return $actual
}
