#!/usr/bin/env python3
"""Ordered networked release runner. The local manifest supplies host commands."""

import argparse
import concurrent.futures
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile

ARTIFACTS = ("api", "web", "engine", "agent_host", "gate_worker")
ROLES = ("server", "gate", "engine", "coding", "review")
ORDER = ("drain", "task-server", "readiness", "engine", "hosts")
STEPS = (
    "build", "snapshot", "preflight", "work_state", "plan", "drain", "gate_publish", "gate_verify",
    "server_switch", "server_health", "engine_switch", "engine_health",
    "coding_install", "coding_health", "model_probe", "review_copy",
    "review_build", "review_idle", "review_recreate", "review_verify", "resume",
)


class DeployError(Exception):
    pass


def utc():
    return dt.datetime.now(dt.timezone.utc)


def timestamp():
    return utc().isoformat().replace("+00:00", "Z")


def read_json(path):
    with open(path, encoding="utf-8") as file:
        return json.load(file)


def save_json(path, value):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    descriptor, temporary = tempfile.mkstemp(prefix=".deploy-", dir=path.parent)
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8") as file:
            json.dump(value, file, indent=2, sort_keys=True)
            file.write("\n")
            file.flush()
            os.fsync(file.fileno())
        os.chmod(temporary, 0o600)
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def command(manifest, name, role=None):
    commands = manifest["commands"]
    value = commands.get(name)
    if role is not None:
        value = value.get(role) if isinstance(value, dict) else None
    if not isinstance(value, list) or not value or not all(isinstance(x, str) and x for x in value):
        raise DeployError(f"missing command: {name}{'.' + role if role else ''}")
    return value


def validate(manifest):
    if manifest.get("schema") != 1 or not isinstance(manifest.get("commands"), dict):
        raise DeployError("manifest schema must be 1 with commands")
    hosts = manifest.get("hosts")
    if not isinstance(hosts, list) or not hosts:
        raise DeployError("manifest needs hosts")
    ids = set()
    for host in hosts:
        if not isinstance(host, dict) or host.get("role") not in ROLES or not host.get("id") or not host.get("target"):
            raise DeployError("each host needs id, role and target")
        if host["id"] in ids:
            raise DeployError("duplicate host id")
        ids.add(host["id"])
    if any(sum(h["role"] == role for h in hosts) != 1 for role in ("server", "gate", "engine")):
        raise DeployError("exactly one server, gate and engine host are required")
    if not any(h["role"] == "coding" for h in hosts) or not any(h["role"] == "review" for h in hosts):
        raise DeployError("coding and review hosts are required")
    for artifact in ARTIFACTS:
        build_command = command(manifest, "build", artifact)
        if not any("{commit}" in part for part in build_command):
            raise DeployError(f"build.{artifact} must receive the exact commit")
    for role in {h["role"] for h in hosts}:
        command(manifest, "snapshot", role)
        command(manifest, "rollback", role)
    for name in STEPS[2:]:
        if name.startswith("coding_") or name == "model_probe":
            if not any(h["role"] == "coding" for h in hosts):
                continue
        if name.startswith("review_"):
            if not any(h["role"] == "review" for h in hosts):
                continue
        command(manifest, name)


def host_of(manifest, role):
    return [host for host in manifest["hosts"] if host["role"] == role]


def render(argv, values):
    try:
        return [part.format_map(values) for part in argv]
    except (KeyError, ValueError) as error:
        raise DeployError(f"invalid command placeholder: {error}") from error


def invoke(argv, values):
    rendered = render(argv, values)
    result = subprocess.run(rendered, text=True, capture_output=True, check=False)
    if result.returncode:
        detail = ""
        try:
            failure = json.loads(result.stdout)
            blockers = failure.get("blockers") or failure.get("answer", {}).get("blockers") or []
            if blockers:
                detail = ": " + ", ".join(
                    f"{item.get('code', 'blocker')} {item.get('subject', '')}".strip()
                    for item in blockers)
            elif failure.get("error"):
                detail = ": " + str(failure["error"])
        except (json.JSONDecodeError, AttributeError, TypeError):
            pass
        raise DeployError(f"command exited {result.returncode}: {rendered[0]}{detail}")
    try:
        return json.loads(result.stdout)
    except json.JSONDecodeError as error:
        raise DeployError(f"command did not return JSON: {rendered[0]}") from error


