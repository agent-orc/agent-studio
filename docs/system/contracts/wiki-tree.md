# Wiki Tree, Rendering & Per-Doc History

The project-level **Wiki / Docs** rail renders the watched project's complete
`docs/` tree from one project-wide source. `wikiSourceBranch = null` preserves
the legacy checkout-backed behavior. A configured git ref such as
`origin/develop` reads tree, content, assets, Pulse inputs, and history from that
ref without switching the working tree.

Branch-backed reads resolve the ref to a commit and materialize `docs/` once as
a SHA-addressed read-only snapshot through the shared Git info cache path. Warm
navigation reuses that snapshot. The Wiki header reports the source branch and
short commit, so Stable and Dev never imply a source from their deployment
checkout.

When hosted publication is enabled, the branch-backed source is pinned to the
**published commit** rather than re-resolved per reader. A supervised sync
fetches the accepted ref, materializes its `docs/` snapshot, and only then swaps
one immutable published-revision reference; every Wiki surface resolves against
that commit until the next promotion. This is what keeps tree, page, assets,
history, search, Pulse, and revision preview single-valued while the source
branch moves on, and it is why a failed fetch or an invalid revision leaves the
previous revision online instead of degrading a read. A published Wiki stays
read-only under the write policy below. The operational contract - freshness
SLO, credentials, triggers, typed deployment failures, and the rollback drill -
is in
[Hosted Wiki publication](../../operations/setup/hosted-wiki-publication.md).

The same source selection applies to Workbench discovery and document reads.
`GET /api/projects/{projectName}/workbenches`, the Workbench catalogue embedded
in Pulse, `GET /api/projects/{projectName}/workbenches/{id}`, the Wiki tree, and
`GET /api/projects/{projectName}/wiki/files/{relPath}` must resolve the same
checkout or branch snapshot. A Workbench found only in the deployment checkout
is therefore not advertised while the project reads its Wiki from another ref.
This closes the former split in which `WorkbenchCatalogueService` always scanned
the working tree while `ProjectDocsService` served a configured Git snapshot.

There is no app-owned organization manifest, no virtual grouping layer, and no
compatibility shim for historical root-level pages. If the Wiki should show a
folder, that folder must exist under `docs/`.

- Backend surface: [`backend/Features/Docs/ProjectDocsEndpoints.cs`](../../../backend/Features/Docs/ProjectDocsEndpoints.cs),
  [`backend/Features/Docs/ProjectDocsService.cs`](../../../backend/Features/Docs/ProjectDocsService.cs),
  [`backend/Features/Docs/ProjectWikiSourceResolver.cs`](../../../backend/Features/Docs/ProjectWikiSourceResolver.cs),
  [`backend/Features/Docs/WikiContentCache.cs`](../../../backend/Features/Docs/WikiContentCache.cs),
  watcher integration in
  [`backend/Features/Tasks/TaskWatcherService.cs`](../../../backend/Features/Tasks/TaskWatcherService.cs),
  git lookups in [`backend/Features/Git/GitService.cs`](../../../backend/Features/Git/GitService.cs).
- Frontend surface: `frontend/src/app/features/project-detail/components/project-wiki-section/`
  with the tree model in `wiki-tree.ts` and the history panel in
  `wiki-doc-history/`.
- Wire shapes: [`frontend/src/app/models/project-docs.model.ts`](../../../frontend/src/app/models/project-docs.model.ts).

## Wiki content cache contract

`WikiContentCache` is the single process-wide source for assembled Wiki reads.
One project snapshot contains the physical tree and its content signature, the
full page list, metadata and saved order, folder projections, Home sections,
Pulse filesystem projections, and the Dossier catalogue used by Pulse.
`/wiki`, `/wiki/tree`, `/wiki/recent`, `/wiki/pulse`, `/wiki/folder`, and
`/wiki/home` all acquire that same snapshot. They never validate it by walking
`docs/` again during a request.

The host preloads every registered project before it starts serving HTTP. A
newly registered project is preloaded when its watcher is installed. Each
project repository also has a recursive `docs/` watcher. Its burst-debounced
event performs a synchronous cache invalidation and rebuild. Backend Wiki
mutations perform the same eager rebuild directly, including page save/create,
classification, ordering, Home curation, grading completion, move, and delete.
The mutation does not return across its cache consistency boundary until the
replacement snapshot has been published. This guarantees read-after-write and
keeps the next HTTP reader warm.

