# Publishing Workflows (concept)

Version: v2 (2026-07-13)
Status: PUB-1 derivation and PUB-2 guided publish actions are implemented.

Operator intent: **dead simple sehen, dass etwas Publizierbares da ist - im
Prinzip nach jedem Task.** After a task lands, the operator should see at a glance
whether the project now has something worth publishing (a package release, a
website deploy) without opening a terminal or remembering per-project release
mechanics.

Publishing a package or website target is distinct from promoting the whole
integration stream. The latter follows the
[`develop -> main` release and stable-freeze contract](release-semantics.md).

## 1. Scope and non-goals

- **PUB-1:** derive publish targets from repository facts, compute the pending
  delta, and show Project Hub badges plus accepted-task chips.
- **PUB-2:** open a guided action panel from a badge, list the pending accepted
  tasks, suggest an editable patch/minor version, and drive the repository's
  existing tag or workflow-dispatch path.
- **Still out of scope:** storing registry secrets or implementing a second
  package publisher. Trusted Publishing remains keyless and the existing
  repository release script or GitHub Actions workflow owns the actual publish.

## 2. Target derivation (from repo facts, never settings)

A project can declare `release.command` and `release.branch` in project.yml. This derives a package target without a GitHub workflow and enables a dry run plus verified script execution on that branch.

A project can have zero or more **publish targets**. Targets are derived, per
project, from repository declarations, workflows, tags, and manifests, and never
from a stored operator setting.

### 2.1 Package target (npm / NuGet)

A declared release command derives a package target directly. Without one, a package target is derived when the repository has:

1. a **release-triggered workflow** under `.github/workflows/` - a workflow
   triggered by a version tag push (`on: push: tags: ['v*']`) or a published
   release (`on: release: types: [published]`); and
2. a **publish step or manifest** identifying the ecosystem:
   - **npm** when a workflow step runs `npm publish` (or uses
     `JS-DevTools/npm-publish`), or a `package.json` is found; the source root is
     the manifest's directory and the package name is its `name`.
   - **NuGet** when a workflow step runs `dotnet nuget push` / `nuget push` /
     `dotnet pack`, or a packable `.csproj` is found (has `IsPackable`,
     `PackageId`, `GeneratePackageOnBuild`, or a `Version`, and is not a test
     project); the source root is the project's directory and the package name is
     its `PackageId`.

The **current version** is the latest `v*` tag with the `v` stripped (e.g.
`v0.3.1` -> `0.3.1`). A package with a release workflow but **no `v*` tag** has
never been published - see the first-publish special state (§4).

Reference layouts the derivation is pinned against:

- **coding-agent-runner**: a NuGet release workflow + a Pages deploy workflow,
  currently at `v0.3.1`. Derives a `NuGet 0.3.1` package target + a website
  target.
- **coding-agent-chat**: an npm release workflow + a Pages deploy workflow, never
  tagged. Derives an npm package in the first-publish-pending state + a website
  target.

### 2.2 Website target

A website target is derived when a workflow deploys a site: the modern Pages
actions (`actions/deploy-pages`, `actions/upload-pages-artifact`), the classic
`peaceiris/actions-gh-pages`, or a workflow whose filename names it
(`deploy-website.yml`, `pages.yml`). The website source folder defaults to
`website/`; when the upload action names a different `path:`, that path is used.

## 3. Pending delta (what changed since the last release/deploy)

For each target, the pending delta is the number of **merged mainline
(first-parent) commits on the integration branch, since the target's reference
point, that touch the target's path scope**. A no-ff integration merge counts
once on the mainline even when the accepted card has several commits. The badge
and action panel label this number as commits. The panel lists accepted cards
whose attributed commits entered through those mainline commits.

