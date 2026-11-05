# API stalls: measurement, causes and the stall alarm

What made the Studio API stop answering for tens of seconds on a real
workspace, what was changed, the numbers before and after, and how a stall
shows up now (AGT-3369).

## The finding (1 November 2026)

With `mem_limit: 8g` the API sat at about 6.5 GB and logged nothing for up to
61 seconds at a time. Users saw the backend go offline and online. The process
had about 1500 threads. Raising the limit to 20 GB was the immediate relief.

## Live operator evidence and follow-up (2 November 2026)

The operator sampled the live API on version `e00a40663` after the first
synthetic investigation. These measurements supersede the synthetic run's
cause attribution for the real incident. The live process was starved of
thread-pool workers; GC pauses were too short to explain the stalls.

| Measure | Live before this follow-up | After this follow-up |
|---|---:|---:|
| Health probe, once per second for 280 s | p50 < 1 ms; two stalls of 17 s and 20 s | Live replay pending |
| Thread-pool queue during a stall | +69 in 14 s; drained when worker count jumped by 13 | Live replay pending |
| Pool minimum / scan worker cap | 12 / 12 | 64 / 4 (configured limits) |
| Concurrent full workspace scans | Several; 3,190 folders took 6 to 16 s each | One (code limit) |
| `git-index-run` scans | 21 in 28 min; Agent Studio mean 10 s | One project folder per index run |
| GC pause | < 0.2 s per 2 s window | No new live measurement |
| RSS | About 3.5 GB | No new live measurement |
| Shared ext4 4 KB durable write | p50 54 ms; p99 0.93 s; max 1.17 s | No new live measurement |

`dotnet-stack` found 15 blocked pool workers. One waited for the PLINQ scan;
11 waited for `AttemptAuthorityService` or `ClientIdentityStore` monitors;
lock holders were doing filesystem calls from `RecordSeen`, runner
authentication, deployment column reads, and CLI log ingestion. This is why
limiting scans alone is insufficient. The pool minimum of 64 leaves at least
52 workers beyond a 12-processor scan or equivalent blocked group for health
and read requests. It is a reserve, not a concurrency target.

The first delivery added the detector, Operations panel, synthetic load harness,
30-minute before/after measurements, and allocation reductions described below.
This follow-up caps and serializes full scans, restricts Git index runs and
project-specific request paths to one watch path, raises the pool minimum,
coalesces identity and authentication last-seen writes onto a 30-second timer,
and verifies runner secrets outside the security store lock. Its source changes
have not yet been measured on the live production workspace. The synthetic
30-minute result below establishes the earlier implementation's result, not a
post-follow-up live result.

### Raw scan callers

| Caller | Trigger | Scope after follow-up |
|---|---|---|
| `TaskIndexCache` refresh | Watcher, mutation, safety TTL | Full scan; serialized |
| `GitStateIndexService.ReadTasks` | Repository index run | One watch path |
| `GitStateIndexService.SweepAsync` | Periodic safety sweep | Full scan once for all repositories |
| `AcceptedIntegrationBackstopHostedService` | Periodic reconciliation | Full scan; requires cross-project candidates |
| `HistoricalIntegrationVerificationSweep` | One-time migration | Full scan; requires terminal cards and duplicates |
| `TaskIntegrationRecordAddressResolver` | Request | Published archive-inclusive identity facts for scoped or unscoped ambiguity; one fresh target folder read before mutation |
| `OperatorCapacityEndpoints` | Request | Published archive-inclusive identity facts filtered by readable projects |
| `OperatorCandidateVerificationEndpoints` | Request | One watch path |
| `DeployService` failure-card reconciliation | Deployment event | One watch path |
| `PublishActionService.GetPanel` | Request | Cached archive-inclusive snapshot |
| `TaskScannerService` uncached fallback methods | Test or recovery | Full scan only without the production cache |

## How it was measured

The production API runs in a container this host cannot attach to, so the
measurement uses a synthetic workspace of the same size and the same load
shape, driven by the scripts in
[`scripts/api-stall-load/`](../../scripts/api-stall-load/):

- `generate-workspace.mjs` writes one project with 246 cards on the board and
  3,154 more in the archive. Each card has its prompt, `task.json`, pipeline
  record, run logs and results, so the workspace repository tracks about
  85,000 files. The code repository is a clone of this repository. Both
  remotes are local bare repositories.
- One API boot on that workspace backfills result scaffolds and integration
  verification records; the warmed state is committed and copied as the start
  state of every measured run, so before and after start from the same bytes
  (91,964 tracked files).
