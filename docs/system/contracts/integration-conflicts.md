# Integration conflict evidence (version 1)

Integration conflicts are selection failures as well as delivery failures. Capture
runs while the Git index and conflict markers still exist, before abort or temporary
worktree cleanup. The evidence does not select cards or change transition rules.

The application stores one JSON record per project/card/delivery SHA/target/pinned
integration tip under the local state root's `agentstudio/integration-conflicts/`
project hash directory. Atomic replacement persists resolution changes. Repeated
rebase and merge recovery stages, fallback probes, and retries against the same tip
do not inflate the count. A new target tip is a new collision. Task moves, archives,
process restarts and worktree cleanup do not remove records. Include this directory
in local-state backups. Existing historical conflicts are not backfilled.

## Stable record

The wire and disk record uses camelCase and `version: 1`. Add fields compatibly;
change the version before changing meanings. This is the input contract for the
future Dossier selection role.

| Field | Meaning |
|---|---|
| `id` | SHA-256 identity of project, card, target, delivery SHA and integration tip. |
| `project`, `cardKey`, `integrationTarget` | Project name, colliding delivery's card and target branch. |
| `deliverySha`, `deliveryBaseSha`, `integrationTipSha` | Exact Git objects. Use the fenced delivery base when available; otherwise the Git merge base. A batch tip can contain admitted siblings not yet published. |
| `detectedAtUtc` | First conflict detection for this identity, never reset by retries. |
| `reviewPassedAtUtc`, `reviewWaitSeconds` | Matching passing review time and elapsed seconds at first detection. Null when the review time is unavailable; never substitute coding completion time. |
| `commitsSinceBase` | All commits reachable from the pinned tip but not from the delivery base, including merge commits. |
| `files[].path`, `files[].hunkCount` | Exact repository-relative path and textual marker hunk count at first detection. Null for nontextual conflicts, including binary, mode and delete/modify conflicts. No file-list truncation. |
| `files[].cardKeys` | Other primary card keys from the subjects of commits touching this file in the base-to-tip range, case normalized. The first key identifies the managed commit owner; secondary title labels and body references are excluded. Attribution is commit metadata, not proof that every named card caused an overlapping hunk. Unattributed commits remain visible below. |
| `files[].commits[]` | Exact touching `sha` and its attributed `cardKeys`, including empty lists when no card is named. |
| `resolution`, `resolutionAtUtc` | Latest handling route and its timestamp: `pending`, `union`, `mechanical`, `agent-round`, `parked`, or `deferred`. Handling routes do not prove successful publication. An agent round is a queued recovery route, not proof of successful final integration. Union preserves both additions; mechanical denotes other automatic recovery. Deferred denotes a sibling collision waiting for another batch. |

Capture covers the individual integration runner, batch assembly, formation
admission and per-task fallback candidate probes. The agent-round service updates
the record after queueing or parking. Every resolution update must name the exact
integration tip as well as the delivery and target. Missing tips leave the record
unchanged; updates never select the latest collision as a substitute. Batch sibling
deferral updates the candidate tip that actually conflicted, even after a clean
probe against the batch base. Individual merges finalize their pinned target
before releasing that scope. Review history is read lazily at the first conflict
capture, once per merge scope; clean merges do not read it for this evidence. Unscoped Git operations (for example a user
merge outside the pipeline) do not create project evidence. Recovery call sites
explicitly capture after reading the unmerged index; `ListUnmergedFiles` only
reads Git facts. Evidence I/O failures are logged and must not interrupt merge
abort, worktree cleanup, or recovery routing. Such failures can leave a gap in
history, so the rate describes captured conflicts, not guaranteed complete history.

## Read API and summary

`GET /api/projects/{name}/integration-conflicts?from=<ISO-8601>&to=<ISO-8601>&target=develop`

`from` and `to` default to the last 30 days. The range is half-open `[from,to)`;
invalid or greater-than-366-day ranges return 400. Unknown projects return 404.
`target` is optional and case sensitive, as Git branch names are.

Reads use the requested project's archive-inclusive task snapshot. Unchanged
timeline and integration-lane files reuse receipt projections; only batch IDs
referenced by published receipts are loaded. Missing manifests are retried on
the next read. Conflict JSON is cached by file length and modification time,
with range and target filtering in the ledger. A cold read still reads this
project's historical receipts to preserve first-integration deduplication across
time ranges; it does not walk other projects or list all batch manifests.

The response contains `project`, `from`, `to`, `records`, and `summary`:

