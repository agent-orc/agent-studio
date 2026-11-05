#!/usr/bin/env python3
"""Remote half of host_adapter.py. Sent over SSH, never installed on a host."""

import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile


def fail(message):
    raise RuntimeError(message)


def run(argv, cwd=None):
    result = subprocess.run(argv, cwd=cwd, text=True, capture_output=True)
    if result.returncode:
        fail(f"{argv[0]} exited {result.returncode}: {result.stderr.strip()}")
    return result.stdout.strip()


def check_command(host, key):
    argv = host.get(key)
    if not isinstance(argv, list) or not argv or not all(isinstance(x, str) and x for x in argv):
        fail(f"host needs {key} command array")
    return run(argv)


def digest(path):
    result = hashlib.sha256()
    with open(path, "rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            result.update(block)
    return result.hexdigest()


def env_read(path):
    result = {}
    for line in Path(path).read_text().splitlines():
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, value = line.split("=", 1)
        result[key] = value
    return result


def env_set(path, changes):
    source = Path(path)
    lines = source.read_text().splitlines()
    for value in changes.values():
        if value is not None and (not isinstance(value, str) or "\n" in value or "\r" in value):
            fail("invalid environment value")
    seen = set()
    output = []
    for line in lines:
        key = line.split("=", 1)[0] if "=" in line else ""
        if key in changes:
            if key not in seen and changes[key] is not None:
                output.append(f"{key}={changes[key]}")
            seen.add(key)
        else:
            output.append(line)
    output.extend(f"{key}={value}" for key, value in changes.items() if key not in seen and value is not None)
    descriptor, temporary = tempfile.mkstemp(prefix=".networked-env-", dir=source.parent)
    try:
        with os.fdopen(descriptor, "w") as stream:
            stream.write("\n".join(output) + "\n")
        os.chmod(temporary, source.stat().st_mode & 0o777)
        os.replace(temporary, source)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def compose(directory, env_file, *args):
    run(["docker", "compose", "--env-file", env_file, "up", "-d", "--no-build", "--no-deps", *args], cwd=directory)


def compose_ps(directory, env_file, service):
    return run(["docker", "compose", "--env-file", env_file, "ps", "-q", service], cwd=directory)


def running_compose_service(project, service):
    # Snapshot the installed stack without rendering the release's newer Compose file.
    # Its required variables may not exist until the first switch writes them.
    containers = run(["docker", "ps", "--filter", f"label=com.docker.compose.project={project}",
                      "--filter", f"label=com.docker.compose.service={service}",
                      "--format", "{{.ID}}"])
    matches = containers.splitlines()
    if len(matches) != 1:
        fail(f"expected one running {service} container in Compose project {project}, found {len(matches)}")
    return matches[0]


def installed_commit(path):
    return (Path(path).resolve() / "COMMIT").read_text().strip()


def install_archive(archive, expected, release_root, commit):
    source = Path(archive)
    if not re.fullmatch(r"[0-9a-f]{64}", expected) or digest(source) != expected:
        fail("transferred release digest mismatch")
    destination = Path(release_root) / expected
    if destination.exists():
        marker = destination / "COMMIT"
        if not marker.is_file():
            # Earlier versions extracted directly into the final directory.
            # A failed extraction left that directory behind without a marker.
            shutil.rmtree(destination)
        elif marker.read_text().strip() != commit:
            fail("release digest has a different commit")
    if not destination.exists():
        destination.parent.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix=".networked-release-", dir=destination.parent) as temporary:
            staged = Path(temporary)
            with tarfile.open(source) as stream:
                stream.extractall(staged, filter="data")
            (staged / "COMMIT").write_text(commit + "\n")
            os.chmod(staged, destination.parent.stat().st_mode & 0o777)
            os.replace(staged, destination)
    return destination


def replace_link(link, destination):
    path = Path(link)
    temporary = path.with_name(path.name + ".networked-next")
    temporary.unlink(missing_ok=True)
    temporary.symlink_to(destination)
    os.replace(temporary, path)


def require_idle(host):
    output = check_command(host, "reviewIdleCommand")
    try:
        observation = json.loads(output)
    except json.JSONDecodeError:
        fail("review idle command must return JSON")
    if observation.get("idle") is not True:
        fail("review host is busy")


