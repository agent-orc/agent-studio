# Changelog

All notable changes to this project will be documented in this file.

The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
uses semantic versioning for tagged releases.

The current development version is declared in [`VERSION`](VERSION), the
repository's single version source. It has not been published as a tagged
release yet.

## [Unreleased]

### Fixed

- Auto Review wait classification now uses the restart-resume policy's
  integration ancestry decision, so a delivery merged by a later gate is
  reported as awaiting its completion transition instead of awaiting delivery
  integration (AGT-2860).
- Lost-worker recovery now retains the latest diagnostic stderr and system
  lines across long worker logs, so earlier crash evidence is not silently
  dropped after each 2,000 scanned lines (AGT-2870).
- Worker resource envelope: the task ceiling is 512 per core of fair share
  (minimum 1024) instead of 128. `pids.max` counts threads, and a worker with a
  .NET test host hit the 307-task ceiling of a 12-core, 5-slot host; the
  runtime aborted with `PAL_SEHException` and the run was lost.
- Runner hosts no longer accumulate processes that outlive their run. Detached
  coding and review workers start with `MSBUILDDISABLENODEREUSE=1` and
  `DOTNET_CLI_USE_MSBUILD_SERVER=0` and shut the build servers down on teardown,
  worker teardown kills whatever is left in the worker cgroup and reports the
  count on the `worker-envelope` line, and daemon start moves processes no
  worker owns any more out of the unit cgroup (killing only aged strays of
  finished attempts) so cgroup delegation succeeds on a host that has run
  before instead of logging `applied=no` and running uncapped (AGT-2868).
- A result transfer that fails because the Task Server is restarting is now
  retried from the persisted runner slot instead of waiting for the next daemon
  restart. The Coding slot stays in phase `finalizing` with its retry
  bookkeeping and the delivery it already secured, the worktree and durable
  worker result are retained, the lease is not released, and the running
  daemon's poll loop re-drives the same idempotent startup-reconciliation step
  once `/api/system/about` answers again (15 s, 30 s, then every 60 s). Retries
  do not stop at the run timeout; outliving it only adds a journal line. The
  journal carries one line per retry with its reason and the final completion
  names the retry count (AGT-2869).
- A remote run whose heartbeat is older than the lease it was renewing, or a
  remote-routed Progress card whose job-folder replay has stopped, is projected
  as `remote-stale` with the last runner event named, instead of continuing to
  read as `remote-running`. An operator and the acceptance rail can now tell a
  phantom run from a live one (AGT-2869).

### Added

- Onboarding declares the kernel limits parallel test suites need:
  `/etc/sysctl.d/90-agent-runner.conf` sets `fs.inotify.max_user_instances` to
  1024 and `fs.inotify.max_user_watches` to 1048576, applies them, and verifies
  both before the systemd phase (AGT-2868).

## [0.8.0] - 2026-09-18

Operations release. Every detached coding and review worker on a Linux runner
host now runs in its own cgroup with a CPU and task ceiling derived from the
host's declared slot counts, so one runaway run can no longer saturate the
host for its siblings. The Update Service evaluates the preconditions of its
post-restart verification matrix in the preflight instead of discovering them
after the stack is down, and the `db-touch` sentinel follows the installed
update contract instead of a DevTools flag. Review gate coverage now includes
the runner and Task Server test projects.

### Added

- Per-worker resource envelope on Linux runner hosts: each detached coding and
  review worker runs in its own cgroup under the role unit (`Delegate=cpu pids`,
  `DelegateSubgroup=daemon`) with `cpu.max`, `cpu.weight` and `pids.max` derived
  from `RUNNER_HOST_CODING_SLOTS` and `RUNNER_HOST_REVIEW_SLOTS`; every run
  reports a `worker-envelope` line with CPU seconds and peak tasks, review
  admission clamps to the same budget, and onboarding writes the unit lines
  and peer slot counts (AGT-2866).
- Update Service preflight evaluates the preconditions of every post-restart
  verification step against the running instance and refuses with a named,
  actionable error when one cannot pass (AGT-2865).
