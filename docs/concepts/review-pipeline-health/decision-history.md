# Decision history — review pipeline health

Newest first. Every entry: date · decision · reasoning · card/commit reference.

## 2026-09-07: PrivateTmp=true made the 2026-08-18 "loss-free" restart claim incomplete
`ReviewSlotReconciler` re-adopting a detached `DurableReviewProcess` worker by
PID-liveness/workspace match proves the worker survives a restart; it does not
prove the worker's environment still works. The units also set
`PrivateTmp=true`, so every `agent-runner.service`/`agent-runner-review.service`
restart unmounted the unit's private `/tmp`, out from under a worker that
`KillMode=process` deliberately left running. The worker kept the deleted mount
(`/proc/<pid>/mountinfo` showed the `/tmp` mount root ending in `/deleted`), and
roughly an hour later `dotnet test`/`dotnet restore` failed with `MSB1025`,
`SocketException (99)`, or a NuGet `mkdtemp` `ENOENT` — infrastructure failures
the grader read as a product regression (`NewTestFailures`/`ProductFailure`).
Decision: ship `PrivateTmp=false` on both units (`deploy/systemd/agent-host.service`,
`scripts/remote-runner-onboard.sh`, `setup/NativeInstaller.cs`, and the
`10-agent-runner-hardening.conf` retrofit drop-in) so host `/tmp` survives a
restart untouched; add `DetachedWorkerTmpMountGuard` so `VerifyLive` positively
detects a torn-down `/tmp` mount at re-adoption time and fails the slot
immediately instead of letting a doomed build run for an hour; and classify the
three captured signatures as `ReviewInfra`/`EnvironmentalTransient` in
`RemoteReviewWorkspace`/`AgentOutcomeAnalyzer` before they ever reach
test-failure parsing. The 2026-08-18 entry below is amended by this one: a
restart is loss-free only when the worker's `/tmp` is also intact. →
AGT-2750, AGT-2749 (general infra-classification rule), AGT-2753 (lease-
preserving handoff).

## 2026-09-01: SSH gate shortcut retired after Remote Review replacement
Canonical Remote review now freezes deterministic build and test commands into
the immutable ReviewSubject. A Review Executor claims the ReviewAttempt through
the Task Server lease and fence contract, materializes the exact Result-SHA,
executes each command as a projected pipeline step, and returns fenced evidence.
AGT-2262 therefore removed `RemoteSshHost`, direct SSH process dispatch, the
process-local host semaphore, `$HOME/gate-work`, alias projection, and local
fallback. Local exact-subject execution remains only for local compatibility
and integration-owner gates. It is not a fallback from canonical Remote review.

## 2026-08-18: Review-plane restart is loss-free; adaptive parallelism and plane priority ship as recommendations, not auto-executors
The critical unknown blocking AGT-2645's auto-scaling proposal, whether
`sudo agent-runner-deploy config review RUNNER_MAX_PARALLELISM <n>` (the
AGT-2628 sanctioned mechanism) loses running review workers on its
`agent-runner-review.service` restart, is resolved: it does not.
`KillMode=process` on the unit (with an explicit hardening-drop-in comment)
keeps the restart's kill set to the daemon's own main PID; `ReviewSlotReconciler`
re-adopts every detached `DurableReviewProcess` worker by PID-liveness/workspace
match at daemon startup. Confirmed by a controlled test
(`Agent_runner_config_restart_uses_new_main_pid_and_role_environment_file_precedence`)
run against a fixture that simulates the restart race with a real detached
background PID. Decision: `AutoReviewQueueTelemetry` adds drain rate and
median duration to the existing queue-depth snapshot
(`GET /api/runner/auto-review-queue`); `AutoReviewQueueStagnationWatchdog`
alarms (rate-limited LogWarning, same shape as the AGT-2627 starvation
watchdog) when depth stays positive with no card started for 20+ minutes;
`AdaptiveReviewParallelismPolicy` and `CodingYieldPolicy` are pure two-sided-
hysteresis raise/lower and yield/restore decisions exposed as recommendation
endpoints only (`/api/runner/auto-review-parallelism-recommendation`,
`/api/runner/coding-yield-recommendation`), deliberately not wired to an
executor, since no reliable signal exists for the live per-host
`RUNNER_MAX_PARALLELISM` in effect, and auto-running a production service
restart from a background heuristic is a hard-to-reverse, shared-
infrastructure action left as an explicit opt-in follow-up. Plane priority
(review over coding under sustained backlog) stays FIFO within each plane;
only the coding-plane ceiling ever moves. → AGT-2645, results dossier.

