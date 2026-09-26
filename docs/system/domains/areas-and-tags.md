# Areas and Tags

System-of-record map for the classification vocabulary decided in Dossier
AGT-W55 (`docs/operations/dossier-consolidation-and-area-tags/index.html`, D3)
and delivered by AGT-2803.

An **area** is a bounded part of the application with its own terms. An area
owns a glossary: the ubiquitous language an agent must use for that area. A
**tag** is a handle on the vocabulary. Tags come in two kinds:

- **area tags** name the area a card, Dossier, or article belongs to,
- **facet tags** name a cross-cutting aspect (a quality domain such as
  `testing`, or a document kind such as `decision`).

AGT-2804 adds the creation-time `auto-tag` classifier and the dry-run/apply
backfill. The [proposed reference set](../../quality/tagging-golden-set/index.html)
contains 60 cards, 20 Dossiers, and glossary proposals awaiting operator
approval. Tag maintenance reviews the resulting vocabulary and usage, but
changes it only after an operator approves the exact proposal.

## Ownership

| Concern | Owner |
|---|---|
| Product-default areas, facet lists, id grammar, stability rules | `backend/Features/Areas/AreaTaxonomy.cs` (pure) |
| Glossary page format (render and parse) | `backend/Features/Areas/AreaGlossaryDocument.cs` (pure) |
| Area / tag filter semantics | `backend/Features/Areas/TagFilter.cs` (pure) |
| Per-project areas registry and the closed tag list | `backend/Features/Areas/AreaRegistryService.cs` |
| Glossary page read and write | `backend/Features/Areas/AreaGlossaryService.cs` |
| Workspace tag registry (`tags.json`) | `backend/Features/Tags/TagRegistryService.cs` |
| Dossier `tags[]` write boundary | `backend/Features/Docs/WorkbenchTagService.cs` |
| Periodic usage review and durable per-project reports | `backend/Features/Tags/Maintenance/TagMaintenanceService.cs` |
| Proposal validation and exact before/after plans | `backend/Features/Tags/Maintenance/TagMaintenancePolicy.cs` (pure) |
| Card, Dossier, wiki, registry, and glossary reads and writes | `backend/Features/Tags/Maintenance/TagMaintenanceWorkspace.cs` |
| Creation classification, confidence policy, proposals, and backfill | `backend/Features/Tags/AutoTaggingService.cs` |

## The ten product areas

Every project inherits them; the ids are stable and can never be removed or
renamed.

`execution-and-runner`, `delivery-chain`, `gates-and-review`, `observation`,
`task-and-board-ui`, `dossiers-and-documentation`, `websites`, `security`,
`retention`, `token-economy`.

## Facets

Quality facets reuse the Quality Studio domain ids named in the
[project-definition v2 plan](../../operations/docker-ausfuehrungswelt-migration/project-definition-v2-plan.md):
`architecture`, `correctness`, `testing`, `privacy`, `payments`, `reliability`,
`performance`, `accessibility`, `localization`, `operations`,
`review-prioritization`, `seo`. The tag system introduces no second list of
quality domains.

Document facets from the Dossier: `decision`, `incident`, `evidence`,
`guideline`, `migration`.

**One id, one entry.** The registry is a flat namespace, so an id that is both
an area and a quality domain resolves to the area. Today that is `security`:
it is a product area, and the quality domain of the same name maps onto it
rather than creating a second row. `architecture` and `performance` remain
facets, as the v2 plan records.

## Project additions

### Auto-tag opt-out

Every project starts with `AutoTag = true` in the workspace project settings.
`PUT /api/projects/{project}/auto-tag` accepts `{"enabled":false}` to opt out
of creation classification. The current v1 `.agent-studio/project.yml` reader
is closed, so the opt-out is not a key there. The project-definition v2 plan
records `tagging.autoTag` as moved to this workspace setting. An explicit
backfill request remains available when automatic creation tagging is off.

A project may add areas and re-label an inherited one. The active
`.agent-studio/project.yml` (v1) is closed for new fields and the v2 plan lists
`project.areas` as a later extension request, so project additions are stored
as the workspace project setting `Areas` in `project-settings.json` until a v2
reader exists. Where a v2 definition declares `project.components[]`, an area
may reference component ids; areas remain a classification vocabulary, not the
component inventory.

## Glossaries

An area's glossary is a normal wiki article at
`docs/areas/<area-id>/glossary.md`, so it is searchable, reviewable, and
editable by hand. The page carries front matter with the area id and the area
tag, and one `##` section per term; a `Synonyms:` line lists accepted
alternatives. Writes go through the managed repository mutation, so the file
write and its path-scoped commit share one gate. `docs/areas` is registered as
a producer write-target in `backend/Features/Docs/WikiProducerTargets.cs`, so
the write path lives in one place and the wiki-path guard covers it.