- Review gate coverage: `runner.Tests` and `task-server.Tests` run as
  deterministic verify commands, so runner and Task Server races are caught in
  review instead of in the promotion gate (AGT-2864).

### Changed

- The `db-touch` sentinel (`POST /api/_internal/probe`) has its own gate,
  `UpdateService:ProbeEnabled`; unset, it is open exactly while
  `.metadata/stable-approved-tag` exists. `DevTools:UpdateStableEnabled` still
  opens it for compatibility but is no longer part of the contract (AGT-2865).
- `CliProcessReaperTests` runs in a non-parallel collection: it asserts the
  process-global reap total, which another collection reaping a live workspace
  could advance mid-test (AGT-2864).

### Fixed

- Remote review: deleting a settled review slot record is final. A heartbeat
  renewal whose persistence was scheduled after the terminal delete used to
  write the record back, leaving an orphan slot that the next daemon generation
  adopted and reported a second time (AGT-2864).
- `setup.Tests`: the join-token file expectation is normalised with
  `Path.GetFullPath` on every platform, so the Windows pre-develop gate no
  longer fails on a path separator (ef81146ae).

## [0.7.0] - 2026-09-17

Operations release, the third of the day, closing what the 0.6.0 rollout
showed. The Update Service probes the frontend the way users reach it and
reports a frontend-only failure as degraded instead of rolling the checkout
back underneath a running backend. A coding run that hits the run timeout with
a salvaged worktree gets one automatic finishing round instead of an
escalation. Review attempts record the release that graded them, adoption
after a daemon restart names a release mismatch, and the operator can drain
instead of adopt.

### Added

- Automatic continuation round after a run timeout with a salvage commit,
  bounded to one per delivery generation; escalations name the salvage ref
  (AGT-2861).
- Review attempt provenance: release id and worker binary in the grade and in
  the adoption log line; drain mode for the review daemon restart (AGT-2863).

### Fixed

- Update Service: the frontend probe tries localhost, 127.0.0.1 and ::1; a
  frontend-only verification failure after a successful backend restart ends
  the run as degraded with listener evidence instead of a rollback (AGT-2862).

### Added

- Review worker release provenance: every review attempt records the agent-host
  release and resolved binary path of the detached worker that produced its
  verdict. The daemon logs `worker-release` / `daemon-release` on adoption and
  one notice per adopted attempt running a superseded build, the grade file's
  *Immutable subject proof* block names both releases, and the card carries a
  `review_graded_by_superseded_release` timeline entry and the release pair on
  its review projection (AGT-2863).
- Opt-in review release drain:
  `agent-runner-deploy --restart-review-drain` drains Review before the
  unchanged promote step, and `RUNNER_REVIEW_RELEASE_DRAIN=1` makes an already
  restarted daemon hold claims until its adopted superseded attempts finish.
  Neither mode ever ends an adopted worker (AGT-2863).

## [0.6.0] - 2026-09-17

Operations release, cut on the day of 0.5.0 from what that rollout exposed.
The pre-develop gate now runs the backend test work package for backend-only
diffs and only the frontend package for frontend-only diffs, so a Windows-only
red test no longer reaches develop while frontend cards stop paying for the
whole backend suite. The update-service restart drill, the board conditional
read, task-key dedup and review-plane diagnostics tests are stable under suite
load. The acceptance rail reads the same integration truth as the card and no
longer refuses integrated cards in a loop. The review executor's torn-down-/tmp
classifier yields to a real test-run summary, cleans its own temp residue, and
the pilot preparation cache has a product-owned, bounded location. A task-server
restart no longer strands cards with a passed review. The activity view renders
agent events as events, the branch-sweep panel reclaims stale branches, and
gate-health alerting attributes branch defects to the branch instead of to
every card.

### Added

- Pre-develop gate work-package matrix: backend-only, frontend-only, mixed and
  docs-only diffs select the matching test commands; the selection is logged
  per gate run (AGT-2854).
- Stale-branch sweep: all-namespace classification, report and
  operator-confirmed reclamation with a project panel (AGT-2794).
