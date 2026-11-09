# Four slow backend test classes, 9 November 2026

AGT-3567 took the four slowest backend test classes after AGT-3507, apart from
`BatchGatePilotServiceTests` (a separate card), from the ranking in
[backend-test-class-durations-2026-11-05.md](backend-test-class-durations-2026-11-05.md):
`GitReadSpawnCensusTests` (220 s, 19 tests), `AcceptanceIntegrationRoundTripTests`
(205 s, 72 tests), `MergeIntoDevelopRunnerTests` (170 s, 92 tests) and
`NetworkedEdgeHardeningOwnHostTests` (164 s, 12 tests). On current `develop`
the acceptance class has 75 tests.

The card ran twice. The first run halved the census and the own-host edge
class; its changes are on `develop`. Review found the acceptance and merge
classes not halved (-1 % and -6 % from their own changes). The follow-up run
profiled those two again on current `develop`, changed the product's Git
process handling and the two fixtures, and measured all four classes on three
builds with the same commands.

## Result

Mean class duration (sum of the TRX test durations, seconds) over three
rounds in each of three layouts, all with the gate's settings and at most
four test processes. *Before the card* is `develop` at `ea372702d`, the state
the first run started from; *develop* is current `develop` with the first
run's changes; *after* adds the follow-up.

| Class | Layout | Before the card | develop | After | Before the card → after | develop → after |
|---|---|---:|---:|---:|---:|---:|
| `GitReadSpawnCensusTests` | four processes | 243.8 | 125.9 | 122.9 | -50 % | -2 % |
| | paired | | 163.5 | 162.5 | | -1 % |
| | one process | 220.9 | 168.7 | 99.0 | -55 % | -41 % |
| `AcceptanceIntegrationRoundTripTests` | four processes | 237.6 | 157.8 | 122.7 | -48 % | -22 % |
| | paired | | 162.6 | 118.6 | | -27 % |
| | one process | 222.4 | 207.6 | 93.4 | -58 % | -55 % |
| `MergeIntoDevelopRunnerTests` | four processes | 195.2 | 152.3 | 114.3 | -41 % | -25 % |
| | paired | | 148.2 | 109.4 | | -26 % |
| | one process | 197.9 | 201.4 | 84.5 | -57 % | -58 % |
| `NetworkedEdgeHardeningOwnHostTests` | four processes | 184.9 | 69.0 | 103.0 | -44 % | +49 % |
| | paired | | 92.2 | 92.8 | | +1 % |
| | one process | 187.9 | 71.1 | 48.5 | -74 % | -32 % |