Page content reads use the source context stored in that published snapshot.
They do not resolve `wikiSourceBranch` independently after the tree or Pulse was
built. If a requested page is absent or the selected source cannot be
materialized, the file endpoint returns a source-aware reason. The reader shows
that reason as an alert instead of replacing it with a generic failed-load
placeholder.

Cache keys normalize display names and short codes to the immutable project id
when a registry record exists. A watcher event that carries a display name
therefore invalidates the same slot used by id-based API routes.

The docs content signature is computed only while filling a snapshot. It is not
a per-request freshness probe. Git history and per-file date indexes retain
their existing HEAD-keyed caches, but they consume page existence, titles, and
folder membership from the central Wiki snapshot.

## Physical tree contract

The docs root is `<project repo>/docs`.

The tree endpoint recursively surfaces:

- folders that contain at least one visible document descendant,
- `.md` files as Markdown pages,
- `.html` and `.htm` files as sandboxed HTML pages,
- image assets only through the asset endpoint when referenced by a page.

Hidden entries (dot-prefixed names such as `.gitkeep`) are skipped. Empty
folders are pruned from the navigation tree.

Siblings are sorted folders first, then files. An optional leading numeric
prefix such as `01-`, `01_`, or `01.` controls sort order and is hidden from the
display title. Without a prefix, items sort by display title. A saved category
and document drag-order (`docs/app/config/wiki-order.json`) overrides each kind
per sibling group; unlisted folders or documents sort behind their saved peers
in the existing default order. The file has schema `wiki-order/v2`, with
`folderOrder` and `fileOrder` maps keyed by the parent docs-relative path
(`""` means the docs root).

There is no pinned node and no immutable node: every folder and page follows
the same sort, rename, move, and delete rules. (The former Engineering
Workstream frame — a pinned `docs/engineering-workstream/` root with locked
folders and shells — was retired 2026-07-19; see the hardening chronicle
[workbenches/haertung-verteilte-ausfuehrung/historie.html](../../operations/haertung-verteilte-ausfuehrung/historie.html)
for the historical record.)

The display title for a document is its first H1 when present; otherwise it is
the file name without extension and without the optional order prefix.

Every page also receives one canonical interaction type: `doc`, `concept`,
`workbench`, `incident`, or `report`. Companion classification is the primary
source. A registered `workbench.json` entry page is always a Dossier. Agreed
path families fill remaining gaps, with `doc` as the default. The tree and page
head use the same type-to-icon mapping.

A malformed `workbench.json` never produces an openable Workbench row. Catalogue
projections retain a disabled repair row with the concrete validation error so
an operator can distinguish invalid metadata from a missing page. Valid
descriptors are listed only when their entrypoint exists inside the selected
Wiki source and passes the Workbench containment and size checks.
Automatic Workbench reference-key assignment runs only for a writable checkout.
A read-only branch snapshot is never mutated for discovery; a descriptor without
a previously assigned key remains readable with a null key until checkout-backed
discovery persists one.

## Durable agent-read evidence

Observed agent reads are runtime telemetry, not document metadata. Each page's
evidence is stored outside the tracked docs tree at
`<project repo>/.orchestrator/wiki-agent-reads/<docs-relative-page>.json`. For
example, `docs/concepts/runner.md` maps to
`.orchestrator/wiki-agent-reads/concepts/runner.md.json`. The repository-wide
`.orchestrator/` ignore convention keeps these atomic state files out of Git,
so read observation cannot make an integration checkout dirty.

```jsonc
{
  "schemaVersion": "wiki-agent-reads/v1",
  "sourcePath": "docs/concepts/runner.md",
  "total": 23,
  "lastReadAt": "2026-07-22T10:15:00Z",
  "recent": [
    { "at": "2026-07-22T10:15:00Z", "taskKey": "AGT-2242" }
  ]
}
```

