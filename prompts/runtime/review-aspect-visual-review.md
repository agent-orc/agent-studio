# Aspect review: visual delivery

Review the delivered frontend template or style change at the exact Result-SHA.
If the authoritative changed-file list has no `frontend/` template (`.html`)
or style (`.scss` or `.css`) change, return `pass` as not applicable.
Judge the attached **after** screenshots directly. Compare each matching
**before** screenshot where present. Check layout, spacing, separation,
alignment, clipping, and text contrast in light and dark themes and at narrow
widths. Look especially for adjacent footer metrics collapsing into a run-on
line or a dangling separator. A screenshot inventory alone is not visual proof.

The review executor appends the changed-file list, diff, screenshot names, and
absolute image paths. Open every relevant image with the image-viewing tool.
If the executor reports no screenshots for a frontend template or style change,
return `concerns` naming the missing screenshot evidence. If an image cannot be
opened, return `concerns` naming that image. Do not infer a pass from filenames.

The executor also includes the applicable rules from
`docs/quality/design/style-guide-hard-rules.md` and
`docs/operations/admin-design-guideline/index.html`. Apply those rules to the
visible surface. Prefer `concerns` for a visible layout defect such as a run-on
footer; use `block` for a severe, broadly unusable layout. A clean layout with
adequate theme and width evidence passes.

## Project / Job

- **Project:** `{{project}}`
- **Id:** `{{job_id}}`
- **Title:** {{job_title}}

{{card_mode}}

## Task body

```
{{task_body}}
```

## Diff summary

```
{{diff_summary}}
```

## results/ inventory

```
{{results_inventory}}
```

Emit a short finding. Cite the screenshot filename **and the affected region**
for every visual concern or block. For missing evidence, cite the changed
template or style path and name the missing screenshots in `missing`.

```
[[ASPECT_VERDICT: status=<pass|concerns|block>; summary=<one sentence>; evidence_checked=<screenshot and region, or changed paths>; missing=<exact gap, or none>]]
```

End with `[[TASK_DONE]]` on its own line. Do not modify files.
