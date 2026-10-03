"""Fresh W: run, source/commit guards, Python fixture and wire audit.

Build is deliberately a separate serial root-thread step. This script runs only
the prebuilt Release DLL and SDK identification, plus the independent oracle.
"""
import argparse
import datetime
import hashlib
import json
import os
from pathlib import Path
import platform
import subprocess
import sys

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[2]


def command(*args):
    return subprocess.check_output(args, cwd=REPO, text=True, encoding="utf-8").strip()


def hashes():
    files = []
    for folder in [REPO / "src" / "Rbf", REPO / "src" / "Data", REPO / "src" / "Primitives", HERE]:
        for path in folder.rglob("*"):
            if path.is_file() and path.suffix in {".cs", ".csproj", ".py"} and not {"bin", "obj", "__pycache__", "results"}.intersection(path.parts):
                files.append(path)
    for name in ["global.json", "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props"]:
        path = REPO / name
        if path.exists():
            files.append(path)
    return {path.relative_to(REPO).as_posix(): hashlib.sha256(path.read_bytes()).hexdigest()
            for path in sorted(set(files))}


def write_json(path, value):
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--quick", action="store_true")
    parser.add_argument("--expect-commit")
    args = parser.parse_args()
    output = args.output.resolve()
    assert str(output).lower().startswith("w:\\"), "Artifacts must live on W:"
    output.mkdir(parents=True, exist_ok=False)
    commit = command("git", "rev-parse", "HEAD")
    assert not args.expect_commit or commit == args.expect_commit, "Unexpected source commit"
    source = hashes()
    started = datetime.datetime.now(datetime.timezone.utc).isoformat()
    metadata = dict(SchemaVersion=1, Profile="RBF3-units-tail-key", SourceCommit=commit,
                    Sdk=command("dotnet", "--version"), SourceHashes=source,
                    GitStatusBefore=command("git", "status", "--short"), StartedUtc=started,
                    Quick=args.quick, Accepted=False, OutputDirectory=str(output))
    env = dict(os.environ)
    temporary = output / "temp"
    temporary.mkdir()
    env["TMP"] = env["TEMP"] = str(temporary)
    env["PYTHONDONTWRITEBYTECODE"] = "1"
    env["DOTNET_TieredCompilation"] = "0"
    env["DOTNET_TC_QuickJitForLoops"] = "0"
    metadata["RuntimeEnvironment"] = {name: env[name] for name in ["DOTNET_TieredCompilation", "DOTNET_TC_QuickJitForLoops"]}
    hardware_script = r"""
$ErrorActionPreference = 'Stop'
function Try-Fact([scriptblock]$readFact) {
    try { [pscustomobject]@{Status='Available'; Fact=(& $readFact)} }
    catch { [pscustomobject]@{Status='Unavailable'; Reason=$_.Exception.Message} }
}
$cpu = Try-Fact { @(Get-CimInstance -ClassName Win32_Processor | Select-Object Name,NumberOfCores,NumberOfLogicalProcessors,MaxClockSpeed) }
$logical = Try-Fact { Get-CimInstance -ClassName Win32_LogicalDisk -Filter "DeviceID='W:'" | Select-Object DeviceID,DriveType,FileSystem,Size,FreeSpace,VolumeName }
$volume = Try-Fact { Get-Volume -DriveLetter W | Select-Object DriveLetter,FileSystem,DriveType,Size,SizeRemaining,AllocationUnitSize }
$disk = Try-Fact { Get-Partition -DriveLetter W | Get-Disk | Select-Object Number,FriendlyName,BusType,Size,IsBoot,IsSystem }
[pscustomobject]@{CPU=$cpu; WLogicalDisk=$logical; WStorageVolume=$volume; WPhysicalDisk=$disk; UserDeclaredWMedia='data SSD'} | ConvertTo-Json -Depth 6
"""
    try:
        hardware = json.loads(subprocess.check_output(["pwsh", "-NoProfile", "-Command", hardware_script],
                             cwd=REPO, text=True, encoding="utf-8"))
    except (OSError, subprocess.CalledProcessError, json.JSONDecodeError) as error:
        hardware = dict(InventoryStatus="Unavailable", Reason=str(error), UserDeclaredWMedia="data SSD")
    hardware["OS"] = platform.platform()
    hardware["Python"] = platform.python_version()
    write_json(output / "environment.json", hardware)
    metadata["MachineEnvironmentArtifact"] = "environment.json"
    dll = HERE / "bin" / "Release" / "net10.0" / "Atelia.UnifiedRootProbe.dll"
    assert dll.exists(), "Build Release before running this probe."
    binary_hashes = {path.name: file_hash(path) for path in sorted(dll.parent.iterdir())
                     if path.is_file() and path.suffix in {".dll", ".pdb", ".json", ".exe"}}
    metadata["BuiltReleaseArtifacts"] = binary_hashes
    fixture_command = [sys.executable, str(HERE / "oracle.py"), "fixtures", str(output)]
    probe_command = ["dotnet", str(dll), "--output", str(output)] + (["--quick"] if args.quick else [])
    verify_command = [sys.executable, str(HERE / "oracle.py"), "verify", str(output)]
    metadata["Commands"] = [fixture_command, probe_command, verify_command]
    write_json(output / "provenance-before.json", metadata)
    try:
        for number, argv in enumerate(metadata["Commands"]):
            with (output / f"run-{number}.log").open("w", encoding="utf-8", newline="\n") as log:
                process = subprocess.run(argv, cwd=REPO, env=env, stdout=log, stderr=subprocess.STDOUT)
            assert process.returncode == 0, f"Step {number} failed ({process.returncode}); see run-{number}.log"
        assert command("git", "rev-parse", "HEAD") == commit, "Commit changed during run"
        assert hashes() == source, "Measured source changed during run"
        assert {path.name: file_hash(path) for path in sorted(dll.parent.iterdir())
                if path.is_file() and path.suffix in {".dll", ".pdb", ".json", ".exe"}} == binary_hashes, "Built binaries changed during run"
        result = json.loads((output / "production-results.json").read_text(encoding="utf-8"))
        assert result["Passed"] and json.loads((output / "python-verification.json").read_text())["Passed"]
        metadata["Accepted"] = True
    finally:
        metadata["SourceHashesAfter"] = hashes()
        metadata["SourceCommitAfter"] = command("git", "rev-parse", "HEAD")
        metadata["GitStatusAfter"] = command("git", "status", "--short")
        metadata["FinishedUtc"] = datetime.datetime.now(datetime.timezone.utc).isoformat()
        metadata["Artifacts"] = {path.relative_to(output).as_posix(): dict(Bytes=path.stat().st_size, Sha256=file_hash(path))
              for path in sorted(output.rglob("*")) if path.is_file() and path.name not in {"provenance-before.json", "provenance.json"}}
        write_json(output / "provenance.json", metadata)
    print(json.dumps(dict(Accepted=True, SourceCommit=commit, OutputDirectory=str(output), Artifacts=len(metadata["Artifacts"]))))


def file_hash(path):
    result = hashlib.sha256()
    with path.open("rb") as stream:
        while block := stream.read(1024 * 1024):
            result.update(block)
    return result.hexdigest()


if __name__ == "__main__":
    main()
