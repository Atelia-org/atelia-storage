#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateSet('All', 'Bounded', 'Legacy')][string]$Matrix = 'All',
    [ValidateSet('empty', 'events-only', 'complex', 'empty-active')][string]$LegacyFixture = 'complex',
    [string]$DotnetPath = 'dotnet',
    [ValidateRange(1, 600)][int]$ReadyTimeoutSeconds = 60,
    [ValidateRange(1, 120)][int]$ExitTimeoutSeconds = 15,
    [ValidateRange(1, 1800)][int]$VerificationTimeoutSeconds = 120
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (!$IsWindows) { throw 'This entry requires native Windows PowerShell 7; Linux SIGKILL uses the Python entries.' }
$repo = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
$boundedDll = Join-Path $repo 'tests/EventJournal.Validation/bin/Release/net10.0/Atelia.EventJournal.Validation.dll'
$legacyDll = Join-Path $repo 'eng/LegacyUpgradeValidation/bin/Release/net10.0/Atelia.EventJournal.LegacyUpgradeValidation.dll'
$toolkitDll = Join-Path $repo 'tools/EventJournal.Toolkit/bin/Release/net10.0/Atelia.EventJournal.Toolkit.dll'
$fixtureRoot = Join-Path $repo 'tests/EventJournal.Toolkit.Tests/LegacyFixtures'

