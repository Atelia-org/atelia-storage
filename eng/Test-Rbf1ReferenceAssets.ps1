[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path $PSScriptRoot -Parent)
)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath($RepositoryRoot)
$expectedVersion = '0.2.0-rbf1-preview.1'
$expectedRevision = '3e9554e2ea70f769607e80b3fc11a85506050533'
$packageIds = @('Atelia.Primitives', 'Atelia.Data', 'Atelia.Rbf')
$requiredProjects = @(
    'src/RbfSegmentStore/RbfSegmentStore.csproj'
    'src/EventJournal/EventJournal.csproj'
    'tests/RbfSegmentStore.Tests/RbfSegmentStore.Tests.csproj'
    'tests/EventJournal.Tests/EventJournal.Tests.csproj'
    'tools/EventJournal.Toolkit/EventJournal.Toolkit.csproj'
    'tests/EventJournal.Toolkit.Tests/EventJournal.Toolkit.Tests.csproj'
    'tests/EventJournal.Validation/EventJournal.Validation.csproj'
)
function Get-AssetsPath([string]$Project) {
    return Join-Path (Split-Path (Join-Path $repo $Project) -Parent) 'obj/project.assets.json'
}

function Assert-Rbf1Assets([string]$Project, [string]$AssetsPath) {
    $assets = Get-Content -LiteralPath $AssetsPath -Raw | ConvertFrom-Json
    $targets = @($assets.targets.PSObject.Properties)
    if ($targets.Count -eq 0) { throw "$Project has no restored targets." }

    foreach ($id in $packageIds) {
        $expectedKey = "$id/$expectedVersion"
        $matches = @($assets.libraries.PSObject.Properties | Where-Object {
            $_.Name.StartsWith("$id/", [StringComparison]::OrdinalIgnoreCase)
        })
        if ($matches.Count -ne 1 -or $matches[0].Name -cne $expectedKey -or $matches[0].Value.type -cne 'package') {
            throw "$Project must resolve $expectedKey as a package, without a mainline project or another version."
        }
        foreach ($target in $targets) {
            $entry = $target.Value.PSObject.Properties[$expectedKey]
            if ($null -eq $entry -or $entry.Value.type -cne 'package') {
                throw "$Project target $($target.Name) does not resolve $expectedKey as a package."
            }
        }

        # Inspect the package selected by the assets folders, without restore or network access.
        $packageDirectory = $null
        foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
            $candidate = Join-Path $folder $matches[0].Value.path
            if (Test-Path -LiteralPath $candidate -PathType Container) {
                $packageDirectory = $candidate
                break
            }
        }
        if ($null -eq $packageDirectory) { throw "$Project package directory is missing for $expectedKey." }
        $nuspecs = @(Get-ChildItem -LiteralPath $packageDirectory -Filter '*.nuspec' -File)
        if ($nuspecs.Count -ne 1) { throw "$expectedKey must have exactly one local nuspec." }
        [xml]$nuspec = Get-Content -LiteralPath $nuspecs[0].FullName -Raw
        $metadata = $nuspec.package.metadata
        if ($metadata.id -cne $id -or $metadata.version -cne $expectedVersion -or $metadata.repository.commit -cne $expectedRevision) {
            throw "$expectedKey nuspec identity or repository commit differs from the fixed RBF1 package source."
        }
    }
    Write-Host "Verified RBF1 package assets: $Project"
}

foreach ($project in $requiredProjects) {
    $assetsPath = Get-AssetsPath $project
    if (-not (Test-Path -LiteralPath $assetsPath -PathType Leaf)) {
        throw "Missing $assetsPath. Restore/build the default solution before checking RBF1 reference assets."
    }
    Assert-Rbf1Assets $project $assetsPath
}
Write-Host "RBF1 reference dependencies match $expectedVersion, source $expectedRevision."
