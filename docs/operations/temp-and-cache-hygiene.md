# Temp and cache hygiene on a runner host

Who owns the bytes an agent host writes outside its workspaces, what bounds
them, and the one supported way to reset each of them.

The shared `/tmp` on an agent host is **not** a cache root and not a workspace
root. Everything the product writes there must be either short-lived and
self-removing, or it must live somewhere product-owned instead.

## The finding this page exists for (AGT-2858, 17.09.2026)

The review host's `/tmp` held **19,313 entries and 42 GB**; 15,959 entries were
older than one day, all owned by the runner service account. Every review runs
the full backend suite, so the leak compounded by roughly a hundred directories
per review.

| Prefix | Older than a day / all | Origin |
|---|---|---|
| `clr-debug-pipe*`, `dotnet-diagnostic*` | 7,488 / 8,752 | IPC sockets of killed or finished dotnet processes (test hosts, MSBuild nodes) |
| `bus-bridge-fake-job*` | 804 / 851 | `backend.Tests` bus-bridge fixtures |
| `atp-crash*` | 776 / 846 | `backend.Tests` crash and recovery fixtures |
| `atp-pickup-atomicity*` | 333 / 459 | `backend.Tests` pickup-atomicity fixtures |
| `MSBuild*` | 710 / 814 | MSBuild node temp |
| `studio-task-artifacts*` | 280 / 343 | `task-server.Tests` artifact fixtures |
| `agent-studio-m1-pilot-cache*` | 3 directories, **36 GB** | The M1 pilot preparation cache plus two hand-made operator copies |

Two product gaps, both closed by AGT-2858 and described below: test fixtures
that nothing removed, and a preparation cache with no owner, no bound and no
supported reset.

> This is hygiene, not isolation. The `PrivateTmp=no` decision from AGT-2750
> stands: detached workers outlive a daemon restart and need a shared `/tmp`
> that no unit restart unmounts underneath them
> (`runner/DetachedWorkerTmpMountGuard.cs`). Do not "fix" temp growth by turning
> `PrivateTmp` back on.

## 1. Test suites: one suite root per test host process

Every .NET test project installs a module initializer
(`<project>/TempRootBootstrap.cs`) that calls
`AgentStudio.TestSupport.TestTempRoot.Redirect()` before any test type is
touched. It points `TMPDIR`, `TMP` and `TEMP` - the variables
`Path.GetTempPath()` reads on Linux and Windows - at

```text
<base>/ats-<8 hex>          # /tmp/ats-3f9c21ab
```

so the several hundred fixtures that build their paths from
`Path.GetTempPath()` land inside that directory instead of beside it. The
directory is removed at process exit, and any suite root a *killed* host left
behind is swept by the next test process once it is older than six hours - by
name shape (`ats-` plus eight hex characters), so nothing else in the base
directory is ever a candidate. A crashed run therefore costs one entry under
`/tmp`, not a hundred.

### The suite root is bounded in length

A redirect that *lengthens* the temp root is not free: paths are a budget.
Windows resolves a bare path against MAX_PATH (260), and a fixture that builds a
repository, clones it and writes a long ref spends that budget below the temp
root. The first cut of this work nested the suite root inside a review
attempt's own temp directory, produced roots of ~190 characters, and broke
`RemoteCommitAttributionGuardTests`, which sizes a branch name so that the
product's fetch crosses MAX_PATH while the fixture's own refs stay below it.

`TempRootLengthPolicy` therefore bounds the redirected root:

| Platform | Bound | Why |
|---|---|---|
| Linux, macOS | **60 characters** | Leaves the ~120 characters a repository-building fixture needs inside MAX_PATH, and `/tmp` fits with room to spare. A macOS `$TMPDIR` (`/var/folders/...`, ~48 characters) does not, so the suite root lands in `/tmp` there |
| Windows | **64 characters** | `%LOCALAPPDATA%\Temp` is 33 to 40 characters on a normal profile, so a tighter bound would fail every Windows suite instead of bounding anything. Still leaves ~195 of the 260 MAX_PATH characters to the tests |

The base directory is the first of these whose suite root fits the bound:

| Order | Base | Note |
|---|---|---|
| 1 | `$AGENT_STUDIO_TEST_TEMP_ROOT` | Operator override, e.g. a short attempt-owned directory |
| 2 | The inherited temp root (`$TMPDIR`) | Preferred while it fits: under a review attempt this is the attempt's own fenced temp directory |
| 3 | `/tmp`, or `%LOCALAPPDATA%\Temp` on Windows | Last resort |

