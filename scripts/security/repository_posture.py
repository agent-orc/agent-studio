#!/usr/bin/env python3
"""Deterministic repository posture report and GitHub ruleset drift check."""

import argparse
import json
import pathlib
import re
import ssl
import subprocess
import sys
import urllib.error
import urllib.request


ACTION = re.compile(r"^\s*(?:-\s*)?uses:\s*([^\s#]+)", re.M)
IMAGE = re.compile(r"^\s*(?:FROM|image:)\s+([^\s#]+)", re.M | re.I)
DIGEST = re.compile(r"@sha256:[a-f0-9]{64}$")
SHA = re.compile(r"@[a-f0-9]{40}$")
REQUIRED_HEADERS = (
    "strict-transport-security", "content-security-policy",
    "x-content-type-options", "cross-origin-opener-policy", "permissions-policy",
)


def finding(code, path, detail):
    return {"code": code, "path": str(path), "detail": detail}


def scan_checkout(root):
    findings = []
    for path in sorted((root / ".github" / "workflows").glob("*.y*ml")):
        content = path.read_text(encoding="utf-8")
        for match in ACTION.finditer(content):
            ref = match.group(1)
            if ref.startswith("./") or ref.startswith("docker://"):
                if ref.startswith("docker://") and not DIGEST.search(ref):
                    findings.append(finding("unpinned-action", path.relative_to(root), ref))
            elif not SHA.search(ref):
                findings.append(finding("unpinned-action", path.relative_to(root), ref))
        for match in IMAGE.finditer(content):
            ref = match.group(1)
            if ref.startswith(("${{", "[")):
                continue  # Matrix values are inspected below.
            if not DIGEST.search(ref):
                findings.append(finding("unpinned-image", path.relative_to(root), ref))
        for matrix in re.findall(r"^\s*image:\s*\[([^\]]+)\]", content, re.M):
            for value in re.findall(r"['\"]([^'\"]+)['\"]", matrix):
                if not DIGEST.search(value):
                    findings.append(finding("unpinned-image", path.relative_to(root), value))
    for path in sorted(root.rglob("Dockerfile")) + sorted(root.rglob("*.Dockerfile")):
        if any(part in {"node_modules", "obj", "bin", ".git"} for part in path.parts):
            continue
        for match in re.finditer(r"^FROM\s+(?:--platform=\S+\s+)?(\S+)", path.read_text(encoding="utf-8"), re.M | re.I):
            ref = match.group(1)
            if not DIGEST.search(ref):
                findings.append(finding("unpinned-image", path.relative_to(root), ref))
    return findings


def expected_rulesets(platform_app_id, checks):
    bypass = [{"actor_id": platform_app_id, "actor_type": "Integration", "bypass_mode": "always"}]
    result = []
    for name, ref_type, patterns in (
        ("studio-branch-integrity", "branch", ["refs/heads/develop", "refs/heads/main"]),
        ("studio-release-tag-integrity", "tag", ["refs/tags/v*"]),
    ):
        result.append({"name": name, "target": ref_type, "enforcement": "active",
                       "conditions": {"ref_name": {"include": patterns, "exclude": []}},
                       "bypass_actors": [],
                       "rules": [{"type": "deletion"}, {"type": "non_fast_forward"}]})
        rules = [{"type": "update", **({"parameters": {"update_allows_fetch_and_merge": False}}
                                     if ref_type == "branch" else {})}]
        if ref_type == "tag":
            rules.append({"type": "creation"})
        result.append({"name": name.replace("integrity", "publisher"), "target": ref_type,
                       "enforcement": "active", "conditions": {"ref_name": {"include": patterns, "exclude": []}},
                       "bypass_actors": bypass, "rules": rules})
    result.append({"name": "studio-branch-gate", "target": "branch", "enforcement": "active",
                   "conditions": {"ref_name": {"include": ["refs/heads/develop", "refs/heads/main"], "exclude": []}},
                   "bypass_actors": [], "rules": [{"type": "required_status_checks", "parameters": {
                       "strict_required_status_checks_policy": True,
                       "required_status_checks": [{"context": check} for check in checks]}}]})
    return result


def check_rulesets(actual, platform_app_id, checks=("gate",), agent_app_id=None):
    findings = []
    if agent_app_id is not None and agent_app_id == platform_app_id:
        findings.append(finding("agent-in-bypass", "rulesets", "Platform and coding Apps must have distinct identities"))
    if agent_app_id is not None:
        for item in actual:
            if any(actor.get("actor_id") == agent_app_id for actor in item.get("bypass_actors", [])):
                findings.append(finding("agent-in-bypass", item.get("name", "ruleset"), "Coding App is in a bypass list"))
    by_name = {item.get("name"): item for item in actual}
    for wanted in expected_rulesets(platform_app_id, checks):
        got = by_name.get(wanted["name"])
        if got is None:
            findings.append(finding("missing-repository-rules", wanted["name"], "Ruleset is absent"))
            continue
        fields = ("target", "enforcement", "conditions", "bypass_actors")
        if any(got.get(key) != wanted[key] for key in fields):
            findings.append(finding("repository-rules-drift", wanted["name"], "Target, enforcement, refs or bypass differs"))
        expected_types = {rule["type"] for rule in wanted["rules"]}
        got_rules = {rule.get("type"): rule for rule in got.get("rules", [])}
        if not expected_types.issubset(got_rules):
            findings.append(finding("repository-rules-drift", wanted["name"], "Required rule is absent"))
        for rule in wanted["rules"]:
            if "parameters" in rule and got_rules.get(rule["type"], {}).get("parameters") != rule["parameters"]:
                findings.append(finding("repository-rules-drift", wanted["name"], rule["type"] + " parameters differ"))
    return findings


