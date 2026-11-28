"""Host-side lifecycle shared by preflight-start.sh and shadow-studio.sh."""

from collections import deque
from contextlib import contextmanager
import fcntl
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import signal
import stat
import subprocess
import sys
import time
import uuid


TOOLS = Path(__file__).resolve().parent
DATA = ("workspace", "projects", "home")
LABEL = "studio.operator-copy"
CADDY = """:8080 {
    encode zstd gzip
    @api path /api /api/* /hubs /hubs/* /healthz /healthz/* /.well-known/agent-studio/*
    handle @api {
        reverse_proxy 127.0.0.1:5031 {
            header_up -X-Forwarded-For
            header_up -X-Forwarded-Proto
        }
    }
    handle {
        root * /srv/studio
        try_files {path} /index.html
        file_server
    }
}
"""


class Failure(Exception):
    def __init__(self, message, code=3):
        super().__init__(message)
        self.code = code


class CommandFailure(Failure):
    pass


def command(*args, cwd=None):
    # Compose output contains credentials. Never include stdout, stderr or
    # command arguments in a failure reported to the terminal or the journal.
    try:
        result = subprocess.run(args, cwd=cwd, capture_output=True, check=True,
                                timeout=90, text=True, errors="replace")
    except (OSError, subprocess.SubprocessError):
        raise CommandFailure(f"{args[0]} {args[1]} failed") from None
    return result.stdout.strip()


def positive(value, name):
    if not re.fullmatch(r"[1-9][0-9]{0,9}", value):
        raise Failure(f"{name} must be a positive decimal integer", 2)
    return int(value)


def image_tag(tag):
    if not re.fullmatch(r"[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}", tag):
        raise Failure("Invalid release tag", 2)
    return tag


def image_for(stack, name, service, tag):
    if tag:
        repository = service["image"].split("@")[0]
        repository = re.sub(r":[^/:]+$", "", repository)
        image = f"{repository}:{image_tag(tag)}"
    else:
        live = command("docker", "compose", "ps", "-q", name, cwd=stack)
        if len(live.split()) != 1:
            raise Failure(f"Expected one running container for {name}", 2)
        image = command("docker", "inspect", "-f", "{{.Image}}", live)
    try:
        command("docker", "image", "inspect", "-f", "{{.Id}}", image)
    except Failure:
        raise Failure(f"Local image missing for {name}: {image}", 2) from None
    return image


def service_name(services, override, candidates):
    names = [os.environ[override]] if os.environ.get(override) else list(candidates)
    if len(names) != 1 or names[0] not in services:
        raise Failure(f"Could not identify one Compose service. Set {override}.", 2)
    return names[0]


def load_services(stack, shadow):
    config = json.loads(command("docker", "compose", "config", "--format", "json", cwd=stack))
    services = config["services"]
    api = service_name(services, "STUDIO_COPY_API_SERVICE", (
        n for n, s in services.items() if "TaskRepository" in s.get("environment", {})
    ))
    web = None
    if shadow:
        web = service_name(services, "STUDIO_COPY_WEB_SERVICE", (
            n for n, s in services.items() if api in s.get("depends_on", {})
            and any(v["target"] == "/etc/caddy/Caddyfile" for v in s.get("volumes", []))
        ))
    return services, api, web


def environment(service, shadow):
    env = service.get("environment") or {}
    if not env:
        raise Failure("Empty API service environment")
    values = {}
    for key, value in env.items():
        if value is None or "\n" in str(value) or "\r" in str(value):
            continue
        if not re.fullmatch(r"[A-Za-z_][A-Za-z_0-9]*", key):
            raise Failure("Invalid API environment variable name")
        values[key] = str(value)
    if not values:
        raise Failure("No single-line API environment values")
    if shadow:
        local = {"Security__Profile": "local", "AllowedHosts": "*",
                 "Security__PublicCloudflareEdge": "false", "STUDIO_DOMAIN": "localhost"}
        drop = {key.lower() for key in (*local, "Security__BootstrapCode", "Security__TrustedProxyAddresses")}
        values = {k: v for k, v in values.items() if k.lower() not in drop
                  and not k.lower().startswith("security__trustedproxyaddresses__")}
        values.update(local)
    return values


def bind(source, target, readonly=False):
    if "," in str(source) or "," in target:
        raise Failure("A bind path contains a Docker mount option delimiter")
    spec = f"type=bind,source={source},target={target}"
    return ["--mount", spec + (",readonly" if readonly else "")]