- Activity view renders Claude tool_progress and agent events as events, with
  synced CLI docs and a runner-group DOM spec (AGT-2814).
- Branch-defect attribution and integration-branch gate-health alerting; the
  three largest components split with lowered baselines; one lint chain
  (AGT-2819).
- Automatic continuation after a task-server restart during the pre-develop
  gate: the un-gated merge is detected and re-verified (AGT-2849).
- Temp and cache hygiene: test fixtures remove their temp roots, a suite-level
  guard keeps the temp root from growing, the review worker redirects and
  cleans its own temp residue, and the pilot preparation cache lives under a
  bounded, documented product-owned root (AGT-2858).

### Fixed

- `UpdateServiceRestartIdentityDrillTests` no longer fails under gate load
  (AGT-2855); `BoardConditionalReadTests`, `DuplicateTaskKeyTests` and
  `V1ReviewPlaneDiagnosticsEndpointTests` share no state with their
  neighbours any more, including a real race in `TaskJsonFile` /
  `TaskMutationService` (AGT-2859).
- The acceptance rail consults the card's integration record and the delivery
  ancestry instead of a separate "pending" computation, and no longer retries
  every 30 seconds (AGT-2856).
- The review executor's `TmpMountTornDown` classifier ignores signature text
  inside test names and output once a test-run summary exists (AGT-2857,
  operator hotfix fcdb68b24).
- A task-server restart no longer exhausts the auto-review post-processing
  deferrals for cards with a passed review; killed gates whose merge was
  pushed later complete on the next pass (AGT-2860).
- The dead `overview-pane/lifecycle-phase.util` re-export shim is gone
  (AGT-2819).

## [0.5.0] - 2026-09-17

Operations release. The Windows integration gate re-runs the tests that
failed once on the same build and records them as flaky instead of failing
the merge, takes its budget from the gate-run policy instead of a hard-coded
30 minutes, and the review executor caches baseline verify results per
repository, baseline and command. Review command watchdogs no longer kill
healthy backend test runs, the adaptive review parallelism advisor reads the
attempt queue it is meant to protect, a degraded result summary no longer
loses a delivered remote run, and the Update Service can complete an upgrade
because the runtime identity check reads the intended manifest. The board
tells the truth about parked and unpickable cards, only reports "delivered"
with proof, and the token panel marks the live run and shows the reasoning
level; task list reads answer 304 with trimmed payloads.

### Added

- A build gate that never reaches a verdict is recorded as an interrupted run.
  The pre-develop gate writes a durable in-flight record before the merge, and
  startup recovery rolls the integration branch back to the exact pre-merge tip -
  the oldest one when several un-gated merges stacked up - or resumes the merge
  whose verdict for that exact SHA is already durable. Affected cards are
  integrated again without a new remote review. A branch whose rollback anchor is
  gone or already published escalates instead of being rewritten (AGT-2849).

- Windows integration gate: one targeted re-run of exactly the failed tests
  on the same build, green re-run keeps the gate green and records the names
  as flaky under the review executor's classification (AGT-2853).
- Review executor: baseline verify results are cached per repository,
  baseline SHA and command (AGT-2843).
- A parked card says why it is parked (AGT-2816); a card that cannot be picked
  says so where it claims to be queued (AGT-2818); "delivered" requires an
  integrated delivery or a named deliverable without code (AGT-2817).
- Token panel on the task Overview shows the reasoning level and separates
  the run hierarchy from the pipeline steps (AGT-2811); "Current" marks the
  live run, not the newest one (AGT-2810).
- ETag/304 for task list and grouped reads with a trimmed payload (AGT-2703).

### Changed

- `integration.status=integrated` now requires the delivery to be reachable from
  the pushed remote integration branch. A delivery that only a local branch or a
  worktree ref can see reports the new `merged-locally` status, naming the
  commits `origin/<branch>` cannot reach. Acceptance and the acceptance rail keep
  moving such a card, because the origin push is a separate, backstopped step
  (AGT-2849).
