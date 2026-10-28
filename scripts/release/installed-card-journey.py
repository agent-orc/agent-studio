#!/usr/bin/env python3
"""Drive one disposable card through the installed Task Server and candidate runners.

The caller owns the installed services and supplies their credential files. This
probe owns only its scratch Git repository, runner processes, and report. It
never places credentials or process logs in the report.
"""

import argparse
import json
import os
from pathlib import Path
import secrets
import socket
import subprocess
import sys
import time
from urllib import error, parse, request
import uuid


ROOT = Path(__file__).resolve().parents[2]
DEFAULT_FAKE_CLI = ROOT / "testsupport/scenario/scenario-coding-agent.sh"


def call(base: str, token: str, method: str, path: str, body=None, session: str = ""):
    headers = {"Authorization": f"Bearer {token}", "X-Task-Protocol-Version": "2"}
    if session:
        headers["X-Studio-Session-Token"] = session
    data = None if body is None else json.dumps(body).encode("utf-8")
    if data is not None:
        headers["Content-Type"] = "application/json"
    target = base.rstrip("/") + path
    try:
        with request.urlopen(request.Request(target, data, headers, method=method), timeout=15) as response:
            return json.load(response)
    except error.HTTPError as failure:
        raise RuntimeError(f"{method} {path} returned HTTP {failure.code}") from None
    except error.URLError:
        raise RuntimeError(f"{method} {path} could not reach the installed Task Server") from None


def git(*arguments: str, cwd: Path) -> str:
    result = subprocess.run(["git", *arguments], cwd=cwd, capture_output=True,
                            text=True, timeout=30, check=False)
    if result.returncode:
        raise RuntimeError(f"Git {arguments[0]} failed with code {result.returncode}")
    return result.stdout.strip()


def start_remote(root: Path) -> tuple[str, subprocess.Popen]:
    repository = root / "scenario.git"
    seed = root / "seed"
    git("init", "--bare", "--initial-branch=main", str(repository), cwd=root)
    git("-C", str(repository), "config", "daemon.receivepack", "true", cwd=root)
    seed.mkdir()
    git("init", "--initial-branch=main", cwd=seed)
    (seed / "tests").mkdir()
    (seed / "README.md").write_text("Installed candidate first-use fixture\n", encoding="utf-8")
    for name, status in (("known-passing.sh", 0), ("known-failing.sh", 1)):
        path = seed / "tests" / name
        path.write_text(f"#!/bin/sh\nexit {status}\n", encoding="utf-8")
        if os.name != "nt":
            path.chmod(0o755)
    git("add", ".", cwd=seed)
    git("-c", "user.name=Scenario", "-c", "user.email=scenario@example.invalid",
        "commit", "-m", "seed", cwd=seed)
    git("remote", "add", "origin", str(repository), cwd=seed)
    git("push", "origin", "main", cwd=seed)
    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        port = probe.getsockname()[1]
    log = (root / "git-daemon.log").open("w", encoding="utf-8")
    daemon = subprocess.Popen(["git", "daemon", "--reuseaddr", "--listen=127.0.0.1",
                               f"--port={port}", f"--base-path={root}", "--export-all",
                               "--enable=receive-pack", str(root)], cwd=root,
                              stdout=log, stderr=subprocess.STDOUT)
    log.close()
    url = f"git://127.0.0.1:{port}/scenario.git"
    for _ in range(30):
        if daemon.poll() is not None:
            raise RuntimeError("Scratch Git daemon exited before it became reachable")
        try:
            if git("ls-remote", url, "refs/heads/main", cwd=root):
                return url, daemon
        except RuntimeError:
            time.sleep(0.2)
    raise RuntimeError("Scratch Git daemon did not become reachable")


def runner(binary: Path, role: str, base: str, remote: str, credential: str,
           root: Path) -> subprocess.Popen:
    identity = f"scenario-{role}"
    environment = os.environ.copy()
    environment.update({"RUNNER_AUTH_TOKEN": credential, "RUNNER_HEARTBEAT_SECONDS": "5",
                        "RUNNER_RUN_TIMEOUT_SECONDS": "120",
                        "SCENARIO_RELEASE_FILE": str(root / "release")})
    for key in tuple(environment):
        if key.startswith("RUNNER_") and key not in (
            "RUNNER_AUTH_TOKEN", "RUNNER_HEARTBEAT_SECONDS", "RUNNER_RUN_TIMEOUT_SECONDS"):
            environment.pop(key)
    arguments = [str(binary), "--poll", "--server", base, "--runner-id", identity,
                 "--runner-name", identity, "--hostname", identity, "--role", role,
                 "--git-remote", remote, "--branch", "main",
                 "--workdir", str(root / f"{role}-work"),
                 "--state-dir", str(root / f"{role}-state"),
                 "--cli", "claude", "--ttl", "15", "--max-parallelism", "1",
                 "--claim-max-load-per-core", "1000", "--poll-seconds", "1"]
    if role == "coding":
        arguments.extend(("--claude-cli", str(root / ("fake-cli.exe" if os.name == "nt" else "fake-cli"))))
    if role == "review":
        arguments.extend(("--review-workdir", str(root / "review-material")))
        environment["RUNNER_HOST_CODING_SLOTS"] = "0"
        environment["RUNNER_HOST_REVIEW_SLOTS"] = "1"
    log = (root / f"{role}.log").open("w", encoding="utf-8")
    process = subprocess.Popen(arguments, cwd=root, env=environment, stdout=log,
                               stderr=subprocess.STDOUT)
    log.close()
    return process


