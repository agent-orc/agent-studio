# Workspace Repository Lifecycle

The TaskRepository is a platform-owned Git repository. It stores durable task
authority and evidence, while runtime streams remain local files outside Git.
The backend owns committing, push retry, drift sweeps, and object maintenance.

The repository has two roles, selected by `TaskArtifacts:Source` in the backend
configuration (pipeline delivery target, section 05 "Task-folder artefacts",
migration M3 and M6, decision D9 A, AGT-3320):

- **Authority** (`workspace`, the default, and `task-server-shadow`): the
  backend writes task folders and commits and pushes them as described in the
  sections below.
- **Export** (`task-server`): the Task Server holds task files and results;
  its periodic export job writes the task folders into a clone of this
  repository for audit and offline reading. The backend makes no commit and no
  push into the TaskRepository, and nothing reads the export in a request
  path. See [Workspace repository as export](#workspace-repository-as-export).

## Path classification

| Path family | Class | Git behavior |
|---|---|---|
| `projects/**` task state, receipts, reviews, prompts, and small reports | Durable authority or evidence | Committed at run boundaries or in debounced transition batches. |
| `logs/bus/**` | Runtime projection | Removed from the index and ignored through `.git/info/exclude`. The bus remains available to runtime readers and follows its own rotation policy. |
| `.metadata/attempt-authority.json`, `.bak`, `.journal`, `.journal.bak`, and `attempt-authority.archive-*.json` | Runtime authority store | Never staged. The live snapshot keeps 128 unprotected terminal attempts younger than 24 hours by default. Compaction archives at most once per hour into that hour's shard (`attempt-authority.archive-yyyy-MM-dd-HH.json`), or earlier when more than one retention window of archivable attempts piles up, merging into the same shard. History reads use an in-memory task-key and delivery-ref index, so they load only the shards that hold the requested task; a task without archived attempts loads none. A background pass builds the index once after startup, so no card-detail read pays for it. Mutations append flushed deltas to the journal; periodic checkpoints replace the snapshot. The previous journal segment can recover the backup snapshot. |
| `*.tmp`, `*.bak`, caches, `.runtime/**`, attachments, and intermediate results selected by `WorkspaceEvidence:ExcludeGlobs` | Runtime or heavy working data | Excluded from transition evidence commits. |

On every maintenance pass, the backend installs the two runtime ignore patterns
in the repository-local exclude file and runs `git rm --cached` for previously
tracked bus and attempt-authority files. The next tracked sweep commits those
index removals. This is local repository policy and does not modify a source
project's `.gitignore`.

Every staging path performs a file-size check first. A single file larger than
`WorkspaceArtifacts:MaximumFileBytes` is reset from the index, excluded from
the add and commit pathspecs, and reported as
`workspace-artifact-file-refused`. The default is 52,428,800 bytes (50 MiB).

## Commit and push cadence

Normal task transitions use the existing evidence batcher: a 15-second quiet
window with a 60-second maximum delay. Run-boundary commits remain immediate.
An additional repository-wide sweep runs at boot and once per hour. It commits
leftover tracked modifications as:

```text
chore(workspace): sweep tracked workspace drift
```

The sweep never adds untracked files and excludes runtime-only paths. Therefore
a tracked durable path cannot remain behind the working tree for more than one
maintenance interval under normal operation.

Each successful platform commit enters `WorkspaceArtifactPushQueue`. Pushes are
single-flight per repository. On startup, the worker also measures and pushes
the configured TaskRepository even when no new commit has entered the queue, so
a restart cannot strand an existing backlog. The first attempt uses the normal 30-second
budget. If it times out, the next attempts use the configurable 600-second
catch-up budget, so a large pack can finish instead of restarting every 30
seconds. Three attempts retain bounded exponential backoff.

Before pushing, the worker measures `origin/<branch>..HEAD` with `rev-list`.
It logs `workspace-artifact-push-backlog` at 50 commits or an estimated 512 MiB.
One push of `HEAD` publishes the entire reachable backlog, including a
2,000-commit backlog. A spent retry budget writes the typed supervisor advisory
`workspace-repository-push-failed`, naming the repository, branch, job, and
ahead count. It also emits the existing managed-repository push failure event.

## Stale git locks and the stall alarm

A git process that dies mid-write leaves `.git/index.lock` (or a ref lock)
behind, and every later evidence flush then fails on "File exists". Before each
workspace commit the server clears a lock that is older than 10 minutes and held
by no git process (`git-stale-lock-cleared repo=... age=...`), and a flush
failure that repeats for more than 15 minutes raises the
`evidence-flush-stalled` pipeline health alarm. See
[stale-git-lock](common-problems/stale-git-lock/README.md) (AGT-3000).

## Object maintenance

`WorkspaceRepositoryMaintenanceWorker` is the scheduler. It runs at boot and
every 60 minutes, but waits for `ILoadThrottleGate` before repository setup and
before object work. It does not install an operating-system timer.

Each pass configures these local values:

```text
gc.auto=5000
maintenance.strategy=incremental
core.fsmonitor=true          # native Windows only, configurable
```

The pass runs the `loose-objects` and `incremental-repack` maintenance tasks.
A new repository with no pack gets one local-only bootstrap repack first,
because Git cannot create a multi-pack index without a pack. `core.fsmonitor`
is enabled by default on native Windows, where the workspace has many paths. It
is left unchanged on other platforms. Set `WorkspaceRepository:CoreFsMonitor`
to `false` if a Windows filesystem or Git build proves incompatible.

Defaults are in `backend/appsettings.json` under `WorkspaceArtifacts` and
`WorkspaceRepository`. Local overrides belong in the normal gitignored local
configuration.

## Manual backlog recovery

The worker should normally recover without intervention. If the final advisory
persists, stop workspace writers or drain the service, then inspect the exact
repository named by the advisory:

```bash
git fetch origin main
git rev-list --count origin/main..HEAD
git rev-list --disk-usage --objects origin/main..HEAD
git status --short
git count-objects -v
```

Confirm that no `attempt-authority*` path is staged. To publish the complete
reachable backlog without the backend process timeout:

```bash
git push origin HEAD:refs/heads/main
```

To repair object storage before retrying:

```bash
git config --local gc.auto 5000
git config --local maintenance.strategy incremental
git maintenance run --task=loose-objects --task=incremental-repack
```

If Git reports that there are no pack files to index, bootstrap once and repeat
the maintenance command:

```bash
git repack -d -l
git maintenance run --task=loose-objects --task=incremental-repack
```

Resume the service only after `git status --short` contains no unexpected
durable tracked drift and `origin/main..HEAD` is empty.

## Workspace repository as export

### What moves where

| Concern | `workspace` | `task-server-shadow` | `task-server` |
|---|---|---|---|
| Task files and results | Task folders, committed by the evidence batcher (15 s quiet window, 60 s maximum) and at run boundaries, pushed by `WorkspaceArtifactPushWorker` | As `workspace`; `TaskFileMirrorWorker` also copies them into the Task Server | Task Server `task_files` and `task_file_revisions` (mirrored until the write cutover, written directly after it) |
| Release card attribution (`DeployService` release creation) | Archive-inclusive task index and each folder's `integration-verification.json` | Same | One Task Server query, `GET /api/v1/projects/{projectId}/integrated-cards`; no task folder is read and there is no fallback to the scan |
| Workspace repository commits and pushes from the backend | Yes | Yes | None: run-boundary, evidence and sweep commits return `workspace-repository-export`; push requests for the TaskRepository are dropped |
| Workspace repository content | Authority | Authority | Export written by the Task Server export job |

Rollback is the setting. A value the backend does not know falls back to
`workspace`. The setting is read per call, so a configuration reload switches
without a restart; when it changes from `task-server` back to an authority
mode, the evidence worker runs its catch-up commit once.

### Task Server storage

Every task file row and revision carries `sha256`, `size_bytes` and
`object_stored`. Content of at most `TaskServer:ContentInlineLimitBytes`
(default 1,048,576 bytes) stays inline in SQLite; larger content of task files
and run artifacts is written once to the content object store,
`TaskServer:ContentObjectStorePath` (default `<DataDirectory>/objects`), at
`sha256/<aa>/<bb>/<sha256>`. Objects are immutable and verified by hash on
every read. Full backup sets (`task-server backup full`) copy every object the
snapshot references into `objects/` and restore them additively; the
five-minute SQLite snapshot does not include them, exactly as it does not
include cold payloads. An object written by a transaction that later rolled
back is unreferenced and harmless.

### Mirror before the write cutover

Until migration M3 the backend is still the write authority, so
`TaskFileMirrorWorker` copies task folders into the Task Server whenever
`TaskArtifacts:Source` is `task-server-shadow` or `task-server`. It runs off
the request path every `TaskArtifacts:Mirror:IntervalSeconds` (default 30),
sends hashes first to `PUT /api/v1/projects/{projectId}/task-files/workspace-import`
and content only for the paths the Task Server answers as needed, and spends
at most `TaskArtifacts:Mirror:MaxBytesPerCycle` (default 64 MiB) per cycle in
batches of `TaskArtifacts:Mirror:BatchBytes` (default 8 MiB). It mirrors task
state, evidence and `results/`; runtime files, `logs/cli-output.log*`, review
stdout logs, attachments (the Task Server keeps those in `task_attachments`)
and files above `TaskArtifacts:Mirror:MaxFileBytes` (default and maximum
16 MiB) stay out. A card the Task Server does not hold yet is answered
`task-missing` and retried; the board mirror creates it, so `Board:Source`
must be `task-server-shadow` or `task-server` as well
([board source cutover](board-source-cutover.md)). Archived cards are
re-checked only when their `task.json` changes.

### Export job

The Task Server export job is off unless `TaskServer:WorkspaceExportRoot`
names a clone of the workspace repository. Every
`TaskServer:WorkspaceExportIntervalMinutes` (default 15) it writes
`projects/<project>/tasks/<bucket>/<KEY>/` for every card, archive included:
`task.json` (the mirrored file, else the imported legacy payload, with id,
key, title, lane, timestamps and order from the Task Server row), `prompt.md`
from the card body when no task file holds it, every task file at its path,
and the newest content of each run artifact under `results/`. Pointer-only
legacy artifacts are counted in `workspace-export.json`, not copied. A file is
rewritten only when its bytes changed and a file under `projects/*/tasks/`
that no card holds any more is removed, so an unchanged store produces no
commit. The job stages only `projects/*/tasks/**` and `workspace-export.json`
(forced, so an ignore rule in the clone cannot drop exported folders), commits
as `agent-studio-export`, and with `TaskServer:WorkspaceExportPush = true`
pushes `HEAD` to `TaskServer:WorkspaceExportBranch` (default `main`) within
`TaskServer:WorkspaceExportPushTimeoutSeconds` (default 600). A failed or
hanging push leaves commits ahead; the next cycle's push publishes the whole
backlog. `GET /api/v1/workspace-export/status` (scope `operations:inspect`)
shows the last export, commit, push result and ahead count.

`task-server export --target <dir> [--commit]` runs one full export offline,
read-only on the store and without lease quarantine, so it may run beside a
live server.

Measured on agent-runner-01 on 27 October 2026 (AGT-3320,
`WorkspaceExportTests.Push_backlog_of_the_export_does_not_change_board_latency`,
300 cards, 400 board reads each): board read p50 2.46 ms and p95 4.31 ms with
the export idle, p50 2.49 ms and p95 4.12 ms while the export push of a
2,002-commit backlog hung on the remote until its timeout. The board reads
SQLite and never waits for the export.

### Cutover order

1. Board source in shadow or cutover mode, so every card exists in the Task
   Server ([board source cutover](board-source-cutover.md)).
2. `TaskArtifacts:Source = task-server-shadow`. Wait until mirror cycles log
   no deferred files and no `task-missing` rejection.
3. Clone the workspace repository for the export and set
   `TaskServer:WorkspaceExportRoot`; push to a separate branch such as
   `workspace-export` first. Enable the export only after step 2 has caught
   up: an export taken before the mirror holds a card's files removes them from
   the export (the rehearsal's first cycle removed 54 files that the next
   cycle restored).
4. `TaskArtifacts:Source = task-server`. From now on the backend neither
   commits nor pushes the TaskRepository; point the export at `main` when the
   export branch has been reviewed.

### Rollback

Before the write cutover the task folders are still the write authority, so
rollback loses nothing:

1. Stop the export job: clear `TaskServer:WorkspaceExportRoot` (or set
   `TaskServer:WorkspaceExportPush = false`) and restart the Task Server.
2. Set `TaskArtifacts:Source = workspace` (or `task-server-shadow` to keep the
   mirror). Release attribution reads the folders again, and the evidence
   worker commits the drift accumulated while the repository was an export.
3. If the export pushed to the branch the backend pushes, reconcile once in
   the TaskRepository, keeping the authority tree and the export history:

   ```bash
   git fetch origin
   git merge -s ours --no-edit origin/main
   git push origin HEAD:refs/heads/main
   ```

   With a separate export branch there is nothing to reconcile.
4. Verify `git status --short` is empty, `git rev-list --count origin/main..HEAD`
   is 0, and the inventory (`task-server inventory --source <TaskRepository>`)
   shows the same counts per project and lane as before.

After the write cutover the Task Server is the authority and the folders are
stale; roll back with the runbook's
[rollback in less than 15 minutes](remote-task-server-local-studio.md#rollback-in-less-than-15-minutes),
then write a fresh full export (`task-server export --target <empty clone> --commit`)
before the folders serve as authority again.

Rehearsed once on a copy on 27 October 2026 (AGT-3320): the ADR-0056 demo
workspace seeded into a temporary repository with a bare origin, imported with
`task-server inventory` and `import`, mirrored, exported by the export job of
a real Task Server process, and read back with `task-server inventory`: equal
counts per project and lane (22 cards) and 74 byte-identical task files; the
two files the mirror excludes by policy (a CLI log and an attachment) were the
only ones absent. Steps 1 to 3 took 108 ms on that copy; afterwards the
authority tree equalled `origin/main`, nothing was ahead or behind, and both
export commits stayed in history. Backend steps were run as the git commands
the evidence and push workers issue. The script and log are in the AGT-3320
task results.