## Where tags live

| Subject | Storage |
|---|---|
| Card | `task.json` → `tags[]` |
| Dossier | `workbench.json` → `tags[]`, projected onto the catalogue item |
| Wiki article | YAML front matter `tags: [a, b]` or a block list; HTML pages use `<meta name="agent-studio-tags" content="a, b">` in `<head>` |
| Dossier entry page in the wiki tree | the Dossier descriptor's `tags[]` |

Auto-tagging also persists an optional `taggingStatus` alongside these tags.
The only recognized values are `tagged` (the classifier applied registry tags)
and `tags-proposed` (low-confidence suggestions are held in the project's
auto-tag state for review). Cards store it at `task.json.taggingStatus`;
Dossiers store it at `workbench.json.taggingStatus`. Wiki Markdown stores
`taggingStatus: tagged` or `taggingStatus: tags-proposed` in YAML front matter.
Wiki HTML stores `<meta name="agent-studio-tagging-status" content="tagged">`
or the same meta element with `content="tags-proposed"` inside `<head>`.
Readers treat missing and unrecognized values as no status marker; they do
not infer successful tagging from an unknown value. Archived items are not
classified.

## API

| Route | Purpose |
|---|---|
| `GET /api/tags` | Workspace registry; every row carries `kind` |
| `POST /api/tags` | Create a facet tag. `kind: area` and area ids are refused |
| `DELETE /api/tags/{id}` | Refused with 409 for a product area id |
| `GET /api/projects/{project}/tags` | The project's effective closed list |
| `GET /api/projects/{project}/areas` | Product defaults merged with the project additions |
| `PUT /api/projects/{project}/areas` | Replace-all write of the project additions |
| `GET /api/projects/{project}/areas/{areaId}/glossary` | Terms, definitions, synonyms |
| `PUT /api/projects/{project}/areas/{areaId}/glossary` | Replace-all write of the glossary page |
| `PUT /api/projects/{project}/workbenches/{id}/tags` | Replace-all write of the Dossier tags |
| `PUT /api/tasks/{jobId}/tags` | Replace-all write of the card tags |
| `GET /api/tasks?area=&tag=` | Filter the task list |
| `GET /api/projects/{project}/workbenches?area=&tag=` | Filter the Dossier list |
| `GET /api/projects/{project}/wiki/tree?area=&tag=` | Prune the wiki tree |
| `GET /api/projects/{project}/tag-maintenance` | Read the project's run, decision, and audit report |
| `POST /api/projects/{project}/tag-maintenance/run` | Start an immediate review in addition to the periodic schedule |
| `POST /api/projects/{project}/tag-maintenance/decisions/{id}` | Apply or keep one proposal with an explicit operator identity |

Filter semantics: ids inside one parameter are alternatives, the two parameters
are a conjunction. A folder survives the wiki-tree filter only while it still
has a matching descendant.

## Validation

- **Unknown tag ids are refused on write.** Every write boundary (card tags,
  Dossier tags) validates against the project's effective list.
- **Reads stay lenient.** A tag retired after it was written renders as a ghost
  chip; it never invalidates the card, Dossier, or page that carries it. A
  descriptor is only rejected when the `tags` field has the wrong shape or an
  id outside the stable grammar.
- **Area ids are stable.** A product id can never be removed or renamed, and a
  project area that is still carried as a tag by a card or a Dossier cannot
  disappear from the registry.
- **Platform stamps** (for example the orchestrator provenance tag) keep using
  the merge-add writer, which is not a public boundary.

## Periodic maintenance

The hosted maintenance step checks every project every 15 minutes and starts a
review when its configured interval is due (seven days by default). Only a
successful report advances that cadence. A failed attempt becomes eligible for
a bounded retry after 60 minutes by default, while cancellation propagates and
leaves no run record. It uses a
Sonnet-class synthesis route at high thinking over the closed registry,
glossaries, global usage counts, and a rotating bounded sample of active cards,
Dossiers, and wiki articles. Each run leaves a durable per-project report under
the task repository. The step proposes at most 20 changes across four kinds:

- retire a facet only when it has no references in any project or history,
- merge near-duplicate facets only when every affected item belongs to the
  approving project,
- add a facet only with at least three distinct active source items, and
- add glossary terms only when an active decision-bearing Dossier or ADR is
  cited.

Every proposal becomes a `decision` card containing Keep and Apply options,
their consequences, evidence, and the exact before/after write plan. These are
prose decision cards until the structured decision-card surface from AGT-W54 is
available. Creating, moving, or running the card never applies the proposal.

Application requires an explicit `apply` choice and `X-Client-Id`. Before the
first write, the service verifies every preimage and checks for new references.
Writes use the existing owning services, verify their result, retain a
write-ahead approval and per-write audit, and can resume idempotently after a
partial failure. The alternative `keep` choice records rejection without any
registry, glossary, or subject write. Area ids and platform provenance tags are
never eligible for retirement or merge.

