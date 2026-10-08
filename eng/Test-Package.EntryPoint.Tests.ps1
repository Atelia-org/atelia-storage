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
$ids = @('Atelia.Primitives', 'Atelia.Data', 'Atelia.Rbf', 'Atelia.Binary')
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
    Calls = [Collections.Generic.List[object]]::new(); GitCalls = 0; AssetsFailure = ''; FailureConsumer = 'RbfPackageSmoke'; FailSmoke = ''
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
    if ($arguments[0] -eq 'restore' -and $arguments[1] -in @('RbfPackageSmoke/RbfPackageSmoke.csproj', 'BinaryPackageSmoke/BinaryPackageSmoke.csproj')) {
        $version = $global:StoragePackageEntryMock.Version
        $project = $arguments[1].Split('/')[0]
        $directId = if ($project -eq 'RbfPackageSmoke') { 'Atelia.Rbf' } else { 'Atelia.Binary' }
        $closureIds = @(if ($project -eq 'RbfPackageSmoke') { 'Atelia.Primitives'; 'Atelia.Data'; 'Atelia.Rbf' } else { 'Atelia.Binary' })
        $failure = if ($project -eq $global:StoragePackageEntryMock.FailureConsumer) { $global:StoragePackageEntryMock.AssetsFailure } else { '' }
        $libraries = [ordered]@{}
        foreach ($id in $closureIds) { $libraries["$id/$version"] = @{ type = 'package' } }
        if ($project -eq 'BinaryPackageSmoke') { $libraries['K4os.Compression.LZ4/1.3.8'] = @{ type = 'package' } }
        switch ($failure) {
            'version' { $libraries.Remove("$directId/$version"); $libraries["$directId/0.0.0"] = @{ type = 'package' } }
            'extra' { $libraries["Atelia.EventJournal/$version"] = @{ type = 'package' } }
            'project' { $libraries["$directId/$version"] = @{ type = 'project' } }
            'missing' { $libraries.Remove("$directId/$version") }
            'libraries-empty' { $libraries.Clear() }
            'cross' { $crossId = if ($project -eq 'RbfPackageSmoke') { 'Atelia.Binary' } else { 'Atelia.Rbf' }; $libraries["$crossId/$version"] = @{ type = 'package' } }
            'external-extra' { $libraries['Unapproved.Codec/1.0.0'] = @{ type = 'package' } }
            'external-version' { $libraries.Remove('K4os.Compression.LZ4/1.3.8'); $libraries['K4os.Compression.LZ4/1.3.7'] = @{ type = 'package' } }
            'external-missing' { $libraries.Remove('K4os.Compression.LZ4/1.3.8') }
        }
        $target = [ordered]@{}
        foreach ($key in $libraries.Keys) { $target[$key] = $libraries[$key] }
        if ($failure -eq 'target') { $target.Remove("$directId/$version") }
        if ($failure -eq 'target-empty') { $target.Clear() }
        if ($failure -eq 'external-target') { $target.Remove('K4os.Compression.LZ4/1.3.8') }
        if ($failure -eq 'direct') { $directId = 'Atelia.Data' }
        $targets = if ($failure -eq 'framework') { @{} } else { @{ 'net10.0' = $target } }
        $dependencies = if ($failure -eq 'direct-empty') { @{} } else { @{ $directId = @{ target = 'Package'; version = "[$version, )" } } }
        if ($failure -eq 'external-direct') { $dependencies['K4os.Compression.LZ4'] = @{ target = 'Package'; version = '[1.3.8]' } }
        $assets = [ordered]@{
            libraries = $libraries; targets = $targets
            project = @{ frameworks = @{ 'net10.0' = @{ dependencies = $dependencies } } }
        }
        New-Item -ItemType Directory -Path (Join-Path $project 'obj') -Force | Out-Null
        $assets | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $project 'obj/project.assets.json') -Encoding utf8NoBOM
    }
    if ($arguments[0] -eq 'run' -and $global:StoragePackageEntryMock.FailSmoke -and $arguments -contains "$($global:StoragePackageEntryMock.FailSmoke)/$($global:StoragePackageEntryMock.FailSmoke).csproj") {
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
    Assert ($global:StoragePackageEntryMock.Calls.Count -eq 6) "$Name command count changed."
    foreach ($call in $global:StoragePackageEntryMock.Calls) {
        Assert ($call.location -eq $work) "$Name ran outside isolated workspace."
        Assert ($call.packages -eq (Join-Path $work 'packages')) "$Name used a shared package cache."
        Assert ($call.httpCache -eq (Join-Path $work 'http-cache')) "$Name used a shared HTTP cache."
        if ($call.arguments[0] -eq 'restore') {
            Assert ($call.arguments -contains '--configfile' -and $call.arguments -contains 'NuGet.Config') "$Name restore missed explicit config."
        }
        if ($call.arguments -contains 'RbfPackageSmoke/RbfPackageSmoke.csproj' -or $call.arguments -contains 'BinaryPackageSmoke/BinaryPackageSmoke.csproj') {
            Assert ($call.arguments -contains "-p:StoragePackageVersion=$version") "$Name lost candidate version."
        }
    }
    $metadata = @($global:StoragePackageEntryMock.Calls | Where-Object { $_.arguments[0] -eq 'run' -and $_.arguments -contains 'PackageMetadataCheck/PackageMetadataCheck.csproj' })
    Assert ($metadata.Count -eq 1 -and $metadata[0].arguments -contains $manifestPath) "$Name missed original All metadata verification."
    foreach ($project in @('RbfPackageSmoke', 'BinaryPackageSmoke')) {
        Assert ($metadata[0].arguments -contains (Join-Path $work "$project/obj/project.assets.json")) "$Name missed $project metadata assets verification."
    }
    Assert (@((Get-Content (Join-Path $work 'verified-packages.json') -Raw | ConvertFrom-Json).packages).Count -eq 4) "$Name evidence did not retain four packages."
    Assert (!(Test-Path (Join-Path $work 'EventJournalSmoke')) -and !(Test-Path (Join-Path $work 'RbfSegmentStoreSmoke'))) "$Name copied a legacy consumer."
    Assert (Test-Path (Join-Path $work 'logs/rbf-assets.log')) "$Name failed to retain assets check."
    Assert (Test-Path (Join-Path $work 'logs/binary-assets.log')) "$Name failed to retain Binary assets check."
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
    # Pack's immutable schema 1 reuse path must bind all four IDs/hashes, without packing.
    $global:StoragePackageEntryMock.Calls.Clear()
    & $pack -Project All -Version $version -OutputDirectory $feed | Out-Host
    Assert ($global:StoragePackageEntryMock.Calls.Count -eq 1 -and $global:StoragePackageEntryMock.Calls[0].arguments[0] -eq '--version') 'Immutable core reuse unexpectedly attempted restore/pack.'
    foreach ($consumer in @('RbfPackageSmoke', 'BinaryPackageSmoke')) {
        $global:StoragePackageEntryMock.FailureConsumer = $consumer
        $failures = @('version', 'extra', 'project', 'missing', 'target', 'direct', 'framework', 'cross', 'libraries-empty', 'target-empty', 'direct-empty', 'external-extra', 'external-direct')
        if ($consumer -eq 'BinaryPackageSmoke') { $failures += @('external-version', 'external-missing', 'external-target') }
        foreach ($failure in $failures) {
            $global:StoragePackageEntryMock.AssetsFailure = $failure
            $global:StoragePackageEntryMock.Calls.Clear()
            $message = ''
            try { & $entry -Version $version -FeedDirectory $feed -WorkDirectory (Join-Path $evidence "bad-assets-$consumer-$failure") }
            catch { $message = $_.Exception.Message }
            Assert ($message -like "*$consumer*") "$consumer/$failure assets did not fail the closure guard with consumer context."
            $expectedCalls = if ($consumer -eq 'RbfPackageSmoke') { 1 } else { 3 }
            Assert ($global:StoragePackageEntryMock.Calls.Count -eq $expectedCalls) "$consumer/$failure assets ran smoke after failed guard."
            $badAssets = Get-Content -LiteralPath (Join-Path $evidence "bad-assets-$consumer-$failure/$consumer/obj/project.assets.json") -Raw | ConvertFrom-Json -AsHashtable
            $directId = if ($consumer -eq 'RbfPackageSmoke') { 'Atelia.Rbf' } else { 'Atelia.Binary' }
            switch ($failure) {
                'version' { Assert (!$badAssets.libraries.Contains("$directId/$version") -and $badAssets.libraries.Contains("$directId/0.0.0")) "$consumer version fixture did not alter the direct package." }
                'extra' { Assert ($badAssets.libraries.Contains("Atelia.EventJournal/$version")) "$consumer extra fixture did not add the foreign package." }
                'project' { Assert ($badAssets.libraries["$directId/$version"].type -ceq 'project') "$consumer project fixture did not alter the direct package type." }
                'missing' { Assert (!$badAssets.libraries.Contains("$directId/$version")) "$consumer missing fixture did not remove its direct package." }
                'libraries-empty' { Assert ($badAssets.libraries.Count -eq 0) "$consumer libraries-empty fixture did not clear its libraries." }
                'target' { Assert (!$badAssets.targets['net10.0'].Contains("$directId/$version")) "$consumer target fixture did not remove its direct package target." }
                'target-empty' { Assert ($badAssets.targets['net10.0'].Count -eq 0) "$consumer target-empty fixture did not clear its target." }
                'direct' { Assert ($badAssets.project.frameworks['net10.0'].dependencies.Contains('Atelia.Data')) "$consumer direct fixture did not replace its direct reference." }
                'direct-empty' { Assert ($badAssets.project.frameworks['net10.0'].dependencies.Count -eq 0) "$consumer direct-empty fixture did not clear its dependencies." }
                'external-extra' { Assert ($badAssets.libraries.Contains('Unapproved.Codec/1.0.0')) "$consumer external-extra fixture did not add its foreign package." }
                'external-version' { Assert ($badAssets.libraries.Contains('K4os.Compression.LZ4/1.3.7')) "$consumer external-version fixture did not change codec version." }
                'external-missing' { Assert (!$badAssets.libraries.Contains('K4os.Compression.LZ4/1.3.8')) "$consumer external-missing fixture did not remove codec." }
                'external-target' { Assert (!$badAssets.targets['net10.0'].Contains('K4os.Compression.LZ4/1.3.8')) "$consumer external-target fixture did not remove codec target." }
                'external-direct' { Assert ($badAssets.project.frameworks['net10.0'].dependencies.Contains('K4os.Compression.LZ4')) "$consumer external-direct fixture did not add a direct codec reference." }
                'framework' { Assert (!$badAssets.targets.Contains('net10.0')) "$consumer framework fixture did not remove net10.0." }
                'cross' {
                    $crossId = if ($consumer -eq 'RbfPackageSmoke') { 'Atelia.Binary' } else { 'Atelia.Rbf' }
                    Assert ($badAssets.libraries.Contains("$crossId/$version")) "$consumer cross fixture did not add the other consumer package."
                }
            }
            Assert-RestoredEnvironment $failure
        }
    }
    $global:StoragePackageEntryMock.AssetsFailure = ''
    foreach ($failure in @('version', 'filename', 'missing', 'binary-filename', 'binary-missing', 'extra', 'schema')) {
        $broken = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        switch ($failure) {
            'version' { $broken.version = '0.0.0' }
            'filename' { ($broken.packages | Where-Object id -CEQ 'Atelia.Rbf').file = 'Atelia.Rbf.0.0.0.nupkg' }
            'missing' { $broken.packages = @($broken.packages | Where-Object id -CNE 'Atelia.Rbf') }
            'binary-filename' { ($broken.packages | Where-Object id -CEQ 'Atelia.Binary').file = 'Atelia.Binary.0.0.0.nupkg' }
            'binary-missing' { $broken.packages = @($broken.packages | Where-Object id -CNE 'Atelia.Binary') }
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
    foreach ($consumer in @('RbfPackageSmoke', 'BinaryPackageSmoke')) {
        $global:StoragePackageEntryMock.FailSmoke = $consumer
        $global:StoragePackageEntryMock.Calls.Clear()
        $message = ''
        try { & $entry -Version $version -FeedDirectory $feed -WorkDirectory (Join-Path $evidence "failed-smoke-$consumer") } catch { $message = $_.Exception.Message }
        $expectedCalls = if ($consumer -eq 'RbfPackageSmoke') { 2 } else { 4 }
        Assert ($message -like '*dotnet failed*' -and $global:StoragePackageEntryMock.Calls.Count -eq $expectedCalls) 'Failed smoke did not stop metadata execution.'
        Assert-RestoredEnvironment 'failed-smoke'
    }
    'Passed: parser, default/explicit four-package All, ten legacy parameter rejections, immutable four-package reuse, twenty-two malformed consumer assets including empty collections and cross-dependencies, seven malformed manifests, both smoke failure environment restorations. All git/dotnet commands mocked; no package consumption proven.' |
        Set-Content -LiteralPath (Join-Path $evidence 'result.txt') -Encoding utf8NoBOM
    Write-Host "MOCK entrypoint tests passed. Evidence: $evidence"
}
finally {
    Remove-Variable -Name StoragePackageEntryMock -Scope Global
    $env:NUGET_PACKAGES = $originalPackages
    $env:NUGET_HTTP_CACHE_PATH = $originalHttpCache
}
