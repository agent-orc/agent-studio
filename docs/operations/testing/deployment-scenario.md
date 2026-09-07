# Deployment regression scenario

One reduced, deterministic end-to-end run that every deployment card and every
release passes. The scenario is the regression suite for the deployed topology:
when a card adds a deployment feature, it adds a step here rather than inventing
its own check.

- Definition: [testsupport/scenario/deployment-regression.json](../../../testsupport/scenario/deployment-regression.json)
- Runner: [scripts/scenario.sh](../../../scripts/scenario.sh), implemented in `scenario/`
- Guard tests: `scenario.Tests/`

## What it proves

The scenario drives one task from creation to completion through a real
deployment and then proves the deployment can be restored from its own backup.
Every step is data; every assertion is typed.

| # | Step | What a failure means |
|---|---|---|
| 1 | `topology-ready` | The Task Server is not a ready authority, or the Studio BFF cannot reach it, or the shipped protocol range changed. |
| 2 | `bootstrap-principals` | Management cannot issue scoped credentials, or a principal kind lost or gained a scope. |
| 3 | `register-runner` | A coding runner cannot authenticate or cannot publish its host projection with capabilities and capacity. |
| 4 | `seed-project` | The Studio surface cannot create a workspace, project, and task, or task keys stopped following the project prefix. |
| 5 | `claim-task` | Fenced work permits are not issued, or the task does not enter progress. |
| 6 | `run-task` | The CLI run does not commit, does not publish bounded evidence, does not reach the auto review lane, or its durable result envelope is not acknowledged. |
| 7 | `auto-review-handoff` | A completed coding result cannot become a fenced review subject that a review executor is offered. **Red today**, see finding 1 below. |
| 8 | `complete-task` | The task API cannot move a task to the completed lane under optimistic versioning. |
| 9 | `orchestrator-turn` | An orchestrator chat turn or its context receipt is not stored or not replayed. |
| 10 | `inventory-before-backup` | The deployment inventory cannot be read. |
| 11 | `backup-store` | Management cannot write a verifiable backup. |
| 12 | `restore-store` | A backup does not restore into an empty store through the maintenance-mode sequence a recovery runbook prescribes. |
| 13 | `inventory-after-restore` | The restored deployment does not carry the identical inventory digest, so the backup is not a faithful copy. |

Steps 1 to 6 are the `smoke` level. Steps 7 to 13 are the `full` level.

Current state: the `smoke` level is green and is what every gate runs. The
`full` level is green apart from step 7, which is blocked by a product defect
the scenario found on its first run (finding 1 below). `full` joins the release
gate once that defect is fixed; until then a release runs `smoke`.

## Running it

```bash
# Fast card gate. No Docker. Runs on Windows and Linux.
bash scripts/scenario.sh --target inproc --level smoke

# Deep local run, including backup, restore, and the inventory digest.
bash scripts/scenario.sh --target inproc --level full

# The docker-compose distributed control plane.
bash scripts/scenario.sh --target compose --level full

# An already running deployment. Non-destructive: its own project, no runner.
bash scripts/scenario.sh --target remote --level smoke \
    --server-url https://tasks.example --token "$TASK_SERVER_TOKEN"
```

Exit codes are stable and callers branch on them:

| Code | Meaning |
|---|---|
| 0 | Every planned step passed. |
| 1 | A planned step failed an assertion or threw. |
| 2 | Usage error in the arguments. |
| 3 | The scenario document is missing or invalid. |
| 4 | The target could not be brought up, so no step was evaluated. |

Code 4 is deliberately distinct from code 1: a missing build or an unreachable
Docker daemon is an environment problem, never a product regression.

### The three targets

| Target | Topology | Runner | Notes |
|---|---|---|---|
| `inproc` | Task Server, Studio BFF, and a coding runner started as sibling processes from the local build | Started by the scenario with the fixture CLI | No Docker. The only target that owns a second store, so the restore steps are declared for it alone. |
| `compose` | `task-server` and `studio-bff` from `docker-compose.yml` under the `distributed` profile | Started as a host process against the published Task Server port | The distributed agent host container is deliberately not started: it ships the real coding CLIs and the scenario must stay deterministic. The orchestrator engine is not started either; see the open findings. |
| `remote` | An existing deployment addressed by URL and credential | None | Non-destructive. It creates its own workspace and project and never restores over a store it does not own. |

## Reading the report

Every run writes two artifacts into `--out` (default: `JOB_RESULTS_DIR`, else
`artifacts/scenario`):

- `scenario-<target>-<level>.md` is the report a deployment card attaches to its
  status. It carries the step table with status, duration, and evidence link,
  a failure section naming every unsatisfied fact, and a full list of observed
  facts so a reported-but-unasserted value is visible.
- `scenario-<target>-<level>.junit.xml` is one JUnit test case per step, so a
  failing step names itself in the CI test list instead of hiding in a log.
- `evidence/` holds the raw JSON and text each step captured. Report links are
  relative to the report, so the directory travels with it.

A step row reads `passed`, `failed`, `skipped (level)`, or `skipped (target)`.
The two skip reasons are distinct on purpose: `skipped (target)` means the step
cannot apply to this topology at all, while `skipped (level)` means a smoke run
did not reach it. Nothing is silently dropped; every step in the document
appears in every report.

### Facts and assertions

An action publishes typed facts. The document asserts over them:

```json
"expect": {
  "facts": { "task.state": "4-auto-review", "result.sha.length": 40 },
  "factsAtLeast": { "artifacts.count": 2 }
}
```

`facts` demands the same kind and the same value, so a textual `"2"` never
satisfies a numeric `2`. `factsAtLeast` demands a numeric fact at or above a
floor, for counts whose exact value is not part of the contract. A fact an
action did not publish is a failure, never a silent pass.

