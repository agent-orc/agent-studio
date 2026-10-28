---
id: stale-git-lock
title: "Evidence flushes or integrations fail for hours on a git lock left by a dead git process"
status: fixed
first-seen: 2026-09-27T15:32:00Z
last-seen: 2026-10-28T09:00:00Z
severity: blocker
category: runtime
tags: [git, index-lock, workspace, evidence, integration-worktree, pipeline-health, windows]
affects:
  - backend/Features/Git/StaleLocks
  - backend/Features/Pipeline/WorkspaceArtifactCommitService.cs
  - backend/Features/Pipeline/WorkspaceEvidenceWorker.cs
  - backend/Features/Pipeline/EvidenceFlushStallTracker.cs
  - backend/Features/Pipeline/PipelineHealthService.cs
  - backend/Features/Git/IntegrationWorktreeProvider.cs
  - backend/Features/Pipeline/BuildTestGateRunner.cs
  - backend/Features/Tasks/TaskTransitionService.cs
related-tasks: [AGT-3000, AGT-3035, AGT-3337]
related-adrs: []
---

# stale-git-lock

**Symptom.** The Task Server log repeats, for hours:

```text
workspace-evidence-flush-failed repo=<workspace> error=git-add: fatal: Unable to create '<workspace>/.git/index.lock': File exists.
```

or an integration slot reports `could not be prepared` on every merge. No
evidence commit reaches the workspace repository.

**What the lock means.** Git creates `<git-dir>/index.lock` (or
`HEAD.lock`, `packed-refs.lock`, `refs/**/<name>.lock`) before it writes the
index or a ref, and renames or deletes it when the write finishes. While the
file exists, every other git write in that repository refuses to start. If the
git process dies mid-write (killed, crashed, host restart), the lock stays
behind, often as a 0-byte file, and nothing removes it. For a linked worktree
the index lock lives in the shared repository under
`.git/worktrees/<name>/index.lock`.

On 2026-09-27 a dead git left a 0-byte `index.lock` in
`agent-taskboard-workspace`: 36 hours of failed flushes (65 warnings in one
night) and no alarm. A second lock, 92 hours old, in
`agent-taskboard-dev/.git/worktrees/agent-taskboard-dev-7cad6883/` made the Temp
integration slot fail every merge. An operator removed both by hand.

**What the server does now.** Before a git write on a repository it owns, the
Task Server runs `GitStaleLockGuard`:

| Where | Locks checked |
|---|---|
| Workspace repository (evidence batches, run-boundary commits, hourly tracked sweep) | `index.lock`, `HEAD.lock`, `packed-refs.lock`, `refs/**/*.lock` |
| Integration worktree (before every reset for a merge) | the worktree's own `index.lock` and `HEAD.lock`, plus the shared ref locks |
| Build/test gate (before fetch and `worktree add`) | shared ref locks: `packed-refs.lock`, `refs/**/*.lock`. The project checkout's own index is not the server's. |
| Build/test gate worktree (before checkout, verification, and cleanup) | the gate worktree's private `index.lock` and `HEAD.lock`, plus shared ref locks. |

For each lock it decides (pure policy, `GitStaleLockPolicy`):

1. **Younger than the threshold** (default 10 minutes): a live writer is
   plausible. Keep it.
2. **Older, and a git process may hold the lock**: keep it. First exclude
   processes started after the lock's last write time and
   `git fsmonitor--daemon` processes, which do not take this lock. Start times come
   from `Win32_Process.CreationDate` on Windows and `/proc/<pid>/stat` on Linux.
   A missing start time cannot rule out a process. For the remaining processes,
   holding means a
   running git child of this server whose working directory is inside the
   repository, or an OS process: on Linux any `git*` process whose
   `/proc/<pid>/cwd` or command line points into the repository, on Windows any
   `git*.exe` whose command line names the path. "The repository" covers
   every working tree on the same ref store: the main checkout and all of its
   linked worktrees (read from `.git/worktrees/*/gitdir`). A `git update-ref`
   started in the project checkout can hold a shared ref lock that the gate
   or integration worktree would otherwise trip over.
3. **Older, and the process list, a still-present Linux `/proc` entry, a
   git process's working directory, or a linked worktree registration cannot
   be read**: keep it. An external
   Windows `git.exe add` may hold the lock without naming the repository in
   its command line; unresolved ownership is never guessed for a possible
   owner. The Windows
   process query also returns unknown after 15
   seconds, including when its output pipe stays open. An entry proven to have
   exited during the Linux scan is skipped.
4. **Older, and no git process holds it**: confirm the lock is still the same file seen before the process check, then delete it and log

   ```text
   git-stale-lock-cleared repo=<repo> lock=<path> age=36h0m
   ```

   then continue with the write.

