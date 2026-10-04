[CmdletBinding()]
param(
    [ValidateSet('All')][string]$Project = 'All',
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$FeedDirectory,
    [Parameter(Mandatory)][string]$WorkDirectory
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$feed = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($FeedDirectory)
$work = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($WorkDirectory)
if (Test-Path -LiteralPath $work) { throw 'WorkDirectory must be new.' }
if ($work -eq $repo -or $work.StartsWith($repo + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'WorkDirectory must be outside the source repository.'
}
$manifestPath = Join-Path $feed "manifest.$Version.json"
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$ids = @('Atelia.Primitives', 'Atelia.Data', 'Atelia.Rbf')
if ($manifest.schemaVersion -ne 1 -or $manifest.version -cne $Version -or @($manifest.packages).Count -ne $ids.Count) {
    throw 'Expected the requested three-package schema 1 candidate.'
}
$verified = @()
# Validate every frozen candidate before making directories or downloading public packages.
foreach ($id in $ids) {
    $records = @($manifest.packages | Where-Object id -CEQ $id)
    if ($records.Count -ne 1 -or $records[0].file -cne "$id.$Version.nupkg") { throw "Unexpected candidate: $id/$Version" }
    $candidate = $records[0]
    $path = Join-Path $feed $candidate.file
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -cne $candidate.sha256) { throw "Frozen candidate changed before public verification: $id" }
}
[void][IO.Directory]::CreateDirectory($work)
foreach ($id in $ids) {
    $candidate = @($manifest.packages | Where-Object id -CEQ $id)[0]
    $candidatePath = Join-Path $feed $candidate.file
    $publicPath = Join-Path $work "$id.$Version.nupkg"
    $normalizedVersion = $Version.ToLowerInvariant()
    $normalizedId = $id.ToLowerInvariant()
    $url = "https://api.nuget.org/v3-flatcontainer/$normalizedId/$normalizedVersion/$normalizedId.$normalizedVersion.nupkg"
    $downloaded = $false
    for ($attempt = 1; $attempt -le 60; $attempt++) {
        try {
            Invoke-WebRequest -Uri $url -OutFile $publicPath -TimeoutSec 30 | Out-Null
            $downloaded = $true
            break
        }
        catch {
            if ($attempt -eq 60) { throw "Published package did not become downloadable: $url" }
            if (Test-Path -LiteralPath $publicPath) { Remove-Item -LiteralPath $publicPath }
            Start-Sleep -Seconds 15
        }
    }
    if (!$downloaded) { throw 'Published package download failed.' }
    $publicHash = (Get-FileHash -LiteralPath $publicPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $candidateArchive = [IO.Compression.ZipFile]::OpenRead($candidatePath)
    $publicArchive = [IO.Compression.ZipFile]::OpenRead($publicPath)
    try {
        if ($candidateArchive.GetEntry('.signature.p7s')) { throw "Candidate is already signed: $id" }
        $signature = $publicArchive.GetEntry('.signature.p7s')
        if (!$signature -or $signature.Length -le 0) { throw "Downloaded public package is not repository signed: $id" }
        $specEntry = $publicArchive.GetEntry("$id.nuspec")
        if (!$specEntry) { throw "Published package has no $id nuspec." }
        $reader = [IO.StreamReader]::new($specEntry.Open())
        try { [xml]$spec = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $metadata = $spec.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
        if ($metadata.SelectSingleNode('*[local-name()="id"]').InnerText -cne $id -or
            $metadata.SelectSingleNode('*[local-name()="version"]').InnerText -cne $Version) { throw "Published package identity differs: $id" }
        $repository = $metadata.SelectSingleNode('*[local-name()="repository"]')
        if (!$repository -or $repository.GetAttribute('commit') -cne $manifest.sourceRevision -or
            $repository.GetAttribute('url') -cne $manifest.repositoryUrl -or $repository.GetAttribute('type') -cne 'git') {
            throw "Published package source differs from candidate: $id"
        }
        foreach ($name in @("$id.nuspec", 'LICENSE', 'README.md', "lib/net10.0/$id.dll", "lib/net10.0/$id.xml")) {
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
    $verified += [ordered]@{
        id = $id; version = $Version; sourceRevision = $manifest.sourceRevision
        candidateSha256 = $candidate.sha256; publishedSha256 = $publicHash; publicUrl = $url
    }
}
# This consumer has no local feed or inherited MSBuild settings. It proves the public closure.
foreach ($file in @('Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props')) {
    '<Project />' | Set-Content -LiteralPath (Join-Path $work $file) -Encoding utf8NoBOM
}
Copy-Item -LiteralPath (Join-Path $repo 'global.json') -Destination $work
$smokeProject = 'RbfPackageSmoke'
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
        & dotnet run --project "$smokeProject/$smokeProject.csproj" -c Release --no-restore "-p:StoragePackageVersion=$Version" -- (Join-Path $work 'rbf-files')
        if ($LASTEXITCODE -ne 0) { throw 'Public Rbf package smoke failed.' }
    }
    finally { Pop-Location }
}
finally {
    $env:NUGET_PACKAGES = $previousPackages
    $env:NUGET_HTTP_CACHE_PATH = $previousHttpCache
}
$assets = Get-Content -LiteralPath (Join-Path $work "$smokeProject/obj/project.assets.json") -Raw | ConvertFrom-Json -AsHashtable
if (@($assets.libraries.Values | Where-Object { $_.type -cne 'package' }).Count) { throw 'Public consumer contains a non-package reference.' }
$expected = @($ids | ForEach-Object { "$_/$Version" })
$actual = @($assets.libraries.Keys | Where-Object { $_ -like 'Atelia.*/*' } | Sort-Object)
if (($actual -join '|') -cne (($expected | Sort-Object) -join '|')) { throw "Public package closure differs: $($actual -join ', ')" }
$frameworks = @($assets.project.frameworks.Values)
if ($frameworks.Count -ne 1) { throw 'Public Rbf smoke must have one target framework.' }
$direct = @($frameworks[0].dependencies.Keys | Where-Object { $_ -like 'Atelia.*' })
if ($direct.Count -ne 1 -or $direct[0] -cne 'Atelia.Rbf') { throw 'Public smoke must directly reference only Atelia.Rbf.' }
foreach ($package in $verified) {
    $id = ([string]$package.id).ToLowerInvariant()
    $packageVersion = $Version.ToLowerInvariant()
    $cachedPath = Join-Path (Join-Path $work 'packages') "$id/$packageVersion/$id.$packageVersion.nupkg"
    if ((Get-FileHash -LiteralPath $cachedPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $package.publishedSha256) {
        throw "Restored package bytes differ from downloaded public package: $($package.id)"
    }
}
[ordered]@{
    schemaVersion = 1; version = $Version; sourceRevision = $manifest.sourceRevision
    packages = $verified; resolvedPackages = $actual
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $work 'published-check.json') -Encoding utf8NoBOM
Write-Host "Public Primitives/Data/Rbf $Version verified from nuget.org."