- `conflicts`: unique record identities detected in the range.
- `integratedCards`: unique card/target pairs whose first recorded successful
  integration falls in the range. Archive-inclusive task timelines and published
  integration-lane receipts supply the denominator. Reacceptance and duplicate
  receipts do not add cards. This is recorded integration evidence, not a count
  of commits or accepted cards.
- `per100IntegratedCards`: `100 * conflicts / integratedCards`. Null when there
  is no denominator; it can exceed 100 because a card can hit multiple tips.
- `topFiles`: path and collision count, descending, one count per record/file.
- `cardPairs`: canonical unordered card pairs and collision counts, counted once
  per record even when several files name the same pair.
- `daily`: UTC day, conflicts, integrated cards and rate. Includes empty days;
  the first and last buckets can be partial. Counts sum to the range totals.

The project Pipeline panel shows the last seven days, daily rates, top five files
and top five pairs. Missing history, loading, errors and zero denominators have
explicit states. History begins when capture is deployed, so the absence of old
records cannot validate a historical conflict-rate claim.

## Automatic individual integration recovery (AGT-4048)

Both direct-merge recovery and net-change replay return the exact delivery SHA,
target tip and conflicting files. The latter uses `merge-tree`, so its files have
no index markers and their hunk counts can be null. The automatic round includes
the target's commit subjects, owning card keys and changed paths since the
delivery's leased base. The target patch starts at that base's common ancestor
with the target, so an unintegrated continuation base does not show the delivery's
own changes as reversals. Missing legacy bases use the Git merge base.
Context beyond 48,000 characters carries an explicit truncation notice and the
range to inspect.

The shared decision library allows two automatic conflict rounds per card and
target, counted across new deliveries and reviews. The third conflict escalates
with the prior rounds' files and cards. Each collision records one handling row
in the card timeline, with delivery SHA, target tip, files and involved cards.
The follow-up retains the source run's CLI and model from its session ledger
(with the existing card route as the legacy fallback), selects medium reasoning,
and moves to the front of Ready after persisting the route and session delta.
The runner owns merging develop into the delivery branch; the agent resolves
conflicts keeping both sides, runs focused verification and delivers again.

Individual integration also saves `integration-conflict-recovery.json` in the
application-owned task folder. It moves with the card and has no expiry. Review
id changes, explicit integration retries and process restarts cannot erase the
failed collision. Integration can run again only when the delivery SHA changes,
or when a changed target tip includes commits or a tree change on the conflicting
paths. Missing delivery identity or unknown Git evidence retains the collision.

Receipt reads catch filesystem and JSON failures, including missing required
fields. A missing receipt is normal. An unreadable receipt is logged and leaves
the affected delivery with an explicit integration error before Git is invoked;
it is retained for diagnosis. Other cards can still drain. Its reservation cannot
be reconstructed until the receipt is readable again. Retry and changed-path Git
facts are read outside the coordinator's queue lock. A queue change during those
reads causes selection to restart; a cached replay can be discarded only if it is
still the completed replay that was inspected.

These retry receipts control admission. The collision-ledger evidence above
remains optional. Receipt writes catch filesystem failures and clean up their
temporary file on a failed atomic replacement. If a conflict receipt cannot be saved, that
delivery returns an explicit integration error and other deliveries can drain.
Preparing a recovery saves its queued receipt in one replacement before changing
the pending intent, delivery subject, route or session delta. A persistence
failure leaves the reviewed delivery intact and records the failed preparation.
Failure to delete an old receipt after successful publication is logged without
changing the successful result or preventing branch reclamation. The receipt
may remain until filesystem access is repaired.

Board publication also reads task folders and conflict receipts outside that
shared lock. It captures the queue, orders that snapshot using recovery
reservations, and publishes only if the queue identity and revision still match.
Concurrent enqueue, dequeue, hold or activity changes require a new snapshot;
removing or replacing the queue discards an old publication. This can repeat
reservation reads when the queue changes, without blocking other projects on
filesystem I/O.

A queued recovery reserves its conflicting paths and original delivery time.
The individual integration queue holds later overlapping deliveries while the
recovery is in Ready, Progress or Auto Review. A repaired delivery retains that
original time when it re-enters the queue; unrelated deliveries can pass. A
successful integration removes the reservation, and parking or moving out of
those active lanes stops it applying. Holds and infrastructure errors retain the
receipt. The cost is waiting on shared files for the recovery and its review,
plus repeated receipt and Git reads while waiting. Reservation-wait sleeps are
capped at one second. An already running merge
cannot be preempted. Batch formation, gates and review verdicts are unchanged.
