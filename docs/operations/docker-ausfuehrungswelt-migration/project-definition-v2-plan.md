# Project definition v2: shared properties and quality applicability

Status: **inactive proposal, 2026-09-13**. This extends the preparation plan in
[AGT-W51](index.html#s3); it does not replace its recorded decisions or claim a
delivered v2 runtime. The [draft schema](project-execution.v2.draft.schema.json)
is a review artifact, deliberately outside the runtime schema directory.
**Do not copy these v2 examples into `.agent-studio/project.yml` today.**

## Current contract and implementation

The repository-owned definition is exactly `.agent-studio/project.yml`, with
`schemaVersion: 1`. Its preparation script is `.agent-studio/prepare`. Coding
runs and the build-test gate read the subject commit, use the shared preparation
step, and record `preparation-manifest.json`. The dossier's former sketch with
`tools`, `tests`, `cache`, top-level `build`, and `image: null` was not a valid v1
definition; [section 3](index.html#s3) now shows the actual v1 shape.

| Authority | Current responsibility |
| --- | --- |
| [Project execution schema](../../app/schemas/project-execution.schema.json) | Version 1, closed top-level and command fields |
| [Shared reader, validator, DTOs and executor](../../../contracts/TaskServer.Contracts/ProjectPreparation.cs) | Restricted YAML syntax, version checks, preparation and manifest production |
| [Preparation evidence schema](../../app/schemas/preparation-manifest.schema.json) | Source revision/digest, tools, lockfiles, caches, duration and failure evidence |
| [Build-test gate](../../../backend/Features/Pipeline/BuildTestGateRunner.cs) and [runner workspace](../../../runner/GitWorkspace.cs) | Preparation in verification and coding worktrees |
| [Setup runbook](../setup/preparation-isolation-orchestrator.md) and [ADR archive, ADR-0073](../../system/architecture/decisions/adr-archive.md) | Delivered M1 and its boundaries with later isolation/orchestrator work |
| [Quality pipeline](../../system/domains/pipeline.md) and [analysis policy schema](../../app/schemas/quality-analysis-policy.schema.json) | AGT analysis activation and ownership of Quality Studio rules |

The reader is a hand-written YAML subset, not a general YAML deserializer. It
supports simple maps and lists, two-space indentation and the named test-suite
shape. Tabs, anchors, aliases, tags and unknown fields are rejected. Inline-list
parsing does not provide full YAML quoting or block-scalar semantics. The JSON
schema is also not a substitute for the runtime validator: the reader supplies
some missing collections as empty, and validation applies its own checks.

The current accepted top-level fields are `schemaVersion`, `stack`,
`toolVersions`, `commands`, `testSuites`, `cachePaths`, `capabilities`,
`environment`, and optional `devServer` and `image`. `toolVersions` refers to
repository files, such as `global.json`, rather than copying their versions.
`image`, when supplied, is a relative definition-file path, not an image URI.
`commands` only supports `prepare`, `build`, `test` and `lint`.

`devServer` and the named `testSuites` inventory are parsed and validated today;
there is no consumer that starts that server or dispatches a suite by its ID.
Existing project URL starts instead use
[`ProjectUrlStartRule`](../../../backend/Shared/Models/RegistryModels.cs) and
[`ProjectUrlProcessService`](../../../backend/Features/Registry/ProjectUrlProcessService.cs).
That service uses Windows `cmd /c` or Unix `sh -c`, while preparation verification
commands use Bash. No YAML-to-start-rule adapter currently connects these paths.

A present but invalid definition is not harmless: the preparation executor
reports `definition:invalid`, blocking preparation and the gate.
[`VerifyCommandPlanner`](../../../backend/Features/Pipeline/VerifyCommandPlanner.cs)
can fall back to profile/discovery when it has no valid definition, but that is
not a general fallback for the preparation path. New v2 keys must never be
inserted under `schemaVersion: 1`.

## Proposed v2 additions

Retain all v1 execution fields and their meanings. Add only two top-level
sections, `project` and optional `quality`. A future v2 reader must be explicitly
implemented before adoption. Do not create a second active manifest or a
different `.yaml` filename to bypass the existing reader.

`project.id` is a stable repository-owned identifier, not an AGT registry ID,
Quality Studio review ID, directory-scope ID, URL record ID or display name.
`project.components[]` gives each component a unique local `id`, repository
relative `path`, optional `name`, local `properties` and `executionRefs`.
Component paths describe source scope; they do not register or deploy services.
A single-component project may use the path `.`.

### Descriptive properties

`project.properties` and `project.components[].properties` are boolean maps.
These are authored declarations of intended product behavior, not test results
or proof of compliance. A missing value is **unknown**, not `false`. Explicit
`false` records an intentional negative declaration. String values such as
`"true"`, nulls and unknown property names fail draft validation.

| Property ID | Meaning when declared `true` |
| --- | --- |
| `public-facing` | Intended to be accessible to a public audience |
| `html-ui` | Presents an HTML user interface |
| `seo-relevant` | Search discoverability is an intended requirement |
| `payment-api` | Exposes or integrates a payment API |
| `personal-data` | Processes personal data |
| `authenticated` | Has authenticated interactions or access control |
| `persistent-data` | Persists application data beyond one process lifetime |
| `realtime` | Has behavior with real-time interaction requirements |
| `localized` | Supports localized user-facing content |
| `deployable` | Is intended to be deployed as an artifact or service |

Values are local: **there is no implicit inheritance** from `project` to its
components or between overlapping component paths. A public website and an
internal API can therefore share a repository without giving the API SEO
requirements. Project-level properties describe the whole product for inventory;
the initial applicability draft matches named components and reads only each
component's local map. Repository-wide existing checks keep their own scope.

Executor `capabilities`, such as `git`, `child-process` or `loopback-network`,
remain requirements on the execution environment. They must not be renamed or
interpreted as these product properties. A declaration also never grants network,
secret, process or deployment permission.

### Build and start references

`executionRefs.build[]`, `.test[]`, `.lint[]` and `.start[]` contain a repository
relative `path` and an optional `pointer` into its parsed JSON/YAML value. A
pointer is a JSON Pointer, for example `/scripts/start` or `/commands/build/0`.
For a script, omit the pointer. The containing repository is always the base;
component paths do not change reference resolution.

These are navigation and provenance references to existing definitions. They
neither duplicate command strings nor provide a new executor. `commands` remains
the preparation/verification authority. A build reference can identify an
existing package script; it does not add it to a gate. A start reference does not
turn `devServer` into an implemented supervisor or authorize running a script.

Any later start adapter needs its own acceptance gate: explicit shell and
working directory, dependencies, foreground/background lifetime, process-tree
cleanup, readiness versus mere liveness, port/origin requirements, cancellation,
and ownership of an already-running instance. Imports must preserve the existing
source and require a reviewed mapping before use. In particular, do not silently
translate a Bash verification command into an AGT registry shell command.

### Quality targets and selectors

`quality.applicability[]` contains a stable local `id`, explicit `componentScope`
(a non-empty list of component IDs), `selector`, and at least one of `ruleIds`
or `domains`. Both target lists are allowed, but they resolve different kinds
of catalogue references. Expand each domain to its **canonical check IDs** through
the pinned Quality Studio catalogue. Retain every check's `method`, `status`,
subject scope, applicability, evidence requirements and limitations, together
with its `ruleIds` and `metricIds`. A domain is not merely a bag of rule IDs.

The catalogue distinguishes `review-rule`, `implemented-metric` and `planned`
checks. Domain resolution must preserve all three. A review check references
existing rules and their supported review method; a measurement check references
its metric sources and required observation context. `ruleIds` in YAML adds
explicit rule targets to this resolved set, subject to existing enablement.
`metricIds` belongs to the resolved catalogue check and preserves its measurement
sources; this draft does not add a separate YAML `metricIds` target list.

Planned checks remain visible as `not-assessed` with a planned reason. An
implemented check whose required producer or evidence is unavailable remains
`unavailable`/`not-assessed`. A catalogue status alone is not evidence that a
measurement or review ran. An empty executable rule set must never turn a domain
request into a successful review: the planned payments checks and service-level
observations in reliability must still appear in the assessment inventory.

Deduplicate assessment by canonical check/rule, subject identity and producer,
preserving distinct checks and their individual evidence requirements. Reuse the
same producer observation for a shared metric, subject revision and observation
context instead of measuring it twice merely because two domains or checks
reference it. A domain reference and an explicit rule reference likewise must
not execute the same rule/subject/producer twice. Retain the links from each
check to that shared evidence; do not erase an unassessed check while deduplicating.

Do not copy check/rule texts, severity defaults, scores or sensor implementations
into YAML. Catalogue IDs, rather than display labels, are the reference boundary.

Current domain IDs include `architecture`, `correctness`, `testing`, `security`,
`privacy`, `payments`, `reliability`, `performance`, `accessibility`, `localization`,
`operations`, `review-prioritization` and `seo`. Unknown rule/domain IDs must produce a resolution error,
not a wildcard or an empty successful review. New catalogue IDs need not change
the YAML grammar, but each run must resolve them against a known catalogue.

Each `selector` may contain these property-ID lists. The table describes a
non-empty group:

| Group | True | False | Unknown |
| --- | --- | --- | --- |
| `allOf` | Every listed property is true | At least one is false | Otherwise |
| `anyOf` | At least one is true | Every listed property is false | Otherwise |
| `noneOf` | Every listed property is false | At least one is true | Otherwise |

Combine non-empty groups using the same three-valued AND as `allOf`. Missing
and empty groups add no condition, including an empty `anyOf`; this is a list
of optional constraints, not a mathematical empty disjunction. `selector: {}`
therefore matches without a property condition, but only within its explicit
non-empty `componentScope`. This matches the Quality Studio catalogue convention. Thus `noneOf: [payment-api]` does not match a component that has
never declared `payment-api`. No consumer may coerce an absent property to false.

For checks and rules targeted by these declarations, the proposed scope is the
union of explicit component matches, constrained by each catalogue entry's own
subject scope and applicability. A component filter must not silently reinterpret
a project-level check as a file-level check. An unlisted component is outside the
declared scope. Multiple matching entries reuse the assessment and evidence keys
described above. A false selector excludes that match. An unknown selector
produces an explainable `not-assessed`/missing-declaration outcome, never a passing
check. Checks and rules with no applicability declaration retain their catalogue
and policy behavior. An explicit true match can establish applicability despite
a second unknown entry; record both evaluation traces instead of counting an
extra review. Applicability cannot satisfy a missing producer or evidence requirement.

**Applicability is not activation.** Existing `.quality/agent-studio.json`
remains AGT's analysis activation. Quality Studio owns its rule enablement and
overrides; its current opt-in SEO rules use `.quality/rules/overrides.json`.
The first rules are `QS-GN-005` through `QS-GN-008`. Today their implementation
does not read this proposed YAML filter. Even after a future adapter exists,
`seo-relevant: true` alone must not enable a rule or pipeline step. An enabled
rule with insufficient applicability evidence must not be reported as passed.

## Inactive example: Agent Studio

This example retains the existing execution fields in a shortened inventory.
Its new `project` block is a draft. Current readers reject the complete v2
document. The actual active v1 file remains unchanged; the frontend's existing
`devServer` declaration is deliberately not portrayed as implemented start support.

```yaml
schemaVersion: 2
stack: [dotnet, node, playwright]
toolVersions:
  node: .nvmrc
  dotnetSdk: global.json
commands:
  prepare: .agent-studio/prepare
  build:
    - dotnet build agent-taskboard.sln --no-restore
    - npm --prefix frontend run build
  test:
    - dotnet test backend.Tests/OrchestratorApi.Tests.csproj --no-build --filter Category!=MachineBound
    - npm --prefix frontend test -- --run
  lint:
    - npm --prefix frontend run lint
testSuites: []
cachePaths: [frontend/node_modules]
capabilities: [git, child-process, loopback-network]
environment:
  DOTNET_NOLOGO: "1"
  NUGET_XMLDOC_MODE: skip
devServer:
  command: npm --prefix frontend start
  workingDirectory: .
  healthUrl: http://127.0.0.1:4010
  startupTimeoutSeconds: 120
  stopWithRun: true
project:
  id: agent-studio
  name: Agent Studio
  properties: {}
  components:
    - id: backend
      path: backend
      properties: {}
      executionRefs:
        build:
          - path: .agent-studio/project.yml
            pointer: /commands/build/0
        start:
          - path: api.sh
    - id: studio-ui
      path: frontend
      properties:
        html-ui: true
      executionRefs:
        build:
          - path: frontend/package.json
            pointer: /scripts/build
        start:
          - path: frontend/package.json
            pointer: /scripts/start
```

## Inactive example: Quality Studio

Quality Studio currently has neither `.agent-studio/project.yml` nor
`.agent-studio/prepare`. This is a proposed onboarding document; its prepare
script still needs to be authored and proven. Build/start references point at
existing sources. The backend is already flat under `backend/`, without an
intermediate `backend/src/`. This is a local Studio example; it does not claim
that the product repository's reference `website/` is the separately published
Quality Studio website. The backend test lane defaults to `Release`; the explicit
`--configuration Release` below matches the preceding Release build when using
`--no-build`.

```yaml
schemaVersion: 2
stack: [dotnet, node]
toolVersions:
  node: .nvmrc
  dotnetSdk: global.json
commands:
  prepare: .agent-studio/prepare
  build:
    - dotnet build QualityStudio.slnx -c Release
    - npm --prefix frontend run build
  test:
    - node scripts/run-dotnet-lane.mjs non-machine --configuration Release --no-build
    - npm --prefix frontend test
  lint:
    - npm --prefix frontend run lint
testSuites: []
cachePaths: [frontend/node_modules]
capabilities: [git, child-process, loopback-network]
environment:
  DOTNET_NOLOGO: "1"
project:
  id: quality-studio
  properties: {}
  components:
    - id: api
      path: backend/QualityStudio.Api
      properties:
        public-facing: false
        html-ui: false
        seo-relevant: false
      executionRefs:
        build:
          - path: QualityStudio.slnx
        start:
          - path: scripts/dev-stack.mjs
    - id: studio-ui
      path: frontend
      properties:
        public-facing: false
        html-ui: true
        seo-relevant: false
      executionRefs:
        build:
          - path: frontend/package.json
            pointer: /scripts/build
        start:
          - path: package.json
            pointer: /scripts/start
```

## Inactive example: Voice Studio

Voice Studio's repository is `voice-lint`. It also currently has no active
project definition or prepare script. The empty `toolVersions` below does not
claim reproducible tool pinning; pin files and preparation evidence are a gate
before onboarding. The test list is illustrative, not its complete test policy.

The Studio is a loopback application, while `website` contains intended public
product pages. The local component values below are explicit proposed owner
declarations. SEO intent requires confirmation before adoption. Other properties
stay unknown when the inventory does not establish them.

```yaml
schemaVersion: 2
stack: [dotnet, node]
toolVersions: {}
commands:
  prepare: .agent-studio/prepare
  build:
    - npm run build
  test:
    - npm run test:library
    - npm run test:backend
  lint: []
testSuites: []
cachePaths: [node_modules]
capabilities: [git, child-process, loopback-network]
environment:
  DOTNET_NOLOGO: "1"
project:
  id: voice-studio
  properties: {}
  components:
    - id: studio-api
      path: backend/VoiceStudio.Api
      properties:
        public-facing: false
        authenticated: true
        persistent-data: true
      executionRefs:
        build:
          - path: package.json
            pointer: /scripts/build:backend
        start:
          - path: scripts/start.sh
    - id: studio-ui
      path: frontend
      properties:
        public-facing: false
        html-ui: true
        localized: true
      executionRefs:
        build:
          - path: frontend/package.json
            pointer: /scripts/build
        start:
          - path: frontend/package.json
            pointer: /scripts/start
    - id: review-library
      path: packages/review
      properties:
        html-ui: true
      executionRefs:
        build:
          - path: packages/review/build.mjs
    - id: writing-rules
      path: packages/writing-rules
      properties: {}
      executionRefs:
        build:
          - path: packages/writing-rules/build.mjs
    - id: contracts
      path: packages/contracts
      properties: {}
    - id: website
      path: website
      properties:
        public-facing: true
        html-ui: true
        seo-relevant: true
        localized: true
        deployable: true
      executionRefs:
        build:
          - path: package.json
            pointer: /scripts/website:build
        start:
          - path: package.json
            pointer: /scripts/website:preview
quality:
  applicability:
    - id: public-website-seo
      componentScope: [website]
      domains: [seo]
      selector:
        allOf: [public-facing, html-ui, seo-relevant]
```

`scripts/start.sh` currently includes build and possible dependency installation,
then runs the API process. That process serves Studio/API on loopback port 5188
and an isolated example site on 5189; Angular development uses 4188. The website
preview is a separate process on 5187 under `/voice/`. Pairing, session ownership
and allowed origins remain Voice runtime concerns. A consumer must not collapse
these into one URL or infer permission to start all of them from `executionRefs`.
Voice's private `checks.json` is a bounded build/test command list loaded at API
startup, not a service supervisor and not this repository definition.

## Compatibility and ownership matrix

| Concern | Agent Studio today | Quality Studio today | Voice Studio today | Proposed integration gate |
| --- | --- | --- | --- | --- |
| Project YAML | Active v1 reader, preparation and evidence; rejects v2 | No active project YAML in the inspected repository | No active project YAML in the inspected repository | Explicit supported-version negotiation before writing v2 |
| Build/verify | V1 command arrays, Bash execution, shared preparation | Solution, npm scripts and test-lane scripts | npm workspace composition and .NET/script checks | Reference resolution preserves each source, shell and verified preparation |
| Start | Registry `ProjectUrlStartRule`; YAML `devServer` has no consumer | `scripts/dev-stack.mjs` supervises local stack | `scripts/start.sh`, Angular dev and separate website preview | Separate adapter contract and lifecycle tests; no starts from metadata discovery |
| Product properties | No `project.properties` runtime | Domain/property catalogue is owned here; no v2 YAML matcher yet | No common YAML property consumer | Shared IDs, local boolean maps, unknown preserved across all DTOs |
| Quality applicability | Existing `.quality/agent-studio.json` activates analysis | Owns canonical rule/domain IDs and rule overrides | Existing Voice checks and writing rules retain their meaning | Read-only scope adapter first; no duplicate rule authority or implicit activation |
| Browser/site boundaries | Existing URL records own their lifecycle | Local Studio and separate published website are distinct repositories | Pairing and separate Studio/target origins required | Repo-local component references cannot erase service/origin boundaries |
| Failure behavior | Invalid present YAML blocks preparation; planner fallback is narrower | Unsupported YAML integration must remain visibly unavailable | Existing checks remain available without adopting YAML | Never turn unsupported, unresolved or unknown into a successful assessment |

The initial Voice adapter, if built, should expose references and property state
without replacing its writing-rule catalogue, private checks, pairing, or task
protocol. AGT consumes Quality Studio through the existing package boundary;
Quality Studio remains the authority for checks, rules, domains and their metric
source mappings. Technology style-guide IDs and
versions in the [existing quality guide catalogue](../../quality/README.md)
remain a separate prompt/documentation contract.

## Versioning, validation and migration

Keep four versions distinct: the YAML `schemaVersion`, the consumer release,
the Quality Studio catalogue revision, and the actual tool versions resolved
from referenced manifests. A future capability advertisement must report
supported definition versions and implemented features such as properties,
selector evaluation and execution-reference display. Put that information in
the consumer/API contract, not in v1 executor `capabilities`. A consumer that can
display a start reference must not advertise lifecycle execution support.

The draft JSON schema checks shape, boolean values, property names, relative path
syntax, selector values and non-empty component scopes/targets. A shared semantic validator is still
required for unique IDs, existing components, safe resolved paths, pointer
targets, canonical catalogue IDs and cross-field behavior. Resolve symlinks and
ensure every source remains inside the subject checkout. No unresolved ID or path
may fall back to the repository root. Overlapping source scopes must retain
canonical file/review identities and deduplicate assessment; changing a display
component ID must not overwrite review history.

Do not extend `preparation-manifest.json` in place under its current version.
A future versioned evidence extension must retain the subject SHA and definition
digest, then record the resolved catalogue revision/hash, selected component
paths, local declaration states, selector traces, active policy identity and
resolved source references. Preserve resolved check IDs, method/status, required
evidence, rule/metric source IDs, producer identity and observation context,
including planned or unavailable entries. Persist unknown or unresolved outcomes so a resumed
run evaluates against the same evidence and does not silently adopt a newer
catalogue or mutable checkout.

1. **Contract review:** agree on the draft and shared fixtures across AGT, Quality
   Studio and Voice Studio. Resolve scope, unknown-state and activation behavior.
   This document and schema alone do not complete this gate.
2. **Reader-first delivery:** implement v1/v2 consumers, common conformance tests,
   capability reporting and visible unsupported-version errors. Deploy compatible
   AGT server, contracts, runner and gates before any repository opts in. Existing
   v1 fixtures and invalid-definition behavior must stay unchanged.
3. **Read-only preview:** show resolved components, local properties, source
   links, enabled-policy state and selector decisions. No starts, new checks or
   gate changes merely from discovering metadata.
4. **Pilot preparation:** author and validate missing prepare scripts for Quality
   Studio and Voice, select/pin tool versions, and prove build/test preparation
   on the subject commit. AGT retains its known working v1 baseline.
5. **Explicit repository migration:** a reviewed commit changes one file from v1
   to v2 after every required consumer reports support. Do not maintain divergent
   parallel active definitions. Preserve the previous v1 blob for rollback;
   downgrade through a reviewed commit, not lossy silent key stripping.
6. **Execution and quality rollout:** only after their separate adapters pass,
   enable lifecycle integration and property-aware applicability through existing
   policy ownership. Record provenance and compare assessments before changing
   gates. Complete all three pilots before general onboarding defaults change.

## Required acceptance evidence

These are implementation acceptance requirements, not claims of tests delivered
by this documentation change.

| Area | Required fixture and expected result |
| --- | --- |
| V1 compatibility | Existing active v1 and generator fixtures still pass unchanged; adding `project`, `quality`, `commands.start` or version 2 to the old reader remains a visible invalid/unsupported definition |
| Schema/reader parity | Shared positive and negative documents across schema, DTO reader, AGT server/runner and future QS/Voice adapters; reject duplicate keys, unsupported YAML features, non-booleans and misspelled property IDs consistently |
| Unknown semantics | Exhaustive true/false/unknown combinations for each selector group and their AND; missing/empty groups are neutral, including `anyOf: []`; `noneOf` with a missing property stays unknown; omission is preserved in API round trips |
| Local scope | A public website plus internal API in one repository; only the website matches SEO; setting project-level SEO values does not change either local component |
| Rule ownership | Disabled rule stays disabled despite a true selector; enabled rule plus unknown selector is not a pass; domain and explicit rule overlap runs once per canonical rule/subject/producer; unknown catalogue IDs are errors |
| Domain completeness | `domains: [payments]` on a component with `payment-api: true`, without an implemented producer, retains the planned canonical checks as `not-assessed`; an empty rule list is never a successful payment review. Reliability retains its planned service observations alongside review rules. |
| Measurement identity | An `implemented-metric` check retains its method, metric IDs and evidence requirements; unavailable producers stay `unavailable`/`not-assessed`. Two checks referencing the same metric/subject/producer/observation context share one measurement while keeping distinct check results. |
| Scope safety | Duplicate component/applicability IDs, missing IDs, traversal, absolute/escaped/symlink paths and unresolved pointers fail visibly; overlapping paths do not duplicate findings or change canonical review IDs |
| Reference display | Resolve existing AGT, QS and Voice build/start sources from the subject commit without executing them; a package-script rename produces a stale-reference error |
| Lifecycle adapter | Preserve Bash/cmd/sh semantics, dependencies, readiness, fixed ports/origins, already-running ownership, cancellation and child cleanup; Voice pairing and isolated target origins still hold |
| Preparation pilots | QS and Voice missing prepare scripts fail preflight until authored; fresh and warm-cache builds record evidence; AGT v1 remains runnable throughout rollout |
| Provenance/resume | Definition, catalogue and policy changes after scheduling cannot change a resumed decision; results identify source/catalogue revisions, canonical check/rule and producer identities, method/status and evidence requirements, including planned and unavailable outcomes |
| Rollback | Older consumers never receive an active v2 definition; reverting the reviewed migration restores the exact prior v1 preparation path without deleting findings or registries |

Current regression starting points are
[`ProjectPreparationTests`](../../../backend.Tests/ProjectPreparationTests.cs)
and [`VerifyCommandPlannerTests`](../../../backend.Tests/VerifyCommandPlannerTests.cs).
They cover existing reader/validator and planning behavior, not the proposed v2
selectors or start adapters. Documentation validation checks the draft schema,
examples and links; it does not certify runtime compatibility.

## Pending v2 extension requests

Recorded on 2026-09-13 from decisions taken the same day. Each is a request for
the contract review in step 1, not an accepted key. None may be written into an
active v1 definition; until a v2 reader ships, every consumer applies a product
default or a workspace project setting instead.

| Request | Source | Proposed shape | Interim behaviour |
| --- | --- | --- | --- |
| Release identity rule | AGT-2792 (Stable release contract follows the project's rule; operator decision 2026-09-13: every project owns its own rules, Agent Studio uses lock files) | `release.identity[]` with `package`, `ecosystem` (nuget, npm), `source` (lock file path, or exact pin plus registry hash); `release.restore[]` commands | Product default: lock-file identity (`backend/packages.lock.json` with `dotnet restore --locked-mode`, `frontend/package-lock.json` with `npm ci`); the manifest records `identitySource` |
| Areas for the tag system | AGT-2803 (Dossier AGT-W55, D3: ten product areas refined per project) | `project.areas[]` with `id`, `label`, `glossary` reference; may reference `project.components[].id` | Project additions in the workspace project settings; product defaults from the areas registry |
| Auto-tagging opt-out | AGT-2804 (Dossier AGT-W55, D4) | `tagging.autoTag: false` | Workspace project setting |

Quality facet tags reuse the domain IDs listed under "Quality targets and
selectors"; the tag system introduces no second list of quality domains.
