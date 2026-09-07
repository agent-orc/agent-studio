#!/usr/bin/env python3
"""Run the deployment regression scenario against a Task Server endpoint."""

from __future__ import annotations

import argparse
import base64
import hashlib
import html
import json
import os
import re
import shutil
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Callable


PROTOCOL = "2"
FIXED_CLOCK = "2026-09-06T12:00:00Z"


class ScenarioFailure(RuntimeError):
    pass


@dataclass
class StepResult:
    step_id: str
    status: str
    duration_ms: int
    evidence: list[str]
    message: str = ""


class Api:
    def __init__(self, base_url: str, studio_token: str | None, runner_token: str | None = None) -> None:
        self.base_url = base_url.rstrip("/")
        self.studio_token = studio_token
        self.runner_token = runner_token or studio_token

    def request(self, method: str, path: str, body: Any | None = None) -> Any:
        data = None if body is None else json.dumps(body, separators=(",", ":")).encode()
        headers = {
            "X-Task-Protocol-Version": PROTOCOL,
            "X-Task-Client-Version": "deployment-scenario/1",
            "X-Actor-Id": "deployment-scenario",
        }
        if data is not None:
            headers["Content-Type"] = "application/json"
        is_runner_mutation = method != "GET" and path.startswith(("/api/v1/runners", "/api/v1/runs", "/api/v1/work-permits"))
        token = self.runner_token if is_runner_mutation else self.studio_token
        if token:
            headers["Authorization"] = f"Bearer {token}"
        request = urllib.request.Request(self.base_url + path, data=data, headers=headers, method=method)
        try:
            with urllib.request.urlopen(request, timeout=20) as response:
                payload = response.read()
        except urllib.error.HTTPError as error:
            detail = error.read().decode(errors="replace")
            raise ScenarioFailure(f"{method} {path} returned {error.code}: {detail}") from error
        except OSError as error:
            raise ScenarioFailure(f"{method} {path} failed: {error}") from error
        return None if not payload else json.loads(payload)