- The completion contract, the archive guard, and the Human Review accept
  action read the same merge question, so a delivery awaiting only its push is
  no longer refused as an unintegrated delivery and is no longer offered a
  second merge. The badge still names the missing push (AGT-2849).

### Fixed

- The pre-develop merge gate honours the gate-run budget policy (AGT-2846).
- Review command watchdogs (silence, no-CPU-progress) are carried into the
  detached review worker and no longer kill healthy backend test runs
  (AGT-2851).
- Adaptive review parallelism advisor reads the remote attempt queue instead
  of the local post-processing queue (AGT-2848).
- A degraded result summary no longer turns a delivered remote run into a
  lost run (AGT-2850).
- Update Service: the runtime identity check reads the intended build
  manifest so an upgrade can pass (AGT-2847).
- UpdateServiceRestartIdentityDrillTests isolate their update-service
  factory state (AGT-2852); GateFlakyRerunBehaviorTests run on Windows
  (operator fix 651d7267a).

## [0.4.0] - 2026-09-16

Operations release. The review executor and the integration gate stop
needing an operator to recover from their own failures: a ReviewInfra verdict
schedules its replacement attempt, auto-review postprocessing no longer waits
for an executor that is already idle, a coding daemon restart keeps its
detached workers, and a successful run never marks its provider as limited.
Branch reclamation is wired into integration, archive and promotion. The
Windows integration gate prepares the repository with a complete base
environment, runs verify commands without the Studio's own listener, and no
longer fails on tests that only measure the host. The product renders one
disclosure grammar (ADM-17), links dossiers wherever they are named, reviews
the models it actually runs, and shows the true integration state of a card.

### Added

- A card held in a pickup lane now says so where it claims to be queued
  (AGT-2818). `TaskInfo.pickupHold` is a read-time projection built by the pure
  `PickupHoldPolicy` from the same facts the runner admission gate consults, and
  carries the mechanism (dependency gate, dispatch rejection, epic container,
  crash backoff, pickup policy), the specific reason, when the hold started, how
  long it has lasted, and the ways out. A `releaseGate` edge pointing at an
  archived, never released target is now classified `unsatisfiable` rather than
  "waiting", earns the same once-per-card runner warning a dependency cycle
  already got, and renders as "this gate can never open" instead of "waits for
  release". The durable `remoteDispatchRejection` is rendered on the board card
  as well as the detail header, with its code and instant. `GET /api/pickup-holds`
  and a boot sweep list every held card across all projects. Everything here is
  report-only: releasing a validation gate stays an operator decision, so the two
  resolutions are offered and never taken. See
  [`docs/concepts/pickup-hold-visibility.md`](docs/concepts/pickup-hold-visibility.md).

- Branch reclamation (AGT-2793) is now wired into the product path instead of
  sitting unreachable: `BranchReclaimTriggerService` fires after a successful
  delivery integration, after a card archives, and (via a new
  `POST /api/git/branch-reclaim/promotion` endpoint called from
  `scripts/release/promote-develop-to-main.sh --project <name>`) after
  develop is promoted to main. Every deletion is appended to a per-project
  `reports/git-branch-reclaim.jsonl` evidence file and, for the two per-task
  triggers, echoed onto that task's own timeline as a `branches_reclaimed`
  entry.
- A bare-remote integration test seeds all six managed ref namespaces
  (`task/*`, `runner/*`, `delivery/*`, `agent-studio/results/*`,
  `agent-studio/salvage/*`, `agent-studio/quarantine/*`) and asserts exactly
  the expected refs are deleted while proof commits stay reachable from
  `main`; a replay test proves a task can be reissued after its `results/*`
  ref is deleted, and a wiring test suite proves each of the three triggers
  fires on its transition and stays silent when that transition fails or
  never lands.

### Fixed