def api_mounts(run, service):
    env = service["environment"]
    volumes = {v["target"]: v for v in service["volumes"] if v["type"] == "bind"}
    mounts = []
    targets = (env["TaskRepository"], env.get("Backup__Sources__projects__Path", "/data/projects"), env["HOME"])
    for role, target in zip(DATA, targets, strict=True):
        lower = Path(volumes[target]["source"]).resolve(strict=True)
        if not lower.is_dir() or run.is_relative_to(lower) or lower.is_relative_to(run):
            raise Failure("Overlay source and scratch must be separate directories")
        if any(c in str(path) for path in (run, lower) for c in ",:\\\n"):
            raise Failure("An overlay path contains a mount option delimiter")
        upper, work, merged = [run / f"{prefix}-{role}" for prefix in ("upper", "work", "merged")]
        for path in (upper, work, merged):
            path.mkdir()
        # Overlayfs gets the merged root's permissions from upperdir. The
        # image UID must have the same access at /data/* as on the live bind.
        info = lower.stat()
        os.chown(upper, info.st_uid, info.st_gid)
        shutil.copystat(lower, upper)
        command("mount", "-t", "overlay", "overlay", "-o",
                f"lowerdir={lower},upperdir={upper},workdir={work}", merged)
        mounts.extend(bind(merged, target))
    files = [env["Secrets__KeyFile"]]
    if "/app/appsettings.Local.json" in volumes:
        files.append("/app/appsettings.Local.json")
    for target in files:
        source = volumes[target]["source"]
        if not volumes[target].get("read_only") or not Path(source).is_file():
            raise Failure("Expected a read-only key or settings file bind")
        mounts.extend(bind(source, target, readonly=True))
    return mounts


def private_dir(path):
    path.mkdir(mode=0o700, parents=True, exist_ok=True)
    info = path.lstat()
    if not stat.S_ISDIR(info.st_mode) or info.st_uid != os.geteuid() or info.st_mode & 0o077:
        raise Failure(f"Expected a private operator-owned directory at {path}")


class Copies:
    def __init__(self, stack, state, kind):
        self.stack = stack.resolve(strict=True)
        self.state = state.absolute()
        private_dir(self.state)
        key = hashlib.sha256(os.fsencode(self.stack)).hexdigest()[:16]
        self.root = self.state / f"{kind}-{key}"
        private_dir(self.root)

    @contextmanager
    def lock(self, blocking=True):
        with (self.root / "lock").open("a") as fd:
            try:
                fcntl.flock(fd, fcntl.LOCK_EX | (0 if blocking else fcntl.LOCK_NB))
            except BlockingIOError:
                raise Failure("Another copy operation is in progress", 2) from None
            yield

    def existing(self):
        return next(self.root.glob("run-*"), None)

    def begin(self):
        old = self.existing()
        if old:
            if command("docker", "ps", "-q", "--filter", label_filter(old)):
                raise Failure("A copy is already running", 2)
            cleanup(old)
        run = self.root / f"run-{uuid.uuid4().hex}"
        run.mkdir()
        return run


def label(run):
    return f"{LABEL}={run.name}"


def label_filter(run):
    return f"label={label(run)}"


def cleanup(run):
    (run / "api.env").unlink(missing_ok=True)
    containers = command("docker", "ps", "-aq", "--filter", label_filter(run)).split()
    if containers:
        try:
            command("docker", "rm", "-fv", *containers)
        except Failure:
            time.sleep(2)
            containers = command("docker", "ps", "-aq", "--filter", label_filter(run)).split()
            if containers:
                command("docker", "rm", "-fv", *containers)
    for network in command("docker", "network", "ls", "-q", "--filter", label_filter(run)).split():
        command("docker", "network", "rm", network)
    for role in DATA:
        merged = run / f"merged-{role}"
        if merged.exists():
            status = subprocess.run(["mountpoint", "-q", merged], check=False).returncode
            if status == 0:
                command("umount", merged)
            elif status != 32:  # util-linux: 32 means specifically "not a mountpoint".
                raise Failure(f"Could not verify mount at {merged}")
    shutil.rmtree(run)


def launch(service, run, image, network, memory, cpus, args):
    user = ["--user", service["user"]] if service.get("user") else []
    return command("docker", "run", "-d", "--pull=never", "--label", label(run),
                   "--network", network, "--memory", memory, "--cpus", cpus,
                   "--cap-drop", "ALL", "--security-opt", "no-new-privileges",
                   *user, *args, image)


def prepare_api(run, service, shadow):
    envfile = run / "api.env"
    with envfile.open("x") as output:
        for key, value in environment(service, shadow).items():
            print(f"{key}={value}", file=output)
    return ["--env-file", envfile, *api_mounts(run, service)]


