#!/usr/bin/env python3
"""Unprivileged host-tool rehearsal. Run directly with Python 3.12+."""
import json
import os
from pathlib import Path
import stat
import subprocess
import sys
import tempfile


TOOLS = Path(__file__).resolve().parents[1] / "deploy/compose/monolith-networked"
FAKES = {"docker", "mount", "umount", "mountpoint", "systemd-run", "id", "df"}


def list_by_label(args, root, names):
    selector = args[args.index("--filter") + 1]
    kind, _, label = selector.partition("=")
    assert kind == "label", f"Invalid Docker filter: {kind}"
    for name in names:
        resource = root / name
        if resource.exists() and resource.read_text() == label:
            print(name)


def fake(program, args, root):
    with (root / "calls").open("a") as calls:
        print(json.dumps([program, *args]), file=calls)
    if program == "id":
        print(0)
    elif program == "df":
        print("Size Available")
        print("100", os.getenv("FREE_PERCENT", "50"))
    elif program == "mount":
        assert args[:3] == ["-t", "overlay", "overlay"]
        paths = dict(part.split("=", 1) for part in args[-2].split(","))
        lower, upper = (Path(paths[key]).stat() for key in ("lowerdir", "upperdir"))
        assert (lower.st_uid, lower.st_gid, lower.st_mode) == (upper.st_uid, upper.st_gid, upper.st_mode)
        assert str(root) in args[-2] and Path(args[-1]).is_relative_to(root)
        if os.getenv("FAIL_MOUNT") == Path(args[-1]).name:
            return 1
        (Path(args[-1]) / "mounted").touch()
    elif program == "mountpoint":
        return 0 if (Path(args[-1]) / "mounted").exists() else 32
    elif program == "umount":
        if os.getenv("BUSY"):
            return 1
        (Path(args[-1]) / "mounted").unlink()
    elif program == "systemd-run":
        if os.getenv("NO_WATCHDOG"):
            return 1
        (root / "watch").write_text(json.dumps(args[args.index("-I") - 1:]))
    elif args[:2] == ["compose", "config"]:
        assert Path.cwd() == root / "stack"
        print((root / "compose.json").read_text())
    elif args[:2] == ["compose", "ps"]:
        print("live-" + args[-1])
    elif args[0] == "ps":
        list_by_label(args, root, ("copy-web", "copy-api", "other-api"))
    elif args[:2] == ["image", "inspect"]:
        if os.getenv("MISSING_IMAGE"):
            return 1
        print("image-id")
    elif args[0] == "inspect":
        if "--size" in args:
            if (root / "fail-size-once").exists():
                (root / "fail-size-once").unlink()
                return 1
            if os.getenv("FAIL_SIZE"):
                return 1
            print(os.getenv("ROOTFS_BYTES", "0"), 0, sep="\n")
        elif "{{.Image}}" in args:
            print("id-of-" + args[-1])
        elif "IPAddress" in args[-2]:
            if (root / "fail-address-once").exists():
                (root / "fail-address-once").unlink()
                return 1
            print("192.0.2.10")
        elif (root / "fail-health-once").exists():
            (root / "fail-health-once").unlink()
            return 1
        elif args[-1] == "copy-web":
            first_check = root / "first-web-check"
            if first_check.exists():
                print(first_check.read_text())
                first_check.unlink()
            else:
                print(os.getenv("WEB_HEALTH", "true healthy"))
        else:
            print(os.getenv("HEALTH", "true healthy"))
    elif args[:2] == ["network", "create"]:
        assert "--internal" in args
        (root / "network").write_text(args[args.index("--label") + 1])
        print("network")
    elif args[:2] == ["network", "ls"]:
        list_by_label(args, root, ("network", "other-network"))
    elif args[:2] == ["network", "rm"]:
        (root / args[-1]).unlink()
    elif args[0] == "run":
        if "--env-file" in args:
            env = Path(args[args.index("--env-file") + 1])
            assert stat.S_IMODE(env.stat().st_mode) == 0o600
            assert stat.S_IMODE(env.parent.stat().st_mode) == 0o700
            values = dict(line.split("=", 1) for line in env.read_text().splitlines())
            assert "MultipleLines" not in values
            for role, target in (("workspace", "/cards"), ("projects", "/projects"), ("home", "/home")):
                assert f"type=bind,source={env.parent}/merged-{role},target={target}" in args
            assert args.count("--mount") == 5 and "--pull=never" in args
            if args[args.index("--network") + 1] == "none":
                assert values["Security__BootstrapCode"] == os.environ["CANARY"]
                assert values["Security__Profile"] == "networked"
            else:
                assert values["Security__Profile"] == "local" and values["AllowedHosts"] == "*"
                assert values["Security__PublicCloudflareEdge"] == "false"
                assert "Security__BootstrapCode" not in values
                assert not any("trustedproxy" in k.lower() for k in values)
            name = "copy-api"
        else:
            assert "container:copy-api" in args and "-p" not in args and "--publish" not in args
            if os.getenv("FAIL_WEB"):
                return 1
            caddy = Path(next(a.split(",")[1][7:] for a in args if a.startswith("type=bind")))
            assert "reverse_proxy 127.0.0.1:5031" in caddy.read_text()
            assert caddy.stat().st_mode & stat.S_IROTH
            name = "copy-web"
        (root / name).write_text(args[args.index("--label") + 1])
        print(name)
    elif args[0] == "rm":
        assert "-fv" in args
        if (root / "fail-rm-once").exists():
            (root / "fail-rm-once").unlink()
            return 1
        for name in args[2:]:
            (root / name).unlink()
    elif args[0] == "logs":
        print("startup-phase", os.environ["CANARY"])
    elif args[0] == "stats":
        print("32MiB cpu=0.1%")
    else:
        raise AssertionError((program, args))
    return 0


