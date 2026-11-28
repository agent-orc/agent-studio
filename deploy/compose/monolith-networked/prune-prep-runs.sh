#!/bin/sh
set -eu
case "${1:-}" in
    --help|-h)
        cat <<'HELP'
Usage: RUNNER_WORKDIR=/path/to/runner-work prune-prep-runs.sh PROJECT
Remove immediate preparation/.runs copies older than 30 minutes whose IDs
are absent from host process environments. Run as root in the host PID namespace.
Print removed, remaining and filesystem use for the scheduler log.
HELP
        exit 0 ;;
    ''|.|..|*[!A-Za-z0-9._-]*) echo 'Expected one project directory name. See --help.' >&2; exit 2 ;;
esac
if [ "$#" != 1 ] || [ -z "${RUNNER_WORKDIR:-}" ]; then
    echo 'Set RUNNER_WORKDIR and pass one project.' >&2
    exit 2
fi
[ "$(id -u)" = 0 ] || { echo 'Root is required to inspect all host processes.' >&2; exit 2; }
exec python3 -I - "$RUNNER_WORKDIR" "$1" <<'PY'
from datetime import datetime, timezone
import fcntl
import os
from pathlib import Path
import re
import shutil
import stat
import sys
import time


def prune():
    work, project = sys.argv[1:]
    if not os.path.isabs(work):
        sys.exit("RUNNER_WORKDIR must be absolute")
    # Pin each component by fd and refuse symlinks, including .runs itself.
    flags = os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW
    fd = os.open(work, flags)
    try:
        for part in (project, "caches", "preparation", ".runs"):
            child = os.open(part, flags, dir_fd=fd)
            os.close(fd)
            fd = child
    except FileNotFoundError:
        print("Preparation copies absent")
        return
    fcntl.flock(fd, fcntl.LOCK_EX)
    cutoff = time.time() - 30 * 60
    candidates = []
    with os.scandir(fd) as entries:
        for entry in entries:
            if re.fullmatch("[0-9a-f]{32}", entry.name) and entry.is_dir(follow_symlinks=False):
                info = entry.stat(follow_symlinks=False)
                if info.st_mtime < cutoff:
                    candidates.append((entry.name, info))

    active = set()
    if candidates:
        # Compare the runner-relative suffix: a process in a work container
        # can see a different absolute path. Never log /proc contents.
        pattern = re.compile(rb"preparation/\.runs/([0-9a-f]{32})(?![0-9a-f])")
        proc = Path(os.getenv("STUDIO_PROC_ROOT", "/proc"))
        pids = [pid for pid in proc.iterdir() if pid.name.isdecimal()]
        if not pids:
            sys.exit("No visible host processes; keeping preparation copies")
        for pid in pids:
            try:
                active.update(pattern.findall((pid / "environ").read_bytes()))
            except ProcessLookupError:
                continue
            except FileNotFoundError:
                if pid.exists():
                    raise  # Missing environment in an incomplete proc mount.
    removed = 0
    for name, old in candidates:
        if name.encode() in active:
            continue
        try:
            current = os.stat(name, dir_fd=fd, follow_symlinks=False)
        except FileNotFoundError:
            continue
        if os.path.samestat(current, old) and stat.S_ISDIR(current.st_mode) and current.st_mtime < cutoff:
            shutil.rmtree(name, dir_fd=fd)
            removed += 1
    fs = os.fstatvfs(fd)
    used = fs.f_blocks - fs.f_bfree
    capacity = max(1, used + fs.f_bavail)
    percent = (100 * used + capacity - 1) // capacity
    stamp = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    print(f"{stamp} removed={removed} left={len(os.listdir(fd))} disk={percent}%")


try:
    prune()
except OSError as error:
    sys.exit(f"Preparation prune failed: {error.strerror}")
PY
