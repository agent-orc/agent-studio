#!/usr/bin/env python3
"""Repository-owned transport and artifact builder for networked deploy."""

import argparse
import hashlib
import io
import json
import os
from pathlib import Path
import re
import shlex
import subprocess
import sys
import tarfile
import tempfile


class AdapterError(Exception):
    pass


def run(argv, **kwargs):
    result = subprocess.run(argv, text=True, capture_output=True, **kwargs)
    if result.returncode:
        raise AdapterError(f"{argv[0]} exited {result.returncode}: {result.stderr.strip()}")
    return result.stdout


def sha(path):
    digest = hashlib.sha256()
    with open(path, "rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def exact_source(repo, commit, target):
    run(["git", "-C", repo, "cat-file", "-e", f"{commit}^{{commit}}"])
    source = subprocess.run(["git", "-C", repo, "archive", "--format=tar", commit], capture_output=True, check=True).stdout
    with tarfile.open(fileobj=io.BytesIO(source)) as archive:
        archive.extractall(target, filter="data")


def build(config, action, commit):
    name = action.removeprefix("build-").replace("-", "_")
    if name not in ("api", "web", "engine", "agent_host", "gate_worker"):
        raise AdapterError(f"unknown build: {name}")
    base = Path(config["artifactDirectory"]).expanduser().resolve() / commit
    base.mkdir(parents=True, exist_ok=True)
    if name in ("agent_host", "gate_worker"):
        existing = base / f"{name}.tar.gz"
        checksum = base / f"{name}.sha256"
        if existing.exists():
            if checksum.is_file():
                recorded = checksum.read_text().strip()
                if re.fullmatch(r"[0-9a-f]{64}", recorded):
                    if recorded != sha(existing):
                        raise AdapterError(f"existing release has no matching checksum: {existing}")
                    return {"commit": commit, "ref": str(existing), "sha256": recorded}
    with tempfile.TemporaryDirectory(prefix="networked-build-") as scratch:
        source = Path(scratch) / "source"
        source.mkdir()
        exact_source(config["repository"], commit, source)
        if name in ("api", "web", "engine"):
            image = config["images"][name]
            dockerfile = {"api": "backend/Dockerfile", "web": "frontend/Dockerfile",
                          "engine": "orchestrator-engine/Dockerfile"}[name]
            metadata = Path(scratch) / "metadata.json"
            run(["docker", "buildx", "build", "--push", "--metadata-file", str(metadata),
                 "--build-arg", f"SHA={commit}", "--tag", f"{image}:{commit}",
                 "-f", dockerfile, "."], cwd=source)
            digest = json.loads(metadata.read_text())["containerimage.digest"]
            if not re.fullmatch(r"sha256:[0-9a-f]{64}", digest):
                raise AdapterError("buildx did not report an immutable image digest")
            return {"commit": commit, "ref": f"{image}@{digest}", "sha256": digest[7:]}
        project = "runner/AgentRunner.csproj" if name == "agent_host" else "backend/OrchestratorApi.csproj"
        output = Path(scratch) / "publish"
        publish = ["dotnet", "publish", project, "-c", "Release", "-o", str(output),
                   f"-p:SourceRevisionId={commit}"]
        if name == "gate_worker":
            publish.append("/p:UseAppHost=false")
        run(publish, cwd=source)
        destination = base / f"{name}.tar.gz"
        checksum_path = base / f"{name}.sha256"
        descriptor, temporary = tempfile.mkstemp(prefix=f".{name}-", suffix=".tar.gz", dir=base)
        os.close(descriptor)
        checksum_temporary = None
        try:
            with tarfile.open(temporary, "w:gz") as archive:
                for path in sorted(output.rglob("*")):
                    archive.add(path, path.relative_to(output))
            checksum = sha(temporary)
            os.replace(temporary, destination)
            # The checksum is the completion marker. A crash before it is written
            # leaves an archive that the next build can replace from the commit.
            descriptor, checksum_temporary = tempfile.mkstemp(prefix=f".{name}-", suffix=".sha256", dir=base)
            with os.fdopen(descriptor, "w") as stream:
                stream.write(checksum + "\n")
            os.replace(checksum_temporary, checksum_path)
            return {"commit": commit, "ref": str(destination), "sha256": checksum}
        finally:
            Path(temporary).unlink(missing_ok=True)
            if checksum_temporary is not None:
                Path(checksum_temporary).unlink(missing_ok=True)


def remote(config, host, action, fields):
    target = host["target"]
    if target.startswith("-") or any(c.isspace() for c in target):
        raise AdapterError("invalid SSH target")
    script = (Path(__file__).parent / "remote_host.py").read_text(encoding="utf-8")
    request = {"action": action, "host": host, "settings": config, "fields": fields}
    command = ["ssh", "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes", "--", target,
               "python3 -c " + shlex.quote(script)]
    result = subprocess.run(command, input=json.dumps(request), text=True, capture_output=True)
    if result.returncode:
        raise AdapterError(f"remote {action} failed on {host['id']}: {result.stderr.strip()}")
    try:
        return json.loads(result.stdout)
    except json.JSONDecodeError as error:
        raise AdapterError(f"remote {action} returned invalid JSON on {host['id']}") from error


def copy_release(host, path, digest, destination):
    if sha(path) != digest:
        raise AdapterError("release archive digest changed")
    target = host["target"]
    if target.startswith("-") or any(c.isspace() for c in target):
        raise AdapterError("invalid SSH target")
    # Transfer to a unique, untrusted staging path; remote code checks the hash before use.
    run(["scp", "-q", "--", path, f"{target}:{destination}"])


def work_state(response):
    """Batch facts for the deploy quiet point, from the operator triage view."""
    projects = response.get("projects")
    if not isinstance(projects, list):
        raise AdapterError("triage response has no projects")
    result = {"formedBatches": [], "runningGates": [], "flights": [], "pendingPushes": []}
    for project in projects:
        for flight in project.get("flights", []):
            name = f"{project['project']}/{flight['batchId']}"
            result["formedBatches"].append(name)
            runs = (flight.get("state") or {}).get("runs") or []
            started = [item.get("startedAtUtc") for item in runs
                       if isinstance(item, dict) and item.get("startedAtUtc") and not item.get("finishedAtUtc")]
            result["flights"].append({"id": name, "phase": flight.get("phase"),
                                      "startedAtUtc": started[-1] if started else None})
        # A flight covers active gate work; the preflight also checks pending pushes.
        result["runningGates"].extend(f"{project['project']}/{member['taskKey']}"
                                      for member in project.get("members", [])
                                      if member.get("status") == "in-batch")
        result["pendingPushes"].extend({"jobId": f"{project['project']}/{push.get('jobId')}",
                                        "sinceUtc": push.get("sinceUtc")}
                                       for push in project.get("pendingPushes", []))
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", required=True)
    parser.add_argument("action")
    parser.add_argument("args", nargs="*")
    args = parser.parse_args()
    try:
        config = json.loads(Path(args.config).read_text(encoding="utf-8"))
        if args.action.startswith("build-"):
            if len(args.args) != 1 or not re.fullmatch(r"[0-9a-f]{40}", args.args[0]):
                raise AdapterError("build needs a full commit SHA")
            result = build(config, args.action, args.args[0])
        elif args.action == "work-state":
            response = json.loads(run(["studio-op", "triage", "--json"]))
            result = work_state(response)
        elif args.action in ("host-drain", "host-status", "host-resume"):
            # AGT-3647: the deploy drain of one runner identity the manifest names.
            if len(args.args) != 2:
                raise AdapterError(f"{args.action} needs host id and client id")
            host = next((h for h in config["hosts"] if h["id"] == args.args[0]), None)
            if host is None or args.args[1] not in host.get("drainClients", []):
                raise AdapterError("client id is not a drain client of that host")
            response = json.loads(run(["studio-op", "host", args.action.removeprefix("host-"),
                                       args.args[1], "--json"]))
            if response.get("ok") is not True:
                raise AdapterError(f"studio-op host {args.action} refused {args.args[1]}")
            result = response
        else:
            if not args.args:
                raise AdapterError("host action needs host id")
            host = next((h for h in config["hosts"] if h["id"] == args.args[0]), None)
            if host is None:
                raise AdapterError("unknown host id")
            fields = args.args[1:]
            action = args.action.replace("-", "_")
            if action in ("gate_publish", "coding_install", "review_copy"):
                if len(fields) < 2:
                    raise AdapterError("release transfer needs path and digest")
                filename = f"/tmp/networked-{fields[1]}.tar.gz"
                copy_release(host, fields[0], fields[1], filename)
                fields = [filename, *fields]
            elif action == "gate_verify":
                if len(fields) != 2:
                    raise AdapterError("gate verify needs release digest and commit")
                fields = [f"/tmp/networked-{fields[0]}.tar.gz", fields[0], fields[1]]
            result = remote(config, host, action, fields)
        print(json.dumps(result, separators=(",", ":")))
    except (AdapterError, OSError, ValueError, KeyError, IndexError, subprocess.CalledProcessError) as error:
        print(f"host-adapter: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