- `run.sh` boots a published API build (Release) on a copy of the warmed
  workspace, with a `PATH` that holds no agent CLI and a scratch `HOME`, then
  runs `drive.mjs` for 30 minutes: six board readers (grouped board, flat
  list, card detail), one writer moving cards between backlog and ready,
  commits on four agent worktrees and growing run logs every five seconds, and
  a `/healthz` probe every 250 ms. A probe slower than five seconds is what a
  user sees as the backend going offline.
- Alongside: `dotnet-counters` (`System.Runtime`, every 5 s), the OS thread
  count and RSS every 5 s, `dotnet-stack` every 5 minutes and `dotnet-gcdump`
  at 10 minutes and at the end. A 60-second `dotnet-trace` with the
  `gc-verbose` profile gave the allocation stacks.
- `summarize.mjs` turns a run directory into the numbers below.

The live production process was sampled from `/proc` for 25 minutes on
2 November 2026, already on the 20 GB limit: 105 threads on average
(maximum 144) and 3.4 to 4.0 GB RSS. The 1500 threads did not recur there or
in any synthetic run; see [Open points](#open-points).

## What the measurement showed

- **No thread explosion and no starved pool in the synthetic reproduction.** The API ran
  with 33 to 48 OS threads. In the stack snapshots most pool threads waited
  idle in the pool's semaphore; one thread sat in a synchronous Git call.
- **The synthetic run's pauses were garbage collection.** Before the change the API spent
  341 of 1,800 seconds in GC pauses (19 percent), up to 3.7 seconds in one
  five-second window. A 60-second allocation trace counted 14.6 GB allocated
  while the live heap stayed at 130 MB: almost everything allocated was
  garbage from re-reading and re-indexing cards that had not changed. The
  rebuilt index maps are larger than 85 KB, land on the large-object heap and
  trigger gen2 collections (16 per minute, reason `AllocLarge`).
- **Live heap by owner** (`dotnet-gcdump`, a few minutes into the load):
  129 MB before and 130 MB after, and still 130 MB at the end of the
  30-minute after run. The largest single objects are the file
  watcher's directory map (1.8 MB for the watched workspace tree) and the
  task index maps (about 0.3 MB each). No cache grew without a bound; the receipt
  memo added by this change holds a 67 KB map for 3,400 cards, and the
  timeline memo is capped at 4,096 folders.

Allocation by owner, inclusive MB in one 60-second `dotnet-trace` window
(steady state, same load):

| Owner | Before | After |
|---|---:|---:|
| Whole process | 14,583 | 7,890 |
| `TaskIndexCache.PublishCore` (one card's publication) | 3,121 | 344 |
| `TaskIndexCache.PublishCoreFacts` (index rebuild per publication) | 3,106 | 510 |
| `ProjectTokenLedger.LoadSnapshotUncached` (receipt walk) | 2,294 | 128 |
| `ReviewProjectionService.BuildLookup` (timelines) | 1,345 | 381 |
| `TaskIntegrationStatusService.BuildLookupCore` | 1,132 | 354 |
| `TaskIndexCache.AddCoreAliases` | 978 | 143 |
| `TaskGitSignature.For` | 965 | 596 |
| Watcher republication (`NotifyCoreFileChanged`) | 832 | 175 |
| `ProjectGitGraphService.BuildInventorySnapshot` | 726 | 84 |
| GC pause inside the window | 13.6 s | 5.4 s |

## What changed

| Cause | Change |
|---|---|
| A watcher event for a `status.md`, `prompt.md` or `timeline.jsonl` write the API had already published triggered a second full core publication. | `TaskIndexCache.NotifyCoreFileChanged` skips a sidecar whose length and write time the published record already carries, like it already did for `task.json`. |
| Every core publication rebuilt the identity map with per-card LINQ, sorted with LINQ, rebuilt three alias strings per card and allocated three comparison arrays per card. | `PublishCoreFacts` builds the same maps with plain loops and one array sort; alias keys are cached per immutable record; `TaskCoreRecord.WithDependencies` compares in place before it allocates. |
| The token ledger re-read and re-parsed `task.json` of every card, archive included, whenever the task index moved. | `ProjectTokenReceiptReader` keeps each folder's parsed receipt against the file's length and write time and forgets removed folders. |
| The board's Git projection re-parsed the whole timeline of every board card on every index run. | `ReviewProjectionService` keeps parsed timelines against the file's length and write time, bounded to 4,096 folders. |
| The integration verdict resolved the repository root once per card, and each resolution re-bound the `WatchPaths` configuration. | `TaskIntegrationStatusService` resolves it once per watch path per computation. |
| The Git inventory matched every branch against every card and read each card's review subject once per branch. | `ProjectGitGraphService.BuildTaskByBranch` indexes each card's branch names once. |
| The Git signature of a card was serialized to a string and then encoded. | `TaskGitSignature` hashes the UTF-8 JSON directly; the hash is unchanged. |

## Numbers before and after

Two 30-minute runs on 2 November 2026, same host, same warmed workspace,
same driver; only the published API build differs. Raw files, counters and
trace reports are in the task's `results/measure/` folder.

| Measure | Before | After |
|---|---:|---:|
| `/healthz` probes slower than 5 s (stall) | 2 | **0** |
| Longest probe | 6.2 s | 3.9 s |
| Probes slower than 1 s | 176 | 13 |
| Probe p50 / p99 | 28 ms / 2,007 ms | 3 ms / 353 ms |
| Longest late tick seen by the stall detector | (not present) | 3.7 s, 9 warnings, none at 5 s |
| GC pause, total over 30 min | 341 s (19 %) | 113 s (6 %) |
| Longest GC pause per 5 s window | 3.7 s | 1.2 s |
| gen2 collections | 242 | 611 (shorter) |
| Board read (grouped) mean / max | 954 ms / 6.5 s | 83 ms / 6.3 s |
| Card move mean / max | 6.7 s / 19.5 s | 1.8 s / 15.7 s |
| Requests served in 30 min | 10,706 | 19,778 |
| Allocation per second / per request | 88 MB/s / 15 MB | 115 MB/s / 10 MB |
| CPU (cores, mean) | 0.41 | 0.69 |
| OS threads mean / max | 38 / 48 | 33 / 48 |
| RSS mean / max | 766 / 1,042 MB | 751 / 1,036 MB |
| Live heap (gcdump) | 129 MB | 130 MB |

The first delivery's after build served almost twice the requests in the same time, which is
why its CPU and allocation per second are higher; per request it allocates a
third less, and the process no longer pauses for more than four seconds.

## The stall alarm

`ApiStallDetector` (hosted service, every API host) waits 500 ms on a timer in
a loop. The continuation runs on the thread pool, so a stop-the-world GC pause
or a starved pool makes it run late. A tick that runs at least one second late
logs a warning, here a line from the after run:

```text
api-stall delayMs=1758 level=Warning gcPauseMs=524 threadPoolThreads=19 pendingWorkItems=6 processThreads=45
```

`gcPauseMs` is the GC pause time since the previous tick. A large value means
a GC pause; a small one with many `pendingWorkItems` means a starved pool.
The detector keeps the last 50 stalls and the longest delay since start.
`GET /api/diagnostics/api-stalls` serves them (every security profile), and
Operations shows them in the **Studio API responsiveness** panel: quiet while
nothing is recorded, **Slow** for a late tick of one second or more, and
**Stalled** for a delay of five seconds or more within the last hour.

## Open points

- **The 6.5 GB heap and the 1500 threads did not reproduce.** The synthetic
  workspace has the incident's card and file counts, but its live heap is
  130 MB and it never exceeded 48 threads. Production cards carry larger logs
  and receipts, and production Git talks to real remotes with a 30-second
  timeout per call. Each synchronous Git call holds a pool thread plus two or
  three reader threads (`GitNetworkProcessRunner`), so many slow network
  calls at once are a plausible source of the thread count; the load here had
  local remotes only. Every `api-stall` warning now carries the GC pause, the
  pool's thread count and its queue, so the next production stall says which
  of the two it was. Take a `dotnet-gcdump` inside the container when RSS
  passes 4 GB to settle the heap question.
- **Remaining allocators after this change:** the Git index's per-card
  signatures (`TaskGitSignature`, 0.6 GB per minute), the full workspace
  rescans of `TaskIndexCache.Refresh` on the 30-second safety TTL and on
  mutations, and the wiki content snapshot (0.65 GB per minute).
- **Single slow requests remain.** A card move still takes up to 16 seconds
  at the tail and a board read up to 6 seconds; the process itself no longer
  stalls, but those requests wait on locks and full rescans.
- The live production process sampled from `/proc` on 2 November 2026 (20 GB
  limit): 105 threads on average, maximum 144, RSS 3.4 to 4.0 GB, one restart
  at 06:40 UTC during the 25-minute window.
