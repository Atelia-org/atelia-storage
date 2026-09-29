[CmdletBinding()]
param([string]$EvidenceDirectory = (Join-Path ([IO.Path]::GetTempPath()) ('storage-package-entry-mock-' + [Guid]::NewGuid().ToString('N'))))
$ErrorActionPreference = 'Stop'
$entry = Join-Path $PSScriptRoot 'Test-Package.ps1'
$tokens = $null
$parseErrors = $null
[void][Management.Automation.Language.Parser]::ParseFile($entry, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
$evidence = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($EvidenceDirectory)
if (Test-Path -LiteralPath $evidence) { throw 'EvidenceDirectory must be fresh.' }
New-Item -ItemType Directory -Path $evidence | Out-Null
$feed = Join-Path $evidence 'synthetic-feed'
New-Item -ItemType Directory -Path $feed | Out-Null
$version = '0.2.0-dev.mock'
$revision = '1111111111111111111111111111111111111111'
$ids = @('Atelia.Primitives', 'Atelia.Data', 'Atelia.Rbf', 'Atelia.RbfSegmentStore', 'Atelia.EventJournal')
$manifest = [ordered]@{
    schemaVersion = 1; version = $version; sourceRevision = $revision
    packages = @($ids | ForEach-Object { [ordered]@{ id = $_; file = "$_.${version}.nupkg" } })
}
$manifestPath = Join-Path $feed "manifest.$version.json"
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
$global:StoragePackageEntryMock = @{ Calls = [Collections.Generic.List[object]]::new(); GitCalls = 0; AssetsFailure = ''; Revision = $revision; Version = $version; Ids = $ids }
$originalPackages = $env:NUGET_PACKAGES
$originalHttpCache = $env:NUGET_HTTP_CACHE_PATH
function Assert([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
# Only these functions stand in for external commands. No dotnet executable is invoked.
function git {
    $global:StoragePackageEntryMock.GitCalls++
    $global:LASTEXITCODE = 0
    if ($args -contains 'rev-parse') { return $global:StoragePackageEntryMock.Revision }
    if ($args -contains 'status') { return }
    throw "Unexpected mocked git command: $args"
}
function dotnet {
    $ids = $global:StoragePackageEntryMock.Ids
    $version = $global:StoragePackageEntryMock.Version
    $arguments = @($args)
    $global:StoragePackageEntryMock.Calls.Add([pscustomobject]@{
        arguments = $arguments; location = (Get-Location).Path
        packages = $env:NUGET_PACKAGES; httpCache = $env:NUGET_HTTP_CACHE_PATH
    })
    $global:LASTEXITCODE = 0
    if ($arguments[0] -eq 'restore' -and $arguments[1] -like '*Smoke/*') {
        $project = Split-Path $arguments[1] -Parent
        $libraries = [ordered]@{}
        $closure = if ($project -eq 'RbfSegmentStoreSmoke') { @($ids | Where-Object { $_ -cne 'Atelia.EventJournal' }) } else { $ids }
        foreach ($id in $closure) { $libraries["$id/$version"] = @{ type = 'package' } }
        if ($project -eq 'RbfSegmentStoreSmoke') {
            switch ($global:StoragePackageEntryMock.AssetsFailure) {
                'version' { $libraries.Remove("Atelia.Rbf/$version"); $libraries['Atelia.Rbf/0.0.0'] = @{ type = 'package' } }
                'extra' { $libraries["Atelia.EventJournal/$version"] = @{ type = 'package' } }
                'project' { $libraries["Atelia.Rbf/$version"] = @{ type = 'project' } }
                'missing' { $libraries.Remove("Atelia.Data/$version") }
            }
        }
        $directId = if ($project -eq 'RbfSegmentStoreSmoke') { 'Atelia.RbfSegmentStore' } else { 'Atelia.EventJournal' }
        $assets = [ordered]@{
            libraries = $libraries; targets = @{ 'net10.0' = $libraries }
            project = @{ frameworks = @{ 'net10.0' = @{ dependencies = @{ $directId = @{ target = 'Package'; version = "[$version, )" } } } } }
        }
        New-Item -ItemType Directory -Path (Join-Path $project 'obj') -Force | Out-Null
        $assets | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $project 'obj/project.assets.json') -Encoding utf8NoBOM
    }
    "MOCK ONLY: dotnet $($arguments -join ' ')"
}
function Run-Case([string]$Name, [bool]$Additional) {
    $global:StoragePackageEntryMock.Calls.Clear()
    $work = Join-Path $evidence $Name
    if ($Additional) { & $entry -Version $version -FeedDirectory $feed -WorkDirectory $work -AdditionalSegmentSmoke }
    else { & $entry -Version $version -FeedDirectory $feed -WorkDirectory $work }
    Assert ($global:StoragePackageEntryMock.Calls.Count -eq $(if ($Additional) { 6 } else { 4 })) "$Name command count changed."
    foreach ($call in $global:StoragePackageEntryMock.Calls) {
        Assert ($call.location -eq $work) "$Name ran outside isolated workspace."
        Assert ($call.packages -eq (Join-Path $work 'packages')) "$Name used a shared package cache."
        Assert ($call.httpCache -eq (Join-Path $work 'http-cache')) "$Name used a shared HTTP cache."
        if ($call.arguments[0] -eq 'restore') {
            Assert ($call.arguments -contains '--configfile' -and $call.arguments -contains 'NuGet.Config') "$Name restore missed explicit config."
        }
        if (@($call.arguments | Where-Object { $_ -like '*Smoke/*Smoke.csproj' }).Count -ne 0) {
            Assert ($call.arguments -contains "-p:StoragePackageVersion=$version") "$Name lost candidate version."
        }
    }
    $metadata = @($global:StoragePackageEntryMock.Calls | Where-Object { $_.arguments[0] -eq 'run' -and $_.arguments -contains 'PackageMetadataCheck/PackageMetadataCheck.csproj' })
    Assert ($metadata.Count -eq 1 -and $metadata[0].arguments -contains $manifestPath) "$Name altered original All metadata verification."
    Assert ((Get-Content -LiteralPath (Join-Path $work 'verified-packages.json') -Raw | ConvertFrom-Json).schemaVersion -eq 1) "$Name converted the All manifest schema."
    Assert ((Test-Path -LiteralPath (Join-Path $work 'RbfSegmentStoreSmoke')) -eq $Additional) "$Name copied the extra smoke unexpectedly."
    if ($Additional) {
        Assert (Test-Path -LiteralPath (Join-Path $work 'logs/segment-smoke.log')) "$Name failed to retain direct smoke log."
        Assert (Test-Path -LiteralPath (Join-Path $work 'logs/segment-assets.log')) "$Name failed to retain assets check log."
    }
    Assert ($env:NUGET_PACKAGES -eq $originalPackages -and $env:NUGET_HTTP_CACHE_PATH -eq $originalHttpCache) "$Name did not restore environment."
}
try {
    Run-Case 'all-original' $false
    Run-Case 'all-additional' $true
    foreach ($project in @('EventJournal', 'RbfSegmentStore')) {
        foreach ($enabled in @($true, $false)) {
            $work = Join-Path $evidence "invalid-$project-$enabled"
            $before = $global:StoragePackageEntryMock.GitCalls
            $errorMessage = ''
            try { & $entry -Project $project -Version $version -FeedDirectory 'missing-feed' -WorkDirectory $work -AdditionalSegmentSmoke:$enabled }
            catch { $errorMessage = $_.Exception.Message }
            Assert ($errorMessage -like '*only supported with Project=All*') "Selective $project did not reject the supplied switch."
            Assert ($global:StoragePackageEntryMock.GitCalls -eq $before -and !(Test-Path -LiteralPath $work)) "Selective $project produced side effects before rejection."
        }
    }
    foreach ($failure in @('version', 'extra', 'project', 'missing')) {
        $global:StoragePackageEntryMock.AssetsFailure = $failure
        $global:StoragePackageEntryMock.Calls.Clear()
        $errorMessage = ''
        try { & $entry -Version $version -FeedDirectory $feed -WorkDirectory (Join-Path $evidence "bad-assets-$failure") -AdditionalSegmentSmoke }
        catch { $errorMessage = $_.Exception.Message }
        Assert ($errorMessage -like '*Direct SegmentStore*') "$failure assets did not fail the closure guard."
        Assert ($global:StoragePackageEntryMock.Calls.Count -eq 5) "$failure assets ran direct smoke after a failed guard."
        Assert ($env:NUGET_PACKAGES -eq $originalPackages -and $env:NUGET_HTTP_CACHE_PATH -eq $originalHttpCache) "$failure failure did not restore environment."
    }
    $global:StoragePackageEntryMock.AssetsFailure = ''
    foreach ($failure in @('version', 'filename', 'missing')) {
        $broken = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        switch ($failure) {
            'version' { $broken.version = '0.0.0' }
            'filename' { ($broken.packages | Where-Object id -CEQ 'Atelia.Rbf').file = 'Atelia.Rbf.0.0.0.nupkg' }
            'missing' { $broken.packages = @($broken.packages | Where-Object id -CNE 'Atelia.Rbf') }
        }
        $broken | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
        $global:StoragePackageEntryMock.Calls.Clear()
        $errorMessage = ''
        try { & $entry -Version $version -FeedDirectory $feed -WorkDirectory (Join-Path $evidence "bad-manifest-$failure") -AdditionalSegmentSmoke }
        catch { $errorMessage = $_.Exception.Message }
        Assert ($errorMessage -like '*All manifest*') "$failure manifest did not fail candidate binding."
        Assert ($global:StoragePackageEntryMock.Calls.Count -eq 5) "$failure manifest ran direct smoke after a failed guard."
        Assert ($env:NUGET_PACKAGES -eq $originalPackages -and $env:NUGET_HTTP_CACHE_PATH -eq $originalHttpCache) "$failure manifest failure did not restore environment."
        $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
    }
    'Passed: PowerShell parser, All original/additional branches, four selective parameter cases, four malformed assets cases, three malformed manifest cases. All git/dotnet commands mocked; no package consumption proven.' |
        Set-Content -LiteralPath (Join-Path $evidence 'result.txt') -Encoding utf8NoBOM
    Write-Host "MOCK entrypoint tests passed. Evidence: $evidence"
}
finally {
    Remove-Variable -Name StoragePackageEntryMock -Scope Global
    $env:NUGET_PACKAGES = $originalPackages
    $env:NUGET_HTTP_CACHE_PATH = $originalHttpCache
}