If any lock is kept, the guard waits once (default 1 second), checks whether it
still exists without deleting it during the same write attempt,
logs `git-lock-kept repo=... lock=... age=... reason=young|owned|owner-unknown`
for every lock still there, and lets the write proceed. The write then fails
exactly as before (the existing short index-lock retry still applies). The next
independent write evaluates the remaining lock again.

**Alarm.** If an evidence flush keeps failing for more than 15 minutes without
a success in between, the Task Server raises the pipeline health alarm
`kind=evidence-flush-stalled` with the repository and the last error:

- in the project's **Pipeline** view, the *Pipeline health* block turns to
  *Attention needed* and shows a *Workspace evidence flush* row with the
  repository and the git error;
- in the orchestrator feed as a `pipeline-health` alert (repeated at most once
  per hour while it lasts);
- in the log as
  `pipeline_health_alarm kind=evidence-flush-stalled project=<name> ...`.

The alarm is raised for every project whose watch path lives in the stalled
repository, and clears on the next successful flush
(`pipeline_health_alarm_cleared kind=evidence-flush-stalled`).

**Configuration.** `GitStaleLocks:ThresholdMinutes` (default 10, 1 to 1440)
and `GitStaleLocks:WaitMilliseconds` (default 1000, 0 to 30000). The 15-minute
stall threshold is a code convention, not a setting.

**Check.** If the alarm fires but no `git-stale-lock-cleared` line follows,
look for `git-lock-kept`. `reason=owned` names a git process that really is
running in the repository: find it before touching anything.

```bash
# Linux
for p in /proc/[0-9]*; do c=$(cat $p/comm 2>/dev/null); case $c in git*) echo "${p#/proc/} $c $(readlink $p/cwd)";; esac; done
```

```powershell
# Windows
Get-CimInstance Win32_Process -Filter "Name LIKE 'git%'" | Select-Object ProcessId,CommandLine
```

`reason=owner-unknown` means the process inventory could not rule out an owner
(for example, an unreadable Linux `/proc` entry or the Windows PowerShell
`Win32_Process` query). Only when no git process runs in the
repository, remove the lock by hand:

```bash
rm <git-dir>/index.lock
```

The next flush commits everything that was pending: evidence batches stage the
whole project folders, so nothing written during the stall is lost.

**Limits.** Windows does not expose another process's working directory in
the process inventory. When a running `git.exe` has no repository path in its
command line, the server keeps the lock because ownership is unknown. After
that process exits, the next write can clear an aged lock.

## Ref locks left by a killed fetch (AGT-3337)

**Symptom.** The backend log shows, every ~33 seconds:

```text
Fetch from "origin" at "<repo>" failed (continuing offline): "git operation timed out after 30 seconds"
```

and `.git/refs/**` fills with `*.lock` files. Later fetches or pushes fail
with `cannot lock ref`. On 2026-10-28 an empty
`refs/remotes/origin/develop.lock` blocked every update of origin/develop for
90 minutes, so completion moves were refused with `integration-dead-end`
although the deliveries were on develop.

**Cause.** The completed-push backstop ran `git fetch --prune origin` for every
completed card. On a checkout with 6,604 remote refs that fetch never finished
within the 30-second git timeout. The timeout kills the git process tree, and a
killed fetch does not release its ref locks.

**What the server does now.**

- The backstop and the completed-push worker skip a card whose integration
  verdict is `integrated` without any git call. Other cards fetch only the
  integration branch refspec (`+refs/heads/<branch>:refs/remotes/origin/<branch>`),
  at most once per repository per backstop cycle. The "already on the remote"
  shortcut reads `origin/<integration branch>`, not `origin/main`.
- `GitRefLockJanitor` runs before every fetch through `GitService.Fetch` or the
  integration fetch, after a fetch timeout, and after a fetch that failed on a
  ref lock. It removes `packed-refs.lock` and `refs/**/*.lock` files that are
  older than `GitStaleLocks:RefJanitorThresholdMinutes` (default 5) when no git
  process holds them (same ownership probe as above). In the same pass it
  prunes unlocked `.git/worktrees/<name>` entries whose working tree no longer
  exists. It also removes entries locked as `initializing` when their `gitdir`
  target is missing and the entry, lock, and pointer are older than the threshold.
- Each removed worktree registration emits an operations event
  `git-stale-lock-cleared` (kind `intervention`) naming that entry. Cleared ref
  locks are reported together in one event.
  A timeout or `cannot lock ref` failure that leaves locks in place emits
  `git-ref-lock-blocked`, at most once per repository per
  `GitStaleLocks:RefLockReportWindowMinutes` (default 15).
