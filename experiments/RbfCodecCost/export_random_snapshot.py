"""Export a new immutable ZeroThenRandom snapshot after accepting the guarded W: run."""
import hashlib
import json
from pathlib import Path
import subprocess
import sys


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    run = Path(sys.argv[1]).resolve()
    target = Path(sys.argv[2]).resolve()
    if target.exists():
        raise SystemExit("Snapshot already exists; choose a new filename")
    repo = Path(__file__).resolve().parents[2]
    provenance = json.loads((run / "provenance.json").read_text(encoding="utf-8-sig"))
    derivation_changes = []
    for entry in provenance["Sources"]:
        current_hash = sha(repo / entry["Path"])
        if current_hash != entry["SHA256"]:
            # This script is a post-run evidence derivation, never a C# benchmark kernel.
            assert entry["Path"] == "experiments/RbfCodecCost/export_random_snapshot.py", entry["Path"]
            derivation_changes.append({"Path": entry["Path"], "RecordedSHA256": entry["SHA256"],
                                       "CurrentSHA256": current_hash, "Reason": "Clarify legacy entrypoint Samples metadata after measurement"})
    data = run / "data"
    summary = json.loads((data / "summary.json").read_text())
    results = json.loads((data / "random-search.json").read_text())
    assert results == summary["RandomSearchEvidence"]
    assert results["Correctness"]["ForcedBitmapFallback"] and results["Correctness"]["ZeroRNGCalls"]
    assert results["Correctness"]["PythonVectors"] == 153
    assert results["MaximumFrame"]["FrameBytes"] == 268435452
    assert results["MaximumFrame"]["BitmapBytes"] == 0
    assert len(results["Cpu"]) == 185 and len(results["IO"]) == 20
    assert all(len(row["Measurement"]["NanosecondsPerOperation"]) == 7 for row in results["Cpu"])
    assert all(len(row["AppendMilliseconds"]) == len(row["FlushMilliseconds"]) == 7 for row in results["IO"])
    production_diff = subprocess.check_output(["git", "diff", "--", "src"], cwd=repo, text=True)
    assert not production_diff, "Unexpected production source change"
    files = [run / name for name in ("build.log", "run.log", "provenance.json", "python.log", "python-random.log")]
    files += [data / name for name in ("summary.json", "random-search.json", "correctness.json", "vectors.json",
                                      "random-vectors.json", "python-verification.json", "python-random-verification.json")]
    payload = {"Schema": 1, "Study": "ZeroThenRandom OS-CSPRNG search, with and without bitmap fallback",
               "EvidenceDirectory": str(run), "Provenance": provenance, "Environment": summary["Environment"],
               "MeasuredProtocol": {"RandomSearchSamples": 7, "CPUOrder": "Rotate five strategies by sample",
                                    "IOOrder": "Rotate five strategies by sample",
                                    "RecordedEnvironmentSamples": "Legacy entrypoint default 5, unused by RandomSearchProbe; all actual CPU/IO sample arrays have length 7"},
               "PostMeasurementDerivationChanges": derivation_changes,
               "LegacyCorrectness": summary["Correctness"], "Results": results,
               "PythonVerification": json.loads((data / "python-verification.json").read_text()),
               "RandomPythonVerification": json.loads((data / "python-random-verification.json").read_text()),
               "Artifacts": [{"Path": str(path), "SHA256": sha(path)} for path in files],
               "Acceptance": "Guarded Release run; 7 rotated samples, all kernels unchanged during measurement. "
                             "Production src unchanged. CPU observations are untimed separate preparations; "
                             "no deterministic time bound for random-loop search, no cold SSD or production recovery acceptance."}
    target.write_text(json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"snapshot": str(target), "sha256": sha(target), "cpu_rows": len(results["Cpu"]),
                      "io_rows": len(results["IO"]), "source_hashes": len(provenance["Sources"])}))


if __name__ == "__main__":
    main()
