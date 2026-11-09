# Operator API reference

The operator section of the Studio HTTP API: the routes an operator agent (a
coding-agent CLI session the Owner runs, see the
[operator-agent-level Dossier](../../operator-agent-level/index.html)) and the
Owner use to operate cards. Authentication, roles and the routes a token can
never call are in the [security overview](../../operations/security/overview.md).
Each slice of the Dossier adds its routes here.

All examples use placeholder names. Send `X-Client-Id` on every write.

## Operator

The official client for these routes is `studio-op` (OP-8): see
[studio-op setup](../../operations/setup/studio-op.md) for the commands, the
token file and the exit codes. `studio-op continue <card> --prompt-file <file>`
sends the round below and prints the returned `base` as "Run base"; exit 11
is `batch-isolation-active`.

### Continue, steer or extend a card

`POST /api/tasks/{id}/continue?watchPath=<project path>`

| Field | Required | Meaning |
|---|---|---|
| `prompt` | yes | The follow-up for the agent. It does not need to name a ref. |
| `mode` | no | `continue` (default), `steer`, `extend` or `newTask`. |
| `reason` | no | One sentence for the run trigger and the timeline. |
| `model`, `cliType`, `thinkingLevel` | no | Route overrides for this card. |
| `modeOverride` | no | Echo of a concept or planning card's mode, to confirm an implementation-shaped prompt. |
| `baseRef` | no | One of the card's own result refs, full (`refs/heads/...`) or short, to start from instead of the latest. |

A round that is queued for a runner claim starts from the card's latest
recorded result ref; the operator no longer types refs into the prompt. The
answer names the base the run starts from:

```json
{
  "status": "queued",
  "queued": { "reason": "lane-not-runnable", "position": 1, "promotedFromState": "5-human-review" },
  "base": {
    "ref": "refs/heads/agent-studio/results/run_example/fence-3/<sha>",
    "sha": "<40-hex sha>",
    "source": "latest-result",
    "resultAttemptId": "run_example"
  }
}
```

`base.source` is `latest-result`, `explicit` (the `baseRef` you sent),
`review-fix-round` (the delivery a `ProductFailure` review graded) or
`existing` (a base another round already saved, such as a salvage). `base` is
absent when the round keeps today's checkout: the card has no result ref, the
round starts on this host, or it joins a run that is still live. The card
timeline shows the same base as a "Run base" entry with the base SHA.

| Status | `error` | When |
|---|---|---|
| `200` | | The round started on this host (`status: started`). |
| `202` | | The round was queued (`queued`) or saved without promotion (`saved`). |
| `400` | `base-ref-not-on-card` | `baseRef` is not one of the card's result refs. Nothing was written. |
| `409` | `base-ref-not-applicable` | `baseRef` was sent for a round that no runner claim starts. Nothing was written. |
| `409` | `batch-isolation-active` | The card is a member of a batch under isolation; `flight` names the batch. |

`GET /api/tasks/{id}/continue/base?watchPath=<project path>` answers
`{ "base": { ... } }` with the base a round without `baseRef` would start
from, or `{ "base": null }` when it keeps today's checkout. It writes nothing.
The task detail's Continue form on Human review and Accepted cards shows it
read only and sends the round through the route above (AGT-3582).

`GET /api/tasks/{id}/failure/continue?watchPath=<project path>` answers
`{ "available": true|false, "reason": "<refusal or null>" }` from the guard of
`POST /api/tasks/{id}/failure/continue`, without continuing. The task detail
offers "Continue with delivery context" only when `available` is true.

### Requeue

`POST /api/tasks/{id}/move` (and `PUT /api/tasks/{id}/state`) with
`targetState` `0-backlog`, `1-preparation`, `1a-orchestrator-prep`, `2-ready`
or `3-progress` is a requeue. `POST /api/tasks/batch-move` items with those
targets are requeues too. For a member of a batch under isolation a single
move answers `409 batch-isolation-active` with `flight`; a batch move item
reports `status: rejected` and `message: batch-isolation-active: flight <id>`.

### Members of a batch under isolation

The batch gate isolates a deterministic red batch by halving its members
([batch gate](../../operations/gates/batch-gate.md)). A member stays under
isolation until the batch pauses or publishes, an isolation finding proves it
red or superseded, or a per-task gate takes it over. Until then continue,
steer and requeue answer:

```json
{
  "error": "batch-isolation-active",
  "message": "<card> is a member of batch flight <batch id>, which is isolating a red result. ...",
  "flight": "<batch id>",
  "project": "<project>",
  "taskKey": "<card>"
}
```

Eject stays the member action during isolation:
`POST /api/projects/{project}/batch-gate/members/{taskKey}/eject` answers
`202` and the batch gate ejects the member on its next pass. Read the
flight with `GET /api/projects/{project}/batch-gate/state`.
