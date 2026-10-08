[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[a-z0-9]+([.-][a-z0-9]+)*)?$')][string]$Version,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateSet('All')][string]$Project = 'All',
    [hashtable]$DependencyVersions
)
$ErrorActionPreference = 'Stop'
if ($PSBoundParameters.ContainsKey('DependencyVersions')) {
    throw [ArgumentException]::new('Main delivers only the Primitives/Data/Rbf/Binary package set. Selective delivery and DependencyVersions are maintained on the RBF1 branch.', 'DependencyVersions')
}
$repo = Split-Path $PSScriptRoot -Parent
$feed = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
$repositoryUrl = 'https://github.com/Atelia-org/atelia-storage'
# The production package list and pack algorithm have one owner: this script.
$projects = @('Primitives', 'Data', 'Rbf', 'Binary')
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
function Normalize-UnsignedPackage([string]$Path) {
    # .NET 10 NuGet pack emits random OPC metadata names and nonrepeatable ZIP timestamps.
    # Keep binary payloads unchanged; normalize the container and equivalent generated XML text.
    $entries = [Collections.Generic.SortedDictionary[string, byte[]]]::new([StringComparer]::Ordinal)
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        if ($archive.Entries | Where-Object FullName -IEQ '.signature.p7s') { throw "Cannot normalize a signed package: $Path" }
        $coreEntries = @($archive.Entries | Where-Object FullName -Match '^package/services/metadata/core-properties/[^/]+\.psmdcp$')
        if ($coreEntries.Count -ne 1) { throw "Expected one NuGet core-properties entry: $Path" }
        $coreName = $coreEntries[0].FullName
        foreach ($entry in $archive.Entries) {
            $buffer = [IO.MemoryStream]::new()
            $inputStream = $entry.Open()
            try { $inputStream.CopyTo($buffer); $bytes = $buffer.ToArray() }
            finally { $inputStream.Dispose(); $buffer.Dispose() }
            $name = $entry.FullName
            if ($name -ceq $coreName) { $name = 'package/services/metadata/core-properties/nuget.psmdcp' }
            if ($name.EndsWith('.nuspec', [StringComparison]::Ordinal)) {
                $nuspec = [xml]([Text.Encoding]::UTF8.GetString($bytes).TrimStart([char]0xFEFF))
                foreach ($repository in $nuspec.SelectNodes("//*[local-name()='repository']")) {
                    $repository.RemoveAttribute('branch')
                }
                foreach ($group in $nuspec.SelectNodes("//*[local-name()='dependencies']/*[local-name()='group']")) {
                    $dependencies = [Collections.Generic.SortedDictionary[string, Xml.XmlElement]]::new([StringComparer]::Ordinal)
                    foreach ($dependency in @($group.ChildNodes)) {
                        if ($dependency.LocalName -eq 'dependency') { $dependencies.Add($dependency.GetAttribute('id'), $dependency) }
                    }
                    foreach ($dependency in $dependencies.Values) { [void]$group.AppendChild($dependency) }
                }
                $bytes = [Text.Encoding]::UTF8.GetBytes($nuspec.OuterXml)
            }
            if ($name -ceq '_rels/.rels') {
                $relationships = [xml]([Text.Encoding]::UTF8.GetString($bytes).TrimStart([char]0xFEFF))
                foreach ($relationship in $relationships.DocumentElement.ChildNodes) {
                    if ($relationship.LocalName -ne 'Relationship') { continue }
                    if ($relationship.GetAttribute('Target').TrimStart('/') -ceq $coreName) {
                        $relationship.SetAttribute('Target', '/package/services/metadata/core-properties/nuget.psmdcp')
                    }
                    $identity = $relationship.GetAttribute('Type') + '|' + $relationship.GetAttribute('Target')
                    $idHash = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($identity))
                    $relationship.SetAttribute('Id', 'R' + [Convert]::ToHexString($idHash).Substring(0, 16))
                }
                $bytes = [Text.Encoding]::UTF8.GetBytes($relationships.OuterXml)
            }
            if ($name.EndsWith('.xml', [StringComparison]::Ordinal) -or $name.EndsWith('.nuspec', [StringComparison]::Ordinal) -or
                $name.EndsWith('.psmdcp', [StringComparison]::Ordinal) -or $name -ceq '_rels/.rels') {
                $xmlText = [Text.Encoding]::UTF8.GetString($bytes).TrimStart([char]0xFEFF).Replace("`r`n", "`n").Replace("`r", "`n")
                $bytes = [Text.Encoding]::UTF8.GetBytes($xmlText)
            }
            if ($entries.ContainsKey($name)) { throw "Duplicate normalized package entry: $name" }
            $entries.Add($name, $bytes)
        }
    }
    finally { $archive.Dispose() }
    $normalizedPath = $Path + '.normalized'
    $outputStream = [IO.File]::Open($normalizedPath, [IO.FileMode]::CreateNew)
    try {
        $outputArchive = [IO.Compression.ZipArchive]::new($outputStream, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            foreach ($item in $entries.GetEnumerator()) {
                $entry = $outputArchive.CreateEntry($item.Key, [IO.Compression.CompressionLevel]::NoCompression)
                $entry.LastWriteTime = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
                $entry.ExternalAttributes = 0
                $entryStream = $entry.Open()
                try { $entryStream.Write($item.Value, 0, $item.Value.Length) }
                finally { $entryStream.Dispose() }
            }
        }
        finally { $outputArchive.Dispose() }
        # ZipArchive stamps its host OS into central-directory "version made by".
        # Our small ZIP32 output has no archive comment; fix only that platform byte.
        $reader = [IO.BinaryReader]::new($outputStream, [Text.Encoding]::UTF8, $true)
        try {
            $outputStream.Position = $outputStream.Length - 22
            $end = $reader.ReadBytes(22)
            if ([BitConverter]::ToUInt32($end, 0) -ne 0x06054b50 -or [BitConverter]::ToUInt16($end, 20) -ne 0) {
                throw 'Expected a small ZIP32 package without an archive comment.'
            }
            $entryCount = [BitConverter]::ToUInt16($end, 10)
            $centralOffset = [BitConverter]::ToUInt32($end, 16)
            if ($entryCount -ne $entries.Count -or $entryCount -eq 65535 -or $centralOffset -eq [uint32]::MaxValue) {
                throw 'Unexpected ZIP64 or central-directory entry count.'
            }
            $outputStream.Position = $centralOffset
            for ($index = 0; $index -lt $entryCount; $index++) {
                $headerOffset = $outputStream.Position
                $header = $reader.ReadBytes(46)
                if ($header.Length -ne 46 -or [BitConverter]::ToUInt32($header, 0) -ne 0x02014b50) { throw 'Invalid central-directory header.' }
                $next = $headerOffset + 46 + [BitConverter]::ToUInt16($header, 28) + [BitConverter]::ToUInt16($header, 30) + [BitConverter]::ToUInt16($header, 32)
                $outputStream.Position = $headerOffset + 5
                $outputStream.WriteByte(0)
                $outputStream.Position = $next
            }
        }
        finally { $reader.Dispose() }
    }
    finally { $outputStream.Dispose() }
    # Replacement is limited to unpublished staging output; existing feed artifacts are never rewritten.
    [IO.File]::Move($normalizedPath, $Path, $true)
}

