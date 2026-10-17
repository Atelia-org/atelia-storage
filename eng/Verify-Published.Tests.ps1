[CmdletBinding()]
param([string]$EvidenceDirectory = (Join-Path ([IO.Path]::GetTempPath()) ('storage-published-resource-probe-' + [Guid]::NewGuid().ToString('N'))))
$ErrorActionPreference = 'Stop'
$probeVerifyScript = Join-Path $PSScriptRoot 'Verify-Published.ps1'
foreach ($probeScript in @($PSCommandPath, $probeVerifyScript)) {
    $probeTokens = $null
    $probeParseErrors = $null
    [void][Management.Automation.Language.Parser]::ParseFile($probeScript, [ref]$probeTokens, [ref]$probeParseErrors)
    if ($probeParseErrors.Count) { throw ($probeParseErrors | Out-String) }
}
$probeEvidence = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($EvidenceDirectory)
$probeArtifacts = Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts'
$probeAllowed = $false
foreach ($probeRoot in @([IO.Path]::GetTempPath(), $probeArtifacts)) {
    $probeBoundary = [IO.Path]::GetFullPath($probeRoot).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if ($probeEvidence.StartsWith($probeBoundary, [StringComparison]::OrdinalIgnoreCase)) { $probeAllowed = $true }
}
if (!$probeAllowed) { throw 'EvidenceDirectory must be a new directory below TEMP or this repository artifacts.' }
if (Test-Path -LiteralPath $probeEvidence) { throw 'EvidenceDirectory must be fresh.' }
[void][IO.Directory]::CreateDirectory($probeEvidence)

function Assert-Probe([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw $Message }
}

function New-ProbeZipBytes {
    $probeMemory = [IO.MemoryStream]::new()
    try {
        $probeArchive = [IO.Compression.ZipArchive]::new($probeMemory, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            $probeEntry = $probeArchive.CreateEntry('fixture.txt', [IO.Compression.CompressionLevel]::NoCompression)
            $probeStream = $probeEntry.Open()
            try {
                $probeBytes = [Text.Encoding]::UTF8.GetBytes('Synthetic resource probe; this is not a NuGet package.')
                $probeStream.Write($probeBytes, 0, $probeBytes.Length)
            }
            finally { $probeStream.Dispose() }
        }
        finally { $probeArchive.Dispose() }
        return ,$probeMemory.ToArray()
    }
    finally { $probeMemory.Dispose() }
}

function Test-ProbeArchiveClosed([IO.Compression.ZipArchive]$Archive) {
    if ($null -eq $Archive) { return $null }
    # GetEntry may return a cached lookup after Dispose. Call the Entries accessor explicitly:
    # PowerShell property access can suppress its disposed exception to null.
    try { [void]$Archive.get_Entries() }
    catch {
        if ($_.Exception.GetBaseException() -is [ObjectDisposedException]) { return $true }
        throw
    }
    return $false
}

function Test-ProbeExclusiveRead([string]$Path) {
    $probeFile = $null
    try {
        $probeFile = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
        return $true
    }
    catch [IO.IOException] { return $false }
    finally { if ($null -ne $probeFile) { $probeFile.Dispose() } }
}