function Assert-Condition([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw $Message }
}
function Write-JsonFile([string]$Path, [object]$Value) {
    [IO.File]::WriteAllText($Path, (ConvertTo-Json -InputObject $Value -Depth 40), [Text.UTF8Encoding]::new($false))
}
function Get-SortedStrings([object[]]$Values) {
    [string[]]$items = @($Values | ForEach-Object { [string]$_ })
    [Array]::Sort($items, [StringComparer]::Ordinal)
    return ,$items
}
function Assert-StringArrays([object[]]$Left, [object[]]$Right, [string]$Context) {
    Assert-Condition ($Left.Count -eq $Right.Count) "$Context count mismatch."
    for ($i = 0; $i -lt $Left.Count; $i++) {
        Assert-Condition ([string]::Equals([string]$Left[$i], [string]$Right[$i], [StringComparison]::Ordinal)) "$Context order or value mismatch."
    }
}
function Assert-NoReparsePath([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if ([IO.File]::Exists($current) -or [IO.Directory]::Exists($current)) {
            Assert-Condition (([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'Reparse paths are not supported.'
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
}
function Get-RelativeSafePath([string]$Root, [string]$Relative) {
    Assert-Condition (![string]::IsNullOrEmpty($Relative)) 'Empty relative path.'
    Assert-Condition (!$Relative.Contains([char]92) -and ![IO.Path]::IsPathRooted($Relative)) 'Manifest path must be relative with / separators.'
    $parts = $Relative.Split('/')
    Assert-Condition (@($parts | Where-Object { $_ -eq '..' -or $_ -eq '.' -or $_ -eq '' -or $_.Contains(':') }).Count -eq 0) 'Unsafe relative path.'
    $path = [IO.Path]::GetFullPath((Join-Path $Root $Relative))
    $prefix = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Root)) + [IO.Path]::DirectorySeparatorChar
    Assert-Condition ($path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) 'Relative path escapes its root.'
    return $path
}
function Get-FileSha256([string]$Path) {
    $stream = [IO.FileStream]::new($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
    finally { $algorithm.Dispose(); $stream.Dispose() }
}
function Get-Inventory([string]$Root) {
    Assert-NoReparsePath $Root
    Assert-Condition ([IO.Directory]::Exists($Root)) 'Inventory root does not exist.'
    $directories = [Collections.Generic.List[string]]::new()
    $directories.Add('.')
    $files = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    $queue = [Collections.Generic.Queue[string]]::new()
    $queue.Enqueue($Root)
    while ($queue.Count) {
        $directory = $queue.Dequeue()
        foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($directory)) {
            $attributes = [IO.File]::GetAttributes($entry)
            Assert-Condition (($attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'Reparse inventory entry.'
            $relative = [IO.Path]::GetRelativePath($Root, $entry).Replace([char]92, [char]47)
            if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) {
                $directories.Add($relative)
                $queue.Enqueue($entry)
            }
            else {
                $files.Add($relative, [ordered]@{ length = [IO.FileInfo]::new($entry).Length; sha256 = Get-FileSha256 $entry })
            }
        }
    }
    return [pscustomobject]@{ Directories = Get-SortedStrings $directories.ToArray(); Files = $files }
}
function Assert-Inventory([object]$Expected, [object]$Actual, [string]$Context) {
    Assert-StringArrays $Expected.Directories $Actual.Directories "$Context directories"
    Assert-Condition ($Expected.Files.Count -eq $Actual.Files.Count) "$Context file count mismatch."
    foreach ($relative in $Expected.Files.Keys) {
        Assert-Condition ($Actual.Files.ContainsKey($relative)) "$Context file missing."
        Assert-Condition ([long]$Expected.Files[$relative].length -eq [long]$Actual.Files[$relative].length) "$Context file length mismatch."
        Assert-Condition ([string]::Equals($Expected.Files[$relative].sha256, $Actual.Files[$relative].sha256, [StringComparison]::Ordinal)) "$Context file hash mismatch."
    }
}
function Save-Inventory([string]$Path, [object]$Inventory) {
    $ordered = [ordered]@{}
    foreach ($relative in (Get-SortedStrings @($Inventory.Files.Keys))) { $ordered.Add($relative, $Inventory.Files[$relative]) }
    Write-JsonFile $Path ([ordered]@{ directories = $Inventory.Directories; files = $ordered })
}
function New-HarnessProcess([string[]]$Arguments, [hashtable]$Environment = @{}) {
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $DotnetPath
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.CreateNoWindow = $true
    $info.WorkingDirectory = $repo
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    foreach ($name in $Environment.Keys) { $info.Environment[$name] = [string]$Environment[$name] }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    [void]$process.Start()
    return $process
}
function Read-OneJson([string]$Text, [string]$Context) {
    Assert-Condition (![string]::IsNullOrWhiteSpace($Text)) "$Context emitted empty stdout."
    # Parse the complete document: toolkit audit emits pretty multiline JSON.
    # JsonDocument rejects trailing non-JSON content and a second JSON document.
    $document = [Text.Json.JsonDocument]::Parse($Text)
    try {
        Assert-Condition ($document.RootElement.ValueKind -eq [Text.Json.JsonValueKind]::Object) "$Context must emit one JSON object."
        return (ConvertFrom-Json -InputObject $Text -AsHashtable)
    }
    finally { $document.Dispose() }
}
function Invoke-Harness([string[]]$Arguments, [string]$LogPrefix) {
    $process = New-HarnessProcess $Arguments
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    try {
        Assert-Condition ($process.WaitForExit($VerificationTimeoutSeconds * 1000)) 'Verification process timed out.'
        Assert-Condition ($stdoutTask.Wait($ExitTimeoutSeconds * 1000) -and $stderrTask.Wait($ExitTimeoutSeconds * 1000)) 'Verification stream drain timed out.'
        Assert-Condition ($process.ExitCode -eq 0) 'Verification process failed; inspect its stdout/stderr logs.'
        return (Read-OneJson $stdoutTask.Result 'Verification')
    }
    finally {
        if (!$process.HasExited) { $process.Kill(); [void]$process.WaitForExit($ExitTimeoutSeconds * 1000) }
        if ($stdoutTask.Wait($ExitTimeoutSeconds * 1000)) { [IO.File]::WriteAllText("$LogPrefix.stdout.log", $stdoutTask.Result) }
        if ($stderrTask.Wait($ExitTimeoutSeconds * 1000)) { [IO.File]::WriteAllText("$LogPrefix.stderr.log", $stderrTask.Result) }
        $process.Dispose()
    }
}
function Invoke-KillAtReady([string[]]$Arguments, [string]$Phase, [string]$CaseDirectory, [hashtable]$Environment = @{}) {
    $process = New-HarnessProcess $Arguments $Environment
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $readyTask = $process.StandardOutput.ReadLineAsync()
    $stdoutTask = $null
    $readyLine = ''
    $killCalled = $false
    try {
        Assert-Condition ($readyTask.Wait($ReadyTimeoutSeconds * 1000)) 'Kill-child readiness timed out.'
        $readyLine = $readyTask.Result
        Assert-Condition (![string]::IsNullOrWhiteSpace($readyLine)) 'Kill-child ended before readiness; inspect its stderr log.'
        $event = ConvertFrom-Json -InputObject $readyLine -AsHashtable
        Assert-Condition ($event.status -eq 'ReadyToKill' -and $event.phase -ceq $Phase) 'Expected phase readiness was not reached.'
        Assert-Condition (!$process.HasExited) 'Kill-child exited before Process.Kill.'
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $process.Kill()
        $killCalled = $true
        Assert-Condition ($process.WaitForExit($ExitTimeoutSeconds * 1000)) 'Killed process did not exit within timeout.'
        Assert-Condition ($process.ExitCode -ne 0) 'Process.Kill unexpectedly produced a success exit code.'
        return [ordered]@{ mechanism = 'native Windows Process.Kill after exact internal phase readiness'; processKillCalled = $killCalled; exitCode = $process.ExitCode; processId = $process.Id; powerLossProof = $false }
    }
    finally {
        if (!$process.HasExited) { $process.Kill(); [void]$process.WaitForExit($ExitTimeoutSeconds * 1000) }
        if (!$stdoutTask -and $readyTask.IsCompleted) { $stdoutTask = $process.StandardOutput.ReadToEndAsync() }
        $remaining = ''
        if ($stdoutTask -and $stdoutTask.Wait($ExitTimeoutSeconds * 1000)) { $remaining = $stdoutTask.Result }
        [IO.File]::WriteAllText((Join-Path $CaseDirectory 'child.stdout.log'), [string]$readyLine + [Environment]::NewLine + $remaining)
        if ($stderrTask.Wait($ExitTimeoutSeconds * 1000)) { [IO.File]::WriteAllText((Join-Path $CaseDirectory 'child.stderr.log'), $stderrTask.Result) }
        Write-JsonFile (Join-Path $CaseDirectory 'termination.json') ([ordered]@{ processKillCalled = $killCalled; exited = $process.HasExited; exitCode = $(if ($process.HasExited) { $process.ExitCode } else { $null }) })
        $process.Dispose()
    }
}
function Assert-CheckpointPublication([string]$Journal, [string]$Phase) {
    # Read only fixed header witnesses; the existing strict-reopen console validates
    # the complete snapshot CRC, anchor and branch identity using production codecs.
    $header = [IO.File]::ReadAllBytes((Join-Path $Journal 'refs/catalog.snapshot'))
    Assert-Condition ([BitConverter]::IsLittleEndian -and $header.Length -ge 68) 'Unsupported snapshot witness encoding.'
    Assert-Condition ([Text.Encoding]::ASCII.GetString($header, 0, 4) -ceq 'EJCS' -and [BitConverter]::ToUInt16($header, 6) -eq 64) 'Unexpected snapshot witness header.'
    $boundary = [BitConverter]::ToUInt64($header, 16)
    $branches = [BitConverter]::ToUInt32($header, 36)
    $tags = [BitConverter]::ToUInt32($header, 40)
    $published = $Phase -in @('CheckpointAfterReplace', 'CheckpointBeforeInstall')
    $expectedBoundary = if ($published) { 260 } else { 4 }
    $expectedBranches = if ($published) { 1 } else { 0 }
    Assert-Condition ($boundary -eq $expectedBoundary -and $branches -eq $expectedBranches -and $tags -eq 0) 'Killed checkpoint published-prefix mismatch.'
    Assert-Condition ([IO.FileInfo]::new((Join-Path $Journal 'refs/ref-op-log.rbf')).Length -eq 260) 'Checkpoint fixture log boundary changed.'
    return [ordered]@{ boundary = $boundary; branchCount = $branches; tagCount = $tags; replacementPublished = $published; completeCodecValidation = 'StrictReopenPassed' }
}
function Restore-LegacyFixture([string]$Kind, [string]$Destination) {
    $provenance = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText((Join-Path $fixtureRoot 'provenance.json'))) -AsHashtable
    foreach ($relative in $provenance.fixtureDirectories[$Kind]) {
        [void][IO.Directory]::CreateDirectory((Get-RelativeSafePath $Destination $relative))
    }
    $fixedRoot = Join-Path $fixtureRoot $Kind
    foreach ($entry in $provenance.fixtures[$Kind]) {
        if (!$entry.relativePath.StartsWith('journal/', [StringComparison]::Ordinal)) { continue }
        $from = Get-RelativeSafePath $fixedRoot $entry.relativePath
        Assert-Condition ([IO.FileInfo]::new($from).Length -eq [long]$entry.length -and (Get-FileSha256 $from) -ceq $entry.sha256) 'Fixed legacy fixture provenance mismatch.'
        $to = Get-RelativeSafePath $Destination $entry.relativePath
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($to))
        [IO.File]::Copy($from, $to, $false)
    }
    return (Join-Path $Destination 'journal')
}
function Assert-LegacyManifest([string]$Bundle, [string]$Source, [string]$CaseDirectory) {
    $manifest = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText((Join-Path $Bundle 'manifest.json'))) -AsHashtable
    Assert-Condition ($manifest.completed -eq $true -and $manifest.factsCopiedByteExactly -eq $true) 'Missing completed byte-exact manifest.'
    Assert-Condition ($manifest.schemaVersion -eq 1 -and $manifest.kind -ceq 'EventJournalLegacyUpgrade') 'Wrong upgrade manifest schema.'
    Assert-Condition ($manifest.sourceProfile -ceq 'legacy-bb7c4fb' -and $manifest.targetLayoutVersion -eq 2) 'Wrong upgrade manifest profile/layout.'
    Assert-Condition ($manifest.profileBaselineRevision -ceq 'bb7c4fb3eb6477783c70ee61bc62b832be195d07') 'Wrong profile baseline.'
    foreach ($name in @('sourceFullScan', 'targetFullAudit', 'dailyReadCheck', 'sourceUnchanged')) {
        Assert-Condition ($manifest.validation[$name] -ceq 'Passed') 'Incomplete manifest validation.'
    }
    $sourceInventory = Get-Inventory $Source
    $targetRoot = Join-Path $Bundle 'journal'
    $targetInventory = Get-Inventory $targetRoot
    Assert-StringArrays $sourceInventory.Directories $manifest.sourceDirectories 'Manifest source directories'
    Assert-StringArrays $targetInventory.Directories $manifest.targetDirectories 'Manifest target directories'
    $sourceFiles = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($entry in $manifest.sourceFiles) {
        [void](Get-RelativeSafePath $Source $entry.relativePath)
        Assert-Condition (!$sourceFiles.ContainsKey($entry.relativePath)) 'Duplicate manifest source file.'
        $sourceFiles.Add($entry.relativePath, [ordered]@{ length = [long]$entry.length; sha256 = $entry.sha256 })
    }
    $outputFiles = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($entry in $manifest.outputs) {
        [void](Get-RelativeSafePath $targetRoot $entry.relativePath)
        Assert-Condition (!$outputFiles.ContainsKey($entry.relativePath)) 'Duplicate manifest output file.'
        $outputFiles.Add($entry.relativePath, [ordered]@{ length = [long]$entry.length; sha256 = $entry.sha256 })
    }
    Assert-Inventory ([pscustomobject]@{ Directories = $manifest.sourceDirectories; Files = $sourceFiles }) $sourceInventory 'Manifest source'
    Assert-Inventory ([pscustomobject]@{ Directories = $manifest.targetDirectories; Files = $outputFiles }) $targetInventory 'Manifest output'
    foreach ($entry in $manifest.sourceFiles) {
        if ($entry.kind -ceq 'ExcludedDerived') { continue }
        Assert-Condition ($outputFiles.ContainsKey($entry.relativePath)) 'Fact missing from target.'
        Assert-Condition ($outputFiles[$entry.relativePath].length -eq $entry.length -and $outputFiles[$entry.relativePath].sha256 -ceq $entry.sha256) 'Copied fact bytes do not match source.'
    }
    Assert-Condition (![IO.Directory]::Exists((Join-Path $targetRoot 'cache'))) 'Legacy derived cache copied to target.'
    $audit = Invoke-Harness @($toolkitDll, 'audit', $targetRoot) (Join-Path $CaseDirectory 'target-audit')
    Assert-Condition ($audit.completed -eq $true -and $audit.factsStatus -ceq 'Healthy' -and $audit.indexesStatus -ceq 'Consistent') 'Post-release target full audit failed.'
    Assert-Inventory $targetInventory (Get-Inventory $targetRoot) 'Post-release target audit source'
    Assert-Inventory $sourceInventory (Get-Inventory $Source) 'Post-release original source'
    return [ordered]@{ manifest = 'CompleteAndHashesMatched'; targetFullAudit = 'Passed'; terminalCreatedReceived = $false }
}

# This entry builds nothing and accepts no live journal input.
Assert-NoReparsePath $output
Assert-Condition (![IO.File]::Exists($output) -and ![IO.Directory]::Exists($output)) 'OutputDirectory must be new.'
$repoPrefix = [IO.Path]::TrimEndingDirectorySeparator($repo) + [IO.Path]::DirectorySeparatorChar
$artifactPrefix = [IO.Path]::TrimEndingDirectorySeparator((Join-Path $repo 'artifacts')) + [IO.Path]::DirectorySeparatorChar
Assert-Condition (!$output.Equals($repo, [StringComparison]::OrdinalIgnoreCase) -and (!$output.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase) -or $output.StartsWith($artifactPrefix, [StringComparison]::OrdinalIgnoreCase))) 'OutputDirectory must be outside the repository or a fresh child under its ignored artifacts/.'
$required = @()
if ($Matrix -in @('All', 'Bounded')) { $required += $boundedDll }
if ($Matrix -in @('All', 'Legacy')) { $required += @($legacyDll, $toolkitDll, (Join-Path $fixtureRoot 'provenance.json')) }
foreach ($path in $required) { Assert-Condition ([IO.File]::Exists($path)) 'Required Release harness binary/fixture missing; build serially first.' }
[void][IO.Directory]::CreateDirectory($output)
$binaryHashes = [ordered]@{}
foreach ($path in $required) { $binaryHashes.Add([IO.Path]::GetRelativePath($repo, $path).Replace([char]92, [char]47), (Get-FileSha256 $path)) }
$drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($output))
Write-JsonFile (Join-Path $output 'environment.json') ([ordered]@{ outputDirectory = $output; repositoryRoot = $repo; outputDrive = $drive.Name; outputFileSystem = $drive.DriveFormat; platform = 'Windows'; os = [Environment]::OSVersion.VersionString; powershell = $PSVersionTable.PSVersion.ToString(); matrix = $Matrix; legacyFixture = $LegacyFixture; dotnetPath = $DotnetPath; binaryHashes = $binaryHashes; mechanism = 'Process.Kill'; powerLossProof = $false })
$results = [IO.StreamWriter]::new((Join-Path $output 'results.jsonl'), $false, [Text.UTF8Encoding]::new($false))
$boundedPhases = @('EventBeforeAppend', 'EventAfterAppend', 'EventAfterDurableFlush', 'tag:BeforeTargetFlush', 'tag:BeforeAppend', 'tag:AfterAppend', 'tag:AfterDurableFlush',
    'CheckpointBeforeLogFlush', 'CheckpointBeforeBoundary', 'CheckpointBeforeWrite', 'CheckpointBeforeTempFlush', 'CheckpointBeforeReplace', 'CheckpointAfterReplace', 'CheckpointBeforeInstall',
    'rotation:OldFlush', 'rotation:NextCreate', 'rotation:NextFlush', 'rotation:LocatorCreate', 'rotation:LocatorFlush', 'rotation:LocatorReplace', 'rotation:LocatorPublished', 'rotation:OldDispose')
