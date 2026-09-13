# Stable Release And Update Contract

Stable deploys releases, not folders or moving branches. A deployable Agent
Studio build is identified by an immutable `v<semver>` tag and a
`build-manifest.json` conforming to
[`build-manifest.schema.json`](../app/schemas/build-manifest.schema.json). Folder
timestamps are never part of freshness, ordering, comparison, or rollback.

## Release identity

The manifest records Agent Studio tag, version, commit, dirty flag, build time,
and integrity. It also records the exact version, tag, commit, and package
integrity for CodingAgentRunner (CAR) and Coding Agent Chat (CAC). A release
build is rejected when any tag is missing, a tag does not equal `v<version>`,
the checkout is dirty, an integrity value is absent, or CAC still resolves from
a local `file:.../dist` dependency.

Every project owns its dependency identity and restore rules in the `release`
section of `.agent-studio/project.yml`. Stable release tooling reads that file
from the tagged candidate commit. It refuses a project with no release rule.
Two identity rule kinds are supported:

- Lock file: `package`, `ecosystem`, and repository-relative `source`. For
  NuGet, the source entry supplies `resolved` and `contentHash`. For npm, the
  source entry supplies `version`, `resolved`, and `integrity`; the sibling
  `package.json` must exact-pin the same registry version.
- Exact pin plus registry hash: `package`, `ecosystem`, `version`, and
  `integrity`. `integrity` starts with `sha256-` or `sha512-`. This rule is for
  a project that has no lock file but can commit the exact registry identity to
  its project definition.

Agent Studio uses lock-file identity and declares this release section:

```yaml
release:
  identity:
    - package: CodingAgentRunner
      ecosystem: nuget
      source: backend/packages.lock.json
    - package: coding-agent-chat
      ecosystem: npm
      source: frontend/package-lock.json
  restore:
    - dotnet restore backend/OrchestratorApi.csproj --locked-mode
    - npm --prefix frontend ci
```

Generate the manifest only after all three upstream identities are known:

```sh
node scripts/release/generate-build-manifest.mjs \
  --tag=v1.0.0 --version=1.0.0 \
  --car-version=0.5.0 --car-tag=v0.5.0 --car-commit=<sha> --car-integrity=sha512-<value> \
  --cac-version=<version> --cac-tag=v<version> --cac-commit=<sha> --cac-integrity=sha512-<value>
```

The generator uses create-new semantics and refuses to overwrite an existing
manifest. It derives CAR and CAC versions and integrity through the candidate
commit's project rules, then refuses supplied metadata that differs. Stable
runs the ordered `release.restore` commands from that same definition, so the
project, not the updater, owns its lock and restore paths. The
backend copies the manifest beside the published assembly and exposes the
same identity from `GET /api/system/about` and `GET /api/system/version`.
`GET /healthz` remains body-compatible and adds tag and commit response headers.

## Stable preflight

The update preflight compares four explicit identities:

- running: the backend about response;
- installed: the manifest belonging to the rollback target;
- candidate: the immutable manifest attached to the requested release tag;
- latest approved: the release channel's signed or operator-approved tag.

The result is `same version`, `upgrade`, `downgrade`, `divergence`, or
`comparison unavailable`. Offline comparison is permitted only with a cached
latest-approved tag and cached immutable manifests. A downgrade needs an
explicit rollback or downgrade authorization. Same-version/different-artifact,
running/installed divergence, package mismatch, dirty builds, and missing tags
are hard failures.

The outer updater downloads the candidate release asset to the configured
candidate-manifest cache using create/replace-by-tag semantics. The Update
Service never manufactures it from a branch checkout. It fetches the manifest's
exact tag without overwriting an existing tag, verifies the dereferenced tag,
then reads `.agent-studio/project.yml` and every declared identity source from
that commit. Manifest versions and integrity must match those rules, and an npm
lock rule must resolve an exact registry artifact rather than `file:.../dist`.
Only then may Stable check out the commit detached, run its declared restore
commands, and install the candidate manifest. It does not use branch distance
to decide whether a release is available. That avoids moving branch identity,
a stale manifest that merely agrees with itself, and a self-referential
manifest commit.

Before mutation, copy the installed manifest and create a self-contained Git
bundle for the installed commit in the run folder. Together they are the exact
rollback target even if the source ref later disappears. Rollback restores the
manifest, can recover the commit from the bundle, and must pass the same runtime
identity comparison after restart. Health alone is not success. Append the full
intended and observed identities, direction, and rollback outcome to deployment
history.