def github_rulesets(repository, token):
    request = urllib.request.Request(
        f"https://api.github.com/repos/{repository}/rulesets?includes_parents=false&per_page=100",
        headers={"Authorization": f"Bearer {token}", "Accept": "application/vnd.github+json", "User-Agent": "agent-studio-posture"})
    with urllib.request.urlopen(request, timeout=15) as response:
        summary = json.load(response)
    details = []
    for item in summary:
        detail_request = urllib.request.Request(
            f"https://api.github.com/repos/{repository}/rulesets/{item['id']}",
            headers={"Authorization": f"Bearer {token}", "Accept": "application/vnd.github+json",
                     "User-Agent": "agent-studio-posture"})
        with urllib.request.urlopen(detail_request, timeout=15) as response:
            details.append(json.load(response))
    return details


def apply_rulesets(repository, token, platform_app_id, checks):
    current = {item["name"]: item for item in github_rulesets(repository, token)}
    for wanted in expected_rulesets(platform_app_id, checks):
        existing = current.get(wanted["name"])
        method = "PUT" if existing else "POST"
        suffix = f"/{existing['id']}" if existing else ""
        request = urllib.request.Request(
            f"https://api.github.com/repos/{repository}/rulesets{suffix}",
            data=json.dumps(wanted).encode(), method=method,
            headers={"Authorization": f"Bearer {token}", "Accept": "application/vnd.github+json",
                     "Content-Type": "application/json", "User-Agent": "agent-studio-posture"})
        with urllib.request.urlopen(request, timeout=15):
            pass


def check_public_url(url):
    if not url:
        return [finding("public-url-unconfigured", "public-url", "PUBLIC_URL is required for the release scan")]
    if not url.startswith("https://"):
        return [finding("tls-invalid", "public-url", "HTTPS is required")]
    try:
        request = urllib.request.Request(url, method="GET", headers={"User-Agent": "agent-studio-posture"})
        with urllib.request.urlopen(request, timeout=15, context=ssl.create_default_context()) as response:
            if not response.geturl().startswith("https://"):
                return [finding("tls-invalid", "public-url", "Redirected outside HTTPS")]
            headers = response.headers
            return [finding("missing-response-header", "public-url", key)
                    for key in REQUIRED_HEADERS if not headers.get(key)]
    except (urllib.error.URLError, TimeoutError, ssl.SSLError) as error:
        return [finding("tls-invalid", "public-url", type(error).__name__)]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=pathlib.Path, default=pathlib.Path(__file__).resolve().parents[2])
    parser.add_argument("--rulesets-json", type=pathlib.Path)
    parser.add_argument("--repository")
    parser.add_argument("--token")
    parser.add_argument("--platform-app-id", type=int)
    parser.add_argument("--agent-app-id", type=int)
    parser.add_argument("--gate-check", action="append", default=[])
    parser.add_argument("--public-url")
    parser.add_argument("--release", action="store_true")
    parser.add_argument("--route-inventory", action="store_true")
    parser.add_argument("--apply", action="store_true", help="Reconcile rulesets with the platform credential")
    args = parser.parse_args()
    if args.repository and not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", args.repository):
        parser.error("--repository must be OWNER/REPO")
    if args.apply:
        if not args.repository or not args.token or not args.platform_app_id or not args.agent_app_id or not args.gate_check:
            parser.error("--apply requires --repository, --token, --platform-app-id, --agent-app-id and --gate-check")
        if args.platform_app_id == args.agent_app_id:
            parser.error("The platform and coding App identities must differ")
        apply_rulesets(args.repository, args.token, args.platform_app_id, args.gate_check)
    findings = scan_checkout(args.root)
    if args.rulesets_json or args.repository:
        if args.repository and not args.agent_app_id:
            findings.append(finding("agent-app-unconfigured", "rulesets", "Coding App id is required for bypass audit"))
        if not args.platform_app_id or not args.gate_check:
            findings.append(finding("repository-rules-unconfigured", "rulesets", "Platform App id and gate check are required"))
        else:
            try:
                rulesets = json.loads(args.rulesets_json.read_text()) if args.rulesets_json else github_rulesets(args.repository, args.token)
                findings += check_rulesets(rulesets, args.platform_app_id, args.gate_check, args.agent_app_id)
            except (OSError, ValueError, urllib.error.URLError) as error:
                findings.append(finding("repository-rules-unavailable", "rulesets", type(error).__name__))
    if args.release:
        findings += check_public_url(args.public_url)
    if args.route_inventory:
        try:
            result = subprocess.run(["dotnet", "test", str(args.root / "backend.Tests/OrchestratorApi.Tests.csproj"),
                                     "--configuration", "Release", "--no-build",
                                     "--filter", "FullyQualifiedName~RoutePermissionInventoryTests"],
                                    cwd=args.root, capture_output=True, text=True, timeout=120)
            passed = result.returncode == 0 and "Passed!" in result.stdout
        except (OSError, subprocess.TimeoutExpired):
            passed = False
        if not passed:
            findings.append(finding("route-permission-inventory", "backend.Tests", "Inventory guard failed"))
    print(json.dumps({"schemaVersion": 1, "sensor": "repository-posture", "status": "fail" if findings else "pass", "findings": findings}, indent=2))
    return 1 if findings else 0


if __name__ == "__main__":
    sys.exit(main())