class Scenario:
    def __init__(self, manifest: dict[str, Any], target: str, level: str, api: Api, output: Path) -> None:
        self.manifest = manifest
        self.target = target
        self.level = level
        self.api = api
        self.output = output
        self.output.mkdir(parents=True, exist_ok=True)
        requested_run_id = os.environ.get("SCENARIO_RUN_ID", "local")
        self.run_suffix = re.sub(r"[^a-z0-9-]", "-", requested_run_id.lower()).strip("-") or "local"
        if target != "remote":
            self.run_suffix = "local"
        self.ids = {
            "workspace": self._id("wsp-deployment-scenario"),
            "project": self._id("prj-deployment-scenario"),
            "runner": self._id("deployment-scenario-runner"),
            "runner_instance": self._id("deployment-scenario-instance"),
            "reviewer": self._id("deployment-scenario-reviewer"),
            "reviewer_instance": self._id("deployment-scenario-review-instance"),
        }
        self.state: dict[str, Any] = {}
        self.work = Path(tempfile.mkdtemp(prefix="deployment-scenario-work-"))
        self.results: list[StepResult] = []

    def _id(self, base: str) -> str:
        return base if self.run_suffix == "local" else f"{base}-{self.run_suffix}"[:96]

    def evidence(self, step: str, name: str, content: str) -> str:
        path = self.output / "evidence" / step / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content, encoding="utf-8")
        return path.relative_to(self.output).as_posix()

    def execute(self) -> None:
        steps = self.manifest["steps"]
        if self.level == "smoke":
            steps = steps[: self.manifest["smokeStepCount"]]
        try:
            for definition in steps:
                started = time.monotonic()
                evidence: list[str] = []
                try:
                    handler = getattr(self, definition["handler"], None)
                    if not callable(handler):
                        raise ScenarioFailure(f"unknown handler {definition['handler']}")
                    evidence = handler()
                    duration = round((time.monotonic() - started) * 1000)
                    self.results.append(StepResult(definition["id"], "passed", duration, evidence))
                    print(f"PASS {definition['id']} ({duration} ms)")
                except Exception as error:  # the report must survive every step failure
                    duration = round((time.monotonic() - started) * 1000)
                    failure = self.evidence(definition["id"], "failure.txt", str(error) + "\n")
                    self.results.append(StepResult(definition["id"], "failed", duration, evidence + [failure], str(error)))
                    raise
        finally:
            if self.target == "remote":
                self.cleanup_remote()
            shutil.rmtree(self.work, ignore_errors=True)

    def bootstrap_principals(self) -> list[str]:
        receipts = []
        for client in ("studio", "runner", "review-runner", "engine", "management"):
            receipt = self.api.request("POST", "/api/v1/protocol/compatibility", {
                "clientKind": client, "clientVersion": "1.0.0", "protocolVersion": 2,
            })
            if receipt.get("supported") is not True:
                raise ScenarioFailure(f"protocol client {client} was rejected")
            receipts.append(receipt)
        return [self.evidence("bootstrap-principals", "receipts.json", pretty(receipts))]

    def register_project(self) -> list[str]:
        fixture = self.manifest["fixture"]
        workspace = self.api.request("POST", "/api/v1/workspaces", {
            "name": fixture["workspace"]["name"], "workspaceId": self.ids["workspace"],
        })
        project = self.api.request("POST", "/api/v1/projects", {
            "workspaceId": workspace["workspaceId"], "name": self._id(fixture["project"]["name"]),
            "taskKeyPrefix": fixture["project"]["prefix"], "projectId": self.ids["project"],
        })
        dossier = self._create_fixture_task(fixture["dossier"], "0-backlog", "Decision gate pending.")
        epic = self._create_fixture_task(
            fixture["epic"], "0-backlog", "Children: " + ", ".join(fixture["epic"]["children"]))
        followup = self._create_fixture_task(fixture["tasks"][1], fixture["tasks"][1]["state"], "Epic: DSR-2")
        self.state.update(workspace=workspace, project=project, dossier=dossier, epic=epic, followup=followup)
        inventory = {"workspace": workspace, "project": project, "seededTaskKeys": [dossier["taskKey"], epic["taskKey"], followup["taskKey"]]}
        return [self.evidence("register-project", "inventory.json", pretty(inventory))]

    def _create_fixture_task(self, fixture: dict[str, Any], state: str, body: str) -> dict[str, Any]:
        suffix = "" if self.run_suffix == "local" else f"-{self.run_suffix}"
        task_id = (fixture["taskId"] + suffix)[:96]
        return self.api.request("POST", f"/api/v1/projects/{self.ids['project']}/tasks", {
            "title": fixture["title"], "body": body, "state": state,
            "taskId": task_id, "taskKey": fixture["taskKey"] + ("" if self.run_suffix == "local" else "-" + self.run_suffix.upper()),
        })

    def register_runner(self) -> list[str]:
        runner = self.api.request("PUT", f"/api/v1/runners/{self.ids['runner']}", {
            "name": "deployment scenario runner", "hostId": self._id("deployment-scenario-host"),
            "instanceId": self.ids["runner_instance"], "runnerVersion": "scenario-fake-cli/1",
            "protocolVersion": 2, "capabilities": ["coding-executor"], "bootstrapMaxParallelism": 1,
        })
        if runner["status"] not in ("active", "online"):
            raise ScenarioFailure(f"runner status was {runner['status']}")
        self.state["runner"] = runner
        return [self.evidence("register-runner", "runner.json", pretty(runner))]

    def create_task(self) -> list[str]:
        task = self._create_fixture_task(self.manifest["fixture"]["tasks"][0], "2-ready", "Epic: DSR-2")
        if task["state"] != "2-ready":
            raise ScenarioFailure("seeded run task is not ready")
        self.state["task"] = task
        return [self.evidence("create-task", "task.json", pretty(task))]

    def claim(self) -> list[str]:
        claim = self.api.request("POST", f"/api/v1/runners/{self.ids['runner']}/claims", {
            "runnerId": self.ids["runner"], "instanceId": self.ids["runner_instance"],
            "requestedTtlSeconds": 120, "availableSlots": 1,
        })
        if claim["status"] != "claimed" or claim["lease"]["fence"] < 1:
            raise ScenarioFailure(f"claim was not granted with a positive fence: {claim}")
        if claim["task"]["taskId"] != self.state["task"]["taskId"]:
            raise ScenarioFailure("runner claimed a task outside the scenario")
        self.state["claim"] = claim
        return [self.evidence("claim", "claim.json", pretty(claim))]

    def run_fake_cli(self) -> list[str]:
        fixture = self.manifest["fixture"]["repository"]
        repository = self.work / "repository"
        repository.mkdir()
        run(["git", "init", "-b", "main"], repository)
        run(["git", "config", "user.name", "Deployment Scenario"], repository)
        run(["git", "config", "user.email", "scenario@example.invalid"], repository)
        base_commit_environment = os.environ.copy()
        base_commit_environment.update({"GIT_AUTHOR_DATE": FIXED_CLOCK, "GIT_COMMITTER_DATE": FIXED_CLOCK})
        (repository / fixture["expectedFile"]).write_text(fixture["passingValue"] + "\n", encoding="utf-8")
        (repository / fixture["resultFile"]).write_text(fixture["failingValue"] + "\n", encoding="utf-8")
        before = compare_fixture(repository, fixture)
        if before:
            raise ScenarioFailure("seeded test unexpectedly passed before the fake CLI")
        run(["git", "add", "."], repository)
        run(["git", "commit", "-m", "test: seed failing deployment fixture"], repository, base_commit_environment)
        base_sha = run(["git", "rev-parse", "HEAD"], repository).strip()
        run(["git", "switch", "-c", "scenario-result"], repository)
        (repository / fixture["resultFile"]).write_text(fixture["passingValue"] + "\n", encoding="utf-8")
        fake_log = "fake-cli: fixed actual.txt\nfake-cli: deterministic output\n[[TASK_DONE]]\n"
        (repository / "scenario.log").write_text(fake_log, encoding="utf-8")
        after = compare_fixture(repository, fixture)
        if not after:
            raise ScenarioFailure("seeded test did not pass after the fake CLI")
        run(["git", "add", "."], repository)
        result_commit_environment = os.environ.copy()
        result_commit_environment.update({
            "GIT_AUTHOR_DATE": "2026-09-06T12:00:01Z",
            "GIT_COMMITTER_DATE": "2026-09-06T12:00:01Z",
        })
        run(["git", "commit", "-m", "fix: make deployment fixture pass"], repository, result_commit_environment)
        result_sha = run(["git", "rev-parse", "HEAD"], repository).strip()
        tree_sha = run(["git", "rev-parse", "HEAD^{tree}"], repository).strip()
        claim = self.state["claim"]
        run_id, lease = claim["run"]["runId"], claim["lease"]
        log_bytes = fake_log.encode()
        log_sha = sha256(log_bytes)
        self.api.request("POST", f"/api/v1/runs/{run_id}/events", {
            "eventId": "scenario-agent-message", "kind": "agent.message",
            "payloadJson": json.dumps({"text": "fake CLI completed", "clock": FIXED_CLOCK}),
            "idempotencyKey": "scenario-event-1", "fence": lease["fence"], "occurredAt": FIXED_CLOCK,
            "runnerId": self.ids["runner"], "instanceId": self.ids["runner_instance"], "leaseId": lease["leaseId"], "sequence": 1,
        })
        self.api.request("POST", f"/api/v1/runs/{run_id}/artifacts", {
            "artifactId": "scenario-fake-cli-log", "name": "scenario/fake-cli.log", "mediaType": "text/plain",
            "contentBase64": base64.b64encode(log_bytes).decode(), "sha256": log_sha,
            "idempotencyKey": "scenario-artifact-1", "fence": lease["fence"],
            "runnerId": self.ids["runner"], "instanceId": self.ids["runner_instance"], "leaseId": lease["leaseId"], "sequence": 2,
        })
        repository_id = "repo_deployment_scenario_v1"
        result_ref = f"refs/heads/agent-studio/results/{run_id}/fence-{lease['fence']}/{result_sha}"
        repository_url = "https://example.invalid/deployment-scenario.git"
        envelope = {
            "repositoryId": repository_id, "sourceRunAttemptId": run_id, "baseSha": base_sha,
            "resultSha": result_sha, "immutableRemoteRef": result_ref, "sourceBundleDigest": None,
            "artifactManifestDigest": log_sha, "submodules": [], "lfsObjects": [], "repositoryUrl": repository_url,
        }
        canonical = dict(envelope)
        # ResultEnvelopeDigest deliberately excludes the informational repository URL.
        canonical["repositoryUrl"] = None
        digest = sha256(json.dumps(canonical, separators=(",", ":")).encode())
        self.api.request("PUT", f"/api/v1/runs/{run_id}/result-handoff", {
            "runnerId": self.ids["runner"], "instanceId": self.ids["runner_instance"], "leaseId": lease["leaseId"],
            "fence": lease["fence"], "sequence": 3, "idempotencyKey": f"scenario-handoff-{run_id}",
            "envelopeDigest": digest, "envelope": envelope,
        })
        completed = self.api.request("POST", f"/api/v1/runs/{run_id}/completion", {
            "runnerId": self.ids["runner"], "instanceId": self.ids["runner_instance"], "leaseId": lease["leaseId"],
            "fence": lease["fence"], "outcome": "success", "summary": "fake CLI produced a passing commit and log",
            "resultEnvelopeDigest": digest, "idempotencyKey": f"scenario-completion-{run_id}", "sequence": 4,
        })
        task = self.api.request("GET", f"/api/v1/projects/{self.ids['project']}/tasks/{self.state['task']['taskId']}")
        if completed["status"] != "success" or task["state"] != "4-auto-review":
            raise ScenarioFailure("successful fake CLI run did not reach Auto Review")
        self.state.update(repository=repository, base_sha=base_sha, result_sha=result_sha, tree_sha=tree_sha,
                          repository_id=repository_id, repository_url=repository_url, result_ref=result_ref,
                          envelope_digest=digest, task=task)
        return [
            self.evidence("run-fake-cli", "test-before.txt", "FAIL: expected green, actual red\n"),
            self.evidence("run-fake-cli", "test-after.txt", "PASS: expected green, actual green\n"),
            self.evidence("run-fake-cli", "fake-cli.log", fake_log),
            self.evidence("run-fake-cli", "result.json", pretty({"baseSha": base_sha, "resultSha": result_sha, "taskState": task["state"]})),
        ]

    def auto_review(self) -> list[str]:
        reviewer = self.ids["reviewer"]
        instance = self.ids["reviewer_instance"]
        self.api.request("PUT", f"/api/v1/runners/{reviewer}", {
            "name": "deployment scenario reviewer", "hostId": self._id("deployment-review-host"),
            "instanceId": instance, "runnerVersion": "scenario-review-cli/1", "protocolVersion": 2,
            "capabilities": ["review-executor", "review:git", "review:semantic"], "bootstrapMaxParallelism": 1,
        })
        run_id = self.state["claim"]["run"]["runId"]
        subject = self.api.request("POST", "/api/v1/reviews/subjects", {
            "taskId": self.state["task"]["taskId"], "sourceRunId": run_id,
            "repositoryId": self.state["repository_id"], "repositoryUrl": self.state["repository_url"],
            "expectedResultSha": self.state["result_sha"], "resultRef": self.state["result_ref"],
            "sourceBundleArtifactId": None, "sourceBundleSha256": None,
            "codingHostId": self._id("deployment-scenario-host"), "reviewPolicyHash": "scenario-policy-v1",
            "plan": {"commands": [{"stepId": "fake-review", "aspect": "deployment", "fileName": "fake-review-cli", "arguments": ["--fixed"], "required": True, "timeoutSeconds": 30, "compareToBaseline": False, "executionKind": "tool"}], "requiredAspects": ["deployment"], "requiresVisualReview": False, "requireDifferentHostFailureDomain": False},
            "idempotencyKey": f"scenario-subject-{run_id}",
        })
        claim = self.api.request("POST", f"/api/v1/runners/{reviewer}/review-claims", {
            "executorId": reviewer, "instanceId": instance, "requestedTtlSeconds": 120, "availableSlots": 1,
        })
        if claim["status"] != "claimed" or claim["subject"]["subjectId"] != subject["subjectId"]:
            raise ScenarioFailure("fake review CLI did not claim the scenario subject")
        lease, attempt = claim["lease"], claim["attempt"]
        workspace = f"/review/{lease['resourceNamespace']}"
        stdout_sha, stderr_sha = sha256(b"review passed\n"), sha256(b"")
        command = {
            "stepId": "fake-review", "aspect": "deployment", "fileName": "fake-review-cli", "arguments": ["--fixed"],
            "expectedResultSha": self.state["result_sha"], "headBefore": self.state["result_sha"], "treeBefore": self.state["tree_sha"],
            "startedAt": FIXED_CLOCK, "finishedAt": FIXED_CLOCK, "exitCode": 0, "signal": None,
            "stdoutSha256": stdout_sha, "stderrSha256": stderr_sha, "phase": "verification", "workspaceRole": "candidate",
            "executionKind": "tool", "executionLocation": "remote", "executorId": reviewer,
            "hostId": lease["hostId"], "attemptId": attempt["attemptId"],
        }
        report = self.api.request("POST", f"/api/v1/reviews/attempts/{attempt['attemptId']}/report", {
            "executorId": reviewer, "instanceId": instance, "leaseId": lease["leaseId"], "fence": lease["fence"],
            "idempotencyKey": f"scenario-report-{attempt['attemptId']}", "outcome": "Pass", "failureClassification": None,
            "summary": "fixed fake review passed", "workspace": {"repositoryId": self.state["repository_id"],
            "expectedResultSha": self.state["result_sha"], "actualHead": self.state["result_sha"], "treeHash": self.state["tree_sha"],
            "dirtyBefore": False, "dirtyAfter": False, "workspaceIdentity": sha256(workspace.encode()), "resourceNamespace": lease["resourceNamespace"]},
            "environment": {"hostId": lease["hostId"], "executorId": reviewer, "instanceId": instance, "osDescription": "scenario",
            "architecture": "deterministic", "runtimeVersion": "1", "toolchain": {"runtime": "scenario;sha256=" + "a" * 64, "git": "git;sha256=" + "b" * 64, "command:fake-review": "fake-review-cli;sha256=" + "c" * 64},
            "isolation": {"workspace": workspace, "cache": workspace + "/cache", "temp": workspace + "/tmp", "ports": f"{lease['portBase']}-{lease['portBase'] + 7}", "containers": lease["resourceNamespace"], "databases": lease["resourceNamespace"], "credentials": "review-read-only"}},
            "commands": [command], "artifacts": [
                {"name": "fake-review.stdout.log", "mediaType": "text/plain", "sha256": stdout_sha, "sizeBytes": 14, "contentBase64": base64.b64encode(b"review passed\n").decode()},
                {"name": "fake-review.stderr.log", "mediaType": "text/plain", "sha256": stderr_sha, "sizeBytes": 0, "contentBase64": ""}],
            "verdicts": [{"aspect": "deployment", "status": "pass", "classification": "Verified", "summary": "deployment passed"}],
        })
        if report["outcome"] != "Pass":
            raise ScenarioFailure(f"fake review was classified as {report['outcome']}")
        cleanup = self.api.request("POST", f"/api/v1/reviews/attempts/{attempt['attemptId']}/cleanup", {
            "executorId": reviewer, "instanceId": instance, "leaseId": lease["leaseId"], "fence": lease["fence"],
            "idempotencyKey": f"scenario-cleanup-{attempt['attemptId']}", "workspaceRemoved": True,
        })
        if cleanup["status"] != "cleaned":
            raise ScenarioFailure("review workspace was not cleaned")
        self.state["review"] = report
        return [self.evidence("auto-review", "review.json", pretty(report))]

    def integrate(self) -> list[str]:
        repository: Path = self.state["repository"]
        run(["git", "switch", "main"], repository)
        run(["git", "merge", "--ff-only", "scenario-result"], repository)
        integrated = run(["git", "rev-parse", "HEAD"], repository).strip()
        if integrated != self.state["result_sha"]:
            raise ScenarioFailure("integrated SHA differs from reviewed result SHA")
        self.state["integrated_sha"] = integrated
        return [self.evidence("integrate", "integration.json", pretty({"strategy": "fast-forward", "integratedSha": integrated}))]

    def complete(self) -> list[str]:
        pending = self.api.request("GET", f"/api/v1/orchestration/runs?projectId={urllib.parse.quote(self.ids['project'])}&status=pending")
        if len(pending) != 1:
            raise ScenarioFailure(f"expected one queued orchestration, found {len(pending)}")
        current = pending[0]
        while current["status"] == "pending":
            stage = current["currentStage"]
            claim = self.api.request("POST", "/api/v1/orchestration/claims", {
                "engineId": self._id("scenario-engine"), "instanceId": self._id("scenario-engine-instance"),
                "supportedStages": [stage], "requestedTtlSeconds": 120,
            })
            lease = claim["lease"]
            action = 3 if stage == 4 else 0
            current = self.api.request("POST", f"/api/v1/orchestration/runs/{current['runId']}/stages/complete", {
                "engineId": self._id("scenario-engine"), "instanceId": self._id("scenario-engine-instance"),
                "leaseId": lease["leaseId"], "fence": lease["fence"], "stage": stage, "action": action,
                "outputJson": json.dumps({"fixedClock": FIXED_CLOCK, "result": "pass"}),
                "idempotencyKey": f"scenario-stage-{current['runId']}-{stage}",
            })
        task = self.api.request("GET", f"/api/v1/projects/{self.ids['project']}/tasks/{self.state['task']['taskId']}")
        if task["state"] != "5-human-review":
            raise ScenarioFailure("orchestration did not reach Human Review")
        task = self.api.request("PUT", f"/api/v1/projects/{self.ids['project']}/tasks/{task['taskId']}", {
            "title": None, "body": None, "state": "6-completed", "expectedVersion": task["version"],
        })
        if task["state"] != "6-completed":
            raise ScenarioFailure("scenario task did not complete")
        self.state["task"] = task
        return [self.evidence("complete", "completion.json", pretty({"orchestration": current, "task": task}))]

    def orchestrator_chat(self) -> list[str]:
        project_name = urllib.parse.quote(self.state["project"]["name"], safe="")
        user_turn = self._id("scenario-user-turn")
        self.api.request("POST", f"/api/v1/orchestrator-contexts/projects/{project_name}/turns", {"turn": {
            "turnId": user_turn, "createdAt": FIXED_CLOCK, "role": "user", "body": "What deployment evidence was used?"}})
        source_sha = sha256(b"deployment-scenario-context-v1")
        self.api.request("POST", f"/api/v1/orchestrator-contexts/projects/{project_name}/turns", {"turn": {
            "turnId": self._id("scenario-orchestrator-turn"), "createdAt": FIXED_CLOCK, "role": "orchestrator",
            "body": "The fixed fixture and reviewed result commit were used.", "model": "fake-orchestrator-v1",
            "tokenUsage": {"model": "fake-orchestrator-v1", "inputTokens": 10, "outputTokens": 8, "cacheReadTokens": 0, "cacheCreationTokens": 0},
            "receipt": {"receiptId": self._id("scenario-context-receipt"), "userTurnId": user_turn, "contextKey": "project:canonicalized",
            "capturedAt": FIXED_CLOCK, "budget": {"automaticSoftCapTokens": 100, "automaticHardCapTokens": 200, "totalHardCapTokens": 300, "estimatedIncludedTokens": 10},
            "sources": [{"sourceId": "scenario/fixture", "kind": "project-base", "revision": "v1", "sha256": source_sha,
            "freshness": "current", "includedCharacters": 40, "estimatedTokens": 10, "status": "included"}]}}})
        transcript = self.api.request("GET", f"/api/v1/orchestrator-contexts/projects/{project_name}/turns")
        replies = [turn for turn in transcript["turns"] if turn.get("receipt")]
        if len(replies) != 1 or replies[0]["receipt"]["userTurnId"] != user_turn:
            raise ScenarioFailure("orchestrator context receipt did not round-trip")
        return [self.evidence("orchestrator-chat", "transcript.json", pretty(transcript))]

    def dossier_decision(self) -> list[str]:
        dossier = self.state["dossier"]
        decision = self.manifest["fixture"]["dossier"]["decision"]
        updated = self.api.request("PUT", f"/api/v1/projects/{self.ids['project']}/tasks/{dossier['taskId']}", {
            "title": None, "body": f"Decision: {decision}\nRecorded at: {FIXED_CLOCK}",
            "state": "6-completed", "expectedVersion": dossier["version"],
        })
        if updated["state"] != "6-completed" or f"Decision: {decision}" not in updated["body"]:
            raise ScenarioFailure("dossier decision was not persisted")
        self.state["dossier"] = updated
        return [self.evidence("dossier-decision", "decision.json", pretty(updated))]

    def backup(self) -> list[str]:
        inventory = self.inventory()
        self.state["inventory_before"] = inventory
        self.state["inventory_hash_before"] = sha256(json.dumps(inventory, sort_keys=True, separators=(",", ":")).encode())
        backup = self.api.request("POST", "/api/v1/management/backups", {"name": "deployment-scenario"})
        if len(backup.get("sha256", "")) != 64 or backup.get("sizeBytes", 0) <= 0:
            raise ScenarioFailure("backup did not return a typed SHA-256 and size")
        self.state["backup"] = backup
        return [self.evidence("backup", "backup.json", pretty(backup))]

    def restore(self) -> list[str]:
        verify_only = self.target == "remote"
        if not verify_only:
            self.api.request("PUT", "/api/v1/management/mode", {"mode": 3, "reason": "deployment scenario restore"})
        restored = self.api.request("POST", "/api/v1/management/restore", {
            "backupId": self.state["backup"]["backupId"], "verifyOnly": verify_only,
        })
        if restored["verified"] is not True or restored["restored"] is verify_only:
            raise ScenarioFailure("backup restore/verification result did not match target policy")
        self.state["restore"] = restored
        return [self.evidence("restore", "restore.json", pretty(restored))]

    def inventory_hash_equality(self) -> list[str]:
        inventory = self.inventory()
        digest = sha256(json.dumps(inventory, sort_keys=True, separators=(",", ":")).encode())
        if digest != self.state["inventory_hash_before"]:
            raise ScenarioFailure(f"inventory hash changed: {self.state['inventory_hash_before']} != {digest}")
        proof = {"before": self.state["inventory_hash_before"], "after": digest, "equal": True}
        return [self.evidence("inventory-hash-equality", "inventory-hashes.json", pretty(proof))]

    def inventory(self) -> dict[str, Any]:
        projects = self.api.request("GET", f"/api/v1/projects?workspaceId={urllib.parse.quote(self.ids['workspace'])}")
        tasks = self.api.request("GET", f"/api/v1/projects/{self.ids['project']}/tasks")
        return {
            "projects": sorted(({"projectId": p["projectId"], "name": p["name"], "taskKeyPrefix": p["taskKeyPrefix"]} for p in projects), key=lambda p: p["projectId"]),
            "tasks": sorted(({"taskId": t["taskId"], "taskKey": t["taskKey"], "title": t["title"], "state": t["state"], "body": t.get("body")} for t in tasks), key=lambda t: t["taskId"]),
        }

    def cleanup_remote(self) -> None:
        if "project" not in self.state:
            return
        try:
            tasks = self.api.request("GET", f"/api/v1/projects/{self.ids['project']}/tasks")
            cleaned = []
            for task in tasks:
                if task["state"] == "3-progress":
                    continue
                updated = self.api.request("PUT", f"/api/v1/projects/{self.ids['project']}/tasks/{task['taskId']}", {
                    "title": None, "body": None, "state": "7-archive", "expectedVersion": task["version"],
                })
                cleaned.append(updated["taskKey"])
            self.evidence("cleanup", "remote-cleanup.json", pretty({"archivedTaskKeys": sorted(cleaned)}))
        except Exception as error:
            self.evidence("cleanup", "remote-cleanup-failure.txt", str(error) + "\n")


