# Deployment regression scenario

The deployment regression scenario is the shared release proof for Task Server,
Studio, Agent Host, installer, control-plane, and cutover changes. It replaces
deployment-specific sequences of curls with one versioned fixture and one
ordered set of typed assertions.

The scenario definition is
[`testsupport/scenario/deployment-scenario.json`](../../../testsupport/scenario/deployment-scenario.json).
Its schema is stored beside it. The portable driver runs every target through
the Task Server HTTP contract. The in-process target starts the same built Task
Server used by `task-server.Tests/TopologyTests.cs`; the Compose target also
retains the browser shell and grouped-task checks formerly owned by
`scripts/compose-smoke-test.sh`.

## What it proves

The fixed fixture contains a tiny Git repository whose comparison first fails
and then passes, one registered project, a decision Dossier, one Epic, and two
child tasks. The fake coding CLI has fixed output and creates a real commit and
log. The fake review CLI reports evidence against that exact result SHA.

The full sequence proves:

1. protocol compatibility for the Studio, coding Runner, review Runner,
   Orchestrator Engine, and management principals;
2. seeded workspace, project, Dossier, Epic, and task inventory;
3. Runner registration, a fenced claim, the intentionally failing test, the
   passing repair, immutable result handoff, and Auto Review transition;
4. fake review, exact-SHA fast-forward integration, orchestration settlement,
   and completion;
5. an orchestrator chat turn whose persisted reply includes the source and
   budget receipt;
6. the Dossier decision, backup SHA-256, restore verification, and equality of
   the normalized inventory hash before and after restore.

The fixed fixture clock is `2026-09-06T12:00:00Z`. Polling uses the product's
bounded readiness contract. The scenario has no runtime network dependency
beyond the chosen target. A failed assertion stops the sequence and exits 1;
bad command-line input exits 2.

## Run it

From the repository root:

```bash
scripts/scenario.sh --target inproc --level smoke
scripts/scenario.sh --target inproc --level full
scripts/scenario.sh --target compose --level full
```

`inproc` needs .NET 10, Python 3, and Git. It builds and starts an isolated Task
Server with temporary storage, and works on Windows through Git Bash as well as
Linux. `smoke` is exactly the first six ordered steps and must remain below
three minutes.

`compose` needs Docker Engine and Compose v2. It starts the default Studio
services plus the distributed Task Server, BFF, and Orchestrator Engine, runs
the old onboarding health checks, then runs the same scenario definition from
the fake Runner CLI image in the Compose `runner` profile. It removes its named
containers and volumes afterwards. `scripts/compose-smoke-test.sh` is retained as a
compatibility wrapper for `compose smoke`.

For a deployed server:

```bash
export SCENARIO_SERVER_URL=https://task-server.example.test
export SCENARIO_AUTH_TOKEN='<deployment-scenario credential>'
export SCENARIO_RUN_ID="cutover-$(date +%Y%m%d%H%M%S)"
scripts/scenario.sh --target remote --level full
```

Use `SCENARIO_STUDIO_TOKEN` and `SCENARIO_RUNNER_TOKEN` instead of the shared
token when the deployment uses the legacy role-specific credential mode.

Remote runs require a unique, traceable `SCENARIO_RUN_ID` when the target may
already contain a previous scenario project. They create only scenario-named
resources and archive their tasks in a final cleanup pass. To preserve remote
data, the restore step uses the Task Server's `verifyOnly` contract. In-process
and Compose full runs enter maintenance mode and execute the actual restore.

Set `JOB_RESULTS_DIR` to choose the collected artifact root, or pass
`--output DIR` for a one-off run. CI uses the former.

## Read the report

Every run writes:

- `scenario-definition.json`, the exact ordered fixture and assertions used;
- `scenario-report.md`, with target, level, overall status, total duration, and
  a row for each step;
- `scenario.junit.xml`, with one test case per scenario step;
- `evidence/<step>/...`, containing the typed API receipts, fixed CLI logs,
  Git identities, transcript, decision, and backup or restore proof.

Evidence links in the Markdown report are relative, so the whole directory can
be attached to a card or uploaded as one CI artifact. A failed step is present
in both reports and the process returns non-zero.

## Extend it

When a deployment card adds a feature, add one bounded step to
`deployment-scenario.json` instead of creating another deployment smoke script.
Give the step a stable kebab-case ID, one snake-case handler, explicit levels,
and one or more uniquely owned typed assertions. Implement the handler in
`run_scenario.py` and persist evidence below that step's directory.

Keep these rules:

- add the step at the real lifecycle position;
- include it in `smoke` only if the first-six-step time budget and purpose still
  hold, otherwise include it in `full`;
- use fixed fixture values and timestamps for observable output;
- use bounded readiness polling, never an arbitrary long sleep;
- leave the target clean, and use verification-only operations when a real
  remote restore or another destructive action would exceed scenario scope;
- prove a new failure mode by making its typed assertion fail and checking the
  stable non-zero exit before accepting the green implementation.

The release workflow runs `compose full` beside the release topology tests.
The cross-platform card gate runs `inproc smoke` twice and uploads both reports.