- The local merge gate no longer hands a verify command the Studio's own
  listener (AGT-2840). `api.sh start` runs the backend through `dotnet run`,
  whose launch profile exports `ASPNETCORE_URLS` into the process environment
  even though the port comes from `--urls`; the gate passed that environment to
  `dotnet test`, and the five `ConnectorProfileTests` that boot
  `WebApplicationFactory<Program>` read the Studio's URL as the connector's own
  and failed with "The connector may listen only on http://[::1]:5031" - only on
  the machine that runs the Studio, sending three already-reviewed cards
  (AGT-2825, AGT-2826, AGT-2827) to Human Review. `HostListenerEnvironment` now
  names the keys that carry a listener (`ASPNETCORE_URLS`, `URLS`,
  `DOTNET_URLS`, the `ASPNETCORE_*_PORTS` pair, and anything under `Kestrel__`)
  and `BuildTestGateRunner` drops them from every verify child, the way the
  preparation gate already curates the prepare script's environment. The
  connector tests additionally boot with those keys set aside, so the class is
  hermetic against any launcher.

- A delivery blocked by the commit candidate gate said so instead of escalating
  without a reason (AGT-2828). WEB-21 captured 12 screenshots, the gate warned
  `binary-surprise` on every one of them, nothing was committed, and the card
  landed in `5e-escalated` with cause `no-completion-signal` and an empty parked
  reason. Now: the gate records what it held back as
  `withheld-commit-candidates.json` on the card (path plus finding codes per
  file); the no-completion-signal park carries its reason through the lane move,
  so the parked marker names the escalation category, the gate, and the count
  instead of being empty, and the card lists which files and why; evidence assets
  under a path the project declares in `ProjectSettings.EvidenceAssetPaths` (a
  recognized asset type at or below 5 MiB) commit without a manual step; and
  `POST /api/tasks/{id}/git/withheld-candidates/commit` is the operator action
  that commits the withheld set after review. Explicit review clears the warnings
  that withheld the files, never a block, so secret material stays refused.

- Concurrent Remote Review workers shared one host-global .NET build server
  (AGT-2831). The attempt workspace fenced every writable path per attempt, but
  Roslyn's `VBCSCompiler` listens on `/tmp/<pipename>` and reusable MSBuild
  worker nodes on `/tmp/MSBuild<pid>`, neither of which honours `TMPDIR`, and
  both keep the working directory of the attempt that started them. At
  `RUNNER_MAX_PARALLELISM=4` a later attempt connected to a server answering
  from a deleted tree and blocked, holding its review slot at near-zero CPU for
  the whole command budget. Every review command - candidate, baseline and
  dependency preparation - now runs with its own server-free build namespace
  (`MSBUILDDISABLENODEREUSE=1`, `DOTNET_CLI_USE_MSBUILD_SERVER=0`,
  `UseSharedCompilation=false`, attempt-local `MSBUILDDEBUGPATH`), applied over
  the immutable plan so a plan can never re-enable a shared server.
- A review verify command that blocks instead of working is now reaped on a
  bounded budget. `RUNNER_REVIEW_NO_CPU_PROGRESS_SECONDS` (default 900, `0`
  disables) kills a command whose whole process tree fails to burn one percent
  of one core within the window and reports the attempt as
  `ReviewInfra/NoCpuProgress`, checked before baseline comparison so a hang is
  never graded as a product regression.

- `BranchRetentionAction.Namespace` and `.TaskKey` were never populated on
  the actions `GitBranchRetentionService.RunRepository`/`ReclaimForTask`
  actually return (always `null`), so the evidence and reason-text those
  actions carry were silently empty for the periodic sweep and every
  per-task reclaim; both fields are now set on every returned action.
- Integration of a local project no longer runs in the developer checkout
  (AGT-2832). Delivery merges happen in a Studio-owned worktree derived from
  the repository path and reset before each integration, so unrelated
  uncommitted edits in that checkout can no longer refuse an integration
  ("Integration working tree has uncommitted changes; refusing to merge", most
  recently QS-100) and a successful merge no longer switches the checkout's
  branch. The worktree stays detached, so the integration branch remains
  available to the person working in the project.

## [0.3.0] - 2026-09-14

