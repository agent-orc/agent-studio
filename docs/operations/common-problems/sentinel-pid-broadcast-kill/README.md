---
id: sentinel-pid-broadcast-kill
title: "A sentinel pid turns a worker kill into kill(-1, SIGKILL) and takes the whole host down"
status: resolved
first-seen: 2026-09-18T09:41:44Z
last-seen: 2026-09-18T09:41:44Z
severity: critical
category: runner
tags: [process-tree, kill, cgroup, dotnet, linux]
affects:
  - "every process of the runner service account"
related-tasks: [AGT-2870]
related-adrs: []
---

# A sentinel pid turns a worker kill into kill(-1, SIGKILL)

**What.** At 09:41:44 on 18.09.2026 every process owned by uid 1000 on the runner
host was killed in the same second: `user@1000.service`, `agent-runner.service`,
`agent-runner-review.service`, the operator's reverse-tunnel ssh session scope and
two coding workers, all `status=9/KILL`, across `system.slice`, `user.slice` and a
session scope. The fleet was down for six minutes and the daemons restart-looped
27 times because the tunnel was gone.

**Why.** `DurableAgentProcess.Attach` substituted `-1` for a slot whose worker
identity had not been persisted (`slot.ProcessId ?? -1`), and `Kill()` handed that
pid to the runtime. On Linux .NET the sentinel is not rejected anywhere:

- `Process.GetProcessById(pid)` gates on `ProcessManager.IsProcessRunning`, which
  is `kill(pid, 0) == 0 || errno == EPERM`. `kill(-1, 0)` means "signal 0 to every
  process I may signal" and returns 0, so `-1` is admitted. `0` and `1` are
  admitted for the same reason.
- `HasExited` is then false.
- `Process.Kill(entireProcessTree: true)` issues `kill(_processId, SIGSTOP)`,
  enumerates children, then `kill(_processId, SIGKILL)`. With `_processId == -1`
  that is a broadcast to the whole uid.

The sentinel had existed for a long time and was harmless while `Kill()` had one
caller, on a path where the slot always carried a recorded pid. It fired the first
time a second caller appeared (the operator-stop path, which by construction can
run before the worker identity exists).

**Signature.** Everything of one uid dying in the same second, across unrelated
slices and session scopes. No cgroup write can do that; a process-group kill
cannot cross `user.slice` and `system.slice`. Only `kill(-1, ...)`, or a kill
aimed at pid 0, or `kill -9 -$pgid` with an empty `$pgid`, produces it.

**Fix (AGT-2870).** `runner/ProcessSignalGuard.cs` is the only place a signal
decision becomes a syscall. It refuses, and logs
`[runner] signal-refused context=... rule=... reason="..."`, for:

- a pid below 2 (covers `-1`, `0`, `1` and `default(int)`),
- the daemon's own pid and its parent,
- a live pid whose `/proc` start time disagrees with the slot's
  `processStartedAtUtc` beyond the 2 s liveness tolerance (a recycled pid number),
- a process group below 2 or equal to the daemon's own group,
- a `cgroup.kill` target that is not a `worker-*` directory below the daemon's own
  delegated unit cgroup.

**Rule for new code.** Never pass a pid that came from persisted state, `/proc`,
`cgroup.procs`, or a `?? -1` fallback to `Process.Kill`, `kill(2)`, or a shell
`kill`. Route it through `ProcessSignalGuard`. A kill issued on a `Process` handle
this process just obtained from `Process.Start` is safe and needs no guard: the
handle holds the child, so it carries no sentinel and no pid-reuse window. In
tests, never signal a pid the test did not spawn itself.
