#!/usr/bin/env python3
"""Measure a backend suite against suite-owned synthetic instance state."""

import argparse
from datetime import datetime, timezone
import gzip
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import time
import xml.etree.ElementTree as ET


def pressure():
    path = Path("/proc/pressure/io")
    if not path.exists():
        return None
    return float(path.read_text().splitlines()[0].split()[1].split("=")[1])


def entries(path):
    return set(os.listdir(path)) if path.exists() else set()


def digest(names):
    return hashlib.sha256("\n".join(sorted(names)).encode()).hexdigest()


def clean_suite(seed):
    """Finish cleanup of this run's recorded root after its test host exits."""
    root = Path(seed["suiteRoot"])
    if not root.is_absolute() or not re.fullmatch(r"ats-[0-9a-f]{8}", root.name) or root.is_symlink():
        raise ValueError("Refusing cleanup of an unexpected suite path")
    if Path(seed["stateRoot"]) != root / "state":
        raise ValueError("State must belong to the recorded suite")
    pid = seed["processId"]
    if not isinstance(pid, int) or pid <= 0:
        raise ValueError("A positive test host process id is required")
    deadline = time.monotonic() + 5
    while True:
        try:
            os.kill(pid, 0)
        except ProcessLookupError:
            break
        if time.monotonic() >= deadline:
            raise RuntimeError(f"Test host {pid} is still alive")
        time.sleep(.05)
    if root.exists():
        shutil.rmtree(root)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--label", required=True)
    parser.add_argument("--siblings", type=int, choices=[0, 200000], required=True)
    parser.add_argument("--results", type=Path, required=True)
    parser.add_argument("--scratch", type=Path, required=True)
    parser.add_argument("--audit-mkdirs", action="store_true",
                        help="Trace directory creation in this process tree (requires strace).")
    parser.add_argument("--focused", action="store_true",
                        help="Allow a class-filtered check on a busy host; full suites still require quiet.")
    parser.add_argument("command", nargs=argparse.REMAINDER)
    args = parser.parse_args()
    command = args.command
    if command and command[0] == "--":
        command = command[1:]
    if not command:
        parser.error("a test command is required after --")
    if args.focused:
        selection_index = command.index("--filter") + 1 if "--filter" in command else len(command)
        if selection_index >= len(command) or "FullyQualifiedName~" not in command[selection_index]:
            parser.error("--focused requires a FullyQualifiedName class filter")
    args.results.mkdir(parents=True, exist_ok=True)
    args.scratch.mkdir(parents=True, exist_ok=True)
    # A full measurement starts only below 75% of available CPUs in the
    # one-minute load average and below 5% I/O stall time over ten seconds.
    cpus = len(os.sched_getaffinity(0)) if hasattr(os, "sched_getaffinity") else os.cpu_count()
    while True:
        load = os.getloadavg()
        io = pressure()
        if args.focused or (load[0] <= cpus * .75 and (io is None or io < 5)):
            break
        print(f"Waiting for quiet host: load={load}, cpus={cpus}, io_some_avg10={io}", flush=True)
        time.sleep(30)

    shared = Path(os.environ.get("XDG_DATA_HOME") or str(Path.home() / ".local/share")) / "agentstudio/instances"
    before = entries(shared)
    seed_file = args.scratch / (args.label + "-seed.json")
    trx_file = args.scratch / (args.label + ".trx")
    seed_file.unlink(missing_ok=True)
    trx_file.unlink(missing_ok=True)
    environment = dict(os.environ)
    environment["AGENT_STUDIO_TEST_INSTANCE_SIBLINGS"] = str(args.siblings)
    environment["AGENT_STUDIO_TEST_INSTANCE_MEASUREMENT_FILE"] = str(seed_file)
    command += ["--logger", f"trx;LogFileName={trx_file}"]
    trace_file = args.scratch / (args.label + "-mkdirs.log")
    actual_command = command
    if args.audit_mkdirs:
        actual_command = ["strace", "--seccomp-bpf", "-f", "-qq", "-e", "trace=mkdir,mkdirat",
                          "-o", str(trace_file), "--", *command]
    log_file = args.results / (args.label + ".log.gz")
    print(f"Starting {args.label}: load={load}, io_some_avg10={io}", flush=True)
    started_at = datetime.now(timezone.utc).isoformat()
    start = time.monotonic()
    with gzip.open(log_file, "wb") as log:
        process = subprocess.Popen(actual_command, env=environment, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        for line in process.stdout:
            log.write(line)
        code = process.wait()
    elapsed = time.monotonic() - start
    load_after = os.getloadavg()
    io_after = pressure()
    seed = json.loads(seed_file.read_text()) if seed_file.exists() else {}
    automatic_cleanup = bool(seed) and not Path(seed["suiteRoot"]).exists()
    cleanup_start = time.monotonic()
    cleanup_error = None
    if seed:
        try:
            clean_suite(seed)
        except (OSError, ValueError, RuntimeError, KeyError) as error:
            cleanup_error = str(error)
    cleanup_seconds = time.monotonic() - cleanup_start
    after = entries(shared)
    shared_creation_calls = None
    if args.audit_mkdirs and trace_file.exists():
        shared_creation_calls = []
        with trace_file.open() as trace, gzip.open(args.results / (args.label + "-mkdirs.log.gz"), "wt") as archived:
            for line in trace:
                archived.write(line)
                if str(shared) in line:
                    shared_creation_calls.append(line.rstrip())
    tests = {}
    failures = []
    test_run_seconds = None
    if trx_file.exists():
        tree = ET.parse(trx_file)
        namespace = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
        counters = tree.find(".//t:Counters", namespace)
        tests = counters.attrib if counters is not None else {}
        times = tree.find("t:Times", namespace)
        if times is not None:
            test_run_seconds = (datetime.fromisoformat(times.get("finish"))
                                - datetime.fromisoformat(times.get("start"))).total_seconds()
        for result in tree.findall(".//t:UnitTestResult", namespace):
            if result.get("outcome") == "Failed":
                failures.append({"test": result.get("testName"), "message": "".join(result.itertext())[:12000]})
    report = {
        "label": args.label, "command": actual_command, "exitCode": code,
        "startedAtUtc": started_at,
        "backendAssemblySha256": hashlib.sha256(
            Path("backend.Tests/bin/Debug/net10.0/OrchestratorApi.dll").read_bytes()).hexdigest(),
        "loadBefore": load, "ioSomeAvg10Before": io, "cpus": cpus,
        "loadAfter": load_after, "ioSomeAvg10After": io_after,
        "commandWallSeconds": elapsed, "wallSeconds": elapsed + cleanup_seconds,
        "testRunSeconds": test_run_seconds,
        "seed": seed, "tests": tests, "failures": failures,
        "suiteRootRemoved": bool(seed) and not Path(seed["suiteRoot"]).exists(),
        "automaticSuiteCleanup": automatic_cleanup,
        "ownerCleanupSeconds": cleanup_seconds, "ownerCleanupError": cleanup_error,
        "sharedRoot": str(shared), "sharedCountBefore": len(before), "sharedCountAfter": len(after),
        "sharedNamesSha256Before": digest(before), "sharedNamesSha256After": digest(after),
        "newSharedEntries": sorted(after - before), "removedSharedEntries": sorted(before - after),
        "sharedCreationCallsFromTestTree": shared_creation_calls,
    }
    destination = args.results / (args.label + ".json")
    destination.write_text(json.dumps(report, indent=2) + "\n")
    print(f"Completed {args.label}: {elapsed:.3f}s, exit={code}, counters={tests}, "
          f"new_shared_entries={len(after - before)}, suite_root_removed={report['suiteRootRemoved']}", flush=True)
    # Preserve failing comparison measurements too; the JSON carries test status.
    return 0 if seed and tests and report["suiteRootRemoved"] and cleanup_error is None else 1


if __name__ == "__main__":
    raise SystemExit(main())
