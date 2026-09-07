# Workspace Repository Lifecycle

The TaskRepository is a platform-owned Git repository. It stores durable task
authority and evidence, while runtime streams remain local files outside Git.
The backend owns committing, push retry, drift sweeps, and object maintenance.

## Path classification

| Path family | Class | Git behavior |
|---|---|---|
| `projects/**` task state, receipts, reviews, prompts, and small reports | Durable authority or evidence | Committed at run boundaries or in debounced transition batches. |
| `logs/bus/**` | Runtime projection | Removed from the index and ignored through `.git/info/exclude`. The bus remains available to runtime readers and follows its own rotation policy. |
| `.metadata/attempt-authority.json`, `.bak` remnants, and `attempt-authority.archive-*.json` | Runtime authority store | Never staged. The live service keeps the newest 2,000 terminal attempts by default and compacts older terminal records into a daily archive. |
| `*.tmp`, caches, `.runtime/**`, attachments, and intermediate results selected by `WorkspaceEvidence:ExcludeGlobs` | Runtime or heavy working data | Excluded from transition evidence commits. |

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