$legacyPhases = @('CopyChunkWritten', 'LocatorWritten', 'CatalogWritten', 'FormatWritten', 'TargetAudited', 'BeforeManifestPublish', 'ManifestPublished')
$passed = 0
try {
    if ($Matrix -in @('All', 'Bounded')) {
        foreach ($phase in $boundedPhases) {
            $case = Join-Path $output ('bounded-' + $phase.Replace(':', '-'))
            [void][IO.Directory]::CreateDirectory($case)
            $journal = Join-Path $case 'journal'
            $termination = Invoke-KillAtReady @($boundedDll, 'kill-child', $journal, $phase) $phase $case
            $before = Get-Inventory $journal
            Save-Inventory (Join-Path $case 'before-verify.json') $before
            $verification = Invoke-Harness @($boundedDll, 'verify', $journal, $phase) (Join-Path $case 'verify')
            $reject = $phase -in @('rotation:NextFlush', 'rotation:LocatorCreate', 'rotation:LocatorFlush', 'rotation:LocatorReplace')
            if ($reject) {
                Assert-Condition ($verification.status -ceq 'StrictReopenRejected' -and $verification.code -ceq 'NextSegmentPresent') 'Rotation publication window was not strictly rejected.'
            }
            else {
                Assert-Condition ($verification.status -ceq 'StrictReopenSucceeded') 'Strict reopen unexpectedly rejected.'
                if ($phase.StartsWith('rotation:', [StringComparison]::Ordinal)) {
                    $active = if ($phase -in @('rotation:LocatorPublished', 'rotation:OldDispose')) { 2 } else { 1 }
                    Assert-Condition ($verification.active -eq $active) 'Killed locator active mismatch.'
                }
            }
            if ($phase.StartsWith('Event', [StringComparison]::Ordinal)) {
                $tail = if ($phase -ceq 'EventBeforeAppend') { 96 } else { 188 }
                $sequence = if ($phase -ceq 'EventBeforeAppend') { 1 } else { 2 }
                Assert-Condition ($verification.eventTail -eq $tail -and $verification.tailSequence -eq $sequence -and $verification.nextSequence -eq ($sequence + 1)) 'Killed append tail/sequence mismatch.'
            }
            if ($phase.StartsWith('tag:', [StringComparison]::Ordinal)) {
                $tagPresent = $phase -in @('tag:AfterAppend', 'tag:AfterDurableFlush')
                Assert-Condition ($verification.savedTagPresent -eq $tagPresent) 'Killed tag visibility mismatch.'
            }
            if ($phase.StartsWith('Checkpoint', [StringComparison]::Ordinal)) {
                $verification['checkpointPublication'] = Assert-CheckpointPublication $journal $phase
            }
            $after = Get-Inventory $journal
            Save-Inventory (Join-Path $case 'after-verify.json') $after
            Assert-Inventory $before $after 'Strict verification source'
            $record = [ordered]@{ matrix = 'Bounded'; phase = $phase; termination = $termination; verification = $verification; sourceBytesAndDirectoriesUnchanged = $true }
            $results.WriteLine((ConvertTo-Json -InputObject $record -Depth 30 -Compress)); $results.Flush()
            $passed++
            Write-Host "Passed Bounded $phase"
        }
    }
    if ($Matrix -in @('All', 'Legacy')) {
        foreach ($phase in $legacyPhases) {
            $case = Join-Path $output ('legacy-' + $phase)
            [void][IO.Directory]::CreateDirectory($case)
            $source = Restore-LegacyFixture $LegacyFixture (Join-Path $case 'fixture')
            $bundle = Join-Path $case 'bundle'
            $before = Get-Inventory $source
            Save-Inventory (Join-Path $case 'before-upgrade.json') $before
            $termination = Invoke-KillAtReady @($legacyDll, 'kill-child', $source, $bundle) $phase $case @{ LEGACY_KILL_PHASE = $phase }
            $after = Get-Inventory $source
            Save-Inventory (Join-Path $case 'after-upgrade.json') $after
            Assert-Inventory $before $after 'Killed upgrade source'
            $manifestPresent = [IO.File]::Exists((Join-Path $bundle 'manifest.json'))
            Assert-Condition ($manifestPresent -eq ($phase -ceq 'ManifestPublished')) 'Wrong completion release visibility.'
            $verification = if ($manifestPresent) { Assert-LegacyManifest $bundle $source $case } else { [ordered]@{ manifest = 'Absent'; bundle = 'IncompleteAndNotEligibleForCutover' } }
            # Post-release audit is read-only: it must preserve target and source manifests' bytes.
            if ($manifestPresent) { Assert-Inventory $after (Get-Inventory $source) 'Post-release source' }
            $record = [ordered]@{ matrix = 'Legacy'; phase = $phase; termination = $termination; verification = $verification; sourceBytesAndDirectoriesUnchanged = $true }
            $results.WriteLine((ConvertTo-Json -InputObject $record -Depth 30 -Compress)); $results.Flush()
            $passed++
            Write-Host "Passed Legacy $phase"
        }
    }
    $expected = if ($Matrix -ceq 'All') { 29 } elseif ($Matrix -ceq 'Bounded') { 22 } else { 7 }
    Assert-Condition ($passed -eq $expected) 'Interruption matrix coverage mismatch.'
    Write-JsonFile (Join-Path $output 'summary.json') ([ordered]@{ status = 'Passed'; platform = 'Windows'; matrix = $Matrix; cases = $passed; processKill = 'Native'; powerLossProof = $false })
}
catch {
    Write-JsonFile (Join-Path $output 'failure.json') ([ordered]@{ status = 'Failed'; completedCases = $passed; errorType = $_.Exception.GetType().Name; detail = $_.Exception.Message })
    throw
}
finally { $results.Dispose() }
