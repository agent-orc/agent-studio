# Stable View URLs

Status: Current contract, introduced by AGT-2225.

A product URL is collaboration context. It must identify the view completely
enough that a person or agent can open it without a written navigation recipe.
The browser origin depends on the deployment; everything below is relative to
that origin.

## Canonical routes

| View | Canonical relative URL | Stable identity |
|---|---|---|
| Task or epic card | `/?task=AGT-1234` | Public task key |
| All Projects Board | `/#/board` | Global workspace scope |
| Activity Feed | `/#/feed` | Global workspace scope |
| Dossier overview | `/#/workbenches[?dossier=<encoded-view-state>]` | Global workspace scope plus optional view state |
| Project Hub overview | `/#/projects/PROJ-002` | Immutable project registry id |
| Project Hub rail | `/#/projects/PROJ-002/<rail-key>` | Project id plus documented rail key |
| Project Dossier overview | `/#/projects/PROJ-002/workbenches[?dossier=<encoded-view-state>]` | Project id plus optional view state |
| Project Dossier detail | `/#/projects/PROJ-002/workbenches/<dossier-id>` | Project id plus catalogued Dossier id |
| Project Wiki page | `/#/projects/PROJ-002/wiki?page=concepts%2Foverview.md` | Project id plus repository-relative page path |
| Project Wiki folder | `/#/projects/PROJ-002/wiki?folder=concepts` | Project id plus repository-relative folder path |
| Workspace settings home | `/#/workspace/settings` | Global route |
| Workspace settings section | `/#/workspace/settings/<section-key>` | Global route plus settings section key |

The Project Hub overview omits `/overview`; both the serializer and examples
use the shorter form. Current Project Hub rail keys are the `key` values in
`PROJECT_RAIL_ITEMS` in
`frontend/src/app/features/project-detail/components/project-shell/project-shell.config.ts`.
Common examples are `project-urls`, `deployment`, `git`, `wiki`, `pipeline`,
`workflow`, `prompts`, and `settings`.

Dossier overview state uses one route-local `dossier` query value so it cannot
collide with sibling hash segments. Its decoded payload is a query string with
optional `q`, `sort`, and `dir` fields. Supported sort values are `status`,
`updatedAt`, `project`, `key`, and `openDecisions`; direction is `asc` or
`desc`. The default decision-first order omits `sort` and `dir`. A URL carrying
`dossier` wins over browser session state. Without it, each global or project
scope restores its own session state and writes that state back into the URL.

Opening a project also opens the existing Orchestrator Chat push-side-sheet by
default, after the project route resolves. This does not create a `/chat` route:
the URL continues to name the visible Board or Project Hub surface. A saved
per-user opt-out, sheet visibility, pin, width, transcript scroll, and composer
draft are transient browser state and do not belong in a shared URL.

## How an agent forms a link

1. Start with the deployment origin, for example `https://studio.example.net`.
2. For a card, append `/?task=<public-task-key>`. The task key is already shown
   on cards and returned by the Task API as `key`.
3. For a Project Hub view, obtain the immutable project `id` from
   `GET /api/workspaces`, then append `/#/projects/<id>` and, when needed, the
   rail key.
4. Percent-encode Wiki page and folder paths as query parameter values.

Examples:

```text
https://studio.example.net/?task=AGT-1234
https://studio.example.net/#/projects/PROJ-002
https://studio.example.net/#/projects/PROJ-002/pipeline
https://studio.example.net/#/projects/PROJ-002/wiki?page=concepts%2Frouting.md
https://studio.example.net/#/workbenches?dossier=q%3Drouting%26sort%3DupdatedAt%26dir%3Ddesc
```

Do not put a filesystem `watchPath`, storage slug, display name, tab order, or
local browser state into a shareable link. Display names and short codes can be
edited. `PROJ-NNN` and the public task key are the durable identities.

## Ownership and composition

The hash can contain one slash-prefixed view route plus independent
key-value segments joined by `&`. Board filters are one independent segment:

```text
/#/projects/PROJ-002/pipeline&filters=type%3Abug
```

One view route has one owner. Opening a task removes a competing project or
settings route before the URL is copied. Opening a Project Hub rail removes the
previous route but retains independent filter state. Rail-owned query state,
such as the Wiki's `?page=`, remains inside the route segment.

Browser Back and Forward are navigation, not merely address-bar changes. Route
owners must restore the matching editor tab and the exact sub-view on reload,
`popstate`, and `hashchange`.

The Activity Feed is a first-class embedded Studio surface. The Activity icon
opens `/#/feed` and marks that destination active. The older project-scoped
feed modal remains available from project and status-bar quick-access entry
points; it is not the canonical shareable Feed view and retains its compatibility
route only for existing links.

## Compatibility and evolution

The former name-derived Project Hub form, such as
`/#/projects/agent-studio/wiki`, is input-only compatibility. The app resolves
it against the current registry and replaces it with
`/#/projects/PROJ-002/wiki`. New links must use the project id.

When another view becomes linkable, its change is incomplete until it has:

1. A canonical serializer based only on immutable public identifiers.
2. A parser with an explicit compatibility policy for old links.
3. Navigation, reload, and Back/Forward synchronization.
4. A row in this document that gives an agent enough information to construct
   the URL.
5. Unit coverage for parse/serialize behavior and a rendered browser test for
   the full navigation cycle.

Never silently reuse a published route for a different view. If a route shape
must change, keep the old parser as an input-only redirect and make the current
serializer emit only the new canonical form.
