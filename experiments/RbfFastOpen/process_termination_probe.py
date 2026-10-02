"""Real process termination at controlled I/O checkpoints, with model-only RBF2 recovery.

The parent kills only children it creates. All images stay in a new OS temporary directory.
file.flush() reaches the OS; this probe does not claim fsync, power-loss, or in-syscall evidence.
"""

import argparse
import json
from pathlib import Path
import queue
import subprocess
import sys
import tempfile
import threading


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def child_main(task):
    """One operation followed by a READY handshake and an indefinite parent-owned wait."""
    path = Path(task["path"])
    with path.open("r+b") as stream:
        mode, stage = task["mode"], task["stage"]
        written = 0
        if mode == "append":
            data = bytes.fromhex(task["dataHex"])
            stream.seek(0, 2)
            written = stream.write(data)
            require(written == len(data), "Child append returned a short write")
        elif mode == "truncate":
            if stage != "before":
                stream.truncate(task["length"])
        elif mode == "closure":
            if stage != "before":
                data = bytes.fromhex(task["dataHex"])[:task["writeCount"]]
                stream.seek(0, 2)
                written = stream.write(data)
                require(written == len(data), "Child tail closure append returned a short write")
        else:
            raise ValueError(f"Unknown child mode: {mode}")
        # Flush Python's buffer to the OS, without fsync or any claim about stable media.
        stream.flush()
        print(json.dumps({
            "ready": True, "mode": mode, "stage": stage,
            "writtenBytes": written, "fileLength": path.stat().st_size,
        }), flush=True)
        # The parent keeps stdin open and kills us here; graceful close/Dispose cannot repair bytes.
        sys.stdin.buffer.read(1)


def kill_after_ready(task, timeout_seconds):
    """Bound every handshake/wait; finally touches only this invocation's Popen child."""
    child = subprocess.Popen(
        [sys.executable, "-B", str(Path(__file__).resolve()), "--child", json.dumps(task)],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        text=True, encoding="utf-8", bufsize=1,
    )
    ready_line = queue.Queue(maxsize=1)

    def read_ready():
        try:
            ready_line.put(child.stdout.readline())
        except Exception as error:
            ready_line.put(error)

    threading.Thread(target=read_ready, daemon=True).start()
    try:
        try:
            line = ready_line.get(timeout=timeout_seconds)
        except queue.Empty as error:
            raise TimeoutError(f"Child READY timed out: {task['mode']}/{task['stage']}") from error
        if isinstance(line, Exception):
            raise line
        require(bool(line), f"Child exited before READY: {task['mode']}/{task['stage']}")
        ready = json.loads(line)
        require(ready.get("ready") is True, "Invalid child READY handshake")
        require(child.poll() is None, "Child exited instead of waiting for the parent's kill")
        child.kill()
        _, stderr = child.communicate(timeout=timeout_seconds)
        require(child.returncode != 0, "A graceful child exit cannot count as process-termination evidence")
        require(not stderr.strip(), f"Child stderr: {stderr.strip()}")
        return ready
    finally:
        if child.poll() is None:
            child.kill()
        try:
            child.communicate(timeout=timeout_seconds)
        except subprocess.TimeoutExpired:
            child.kill()
            child.wait(timeout=timeout_seconds)


def recovery_plan(image, recovered, action, affected_start):
    if action == "None":
        require(recovered == image and affected_start is None, "None must preserve bytes and have no affected frame")
        return None
    if action == "Truncated":
        require(affected_start == len(recovered), "Truncated must end at the affected frame's real start")
        require(image.startswith(recovered), "Truncation modified the closed prefix")
        return {"mode": "truncate", "length": len(recovered)}
    if action == "CompletedTail":
        require(recovered.startswith(image), "Tail completion modified existing body, Key or Fence bytes")
        suffix = recovered[len(image):]
        require(1 <= len(suffix) <= 8, "Tail completion must append only missing Key/Fence bytes")
        return {"mode": "closure", "dataHex": suffix.hex()}
    raise AssertionError(f"Unexpected recovery action: {action}")


