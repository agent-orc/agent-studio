---
lifecycleSchema: wiki-page-lifecycle/v1
pageKind: concept
lifecycleState: in-progress
editedBy: "Codex / AGT-2520"
editedAt: 2026-08-09T09:15:00Z
lifecycleHistory:
  - state: in-progress
    editedBy: "Codex / AGT-2137"
    editedAt: 2026-07-21T05:46:33Z
    note: "Initial classification: the read-only slice exists; chat and decision mutations remain open."
  - state: in-progress
    editedBy: "Codex / AGT-2520"
    editedAt: 2026-08-09T09:15:00Z
    note: "Documented inline decision markup, file-scoped freshness, and durable created-card receipts."
---

# Experiment workbenches

Status: concept and mockup complete, 2026-07-11. The first read-only production
slice landed on 2026-07-12: repository discovery, Explorer catalogue, isolated
viewer, Pulse thinking inbox, and the curated legacy pilot. The card-scoped
Decision panel and repository-backed decision mutation landed on 2026-07-26.
Inline decision points replaced the separate form on 2026-08-09. Chat
attachment remains a future slice.

Mockup:
[mockups/experimentier-workbench.html](mockups/experimentier-workbench.html).

## Decision in one paragraph

A **Dossier** is a repository-owned, current experiment around one product
question. It combines a self-contained HTML representation with a first-class
Explorer entry, the existing project orchestrator chat pinned to that artifact,
and an explicit closing decision: create a feature card through the normal task
mutation path, or archive the experiment with a reason. The full vision is a
large cross-domain feature. A read-only Explorer list plus safe viewer is a
medium MVP; chat and decision-to-task are separate later slices.

## 1. Why this is a product object

Development repeatedly needs a place to see one uncertain aspect, manipulate
examples, and converge before it becomes production work. Pipeline workbenches,
mockup families, and application surveys already use this pattern, but today
they are discoverable only if someone remembers a path.

A Dossier makes the pattern visible without turning every HTML document into
an application:

- the repository owns the experiment and its Git history;
- Agent Studio owns navigation, isolation, chat, and mutations;
- the canonical project orchestrator is the conversational actor;
- a human decision, not a model utterance, crosses from exploration into work.

The user-facing name is **Dossiers**. `Topics` is useful language inside a
Dossier, but is too broad for the Explorer section because Wiki pages,
decisions, tasks, and chats are all topics too.

## 2. Object and storage contract

Each Dossier is one physical folder that sits with its own documentation
theme below the project's docs root (a Dossier is any folder carrying a
`workbench.json`; discovery scans `docs/**/workbench.json` recursively, skipping
dot-directories and node_modules-like folders):

```text
docs/operations/                     # or docs/quality/, docs/system/, ...
  haertung-verteilte-ausfuehrung/
    index.html
    workbench.json
    brief.md                         # optional, prompt-friendly context
```

`index.html` is the presentation source. In v1 it is self-contained: inline
CSS, inline script when the Dossier sandbox is enabled, and no required
network or package dependencies. Its hypothesis and result remain readable when
scripts are disabled, so the ordinary Wiki reader is a useful static fallback.
A folder, rather than a loose HTML file, leaves room for future local assets
without adding a global registry. Optional `brief.md` gives the orchestrator a
bounded semantic source without making it scrape arbitrary HTML.

`workbench.json` is the small query and lifecycle contract. HTML frontmatter is
not used: YAML before the doctype makes the file invalid HTML, comments are a
fragile query format, and the existing Wiki companion schema has a different
purpose. Schema version 2 uses the same `pageKind`, `lifecycleState`,
`editedBy`, `editedAt`, and `lifecycleHistory` fields as lifecycle-aware
Markdown pages. The descriptor is the only Dossier lifecycle source and is
reviewed and versioned beside the experiment.

Example:

