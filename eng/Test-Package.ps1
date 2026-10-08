[CmdletBinding()]
param(
    [ValidateSet('All')][string]$Project = 'All',
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$FeedDirectory,
    [Parameter(Mandatory)][string]$WorkDirectory,
    [switch]$AdditionalSegmentSmoke
)
$ErrorActionPreference = 'Stop'
if ($PSBoundParameters.ContainsKey('AdditionalSegmentSmoke')) {
    throw [ArgumentException]::new('Main delivers only Primitives/Data/Rbf/Binary. AdditionalSegmentSmoke is maintained on the RBF1 branch.', 'AdditionalSegmentSmoke')
}
$repo = Split-Path $PSScriptRoot -Parent
$feed = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($FeedDirectory)
$work = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($WorkDirectory)
if (Test-Path -LiteralPath $work) { throw 'WorkDirectory must be a fresh directory (private NuGet cache and new RBF files).' }
if ($work -eq $repo -or $work.StartsWith($repo + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'WorkDirectory must be outside the source repository.'
}
$manifestPath = Join-Path $feed "manifest.$Version.json"
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$ids = @('Atelia.Primitives', 'Atelia.Data', 'Atelia.Rbf', 'Atelia.Binary')
if ($manifest.schemaVersion -ne 1 -or $manifest.version -cne $Version -or @($manifest.packages).Count -ne $ids.Count) {
    throw 'All manifest must describe the requested four-package schema 1 candidate.'
}
foreach ($id in $ids) {
    $records = @($manifest.packages | Where-Object id -CEQ $id)
    if ($records.Count -ne 1 -or $records[0].file -cne "$id.$Version.nupkg") {
        throw "All manifest does not contain the expected candidate: $id/$Version"
    }
}
$revision = & git -C $repo rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $revision -ne $manifest.sourceRevision) { throw 'Checkout does not match package source revision.' }
$status = & git -C $repo status --porcelain --untracked-files=normal
if ($LASTEXITCODE -ne 0 -or $status) { throw 'Package source verification requires a clean checkout.' }
New-Item -ItemType Directory -Path $work | Out-Null
# These explicit files prevent all parent Directory.Build imports, including callers' settings.
foreach ($file in @('Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props')) {
    '<Project />' | Set-Content -LiteralPath (Join-Path $work $file) -Encoding utf8NoBOM
}
Copy-Item -LiteralPath (Join-Path $repo 'global.json') -Destination $work
foreach ($smokeProject in @('RbfPackageSmoke', 'BinaryPackageSmoke')) {
    Copy-Item -LiteralPath (Join-Path $repo "examples/$smokeProject") -Destination $work -Recurse
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
function Assert-ConsumerAssets([string]$smokeProject, [string]$directId, [string[]]$closureIds, [string]$logName) {
    $assets = Get-Content -LiteralPath (Join-Path $work "$smokeProject/obj/project.assets.json") -Raw | ConvertFrom-Json
    $libraries = @($assets.libraries.PSObject.Properties)
    if (@($libraries | Where-Object { $_.Value.type -cne 'package' }).Count -ne 0) {
        throw "$smokeProject consumer assets contain a non-package reference."
    }
    $expected = @($closureIds | ForEach-Object { "$_/$Version" })
    $actual = @($libraries | ForEach-Object { $_.Name } | Where-Object { $_.StartsWith('Atelia.', [StringComparison]::OrdinalIgnoreCase) })
    if ($actual.Count -ne $expected.Count -or @($actual | Where-Object { $expected -cnotcontains $_ }).Count -ne 0) {
        throw "$smokeProject restored package closure differs from the expected All subset."
    }
    $target = $assets.targets.PSObject.Properties['net10.0'].Value
    if ($null -eq $target) { throw "$smokeProject consumer assets lack net10.0 target." }
    $targetIds = @($target.PSObject.Properties | ForEach-Object { $_.Name } | Where-Object { $_.StartsWith('Atelia.', [StringComparison]::OrdinalIgnoreCase) })
    if ($targetIds.Count -ne $expected.Count -or @($targetIds | Where-Object { $expected -cnotcontains $_ }).Count -ne 0) {
        throw "$smokeProject target closure differs from the expected All subset."
    }
    $frameworks = @($assets.project.frameworks.PSObject.Properties)
    if ($frameworks.Count -ne 1) { throw "$smokeProject smoke must have one target framework." }
    $direct = @($frameworks[0].Value.dependencies.PSObject.Properties | ForEach-Object { $_.Name } | Where-Object { $_.StartsWith('Atelia.', [StringComparison]::OrdinalIgnoreCase) })
    if ($direct.Count -ne 1 -or $direct[0] -cne $directId) {
        throw "$smokeProject smoke must PackageReference only $directId."
    }
    "Verified $directId direct reference and $($closureIds.Count)-package assets against the schema 1 All subset." |
        Set-Content -LiteralPath (Join-Path $logs $logName) -Encoding utf8NoBOM
}
$previousPackages = $env:NUGET_PACKAGES
$previousHttpCache = $env:NUGET_HTTP_CACHE_PATH
try {
    $env:NUGET_PACKAGES = Join-Path $work 'packages'
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $work 'http-cache'
    Push-Location $work
    try {
        Invoke-LoggedDotnet @('restore', 'RbfPackageSmoke/RbfPackageSmoke.csproj', '--configfile', 'NuGet.Config', "-p:StoragePackageVersion=$Version") 'primary-restore.log'
        Assert-ConsumerAssets 'RbfPackageSmoke' 'Atelia.Rbf' @('Atelia.Primitives', 'Atelia.Data', 'Atelia.Rbf') 'rbf-assets.log'
        Invoke-LoggedDotnet @('run', '--project', 'RbfPackageSmoke/RbfPackageSmoke.csproj', '-c', 'Release', '--no-restore', "-p:StoragePackageVersion=$Version", '--', (Join-Path $work 'rbf-files')) 'primary-smoke.log'
        Invoke-LoggedDotnet @('restore', 'BinaryPackageSmoke/BinaryPackageSmoke.csproj', '--configfile', 'NuGet.Config', "-p:StoragePackageVersion=$Version") 'binary-restore.log'
        Assert-ConsumerAssets 'BinaryPackageSmoke' 'Atelia.Binary' @('Atelia.Binary') 'binary-assets.log'
        Invoke-LoggedDotnet @('run', '--project', 'BinaryPackageSmoke/BinaryPackageSmoke.csproj', '-c', 'Release', '--no-restore', "-p:StoragePackageVersion=$Version") 'binary-smoke.log'
        Invoke-LoggedDotnet @('restore', 'PackageMetadataCheck/PackageMetadataCheck.csproj', '--configfile', 'NuGet.Config') 'metadata-restore.log'
        Invoke-LoggedDotnet @('run', '--project', 'PackageMetadataCheck/PackageMetadataCheck.csproj', '-c', 'Release', '--no-restore', '--', $manifestPath, $repo, (Join-Path $work 'RbfPackageSmoke/obj/project.assets.json'), (Join-Path $work 'BinaryPackageSmoke/obj/project.assets.json'), $Version) 'metadata-check.log'
    }
    finally { Pop-Location }
    Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $work 'verified-packages.json')
    Write-Host "Package smoke passed. Evidence and isolated assets: $work"
}
finally {
    $env:NUGET_PACKAGES = $previousPackages
    $env:NUGET_HTTP_CACHE_PATH = $previousHttpCache
}
