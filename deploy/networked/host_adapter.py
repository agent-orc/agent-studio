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
            if not checksum.is_file() or checksum.read_text().strip() != sha(existing):
                raise AdapterError(f"existing release has no matching checksum: {existing}")
            return {"commit": commit, "ref": str(existing), "sha256": sha(existing)}
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
        with tarfile.open(destination, "w:gz") as archive:
            for path in sorted(output.rglob("*")):
                archive.add(path, path.relative_to(output))
        os.chmod(destination, 0o600)
        checksum = sha(destination)
        checksum_path = base / f"{name}.sha256"
        checksum_path.write_text(checksum + "\n")
        os.chmod(checksum_path, 0o600)
        return {"commit": commit, "ref": str(destination), "sha256": checksum}


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
            projects = response.get("projects")
            if not isinstance(projects, list):
                raise AdapterError("triage response has no projects")
            result = {"formedBatches": [], "runningGates": []}
            for project in projects:
                result["formedBatches"].extend(f"{project['project']}/{flight['batchId']}"
                                               for flight in project.get("flights", []))
                # A flight covers active gate work; the preflight also checks pending pushes.
                result["runningGates"].extend(f"{project['project']}/{member['taskKey']}"
                                              for member in project.get("members", [])
                                              if member.get("status") == "in-batch")
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