```json
{
  "schemaVersion": 2,
  "id": "project-state-at-a-glance",
  "title": "Project state at a glance",
  "summary": "Compare compact Explorer signals before choosing one.",
  "entrypoint": "index.html",
  "pageKind": "workbench",
  "lifecycleState": "in-progress",
  "phase": "testing",
  "editedBy": "Robert",
  "editedAt": "2026-07-11T10:30:00Z",
  "lifecycleHistory": [
    {
      "state": "in-progress",
      "editedBy": "Robert",
      "editedAt": "2026-07-11T10:30:00Z"
    }
  ],
  "sourceTaskKeys": ["AGT-2083"],
  "relatedTaskKeys": ["AGT-2050"],
  "projectUrlIds": ["dev-preview"],
  "decision": null
}
```

The minimum required fields are `schemaVersion`, stable `id`, `title`,
`summary`, relative `entrypoint`, and the shared lifecycle fields. The allowed
lifecycle values are:

| Field | Values | Meaning |
|---|---|---|
| `lifecycleState` | `in-progress`, `review-requested`, `decided`, `done` | The durable state shared with lifecycle-aware Wiki pages. |
| `phase` | `shaping`, `testing`, `decision-ready` | Optional progress within `active`. |
| `decision.outcome` | `feature-spawn`, `archive` | The explicit terminal decision. |
| `decision.state` | `pending`, `failed`, `succeeded` | Recoverable mutation state for a feature decision. |

A feature decision records `decidedAt`, the source revision, an operation id,
mutation state, the inline `responses`, and one or more `spawnedTaskKeys`. The
created keys are also appended to the descriptor's `relatedTaskKeys`, so every
Dossier projection carries the durable task relationship. An archive decision
records the same provenance plus a non-empty reason. A task-created/manifest-not-updated
failure remains `decision-pending` and can be reconciled by operation id.
Invalid descriptors remain visible as an Explorer error row with their path and
validation problem; they are never silently omitted.

Catalogue reads are containment-checked against the physical repository path.
Any symbolic-link or reparse-point component below that root is rejected, so a
descriptor, entrypoint, or Dossier folder cannot redirect discovery outside
the checkout. HTML is capped at 20 MiB before it is read; oversized entries are
reported as invalid rather than loaded into the API or browser.

There is no workbench registry file. The physical folders (each with a
`workbench.json`, discovered recursively under docs/) are the organization
model, consistent with the
[Wiki tree contract](../system/contracts/wiki-tree.md). The descriptor adds
properties to one physical object, not a virtual tree.

### Inline decision markup

Dossier authors put decision points where the supporting analysis reaches the
choice. The HTML remains self-contained and readable without Studio. Studio
adds controls and persistence as progressive enhancement; authors do not add an
API script or a second form.

Minimal example:

```html
<section data-decision-id="contrast-set" data-decision-kind="single">
  <h3>Which contrast set should Studio use?</h3>
  <ul>
    <li data-option-id="set-a">Set A: moderate contrast</li>
    <li data-option-id="set-b">Set B: stronger contrast</li>
  </ul>
  <label>
    Optional note
    <textarea data-comment="Optional note for this decision"></textarea>
  </label>
</section>
```

The convention is deliberately small:

- `data-decision-id` is unique in the document and uses 1-80 ASCII letters,
  digits, `_`, or `-`.
- `data-decision-kind="single"` produces radios, `multi` produces checkboxes,
  and `confirm` produces one or more explicit confirmation checkboxes.
- Every option is a readable child with a unique `data-option-id` using the
  same safe-id alphabet. `data-option-label` may provide a shorter card label.
- One optional element marked with `data-comment` becomes the bounded free-text
  field. A normal `textarea` is preferred because the static page remains clear.
- Existing checked inputs are respected as defaults. Studio owns the injected
  controls, live state, validation, and the sandboxed `postMessage` bridge.

On confirmation the receipt stores each decision id, kind, selected option ids,
and optional comment together with the receipt's timestamp and operator. The
feature-card preview derives its title, goal, and chosen-option summary from
those responses. Repository revision remains provenance; stale-decision checks
use the file-scoped SHA-256 fingerprint of `workbench.json` plus the entry HTML,
so unrelated commits do not invalidate an unchanged Dossier.

## 3. Lifecycle

