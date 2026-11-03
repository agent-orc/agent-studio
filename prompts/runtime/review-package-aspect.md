# Package review: {{aspect_title}}

This is a **package review in shadow** (Dossier AGT-3400). The package holds
{{member_count}} card(s) of one Dossier wave. They are applied in stack order
on the current integration tip, each with its own commits, so every changed
line belongs to one member. Review the aspect below once for the whole
package and give **one verdict per member**. Your result is recorded for
comparison only; it does not move any card.

## Aspect rules

{{aspect_rules}}

## Package

- **Project:** `{{project}}`
- **Dossier:** `{{dossier_path}}`
- **Aspect:** `{{aspect_id}}`

## Members in stack order

{{members}}

The stacked material follows this prompt: the integration tip, the stacked
candidate, and each member's diff from the candidate before it to the
candidate after it. Line numbers on the `+` side of a member diff are lines of
the stacked candidate.

## What you must emit

Reason briefly, under 300 words, member by member. Name the files and diff
sections you checked. Then end with exactly one fenced block tagged
`package-review` that holds one JSON object:

```package-review
{
  "members": [
    {
      "taskKey": "<member key>",
      "status": "pass|concerns|block",
      "summary": "<one short sentence>",
      "acceptanceCriteria": [
        { "criterion": "<criterion as listed>", "status": "met|unmet|not-assessed" }
      ]
    }
  ],
  "findings": [
    {
      "file": "<repository-relative path>",
      "line": 1,
      "severity": "block|concern|note",
      "summary": "<one short sentence>",
      "member": "<member key you believe owns the line>",
      "relatedMembers": ["<other member key>"]
    }
  ]
}
```

Rules for the block:

- `members` lists every member exactly once: {{member_keys}}.
- `acceptanceCriteria` repeats the member's listed criteria. Mark a criterion
  `met` or `unmet` only where this aspect can judge it; otherwise use
  `not-assessed`.
- Every finding cites a `file` and a `line` of the stacked candidate. A
  finding without both is discarded.
- The line decides the owner: the member whose commits introduced the cited
  line owns the finding. A line no member introduced is a cross-card finding
  and goes to the later member in stack order. `member` is only your hint.
- A member `block` needs at least one finding of this aspect owned by that
  member; without one it is recorded as `concerns`.
- Use an empty `findings` array when there is nothing to cite.

Then end with `[[TASK_DONE]]` on its own line.