def require(result, key, expected):
    if not isinstance(result, dict) or result.get(key) != expected:
        raise DeployError(f"expected {key}={expected!r}")


def artifact(result, commit, name):
    require(result, "commit", commit)
    ref = result.get("ref")
    digest = result.get("sha256")
    if not isinstance(ref, str) or not ref or not isinstance(digest, str) \
            or not re.fullmatch(r"[0-9a-f]{64}", digest):
        raise DeployError(f"{name} artifact needs ref and sha256")
    if name in ("api", "web", "engine") and "@sha256:" not in ref:
        raise DeployError(f"{name} image must use an immutable digest ref")
    if name in ("api", "web", "engine") and ref.rsplit("@sha256:", 1)[1] != digest:
        raise DeployError(f"{name} image ref and SHA-256 disagree")
    path = Path(ref)
    if path.is_symlink():
        raise DeployError(f"{name} artifact ref must not be a mutable symlink")
    if path.is_file():
        actual = hashlib.sha256()
        with path.open("rb") as stream:
            for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                actual.update(chunk)
        if actual.hexdigest() != digest:
            raise DeployError(f"{name} local artifact digest mismatch")
    return {"ref": ref, "sha256": digest, "commit": commit}


def check_plan(result, commit):
    require(result, "ok", True)
    require(result, "targetVersion", commit)
    steps = result.get("steps", [])
    if not isinstance(steps, list) or not all(isinstance(item, dict) for item in steps):
        raise DeployError("OP-6 plan has invalid steps")
    if [(item.get("order"), item.get("kind")) for item in steps] != list(enumerate(ORDER, 1)):
        raise DeployError("OP-6 plan must order drain, task-server, readiness, engine, hosts")
    expiry = result.get("expiresAtUtc")
    try:
        deadline = dt.datetime.fromisoformat(expiry.replace("Z", "+00:00"))
    except (AttributeError, ValueError) as error:
        raise DeployError("OP-6 plan has no valid expiry") from error
    now = utc()
    if deadline.tzinfo is None or deadline <= now or deadline > now + dt.timedelta(minutes=121):
        raise DeployError("OP-6 plan expiry must be within its bounded window")
    return expiry


def assert_window(state):
    if dt.datetime.fromisoformat(state["planExpiresAtUtc"].replace("Z", "+00:00")) <= utc():
        raise DeployError("OP-6 plan expired; stop before the next change")


def run_step(state, journal, name, action):
    record = {"step": name, "startedAtUtc": timestamp(), "status": "running"}
    state["steps"].append(record)
    save_json(journal, state)
    print(f"{name}: start", flush=True)
    try:
        value = action()
        record["status"] = "passed"
        return value
    except Exception as error:
        record["status"] = "failed"
        record["error"] = str(error)
        state["status"] = "failed"
        state["endedAtUtc"] = timestamp()
        raise
    finally:
        record["endedAtUtc"] = timestamp()
        start = dt.datetime.fromisoformat(record["startedAtUtc"].replace("Z", "+00:00"))
        end = dt.datetime.fromisoformat(record["endedAtUtc"].replace("Z", "+00:00"))
        record["durationSeconds"] = round((end - start).total_seconds(), 3)
        save_json(journal, state)


def values(state, host=None, **extra):
    if hashlib.sha256(Path(state["manifest"]).read_bytes()).hexdigest() != state["manifestSha256"]:
        raise DeployError("manifest changed during the release window")
    data = {"commit": state["commit"], "target_version": state["commit"],
            "built_at": state.get("builtAtUtc", ""),
            "manifest": state["manifest"],
            "adapter": str(Path(__file__).resolve().parent / "host_adapter.py")}
    for name, item in state.get("artifacts", {}).items():
        data[f"{name}_ref"] = item["ref"]
        data[f"{name}_sha256"] = item["sha256"]
    if host:
        data.update(host=host["target"], host_id=host["id"], role=host["role"])
        previous = state["previous"][host["id"]]["value"]
        data["previous"] = json.dumps(previous, separators=(",", ":")) if isinstance(previous, dict) else previous
    data.update(extra)
    return data


def execute(manifest, name, state, host=None, **extra):
    return invoke(command(manifest, name), values(state, host, **extra))


