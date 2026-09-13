[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[a-z0-9]+([.-][a-z0-9]+)*)?$')][string]$Version,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$feed = [IO.Path]::GetFullPath($OutputDirectory)
$repositoryUrl = 'https://github.com/Atelia-org/atelia-storage'
# The production package list and pack algorithm have one owner: this script.
$projects = @('Primitives', 'Data', 'Rbf', 'RbfSegmentStore', 'EventJournal')
function Git-Value([string[]]$Arguments) {
    $value = & git -C $repo @Arguments
    if ($LASTEXITCODE -ne 0) { throw "git failed: $Arguments" }
    return ($value -join "`n").Trim()
}
function Assert-Clean {
    if (Git-Value @('status', '--porcelain', '--untracked-files=normal')) {
        throw 'Pack requires a clean committed source tree. Commit changes and use an ignored or external output directory.'
    }
}
Assert-Clean
$revision = Git-Value @('rev-parse', 'HEAD')
$origin = (Git-Value @('remote', 'get-url', 'origin')) -replace '\.git$', ''
if ($origin -cne $repositoryUrl) { throw "origin must be $repositoryUrl (optional .git suffix) for reproducible Source Link metadata." }
$manifestPath = Join-Path $feed "manifest.$Version.json"
if (Test-Path -LiteralPath $manifestPath) {
    $previous = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($previous.schemaVersion -ne 1 -or $previous.version -cne $Version -or
        $previous.sourceRevision -ne $revision -or $previous.repositoryUrl -ne $repositoryUrl -or
        @($previous.packages).Count -ne $projects.Count) { throw 'Existing version has different provenance. Choose a new version.' }
    foreach ($name in $projects) {
        $package = @($previous.packages | Where-Object id -CEQ "Atelia.$name")
        if ($package.Count -ne 1) { throw "Manifest package mismatch: $name" }
        foreach ($pair in @(@('file', 'sha256', 'nupkg'), @('symbolsFile', 'symbolsSha256', 'snupkg'))) {
            $filename = "Atelia.$name.$Version.$($pair[2])"
            if ($package[0].($pair[0]) -cne $filename) { throw "Unexpected manifest filename: $name" }
            $path = Join-Path $feed $filename
            if (!(Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $package[0].($pair[1])) {
                throw "Existing immutable package missing or changed: $filename"
            }
        }
    }
    Write-Host "Reusing verified immutable package set $Version from $revision"
    Write-Output $manifestPath
    return
}
foreach ($name in $projects) {
    foreach ($extension in @('nupkg', 'snupkg')) {
        if (Test-Path -LiteralPath (Join-Path $feed "Atelia.$name.$Version.$extension")) {
            throw "Version $Version already has package files without a complete manifest. Choose a new version."
        }
    }
}
New-Item -ItemType Directory -Path $feed -Force | Out-Null
$stage = Join-Path $feed ('.pack-' + $Version + '-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
$packages = @()
foreach ($name in $projects) {
    # PackageVersion is independent of the existing 1.0.0.0 assembly/file identity.
    & dotnet pack (Join-Path $repo "src/$name/$name.csproj") -c Release -o $stage "-p:PackageVersion=$Version" "-p:RepositoryCommit=$revision" '-p:ContinuousIntegrationBuild=true' | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet pack failed: $name (partial output retained at $stage)" }
    $file = "Atelia.$name.$Version.nupkg"
    $symbols = "Atelia.$name.$Version.snupkg"
    $packages += [ordered]@{
        id = "Atelia.$name"; file = $file
        sha256 = (Get-FileHash -LiteralPath (Join-Path $stage $file) -Algorithm SHA256).Hash.ToLowerInvariant()
        symbolsFile = $symbols
        symbolsSha256 = (Get-FileHash -LiteralPath (Join-Path $stage $symbols) -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}
Assert-Clean
if ((Git-Value @('rev-parse', 'HEAD')) -ne $revision) { throw 'Source commit changed during pack.' }
foreach ($package in $packages) {
    foreach ($file in @($package.file, $package.symbolsFile)) {
        Move-Item -LiteralPath (Join-Path $stage $file) -Destination (Join-Path $feed $file) -ErrorAction Stop
    }
}
[ordered]@{ schemaVersion = 1; version = $Version; sourceRevision = $revision; repositoryUrl = $repositoryUrl; packages = $packages } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
Write-Host "Packed $($packages.Count) libraries from $revision. No remote publication was performed."
Write-Output $manifestPath
