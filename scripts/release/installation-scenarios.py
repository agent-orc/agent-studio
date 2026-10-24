#!/usr/bin/env python3
"""Run candidate installation probes and write credential-free evidence.

Each probe owns its temporary installation. This driver records only command
identities and exit status, never process output or environment values.
"""

import argparse
import datetime as dt
import hashlib
import html
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import time


ROOT = Path(__file__).resolve().parents[2]
SCENARIOS = {
    "linux-native": "Install and join a native Linux host from candidate assets",
    "linux-docker": "Install, uninstall, and reinstall the one-box Docker stack",
    "linux-vm": "Rehearse the one-box Compose story in a disposable VM",
    "coding-review": "Run a disposable fake-CLI coding and review journey",
    "upgrade": "Upgrade a retained Compose store from the previous release",
    "windows-native": "Install and reinstall Windows native services",
    "windows-linux-host": "Attach Windows Studio to a Linux Task Server",
    "simulated-user": "Docs-only simulated first-time user",
    "broken-prerequisites": "Clear guidance for broken first-use prerequisites",
}
OPTIONAL_SCENARIOS = {"linux-vm", "windows-linux-host", "simulated-user", "broken-prerequisites", "upgrade"}
# Remove an id here when its installed first-use journey becomes a required check.
DEFERRED_STEP_IDS = {
    "linux-native-installed-card",
    "linux-docker-installed-first-use",
    "coding-review-installed-integration",
    "windows-native-local-studio-runner",
}


def pending_step(step_id: str, label: str, reason: str) -> dict:
    return {"id": step_id, "step": label,
            "status": "deferred" if step_id in DEFERRED_STEP_IDS else "failed",
            "reason": reason, "durationSeconds": 0}


def verify_candidate(directory: Path, windows: bool) -> dict:
    sums = directory / "SHA256SUMS"
    setup = "agent-studio-setup.exe" if windows else "agent-studio-setup"
    if not sums.is_file() or not (directory / setup).is_file():
        raise ValueError(f"candidate directory needs SHA256SUMS and {setup}")
    found = {}
    for line in sums.read_text(encoding="ascii").splitlines():
        match = re.fullmatch(r"([a-fA-F0-9]{64})  ([^/\\]+)", line)
        if not match:
            raise ValueError("SHA256SUMS contains an invalid entry")
        digest, name = match.groups()
        file_path = directory / name
        if not file_path.is_file():
            raise ValueError(f"candidate asset is missing: {name}")
        hasher = hashlib.sha256()
        with file_path.open("rb") as asset:
            for chunk in iter(lambda: asset.read(1024 * 1024), b""):
                hasher.update(chunk)
        if hasher.hexdigest() != digest.lower():
            raise ValueError(f"candidate asset checksum failed: {name}")
        found[name] = digest.lower()
    if setup not in found:
        raise ValueError(f"SHA256SUMS does not cover {setup}")
    if not windows and not any(name.startswith("agent-orchestrator-") and name.endswith("-linux-x64.tar.gz") for name in found):
        raise ValueError("candidate Linux authority archive is missing")
    return {"assetCount": len(found), "setup": setup, "setupSha256": found[setup]}


def run(label: str, command: list[str], env: dict[str, str], timeout: int) -> dict:
    started = time.monotonic()
    try:
        with open(os.devnull, "w", encoding="utf-8") as discard:
            result = subprocess.run(command, cwd=ROOT, env=env, stdout=discard,
                                    stderr=discard, timeout=timeout, check=False)
        step = {"step": label, "status": "passed" if result.returncode == 0 else "failed",
                "exitCode": result.returncode, "durationSeconds": round(time.monotonic() - started, 2)}
        if result.returncode != 0:
            step["reason"] = f"probe exited with code {result.returncode}"
        return step
    except (OSError, subprocess.TimeoutExpired) as error:
        return {"step": label, "status": "failed",
                "reason": "timed out" if isinstance(error, subprocess.TimeoutExpired) else "command unavailable",
                "durationSeconds": round(time.monotonic() - started, 2)}


