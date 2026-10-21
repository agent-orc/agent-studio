---
lifecycleSchema: wiki-page-lifecycle/v1
pageKind: concept
lifecycleState: in-progress
editedBy: "Codex / AGT-2137"
editedAt: 2026-07-21T05:46:33Z
lifecycleHistory:
  - state: in-progress
    editedBy: "Codex / AGT-2137"
    editedAt: 2026-07-21T05:46:33Z
    note: "Initial classification: the initial workflow exists; broader progress and compiler refinements remain open."
---

# Deployment as a first-class citizen

## Stage and production host deployments

Projects declare each environment in `docs/deployments/environments/<id>.json`,
using the [environment schema](../app/schemas/project-environment.schema.json).
The declaration names its kind, host, address, container or static template,
destination, health URL, promotion rule, and host-held secret references.
For a container, `destination` is the local port behind the existing reverse
proxy. For a static site, it is the served release directory; the proxy serves
its `current` symlink. Static artifacts are tar or tar.gz files in the deploy
host's artifact store, named by their SHA-256 digest.

After the full gate passes and the release is pushed, the promotion script can
submit the measured package digest or the gate-built image's registry digest
to `POST /api/deploy/gate-releases`. This route requires a service credential
with only `runner.release`. The script checks that a container image carries
the exact gated commit as its OCI revision label. Studio records the immutable
commit/digest pair before queueing stage. The operator release route accepts
only an artifact already recorded for that exact commit. For a static package,
`DEPLOY_ARTIFACT_STORE` must name a store shared with the deploy host; the
script copies the archive there under its digest. Set
`DEPLOY_RELEASE_TOKEN_FILE` and pass `--project` so successful gates queue
stage automatically. A deploy-role credential claims work through
`/api/deploy/jobs/claim`.
`agent-host deploy-loop` runs the container or static template on the declared
host, resolves secret names from its local secret store, checks health, and
reports a bounded result code. The Studio shows history and pending promotion.
Every production release needs a person in Studio (BD-1). The declaration value
`approval: stage-healthy-10m` is still accepted but no longer promotes
production on its own. An approval expires after 24 hours.
Failed production health queues the previously healthy artifact for rollback
and leaves a failure card with the bounded health evidence in Studio.
After the deploy, the control plane keeps probing each environment from outside
its host; see [environment health checks](../operations/environment-health-checks.md).

### Releases, standard lifecycle and deploy runs (BD-1)

Stage P1 of the [deployment Dossier](../operations/bereitstellung/index.html)
adds a release layer in `backend/Features/Deploy/`. The host deploy queue above
stays the only executor; releases and runs describe and steer it.

- **Release.** A green gate release (`POST /api/deploy/gate-releases`, or the
  operator route `POST /api/projects/{project}/environments/releases`) records an
  immutable release: version `yyyy.MM.dd.N`, commit, artifact digests, a process
  version (hash of the project.yml schema 2 artifacts, lifecycles, channels and
  deploy steps at the release commit plus the environment declarations), a
  variables version (hash of the project.yml schema 2 `variables` section at the
  release commit, where a secret counts only by name, and of the secret
  reference names per environment), the
  cards it adds, and exactly one channel. Schema 2 `channels` select a named
  lifecycle using `versionRules.prereleaseTag` and/or
  `versionRules.branchPattern`; exactly one
  channel has `default: true` as the fallback. The release request may supply
  `versionTag`, `branch`, and `channel`; Owner or Maintainer can override rule
  matching. Rule refusals name the rule. Variables may add `channels` alongside
  `environments`; both scopes must match and the most specific scope wins.
  A named lifecycle's approval list is checked against the caller's resolved
  Owner or Maintainer role.
  Each release is one write-once file under
  `<TaskRepository>/deployments/projects/<project>/releases/`, written as a
  flushed temporary file and moved into place without overwrite. The same
  project, commit and artifacts always resolve to the same release. Cards are
  those whose integration verification names a commit reachable from the release
  commit and not from the previous release (at most 200 commits for the first
  release).
