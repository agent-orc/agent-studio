# Result view and case-based templates (Protocol to Result)

Design record for the run-result redesign: replace the one-size-fits-all
bullet summary with a layered, shareable **Result** view whose shape adapts to
what kind of run it was. Ties together three moving parts that share one data
source: the layered view, the summarizer prompt strategy, and the structured
artefacts that feed both the view and the Files tab.

> Status: shipped across two slices. Teil 1 landed the layered client view, the
> case classifier, and the summarizer header. Teil 2 completed the deferred
> remainder: the structured-JSON aspect artefact (section 5) is in the tree
> (`aspect-{id}.json` written by `AspectRunnerService`, rendered by
> `AspectJsonCardComponent`), the two quality-head metrics (files changed, tests
> passed - section 2) now render from summarizer header lines, and the per-case
> template divergence (section 3 / §8.3) reshapes the overview per case. This is
> a concept doc, not an ADR; it becomes a domain map or ADR if the JSON artefact
> contract grows load-bearing consumers.

## 1. Problem and goal

The old run summary was an "Einheitsbrei" bullet list: every run, whatever it
did, rendered as the same flat `## What Was Done` list. The operator wants to
**share** a result, so it needs to read top-down:

1. the overview first (problem to solution), simple enough to share on its own;
2. layered detail underneath ("if you have the energy, read on");
3. a quality/metric head: review verdict, files/tests, duration, tokens;
4. a shape that fits the run: a bugfix, a feature, a refactor, a doc, a
   forensic dig, a UI cleanup, and a blocked/partial run should not all look
   identical;
5. the surface is renamed **Protocol to Result**.

## 2. The layered Result view (shipped)

The middle pane of the task-detail split renders a finished run through
`<app-result-view>` (`frontend/.../protocol-pane/result-view/`), top to bottom:

- **Metric head** - a case badge plus at-a-glance chips (verdict, code-review
  grade, duration, **files changed**, **tests passed**, tokens, commits).
  Answers "is this fine, and how big is it?" before any prose. Chips are emitted
  only when they carry real data. Files and tests ride optional `# Status`
  header lines (`- Files:` / `- Tests:`) that the summarizer emits only when the
  run log proves a real count; `buildResultDocument` parses them and
  `classifyTestsMetric` tones the tests chip (an `X/Y` tally with misses reads
  `warn`, an all-green tally reads `ok`).
- **Overview** - a "problem to solution" card with case-tuned labels (a bugfix
  reads Symptom/Fix, a feature reads Goal/What shipped, a blocked run reads
  Goal/Where it stopped). This is the shareable one-liner.
- **Detail** - the existing rich markdown body (What Was Done / Open Items /
  Notes / Images), delegated to `<app-beautiful-results>` so the redesign adds
  zero regression to source links, diffs, and image lightboxes.

All parse and metric logic is pure and unit-tested in `result-document.ts`
(`buildResultDocument`), so the component is a thin OnPush projection. The view
builds itself from `status.md` plus task metadata, so **every historical run
renders without a backend change**.

The rename is a UI-surface rename: the inspector tab label reads **Result**, but
the tab `id`/`testid` (`protocol`, `inspector-tab-protocol`) and the artefact
(`status.md`) stay as-is so the many inputs/specs keyed on them keep working.
Remaining user-facing "Protocol" strings inside the pane (error title, menu
label, spinner) were renamed; see the follow-up list for the peripheral ones.

## 3. Case taxonomy and classifier (shipped)

Eight cases (`result-case.ts`): `bugfix`, `feature`, `refactor`, `docs`,
`forensics`, `ui-cleanup`, `blocked`, `generic`. Each carries presentation
metadata (`RESULT_CASE_META`): label, glyph, semantic tone, the two overview
labels, a one-line intent `blurb`, and a `layout`. The view is one template
whose overview reshapes per case via a `data-layout` hook (four arrangements:
`standard` stacked, `sequence` stepped flow with a connector arrow,
`before-after` two columns, `blocker` warn callout), plus the `data-case`
styling hook and tone accent. This keeps the divergence visible per case
without eight separate markup blocks; the arrangement lives in
`RESULT_CASE_META.layout` + CSS, so a new case picks a layout by data, not new
template code.

`classifyResultCase` is pure and deterministic and layers four evidence sources,
strongest first:

1. **outcome framing** - a run that did not land (`verdictKind === 'problem'`,
   or label Partial/Needs input/Blocked/Failed) is `blocked`, whatever the work
   type. A blocked bugfix reads better as "here is what stopped me" than as a
   triumphant "fix shipped"; the underlying work type is still recoverable from
   metadata.
2. **explicit hint** - a `- Case: <x>` line the summary prompt emitted.
3. **metadata** - `taskType` (`bug`/`feature`) and `mode` (`research` to
   forensics, `planning` to docs).
4. **body keywords**, then `generic` as the floor.

Because the classifier is client-side, it works for legacy runs and never
blocks on a backend round-trip. The prompt hint (section 4) is the *preferred*
signal but never the *only* one.

## 4. Data source and prompt strategy

`status.md` is the single client-side data source. It is generated by
`SummaryGenerationService` handing a bounded task-level evidence package to the
project pipeline's configured one-shot CLI and model, rendered from
[`prompts/runtime/summary-protocol.md`](../../prompts/runtime/summary-protocol.md).
The contract for the file's shape lives in
[contracts/protocol-style.md §3](../system/contracts/protocol-style.md).

The prompt strategy has three parts:

- **Emit the structure the view reads.** The prompt now asks for a `- Case:`
  line in the `# Status` header and a `## Overview` section (`- Problem:` /
  `- Solution:`) leading the body. The frontend already parsed both; before this
  change no producer emitted them, so every run fell back to a synthesized
  overview and a heuristic case. This is the change that lights up the view.
- **Ground the summary in task intent and delivery evidence.**
  `SummaryGenerationService.BuildSummarySlots` injects `taskTitle`,
  `taskPrompt`, `rounds`, `agentStatus`, `delivery`, and `log`, in that order.
  The task prompt owns `Problem` and acceptance criteria. The round ledger and
  delivery facts prove totals and outcomes. The last-run log is supporting
  evidence only, so a recovery round cannot replace the task-level Result.
- **Keep the verdict deterministic.** The `- Result:` line is still overwritten
  after the model replies from the terminal run-outcome classifier, so the
  protocol, lane routing, and failure toast share one classification. The
  model's `Case` is advisory only.

**Backward compatible by construction.** The two new pieces are optional. A
legacy `status.md` (or a model reply that omits them) still renders: the view
synthesizes the overview from the task title + first bullet and infers the case
heuristically. New runs simply get a sharper, explicit version.

Verification note: a prompt change under `prompts/runtime/` is a CLI-behaviour
change and normally wants a live Haiku probe. In a headless task run with the
dev backend offline that probe cannot execute; it is replaced by deterministic
coverage - the prompt-contract tests in `TaskRunnerPromptTests` pin the emitted
structure and the metadata wiring, and the client parse/classify specs
(`result-document.spec.ts`, `result-case.spec.ts`) pin the read side.

## 5. One data source, two renderings (shipped)

The brief's second half. The per-aspect result documents an agent/reviewer
writes (`aspect-code-quality.md`, `aspect-documentation-impact.md`, ...) kept
their YAML-ish frontmatter + fenced markdown body twin
(`AspectVerdictParsing.RenderReport`) for every existing backend reader, and
gained a **structured JSON source of truth** rendered as **one JSON source, two
presentations**:

1. **Producers write structured JSON.** `AspectRunnerService.RunOneAspectAsync`
   writes `aspect-{id}.json` next to the `.md` twin via
   `AspectVerdictParsing.RenderJson`. The wire shape is `AspectDocument`:
   `{ schemaVersion, aspect, status, summary, details, createdAt, model, tag,
   metrics }`. The verdict sentinel the reviewer emits
   (`[[ASPECT_VERDICT: status=...; summary=...]]`) is parsed into the payload
   rather than left in a code fence.
2. **The Result head carries files-changed and tests-passed.** These two chips
   ship (section 2) sourced from the summarizer's optional `# Status` header
   lines, so the metric head is complete without the Result view fetching and
   merging four per-aspect files. The `AspectDocument.metrics` map stays the
   reserved forward-compat carrier for richer per-aspect counts (empty on the
   wire until a producer fills it).