# Resolve caller-relative arguments above, then select the SDK from this repository's global.json.
Push-Location $repo
$previousUiLanguage = $env:DOTNET_CLI_UI_LANGUAGE
try {
$env:DOTNET_CLI_UI_LANGUAGE = 'en-US'
$requiredSdk = (Get-Content -LiteralPath (Join-Path $repo 'global.json') -Raw | ConvertFrom-Json).sdk.version
$sdkVersion = (& dotnet --version | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $sdkVersion -cne $requiredSdk) { throw "Pack requires exact .NET SDK $requiredSdk; selected '$sdkVersion'." }
Assert-Clean
$revision = Git-Value @('rev-parse', 'HEAD')
$origin = (Git-Value @('remote', 'get-url', 'origin')) -replace '\.git$', ''
if ($origin -cne $repositoryUrl) { throw "origin must be $repositoryUrl (optional .git suffix) for reproducible Source Link metadata." }
$manifestPath = Join-Path $feed "manifest.$Version.json"
if (Test-Path -LiteralPath $manifestPath) {
    $previous = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($previous.schemaVersion -ne 1 -or $previous.version -cne $Version -or
        $previous.sourceRevision -ne $revision -or $previous.repositoryUrl -ne $repositoryUrl -or $previous.sdkVersion -cne $sdkVersion -or
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
    & dotnet pack (Join-Path $repo "src/$name/$name.csproj") -c Release -o $stage "-p:PackageVersion=$Version" "-p:RepositoryCommit=$revision" '-p:RepositoryBranch=' '-p:ContinuousIntegrationBuild=true' '-p:StorageDeterministicPack=true' | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet pack failed: $name (partial output retained at $stage)" }
    $file = "Atelia.$name.$Version.nupkg"
    $symbols = "Atelia.$name.$Version.snupkg"
    Normalize-UnsignedPackage (Join-Path $stage $file)
    Normalize-UnsignedPackage (Join-Path $stage $symbols)
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
[ordered]@{ schemaVersion = 1; version = $Version; sourceRevision = $revision; repositoryUrl = $repositoryUrl; sdkVersion = $sdkVersion; packages = $packages } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
Write-Host "Packed $($packages.Count) libraries from $revision. No remote publication was performed."
Write-Output $manifestPath
}
finally {
    $env:DOTNET_CLI_UI_LANGUAGE = $previousUiLanguage
    Pop-Location
}
