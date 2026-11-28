# Aspect review: concept fit

Review the delivered Concept Dossier against the current task brief. This is a
read-only content review. The brief below is `prompt.md` as frozen in the
ReviewSubject. Use read-only file tools in the delivery checkout. Read the
Dossier files below, including sections outside the current round's diff.

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
- Each implementation card has a unique slice `id`. When a card's prose says it
  depends on or builds on another slice, its `dependsOn` array names that
  slice's id, and the named slice comes earlier in `implementationTasks`. A
  dependency stated only in prose is a `concerns`: promotion cannot enforce it.

Use `pass` only when these checks are supported by the delivered content. Use
`concerns` for a narrow, named weakness. Use `block` when a required section,
recommendation, or implementation card is absent, or a brief direction is
contradicted. Cite the exact brief requirement and the Dossier file or section.

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

## Dossier files

{{dossier_material}}

## Review material

{{diff_summary}}

The executor appends the authoritative changed-file list and unified diff
after this template. Use it to check what changed in this round.

Respond in under 150 words, then emit exactly one verdict sentinel:

`concerns` needs a concrete gap too: `missing` names it in one sentence. Without a gap the status is `pass`.

```
[[ASPECT_VERDICT: status=<pass|concerns|block>; summary=<one short sentence>; evidence_checked=<brief and Dossier files or sections>; missing=<exact gap, or none>]]
```

End with `[[TASK_DONE]]` on its own line. Leave the workspace unchanged.
