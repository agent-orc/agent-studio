# Out-of-band Task Completion (concept)

Status: partially implemented. §3 (the reconciliation endpoint), the "extern
erledigt" card badge, the `external_completion` timeline entry, and the
partial-results escalation hint (§3, last paragraph) are shipped. Written to
retire the AGT-1917 corpse — a task whose work was done and committed outside
the runner but whose card stayed stuck in a running phase with an empty summary.

## 1. The problem — work happens, the card stays a corpse

Not every task is finished by a local runner run. Sometimes the work is done
out-of-band:

- an **operator** does it by hand (or in the orchestrator chat) and commits it,
- an **external agent** or a **remote host** produces the result, or
- a run dies mid-flight but the deliverables are already on disk.

When that happens today the card is left looking abandoned even though the work
is real. AGT-1917 was the motivating case: the change was implemented and
committed, but

- `status.md` still said *"escalated / no summary"*,
- `lifecycle.json` was stuck in `post-processing-running` (spamming the scanner
  warning that a card was mid-phase with no live run), and
- the run history ended in a corpse — no event said *"this was finished, just
  not here"*.

The lane could be dragged by hand, but the card's **story** stayed wrong. The
board could not explain what happened to it.

## 2. Model — an out-of-band completion is a first-class ingest, not a hack

The fix treats "work that arrived from outside the local runner" as a first-class
ingest path with its own provenance, not a manual folder edit:

- **Actor `external`.** Timeline events, the decision ledger, and the card badge
  all attribute the completion to `external` (or the concrete operator/agent
  named in the request), distinct from `agent` / `orchestrator` / `system`, so
  the Timeline filter chips can tell externally produced results from runner
  activity.
- **Canonical narrative on disk.** `results/deliverables.md` is the human record
  of *what was delivered and where*; the `external_completion` timeline row and
  the card badge are small renderable summaries that point at it.
- **The lane move is a consequence, not the act.** Moving the card is the last
  step, after the evidence is reconciled — never a substitute for it.
- **Remote delivery requires repository proof.** A remote runner may use this
  path for a missing terminal sentinel only after `git ls-remote` against the
  repository URL in the project registration resolves the delivered ref to the
  exact local result commit. Push success and the configured push URL are not
  evidence. A missing or mismatched ref is a recoverable Escalated failure:
  retain the host worktree and report the cause, hostname, worktree path,
  branch, and recovery recipe instead of writing external-completion
  provenance.
- **The server re-verifies the proof; it does not read it** (AGT-2220). The
  runner's `ls-remote` check is a precondition, not the enforcement point. The
  claim travels as data — `resultSha` + `resultRef` — and the endpoint proves it
  again against the target repository via `GitService.VerifyDeliveredCommit`
  before it writes anything. A completion whose commit cannot be proven never
  becomes a stamp; it becomes an `unverified-delivery` record (see §3.1). This
  closes the shape behind the AGT-2400 ghost badges, the 11.07. phantom wave,
  and the "Delivered lies" series: all three stamped cards from a *sentence*
  asserting verification that nothing ever re-checked.

## 3. The reconciliation endpoint — `POST /api/tasks/{id}/external-completion`

One atomic call reconciles a task finished outside the runner. It is a thin
validation + status-to-HTTP shell over `ExternalCompletionService`, matching the
neighbor task endpoints.

**Request** (`ExternalCompletionRequest`):

| Field | Required | Meaning |
|-------|----------|---------|
| `summary` | **yes** | Result summary that replaces the stale `status.md` text. |
| `deliverables[]` | no | What was delivered and where — each has `path` (repo-relative, optional `@sha`), `url`, and/or `note`. |
| `source` | no | Who / which channel did the work (operator name, agent id, `"chat"`, …). Defaults to `external`. |
| `targetState` | no | Destination lane. Defaults to `5-human-review` (the card still gets a quick operator confirmation). Must be a valid lane. |
| `gateItems[]` | no | Open operator checklist items written to `orchestrator-follow-up.md`, for example a remote `worktree-blocked` salvage failure. |
| `resultSha` | for commit-producing modes | Full 40-character commit the completion claims was delivered. Re-verified against the target repository (AGT-2220). |
| `resultRef` | no | Ref expected to carry `resultSha`. Without it the SHA is searched across all remote refs. |
| `baseSha` | for remote runner reconciliation | Full 40-character attempt base. The endpoint refuses the completion when it equals `resultSha`, because an unchanged base is not a delivery. |

Attribution is split deliberately: the **caller** (`X-Client-Id`) is the operator
who *relayed* the result and drives the `lane_changed` ledger row; the completion
**source** (who actually *did* the work) is the `source` field on the body.

**What it does, in order** (`ExternalCompletionService.CompleteAsync`). Step 0 is
the gate; every later write targets the task's *current* folder first, and the
lane move is last so all the evidence lands together before the folder is
renamed:

0. **Verification gate** (AGT-2220) — before *any* evidence is written, the
   claimed `resultSha`/`resultRef` is proven against the target repository. A
   remote runner request whose `baseSha` equals `resultSha` is rejected without
   mutating the task, even when the result ref resolves correctly: the ref only
   proves that the unchanged base is reachable, not that the attempt delivered
   work.
   `OutOfBandStampPolicy.Decide` then rules: commit-producing modes (`coding`,
   `concept`) need a proven commit; report-only modes (`planning`, `research`)
   deliver a document rather than commits and carry no claim to prove. An
   unproven claim short-circuits into §3.1 and none of the steps below run.