| Stage | Durable state | Product behavior |
|---|---|---|
| Topic appears | No Dossier yet | A user, task result, proposal, or chat identifies a question worth seeing. |
| Shape | `active / shaping` | Create the folder, descriptor, and initial HTML representation. |
| Iterate | `active / testing` | Change the files through normal Git-aware editing; use the project orchestrator with the Dossier pinned as context. |
| Decide | `active / decision-ready` | The operator answers decision points inside the document, then opens the compact feature-card preview or enters an archive reason. Nothing is created yet. |
| Build pending | `decision-pending / feature-spawn` | Confirmation records an operation id and expected revision; failure remains visible and retryable. |
| Build settled | `decided / feature-spawn` | The card exists and the Dossier records its task receipt. |
| Stop | `archived / archive` | Explicit confirmation records why no feature follows. |

The closing invariant mirrors the intent of the
[planning-task spawn contract](planning-task-lifecycle.md) without pretending a
Dossier is a planning task:

> A Dossier leaves the active list only with a spawned task receipt or an
> explicit archive reason.

If a planning task owns the Dossier, its AGT-2069 gate remains authoritative.
The spawned card must be related back to that source task **and** recorded
through the existing
[`SpawnedTaskLedger`](../../backend/Features/Pipeline/SpawnedTaskLedger.cs) at
`.metadata/spawned-tasks.jsonl`, because that ledger feeds the planning spawn
summary. A `relatedTo` edge alone is not completion evidence. Merely setting a
Dossier to `decided` does not satisfy the planning gate.

Git commits are the iteration history for `index.html` and `workbench.json`.
The chat transcript remains in the existing orchestrator store. The descriptor
may record source and related task keys, but it does not copy chat history into
Git.

Backend-owned writes to tracked project files use
`ManagedRepositoryMutationService`. The service serializes by repository,
refuses to overwrite a target path that was already dirty, writes and commits
only the declared paths, and restores those paths to `HEAD` if the commit
fails. A successful commit queues its exact SHA and checked-out branch on the
existing background auto-push path unless project auto-push is disabled. This
boundary owns discovery key assignment, Dossier decisions, Dossier
lifecycle transitions, Wiki grading companions, automatic Wiki task-link
companions, and source-checkout prompt-review companions.

The remaining repository-write classes have explicit durability ownership:

- Interactive Wiki CRUD, curation, order, and classification endpoints commit
  their source and content-metadata companion paths before returning success.
- Pipeline Wiki maintenance, learnings, designated-topic sync, and Concept
  Dossier placement run inside `ManagedProjectArtifactCommitService`, which
  commits, stamps, and queues their task-attributed changes.
- Package publishing commits the bounded version file and performs its atomic
  commit-and-tag push as the publish transaction.
- Observational `agentReads` telemetry is not document metadata. It writes only
  below the gitignored `.orchestrator/wiki-agent-reads/` runtime tree. Adjacent
  tracked `.meta.json` files remain authoritative for grading, classification,
  and task links, so those content-metadata writes are committed rather than
  ignored.

## 4. Explorer integration

Under each expanded project, **Dossiers** is a first-class row immediately
after **Wiki**. It is a sibling surface backed by the `workbench.json`
descriptors distributed across the docs themes, not a special folder injected
into the ordinary Wiki tree.

```text
Agent Studio
  Board
  Project Hub
  Wiki
  Dossiers                         3
    Project state at a glance         testing
    Pipeline completion flow          decision-ready
    App surface measurement           shaping
  Epics
```

The default expansion lists current items: `active` plus
`decision-pending`, sorted by `updatedAt` newest first. Pending failures
remain visible until repaired. The count equals the visible current children.
A quiet history action opens decided and archived items; settled experiments do
not keep an acute signal. Status uses text, a dot, or a background tint, never a
colored left accent bar.

The catalogue loads lazily when a project's Dossiers row expands, or through
one bounded batch for already-expanded projects. Explorer startup must not issue
one Dossier request for every registered project.

