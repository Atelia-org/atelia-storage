[CmdletBinding()]
param([string]$EvidenceDirectory = (Join-Path ([IO.Path]::GetTempPath()) ('storage-package-entry-mock-' + [Guid]::NewGuid().ToString('N'))))
$ErrorActionPreference = 'Stop'
$entry = Join-Path $PSScriptRoot 'Test-Package.ps1'
$pack = Join-Path $PSScriptRoot 'Pack.ps1'
$verify = Join-Path $PSScriptRoot 'Verify-Published.ps1'
foreach ($script in @($entry, $pack, $verify, (Join-Path $PSScriptRoot 'PackageMetadataCheck/ConsumerContracts.ps1'))) {
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
$ids = @('Atelia.Primitives', 'Atelia.Data', 'Atelia.Rbf', 'Atelia.Binary', 'Atelia.FrameStore', 'Atelia.HeadStore')
$contracts = [ordered]@{
    RbfPackageSmoke = @{ DirectId = 'Atelia.Rbf'; Closure = @('Atelia.Primitives', 'Atelia.Data', 'Atelia.Rbf') }
    BinaryPackageSmoke = @{ DirectId = 'Atelia.Binary'; Closure = @('Atelia.Binary') }
    FrameStorePackageSmoke = @{ DirectId = 'Atelia.FrameStore'; Closure = @('Atelia.Primitives', 'Atelia.Data', 'Atelia.Rbf', 'Atelia.Binary', 'Atelia.FrameStore') }
    HeadStorePackageSmoke = @{ DirectId = 'Atelia.HeadStore'; Closure = @('Atelia.Primitives', 'Atelia.Data', 'Atelia.Rbf', 'Atelia.Binary', 'Atelia.FrameStore', 'Atelia.HeadStore') }
}
$consumerNames = @($contracts.Keys)
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
    Revision = $revision; Version = $version; Ids = $ids; SdkVersion = $manifest.sdkVersion; Contracts = $contracts; GitDirty = $false
}
$originalPackages = $env:NUGET_PACKAGES
$originalHttpCache = $env:NUGET_HTTP_CACHE_PATH
$originalUiLanguage = $env:DOTNET_CLI_UI_LANGUAGE
$originalLocation = (Get-Location).Path
function Assert([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
# Only these functions stand in for external commands. No dotnet executable is invoked.
function git {
    $global:StoragePackageEntryMock.GitCalls++
    $global:LASTEXITCODE = 0
    if ($args -contains 'rev-parse') { return $global:StoragePackageEntryMock.Revision }
    if ($args -contains 'status') { if ($global:StoragePackageEntryMock.GitDirty) { return ' M src/HeadStore/HeadStore.csproj' }; return }
    if ($args -contains 'get-url') { return 'https://github.com/Atelia-org/atelia-storage.git' }
    throw "Unexpected mocked git command: $args"
}
function dotnet {
    $arguments = @($args)
    $global:StoragePackageEntryMock.Calls.Add([pscustomobject]@{
        arguments = $arguments; location = (Get-Location).Path
        packages = $env:NUGET_PACKAGES; httpCache = $env:NUGET_HTTP_CACHE_PATH
        inheritedOutputs = @(if ($arguments[0] -eq 'restore') {
            $projectDirectory = $arguments[1].Split('/')[0]
            @('bin', 'obj') | Where-Object { Test-Path -LiteralPath (Join-Path $projectDirectory $_) }
        })
    })
    $global:LASTEXITCODE = 0
    if ($arguments[0] -eq '--version') { return $global:StoragePackageEntryMock.SdkVersion }
    if ($arguments[0] -eq 'restore' -and $arguments[1] -ne 'PackageMetadataCheck/PackageMetadataCheck.csproj') {
        $version = $global:StoragePackageEntryMock.Version
        $project = $arguments[1].Split('/')[0]
        $contract = $global:StoragePackageEntryMock.Contracts[$project]
        if (!$contract) { throw "Unexpected mocked consumer restore: $project" }
        $directId = $contract.DirectId
        $closureIds = $contract.Closure
        $failure = if ($project -eq $global:StoragePackageEntryMock.FailureConsumer) { $global:StoragePackageEntryMock.AssetsFailure } else { '' }
        $libraries = [ordered]@{}
        foreach ($id in $closureIds) { $libraries["$id/$version"] = @{ type = 'package' } }
        if ($closureIds -contains 'Atelia.Binary') { $libraries['K4os.Compression.LZ4/1.3.8'] = @{ type = 'package' } }
        switch ($failure) {
            'version' { $libraries.Remove("$directId/$version"); $libraries["$directId/0.0.0"] = @{ type = 'package' } }
            'extra' { $libraries["Atelia.EventJournal/$version"] = @{ type = 'package' } }
            'legacy-segment' { $libraries["Atelia.RbfSegmentStore/$version"] = @{ type = 'package' } }
            'project' { $libraries["$directId/$version"] = @{ type = 'project' } }
            'missing' { $libraries.Remove("$directId/$version") }
            'libraries-empty' { $libraries.Clear() }
            'cross' { $crossId = if ($project -eq 'RbfPackageSmoke') { 'Atelia.Binary' } elseif ($project -eq 'BinaryPackageSmoke') { 'Atelia.Rbf' } elseif ($project -eq 'FrameStorePackageSmoke') { 'Atelia.HeadStore' } else { 'Atelia.Diagnostics' }; $libraries["$crossId/$version"] = @{ type = 'package' } }
            'external-extra' { $libraries['Unapproved.Codec/1.0.0'] = @{ type = 'package' } }
            'external-version' { $libraries.Remove('K4os.Compression.LZ4/1.3.8'); $libraries['K4os.Compression.LZ4/1.3.7'] = @{ type = 'package' } }
            'external-missing' { $libraries.Remove('K4os.Compression.LZ4/1.3.8') }
            'transitive-version' { $libraries.Remove("Atelia.Rbf/$version"); $libraries['Atelia.Rbf/0.0.0'] = @{ type = 'package' } }
            'transitive-missing' { $libraries.Remove("Atelia.Binary/$version") }
            'transitive-project' { $libraries["Atelia.FrameStore/$version"] = @{ type = 'project' } }
        }
        $target = [ordered]@{}
        foreach ($key in $libraries.Keys) { $target[$key] = $libraries[$key] }
        if ($failure -eq 'target') { $target.Remove("$directId/$version") }
        if ($failure -eq 'target-empty') { $target.Clear() }
        if ($failure -eq 'external-target') { $target.Remove('K4os.Compression.LZ4/1.3.8') }
        if ($failure -eq 'direct') { $directId = 'Atelia.Data' }
        $targets = if ($failure -eq 'framework') { @{} } else { @{ 'net10.0' = $target } }
        $dependencies = if ($failure -eq 'direct-empty') { @{} } else { @{ $directId = @{ target = 'Package'; version = "[$version, $version]" } } }
        if ($failure -eq 'external-direct') { $dependencies['K4os.Compression.LZ4'] = @{ target = 'Package'; version = '[1.3.8]' } }
        if ($failure -eq 'direct-minimum') { $dependencies[$directId].version = "[$version, )" }
        if ($failure -eq 'direct-version') { $dependencies[$directId].version = '[0.0.0, 0.0.0]' }
        if ($failure -eq 'direct-project') { $dependencies[$directId].target = 'Project' }
        $frameworks = @{ 'net10.0' = @{ dependencies = $dependencies } }
        if ($failure -eq 'extra-framework') { $frameworks['net9.0'] = @{ dependencies = $dependencies } }
        if ($failure -eq 'extra-target') { $targets['net9.0'] = $target }
        $packageFolders = if ($failure -eq 'cache-missing') { @{} } else { @{ ($env:NUGET_PACKAGES + [IO.Path]::DirectorySeparatorChar) = @{} } }
        if ($failure -eq 'cache-shared') { $packageFolders = @{ (Join-Path ([IO.Path]::GetTempPath()) 'shared-storage-cache') = @{} } }
        if ($failure -eq 'cache-extra') { $packageFolders[(Join-Path ([IO.Path]::GetTempPath()) 'extra-storage-cache')] = @{} }
        $assets = [ordered]@{
            libraries = $libraries; targets = $targets
            packageFolders = $packageFolders; project = @{ frameworks = $frameworks }
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
    Assert ($env:NUGET_PACKAGES -eq $originalPackages -and $env:NUGET_HTTP_CACHE_PATH -eq $originalHttpCache -and $env:DOTNET_CLI_UI_LANGUAGE -eq $originalUiLanguage) "$Name did not restore environment."
    Assert ((Get-Location).Path -ceq $originalLocation) "$Name did not restore working directory."
}
function Run-Case([string]$Name) {
    $global:StoragePackageEntryMock.Calls.Clear()
    $work = Join-Path $evidence $Name
    & $entry -Version $version -FeedDirectory $feed -WorkDirectory $work
    Assert ($global:StoragePackageEntryMock.Calls.Count -eq 10) "$Name command count changed."
    foreach ($call in $global:StoragePackageEntryMock.Calls) {
        Assert ($call.location -eq $work) "$Name ran outside isolated workspace."
        Assert ($call.packages -eq (Join-Path $work 'packages')) "$Name used a shared package cache."
        Assert ($call.httpCache -eq (Join-Path $work 'http-cache')) "$Name used a shared HTTP cache."
        if ($call.arguments[0] -eq 'restore') {
            Assert ($call.arguments -contains '--configfile' -and $call.arguments -contains 'NuGet.Config') "$Name restore missed explicit config."
            Assert ($call.inheritedOutputs.Count -eq 0) "$Name inherited repository bin/obj output."
        }
        if (@($consumerNames | Where-Object { $call.arguments -contains "$_/$_.csproj" }).Count) {
            Assert ($call.arguments -contains "-p:StoragePackageVersion=$version") "$Name lost candidate version."
        }
    }
    $metadata = @($global:StoragePackageEntryMock.Calls | Where-Object { $_.arguments[0] -eq 'run' -and $_.arguments -contains 'PackageMetadataCheck/PackageMetadataCheck.csproj' })
    Assert ($metadata.Count -eq 1 -and $metadata[0].arguments -contains $manifestPath) "$Name missed original All metadata verification."
    foreach ($project in $consumerNames) {
        Assert ($metadata[0].arguments -contains (Join-Path $work "$project/obj/project.assets.json")) "$Name missed $project metadata assets verification."
    }
    $payloadStart = [Array]::IndexOf($metadata[0].arguments, '--') + 1
    $payload = @($metadata[0].arguments | Select-Object -Skip $payloadStart)
    $expectedPayload = @($manifestPath, (Split-Path $PSScriptRoot -Parent)) + @($consumerNames | ForEach-Object { Join-Path $work "$_/obj/project.assets.json" }) + @($version)
    Assert ($payload.Count -eq 7 -and ($payload -join '|') -ceq ($expectedPayload -join '|')) "$Name metadata CLI order differs from the seven-argument contract."
    Assert (@((Get-Content (Join-Path $work 'verified-packages.json') -Raw | ConvertFrom-Json).packages).Count -eq 6) "$Name evidence did not retain six packages."
    Assert (!(Test-Path (Join-Path $work 'EventJournalSmoke')) -and !(Test-Path (Join-Path $work 'RbfSegmentStoreSmoke'))) "$Name copied a legacy consumer."
    Assert (Test-Path (Join-Path $work 'logs/rbf-assets.log')) "$Name failed to retain assets check."
    Assert (Test-Path (Join-Path $work 'logs/binary-assets.log')) "$Name failed to retain Binary assets check."
    Assert (Test-Path (Join-Path $work 'logs/frame-store-assets.log')) "$Name failed to retain FrameStore assets check."
    Assert (Test-Path (Join-Path $work 'logs/head-store-assets.log')) "$Name failed to retain HeadStore assets check."
    Assert-RestoredEnvironment $Name
}
function Assert-ParameterRejection([string]$Name, [scriptblock]$Invoke, [string]$UnexpectedDirectory) {
    $before = $global:StoragePackageEntryMock.GitCalls
    $global:StoragePackageEntryMock.Calls.Clear()
    $failure = $null
    try { & $Invoke } catch { $failure = $_ }
    Assert ($null -ne $failure -and $failure.FullyQualifiedErrorId -like 'NamedParameterNotFound*') "$Name did not reject the removed parameter during binding."
    Assert ($global:StoragePackageEntryMock.GitCalls -eq $before -and $global:StoragePackageEntryMock.Calls.Count -eq 0) "$Name called external commands before rejection."
    Assert (!(Test-Path -LiteralPath $UnexpectedDirectory)) "$Name created a directory before rejection."
    Assert-RestoredEnvironment $Name
}
try {
    . (Join-Path $PSScriptRoot 'PackageMetadataCheck/ConsumerContracts.ps1')
    $copySource = Join-Path $evidence 'CopyFixture'
    $copyWork = Join-Path $evidence 'copy-isolation'
    foreach ($directory in @($copySource, $copyWork, (Join-Path $copySource 'bin'), (Join-Path $copySource 'obj'))) {
        New-Item -ItemType Directory -Path $directory | Out-Null
    }
    'source' | Set-Content -LiteralPath (Join-Path $copySource 'Program.cs') -Encoding utf8NoBOM
    '<Project />' | Set-Content -LiteralPath (Join-Path $copySource 'CopyFixture.csproj') -Encoding utf8NoBOM
    'stale assembly' | Set-Content -LiteralPath (Join-Path $copySource 'bin/CopyFixture.dll') -Encoding utf8NoBOM
    'stale shared cache assets' | Set-Content -LiteralPath (Join-Path $copySource 'obj/project.assets.json') -Encoding utf8NoBOM
    Copy-PackageSmokeSources $copySource $copyWork
    $copied = Join-Path $copyWork 'CopyFixture'
    Assert ((Test-Path (Join-Path $copied 'Program.cs')) -and (Test-Path (Join-Path $copied 'CopyFixture.csproj'))) 'Source-copy fixture lost top-level source files.'
    Assert (!(Test-Path (Join-Path $copied 'bin')) -and !(Test-Path (Join-Path $copied 'obj'))) 'Source-copy fixture imported stale bin/obj output.'
    Run-Case 'six-packages'
    # CmdletBinding rejects removed names before any filesystem or external-command work.
    $path = Join-Path $evidence 'reject-pack-project'
    Assert-ParameterRejection 'pack-project' { & $pack -Project All -Version $version -OutputDirectory $path } $path
    $path = Join-Path $evidence 'reject-test-project'
    Assert-ParameterRejection 'test-project' { & $entry -Project All -Version $version -FeedDirectory 'missing-feed' -WorkDirectory $path } $path
    $path = Join-Path $evidence 'reject-verify-project'
    Assert-ParameterRejection 'verify-project' { & $verify -Project All -Version $version -FeedDirectory 'missing-feed' -WorkDirectory $path } $path
    foreach ($enabled in @($true, $false)) {
        $path = Join-Path $evidence "reject-additional-$enabled"
        Assert-ParameterRejection "additional-$enabled" { & $entry -Version $version -FeedDirectory 'missing-feed' -WorkDirectory $path -AdditionalSegmentSmoke:$enabled } $path
    }
    foreach ($dependencies in @(@{}, $null)) {
        $path = Join-Path $evidence ('reject-dependencies-' + [Guid]::NewGuid().ToString('N'))
        Assert-ParameterRejection 'dependencies' { & $pack -Version $version -OutputDirectory $path -DependencyVersions $dependencies } $path
    }
    # Pack's immutable schema 1 reuse path must bind all six IDs/hashes, without packing.
    $global:StoragePackageEntryMock.Calls.Clear()
    & $pack -Version $version -OutputDirectory $feed | Out-Host
    Assert ($global:StoragePackageEntryMock.Calls.Count -eq 1 -and $global:StoragePackageEntryMock.Calls[0].arguments[0] -eq '--version') 'Immutable core reuse unexpectedly attempted restore/pack.'
    foreach ($failure in @('frame-store-missing', 'head-store-filename', 'head-store-symbols-hash', 'legacy-replacement')) {
        $broken = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        switch ($failure) {
            'frame-store-missing' { $broken.packages = @($broken.packages | Where-Object id -CNE 'Atelia.FrameStore') }
            'head-store-filename' { ($broken.packages | Where-Object id -CEQ 'Atelia.HeadStore').file = 'Atelia.HeadStore.0.0.0.nupkg' }
            'head-store-symbols-hash' { ($broken.packages | Where-Object id -CEQ 'Atelia.HeadStore').symbolsSha256 = '0' * 64 }
            'legacy-replacement' { ($broken.packages | Where-Object id -CEQ 'Atelia.HeadStore').id = 'Atelia.EventJournal' }
        }
        $broken | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
        $global:StoragePackageEntryMock.Calls.Clear()
        $message = ''
        try { & $pack -Version $version -OutputDirectory $feed | Out-Null } catch { $message = $_.Exception.Message }
        Assert ($message.Length -gt 0 -and $global:StoragePackageEntryMock.Calls.Count -eq 1 -and $global:StoragePackageEntryMock.Calls[0].arguments[0] -eq '--version') "$failure immutable candidate rejection attempted restore/pack."
        Assert-RestoredEnvironment "reuse-$failure"
        $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
    }
    foreach ($failure in @('revision', 'dirty')) {
        $global:StoragePackageEntryMock.Revision = if ($failure -eq 'revision') { '2' * 40 } else { $revision }
        $global:StoragePackageEntryMock.GitDirty = $failure -eq 'dirty'
        $global:StoragePackageEntryMock.Calls.Clear()
        $path = Join-Path $evidence "bad-source-$failure"
        $message = ''
        try { & $entry -Version $version -FeedDirectory $feed -WorkDirectory $path } catch { $message = $_.Exception.Message }
        Assert ($message.Length -gt 0 -and $global:StoragePackageEntryMock.Calls.Count -eq 0 -and !(Test-Path $path)) "$failure source verification did not reject before workspace creation."
        Assert-RestoredEnvironment "source-$failure"
    }
    $global:StoragePackageEntryMock.Revision = $revision
    $global:StoragePackageEntryMock.GitDirty = $false
    $malformedAssetsCount = 0
    foreach ($consumer in $consumerNames) {
        $global:StoragePackageEntryMock.FailureConsumer = $consumer
        $failures = @('version', 'extra', 'legacy-segment', 'project', 'missing', 'target', 'direct', 'framework', 'cross', 'libraries-empty', 'target-empty', 'direct-empty', 'external-extra', 'external-direct', 'direct-minimum', 'direct-version', 'direct-project', 'extra-framework', 'extra-target', 'cache-missing', 'cache-shared', 'cache-extra')
        if ($contracts[$consumer].Closure -contains 'Atelia.Binary') { $failures += @('external-version', 'external-missing', 'external-target') }
        if ($consumer -in @('FrameStorePackageSmoke', 'HeadStorePackageSmoke')) { $failures += @('transitive-version', 'transitive-missing') }
        if ($consumer -eq 'HeadStorePackageSmoke') { $failures += 'transitive-project' }
        foreach ($failure in $failures) {
            $malformedAssetsCount++
            $global:StoragePackageEntryMock.AssetsFailure = $failure
            $global:StoragePackageEntryMock.Calls.Clear()
            $message = ''
            try { & $entry -Version $version -FeedDirectory $feed -WorkDirectory (Join-Path $evidence "bad-assets-$consumer-$failure") }
            catch { $message = $_.Exception.Message }
            Assert ($message -like "*$consumer*") "$consumer/$failure assets did not fail the closure guard with consumer context."
            $expectedCalls = 2 * [Array]::IndexOf($consumerNames, $consumer) + 1
            Assert ($global:StoragePackageEntryMock.Calls.Count -eq $expectedCalls) "$consumer/$failure assets ran smoke after failed guard."
            $badAssets = Get-Content -LiteralPath (Join-Path $evidence "bad-assets-$consumer-$failure/$consumer/obj/project.assets.json") -Raw | ConvertFrom-Json -AsHashtable
            $directId = $contracts[$consumer].DirectId
            switch ($failure) {
                'version' { Assert (!$badAssets.libraries.Contains("$directId/$version") -and $badAssets.libraries.Contains("$directId/0.0.0")) "$consumer version fixture did not alter the direct package." }
                'extra' { Assert ($badAssets.libraries.Contains("Atelia.EventJournal/$version")) "$consumer extra fixture did not add the foreign package." }
                'legacy-segment' { Assert ($badAssets.libraries.Contains("Atelia.RbfSegmentStore/$version")) "$consumer fixture did not add the legacy segment package." }
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
                'direct-minimum' { Assert ($badAssets.project.frameworks['net10.0'].dependencies[$directId].version -ceq "[$version, )") "$consumer fixture did not make its candidate range unbounded." }
                'direct-version' { Assert ($badAssets.project.frameworks['net10.0'].dependencies[$directId].version -ceq '[0.0.0, 0.0.0]') "$consumer fixture did not change its direct candidate range." }
                'direct-project' { Assert ($badAssets.project.frameworks['net10.0'].dependencies[$directId].target -ceq 'Project') "$consumer fixture did not change its direct reference type." }
                'extra-framework' { Assert ($badAssets.project.frameworks.Contains('net9.0')) "$consumer fixture did not add another project framework." }
                'extra-target' { Assert ($badAssets.targets.Contains('net9.0')) "$consumer fixture did not add another restored target." }
                'cache-missing' { Assert ($badAssets.packageFolders.Count -eq 0) "$consumer fixture did not clear its package folders." }
                'cache-shared' { Assert (!$badAssets.packageFolders.Contains((Join-Path $evidence "bad-assets-$consumer-$failure/packages") + [IO.Path]::DirectorySeparatorChar)) "$consumer fixture still uses the private cache." }
                'cache-extra' { Assert ($badAssets.packageFolders.Count -eq 2) "$consumer fixture did not add another cache." }
                'transitive-version' { Assert ($badAssets.libraries.Contains('Atelia.Rbf/0.0.0') -and !$badAssets.libraries.Contains("Atelia.Rbf/$version")) "$consumer fixture did not alter a transitive version." }
                'transitive-missing' { Assert (!$badAssets.libraries.Contains("Atelia.Binary/$version")) "$consumer fixture did not remove transitive Binary." }
                'transitive-project' { Assert ($badAssets.libraries["Atelia.FrameStore/$version"].type -ceq 'project') "$consumer fixture did not change transitive FrameStore to a project reference." }
                'framework' { Assert (!$badAssets.targets.Contains('net10.0')) "$consumer framework fixture did not remove net10.0." }
                'cross' {
                    $crossId = if ($consumer -eq 'RbfPackageSmoke') { 'Atelia.Binary' } elseif ($consumer -eq 'BinaryPackageSmoke') { 'Atelia.Rbf' } elseif ($consumer -eq 'FrameStorePackageSmoke') { 'Atelia.HeadStore' } else { 'Atelia.Diagnostics' }
                    Assert ($badAssets.libraries.Contains("$crossId/$version")) "$consumer cross fixture did not add the other consumer package."
                }
            }
            Assert-RestoredEnvironment $failure
        }
    }
    $global:StoragePackageEntryMock.AssetsFailure = ''
    $manifestFailures = @('version', 'filename', 'missing', 'binary-filename', 'binary-missing', 'frame-store-filename', 'frame-store-missing', 'head-store-filename', 'head-store-missing', 'extra', 'replace-with-legacy', 'duplicate', 'schema', 'origin', 'revision', 'hash', 'symbols-hash', 'symbols-filename')
    foreach ($failure in $manifestFailures) {
        $broken = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        switch ($failure) {
            'version' { $broken.version = '0.0.0' }
            'filename' { ($broken.packages | Where-Object id -CEQ 'Atelia.Rbf').file = 'Atelia.Rbf.0.0.0.nupkg' }
            'missing' { $broken.packages = @($broken.packages | Where-Object id -CNE 'Atelia.Rbf') }
            'binary-filename' { ($broken.packages | Where-Object id -CEQ 'Atelia.Binary').file = 'Atelia.Binary.0.0.0.nupkg' }
            'binary-missing' { $broken.packages = @($broken.packages | Where-Object id -CNE 'Atelia.Binary') }
            'frame-store-filename' { ($broken.packages | Where-Object id -CEQ 'Atelia.FrameStore').file = 'Atelia.FrameStore.0.0.0.nupkg' }
            'frame-store-missing' { $broken.packages = @($broken.packages | Where-Object id -CNE 'Atelia.FrameStore') }
            'head-store-filename' { ($broken.packages | Where-Object id -CEQ 'Atelia.HeadStore').file = 'Atelia.HeadStore.0.0.0.nupkg' }
            'head-store-missing' { $broken.packages = @($broken.packages | Where-Object id -CNE 'Atelia.HeadStore') }
            'extra' { $broken.packages += [pscustomobject]@{ id = 'Atelia.EventJournal'; file = 'extra.nupkg' } }
            'replace-with-legacy' { ($broken.packages | Where-Object id -CEQ 'Atelia.HeadStore').id = 'Atelia.EventJournal' }
            'duplicate' { ($broken.packages | Where-Object id -CEQ 'Atelia.HeadStore').id = 'Atelia.FrameStore' }
            'schema' { $broken.schemaVersion = 2 }
            'origin' { $broken.repositoryUrl = 'https://github.com/other/storage' }
            'revision' { $broken.sourceRevision = 'not-a-git-revision' }
            'hash' { ($broken.packages | Where-Object id -CEQ 'Atelia.FrameStore').sha256 = '0' * 64 }
            'symbols-hash' { ($broken.packages | Where-Object id -CEQ 'Atelia.HeadStore').symbolsSha256 = '0' * 64 }
            'symbols-filename' { ($broken.packages | Where-Object id -CEQ 'Atelia.HeadStore').symbolsFile = '../escape.snupkg' }
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
    foreach ($consumer in $consumerNames) {
        $global:StoragePackageEntryMock.FailSmoke = $consumer
        $global:StoragePackageEntryMock.Calls.Clear()
        $message = ''
        try { & $entry -Version $version -FeedDirectory $feed -WorkDirectory (Join-Path $evidence "failed-smoke-$consumer") } catch { $message = $_.Exception.Message }
        $expectedCalls = 2 * [Array]::IndexOf($consumerNames, $consumer) + 2
        Assert ($message -like '*dotnet failed*' -and $global:StoragePackageEntryMock.Calls.Count -eq $expectedCalls) 'Failed smoke did not stop metadata execution.'
        Assert-RestoredEnvironment 'failed-smoke'
    }
    "Passed: parser, source-only copy excluding stale bin/obj, fixed six-package set and seven-argument metadata CLI, seven removed-parameter binding rejections, immutable six-package reuse and four reuse rejections, two source checkout rejections, $malformedAssetsCount malformed consumer assets (four closures, transitive versions, exact references, K4os, project rejection and cache isolation), $($manifestFailures.Count) malformed manifests, four smoke failure environment restorations. All git/dotnet commands mocked; no package consumption proven." |
        Set-Content -LiteralPath (Join-Path $evidence 'result.txt') -Encoding utf8NoBOM
    Write-Host "MOCK entrypoint tests passed. Evidence: $evidence"
}
finally {
    Remove-Variable -Name StoragePackageEntryMock -Scope Global
    $env:NUGET_PACKAGES = $originalPackages
    $env:NUGET_HTTP_CACHE_PATH = $originalHttpCache
    $env:DOTNET_CLI_UI_LANGUAGE = $originalUiLanguage
}

# Run archive resource probes after restoring the environment, in a process without these git/dotnet mocks.
$publishedResourceEvidence = Join-Path ([IO.Path]::GetTempPath()) ('storage-published-resource-entry-' + [Guid]::NewGuid().ToString('N'))
& pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Verify-Published.Tests.ps1') -EvidenceDirectory $publishedResourceEvidence
if ($LASTEXITCODE -ne 0) { throw "Verify-Published resource tests failed; see $publishedResourceEvidence/result.json." }
$publishedResourceEvidence | Set-Content -LiteralPath (Join-Path $evidence 'verify-published-resources.txt') -Encoding utf8NoBOM