Model routing now names a Claude model and a reasoning level per tier instead
of falling back to a positional pick, floors economy mode at Sonnet with low
reasoning, and migrates retired model ids forward. The Update Service stops the
stack before the locked restore, installs a candidate manifest only after a
verified restart, and waits for a cold backend compile. The repository
definition carries an optional release section, Docker Compose brings up the
product without preset credentials, and the task detail shows one header row
for a result with the regression radar below the runs.
### Added

- Economy-mode model routing floor: `sonnet-low` (`claude-sonnet-5`/`gpt-5.6-sol`
  at `low`) is now the lowest tier economy mode may select for feature and bug
  cards, closing a gap where an unrecognized vendor catalogue's positional
  fallback could silently route a Claude CLI card to `claude-haiku-4-5` with no
  thinking level (AGT-2793, AGT-2807). `ModelRoutingPolicyRegistry.Recommend()`
  now always returns an explicit thinking level.
- `POST /api/admin/maintenance/backfill-thinking-levels` reports (default) or
  applies (`apply=true`) a policy-derived `thinkingLevel` for existing cards
  that carry a `model` with no level.
- The model-level badge now renders a visible placeholder when a model has no
  thinking level, instead of omitting the level silently.

## [0.2.0] - 2026-09-13

First tagged Stable release. It moves the Windows Stable from the untagged legacy checkout (cf1997665) to the immutable tag v0.2.0 through the release contract: build manifest, candidate and approved tag, locked dependency restore, runtime identity verification.
### Added

- Project-owned Stable release identity and restore rules, with lock-file and
  exact-pin-plus-registry-integrity validation, fixture coverage, and a legacy
  Stable first-release runbook.
- Stage M1 repository execution definitions, shared preparation manifests,
  content-addressed npm, NuGet, and Playwright caches, Linux Runner stable
  checkouts with leased subject worktrees, proposal cards, and the project
  Settings > Execution surface.
- Durable restart continuity for local and Remote runs, review aspects, and
  pipeline post-steps, including operator-visible bridge and loss events plus a
  Windows Studio release drill.
- Initial open source community and release-hygiene baseline.
- Dossier relevance-review metadata, managed review writes and history,
  configurable review-due policy, and review tags, tooltips, filtering,
  sorting, and viewer recording controls.
- Local run teardown now owns and ends complete Windows and Linux process trees,
  retries worktree removal, renames persistent stale directories aside, and
  periodically sweeps orphan directories that lost their `.git` link. Worktree
  preparation failures are visible on cards and timelines, retry with bounded
  backoff, and park with a path-specific blocker after five attempts. Local run
  admission also warns when a configured project URL port already has a listener.

### Changed

- Dossier list cards now use a wide clamped summary, anchored footer actions,
  copyable keys, and labelled lane-colour references with immediate complete
  tooltips.

### Fixed

- Stable release manifest generation and candidate preflight no longer assume
  a product-wide `backend/packages.lock.json`; Agent Studio now declares and
  commits its NuGet lock, and promotion rejects missing or stale lock state.
- Develop-to-main promotion now supplies a configurable annotated-tag identity
  independently of host Git configuration and records post-gate tag or atomic
  push failures in the durable promotion record.
- Production builds no longer load the task-detail component graph with the
  initial task board bundle, restoring release-budget headroom without raising
  the configured limit.
- Process launches across backend, retention, setup, and repository Node
  scripts now suppress Windows console windows, drain redirected CLI stderr,
  and carry repository-wide regression guards for future spawn sites.
- Public-demo execution-route inventory now includes
  `POST /api/v1/reviews/attempts/{attemptId}/reclaim` in the `Continue` path.
- Frontend release coverage is green again for the `CliAdminPanelComponent`
  smoke test, the `CliModelsPanelComponent` known-CLI grouping test, the
  `ExecutionAssignmentCardComponent` delivery-failure test, the
  `WorkspaceOverlaysComponent` smoke test, and the Dossier and Wiki
  `GlobalSearchComponent` navigation tests. The additionally exposed
  `WorkbenchTabHostComponent` catalogue-backfill test is isolated from
  persisted tab state as well.
