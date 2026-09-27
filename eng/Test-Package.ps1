[CmdletBinding()]
param(
    [ValidateSet('All', 'EventJournal', 'RbfSegmentStore')][string]$Project = 'All',
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$FeedDirectory,
    [Parameter(Mandatory)][string]$WorkDirectory
)
$ErrorActionPreference = 'Stop'
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
$previousPackages = $env:NUGET_PACKAGES
$previousHttpCache = $env:NUGET_HTTP_CACHE_PATH
try {
    $env:NUGET_PACKAGES = Join-Path $work 'packages'
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $work 'http-cache'
    $smokeData = if ($Project -eq 'RbfSegmentStore') { 'segment-store' } else { 'journal' }
    Push-Location $work
    try {
        & dotnet restore "$smokeProject/$smokeProject.csproj" --configfile NuGet.Config "-p:StoragePackageVersion=$Version"
        if ($LASTEXITCODE -ne 0) { throw 'Isolated PackageReference restore failed.' }
        & dotnet run --project "$smokeProject/$smokeProject.csproj" -c Release --no-restore "-p:StoragePackageVersion=$Version" -- (Join-Path $work $smokeData)
        if ($LASTEXITCODE -ne 0) { throw "$Project public API smoke failed." }
        & dotnet restore PackageMetadataCheck/PackageMetadataCheck.csproj --configfile NuGet.Config
        if ($LASTEXITCODE -ne 0) { throw 'Metadata checker restore failed.' }
        & dotnet run --project PackageMetadataCheck/PackageMetadataCheck.csproj -c Release --no-restore -- $manifestPath $repo (Join-Path $work "$smokeProject/obj/project.assets.json") $Version
        if ($LASTEXITCODE -ne 0) { throw 'Package metadata / Source Link / dependency graph verification failed.' }
    }
    finally { Pop-Location }
    Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $work 'verified-packages.json')
    Write-Host "Package smoke passed. Evidence and isolated assets: $work"
}
finally {
    $env:NUGET_PACKAGES = $previousPackages
    $env:NUGET_HTTP_CACHE_PATH = $previousHttpCache
}