def check_missing_git(setup: Path) -> dict:
    started = time.monotonic()
    with tempfile.TemporaryDirectory(prefix="agent-studio-no-git-") as empty_path:
        environment = os.environ.copy()
        environment["PATH"] = empty_path
        try:
            result = subprocess.run([str(setup), "preflight", "--mode", "studio", "--target", "native",
                                     "--install-dir", empty_path], env=environment, cwd=empty_path,
                                    capture_output=True, text=True, timeout=30, check=False)
            output = result.stdout + result.stderr
            passed = result.returncode == 1 and "Install Git" in output \
                and "Git" in output and "stack trace" not in output.lower()
        except (OSError, subprocess.TimeoutExpired):
            passed = False
    return {"step": "Missing Git gives recovery guidance", "status": "passed" if passed else "failed",
            "durationSeconds": round(time.monotonic() - started, 2),
            **({} if passed else {"reason": "expected Git recovery guidance was absent"})}


def write_report(report_dir: Path, scenario: str, candidate: Path, steps: list[dict]) -> bool:
    report_dir.mkdir(parents=True, exist_ok=True)
    known_statuses = {"passed", "failed", "deferred", "skipped", "not_applicable"}
    failed = not steps or any(
        step["status"] not in known_statuses or step["status"] == "failed"
        or (step["status"] == "deferred" and step.get("id") not in DEFERRED_STEP_IDS)
        for step in steps
    )
    statuses = {step["status"] for step in steps}
    status = "failed" if failed else next(
        (value for value in ("skipped", "deferred", "not_applicable") if value in statuses), "passed")
    gate_passed = not failed and ("skipped" not in statuses or scenario in OPTIONAL_SCENARIOS)
    report = {
        "schemaVersion": 1,
        "scenario": scenario,
        "description": SCENARIOS[scenario],
        "candidate": candidate.name,
        "status": status,
        "timestampUtc": dt.datetime.now(dt.timezone.utc).isoformat(),
        "steps": steps,
    }
    (report_dir / f"{scenario}.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    rows = "\n".join(
        "<tr><th scope='row'>{}</th><td>{}</td><td>{}</td><td>{}</td></tr>".format(
            html.escape(step["step"]), html.escape(step["status"]), step["durationSeconds"],
            html.escape(step.get("reason", "")))
        for step in steps
    )
    friction_html = ""
    friction_path = report_dir / "simulated-user-friction.json"
    if scenario == "simulated-user" and friction_path.is_file():
        friction = json.loads(friction_path.read_text(encoding="utf-8"))
        report["frictionReport"] = friction_path.name
        (report_dir / f"{scenario}.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
        issues = "".join(
            "<tr><td>{}</td><td>{}</td><td>{}</td></tr>".format(
                html.escape(item.get("location", "")), html.escape(item.get("guess", "")),
                html.escape(item.get("evidence", "")))
            for item in friction.get("friction", [])
        )
        friction_html = "<h2>Simulated user friction</h2><table><thead><tr><th>Location</th>" \
            "<th>Guess or blocker</th><th>Evidence</th></tr></thead><tbody>" + issues + "</tbody></table>"
    page = f"""<!doctype html><html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1"><title>{html.escape(scenario)} evidence</title>
<style>body{{font:16px system-ui;margin:2rem auto;max-width:60rem;padding:0 1rem;color:#182033;background:#f7f8fb}}
table{{border-collapse:collapse;width:100%;background:white}}th,td{{border:1px solid #ccd3de;padding:.7rem;text-align:left}}
@media(prefers-color-scheme:dark){{body{{color:#eef0f5;background:#15171c}}table{{background:#1d2027}}th,td{{border-color:#414855}}}}</style></head>
<body><h1>{html.escape(SCENARIOS[scenario])}</h1><p>Candidate: {html.escape(candidate.name)}. Result: <strong>{report['status']}</strong>.</p>
<table><thead><tr><th>Step</th><th>Status</th><th>Seconds</th><th>Reason</th></tr></thead><tbody>{rows}</tbody></table>{friction_html}</body></html>"""
    (report_dir / f"{scenario}.html").write_text(page, encoding="utf-8")
    summary_path = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary_path:
        deferred = [step["step"] for step in steps if step["status"] == "deferred"]
        with open(summary_path, "a", encoding="utf-8") as summary:
            summary.write(f"- **{scenario}: {status}** ({'gate passed' if gate_passed else 'gate failed'})")
            if deferred:
                summary.write(". Deferred: " + ", ".join(deferred))
            summary.write(".\n")
    return gate_passed


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--scenario", required=True, choices=SCENARIOS)
    parser.add_argument("--candidate-dir", required=True, type=Path)
    parser.add_argument("--report-dir", type=Path, default=Path(os.environ.get("JOB_RESULTS_DIR", "artifacts/installation-scenarios")))
    parser.add_argument("--previous-version", default="")
    parser.add_argument("--server-url", default="")
    parser.add_argument("--token-file", type=Path)
    parser.add_argument("--operator-profile", action="store_true",
                        help="Use the host's existing Codex login for the simulated user")
    args = parser.parse_args()
    candidate = args.candidate_dir.resolve()
    report_dir = args.report_dir.resolve()
    steps = []
    try:
        detail = verify_candidate(candidate, args.scenario.startswith("windows-"))
        steps.append({"step": f"Verify {detail['assetCount']} candidate assets", "status": "passed",
                      "setupSha256": detail["setupSha256"], "durationSeconds": 0})
    except (ValueError, OSError) as error:
        steps.append({"step": "Verify candidate assets", "status": "failed", "reason": str(error), "durationSeconds": 0})
        write_report(report_dir, args.scenario, candidate, steps)
        print(f"Candidate verification failed: {error}", file=sys.stderr)
        return 1

    environment = os.environ.copy()
    environment.pop("INSTALL_SCENARIO_DEPLOY_KEY", None)
    environment.pop("INSTALL_SCENARIO_KNOWN_HOSTS", None)
    environment.pop("SCENARIO_GIT_REMOTE", None)
    if args.scenario == "linux-native":
        optional = (environment.get("AGENT_STUDIO_SCENARIO_DEPLOY_KEY", ""),
                    environment.get("AGENT_STUDIO_SCENARIO_GIT_REMOTE", ""),
                    environment.get("AGENT_STUDIO_SCENARIO_KNOWN_HOSTS", ""))
        if all(optional):
            environment["INSTALL_SCENARIO_DEPLOY_KEY"] = optional[0]
            environment["SCENARIO_GIT_REMOTE"] = optional[1]
            environment["INSTALL_SCENARIO_KNOWN_HOSTS"] = optional[2]
        environment["SETUP_E2E_RELEASE_DIR"] = str(candidate)
        steps.append(run("Native install and host join", ["bash", str(ROOT / "scripts/release/setup-e2e-probe.sh"),
                           str(report_dir / "linux-native-probe.md")], environment, 900))
        if steps[-1]["status"] == "passed":
            steps.append(pending_step("linux-native-installed-card", "Installed candidate card through integration",
                                      "Task Server has no native integration transition; coding fixture uses a separate source-built topology"))
    elif args.scenario == "linux-docker":
        steps.append(run("Docker install and retained-data reinstall",
                         ["bash", str(ROOT / "scripts/release/installation-docker-probe.sh"), str(candidate)], environment, 1200))
        if steps[-1]["status"] == "passed":
            steps.append(pending_step("linux-docker-installed-first-use",
                                      "Owner, project and card through integration on installed Compose stack",
                                      "The Docker probe currently checks an API workspace and retained data only"))
    elif args.scenario == "linux-vm":
        if not args.operator_profile:
            steps.append({"step": "Disposable VM Compose onboarding", "status": "skipped",
                          "reason": "Requires the operator KVM host profile", "durationSeconds": 0})
        else:
            environment["COMPOSE_SMOKE_VM_ARTIFACTS"] = str(report_dir / "linux-vm-detail")
            steps.append(run("Disposable VM Compose onboarding",
                             ["bash", str(ROOT / "scripts/compose-smoke-vm-test.sh")], environment, 4200))
    elif args.scenario == "coding-review":
        steps.append(run("Fake CLI coding and review",
                         ["bash", str(ROOT / "scripts/scenario.sh"), "--target", "compose", "--level", "full",
                          "--report-dir", str(report_dir / "coding-review-detail")], environment, 1800))
        if steps[-1]["status"] == "passed":
            steps.append(pending_step("coding-review-installed-integration", "Integration on installed candidate",
                                      "Source-built fixture stops at human review and is separate from installed candidate"))
    elif args.scenario == "upgrade":
        if args.previous_version == "none":
            steps.append({"step": "Previous release", "status": "not_applicable",
                          "reason": "No earlier GitHub Release exists", "durationSeconds": 0})
        elif not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", args.previous_version):
            steps.append({"step": "Previous release version", "status": "failed", "reason": "--previous-version is required", "durationSeconds": 0})
        else:
            environment["PREVIOUS_VERSION"] = args.previous_version
            try:
                with open(os.devnull, "w", encoding="utf-8") as discard:
                    version = subprocess.check_output([str(candidate / "agent-studio-setup"), "--version"],
                                                      text=True, stderr=discard, timeout=10).split()[-1]
                if not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", version):
                    raise ValueError("candidate version is invalid")
                environment["CURRENT_VERSION"] = version
                steps.append(run("Retained Compose store upgrade", ["bash", str(ROOT / "scripts/compose-upgrade-test.sh")], environment, 1800))
            except (OSError, subprocess.SubprocessError, IndexError, ValueError):
                steps.append({"step": "Candidate version", "status": "failed", "reason": "setup version unavailable", "durationSeconds": 0})
    elif args.scenario == "windows-native":
        steps.append(run("Windows service install and retained-data reinstall",
                         ["pwsh", "-NoProfile", "-File", str(ROOT / "scripts/release/installation-windows-probe.ps1"),
                          "-CandidateDir", str(candidate)], environment, 1200))
    elif args.scenario == "windows-linux-host":
        if not args.server_url or not args.token_file or not args.token_file.is_file():
            steps.append({"step": "Remote authority inputs", "status": "skipped",
                          "reason": "Operator KORI profile needs --server-url and --token-file", "durationSeconds": 0})
        else:
            steps.append(run("Windows connector to Linux authority",
                             ["pwsh", "-NoProfile", "-File", str(ROOT / "scripts/release/installation-windows-connector-probe.ps1"),
                              "-CandidateDir", str(candidate), "-ServerUrl", args.server_url,
                              "-TokenFile", str(args.token_file.resolve())], environment, 1200))
    elif args.scenario == "simulated-user":
        if not args.operator_profile and not environment.get("OPENAI_API_KEY"):
            steps.append({"step": "Docs-only AI installation user", "status": "skipped",
                          "reason": "AGENT_STUDIO_SIMULATED_USER_API_KEY is not configured", "durationSeconds": 0})
        else:
            simulated_command = [sys.executable, str(ROOT / "scripts/release/simulated-install-user.py"),
                              "--release-url", candidate.as_uri(),
                              "--candidate-dir", str(candidate),
                              "--report", str(report_dir / "simulated-user-friction.json")]
            if args.operator_profile:
                simulated_command.append("--operator")
            steps.append(run("Docs-only AI installation user",
                             simulated_command, environment, 1000))
    elif args.scenario == "broken-prerequisites":
        steps.append(check_missing_git(candidate / "agent-studio-setup"))
        for label, reason in (
            ("Port in use", "Port occupancy is not yet a setup preflight check"),
            ("Provider login", "Requires a join-host fixture with a valid owner-issued token"),
            ("GitHub access", "Local scratch remote is the default; optional GitHub key path needs operator inputs"),
        ):
            steps.append({"step": label, "status": "skipped", "reason": reason, "durationSeconds": 0})
    passed = write_report(report_dir, args.scenario, candidate, steps)
    outcome = json.loads((report_dir / f"{args.scenario}.json").read_text(encoding="utf-8"))["status"]
    print(f"{args.scenario}: {outcome}; evidence: {report_dir}")
    return 0 if passed else 1


if __name__ == "__main__":
    raise SystemExit(main())