- **Path scope.** Package = the package source paths (the manifest's directory),
  minus the website folder and repo-meta folders (`.github`, `docs`,
  `.orchestrator`) that are never package source. Website = the website folder.
  So a website change never counts toward the package, and vice versa.
- **Reference point.**
  - Package: the last `v*` tag (`referenceKind: tag`).
  - Website: the tip date of a `gh-pages` deploy branch when one exists
    (`referenceKind: pages-branch`) - the only website-deploy record that lives
    in git; else the last release tag as a documented approximation
    (`referenceKind: release-tag`); else no baseline (`referenceKind: none`),
    because the modern `actions/deploy-pages` flow leaves **no** git marker. When
    there is no baseline the count is not asserted (null) and the UI stays quiet
    rather than inventing a number.
- **Quiet by default.** `pendingCount === 0` renders no badge. Silence means
  “nothing to publish”.

### 3.1 UI surfaces (read-only)

- **Project Hub badge:** one badge per non-quiet target, e.g.
  `NuGet 0.3.1 -> 4 commits pending`. Green for a normal package delta, violet for
  a website delta, amber for the first-publish state.
- **Accepted-task chip:** a `publishable: npm, website` chip on a completed task,
  listing the targets whose scope that task's merged work touched. It is derived
  by set-membership of the task's attributed commits against the commits newly
  introduced by each scoped mainline change, once per project rather than once
  per card.

## 4. Special state: first publish pending

A package with a release workflow / manifest but **no `v*` tag at all** has never
been published. There is no version and no meaningful delta baseline, so instead
of a count the Hub shows `<ecosystem> first publish pending (manual, operator)`.
The first publish is intentionally a manual operator action.

For later releases, a feature task in the pending set suggests the next minor
version; a mix containing only bugs and chores suggests the next patch. The
operator can edit the suggestion before confirmation. Confirmation requires a
clean worktree, updates `package.json` or the packable `.csproj`, creates one
release commit and `vX.Y.Z`, then atomically pushes the release branch and tag to
`origin`. The legacy action requires a `main` checkout and also updates npm lockfile root versions. The legacy tag action leaves registry publishing to GitHub. A declared project release command runs the repository script after verification; see [Library releases](../operations/setup/library-releases.md).

Each target stores an automation mode: `manual`, `suggest`, or `auto`. Package
targets clamp `auto` to `suggest`; website targets may use all three modes. The
action panel is the shared operator surface for the suggestion and current
workflow result. Website `auto` subscribes to the accepted-task transition and
waits for that task's integration merge to appear in the website delta before
dispatching the existing workflow.

## 5. Implementation map

- Backend derivation: `backend/Features/Publishing/` -
  `PublishWorkflowParser`, `PublishManifestLocator`, `PublishTargetService`,
  `TaskPublishableService`, and `PublishEndpoints`.
- Repository releases: `ProjectReleaseExecution` previews the declared branch subject, verifies it, runs the repository command, and atomically pushes its branch and tag. Contained pending cards receive package release timeline receipts.
- Backend actions: `PublishActionService` performs guarded manifest/tag pushes,
  GitHub workflow dispatch, and `gh api` run tracking. Authorization comes from
  the operator-managed `gh` CLI session; the product never persists a token.
- Git primitives: `GitService.GetLatestVersionTag`,
  `GetMainlineCommitsForScope`, and `GetTipCommitDateUtc`.
- Snapshot fold: `publishTargets` on the project snapshot.
- Board fold: `TaskInfo.PublishSignal` on task lists and detail.
- Frontend: `project-publish-panel` provides the guided release/deploy flow; the
  accepted-task chip remains the per-task signal.

## 6. Honest limitations

- Website pending is only precise when a `gh-pages` deploy branch exists. A
  modern `actions/deploy-pages` deployment is not recorded in git, so PUB-1
  labels a release-tag approximation or stays quiet when no anchor exists.
- Derivation is heuristic over workflow text. An unusual custom pipeline may not
  be recognized. Declare `release.command` and `release.branch` for a custom release pipeline.
- Target publishing does not answer whether all of `develop` should be promoted
  to `main`; the transparent release manifest owns that broader decision.

## 7. Executed reference instance

On 2026-07-10, CodingAgentRunner `v0.5.0` was published to NuGet through its
existing tag-driven Trusted Publishing workflow. The consumer bump remained a
separate task. This is the reference shape for a package target: Agent Studio
drives and observes the existing workflow instead of becoming a second package
publisher.