`total` is the lifetime count reconstructed from durable task CLI logs plus
continuously observed local and remote runs. `recent` is newest first and
retains at most 20 reads. The one-time startup backfill is guarded by
`<TaskRepository>/.metadata/wiki-agent-reads-backfill-v1.json`; runtime state
updates use atomic replace writes.

Adjacent `<page>.meta.json` companions may still contain `agentReads` blocks
written before 8 August 2026. Readers fall back to that legacy block when no
runtime state exists. The first subsequent read copies the complete legacy
total and retained history into runtime state before adding the new event,
without changing the tracked companion. When a grading or classification write
later changes that companion for a content-metadata reason, the same baseline
is persisted if needed and the legacy block is removed. This copy-on-write
migration preserves the history while keeping telemetry-only writes outside
Git from the first read after deployment.

Content-metadata companion writes remain versioned. Autonomous grading and
task-link producers commit their exact companion paths through the managed
repository mutation boundary and queue the resulting SHA for background push;
interactive classification writes use the commit-backed Wiki endpoint. Only
the runtime `agentReads` projection belongs under the ignored sidecar tree.

The persistence and initialization contract is:

- startup scans the complete current and archived task inventory before CLI
  reattachment, including every `logs/cli-output.log` line rather than the
  bounded UI log window,
- the marker has schema `wiki-agent-read-backfill/v1` and records completion
  time, logs scanned, and reads applied; its presence makes later startups a
  no-op,
- a restart after runtime-state writes but before marker creation is safe because
  backfill merges a monotonic reconstructed baseline instead of adding the
  baseline again,
- local CLI output and fenced remote-runner log ingestion both feed the same
  live attribution method after the log line is durable,
- each runtime update is serialized per page and published with an atomic
  temporary-file replacement.

Only actual read tool uses and recognized read-only shell commands count.
Agent prose that merely mentions a `docs/**` path, writes, edits, `docs/app/**`
contracts, companion files, and generated reports do not count. This evidence
is observational only. It never affects drift, gates, or workflow state.

## Pulse drift groups and the `human-action` convention

The wiki Pulse drift bar grades the **real top-level `docs/` folders**: every
top-level folder that holds at least one page is a drift group (first path
segment = group; the group title is the folder name without its order prefix).
Folders without pages do not appear; group order follows the saved
`docs/app/config/wiki-order.json` root order, unlisted folders behind in the tree's
default order (numeric `NN-` prefix, then name). Pages
directly at the docs root belong to no group. The Pulse change-feed area badge
uses the same top-folder mapping.

The **`human-action` signal is a folder-independent frontmatter convention**:
any wiki page (every document type the tree surfaces is scanned; in practice
the signal lives in Markdown frontmatter), wherever it lives, that carries
frontmatter with

```yaml
human-action: <what a human should do>
status: observed   # or: active
```

raises a Pulse warning (`kind: human-action`) until its `status` leaves
`observed`/`active` (e.g. becomes `resolved`). The `human-action` value is the
action text shown to the operator.

## Page lifecycle frontmatter

Designs, concepts, and explorations opt into one shared lifecycle by carrying
the fields defined in
[`wiki-page-lifecycle.schema.json`](../../app/schemas/wiki-page-lifecycle.schema.json):

```yaml
---
lifecycleSchema: wiki-page-lifecycle/v1
pageKind: exploration
lifecycleState: review-requested
editedBy: "Robert"
editedAt: 2026-07-21T05:46:33Z
lifecycleHistory:
  - state: review-requested
    editedBy: "Robert"
    editedAt: 2026-07-21T05:46:33Z
    note: "Options are ready for a decision."
---
```

The states are `in-progress`, `review-requested`, `decided`, and `done`.
`editedBy` and `editedAt` describe the lifecycle edit, not an inferred Git
author. Every transition appends a history entry and updates the current state,
editor, and timestamp together.

This frontmatter is the lifecycle source of truth for Markdown. The adjacent
`.meta.json` companion remains authoritative for grading, consolidation
classification, and task links and must not copy lifecycle fields. HTML cannot
carry leading YAML, so a Dossier uses the same field names and values in its
single `workbench.json` descriptor (`schemaVersion: 2`). Pulse normalizes both
authoring shapes into one projection and groups them by the same state machine.

