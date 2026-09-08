# Deployment regression scenario (AGT-2739)

One reduced, deterministic end-to-end run that every deployment card and every
release proves itself against, instead of ad-hoc checks. Reduced and
repeatable was an explicit operator requirement (2026-09-06): *"Mehrfach hier
klein testen, reduziert. Testszenario ist wichtig, wir brauchen auch
zukünftig Regressionstests, das muss sauber sein."* This page is the scenario
itself, growing forward: when a card adds a feature to the distributed
topology, it adds a step here rather than inventing a parallel ad-hoc check.

## What it proves

The scenario boots the Task Server / Studio BFF / Runner topology (the same
components [`task-server.Tests/TopologyTests.cs`](../../../task-server.Tests/TopologyTests.cs)
proves individually) and drives one seeded fixture through an ordered set of
steps over real HTTP against real subprocesses. The coding boundary uses a
fixed fake CLI, while Task Server, Runner, HTTP, Git, backup, and restore are
real processes rather than an in-memory test host. The fixture and steps are data
([`testsupport/scenario/fixture.json`](../../../testsupport/scenario/fixture.json),
[`testsupport/scenario/steps.json`](../../../testsupport/scenario/steps.json));
the assertions are typed C#
([`task-server.Tests/ScenarioContext.cs`](../../../task-server.Tests/ScenarioContext.cs)).

`smoke` runs the first six steps (bootstrap through auto-review) and is the
gate every deployment card build must pass. `full` runs every step and is the
deeper release gate.

| # | Step | Level | Proves |
|---|---|---|---|
| 1 | Bootstrap principals | smoke | The in-process target issues a runner credential over the management API; Compose verifies the fixed, startup-bootstrapped runner principal. |
| 2 | Register runner | smoke | A real runner binary registers against the Task Server. |
| 3 | Create task | smoke | The seeded workspace/project/task exist in `2-ready`. |
| 4 | Claim task | smoke | The runner claims the task under a fenced lease. |
| 5 | Run with the fake CLI | smoke | The runner drives a fake coding CLI that runs the fixture's known-passing and known-failing checks, commits, and pushes; the task reaches `4-auto-review`. |
| 6 | Auto-review | smoke | A review subject is claimed, reported, and cleaned up; the queued orchestration run is settled; the task reaches `5-human-review`. |
| 7 | Orchestrator chat turn with context receipt | full | A chat turn round-trips with a persisted context receipt (token budget, sources). |
| 8 | Backup | full | `POST /api/v1/management/backups` returns a file digest. |
| 9 | Restore into an empty store, inventory hash equality | full | A second, empty Task Server instance restores that backup and reports the same SHA-256 (the most direct "before vs. after" equality check the store exposes today; see "Known gaps"). |

## How to run each target

```bash
scripts/scenario.sh --target inproc --level smoke   # < 3 minutes, no Docker, Windows or Linux
scripts/scenario.sh --target inproc --level full
scripts/scenario.sh --target compose --level smoke  # folded default-profile health check
scripts/scenario.sh --target compose --level full   # distributed stack + fake-CLI runner
scripts/scenario.sh --target remote --level smoke --remote-url https://... --remote-token ...
```

- **`inproc`** reuses the `TopologyTests` process-orchestration pattern
  (`testsupport/BuiltProcessLauncher.cs`, `testsupport/ProcessWaiters.cs`,
  `testsupport/ManagedProcess.cs`): it boots `task-server.dll` and
  `agent-host.dll` as sibling `dotnet test`-owned processes. Build the
  solution first (or let `scripts/scenario.sh` do it); no Docker, no network
  beyond `127.0.0.1`.
- **`compose`** at `--level smoke` delegates to the folded
  `scripts/compose-smoke-test.sh` check for the default `orchestrator-api` and
  `frontend` stack. At `--level full`, it builds the `task-server`,
  `studio-bff`, and `agent-host-distributed` services from the development
  checkout with the `distributed` and `runner` profiles, applies
  `testsupport/scenario/docker-compose.scenario.yml`, and runs the same nine
  typed steps as `inproc`. The override uses
  `testsupport/scenario/runner.Dockerfile`, which contains the fixed
  `scenario-coding-agent` instead of relying on an installed provider CLI.
  The run uses its own Compose project, bind-mounted fixture repository, ports,
  volumes, and credentials, then removes them on exit. Host-port discovery is
  deadline-bounded: the runner polls the Compose assignment for up to 30
  seconds, fails immediately if a service exits, and never substitutes an
  arbitrary startup sleep. On any Compose failure it captures service status
  and logs before removing the isolated stack. Containers that write into the
  bind-mounted fixture run as the invoking UID/GID with a user-owned,
  mode-0600 scenario token, so cleanup does not require root or leave
  root-owned test data behind.
- **`remote`** runs only the non-destructive management-plane steps
  (bootstrap a principal, create a scenario project/task, back up, archive
  the task) against an already-deployed Task Server, for the control-plane
  and cutover cards. It needs `--remote-url` and `--remote-token`, cleans up
  by archiving its own task, and does not drive a coding/review run. That
  needs a runner already attached to that deployment, which this script does
  not provision.

## Determinism

- Fixed fake-CLI outputs (`testsupport/scenario/fixture.json`'s two seed
  scripts, `tests/known-passing.sh` exit 0 and `tests/known-failing.sh` exit
  1); no runtime network beyond the target; polling follows the same
  deadline-and-retry contract as `TopologyTests`, so a crashed process fails
  fast with its captured output instead of waiting out the full timeout.
- Flake budget is zero. `TopologyTests.cs` is separately tagged
  `ReviewFlaky` for known pre-existing timing sensitivity in the outage/restart
  test; the scenario steps are not exempt from that expectation and should be
  fixed rather than quarantined if they start flaking.