- **Release order.** Releases never move back in history. A commit that is a
  strict ancestor of the newest release's commit is refused with 409 and
  `"reason":"superseded"`, so a gate
  run that finishes after the run of a later commit cannot create an older
  release after a newer one. Re-posting an existing release starts Staging again
  only while it is the newest release; an older one answers the same
  superseded 409 and moves back
  only through rollback. A rebuild of the newest commit and a commit unrelated in
  history stay allowed. Creation order is therefore commit order for every
  release the lifecycle compares. The ancestry check fails closed: when git
  cannot answer it (an error, a timeout or an exhausted process budget), the
  release answers 409 without a reason. The promotion script matches
  `"reason":"superseded"`, logs that 409 and continues; any other refusal,
  including every other 409, stops it with exit code 9. A
  declaration snapshot that git cannot read completely also answers 409;
  invalid input, including a malformed committed declaration, answers 400.
- **Declaration snapshot.** The release file also stores the environment
  declarations (`docs/deployments/environments/*.json`) as committed at the
  release commit. Every deploy job of the release, redeploy and rollback
  included, uses that snapshot, so a later change in the working tree or a later
  commit cannot change what an existing release deploys. The snapshot decides
  what is deployed (template, destination, health URL, container port, secret
  references); the current declaration decides where (`targetHostId`). After an
  environment moves to another host, a rollback or a late approval of an older
  release goes to the new host, and the run log names the host change. The
  environment must still be declared; a release without a snapshot for an
  environment cannot be deployed there. Automatic and history rollbacks of the
  host queue repeat the declaration the artifact last deployed healthy with, on
  the currently declared host. A job queued before the host moved stays with the
  old host; cancel it and deploy again.
- **Standard lifecycle.** Staging deploys automatically after integration.
  Production needs a successful Staging run of the same release and approval by
  Owner or Maintainer. Owner is the Studio owner role; Maintainer is an account
  with the project's production approval grant. Approval needs a signed-in
  Studio session in every profile; on the local profile a mutation counts the
  session only with its CSRF token. A caller without a session, such as a coding
  agent on the local API, is `local-operator` and never approves.
- **One environment, one direction.** A request or an approval is refused while
  another run is queued or running in the environment. The automatic Staging
  run after integration does not ask the lifecycle: it queues behind the runs
  already queued, and the host runs them one at a time in queue order. Because
  releases never move back in history, that queue only moves forward. A deploy
  moves only to a release newer than the current one;
  the current release runs again only as a redeploy, and an older release only as
  the rollback target. An approval decides these rules again, because another
  run may have finished meanwhile. Queuing a run closes the waiting runs it makes
  invalid, with their pending promotions: an approved release supersedes waiting
  deploys of older releases and waiting redeploys and rollbacks. The AGT-3176
  promotion and rollback routes answer through the same rules.
- **Deploy run.** Each run of a release in an environment has the state
  `queued`, `running`, `succeeded`, `failed`, `cancelled` or `awaiting-approval`,
  and the steps `approval` (production only), `publish-static` or
  `start-container`, and `health`. The host reports one bounded result code, so
  the health step has one observed instant. Step log lines are written by the
  Studio, never copied from host output. Runs follow the queue: jobs started by
  the gate, the older promotion and rollback routes, and automatic rollback are
  adopted into runs, and pending promotions become runs that await approval. A
  failed deploy gets no automatic rollback when a newer job already waits for
  the environment, so the rollback cannot replace that newer release.
- **Redeploy and rollback.** Redeploy runs the environment's current release
  again. Rollback runs the latest successful release older than the current one.
  Both follow the same approval rule. When the environment's first release has
  no older release to return to, the AGT-3176 rollback route falls back to the
  artifact history (the artifact deployed before releases existed), only while
  the environment is idle and only when the history target belongs to no
  release. Any other route rollback without an older release (for example
  after a lifecycle rollback to the oldest release) answers 409 "no previous
  release to roll back to" and queues nothing. The pre-release artifact has no
  release, so EnvironmentStatus keeps naming the last release run as current;
  health follows the host history.
