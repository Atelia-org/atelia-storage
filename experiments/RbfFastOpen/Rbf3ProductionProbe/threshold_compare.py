"""Freeze prebuilt Release bundles; compare one 4KiB/8KiB pair without building.

All .NET work is an explicit serial parent-thread action. Frozen source snapshots
record the build tree; hashes/ProductVersion identify the actual loaded binaries.
They do not turn a dirty-tree ProductVersion into a clean-commit attestation.
"""
import argparse
import datetime
import difflib
import json
import os
from pathlib import Path
import platform
import shutil
import statistics
import subprocess
import sys

sys.dont_write_bytecode = True
from run import HERE, REPO, command, file_hash, hashes, write_json
from oracle import F, decode, encode

PROBE = "Atelia.UnifiedRootProbe.dll"
APPEND_SOURCE = "src/Rbf/Internal/RbfAppendImpl.cs"
JIT_ENV = {"DOTNET_TieredCompilation": "0", "DOTNET_TC_QuickJitForLoops": "0"}


def utc():
    return datetime.datetime.now(datetime.timezone.utc).isoformat()


def fresh_w(path):
    path = path.resolve()
    if path.drive.lower() != "w:":
        raise ValueError("All experiment bundles/output must live on W:")
    path.mkdir(parents=True, exist_ok=False)
    return path


def tree_hashes(folder):
    return {path.relative_to(folder).as_posix(): file_hash(path)
            for path in sorted(folder.rglob("*")) if path.is_file()}


def source_hashes():
    result = hashes()
    for name in [".editorconfig", "NuGet.Config", "nuget.config"]:
        path = REPO / name
        if path.is_file():
            result[name] = file_hash(path)
    return dict(sorted(result.items()))


def freeze(args):
    source = source_hashes()
    binaries = args.binary_directory.resolve()
    if not (binaries / PROBE).is_file():
        raise ValueError("Build the Release probe before freezing its complete output directory")
    if not args.build_log.is_file():
        raise ValueError("Preserve the actual serial Release build log")
    before = tree_hashes(binaries)
    folder = fresh_w(args.output)
    shutil.copytree(binaries, folder / "bin")
    for name, digest in source.items():
        target = folder / "source" / name
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(REPO / name, target)
        if file_hash(target) != digest:
            raise ValueError(f"Source changed while freezing: {name}")
    shutil.copyfile(args.build_log, folder / "build.log")
    if source_hashes() != source or tree_hashes(binaries) != before or tree_hashes(folder / "bin") != before:
        raise ValueError("Build tree changed while freezing")
    write_json(folder / "bundle.json", dict(
        SchemaVersion=1, Label=args.label, DeclaredThreshold=args.threshold, CreatedUtc=utc(),
        SourceCommit=command("git", "rev-parse", "HEAD"), GitStatus=command("git", "status", "--short"),
        Sdk=command("dotnet", "--version"), InstalledRuntimes=command("dotnet", "--list-runtimes"),
        BuildCommand=args.build_command, BuildLogSha256=file_hash(folder / "build.log"),
        SourceHashes=source, BinaryHashes=before, Configuration="Release",
        IdentityScope="Exact captured build-tree source/config and full copied output; matching build is verified by parent review, not inferred from ProductVersion. Threshold behavior also checked by actual public output/count passes."))
    print(json.dumps(dict(Bundle=str(folder), Label=args.label, Sources=len(source), Binaries=len(before))))


def load_bundle(path):
    folder = path.resolve()
    if folder.drive.lower() != "w:":
        raise ValueError("Frozen bundles must be on W:")
    manifest = json.loads((folder / "bundle.json").read_text(encoding="utf-8"))
    if tree_hashes(folder / "source") != manifest["SourceHashes"]:
        raise ValueError(f"Frozen sources changed: {folder}")
    if tree_hashes(folder / "bin") != manifest["BinaryHashes"]:
        raise ValueError(f"Frozen binaries changed: {folder}")
    if file_hash(folder / "build.log") != manifest["BuildLogSha256"]:
        raise ValueError(f"Frozen build log changed: {folder}")
    return folder, manifest


def child_env(output):
    env = dict(os.environ)
    # Remove inherited diagnostic switches that would make timing incomparable.
    for name in list(env):
        if name.lower().startswith(("dotnet_jit", "complus_jit")) or name.lower() in {
                "complus_tieredcompilation", "complus_tc_quickjitforloops"}:
            del env[name]
    temporary = output / "temp"
    temporary.mkdir(exist_ok=True)
    env.update(JIT_ENV)
    env.update(TMP=str(temporary), TEMP=str(temporary), PYTHONDONTWRITEBYTECODE="1")
    return env


