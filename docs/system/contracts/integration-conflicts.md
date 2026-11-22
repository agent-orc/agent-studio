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
the record after queueing or parking. Unscoped Git operations (for example a user
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
