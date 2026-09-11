# Orchestrator Domain Map

Version: 2026-09-11
Status: System-of-record map for orchestrator decisions, intervention events,
task attribution, and project sessions.

## Failure interventions

The orchestrator may raise a follow-up task when an enabled pipeline failure
check identifies actionable failed evidence. The task is created through the
normal task mutation path. `creationSource: orchestrator` and
`createdBy: Orchestrator` are durable creation provenance, distinct from the
task's later execution agent and from external completion attribution.

The orchestrator surface receives the same intervention at three levels:

- the origin timeline records `failure_intervention_raised` with the follow-up
  key, failure class, fingerprint, and dedupe result;
- the project feed records an `intervention` item with topic
  `failure-intervention`;
- the project session lists open items through
  `GET /api/projects/{project}/interventions?openOnly=true`.

Task references carry the graph. The follow-up has `followUpOf` edges to every
origin. Origins have `raisedFollowUps` plus `blockedBy` to the follow-up. The
board chip and Task Detail references resolve the linked task's live lane and
state from the ordinary task read model.

Reporting reads the project ledger at
`.orchestrator/failure-interventions.json`. It exposes count, open and closed
state, failure class, affected cards, first-failure-to-creation duration, and
first-failure-to-resolution duration. This is reporting data, not a second workflow
state machine.

See [ADR-0070](../architecture/decisions/adr-archive.md#adr-0070---pipeline-failures-raise-deduplicated-orchestrator-intervention-tasks-2026-09-11)
and the [pipeline domain](pipeline.md#failure-intervention-step).
