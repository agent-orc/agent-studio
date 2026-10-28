#!/usr/bin/env python3
"""Drive one registered project card through the installed Task Server.

The prepare phase runs after owner bootstrap and before starting the dedicated
review and integration workers. Credentials stay in private files. The verify
phase requires the installed workers to publish the reviewed result to Git.
"""

import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import time
from urllib import error, parse, request


PROJECT_ID = "scenario-project"
REVIEW_ID = "scenario-install-review"
INTEGRATION_ID = "scenario-install-integration"


def api(url, token, path, method="GET", payload=None, session=""):
    headers = {"Authorization": f"Bearer {token}", "X-Task-Protocol-Version": "2"}
    if session:
        headers["X-Studio-Session-Token"] = session
    body = None
    if payload is not None:
        headers["Content-Type"] = "application/json"
        body = json.dumps(payload).encode("utf-8")
    call = request.Request(url.rstrip("/") + path, data=body, headers=headers, method=method)
    try:
        with request.urlopen(call, timeout=20) as response:
            return json.load(response)
    except error.HTTPError as failure:
        raise RuntimeError(f"{method} {path} returned HTTP {failure.code}") from None
    except error.URLError:
        raise RuntimeError(f"{method} {path} could not reach Task Server") from None


def private_write(path, value):
    descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(descriptor, "w", encoding="utf-8") as output:
        output.write(value + "\n")


def prepare(args, token):
    session = args.owner_session_file.read_text(encoding="utf-8").strip()
    if not session:
        raise ValueError("Owner session is empty")
    plan = {
        "commands": [{"stepId": "fixture-checks", "aspect": "build-tests",
                      "fileName": args.review_shell,
                      "arguments": ["-c", "test -s scenario-run-log.txt && sh tests/known-passing.sh && ! sh tests/known-failing.sh"],
                      "timeoutSeconds": 30}],
        "requiredAspects": ["build-tests"], "integrationRef": "refs/heads/main",
    }
    api(args.server_url, token, f"/api/v1/orchestration/projects/{PROJECT_ID}/review-plan", "PUT",
        {"expectedVersion": 0, "reviewPolicyHash": "installation-fixture-v1", "plan": plan})
    for runner_id, target in ((REVIEW_ID, args.review_token_file),
                              (INTEGRATION_ID, args.integration_token_file)):
        issued = api(args.server_url, token, "/api/v1/management/principals", "POST",
                     {"principalId": f"runner:{runner_id}", "kind": "runner", "runnerId": runner_id}, session)
        credential = issued.get("credential")
        if not credential:
            raise RuntimeError(f"Task Server did not issue {runner_id} credential")
        private_write(target, credential)
    card = api(args.server_url, token, f"/api/v1/projects/{PROJECT_ID}/tasks", "POST",
               {"title": "Installed first-use fixture", "body": "Run both fixture checks and commit their result log.",
                "state": "2-ready"})
    if card.get("state") != "2-ready" or not card.get("taskId") or not card.get("taskKey"):
        raise RuntimeError("Task Server did not create the ready fixture card")
    private_write(args.card_file, json.dumps({"taskId": card["taskId"], "taskKey": card["taskKey"]}))
    print("Installed first-use card prepared.")


def verify(args, token, monotonic=time.monotonic, pause=time.sleep):
    card = json.loads(args.card_file.read_text(encoding="utf-8"))
    task_id, task_key = card["taskId"], card["taskKey"]
    deadline = monotonic() + args.timeout
    states = []
    while monotonic() < deadline:
        task = api(args.server_url, token,
                   f"/api/v1/projects/{PROJECT_ID}/tasks/{parse.quote(task_key)}")
        state = task["state"]
        if state not in states:
            states.append(state)
        if state == "5-human-review":
            break
        if state in ("5e-escalated", "7-archive"):
            raise RuntimeError(f"fixture card stopped in {state}")
        pause(2)
    else:
        raise RuntimeError(f"fixture card did not reach integration; observed {', '.join(states)}")
    history = api(args.server_url, token,
                  f"/api/v1/projects/{PROJECT_ID}/tasks/{parse.quote(task_key)}/history")
    runs = history.get("runs", [])
    if len(runs) != 1 or not runs[0].get("resultSha"):
        raise RuntimeError("fixture card has no fenced coding result")
    artifacts = history.get("artifacts", [])
    if not any("scenario-run-log" in item.get("name", "") for item in artifacts):
        raise RuntimeError("fixture card has no fake CLI result artifact")
    integrations = api(args.server_url, token,
                       "/api/v1/integrations?taskId=" + parse.quote(task_id))
    published = [item for item in integrations if item.get("status") == "integrated"
                 and item.get("lastOutcome") in ("integrated", "already-integrated")]
    if len(published) != 1 or not published[0].get("integratedSha"):
        raise RuntimeError("fixture card has no published integration record")
    audited = api(args.server_url, token, "/api/v1/management/audit")
    reviewed = [item for item in audited if item.get("action") == "review.reported"
                and item.get("targetId") == published[0].get("reviewAttemptId")]
    if len(reviewed) != 1 or json.loads(reviewed[0].get("detailJson", "{}")) .get("Outcome") != "Pass":
        raise RuntimeError("fixture card has no passing review report")
    remote = subprocess.run(["git", "ls-remote", args.repository_url, "refs/heads/main"],
                            capture_output=True, text=True, timeout=20, check=True)
    head = remote.stdout.split()[0] if remote.stdout.split() else ""
    if head.lower() != published[0]["integratedSha"].lower():
        raise RuntimeError("Git integration ref does not match the published record")
    print(f"Installed card {task_key} reached coding, review, and integration at {head}.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("phase", choices=("prepare", "verify"))
    parser.add_argument("--server-url", required=True)
    parser.add_argument("--studio-token-file", type=Path, required=True)
    parser.add_argument("--owner-session-file", type=Path)
    parser.add_argument("--repository-url", required=True)
    parser.add_argument("--card-file", type=Path, required=True)
    parser.add_argument("--review-token-file", type=Path)
    parser.add_argument("--integration-token-file", type=Path)
    parser.add_argument("--timeout", type=int, default=300)
    parser.add_argument("--review-shell", default="sh")
    args = parser.parse_args()
    try:
        token = args.studio_token_file.read_text(encoding="utf-8").strip()
        if not token:
            raise ValueError("Studio management token is empty")
        if args.phase == "prepare":
            if not args.review_token_file or not args.integration_token_file or not args.owner_session_file:
                raise ValueError("prepare needs owner, review, and integration token files")
            prepare(args, token)
        else:
            verify(args, token)
        return 0
    except (OSError, ValueError, KeyError, RuntimeError, subprocess.SubprocessError) as failure:
        print(f"Installed card probe failed: {failure}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
