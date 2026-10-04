[CmdletBinding()]
param([string]$EvidenceDirectory = (Join-Path ([IO.Path]::GetTempPath()) ('storage-package-entry-mock-' + [Guid]::NewGuid().ToString('N'))))
$ErrorActionPreference = 'Stop'
$entry = Join-Path $PSScriptRoot 'Test-Package.ps1'
$pack = Join-Path $PSScriptRoot 'Pack.ps1'
$verify = Join-Path $PSScriptRoot 'Verify-Published.ps1'
foreach ($script in @($entry, $pack, $verify)) {
    $tokens = $null
    $parseErrors = $null
    [void][Management.Automation.Language.Parser]::ParseFile($script, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
}
$evidence = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($EvidenceDirectory)
if (Test-Path -LiteralPath $evidence) { throw 'EvidenceDirectory must be fresh.' }
New-Item -ItemType Directory -Path $evidence | Out-Null
$feed = Join-Path $evidence 'synthetic-feed'
New-Item -ItemType Directory -Path $feed | Out-Null
$version = '0.2.0-dev.mock'
$revision = '1111111111111111111111111111111111111111'
$ids = @('Atelia.Primitives', 'Atelia.Data', 'Atelia.Rbf')
$manifest = [ordered]@{
    schemaVersion = 1; version = $version; sourceRevision = $revision
    repositoryUrl = 'https://github.com/Atelia-org/atelia-storage'
    sdkVersion = (Get-Content (Join-Path (Split-Path $PSScriptRoot -Parent) 'global.json') -Raw | ConvertFrom-Json).sdk.version
    packages = @($ids | ForEach-Object {
        $id = $_
        $record = [ordered]@{ id = $id }
        foreach ($kind in @('nupkg', 'snupkg')) {
            $filename = "$id.$version.$kind"
            $path = Join-Path $feed $filename
            'MOCK artifact; this is not a NuGet archive.' | Set-Content -LiteralPath $path -Encoding utf8NoBOM
            if ($kind -eq 'nupkg') { $record.file = $filename; $record.sha256 = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant() }
            else { $record.symbolsFile = $filename; $record.symbolsSha256 = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant() }
        }
        $record
    })
}
$manifestPath = Join-Path $feed "manifest.$version.json"
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
$global:StoragePackageEntryMock = @{
    Calls = [Collections.Generic.List[object]]::new(); GitCalls = 0; AssetsFailure = ''; FailSmoke = $false
    Revision = $revision; Version = $version; Ids = $ids; SdkVersion = $manifest.sdkVersion
}
$originalPackages = $env:NUGET_PACKAGES
$originalHttpCache = $env:NUGET_HTTP_CACHE_PATH
function Assert([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
# Only these functions stand in for external commands. No dotnet executable is invoked.
function git {
    $global:StoragePackageEntryMock.GitCalls++
    $global:LASTEXITCODE = 0
    if ($args -contains 'rev-parse') { return $global:StoragePackageEntryMock.Revision }
    if ($args -contains 'status') { return }
    if ($args -contains 'get-url') { return 'https://github.com/Atelia-org/atelia-storage.git' }
    throw "Unexpected mocked git command: $args"
}
function dotnet {
    $arguments = @($args)
    $global:StoragePackageEntryMock.Calls.Add([pscustomobject]@{
        arguments = $arguments; location = (Get-Location).Path
        packages = $env:NUGET_PACKAGES; httpCache = $env:NUGET_HTTP_CACHE_PATH
    })
    $global:LASTEXITCODE = 0
    if ($arguments[0] -eq '--version') { return $global:StoragePackageEntryMock.SdkVersion }
    if ($arguments[0] -eq 'restore' -and $arguments[1] -eq 'RbfPackageSmoke/RbfPackageSmoke.csproj') {
        $version = $global:StoragePackageEntryMock.Version
        $project = 'RbfPackageSmoke'
        $libraries = [ordered]@{}
        foreach ($id in $global:StoragePackageEntryMock.Ids) { $libraries["$id/$version"] = @{ type = 'package' } }
        switch ($global:StoragePackageEntryMock.AssetsFailure) {
            'version' { $libraries.Remove("Atelia.Rbf/$version"); $libraries['Atelia.Rbf/0.0.0'] = @{ type = 'package' } }
            'extra' { $libraries["Atelia.EventJournal/$version"] = @{ type = 'package' } }
            'project' { $libraries["Atelia.Rbf/$version"] = @{ type = 'project' } }
            'missing' { $libraries.Remove("Atelia.Data/$version") }
        }
        $target = [ordered]@{}
        foreach ($key in $libraries.Keys) { $target[$key] = $libraries[$key] }
        if ($global:StoragePackageEntryMock.AssetsFailure -eq 'target') { $target.Remove("Atelia.Rbf/$version") }
        $directId = if ($global:StoragePackageEntryMock.AssetsFailure -eq 'direct') { 'Atelia.Data' } else { 'Atelia.Rbf' }
        $targets = if ($global:StoragePackageEntryMock.AssetsFailure -eq 'framework') { @{} } else { @{ 'net10.0' = $target } }
        $assets = [ordered]@{
            libraries = $libraries; targets = $targets
            project = @{ frameworks = @{ 'net10.0' = @{ dependencies = @{ $directId = @{ target = 'Package'; version = "[$version, )" } } } } }
        }
        New-Item -ItemType Directory -Path (Join-Path $project 'obj') -Force | Out-Null
        $assets | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $project 'obj/project.assets.json') -Encoding utf8NoBOM
    }
    if ($arguments[0] -eq 'run' -and $arguments -contains 'RbfPackageSmoke/RbfPackageSmoke.csproj' -and $global:StoragePackageEntryMock.FailSmoke) {
        $global:LASTEXITCODE = 1
    }
    "MOCK ONLY: dotnet $($arguments -join ' ')"
}
function Assert-RestoredEnvironment([string]$Name) {
    Assert ($env:NUGET_PACKAGES -eq $originalPackages -and $env:NUGET_HTTP_CACHE_PATH -eq $originalHttpCache) "$Name did not restore environment."
}
function Run-Case([string]$Name, [bool]$ExplicitAll) {
    $global:StoragePackageEntryMock.Calls.Clear()
    $work = Join-Path $evidence $Name
    if ($ExplicitAll) { & $entry -Project All -Version $version -FeedDirectory $feed -WorkDirectory $work }
    else { & $entry -Version $version -FeedDirectory $feed -WorkDirectory $work }
    Assert ($global:StoragePackageEntryMock.Calls.Count -eq 4) "$Name command count changed."
    foreach ($call in $global:StoragePackageEntryMock.Calls) {
        Assert ($call.location -eq $work) "$Name ran outside isolated workspace."
        Assert ($call.packages -eq (Join-Path $work 'packages')) "$Name used a shared package cache."
        Assert ($call.httpCache -eq (Join-Path $work 'http-cache')) "$Name used a shared HTTP cache."
        if ($call.arguments[0] -eq 'restore') {
            Assert ($call.arguments -contains '--configfile' -and $call.arguments -contains 'NuGet.Config') "$Name restore missed explicit config."
        }
        if ($call.arguments -contains 'RbfPackageSmoke/RbfPackageSmoke.csproj') {
            Assert ($call.arguments -contains "-p:StoragePackageVersion=$version") "$Name lost candidate version."
        }
    }
    $metadata = @($global:StoragePackageEntryMock.Calls | Where-Object { $_.arguments[0] -eq 'run' -and $_.arguments -contains 'PackageMetadataCheck/PackageMetadataCheck.csproj' })
    Assert ($metadata.Count -eq 1 -and $metadata[0].arguments -contains $manifestPath) "$Name missed original All metadata verification."
    Assert (@((Get-Content (Join-Path $work 'verified-packages.json') -Raw | ConvertFrom-Json).packages).Count -eq 3) "$Name evidence did not retain three packages."
    Assert (!(Test-Path (Join-Path $work 'EventJournalSmoke')) -and !(Test-Path (Join-Path $work 'RbfSegmentStoreSmoke'))) "$Name copied a legacy consumer."
    Assert (Test-Path (Join-Path $work 'logs/rbf-assets.log')) "$Name failed to retain assets check."
    Assert-RestoredEnvironment $Name
}
function Assert-ParameterRejection([string]$Name, [scriptblock]$Invoke, [string]$UnexpectedDirectory) {
    $before = $global:StoragePackageEntryMock.GitCalls
    $global:StoragePackageEntryMock.Calls.Clear()
    $message = ''
    try { & $Invoke } catch { $message = $_.Exception.Message }
    Assert ($message.Length -gt 0) "$Name did not reject the legacy parameter."
    Assert ($global:StoragePackageEntryMock.GitCalls -eq $before -and $global:StoragePackageEntryMock.Calls.Count -eq 0) "$Name called external commands before rejection."
    Assert (!(Test-Path -LiteralPath $UnexpectedDirectory)) "$Name created a directory before rejection."
    Assert-RestoredEnvironment $Name
}
try {
    Run-Case 'all-default' $false
    Run-Case 'all-explicit' $true
    foreach ($project in @('EventJournal', 'RbfSegmentStore')) {
        $path = Join-Path $evidence "reject-pack-$project"
        Assert-ParameterRejection "pack-$project" { & $pack -Project $project -Version $version -OutputDirectory $path } $path
        $path = Join-Path $evidence "reject-test-$project"
        Assert-ParameterRejection "test-$project" { & $entry -Project $project -Version $version -FeedDirectory 'missing-feed' -WorkDirectory $path } $path
        $path = Join-Path $evidence "reject-verify-$project"
        Assert-ParameterRejection "verify-$project" { & $verify -Project $project -Version $version -FeedDirectory 'missing-feed' -WorkDirectory $path } $path
    }
    foreach ($enabled in @($true, $false)) {
        $path = Join-Path $evidence "reject-additional-$enabled"
        Assert-ParameterRejection "additional-$enabled" { & $entry -Version $version -FeedDirectory 'missing-feed' -WorkDirectory $path -AdditionalSegmentSmoke:$enabled } $path
    }
    foreach ($dependencies in @(@{}, $null)) {
        $path = Join-Path $evidence ('reject-dependencies-' + [Guid]::NewGuid().ToString('N'))
        Assert-ParameterRejection 'dependencies' { & $pack -Version $version -OutputDirectory $path -DependencyVersions $dependencies } $path
    }
    # Pack's pre-existing immutable schema 1 reuse path must bind all three IDs/hashes, without packing.
    $global:StoragePackageEntryMock.Calls.Clear()
    & $pack -Project All -Version $version -OutputDirectory $feed | Out-Host
    Assert ($global:StoragePackageEntryMock.Calls.Count -eq 1 -and $global:StoragePackageEntryMock.Calls[0].arguments[0] -eq '--version') 'Immutable core reuse unexpectedly attempted restore/pack.'
    foreach ($failure in @('version', 'extra', 'project', 'missing', 'target', 'direct', 'framework')) {
        $global:StoragePackageEntryMock.AssetsFailure = $failure
        $global:StoragePackageEntryMock.Calls.Clear()
        $message = ''
        try { & $entry -Version $version -FeedDirectory $feed -WorkDirectory (Join-Path $evidence "bad-assets-$failure") }
        catch { $message = $_.Exception.Message }
        Assert ($message -like '*Rbf*') "$failure assets did not fail the closure guard."
        Assert ($global:StoragePackageEntryMock.Calls.Count -eq 1) "$failure assets ran smoke after failed guard."
        Assert-RestoredEnvironment $failure
    }
    $global:StoragePackageEntryMock.AssetsFailure = ''
    foreach ($failure in @('version', 'filename', 'missing', 'extra', 'schema')) {
        $broken = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        switch ($failure) {
            'version' { $broken.version = '0.0.0' }
            'filename' { ($broken.packages | Where-Object id -CEQ 'Atelia.Rbf').file = 'Atelia.Rbf.0.0.0.nupkg' }
            'missing' { $broken.packages = @($broken.packages | Where-Object id -CNE 'Atelia.Rbf') }
            'extra' { $broken.packages += [pscustomobject]@{ id = 'Atelia.EventJournal'; file = 'extra.nupkg' } }
            'schema' { $broken.schemaVersion = 2 }
        }
        $broken | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
        $path = Join-Path $evidence "bad-manifest-$failure"
        $before = $global:StoragePackageEntryMock.GitCalls
        $global:StoragePackageEntryMock.Calls.Clear()
        $message = ''
        try { & $entry -Version $version -FeedDirectory $feed -WorkDirectory $path } catch { $message = $_.Exception.Message }
        Assert ($message -like '*All manifest*') "$failure manifest did not fail candidate binding."
        Assert ($global:StoragePackageEntryMock.Calls.Count -eq 0 -and $global:StoragePackageEntryMock.GitCalls -eq $before -and !(Test-Path $path)) "$failure manifest rejection had side effects."
        Assert-RestoredEnvironment $failure
        $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
    }
    $global:StoragePackageEntryMock.FailSmoke = $true
    $global:StoragePackageEntryMock.Calls.Clear()
    $message = ''
    try { & $entry -Version $version -FeedDirectory $feed -WorkDirectory (Join-Path $evidence 'failed-smoke') } catch { $message = $_.Exception.Message }
    Assert ($message -like '*dotnet failed*' -and $global:StoragePackageEntryMock.Calls.Count -eq 2) 'Failed smoke did not stop metadata execution.'
    Assert-RestoredEnvironment 'failed-smoke'
    'Passed: parser, default/explicit three-package All, ten legacy parameter rejections, immutable three-package reuse, seven malformed assets, five malformed manifests, smoke failure environment restoration. All git/dotnet commands mocked; no package consumption proven.' |
        Set-Content -LiteralPath (Join-Path $evidence 'result.txt') -Encoding utf8NoBOM
    Write-Host "MOCK entrypoint tests passed. Evidence: $evidence"
}
finally {
    Remove-Variable -Name StoragePackageEntryMock -Scope Global
    $env:NUGET_PACKAGES = $originalPackages
    $env:NUGET_HTTP_CACHE_PATH = $originalHttpCache
}
