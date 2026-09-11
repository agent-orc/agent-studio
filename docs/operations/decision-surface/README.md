# Operator Decision Surface

Status: minimal end-to-end slice implemented on 2026-07-28  
Seed case: AGT-2355 icon selection  
Audience: operators, runner and pipeline authors, coding agents, frontend engineers

## Purpose

This runbook explains how an operator reads and resolves automation-owned and
human-owned interventions without reconstructing the decision from raw logs.

## Failure-created intervention escalations

When a card is parked with blocker type `failure-intervention`, the park is an
owned automation hand-off, not a request for an operator to reconstruct the
failure. The wait reason names the follow-up task, for example
`waiting on AGT-2810: review toolchain unavailable`. Open that task from the
board intervention chip or the Task Detail `Raised follow-up` reference.

The follow-up header must say `Created by Orchestrator`, and its `Follow-up of`
references list every affected origin. The orchestrator feed contains one event
per attachment, while project reporting contains one row per deduplicated
intervention. Completing the follow-up closes the reporting item; re-driving
the waiting origins remains an explicit operator or orchestrator action. The
detection step never performs automatic remediation.

A task in `5e-escalated` already says that automation could not safely finish
the decision. It does not, by itself, give the operator the material needed to
make that decision. The operator should not have to reconstruct the question,
options, and consequences from Activity, `status.md`, and unrelated files.

The decision surface closes that gap:

1. The escalating run writes one active decision artifact under `results/`.
2. Task Detail presents that artifact at the top of the Result tab.
3. The operator chooses an option and may add steering text.
4. Studio submits the choice through the existing Move or Continue/Steer path.
5. The existing task timeline, activity log, and review-attempt machinery retain
   the reason. There is no decision database or second state machine.

This page defines who owns a decision, the artifact contract, and the lifecycle.
The filesystem summary lives in
[the task filesystem contract](../../system/contracts/filesystem.md).

## Decision ownership

The key question is not "can an agent produce an answer?" It is "who has the
authority to commit the project to that answer?"

### Operator-Agent owns the decision

The Operator-Agent continues without Robert only when every condition below is
true:

- The choice stays inside the accepted task scope and product direction.
- One option is materially better under already recorded constraints.
- The evidence needed to choose is available in the repository, task results,
  current settings, or an existing operator rule.
- The action is reversible or is already authorized by the task.
- The choice does not spend money, publish externally, grant access, reveal
  secrets, delete material data, or change a protected environment.
- The choice does not lower a correctness, security, review, or test floor.
- The Operator-Agent can record a concrete reason and verify the consequence.

Typical examples are retrying after a transient tool failure, selecting the only
icon source that matches an already adopted design system, or choosing the
documented compatibility path when the alternatives violate an existing
contract.

If these conditions hold, the Operator-Agent should make the choice, record it,
and keep the task moving. It should not create a `5e-escalated` interruption
merely to ask Robert to repeat an existing rule.

### Robert owns the decision

Escalate to Robert when any of these conditions applies:

- The choice defines product taste, brand identity, user experience, or roadmap
  priority and no existing rule settles it.
- Two or more options remain valid after applying all recorded constraints, and
  the trade-off is preference rather than correctness.
- The choice expands or replaces task scope, changes an accepted product
  boundary, or commits another team to work.
- The action is irreversible or externally visible, including production
  publication, destructive data changes, credential or permission changes,
  legal commitments, or material spend.
- The evidence is missing, contradictory, or too uncertain for the consequence.
- Proceeding would lower a correctness-risk floor, bypass a required review, or
  turn an unresolved security concern into an implicit acceptance.
- The task explicitly reserves the decision for Robert or another named human.

When ownership is unclear, Robert owns the decision. Uncertainty is not
permission.

### Ownership examples

