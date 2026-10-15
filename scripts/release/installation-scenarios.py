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
}


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


def write_report(report_dir: Path, scenario: str, candidate: Path, steps: list[dict]) -> bool:
    report_dir.mkdir(parents=True, exist_ok=True)
    not_applicable = bool(steps) and all(step["status"] in ("passed", "not_applicable") for step in steps) \
        and any(step["status"] == "not_applicable" for step in steps)
    passed = bool(steps) and all(step["status"] == "passed" for step in steps)
    status = "not_applicable" if not_applicable else "passed" if passed else "failed"
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
    page = f"""<!doctype html><html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1"><title>{html.escape(scenario)} evidence</title>
<style>body{{font:16px system-ui;margin:2rem auto;max-width:60rem;padding:0 1rem;color:#182033;background:#f7f8fb}}
table{{border-collapse:collapse;width:100%;background:white}}th,td{{border:1px solid #ccd3de;padding:.7rem;text-align:left}}
@media(prefers-color-scheme:dark){{body{{color:#eef0f5;background:#15171c}}table{{background:#1d2027}}th,td{{border-color:#414855}}}}</style></head>
<body><h1>{html.escape(SCENARIOS[scenario])}</h1><p>Candidate: {html.escape(candidate.name)}. Result: <strong>{report['status']}</strong>.</p>
<table><thead><tr><th>Step</th><th>Status</th><th>Seconds</th><th>Reason</th></tr></thead><tbody>{rows}</tbody></table></body></html>"""
    (report_dir / f"{scenario}.html").write_text(page, encoding="utf-8")
    return passed or not_applicable


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--scenario", required=True, choices=SCENARIOS)
    parser.add_argument("--candidate-dir", required=True, type=Path)
    parser.add_argument("--report-dir", type=Path, default=Path(os.environ.get("JOB_RESULTS_DIR", "artifacts/installation-scenarios")))
    parser.add_argument("--previous-version", default="")
    parser.add_argument("--server-url", default="")
    parser.add_argument("--token-file", type=Path)
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
    if args.scenario != "linux-native":
        environment.pop("INSTALL_SCENARIO_DEPLOY_KEY", None)
        environment.pop("INSTALL_SCENARIO_KNOWN_HOSTS", None)
    if args.scenario == "linux-native":
        environment["SETUP_E2E_RELEASE_DIR"] = str(candidate)
        steps.append(run("Native install and host join", ["bash", str(ROOT / "scripts/release/setup-e2e-probe.sh"),
                           str(report_dir / "linux-native-probe.md")], environment, 900))
    elif args.scenario == "linux-docker":
        steps.append(run("Docker install and retained-data reinstall",
                         ["bash", str(ROOT / "scripts/release/installation-docker-probe.sh"), str(candidate)], environment, 1200))
    elif args.scenario == "linux-vm":
        environment["COMPOSE_SMOKE_VM_ARTIFACTS"] = str(report_dir / "linux-vm-detail")
        steps.append(run("Disposable VM Compose onboarding",
                         ["bash", str(ROOT / "scripts/compose-smoke-vm-test.sh")], environment, 4200))
    elif args.scenario == "coding-review":
        steps.append(run("Fake CLI coding and review",
                         ["bash", str(ROOT / "scripts/scenario.sh"), "--target", "compose", "--level", "full",
                          "--report-dir", str(report_dir / "coding-review-detail")], environment, 1800))
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
            steps.append({"step": "Remote authority inputs", "status": "failed",
                          "reason": "--server-url and --token-file are required", "durationSeconds": 0})
        else:
            steps.append(run("Windows connector to Linux authority",
                             ["pwsh", "-NoProfile", "-File", str(ROOT / "scripts/release/installation-windows-connector-probe.ps1"),
                              "-CandidateDir", str(candidate), "-ServerUrl", args.server_url,
                              "-TokenFile", str(args.token_file.resolve())], environment, 1200))
    passed = write_report(report_dir, args.scenario, candidate, steps)
    outcome = "not applicable" if steps[-1]["status"] == "not_applicable" else "passed" if passed else "failed"
    print(f"{args.scenario}: {outcome}; evidence: {report_dir}")
    return 0 if passed else 1


if __name__ == "__main__":
    raise SystemExit(main())