def compare_fixture(repository: Path, fixture: dict[str, Any]) -> bool:
    expected = (repository / fixture["expectedFile"]).read_text(encoding="utf-8")
    actual = (repository / fixture["resultFile"]).read_text(encoding="utf-8")
    return expected == actual


def run(command: list[str], cwd: Path, environment: dict[str, str] | None = None) -> str:
    completed = subprocess.run(
        command, cwd=cwd, env=environment, text=True, capture_output=True, timeout=30, check=False)
    if completed.returncode != 0:
        raise ScenarioFailure(f"{' '.join(command)} failed ({completed.returncode}): {completed.stderr.strip()}")
    return completed.stdout


def sha256(content: bytes) -> str:
    return hashlib.sha256(content).hexdigest()


def pretty(value: Any) -> str:
    return json.dumps(value, indent=2, sort_keys=True) + "\n"


def validate_manifest(manifest: dict[str, Any]) -> None:
    required = ("id", "seed", "clock", "smokeStepCount", "fixture", "steps")
    missing = [key for key in required if key not in manifest]
    if missing:
        raise ScenarioFailure("scenario definition is missing: " + ", ".join(missing))
    steps = manifest["steps"]
    if not isinstance(steps, list) or not steps:
        raise ScenarioFailure("scenario definition requires an ordered non-empty steps array")
    if not 0 < manifest["smokeStepCount"] <= len(steps):
        raise ScenarioFailure("smokeStepCount is outside the ordered step range")
    step_ids: set[str] = set()
    assertion_types: set[str] = set()
    for step in steps:
        if step.get("id") in step_ids:
            raise ScenarioFailure(f"duplicate scenario step id: {step.get('id')}")
        step_ids.add(step.get("id"))
        if not re.fullmatch(r"[a-z0-9-]+", step.get("id", "")):
            raise ScenarioFailure(f"invalid scenario step id: {step.get('id')}")
        if not re.fullmatch(r"[a-z0-9_]+", step.get("handler", "")):
            raise ScenarioFailure(f"invalid typed handler for {step['id']}")
        if not step.get("assertions"):
            raise ScenarioFailure(f"step {step['id']} has no typed assertions")
        for assertion in step["assertions"]:
            assertion_type = assertion.get("type", "")
            if not re.fullmatch(r"[a-z0-9_]+", assertion_type):
                raise ScenarioFailure(f"step {step['id']} has an invalid typed assertion")
            if assertion_type in assertion_types:
                raise ScenarioFailure(f"typed assertion is not uniquely owned: {assertion_type}")
            assertion_types.add(assertion_type)