- **Cancel, abort and lost hosts.** Cancel stops a queued or waiting run. Cancel
  on a running run aborts it: the host's lease is revoked, so its late report is
  refused, and the environment records the interrupted deploy as failed. Abort
  only revokes the lease: the Studio cannot stop the host process, which may
  still finish its step. Treat the environment as unknown or unhealthy until the
  next deploy, redeploy or rollback.
  Closing a pending production promotion and aborting a running production run
  need Owner or Maintainer. The host's report is accepted while its lease token
  is valid. After five minutes without a report, the same host may claim its job
  again (it restarted); each reclaim is logged in the run and the audit. A claim
  without report or reclaim for 15 minutes fails as `host-lost`, without an
  automatic rollback to the same host.
- **Audit.** Every release, request, approval, rejection, cancellation and host
  transition appends an entry to `audit.jsonl` next to the runs.

Routes, under `/api/projects/{project}/deploy`: `GET environments`, `status`
(one EnvironmentStatus per environment), `releases`, `releases/{id}`,
`deployments[?environmentId=]`, `deployments/{id}`, `deployments/{id}/log[?step=]`
and `audit`; `POST releases/{id}/deployments` (`{environmentId, approve}`),
`deployments/{id}/approve`, `deployments/{id}/reject`, `deployments/{id}/cancel`,
`environments/{id}/redeploy` and `environments/{id}/rollback` (`{approve}`).
`GET /api/deploy/matrix` returns the workspace dashboard rows, filtered to the
caller's projects.

The deploy host uses a credential with only `runner.deploy`; its service
identity must match `targetHostId`. In the managed host record, the deploy
principal and host id are the same enrolled `runner_<id>` value. Its secret
store is a local directory of
files named by the declared `secretRefs`. Secret values never enter the API,
job state, or audit history. The claim response carries names only.
The managed `remote-runner-onboard.sh --role deploy` path installs the separate
`agent-runner-deploy.service` and generated `deploy.env` for the host.

Status: concept and initial workflow implemented, 2026-07-11. The shared
Deployment contract now includes DEP-1 history and DEP-2 repository-derived
targets. Runnable deploy-stable and descriptor targets launch normal visible
CLI tasks (DEP-3), and the first bounded PDU compiler turns repository-script
commands with declared typed slots into a human-reviewed run form. Its operator
surface uses labelled command and parameter controls, progressively discloses
the generated source, and shows empty, loading, field-error, and valid preview
states. Package release targets remain delegated to Publishing. The richer
shared live-progress presentation and broader natural-language compiler remain
future refinements.

Promotion of the complete integration stream and the separate stable freeze use
the decided [release-semantics contract](release-semantics.md). Deployment owns
the project-specific executable steps; it does not redefine acceptance or hide
unreviewed work from a release manifest.

Mockup:
[mockups/deployment-first-class.html](mockups/deployment-first-class.html).

## Decision in one paragraph

**Deployment becomes a first-class per-project surface, the way Project URLs
already are.** Every project gets a **Deployment page** that shows its deploy
targets, their current state, and their run history. The known cases are modeled
as **scenario templates** (the Studio `deploy-stable` cycle, a Caddy site deploy
on the `agent-orchestrator-web` VM, and a NuGet/npm release via tag push). Where
no template fits, the operator describes the deployment as a **prompt**, and the
prompt is compiled into a small **dynamic UI** (fields, parameters, one run
button) for launching concrete deploys. Every actual run - template or
prompt-defined - executes through the existing **visible CLI-task substrate**
(AGT-2093), so progress, chat, operator input, and durable history come for
free. The read-only start (DEP-1: a Deployment page that renders the existing
`deploy-stable` history from git and logs) is small; the template runner and the
prompt-to-UI compiler are separate later slices.

## 1. Why deployment is a product object

Deployment is real, recurring work that today lives entirely outside the
product. It happens in a terminal, from memory, per project:

- the Studio itself ships through the `deploy-stable` cycle - the devspace-level
  `update-stable.sh` stops stable, pulls, and restarts, driven by
  [`restart-stable-after-batch.sh`](../../scripts/supervisor/restart-stable-after-batch.sh),
  which already appends a structured record to
  `<workspace>/logs/stable-restarts.jsonl` per restart;
- a public site is served by a reverse proxy (Caddy) on the
  `agent-orchestrator-web` VM
  ([linux-runner-host](../operations/setup/linux-runner-host.md) documents the
  same-origin `/api` + `/hubs` reverse-proxy shape);
