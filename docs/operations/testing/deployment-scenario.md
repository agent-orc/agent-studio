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

`smoke` runs the seven smoke-level steps (bootstrap through auto-review) and
is the gate every deployment card build and the develop-to-main promotion train
must pass. `full` runs every step and is the deeper release gate.

The coding attempt runs on both runner planes (AGT-2985). Step 5 drives the v1
plane of the distributed Task Server, where the claim carries a run id. Step 6
drives the legacy plane that the production fleet uses: the backend monolith
serves `/api/runner/claim` and `/api/runner/completion`, the claim carries no
run id, and the runner's own ids and the server's fenced attempt id can drift
there. Stable 0.9.3 passed the old v1-only scenario and then had every
production completion rejected on the legacy plane.

| # | Step | Level | Proves |
|---|---|---|---|
| 1 | Bootstrap principals | smoke | The in-process target issues a runner credential over the management API; Compose verifies the fixed, startup-bootstrapped runner principal. |
| 2 | Register runner | smoke | A real runner binary registers against the Task Server. |
| 3 | Create task | smoke | The seeded workspace/project/task exist in `2-ready`. |
| 4 | Claim task | smoke | The runner claims the task under a fenced lease. |
| 5 | Run with the fake CLI | smoke | The runner drives a fake coding CLI that runs the fixture's known-passing and known-failing checks, commits, and pushes; the task reaches `4-auto-review`. |
| 6 | Run with the fake CLI on the legacy runner plane | smoke | The backend monolith, a second real runner, and the fixture repository served over smart HTTP (`git http-backend` behind `testsupport/GitSmartHttpServer.cs`; the project registry accepts only http(s) URLs) run as sibling processes. The runner passes the project delivery preflight, claims over `/api/runner/claim`, runs the fake CLI, and completes over `/api/runner/completion` with session-continuation evidence. The completion is accepted, the task reaches `4-auto-review`, the ledger entry names the server's fenced attempt id (not the lease id), and a new commit lands. It fails with the production symptom on the 0.9.3 runner code. |
| 7 | Auto-review | smoke | The real review executor checks out the coding run's exact SHA, executes its checks, reports, and cleans up; a supervised fixture publisher verifies that SHA at canonical `main`; the queued orchestration run is settled; the task reaches `5-human-review`. |
| 8 | Orchestrator chat turn with context receipt | full | A chat turn round-trips with a persisted context receipt (token budget, sources). |
| 9 | Backup | full | `POST /api/v1/management/backups` returns a file digest. |
| 10 | Restore into an empty store, inventory hash equality | full | A second, empty Task Server instance restores that backup and reports the same SHA-256 (the most direct "before vs. after" equality check the store exposes today; see "Known gaps"). |

### Connector negative matrix (full, `inproc`)

