# Four slow backend test classes, 9 November 2026

AGT-3567 took the four slowest backend test classes after AGT-3507, apart from
`BatchGatePilotServiceTests` (a separate card), from the ranking in
[backend-test-class-durations-2026-11-05.md](backend-test-class-durations-2026-11-05.md):
`GitReadSpawnCensusTests` (220 s, 19 tests), `AcceptanceIntegrationRoundTripTests`
(205 s, 72 tests), `MergeIntoDevelopRunnerTests` (170 s, 92 tests) and
`NetworkedEdgeHardeningOwnHostTests` (164 s, 12 tests). On current `develop`
the acceptance class has 75 tests. AGT-3567 profiled each class,
changed what could be changed without weakening an assertion, and measured
before and after.

Result: `NetworkedEdgeHardeningOwnHostTests` (-62 to -68 %) and
`GitReadSpawnCensusTests` (-48 to -54 % in the round layouts) are at or about
half their former duration. `AcceptanceIntegrationRoundTripTests` and
`MergeIntoDevelopRunnerTests` are not halved (-1 % and -6 % from their own
changes): their time is Git processes and fsyncs that the product code under
test starts, not fixture setup. The section [What remains](#what-remains)
names the product changes that would be needed.

## Method

- Host `agent-studio-runner` (12 cores, shared with other runners), .NET SDK
  10.0.301, Git 2.43.0. Before is `develop` at
  `ea372702d4f9023f3f649e4e5fab4b901cffd543`; its test output was copied
  aside before the first change, so before and after ran from the same
  checkout.
- The gate's settings: `DOTNET_PROCESSOR_COUNT=6`, `--no-build`,
  `--filter "Category!=MachineBound&FullyQualifiedName~AgentStudio.Tests.<Class>."`,
  one test process per class, at most four test processes at a time. No
  synthetic load was started; the load came from other runners' builds and
  gates on the same host.
- Class duration is the sum of the test durations in the class's TRX file, as
  in the AGT-3507 ranking. Tests of one class run one after another, so for
  these classes the sum is close to the class's wall time inside the process.
  The process wall time, which adds process start, discovery and the Git
  facts board fixture, is recorded as well.
- The host load (1-minute load average) at the start and end of every process
  is recorded. It moved between 7 and 118 during the measurements, which
  changed class durations by a factor of two on its own. Three layouts were
  therefore measured:
  1. **Rounds**: the four classes as four parallel processes, three rounds
     before (10:13 to 10:25) and three after (11:40 to 11:47).
  2. **Interleaved rounds**: the same layout, alternating before and after
     (before, after, before, after, before, after), so that both sides see
     the same stretch of host load. This is the comparison the table below
     uses.
  3. **Paired rounds**: the before and after processes of one class started at
     the same moment. This is fair for classes whose two sides take equally
     long. When the after side is much faster, its later tests overlap the
     before side's slow early phase, so this layout understates large gains.
     Three paired rounds (`pair2` to `pair4`) use the final build; `pair1` in
     the evidence used an intermediate build and is not compared here.
- Profiles: EventPipe sample profiles (`DOTNET_EnableEventPipe`, converted with
  `dotnet-trace`) of each class alone, and a `git` wrapper on `PATH` that logged
  every Git child process with its duration and arguments.

All TRX files, console logs, `runs.csv` and the derived `class-runs.csv` are in
the AGT-3567 run evidence (`measurements/`).

## Where the time went

| Class | Finding |
|---|---|
| `NetworkedEdgeHardeningOwnHostTests` | Password hashing. PBKDF2-SHA512 with 600,000 iterations took one to four seconds per hash on the loaded host. `Trusted_frontend_forwards_the_client_address_and_untrusted_peers_cannot` alone bootstraps four hosts and sends 58 logins with the same two passwords: 62 hashes, 103 to 154 s of a 148 to 217 s class. The other tests bootstrap an owner on their own host, one hash each. |
| `GitReadSpawnCensusTests` | About 85 % was host start. The 18 scenarios and their cold reads boot 26 hosts over the 3,000-card Git facts board, and every start scanned all 3,000 card folders three times: the first repair sweep (`BackfillAgentDefaults`) built the task index, `TaskAccess` boot rebuilt it, and the run-liveness boot adoption scanned the board raw. The first host also demoted the board's 30 in-progress cards one by one, each move followed by a full rescan. The census requests themselves took 10 to 15 % of the class. |
| `MergeIntoDevelopRunnerTests` | About 80 % was Git processes: 7,220 in 92 tests (78 per test, 4,050 of them `rev-parse`). About three quarters of that Git time is started by `MergeIntoDevelopRunner` and `GitService`, spread over more than 40 call sites; the largest are integration merges, fetches, lane ref reads (`rev-parse --verify --quiet refs/agent-studio/integration/<branch>`, 1,179 calls) and `--git-dir`/`--git-common-dir` lookups (1,044 calls). The test helpers start the remaining quarter, spread evenly over the tests. |
| `AcceptanceIntegrationRoundTripTests` | 4,315 Git processes in 75 tests (about 32 % of the class, two thirds of it started by the product), atomic JSON writes with two fsyncs each (about 23 %), and seven test hosts (about 22 %, most of it the first host start of the process). The fsyncs come from `task.json` writes in the transitions under test, the attempt authority store, and the fixture's project settings. |

## Changes

| Class | Change |
|---|---|
| `NetworkedEdgeHardeningOwnHostTests` (and the shared-host `NetworkedEdgeHardeningTests`) | The test host's `AccessSecurityStore` hashes and verifies through the real `PasswordSecretHasher`, once per password and stored hash per test process. `AccessSecurityStore` gained an internal password-hash seam next to its existing internal verify seam. Throttle, hash slots, cookies and responses are unchanged. |
| `GitReadSpawnCensusTests` | The census hosts boot quietly: `Startup:RepairSweeps=false` (new, honoured only in the `Test` and `Testing` environments) skips the one-time repair, backfill and report sweeps of `Program`, and the existing `Runner:RunLiveness:Enabled=false` skips the boot adoption. The hosted services were already removed, so no runner could own a run on these hosts. The index boot, crash recovery and the stale-progress archiver still run, so each host scans the board about once. Side effect: the board keeps its 30 in-progress cards in `3-progress`, as `GitFactsBoardFixture` documents, instead of having them demoted by whichever host booted first. The scenarios, the strict guard and every assertion are unchanged, and all 19 tests pass. |
| `MergeIntoDevelopRunnerTests` | `GitService.IsLinkedWorktree` asks `git rev-parse --git-dir --git-common-dir` in one process instead of two. |
| `AcceptanceIntegrationRoundTripTests` | The fixture's `ProjectSettingsService` writes through an atomic writer without fsync (temp file, then rename). The settings content is unchanged and no test depends on it surviving a power loss. The attempt authority store keeps the production writer, because its journal is enabled only for that writer type. |

## Results

Interleaved rounds (class duration = TRX sum in seconds; load = 1-minute load
average at process start and end):

| Round | Build | Class | Tests | Class duration (s) | Process wall (s) | Load start → end |
|---|---|---|---:|---:|---:|---|
| 1 | before | `GitReadSpawnCensusTests` | 19 | 256.2 | 302.0 | 22.85 → 78.17 |
| 1 | before | `AcceptanceIntegrationRoundTripTests` | 75 | 203.9 | 226.6 | 22.85 → 68.78 |
| 1 | before | `MergeIntoDevelopRunnerTests` | 92 | 173.8 | 197.2 | 22.85 → 63.51 |
| 1 | before | `NetworkedEdgeHardeningOwnHostTests` | 12 | 211.0 | 233.0 | 22.85 → 68.24 |
| 1 | after | `GitReadSpawnCensusTests` | 19 | 133.5 | 217.1 | 78.17 → 50.25 |
| 1 | after | `AcceptanceIntegrationRoundTripTests` | 75 | 185.6 | 228.5 | 78.17 → 45.76 |
| 1 | after | `MergeIntoDevelopRunnerTests` | 92 | 175.2 | 214.6 | 78.17 → 50.25 |
| 1 | after | `NetworkedEdgeHardeningOwnHostTests` | 12 | 78.6 | 119.7 | 78.17 → 93.64 |
| 2 | before | `GitReadSpawnCensusTests` | 19 | 201.9 | 265.9 | 45.76 → 29.20 |
| 2 | before | `AcceptanceIntegrationRoundTripTests` | 75 | 154.6 | 183.7 | 45.76 → 25.36 |
| 2 | before | `MergeIntoDevelopRunnerTests` | 92 | 145.3 | 176.4 | 45.76 → 26.26 |
| 2 | before | `NetworkedEdgeHardeningOwnHostTests` | 12 | 176.6 | 206.1 | 45.76 → 25.32 |
| 2 | after | `GitReadSpawnCensusTests` | 19 | 96.6 | 159.7 | 29.20 → 52.22 |
| 2 | after | `AcceptanceIntegrationRoundTripTests` | 75 | 122.2 | 154.6 | 29.20 → 54.15 |
| 2 | after | `MergeIntoDevelopRunnerTests` | 92 | 110.9 | 143.2 | 29.20 → 51.73 |
| 2 | after | `NetworkedEdgeHardeningOwnHostTests` | 12 | 61.5 | 93.3 | 29.20 → 39.48 |
| 3 | before | `GitReadSpawnCensusTests` | 19 | 278.9 | 355.8 | 52.22 → 62.66 |
| 3 | before | `AcceptanceIntegrationRoundTripTests` | 75 | 232.8 | 260.6 | 52.22 → 84.41 |
| 3 | before | `MergeIntoDevelopRunnerTests` | 92 | 225.0 | 255.2 | 52.22 → 83.14 |
| 3 | before | `NetworkedEdgeHardeningOwnHostTests` | 12 | 241.4 | 267.1 | 52.22 → 87.10 |
| 3 | after | `GitReadSpawnCensusTests` | 19 | 106.6 | 164.3 | 62.66 → 32.86 |
| 3 | after | `AcceptanceIntegrationRoundTripTests` | 75 | 136.9 | 167.7 | 62.66 → 32.86 |
| 3 | after | `MergeIntoDevelopRunnerTests` | 92 | 118.5 | 150.5 | 62.66 → 37.36 |
| 3 | after | `NetworkedEdgeHardeningOwnHostTests` | 12 | 62.5 | 92.4 | 62.66 → 33.89 |

Means over the three interleaved rounds:

| Class | Before (s) | After (s) | Change | Process wall before → after (s) |
|---|---:|---:|---:|---|
| `GitReadSpawnCensusTests` | 245.7 | 112.2 | -54 % | 308 → 180 (-41 %) |
| `AcceptanceIntegrationRoundTripTests` | 197.1 | 148.2 | -25 % | 224 → 184 (-18 %) |
| `MergeIntoDevelopRunnerTests` | 181.4 | 134.9 | -26 % | 210 → 169 (-19 %) |
| `NetworkedEdgeHardeningOwnHostTests` | 209.7 | 67.5 | -68 % | 235 → 102 (-57 %) |

The other two layouts:

- Rounds measured an hour apart: census 169 → 88 s (-48 %), acceptance
  161 → 107 s (-33 %), merge 131 → 98 s (-26 %), edge 162 → 57 s (-65 %). The
  after rounds ran at a lower load than the before rounds, so these
  overstate the gains.
- Paired rounds: census 172 → 140 s (-19 %), acceptance 149 → 147 s (-1 %),
  merge 144 → 136 s (-6 %), edge 179 → 68 s (-62 %). For the census this
  layout understates the gain (see Method); its per-test durations show the
  cause: the after side's later scenarios ran while the before side was
  still in its slow first scenarios.

How to read the acceptance and merge rows: in the round layouts both classes
ran about a quarter faster, but most of that is their neighbours. In those
rounds they shared the host with the census and edge processes of the same
build, and the after build of those two uses far less CPU. The paired rounds,
where each class ran next to its own other build and both sides took the
same time, isolate the classes' own changes: -1 % and -6 %. In a gate shard
the neighbour effect is real, because all classes of a shard share the
processors, but it is not a change to these two classes.

All runs passed every test of the four classes (the before build at
`ea372702d` and the after build).

## What remains

- **`MergeIntoDevelopRunnerTests`, `AcceptanceIntegrationRoundTripTests`.**
  Halving them needs fewer Git processes and fsyncs in the product paths they
  test, which is product work rather than test work:
  the integration path reads the same lane and branch refs several times per
  merge (one process each), `IntegrationWorktreeProvider` lists and refreshes
  worktrees per integration, and `AtomicJsonFileWriter` flushes the file and
  its directory on every `task.json` field update. Fixture changes alone
  cannot reach half: the test helpers start about a quarter of the Merge
  class's Git time, and the fixture's settings writes were about 6 % of the
  Acceptance class.
  Fewer processes would also help production integrations and the batch gate.
- **The first host of a test process.** The first `WebApplicationFactory`
  start in a process takes 15 to 90 s on this host (JIT and first scans).
  Whichever class starts the first host pays it; in these measurements that
  was `R12` in the census, the first `FailureContinueHttp...` case in the
  acceptance class and the first own-host edge test. In a gate shard it is
  paid once per process.
- **Boot scans in production.** `TaskAccess` boot rebuilds the task index
  that the first repair sweep built moments earlier, and the run-liveness
  boot adoption scans the board once more. On a 3,000-card workspace each
  full scan costs about one to two seconds per start on this host. Removing
  the duplicate would shorten every host start and every Studio restart (lever
  B5 of the [test acceleration dossier](../test-acceleration/index.html)).