def layout(root):
    for name in ("bin", "stack", "source workspace", "project-data", "local-home"):
        (root / name).mkdir()
    volumes = []
    for target, source in [("/cards", "source workspace"), ("/projects", "project-data"), ("/home", "local-home"),
                           ("/keys/kek", "test.key"), ("/app/appsettings.Local.json", "settings.json")]:
        file = target in ("/keys/kek", "/app/appsettings.Local.json")
        if file:
            (root / source).touch()
        volumes.append(dict(type="bind", source=str(root / source), target=target, read_only=file))
    config = {"services": {
        "backend-fixture": {"image": "example.invalid:5050/release/api@sha256:" + "f"*64, "volumes": volumes,
                            "environment": {"TaskRepository": "/cards", "HOME": "/home", "Secrets__KeyFile": "/keys/kek",
                                            "Backup__Sources__projects__Path": "/projects",
                                            "Security__BootstrapCode": os.environ["CANARY"],
                                            "Security__Profile": "networked", "Security__PublicCloudflareEdge": "true",
                                            "Security__TrustedProxyAddresses__1": "192.0.2.1",
                                            "MultipleLines": "first\nsecond"}},
        "site-fixture": {"image": "example.invalid:5050/site:old", "depends_on": {"backend-fixture": {}},
                         "volumes": [{"target": "/etc/caddy/Caddyfile"}]}}}
    (root / "compose.json").write_text(json.dumps(config))
    for name in FAKES:
        (root / "bin" / name).symlink_to(Path(__file__).resolve())


def call(*args, code=0, **env):
    result = subprocess.run(args, capture_output=True, text=True, timeout=45, env={**os.environ, **env})
    output = result.stdout + result.stderr
    assert os.environ["CANARY"] not in output
    assert result.returncode == code, (args, code, result.returncode, output)
    return result.stdout