Under a review attempt the inherited root is
`$RUNNER_REVIEW_WORKDIR/<attempt>/tmp`, roughly 80 characters, so the suite root
falls back to `/tmp/ats-<8 hex>`: one self-deleting directory per test host
instead of a hundred fixture directories, swept after six hours if the host is
killed. Point `AGENT_STUDIO_TEST_TEMP_ROOT` at a short attempt-owned directory
to keep even that inside the attempt.

`TempRootHygieneGuardTests.Redirected_temp_root_stays_within_the_documented_length_bound`
fails the suite if no candidate fits, rather than letting a silently long root
break path-length-sensitive tests somewhere else.

Individual fixtures additionally remove their own directory
(`TempWorkspace`, or `TestTempRoot.TryDelete` in a hand-written `Dispose`),
which clears the read-only attribute Git puts on `.git/objects` and retries a
handle Windows has not released yet.

Two checks keep this in place, both in `backend.Tests`:

- `TempRootHygieneGuardTests` asserts the redirect is active, that every listed
  test project still carries its module initializer, and that the shared temp
  root gained no entry with a known fixture prefix while the suite ran.
- `scripts/measure-temp-residue.sh` takes the same count from outside the
  process, which is the only way to see what the .NET runtime itself created
  (`clr-debug-pipe*`, `dotnet-diagnostic*`) before any managed code ran.

```bash
scripts/measure-temp-residue.sh --label run1 --out residue.jsonl -- \
  dotnet test backend.Tests/OrchestratorApi.Tests.csproj
```

**Override.** `AGENT_STUDIO_TEST_TEMP_ROOT` moves the base directory. A host
that already fences its verify commands can point it at the attempt-owned temp
path so not even the suite root touches the shared root - provided that path
leaves the suite root inside the bound above (base plus 13 characters).

**What the redirect cannot move.** The runtime creates its own diagnostic
sockets (`clr-debug-pipe-<pid>-*`, `dotnet-diagnostic-<pid>-*`) while the
process starts, before any managed code runs, so they follow the `TMPDIR` the
test host was *launched* with. A review attempt already launches its commands
with the attempt temp path, so they land there and go with the attempt. Outside
a review - an operator or agent running the suite by hand on a shared host -
export a temp root first, otherwise each killed test host leaves its socket
behind in `/tmp`:

```bash
export TMPDIR="$PWD/.tmp"; mkdir -p "$TMPDIR"
dotnet test --filter 'Category!=MachineBound'
```

The second case is a child process that **outlives** the test host it was
spawned from - a reusable MSBuild node (`/nodeReuse:true`) is the usual one. It
inherited the suite root as its `TMPDIR`, so it recreates its sockets there
after the host already deleted the directory on its way out. The result is one
`ats-*` entry that reappears after the run and is then reclaimed by the
six-hour sweep. A verify command avoids it up front:
`MSBUILDDISABLENODEREUSE=1` and `-nodeReuse:false` are already part of the
review attempt environment and of the frozen plan.

## 2. Review attempts: the executor empties its own temp

`RemoteReviewWorkspace` roots every writable path of an attempt under the
attempt directory and points `TMPDIR`/`TMP`/`TEMP`, `DOTNET_CLI_HOME`,
`XDG_CACHE_HOME`, the package caches and `MSBUILDDEBUGPATH` at it:

```text
$RUNNER_REVIEW_WORKDIR/<attempt>/tmp
```

Nothing a verify command writes reaches the shared `/tmp` - including the
runtime's own diagnostic sockets, which follow `TMPDIR` on Linux.

Since AGT-2858 the residue is also bounded *inside* the attempt: after each
review command's process tree is gone, `ReviewTempResidue` empties that
command's temp directory and keeps the directory itself (it is the value the
next command inherits). A frozen plan runs preparation, build, several test
commands and the semantic aspects one after another, so without this they all
accumulated until the attempt workspace was finally deleted. The journal line is

```text
review-temp-purged step=<stepId> removed=<n> retained=<n>
```

The purge is fenced to paths strictly below the attempt root, so an attempt can
never empty a host-shared or foreign temp directory.

Whole attempt workspaces are removed after report acceptance; inactive remnants
older than 72 hours are swept hourly (`ReviewWorkspaceRetention`).

## 3. Repository preparation cache: product-owned, bounded, resettable

The prepare cache holds the restored npm, NuGet and Playwright trees that make a
second preparation of an unchanged repository a cache hit. It is **not** in a
temp directory.

**Where it lives.** A coding run uses the runner's own cache root,
`$RUNNER_WORKDIR/<project>/caches/preparation` (`GitWorkspace.PreparationCachePath`).
Anything that has no runner workspace - the M1 pilot preparation, local
experiments - resolves `ProjectPreparationPaths.ProjectCacheRoot(<project>)`:

