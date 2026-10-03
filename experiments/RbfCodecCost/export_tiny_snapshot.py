"""Export guarded CPU-only evidence for the 256B EscapePayload scalar bitmap."""
import hashlib
import json
from pathlib import Path
import subprocess
import sys


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    run, target = (Path(value).resolve() for value in sys.argv[1:3])
    if target.exists():
        raise SystemExit("Snapshot already exists; choose a new filename")
    repo = Path(__file__).resolve().parents[2]
    provenance = json.loads((run / "provenance.json").read_text(encoding="utf-8-sig"))
    for entry in provenance["Sources"]:
        assert sha(repo / entry["Path"]) == entry["SHA256"], entry["Path"]
    data = run / "data"
    summary = json.loads((data / "summary.json").read_text())
    result = json.loads((data / "tiny-key.json").read_text())
    assert summary["TinyKeyEvidence"] == result and summary["Environment"]["Samples"] == 7
    assert len(result["Cpu"]) == 104 and result["Correctness"]["Frames"] == 448
    assert all(len(row["Measurement"]["NanosecondsPerOperation"]) == 7 for row in result["Cpu"])
    assert result["Correctness"]["InclusiveInterval256Boundary"] and result["Correctness"]["HighShiftAliasRejected"]
    assert not subprocess.check_output(["git", "diff", "--", "src"], cwd=repo)
    files = [run / name for name in ("build.log", "run.log", "provenance.json", "python.log", "python-random.log")]
    files += [data / name for name in ("summary.json", "tiny-key.json", "correctness.json", "vectors.json",
                                     "random-vectors.json", "python-verification.json", "python-random-verification.json")]
    snapshot = {"Schema": 1, "Study": "Zero-first, scalar ulong bitmap for EscapePayload<=256B, random otherwise",
                "EvidenceDirectory": str(run), "Provenance": provenance, "Environment": summary["Environment"],
                "Results": result, "LegacyCorrectness": summary["Correctness"],
                "PythonVerification": json.loads((data / "python-verification.json").read_text()),
                "TinyPythonVerification": json.loads((data / "python-random-verification.json").read_text()),
                "Artifacts": [{"Path": str(path), "SHA256": sha(path)} for path in files],
                "Acceptance": "104 CPU rows, seven sample rotations, source hash guard, independent wire and CRC. "
                              "No storage throughput measurement, production integration or recovery acceptance."}
    target.write_text(json.dumps(snapshot, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"snapshot": str(target), "sha256": sha(target), "cpu_rows": len(result["Cpu"])}))


if __name__ == "__main__":
    main()