1. **`results/deliverables.md`** — the canonical narrative: date, source, the
   summary, a bullet list of delivered artifacts (paths/URLs + notes), and a
   provenance block. Files under `results/` that predate this reconciliation are
   flagged as the dead run's drafts.
2. **`status.md`** — deliberately *overwritten* (unlike the escalation stub,
   which never clobbers a real summary): a `- Result: Completed out-of-band
   (<source>)` line, the summary, the date, and a pointer to `deliverables.md`.
   Retiring the *"escalated / no summary"* corpse is the whole point.
3. **Optional gate checklist** - non-empty `gateItems[]` rows are appended as
   open items in `orchestrator-follow-up.md`, which makes remote recovery work
   visible in the card's escalation summary.
4. **`task.json` provenance** - `ExternalCompletionInfo { source, summary,
   completedAt }` is recorded on the card, mirrored to the frontend
   `TaskInfo.externalCompletion` and driving the badge (§4).
5. **`lifecycle.json` terminalized** - the phase is set to `awaiting-review` and
   every still-`running`/`pending` intake or post-processing check is flipped to
   `skipped` with a *"Superseded by out-of-band completion"* note, so the card
   stops spamming the `post-processing-running` scanner warning. The mirrored
   `task.json` phase is cleared to match.
6. **`external_completion` timeline row** - actor `external`, summary *"Completed
   externally by <source>"*, `payloadRef` → `results/deliverables.md`, details
   carry `source` and `targetState`. Appended *before* the move so it lands in
   the same folder as the rest of the evidence; the `lane_changed` row is emitted
   by the state machine.
7. **Lane move** - `MoveAsync` to `targetState` (default `5-human-review`). A move
   out of `3-progress` runs `EnterPostProcessingPhase`, which would otherwise
   reset `lifecycle.json` back to `post-processing-running` — the exact stuck
   state this endpoint exists to retire — so the terminal lifecycle is
   *re-asserted* into the moved folder afterwards (idempotent for every other
   source lane).
8. **Evidence commit** - `WorkspaceArtifactCommitService.TryCommitExternalCompletion`
   commits the workspace snapshot (before + after folder) so the reconciliation
   is durable, not just local. A failed commit is logged, not fatal.

**Status → HTTP mapping**: `Success` → `200` with `{ jobId, targetState, source,
evidenceCommitSha }`; `NotFound` → `404`; `InvalidRequest` (missing summary /
unknown `targetState`) → `400`; `MoveConflict` (target folder exists / directory
locked) → `409`; `UnverifiedDelivery` → `409` (see §3.1); `NoDelivery` (attempt
base equals result) → `409`; everything else → `500`. The canonical writes
(status / deliverables / `task.json`) are treated as
fatal on hard failure so the caller is never told a half-reconciled card is done;
the ancillary writes (lifecycle, timeline, commit) are best-effort-logged.

### 3.1 The honest state — `unverified-delivery`

A completion claim the target repository cannot confirm does **not** produce a
weaker stamp, a warning-annotated stamp, or a silent skip. It produces a
first-class record of the refusal:

- **`results/unverified-delivery.md`** — what was claimed (SHA + ref), what the
  repository actually holds at that ref, the verification verdict, the reported
  (unconfirmed) summary, and the next step.
- **`status.md`** — `- Result: Unverified delivery - completion stamp refused`
  plus the reason. No "Completed out-of-band" line is ever written.
- **`delivery:unverified` tag** — the refusal is visible on the board, not only
  in a log line.
- **`delivery_unverified` timeline row** — carries the verdict, the claimed
  SHA/ref and the SHA the repository actually holds.
- **Lane** — escalated under the `unverified-delivery` category.

Nothing on this path writes `externalCompletion`, terminalizes `lifecycle.json`,
or touches `commits[]`: an unproven delivery must leave no artifact that could
later be mistaken for evidence.

Three verdicts are distinguished, and the difference is load-bearing:
`Verified` / `VerifiedContained` (proven — the commit is the ref tip, or is
contained in its history because the branch moved on), the *disproved* verdicts
`ShaMismatch` / `RefMissing` / `CommitMissing` (the repository contradicts the
claim), and `NotVerifiable` (no origin, no claim, unreachable repo). The last one
is never laundered into proof — but it is also never reported as disproof.

**Partial results present.** The same "the work is more real than the card looks"
insight applies to the *escalation* path this endpoint complements. When the
orchestrator runtime routes a card to `5e-escalated` without an automated quality
review, its `status.md` stub used to say only *"no agent-written summary"* — which
made AGT-1917 look twice as lost as it was. When a dying run has left files in
`results/`, the stub instead states that **partial results are present in
`results/`, review them before deciding**, so a reviewer inspects the
deliverables (and can then reconcile them through this endpoint) rather than
treating the card as empty. The probe is best-effort and fails closed: a wrong
*"partial results present"* line is worse than a missing one.

## 4. Frontend — badge + timeline rendering

- **"extern erledigt" badge.** `buildExternalDoneBadge` renders a badge next to
  the CLI/model chip on any card whose `externalCompletion` is set, so a card
  whose work happened outside the runner reads as intentionally done, not
  abandoned. The tooltip names the source and date and points at
  `results/deliverables.md`.
- **Timeline row.** The `external_completion` event renders in the task timeline
  under the `external` actor glyph/filter chip, with the summary line and the
  `results/deliverables.md` payload expandable inline — so the card's history
  shows the external hand-off instead of ending in a corpse.
