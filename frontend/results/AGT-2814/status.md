# AGT-2814 delivery status

## Basis and salvage

- Recovered the task-owned Activity view rendering work from salvage commit
  `9acafd0ba5fbb82ff2862d3a1b35b182df474d99` (outcome `Done`, cut on top of
  `origin/develop` at `b5c278564`), cherry-picked onto this branch as
  `8b4a24960`.
- A second, older salvage candidate (`bf26459c5`, cut on a stale base with
  unrelated destructive deletions) was inspected and discarded.

## Requirement 1 — group and collapse unknown protocol frames

Unknown frames sharing `(cli, adapterVersion, frameType)` collapse into one
muted row with an occurrence count instead of one raw JSON block per
occurrence, with the payload behind the shared disclosure.

- Code: `frontend/src/app/features/task-detail/components/conversation-projection.ts`
  — `protocolNoveltyGroupLabel` (grouping/label) and `emitNonRenderable`
  (accumulates the run and rewrites the marker's `text`/`internalDetail`).
- Test: `conversation-projection.spec.ts` — "groups four unknown frames of the
  same kind into one row with the count and known rows unchanged".
- End-to-end wiring test: `activity-log-view.spec.ts` — "renders four unknown
  frames of the same kind as one grouped Trace row, known rows unchanged"
  (asserts the component, not just the projection helper).

## Requirement 2 — drop empty `[internal event]` rows

A redacted line with no non-whitespace detail is never emitted as a row.

- Code: `conversation-projection.ts` — `emitNonRenderable` returns early
  (`if (!detail.trim()) return;`) before pushing a marker when there is
  nothing to disclose.
- Test: covered by the same "known rows unchanged" assertions in
  `conversation-projection.spec.ts` and `activity-log-view.spec.ts` (no empty
  rows appear alongside the grouped row and the readable rows).

## Requirement 3 — classify `tool_progress` in the runner adapter

`tool_progress` (Claude / CodingAgentRunner 0.7.0) is a known, frequent
progress heartbeat with no per-tool progress UI yet in the product, so it is
classified as ignorable rather than left to fall through to
`totalUnknownFrames`.

- Code: `runner/CliProtocolNoveltyTracker.cs` — added to the ignorable frame
  set for the `claude` CLI, with the reasoning recorded in a comment.
- Test: `runner.Tests/CliProtocolNoveltyTests.cs` —
  `Claude_tool_progress_heartbeat_is_ignored_not_unknown` asserts
  `TryObserveFrame` returns `false` (not observed as novel/unknown) for a
  `tool_progress` frame.

## Requirement 4 — fixture test: 4 unknown + 2 known rows

- Test: `conversation-projection.spec.ts` ("groups four unknown frames...")
  and `activity-log-view.spec.ts` ("renders four unknown frames...") both
  build a fixture stream of four unknown frames of one kind plus known rows,
  and assert: one grouped row, count 4, no empty rows, known rows unchanged.

## Additional evidence-set fixes (raw tool-result envelopes, Runner rows)

**Raw `tool_result` envelope compression.** A pretty-printed transport frame
(`tool_use`, `tool_result`, bare Messages envelope, ...) that is split across
physical stdout lines is buffered and collapsed into one `[internal event]`
marker with the full JSON preserved on `internalDetail` for on-demand
disclosure, instead of printing as raw prose repeated once per physical line
or once per repeated occurrence.

- Code: `conversation-projection.ts` — `MULTILINE_FRAME_OPENER` /
  `PendingMultilineFrame` buffering, `isNonRenderableRawLine`,
  `isStrongFramePart`, `sanitizeProjectionLines`.
- Test: `conversation-projection.spec.ts` (AGT-2793 section, from line ~437):
  - "collapses a pretty-printed tool_result envelope split across physical
    lines into one internal-event marker" — asserts one marker line, and that
    `JSON.parse(internalDetail)` round-trips the original envelope exactly.
  - A second case feeding the same envelope three times in a row, asserting
    it still collapses to one row (matches the operator report of the
    envelope "repeated 3x").

**Runner status rows.** Consecutive `system.status` events with
`category: 'runner'` collapse into a single `runner-group` row (one heading,
one trace affordance for the group) instead of repeating the `Runner` label
once per fact with its own trace button.

- Code: `activity-event-presentation.ts` — `groupConsecutiveRunnerStatus`,
  `mergeRunnerGroup`, `PresentedRunnerGroupEvent`/`RunnerGroupRow`.
- Rendering: `activity-event-presentation.directive.ts` renders the group's
  per-fact breakdown behind the shared disclosure.
- Test: `activity-event-presentation.spec.ts` covers the grouping and the
  per-row disclosure content.

## Verification

- `npm run lint:components`: `OK: scanned 333 Angular components; component
  size budgets hold.` No baselines were raised; the new rendering logic was
  kept within the recorded per-component budgets (extracted into
  `activity-event-presentation.directive.ts` rather than growing an existing
  component past its baseline).
- `ng test` (Vitest via Angular CLI) for the touched specs — 93 passed, 0
  failed:
  `conversation-projection.spec.ts`, `activity-event-presentation.spec.ts`,
  `activity-log-view.spec.ts`, `task-reference-microcard-hydrator.service.spec.ts`.
- `dotnet test runner.Tests --filter "FullyQualifiedName~CliProtocolNoveltyTests"`:
  8 passed, 0 failed.