- packages ship through a tag-push release picked up by an existing GitHub
  Actions workflow, already modeled read-only by
  [publishing workflows](publishing-workflows.md) (PUB-1 / PUB-2).

Deployment is **super fuzzy**: it differs per project and resists one universal
model. What changed is the substrate. AGT-2093 gives us **lightweight,
first-class CLI tasks with full visibility** - the same object the Remote Hosts
onboarding already reuses (AGT-2094): "the action creates a normal visible CLI
task, so the existing task conversation owns live output, operator input,
completion, and durable history"
([linux-runner-host](../operations/setup/linux-runner-host.md)). Once a deploy
run *is* a visible CLI task, deployment stops needing its own execution engine.
It needs a **page to see it**, **templates for the known shapes**, and a **way to
describe the unknown ones** - which is exactly this concept.

The user-facing name is **Deployment** (a project surface). A single configured
deploy path is a **target**; one execution is a **run**. This mirrors the
publishing vocabulary (target / run) so the two surfaces read as siblings, not
rivals.

## 2. The three shapes of deployment

Deployment splits cleanly into three shapes. The product models all three with
one page and one execution substrate; only the *definition* of a run differs.

| Shape | Where the run definition comes from | First examples |
|---|---|---|
| **Template** | A repository-recognized pattern with a known parameter set. | `deploy-stable` cycle, Caddy site deploy, NuGet/npm tag-push release. |
| **Prompt** | An operator prompt compiled into a dynamic UI. | Any project-specific deploy that no template matches. |
| **Derived (read-only)** | Facts already in git and logs, shown without a run action. | `deploy-stable` history from `stable-restarts.jsonl`; publish deltas from PUB-1. |

The derived shape is deliberately first: it is honest, safe, and immediately
useful, and it is what DEP-1 ships.

## 3. Scenario templates (the known cases)

A **template** is a named deploy shape with a small, typed parameter set and a
single command path, executed as a CLI task. Templates are how we avoid asking
the operator to re-describe a deployment the product already understands.

The three first templates are modeled on processes that exist today. They are
descriptions of reality, not new automation:

### 3.1 `deploy-stable` cycle (Studio self-deploy)

The Studio ships to its own stable seat. Stable never restarts itself
(ADR-0021); an external watcher counts new jobs in the review lane, checks that
stable is idle, then runs `update-stable.sh` and records the outcome.

- **Parameters:** target seat (stable), restart threshold (informational), a
  confirmation that stable is idle.
- **Run:** a CLI task that invokes the existing update script and streams its
  output; on success it verifies the runner resumed
  ([`resume-runner.sh`](../../scripts/supervisor/resume-runner.sh)).
- **History source:** `logs/stable-restarts.jsonl` (`headBefore`, `headAfter`,
  `jobsSinceLastRestart`, `durationSeconds`, `status`) plus the git range between
  the two HEADs. This is the DEP-1 read-only view - it needs no new run action.

### 3.2 Caddy site deploy (`agent-orchestrator-web` VM)

A public site is deployed behind a Caddy reverse proxy on a remote VM. The
deploy is a build plus a file sync plus a proxy reload on the host.

- **Parameters:** target host, source branch, site path, whether to reload the
  proxy.
- **Run:** a CLI task that executes the deploy over SSH on the remote host - the
  same remote-command pattern the Remote Hosts onboarding already uses through
  [`remote-runner-onboard.sh`](../../scripts/remote-runner-onboard.sh).
- **History source:** the CLI-task run history for this target, plus the deployed
  revision recorded per run.
- **Routing vs publishing.** A site published by a **workflow** (GitHub Pages /
  `deploy-pages`) is a *publishing* website target and stays owned by
  [publishing workflows](publishing-workflows.md) (PUB-1). A site deployed to a
  **host over SSH** (Caddy / rsync) is a *deployment* template target. The
  trigger mechanism - workflow versus host command - decides which surface owns
  a given site; the two never derive the same site twice.

### 3.3 NuGet / npm release via tag push

A package ships when a `vX.Y.Z` tag is pushed and an existing GitHub Actions
workflow publishes it. This template does not re-implement publishing; it is the
**launch surface** over the derivation and guarded actions that
[publishing workflows](publishing-workflows.md) already own.