Read the layouts like this (details in [Method](#method)):

- **One process**, the four classes in one test process as in a gate shard:
  every class is at least halved against the state before the card (-55 to
  -74 %), and the two classes the review named are halved against current
  `develop` as well (acceptance -55 %, merge -58 %). The process wall time
  went from 264 s before the card and 216 s on `develop` to 138 s.
- **Four processes** (interleaved rounds, one process per class) and
  **paired**: acceptance and merge are a quarter faster than on `develop`
  (-22 to -27 %), and -48 % and -41 % against the state before the card. In
  this layout they are not halved against `develop`.
- The follow-up did not touch the census and edge classes' own paths. Paired
  with their own `develop` build they are unchanged (-1 %, +1 %); in one
  process they gain (-41 %, -32 %) because they share it with the Git-heavy
  classes. In the four-process rounds they ran slower next to the faster
  after classes: both are CPU-bound (host starts, password hashing), and their
  neighbours now do the same CPU work in less time. The longest process of a four-process round
  took as long as before (169, 175 and 236 s on `develop`; 171, 207 and
  212 s after), so on this host that layout gained per class, not in total.

Why one process gains more, most likely: Git children are started, reaped
and drained by machinery the whole test process shares. `Process.Start` takes
a process-wide lock that the runtime's SIGCHLD handler also takes to reap
children, and every child used to start two new drain threads.
With four Git-heavy classes in one process those hand-offs queue behind each
other; halving the children and dropping the thread starts relieves all of
them at once. With one class per process each class has this machinery to
itself, and the gain is what a class saves on its own hand-offs. The gate runs
each shard as one test process with a partition of classes running in
parallel ([pipeline](../../system/domains/pipeline.md#sharded-test-suites-in-the-gate-agt-3232)).

## Method

- Host `agent-studio-runner` (12 cores, shared with other runners), .NET SDK
  10.0.301, Git 2.43.0.
- Three builds, each from its own copy of the test output, so all of them ran
  in the same stretch of host load:
  - **base**: `develop` at `ea372702d4f9023f3f649e4e5fab4b901cffd543`, before
    the card (built in a separate worktree);
  - **dev**: current `develop` (`9e61327e6`, with the first run's delivery
    merged), built before the first follow-up change;
  - **after**: dev plus the follow-up changes below. The delivery merged a
    later `develop` after the measurement; that merge brought unrelated work
    and new acceptance tests, which use the same `PublishDelivery` helper.
- The gate's settings for every test process: `DOTNET_PROCESSOR_COUNT=6`,
  `dotnet test <build>/OrchestratorApi.Tests.dll`, and
  `--filter "Category!=MachineBound&FullyQualifiedName~AgentStudio.Tests.<Class>."`.
  At most four test processes ran at a time. No synthetic load was started;
  the load came from other runners' builds and gates on the same host, and
  the 1-minute load average is recorded at the start and end of every process.
  It moved between 23 and 137 during the final measurement.
- Class duration is the sum of the test durations in the class's TRX file, as
  in the AGT-3507 ranking. The process wall time is recorded as well.
- Three layouts, three rounds each, all on the final build:
  1. **Interleaved rounds**: the four classes as four parallel processes from
     one build, in the order base, dev, after, base, dev, after, ...
  2. **Paired rounds**: per class, the dev and the after process start at the
     same moment (two classes per batch, four processes). Both sides see the
     same load; when the after side finishes much earlier, the dev side runs
     its last tests on a quieter host, so this layout understates large gains.
  3. **One process**: the four classes in one test process (one filter for all
     four), as in a gate shard that holds them. xUnit runs the classes in
     parallel, and the process pays the first web host start and the JIT
     warm-up once instead of once per class.
- Profiles: EventPipe sample profiles of each class alone, a `git` wrapper on
  `PATH` that logged every Git child with its duration and arguments, and a
  `/proc` sampler that recorded the CPU of the test host by thread and of its
  reaped Git children.

All TRX files, console logs, `runs.csv`, `class-runs.csv` and the Git process
counts are in the AGT-3567 run evidence (`measurements/`, `profile/`).

## Where the time went

First run, on `develop` at `ea372702d`:

| Class | Finding |
|---|---|
| `NetworkedEdgeHardeningOwnHostTests` | Password hashing. PBKDF2-SHA512 with 600,000 iterations took one to four seconds per hash on the loaded host. `Trusted_frontend_forwards_the_client_address_and_untrusted_peers_cannot` alone bootstraps four hosts and sends 58 logins with the same two passwords: 62 hashes, 103 to 154 s of a 148 to 217 s class. The other tests bootstrap an owner on their own host, one hash each. |
| `GitReadSpawnCensusTests` | About 85 % was host start. The 18 scenarios and their cold reads boot 26 hosts over the 3,000-card Git facts board, and every start scanned all 3,000 card folders three times: the first repair sweep (`BackfillAgentDefaults`) built the task index, `TaskAccess` boot rebuilt it, and the run-liveness boot adoption scanned the board raw. The first host also demoted the board's 30 in-progress cards one by one, each move followed by a full rescan. The census requests themselves took 10 to 15 % of the class. |

Follow-up, on current `develop`:

| Class | Finding |
|---|---|
| `MergeIntoDevelopRunnerTests` | 6,829 Git processes in 92 tests, 3,659 of them `rev-parse`. In the profiles, starting, waiting for and draining Git children took three quarters or more of the test thread's time, a fifth to a third of it started by the test helpers. A child cost about 20 ms of wall time on the loaded host but only 2 to 3 ms of CPU: in one 120 s run the test host used 24.5 s of CPU and all its Git children together 23.5 s. The rest is waiting for a processor at every hand-off: the process start, the exit signal, and two new pipe-drain threads per child, whose `Thread.Start` returns only once the thread runs. On an intermediate build, `DOTNET_TieredPGO=0` cut the CPU by a fifth and left the class duration unchanged (paired, within 2 %), so the class is bound by hand-offs, and the lever is fewer processes and fewer thread starts. About half of the processes were small reads that the repository files answer (ref tips, `HEAD`, `--abbrev-ref HEAD`, `--git-dir`, `--git-common-dir`, `--show-toplevel`, `git remote`), and every `commit`, `merge` and `fetch` started one more `git maintenance run --auto` child, every push a `gc --auto` on the receiving side. |
| `AcceptanceIntegrationRoundTripTests` | 4,196 Git processes in 75 tests, the same per-child costs, and 9 of them per test in the fixture's `PublishDelivery` (checkout, commit, push with four more processes inside, clean-up). File and directory fsyncs of `task.json`, the attempt authority store and its journal were about a third of the test thread in a profile at load 20; single fsyncs took up to 6.8 s while other runners wrote. The first web host start of the process takes 15 to 50 s depending on load (JIT); the other seven host tests take 1.5 to 8 s each. |

## Changes

First run (on `develop`):

| Class | Change |
|---|---|
| `NetworkedEdgeHardeningOwnHostTests` (and the shared-host `NetworkedEdgeHardeningTests`) | The test host's `AccessSecurityStore` hashes and verifies through the real `PasswordSecretHasher`, once per password and stored hash per test process. `AccessSecurityStore` gained an internal password-hash seam next to its existing internal verify seam. Throttle, hash slots, cookies and responses are unchanged. |
| `GitReadSpawnCensusTests` | The census hosts boot quietly: `Startup:RepairSweeps=false` (honoured only in the `Test` and `Testing` environments) skips the one-time repair, backfill and report sweeps of `Program`, and the existing `Runner:RunLiveness:Enabled=false` skips the boot adoption. The index boot, crash recovery and the stale-progress archiver still run. The board keeps its 30 in-progress cards in `3-progress`, as `GitFactsBoardFixture` documents. |
| `MergeIntoDevelopRunnerTests` | `GitService.IsLinkedWorktree` asked `git rev-parse --git-dir --git-common-dir` in one process instead of two. |
| `AcceptanceIntegrationRoundTripTests` | The fixture's project settings were written without fsync (replaced in the follow-up, below). |

Follow-up. The product changes apply to every Studio Git call, not only to
tests:

| Where | Change |
|---|---|
| Product: `GitPipeDrainThreads` (new), `GitNetworkProcessRunner` | The standard output, standard error and standard input of a synchronous Git child are drained by reusable dedicated threads instead of two or three new threads per child. A drain takes an idle thread or starts one when none is idle, so concurrency stays unbounded and a blocked read never waits for another; idle threads end after 30 s. The drains stay off the ThreadPool, as before. |
| Product: `GitRefFiles` (new), `GitService` | The small reads are answered from the repository's files when the files settle them, exactly as Git would answer: `rev-parse --verify --quiet` of a full ref or a branch-like short name in Git's lookup order (`GetBranchTip`, `BranchExists`, `RemoteBranchExists`), of `MERGE_HEAD` (interrupted-merge check), `rev-parse HEAD` (`ReadHeadShaAt`), `--abbrev-ref HEAD` (`ReadBranchAt`, integration head check), `--git-dir` (`IsRebaseInProgress`), `--git-common-dir` (remote gate key), the linked-worktree check, `--show-toplevel` for a work tree root, and "origin is configured" (`HasRemote`, true answers only). Anything the files do not settle runs Git as before: a reftable repository, a symbolic or per-worktree ref, a subdirectory, a bare repository, `GIT_DIR` in the environment, an ambiguous short name, a configured `core.worktree`, a symbolic link in the path, Windows path spelling for the toplevel, or an unreadable file. Like `rev-parse --verify`, a ref read does not check that the object exists. |
| Product: `GitService` dirty checks | The two checks that only ask whether a checkout is clean (`DirtyTreeRefusal`, the rollback's checkout check) read `status --porcelain=v1` alone instead of the full status with branch and two numstat diffs: one process instead of four, same paths, same treatment of a failed read. |
| Product: `IntegrationWorktreeProvider` | A slot that `git worktree add --detach` has just created at the base ref skips the checkout, reset and clean that follow; a reused slot is refreshed as before. |
| Product seam: `AtomicJsonFileWriter.SuspendDiskFlushForTests` | An internal, flow-scoped (AsyncLocal) switch for test fixtures that never simulate a power loss: writes in the calling flow stay atomic (temp file, then rename) but skip the file and directory fsync. It covers `AtomicJsonFileWriter` and the attempt authority journal. Code in other flows, such as a request a test host serves, still flushes. The acceptance fixture calls it in its constructor; its own settings writer from the first run is gone, so the settings use the production writer again. |
| Fixture: `AcceptanceIntegrationRoundTripTests.PublishDelivery` | The delivery commit is written with the same `git fast-import` stream into the local repository and into origin, which yields the same commit in both: three processes (parent lookup and two imports) instead of nine, no push. The end state is the same: origin's delivery ref holds one commit on `origin/develop` that adds the file, the local repository holds the commit without a ref and stays on its clean `main`. The commit carries its parent's commit time, so the fixture reads no wall clock. |
| Fixture: merge and acceptance templates | Their repositories set `maintenance.auto=false`, and their bare origins `receive.autoGc=false`, so `commit`, `merge`, `fetch` and push start no automatic maintenance child. The product's commands in these repositories read the same configuration. Template keys moved to `v2`. |

No assertion changed. New tests: `GitRefFilesTests` (36 cases; every answer
is compared with `git rev-parse`, `git remote` or `git worktree` output, and
every case the files cannot settle must be left to Git) and
`GitPipeDrainThreadsTests` (results, failures, reuse, 24 drains blocked at
once).

Git processes per class run, counted with the wrapper:

| Class | develop | After | Change |
|---|---:|---:|---:|
| `MergeIntoDevelopRunnerTests` | 6,829 | 3,133 | -54 % |
| `AcceptanceIntegrationRoundTripTests` | 4,196 | 2,046 | -51 % |

The wrapper counts the processes Studio and the tests start, not the children
Git starts itself (such as the maintenance children that the template
configuration now avoids).

## Results

Class duration is the TRX sum in seconds. Load is the 1-minute load average
when the round's processes started and the highest value when one of them
ended.

### Four processes, interleaved rounds

| Run | Build | `GitReadSpawnCensusTests` | `AcceptanceIntegrationRoundTripTests` | `MergeIntoDevelopRunnerTests` | `NetworkedEdgeHardeningOwnHostTests` | Load start → end |
|---|---|---:|---:|---:|---:|---|
| r1 | base | 224.2 | 267.1 | 181.7 | 174.9 | 40.36 → 49.88 |
| r1 | dev | 112.4 | 140.5 | 131.6 | 62.9 | 49.88 → 43.12 |
| r1 | after | 113.9 | 111.9 | 101.0 | 93.5 | 43.12 → 43.97 |
| r2 | base | 265.3 | 234.8 | 200.8 | 173.3 | 31.79 → 73.42 |
| r2 | dev | 98.9 | 144.4 | 135.3 | 72.0 | 73.42 → 66.50 |
| r2 | after | 133.8 | 122.3 | 108.4 | 107.9 | 27.11 → 55.59 |
| r3 | base | 242.0 | 211.0 | 203.1 | 206.3 | 55.59 → 93.01 |
| r3 | dev | 166.4 | 188.5 | 189.9 | 72.1 | 90.12 → 75.67 |
| r3 | after | 121.1 | 134.1 | 133.6 | 107.6 | 49.19 → 87.16 |

Means (interleaved rounds), TRX sum in seconds:

| Class | base | dev | after | base → after | dev → after |
|---|---:|---:|---:|---:|---:|
| `GitReadSpawnCensusTests` | 243.8 | 125.9 | 122.9 | -50 % | -2 % |
| `AcceptanceIntegrationRoundTripTests` | 237.6 | 157.8 | 122.7 | -48 % | -22 % |
| `MergeIntoDevelopRunnerTests` | 195.2 | 152.3 | 114.3 | -41 % | -25 % |
| `NetworkedEdgeHardeningOwnHostTests` | 184.9 | 69.0 | 103.0 | -44 % | +49 % |

### Paired rounds

| Run | Build | `GitReadSpawnCensusTests` | `AcceptanceIntegrationRoundTripTests` | `MergeIntoDevelopRunnerTests` | `NetworkedEdgeHardeningOwnHostTests` | Load start → end |
|---|---|---:|---:|---:|---:|---|
| p1 | dev | 134.4 | 107.7 | 97.7 | 122.6 | 28.60 → 59.74 |
| p1 | after | 138.3 | 83.9 | 75.3 | 122.4 | 28.60 → 59.74 |
| p2 | dev | 160.8 | 198.3 | 180.1 | 67.6 | 28.42 → 89.49 |
| p2 | after | 163.8 | 161.3 | 150.3 | 68.7 | 28.42 → 89.49 |
| p3 | dev | 195.4 | 181.9 | 166.8 | 86.4 | 69.54 → 94.81 |
| p3 | after | 185.4 | 110.7 | 102.5 | 87.3 | 69.54 → 96.01 |

Means (paired rounds), TRX sum in seconds:

| Class | dev | after | dev → after |
|---|---:|---:|---:|
| `GitReadSpawnCensusTests` | 163.5 | 162.5 | -1 % |
| `AcceptanceIntegrationRoundTripTests` | 162.6 | 118.6 | -27 % |
| `MergeIntoDevelopRunnerTests` | 148.2 | 109.4 | -26 % |
| `NetworkedEdgeHardeningOwnHostTests` | 92.2 | 92.8 | +1 % |

### One process for the four classes

| Run | Build | `GitReadSpawnCensusTests` | `AcceptanceIntegrationRoundTripTests` | `MergeIntoDevelopRunnerTests` | `NetworkedEdgeHardeningOwnHostTests` | Process wall (s) | Load start → end |
|---|---|---:|---:|---:|---:|---:|---|
| o1 | base | 208.9 | 231.8 | 223.8 | 200.3 | 257.1 | 53.80 → 37.81 |
| o1 | dev | 156.8 | 185.8 | 169.4 | 95.2 | 191.5 | 37.81 → 60.61 |
| o1 | after | 98.7 | 94.0 | 86.5 | 40.4 | 134.9 | 60.61 → 40.45 |
| o2 | base | 204.6 | 198.9 | 154.3 | 146.3 | 232.5 | 40.45 → 23.21 |
| o2 | dev | 163.1 | 188.4 | 188.0 | 47.0 | 194.5 | 23.21 → 63.74 |
| o2 | after | 124.2 | 93.1 | 81.6 | 50.4 | 157.5 | 63.74 → 86.04 |
| o3 | base | 249.0 | 236.5 | 215.6 | 217.2 | 303.5 | 86.04 → 76.61 |
| o3 | dev | 186.2 | 248.7 | 246.7 | 70.9 | 260.5 | 76.61 → 69.22 |
| o3 | after | 74.1 | 93.3 | 85.5 | 54.6 | 122.4 | 69.22 → 39.57 |

Means (one process), TRX sum in seconds:

| Class | base | dev | after | base → after | dev → after |
|---|---:|---:|---:|---:|---:|
| `GitReadSpawnCensusTests` | 220.9 | 168.7 | 99.0 | -55 % | -41 % |
| `AcceptanceIntegrationRoundTripTests` | 222.4 | 207.6 | 93.4 | -58 % | -55 % |
| `MergeIntoDevelopRunnerTests` | 197.9 | 201.4 | 84.5 | -57 % | -58 % |
| `NetworkedEdgeHardeningOwnHostTests` | 187.9 | 71.1 | 48.5 | -74 % | -32 % |
| (process wall) | 264.4 | 215.5 | 138.3 | -48 % | -36 % |

Every `develop` and after run passed all tests of the four classes (54 test
processes, 72 class runs). One run of the build before the card failed one
census case
(`o3 base`, `R13 Wiki in checkout mode` answered 500 at load 86, with the four
classes sharing one process); that build is the state before the card and was
measured only for comparison.

## What remains

- **The first web host start of a test process.** It takes 15 to 50 s on this
  host (JIT of the whole application) and is paid once per process: in the
  per-class layouts by the first host test of the acceptance class (about a
  quarter of its after duration at load 50 and more), in a gate shard by
  whichever class starts the first host.
- **The remaining Git children of an integration.** The merge class still
  starts 3,133 processes, mostly the work an integration needs (worktree add,
  checkout, merge, fetch, push, update-ref). Smaller items that could follow:
  `merge-base --is-ancestor X X` for the same commit (169 calls in the merge
  class, answered true by Git whenever the commit exists), `git remote` in a
  repository without `origin` (127; the files cannot rule out a remote from
  the system configuration), `worktree list` per integration (101), and the
  fixture's own `checkout` and `commit` calls when it builds task branches
  (about 150).
- **CPU-bound neighbours.** With one process per class, the census and edge
  classes are CPU-bound and run slower next to classes that became faster.
  Their own levers are in the first run's findings (host starts, password
  hashing).
- **Boot scans in production.** `TaskAccess` boot rebuilds the task index
  that the first repair sweep built moments earlier, and the run-liveness
  boot adoption scans the board once more. On a 3,000-card workspace each
  full scan costs about one to two seconds per start on this host. Removing
  the duplicate would shorten every host start and every Studio restart (lever
  B5 of the [test acceleration dossier](../test-acceleration/index.html)).
