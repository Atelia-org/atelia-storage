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
if (Test-Path -LiteralPath $work) { throw 'WorkDirectory must be new.' }
if ($work -eq $repo -or $work.StartsWith($repo + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'WorkDirectory must be outside the source repository.'
}
$manifestPath = Join-Path $feed "manifest.$Version.json"
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$ids = @(Assert-PackageSmokeManifest $manifest $Version $feed)
$verified = @()
# All frozen candidate artifacts were validated before making directories or downloading public packages.
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
    $candidateArchive = $null
    $publicArchive = $null
    try {
        $candidateArchive = [IO.Compression.ZipFile]::OpenRead($candidatePath)
        $publicArchive = [IO.Compression.ZipFile]::OpenRead($publicPath)
        $candidateEntries = [Collections.Generic.Dictionary[string, IO.Compression.ZipArchiveEntry]]::new([StringComparer]::Ordinal)
        $publicEntries = [Collections.Generic.Dictionary[string, IO.Compression.ZipArchiveEntry]]::new([StringComparer]::Ordinal)
        foreach ($entry in $candidateArchive.Entries) {
            if (!$candidateEntries.TryAdd($entry.FullName, $entry)) { throw "Duplicate candidate package entry: $($entry.FullName)" }
        }
        foreach ($entry in $publicArchive.Entries) {
            if (!$publicEntries.TryAdd($entry.FullName, $entry)) { throw "Duplicate public package entry: $($entry.FullName)" }
        }
        if ($candidateEntries.ContainsKey('.signature.p7s')) { throw "Candidate is already signed: $id" }
        if (!$publicEntries.ContainsKey('.signature.p7s') -or $publicEntries['.signature.p7s'].Length -le 0) {
            throw "Downloaded public package is not repository signed: $id"
        }
        if (!$publicEntries.ContainsKey("$id.nuspec")) { throw "Published package has no $id nuspec." }
        $specEntry = $publicEntries["$id.nuspec"]
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
            if (!$candidateEntries.ContainsKey($name) -or !$publicEntries.ContainsKey($name)) { throw "Missing package asset: $name" }
        }
        # NuGet repository signing adds only .signature.p7s; existing entry payloads, including
        # OPC metadata, remain unchanged. Compare decompressed entries, independent of ZIP layout.
        if ($publicEntries.Count -ne $candidateEntries.Count + 1) { throw "Published package entry set differs from candidate: $id" }
        foreach ($name in $candidateEntries.Keys) {
            if (!$publicEntries.ContainsKey($name)) { throw "Published package entry set differs from candidate: $id" }
            $before = $candidateEntries[$name]
            $after = $publicEntries[$name]
            $beforeStream = $null
            $afterStream = $null
            try {
                $beforeStream = $before.Open()
                $afterStream = $after.Open()
                $beforeHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($beforeStream))
                $afterHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($afterStream))
                if ($beforeHash -cne $afterHash) { throw "Published package asset differs from candidate: $name" }
            }
            finally {
                try { if ($null -ne $beforeStream) { $beforeStream.Dispose() } }
                finally { if ($null -ne $afterStream) { $afterStream.Dispose() } }
            }
        }
    }
    finally {
        try { if ($null -ne $candidateArchive) { $candidateArchive.Dispose() } }
        finally { if ($null -ne $publicArchive) { $publicArchive.Dispose() } }
    }
    $verified += [ordered]@{
        id = $id; version = $Version; sourceRevision = $manifest.sourceRevision
        candidateSha256 = $candidate.sha256; publishedSha256 = $publicHash; publicUrl = $url
    }
}
# These separate consumers have no local feed or inherited MSBuild settings. Each proves its own public closure.
foreach ($file in @('Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props')) {
    '<Project />' | Set-Content -LiteralPath (Join-Path $work $file) -Encoding utf8NoBOM
}
Copy-Item -LiteralPath (Join-Path $repo 'global.json') -Destination $work
foreach ($consumer in $consumers) {
    Copy-PackageSmokeSources (Join-Path $repo "examples/$($consumer.Name)") $work
}
@'
<?xml version="1.0" encoding="utf-8"?>
<configuration><packageSources><clear /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources><fallbackPackageFolders><clear /></fallbackPackageFolders></configuration>
'@ | Set-Content -LiteralPath (Join-Path $work 'NuGet.Config') -Encoding utf8NoBOM
$logs = Join-Path $work 'logs'
New-Item -ItemType Directory -Path $logs | Out-Null
function Invoke-LoggedDotnet([string[]]$Arguments, [string]$LogName) {
    & dotnet @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $logs $LogName) | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Public dotnet failed; see $logs/$LogName" }
}
$consumerReports = @()
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
            $consumerReports += [ordered]@{ directReference = $consumer.DirectId; resolvedPackages = $resolved }
            "Verified $($consumer.DirectId) exact direct reference and $($resolved.Count)-package public closure." |
                Set-Content -LiteralPath (Join-Path $logs $consumer.AssetsLog) -Encoding utf8NoBOM
            $runArguments = @('run', '--project', $projectPath, '-c', 'Release', '--no-restore', "-p:StoragePackageVersion=$Version")
            if ($consumer.DataDirectory) { $runArguments += @('--', (Join-Path $work $consumer.DataDirectory)) }
            Invoke-LoggedDotnet $runArguments "$($consumer.LogPrefix)-smoke.log"
        }
    }
    finally { Pop-Location }
}
finally {
    $env:NUGET_PACKAGES = $previousPackages
    $env:NUGET_HTTP_CACHE_PATH = $previousHttpCache
}
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
    packages = $verified
    consumers = $consumerReports
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $work 'published-check.json') -Encoding utf8NoBOM
Write-Host "Public six-package candidate $Version verified from nuget.org through four separate consumers."