- Two consecutive green CI runs are required before merging a change to this
  scenario itself (not just to the feature it is testing). This self-referential
  check proves that the regression suite is trustworthy before other cards
  start depending on it.

## Reading the report

Every run writes `scenario-<target>-<level>.junit.xml` for CI and
`scenario-<target>-<level>.md` for a human to `--report-dir` (default
`artifacts/scenario-reports/`). The typed test runner honors
`$SCENARIO_REPORT_DIR`, then `$JOB_RESULTS_DIR`, so a managed card gate puts
the report in collected task evidence without another wrapper. The Markdown
table has one row per step: status
(`Passed`/`Failed`/`Skipped`), duration, and an evidence string (a short fact
proving the step happened, such as a run id, a commit count, or a SHA-256
prefix). A step after the first failure is marked `Skipped`,
not silently omitted, since each step depends on the state the previous one
left behind. This is the report a deployment card attaches to its status.
If Compose fails before the typed steps can start, the runner still writes a
failed JUnit report, a failed Markdown topology-readiness row, and
`scenario-compose-full.compose.log` with the pre-cleanup service status and
logs.

## Local and CI execution

`inproc` needs the .NET 10 SDK and Git. `compose full` additionally needs
Docker Engine with the Compose v2 plugin and permission to access the Docker
socket. The runner builds only from the active checkout and allocates an
isolated Compose project; no `runner.env` or checked-in token file is needed.
Use a task-specific report directory when the output must survive the run:

```bash
scripts/scenario.sh --target inproc --level smoke \
  --report-dir "$JOB_RESULTS_DIR/inproc-smoke"
scripts/scenario.sh --target compose --level full \
  --configuration Release \
  --report-dir "$JOB_RESULTS_DIR/compose-full"
```

The card build profile invokes the smoke test from
`scripts/release/promotion-full-gate.sh`. The tag-triggered Release workflow
keeps `Test release topology`, then runs `inproc full` and the blocking
`scripts/scenario.sh --target compose --level full` step before any release
asset is built or published. GitHub Actions uploads the report directory as
`deployment-regression-scenario-<commit SHA>` even when a scenario step fails.
Because the Compose command has no `continue-on-error`, a red scenario prevents
all later release steps.

## How a card adds a new step

The scenario is the regression suite from now on; it grows only here.

1. Add the step's id/title/level/description/expectedOutcome to
   `testsupport/scenario/steps.json`, in the order it should run.
2. Add its fixture data (if any) to `testsupport/scenario/fixture.json`.
3. Add a case to the `switch` in `ScenarioContext.ExecuteAsync` and a private
   method implementing it, using the existing `ProcessWaiters`/
   `BuiltProcessLauncher` helpers from `testsupport/` rather than duplicating
   polling or process-boot logic.
4. Run both `scripts/scenario.sh --target inproc --level full` and
   `scripts/scenario.sh --target compose --level full` locally. Require two
   consecutive green CI runs before merging the scenario change.

## Known gaps

Found while building this scenario; each is a real, current limitation of the
distributed Task Server / Studio BFF / Runner topology, not a shortcut taken
by the scenario itself.

- **A CLI-driven run's result SHA never reaches the run row.** The runner
  completes with the outcome string `"SuccessfulCompletion"`
  (`ExecutionOutcomeKind.ToString()`), but
  `TaskServerStore.RequiresResultEnvelope` only recognizes the legacy
  `"success"`/`"done"`/`"noop"`/`"no-op"` strings. So a real coding run's
  `result_sha`/`repository_id` stay `null` on the `runs` row, and
  `POST /api/v1/reviews/subjects` can never reference it (its `RepositoryId`
  is non-nullable in the request but must equal that `null` column, an
  impossible match). Step 6 (auto-review) works around this today by
  completing a *second*, purpose-built coding attempt with the literal
  outcome `"success"` purely to exercise the review/orchestration wiring; it
  does not review the same run step 5 produced. After outcome-string
  reconciliation lands, step 6 should point back at the fixture task's real
  run.
- **No dossier / decision-gate concept in this topology.** `docs/concepts/`
  and the fixture's `dossier` section describe "one dossier with a decision
  gate," but that concept exists only in the separate backend monolith
  (`backend/Features/Tasks/TaskCrudEndpoints.cs`), not in `task-server`,
  `studio-bff`, or `runner`. `testsupport/scenario/fixture.json` reserves the
  data for when the concept lands here; no typed step reads it yet.
- **No native `6-completed`/`7-archive` transition in `task-server`.**
  "Integrate" and "complete" are backend-monolith concepts too. `5-human-review`
  (reached by step 6) is the current ceiling for this topology.
- **`--target remote` cannot drive a coding/review run.** It only exercises
  the management plane (principals, project/task CRUD, backup) because
  driving a real run needs a runner already attached to that specific
  deployment, which a scenario script visiting from outside cannot provision
  without becoming a deployment tool itself.

## See also

- [`task-server.Tests/TopologyTests.cs`](../../../task-server.Tests/TopologyTests.cs):
  the release-blocking topology and protocol-compatibility proof this
  scenario extends (HTTPS auth, outage fail-closed, transport replay,
  standalone binary contracts); still run separately, not duplicated here.
- [Release topology rehearsal](../setup/task-server.md#release-topology-rehearsal):
  the existing release-gate section this page sits next to.
- [`docs/operations/testing/windows-baseline-and-platform-gates.md`](windows-baseline-and-platform-gates.md):
  the `PlatformGate`/`PosixShell` convention the scenario's Linux-only steps
  follow.