def health(container):
    try:
        state = command("docker", "inspect", "-f",
                        "{{.State.Running}} {{if .State.Health}}{{.State.Health.Status}}{{end}}", container)
    except CommandFailure:
        return "unknown"
    running, _, status = state.partition(" ")
    if running == "false":
        return "exited"
    if running != "true":
        return "unknown"
    return status or "running"


def diagnostics(container, service, log):
    # Docker logs can contain service values. Redact those before writing
    # bounded tails to either destination. Keep startup phases and memory use.
    noise = re.compile(r"(ERR|WRN)\].*(No such host|Name or service not known|Network is unreachable|Connection refused)", re.I)
    phases = re.compile(r"startup-phase|startup-repair|registry-bootstrap-complete|TaskRunnerService booting|Now listening|Application started")
    recent, startup = deque(maxlen=14), deque(maxlen=20)
    try:
        with subprocess.Popen(["docker", "logs", container], stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                              text=True, errors="replace") as logs:
            for line in logs.stdout:
                if not noise.search(line):
                    recent.append(line.rstrip())
                if phases.search(line):
                    startup.append(line.rstrip())
        values = sorted({part for v in service["environment"].values() if v
                         for part in str(v).splitlines() if part}, key=len, reverse=True)
        for title, selection in [("Recent API logs", recent), ("Startup phases", startup)]:
            log(title)
            for line in selection:
                for value in values:
                    line = line.replace(value, "[redacted]")
                log(line[:220])
        log(command("docker", "stats", "--no-stream", "--format", "{{.MemUsage}} cpu={{.CPUPerc}}", container))
    except (Failure, OSError, subprocess.SubprocessError):
        log("Some container diagnostics are unavailable")


def preflight(copies, tag, deadline):
    tag = image_tag(tag)
    deadline = 0 if deadline == "0" else positive(deadline, "Deadline")
    with copies.lock(blocking=False), (copies.root / f"preflight-{tag}.log").open("w") as logfile:
        def log(line):
            print(line, flush=True)
            print(line, file=logfile, flush=True)

        services, api, _ = load_services(copies.stack, False)
        service = services[api]
        image = image_for(copies.stack, api, service, tag)
        run = copies.begin()
        try:
            log(f"Preflight of {image}, deadline {deadline}s")
            args = prepare_api(run, service, False)
            start = time.monotonic()
            container = launch(service, run, image, "none", "12g", "6", args)
            (run / "api.env").unlink()
            while True:
                result = health(container)
                elapsed = int(time.monotonic() - start)
                if result in ("healthy", "exited"):
                    break
                if elapsed >= deadline:
                    result = "unhealthy"
                    break
                time.sleep(min(3, deadline - elapsed))
            diagnostics(container, service, log)
        finally:
            cleanup(run)
        log(f"Preflight {result} after {elapsed}s")
        log(f"result={result} seconds={elapsed}")
        return 0 if result == "healthy" else 1


def disk_admission(path):
    report = command("df", "--output=size,avail", "-k", path)
    total, available = map(int, report.splitlines()[-1].split())
    if total <= 0 or available * 100 < total * 15:
        raise Failure("Shadow requires at least 15 percent free on the scratch filesystem", 2)


def inspect_shadow(*args):
    try:
        return command("docker", "inspect", *args)
    except CommandFailure:
        time.sleep(1)
        return command("docker", "inspect", *args)


def shadow_usage(run, session):
    # Allocated blocks in the three upperdirs plus Docker's writable rootfs bytes.
    dirs = [run / f"upper-{role}" for role in DATA]
    kib = command("du", "-skx", *dirs)
    layers = inspect_shadow("--size", "-f", "{{.SizeRw}}", session["api"], session["web"])
    size = sum(int(line.split()[0]) * 1024 for line in kib.splitlines())
    size += sum(int(line) for line in layers.splitlines())
    return size, int(time.monotonic() - session["start"])


def shadow_limit(run, session, report=False):
    size, age = shadow_usage(run, session)
    if report:
        print(f"write_bytes={size} limit_bytes={session['limit']} "
              f"age_seconds={age} lifetime_seconds={session['lifetime']}")
    if age >= session["lifetime"]:
        return "lifetime reached"
    if size >= session["limit"]:
        return "write limit reached"
    return None


def shadow_address(api):
    ip = inspect_shadow("-f", "{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}", api)
    if not ip:
        raise Failure("Shadow API has no internal address")
    print(f"address {ip}:8080")