The descriptor may also carry `pattern: ui | concept`. This presentation hint
selects the catalogue icon and the article-template variant, not a separate
lifecycle or content system. Missing and unknown values resolve to `concept`
without making the descriptor invalid. Existing active documents adopt the
field and the canonical v2 article template only when they are otherwise
changed. The authoring contract and copyable source live in
[`article-document-authoring.md`](../../operations/article-document-authoring.md).
That contract also owns the full-bleed screenshot evidence figure, per-image
capture dating, provenance, sparse annotation, theme coverage, and the
`docs/operations/<slug>/assets/` publication convention.

## API endpoints

All paths are rooted at `/api/projects/{projectName}/wiki`. `{projectName}` is a
`WatchPaths` entry name; an unknown project yields `404`. Wiki paths are always
relative to `docs/`, must not contain `..`, and must not be rooted.

The tree, recent-edits, per-file history, and immutable revision reads return
an `ETag` with `Cache-Control: no-cache`. A matching `If-None-Match` returns
`304 Not Modified` without a response body.

### `GET /wiki/tree`

Returns the recursive physical docs tree. A page's compact `metadata` includes
`agentReads` when runtime state or a tolerated legacy companion contains
observed read evidence.

```jsonc
{
  "projectName": "Agent Task Processor",
  "baseDir": "C:/repo/docs",
  "exists": true,
  "source": {
    "mode": "branch",
    "branch": "origin/develop",
    "commit": "8d10db4e...",
    "shortCommit": "8d10db4e",
    "writable": false,
    "error": null
  },
  "root": [
    {
      "name": "architecture",
      "title": "architecture",
      "relPath": "architecture",
      "type": "folder",
      "children": [
        {
          "name": "model.md",
          "title": "Architecture Model",
          "relPath": "architecture/model.md",
          "type": "md",
          "children": []
        }
      ]
    }
  ]
}
```

### `GET /wiki/recent`

Returns recently edited Wiki pages in newest-first Git order. The Overview uses
this endpoint as a 15-second conditional poll while it is visible. A `304`
leaves the rendered feed untouched; a changed response replaces the feed only
when its page rows differ.

### `GET /wiki/files/{relPath}`

Reads a Markdown or HTML page from the physical docs tree. HTML pages are served
as content and rendered by the frontend in a script-enabled sandboxed iframe.
The iframe grants only `allow-scripts`; without `allow-same-origin` it receives
an opaque origin and cannot inherit Studio's origin or directly access its
cookies, storage, or DOM. Network requests remain subject to normal browser and
CORS policy; this sandbox is not a network-deny boundary.

### `GET /wiki/folder/{relPath}`

Returns one directory level for the folder overview table. Each page row uses
the author date of the most recent commit that touched that file, not the
working-copy mtime. Folder rows use the newest date among their visible
descendant pages. The backend obtains all per-file dates through one
`git log --name-only` walk over `docs/` and caches that index by repository
HEAD, so rendering a folder never runs Git once per row.

Page rows include the same optional `agentReads` projection as the tree.
Folder rows never carry agent-read evidence.

`updatedAtSource` is `git` for committed history. A new local page with no Git
history falls back to its filesystem mtime and returns
`updatedAtSource: "mtime"`; the UI marks that fallback with an asterisk and an
explanatory tooltip.

### `GET /wiki/assets/{relPath}`

Streams a referenced image or diagram asset. This is intentionally limited to
image-like extensions so wiki pages can render relative screenshots without
turning the endpoint into arbitrary file serving.

### `GET /wiki/history/{relPath}`

Returns per-document provenance and Git history.

```jsonc
{
  "relPath": "architecture/model.md",
  "model": "claude-opus-4-8",
  "metadata": {
    "model": "claude-opus-4-8",
    "updatedAt": "2026-06-09",
    "reason": "distil run learnings",
    "taskKey": "ASS-1709",
    "status": "active",
    "runCount": "3",
    "hasFrontmatter": true
  },
  "commits": [
    {
      "sha": "8d10db4e...",
      "shortSha": "8d10db4e",
      "authorDateUtc": "2026-06-09T20:03:16Z",
      "author": "Crash Recovery",
      "subject": "Wiki: improve navigation",
      "filesChanged": 15,
      "added": 1905,
      "removed": 87
    }
  ]
}
```

