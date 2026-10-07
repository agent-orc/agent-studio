# Workflow-Sized Task Cutting

Use one leaf card for one independently deliverable and reviewable unit. A card
may reference a larger Epic, Dossier, or recommendation list for context, but
its acceptance boundary must fit one delivery.

## Card boundary

- Cut one card per implementation slice.
- Use an Epic or another parent card when several slices share one outcome.
- Put dependencies between slice cards when ordering matters.
- Do not create one leaf card that says `implement all recommendations`,
  `complete the Dossier`, or another open-ended equivalent.
- Keep criteria observable. Name the behavior, artifact, or verification that
  makes this slice successful.

For an approved Dossier, each `workbench.json` `implementationTasks` entry is
one slice and promotes to one coding card. New entries should carry an explicit
bounded acceptance scope. Legacy entries without the field are treated as one
slice using that entry's title and prompt. Promotion rejects one open-ended
entry that asks for all recommendations.

### Decision items

An entry whose slice depends on a fork the Dossier cannot settle carries a
`decision` block instead of a coding slice. Promotion then creates a decision
card (kind `decision`) with that options payload rather than a coding card:

```json
{
  "title": "Stable release contract",
  "promptMarkdown": "Optional context for the decider.",
  "decision": {
    "question": "Ship the Stable release with a lock file?",
    "options": [
      { "id": "a", "label": "Lock file", "consequences": "Reproducible installs" },
      {
        "id": "b",
        "label": "No lock file",
        "requirements": [
          {
            "title": "Release identity without lock file",
            "promptMarkdown": "Derive the release identity from the manifest.",
            "acceptanceScope": { "deliveryMode": "bounded-slice", "slice": "S1", "criteria": ["..."] }
          }
        ]
      }
    ],
    "recommendedOptionId": "a",
    "recommendationReason": "Reproducibility outweighs churn."
  }
}
```

A decision item needs a `title`; `promptMarkdown` is optional context. The
`decision` block follows the decision card contract (a question, two to four
options with unique ids, an optional recommendation). Each option may list
`requirements`: implementation items with the same shape and the same
workflow-sized rule as a top-level entry. Once the decision is taken, the chosen
option's requirements become `2-ready` coding cards through the same promotion
ledger; see [the task domain](../system/domains/tasks.md#decision-cards).

## Acceptance scope contract

The card stores its review boundary in `job.json` as `acceptanceScope`. Task and
Epic creation APIs accept the same field:

```json
{
  "acceptanceScope": {
    "deliveryMode": "bounded-slice",
    "slice": "S1: stop repeated requirement-fit loops",
    "criteria": [
      "The same requirement-fit block escalates after two review rounds",
      "The escalation evidence includes the repeated aspect and reason"
    ]
  }
}
```

`deliveryMode: bounded-slice` requires a non-empty `slice` and at least one
criterion. The requirement-fit reviewer treats those fields as the maximum
acceptance boundary. A broader task body, linked Dossier, parent objective, or
unchecked recommendation remains context and cannot block the slice.

A missing scope retains the full-task interpretation. For older cards only,
the reviewer may infer a bounded slice when the prompt explicitly says
`partial delivery is success`, `one slice per delivery`, or an equivalent
supported declaration. Do not rely on inference for newly cut work.

## Review convergence

Requirement-fit reviews compare the delivered diff and evidence with the
card's acceptance scope, not with the parent wishlist. One bounded slice can
therefore pass while later slices remain open.

When the same blocking aspect and normalized reason repeat in consecutive
review rounds, the orchestrator escalates the card with that finding instead of
starting another coding round. The default identical-block limit is two
rounds. The ordinary task-wide reissue budget still bounds changing findings.
An operator requeue starts a new explicit attempt epoch; service restarts do
not reset either chain.

## Dossier example

Cut this:

1. S1 card: add the retry fingerprint and bounded escalation policy.
2. S2 card: expose the escalation reason in task history.
3. S3 card: add operator-facing recovery controls.

Do not cut one card named `Implement all retry Dossier recommendations`.