def free_port() -> int:
    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        return int(probe.getsockname()[1])


def wait_ready(url: str, process: subprocess.Popen[str] | None = None) -> None:
    deadline = time.monotonic() + 30
    while time.monotonic() < deadline:
        if process is not None and process.poll() is not None:
            output = process.stdout.read() if process.stdout else ""
            raise ScenarioFailure(f"Task Server exited before readiness ({process.returncode}):\n{output}")
        try:
            with urllib.request.urlopen(url.rstrip("/") + "/readyz", timeout=1) as response:
                if response.status == 200:
                    return
        except OSError:
            time.sleep(0.1)
    raise ScenarioFailure(f"Task Server did not become ready at {url}")


def write_reports(output: Path, scenario_id: str, target: str, level: str, results: list[StepResult], elapsed_ms: int) -> None:
    failures = sum(result.status != "passed" for result in results)
    suite = ET.Element("testsuite", name=scenario_id, tests=str(len(results)), failures=str(failures), time=f"{elapsed_ms / 1000:.3f}")
    for result in results:
        case = ET.SubElement(suite, "testcase", classname=f"deployment.{target}.{level}", name=result.step_id, time=f"{result.duration_ms / 1000:.3f}")
        ET.SubElement(case, "system-out").text = "\n".join(result.evidence)
        if result.status != "passed":
            ET.SubElement(case, "failure", message=result.message).text = result.message
    tree = ET.ElementTree(suite)
    ET.indent(tree, space="  ")
    tree.write(output / "scenario.junit.xml", encoding="utf-8", xml_declaration=True)
    rows = []
    for result in results:
        links = "<br>".join(f"[{html.escape(Path(item).name)}]({item})" for item in result.evidence) or "-"
        rows.append(f"| {result.step_id} | {result.status} | {result.duration_ms} | {links} |")
    report = "\n".join([
        "# Deployment scenario report", "", f"- Scenario: `{scenario_id}`", f"- Target: `{target}`",
        f"- Level: `{level}`", f"- Status: `{'passed' if failures == 0 else 'failed'}`", f"- Duration: `{elapsed_ms} ms`",
        f"- Fixed fixture clock: `{FIXED_CLOCK}`", "", "| Step | Status | Duration (ms) | Evidence |",
        "|---|---:|---:|---|", *rows, "",
    ])
    (output / "scenario-report.md").write_text(report, encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--target", choices=("inproc", "compose", "remote"), required=True)
    parser.add_argument("--level", choices=("smoke", "full"), required=True)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--server-url")
    parser.add_argument("--task-server-dll", type=Path)
    args = parser.parse_args()
    manifest = json.loads(args.manifest.read_text(encoding="utf-8"))
    validate_manifest(manifest)
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    process: subprocess.Popen[str] | None = None
    local_store: tempfile.TemporaryDirectory[str] | None = None
    started = time.monotonic()
    scenario: Scenario | None = None
    try:
        server_url = args.server_url
        if args.target == "inproc":
            if args.task_server_dll is None or not args.task_server_dll.exists():
                raise ScenarioFailure("--task-server-dll must identify a built Task Server for inproc")
            local_store = tempfile.TemporaryDirectory(prefix="deployment-scenario-store-")
            port = free_port()
            server_url = f"http://127.0.0.1:{port}"
            environment = os.environ.copy()
            environment.update({"LISTEN_URL": server_url, "STORE_PATH": local_store.name, "BACKUP_PATH": str(Path(local_store.name) / "backups"), "AUTH": "none"})
            process = subprocess.Popen(["dotnet", str(args.task_server_dll.resolve())], cwd=args.task_server_dll.parent,
                                       env=environment, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
        if not server_url:
            raise ScenarioFailure("a target server URL is required")
        wait_ready(server_url, process)
        shared_token = os.environ.get("SCENARIO_AUTH_TOKEN")
        scenario = Scenario(manifest, args.target, args.level, Api(
            server_url,
            os.environ.get("SCENARIO_STUDIO_TOKEN") or shared_token,
            os.environ.get("SCENARIO_RUNNER_TOKEN") or shared_token), output)
        (output / "scenario-definition.json").write_text(pretty(manifest), encoding="utf-8")
        scenario.execute()
        return_code = 0
    except Exception as error:
        print(f"FAIL deployment scenario: {error}", file=sys.stderr)
        return_code = 1
        if scenario is None:
            scenario = Scenario(manifest, args.target, args.level, Api(args.server_url or "http://127.0.0.1", None), output)
            scenario.results.append(StepResult("startup", "failed", 0, [], str(error)))
    finally:
        if process is not None:
            process.terminate()
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                process.kill()
        if local_store is not None:
            local_store.cleanup()
        elapsed = round((time.monotonic() - started) * 1000)
        if scenario is not None:
            write_reports(output, manifest["id"], args.target, args.level, scenario.results, elapsed)
    return return_code


if __name__ == "__main__":
    raise SystemExit(main())