| Situation | Owner | Reason |
|---|---|---|
| The repository already standardizes on one icon library and the new icon exists there | Operator-Agent | Existing product rule, reversible implementation |
| The project has no icon language and the choice changes the visual identity | Robert | Product taste and brand commitment |
| A transient test host lock has a documented retry policy | Operator-Agent | Existing operational rule |
| Accepting a known accessibility regression to meet a date | Robert | Correctness floor and product trade-off |
| A follow-up task is required by an already accepted plan | Operator-Agent | Execution of an approved plan |
| Choosing whether the follow-up belongs on this quarter's roadmap | Robert | Priority and scope commitment |

## Escalation entry contract

Before a run ends with `[[TASK_NEEDS_INPUT:<reason>]]` or otherwise causes a
human-input escalation, it should:

1. Apply the ownership test above.
2. If the Operator-Agent owns the choice, decide and continue the same task.
3. If Robert owns the choice, write `results/decision.json` or
   `results/decision.html`.
4. State the same short question in the terminal reason so the lane remains
   understandable even if the artifact is malformed.

The artifact is task-local review evidence. It must describe the current
decision only. A later escalation replaces the active `decision.*` artifact
with the new question. Historical lane moves and activity remain append-only.

Legacy escalations without a decision artifact remain supported. They keep the
existing escalation summary and Continue, Accept, Resolve manually, and Abort
actions, but they do not gain a fabricated decision model.

## `decision-surface/v1` contract

The preferred machine-readable form is:

```text
<task-folder>/
  results/
    decision.json
```

`decision.html` is the visual alternative. A conforming HTML artifact embeds
the same JSON object in an inert script element:

```html
<script type="application/json" data-agent-studio-decision>
{
  "version": 1,
  "id": "icon-source",
  "title": "Choose the icon source",
  "question": "Which icon family should the new action use?",
  "recommendation": {
    "optionId": "lucide",
    "reason": "It matches the repository's existing outline icon language."
  },
  "options": []
}
</script>
```

Studio renders the HTML in a script-enabled iframe with an opaque origin and a
deny-by-default content security policy. The iframe cannot call Studio APIs,
read Studio cookies or storage, submit forms, navigate the parent, or select an
action directly. The host parses only the inert JSON block and renders the
trusted action form outside the iframe.

When both files exist, `decision.html` supplies the visual explanation and
`decision.json` supplies the action contract. The standalone JSON file wins if
the embedded copy differs. This permits a rich visual without making repository
HTML an authority boundary.

### JSON shape

```json
{
  "version": 1,
  "id": "icon-source",
  "title": "Choose the icon source",
  "question": "Which icon family should the new task action use?",
  "context": "The action needs search, close, and retry icons in both themes.",
  "recommendation": {
    "optionId": "lucide",
    "reason": "Lucide matches the existing 1.5 px outline language and adds no second visual grammar."
  },
  "options": [
    {
      "id": "lucide",
      "label": "Use Lucide",
      "summary": "Adopt the existing outline family.",
      "consequences": [
        "Consistent with the current shell",
        "One package remains the icon source"
      ],
      "action": {
        "kind": "steer",
        "prompt": "Use Lucide for the new action icons and keep the existing size and stroke tokens."
      }
    },
    {
      "id": "keep-current",
      "label": "Keep the current glyphs",
      "summary": "Accept the existing implementation without another run.",
      "consequences": [
        "No additional implementation work",
        "The mixed icon grammar remains"
      ],
      "action": {
        "kind": "move",
        "targetState": "6-completed"
      }
    }
  ],
  "steer": {
    "label": "Additional guidance",
    "placeholder": "Optional constraints for the next run",
    "required": false
  }
}
```

| Field | Required | Contract |
|---|---|---|
| `version` | yes | Integer `1`. Unknown versions fail closed. |
| `id` | yes | Stable decision id within the task. Lowercase letters, digits, `_`, and `-`. |
| `title` | yes | Short operator-facing heading. |
| `question` | yes | One decision question, not a general status summary. |
| `context` | no | The minimum shared context needed to compare the options. |
| `recommendation` | no | `optionId` plus a concrete evidence-based reason. |
| `options` | yes | One to eight mutually distinguishable options. |
| `options[].id` | yes | Stable id unique within the artifact. |
| `options[].label` | yes | Short action label. |
| `options[].summary` | yes | What choosing this option means. |
| `options[].consequences` | yes | One or more concrete consequences or trade-offs. |
| `options[].action` | yes | An allowlisted existing `steer` or `move` action. |
| `steer` | no | Label, placeholder, and optional required flag for free operator guidance. |