- **Parameters:** ecosystem (derived), suggested next version (editable),
  release notes source.
- **Run:** delegates to the PUB-2 guided action (clean-worktree check, manifest
  bump, one release commit, atomic `HEAD` + tag push). The product never runs
  `npm publish` / `dotnet nuget push`; Trusted Publishing and the workflow stay
  authoritative.
- **History source:** the target's release tags and workflow-dispatch run
  status, already surfaced by PUB-1.

Templates are **derived from repository facts plus a repository-owned
descriptor**, never from an opaque setting stored outside the repo. Fact-only
derivation holds for the derived and release shapes; a host-bound template such
as Caddy additionally needs a small repository-owned descriptor for its host and
site path (§6), because SSH host identity is environment, not a repo fact. A
project that has none of these shapes simply shows no templates rather than an
invented one.

## 4. Prompt to dynamic UI (the individual case)

Most projects will have at least one deploy that no template matches. Instead of
forcing every such case into a bespoke feature, the operator **describes the
deployment in a prompt**, and the prompt is compiled into a **dynamic UI**: a
small set of typed fields, sensible defaults, and one run button. Submitting the
UI launches a CLI task with the resolved values.

The flow:

1. **Describe.** The operator writes a prompt: "Deploy the docs site: build with
   `pnpm build`, rsync `dist/` to `web@vm:/srv/docs`, reload Caddy. Let me pick
   the branch and choose whether to purge the CDN."
2. **Compile.** The product extracts a **UI schema** from the prompt - a bounded
   list of typed parameters (branch: enum of branches; purge-CDN: boolean;
   confirm: required), a human title, and a command intent. The schema is
   reviewable and editable; the operator confirms it before it becomes a
   reusable definition.
3. **Persist.** The confirmed definition is saved as a repository-owned deploy
   descriptor (see §6) - so the dynamic UI is now a stable, versioned target,
   not a one-off.
4. **Run.** Filling the fields substitutes the values into the compiled command
   template and launches it as a CLI task. Progress, chat, and history are the
   CLI task's, exactly like a template run.

The safety boundary is worth stating precisely, because "the UI has no execution
authority" is true but not the interesting claim - the interesting question is
what the *executor* is allowed to do. A PDU does **not** compile to a free-form
agent prompt (which would be unbounded shell); it compiles to a **bounded
command template with typed slots** - the same shape as a §3 template, only
authored from a prompt instead of recognized from repo facts. The dynamic UI
fills the slots; it never rewrites the command. Classifying an ask as
"dangerous" is a **compile-time, human-reviewed** step, not a runtime model
judgement: the compiler reduces the prompt to a bounded command and **flags
anything it cannot bound** (raw shell, an unpinned secret, a destructive verb),
and the operator sees and edits the template before it is saved or run. An ask
the compiler cannot bound stays flagged until the operator resolves it; it never
silently degrades into a free agent prompt. This is deliberately **weaker** than
a code-pinned template, and the card owns that cost: a PDU trades the correctness
guarantee of a pinned command path for flexibility, and its safety rests on human
review of a bounded template, not on the substrate.

## 5. Meta-pattern: dynamic UI from a prompt

"Compile a prompt into a small dynamic UI, then execute through the CLI-task
substrate" is a **reusable product pattern**, not a deployment-only trick. It
will recur wherever a task is *shaped* (has a small, stable parameter set) but
not *templated* (common enough to hard-code). Deployment is its first home; the
same pattern fits ad-hoc maintenance jobs, data backfills, and one-off
operational runs.

Name it explicitly so later work reuses it rather than reinventing it:
**Prompt-defined dynamic UI (PDU)**. Its contract:

- **Input:** a natural-language prompt plus the project context.
- **Output:** a bounded, typed UI schema (fields, types, defaults, required
  flags, a title, a command intent) that a human reviews and edits.
- **Persistence:** the confirmed schema is repository content, versioned and
  diffable - not a hidden model artifact.
- **Execution:** always the CLI-task substrate. The compiled artifact is a
  bounded command template with typed slots, not a free-form agent prompt; the
  UI fills slots and never holds execution authority.