## Extending it when a card adds a feature

The scenario grows only here. A card that adds a deployment feature:

1. Adds an action to `scenario/ScenarioActions.cs` if no existing action covers
   the feature, publishing the typed facts the feature guarantees. Register its
   id in `ScenarioActions.Known` and in the dispatch switch.
2. Adds the step to `testsupport/scenario/deployment-regression.json` in the
   position where its inputs already exist. A step reads what earlier steps
   recorded, so order is part of the contract.
3. Chooses `level` and `targets`. Put a step in `smoke` only if the whole smoke
   level still finishes well inside three minutes, and only if every earlier
   smoke step is also `smoke`: the guard test in `scenario.Tests` refuses a
   smoke step that sits behind a full step. Declare `targets` narrowly when a
   step needs something a topology does not have.
4. Documents the step in the table above with what a failure means.
5. Runs `bash scripts/scenario.sh --target inproc --level full` twice. The flake
   budget is zero: two consecutive green runs are required before merge.

`scenario.Tests` guards the document without a live topology. It refuses an
unknown action id, a step that asserts nothing, a smoke step behind a full step,
a smoke level larger than six steps, a store-destructive step declared for a
target the scenario does not own, and a runner-dependent step declared for the
remote target.

## Determinism

- The fixture repository is created with a fixed author, committer, and commit
  date, so the seed commit is identical on every host.
- The coding and review CLIs are shell scripts with fixed output. Neither
  branches on wall-clock time, host name, or network state.
- Ports are allocated from the ephemeral range per run, and every run uses its
  own temporary store, backup directory, and runner workspace.
- Waits are bounded and poll at 200 ms. There is no sleep longer than the
  runner's own one second polling contract.
- The one second pause in the fixture coding CLI models a provider CLI that
  streams over time rather than writing everything in one burst before exiting.
  See the open findings below for why that matters.

## Where the gates use it

| Gate | Command |
|---|---|
| Card build gate | `bash scripts/scenario.sh --target inproc --level smoke` |
| develop to main promotion | `scripts/release/promotion-full-gate.sh` runs the inproc smoke level |
| Release workflow | `.github/workflows/release.yml` runs the inproc smoke level next to "Test release topology" |
| Compose stack | `.github/workflows/compose-smoke.yml` runs the compose smoke level |

The card build gate is project configuration, not a repository file. Add the
scenario to this project's continuous commands once, through the API:

```bash
curl -i -X PUT http://localhost:5030/api/projects/<project>/test-execution \
  -H 'Content-Type: application/json' \
  --data '{ "continuousCommands": ["bash scripts/scenario.sh --target inproc --level smoke"] }'
```

## Open findings this scenario surfaced

The scenario found three defects on its first run. They are recorded here
because two of them bound what the scenario can assert today.

1. **A completed coding run never records its result SHA, so auto-review cannot
   start.** `TaskServerStore.RequiresResultEnvelope` matches the outcome names
   `success`, `done`, `noop`, and `no-op`. A remote coding run completes with
   the typed outcome `SuccessfulCompletion`, which matches none of them, so the
   acknowledged result envelope is never read at completion and `runs.result_sha`,
   `repository_id`, `repository_url`, and `result_ref` are all written as NULL.
   `POST /api/v1/reviews/subjects` then rejects every subject for that run with
   `409 result-sha-mismatch`. The durable handoff itself is correct and readable
   at `GET /api/v1/runs/{runId}/result-handoff`; only the run projection loses
   the link. This is why step 7 is red today.
2. **The shipped narrative is not deterministic, and under host load it can be
   lost entirely.** How many CLI stdout lines reach the Task Server, and whether
   a frame is classified as a tool trace or as an unknown frame, varies between
   otherwise identical runs: observed counts on an idle host ranged from one to
   three, and a run on a host that was busy with the full test suite shipped
   zero. When the terminal line is not shipped, the Result summary is finalized
   as `Partial` even though the runner classified the run as
   `SuccessfulCompletion`, so the operator-visible Result flips for an identical
   run. Step 6 therefore reports `events.agent-message.count`,
   `events.tool-trace.count`, `events.narrative.count`, and `status.result` as
   observed facts and asserts none of them. Asserting even a floor of one made
   the gate red without any product change. Tighten these to exact assertions
   once the shipping race is fixed; the run itself is otherwise fully asserted
   through the durable result envelope, the artifacts, and the lane state.
3. **The orchestrator engine cannot claim orchestration work.** Its client
   serializes `OrchestrationStage` as a string while the Task Server binds the
   enum numerically, so the whole claim request fails to bind and the server
   answers `400 invalid-request` with the misleading message "Engine and
   instance ids are required." Reproduce with two requests to
   `POST /api/v1/orchestration/claims` that differ only in
   `"supportedStages": ["ReviewDecision"]` versus `"supportedStages": [0]`. The
   inproc target does not start the engine for this reason.

## Scope boundaries

- Step 7 proves the review handoff, not a full review verdict. Submitting a
  review report requires reproducing the server's resource namespace and
  workspace identity conventions; that coverage stays in
  `TaskServer.Tests.RemoteReviewAuthorityTests`, which exercises it in process.
- [scripts/compose-smoke-test.sh](../../../scripts/compose-smoke-test.sh) is not
  folded into the scenario. It covers the default new-user compose path
  (`orchestrator-api` plus `frontend`), which is a different stack from the
  `distributed` control plane the scenario's compose target drives. The two are
  complementary; folding waits until the scenario can select a compose profile.
- The scenario owns its own topology harness in `scenario/ScenarioProcess.cs`.
  `TaskServer.Tests.TopologyTests` keeps its own copy because it is a
  release-blocking gate and was left untouched by this change. Consolidating the
  two is a follow-up.