function Invoke-ProbeCase([string]$Case, [string]$Root, [string]$VerifyScript) {
    # Probe variables use their own prefix: the real script is dot-sourced only to keep
    # its archive references alive for post-error resource assertions, without rewriting its logic.
    $probeCaseDirectory = Join-Path $Root $Case
    $probeFeedDirectory = Join-Path $probeCaseDirectory 'synthetic-feed'
    # Evidence may live in repository artifacts; the real verifier still requires
    # its independent public workspace outside the source tree, so use a fresh TEMP leaf.
    $probeWorkDirectory = Join-Path ([IO.Path]::GetTempPath()) ('storage-published-resource-probe-' + $Case + '-' + [Guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($probeFeedDirectory)
    $probeVersion = '0.3.0-resource-probe'
    $probeIds = @('Atelia.Primitives', 'Atelia.Data', 'Atelia.Rbf', 'Atelia.Binary', 'Atelia.FrameStore', 'Atelia.HeadStore')
    [byte[]]$probeValidZip = New-ProbeZipBytes
    [byte[]]$probeBadZip = [Text.Encoding]::UTF8.GetBytes('Resource probe: deliberately not a ZIP archive.')
    $probeRecords = @($probeIds | ForEach-Object {
        $probeId = $_
        $probeRecord = [ordered]@{ id = $probeId }
        foreach ($probeKind in @('nupkg', 'snupkg')) {
            $probeFilename = "$probeId.$probeVersion.$probeKind"
            $probeArtifactPath = Join-Path $probeFeedDirectory $probeFilename
            [byte[]]$probeContents = if ($Case -ceq 'bad-candidate-zip' -and $probeId -ceq $probeIds[0] -and $probeKind -ceq 'nupkg') { $probeBadZip } else { $probeValidZip }
            [IO.File]::WriteAllBytes($probeArtifactPath, $probeContents)
            $probeHash = (Get-FileHash -LiteralPath $probeArtifactPath -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($probeKind -ceq 'nupkg') { $probeRecord.file = $probeFilename; $probeRecord.sha256 = $probeHash }
            else { $probeRecord.symbolsFile = $probeFilename; $probeRecord.symbolsSha256 = $probeHash }
        }
        $probeRecord
    })
    $probeManifest = [ordered]@{
        schemaVersion = 1; version = $probeVersion; sourceRevision = '1111111111111111111111111111111111111111'
        repositoryUrl = 'https://github.com/Atelia-org/atelia-storage'; packages = $probeRecords
    }
    $probeManifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $probeFeedDirectory "manifest.$probeVersion.json") -Encoding utf8NoBOM
    $probeCommands = [pscustomobject]@{
        Downloads = 0; DotnetCalls = 0; WaitCalls = 0
        Contents = if ($Case -ceq 'bad-public-zip') { $probeBadZip } else { $probeValidZip }
    }
    function Invoke-WebRequest {
        [CmdletBinding()]
        param([Parameter(Mandatory)][string]$Uri, [Parameter(Mandatory)][string]$OutFile, [int]$TimeoutSec)
        $probeCommands.Downloads++
        Assert-Probe ($probeCommands.Downloads -eq 1) 'The fixture unexpectedly requested a second download.'
        Assert-Probe ($Uri -ceq "https://api.nuget.org/v3-flatcontainer/atelia.primitives/$probeVersion/atelia.primitives.$probeVersion.nupkg") 'Unexpected mocked download URI.'
        Assert-Probe ($TimeoutSec -eq 30) 'Unexpected mocked download timeout.'
        [IO.File]::WriteAllBytes($OutFile, [byte[]]$probeCommands.Contents)
    }
    function dotnet {
        $probeCommands.DotnetCalls++
        throw 'Resource probes must stop before dotnet; no dotnet executable is invoked.'
    }
    function Start-Sleep {
        $probeCommands.WaitCalls++
        throw 'The local download fixture unexpectedly reached a retry; no waiting is permitted.'
    }

    $probeObserved = $null
    try { . $VerifyScript -Version $probeVersion -FeedDirectory $probeFeedDirectory -WorkDirectory $probeWorkDirectory }
    catch { $probeObserved = $_ }
    $probeHeldCandidate = Get-Variable -Name candidateArchive -Scope Local -ValueOnly -ErrorAction SilentlyContinue
    $probeHeldPublic = Get-Variable -Name publicArchive -Scope Local -ValueOnly -ErrorAction SilentlyContinue
    $probeCandidatePath = Join-Path $probeFeedDirectory "Atelia.Primitives.$probeVersion.nupkg"
    $probePublicPath = Join-Path $probeWorkDirectory "Atelia.Primitives.$probeVersion.nupkg"
    $probeResult = [ordered]@{
        case = $Case; passed = $false; failure = ''
        feedDirectory = $probeFeedDirectory; workDirectory = $probeWorkDirectory
        observedErrorType = if ($null -ne $probeObserved) { $probeObserved.Exception.GetBaseException().GetType().FullName } else { '' }
        observedError = if ($null -ne $probeObserved) { $probeObserved.Exception.Message } else { '' }
        candidateClosed = $null; publicClosed = $null
        candidateExclusiveWindows = $null; publicExclusiveWindows = $null
        downloads = $probeCommands.Downloads; dotnetCalls = $probeCommands.DotnetCalls; waitCalls = $probeCommands.WaitCalls
    }
    try {
        $probeResult.candidateClosed = Test-ProbeArchiveClosed $probeHeldCandidate
        $probeResult.publicClosed = Test-ProbeArchiveClosed $probeHeldPublic
        if ([OperatingSystem]::IsWindows()) {
            $probeResult.candidateExclusiveWindows = Test-ProbeExclusiveRead $probeCandidatePath
            $probeResult.publicExclusiveWindows = Test-ProbeExclusiveRead $probePublicPath
        }
        Assert-Probe ($null -ne $probeObserved) 'The real verification script did not reject the fixture.'
        Assert-Probe ($probeCommands.Downloads -eq 1 -and $probeCommands.DotnetCalls -eq 0 -and $probeCommands.WaitCalls -eq 0) 'The resource case reached an unexpected external-operation phase.'
        switch ($Case) {
            'bad-public-zip' {
                Assert-Probe ($probeObserved.Exception.GetBaseException() -is [IO.InvalidDataException]) 'The public ZIP parsing error was replaced by another failure.'
                Assert-Probe ($null -ne $probeHeldCandidate -and $null -eq $probeHeldPublic) 'The fixture did not fail while acquiring the second archive.'
                Assert-Probe ($probeResult.candidateClosed -eq $true) 'The candidate archive remained open after public ZIP acquisition failed.'
            }
            'bad-candidate-zip' {
                Assert-Probe ($probeObserved.Exception.GetBaseException() -is [IO.InvalidDataException]) 'The first ZIP parsing error was replaced, for example by null Dispose.'
                Assert-Probe ($null -eq $probeHeldCandidate -and $null -eq $probeHeldPublic) 'The fixture unexpectedly acquired an archive after candidate ZIP parsing failed.'
            }
            'missing-signature' {
                Assert-Probe ($probeObserved.Exception.Message -ceq 'Downloaded public package is not repository signed: Atelia.Primitives') 'The original signature comparison rejection was replaced.'
                Assert-Probe ($probeResult.candidateClosed -eq $true -and $probeResult.publicClosed -eq $true) 'Comparison rejection left an acquired archive open.'
            }
            default { throw "Unknown resource probe: $Case" }
        }
        if ([OperatingSystem]::IsWindows()) {
            Assert-Probe ($probeResult.candidateExclusiveWindows -eq $true -and $probeResult.publicExclusiveWindows -eq $true) 'A rejected fixture retained a file handle that prevents Windows exclusive reopening.'
        }
        $probeResult.passed = $true
    }
    catch { $probeResult.failure = $_.Exception.Message }
    finally {
        # The old implementation intentionally stays alive for the red assertions above.
        # Explicitly close its leaked handle only afterwards; retain every fixture file.
        try { if ($null -ne $probeHeldCandidate) { $probeHeldCandidate.Dispose() } }
        finally { if ($null -ne $probeHeldPublic) { $probeHeldPublic.Dispose() } }
    }
    return [pscustomobject]$probeResult
}

function New-ProbePackageBytes([string]$Id, [string]$Version, [string]$Revision, [string]$Case, [bool]$Public) {
    # Include executable build assets and OPC metadata outside the old five-asset comparison.
    $probeEntries = @(
        [pscustomobject]@{ Name = "$Id.nuspec"; Text = "<package><metadata><id>$Id</id><version>$Version</version><repository type=`"git`" url=`"https://github.com/Atelia-org/atelia-storage`" commit=`"$Revision`" /></metadata></package>" },
        [pscustomobject]@{ Name = 'LICENSE'; Text = 'Synthetic license bytes' },
        [pscustomobject]@{ Name = 'README.md'; Text = 'Synthetic README bytes' },
        [pscustomobject]@{ Name = "lib/net10.0/$Id.dll"; Text = 'Synthetic assembly bytes, not a real DLL' },
        [pscustomobject]@{ Name = "lib/net10.0/$Id.xml"; Text = '<doc />' },
        [pscustomobject]@{ Name = "buildTransitive/$Id.targets"; Text = '<Project />' },
        [pscustomobject]@{ Name = '[Content_Types].xml'; Text = '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types" />' },
        [pscustomobject]@{ Name = '_rels/.rels'; Text = '<Relationships />' },
        [pscustomobject]@{ Name = 'package/services/metadata/core-properties/nuget.psmdcp'; Text = '<coreProperties />' }
    )
    if ($Id -ceq 'Atelia.Primitives') {
        if ($Case -ceq 'both-missing-required') { $probeEntries = @($probeEntries | Where-Object Name -CNE 'README.md') }
        if ($Public) {
            switch ($Case) {
                'extra-entry' { $probeEntries += [pscustomobject]@{ Name = 'buildTransitive/Injected.targets'; Text = '<Project><Target Name="Injected" /></Project>' } }
                'deleted-entry' { $probeEntries = @($probeEntries | Where-Object Name -CNE "buildTransitive/$Id.targets") }
                'changed-entry' {
                    $probeEntries = @($probeEntries | ForEach-Object {
                        if ($_.Name -ceq "buildTransitive/$Id.targets") { [pscustomobject]@{ Name = $_.Name; Text = '<Project><Target Name="Changed" /></Project>' } }
                        else { $_ }
                    })
                }
                'changed-opc-metadata' {
                    $probeEntries = @($probeEntries | ForEach-Object {
                        if ($_.Name -ceq '[Content_Types].xml') { [pscustomobject]@{ Name = $_.Name; Text = '<Types />' } }
                        else { $_ }
                    })
                }
                'case-changed-entry' {
                    $probeEntries = @($probeEntries | ForEach-Object {
                        if ($_.Name -ceq "buildTransitive/$Id.targets") { [pscustomobject]@{ Name = "buildTransitive/$($Id.ToLowerInvariant()).targets"; Text = $_.Text } }
                        else { $_ }
                    })
                }
                'public-duplicate' { $probeEntries += [pscustomobject]@{ Name = "lib/net10.0/$Id.dll"; Text = 'Synthetic assembly bytes, not a real DLL' } }
            }
        }
        elseif ($Case -ceq 'candidate-duplicate') {
            $probeEntries += [pscustomobject]@{ Name = "lib/net10.0/$Id.dll"; Text = 'Different second DLL entry' }
        }
        elseif ($Case -ceq 'candidate-signed') {
            $probeEntries += [pscustomobject]@{ Name = '.signature.p7s'; Text = 'Synthetic candidate signature marker' }
        }
    }
    if ($Public) {
        $probeSignature = if ($Case -ceq 'empty-signature' -and $Id -ceq 'Atelia.Primitives') { '' } else { 'Nonempty synthetic signature marker; no valid signature is claimed.' }
        $probeEntries += [pscustomobject]@{ Name = '.signature.p7s'; Text = $probeSignature }
        if ($Case -ceq 'duplicate-signature' -and $Id -ceq 'Atelia.Primitives') {
            $probeEntries += [pscustomobject]@{ Name = '.signature.p7s'; Text = 'Second synthetic signature marker' }
        }
        [Array]::Reverse($probeEntries)
    }
    $probeMemory = [IO.MemoryStream]::new()
    try {
        $probeArchive = [IO.Compression.ZipArchive]::new($probeMemory, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            foreach ($probeItem in $probeEntries) {
                # The control changes ZIP entry order, compression and timestamps without changing payloads.
                $probeCompression = if ($Public -and $probeItem.Name -cne '.signature.p7s') { [IO.Compression.CompressionLevel]::Optimal } else { [IO.Compression.CompressionLevel]::NoCompression }
                $probeEntry = $probeArchive.CreateEntry($probeItem.Name, $probeCompression)
                $probeEntry.LastWriteTime = if ($Public) { [DateTimeOffset]::new(2022, 1, 1, 0, 0, 0, [TimeSpan]::Zero) } else { [DateTimeOffset]::new(2001, 1, 1, 0, 0, 0, [TimeSpan]::Zero) }
                $probeStream = $probeEntry.Open()
                try {
                    $probeBytes = [Text.Encoding]::UTF8.GetBytes($probeItem.Text)
                    $probeStream.Write($probeBytes, 0, $probeBytes.Length)
                }
                finally { $probeStream.Dispose() }
            }
        }
        finally { $probeArchive.Dispose() }
        return ,$probeMemory.ToArray()
    }
    finally { $probeMemory.Dispose() }
}

function Invoke-ProbeIntegrityCase([string]$Case, [string]$Root, [string]$VerifyScript) {
    $probeFeedDirectory = Join-Path (Join-Path $Root $Case) 'synthetic-feed'
    [void][IO.Directory]::CreateDirectory($probeFeedDirectory)
    $probeWorkDirectory = Join-Path ([IO.Path]::GetTempPath()) ('storage-published-integrity-probe-' + $Case + '-' + [Guid]::NewGuid().ToString('N'))
    $probeVersion = '0.3.0-integrity-probe'
    $probeRevision = '1111111111111111111111111111111111111111'
    $probeIds = @('Atelia.Primitives', 'Atelia.Data', 'Atelia.Rbf', 'Atelia.Binary', 'Atelia.FrameStore', 'Atelia.HeadStore')
    $probePayloads = [Collections.Generic.Dictionary[string, byte[]]]::new([StringComparer]::Ordinal)
    $probeRecords = @(
        foreach ($probeId in $probeIds) {
            [byte[]]$probeCandidate = New-ProbePackageBytes $probeId $probeVersion $probeRevision $Case $false
            $probeNormalizedId = $probeId.ToLowerInvariant()
            $probeUrl = "https://api.nuget.org/v3-flatcontainer/$probeNormalizedId/$probeVersion/$probeNormalizedId.$probeVersion.nupkg"
            $probePayloads.Add($probeUrl, (New-ProbePackageBytes $probeId $probeVersion $probeRevision $Case $true))
            $probeRecord = [ordered]@{ id = $probeId }
            foreach ($probeKind in @('nupkg', 'snupkg')) {
                $probeFilename = "$probeId.$probeVersion.$probeKind"
                $probeArtifactPath = Join-Path $probeFeedDirectory $probeFilename
                [IO.File]::WriteAllBytes($probeArtifactPath, $probeCandidate)
                $probeHash = (Get-FileHash -LiteralPath $probeArtifactPath -Algorithm SHA256).Hash.ToLowerInvariant()
                if ($probeKind -ceq 'nupkg') { $probeRecord.file = $probeFilename; $probeRecord.sha256 = $probeHash }
                else { $probeRecord.symbolsFile = $probeFilename; $probeRecord.symbolsSha256 = $probeHash }
            }
            $probeRecord
        }
    )
    [ordered]@{
        schemaVersion = 1; version = $probeVersion; sourceRevision = $probeRevision
        repositoryUrl = 'https://github.com/Atelia-org/atelia-storage'; packages = $probeRecords
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $probeFeedDirectory "manifest.$probeVersion.json") -Encoding utf8NoBOM
    $probeCommands = [pscustomobject]@{ Downloads = 0; DotnetCalls = 0; WaitCalls = 0 }
    function Invoke-WebRequest {
        [CmdletBinding()]
        param([Parameter(Mandatory)][string]$Uri, [Parameter(Mandatory)][string]$OutFile, [int]$TimeoutSec)
        Assert-Probe ($probePayloads.ContainsKey($Uri) -and $TimeoutSec -eq 30) 'Unexpected mocked download.'
        $probeCommands.Downloads++
        [IO.File]::WriteAllBytes($OutFile, $probePayloads[$Uri])
    }
    function dotnet {
        $probeCommands.DotnetCalls++
        Assert-Probe ($args[0] -ceq 'restore') 'The integrity fixture unexpectedly reached another dotnet phase.'
        throw 'Integrity probe reached consumer restore; no dotnet executable is invoked.'
    }
    function Start-Sleep {
        $probeCommands.WaitCalls++
        throw 'No retry or waiting expected in the local integrity probe.'
    }
    $probePreviousPackages = $env:NUGET_PACKAGES
    $probePreviousHttpCache = $env:NUGET_HTTP_CACHE_PATH
    $probePreviousLocation = (Get-Location).Path
    $probeObserved = $null
    try { . $VerifyScript -Version $probeVersion -FeedDirectory $probeFeedDirectory -WorkDirectory $probeWorkDirectory }
    catch { $probeObserved = $_ }
    $probeHeldCandidate = Get-Variable -Name candidateArchive -Scope Local -ValueOnly -ErrorAction SilentlyContinue
    $probeHeldPublic = Get-Variable -Name publicArchive -Scope Local -ValueOnly -ErrorAction SilentlyContinue
    $probeResult = [ordered]@{
        case = $Case; passed = $false; failure = ''
        feedDirectory = $probeFeedDirectory; workDirectory = $probeWorkDirectory
        observedError = if ($null -ne $probeObserved) { $probeObserved.Exception.Message } else { '' }
        downloads = $probeCommands.Downloads; dotnetCalls = $probeCommands.DotnetCalls; waitCalls = $probeCommands.WaitCalls
        candidateClosed = Test-ProbeArchiveClosed $probeHeldCandidate; publicClosed = Test-ProbeArchiveClosed $probeHeldPublic
    }
    try {
        Assert-Probe ($null -ne $probeObserved) 'The real verifier unexpectedly finished the synthetic package fixture.'
        Assert-Probe ($probeCommands.WaitCalls -eq 0) 'Integrity verification unexpectedly retried the local download.'
        Assert-Probe ($probeResult.candidateClosed -eq $true -and $probeResult.publicClosed -eq $true) 'Integrity verification retained a package archive.'
        Assert-Probe ($env:NUGET_PACKAGES -eq $probePreviousPackages -and $env:NUGET_HTTP_CACHE_PATH -eq $probePreviousHttpCache -and (Get-Location).Path -ceq $probePreviousLocation) 'Integrity verification did not restore its environment.'
        if ($Case -ceq 'reordered-compressed-control') {
            Assert-Probe ($probeCommands.Downloads -eq 6 -and $probeCommands.DotnetCalls -eq 1) 'Equivalent entry payloads did not pass all six archive checks.'
            Assert-Probe ($probeResult.observedError -ceq 'Integrity probe reached consumer restore; no dotnet executable is invoked.') 'The positive control stopped before consumer restore.'
        }
        else {
            Assert-Probe ($probeCommands.Downloads -eq 1 -and $probeCommands.DotnetCalls -eq 0) 'An altered archive reached another package or consumer restore.'
            $probeExpected = switch ($Case) {
                'candidate-duplicate' { 'Duplicate candidate package entry: lib/net10.0/Atelia.Primitives.dll' }
                'public-duplicate' { 'Duplicate public package entry: lib/net10.0/Atelia.Primitives.dll' }
                'duplicate-signature' { 'Duplicate public package entry: .signature.p7s' }
                'empty-signature' { 'Downloaded public package is not repository signed: Atelia.Primitives' }
                'candidate-signed' { 'Candidate is already signed: Atelia.Primitives' }
                'both-missing-required' { 'Missing package asset: README.md' }
                'changed-entry' { 'Published package asset differs from candidate: buildTransitive/Atelia.Primitives.targets' }
                'changed-opc-metadata' { 'Published package asset differs from candidate: [Content_Types].xml' }
                default { 'Published package entry set differs from candidate: Atelia.Primitives' }
            }
            Assert-Probe ($probeResult.observedError -ceq $probeExpected) 'The altered archive did not fail at its intended integrity guard.'
        }
        if ([OperatingSystem]::IsWindows()) {
            foreach ($probePath in @((Join-Path $probeFeedDirectory "Atelia.Primitives.$probeVersion.nupkg"), (Join-Path $probeWorkDirectory "Atelia.Primitives.$probeVersion.nupkg"))) {
                Assert-Probe (Test-ProbeExclusiveRead $probePath) 'Integrity verification retained a Windows file handle.'
            }
        }
        $probeResult.passed = $true
    }
    catch { $probeResult.failure = $_.Exception.Message }
    finally {
        try { if ($null -ne $probeHeldCandidate) { $probeHeldCandidate.Dispose() } }
        finally { if ($null -ne $probeHeldPublic) { $probeHeldPublic.Dispose() } }
    }
    return [pscustomobject]$probeResult
}

$probeResults = @(
    foreach ($probeCase in @('bad-public-zip', 'bad-candidate-zip', 'missing-signature')) {
        Invoke-ProbeCase $probeCase $probeEvidence $probeVerifyScript
    }
    foreach ($probeCase in @('reordered-compressed-control', 'extra-entry', 'deleted-entry', 'changed-entry', 'changed-opc-metadata', 'case-changed-entry', 'candidate-duplicate', 'public-duplicate', 'duplicate-signature', 'empty-signature', 'candidate-signed', 'both-missing-required')) {
        Invoke-ProbeIntegrityCase $probeCase $probeEvidence $probeVerifyScript
    }
)
[ordered]@{
    schemaVersion = 1; operatingSystem = [Environment]::OSVersion.ToString()
    verificationScriptSha256 = (Get-FileHash -LiteralPath $probeVerifyScript -Algorithm SHA256).Hash.ToLowerInvariant()
    cases = $probeResults
    scope = 'Local synthetic archive resources and complete entry integrity. HTTP and dotnet are mocked; no cryptographic signature, package consumption or publication is proven. Entry-stream acquisition/Dispose failure is not dynamically injected.'
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $probeEvidence 'result.json') -Encoding utf8NoBOM
$probeFailures = @($probeResults | Where-Object { !$_.passed })
if ($probeFailures.Count) {
    throw "Verify-Published archive tests failed: $($probeFailures.case -join ', '). Evidence: $probeEvidence/result.json"
}
Write-Host "MOCK Verify-Published archive tests passed: $($probeResults.Count) resource/integrity cases, no HTTP or dotnet. Evidence: $probeEvidence"