### When template, when prompt

| Use a **template** when | Use a **prompt UI** when |
|---|---|
| The shape is recognized from repo facts (workflow, script, manifest). | No template matches and the deploy is project-specific. |
| The parameter set is known and shared across projects. | The parameters are idiosyncratic to this one project. |
| Correctness matters enough to pin the command path in code. | Flexibility matters more than a fixed command path. |
| Examples: `deploy-stable`, Caddy site, tag-push release. | Examples: bespoke rsync deploy, a custom migration run. |

A prompt UI that proves common across projects is a candidate to **graduate into
a template**. That promotion is a normal feature decision, not an automatic
step. This meta-pattern also belongs in the public working-model chapter of the
website/docs, described as one of the ways Agent Studio turns intent into
governed, visible work.

## 6. Object and storage contract

A project's deploy configuration is repository content, consistent with how
[workbenches](experimentier-workbench.md) and the
[wiki tree](../system/contracts/wiki-tree.md) store their objects as physical folders
rather than a virtual registry:

```text
docs/deployments/
  docs-site/
    deployment.json                  # target descriptor (schema + lifecycle)
    prompt.md                        # optional, the source prompt for a PDU target
```

`deployment.json` is the small query and lifecycle contract:

```json
{
  "schemaVersion": 1,
  "id": "docs-site",
  "title": "Docs site (Caddy on agent-orchestrator-web)",
  "kind": "template",
  "template": "caddy-site",
  "summary": "Build and sync the docs site, reload the proxy.",
  "parameters": [
    { "name": "branch", "type": "branch", "required": true },
    { "name": "purgeCdn", "type": "boolean", "default": false }
  ],
  "targetHostId": "agent-orchestrator-web",
  "updatedAt": "2026-07-11T10:30:00Z"
}
```

| Field | Values | Meaning |
|---|---|---|
| `kind` | `template`, `prompt`, `derived` | Which of the three shapes defines runs. |
| `template` | template id | Present when `kind` is `template`. |
| `parameters[].type` | `string`, `boolean`, `branch`, `enum`, `secret-ref` | Typed capture; `secret-ref` names a host secret, never stores it. |

Derived targets (like `deploy-stable` history) need no descriptor at all - they
are computed from git and logs. Descriptors exist only for targets that have a
run action. There is no `registry.json`; the folders are the model. Runs
themselves are **not** stored here: a run is a CLI task, and its record lives in
the task store, referenced by task key.

## 7. Execution is always a CLI task

Every deploy run is a normal visible CLI task (AGT-2093). This is the load-
bearing decision that makes deployment cheap to add:

- **Progress, chat, and history are free.** The task conversation already owns
  live output, operator input, completion, and durable history. Deployment does
  not build a second progress model.
- **The Deployment page launches and links, it does not execute.** Pressing run
  from a template or a prompt UI creates a CLI task through the existing task
  mutation boundary and then shows that task's live surface. The page is a
  launcher and a history view over deploy-tagged tasks.
- **The deployed revision is justified by a test run.** The normal launcher
  choice is the latest completed successful project test run. The visible task
  records its run id, exact commit, and distance to Head. Selecting Head is a
  reasoned exception and does not let an older green run bless newer commits.
- **Secrets stay on the host.** A `secret-ref` parameter names a
  host-managed secret; the product injects nothing. GitHub authorization for the
  release template comes from the operator's `gh` session, exactly as
  publishing-workflows requires.
- **The running deploy uses the CLI-progress pattern.** A live deploy renders in
  the same progress/step/chat layout as any CLI task, so operators already know
  how to read it. The mockup demonstrates this third view.

What the substrate gives for free is the **plumbing** - progress, chat, live
output, and durable history. It does **not** give deploy *correctness*: the
command path, host secrets, a health check, and rollback on failure are still
per-target work that each template (or PDU command) must get right. This concept
makes deployment cheap to *see and launch*; the hard parts of a specific deploy
stay as hard as they are.

## 8. Deployment page (read-only first)

Under each expanded project, **Deployment** is a first-class Explorer row (a
sibling of Wiki, Dossiers, and Project Hub), and a full-bleed page when
opened. The page has three regions:

Project Overview may render a compact last-deploy and pending-delta summary as
an entry point to this page. That block consumes the same DEP-1 summary contract
as Deployment itself. It does not parse `stable-restarts.jsonl` or git again,
persist a second last-deploy truth, or gain a separate run action.

```text
+-- Deployment (project) ---------------------------------------------+
| Targets            | History                                        |
|  deploy-stable  ●  |  vX @ab12  ->  @cd34   3 tasks   42s   ok       |
|  docs-site (Caddy) |  ...                                           |
|  NuGet 0.3.1       |                                                |
|  + Describe a deploy (prompt)                                       |
+---------------------------------------------------------------------+
```

- **Targets** lists derived + templated + prompt targets, each with a quiet
  status (dot or tint, never a colored left bar -
  [style-guide R1](../quality/design/style-guide-hard-rules.md)).
- **History** shows recent runs for the selected target. For `deploy-stable`
  this is read straight from `stable-restarts.jsonl` and the git range; no run
  action is required.
- **Describe a deploy** opens the prompt-to-UI flow (§4).

Counts reconcile to visible children (R3). Settled runs render quietly; only a
currently-running deploy carries an acute signal (R4). Both themes, keyboard
access, reduced motion (R5). The page never centers itself in a narrow column
(R2).

## 9. Invariants and non-goals

- Deploy targets are derived from repository facts and repository-owned
  descriptors, never from opaque stored settings.
- Every run executes as a visible CLI task; deployment owns no second execution
  engine, progress model, or scheduler.
- Every normal deployment names the successful test run and exact commit that
  justify it. A Head exception records its reason durably in the deployment
  task.
- The Deployment page and any prompt-compiled UI capture parameters and launch
  tasks; they never hold credentials, secrets, or direct execution authority. A
  PDU compiles to a bounded command template with typed slots, never a free-form
  agent prompt.
- Secrets are named by reference and stay host-managed; the product injects
  none.
- The release template delegates to publishing-workflows; the product never runs
  `npm publish` or `dotnet nuget push`.