`--target inproc --level full` also runs the Studio connector negative-test
matrix (gate 4 of
[Remote Task Server with local Studio](../remote-task-server-local-studio.md#current-cutover-gates),
AGT-2984). It lives in `backend.Tests/ConnectorNegativeMatrixTests.cs`
because the connector is the OrchestratorApi connector profile. It starts
the built `task-server.dll` in bearer mode as a real process. The connector
runs in-process with its production transport and its production credential
source: the owner-only credential file on Linux, and a real Windows
Credential Manager generic credential on Windows. Only a Windows logon
session without a writable vault falls back to an in-memory source; the
report's `credentialStore` field names the store that ran. The matrix proves
these outcomes:

- absent bearer: 401 from the Task Server, and `credential-unavailable`
  from the connector;
- invalid bearer: 401 from the Task Server, and `credential-rejected` from
  the connector;
- cross-origin, unconfigured loopback Origin, and missing Origin mutations:
  403;
- missing session: 401;
- missing CSRF token: 403;
- replayed CSRF token, from another session or after logout: 403 or 401;
- `/api/v1` and Studio hub protocol mismatch: `503 connector-attach-refused`
  with the Task Server's operator-readable reason.

Positive controls prove an accepted mutation, a credential rotated in place
without a restart, and that no rejected probe created data. The step runs
even when a scenario step failed, so both reports reach the bundle.

## How to run each target

```bash
scripts/scenario.sh --target inproc --level smoke   # about one minute on an idle host, no Docker
scripts/scenario.sh --target inproc --level full
scripts/scenario.sh --target compose --level smoke  # one-box edge and authority check
scripts/scenario.sh --target compose --level full   # Task Server + fake-CLI runner
scripts/scenario.sh --target remote --level smoke --remote-url https://... --remote-token ...
```

- **`inproc`** reuses the `TopologyTests` process-orchestration pattern
  (`testsupport/BuiltProcessLauncher.cs`, `testsupport/ProcessWaiters.cs`,
  `testsupport/ManagedProcess.cs`): it boots `task-server.dll` and
  `agent-host.dll` as sibling `dotnet test`-owned processes, and for the
  legacy-plane step also `OrchestratorApi.dll` (from a temporary working
  directory, because the monolith writes `logs/` beside its content root) and a
  second `agent-host.dll`. `task-server.Tests` builds the backend for that
  reason without referencing it. The legacy runner does not inherit the host's
  `RUNNER_*` variables, so a managed runner host can run the scenario. Build the
  solution first (or let `scripts/scenario.sh` do it); no Docker, no network
  beyond `127.0.0.1`. Every step runs on Linux only.
- **`compose`** at `--level smoke` delegates to the folded
  `scripts/compose-smoke-test.sh` check for the one-box Task Server, engine,
  BFF, frontend and runner roles. At `--level full`, it builds the `task-server`,
  `studio-bff`, and `agent-host-distributed` services from the development
  checkout, applies
  `testsupport/scenario/docker-compose.scenario.yml`, and runs the same ten
  typed steps as `inproc`. The legacy-plane step runs its backend monolith and
  runner as host processes there too: neither is part of the Compose stack.
  Its bootstrap runs from the same source-built Task Server image and
  generates the principal credentials in the product's persistent `secrets`
  volume. The harness reads those credentials through
  `docker compose exec`; it does not inject a second token set. The override uses
  `testsupport/scenario/runner.Dockerfile`, which contains the fixed
  `scenario-coding-agent` instead of relying on an installed provider CLI.
  The run uses its own Compose project, bind-mounted fixture repository, ports,
  volumes, credentials, and images, then removes them on exit, also after a
  failure or `SIGTERM` (AGT-2993). Before it builds, it runs
  `scripts/docker-scenario-retention.sh`, which clears scenario and smoke
  images older than six hours that no container uses and caps the BuildKit
  cache at 40 GB; see
  [Docker scenario image retention](../setup/linux-runner-host.md#docker-scenario-image-retention). Host-port discovery is
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
At `--target inproc --level full` the same directory also receives
`connector-negative-matrix.md` (one row per probe with expected and observed
status, code, and refusal reason) and `connector-negative-matrix.json`.
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

A card whose diff can change the Compose stack also renders it in its own
gate. The gate runs `scripts/scenario.test.sh` and
`scripts/compose-smoke-version.test.sh` on a host that advertises
`toolchain:compose-render`, which needs the Docker CLI with the compose plugin
but no daemon. A gate host without it fails with a routing verdict instead of
passing. The trigger paths are listed in the
[Compose-render gate step](../../system/domains/pipeline.md#compose-render-gate-step-agt-2981).

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

## Bounded one-box publication canary

The full scenario uses one coding run, its immutable result ref and SHA, and
one real review daemon with a detached `RemoteReviewExecutor` worker. The daemon
runs natively on the same Linux host against the Compose Task Server. Both
executors see the same fixture repository URL through a bind mount. Required
checks read the coding results log and execute the known passing and failing
checks. A synthetic passing review report is no longer used.
The harness reserves one review slot and overrides only its test daemon's
load threshold so other jobs on a shared CI host cannot prevent this bounded
fixture from starting. Product admission defaults remain unchanged.

For a provider-authenticated semantic review, run the same bounded scenario
with an existing Codex host login (credentials stay outside reports):

```sh
SCENARIO_PROVIDER_REVIEW=1 scripts/scenario.sh --target compose --level full \
  --report-dir artifacts/provider-canary
```

This adds a read-only `gpt-5.6-sol` / `medium` review of the tiny fixture diff,
using the demanding-analysis tier from the model routing policy. It fails if
the provider cannot authenticate or the aspect cannot produce a passing
verdict; it never substitutes a fixture verdict. Routine CI leaves the flag
unset and runs actual deterministic review commands without a provider call.

After Task Server records Pass and cleanup, the supervised test publisher
fetches that exact immutable ref, checks the reviewed SHA, requires a fast
forward and pushes with an expected-old-SHA lease to the disposable origin's
canonical `refs/heads/main`. It independently reads the remote ref back.
`canary-publication.json` joins coding run, review subject, review attempt,
reviewed SHA and published SHA. The Compose BFF remains stopped through review
and publication. No product repository or production branch is published.

This is a bounded supervised installation canary, not an implementation of the
autonomous publication authority. I08 still owns detached automatic publication
and recovery acceptance across the full deployment ladder. Source-build,
published-image, N-1 and supported desktop/VM results remain separate evidence.

## Known gaps

Found while building this scenario; each is a real, current limitation of the
distributed Task Server / Studio BFF / Runner topology, not a shortcut taken
by the scenario itself.

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
- **Legacy-plane result artifacts are not shipped.** Since AGT-2890 the runner
  uploads result artifacts after the fenced completion. The v1 Task Server
  admits that with the outbox authority; the backend monolith answers
  `409 RunAttempt is Completed`, so a legacy-plane run records
  `artifacts=partial uploaded=0` and ships no result artifacts. The
  legacy-plane step therefore asserts completion acceptance and the ledger
  entry, not artifacts. Found by the step itself (AGT-2985); it needs its own
  fix card.

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
