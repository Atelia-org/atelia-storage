[CmdletBinding()]
param(
    [ValidateSet('EventJournal', 'RbfSegmentStore')][string]$Project = 'EventJournal',
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$FeedDirectory,
    [Parameter(Mandatory)][string]$WorkDirectory
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$feed = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($FeedDirectory)
$work = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($WorkDirectory)
if (Test-Path -LiteralPath $work) { throw 'WorkDirectory must be new.' }
if ($work.StartsWith($repo + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'WorkDirectory must be outside the source repository.'
}
$packageId = "Atelia.$Project"
$manifestPath = Join-Path $feed "manifest.$packageId.$Version.json"
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 2 -or $manifest.version -cne $Version -or @($manifest.packages).Count -ne 1 -or
    $manifest.packages[0].id -cne $packageId) { throw "Expected one selective $packageId candidate." }
$candidate = $manifest.packages[0]
$candidatePath = Join-Path $feed $candidate.file
$candidateHash = (Get-FileHash -LiteralPath $candidatePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($candidateHash -cne $candidate.sha256) { throw 'Frozen candidate changed before public verification.' }
[void][IO.Directory]::CreateDirectory($work)
$publicPath = Join-Path $work "$packageId.$Version.nupkg"
$normalizedVersion = $Version.ToLowerInvariant()
$normalizedId = $packageId.ToLowerInvariant()
$url = "https://api.nuget.org/v3-flatcontainer/$normalizedId/$normalizedVersion/$normalizedId.$normalizedVersion.nupkg"
$downloaded = $false
for ($attempt = 1; $attempt -le 60; $attempt++) {
    try {
        Invoke-WebRequest -Uri $url -OutFile $publicPath -TimeoutSec 30 | Out-Null
        $downloaded = $true
        break
    }
    catch {
        if ($attempt -eq 60) { throw "Published package was not downloadable after 15 minutes: $url" }
        if (Test-Path -LiteralPath $publicPath) { Remove-Item -LiteralPath $publicPath }
        Start-Sleep -Seconds 15
    }
}
if (!$downloaded) { throw 'Published package download failed.' }
$publicHash = (Get-FileHash -LiteralPath $publicPath -Algorithm SHA256).Hash.ToLowerInvariant()
$candidateArchive = [IO.Compression.ZipFile]::OpenRead($candidatePath)
$publicArchive = [IO.Compression.ZipFile]::OpenRead($publicPath)
try {
    if (!$publicArchive.GetEntry('.signature.p7s')) { throw 'The downloaded public package is not repository signed.' }
    $specEntry = $publicArchive.GetEntry("$packageId.nuspec")
    if (!$specEntry) { throw "Published package has no $packageId nuspec." }
    $reader = [IO.StreamReader]::new($specEntry.Open())
    try { [xml]$spec = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $metadata = $spec.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
    if ($metadata.SelectSingleNode('*[local-name()="id"]').InnerText -cne $packageId -or
        $metadata.SelectSingleNode('*[local-name()="version"]').InnerText -cne $Version) { throw 'Published package identity differs from candidate.' }
    $repository = $metadata.SelectSingleNode('*[local-name()="repository"]')
    if (!$repository -or $repository.GetAttribute('commit') -cne $manifest.sourceRevision -or
        $repository.GetAttribute('url') -cne $manifest.repositoryUrl) { throw 'Published package source differs from candidate.' }
    foreach ($name in @("$packageId.nuspec", 'LICENSE', 'README.md', "lib/net10.0/$packageId.dll", "lib/net10.0/$packageId.xml")) {
        $before = $candidateArchive.GetEntry($name)
        $after = $publicArchive.GetEntry($name)
        if (!$before -or !$after) { throw "Missing package asset: $name" }
        $beforeStream = $before.Open()
        $afterStream = $after.Open()
        try {
            $beforeHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($beforeStream))
            $afterHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($afterStream))
            if ($beforeHash -cne $afterHash) { throw "Published package asset differs from candidate: $name" }
        }
        finally { $beforeStream.Dispose(); $afterStream.Dispose() }
    }
}
finally { $candidateArchive.Dispose(); $publicArchive.Dispose() }

# This consumer has no local feed or inherited MSBuild settings. It proves the public closure.
foreach ($file in @('Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props')) {
    '<Project />' | Set-Content -LiteralPath (Join-Path $work $file) -Encoding utf8NoBOM
}
Copy-Item -LiteralPath (Join-Path $repo 'global.json') -Destination $work
$smokeProject = "${Project}Smoke"
Copy-Item -LiteralPath (Join-Path $repo "examples/$smokeProject") -Destination $work -Recurse
@'
<?xml version="1.0" encoding="utf-8"?>
<configuration><packageSources><clear /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources><fallbackPackageFolders><clear /></fallbackPackageFolders></configuration>
'@ | Set-Content -LiteralPath (Join-Path $work 'NuGet.Config') -Encoding utf8NoBOM
$previousPackages = $env:NUGET_PACKAGES
$previousHttpCache = $env:NUGET_HTTP_CACHE_PATH
try {
    $env:NUGET_PACKAGES = Join-Path $work 'packages'
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $work 'http-cache'
    Push-Location $work
    try {
        & dotnet restore "$smokeProject/$smokeProject.csproj" --configfile NuGet.Config "-p:StoragePackageVersion=$Version"
        if ($LASTEXITCODE -ne 0) { throw 'Public NuGet restore failed.' }
        & dotnet run --project "$smokeProject/$smokeProject.csproj" -c Release --no-restore "-p:StoragePackageVersion=$Version" -- (Join-Path $work 'store')
        if ($LASTEXITCODE -ne 0) { throw "Public $packageId smoke failed." }
    }
    finally { Pop-Location }
}
finally {
    $env:NUGET_PACKAGES = $previousPackages
    $env:NUGET_HTTP_CACHE_PATH = $previousHttpCache
}
$assetsPath = Join-Path $work "$smokeProject/obj/project.assets.json"
$assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json -AsHashtable
$expected = @("$packageId/$Version")
foreach ($dependency in $manifest.dependencies) { $expected += "$($dependency.id)/$($dependency.version)" }
$actual = @($assets.libraries.Keys | Where-Object { $_ -like 'Atelia.*/*' } | Sort-Object)
if (($actual -join '|') -cne (($expected | Sort-Object) -join '|')) { throw "Public package closure differs: $($actual -join ', ')" }
$cachedPath = Join-Path (Join-Path $work 'packages') "$normalizedId/$normalizedVersion/$normalizedId.$normalizedVersion.nupkg"
if ((Get-FileHash -LiteralPath $cachedPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $publicHash) {
    throw "Restored $packageId bytes differ from downloaded public package."
}
foreach ($dependency in $manifest.dependencies) {
    $id = ([string]$dependency.id).ToLowerInvariant()
    $dependencyVersion = ([string]$dependency.version).ToLowerInvariant()
    $cachedDependency = Join-Path (Join-Path $work 'packages') "$id/$dependencyVersion/$id.$dependencyVersion.nupkg"
    if ((Get-FileHash -LiteralPath $cachedDependency -Algorithm SHA256).Hash.ToLowerInvariant() -cne $dependency.sha256) {
        throw "Restored dependency bytes differ from frozen public package: $($dependency.id)"
    }
}
[ordered]@{
    id = $packageId; version = $Version; sourceRevision = $manifest.sourceRevision
    candidateSha256 = $candidateHash; publishedSha256 = $publicHash; publicUrl = $url
    resolvedPackages = $actual
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $work 'published-check.json') -Encoding utf8NoBOM
Write-Host "Public $packageId/$Version verified from nuget.org: $publicHash"