def run_child(bundle, manifest, output, round_number, env, batch_mib, operation_cap, jit_only=False):
    argv = ["dotnet", str(bundle / "bin" / PROBE), "--threshold-sample", "--output", str(output),
            "--variant", manifest["Label"], "--round", str(round_number),
            "--threshold", str(manifest["DeclaredThreshold"]), "--batch-mib", str(batch_mib),
            "--operation-cap", str(operation_cap)]
    if jit_only:
        argv.append("--jit-only")
    log_path = output.with_suffix(".log")
    with log_path.open("w", encoding="utf-8", newline="\n") as log:
        completed = subprocess.run(argv, cwd=bundle / "bin", env=env, stdout=log, stderr=subprocess.STDOUT)
    if completed.returncode:
        raise RuntimeError(f"Frozen probe failed ({completed.returncode}); see {log_path}")
    sample = json.loads((output / "sample.json").read_text(encoding="utf-8"))
    if not sample["Passed"] or sample["RuntimeEnvironment"]["DOTNET_TieredCompilation"] != "0" or (
            not jit_only and sample["ObservedThreshold"] != manifest["DeclaredThreshold"]):
        raise ValueError("Invalid sample/JIT environment")
    for assembly in sample["LoadedAssemblies"]:
        name = Path(assembly["Location"]).name
        if assembly["Sha256"] != manifest["BinaryHashes"][name]:
            raise ValueError(f"Unexpected loaded assembly: {name}")
    return sample, argv