- Derived/read-only history is shown without inventing numbers; when there is no
  reliable record the page stays quiet (matching PUB-1's discipline).
- Aggregate counts equal visible children; settled runs are calm; both themes,
  keyboard, narrow layouts, reduced motion, and no colored left accent bars are
  part of every slice.
- The first version is not a CD platform, a pipeline engine, an environment
  promotion graph, a secret store, or an approval-workflow system.

## 10. Boundary with adjacent systems

| Object | Primary question | Owns | Relationship to deployment |
|---|---|---|---|
| Publishing workflows | Is something publishable, and ship it? | Package + workflow-published site target derivation + guarded release actions (PUB-1/2). | The release template is a launcher over it; host/SSH sites are deployment's, workflow/Pages sites stay publishing's. |
| [Project URLs / iframe](distributed-agent-studio-target-architecture.md) | Where does the running result live? | Configured per-project URLs and previews (AGT-2095). | A deploy target may point at a Project URL to preview the deployed result. |
| Dossiers | What should we see and decide? | Repository HTML experiments + decision-to-task (AGT-2084). | A deploy prompt is a decision that spawns a run, not an experiment. |
| Remote Hosts onboarding | Provision a runner host. | Visible-CLI-task provisioning over SSH (AGT-2094). | Same CLI-task substrate and same remote-command pattern as a Caddy deploy. |
| CLI-task substrate | Run and see one command. | Lightweight visible CLI tasks (AGT-2093). | The execution layer for every deploy run. |

Deployment is the **launch-and-history surface**; publishing owns package/site
release actions; the CLI-task substrate owns execution; Project URLs own the
live result. Deployment does not absorb any of them.

## 11. Implementation slices and honest size

The complete feature is **large**: it crosses repository discovery, a new
Explorer surface and page, template runners, a prompt-to-UI compiler, task
mutation, and read-only history over two data sources. It should be an Epic or
coordinated card family, not one coding card. The first read-only cut is small.

| Slice | Honest size | Scope | Acceptance boundary |
|---|---|---|---|
| **DEP-1: Deployment page, `deploy-stable` history (read-only)** | M | Add the Explorer row and page; render `deploy-stable` runs from `stable-restarts.jsonl` + the git HEAD range. No run action. | History reconciles to the log; empty/missing log stays quiet; the per-row git range is batched/cached (no per-row git spawn - see the AGT-2007 git-info work); both themes; no left accent bars; no execution path. |
| **DEP-2: Template targets, read-only** | M | Implemented: derive tag-push-release targets via Publishing, deploy-stable from repository facts, and host/SSH targets from repository-owned descriptors. | The shared summary returns no invented target when facts or descriptors are absent; workflow-published websites stay Publishing's. |
| **DEP-3: Template run over the CLI-task substrate** | M/L | Implemented: parameter capture and confirmation launch deploy-stable and runnable descriptor targets through `VisibleCliTaskService`. | The action creates a normal Ready task through the existing mutation boundary; secrets remain references; release targets delegate to Publishing. |
| **DEP-4: Prompt-to-dynamic-UI compiler (PDU)** | L | Initial bounded compiler implemented: a prompt declaring a repository script command and typed slots renders a review form and launches through the same CLI-task substrate. Descriptor persistence and richer natural-language extraction remain follow-up scope. | Shell chaining, redirection, undeclared slots, and commands outside `scripts/*.sh` are surfaced as non-runnable warnings. |
| **DEP-5: Running-deploy view in the CLI-progress pattern** | S/M | Present a live deploy in the shared CLI-task progress/step/chat layout from the Deployment page. | Reuses the existing CLI-task live surface; acute only while running; settles quietly into history. |

DEP-4 is the risk-bearing slice (prompt-to-schema extraction, safety review,
persistence). DEP-1 and DEP-2 are safe read-only starts that deliver value
before any run action exists. Dependency order: DEP-1 -> DEP-2 -> DEP-3, with
DEP-4 after DEP-3 and DEP-5 alongside DEP-3.

## 12. Feature handoff status

The concept began as a production-code-free handoff, but the first workflow is
now implemented. DEP-1 and DEP-2 share one backend projection for history and
repository-derived targets. DEP-3 launches runnable targets as normal Ready CLI
tasks, and the bounded first DEP-4 compiler turns a repository-script command
with declared typed slots into the same reviewed run form. The remaining
follow-ups are descriptor persistence, broader natural-language extraction, and
the richer shared live-progress presentation described in DEP-5.

## 13. Second-opinion pass

An independent product and architecture review was applied on 2026-07-11. It
challenged the first draft on scope creep, execution ownership, prompt safety,
site-derivation ownership, and honest read-only limits. The resulting changes
are part of this concept:

- deployment explicitly owns **no execution engine**; every run is a CLI task,
  and the page is a launcher plus history view, not a runtime;
- the release template **delegates** to publishing-workflows, and §3.2 adds an
  explicit **site-ownership routing rule** (workflow/Pages sites are
  publishing's; host/SSH Caddy sites are deployment's) so no site derives twice;
- the prompt-to-UI safety story was sharpened: a PDU compiles to a **bounded
  command template with typed slots, not a free-form agent prompt**, danger
  classification is a **compile-time human-reviewed** step, and the card now
  **owns that a PDU is weaker than a code-pinned template** rather than implying
  parity;
- "derived from repo facts, never a stored setting" was corrected to **facts
  plus a repository-owned descriptor**, because a Caddy host/SSH identity is
  environment, not a repo fact; DEP-2's acceptance is scoped accordingly;
- §7 now states plainly that the substrate gives **plumbing, not deploy
  correctness** (command paths, secrets, health check, rollback stay per-target
  work), so "cheap to add" is not oversold;
- secrets are **named by reference and stay host-managed**; the product injects
  none; the read-only history is **quiet when there is no reliable record**,
  matching PUB-1's no-invented-numbers discipline;
- DEP-1 was re-rated **M** with a git-batching acceptance clause (the AGT-2007
  hazard), DEP-1/DEP-2 stay **read-only** so the first cut ships value without
  any run path, and DEP-4 is the single large risk slice.

The interactive mockup demonstrates the three views (Deployment page with
templates, prompt-to-dynamic-UI, and a running deploy in the CLI-progress
pattern). Its simulated run output and compiled schema are illustrative, not
product behavior.