| Order | Location | When |
|---|---|---|
| 1 | `$AGENT_STUDIO_CACHE_ROOT/<project>` | The operator set the variable |
| 2 | `/var/lib/agent-runner/cache/<project>` | A service-managed Linux host has the directory and the service account can write it |
| 3 | `$LOCALAPPDATA`/`~/.local/share` + `/agent-studio/cache/<project>` | Otherwise |

The temp root is deliberately not a candidate. A cache there has no owner, no
bound, and is a legitimate target for every `/tmp` sweep on the host - which is
exactly how the pilot cache reached 36 GB and then had to be deleted by hand.

**What bounds it.** Every preparation sweeps the project's published entries
(`ProjectPreparationCachePolicy`):

| Bound | Default | Override |
|---|---|---|
| Age since last use | 30 days | `AGENT_STUDIO_PREPARE_CACHE_MAX_AGE_DAYS` |
| Total size per project | 20 GiB | `AGENT_STUDIO_PREPARE_CACHE_MAX_GIB` |

A cache *hit* stamps the entry, so age measures last use, not publication: a
repository that keeps hitting the same key never expires. Over the size bound,
the least recently used entries go until the cache fits. The entries the running
preparation just restored from or published are never evicted. An unparsable or
non-positive override is ignored rather than removing the bound. The journal
lines are `project-prepare cache evicted ...` and `project-prepare cache swept
...`.

Empty blocks are not published. A pre-fix entry with `sizeBytes: 0` and an
empty `content/` directory is treated as a compatibility miss and replaced
when the block produces files. A genuinely incomplete entry is atomically
renamed into `.quarantine/` under a per-entry lock, logged as
`state=evicted-incomplete`, and treated as a miss; it never fails a preparation.
The existing sweep removes quarantined entries. The lock ensures one run cannot
rename an entry while another run is copying it into its private run root.

**Where the bytes actually are.** The published entries are the smaller half.
Each preparation also gets a private working copy of every block it uses, under
`.runs/<runId>/`, and the gate or coding run releases it when its last command
finishes. A run that is killed never gets to, so those copies accumulate. On
this repository's own runner cache that was 1.3 GB of published entries under
16 GB of unreleased run folders. They are reclaimed once they are older than 24
hours - long enough that a live run is never touched - and the reclamation now
runs both before and after every preparation, with the journal line
`project-prepare cache run-root released path=... ageHours=... bytes=...`. The
report mode of the reset script prints both numbers:

```text
size           17G
block          npm          entries=1 size=133M
block          nuget        entries=1 size=473M
block          playwright   entries=1 size=631M
run folders    13 size=16G
```

**How to reset it.** One script, with a report-only default:

```bash
# Report what is cached for a project, safe on a busy host
scripts/reset-preparation-cache.sh --project agent-studio-m1-pilot

# A runner coding cache has the other layout, so address it directly
scripts/reset-preparation-cache.sh \
  --project-cache "$RUNNER_WORKDIR/<project>/caches/preparation"

# Drop everything, costing the next preparation one cold restore
scripts/reset-preparation-cache.sh --project agent-studio-m1-pilot --reset

# Drop one technology block only
scripts/reset-preparation-cache.sh --project agent-studio-m1-pilot --reset-block nuget
```

It never touches task storage, worktrees or review workspaces. Do not make ad
hoc copies of a cache directory: on the review host the cache plus two hand-made
`-before-*` copies of it added up to the 36 GB.

## When `/tmp` fills up anyway

1. Measure before deleting, so the next card has a prefix breakdown:

   ```bash
   find /tmp -maxdepth 1 -mindepth 1 -user "$(id -un)" -mtime +1 \
     | sed 's#.*/##; s/[0-9a-f]\{8,\}.*//' | sort | uniq -c | sort -rn | head -20
   du -sh /tmp
   ```

2. Remove only what is provably residue - entries owned by the service account
   and untouched for more than a day:

   ```bash
   sudo -u agent find /tmp -maxdepth 1 -mindepth 1 -user agent -mtime +1 -exec rm -rf {} +
   ```

3. If a prefix in the breakdown is not on this page, it is a new leak: file a
   card with the prefix, the count and the owning component rather than adding a
   cron job.

## Related

- [Linux runner host runbook](./setup/linux-runner-host.md) - service setup,
  `RUNNER_REVIEW_WORKDIR`, build-server isolation, workspace retention.
- [Runner domain](../system/domains/runner.md) - detached workers and the
  `/tmp` mount guard behind the `PrivateTmp=no` decision.