Selecting an item opens a Dossier tab and preserves project, path, viewed
branch, and revision in the header. This uses the shared Branch Context Control
from the [Distributed Agent Studio target architecture](distributed-agent-studio-target-architecture.md). The list
and viewer must never imply that content from one branch represents another.
When the descriptor or entrypoint has uncommitted working-tree changes, the
viewer says so explicitly and withholds the HEAD revision instead of attaching
that SHA to bytes HEAD does not contain.

Because the files remain below `docs/`, they also stay visible in the physical
Wiki tree and Git/Pulse history. The ordinary Wiki view renders the entrypoint
as an interactive but isolated artifact. The Dossiers row is a narrow
generated projection with lifecycle actions, not a second content tree.

Pulse may report recently changed, invalid, or decision-ready Dossiers, but
Pulse does not own their list or lifecycle. Its role remains a generated entry
view, as defined in [Wiki Pulse](wiki-pulse-dashboard.md).

A Dossier's discovery key, id, title, summary, status, and phase are
searchable from the title-bar global search palette (Ctrl+K): typing
`PROJECT-W<n>` or a title or summary word opens the Dossier viewer directly,
the same navigation the Explorer row uses. See
[global search](../system/domains/frontend.md#global-search) for the domain
contract.

## 5. Viewer, interactive HTML, and project previews

The Dossier view is host chrome around isolated content:

```text
+ Explorer +---- Dossier host ----------------------+-- Orchestrator chat --+
| active list | header, revision, decision action    | project session       |
|             | sandboxed index.html or bound panel  | pinned artifact       |
+-------------+---------------------------------------+-----------------------+
```

Ordinary Wiki, Git-pane preview, and Files-tab HTML all use the same baseline
viewer policy: `srcdoc` with `sandbox="allow-scripts"`. Before assignment, the
host parses the source in an inert document and moves it into a fixed wrapper
whose CSP and `about:blank` base precede every artifact node; artifact CSP,
refresh, and base elements cannot move ahead of that boundary. Scripts can power
self-contained interactions, while the deliberate omission of
`allow-same-origin` gives the document an opaque origin and prevents it from
inheriting Studio's origin or directly reading Studio cookies, storage, and DOM.
The Dossier CSP denies network requests. The ordinary viewer does not promise
same-origin integration or network-backed application behavior.

The Dossier remains the distinct viewer for artifacts that need more than
that baseline, including controlled network-backed previews or a future
same-origin capability. Its host must not grant same-origin implicitly. It also
must not receive top navigation, forms, downloads, modals, popups, clipboard
access, or direct Agent
Studio credentials. A restrictive Content Security Policy blocks network,
frames, forms, and external assets by default while permitting only the inline
CSS/script and data images required by the self-contained artifact.

The HTML payload cannot call task, chat, filesystem, or project APIs. A narrow,
versioned `postMessage` bridge may expose presentation-only events such as
`selection-changed` or `request-resize`. The host validates origin, source
window, a per-frame capability token, schema version, event name, and payload
size. An opaque sandbox reports a `null` origin, so origin checking alone is
never treated as authentication. Mutating actions always stay in host chrome.
The frame never persists state itself. Studio keeps live responses in trusted
host state and writes the settled receipt through `WorkbenchDecisionService`.

Configured Project URLs can be attached by stable `projectUrlIds`. The host
resolves the URL, represented branch, and current availability from Project
Settings. A preview uses its own restricted iframe and shows branch provenance.
Raw external URLs embedded in `workbench.json` are not accepted. WB-2 may ship
HTML viewing before live URL embedding if nested-frame and Content Security
Policy behavior needs a separate proof.

The AGT-2067 Project URL preview is a precedent for tab chrome, availability,
and external-browser fallback, not a sandbox template: configured development
sites and repository-authored HTML have different trust profiles. In the current
documentation, AGT-1915 names the planning task that closed without a follow-up,
so it informs the Dossier closure invariant through AGT-2069 rather than the
iframe policy.

## 6. Chat is the orchestrator

Opening a Dossier does not create a new chatbot, model owner, or peer project
session. The right column is the existing resizable project Orchestrator Chat
described in [Persistent Orchestrator Chat](./orchestrator-chat.md) and
[Orchestrator in-app](orchestrator-in-app.md).

The canonical context stays `project:<PROJ-ID>`. The Dossier is a pinned
context attachment containing:

- workbench id, title, path, branch, revision, status, and phase;
- a bounded text description derived from the descriptor;
- the current presentation selection sent through the safe bridge;
- batched status for referenced task keys;
- explicit freshness and validation failures.

This preserves one project orchestrator and one durable project conversation.
Dossier open/close and decision events can appear as compact transcript
anchors. Every anchored turn records the Dossier id and entrypoint fingerprint
or Git revision it actually saw. The smaller first version deliberately shares
the project transcript across Dossiers. A future
`workbench:<PROJ-ID>/<WB-ID>` transcript key is a larger registry, routing,
storage, and digest contract, not a cosmetic UI change; it still must not create
a second canonical project brain.

Chat can explain, compare options, change presentation-only selections through
typed host tools, and prepare a task draft. Source edits still use an explicit
Git-aware editing flow. A sentence such as "yes, build it" may open the spawn
preview, but it never bypasses confirmation.

## 7. Decision to feature card

**Build as feature** opens a preview before any mutation. The preview includes:

- target project, initial lane, coding mode, agent/model defaults, and task type;
- editable title, goal, acceptance criteria, and evidence links;
- Dossier path, exact source revision, chosen option, and related task keys;
- the resulting relationship to a source planning task when one exists.

The preview is a user-driven task draft editor in trusted Dossier host chrome.
The operator can revise generated or chat-prepared defaults before submission,
including the title, goal, acceptance criteria, evidence, target project, and
lane. Chat may prepare values and request that the editor open, but it cannot
persist, confirm, or submit the draft. The sandboxed Dossier HTML cannot read
or write the editor fields. This keeps the final task authoring decision with
the operator even though the editor is visually inside the Dossier view.

Confirmation creates the card through `TaskMutationService.CreateJob`, the same
bounded entry point used by the Project Hub proposal flow and the AGT-2028 task
spawner. It does not invoke the post-task-spawner pipeline step, because this is
an explicit operator action rather than post-run relevance automation.

The trusted Dossier host now owns this action through
`WorkbenchDecisionPanelComponent` and `WorkbenchDecisionStore`. The panel first
*prepares*: the backend validates the draft against the exact Dossier
revision/fingerprint and writes nothing. A separate visible confirmation is the
single durable write, and it lands in the Dossier's own `workbench.json`
(schema v2: `lifecycleState` + a `lifecycleHistory` entry carrying the decision
text; schema v1: `status` + `updatedAt`) together with a `decision` receipt.
Visibility hangs on that descriptor, never on a `.meta.json` sidecar - which is
why generic Wiki classification cannot archive a canonical Dossier.

The delivered slice deliberately stops short of creating the card: the decision
service records the direction and hands the validated draft back, and task
creation stays on the existing task API owned by the client. A settled feature
receipt therefore carries the spawned task keys only once the client reports
them.

WB-4 should implement a narrow **decision draft -> validated task mutation ->
receipt** service over the existing mutation boundary instead of copying the
Project Proposal endpoint. Generalizing Project Proposals onto that service can
follow, but is not a hidden prerequisite for WB-4. The Dossier flow keeps the
useful existing semantics:

- a prepared `decision-pending` record stores one operation id and expected
  Dossier revision before task creation;
- a deterministic requested task id and operation lookup make retries
  idempotent;
- the card carries a non-blocking `relatedTo` reference;
- the decision records the spawned task key only after creation succeeds;
- if the task exists but the final Git-aware descriptor update fails, retry
  finds that task by operation id and repairs the descriptor instead of spawning
  again;
- structured timing and outcome events make the user-visible mutation auditable.

After success, every spawned key renders with the existing AGT-2050
[task reference microcard](../../frontend/src/app/components/task-reference-microcard/).
Keys from `workbench.json`, `brief.md`, and chat are batch-hydrated through
the same reference-status contract and rendered in trusted host chrome. Bare
keys inside the opaque Dossier iframe remain plain text or ordinary links:
the Angular hydrator cannot and must not mount components across that sandbox
boundary. The HTML itself receives no task data or credentials.

## 8. Boundary with adjacent systems

| Object | Primary question | Source and lifecycle | Conversion to work |
|---|---|---|---|
| Wiki page | What do we know? | Repository document; knowledge and history. | Links to tasks, but has no required closing decision. |
| Dossier | What should we see, test, and decide? | Repository HTML plus descriptor; active -> pending -> decided, or archived. | Explicit preview and confirmed feature spawn. |
| Project proposal | Should this generated finding be approved? | Dated Markdown finding with severity, evidence, and proposed/approved/rejected/spawned status. | Approve creates one coding card. |
| Planning task | What work should follow this analysis? | Application-owned task with a planning mode and completion gate. | Must spawn a follow-up or declare none. |
| Design mockup | What could this surface look like? | Usually a standalone concept artifact with no runtime lifecycle. | May be promoted into a Dossier when ongoing iteration is useful. |

The Project Hub proposal flow from AGT-2074 is a specialization of the same
human-controlled conversion pattern. It remains optimized for generated
finding -> approve/reject -> one card. A Dossier is broader and longer lived:
it can start from a proposal, host several iterations, and close only after the
operator has enough visual evidence.

Existing mockups under `docs/quality/design/` and `docs/concepts/mockups/` are not
automatically moved. A promoted Dossier should preserve the original path as a
source link or copy its self-contained HTML once with provenance. There must not
be two files both claiming to be the live Dossier.

## 9. Invariants and non-goals

- Repository files are content; application APIs own task and chat mutations.
- Dossiers never execute with the Wiki reader's authority or origin.
- Opening a Dossier is read-only and does not spend model quota.
- Chat turns spend quota only on explicit send or existing auto-mode rules.
- Build and archive decisions require visible human confirmation.
- Current counts equal visible active and decision-pending rows.
- Both themes, keyboard navigation, narrow layouts, and reduced motion are part
  of each UI slice.
- No colored left accent bars are used.
- Archived Dossiers stay available as quiet history and are never deleted as
  a side effect of archiving.
- The first version is not a plugin runtime, workflow engine, collaborative
  canvas, arbitrary website host, or automatic prototype-to-production compiler.

## 10. Implementation slices and honest size

The complete feature is **large**: it crosses repository discovery, Explorer,
secure HTML rendering, orchestrator context, task mutation, and durable
decisions. It should be an Epic or coordinated card family, not one coding card.
The first useful read-only cut is medium.

| Slice | Honest size | Scope | Acceptance boundary |
|---|---|---|---|
| **WB-1: Folder contract and current Explorer list** | M | Validate `docs/<theme>/<id>/index.html + workbench.json`, expose a bounded lazy list, and add the collapsible project row. | Current count reconciles to visible children; invalid entries are explicit; decided/archive history is reachable; no viewer or mutation. |
| **WB-2: Dossier viewer and isolation** | L | Open a tab with branch/revision provenance, a script-capable opaque-origin sandbox, strict CSP, static Wiki fallback, and the tiny presentation event bridge. | Static and interactive fixtures work in both themes and at narrow width; malicious bridge/network/navigation fixtures fail; no chat or task creation. |
| **WB-3: Canonical orchestrator attachment** | M/L | Reuse the project chat side sheet and attach the bounded Dossier digest plus current selection. Add compact open/close anchors. | The context inspector shows path and revision; no cross-project leakage; no new canonical session key; source editing remains out of scope. |
| **WB-4: Host-owned task editor, decision spawn, and receipt** | L | Add the user-driven task draft editor and Build/Archive previews in trusted host chrome, including field validation, explicit confirmation, shared validated task creation, idempotent operation handling, manifest transition, planning-ledger recording, and AGT-2050 receipts. | Generated and chat-prepared values remain editable; neither chat nor iframe can confirm; retry cannot duplicate a card; failed partial completion is visible and repairable; a source planning task receives both `relatedTo` and a `SpawnedTaskLedger` record. |
| **WB-5: Curated migration pilot** | M | Promote a small named set such as pipeline workbench, project-state exploration, and app survey; document provenance and leave other mockups untouched. | Each promoted item has one live source, valid metadata, and an explicit owner; incompatible storage/network assumptions are reported; no bulk heuristic migration. |

### First production slice, 2026-07-12

The implemented MVP combines the useful read-only boundaries of WB-1 and WB-2
with Robert's required Pulse entry point and the first WB-5 discovery pilot:

- `GET /api/projects/{projectName}/workbenches` validates canonical folders,
  keeps invalid descriptors visible, sorts current items newest first, and can
  include settled history through `?history=true`;
- the Explorer loads a project's catalogue only when its Dossiers row is
  expanded, and the count equals the visible rows;
- the viewer carries project, path, branch, and revision provenance and renders
  `srcdoc` with `sandbox="allow-scripts"`, an opaque origin, and a restrictive
  CSP that denies network, frames, forms, objects, workers, and base URLs;
- Pulse receives the same current catalogue as an "Open Dossier topics"
  thinking inbox and opens the same viewer;
- a named migration allowlist projects the existing pipeline companion report,
  Dossier mockup family, and application survey from their single live paths.
  The exact `docs/concepts/mockups/decoupled-lifecycles.html` path joins the list
  automatically when that artifact lands. There is no general HTML heuristic.

This slice deliberately exposes no chat pinning, source editing, archive/build
action, or decision-to-task mutation. The typed Dossier tab/document boundary
is the host-side seam for those later features; the iframe receives none of it.

WB-2, WB-3, and WB-4 are the risk-bearing slices. If the team requires strictly
small cards, split WB-3 into context builder and UI attachment, and WB-4 into
preview and mutation/receipt. That makes seven cards but does not change the
dependency order: WB-1 -> WB-2 -> WB-3, with WB-4 after WB-1 and WB-5 last.
Chat-driven source changes are a separate future ORCH-Hands slice and are not
hidden inside WB-3.

WB-4 remains a distinct large risk slice even though its editor shares the
Dossier composition with WB-2 and WB-3. It crosses operator-controlled form
state, validation, task mutation, Git-aware decision state, idempotent recovery,
and planning-ledger evidence. It must not be absorbed into the viewer or chat
cards.

## 11. Feature handoff status

The concept card remains complete. The 2026-07-12 implementation records the
first read-only product slice described above without retroactively expanding
the concept card's original scope. WB-3 and WB-4 remain separate future work;
WB-4 is still handed off as its own large card, not as residual viewer work.
Further WB-2 hardening and additional curated migrations should continue to be
accepted against the boundaries in the slice table.

## 12. Validation plan for implementation

- Contract tests for path containment, descriptor validation, ordering, visible
  counts, branch provenance, and invalid entries.
- Browser tests for Explorer disclosure, viewer resize, theme parity, keyboard
  flow, reduced motion, and settled history.
- Adversarial sandbox tests for network, top navigation, storage/origin access,
  oversized messages, unknown message types, and nested project previews.
- Orchestrator tests proving the attachment is bounded, revision-specific, and
  project-isolated.
- Mutation tests proving confirmation, idempotent retry, partial-failure
  reconciliation, source-task relationship plus planning-ledger recording, and
  host-owned microcard hydration.

## 13. Second-opinion pass

An independent product and architecture review was applied on 2026-07-11. It
challenged the first draft on trust, recoverability, chat ownership, and size.
The resulting changes are part of this concept:

- interactive HTML has a separate opaque-origin viewer instead of weakening the
  Wiki sandbox;
- feature decisions have a visible pending/failed state and deterministic retry
  path instead of assuming two stores update atomically;
- the MVP uses the canonical project transcript and states that per-Dossier
  transcript continuity would be a larger context-key extension;
- WB-2 is rated large and security-sensitive, and the complete family is an
  Epic rather than a single feature card;
- WB-4 is rated large, records planning-owned spawns in the existing ledger, and
  keeps reference microcards in trusted host chrome;
- the mockup exposes both feature-spawn and archive-with-reason previews;
- migration is curated and incompatibilities are reported instead of treating
  every historical mockup as current.

The interactive mockup demonstrates the intended composition and decision
preview only. Its simulated task key and chat responses are not product
behavior.