def running_image(container, repository):
    original = run(["docker", "inspect", "--format", "{{.Config.Image}}", container])
    image_id = run(["docker", "inspect", "--format", "{{.Image}}", container])
    if not re.fullmatch(r"sha256:[0-9a-f]{64}", image_id):
        fail("running container has no image ID")
    if re.fullmatch(re.escape(repository) + r"@sha256:[0-9a-f]{64}", original):
        pinned = original
    else:
        # A container from the prior Compose file may use a tag. Resolve the
        # tag's repository digest while checking that the tag still names the
        # image used by the running container.
        lookup = original
        try:
            if run(["docker", "image", "inspect", "--format", "{{.Id}}", original]) != image_id:
                lookup = image_id
        except RuntimeError:
            lookup = image_id
        refs = json.loads(run(["docker", "image", "inspect", "--format",
                               "{{json .RepoDigests}}", lookup]))
        if not isinstance(refs, list):
            fail("running image has no repository digests")
        matches = [ref for ref in refs if isinstance(ref, str) and
                   re.fullmatch(re.escape(repository) + r"@sha256:[0-9a-f]{64}", ref)]
        if len(matches) != 1:
            fail(f"running image needs one digest for {repository}")
        pinned = matches[0]
    return {"image": original, "digest": pinned.rsplit("@sha256:", 1)[1], "imageId": image_id}


