# Aspect review: concept fit

Review the delivered Concept Dossier against the current task brief. This is a
read-only content review. The brief below is `prompt.md` as frozen in the
ReviewSubject; the executor appends the exact Result-SHA diff containing the
delivered Dossier. Use that diff and the results inventory as evidence. Do not
infer the Dossier from the agent's status claim.

Check each requirement the brief actually makes:

- Every required Dossier section is present with substantive content.
- Operator directions in the brief are followed and not contradicted. An
  explicit Dossier direction that contradicts the brief is a `block`, even if
  build and lint pass.
- Every open decision has a stated recommendation. Operator acceptance may
  happen later; recommending an option is part of the delivery.
- Implementation cards exist in the delivered workbench source data and cover
  independently reviewable slices. Check the `implementationTasks` entries or
  another explicit card source named by the brief.

Use `pass` only when these checks are supported by the delivered content. Use
`concerns` for a narrow, named weakness. Use `block` when a required section,
recommendation, or implementation card is absent, or a brief direction is
contradicted. Cite the exact brief requirement and the Dossier file or section.
If the exact Dossier content is absent or truncated so a required check cannot
be made, return `concerns` and name the missing evidence. Do not claim a pass.

## Project and card

- Project: `{{project}}`
- Card: `{{job_id}}` - {{job_title}}

{{card_mode}}

## Delivery acceptance scope

{{acceptance_scope}}

## Current brief (`prompt.md`)

```
{{task_body}}
```

## Status summary

```
{{status_summary}}
```

## Results inventory

```
{{results_inventory}}
```

## Review material

{{diff_summary}}

The executor appends the authoritative changed-file list and unified diff
after this template. That material is the source for Dossier content.

Respond in under 150 words, then emit exactly one verdict sentinel:

```
[[ASPECT_VERDICT: status=<pass|concerns|block>; summary=<one short sentence>; evidence_checked=<brief and Dossier files or sections>; missing=<exact gap, or none>]]
```

End with `[[TASK_DONE]]` on its own line. Do not edit files or run tools.