def start_shadow(copies, tag):
    disk_admission(copies.root)
    limit = positive(os.getenv("STUDIO_SHADOW_LIMIT_MIB", "4096"), "Size limit") * 1024**2
    lifetime = positive(os.getenv("STUDIO_SHADOW_LIFETIME_SECONDS", "1800"), "Lifetime")
    services, api, web = load_services(copies.stack, True)
    images = [image_for(copies.stack, name, services[name], tag) for name in (api, web)]
    run = copies.begin()
    try:
        session = {"start": time.monotonic(), "limit": limit, "lifetime": lifetime}
        # Register before creating mounts or containers. The watcher waits for
        # our setup lock. An interrupted setup leaves it a session to reclaim.
        docker_env = [f"--setenv={key}" for key in ("DOCKER_CONTEXT", "DOCKER_HOST", "DOCKER_CONFIG",
                      "DOCKER_TLS_VERIFY", "DOCKER_CERT_PATH") if key in os.environ]
        command("systemd-run", "--collect", "--service-type=exec", *docker_env, f"--unit=studio-shadow-{run.name}",
                "--property=Restart=on-failure", "--property=RestartSec=5s",
                sys.executable, "-I", Path(__file__).resolve(), "watch", copies.stack, copies.state, run.name)
        args = prepare_api(run, services[api], True)
        caddy = run / "Caddyfile"
        caddy.write_text(CADDY)
        caddy.chmod(0o644)  # The frontend image reads the bind as UID 10001.
        network = command("docker", "network", "create", "--internal", "--label", label(run), run.name)
        session["api"] = launch(services[api], run, images[0], network, "12g", "4", args)
        (run / "api.env").unlink()
        session["web"] = launch(services[web], run, images[1], "container:" + session["api"],
                                "512m", "1", bind(caddy, "/etc/caddy/Caddyfile", True))
        while True:
            reason = shadow_limit(run, session)
            if reason:
                raise Failure(f"Shadow {reason}", 1)
            api_state = health(session["api"])
            web_state = health(session["web"])
            if api_state == "healthy" and web_state in ("healthy", "running"):
                break
            if "exited" in (api_state, web_state) or time.monotonic() - session["start"] >= 420:
                diagnostics(session["api"], services[api], print)
                raise Failure("Shadow failed to become healthy", 1)
            time.sleep(3)
        (run / "session.json").write_text(json.dumps(session))
        shadow_address(session["api"])
    except BaseException:
        cleanup(run)
        raise


def watch(copies, generation):
    while True:
        with copies.lock():
            run = copies.existing()
            if run is None or run.name != generation:
                return
            try:
                session = json.loads((run / "session.json").read_text())
                reason = shadow_limit(run, session)
                if not reason and any(health(session[r]) == "exited" for r in ("web", "api")):
                    reason = "container exited"
            except (Failure, OSError, KeyError, ValueError):
                reason = "could not read shadow state or usage"
            if reason:
                print(f"Stopping shadow: {reason}", flush=True)
                cleanup(run)
                return
        time.sleep(5)


def shadow(copies, action, tag):
    with copies.lock(blocking=action != "start"):
        if action == "start":
            start_shadow(copies, tag)
            return
        run = copies.existing()
        if not run:
            print("Shadow is stopped")
        elif action == "stop":
            cleanup(run)
            print("Shadow stopped")
        else:
            session = json.loads((run / "session.json").read_text())
            shadow_limit(run, session, report=True)
            print(f"api={health(session['api'])} web={health(session['web'])}")
            shadow_address(session["api"])
            print(command("docker", "stats", "--no-stream", "--format",
                          "{{.Name}} {{.MemUsage}} cpu={{.CPUPerc}}", session["api"], session["web"]))


def interrupted(signum, _frame):
    raise Failure(f"Interrupted by signal {signum}")


def main():
    signal.signal(signal.SIGTERM, interrupted)
    signal.signal(signal.SIGHUP, interrupted)
    os.umask(0o077)
    mode, *args = sys.argv[1:]
    if mode == "watch":
        stack, state, generation = args
        watch(Copies(Path(stack), Path(state), "shadow"), generation)
    else:
        copies = Copies(Path(os.getenv("STUDIO_STACK_DIR", TOOLS)),
                        Path(os.getenv("STUDIO_COPY_STATE_DIR", "/var/lib/agent-studio-copies")), mode)
        if mode == "preflight":
            return preflight(copies, *args)
        shadow(copies, *args)
    return 0


if __name__ == "__main__":
    invoked = time.monotonic()
    try:
        sys.exit(main())
    except (Failure, OSError, KeyError, ValueError, KeyboardInterrupt) as exc:
        # Parser failures must never reveal a value taken from the Compose environment.
        print(str(exc) if isinstance(exc, Failure) else f"Failed to read host configuration or state ({type(exc).__name__})",
              file=sys.stderr, flush=True)
        if sys.argv[1] == "preflight":
            print(f"result=unhealthy seconds={int(time.monotonic() - invoked)}", flush=True)
        sys.exit(exc.code if isinstance(exc, Failure) else 3)
