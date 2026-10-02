param(
    [string]$OutputDirectory = (Join-Path 'W:\RbfCodecCost' ('run-' + [Guid]::NewGuid().ToString('N'))),
    [switch]$Quick,
    [switch]$CpuOnly,
    [switch]$IoOnly,
    [switch]$Focused,
    [switch]$RandomSearch
)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$taskOutput = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $taskOutput.StartsWith('W:\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Use W: for experiment artifacts and real file I/O.' }
if ((Test-Path -LiteralPath $taskOutput) -and (Get-ChildItem -LiteralPath $taskOutput -Force | Select-Object -First 1)) { throw 'Output directory must be new or empty.' }
New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
Push-Location $taskRoot
$taskPreviousTiering = $env:DOTNET_TieredCompilation
try {
    $taskSources = @(Get-ChildItem -LiteralPath $PSScriptRoot -File | Where-Object Extension -In '.cs','.csproj','.py','.ps1' | Sort-Object Name)
    $taskBeforeHashes = @($taskSources | ForEach-Object { [ordered]@{ Path = 'experiments/RbfCodecCost/' + $_.Name; SHA256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() } })
    $taskHost = [ordered]@{
        SourceRevision = (git rev-parse HEAD).Trim()
        WorktreeStatus = @(git status --short)
        SDK = (dotnet --version).Trim()
        CPU = @(Get-CimInstance Win32_Processor | Select-Object Name,NumberOfCores,NumberOfLogicalProcessors)
        OS = @(Get-CimInstance Win32_OperatingSystem | Select-Object Caption,Version,TotalVisibleMemorySize)
        Storage = @(Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='W:'" | Select-Object DeviceID,DriveType,FileSystem,VolumeName,Size,FreeSpace)
        Sources = $taskBeforeHashes
    }
    dotnet build experiments/RbfCodecCost/RbfCodecCost.csproj -c Release *> (Join-Path $taskOutput 'build.log')
    if ($LASTEXITCODE -ne 0) { throw "Release build failed; inspect $taskOutput\build.log" }
    $env:DOTNET_TieredCompilation = '0'
    # Keep build/run logs outside the program's protected new-or-empty data directory.
    $taskDataOutput = Join-Path $taskOutput 'data'
    $taskArguments = @('--output', $taskDataOutput)
    if ($Quick) { $taskArguments += '--quick' }
    if ($CpuOnly) { $taskArguments += '--cpu-only' }
    if ($IoOnly) { $taskArguments += '--io-only' }
    if ($Focused) { $taskArguments += '--focused' }
    if ($RandomSearch) { $taskArguments += '--random-search' }
    dotnet experiments/RbfCodecCost/bin/Release/net10.0/Atelia.UnifiedRootProbe.dll @taskArguments | Tee-Object -FilePath (Join-Path $taskOutput 'run.log')
    if ($LASTEXITCODE -ne 0) { throw "Probe failed; retain output $taskOutput" }
    python -B experiments/RbfCodecCost/verify_vectors.py $taskDataOutput | Tee-Object -FilePath (Join-Path $taskOutput 'python.log')
    if ($LASTEXITCODE -ne 0) { throw 'Independent Python wire verification failed.' }
    if ($RandomSearch) {
        python -B experiments/RbfCodecCost/verify_random_vectors.py $taskDataOutput | Tee-Object -FilePath (Join-Path $taskOutput 'python-random.log')
        if ($LASTEXITCODE -ne 0) { throw 'Independent Python random-key wire verification failed.' }
    }
    foreach ($taskSource in $taskSources) {
        $taskCurrentHash = (Get-FileHash -LiteralPath $taskSource.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $taskRecorded = $taskBeforeHashes | Where-Object Path -EQ ('experiments/RbfCodecCost/' + $taskSource.Name)
        if ($taskCurrentHash -ne $taskRecorded.SHA256) { throw "Source changed during measurement: $($taskSource.Name)" }
    }
    $taskHost | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskOutput 'provenance.json') -Encoding utf8NoBOM
    Write-Output "Verified evidence: $taskOutput"
} finally {
    $env:DOTNET_TieredCompilation = $taskPreviousTiering
    Pop-Location
}
