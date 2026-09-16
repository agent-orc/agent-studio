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

Auto-tagging is a separate card (AGT-2804). Nothing in this domain assigns a
tag by itself; it defines the vocabulary, the storage, and the API.

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
| Wiki article | YAML front matter `tags: [a, b]` or a block list |
| Dossier entry page in the wiki tree | the Dossier descriptor's `tags[]` |

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

## Tests

`backend.Tests/AreaTaxonomyTests.cs`, `AreaGlossaryDocumentTests.cs`,
`TagFilterTests.cs`, `AreaRegistryServiceTests.cs`,
`AreaGlossaryServiceTests.cs`, `WorkbenchTagServiceTests.cs`,
`WikiArticleTagsTests.cs`.