Frontend installation has one additional cache boundary. Agent Studio's
postinstall bridges patch `node_modules/coding-agent-chat` in place, while the
Angular/Vite optimizer cache key does not reflect those changed bytes. Every
Stable update that runs `npm install` or `npm ci` must therefore remove
`frontend/.angular/cache` after the install and before frontend startup. After
startup, load the frontend once through `playwright-core` with a `pageerror`
listener registered before navigation. A page error is a failed deployment,
even when the frontend port and backend health endpoint are reachable.

Offline mode is an explicit updater input (`ReleaseMetadataOffline`), not an
inference from cache presence or age. It is accepted only when both cached
manifests and the cached latest-approved tag still pass the same comparison.

## Update Service ordering, mutation boundary, and health wait

Three incidents from the first tagged release (v0.2.0, 2026-09-13) changed the
Update Service's phase ordering. This section documents the current contract
so a future change does not reintroduce them.

**Stop before restore.** The stack (backend, frontend dev server, and any
process the checkout owns) is stopped before the locked dependency restore
(`dotnet restore --locked-mode`, `npm ci`) runs, not after. Before the restore
runs, the orchestrator also confirms nothing still holds files open under
`frontend/node_modules`; if it does, the run fails immediately and names the
holding process (PID and command line where determinable) instead of letting
`npm ci` fail with an opaque `EPERM` unlinking `esbuild.exe`.

**Mutation boundary.** The checkout is moved to the candidate commit
(`git checkout --detach --force`) before restore and restart, but
`build-manifest.json` is only written into the live Stable checkout after
restart has cleared health, runtime-identity verification, and the frontend
port check. The intended manifest is kept in the run folder
(`intended-build-manifest.json`) until that point. Any failure before the
manifest is committed automatically reverts the checkout to the pre-run
commit; the manifest file is never touched, so the next preflight is
unaffected and no manual manifest deletion is needed.

*Recovery if this still happens.* If a run somehow leaves the checkout ahead
of the installed manifest anyway (for example, a crash of the Update Service
process itself between the checkout move and the revert), running the next
update is normally sufficient: the orchestrator re-evaluates the preflight
and, if it needs to install a different candidate, checks out the new target
regardless of where HEAD currently sits. If the preflight instead reports
"running identity diverges from the installed manifest", the response now
also names the run (`runId`, finish time, status) whose `IntendedTag` matches
what is currently installed, from deployment history — check that run's
folder under `RunsDirectory` first before touching anything by hand. Only
delete `build-manifest.json` manually as a last resort, and only after
confirming from the run folder that no run actually committed a manifest for
the currently-running identity.

**Cold-compile health wait.** A cold compile (many changed commits since the
last Stable build) can take roughly three minutes before `dotnet` starts
listening on port 5031. The restart health wait budget
(`RestartHealthWaitSeconds`, default 600s / 10 minutes) is sized for that,
so a slow-but-alive compile is not reported as "backend exited before it
started listening". The wait only stops early on success; a genuinely dead
launch still fails once the full budget elapses. The observed startup
duration is recorded in the run's `summary.md` and in deployment history
(`backendStartupSeconds`, `frontendStartupSeconds`) so operators can tell a
slow-but-fine run from a stuck one after the fact.

**Frontend restart.** After backend health and identity verification pass,
the orchestrator waits for the frontend dev server's port
(`FrontendUrl`, default `http://127.0.0.1:4011`) to accept connections before
the run is allowed to reach `phase=done`. A run that leaves the backend up
and the frontend down (or vice versa) is reported failed, not done.

## Migration

An installation without a manifest reports `tag=untagged`, `dirty=true`,
`legacy=true`, and no inferred build time. This is an honest migration identity,
not a releasable candidate. Record its current commit as the initial rollback
anchor, then deploy the first tagged Agent Studio release through the normal
preflight.

### First tagged release from a legacy Windows Stable

The release owner chooses and approves `<version>` and `<promoted-main-sha>`.
Run these commands from a clean checkout whose `HEAD` is the promoted `main`
commit. Do not use the `release/<timestamp>` promotion marker as the Stable
release tag.

```powershell
$Version = '<version>'
$MainSha = '<promoted-main-sha>'
git fetch origin main --tags
git checkout --detach $MainSha
dotnet restore backend/OrchestratorApi.csproj --locked-mode
npm --prefix frontend ci
if ((git status --porcelain).Length -ne 0) { throw 'Release checkout is dirty' }
git tag -a "v$Version" $MainSha -m "Agent Studio v$Version"

node scripts/release/generate-build-manifest.mjs `
  --tag="v$Version" --version="$Version" `
  --car-version='<car-version>' --car-tag='v<car-version>' --car-commit='<car-commit>' --car-integrity='sha512-<car-hash>' `
  --cac-version='<cac-version>' --cac-tag='v<cac-version>' --cac-commit='<cac-commit>' --cac-integrity='sha512-<cac-hash>'

