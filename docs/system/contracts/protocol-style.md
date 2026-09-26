# Protocol & Activity Log Style Guide

Single source of truth for **what a job's protocol looks like** and **how images flow through it**. Read this before changing anything that touches `status.md`, the Activity Log, the protocol pane, or screenshots.

> **Language:** English. See [AGENTS.md](../../../AGENTS.md#documentation-language).

## 1. Two artefacts, two audiences

A finished job exposes two views of what happened:

| Artefact | Audience | Generator | File |
|----------|----------|-----------|------|
| **Activity Log** | Live observer / debugger | Streamed by the CLI driver, parsed by the frontend | `logs/cli-output.log` (raw) |
| **Protocol** | Reviewer ("what did the task deliver?") | Project-routed one-shot summary of bounded task, round, and delivery evidence, with a deterministic transition scaffold when that summary is missing | `status.md` |

Keep the boundary clean:

- The Activity Log is **mechanical**. Every tool call, every command, every diff snippet. Do not curate it.
- The Protocol is **editorial**. It is a task-level Result the reviewer can scan in 30 seconds. It is regenerated from the full round ledger on demand and after later rounds, so do not hand-edit it.

> ⚠ Agents must **never write `status.md` themselves.** It is application-owned. [`SummaryGenerationService`](../../../backend/Features/Review/SummaryGenerationService.cs) rewrites it from bounded task, round, delivery, and log evidence, while `TaskTransitionService` may create a marked fallback scaffold at a review or terminal transition. Anything written by hand can be lost. Agents may write `results/status.md` as delivery evidence.

---

## 2. The Activity Log (`logs/cli-output.log`)

### 2.1 Marker-line format

The frontend's [`activity-log.parser`](../../frontend/src/app/components/activity-log.parser.ts) classifies entries by leading marker. Drivers translate native CLI output into these markers in `TransformReadLine`:

| Marker | Kind | Example |
|--------|------|---------|
| `● Read /path` | Read | `● Read /src/foo.ts` |
| `● Edit /path` | Edit | `● Edit /src/foo.ts` |
| `● Run <cmd>` | Run | `` ● Run `npm test` `` |
| `● Search <pattern>` | Search | `● Search "TODO"` |
| `● Todo <text>` | Todo | `● Todo Refactor parser` |
| `● Task <text>` | Task | `● Task Investigate flaky spec` |
| Everything else | Message | freeform model output |

Rules:

- ANSI escapes are stripped on write. The base class does this; do not re-add them.
- UTF-8 only. The base class forces UTF-8 stdout/stderr; do not override.
- Streamed line-by-line, never buffered until the run finishes.

See [docs/system/cli/supported-clis.md §2.5](../cli/supported-clis.md) for the per-CLI translation rules.

### 2.2 What the log can reference

Lines may contain:

- Absolute project paths, for example `C:\Projects\...\frontend\src\foo.ts`. The parser leaves these inline.
- Relative paths from the job folder, for example `attachments/abc.png`, `results/foo.png`. These are how images travel from the log into the protocol.
- Inline code spans in single backticks. Avoid triple backticks in marker lines.

---

## 3. The Protocol (`status.md`)

### 3.1 Canonical structure

[`prompts/runtime/summary-protocol.md`](../../../prompts/runtime/summary-protocol.md) is rendered by `SummaryGenerationService` after a successful CLI completion and instructs the project-routed model to emit exactly this English shape:

```markdown
# Status

- Result: <Success|Failed|NoOp|Blocked|NeedsInput|Partial>
- Case: <bugfix|feature|refactor|docs|forensics|ui-cleanup|blocked|generic>
- Acceptance: <n of m met>
- Duration: <total across all task rounds>
- Files: <task total from delivery facts; optional when not verifiable>
- Tests: <task-level tally or named gate result; optional when not verifiable>

## Overview
- Problem: <one sentence restating the task goal or defect from the task prompt>
- Solution: <one sentence naming the root cause or delivered capability and verification>

## What Was Done
- One evidence-backed bullet per acceptance criterion.

## Rounds
- At most one line per round. Omit for single-round tasks.

## Open Items
- Every unmet or not-verifiable acceptance criterion, or "None."

## Notes
- 0 to 3 bullets with warnings, failures, or workarounds. Omit this section when empty.

## Images
- ![](../results/<name>.png) or ![](../attachments/<name>.png). Omit this section when no images appear in the log.
```

The `Case` and `## Overview` block feed the case-based, overview-first **Result** view (the UI surface formerly labelled "Protocol"; the artefact/file stays `status.md`). See [concepts/result-view-and-case-templates.md](../../concepts/result-view-and-case-templates.md) for the layered view and the client-side case classifier. Both are additive and optional: the frontend synthesizes an overview from the task title and the first `What Was Done` bullet and heuristically infers a case when either is missing, so every legacy `status.md` still renders.

The optional `- Files:` and `- Tests:` header lines feed the two quality-head metric chips. They are honest-or-absent and come from delivery facts across all rounds, not the last log. The additive `Acceptance` line reports proved criteria. `Rounds` carries integration recovery, review fixes, and other housekeeping without displacing the task goal from `Problem` and `Solution`.

The six input sections have independent budgets totaling 78,500 characters:
title 500, task prompt 24,000, rounds 12,000, agent-written
`results/status.md` 10,000, delivery facts 12,000, and last-run log 20,000.
Task-prompt truncation preserves the head and tail. A later round or terminal
acceptance regenerates from the full ledger. The card action `Regenerate result`
uses the same service for historical cards.

`TaskTransitionService` enforces the fallback invariant for every move into
`4-auto-review`, `5-human-review`, `5e-escalated`, or `6-completed`. Before the
folder move it creates `status.md` when the file is absent or empty; an
unwritable Result refuses the transition. The fallback is identified by
`<!-- agent-studio:result-scaffold -->` and states only facts available from the
run outcome, grade tag/artifact, `results/deliverables.md`, task metadata, and
the computed integration projection. A post-move refresh may enrich that marked
scaffold with the target-lane integration status. A real generated protocol is
never overwritten. The startup repair applies the same scaffold once to
missing Results in `5-human-review`, `6-completed`, and `7-archive`, adding
`<!-- agent-studio:operator-result-backfill -->` for provenance. Each newly
created scaffold emits a structured `result-scaffold-created` warning with
`project`, `job`, target `state`, provenance, and a process-local recurrence
count. Refreshing the same owned scaffold after a lane move does not increment
the count.

The rendered Result view recognizes this marker as presentation metadata. It
replaces the scaffold's internal `## Overview` provenance prose with one compact
notice that explains the missing `status.md`, shows the best available
timestamp and stable card key, and links the task's artifacts. `## What Was
Done`, `## Open Items`, and the remaining evidence body
still render normally. Composite task ids, absolute paths, and the HTML marker
do not appear in the rendered view; the source file and scaffold creation
mechanic remain unchanged.

Remote coding runs preserve the same application ownership. After the runner
flushes `cli-output.log`, its final artifact upload carries Result-finalization
intent through both transports. The compatibility endpoint awaits bounded
`SummaryGenerationService` attempts against that durable log. V1 runs execute
the Task Server's `post-result-finalization` step and persist `status.md` as an
application-generated artifact from durable run events and deliverables. The
runner may tear down its worktree only after the finalization acknowledgement.
Only the summary step retries; the completed core agent run never reopens.
Successful finalization is `Ready`. Exhaustion is the typed terminal state
`Degraded`, remains reviewable, and may use the marked transition scaffold only
as the explicit fallback described above.

Hard rules:

- No `# Status` is omitted. No extra `H1`s are added (`## Overview` is an H2 and leads the body).
- `Case` is one of the eight ids above; a run that did not fully land (Blocked / NeedsInput / Partial / Failed) uses `blocked` whatever the underlying work was.
- Total prose is at most 250 words. Images and Rounds do not count.
- Paths and commands in single backticks.
- No marketing tone, no recap of what the user already asked for.
- No em dashes.
- A deterministically-appended `## Images` bullet may carry a plain-text source hint (`(source: real|mocked|composite ...)`); the grammar and where it comes from are in §4.4.
- The application enforces the `Result` line from the deterministic run-outcome contract after summarization, so the protocol, lane routing, and failure toast share one classification. The `Case` is a hint only; the client's `blocked` framing still wins from the enforced `Result` even if the model mislabels it.

If you change the prompt, mirror the change here and bump the example.

### 3.2 Why it's regenerated, not hand-written

- The reviewer sees a fresh summary of the **task**, including all recorded rounds and the final delivery, rather than a report of only the most recent run.
- The "Regenerate result" action re-runs the configured summary pipeline step with the bounded task prompt, round ledger, agent-written delivery status, delivery facts, and last-run log tail.
- Application-owned `status.md` remains replaceable by regeneration. Agent-written `results/status.md` is evidence supplied to the summary input and is not treated as the complete result by itself.

---

## 4. Image flow

Two folders, two purposes. Keep them separate:

| Folder | Direction | Lifetime | Used by |
|--------|-----------|----------|---------|
| `<job>/attachments/` | **Input.** Pasted/dropped into the prompt editor before the run. | Created lazily, persists with the job. | The CLI agent reads them via the relative path baked into `prompt.md`. |
| `<job>/results/` | **Output.** Screenshots produced *during* the run that prove the change, plus the optional `review-evidence.jsonl` audit/review findings file. | Created on demand by the agent or reviewer, persists with the job. | The protocol pane renders screenshots inline and lists findings in the evidence panel; the reviewer reads both. |

Bare filenames in `status.md` (e.g. `![](../foo.png)` with no folder prefix) are resolved against `results/` in the local reader as a fallback for older protocols. New work should always include the prefix.

### 4.1 Where images come from per CLI

| CLI | Image-producing capability | Default landing spot | Retention rule |
|-----|---------------------------|----------------------|----------------|
| **Claude Code** | None native. Produces images only by running tools (Playwright, custom scripts). | Wherever the tool writes them. | Agent must `cp`/`mv` into `<job>/results/` before declaring done. Otherwise lost on next Playwright run. |
| **Codex CLI** | Same as Claude. No native screenshot capability. | Tool-driven. | Same. Copy into `<job>/results/`. |
| **GitHub Copilot CLI** | Same. No native screenshot capability. | Tool-driven. | Same. Copy into `<job>/results/`. |
| **Gemini CLI** | Same. No native screenshot capability. | Tool-driven. | Same. Copy into `<job>/results/`. |
| **Playwright** (driven by any CLI above) | Test artifacts (screenshots, videos, traces) via `page.screenshot()`, etc. Project-wide default `outputDir: 'test-results'`. **Auto-harvested by `JobArtifactReporter` when `JOB_RESULTS_DIR` env var is set.** | When running under the agent task orchestrator: `<job>/results/playwright/<spec-name>/...` with `index.json` summary. Local dev (no env var): `frontend/e2e/test-results/<spec>/...` (ephemeral). | **Under orchestrator:** persistent, auto-copied with summary index. Protocol pane renders these images inline. **Local dev:** ephemeral scratch, manually copy if needed for review. Never reference `test-results/<...>.png` from durable `status.md`; use `results/` paths. |

**The retention rule, restated:** If a screenshot matters for the protocol, it must end up under `<job>/results/`. When running under the orchestrator, `JobArtifactReporter` copies Playwright artifacts automatically; otherwise manually `cp`/`mv`. Anywhere else is treated as scratch and may disappear on the next test run, the next CI cleanup, or a `git clean`. Do not reference `test-results/<...>.png` from `status.md`; it works locally for ten minutes and breaks for the reviewer.

### 4.1.5 Screenshot strip and workspace reel

Two endpoints expose the harvested files for the inline strip in the
protocol pane and the workspace-wide visual evidence reel:

| Endpoint | Purpose |
|----------|---------|
| `GET /api/tasks/{id}/results/{**path}?watchPath=...` | Guarded, task-relative artifact server for top-level and nested `results/` files. HTML is served inline under a response sandbox; known preview types retain their MIME type and unknown types retain download behavior. |
| `GET /api/tasks/{id}/screenshots?watchPath=...` | Recursive walk over `<job>/results/`, ordered oldest-first, captioned by spec/folder name with optional pass-fail status from `results/playwright/index.json`. Drives the per-task strip + lightbox above the protocol body. |
| `GET /api/tasks/{id}/screenshot?path=<rel>&watchPath=...` | Compatibility image server used by screenshot listings. Path-traversal-guarded; only image content types are served. |
| `GET /api/workspace/screenshots?windowHours=N&projectFilter=...` | Newest-first reel across every watched job whose `results/` folder was touched inside the window. Drives the workspace "Visual evidence" overlay (`#/workspace/screenshots`). |

The retention rule from §4.1 still applies: only files that already
live under `<job>/results/` are surfaced. There is no separate index
or upload path, and no CDN.

### 4.1.6 Workspace executive summary

The executive summary answers "what happened in the last N hours?"
across every watched project. It never invents events: every row
references a record the aggregator can prove on disk (an orphan-recovery
line, a supervisor advisory, a merged commit, a decision-journal entry).

| Endpoint | Purpose |
|----------|---------|
| `GET /api/workspace/summary?windowHours=N` | Folds per-project job moves, decisions, advisories, commits, crash evidence, and open human decisions into one `ExecutiveSummary` payload. `windowHours` defaults to 24 and accepts 1 / 6 / 24 / 168. Drives the workspace "Executive summary" overlay (`#/workspace/summary`, deep-link alias `#/summary`). |

The payload is validated by
[`executive-summary.schema.json`](../schemas/executive-summary.schema.json).
The decisions block is folded from the per-project journal
`logs/decisions/<project>.jsonl` (one row per
[`orchestrator-decision.schema.json`](../schemas/orchestrator-decision.schema.json)),
ranked by severity (`High > Warn > Info`) then recency. The overlay
exposes a 6 h / 24 h / 7 days toggle that re-queries the endpoint and
persists the choice in `localStorage`.

The per-task protocol header that the summary complements is described
by [`protocol-header.schema.json`](../schemas/protocol-header.schema.json):
the structured front-matter a `status.md` carries so the protocol pane
can render a header card and a multi-step history navigator.

#### Deferred, with a proposed follow-up task

This task shipped the workspace-level surface end to end (both schemas,
the aggregator endpoint, the decisions fold, and the `#/workspace/summary`
overlay). Three producers remain deferred because they depend on runner
state-machine instrumentation that does not exist yet:

1. **`RuntimePromptService` header writer** - inject the validated
   `protocol-header.schema.json` block into each job's `status.md` as the
   agent runs, rather than relying on hand-authored front-matter.
2. **Multi-step history snapshots** - append-only copies of each phase
   (analysis / plan / implement / review / decisions / fixes) under
   `<job>/history/`, so the protocol pane can show a step navigator.
3. **Protocol header card + step navigator (frontend)** - the per-task
   render of (1) and (2); distinct from this task's workspace roll-up.

These are intentionally one cohesive follow-up rather than four loose
ends: all three hang off the same missing prerequisite (a writer that
stamps protocol state during a run) and share one schema
(`protocol-header.schema.json`). Proposed follow-up task:
**"Protocol header writer and per-task step history"** - implement the
`RuntimePromptService` header injection, the `<job>/history/` snapshot
copies, and the protocol-pane header card + step navigator that consume
them. The workspace summary already links each project to its decisions
journal, so the per-task surface can reuse the same records.

### 4.2 Local rendering

Task, Activity, Result, and Docs render Markdown through the canonical
`<cac-markdown>` surface or the Result view's specialized renderer. A shared
host resolver binds artifact links to the open card rather than the browser URL
or an execution-host filesystem path:

| Markdown link source | Resolved URL |
|----------------------|--------------|
| `results/<path>` | `/api/tasks/{jobId}/results/<path>?watchPath=…` |
| Allowed text `logs/<path>` (`.log`, Markdown, JSON/JSONL, CSV, XML, YAML, or plain text) | `/api/tasks/{jobId}/files/logs/<path>?watchPath=…&scope=workspace` |
| Absolute runner path ending in `results/<path>` or `logs/<path>` | Same task-relative route after discarding the host-specific prefix |

Artifact links open in a new tab. The server response decides inline preview or
download by content type. External URLs, source references, Wiki links, and
task references keep their existing navigation behavior. Traversal and encoded
traversal are not rewritten.

Image sources use the same card context and map as follows:

| Markdown source | Resolved URL |
|-----------------|--------------|
| `attachments/<name>` | `/api/tasks/{jobId}/attachments/{name}?watchPath=…` |
| `results/<path>` | `/api/tasks/{jobId}/results/{path}?watchPath=…` |
| `<name>.png` (no prefix) | `/api/tasks/{jobId}/results/{name}?watchPath=…` (fallback for legacy protocols) |
| Absolute `http(s)://…` | passed through unchanged |

The flat `attachments/<name>` endpoint serves only files directly under that
folder. The guarded `results/{**path}` route serves both top-level and nested
result artifacts. Screenshot listings retain the compatibility
`/screenshot?path=...` image route. All serving paths reject traversal; see
`TaskScannerService.ResolveAttachment`, `TaskScannerService.ResolveResult`, and
`ScreenshotIndexService.ResolveScreenshotFile`.

### 4.2.5 Playwright artifact harvesting

When the agent task orchestrator runs a CLI (Claude Code, Codex, Copilot, Gemini), it sets the `JOB_RESULTS_DIR` environment variable to `<job>/results`. The `JobArtifactReporter` custom Playwright reporter (wired in `frontend/playwright.config.ts`) monitors this env var:

- **If set (orchestrator mode):** copies all test artifacts (screenshots, videos, traces) from `frontend/e2e/test-results/<spec>/...` into `<job>/results/playwright/<spec>/...`, preserving the subfolder structure. Writes `<job>/results/playwright/index.json` with a summary listing test status and artifact paths.
- **If unset (local dev):** reporter is silent; Playwright artifacts stay in the ephemeral `test-results/` folder as usual.

The frontend's markdown renderer and protocol pane already handle `results/playwright/<spec>/<name>` paths just like any other `results/` image. The task summary (`status.md`) extracts image references from its evidence; if the run produced screenshots and the CLI mentioned them, the routed model includes them in the `## Images` section. `SummaryGenerationService` also runs a deterministic pass over the full CLI log and appends any missing `results/` or `attachments/` image references so visible proof is not lost when the summarizer omits a path or the image appeared before the bounded last-run tail.

### 4.2.6 Review-evidence panel reference rendering

The evidence panel (`review-evidence-panel.component`, driven by `review-evidence.jsonl`) renders each finding's `artifacts` and `fileRefs` — it does **not** go through the `status.md` markdown path in §4.2, so its reference rendering is specified separately here:

| Reference kind | How it renders |
|----------------|----------------|
| Bitmap image (`.png` / `.jpg` / `.jpeg` / `.webp` / `.gif` / `.avif` / `.bmp`) | An **inline thumbnail** (not a bare path). The `src` is resolved by [`resolveProtocolImageSrc`](../../../frontend/src/app/features/task-detail/components/protocol-pane/protocol-image-resolver.ts) to the same `/api/tasks/{jobId}/results/{name}?watchPath=…` URL as §4.2. Thumbnails are `loading="lazy"` / `decoding="async"` (a finding may carry many PNGs) and height-bounded (`max-height: 96px`). Clicking one opens the shared `MediaLightboxService` gallery focused on the clicked image. |
| Any other reference (`.md`, `.jsonl`, `.log`, config, `.csv`, source `file:line`, …) | A labelled **text row** prefixed by a **file-type-specific glyph** (`refIcon()` distinguishes markdown / json / log / config / csv / code from a plain file) instead of one generic artifact icon. |

Both surfaces read `--studio-*` design tokens so they render correctly in either theme. Image refs are pulled out of `artifacts` + `fileRefs` (artifacts first) so the lightbox gallery index matches the on-screen thumbnail order.

### 4.3 Git policy

Task folders live in the central task-store evidence repository, never in the
product repository or this app's source repository. The product position is:
**the protocol is the durable record; the screenshots are local proof.** Logs
are text and cheap, so the evidence repository can retain them; images are
heavy binaries and stay local.

Recommended `.gitignore` for the central `TaskRepository` evidence checkout:

```gitignore
# Canonical projects/<PROJ-NNN>/tasks/<lane>/<task>/ layout.
projects/*/tasks/*/*/attachments/
projects/*/tasks/*/*/results/
**/logs/cli-output.log.1
```

Logs (`logs/`) are intentionally **not** ignored in the evidence repository;
the active `cli-output.log` is the audit trail. Its bounded `.1` predecessor is
ignored because the active file already carries the rotation marker and newest
tail. Product-repository `.gitignore` files do not need Agent Studio task-folder
rules because onboarding never creates task data there.

Already-committed images are not retroactively untracked by adding these rules. If a workspace has historical images in git, run `git rm --cached <path>` once to stop tracking them; the files stay on disk.

### 4.4 Screenshot source labels and reference validation

Evidence screenshots are only trustworthy if the reviewer can tell **how** each was captured and can trust that every link resolves. Two conventions make that explicit; both are honoured by the screenshot strip, the lightbox, and `SummaryGenerationService`.

**Source label (filename suffix).** A screenshot declares its provenance through the final `--`-delimited segment of its filename, before the extension:

| Filename | Source | Meaning |
|----------|--------|---------|
| `dashboard--real.png` | `real` | Captured against a running backend. The recommended evidence for UI-acceptance claims. |
| `dashboard--mocked.png` | `mocked` | Captured from an e2e run whose API routes were mocked (for example Playwright `page.route`). Allowed, but labelled so nobody mistakes it for live proof. |
| `before-after--composite.png` | `composite` | A stitched image; parts unspecified. |
| `before-after--composite-real-mocked.png` | `composite` | A stitched image whose parts are a `real` shot and a `mocked` shot, in that order. |
| `landing-board--pinned.png` | `pinned` | Documentation or marketing capture from the committed deterministic workspace snapshot. |
| `dashboard.png` | `unlabeled` | No recognised suffix. The UI makes no real/mocked claim. |

The base name may contain single dashes (`before-after`); only the `--` boundary introduces the source segment, so pre-existing filenames are always `unlabeled`. The parser lives in [`ScreenshotSourceParser`](../../../backend/Shared/Models/ScreenshotSource.cs); the label is surfaced text-only next to each thumbnail caption and in the lightbox. A composite spells out its part sources (for example `composite (real, mocked)`). `Pinned` is a documentation-capture class and does not redefine task-run evidence: `real`, `mocked`, and `composite` retain their existing meanings.

**Recommendation, not compulsion.** Mocked-route screenshots stay allowed; they are the right evidence for some changes. But for **UI-acceptance** evidence, prefer a shot against a running backend and name it `--real`. Composite / stitched before-after images are explicitly welcome; just label the parts. Documentation and marketing images use `--pinned`. When `SummaryGenerationService` deterministically appends an image to the protocol's `## Images` section, it annotates the bullet with a plain-text source hint (for example `- ![](../results/dashboard--mocked.png) (source: mocked)`) so the provenance reads straight from `status.md`. Unlabeled files get no hint; the protocol never claims a source it cannot prove.

**Reference validation.** Every regenerate runs [`ProtocolImageReferenceValidator`](../../../backend/Features/Review/ProtocolImageReferenceValidator.cs) over the finished `status.md`. Any job-local image link (`results/...`, `attachments/...`, or a bare filename resolved under `results/`) that points at a missing file is recorded as a `warn`-severity `review-evidence.jsonl` finding (`id = broken-image-ref:<path>`) instead of rendering as a silently empty `<img>`. The reviewer sees the broken link in the evidence panel; external URLs, `data:` URIs, and rooted/absolute paths are left alone.

---

## 5. Changing this contract

Before you touch any of the moving parts:

1. If you change the **Result summary prompt** in `prompts/runtime/summary-protocol.md`, update section 3.1 in this file in the same PR.
2. If you change the **marker-line vocabulary**, update §2.1 here and the corresponding row in [docs/system/cli/supported-clis.md §2.5](../cli/supported-clis.md).
3. If you add a new **image folder** convention, add a row to §4 and a resolver branch in `protocol-pane.component.ts`.
4. If you add a new **CLI**, fill in its row in §4.1 with observed behaviour, not assumptions.
5. If you change the **screenshot source-label grammar** or the **reference validator**, update §4.4 here alongside `ScreenshotSourceParser` and `ProtocolImageReferenceValidator` in the same PR.

The single-source-of-truth rule from [AGENTS.md](../../../AGENTS.md): if the doc and the code disagree, the doc is wrong. Fix it.