def copies(root):
    preflight = TOOLS / "preflight-start.sh"
    shadow = TOOLS / "shadow-studio.sh"
    for name in ("other-api", "other-network"):
        (root / name).write_text("studio.operator-copy=run-other")
    for script in (preflight, shadow, TOOLS / "prune-prep-runs.sh"):
        assert "Usage:" in call(script, "--help")
    for state, code, result in [("true healthy", 0, "healthy"), ("false starting", 1, "exited"),
                                ("true starting", 1, "unhealthy"), ("true", 1, "unhealthy")]:
        out = call(preflight, "candidate", "0", HEALTH=state, code=code)
        assert out.splitlines()[-1].startswith(f"result={result} seconds=")
        assert "startup-phase [redacted]" in out
    (root / "fail-health-once").touch()
    assert call(preflight, "candidate", "0", code=1).splitlines()[-1].startswith("result=unhealthy seconds=")
    call(preflight, "candidate", MISSING_IMAGE="1", code=2)
    call(preflight, "candidate", FAIL_MOUNT="merged-projects", BUSY="1", code=3)
    assert list((root / "state").glob("*/run-*/merged-workspace/mounted"))
    call(preflight, "candidate", FAIL_MOUNT="merged-projects", code=3)
    assert not list((root / "state").glob("*/run-*"))
    call(shadow, "start", FREE_PERCENT="14", code=2)
    call(shadow, "start", NO_WATCHDOG="1", code=3)
    call(shadow, "start", FAIL_WEB="1", code=3)
    (root / "first-web-check").write_text("true unhealthy")
    call(shadow, "start", WEB_HEALTH="false", code=1)
    assert not any((root / n).exists() for n in ("copy-api", "copy-web", "network"))
    (root / "fail-health-once").touch()
    call(shadow, "start", WEB_HEALTH="true", FREE_PERCENT="15")
    assert "api=healthy web=running" in call(shadow, "status", WEB_HEALTH="true")
    old_watch = json.loads((root / "watch").read_text())
    assert "limit_bytes=4294967296" in call(shadow, "status")
    assert "lifetime_seconds=1800" in call(shadow)
    assert not list((root / "state").rglob("api.env"))
    call(shadow, "start", code=2)
    (root / "fail-rm-once").touch()
    call(shadow, "stop")
    assert not any((root / n).exists() for n in ("copy-api", "copy-web", "network", "fail-rm-once"))
    call(shadow, "start", "candidate", STUDIO_SHADOW_LIMIT_MIB="1")
    call(*old_watch)
    # A missed size sample and a missed health check precede the confirmed exit.
    (root / "fail-size-once").touch()
    (root / "fail-health-once").touch()
    (root / "first-web-check").write_text("false")
    watchdog_output = call(*json.loads((root / "watch").read_text()))
    assert "container exited" in watchdog_output, watchdog_output
    assert not (root / "first-web-check").exists()
    (root / "fail-size-once").touch()
    (root / "fail-address-once").touch()
    assert "address 192.0.2.10:8080" in call(shadow, "start", "candidate", STUDIO_SHADOW_LIMIT_MIB="1")
    run, = (root / "state").glob("shadow-*/run-*")
    with (run / "upper-home" / "growth").open("wb") as fd:
        fd.write(bytes(1024**2))
    assert "write limit reached" in call(*json.loads((root / "watch").read_text()))
    call(shadow, "start", STUDIO_SHADOW_LIFETIME_SECONDS="600")
    receipt, = (root / "state").glob("shadow-*/run-*/session.json")
    session = json.loads(receipt.read_text())
    assert session["lifetime"] == 600
    session["start"] -= 600
    receipt.write_text(json.dumps(session))
    assert "lifetime reached" in call(*json.loads((root / "watch").read_text()))
    call(shadow, "start")
    assert "could not read shadow state or usage" in call(*json.loads((root / "watch").read_text()), FAIL_SIZE="1")
    assert "stopped" in call(shadow)
    call(shadow, "start", ROOTFS_BYTES=str(4096 * 1024**2), code=1)
    assert "stopped" in call(shadow)
    assert not any((root / n).exists() for n in ("copy-api", "copy-web", "network"))
    assert not list((root / "state").glob("*/run-*"))
    for name in ("other-api", "other-network"):
        assert (root / name).read_text() == "studio.operator-copy=run-other"
    trace = (root / "calls").read_text()
    for needle in ("example.invalid:5050/release/api:candidate", "example.invalid:5050/site:candidate",
                   "id-of-live-backend-fixture", "id-of-live-site-fixture", "target=/keys/kek,readonly",
                   "target=/app/appsettings.Local.json,readonly", "target=/cards", "lowerdir=" + str(root / "source workspace")):
        assert needle in trace, needle
    assert os.environ["CANARY"] not in trace
    assert not any(os.environ["CANARY"] in log.read_text() for log in (root / "state").rglob("*.log"))


def prune(root):
    tool = TOOLS / "prune-prep-runs.sh"
    work = root / "runner work"
    runs = work / "proj" / "caches" / "preparation" / ".runs"
    runs.mkdir(parents=True)
    environ = root / "proc" / "123" / "environ"
    environ.parent.mkdir(parents=True)
    opts = dict(RUNNER_WORKDIR=str(work), STUDIO_PROC_ROOT=str(root / "proc"))
    old, active, fresh, link = [k * 32 for k in "abcd"]
    for name in (old, active, fresh, "other name"):
        (runs / name).mkdir()
        if name != fresh:
            os.utime(runs / name, (1, 1))
    (runs / link).symlink_to(root / "source workspace")
    environ.write_bytes(f"x=y\0CACHE=/container/preparation/.runs/{active}/packages\0".encode())
    assert "removed=1 left=4" in call(tool, "proj", **opts)
    assert {p.name for p in runs.iterdir()} == {active, fresh, link, "other name"}
    environ.unlink()
    environ.mkdir()  # Unreadable environment, even when the test runs as root.
    call(tool, "proj", code=1, **opts)
    assert (runs / active).is_dir()
    (work / "alias").symlink_to(work / "proj")
    call(tool, "alias", code=1, **opts)
    call(tool, "../proj", code=2, **opts)
    call(tool, "absent", **opts)


def main():
    with tempfile.TemporaryDirectory(prefix="studio-host-tools-") as tmp:
        root = Path(tmp)
        os.environ.update(FIXTURE=tmp, CANARY=os.urandom(16).hex(),
                          STUDIO_STACK_DIR=str(root / "stack"), STUDIO_COPY_STATE_DIR=str(root / "state"),
                          PATH=str(root / "bin") + os.pathsep + os.environ["PATH"])
        layout(root)
        copies(root)
        prune(root)
    print("PASS: preflight, shadow lifecycle and watchdog, disk admission, and preparation prune.")
    print("Not exercised: real overlayfs, Docker image boot and networking, systemd scheduling, SSH/browser access or host PID visibility.")


if __name__ == "__main__":
    name = Path(sys.argv[0]).name
    if name in FAKES:
        sys.exit(fake(name, sys.argv[1:], Path(os.environ["FIXTURE"])))
    main()