## 2026-07-24: Persisted attempts are the distributed write authority
Run leases, review work, and lane-affecting Remote completion previously used
overlapping task, lease, session, and local-checkout identities. Decision: the
Task Server persists separate `RunAttempt` and `ReviewAttempt` records, with one
immutable `ReviewSubject` per expected Result-SHA. Attempt ID, monotonic fence,
authority epoch, and operation-scoped idempotency key are required on Remote
writes. Restart recovery and lease release retain the canonical attempt chain,
while stale, superseded, wrong-epoch, duplicate, and SHA-mismatched deliveries
receive typed outcomes. Infrastructure-only review retry creates a new
ReviewAttempt for the same subject and leaves the card in Auto Review. The
Task Server does not materialize or test the product repository for canonical
Remote review, and legacy tasks without attempts remain compatible. Daemon
claim replay resolves its successful acquire delivery before Ready-task
selection and returns the original lease after the card is in Progress.
Canonical log batches persist a hashed delivery receipt in the same durable
append, so an authority-store failure after append cannot duplicate the log on
retry. → AGT-2182.

## 2026-07-23 — Operator requeue opens a fresh review cycle (epoch semantics)
The stale-verdict guards anchored on the card's whole decision history; an
operator move out of `5e-escalated` was instantly re-escalated from pre-requeue
artifacts (requeue ping-pong). Decision: the card's most recent recorded
transition INTO `4-auto-review` (provenance, single MoveAsync hook) is the
**review-cycle epoch** — older verdicts are a closed cycle: never backfilled,
never counted as this cycle's verdict. Automatic moves never bump the epoch, so
loop protection stays intact. → AGT-2260 (full scope: artifact rotation,
attempt-epoch UI), delivered core in `fix: operator requeue opens a fresh review cycle`.

## 2026-07-23 — SSH-gate bridge: gates run on the remote host (shortcut)
Serial local gates (15–27 min each, repo contention, load flakes) were the
throughput floor. Decision: bridge now, clean solution after — the gate locates
the subject SHA in the host's per-project repos, runs the build-profile commands
over ssh in a disposable worktree (2 in parallel, remote-side timeout), falls
back to local on any remote infrastructure problem. Full suite: **2 min 11 s**
remote vs 15–27 min local. Explicitly a self-contained bridge: superseded by
claimable remote gate steps (AGT-2229), then removed wholesale (cleanup card
`ssh-gate-shortcut-cleanup`). → AGT-2222 tranche 2.

## 2026-07-23 — Host slots 5 → 20 (operator-directed)
The remote host idled at load 0.34 with 12 CPUs/62 GB while 5 slots capped the
fleet; runs are LLM-bound. Raised `RUNNER_MAX_PARALLELISM` to 20; watch host
load, trim to 14–16 if simultaneous build phases thrash.

## 2026-07-23 — Build-profile filter carries the poison-class excludes
MachineBound taggings only reach branches that contain them (stale-branch trap);
the build-profile test filter applies to EVERY gate regardless of branch age.
Decision: known flaky/self-blocking test classes are excluded in the profile
filter until gates test the subject merged onto current develop (AGT-2258),
then the filter shrinks back. Trade-off consciously accepted: profile-level
excludes are a blunt instrument, tracked for rollback.

## 2026-07-23 — Post-processing admission is derived, not pinned
A forgotten `PostProcessing:MaxParallelism = 3` override silently beat the
capacity derivation (cores/2, clamped 3–12) and starved whole projects out of
the queue. Decision: convention over settings — the derivation is the default,
the key remains only as a conscious override. → commit
`fix: drop fixed post-processing parallelism override`.

## 2026-07-22 — Two-pool admission (build vs api) is the target design
Pipeline steps declare a resource class: build (coding runs + gates, build
slots), api (LLM steps, own pool), mini (free). Admission by lane age with
project fairness. → AGT-2222 remainder, AGT-2229 host orchestrator.

## 2026-07-22 — MachineBound is the quarantine convention for load-sensitive tests
Tests that fail under machine load but pass at idle get
`[Trait("Category","MachineBound")]` and are excluded from gate runs
(`Category!=MachineBound`). Reference commits: `227b7041`, night-flake tail
additions of 22./23.07.

## 2026-07-23 — Everything moves to the remote host; tunnels are interim only
Operator direction: the target picture is the full system (task server, studio,
runners) living on the remote host — the Remote-Ready line. The ssh reverse
tunnel that unblocked UI-evidence cards (AGT-2240/2254) is explicitly an
interim: any bridging solution must be removable without residue once the
system itself runs remotely. → ui-evidence card + AGT-2229 line.
