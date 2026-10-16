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

$probeResults = @(
    foreach ($probeCase in @('bad-public-zip', 'bad-candidate-zip', 'missing-signature')) {
        Invoke-ProbeCase $probeCase $probeEvidence $probeVerifyScript
    }
)
[ordered]@{
    schemaVersion = 1; operatingSystem = [Environment]::OSVersion.ToString()
    verificationScriptSha256 = (Get-FileHash -LiteralPath $probeVerifyScript -Algorithm SHA256).Hash.ToLowerInvariant()
    cases = $probeResults
    scope = 'Local synthetic archive resources only. Downloads are mocked; no HTTP, dotnet, package consumption or publication. Entry-stream acquisition/Dispose failure is not dynamically injected.'
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $probeEvidence 'result.json') -Encoding utf8NoBOM
$probeFailures = @($probeResults | Where-Object { !$_.passed })
if ($probeFailures.Count) {
    throw "Verify-Published resource tests failed: $($probeFailures.case -join ', '). Evidence: $probeEvidence/result.json"
}
Write-Host "MOCK Verify-Published resource tests passed: three acquisition/comparison cases, no HTTP or dotnet. Evidence: $probeEvidence"
