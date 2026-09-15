---
id: windows-launcher-exit-during-compile
title: "api.sh start reports a crash on Windows while dotnet run is still compiling"
status: fixed
first-seen: 2026-09-15T06:02:00Z
last-seen: 2026-09-15T06:02:00Z
severity: major
category: cli
tags: [api-sh, start, windows, git-bash, dotnet-run, process-control, port, stable]
affects: [api.sh, docs/operations/setup/contributor-setup.md]
related-tasks: [AGT-2830]
related-adrs: []
---

# windows-launcher-exit-during-compile

**What.** `./start-stable.sh` on Windows printed `ERROR: the backend exited
before it started listening on port 5031` although the backend became
healthy roughly two minutes later. `start.sh` runs with `set -e`, so it
aborted before starting the frontend and every Stable start needed a manual
frontend start.

**Why.** `api.sh start` backgrounds `dotnet run` and tracks the PID `$!`
returns as "the launcher". On Windows Git Bash that PID belongs to a Git
Bash/MSYS process, not the native process that actually compiles the backend
and later owns the port. `dotnet run`'s implicit build can take roughly two
minutes on a cold cache, and the launcher PID can exit well before that
finishes. The old poll loop read "the launcher PID is gone and nothing is
listening yet" as a confirmed crash on the very first check after the
launcher exited, regardless of whether a build for this project was still
running. The 30-second poll budget made this worse: even a launcher that
stayed alive could not outlast a two-minute cold compile.

**Workaround.** None needed on a current checkout. Historically: ignore the
error, wait roughly two minutes, then confirm with `./api.sh status` and start
the frontend by hand.

**Long-term.** Fixed in AGT-2830. `api.sh start`'s poll budget is now
`API_START_TIMEOUT_SECS` (default 180s, was a hardcoded 30s), and it only
reports a confirmed failure (exit 1, "the backend exited before it started
listening") when neither the port nor any process building or running this
checkout's backend exists - checked via `launch_activity_pids`, a version of
the process match used for `stop`/`restart` that does not exclude MSBuild
worker command lines (that exclusion exists so `stop` never sweeps an
unrelated `dotnet test` run; it was also hiding the exact process still doing
the work during a `start` poll). If the poll budget runs out while such a
process is still active, `start` now exits `2` ("inconclusive, still
starting") instead of `1`, so an outer `start.sh` wrapper can tell that apart
from a confirmed crash and still bring up the frontend. See
[contributor-setup.md](../../setup/contributor-setup.md) for the wrapper
contract and
[`scripts/api-start-windows-launcher.test.sh`](../../../../scripts/api-start-windows-launcher.test.sh)
for the hermetic regression test, which reproduces the launcher-exit shape
without a real .NET build.