def run(timeout_seconds):
    # Child mode needs no model imports. This also keeps real I/O operations independent of the oracle.
    from probe import F2, MARK, encode, word
    from qualification_probe import recover_image

    directory = Path(tempfile.mkdtemp(prefix="atelia-rbf-process-termination-"))
    base = F2 + encode(b"durable predecessor A", b"meta A") + encode(bytes(range(32)), b"meta B")
    frame = encode(bytes(range(64)) + F2 * 4, b"tail metadata", tag=MARK)
    length = word(frame)
    coverage_end, trailer_start, tail_key_start = length - 24, length - 20, length - 4
    stages = []
    stages.extend((f"partial-head-{n}", n) for n in (1, 2, 3))
    stages.extend((f"coverage-{n}", n) for n in (8, 9, 25, coverage_end - 1))
    stages.extend((f"payload-crc-{n}", coverage_end + n) for n in (1, 2, 3, 4))
    stages.extend((f"trailer-{n}", trailer_start + n) for n in (1, 4, 5, 7, 8, 11, 12, 15, 16))
    stages.extend((f"tail-key-{n}", tail_key_start + n) for n in (1, 2, 3))
    stages.extend((f"fence-{n}", length + n) for n in (0, 1, 2, 3, 4))

    append_kills = recovery_kills = completion_kills = 0
    observations = []
    for index, (name, cut) in enumerate(stages):
        path = directory / f"{index:02d}-{name}.rbf"
        path.write_bytes(base)
        kill_after_ready({
            "path": str(path), "mode": "append", "stage": name,
            "dataHex": frame[:cut].hex(),
        }, timeout_seconds)
        append_kills += 1
        image = path.read_bytes()
        require(image == base + frame[:cut], f"{name}: actual append image differs from the acknowledged prefix")
        recovered, action, affected = recover_image(image)
        expected_action = "Truncated" if cut < length - 4 else "CompletedTail" if cut < length + 4 else "None"
        expected = base if cut < length - 4 else base + frame
        require(action == expected_action, f"{name}: wrong action {action}, expected {expected_action}")
        require(recovered == expected and recovered.startswith(base), f"{name}: predecessor or complete FrameBytes changed")
        if action != "None":
            require(affected == len(base), f"{name}: recovery chose the wrong frame start")
            try:
                recover_image(image, read_only=True)
            except (ValueError, AssertionError):
                pass
            else:
                raise AssertionError(f"{name}: read-only recovery accepted an unclosed image")
        plan = recovery_plan(image, recovered, action, affected)
        operation_stages = []
        if plan is not None:
            if plan["mode"] == "truncate":
                operation_stages = [("before", 0), ("after-truncate", 0)]
            else:
                missing = len(bytes.fromhex(plan["dataHex"]))
                operation_stages = [("before", 0)] + [(f"after-closure-{n}", n) for n in range(missing + 1)]
        for operation_index, (stage, write_count) in enumerate(operation_stages):
            mutation_path = directory / f"{index:02d}-{name}-repair-{operation_index}.rbf"
            mutation_path.write_bytes(image)
            task = {"path": str(mutation_path), **plan, "stage": stage, "writeCount": write_count}
            kill_after_ready(task, timeout_seconds)
            recovery_kills += 1
            interrupted = mutation_path.read_bytes()
            restarted, restarted_action, restarted_affected = recover_image(interrupted)
            require(restarted == expected and interrupted.startswith(base), f"{name}/{stage}: restart lost the predecessor or complete frame")

            # Actually finish the derived operation in a new child, then kill after its acknowledged output.
            # Expected complete bytes are only a test oracle; the child consumes a length or closure suffix.
            remaining = recovery_plan(interrupted, restarted, restarted_action, restarted_affected)
            if remaining is not None:
                remaining_count = len(bytes.fromhex(remaining.get("dataHex", "")))
                kill_after_ready({
                    "path": str(mutation_path), **remaining,
                    "stage": "after-completion", "writeCount": remaining_count,
                }, timeout_seconds)
                completion_kills += 1
            final_image = mutation_path.read_bytes()
            require(final_image == expected, f"{name}/{stage}: actual completed file differs from the permitted result")
            final, final_action, final_affected = recover_image(final_image)
            require(final == expected and final_action == "None" and final_affected is None,
                    f"{name}/{stage}: completed tail is not idempotent on reopen")
        observations.append({
            "appendStage": name, "framePrefixBytes": cut, "initialAction": action,
            "affectedStart": affected, "recoveryTerminationStages": len(operation_stages),
        })

    return {
        "schemaVersion": 2,
        "passed": True,
        "temporaryDirectory": str(directory),
        "appendTerminationCases": append_kills,
        "recoveryTerminationCases": recovery_kills,
        "completedRecoveryTerminationCases": completion_kills,
        "totalKilledChildren": append_kills + recovery_kills + completion_kills,
        "closedPredecessorBytes": len(base),
        "candidateFrameBytes": length,
        "observations": observations,
        "evidence": {
            "realChildProcessesKilled": True,
            "actualFilesReopened": True,
            "ioFlush": "Python file.flush() to the OS; no fsync or device durability claim",
            "recoveryAlgorithm": "qualification_probe.recover_image; experimental RBF2 model, not production RBF",
            "productionRbf2Invoked": False,
            "terminationPoints": "Only acknowledged READY checkpoints, before/after truncate and after every missing Key/Fence byte prefix",
            "arbitraryInSyscallTerminationCovered": False,
            "machinePowerLossCovered": False,
            "cleanup": "Generated files retained; finally kills only this invocation's own Popen child",
        },
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--timeout-seconds", type=float, default=10.0)
    parser.add_argument("--child", help=argparse.SUPPRESS)
    args = parser.parse_args()
    if args.child is not None:
        child_main(json.loads(args.child))
        return
    require(0 < args.timeout_seconds <= 60, "Timeout must be in (0, 60] seconds")
    print(json.dumps(run(args.timeout_seconds), indent=2))


if __name__ == "__main__":
    main()
