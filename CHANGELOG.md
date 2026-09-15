# Changelog

All notable changes to this project will be documented in this file.

The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
uses semantic versioning for tagged releases.

The current development version is declared in [`VERSION`](VERSION), the
repository's single version source. It has not been published as a tagged
release yet.

## [Unreleased]

### Added

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