def main(request):
    action = request["action"]
    host = request["host"]
    fields = request["fields"]
    config = request["settings"]
    commit = fields[-1] if fields else None
    if action == "snapshot":
        role = host["role"]
        if role == "server":
            images = {}
            containers = {}
            for service, repository in (("api", config["images"]["api"]),
                                        ("frontend", config["images"]["web"])):
                container = running_compose_service(host["composeProject"], service)
                containers[service] = container
                images[service] = running_image(container, repository)
            running_env = run(["docker", "inspect", "--format", '{{range .Config.Env}}{{println .}}{{end}}',
                               containers["api"]]).splitlines()
            worker = next((line.split("=", 1)[1] for line in running_env
                           if line.startswith("RemoteGate__WorkerPath=")), None)
            gate_host = next((line.split("=", 1)[1] for line in running_env
                              if line.startswith("RemoteGate__SshHost=")), None)
            return {"value": {"apiImage": images["api"]["image"],
                              "webImage": images["frontend"]["image"],
                              "apiDigest": images["api"]["digest"],
                              "webDigest": images["frontend"]["digest"],
                              "apiImageId": images["api"]["imageId"],
                              "webImageId": images["frontend"]["imageId"],
                              "gateSshHost": gate_host, "workerPath": worker}}
        if role == "engine":
            container = running_compose_service(host["engineComposeProject"], "orchestrator-engine")
            return {"value": {"engineVersion": run(["docker", "inspect", "--format",
                                                     "{{.Config.Image}}", container])}}
        if role == "gate":
            return {"value": {"workerPath": str(Path(host["gateCurrent"]).resolve())}}
        if role == "coding":
            return {"value": {"agentHostRelease": str(Path(host["runnerCurrent"]).resolve())}}
        if role == "review":
            return {"value": {"agentHostRelease": str(Path(host["runnerCurrent"]).resolve()),
                              "image": env_read(host["reviewEnvFile"])["REVIEW_IMAGE_REF"]}}
    if action == "drain":
        check_command(host, "admissionDrainCommand")
        return {"drained": True}
    if action == "resume":
        check_command(host, "admissionResumeCommand")
        return {"admitting": True}
    if action == "gate_publish":
        archive, original, expected, commit = fields
        destination = install_archive(archive, expected, host["gateReleaseRoot"], commit)
        if not (destination / "OrchestratorApi.dll").is_file():
            fail("gate worker release lacks OrchestratorApi.dll")
        replace_link(host["gateCurrent"], destination)
        return {"commit": commit, "artifactRef": original,
                "workerPath": str(Path(host["gateCurrent"]).resolve() / "OrchestratorApi.dll")}
    if action == "gate_verify":
        archive, expected, commit = fields
        current = Path(host["gateCurrent"]).resolve()
        if digest(archive) != expected or not current.is_dir() or installed_commit(current) != commit:
            fail("gate worker verification failed")
        return {"sha256": expected}
    if action == "server_switch":
        api, web, worker = fields
        api_digest = api.rsplit("@sha256:", 1)[1]
        web_digest = web.rsplit("@sha256:", 1)[1]
        if not all(re.fullmatch(r"[0-9a-f]{64}", x) for x in (api_digest, web_digest)):
            fail("server images must be pinned digests")
        if not host.get("gateSshHost") or not os.path.isabs(worker):
            fail("server switch needs a gate SSH host and absolute worker path")
        run(["docker", "pull", api])
        run(["docker", "pull", web])
        env_set(host["envFile"], {"STUDIO_API_DIGEST": api_digest, "STUDIO_WEB_DIGEST": web_digest,
                                   "STUDIO_GATE_SSH_HOST": host["gateSshHost"],
                                   "STUDIO_GATE_WORKER_PATH": worker})
        compose(host["composeDir"], host["envFile"], "api", "frontend")
        return {"switched": True}
    if action == "server_health":
        expected = fields[0]
        run(["curl", "-fsS", host["healthUrl"]])
        for service in ("api", "frontend"):
            container = compose_ps(host["composeDir"], host["envFile"], service)
            revision = run(["docker", "inspect", "--format", '{{index .Config.Labels "org.opencontainers.image.revision"}}', container])
            if revision != expected:
                fail(f"{service} running revision {revision}, expected {expected}")
        return {"healthy": True, "commit": expected}
    if action == "engine_switch":
        image = fields[0]
        if not re.search(r"@sha256:[0-9a-f]{64}$", image):
            fail("engine image must be pinned")
        run(["docker", "pull", image])
        env_set(host["engineEnvFile"], {"ORCHESTRATOR_ENGINE_IMAGE_REF": image})
        compose(host["engineComposeDir"], host["engineEnvFile"], "orchestrator-engine")
        return {"switched": True}
    if action == "engine_health":
        expected = fields[0]
        container = compose_ps(host["engineComposeDir"], host["engineEnvFile"], "orchestrator-engine")
        revision = run(["docker", "inspect", "--format", '{{index .Config.Labels "org.opencontainers.image.revision"}}', container])
        if revision != expected:
            fail("engine running revision mismatch")
        if run(["docker", "inspect", "--format", "{{.State.Health.Status}}", container]) != "healthy":
            fail("engine container is not healthy")
        return {"healthy": True, "commit": expected}
    if action == "coding_install":
        archive, original, expected, commit = fields
        incoming = Path(host["runnerIncoming"])
        if not re.fullmatch(r"[0-9a-f]{64}", expected) or digest(archive) != expected:
            fail("transferred coding release digest mismatch")
        release_id = f"{commit[:12]}-{expected[:12]}"
        if incoming.exists() and any(incoming.iterdir()):
            marker = incoming / "COMMIT"
            identity = incoming / "release-id"
            if marker.is_file() and identity.is_file():
                if marker.read_text().strip() != commit or identity.read_text().strip() != release_id:
                    fail("runner incoming directory contains another release")
            else:
                # Recover an extraction interrupted by the previous direct-write flow.
                for entry in incoming.iterdir():
                    if entry.is_dir() and not entry.is_symlink():
                        shutil.rmtree(entry)
                    else:
                        entry.unlink()
        if not incoming.exists() or not any(incoming.iterdir()):
            incoming.mkdir(exist_ok=True)
            with tarfile.open(archive) as stream:
                stream.extractall(incoming, filter="data")
            (incoming / "COMMIT").write_text(commit + "\n")
            (incoming / "release-id").write_text(release_id + "\n")
        run(["sudo", "/usr/local/sbin/agent-runner-deploy", "--restart-review-drain"])
        return {"installed": True}
    if action == "coding_health":
        expected = fields[0]
        if installed_commit(host["runnerCurrent"]) != expected:
            fail("coding release commit mismatch")
        run(["systemctl", "is-active", "--quiet", "agent-runner.service"])
        run(["systemctl", "is-active", "--quiet", "agent-runner-review.service"])
        return {"healthy": True, "commit": expected}
    if action == "model_probe":
        probes = host.get("modelProbes")
        if not isinstance(probes, list) or not probes:
            fail("coding host needs modelProbes")
        for probe in probes:
            argv, marker = probe["argv"], probe["contains"]
            if not isinstance(argv, list) or not argv or not isinstance(marker, str) or not marker:
                fail("invalid model acceptance probe")
            if marker not in run(argv):
                fail("model acceptance probe did not return its marker")
        return {"accepted": True}
    if action == "review_copy":
        archive, original, expected, commit = fields
        install_archive(archive, expected, host["runnerReleaseRoot"], commit)
        return {"sha256": expected}
    if action == "review_build":
        expected_commit, agent_sha = fields
        release = Path(host["runnerReleaseRoot"]) / agent_sha
        if installed_commit(release) != expected_commit:
            fail("review release commit mismatch")
        base = host["reviewBaseImage"]
        if not re.fullmatch(r"[A-Za-z0-9./:_-]+@sha256:[0-9a-f]{64}", base):
            fail("review base image must be pinned")
        recipe = "review-copy-v1:FROM base;COPY publish /opt/agent-host;labels revision and plan"
        plan = hashlib.sha256(f"{recipe}\n{base}\n{agent_sha}\n{expected_commit}\n".encode()).hexdigest()
        image = host["reviewImageRepository"]
        with tempfile.TemporaryDirectory(prefix="networked-review-") as scratch:
            root = Path(scratch)
            (root / "Dockerfile").write_text(f"FROM {base}\nCOPY publish/ /opt/agent-host/\nLABEL org.opencontainers.image.revision={expected_commit}\nLABEL org.agent-studio.review-plan={plan}\n")
            shutil.copytree(release, root / "publish")
            metadata = root / "metadata.json"
            run(["docker", "buildx", "build", "--push", "--metadata-file", str(metadata),
                 "-t", f"{image}:{expected_commit}-{plan[:12]}", "."], cwd=root)
            digest_value = json.loads(metadata.read_text())["containerimage.digest"]
        if not re.fullmatch(r"sha256:[0-9a-f]{64}", digest_value):
            fail("review build did not return image digest")
        return {"commit": expected_commit, "agentHostSha256": agent_sha,
                "planDigest": plan, "imageRef": f"{image}@{digest_value}"}
    if action == "review_idle":
        require_idle(host)
        return {"idle": True}
    if action == "review_recreate":
        image, plan, agent_sha = fields
        check_command(host, "reviewDrainCommand")
        require_idle(host)
        run(["docker", "pull", image])
        observed_plan = run(["docker", "image", "inspect", "--format",
                             '{{index .Config.Labels "org.agent-studio.review-plan"}}', image])
        if observed_plan != plan:
            fail("review image plan digest mismatch")
        release = Path(host["runnerReleaseRoot"]) / agent_sha
        if not release.is_dir():
            fail("review release is missing")
        env_set(host["reviewEnvFile"], {"REVIEW_IMAGE_REF": image})
        compose(host["reviewComposeDir"], host["reviewEnvFile"], *host["reviewServices"])
        replace_link(host["runnerCurrent"], release)
        return {"recreated": True}
    if action == "review_verify":
        commit = fields[0]
        image = env_read(host["reviewEnvFile"])["REVIEW_IMAGE_REF"]
        plans = set()
        for service in host["reviewServices"]:
            container = compose_ps(host["reviewComposeDir"], host["reviewEnvFile"], service)
            revision = run(["docker", "inspect", "--format", '{{index .Config.Labels "org.opencontainers.image.revision"}}', container])
            plan = run(["docker", "inspect", "--format", '{{index .Config.Labels "org.agent-studio.review-plan"}}', container])
            if revision != commit:
                fail("review running revision mismatch")
            configured_image = run(["docker", "inspect", "--format", "{{.Config.Image}}", container])
            if configured_image != image:
                fail("review container image differs from pinned image")
            if run(["docker", "inspect", "--format", "{{.State.Health.Status}}", container]) != "healthy":
                fail("review container is not healthy")
            plans.add(plan)
        if len(plans) != 1:
            fail("review containers use different plans")
        return {"commit": commit, "planDigest": plans.pop(), "imageRef": image}
    if action.startswith("rollback_"):
        previous = json.loads(fields[0])
        role = action.removeprefix("rollback_")
        if role == "review":
            check_command(host, "reviewDrainCommand")
            require_idle(host)
            replace_link(host["runnerCurrent"], previous["agentHostRelease"])
            env_set(host["reviewEnvFile"], {"REVIEW_IMAGE_REF": previous["image"]})
            compose(host["reviewComposeDir"], host["reviewEnvFile"], *host["reviewServices"])
            require_idle(host)
            for service in host["reviewServices"]:
                container = compose_ps(host["reviewComposeDir"], host["reviewEnvFile"], service)
                if run(["docker", "inspect", "--format", "{{.Config.Image}}", container]) != previous["image"]:
                    fail("review rollback image mismatch")
                if run(["docker", "inspect", "--format", "{{.State.Health.Status}}", container]) != "healthy":
                    fail("review rollback container is not healthy")
        elif role == "coding":
            release = Path(previous["agentHostRelease"])
            if not release.is_dir() or not release.is_relative_to(host["runnerReleaseRoot"]):
                fail("coding rollback release is outside the release root")
            run(["sudo", "ln", "-sfnT", str(release), host["runnerCurrent"]])
            run(["sudo", "/usr/local/sbin/agent-runner-deploy", "restart-review"])
            run(["sudo", "systemctl", "restart", "agent-runner.service"])
            run(["systemctl", "is-active", "--quiet", "agent-runner.service"])
            run(["systemctl", "is-active", "--quiet", "agent-runner-review.service"])
        elif role == "server":
            api = previous["apiDigest"]
            web = previous["webDigest"]
            if not all(re.fullmatch(r"[0-9a-f]{64}", value) for value in (api, web)):
                fail("server rollback snapshot has invalid image digests")
            run(["docker", "pull", f'{config["images"]["api"]}@sha256:{api}'])
            run(["docker", "pull", f'{config["images"]["web"]}@sha256:{web}'])
            env_set(host["envFile"], {"STUDIO_API_DIGEST": api, "STUDIO_WEB_DIGEST": web,
                                       "STUDIO_GATE_SSH_HOST": previous["gateSshHost"],
                                       "STUDIO_GATE_WORKER_PATH": previous["workerPath"]})
            compose(host["composeDir"], host["envFile"], "api", "frontend")
            run(["curl", "-fsS", host["healthUrl"]])
            for service, expected in (("api", previous["apiImageId"]),
                                      ("frontend", previous["webImageId"])):
                container = compose_ps(host["composeDir"], host["envFile"], service)
                if run(["docker", "inspect", "--format", "{{.Image}}", container]) != expected:
                    fail(f"{service} rollback image mismatch")
        elif role == "engine":
            env_set(host["engineEnvFile"], {"ORCHESTRATOR_ENGINE_IMAGE_REF": previous["engineVersion"]})
            compose(host["engineComposeDir"], host["engineEnvFile"], "orchestrator-engine")
            container = compose_ps(host["engineComposeDir"], host["engineEnvFile"], "orchestrator-engine")
            if run(["docker", "inspect", "--format", "{{.Config.Image}}", container]) != previous["engineVersion"]:
                fail("engine rollback image mismatch")
            if run(["docker", "inspect", "--format", "{{.State.Health.Status}}", container]) != "healthy":
                fail("engine rollback container is not healthy")
        elif role == "gate":
            replace_link(host["gateCurrent"], previous["workerPath"])
            if not Path(host["gateCurrent"]).resolve().is_dir():
                fail("gate rollback target missing")
        return {"restored": True}
    fail(f"unknown action: {action}")


if __name__ == "__main__":
    try:
        print(json.dumps(main(json.load(sys.stdin)), separators=(",", ":")))
    except (RuntimeError, OSError, ValueError, KeyError, IndexError, TypeError) as error:
        print(f"remote-host: {error}", file=sys.stderr)
        sys.exit(1)