def wait_for_runners(base: str, token: str, processes: dict[str, subprocess.Popen]) -> None:
    deadline = time.monotonic() + 60
    # The host projection lists coding workers; review and integration roles
    # register with their own claim planes and are checked by their process
    # lifetime and the final review and integration receipts.
    expected = {"scenario-coding"}
    while time.monotonic() < deadline:
        for role, process in processes.items():
            if process.poll() is not None:
                raise RuntimeError(f"Candidate {role} runner exited with code {process.returncode}")
        actual = {row["runnerId"] for row in call(base, token, "GET", "/api/v1/runners")}
        if expected <= actual:
            return
        time.sleep(1)
    raise RuntimeError("Candidate coding runner did not register")


def run(args) -> dict:
    token = args.studio_token_file.read_text(encoding="utf-8").strip()
    code = (args.owner_code_file.read_text(encoding="utf-8").strip()
            if not args.owner_session_file else "")
    if not token or (not args.owner_session_file and not code):
        raise RuntimeError("Installed owner code or Studio token is empty")
    if not args.runner_binary.is_file() or not args.fake_cli.is_file():
        raise RuntimeError("Candidate runner binary or fixture CLI is missing")
    args.work_dir.mkdir(parents=True, exist_ok=True)
    fake_link = args.work_dir / "fake-cli"
    if fake_link.exists():
        raise RuntimeError("Scenario fake CLI path already exists")
    if os.name == "nt":
        fake_link = args.work_dir / "fake-cli.exe"
        fake_link.write_bytes(args.fake_cli.read_bytes())
    else:
        fake_link.symlink_to(args.fake_cli.resolve())
    processes = {}
    daemon = None
    try:
        if args.repository_url:
            remote = args.repository_url
        else:
            remote, daemon = start_remote(args.work_dir)
        initial_sha = git("ls-remote", remote, "refs/heads/main", cwd=args.work_dir).split()[0]
        if args.owner_session_file:
            session = args.owner_session_file.read_text(encoding="utf-8").strip()
            project_id = args.project_id
            if not session or not project_id:
                raise RuntimeError("Existing owner session and project id are required together")
        else:
            owner = call(args.server_url, token, "POST", "/api/v1/studio/auth/bootstrap", {
                "username": "scenario-owner", "password": secrets.token_urlsafe(30),
                "bootstrapCode": code,
            })
            session = owner["sessionToken"]
            workspace = call(args.server_url, token, "POST", "/api/v1/workspaces",
                             {"name": "Installation scenario"}, session)
            project_id = f"scenario-{uuid.uuid4().hex[:12]}"
            call(args.server_url, token, "POST", "/api/v1/projects/registrations", {
                "projectId": project_id, "workspaceId": workspace["workspaceId"],
                "name": "Installed candidate scratch", "taskKeyPrefix": "SCN",
                "repositoryUrl": remote, "integrationRef": "refs/heads/main",
                "deliveryPolicy": "reviewed-publication",
            }, session)
        call(args.server_url, token, "PUT", f"/api/v1/orchestration/projects/{project_id}/review-plan", {
            "expectedVersion": None, "reviewPolicyHash": "installed-scenario-v1",
            "plan": {"commands": [{"stepId": "fixture-checks", "aspect": "build-tests",
                                   "fileName": sys.executable,
                                   "arguments": ["-c", "from pathlib import Path; p=Path('scenario-run-log.txt'); assert p.is_file() and 'known-passing.sh exit=0' in p.read_text() and 'known-failing.sh exit=1' in p.read_text()"],
                                   "timeoutSeconds": 30}],
                     "requiredAspects": ["build-tests"], "integrationRef": "refs/heads/main"},
        })
        credentials = {}
        for role in ("coding", "review", "integration"):
            issued = call(args.server_url, token, "POST", "/api/v1/management/principals", {
                "principalId": f"runner:scenario-{role}", "kind": "runner",
                "runnerId": f"scenario-{role}",
            })
            credentials[role] = issued["credential"]
        (args.work_dir / "release").write_text("continue\n", encoding="ascii")
        for role, credential in credentials.items():
            processes[role] = runner(args.runner_binary, role, args.server_url, remote,
                                     credential, args.work_dir)
        wait_for_runners(args.server_url, token, processes)
        task = call(args.server_url, token, "POST", f"/api/v1/projects/{project_id}/tasks", {
            "title": "Installed candidate first-use card",
            "body": "Run tests/known-passing.sh and tests/known-failing.sh, commit the results log, and stop.",
            "state": "2-ready",
        }, session)
        task_id, task_key = task["taskId"], task["taskKey"]
        deadline = time.monotonic() + args.timeout
        state = "2-ready"
        integration = None
        while time.monotonic() < deadline:
            for role, process in processes.items():
                if process.poll() is not None:
                    raise RuntimeError(f"Candidate {role} runner exited with code {process.returncode}")
            current = call(args.server_url, token, "GET",
                           f"/api/v1/projects/{project_id}/tasks/{task_key}")
            state = current["state"]
            integrations = call(args.server_url, token, "GET",
                                "/api/v1/integrations?taskId=" + parse.quote(task_id))
            integration = integrations[-1] if integrations else None
            if state == "5-human-review" and integration and integration["status"] == "integrated":
                break
            if state in ("5e-escalated", "6-completed", "7-archive"):
                raise RuntimeError(f"Card entered unexpected lane {state} before integration proof")
            time.sleep(2)
        else:
            raise RuntimeError(f"Card did not reach reviewed integration; last lane {state}, integration {integration['status'] if integration else 'absent'}")
        history = call(args.server_url, token, "GET",
                       f"/api/v1/projects/{project_id}/tasks/{task_key}/history")
        audit = call(args.server_url, token, "GET", "/api/v1/management/audit")
        if not any(row["action"] == "review.reported" and
                   row["targetId"] == integration["reviewAttemptId"] for row in audit):
            raise RuntimeError("Installed review left no report in card history")
        if not any(row["action"] == "integration.reported" and
                   row["targetId"] == integration["integrationId"] for row in audit):
            raise RuntimeError("Installed integration left no report in card history")
        if not any(row.get("resultSha") for row in history["runs"]):
            raise RuntimeError("Installed coding run has no result SHA")
        published_sha = git("ls-remote", remote, "refs/heads/main", cwd=args.work_dir).split()[0]
        if published_sha == initial_sha or published_sha != integration["integratedSha"]:
            raise RuntimeError("Loopback main does not match the recorded integration SHA")
        verify = args.work_dir / "verify"
        verify.mkdir()
        git("init", cwd=verify)
        git("remote", "add", "origin", remote, cwd=verify)
        git("fetch", "origin", "refs/heads/main", integration["resultRef"], cwd=verify)
        git("merge-base", "--is-ancestor", integration["resultSha"], published_sha, cwd=verify)
        return {"status": "passed", "projectId": project_id, "taskKey": task_key,
                "state": state, "initialSha": initial_sha, "resultSha": integration["resultSha"],
                "integrationSha": published_sha, "reviewReports": 1,
                "runnerRoles": list(processes)}
    finally:
        for process in processes.values():
            if process.poll() is None:
                process.terminate()
        for process in processes.values():
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)
        if daemon is not None:
            if daemon.poll() is None:
                daemon.terminate()
            try:
                daemon.wait(timeout=5)
            except subprocess.TimeoutExpired:
                daemon.kill()
                daemon.wait(timeout=5)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server-url", required=True)
    parser.add_argument("--studio-token-file", type=Path, required=True)
    parser.add_argument("--owner-code-file", type=Path, required=True)
    parser.add_argument("--owner-session-file", type=Path)
    parser.add_argument("--project-id", default="")
    parser.add_argument("--repository-url", default="")
    parser.add_argument("--runner-binary", type=Path, required=True)
    parser.add_argument("--fake-cli", type=Path, default=DEFAULT_FAKE_CLI)
    parser.add_argument("--work-dir", type=Path, required=True)
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=300)
    args = parser.parse_args()
    args.runner_binary = args.runner_binary.resolve()
    args.fake_cli = args.fake_cli.resolve()
    args.work_dir = args.work_dir.resolve()
    args.report = args.report.resolve()
    try:
        result = run(args)
    except (OSError, KeyError, ValueError, RuntimeError, subprocess.TimeoutExpired) as failure:
        result = {"status": "failed", "reason": str(failure)}
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(f"Installed card journey: {result['status']}; evidence: {args.report}")
    return 0 if result["status"] == "passed" else 1


if __name__ == "__main__":
    raise SystemExit(main())
