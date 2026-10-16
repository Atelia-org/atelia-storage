[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$FeedDirectory,
    [Parameter(Mandatory)][string]$WorkDirectory
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'PackageMetadataCheck/ConsumerContracts.ps1')
$consumers = @(Get-PackageSmokeContracts)
$feed = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($FeedDirectory)
$work = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($WorkDirectory)
if (Test-Path -LiteralPath $work) { throw 'WorkDirectory must be a fresh directory (private NuGet cache and new RBF files).' }
if ($work -eq $repo -or $work.StartsWith($repo + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'WorkDirectory must be outside the source repository.'
}
$manifestPath = Join-Path $feed "manifest.$Version.json"
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
[void](Assert-PackageSmokeManifest $manifest $Version $feed)
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
foreach ($consumer in $consumers) {
    Copy-PackageSmokeSources (Join-Path $repo "examples/$($consumer.Name)") $work
}
Copy-PackageSmokeSources (Join-Path $PSScriptRoot 'PackageMetadataCheck') $work
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
$previousPackages = $env:NUGET_PACKAGES
$previousHttpCache = $env:NUGET_HTTP_CACHE_PATH
try {
    $env:NUGET_PACKAGES = Join-Path $work 'packages'
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $work 'http-cache'
    Push-Location $work
    try {
        foreach ($consumer in $consumers) {
            $projectPath = "$($consumer.Name)/$($consumer.Name).csproj"
            Invoke-LoggedDotnet @('restore', $projectPath, '--configfile', 'NuGet.Config', "-p:StoragePackageVersion=$Version") "$($consumer.LogPrefix)-restore.log"
            $resolved = @(Assert-PackageSmokeAssets $consumer $work $Version)
            "Verified $($consumer.DirectId) exact direct reference and $($resolved.Count)-package assets ($($consumer.Closure.Count) Atelia) against the approved closure." |
                Set-Content -LiteralPath (Join-Path $logs $consumer.AssetsLog) -Encoding utf8NoBOM
            $runArguments = @('run', '--project', $projectPath, '-c', 'Release', '--no-restore', "-p:StoragePackageVersion=$Version")
            if ($consumer.DataDirectory) { $runArguments += @('--', (Join-Path $work $consumer.DataDirectory)) }
            Invoke-LoggedDotnet $runArguments "$($consumer.LogPrefix)-smoke.log"
        }
        Invoke-LoggedDotnet @('restore', 'PackageMetadataCheck/PackageMetadataCheck.csproj', '--configfile', 'NuGet.Config') 'metadata-restore.log'
        $metadataArguments = @('run', '--project', 'PackageMetadataCheck/PackageMetadataCheck.csproj', '-c', 'Release', '--no-restore', '--', $manifestPath, $repo)
        $metadataArguments += @($consumers | ForEach-Object { Join-Path $work "$($_.Name)/obj/project.assets.json" })
        $metadataArguments += $Version
        Invoke-LoggedDotnet $metadataArguments 'metadata-check.log'
    }
    finally { Pop-Location }
    Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $work 'verified-packages.json')
    Write-Host "Package smoke passed. Evidence and isolated assets: $work"
}
finally {
    $env:NUGET_PACKAGES = $previousPackages
    $env:NUGET_HTTP_CACHE_PATH = $previousHttpCache
}