When a valid golden-set file is present, the run records micro precision and
recall per classification tier. The repository's Agent Studio set contains 60
cards and 20 Dossiers and is marked `proposed`, with no approver. Its metrics
are explicitly labelled as agreement with a proposed reference, pending the
operator decision. An approved set requires `approvedBy` and `approvedAt`.
Tier 1 uses the Sonnet-class route at low thinking. Precision below 0.9
automatically evaluates and selects tier 2 at high thinking. Missing,
undersized, malformed, or out-of-registry data produces an explicit report
status instead of a metric claim.

### Filesystem and report contract

The durable maintenance report for project `<project>` is
`<TaskRepository>/tag-maintenance/<sha256(project)>.json`, where the digest is
the lowercase hexadecimal SHA-256 of the exact project name. It is a JSON
object with these arrays:

| Field | Schema |
|---|---|
| `runs[]` | `id`, `startedAt`, `status` (`running`, `reported`, or `failed`), `model`, `thinkingLevel`, `itemsReviewed`, `eligibleItems`, `textOffset`, `excerptLimit`, `globalUsage` (tag-id to count), nullable `error`, `decisions[]` (decision ids), and `goldenSet` |
| `decisions[]` | `id`, `cardId`, `proposal`, exact `changes[]` (`kind`, `project`, `id`, serialized `before`, serialized `after`), `status` (`pending`, `applying`, `partial`, `applied`, or `rejected`), and nullable `error` |
| `audit[]` | `at`, `decisionId`, `actor`, `outcome`, and `detail`; approval is written before mutations, every completed write is recorded, and partial/application outcomes are appended |

Within a decision, `proposal` contains `kind`, `source`, `target`, `label`,
`reason`, `evidence[]`, `area`, and `terms[]`; each term contains `term`,
`definition`, and `synonyms[]`. The serialized `before` and `after` values in a
change are JSON strings because they are also the optimistic-concurrency
preimages verified immediately before application.

Each run's `goldenSet` object contains `status`, `metrics`, `path`, `message`,
`cardCount`, `dossierCount`, `selectedTier`, and `tiers[]`. Each tier row contains
`tier`, `model`, `thinkingLevel`, `items`, `precision`, `recall`, and
`meanConfidence`. A valid proposed file reports `evaluated-proposed` and
`available (proposed reference)`; no file leaves `tiers` empty and emits no
precision or recall numbers.

The evaluation harness reads the proposed Agent Studio set at
`docs/quality/tagging-golden-set/items.json` when present, and otherwise reads
`<TaskRepository>/tag-golden-sets/<sha256(project)>.json` by default. Its schema
has `status`, nullable `approvedBy` and `approvedAt`, and `items[]`.
Each item has `kind` (`card` or `dossier`), `id`, `title`, `text`, and a non-empty
`tags[]` drawn from the effective closed registry. `(kind, id)` pairs are
unique. A usable file contains at least 60 cards and 20 Dossiers. Invalid,
undersized, or out-of-registry input is reported as invalid. Proposed input is
measured but is never represented as operator-approved ground truth.

### Configuration

| Key | Default | Meaning |
|---|---|---|
| `TagMaintenance:Enabled` | `true` | Enables the hosted periodic sweep. `false` skips all projects; the explicit run API remains available. |
| `TagMaintenance:Projects:<project>:Enabled` | `true` | Enables the hosted sweep for one exact project name. `false` skips that project; the explicit run API remains available. |
| `TagMaintenance:IntervalHours` | `168` | Cadence after the most recent successful (`reported`) run. Values are clamped to 1 through 8760 hours. |
| `TagMaintenance:RetryDelayMinutes` | `60` | Delay after the most recent failed attempt since the last success. Values are clamped to 1 through 1440 minutes. Cancellation creates no run and does not alter due time. |
| `TagMaintenance:GoldenSetPath` | Agent Studio: shipped proposed set; other projects: `<TaskRepository>/tag-golden-sets/<sha256(project)>.json` | Optional golden-set path override. Every `{project}` token is replaced with the exact project name, then the result is resolved to an absolute path. |

`TaskRepository` is the required workspace root for reports and per-project
golden-set paths outside the shipped Agent Studio proposal. The hosted worker checks eligibility every 15 minutes; that
poll interval is fixed and is not a `TagMaintenance` configuration key.

## Tests

`backend.Tests/AreaTaxonomyTests.cs`, `AreaGlossaryDocumentTests.cs`,
`TagFilterTests.cs`, `AreaRegistryServiceTests.cs`,
`AreaGlossaryServiceTests.cs`, `WorkbenchTagServiceTests.cs`,
`WikiArticleTagsTests.cs`, `TagMaintenanceTests.cs`.
