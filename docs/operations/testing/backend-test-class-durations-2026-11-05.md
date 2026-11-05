# Backend test class durations, 5 November 2026

AGT-3507 measured the complete `backend.Tests` project before and after three
test fixture changes. The three classes were the slowest in the baseline run.
This record preserves the measured class rankings in the repository, where a
delivery diff can show them.

## Method and run outcomes

Both runs used this host (`agent-studio-runner`) with
`DOTNET_PROCESSOR_COUNT=6`, `-maxcpucount:2`, `--no-build --no-restore`, and
`--filter Category!=MachineBound`. Each revision had one complete test process;
the before process finished before the after process started. Class time is the
sum of individual test durations in its TRX file. xUnit can run classes at the
same time, so class sums cannot be added to obtain project wall time. These are
single-process runs, not the four-shard gate layout.

| Revision | Commit SHA | Project wall time | TRX total | Passed | Failed | Skipped |
|---|---|---:|---:|---:|---:|---:|
| Before | `1c59d069b6a8b274011a282e52e8a93c3b761528` | 1,516.13 s | 14,657 | 14,640 | 2 | 15 |
| After | `4a28aaace2c65852b0c9408e408fe5a08eb7372d` | 1,712.76 s | 14,658 | 14,642 | 1 | 15 |

Every TRX result mapped to a class. The after revision includes one additional
`RemoteCompletionEnvelopePolicyTests` case from current `develop`. The before
run failed `TaskFileHistoryOwnHostTests.WorkspaceArtifactHistory_ReturnsTrailersAndVersionContent`
and `GitFactsBudgetTests.Unchanged_input_index_run_starts_no_git_process`. The
after run failed that same Git facts case. All tests in the three changed
classes passed in both runs.

The full TRX files and console logs are retained in the AGT-3507 run evidence
as `before.trx.gz`, `after.trx.gz`, `before-test.log`, and `after-test.log`.
The compressed files contain standard TRX. The corresponding complete class
rankings are `before-classes.csv` and `after-classes.csv` in the same run
evidence.

## Before: top 20 classes

| Rank | Class (`AgentStudio.Tests`) | Summed seconds | Tests | Failed |
|---:|---|---:|---:|---:|
| 1 | `BatchGatePilotServiceTests` | 797.57 | 214 | 0 |
| 2 | `ScopedOperatorAuthorizationTests` | 324.55 | 60 | 0 |
| 3 | `WorkspaceQuotaServiceTests` | 257.77 | 24 | 0 |
| 4 | `GitReadSpawnCensusTests` | 216.73 | 19 | 0 |
| 5 | `IamAccessMiddlewareTests` | 180.48 | 24 | 0 |
| 6 | `AcceptanceIntegrationRoundTripTests` | 179.56 | 72 | 0 |
| 7 | `MergeIntoDevelopRunnerTests` | 129.96 | 92 | 0 |
| 8 | `AccessSecurityLoginThrottleTests` | 121.10 | 27 | 0 |
| 9 | `NetworkedEdgeHardeningOwnHostTests` | 113.73 | 12 | 0 |
| 10 | `StandingGrantTests` | 106.72 | 13 | 0 |
| 11 | `GitHubAppBrokerTests` | 105.66 | 73 | 0 |
| 12 | `AccessSecurityTests` | 105.21 | 27 | 0 |
| 13 | `IamAccessStoreTests` | 96.25 | 26 | 0 |
| 14 | `OrchestratorDecisionReceiptTests` | 95.96 | 35 | 0 |
| 15 | `OperatorAgentRoleTests` | 87.95 | 44 | 0 |
| 16 | `IamHandlerAccessTests` | 86.29 | 35 | 0 |
| 17 | `DeployServiceTests` | 75.84 | 83 | 0 |
| 18 | `IamMigrationTests` | 73.89 | 17 | 0 |
| 19 | `TelemetryStoreTests` | 72.28 | 28 | 0 |
| 20 | `GitFactsBudgetTests` | 71.44 | 7 | 1 |

## After: top 20 classes

| Rank | Class (`AgentStudio.Tests`) | Summed seconds | Tests | Failed |
|---:|---|---:|---:|---:|
| 1 | `BatchGatePilotServiceTests` | 700.76 | 214 | 0 |
| 2 | `GitReadSpawnCensusTests` | 220.39 | 19 | 0 |
| 3 | `AcceptanceIntegrationRoundTripTests` | 204.84 | 72 | 0 |
| 4 | `MergeIntoDevelopRunnerTests` | 170.00 | 92 | 0 |
| 5 | `NetworkedEdgeHardeningOwnHostTests` | 163.97 | 12 | 0 |
| 6 | `IamAccessMiddlewareTests` | 154.41 | 24 | 0 |
| 7 | `IamHandlerAccessTests` | 150.20 | 35 | 0 |
| 8 | `OrchestratorDecisionReceiptTests` | 124.39 | 35 | 0 |
| 9 | `NetworkedSecurityEndpointTests` | 100.55 | 17 | 0 |
| 10 | `AccessSecurityLoginThrottleTests` | 96.21 | 27 | 0 |
| 11 | `AccessSecurityTests` | 95.03 | 27 | 0 |
| 12 | `BoardSourceIntegrationTests` | 94.45 | 2 | 0 |
| 13 | `GitFactsBudgetTests` | 92.90 | 7 | 1 |
| 14 | `DeployServiceTests` | 90.55 | 83 | 0 |
| 15 | `GitCardFactReadTests` | 88.85 | 11 | 0 |
| 16 | `SecretEndpointTests` | 80.73 | 9 | 0 |
| 17 | `TaskCoreEndpointTests` | 79.88 | 14 | 0 |
| 18 | `IamAccessStoreTests` | 79.26 | 26 | 0 |
| 19 | `FirstRunOwnerAccessTests` | 78.76 | 20 | 0 |
| 20 | `PrivateRepositoryReviewClaimTests` | 76.17 | 32 | 0 |

## Changes to the three baseline leaders

| Class | Before | After | Reduction | Test cases | Change |
|---|---:|---:|---:|---:|---|
| `BatchGatePilotServiceTests` | 797.57 s | 700.76 s | 96.81 s (12.1%) | 214 | Remove all hosted services from its test host, then restore the required review evidence projection worker. Factory-registered background workers had continued running. |
| `ScopedOperatorAuthorizationTests` | 324.55 s | 61.72 s | 262.84 s (81.0%) | 60 | Seed valid users and hashed sessions in each isolated store instead of repeating password hashing and login for route-scope cases. The forced-password-change cases still exercise the real login flow. |
| `WorkspaceQuotaServiceTests` | 257.77 s | 12.17 s | 245.60 s (95.3%) | 24 | Remove unrelated hosted services from the quota test host. They had made a 90-day fake-clock advance spend time in recurring background timers. |

The earlier focused after runs, recorded in `class-before-after.csv` in the
first AGT-3507 run evidence, also showed reductions for these classes. Their
class sums differ from this table because they used separate focused processes;
the paired complete-process values above are the comparison for this report.

## Project wall time and limit of this comparison

The whole-project wall time rose from 1,516.13 s to 1,712.76 s, an increase of
196.63 s (13.0%). The runner host was under heavy parallel load during these
runs. Several unrelated class durations also rose, and classes overlap in
execution, so this pair does not establish a speed improvement for the whole
project or for the four-shard batch gate. A comparable project-level throughput
measurement would need repeated before and after runs at controlled, similar
host load, using the same build and test settings and the gate's four-shard
layout when evaluating gate wall time.
