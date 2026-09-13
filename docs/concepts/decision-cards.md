# Decision cards

Status: current (AGT-2795)

A **decision card** is a first-class card kind for a fork an agent must not take
alone: lock file versus no lock file, a version number, a breaking contract
change, deleting data. Before this, such a fork lived as prose ("Decision needed
(operator)") on an ordinary coding card, with no interface to decide, no call to
action, and an invisible card type. A decision card makes the fork recognisable
at a glance, decidable in one action with a rationale, and, once decided, a
durable record that unblocks the implementation work waiting on it.

## The card kind

`kind` now carries a third value next to `task` and `epic`:

- `task` - a runnable unit of work (default).
- `epic` - a container for sub-tasks.
- `decision` - a decision request. Never code-executed, never auto-picked into a
  runner lane, and always branch-less (`noBranchExpected`). See
  [`TaskKinds`](../../backend/Shared/Models/TaskKinds.cs) and
  [`DecisionContent`](../../backend/Shared/Models/DecisionCard.cs).

A decision card carries structured content in the `decision` object of
`task.json`:

- `question` - the fork to resolve;
- `options` - two to four options, each with `id`, `label`, and the structured
  fields `consequences`, `effort`, `risks` (and optional `requirements`);
- `recommendedOptionId` + `recommendationReason` - an optional recommendation;
- `decider` - a client identity id, or the role `operator`;
- `dueDate` - optional;
- `blockedCards` - the cards this decision unblocks;
- the recorded outcome once decided: `status`, `chosenOptionId`, `rationale`,
  `decidedBy`, `decidedAt`, `recordPath`.

Prose stays possible in the card prompt, but the options are fields, not
markdown. The validation matrix (question required, two-to-four options with
unique ids and labels, a recommendation that names a real option) is a pure
policy, `DecisionCardPolicy`, tested directly.

## Creation

Agents and services create a decision card through the task API with
`kind: decision` and a `decision` options payload
(`POST /api/tasks`). A malformed payload is rejected before a card folder is
created. A default create lands the card in `1-preparation` with the decision
badge rather than in the backlog. Because a decision is never executed, the
runner's pickup gate skips it the same way it skips epic containers.

## Visibility

- The board and list render a distinct **Decision** badge and the **decider**
  on the card (`data-testid="task-card-decision-badge"` /
  `task-card-decider`). An open decision uses the acute attention tint; a
  decided card renders quietly (history).
- A decision card sits in `1-preparation` with the badge until decided; it never
  enters `2-ready` or a runner lane.

## Call to action

The task-detail Overview shows the decision panel
(`app-decision-panel`): the question, every option with its consequences,
effort, and risks, the recommendation marked, and a **Choose** action per option
with a rationale field. Deciding records the option, rationale, decider identity,
and timestamp, moves the card to `6-completed` as a durable record, and writes an
ADR-style `decision-record.md` into the card folder (surfaced in the Files tab
and linkable, mirroring the Dossier decision record's fields).

## Unblocking

Cards that depend on a decision through `references.dependsOn` are held back by
the normal waits-on gate until the decision reaches a terminal lane. On decision:

- every implementation card that depends on the decision has the decision block
  (question, chosen option, rationale, record link) appended to its prompt and,
  if it is still in an intake lane, is promoted to `2-ready`;
- when no implementation card exists yet and the chosen option carries
  `requirements`, one `2-ready` coding card is seeded from those requirements
  and linked back to the decision.

**Rework:** a decider can reopen a decided card with a note. Reopening clears the
recorded choice and returns the card to `1-preparation`, which re-blocks its
dependants through the waits-on gate.

## API and events

- `POST /api/tasks/{id}/decision` - `{ optionId, rationale }`. The `X-Client-Id`
  of the caller is the record's author (the decider).
- `POST /api/tasks/{id}/decision/reopen` - `{ note }`.
- Timeline events `decision_requested`, `decision_decided`, and
  `decision_reopened` record the fork's lifetime in `logs/timeline.jsonl`.

## Scope note

The durable decision record is written into the card folder as an ADR-style
`decision-record.md` (a top-level task document, surfaced in the Files tab and
linkable) rather than committed into a project wiki page. It reuses the field set
and heading style of the Dossier decision record so the two read the same.