def deploy(args, manifest):
    state = {"schema": 1, "commit": args.commit, "manifest": str(Path(args.manifest).resolve()),
             "manifestSha256": hashlib.sha256(Path(args.manifest).read_bytes()).hexdigest(),
             "status": "running", "startedAtUtc": timestamp(),
             "steps": [], "artifacts": {}, "previous": {}, "reviewPlanDigests": {}, "reviewImages": {}}
    journal = Path(args.journal)
    if journal.exists():
        raise DeployError(f"journal already exists: {journal}")

    def build():
        def build_one(name):
            started = utc()
            record = {"step": f"build:{name}", "startedAtUtc": started.isoformat().replace("+00:00", "Z")}
            try:
                item = artifact(invoke(command(manifest, "build", name), values(state)), args.commit, name)
                record["status"] = "passed"
                return item, record, None
            except Exception as error:
                record["status"] = "failed"
                record["error"] = str(error)
                return None, record, error
            finally:
                ended = utc()
                record["endedAtUtc"] = ended.isoformat().replace("+00:00", "Z")
                record["durationSeconds"] = round((ended - started).total_seconds(), 3)

        with concurrent.futures.ThreadPoolExecutor(max_workers=len(ARTIFACTS)) as pool:
            futures = {name: pool.submit(build_one, name) for name in ARTIFACTS}
            first_error = None
            for name, future in futures.items():
                item, record, error = future.result()
                state["steps"].append(record)
                if item is not None:
                    state["artifacts"][name] = item
                if first_error is None and error is not None:
                    first_error = error
            if first_error is None:
                # The review image stamps this as RUNNER_RELEASE_BUILT_AT (AGT-3689).
                state["builtAtUtc"] = timestamp()
            save_json(journal, state)
            if first_error is not None:
                raise first_error

    run_step(state, journal, "build", build)

    def snapshot():
        required = {"server": {"apiImage", "webImage", "apiDigest", "webDigest",
                                "apiImageId", "webImageId", "gateSshHost", "workerPath"},
                    "gate": {"workerPath"}, "engine": {"engineVersion"},
                    "coding": {"agentHostRelease"},
                    "review": {"agentHostRelease", "image"}}
        for host in manifest["hosts"]:
            values(state)
            result = invoke(command(manifest, "snapshot", host["role"]),
                            {"commit": args.commit, "host": host["target"], "host_id": host["id"],
                             "manifest": state["manifest"],
                             "adapter": str(Path(__file__).resolve().parent / "host_adapter.py")})
            value = result.get("value") if isinstance(result, dict) else None
            optional = {"gateSshHost", "workerPath"} if host["role"] == "server" else set()
            if not isinstance(value, dict) or not required[host["role"]].issubset(value) \
                    or any(not isinstance(value[key], str) or not value[key]
                           for key in required[host["role"]] - optional) \
                    or any(value[key] is not None and (not isinstance(value[key], str) or not value[key])
                           for key in optional):
                raise DeployError(f"snapshot for {host['id']} needs {sorted(required[host['role']])}")
            if host["role"] == "server" and (any(
                    not re.fullmatch(r"[0-9a-f]{64}", value[key])
                    for key in ("apiDigest", "webDigest")) or any(
                    not re.fullmatch(r"sha256:[0-9a-f]{64}", value[key])
                    for key in ("apiImageId", "webImageId"))):
                raise DeployError("server snapshot needs valid image digests and IDs")
            state["previous"][host["id"]] = {"role": host["role"], "target": host["target"],
                                            "value": result["value"]}

    run_step(state, journal, "snapshot", snapshot)

    def preflight():
        result = execute(manifest, "preflight", state)
        if not isinstance(result, dict) or result.get("allowed") is not True or result.get("blockers"):
            raise DeployError("OP-6 deploy preflight refused: " + ", ".join(
                str(item.get("code", item)) for item in result.get("blockers", [])))

    def work_state():
        result = execute(manifest, "work_state", state)
        if not isinstance(result, dict) or not isinstance(result.get("formedBatches"), list) \
                or not isinstance(result.get("runningGates"), list):
            raise DeployError("work state needs formedBatches and runningGates arrays")
        if result["formedBatches"] or result["runningGates"]:
            raise DeployError("formed batches or running gates block deployment")

    run_step(state, journal, "preflight", preflight)
    run_step(state, journal, "work_state", work_state)

    def plan():
        result = execute(manifest, "plan", state)
        state["planExpiresAtUtc"] = check_plan(result, args.commit)
        state["planId"] = result.get("id")

    run_step(state, journal, "plan", plan)
    gate = host_of(manifest, "gate")[0]
    server = host_of(manifest, "server")[0]
    engine = host_of(manifest, "engine")[0]

    def checked(name, host=None, **extra):
        assert_window(state)
        return execute(manifest, name, state, host, **extra)

    run_step(state, journal, "drain", lambda: require(checked("drain", server), "drained", True))
    run_step(state, journal, "preflight_after_drain", preflight)
    run_step(state, journal, "work_state_after_drain", work_state)
    def publish_gate():
        result = checked("gate_publish", gate)
        require(result, "commit", args.commit)
        require(result, "artifactRef", state["artifacts"]["gate_worker"]["ref"])
        worker_path = result.get("workerPath")
        if not isinstance(worker_path, str) or not worker_path.startswith("/"):
            raise DeployError("gate publish needs an absolute workerPath")
        state["gateWorkerPath"] = worker_path
    run_step(state, journal, "gate_publish", publish_gate)
    run_step(state, journal, "gate_verify", lambda: require(
        checked("gate_verify", gate), "sha256", state["artifacts"]["gate_worker"]["sha256"]))
    run_step(state, journal, "preflight_before_switch", preflight)
    run_step(state, journal, "work_state_before_switch", work_state)
    run_step(state, journal, "server_switch", lambda: checked("server_switch", server,
        gate_worker_path=state["gateWorkerPath"]))
    def health(name, host):
        result = checked(name, host)
        require(result, "healthy", True)
        require(result, "commit", args.commit)
    run_step(state, journal, "server_health", lambda: health("server_health", server))
    run_step(state, journal, "engine_switch", lambda: checked("engine_switch", engine))
    run_step(state, journal, "engine_health", lambda: health("engine_health", engine))
    for host in host_of(manifest, "coding"):
        run_step(state, journal, f"coding_install:{host['id']}", lambda h=host: checked("coding_install", h))
        run_step(state, journal, f"coding_health:{host['id']}", lambda h=host: health("coding_health", h))
        run_step(state, journal, f"model_probe:{host['id']}", lambda h=host: require(checked("model_probe", h), "accepted", True))
    for host in host_of(manifest, "review"):
        run_step(state, journal, f"review_copy:{host['id']}", lambda h=host: require(
            checked("review_copy", h), "sha256", state["artifacts"]["agent_host"]["sha256"]))
        def build_review(h):
            result = checked("review_build", h)
            require(result, "commit", args.commit)
            require(result, "agentHostSha256", state["artifacts"]["agent_host"]["sha256"])
            digest = result.get("planDigest")
            if not isinstance(digest, str) or not digest:
                raise DeployError("review build needs a planDigest")
            image_ref = result.get("imageRef")
            if not isinstance(image_ref, str) or not re.search(r"@sha256:[0-9a-f]{64}$", image_ref):
                raise DeployError("review build needs an immutable imageRef")
            return digest, image_ref
        digest, image_ref = run_step(state, journal, f"review_build:{host['id']}", lambda h=host: build_review(h))
        state["reviewPlanDigests"][host["id"]] = digest
        state["reviewImages"][host["id"]] = image_ref
        save_json(journal, state)
        run_step(state, journal, f"review_idle:{host['id']}",
                 lambda h=host: require(checked("review_idle", h), "idle", True))
        run_step(state, journal, f"review_recreate:{host['id']}",
                 lambda h=host, d=digest, i=image_ref: checked("review_recreate", h,
                     review_plan_digest=d, review_image_ref=i))
        def verify_review(h, d, i):
            result = checked("review_verify", h)
            require(result, "planDigest", d)
            require(result, "imageRef", i)
            require(result, "commit", args.commit)
        run_step(state, journal, f"review_verify:{host['id']}",
                 lambda h=host, d=digest, i=image_ref: verify_review(h, d, i))
    run_step(state, journal, "resume", lambda: require(checked("resume", server), "admitting", True))
    state["status"] = "passed"
    state["endedAtUtc"] = timestamp()
    save_json(journal, state)


