"""Export immutable Data public-input cost qualification, with all measured source hashes."""
import argparse
import hashlib
import json
from pathlib import Path
import xml.etree.ElementTree as ET


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("run")
    parser.add_argument("snapshot")
    parser.add_argument("--test-trx", action="append", default=[])
    args = parser.parse_args()
    run, target = Path(args.run).resolve(), Path(args.snapshot).resolve()
    if target.exists():
        raise SystemExit("Snapshot exists; choose a new immutable filename")
    repo = Path(__file__).resolve().parents[2]
    data = run / "data"
    provenance = read(run / "provenance.json")
    for entry in provenance["Sources"]:
        assert sha(repo / entry["Path"]) == entry["SHA256"], entry["Path"]
    assert any(entry["Path"].startswith("src/Data/Binary/XorEscape") for entry in provenance["Sources"])
    assert any(entry["Path"] == "src/Data/SinkReservableWriter.cs" for entry in provenance["Sources"])
    summary = read(data / "summary.json")
    results = read(data / "data-escape.json")
    assert results == summary["DataEscapeEvidence"]
    assert not results["Quick"] and results["Samples"] == 7
    assert len(results["Cpu"]) == 189 and len(results["Writer"]) == 40
    assert results["QualifiedAppendCases"] == 63 and results["QualifiedWriterCases"] == 10
    assert all(len(row["NanosecondsPerOperation"]) == 7 for row in results["Cpu"] + results["Writer"])
    assert all(row["Rents"] == row["Returns"] for row in results["Writer"])
    # Reuse every workload, but do not compare historical ns directly: current meta is min(input,3).
    tiny = read(repo / "experiments/RbfCodecCost/results/tiny-ulong-7667b9c-20261003.json")
    random = read(repo / "experiments/RbfCodecCost/results/zero-then-random-0e0df09-20261003.json")
    for cohort, old in (("tiny", tiny), ("random", random)):
        expected = {row["Measurement"]["Workload"] for row in old["Results"]["Cpu"]}
        actual = {row["Workload"] for row in results["Cpu"] if row["Cohort"] == cohort}
        assert actual == expected, (cohort, actual ^ expected)
    python_data = read(data / "python-data-verification.json")
    assert python_data["data_vectors"] == 38
    artifacts = [run / name for name in ("provenance.json", "build.log", "run.log", "python.log", "python-data.log")]
    artifacts += [data / name for name in ("summary.json", "data-escape.json", "data-vectors.json",
                                           "python-data-verification.json", "correctness.json", "vectors.json",
                                           "python-verification.json")]
    tests = []
    for value in args.test_trx:
        path = Path(value).resolve()
        tree = ET.parse(path)
        counter = next(element for element in tree.iter() if element.tag.endswith("Counters"))
        assert int(counter.attrib.get("failed", "0")) == 0
        assert int(counter.attrib["passed"]) == int(counter.attrib["total"])
        tests.append({"Path": str(path), "SHA256": sha(path), "Counters": counter.attrib})
    result = {
        "Schema": 1, "Study": "Production Data XorEscape borrowed-three-spans and actual owned-writer costs",
        "EvidenceDirectory": str(run), "Provenance": provenance, "Environment": summary["Environment"],
        "Results": results, "LegacyCorrectness": summary["Correctness"], "PythonDataVerification": python_data,
        "Artifacts": [{"Path": str(path), "SHA256": sha(path)} for path in artifacts], "Tests": tests,
        "Acceptance": "All C# Data/experimental kernels, runner and oracle hashes unchanged. Seven rotated samples. "
                      "26 tiny and 37 random workload labels reused with current meta=min(input,3); do not compare old ns directly. "
                      "CPU/Shared ArrayPool/synchronous sink qualification only. Legacy byte-length fixture is not units RBF, "
                      "RBF2 append/open/recovery, cold SSD, solution build or package-consumption acceptance."
    }
    target.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"snapshot": str(target), "sha256": sha(target), "cpu_rows": len(results["Cpu"]),
                      "writer_rows": len(results["Writer"]), "source_hashes": len(provenance["Sources"])}))


if __name__ == "__main__":
    main()