Provenance precedence: frontmatter `model:` / `last-distilled:` / `why:` wins;
when absent, `model` falls back to the `Co-authored-by` trailer of the most
recent commit that touched the file. `commits` is empty when the repo root
cannot be resolved, but frontmatter still renders.

The history validator is scoped to the selected file: it combines the latest
commit that touched that file with its live working-tree mtime. Unrelated
repository commits therefore remain `304`, while committed or uncommitted edits
to the open file change the ETag. The reader uses that change only to show an
update banner; content is fetched again after the operator confirms reload.

### `GET /wiki/revisions/{sha}/{relPath}`

Returns the content of a Markdown or HTML page at a past commit so the history
panel can preview old revisions.

### `POST /wiki/pages`

Creates a real `.md`, `.html`, or `.htm` page under `docs/` and commits it into
the project repository.

### `POST /wiki/folders`

Creates a real folder under `docs/`, seeds `.gitkeep`, and commits it. The folder
appears in the Wiki once it contains at least one visible page.

### `POST /wiki/move`

Moves or renames a real file or folder through `git mv` and commits the change.
This is how Wiki organization changes are made: move the actual path.

### `PUT /wiki/classification/{relPath}`

Updates page lifecycle metadata in the adjacent companion and commits that
sidecar. `status: archived` retains the source file at its current path as quiet
history. `status: aktuell` restores the current classification. This endpoint
does not move or delete the source page.

### `GET /wiki/home`

Reads curated Wiki Overview sections from `docs/app/config/home.json`. These
links are shared repository navigation, not operator-local favorites.

### `PUT /wiki/home/pins/{relPath}`

Adds, moves, updates, or removes one shared Overview entry and commits
`docs/app/config/home.json`. A pin request carries `pinned: true`,
`sectionTitle`, `label`, and optional `note`; `pinned: false` removes the page
from every section. The page itself remains unchanged.

### `PUT /wiki/folder-order` and `PUT /wiki/file-order`

Both endpoints accept `{ "parentRelPath": "concepts", "orderedNames": [...] }`,
persist the respective sibling order in `docs/app/config/wiki-order.json`, and
commit that config change. The response uses the standard wiki mutation shape.
The frontend applies document reorders in place, then soft-refreshes the tree
and any mounted folder overview after persistence succeeds.

### `DELETE /wiki/files/{relPath}`

Deletes a real file or folder through `git rm` and commits the change.

## Write policy

Checkout-backed Wikis retain commit-backed page edits, creates, moves, deletes,
and uploads. A branch-backed Wiki is deliberately read-only: all mutation
endpoints reject the operation with an explicit divergence-prevention message,
and the UI disables the corresponding controls. Operators switch the project
setting back to Checkout before writing. The application never guesses a write
branch and never writes into one checkout while displaying another ref.

## Frontend behavior

The Wiki behaves like an app inside the app:

- only one document is open at a time,
- the left navigation and right context panel can be collapsed and resized,
- collapsed / width / selected-page state survives F5 through local storage,
- the filter is subtle and opt-in,
- right-click on files and folders opens a text-only context menu,
- the context rail shows file path, metadata, history, linked-doc information,
  drift actions, and the open page's agent-read total, last-read timestamp, and
  recent task history,
- folder overview tables show a narrow `Reads` column with the total and a
  last-read tooltip,
- an open page has the shared page-head action bar for task creation, archive,
  project chat, and shared Home curation; type-specific actions follow the
  standards.
- stars are the operator's personal shortlist and feed only the Starred panel;
  Home pins are shared Git-backed navigation and never enter that panel.

Page chat stays in the existing project `OrchestratorContextKey`. The current
page is embedded in `navigationContext` as `pageRef`, `pageTitle`, `pageType`,
and a bounded `pageExcerpt`. See
[Wiki as a Cognitive Interface](../../concepts/wiki-as-cognitive-interface.md).

## File organization rule

When a page feels lost, move it into a better real folder. Do not create virtual
categories, manifest nodes, or root-level back-compat pages. The repository tree
is the Wiki tree.
