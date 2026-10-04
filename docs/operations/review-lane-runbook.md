# Review lane runbook: capabilities, empty claims, and the stall alarm

Operator runbook for the Remote Review lane: which capabilities a Review
Executor must register, how to read an empty review claim, and which alarm
fires when pending ReviewAttempts stop being claimed. Background incident:
AGT-2987 (2026-09-27 15:08Z to 2026-09-28 14:54Z), where the lane went silent
for 24 hours with no alarm.

## What a review claim is matched against

Every ReviewAttempt carries a sealed library-v1 plan
(`contracts/TaskServer.Contracts/ReviewLibraryStepPolicy.cs`). Each step lists
its required capabilities: `review:library-step:v1`, the baseline and
preparation keys, the CLI and provider keys of semantic aspects, and one
`toolchain:*` key for every tool the command invokes.

The toolchain keys come from one published table,
`ReviewLibraryStepPolicy.ToolchainRequirements`:

| Key | Command tokens that require it | Executable the host probes |
|---|---|---|
| `toolchain:dotnet` | `dotnet` | `dotnet` |
| `toolchain:node` | `node`, `npm`, `npx` | `node` |
| `toolchain:playwright` | `playwright` | `playwright` |
| `toolchain:compose-render` | A declared Compose render script | `docker compose version` |

Both Task Server implementations (the backend `V1ReviewPlaneEndpoints` claim
and the standalone `TaskServerReviewStore.ClaimReviewAsync`) match a plan
against the executor's **registration** capability set, not against the
minutely advertisement.

### Decision (AGT-2987): registration carries what plans require

The card offered two options: register every key the library can require, or
let the claim check read the latest ready advertisement. The implemented
option is **registration**:

- `RunnerCapabilityProbe.ReviewRegistrationCapabilities` adds every key from
  `ToolchainRequirements` whose probe executable is on the review unit's
  `PATH`, plus `toolchain:compose-render` when the Docker Compose plugin answers its
  version probe. The advertisement uses the same probes.
- Both server implementations already filter on registration. Changing the
  runner fixes both without a second freshness rule on the claim path. A
  claim check that reads the advertisement would go silent again whenever an
  advertisement is stale.
- A toolchain that disappears after startup is still handled by the existing
  capability-failure drain (`toolchain:*` failure reports pause the executor).
- A new key added to `ToolchainRequirements` reaches plans and registrations
  in the same build. The contract test
  `ReviewClaimCapabilityRegistrationTests.Registration_is_a_superset_of_every_toolchain_key_the_review_catalogue_can_require`
  fails if they drift apart.

`RUNNER_REQUIRED_CAPABILITIES` is no longer needed for toolchains. It stays an
additive override: its keys are appended to the registration and sent as claim
requirements. The interim host fix
(`RUNNER_REQUIRED_CAPABILITIES=toolchain:dotnet,toolchain:node` in
`/etc/agent-runner/runner-review.env`) can be removed once the review unit
runs this release. Leaving it in place is harmless.

### Checking a host

1. Confirm the tools resolve with the unit's environment:
   `sudo systemctl show agent-runner-review.service -p Environment` for its
   `PATH`, then `command -v dotnet node playwright` with that `PATH` as the
   review service account.
2. After a restart, the registration and the advertisement both list the keys.
   The Execution Hosts view (or `GET /api/v1/management/remote-hosts` on the
   standalone Task Server) shows the advertised side.
3. A missing executable means a missing key. Install the tool on the unit's
   `PATH` and restart the review unit so it registers again.

## Reading an empty review claim

`POST /api/v1/runners/{id}/review-claims` answers HTTP 200 with
`status: "empty"` whenever nothing is handed out. Every empty answer now
carries a typed `reason`:

| `reason` | Meaning | Action |
|---|---|---|
| `queue-empty` | No pending attempt exists that any executor could take. | None. |
| `no-available-slot` | The request declared zero free slots. | None. |
| `executor-paused` | A drained capability paused this executor until its cooldown ends. | See the capability failure in Execution Hosts. |
| `quota-deferred` | Quota admission deferred a legacy plan. | Wait for the quota window. |
| `capability-admission` | Standalone Task Server only: an advertised capability is stale, missing, or not ready. | Read `message`. |
| `unclaimable-plan-requirements` | Pending attempts exist, but their sealed plans require capabilities this executor did not register. | Add the named keys (see above). |

For `unclaimable-plan-requirements` the response also carries:

- `missingCapabilities`: the union of keys the executor lacks, sorted.
- `unclaimableAttempts`: up to 20 attempts, oldest first, each with
  `attemptId`, `taskKey`, `createdAt`, and its own `missingCapabilities`.

Logs on both sides:

- Server: `review-claim-unclaimable reason=... attempt=... task=... executor=... missing=... pendingSince=...`
  at warning level, at most once per attempt per hour. Every unclaimable
  attempt is logged, including those beyond the 20 the response names and, on
  the standalone Task Server, those on later claim pages. The server scans
  each page for a claimable attempt before it returns an empty response.
- Runner journal: `review claim warning: reason=unclaimable-plan-requirements missing=... unclaimableAttempts=... oldestAttempt=... oldestTask=...`.
  It repeats every 15 minutes for the same key set, and at once when the set
  changes.

## The stall signal and alarm

`GET /api/runner/auto-review-queue` reports:

| Field | Meaning |
|---|---|
| `isStagnant` | Either side of the backlog is stagnant (see below). |
| `reviewClaimStagnant` | Pending ReviewAttempts exist and none has been **claimed** for `stagnantThresholdMinutes` (default 20). |
| `stagnantSince` | Earliest start of a stagnant period. |
| `lastReviewClaimAt` | Latest delivered claim. It is rebuilt from persisted leases after a restart. |
| `oldestPendingAttemptId`, `oldestPendingTaskKey`, `oldestPendingAttemptCreatedAt` | The attempt that has waited longest. |
| `unclaimableReason`, `unclaimableMissingCapabilities` | Why that attempt was refused, as last observed by the claim endpoint. |

The claim clock starts at the later of the last delivered claim and the oldest
pending attempt's creation. Only a delivered claim restarts it. Coding runs,
legacy post-processing dequeues, and claims the server deferred before
delivery (capability or quota) do not. Claim times are tracked per lease, so
when several concurrent claims are deferred in any order, none of them counts
and an earlier delivered claim keeps its time. Before AGT-2987 one shared clock was
reset by any legacy dequeue, which kept `isStagnant` false for the whole
incident. The legacy local queue keeps its own clock, reset only by legacy
starts.

When `reviewClaimStagnant` is true, `PipelineHealthService` raises
`pipeline_health_alarm kind=lane-drain-stalled` for the project that owns the
oldest pending attempt (or `review-plane` if the card is not found). The
summary reads `4-auto-review has N pending review attempt(s) and no claim for M min`.
The detail names the oldest attempt and task, the last claim time, and either
the unclaimable reason with its missing keys or a hint to check that an
executor is registered and polling. The alarm is appended to the project's
orchestrator feed, marks the project's pipeline-health snapshot as `alarm`,
repeats at most hourly, and clears when a claim resumes. The file-based lane
drain alarm (no card left the lane for an hour) is still raised separately.

## Triage order

1. Alarm or `isStagnant` with `reviewClaimStagnant: true`: read
   `unclaimableReason`.
2. `unclaimable-plan-requirements`: fix the host toolchain named in
   `unclaimableMissingCapabilities`, or add an override key, then restart the
   review unit.
3. No reason recorded: no executor has polled. Check that the review unit is
   running, registered, and not paused (`executor-paused`).
