# Magazine

The magazine holds the summaries that agents keep up to date inside the
Studio, such as the orchestrator's decision notes ("29 cards wait for you:
24 can be accepted, 4 need your decision, 1 gets rework"). There is one
magazine for the installation, one per workspace, and one per project
(MAG-1, AGT-3714). MAG-2 builds the views on this store and API. PT-8
(AGT-3601) composes immutable workspace issues from persona items and cites
workspace article revisions.

PT-11 adds a monthly team review to the first weekly issue of each month.
Its immutable snapshot covers the previous calendar month per persona, with
importance-weighted accepted value per 100,000 tokens, cost per accepted
finding, noise, Owner rating counts, linked misses and rule-based suggestions.
The issue keeps persona and role separate, so several personas can share a
role. Suggestions require an Owner decision; publication never retires a persona.

Code: `backend/Features/Magazine/`. CLI: `studio-op magazine publish`
([studio-op](../../operations/setup/studio-op.md#magazine)).

## Model

An article belongs to exactly one scope: `installation`, `workspace:<id>`
(a `WorkspaceRegistry` id) or `project:<id>` (a project id; a short code
resolves to the id). Its slug, 1 to 80 characters of `a-z`, `0-9` and inner
hyphens, is unique within the scope. A publisher refreshes the same slug
instead of creating a new article.

| Field | Meaning |
|---|---|
| `title` | One line, at most 200 characters. |
| `summary` | One short paragraph, at most 600 characters, no blank line. |
| `body` | Markdown text, at most 256 KiB in UTF-8 (`magazine-body-too-large`, 413). |
| `kind` | `digest`, `decision-brief`, `report` or `note`. |
| `author`, `authors` | The principal of a revision, set by Studio from the authenticated caller, an optional persona name, persona id and role id. `authors` lists every distinct author. Old articles without ids remain readable. The orchestrator may publish with a persona name and no role id until persona records exist. |
| `sources` | Up to 100 `{ kind, ref }` entries with kind `card`, `dossier` or `batch`. |
| `createdAt`, `updatedAt`, `revision` | `updatedAt` and `revision` change with every content update. |
| `pinned`, `archivedAt` | Flags. Changing them appends no revision and leaves `updatedAt` alone. |

Every upsert appends a revision, and every earlier revision stays readable.
A title, summary, or body holding a credential-shaped value is refused
(`magazine-credential-in-content`) rather than masked.

## Store

Articles live in the server data store, not in workspace commits:

```text
<TaskRepository>/.magazine/<scope folder>/<slug>/article.json          head: current revision, flags
<TaskRepository>/.magazine/<scope folder>/<slug>/revisions/000003.json  one immutable revision
```

The scope folder is `installation`, `workspace-<id>` or `project-<id>`.
Each upsert first writes the next revision file and then swaps the head,
both through `IAtomicJsonFileWriter`, under one store lock that also checks
the expected revision. A crash between the two writes leaves an
unreferenced revision file, and the next upsert overwrites it. A revision
the head has referenced is never rewritten. That makes
`scope/slug@revision` a stable citation for an issue.

`.magazine/` is server state, handled like `.security/`
(`MagazineStorePathPolicy`):

- It is in the workspace `.gitignore` and `info/exclude`.
- The tracked sweep skips it.
- Runtime retention, which prunes `.runtime/`, leaves it alone.
- Backups include it with the rest of the workspace.

The store is not proxied to a standalone Task Server.

## API

| Route | Permission | Answer |
|---|---|---|
| `GET /api/magazine?scope=<scope>[&includeArchived=true]` | `magazine.read` | `{ scope, articles }`, without bodies, pinned first, then newest `updatedAt`. Archived articles only with `includeArchived`. |
| `GET /api/magazine/{scope}/{slug}[?revision=n]` | `magazine.read` | The article with `body`, `bodyFormat: "markdown"`, `revision` (the one shown) and `currentRevision`. `ETag` is the current revision. |
| `GET /api/magazine/{scope}/{slug}/history` | `magazine.read` | `{ scope, slug, currentRevision, revisions }`, newest first, without bodies. |
| `PUT /api/magazine/{scope}/{slug}` | `magazine.write` | Upsert. The body is `{ title, summary, body, kind, persona?, personaId?, roleId?, sources? }`. 201 on create, 200 on update. |
| `POST /api/magazine/{scope}/{slug}/pin` | `magazine.write` | `{ pinned }`, default `true`. |
| `POST /api/magazine/{scope}/{slug}/archive` | `magazine.write` | `{ archived }`, default `true`. `false` restores the article. |

How `PUT` treats `If-Match`:

- An existing article needs `If-Match` with its current revision (`"3"`, `3` or `W/"3"`).
- Without the header, Studio answers 428 `if-match-required`.
- With a stale revision, Studio answers 409 `revision-conflict`.
- Both refusals carry `currentRevision`.
- A create sends no `If-Match`. A positive revision on an absent article is a 409.
- A request above 2 MiB is refused before it is read (`magazine-body-too-large`).

Errors are `{ error, message }` with stable codes: `magazine-scope-invalid`,
`magazine-scope-not-found`, `magazine-scope-denied`,
`magazine-article-not-found`, `magazine-revision-not-found`,
`magazine-slug-invalid`, `magazine-title-invalid`,
`magazine-summary-too-long`, `magazine-summary-paragraphs`,
`magazine-kind-invalid`, `magazine-source-invalid`,
`magazine-sources-too-many`, `magazine-persona-invalid`,
`magazine-body-invalid`, `magazine-request-invalid`.

The body is text in a JSON string. Studio never renders it as HTML or
executes it. The MAG-2 views must render the Markdown through the
frontend's sanitizing renderer.

## Views (MAG-2)

The header Magazine action opens the installation scope. Workspace rows in
the Explorer open their scope, and the project hub Magazine rail opens the
project scope. Installation and workspace views list the visible scopes below
them. Lists put pinned articles first, then order by update time. A browser
remembers the latest revision opened for each article and marks a later
revision as updated. An article shows its sources and read-only revision
history. Its Markdown is rendered without raw HTML, and references are
resolved against the viewer's available cards, Dossiers and projects when
shown. Unresolved references remain text.

## Access

`magazine.read` and `magazine.write` are project-level permissions:

- Owner holds both.
- Admin and Developer hold both, as the legacy operator.
- Operator agent holds both. This is how the orchestrator agent's API token
  publishes.
- Viewer, Product (`contributor`) and Approver hold `magazine.read`. The
  Product route allowlist (`ContributorAccessPolicy`) admits the magazine reads.
- An API token with the `viewer` role cannot publish: 403 `permission-denied`.

The middleware addresses `project:<id>` routes to that project, so a
member without the project gets 403. Installation and workspace routes and
the list are deferred to `MagazineAccess`, which applies these rules:

- `project:<id>`: the permission in that project.
- `workspace:<id>`: reads need a workspace binding or the read permission in
  one of the workspace's projects. Writes need a workspace binding.
- `installation`: a workspace binding.

The local profile has no principal, and the local operator reads and writes
every scope.

Operator sessions (`oas.*`) can read the magazine as a T0 observe operation.
They cannot publish until the operation catalogue gains a magazine
operation, which is a T2 change by the Owner. API tokens publish today.

## Audit

API-token calls are audited by the access middleware like every bearer
call. In addition, every successful write appends an `operator` stream
entry to the control plane audit:

- operation `magazine.article.upsert`, outcome `created` or `updated`, target `scope/slug@revision`;
- `magazine.article.pin` and `magazine.article.unpin`;
- `magazine.article.archive` and `magazine.article.restore`.

## Issues (PT-8)

Issues live separately at `<TaskRepository>/.metadata/weekly-issues/<workspace>/<issue>.json`.
The Watcher sweep composes one `weekly-YYYY-MM-DD` issue after Monday 07:00
in the workspace time zone. The Owner may compose a front-page-only daily
brief after 07:00. A second composition returns the existing file. The issue
stores sorted item snapshots, fifteen operations watch rows from the latest
warden fact snapshot, eight trend weeks, corrections, colophon entries and
`{ scope, slug, revision }` article references. Each article reference keeps
its persona and role byline, title and summary. Article revisions remain in
the MAG-1 store and can be read at `GET /api/magazine/{scope}/{slug}?revision=n`.
Trend pool-use percentages use the current workspace pool as their denominator;
the issue records that snapshot and does not change after publication.

The item count in the masthead equals the sum of visible role section counts.
The highest open item leads. Side stories follow importance order with at
most two per persona, while every open Critical item reaches the front page.
The Operations watch follows Decisions needed and includes passing, failing
and unknown rows. A missing or stale fact is unknown at importance 70 or more.

One editor CLI call may write the lead paragraph and item teasers when the
presenter allowance and pool have room. Every editor sentence must cite a
known item id; rejected or unavailable output leaves titles only. The issue
routes are `GET /api/workspaces/{id}/personas/issues`,
`GET /api/workspaces/{id}/personas/issues/{issueId}` and
`GET /api/workspaces/{id}/personas/issues/{issueId}/export`. The Owner-only
`POST /api/workspaces/{id}/personas/issues/compose?daily=true` creates a due
brief. The Studio viewer is `#/workspace/team/magazine`, and the Team page
links the newest three. Export is standalone article HTML with light, dark
and print styles for browser PDF output. Sending is disabled; the summary
renderer contains only the masthead, coverage, front-page titles, importance
numbers and an HTTPS sign-in link.

## Presenter decks (PT-9)

After a weekly issue is published, the Watcher sweep writes an immutable
`<TaskRepository>/.metadata/presenter-decks/<workspace>/<deck-id>/deck.json`.
Each issue gets a seven-slide weekly deck. Every fourth consecutive issue also
produces a monthly review covering those four issues, with ten to twelve
slides. Layout is deterministic: items at importance 40 or above, Critical
first, no more than three blocks per slide, a decisions slide even when empty,
and a role slide only when that role has an item at 70 or above. A trend slide
requires four published weekly issues; the issue's padded trend rows alone do
not count as observed history.

Each slide block records its issue id and either an item id plus the sourced
importance field, a watch row stats field, or an exact issue
trend field and value. Validation rejects an unknown item, a changed claim, a
number without a source field, or a value that differs from the immutable
issue. One optional presenter model call supplies slide titles and cited
speaker notes. Invalid or unavailable output leaves the deterministic titles.
The role remains a catalogue entry; the deck records the selected active
presenter persona id separately from the presenter role id. When several
presenter personas are active, the lowest stable persona id supplies the call.
The deck records editor tokens; admission includes prior issue and deck editor
tokens in the current budget week. Historical backfills use deterministic
titles without a model call.

The Studio magazine view opens the deck at 16:9 in both themes. Read routes are
`GET /api/workspaces/{id}/personas/decks` and `GET .../decks/{deckId}`.
`GET .../decks/{deckId}/export?format=html|pdf&audience=owner|stakeholder`
returns standalone slide HTML with arrow-key navigation and print styling, or
a 16:9 PDF with one page per slide. The stakeholder export uses generic item
labels and omits notes, bodies, host names and monetary costs. PPTX is outside
this slice.