### Action allowlist

`steer` submits the generated prompt through
`POST /api/tasks/{id}/continue` with `mode: "steer"`. The option prompt, option
label, consequences, source artifact, and optional free text are included in the
user message, so the existing Activity stream is the durable explanation.

`move` submits through `POST /api/tasks/{id}/move`. The host accepts only these
targets from a decision artifact:

- `2-ready`
- `5-human-review`
- `6-completed`
- `7-archive`

The selected option and optional free text are sent as `reason`. The existing
lane-change timeline therefore records the decision without a new persistence
store. Existing transition guards still apply. For example, accepting an
escalated coding task still requires its latest task commit to be integrated.

The artifact cannot choose a model, thinking level, CLI, project, arbitrary API
route, or arbitrary lane. It cannot bypass confirmation or transition policy.

## Lifecycle

```text
run identifies Robert-owned choice
  -> writes results/decision.json or results/decision.html
  -> task reaches 5e-escalated
  -> Robert opens Result and applies one option
       -> steer: existing continuation runs on the same task
       -> move: existing lane transition applies with a recorded reason
  -> continued work creates any required follow-up task through the existing
     Task API and records its reference
  -> current task reaches its normal terminal lane
```

A follow-up is not a third decision-surface persistence mode. If the chosen
option requires work outside the current scope, the steered Operator-Agent uses
the existing Task API to create and link that card, then concludes the current
task. The decision surface itself does not write task folders or invent a
parallel queue.

The active surface is acute state. It renders prominently only while the task
is in `5e-escalated`. After the task leaves that lane, the artifact remains
ordinary task evidence and the timeline or Activity entry is the decision
record. A later escalation must replace the active artifact rather than reuse a
stale question.

## Relationship to the existing 5e flow

The decision surface extends 5e. It does not replace it.

| Existing 5e responsibility | Decision-surface responsibility |
|---|---|
| Decide that automation cannot safely continue | Explain the exact human choice |
| Route the card to `5e-escalated` | Render options, recommendation, and consequences |
| Enforce reissue budgets and review-attempt epochs | Submit an operator choice through an existing endpoint |
| Offer generic Continue, Resolve manually, Accept, and Abort escape actions | Offer task-specific choices with optional steering |
| Record lane history and operator requeue boundaries | Supply the reason that those existing records retain |

The live `[[TASK_NEEDS_INPUT]]` banner shown while a CLI process is still
running is also separate. That surface answers an in-flight question through
the existing Steer channel. `decision-surface/v1` is the durable post-run
handoff for a task that has reached the human-owned escalation lane.

No new lane, backend table, workflow engine, or decision status is introduced.

## Failure behavior

- Neither file exists: render no decision surface and preserve the legacy 5e UI.
- HTML exists without embedded JSON: render the isolated explanation, but no
  artifact-derived action form.
- JSON is malformed or violates the allowlist: show a compact invalid-contract
  notice and keep the generic 5e actions available.
- A Move or Steer request fails: keep the selected option and free text, clear
  the pending state, and surface the existing task-action error.
- A request succeeds: keep the action pending until the task leaves the acute
  lane, preventing duplicate submission during the next poll.

## Implementation references

- Result host: `frontend/src/app/features/task-detail/components/decision-surface/`
- Existing actions: `frontend/src/app/features/task-detail/state/triage-actions.model.ts`
- Existing Move reason: `backend/Shared/Models/TaskRequests.cs`
- Existing Steer endpoint: `backend/Features/Tasks/TaskRunnerEndpoints.cs`
- HTML isolation pattern:
  `frontend/src/app/features/project-detail/components/workbench-viewer/`
- Functional icon example:
  `frontend/e2e/fixtures/decision-surface/icon-pick-decision.html`
