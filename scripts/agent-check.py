"""Run repository checks with quiet output and share overlapping results."""

import argparse
from collections import deque
from contextlib import contextmanager
import fcntl
import hashlib
import json
import math
import os
from pathlib import Path
import re
import shlex
import signal
import subprocess
import sys
import tempfile
import time


ROOT = Path(__file__).resolve().parent.parent
PROJECTS = {
    "backend": "backend.Tests/OrchestratorApi.Tests.csproj",
    "runner": "runner.Tests/AgentRunner.Tests.csproj",
    "task-server": "task-server.Tests/TaskServer.Tests.csproj",
    "orchestrator-engine": "orchestrator-engine.Tests/OrchestratorEngine.Tests.csproj",
}
GUARD_FILTER = "FullyQualifiedName~.Architecture.|Category=Guard"
TEST_FILTER = "Category!=MachineBound"
NPM = ["bash", "scripts/npm-frontend.sh", "run"]
CHECKS = {
    "build": (1800, ["dotnet", "build", "agent-taskboard.sln", "--no-restore"]),
    "release-build": (1800, ["bash", "scripts/check-release-build.sh"]),
    "frontend-build": (900, NPM + ["build"]),
    "frontend-lint": (900, NPM + ["lint"]),
    "self-test": (60, [sys.executable, "-B", "scripts/agent-check.test.py"]),
}
ANSI = re.compile(r"\x1b\[[0-?]*[ -/]*[@-~]")
BUILD_ERROR = re.compile(r":\s*error\b|\b(?:ERROR|Error TS\d+)\b")
VSTEST_FAILURE = re.compile(r"Failed (.+?)\s+\[(?:< ?)?[\d.]+\s*(?:ms|s|m|min|h)\]$")
TEST_FAILURE = re.compile(r"(?:FAIL\s+|(?:FAIL|ERROR):\s+|\d+\)\s+\[.+?\]\s+›\s+)(.+)")
TEST_ERROR = re.compile(r"(?:\w*Error|expect)\b")
MAX_SUMMARY_LINES = 20