def audit_sample(folder, sample, threshold):
    audited = []
    for row in sample["Timings"] + sample["Counts"]:
        case = row["Case"]
        size, meta_length = case["TotalBytes"], case["MetaBytes"]
        combined = (F.to_bytes(4, "little") * ((size - 32 + 3) // 4))[:size - 32] if case["Marker"] else bytes(size - 32)
        payload = combined[:-meta_length] if meta_length else combined
        meta = combined[-meta_length:] if meta_length else b""
        first = (folder / row["FirstWire"]).read_bytes()
        last = (folder / row["LastWire"]).read_bytes()
        for wire, key in [(first, row["Key"]), (last, row["LastKey"])]:
            decoded = decode(wire)  # Independent bitwise CRC32C, including complete payload.
            if decoded[:3] != (payload, meta, 83) or encode(payload, meta, 83, decoded[3]) != wire:
                raise ValueError(f"Independent full wire audit failed: {case['Name']}")
            if decoded[3] != key or bool(key) != case["Marker"] or len(wire) != size + 4:
                raise ValueError("Actual key category/layout mismatch")
        expected_scratch = 1024 * 1024 if row["Key"] and size > threshold else 0
        if row["ScratchAfterFirst"] != expected_scratch or row["ScratchAfterWarm"] != expected_scratch or row["ScratchAfterDispose"]:
            raise ValueError("Actual timed/counted scratch lifecycle mismatch")
        # The production selector can use different legal random keys on successive
        # frames. Audit both actual endpoints independently, never require encoded
        # equality across Appends or variants. Intermediate batch frames are not audited.
        operations = row.get("WarmOperations", 1) + 1
        with (folder / row["File"]).open("rb") as stream:
            if stream.read(4) != first[:4]:
                raise ValueError("Measured Header differs from audited wire")
            if stream.read(size) != first[4:]:
                raise ValueError("Measured first frame differs from independently audited wire")
            stream.seek(4 + (operations - 1) * size)
            if stream.read(size) != last[4:] or stream.read(1):
                raise ValueError("Measured last frame/EOF differs from independently audited wire")
            if stream.tell() != 4 + operations * size:
                raise ValueError("Measured file length mismatch")
        audited.append(dict(Case=case["Name"], Frames=operations, IndependentlyAuditedFrames=2,
                            Key=row["Key"], LastKey=row["LastKey"],
                            FirstWireSha256=file_hash(folder / row["FirstWire"]), LastWireSha256=file_hash(folder / row["LastWire"])))
    return audited


def compare(args):
    baseline, a = load_bundle(args.baseline)
    candidate, b = load_bundle(args.candidate)
    if a["DeclaredThreshold"] != 4096 or b["DeclaredThreshold"] != 8192 or a["Label"] == b["Label"]:
        raise ValueError("Use distinct frozen 4KiB baseline and 8KiB candidate labels")
    names = set(a["SourceHashes"]) | set(b["SourceHashes"])
    changed = sorted(name for name in names if a["SourceHashes"].get(name) != b["SourceHashes"].get(name))
    if changed != [APPEND_SOURCE]:
        raise ValueError(f"Candidate may change only AppendRbf3 threshold source; changed={changed}")
    if a["Sdk"] != b["Sdk"] or a["InstalledRuntimes"] != b["InstalledRuntimes"]:
        raise ValueError("Frozen variants have different SDK/runtime inventory")
    if command("dotnet", "--version") != a["Sdk"] or command("dotnet", "--list-runtimes") != a["InstalledRuntimes"]:
        raise ValueError("Current SDK/runtime inventory differs from frozen build inventory")
    verifier_hashes = {}
    for name in ["threshold_compare.py", "run.py", "oracle.py"]:
        relative = (HERE / name).relative_to(REPO).as_posix()
        verifier_hashes[relative] = file_hash(HERE / name)
        if verifier_hashes[relative] != a["SourceHashes"][relative]:
            raise ValueError(f"Live verifier differs from frozen source: {name}")
    for name in ["Atelia.Data.dll", "Atelia.Primitives.dll"]:
        if a["BinaryHashes"][name] != b["BinaryHashes"][name]:
            raise ValueError(f"Unrelated production dependency changed: {name}")
    if a["BinaryHashes"]["Atelia.Rbf.dll"] == b["BinaryHashes"]["Atelia.Rbf.dll"]:
        raise ValueError("Candidate Rbf binary is identical to baseline")
    if args.rounds < 7:
        raise ValueError("Comparison requires at least seven alternating pairs")
    output = fresh_w(args.output)
    env = child_env(output)
    provenance = dict(SchemaVersion=1, Accepted=False, StartedUtc=utc(), Baseline=a, Candidate=b,
                      BundlePaths=[str(baseline), str(candidate)], RuntimeEnvironment=JIT_ENV,
                      OS=platform.platform(), Python=platform.python_version(), Processor=platform.processor(),
                      LogicalProcessors=os.cpu_count(), WDiskUsage=shutil.disk_usage(output)._asdict(),
                      UserDeclaredWMedia="SSD", VerifierHashes=verifier_hashes, Commands=[],
                      Rounds=args.rounds, BatchMiB=args.batch_mib, OperationCap=args.operation_cap,
                      Scope="Exploratory paired public Append on W:. Same optimized JIT, warm process/shared pool, no timing hooks; separate full-wire oracle/count passes. No workload-wide throughput, cold/device/crash/downstream/package claim.")
    source_a = (baseline / "source" / APPEND_SOURCE).read_text(encoding="utf-8")
    source_b = (candidate / "source" / APPEND_SOURCE).read_text(encoding="utf-8")
    (output / "candidate.patch").write_text("".join(difflib.unified_diff(source_a.splitlines(True), source_b.splitlines(True), fromfile="baseline", tofile="candidate")), encoding="utf-8", newline="\n")
    write_json(output / "provenance-before.json", provenance)
    samples = []
    try:
        for round_number in range(args.rounds):
            pair = [(baseline, a), (candidate, b)]
            if round_number & 1:
                pair.reverse()
            for position, (bundle, manifest) in enumerate(pair):
                folder = output / f"round-{round_number:02}-{position}-{manifest['Label']}"
                sample, argv = run_child(bundle, manifest, folder, round_number, env, args.batch_mib, args.operation_cap)
                provenance["Commands"].append(argv)
                samples.append((folder, sample))
                print(f"Completed pair {round_number + 1}/{args.rounds}, position {position}, {manifest['Label']}", flush=True)
        if len({(sample["Runtime"], sample["Architecture"], sample["StopwatchFrequency"]) for _, sample in samples}) != 1:
            raise ValueError("Measured processes have inconsistent runtime/architecture/clock")
        # Keep the slow independent oracle and full artifact hashing out of all timing pairs.
        audit = [dict(Sample=folder.name, Rows=audit_sample(folder, sample, sample["DeclaredThreshold"]))
                 for folder, sample in samples]
        write_json(output / "python-verification.json", dict(Passed=True, Samples=audit,
                   Scope="Existing independent Python bitwise CRC/XOR/units oracle checks complete actual first and last frames, their key categories and correspondence to measured file/EOF. Intermediate batch frames are not independently audited."))
        results = []
        for case in samples[0][1]["Cases"]:
            rows = []
            for round_number in range(args.rounds):
                selected = {sample["DeclaredThreshold"]: next(row for row in sample["Timings"] if row["Case"]["Name"] == case["Name"])
                            for _, sample in samples if sample["Round"] == round_number}
                raw = {}
                for threshold, row in selected.items():
                    sample = next(value for _, value in samples if value["Round"] == round_number and value["DeclaredThreshold"] == threshold)
                    raw[str(threshold)] = dict(WarmNsPerFrame=row["WarmAppendTicks"] * 1e9 / sample["StopwatchFrequency"] / row["WarmOperations"],
                                              FirstNs=row["FirstAppendTicks"] * 1e9 / sample["StopwatchFrequency"],
                                              WarmBatchMs=row["WarmAppendTicks"] * 1000 / sample["StopwatchFrequency"],
                                              FirstAllocatedBytes=row["FirstAllocatedBytes"], WarmAllocatedBytes=row["WarmAllocatedBytes"],
                                              FirstFlushTicks=row["FirstFlushTicks"], WarmFlushTicks=row["WarmFlushTicks"],
                                              ScratchAfterFirst=row["ScratchAfterFirst"], ScratchAfterWarm=row["ScratchAfterWarm"], Key=row["Key"], LastKey=row["LastKey"])
                rows.append(dict(Round=round_number, Order="A/B" if not round_number & 1 else "B/A", Raw=raw,
                                 CandidateOverBaseline=raw["8192"]["WarmNsPerFrame"] / raw["4096"]["WarmNsPerFrame"]))
            results.append(dict(Case=case, Pairs=rows, MedianPairedRatio=statistics.median(row["CandidateOverBaseline"] for row in rows),
                                AllWarmBatchesAtLeast1ms=all(value["WarmBatchMs"] >= 1 for row in rows for value in row["Raw"].values())))
        write_json(output / "comparison.json", dict(SchemaVersion=1, Passed=True, Rows=results,
                   Qualification="Raw paired measurements; no automatic winner/merge or confidence proof. Inspect noise, small/nonzero regressions and separate JIT stack evidence before deciding."))
        load_bundle(baseline)
        load_bundle(candidate)
        for name, digest in verifier_hashes.items():
            if file_hash(REPO / name) != digest:
                raise ValueError(f"Verifier changed during comparison: {name}")
        provenance["Accepted"] = True
    finally:
        provenance["FinishedUtc"] = utc()
        provenance["Artifacts"] = {name: dict(Sha256=digest, Bytes=(output / name).stat().st_size)
                                   for name, digest in tree_hashes(output).items()
                                   if name not in {"provenance-before.json", "provenance.json"}}
        write_json(output / "provenance.json", provenance)
    print(json.dumps(dict(Accepted=True, Output=str(output), PairedRounds=args.rounds)))


def jit(args):
    bundle, manifest = load_bundle(args.bundle)
    output = fresh_w(args.output)
    env = child_env(output)
    env["DOTNET_JitDisasm"] = "Atelia.Rbf.Internal.RbfAppendImpl:AppendRbf3"
    env["DOTNET_JitStdOutFile"] = str(output / "append-jit.asm")
    sample, argv = run_child(bundle, manifest, output / "sample", 0, env, 1, 2, jit_only=True)
    assembly = output / "append-jit.asm"
    text = assembly.read_text(encoding="utf-8")
    if "AppendRbf3" not in text:
        raise ValueError("Target JIT disassembly was not captured")
    write_json(output / "jit-provenance.json", dict(Bundle=manifest, Command=argv,
               Runtime=sample["Runtime"], Architecture=sample["Architecture"],
               RuntimeEnvironment={name: env[name] for name in [*JIT_ENV, "DOTNET_JitDisasm", "DOTNET_JitStdOutFile"]},
               DisassemblySha256=file_hash(assembly),
               StackQualification="Manual target-JIT review required: saved registers/static prolog plus path-specific localloc/stack probe. Disassembly capture is not an automatic whole-call-stack peak measurement."))
    load_bundle(bundle)
    print(json.dumps(dict(JitDisassembly=str(assembly), Threshold=manifest["DeclaredThreshold"])))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="action", required=True)
    frozen = commands.add_parser("freeze")
    frozen.add_argument("--output", type=Path, required=True)
    frozen.add_argument("--label", choices=["baseline", "candidate"], required=True)
    frozen.add_argument("--threshold", type=int, choices=[4096, 8192], required=True)
    frozen.add_argument("--binary-directory", type=Path, default=HERE / "bin" / "Release" / "net10.0")
    frozen.add_argument("--build-log", type=Path, required=True)
    frozen.add_argument("--build-command", required=True)
    frozen.set_defaults(run=freeze)
    paired = commands.add_parser("compare")
    paired.add_argument("--baseline", type=Path, required=True)
    paired.add_argument("--candidate", type=Path, required=True)
    paired.add_argument("--output", type=Path, required=True)
    paired.add_argument("--rounds", type=int, default=7)
    paired.add_argument("--batch-mib", type=int, choices=range(1, 65), default=8)
    paired.add_argument("--operation-cap", type=int, default=16384)
    paired.set_defaults(run=compare)
    diagnostic = commands.add_parser("jit")
    diagnostic.add_argument("--bundle", type=Path, required=True)
    diagnostic.add_argument("--output", type=Path, required=True)
    diagnostic.set_defaults(run=jit)
    args = parser.parse_args()
    args.run(args)


if __name__ == "__main__":
    main()
