---
id: private-tmp-unmounted-by-daemon-restart
title: "Runner daemon restart unmounts PrivateTmp and breaks every surviving worker's build"
status: fixed
first-seen: 2026-09-07T01:10:00Z
last-seen: 2026-09-07T02:51:00Z
severity: blocker
category: runner
tags: [runner, systemd, restart, linux, build-gate, tests, filesystem, msbuild, nuget]
affects:
  - "agent-runner.service and agent-runner-review.service on managed Linux hosts"
  - "detached coding and review workers running a build or test across a restart"
related-tasks: [AGT-2750, AGT-2749, AGT-2753, AGT-2711]
related-adrs: []
---

# Runner daemon restart unmounts PrivateTmp and breaks every surviving worker's build

**What.** After a runner daemon restarts, workers that were mid-build keep
running but every `dotnet build`, `dotnet test`, and `dotnet restore` they issue
fails. The review grader turns the empty result into a new test failure and the
card lands in Human Review with "Delivery gate failed", even though the reviewed
change is fine.

Symptoms, in the order they were seen:

- `MSBUILD : error MSB1025: An internal failure occurred while running MSBuild.`
  followed by `System.Net.Sockets.SocketException (99): Cannot assign requested
  address`. The run exits 1 without executing a single test, and the report
  reads `build-tests: 1 new failures: <unparsed failure in verify-2>`.
- `mkdtemp("/tmp/.dotnet.XXXXXX") == nullptr; errno == ENOENT` during
  `dotnet restore` in the baseline preparation, which is classified as
  `ReviewInfra` and burns a full baseline retry.
- Stale `systemd-private-...-agent-runner*.service-*` directories accumulating
  under `/tmp`.

**Why.** The units combined `PrivateTmp` with `KillMode=process`. `KillMode` is
correct and deliberate: it keeps detached workers alive so the replacement
daemon can reattach them. `PrivateTmp`, however, is bound to the unit lifecycle.
On restart systemd unmounts the unit's private `/tmp` while the surviving
workers keep the deleted mount:

```
$ sudo grep ' /tmp ' /proc/<worker-pid>/mountinfo
... /tmp/systemd-private-<id>-agent-runner-review.service-WhdhIS/tmp//deleted /tmp ...
```

MSBuild creates its node pipe at the hard-coded host path `/tmp/MSBuild<pid>`
and NuGet creates its migrations mutex directory under the host `/tmp`; neither
honours `TMPDIR`. The per-slot `TMPDIR` the runner already sets therefore does
not cover them, and the worker is unusable for the rest of its run.

The blast radius is wider than the workers that were busy during the restart.
MSBuild starts reusable node daemons (`/nodemode:1 /nodeReuse:true`) that
outlive the build. After a restart these daemons sit in the old, deleted
namespace while a fresh build runs in the new one, and the fresh build still
tries to reach them through `/tmp/MSBuild<pid>`.

Measured on agent-runner-01 on 2026-09-07: nine live processes across four
different `systemd-private-...` namespaces, none of them adoptable and none of
them reaped. Four reusable MSBuild nodes (including pid 3491585, the one the
journal reported as "remains running after unit stopped"), two `dotnet restore`
processes hung since 04:03 and 04:07, one `git remote-https` transport from
2026-08-28, and two stale dev-stack node servers from 2026-08-31.

**Workaround.** Until the host carries the fix, restart a runner unit only while
its slots are idle, and treat any review report that names one of the three
signatures above as infrastructure regardless of its recorded outcome.

Check the current state of a host:

```bash
scripts/verify-runner-tmp-namespace.sh
```

It asserts `PrivateTmp=no` and `KillMode=process` on both units and lists every
live process still holding a deleted `/tmp` mount. Leftover MSBuild nodes found
this way should be terminated once their unit has no busy slot.

**Long-term.** Fixed in AGT-2750, in three layers:

1. The shipped units (`deploy/systemd/agent-host.service`,
   `scripts/remote-runner-onboard.sh`, `setup/NativeInstaller.cs`) and the
   versioned drop-in `deploy/agent-host/systemd/10-agent-runner-hardening.conf`
   set `PrivateTmp=false`. Worker isolation stays with the per-slot `TMPDIR` the
   runner owns. `scripts/harden-agent-runner-host.sh` verifies the effective
   value and removes any hand-made interim drop-in.
2. The runner's liveness proof reads `/proc/<pid>/mountinfo` during startup
   re-adoption. A worker whose `/tmp` mount root ends in `//deleted` is settled
   immediately instead of running for another hour before being graded.
3. The Review Executor and the Task Server classify the three signatures, when
   combined with no parsed test result at all, as
   `ReviewInfra / HostTempUnavailable` so the attempt retries instead of
   blocking the card.
4. Review commands run with `MSBUILDDISABLENODEREUSE=1`, so a review no longer
   leaves reusable nodes behind for the next namespace to trip over.
