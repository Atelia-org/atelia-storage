"""Run model and current-production cross-checks; .NET build/run are serialized."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile

sys.dont_write_bytecode = True
HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]


def command(arguments, log=None):
    environment = os.environ.copy()
    environment["PYTHONDONTWRITEBYTECODE"] = "1"
    environment["DOTNET_CLI_UI_LANGUAGE"] = "en-US"
    completed = subprocess.run(arguments, cwd=REPO, env=environment, capture_output=True,
                               text=True, encoding="utf-8", errors="replace")
    if log:
        log.write_text(completed.stdout + completed.stderr, encoding="utf-8", newline="\n")
    if completed.returncode:
        raise RuntimeError(f"Command failed ({completed.returncode}): {arguments}\n{completed.stdout}\n{completed.stderr}")
    return completed.stdout


def json_command(arguments, path):
    result = json.loads(command(arguments, path.with_suffix(".log")))
    path.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8", newline="\n")
    return result


def verify_production_output(production):
    from compat_probe import strict_read
    verified = []
    for fixture in production["fixtureVerification"]["fixtures"]:
        wire = bytes.fromhex(fixture["wireHex"])
        actual = strict_read(wire)
        expected = [(f["offset"], f["length"], bytes.fromhex(f["payloadHex"]),
                     bytes.fromhex(f["metaHex"]), f["tag"]) for f in fixture["frames"]]
        assert actual == expected, fixture["name"]
        assert all(not f["isTombstone"] for f in fixture["frames"])
        verified.append({"name": fixture["name"], "frames": len(actual), "wireBytes": len(wire),
                         "wireSha256": hashlib.sha256(wire).hexdigest()})
    return {"passed": True, "direction": "production RBF1 writer -> independent Python strict decoder",
            "fixtures": verified}


def run(output, assert_baseline):
    output.mkdir(parents=True, exist_ok=True)
    # Preserve failed runs' logs as well as successful JSON evidence.
    if any(output.iterdir()):
        raise ValueError(f"Output must be empty; choose a new directory: {output}")
    models = {}
    for name in ("probe", "compat_probe", "qualification_probe", "process_termination_probe"):
        arguments = [sys.executable, "-B", str(HERE / (name + ".py"))]
        if name == "compat_probe":
            arguments.extend(["--export-fixtures", str(output / "fixtures")])
        models[name] = json_command(arguments, output / (name + ".json"))
    project = HERE / "ProductionProbe" / "ProductionProbe.csproj"
    command(["dotnet", "build", str(project), "-c", "Release", "--nologo"], output / "production-build.log")
    arguments = ["dotnet", "run", "--project", str(project), "-c", "Release", "--no-build", "--",
                 "--verify-fixtures", str(output / "fixtures")]
    if assert_baseline:
        arguments.append("--assert-baseline-5711c47")
    production = json_command(arguments, output / "production.json")
    assert production["passed"]
    crosscheck = verify_production_output(production)
    (output / "production-crosscheck.json").write_text(json.dumps(crosscheck, indent=2) + "\n", encoding="utf-8", newline="\n")
    process = models["process_termination_probe"]
    assert process["passed"]
    summary = {"schemaVersion": 3, "recoveryPolicy": "truncate-incomplete-body-complete-tail-key-fence",
               "openPolicy": "structure-only-payload-crc-on-read", "keyPlacement": "tail-only",
               "faultTarget": "process-termination",
               "sourceRevision": command(["git", "rev-parse", "HEAD"]).strip(),
               "sdk": command(["dotnet", "--version"]).strip(),
               "scriptSha256": {str(path.relative_to(HERE)).replace("\\", "/"): hashlib.sha256(path.read_bytes()).hexdigest()
                                for path in [HERE / "probe.py", HERE / "compat_probe.py", HERE / "qualification_probe.py",
                                             HERE / "process_termination_probe.py", Path(__file__), project, project.parent / "Program.cs"]},
               "model": models["probe"], "qualification": models["qualification_probe"],
               "compatibility": {k: v for k, v in models["compat_probe"].items() if k != "exported_fixtures"},
               "processTermination": {k: v for k, v in process.items() if k not in ("temporaryDirectory", "observations")},
               "productionGoldenCrosscheck": crosscheck,
               "productionOpenMetrics": production["openMetrics"],
               "historicalFenceTrace": {"currentStrictOpenRejected": production["historicalFenceTrace"]["strictOpen"]["rejected"],
                   "ordinaryCheckedReadSucceeded": production["historicalFenceTrace"]["unvalidatedOrdinaryFacade"]["ordinaryFrameReadSucceeded"],
                   "boundaryRejected": production["historicalFenceTrace"]["unvalidatedOrdinaryFacade"]["getScanBoundaryAfterRejected"],
                   "fixedEofCandidateRejected": production["historicalFenceTrace"]["fixedEofOfflineCandidate"]["frameReadRejected"]},
               "limits": ["No production RBF2 codec was exercised; recovery is an experimental model.",
                          "Process termination was tested only at acknowledged I/O checkpoints, not inside arbitrary syscalls.",
                          "Not solution acceptance, package consumption, power-loss testing, or a latency benchmark."]}
    (output / "summary.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8", newline="\n")
    return {"passed": True, "outputDirectory": str(output), "summary": str(output / "summary.json"),
            "productionFixtures": len(crosscheck["fixtures"]),
            "secondRecoveryStates": summary["qualification"]["second_recovery_states"],
            "killedChildren": process["totalKilledChildren"]}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output-directory", type=Path, help="New directory; default is a fresh OS temporary directory.")
    parser.add_argument("--assert-baseline-5711c47", action="store_true")
    args = parser.parse_args()
    directory = args.output_directory.resolve() if args.output_directory else Path(tempfile.mkdtemp(prefix="rbf-fast-open-evidence-"))
    print(json.dumps(run(directory, args.assert_baseline_5711c47), indent=2))
