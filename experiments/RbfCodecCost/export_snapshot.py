"""Join accepted stages without retaining the discarded first I/O measurements."""
import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import subprocess
import xml.etree.ElementTree as ET


HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]


def sha(path):
    digest = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def read(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--cpu-directory", type=Path, required=True)
    parser.add_argument("--io-directory", type=Path, required=True, help="runner root, not data/")
    parser.add_argument("--focused-directory", type=Path, required=True)
    parser.add_argument("--test-directory", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if args.output.exists():
        raise FileExistsError("Immutable snapshot already exists; choose another name")
    cpu = read(args.cpu_directory / "cpu.json")
    cpu_environment = read(args.cpu_directory / "summary.json")["Environment"]
    io = read(args.io_directory / "data" / "summary.json")
    focused = read(args.focused_directory / "data" / "summary.json")
    io_provenance = read(args.io_directory / "provenance.json")
    focused_provenance = read(args.focused_directory / "provenance.json")
    current_sources = {str(path.relative_to(ROOT)).replace("\\", "/"): sha(path)
                       for path in sorted(HERE.iterdir()) if path.suffix in (".cs", ".csproj", ".py", ".ps1")}
    io_changed = [item["Path"] for item in io_provenance["Sources"] if current_sources[item["Path"]] != item["SHA256"]]
    assert set(io_changed) <= {"experiments/RbfCodecCost/Program.cs", "experiments/RbfCodecCost/Run-Probe.ps1"}
    assert all(current_sources[item["Path"]] == item["SHA256"] for item in focused_provenance["Sources"])
    cpu_kernels = ["CpuProbe.cs", "Measurement.cs", "PrototypeCodec.cs", "XorTransform.cs"]
    io_hashes = {item["Path"]: item["SHA256"] for item in io_provenance["Sources"]}
    cpu_hashes = {"experiments/RbfCodecCost/" + name: current_sources["experiments/RbfCodecCost/" + name] for name in cpu_kernels}
    assert all(io_hashes[path] == value for path, value in cpu_hashes.items())
    assert len(cpu) == 510 and len({row["Workload"] for row in cpu}) == 34
    assert len(io["IO"]) == 69 and len(io["Metadata"]) == 16
    assert len(focused["FocusedEvidence"]["Reader"]) == 20
    trx_files = list(args.test_directory.glob("*.trx"))
    assert len(trx_files) == 1
    counters = ET.parse(trx_files[0]).find(".//{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}Counters").attrib
    assert counters["failed"] == "0" and counters["passed"] == "504"
    git = lambda *command: subprocess.check_output(["git", *command], cwd=ROOT, text=True).strip()
    assert not git("diff", "--name-only", "--", "src/Rbf", "src/Data", "src/Primitives")
    large = focused["FocusedEvidence"]["LargeFrame"]
    assert Path(large["Artifact"]).stat().st_size == large["FileBytes"]
    snapshot = {
        "schemaVersion": 1, "capturedUtc": datetime.now(timezone.utc).isoformat(),
        "productionBaselineRevision": git("rev-parse", "HEAD"),
        "scope": "Experimental RBF2 codec costs and RBF1 reference APIs; production integration pending",
        "currentExperimentSources": current_sources,
        "cpu": {"artifact": str(args.cpu_directory / "cpu.json"), "sha256": sha(args.cpu_directory / "cpu.json"),
                "environment": cpu_environment, "kernelSourceHashes": cpu_hashes,
                "provenanceNote": "Manual serial Release run. CPU kernel files remained unchanged through the hash-checked accepted IO run; original CPU entry-point binary was subsequently rebuilt. First IO rows from this directory are discarded.",
                "measurements": cpu},
        "io": {"artifact": str(args.io_directory / "data" / "summary.json"), "sha256": sha(args.io_directory / "data" / "summary.json"),
               "provenance": io_provenance, "laterChangedEntryPoints": io_changed,
               "environment": io["Environment"], "correctness": io["Correctness"],
               "pythonVerification": read(args.io_directory / "data" / "python-verification.json"),
               "measurements": io["IO"], "metadata": io["Metadata"]},
        "focused": {"artifact": str(args.focused_directory / "data" / "summary.json"), "sha256": sha(args.focused_directory / "data" / "summary.json"),
                    "provenance": focused_provenance, "environment": focused["Environment"],
                    "correctness": focused["Correctness"],
                    "pythonVerification": read(args.focused_directory / "data" / "python-verification.json"),
                    "reader": focused["FocusedEvidence"]["Reader"], "largeFrame": large,
                    "largeFrameSha256": sha(large["Artifact"])},
        "productionRbfTests": {"artifact": str(trx_files[0]), "sha256": sha(trx_files[0]), "configuration": "Release", "counters": counters},
        "limits": ["One CPU/SSD, OS warm buffered I/O; not controlled cold-device performance.",
                   "No production RBF2 Builder visitor, pooled workspace lifecycle or recovery implementation was exercised.",
                   "Addition was tested as complete-word kernels and independent wire vectors, not a whole writer or nonaligned preview I/O pipeline.",
                   "PreparedFrame/footer/bitmap allocations and fixed workspace ownership are prototype choices.",
                   "No solution, package, publication, arbitrary syscall termination or power-loss acceptance."]
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(snapshot, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(json.dumps({"snapshot": str(args.output), "cpuRows": len(cpu), "ioRows": len(io["IO"]), "focusedReaderRows": 20,
                      "changedIoEntryPoints": io_changed, "sourceFiles": len(current_sources), "rbfPassed": 504}))


if __name__ == "__main__":
    main()
