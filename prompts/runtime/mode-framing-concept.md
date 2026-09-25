## Concept mode: repository Dossier delivery

This is a concept task. Do not modify product source, configuration, tests, build files, or existing documents outside the concept deliverable.

Create exactly one Dossier directory at `docs/<slug>/` in the project repository. The only repository changes allowed for this run are inside that one directory. The canonical result is the repository Dossier, not a copy under the task `results/` directory. A `results/` copy is optional. The Dossier must contain:

- `workbench.json`, with `schemaVersion`, `id`, `title`, `summary`, `entrypoint: "index.html"`, `status: "decision-pending"`, `phase`, `updatedAt`, `sourceTaskKeys`, and an `implementationTasks` array, which may be empty. Read this task's own visible key from the task context or job metadata and make `sourceTaskKeys` contain that card;
- a self-contained `index.html` in English with clearly identifiable Alternatives, Recommendation, Evidence, and Open decisions sections.

Use the calm house document layout. Read `docs/app/templates/article-document-v2.html`, the canonical AGT-2536 article template, and reuse its current style and figure blocks. If that template is absent in the target repository, use the style block at `docs/operations/haertung-verteilte-ausfuehrung/index.html` as the compatibility fallback. Keep headings as plain nouns, language factual, and reading width bounded. Support light and dark themes through CSS variables. Use inline SVG for diagrams. Do not invent a separate colour system or font family for this dossier.

When the Dossier makes a claim about a visible product surface, prose alone is insufficient: prove the claim with a screenshot in the canonical full-bleed evidence figure. Every such figure must have a caption that states the exact visible fact it proves, an as-of line with the capture date and provenance for every image, useful alternative text, and no more than one accented annotation per finding. Include light and dark captures when theme tokens, contrast, status, or visual hierarchy are part of the claim. Keep published operations-Dossier captures under `docs/operations/<slug>/assets/` with descriptive filenames; while this concept delivery remains at `docs/<slug>/`, use its adjacent `docs/<slug>/assets/` directory.

Use `docs/operations/setup/presentation-capture.md` and `frontend/e2e/visual-evidence/presentation-capture.spec.ts` for deterministic pinned captures. For another surface, follow the browser readiness and page-error pattern in `scripts/stable-frontend-boot-probe.mjs` before capture. In a task worktree, start the dev backend only from a Playwright spec through `frontend/e2e/fixtures/dev-backend.ts`. Do not treat the boot probe itself as screenshot output, and do not fabricate a product state to support a claim.

Use `data-concept-section="alternatives"`, `"recommendation"`, `"evidence"`, and `"open-decisions"` on those four sections. Each `implementationTasks` item must contain a concise `title` and implementation-ready `promptMarkdown`.

Make the Dossier discoverable from the card. In the task job folder, name the exact repository-relative `docs/<slug>/index.html` path in both `results/deliverables.md` and `status.md`. The application may regenerate the rest of `status.md`, but preserves this Dossier reference. A Dossier without both task-file references is incomplete.

Recommend a working option for every decision point. Deliver the completed Dossier with `[[TASK_DONE]]`. Sight review, decision acceptance, and operator approval are pipeline lanes after delivery; do not stop this run to request them or claim that they have already happened. Use `[[TASK_NEEDS_INPUT:<short missing fact>]]` only when a fact you cannot obtain, such as credentials, an absent required file, or ambiguous scope with no defensible recommendation, prevents delivery.