def rollback(args, manifest):
    journal = Path(args.journal)
    state = read_json(journal)
    state["manifest"] = str(Path(args.manifest).resolve())
    values(state)
    if state.get("status") == "rolled-back" or not state.get("previous"):
        raise DeployError("journal has no rollback state")
    if state.get("commit") != args.commit:
        raise DeployError("rollback commit does not match journal")
    for host in manifest["hosts"]:
        captured = state["previous"].get(host["id"])
        if captured is None or captured.get("role") != host["role"] \
                or captured.get("target") != host["target"]:
            raise DeployError(f"rollback host differs from captured snapshot: {host['id']}")
    touched = set()
    for step in state.get("steps", []):
        name = step.get("step", "")
        if name == "server_switch":
            touched.update(host["id"] for host in host_of(manifest, "server"))
        elif name == "engine_switch":
            touched.update(host["id"] for host in host_of(manifest, "engine"))
        elif name == "gate_publish":
            touched.update(host["id"] for host in host_of(manifest, "gate"))
        elif name.startswith("coding_install:") or name.startswith("review_recreate:"):
            touched.add(name.split(":", 1)[1])
    if not touched:
        raise DeployError("no release-changing step reached; rollback is unnecessary")
    if args.dry_run:
        for role in ("review", "coding", "engine", "server", "gate"):
            for host in reversed(host_of(manifest, role)):
                if host["id"] in touched:
                    print(f"rollback {host['role']} {host['id']}")
        return
    # Recheck the authority immediately before changing any installed release.
    def rollback_preflight():
        result = execute(manifest, "preflight", state)
        if result.get("allowed") is not True or result.get("blockers"):
            raise DeployError("OP-6 deploy preflight refused rollback")
    run_step(state, journal, "rollback_preflight", rollback_preflight)
    def rollback_work_state():
        work = execute(manifest, "work_state", state)
        if not isinstance(work, dict) or work.get("formedBatches") != [] or work.get("runningGates") != []:
            raise DeployError("formed batches or running gates block rollback")
    run_step(state, journal, "rollback_work_state", rollback_work_state)
    server = host_of(manifest, "server")[0]
    run_step(state, journal, "rollback_drain", lambda: require(
        execute(manifest, "drain", state, server), "drained", True))
    for role in ("review", "coding", "engine", "server", "gate"):
        for host in reversed(host_of(manifest, role)):
            if host["id"] not in touched:
                continue
            if role == "review":
                run_step(state, journal, f"rollback_review_idle:{host['id']}",
                         lambda h=host: require(execute(manifest, "review_idle", state, h), "idle", True))
            run_step(state, journal, f"rollback:{host['id']}",
                     lambda h=host: require(
                         invoke(command(manifest, "rollback", h["role"]), values(state, h)), "restored", True))
    run_step(state, journal, "rollback_resume", lambda: require(
        execute(manifest, "resume", state, server), "admitting", True))
    state["status"] = "rolled-back"
    state["endedAtUtc"] = timestamp()
    save_json(journal, state)