3. **Files tab renders the JSON structurally.** `AspectJsonCardComponent`
   (parsed by `parseAspectDocument`) shows a meta header (aspect, status badge,
   model) with the details collapsible and a metrics strip, reusing the
   `aspectVerdictTone` / `aspectVerdictLabel` pass/concerns/block tone
   vocabulary rather than a raw text dump.
4. **Backward compatible.** Existing markdown aspect files keep rendering
   through the markdown path; the Files tab branches on artefact shape (JSON vs
   markdown), not on run age, and `TaskScannerService.ListArtifacts` prefers the
   `.json` and suppresses its `.md` twin from the list.

Precedent followed: `.metadata/files.json` (`FileGenerationIndex`) and
`pipeline-execution.json` serialize typed records to camelCase JSON;
`AspectVerdictParsing.RenderJson` reuses the same `CamelCase` +
`WhenWritingNull` options. `AspectDocumentSchemaVersion` pins the wire format;
`aspect-document.model.ts` mirrors the record on the client and must move with
it.

## 6. Theming and self-contained fragments

The Result view and any case template read only Tier-2 semantic tokens
(`--studio-*`, `--severity-*`, `--studio-spacing-*`, `--font-*`), which flip via
the `data-studio-theme` attribute on `<html>`. A fragment injected anywhere in
the document inherits both light and dark automatically - no per-fragment light
bridge (same contract the verdict banner notes). Status colours follow the house
recipe: `color: var(--severity-X); background: color-mix(... 8-18% ...); border:
color-mix(... 40-65% ...)`. The verdict vocabulary is pass/concerns/block and
ok/problem/unclear; there is no letter-grade or "Great" vocabulary in the
frontend, so the metric head reuses the review grade (A-D from the
`code-review:grade-*` tag) and the three-state verdict.

## 7. File map

| Piece | Path |
|---|---|
| Result view component | `frontend/src/app/features/task-detail/components/protocol-pane/result-view/` |
| Result document model (pure) | `.../protocol-pane/result-document.ts` |
| Case taxonomy + classifier (pure) | `.../protocol-pane/result-case.ts` |
| Markdown detail renderer | `.../task-detail/components/beautiful-results/` |
| Tab label (Protocol to Result) | `.../protocol-pane/protocol-pane/protocol-pane-view-model.ts` |
| Summarizer prompt | `prompts/runtime/summary-protocol.md` |
| Summary service + slot wiring | `backend/Features/Review/SummaryGenerationService.cs` |
| status.md contract | `docs/system/contracts/protocol-style.md` §3 |
| Aspect writer (markdown twin + JSON source of truth) | `backend/Features/Runner/AspectRunnerService.cs`, `AspectVerdict.cs` (`RenderJson`) |
| Aspect JSON list preference (prefers `.json`, suppresses `.md` twin) | `backend/Features/Tasks/TaskScannerService.cs` (`ListArtifacts`) |
| Files tab (JSON card + markdown fallback) | `frontend/src/app/features/task-detail/components/prompt-pane/files-pane/` (`aspect-json-card.component.ts`, `aspect-document.model.ts`) |
| Structured findings chips (reuse target) | `frontend/src/app/components/aspect-findings/` |

## 8. Follow-up scope

Delivered in Teil 2 (was follow-up in Teil 1):

1. **Structured JSON aspect artefacts** (section 5) end to end: the
   `AspectDocument` schema, `AspectRunnerService` writing `aspect-{id}.json`, the
   `AspectJsonCardComponent` structured renderer, and backward-compat with
   markdown aspects.
2. **Two metric chips** (files changed, tests passed): shipped in the metric
   head from summarizer `# Status` header lines (section 2).
3. **Per-case template divergence**: the overview reshapes per case via
   `RESULT_CASE_META.layout` + `data-layout` CSS (section 3).

Still tracked, not in scope here:

- **Per-aspect metrics in `AspectDocument.metrics`**: the reserved map is empty
  on the wire until a producer plumbs real diff/test counts into the aspect
  writer. The Result head does not depend on it (it reads the summarizer header
  lines), so this is a pure enrichment, not a blocker.
- **Remaining "Protocol" rename** on peripheral surfaces: the log-overlay
  parsed-entries heading (a different "protocol" concept - the parsed activity
  log, not the run result) and the shell pane-toggle aria/tooltip
  ("Protocol & chat"), left untouched to avoid conflating concepts.
