[CmdletBinding()]
param(
    [ValidateSet('All', 'EventJournal', 'RbfSegmentStore')][string]$Project = 'All',
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$FeedDirectory,
    [Parameter(Mandatory)][string]$WorkDirectory,
    [switch]$AdditionalSegmentSmoke
)
$ErrorActionPreference = 'Stop'
if ($Project -ne 'All' -and $PSBoundParameters.ContainsKey('AdditionalSegmentSmoke')) {
    throw [ArgumentException]::new('AdditionalSegmentSmoke is only supported with Project=All.', 'AdditionalSegmentSmoke')
}
$repo = Split-Path $PSScriptRoot -Parent
$feed = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($FeedDirectory)
$work = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($WorkDirectory)
if (Test-Path -LiteralPath $work) { throw 'WorkDirectory must be a fresh directory (private NuGet cache and new journal).' }
if ($work.StartsWith($repo + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'WorkDirectory must be outside the source repository.'
}
$manifestName = if ($Project -eq 'All') { "manifest.$Version.json" } else { "manifest.Atelia.$Project.$Version.json" }
$manifestPath = Join-Path $feed $manifestName
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($Project -ne 'All' -and $manifest.schemaVersion -ne 2) { throw 'Selective smoke requires a schema 2 manifest.' }
if ($Project -eq 'All' -and $manifest.schemaVersion -ne 1) { throw 'Five-package smoke requires a schema 1 manifest.' }
$revision = & git -C $repo rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $revision -ne $manifest.sourceRevision) { throw 'Checkout does not match package source revision.' }
$status = & git -C $repo status --porcelain --untracked-files=normal
if ($LASTEXITCODE -ne 0 -or $status) { throw 'Package source verification requires a clean checkout.' }
New-Item -ItemType Directory -Path $work | Out-Null
# These explicit files prevent all parent Directory.Build imports, including callers' settings.
'<Project />' | Set-Content -LiteralPath (Join-Path $work 'Directory.Build.props') -Encoding utf8NoBOM
'<Project />' | Set-Content -LiteralPath (Join-Path $work 'Directory.Build.targets') -Encoding utf8NoBOM
'<Project />' | Set-Content -LiteralPath (Join-Path $work 'Directory.Packages.props') -Encoding utf8NoBOM
Copy-Item -LiteralPath (Join-Path $repo 'global.json') -Destination $work
$smokeProject = if ($Project -eq 'RbfSegmentStore') { 'RbfSegmentStoreSmoke' } else { 'EventJournalSmoke' }
Copy-Item -LiteralPath (Join-Path $repo "examples/$smokeProject") -Destination $work -Recurse
if ($AdditionalSegmentSmoke) {
    Copy-Item -LiteralPath (Join-Path $repo 'examples/RbfSegmentStoreSmoke') -Destination $work -Recurse
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'PackageMetadataCheck') -Destination $work -Recurse
$escapedFeed = [Security.SecurityElement]::Escape($feed)
@"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources><clear /><add key="storage" value="$escapedFeed" /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources>
  <packageSourceMapping><clear /><packageSource key="storage"><package pattern="Atelia.*" /></packageSource><packageSource key="nuget.org"><package pattern="*" /></packageSource></packageSourceMapping>
  <fallbackPackageFolders><clear /></fallbackPackageFolders>