git push origin "refs/tags/v$Version"
```

The locked restore is the freshness guard for Agent Studio's NuGet graph. If
`backend/packages.lock.json` is missing or stale, stop and update the dependency
and lock together on `develop`; do not regenerate the lock on the promoted
commit.

If the host's normal candidate-asset downloader is not being used, place and
approve the candidate before triggering the Update Service. Ensure no update
trigger is already in flight:

```powershell
$Metadata = 'C:\Projects\agent-taskboard-workspace\.metadata'
New-Item -ItemType Directory -Force $Metadata | Out-Null
Copy-Item -Force .\build-manifest.json "$Metadata\stable-candidate-manifest.json"
Set-Content -NoNewline "$Metadata\stable-approved-tag" "v$Version"
```

Start or leave the Update Service running, inspect the non-mutating preflight,
then trigger the update. Include `X-Update-Token` when the service is configured
with `ATP_UPDATE_TOKEN`.

```powershell
Invoke-RestMethod http://127.0.0.1:5039/update/preflight
Invoke-RestMethod -Method Post -ContentType 'application/json' `
  -Body '{"reason":"manual","force":false}' `
  http://127.0.0.1:5039/update/trigger
Invoke-RestMethod http://127.0.0.1:5039/update/status
```

Wait for `phase=done`, then verify the complete runtime identity, not only
health. The response tag and commit must equal `v<version>` and
`<promoted-main-sha>`, `dirty` must be false, and the CAR and CAC version,
commit, tag, and integrity fields must equal `build-manifest.json`.

```powershell
$About = Invoke-RestMethod http://127.0.0.1:5031/api/system/about
$Manifest = Get-Content .\build-manifest.json -Raw | ConvertFrom-Json
$RuntimeArtifacts = @($About.codingAgentRunner, $About.codingAgentChat) | ConvertTo-Json -Depth 5 -Compress
$ManifestArtifacts = @($Manifest.codingAgentRunner, $Manifest.codingAgentChat) | ConvertTo-Json -Depth 5 -Compress
if ($About.tag -ne $Manifest.tag -or
    $About.commit -ne $Manifest.commit -or
    $About.integrity -ne $Manifest.integrity -or
    $RuntimeArtifacts -ne $ManifestArtifacts -or
    $About.dirty) {
  throw 'Stable runtime identity does not match the approved candidate'
}
$About
```

The first run preserves the legacy checkout commit and installed identity in
its run folder before mutation. Keep that update run folder as the initial
rollback evidence.

CAC is exact-pinned from the npm registry by Agent Studio's package manifest
and lock. Do not copy a local CAC `dist` into a release or relax that pin to a
range. Read its release version and integrity from the tagged commit rather
than copying a historical value from this runbook.

A task worktree does not update Stable directly. After the integration commit
is accepted, the release owner creates the immutable Agent Studio tag,
generates the build manifest with the CAC identity above, deploys it through
the normal preflight, and records the observed Agent Studio tag, commit, and
CAC version in deployment history. The reissued integration task retains the
original AGT-2170 relationship for audit continuity.

## Three-component topology gate

A release that claims the distributed Agent Studio architecture must pass the
real-process topology gate after the normal solution build:

```bash
dotnet test runner.Tests/AgentRunner.Tests.csproj \
  --no-build \
  --filter "FullyQualifiedName~AgentRunner.Tests.LogShipperCapTests|FullyQualifiedName~AgentRunner.Tests.BoundedOutputBufferTests"
dotnet test task-server.Tests/TaskServer.Tests.csproj \
  --no-build \
  --filter "FullyQualifiedName~TaskServer.Tests.TopologyTests|FullyQualifiedName~TaskServer.Tests.ProtocolTests" \
  --logger "console;verbosity=normal"
```

The gate is bounded and CI-runnable. It must pass all four topology scenarios:
client-off completion and replay, brief Runner transport interruption with
idempotent typed replay, fail-closed Task Server outage with
positive-no-overlap recovery, and authenticated HTTPS event ingestion and
replay. It also runs the published mixed-version fixtures and proves an
unsupported Runner is rejected before registration or claim.

Do not replace a failed scenario with a timeout-only assertion or a manual
observation. The canonical history must contain typed lifecycle evidence for
messages, bounded traces, artifacts, completion, failure classification, and
recovery proof. Review and reissue remain the deployed backend's authority.
Process-parent assertions must show that Studio and its optional BFF own neither
Task Server nor Runner.

The scenario-to-contract map and replay route are maintained in
[Distributed Agent Studio target architecture](../concepts/distributed-agent-studio-target-architecture.md#release-proof).
