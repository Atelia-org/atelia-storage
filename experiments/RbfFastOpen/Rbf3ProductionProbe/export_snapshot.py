"""Create a new immutable summary after verifying raw artifact/source guards.

No historical JSON is opened or rewritten. Large W: files stay in the raw run;
the snapshot embeds production/Python results and their full hash manifest.
"""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys

sys.dont_write_bytecode = True
from run import REPO, file_hash, hashes, write_json


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("run", type=Path)
    parser.add_argument("snapshot", type=Path)
    args = parser.parse_args()
    folder, target = args.run.resolve(), args.snapshot.resolve()
    assert not target.exists(), "Immutable snapshot target already exists"
    provenance = json.loads((folder / "provenance.json").read_text(encoding="utf-8"))
    assert provenance["Accepted"] and not provenance["Quick"], "Only accepted formal runs may be exported"
    assert provenance["SourceHashes"] == provenance["SourceHashesAfter"] == hashes(), "Current source differs from measured source"
    assert provenance["SourceCommit"] == provenance["SourceCommitAfter"] == subprocess.check_output(
        ["git", "rev-parse", "HEAD"], cwd=REPO, text=True).strip(), "Current commit differs from measured commit"
    for name, info in provenance["Artifacts"].items():
        path = folder / name
        assert path.stat().st_size == info["Bytes"] and file_hash(path) == info["Sha256"], f"Artifact changed: {name}"
    snapshot = dict(SchemaVersion=1, Profile="RBF3-units-tail-key", Provenance=provenance,
                    Production=json.loads((folder / "production-results.json").read_text(encoding="utf-8")),
                    Python=json.loads((folder / "python-verification.json").read_text(encoding="utf-8")),
                    PythonFixtures=json.loads((folder / "python-fixtures.json").read_text(encoding="utf-8")),
                    RawProvenanceSha256=file_hash(folder / "provenance.json"))
    target.parent.mkdir(parents=True, exist_ok=True)
    write_json(target, snapshot)
    print(json.dumps(dict(Snapshot=str(target), Sha256=file_hash(target), SourceCommit=provenance["SourceCommit"])))


if __name__ == "__main__":
    main()
