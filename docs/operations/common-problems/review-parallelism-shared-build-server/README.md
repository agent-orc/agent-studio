---
id: review-parallelism-shared-build-server
title: "Concurrent review workers deadlock on one host-shared .NET build server"
status: fixed
first-seen: 2026-09-15T00:00:00Z
last-seen: 2026-09-15T02:00:00Z
severity: blocker
category: runner
tags: [review, parallelism, msbuild, roslyn, build-server, deadlock, watchdog]
affects:
  - runner/RemoteReviewWorkspace.cs
  - runner/ReviewBuildServerIsolation.cs
  - runner/CommandProgressWatchdog.cs
  - /etc/agent-runner/runner-review.env
related-tasks: [AGT-2831, AGT-2750, AGT-2759]
related-adrs: []
---

# review-parallelism-shared-build-server

**What.** With `RUNNER_MAX_PARALLELISM=4` on `agent-runner-01-review`, concurrent
review workers deadlocked inside the backend test suite: `dotnet test` sat at
0.1 percent CPU for 87 minutes and held its review slot until the two-hour
command budget expired. The host was reduced to two review slots, which made
review the pipeline bottleneck with 20+ cards queued.

**Why.** `RemoteReviewWorkspace` roots every writable path of an attempt under
the attempt directory - `HOME`, `TMPDIR`, `NUGET_PACKAGES`, `DOTNET_CLI_HOME`,
`XDG_CACHE_HOME` - and the Task Server hands each attempt a private
`ResourceNamespace` and an eight-port window. None of that fences the .NET build
servers, because they do not live under `TMPDIR` on Linux:

- Roslyn's `VBCSCompiler` listens on `/tmp/<pipename>`, where `<pipename>` is a
  hash of the compiler directory and the user name. One host, one service
  account and one SDK therefore resolve to exactly one compiler server.
- Reusable MSBuild worker nodes listen on `/tmp/MSBuild<pid>` and outlive the
  build that started them (`/nodeReuse:true` is the `dotnet build` default).

Both servers are *inherited*: they keep the working directory and environment of
whichever attempt started them. When that attempt finishes, `CleanupAsync`
deletes its attempt root while the server keeps running against the deleted
tree. Later attempts still resolve the same host-global socket, connect, and
block on the handshake. A blocked client consumes no CPU, produces no output and
never exits, so only the command budget ends it.

Measured on a review-class host with `scripts/review-build-server-isolation-probe.sh`:
four concurrent attempt-shaped builds found 382 `MSBuild*` sockets and one
compiler socket under `/tmp`, **zero** under the attempts' own `TMPDIR`, and 27
reusable nodes adoptable by any attempt. A live compiler server was observed
serving the host while holding `PWD=/tmp/agentstudio-review-gates/<attempt>` from
an unrelated attempt.

Nothing noticed. Wall-clock timeouts cannot distinguish a deadlocked build from a
slow one, and no guard looked at whether the process tree was still doing work.

**Workaround (superseded).** Reduce `RUNNER_MAX_PARALLELISM` for the review role.
This trades throughput for safety and is no longer required.

**Long-term (applied, AGT-2831).**
1. `ReviewBuildServerIsolation` gives every attempt command - candidate,
   baseline and dependency preparation alike - a server-free build namespace:
   `MSBUILDDISABLENODEREUSE=1`, `DOTNET_CLI_USE_MSBUILD_SERVER=0`,
   `UseSharedCompilation=false` and an attempt-local `MSBUILDDEBUGPATH`. The
   compile then happens in-process in a node the attempt owns and dies with.
   The fence always wins over the immutable plan: a plan can never re-enable a
   host-shared server.
2. `CommandProgressWatchdog` samples the command's whole process tree from
   `/proc` and kills it when it fails to burn at least one percent of one core
   within `RUNNER_REVIEW_NO_CPU_PROGRESS_SECONDS` (default 900). The attempt is
   reported as `ReviewInfra/NoCpuProgress`, so a hang is retried as
   infrastructure instead of being graded as a product regression.
3. The report's environment evidence carries `buildServers=per-attempt` and the
   watchdog window, so a deadlocked host is visible in the evidence.

See [measures.md](./measures.md), [occurrences.md](./occurrences.md) and
[protocol.md](./protocol.md).