</configuration>
"@ | Set-Content -LiteralPath (Join-Path $work 'NuGet.Config') -Encoding utf8NoBOM
$logs = Join-Path $work 'logs'
New-Item -ItemType Directory -Path $logs | Out-Null
function Invoke-LoggedDotnet([string[]]$Arguments, [string]$LogName) {
    & dotnet @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $logs $LogName) | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed; see $logs/$LogName" }
}
function Assert-AdditionalSegmentAssets {
    # All remains schema 1: versions are shared, and each corresponding package filename binds its ID.
    if ($manifest.version -cne $Version) { throw 'All manifest version differs from requested candidate version.' }
    $ids = @('Atelia.Primitives', 'Atelia.Data', 'Atelia.Rbf', 'Atelia.RbfSegmentStore')
    foreach ($id in $ids) {
        $records = @($manifest.packages | Where-Object id -CEQ $id)
        if ($records.Count -ne 1 -or $records[0].file -cne "$id.$Version.nupkg") {
            throw "All manifest does not contain the expected candidate: $id/$Version"
        }
    }
    $assets = Get-Content -LiteralPath (Join-Path $work 'RbfSegmentStoreSmoke/obj/project.assets.json') -Raw | ConvertFrom-Json
    $libraries = @($assets.libraries.PSObject.Properties)
    if (@($libraries | Where-Object { $_.Value.type -cne 'package' }).Count -ne 0) {
        throw 'Direct SegmentStore consumer assets contain a non-package reference.'
    }
    $expected = @($ids | ForEach-Object { "$_/$Version" })
    $actual = @($libraries.Name | Where-Object { $_.StartsWith('Atelia.', [StringComparison]::OrdinalIgnoreCase) })
    if ($actual.Count -ne $expected.Count -or @($actual | Where-Object { $expected -cnotcontains $_ }).Count -ne 0) {
        throw 'Direct SegmentStore restored four-package closure differs from All manifest.'
    }
    $target = $assets.targets.PSObject.Properties['net10.0'].Value
    if ($null -eq $target) { throw 'Direct SegmentStore consumer assets lack net10.0 target.' }
    $targetIds = @($target.PSObject.Properties.Name | Where-Object { $_.StartsWith('Atelia.', [StringComparison]::OrdinalIgnoreCase) })
    if ($targetIds.Count -ne $expected.Count -or @($targetIds | Where-Object { $expected -cnotcontains $_ }).Count -ne 0) {
        throw 'Direct SegmentStore target closure differs from All manifest.'
    }
    $frameworks = @($assets.project.frameworks.PSObject.Properties)
    $direct = @($frameworks[0].Value.dependencies.PSObject.Properties.Name | Where-Object { $_.StartsWith('Atelia.', [StringComparison]::OrdinalIgnoreCase) })
    if ($frameworks.Count -ne 1 -or $direct.Count -ne 1 -or $direct[0] -cne 'Atelia.RbfSegmentStore') {
        throw 'Direct SegmentStore smoke must PackageReference only Atelia.RbfSegmentStore.'
    }
    'Verified direct SegmentStore four-package assets against the original schema 1 All manifest.' |
        Set-Content -LiteralPath (Join-Path $logs 'segment-assets.log') -Encoding utf8NoBOM
}
$previousPackages = $env:NUGET_PACKAGES
$previousHttpCache = $env:NUGET_HTTP_CACHE_PATH
try {
    $env:NUGET_PACKAGES = Join-Path $work 'packages'
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $work 'http-cache'
    $smokeData = if ($Project -eq 'RbfSegmentStore') { 'segment-store' } else { 'journal' }
    Push-Location $work
    try {
        Invoke-LoggedDotnet @('restore', "$smokeProject/$smokeProject.csproj", '--configfile', 'NuGet.Config', "-p:StoragePackageVersion=$Version") 'primary-restore.log'
        Invoke-LoggedDotnet @('run', '--project', "$smokeProject/$smokeProject.csproj", '-c', 'Release', '--no-restore', "-p:StoragePackageVersion=$Version", '--', (Join-Path $work $smokeData)) 'primary-smoke.log'
        Invoke-LoggedDotnet @('restore', 'PackageMetadataCheck/PackageMetadataCheck.csproj', '--configfile', 'NuGet.Config') 'metadata-restore.log'
        Invoke-LoggedDotnet @('run', '--project', 'PackageMetadataCheck/PackageMetadataCheck.csproj', '-c', 'Release', '--no-restore', '--', $manifestPath, $repo, (Join-Path $work "$smokeProject/obj/project.assets.json"), $Version) 'metadata-check.log'
        if ($AdditionalSegmentSmoke) {
            $directSegmentStoreSmoke = 'RbfSegmentStoreSmoke/RbfSegmentStoreSmoke.csproj'
            Invoke-LoggedDotnet @('restore', $directSegmentStoreSmoke, '--configfile', 'NuGet.Config', "-p:StoragePackageVersion=$Version") 'segment-restore.log'
            Assert-AdditionalSegmentAssets
            Invoke-LoggedDotnet @('run', '--project', $directSegmentStoreSmoke, '-c', 'Release', '--no-restore', "-p:StoragePackageVersion=$Version", '--', (Join-Path $work 'direct-segment-store')) 'segment-smoke.log'
        }
    }
    finally { Pop-Location }
    Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $work 'verified-packages.json')
    Write-Host "Package smoke passed. Evidence and isolated assets: $work"
}
finally {
    $env:NUGET_PACKAGES = $previousPackages
    $env:NUGET_HTTP_CACHE_PATH = $previousHttpCache
}