def parse_check(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    checks = parser.add_subparsers(dest="check", required=True)
    for name in (*CHECKS, "guards", "docs"):
        checks.add_parser(name)
    test = checks.add_parser("test", help="run a .NET test class or VSTest filter")
    test.add_argument("filter")
    test.add_argument("--project", choices=PROJECTS)
    specs = checks.add_parser("frontend-specs", help="run selected Angular unit specs")
    specs.add_argument("paths", nargs="+")
    e2e = checks.add_parser("frontend-e2e", help="run Playwright, optionally narrowed to selected specs")
    e2e.add_argument("paths", nargs="*")
    return parser.parse_args(argv)


def check_commands(args):
    if args.check in CHECKS:
        seconds, command = CHECKS[args.check]
        return seconds, [command]
    if args.check == "frontend-specs":
        includes = [arg for path in args.paths for arg in ("--include", path)]
        return 900, [NPM + ["test:ci", "--", *includes]]
    if args.check == "frontend-e2e":
        command = NPM + ["e2e"]
        if args.paths:
            command += ["--", *args.paths]
        return 1800, [command]
    if args.check == "docs":
        return 300, [
            ["node", "--test", "scripts/check-public-docs.test.mjs"],
            ["node", "scripts/check-public-docs.mjs", "--verify-install-urls"],
        ]

    test_filter = GUARD_FILTER
    projects = PROJECTS.values()
    if args.check == "test":
        if args.project:
            projects = [PROJECTS[args.project]]
        test_filter = args.filter
        if re.fullmatch(r"[\w.]+", test_filter):
            test_filter = f"FullyQualifiedName~{test_filter}"
    return 300 * len(projects), [
        ["dotnet", "test", project, "--no-build", "--filter", f"({TEST_FILTER})&({test_filter})"]
        for project in projects
    ]


def host_timeout(base_seconds):
    try:
        load = os.getloadavg()[0]
    except OSError:
        return base_seconds
    cpu_count = os.cpu_count() or 1
    return math.ceil(base_seconds * max(1, min(4, load / cpu_count)))


@contextmanager
def check_flight(key):
    # The gate protects the active file's name. Followers keep its descriptor,
    # so a new invocation can start immediately without replacing their result.
    state = Path(tempfile.gettempdir()) / f"agent-check-{os.getuid()}"
    state.mkdir(mode=0o700, exist_ok=True)
    active = state / key
    with (state / "gate").open("a+b") as gate:
        fcntl.flock(gate, fcntl.LOCK_EX)
        flight = join_flight(active)
        owner = flight is None
        if owner:
            flight = active.open("x+b")
            fcntl.flock(flight, fcntl.LOCK_EX)
        fcntl.flock(gate, fcntl.LOCK_UN)
        with flight:
            if not owner:
                fcntl.flock(flight, fcntl.LOCK_SH)
            try:
                yield owner, flight
            finally:
                if owner:
                    fcntl.flock(gate, fcntl.LOCK_EX)
                    active.unlink()


def join_flight(active):
    try:
        flight = active.open("rb")
    except FileNotFoundError:
        return None
    try:
        fcntl.flock(flight, fcntl.LOCK_SH | fcntl.LOCK_NB)
    except BlockingIOError:
        return flight
    # A killed owner may have left this name behind. Followers already
    # attached to its descriptor can still read the interrupted result.
    flight.close()
    active.unlink()
    return None


def run_command(command, log, deadline):
    log.write(f"$ {shlex.join(command)}\n".encode())
    try:
        child = subprocess.Popen(
            command, stdin=subprocess.DEVNULL, stdout=log, stderr=subprocess.STDOUT,
            start_new_session=True, env={**os.environ, "DOTNET_CLI_UI_LANGUAGE": "en-US", "NO_COLOR": "1"},
        )
    except OSError as error:
        log.write(f"Error: {error}\n".encode())
        return 1, False
    try:
        code = child.wait(timeout=max(0, deadline - time.monotonic()))
        return code if code >= 0 else 128 - code, False
    except subprocess.TimeoutExpired:
        return 124, True
    finally:
        if child.returncode is None:
            try:
                os.killpg(child.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
            child.wait()


def log_lines(start, end, log_path):
    with log_path.open("rb") as log:
        log.seek(start)
        while log.tell() < end:
            yield ANSI.sub("", log.readline().decode(errors="replace")).strip()


def failure_details(lines):
    tests = {}
    diagnostics = {}
    tail = deque(maxlen=4)
    current = None
    message = None
    for line in lines:
        if not line or line.startswith("$ "):
            continue
        tail.append(line)
        vstest = VSTEST_FAILURE.fullmatch(line)
        other_test = TEST_FAILURE.fullmatch(line)
        if vstest or other_test:
            current = (vstest or other_test)[1]
            tests.setdefault(current, "")
            message = "exception" if other_test else None
        elif current and line == "Error Message:":
            message = "vstest"
        elif current and line == "Stack Trace:":
            current = None
        elif current and not tests[current] and (
            message == "vstest" or (message == "exception" and TEST_ERROR.match(line))
        ):
            tests[current] = line
        elif BUILD_ERROR.search(line) or line.startswith(("Error:", "npm error", "No test matches")):
            diagnostics.setdefault(line, None)

    if tests:
        return [f"Failed {name}" + (f" | {message}" if message else "") for name, message in tests.items()]
    return list(diagnostics) or list(tail)


def run_check(commands, base_seconds, results, label, check):
    started = time.monotonic()
    budget = host_timeout(base_seconds)
    deadline = started + budget
    details = []
    code = 0
    timed_out = False
    test_matched = False
    with tempfile.NamedTemporaryFile(
        mode="wb", buffering=0, dir=results, prefix=f"{check}-", suffix=".log", delete=False,
    ) as log:
        log_path = Path(log.name)
        for command in commands:
            start = log.tell()
            code_for_command, timed_out = run_command(command, log, deadline)
            end = log.tell()
            if check == "test" and not code_for_command:
                test_matched |= any(line.startswith(("Passed!", "Skipped!")) for line in log_lines(start, end, log_path))
            if code_for_command:
                code = code_for_command
                errors = failure_details(log_lines(start, end, log_path))
                details.extend(errors or [f"{shlex.join(command)} produced no diagnostic output"])
            if timed_out:
                break

    if check == "test" and not code and not test_matched:
        code = 1
        details.append("No test results matched the filter in the selected projects.")
    duration = time.monotonic() - started
    if timed_out:
        status = f"TIMEOUT after {budget}s"
    elif code:
        status = f"FAIL (exit {code})"
    else:
        status = "PASS"
    summary = f"{label} | {status} | {duration:.1f}s | log: {log_path}"
    details = list(dict.fromkeys(details))
    if len(details) >= MAX_SUMMARY_LINES:
        shown = MAX_SUMMARY_LINES - 2
        details = details[:shown] + [f"{len(details) - shown} more diagnostic lines in the full log"]
    return code, "\n".join([summary, *(line[:500] for line in details)])


def interrupted(signum, _frame):
    raise SystemExit(128 + signum)


def shared_result(key, commands, seconds, results, label, check):
    with check_flight(key) as (owner, flight):
        if owner:
            code, summary = run_check(commands, seconds, results, label, check)
            flight.write(json.dumps([code, summary]).encode())
            flight.flush()
            return code, summary
        try:
            return json.load(flight)
        except (json.JSONDecodeError, UnicodeDecodeError):
            return 1, f"{label} | FAIL | interrupted"


def main():
    args = parse_check()
    seconds, commands = check_commands(args)
    # The managed attempt supplies its own results directory. The fallback is
    # useful for local contributors and stays scoped to the current checkout.
    result_root = Path(os.environ.get("JOB_RESULTS_DIR") or ROOT / "results").resolve()
    results = result_root / "agent-check"
    results.mkdir(parents=True, exist_ok=True)
    os.chdir(ROOT)
    identity = [str(ROOT), str(result_root), args.check, commands, os.environ.get("SKIP_BILLABLE")]
    key = hashlib.sha256(json.dumps(identity).encode()).hexdigest()
    label = shlex.join(["agent-check", *sys.argv[1:]]).replace("\n", "\\n").replace("\r", "\\r")
    for signum in (signal.SIGINT, signal.SIGTERM):
        signal.signal(signum, interrupted)
    code, summary = shared_result(key, commands, seconds, results, label, args.check)
    print(summary)
    return code


if __name__ == "__main__":
    raise SystemExit(main())