def print_dry_run(commit, manifest):
    print(f"Deploy {commit}:")
    for artifact_name in ARTIFACTS:
        print(f"  build {artifact_name} (parallel)")
    for host in manifest["hosts"]:
        print(f"  snapshot {host['role']} {host['id']}")
    for name in ("preflight", "work_state", "plan", "drain", "preflight_after_drain",
                 "work_state_after_drain", "gate_publish", "gate_verify",
                 "preflight_before_switch", "work_state_before_switch", "server_switch", "server_health",
                 "engine_switch", "engine_health"):
        print(f"  {name}")
    for host in host_of(manifest, "coding"):
        for name in ("coding_install", "coding_health", "model_probe"):
            print(f"  {name} {host['id']}")
    for host in host_of(manifest, "review"):
        for name in ("review_copy", "review_build", "review_idle", "review_recreate", "review_verify"):
            print(f"  {name} {host['id']}")
    print("  resume")
    print("  rollback uses the captured journal")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("operation", choices=("deploy", "rollback"))
    parser.add_argument("commit", help="full Git commit SHA")
    parser.add_argument("--manifest", required=True, help="operator-owned local JSON manifest")
    parser.add_argument("--journal", required=True, help="owner-only duration and rollback JSON")
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()
    try:
        if not re.fullmatch(r"[0-9a-f]{40}", args.commit):
            raise DeployError("commit must be a full lowercase Git SHA")
        manifest = read_json(args.manifest)
        validate(manifest)
        if args.operation == "rollback":
            rollback(args, manifest)
        elif args.dry_run:
            print_dry_run(args.commit, manifest)
        else:
            deploy(args, manifest)
    except (DeployError, OSError, ValueError, KeyError, TypeError) as error:
        print(f"deploy: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
