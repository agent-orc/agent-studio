# Model Routing Policy

Version: 2026-10-04

Status: Canonical policy, initial hypothesis based on the 2026-07-23 historical benchmark

Owner: Pipeline and CLI domains

This page is the authoritative answer to two questions:

1. Which model and thinking level should perform a task or bounded pipeline
   decision?
2. Why is that route proportionate to the task's correctness risk, expected
   scope, context demand, available quota, and observed outcomes?

The policy selects the cheapest tier that clears the required capability floor.
It does not claim that a larger model repairs a vague task, a broken gate, or
missing evidence. Explicit operator pins still win, but the UI or orchestrator
should explain when a pin is below the policy floor.

## Routing tiers

| Route | Default use | Do not use for | Evidence and rationale |
|---|---|---|---|
| `gpt-6-luna` / `medium` (`luna-medium`) | Trivial, mechanical, locally specified changes with a small expected diff and an obvious verification path. Examples: remove one control, rename a local label, update a narrow fixture. | Unclear bugs, cross-subsystem behavior, public contracts, migrations, security, concurrency, or distributed state. | No Luna cohort existed in the 2026-07-23 benchmark. This is therefore a cost-saving hypothesis, not a validated quality claim. The empirical uncertainty adds points and keeps borderline work on Terra. |
| `gpt-6-sol` / `low` (`sonnet-low`; or `claude-sonnet-5` / `low`) | Added 2026-09-13 (AGT-2808). The economy floor for feature (and any coding/concept/planning/research) work: the weakest route economy mode may ever select for that class. Not a normal-mode default; only reached by an economy-mode downgrade. | Anything below the floor - economy mode must never fall through to a Haiku-class model for feature/bug work. Haiku-class models are only used by the separate pipeline-support/classification path (`ModelFamilyResolver`, not this registry). | No dedicated historical cohort; introduced to close the Haiku-fallback gap found in AGT-2793/AGT-2807 (economy mode landing Claude-CLI feature cards on `claude-haiku-4-5` via the old positional catalogue fallback). |
| `gpt-5.6-terra` / `medium` (`terra-medium`) | Economy downgrade for standard features and bugs when the correctness floor permits it; also suited to content and reversible UI or service changes inside one subsystem. | P0 work, fencing, distributed authority, data-loss paths, or changes that require broad architectural reconstruction. | The historical report contained eight Terra/medium records, but none had a known grade and none formed a trustworthy terminal cohort. Promote on substantive reissue until controlled data validates it. |
| `gpt-6-sol` / `medium` (`sol-medium`) | Default for new feature and bug cards, including demanding implementation, investigation, or analysis with several interacting concepts. | Correctness-critical control-plane work that meets a hard floor. | The GPT-6 route is provisional. Its predecessor Sol/medium had seven standard chore/feature runs with zero reissues. Five had known grades and all five were A or B. This is the strongest favorable historical signal, although the sample is still small and observational. |
| `gpt-6-sol` / `xhigh` (`sol-xhigh`) | Correctness-critical work: P0, fencing, leases, distributed authority, security boundaries, destructive migrations, data-loss prevention, or subtle concurrent state machines. | Routine work merely because quota is available. More thinking is not a substitute for tighter scope or deterministic tests. | The xhigh cohort was heavily selected for difficult and incident-driven work: 78 runs, 32 reissued, with only 22 known grades. Its high reissue rate is a warning about cohort and pipeline churn, not proof that xhigh causes poor outcomes. This tier is selected by the correctness floor while controlled benchmarks remain open. |

`high` and `ultra` are supported reasoning levels but are not default core-task
routes in this policy. Add a default tier only after controlled comparisons
show a repeatable benefit over `medium` or `xhigh`.

`gpt-6-astra` is **not yet tiered**. It is onboarded as a known model so the
picker can offer it (or explain its absence) when the installed codex-cli lists
it, but it has no routing tier, is not the product default, and has no cohort
in the benchmark below. Whether it becomes a tier or the default is a separate
operator decision; until then it is selectable only as an explicit pin, and an
explicit pin is not evidence that it clears any correctness floor.

### GPT-6 re-base (policy 2026-10-04, AGT-2903)

The operator moved new fleet cards to GPT-6 Sol on 2026-09-25. Policy
`2026-10-04` records that decision and mirrors the Token Economy GPT-6 ladder
revision shipped in TokenEconomy `0.3.6` (TE-57 routes, TE-59 catalogue
refresh, TE policy `2026-09-25`):

- `luna-medium` routes to `gpt-6-luna`; `sonnet-low`, `sol-medium` and
  `sol-xhigh` route to `gpt-6-sol`. `terra-medium` stays on `gpt-5.6-terra`
  because Token Economy retained Terra as the 21-50 tier. The tier count is
  unchanged, so no ADR was needed.
- Feature and bug cards default to `sol-medium` (intake score 51). This is the
  operator decision, not a cohort result: at dated prices GPT-6 Sol
  (USD 2 / 0.20 / 10 per MTok input / cached / output) is cheaper on output than
  GPT-5.6 Terra (2 / 0.20 / 12). The Anthropic route for Sol/medium is the same
  Sonnet 5/medium route Terra used, so Claude cards are unaffected. The
  correctness floor for bugs (`terra-medium`) and the feature economy floor
  (`sonnet-low`) are unchanged. Chores stay on `luna-medium`.
- Economy mode still lowers one tier, so a feature or bug card lands on
  Terra/medium. At dated prices that saves nothing over GPT-6 Sol/medium, so
  `estimatedSavingsPercent` for Terra is now 0. Revisit this when Token Economy
  decides Terra's future.
- `estimatedSavingsPercent` is measured against GPT-6 Sol/medium: Luna 95 (GPT-6
  Luna lists at USD 0.10 / 0.01 / 0.50), Sol/low 50 (fewer reasoning tokens,
  same price; still a hypothesis).
- Everything GPT-6 is still **provisional**. TE-57 ran 32 medium fixture
  attempts without returned model identity (GPT-6 Luna passed 5/8 with one
  output-contract failure), and xhigh, max and ultra have no local cohort. The
  routes rest on dated prices, vendor claims (OpenAI states GPT-6 Sol makes
  about half the mistakes of GPT-5.6 Sol) and the operator decision. Promote on
  substantive reissue as usual.
- When the installed codex-cli does not offer the GPT-6 tier model, the
  recommendation uses the declared provider-rejection sibling (`gpt-6-sol` to
  `gpt-5.6-sol`, `gpt-6-luna` to `gpt-5.6-luna`) instead of the positional
  catalogue fallback. Those siblings also inherit the GPT-6 tier when
  correctness floors are checked, so an existing `gpt-5.6-sol xhigh` pin still
  clears Sol/xhigh.
- `gpt-6-astra` is still untiered: two `access_programs.cyber` HTTP 400s in
  about eight runs on 2026-09-18 keep it explicit-pin only. `gpt-6.1-sol`
  is priced and selectable with TokenEconomy `0.3.7` when the installed CLI
  offers it. It is not a default route.

**Codex default.** `ModelMetadataRegistry.DefaultForCli(codex)` prefers
`gpt-6-sol` when live discovery offers it, then `gpt-5.6-sol`, then the static
`gpt-5.5` baseline. The CLI's own flagged default does not override this
order. Its default level comes from discovery (`medium`).

**Ladders and unoffered levels.** Live codex-cli discovery on 2026-10-04
reports `gpt-6-sol` as `low, medium, high, xhigh, max, ultra` and `gpt-6-luna`
as `low, medium, high, xhigh, max` (both default `medium`; `minimal` is
rejected). No per-model mapping is hard-coded. A pinned level the discovered
ladder does not offer runs at the highest offered rung below it (for example
`ultra` on `gpt-6-luna` runs at `max`; `ultra` on a ladder ending at `xhigh`
runs at `xhigh`). An unknown level, or one below the ladder, still lands on the
model default. The picker and the card's model badge state the mapping
visibly ("Pinned ultra is not offered by gpt-6-luna; runs at max."). The badge
shows it as an `ultra→max` chip.

**Picker and prices.** The picker lists the newest generation first, so GPT-6
models lead and GPT-5.6 entries sit under "Older models". Each row shows the
current input/output list price from the TokenEconomy cost API
(`POST /api/token-pricing/calculate`). Studio holds no rates. Token cost for
GPT-6 runs comes from the same dated TokenEconomy price snapshots. Cached
input is priced once at the cache-read rate (AGT-2882).

GPT-6 Sol and GPT-6 Luna require codex-cli `0.155.0`. GPT-6.1 Sol's registry
version marker is codex-cli `0.155.0`, the version in the TokenEconomy 0.3.7
probe. That probe received HTTP 400, so the marker does not guarantee execution;
only live CLI discovery can offer the model. Claude Opus 5.5 uses
Studio's `high` default effort, while [Anthropic's API default is `medium`](https://platform.claude.com/docs/en/models/opus-5-5/overview).
Its CLI floor is claude-code `2.1.281`. TokenEconomy `0.3.7` records Claude
Sonnet 5.5 as `unsupported` because the probed CLI did not offer it. Studio
offers it only when installed claude-code `2.1.284` or later lists it.
The [GPT-6.1 Sol model page](https://developers.openai.com/api/docs/models/gpt-6.1-sol)
lists a 1,050,000 token context window. Production CLI discovery reported
`minimal, low, medium, high, xhigh` with default `xhigh`; Studio uses that
discovered ladder even though the API model page lists a different API ladder.
The [Sol model page](https://developers.openai.com/api/docs/models/gpt-6-sol)
and [Luna model page](https://developers.openai.com/api/docs/models/gpt-6-luna)
each list a 1,050,000 token context window.
The picker disables a missing model with a version reason when an older CLI is
installed. Pricing and aliases come from TokenEconomy `0.3.7`, using
[Anthropic's pricing](https://platform.claude.com/docs/en/about-claude/pricing)
and [OpenAI's pricing](https://developers.openai.com/api/docs/pricing).

`gpt-5.6-luna` and `gpt-5.6-terra` above were already this policy's routing
tiers, but until AGT-2707 round 2 they had no
`ModelMetadataRegistry` entry, so a codex-cli that did not offer one left it
silently invisible in the picker instead of disabled-with-a-reason. They are
now registry entries for that catalog-visibility reason only: their routing
tiers, reasoning ladders, and defaults above are unchanged. Their registry
`Available` baseline is deliberately false so a total CLI-probe failure never assumes a
gpt-5.6 model is offered - the same detection-only posture `gpt-5.6-sol` keeps
by having no registry entry at all (AGT-2025).

## Weighted decision

Score the task at intake from information available before implementation. Use
the expected diff and affected contracts, not the eventual diff. The maximum is
100 points.

| Criterion | Weight | Scoring anchors |
|---|---:|---|
| Correctness risk | 35 | `0`: prose, formatting, or a non-behavioral local edit. `12`: reversible local behavior with a clear test. `24`: persistent state, a public contract, an unclear bug, or a consequential migration. `35`: P0, fencing or lease authority, security boundary, distributed concurrency, or plausible data loss. |
| Expected scope | 20 | `0`: up to about 50 changed lines in one subsystem. `8`: about 51-200 lines or two tightly related components. `14`: about 201-500 lines or three subsystems. `20`: more than 500 lines, four or more subsystems, or a repository-wide migration. Generated files do not count. |
| Context demand | 20 | `0`: exact file and behavior are known. `8`: one adjacent component or contract must be read. `14`: several layers or historical behavior must be reconciled. `20`: broad codebase references, architecture history, and cross-repository or distributed invariants are required. |
| Task type and uncertainty | 10 | `0`: mechanical chore or copy change. `3`: clear refactor or content task. `6`: well-specified bug or feature. `10`: unknown root cause, architecture decision, or requirements that must be derived. Task type is a prior, not a verdict. |
| Empirical confidence | 10 | `0`: a comparable cohort has at least 20 runs, useful grade coverage, at least 70% A/B among known grades, and under 10% reissue. `3`: at least five favorable comparable runs. `6`: sparse or mixed evidence. `10`: no comparable cohort, repeated reissues, or an unfavorable cohort. |
| Quota and cost headroom | 5 | `5`: the preferred provider is comfortably below its caps. `3`: a quota window is nearing its cap. `0`: the preferred route is capped or unavailable. This criterion may move a borderline task down, but never below a hard floor. |

For core task execution, map the total to the ladder:

| Score | Route |
|---:|---|
| `0-20` | Luna / medium (GPT-6 Luna) |
| `21-24` | Sol / low (`sonnet-low`, economy floor only - see below) |
| `25-50` | Terra / medium |
| `51-69` | Sol / medium (GPT-6 Sol) |
| `70-100` | Sol / xhigh (GPT-6 Sol) |

Luna/high is a role exception for bounded pipeline decisions with structured
evidence and a parseable output contract. It is not the bottom rung of the
core-task ladder. GPT-5.4 Mini was retired on 2026-09-11 and is retained only
as a historical identifier.

### Automated card convention

The machine-readable registry is
[`backend/Policies/model-routing-policy.v1.json`](../../../backend/Policies/model-routing-policy.v1.json).
Its `version` must match this page. The registry owns tier ids, concrete Codex
routes, task-type intake defaults, and correctness floors; appsettings must not
redefine them.

The registry also lists canonical model ids permitted as explicit continuation
pins for the `codex` and `claude` runner CLIs. This includes known untiered
models; a tier is not required for an operator pin. The Task Server validates
the CLI id, model id, and their pairing against that list when accepting a
continuation intent. It preserves a valid explicit selection. The static
catalogue does not prove that a particular runner offers the model; execution
still depends on the host CLI. An unknown id is rejected rather than
being silently replaced by a policy recommendation.

A fresh coding claim after a resumed mechanical integration round is qualified
again when the round found a semantic conflict or the deterministic gate failed.
An integration recovery claim with a pending mechanical delta is qualified before
admission at the stronger of its correctness floor and Terra/medium. This also
covers a fresh run when the runner rejects the resume for a missing or stale
session, changed provider, or lineage mismatch after claim. A resumed round may
therefore use the same qualified route. Explicit operator pins retain their
existing policy treatment.
The claim keeps a route that clears the policy floor; otherwise it selects the
registry's provider route at the stronger of the task's correctness floor and
Sol/medium for a semantic conflict, or Terra/medium for a gate failure. The
fresh-run reason is visible in the task's continuation ledger; the selected
model is visible in the run-session event.
When the standalone Task Server issues a required mechanical fresh route for
that claim, the route takes precedence over a continuation intent's route
selection. The continuation instruction still reaches the claimed run. Without
a mechanical route, only fields explicitly submitted in the continuation
override normal route resolution.
The standalone Task Server uses the same versioned policy document for both
direct claims and accepted host permits. A pending mechanical delta gets at
least Terra/medium before the runner checks session admission; a recorded
semantic fallback gets at least Sol/medium on the next fresh claim. Task text
that names a critical boundary raises the route to Sol/xhigh. The claim carries
the selected CLI, model, thinking level, and reason to the runner.

When a new task has no explicit model pin (`modelExplicit=false`), model
qualification starts from this convention:

| Task type | Intake score | Normal tier | Economy mode | Correctness floor | Economy floor |
|---|---:|---|---|---|---|
| Chore | 15 | Luna / medium | Luna / medium | None | None |
| Feature | 51 | Sol / medium | Terra / medium | None | `sonnet-low` |
| Bug | 51 | Sol / medium | Terra / medium | Terra / medium | None (the correctness floor already exceeds `sonnet-low`) |

This is an intake fallback, not permission to ignore better task evidence.
Security boundaries, distributed authority, credible data-loss paths, and
subtle concurrent state machines promote to Sol/xhigh. Public protocols,
persistent-state migrations, and changes spanning three or more runtime
subsystems promote to at least Sol/medium. These promotions become correctness
floors and economy mode cannot lower them.

Added 2026-09-13 (AGT-2808): economy mode's one-step downgrade can never drop
feature (or any coding/concept/planning/research) work below `sonnet-low`
(Sonnet 5, or `gpt-6-sol`, at `low`). Before this floor existed, the
registry's per-vendor model id fallback for an unrecognized catalogue (e.g. a
Claude CLI, since this registry's tiers are keyed to `gpt-5.6-*` ids) picked a
model by position in the catalogue's ranked list; on a Claude catalogue this
silently resolved to `claude-haiku-4-5`, the cheapest entry, with no
guaranteed thinking level (AGT-2793, AGT-2807). Every tier now carries an
explicit per-vendor route (`vendorOverrides` in the JSON) so a known vendor
never falls through to the positional guess, and `Recommend()` is guaranteed
to never return a null thinking level. Haiku-class models remain in use only
on the separate pipeline-support/classification path
(`ModelFamilyResolver`/`PipelineStepModelDefaults`), which never calls this
registry.

An explicit pin is also an execution constraint, not a preference. Before a
local process starts, Studio checks a Claude pin against the model registry's
minimum CLI version and a Codex pin against the installed CLI's live model
catalogue. Remote claim admission applies the same policy to the Runner's
advertised CLI version and Codex catalogue. An unsupported pin stays Ready and
gets the durable `model-unsupported` dispatch reason. For example,
`claude-opus-5-5` requires Claude Code 2.1.281; a host on 2.1.270 reports
`model unsupported by installed CLI 2.1.270 (minimum 2.1.281)` and does not
spawn the CLI.

The create-task UI shows the recommendation, policy version, task type, tier,
and whether economy mode caused a safe one-step downgrade. Choosing a model or
thinking level marks the card explicit in one action. Explicit pins remain
untouched by qualification, while the policy recommendation stays visible for
comparison.

The `auto-tag` creation step is a separate bounded classification route. Its
operator floor is Sonnet-class at low thinking, despite the earlier D5 economy
recommendation, because the 2026-09-13 follow-up judged Haiku-class too small.
It uses the closed tag registry and glossaries, never the card's coding model.
The one-shot call records usage under `auto-tag` in the token ledger. A proposed
80-item golden set measures tier 1; precision below 0.9 selects Sonnet-class
high thinking for item classification until the service restarts. Individual
low-confidence results are retried on Sonnet-class high thinking; only a final
result below the 0.8 confidence threshold remains a proposal. See
[the tagging reference set](../../quality/tagging-golden-set/index.html).

### Create-card contract

`TaskCrudEndpoints`' create handler never persists a card with a model but no
thinking level: when the caller omits `thinkingLevel`, the handler calls
`ModelRoutingPolicyRegistry.Recommend()` and stamps both `model` and
`thinkingLevel` from the same recommendation, with `thinkingLevelExplicit`
mirroring `modelExplicit` (`false` for a policy-derived pick, `true` only when
the caller pinned a level). The model-level badge (`model-level-indicator`)
always renders a level code for any non-human, non-unknown model family, so a
model without a level shows as missing (`?`, dimmed) instead of silently
rendering only the model code.

### Backfill contract

`POST /api/admin/maintenance/backfill-thinking-levels?apply={bool}` (see
`AdminConfigEndpoints`, backed by
`TaskMutationService.BackfillThinkingLevels`) finds cards, including archived
ones, that carry a `model` but no `thinkingLevel` (the pre-AGT-2808 creation
gap; AGT-2793 and AGT-2807 are the reference cases). With `apply=false`
(default query use) it only reports the affected jobs and the level the
policy would resolve for each. With `apply=true` it writes that resolved
level to disk with `thinkingLevelExplicit=false`, since the value is
policy-derived, not an operator pin, and invalidates the job scan cache. Run
the report first and confirm the entry list before applying.

### Hard floors

Apply these after scoring:

- P0, fencing, lease ownership, stale-write rejection, distributed authority,
  security boundaries, and credible data-loss paths require Sol/xhigh.
- A public protocol, persistent-state migration, or change spanning three or
  more runtime subsystems requires at least Sol/medium.
- An unclear bug requires at least Terra/medium even when the expected diff is
  tiny.
- A bounded decision that can itself authorize a destructive, security, or
  lane-affecting action must move from Mini to Sol/medium when its evidence is
  ambiguous or unbounded.
- Quota and cost never lower a hard floor. Prefer an equivalent-capability
  provider fallback, wait for quota, or request an explicit human override.

### Reissue rule

Re-score from the newest evidence. A substantive C/D review or a semantic
reissue sets empirical confidence to `10` and raises the next attempt by at
least one core tier. Do not promote for an environmental failure, stale base,
broken test host, or missing delivery path. Fix that substrate instead.

After two semantic failures at the stronger tier, stop model escalation. Narrow
the task, improve its evidence, or ask for a human decision.

### Benchmark candidate notes

Agent Studio consumes the Token Economy `ModelBenchmarkMatrix.FindCandidates`
query from package version 0.3.6. The benchmark library recommends alternatives;
Agent Studio keeps the selected route unchanged. A candidate can therefore
inform an operator decision but cannot bypass the score ladder, correctness
floors, explicit pins, or quota admission.

For a Ready task, the task API evaluates the stored model and thinking level
against the project's `BenchmarkCapabilityClass`. The default class is
`CodingAgent`. The projected `betterCandidates` note contains the candidate
model and thinking level, benchmark id and name, score and cost deltas, evidence
age and stale flag, evidence snapshot, and the public matrix link. Task detail,
Ready-card responses, the model picker, and Execution Hosts all render this same
payload. Other clients must consume it instead of running their own matrix
query.

Quota admission evaluates the effective route again and writes the note into
the admission decision event. This second boundary matters because a route can
change between board projection and launch. The cache key is model, effort,
benchmark type, and evidence snapshot, so repeated board refreshes reuse the
same library result. Evidence age is calculated at projection time and does not
invalidate the candidate query cache.

The workspace token report attributes each token call to the latest durable
admission boundary for its task. When that boundary carried a matching
candidate note, the report emits a separate project and UTC-week line with
calls, tokens, and theoretical cost. A later admission without candidates
closes the interval.

## Model families and migrations

Added 2026-09-08 (AGT-2716). This section governs a different question from
the routing tiers above: not "which tier of capability", but "which concrete
model id a family currently resolves to", so a supporting-agent default or a
pinned card does not quietly age behind the newest generation the installed
CLI actually offers.

### Families

`ModelFamilyResolver` (`backend/Shared/Models/CliModels.cs`) resolves a family
id to the newest available model, live-catalog first, falling back to the
static registry's declared (newest-first) generation order and
Available/Deprecated flags when discovery has not run yet:

| Family | Members today (newest first) | Resolves to today |
|---|---|---|
| `claude-haiku` | claude-haiku-4-5 | claude-haiku-4-5 |
| `claude-sonnet` | claude-sonnet-5, claude-sonnet-4-6, claude-sonnet-4-5 | claude-sonnet-5 |
| `claude-opus` | claude-opus-5, claude-opus-4-8, claude-opus-4-7, claude-opus-4-6, claude-opus-4-5 | claude-opus-5 |
| `gpt-mini` compatibility id | gpt-5.6-luna | gpt-5.6-luna |
| `gpt-flagship` | detected gpt-6-sol, else gpt-5.6-sol, else gpt-5.5 | alias of the existing Codex detection layer (`ModelMetadataRegistry.DefaultForCli`) |

The explicit-pin registry includes these newly onboarded models. Neither changes
a default route or family resolver:

| Model | Picker availability | Thinking ladder | Context window |
|---|---|---|---:|
| `gpt-6.1-sol` | Codex live discovery | CLI reported `minimal, low, medium, high, xhigh`; default `xhigh` | 1,050,000 |
| `claude-sonnet-5-5` | Claude live discovery at `2.1.284` or later | `low, medium, high, xhigh, max`; default `medium` | 1,000,000 |

Both start unavailable in the registry and use TokenEconomy `0.3.7` prices at
ledger read time.

Every former hardcoded `ModelIds.ClaudeHaiku45` / `ModelIds.Gpt54Mini` runtime
default (`OrchestratorRunner.DefaultModel`, `SummaryGenerationService`,
`TitleGenerationService`, `PromptEnhancementService`, `WikiSearchService`,
`SoftReasoningHostedService`, `CodePatternDriftAnalysisService`,
`ProjectProposalDraftingService`, `GenericCliExecutionService.DefaultOpusModel`,
`WikiMaintenanceModelService.DefaultModel`, `PipelineStepModelDefaults.SupportModel`,
`DriftPostStepRunner.DefaultModel`, `GitService`'s commit-message model, and both
Codex supporting-call defaults in `ReviewDecisionOrchestrator`) now resolves
through this family layer instead of a pinned literal. The `gpt-mini`
compatibility family now resolves to Luna, so no runtime default selects the
retired Mini model. Configuration keys
(e.g. `ClaudeCli:SummaryModel`) still win when an operator sets one - a
configuration pin is an explicit choice and is never overridden.

There is deliberately no Haiku-5 entry: the 2026-09-06 fact check against the
installed Claude Code 2.1.263 `/model` picker found no Haiku generation beyond
4.5. GPT-5.6 Luna replaced GPT-5.4 Mini for cheap supporting calls on
2026-09-11.

### Migration catalog

`backend/Policies/model-migration-catalog.v1.json` (loaded by
`ModelMigrationCatalogRegistry`, served at `GET /api/cli/model-migrations`) is
the versioned list of known-safe "from model -> to model" replacements: same
family, newer generation, `safeAuto: true` when the orchestrator may apply it
without operator confirmation. Today it holds exactly the superseded Opus and
Sonnet generations pointing at `claude-opus-5` / `claude-sonnet-5`; it holds no
Haiku entry. Retired GPT-5.4 Mini is not a migration target or route.
It also proposes `claude-opus-5` to `claude-opus-5-5`, `gpt-5.6-sol` to
`gpt-6-sol`, `gpt-5.6-luna` to `gpt-6-luna`, and `gpt-6-sol` to
`gpt-6.1-sol`. These entries have
`safeAuto: false`, mirroring the TokenEconomy `0.3.6` and `0.3.7` verdicts.
The GPT-6.1 Sol target has a shorter ladder and a lower cached input price;
no qualifying no-regression evidence supports automatic migration. Operators can review
and apply them, while run admission never applies them. A card pinned to the
source model shows the proposal on its model badge. The GPT-6.1 Sol proposal
shows that `max` and `ultra` pins would run at `xhigh` after acceptance. Until
acceptance, the card retains its GPT-6 Sol pin and level. The operator accepts it per
card (`PUT /api/tasks/{id}/model`) or for every explicitly pinned card of a
project in backlog, preparation, orchestrator-prep, or ready
(`POST /api/projects/{project}/model-migrations/apply` with `{ "from": ... }`).
Running, reviewed, and finished cards are never rewritten, and a non-explicit
card follows the policy default instead. The release dates and model ids are documented in the
[Anthropic release notes](https://platform.claude.com/docs/en/release-notes/overview#september-22-2026)
and [OpenAI API changelog](https://developers.openai.com/api/docs/changelog).

This catalog is currently an interim Studio-owned copy, following the same
replaceable-seam posture as `IModelEconomyAdvisor` and the TokenEconomy pricing
adapter (`ITokenPriceProvider`): `IModelMigrationCatalogSource` is the data
seam, `EmbeddedModelMigrationCatalogSource` is the built-in implementation, and
a Token Economy package can implement the same contract once it ships a
migration-catalog artifact of its own, without changing
`ModelMigrationCatalogRegistry` or any call site. Studio does not vendor or
modify the separate TokenEconomy repository as part of this policy; TE
ownership of this catalog's actual content is a follow-up integration, not
implemented by AGT-2716.

### Where a superseded model surfaces, and who may touch it

| Surface | Visible when superseded | Applied automatically | Explicit pin protection |
|---|---|---|---|
| Task/card pinned model (`modelExplicit == true`) | Card model badge shows "update available" with a one-click apply | Never | Always - the orchestrator only rewrites `modelExplicit == false` models |
| Task/card model when `modelExplicit == false` | N/A - already resolves live via `ModelQualificationService`; a stale stored literal self-heals at run admission | Yes, when the workspace switch is on and the catalog entry is `safeAuto` | N/A |
| Project pipeline-step override | Pipeline settings row shows "update available" with a one-click apply | Never - an explicit project choice | Always |
| Configuration pin (e.g. `ClaudeCli:SummaryModel`) | Not surfaced by this policy; an operator-edited config key is out of the runtime settings surface | Never | Always |

Automatic application happens at run admission
(`ProjectRunner.ApplyAutoModelMigration`, decided by the pure
`ModelMigrationPolicy.DecideAutoMigration`): a non-explicit task whose stored
`Model` matches a `safeAuto` catalog entry is rewritten, the change is
persisted to `task.json`, and a `model_migrated` timeline event records
`fromModel`, `toModel`, `family`, and the catalog version that authorized it.
A workspace can turn this off entirely with
`PUT /api/workspaces/{id}/auto-apply-model-migrations`
(`WorkspaceSettings.AutoApplyModelMigrations`, default on); the switch never
affects the visibility of the "update available" offer, only whether it can
apply itself.

## Benchmark basis

AGT-2243 produced `results/model-benchmark.md` and
`results/model-benchmark.json` from a read-only snapshot on 2026-07-23. The
snapshot found 152 task records across nine projects, included 121 records with
run evidence, and formed 19 model, thinking-level, and task-type cohorts.

The report is observational history, not a controlled benchmark:

- Grade coverage was 33.9%.
- Duration coverage was 91.7%.
- Token coverage was 36.4%.
- A record retains only its final model and thinking level, so attempts cannot
  be split when a card changed route.
- Reissues also reflect task quality, gate defects, stale bases, and historical
  orchestrator behavior.

These are the policy-relevant aggregates:

| Cohort | Runs | Known grade result | Reissue result | Policy reading |
|---|---:|---|---|---|
| Sol/medium, chores and features | 7 | 5 known, all A/B | 0/7 | Supports Sol/medium as the demanding-work sweet spot. |
| Sol/high, chores and features | 6 | 5 known: A2, B2, C1 | 0/6 | Favorable but too small to justify a separate default tier. |
| Sol/xhigh, all task types | 78 | 22 known: A2, B2, C2, D16 | 32/78 | Strong selection bias and pipeline churn. Keep it as a risk floor, not a blanket default. |
| Terra/medium, chores and features | 8 | 0 known | 0/8; records were backlog or progress | Insufficient terminal evidence. Terra remains provisional. |
| Mini/medium, chores and features | 2 | Both B | 0/2 | Too small and the wrong role to validate Mini for core implementation. |
| Claude Sonnet 5/high, features | 4 | 3 known, all A/B | 0/4 | A reasonable equivalent-provider signal when Codex quota is constrained, still with a small sample. |

The single Sol/medium bug record had an unknown grade and was reissued, so it
does not support a bug-quality conclusion. Token coverage is also too low to use
the reported token medians as routing thresholds.

There was no Luna cohort. AGT-2200 had not run and its 2026-07-23 scope update
moved controlled model comparisons to the Token Economy A/B harness. Therefore
the Luna and Terra tiers must remain visibly provisional until fresh, identical
scenario runs exist.

## Five historical cards

The score below is the route that would have been chosen at intake from the
card text. The observed route and later outcome are evidence, not inputs
silently used to rewrite the initial estimate.

| Card | Risk | Scope | Context | Type | Empirical | Quota | Initial route | Why |
|---|---:|---:|---:|---:|---:|---:|---|---|
| AGT-2241, remove the chat paperclip control while preserving paste | 0 | 0 | 0 | 0 | 10 | 5 | `15`, Luna/medium | A local mechanical removal with a named regression spec. Luna was unvalidated, so all uncertainty points remain. |
| AGT-2268, copy the task key from detail and board surfaces | 12 | 8 | 8 | 6 | 10 | 5 | `49`, Terra/medium | Reversible UI behavior across two surfaces, clipboard interaction, feedback, and Playwright proof. Its later semantic reissues would promote the next attempt to Sol/medium under the reissue rule. |
| AGT-2249, align pipeline settings rows and expose all step toggles | 12 | 8 | 8 | 6 | 6 | 5 | `45`, Terra/medium | A standard frontend feature in one subsystem with several related components and an explicit visual test. Later semantic reissues would promote it to Sol/medium. |
| AGT-2243, aggregate model history across storage variants | 12 | 14 | 20 | 6 | 10 | 5 | `67`, Sol/medium | The source change is a script, but correctness depends on broad task-schema history, legacy fields, lane semantics, idempotency, and data-quality interpretation. The observed Terra run required reissues and ended grade D, which is consistent with choosing a stronger initial route, not proof of causality. |
| AGT-2182, persist restart-safe RunAttempt and ReviewAttempt fencing | 35 | 20 | 20 | 10 | 10 | 5 | `100`, Sol/xhigh | P0 distributed authority, stale-write rejection, leases, idempotency, restart behavior, and many interacting runtime paths trigger the hard floor independently of quota. |

For every one of these cards, bounded supporting aspect and orchestrator calls
may still use Luna/high. The table selects the core implementation route.

## Quota and provider handling

1. Establish the correctness floor and score before consulting quota.
2. If the preferred model is available, use the scored route.
3. If a quota window is near its cap, first select a benchmark-supported,
   equivalent-capability provider route. Record that fallback in the run.
4. Downgrade one core tier only when the score is within five points of the
   lower threshold, no hard floor applies, and verification is deterministic.
5. If no safe route is available, wait or ask for an explicit override. Never
   silently spend correctness margin.

Quota state is run-scoped. The workspace economy switch changes qualification
and new-card suggestions without rewriting existing cards or turning a
suggestion into an explicit pin. The decision log must retain the policy
version, recommended tier and route, selected route, selection source, score,
economy state, correctness floor, and reason.

Step 3's "equivalent-capability provider route" is code, not operator
judgment, as of AGT-2751. [CliQuotaFallbackService](../../../backend/Features/Cli/Quota/CliQuotaFallbackService.cs)
derives it from [IModelEquivalenceCatalog](../../../backend/Features/Cli/Quota/ModelEquivalenceCatalog.cs)
when no explicit `cli-model-routing.json` fallback is configured, and the
application-wide [QuotaAdmissionService](../../../backend/Features/Cli/Quota/QuotaAdmissionService.cs)
applies the same switch, throttle, or wait decision before every execution path
that launches a coding-agent CLI. The exhaustive path-to-source and
path-to-test map is the CLI domain's
[quota admission coverage](cli.md#quota-admission-coverage), and the
load-bearing boundary is recorded in
[ADR-0069](../architecture/decisions/adr-archive.md#adr-0069---quota-admission-is-one-run-scoped-boundary-for-every-cli-execution-path-2026-09-08).

The equivalence adapter reads Token Economy's published, embedded model-routing
knowledge base and price catalogue from the exactly pinned NuGet package. It
selects another provider only within the same capability class, excludes
unqualified and retired routes, and admits Codex routes only when their model
appears in this repository's versioned routing tiers. That local policy gate
keeps a TokenEconomy price or knowledge update from promoting a newly listed
model into automatic quota fallback before Studio adopts the route. It
preserves a supported explicit thinking level,
and chooses the cheapest remaining comparable route. The package version and
its independent routing-policy version are recorded together. Admission makes
no network call. An operator-configured pair remains an explicit override and
wins over catalogue selection.

With the current catalogue, Claude Opus 5/high maps to Sol/high, Claude Sonnet
5 with no stronger pin maps to Sol/medium, and the Haiku class maps to
Luna/medium. GPT-6 Astra remains unqualified for automatic selection under this
policy. GPT-5.4 Mini is retired and never appears as a route. If no candidate
preserves the capability and thinking floor, admission follows the existing
wait policy instead of downgrading.

When the catalogue has no route for a proposal-only successor, the lookup uses
its predecessor from the migration catalog: Claude Opus 5.5 resolves through
Opus 5 and GPT-6 Sol through GPT-5.6 Sol at the requested thinking level. The
route still names the requested model. Proposal-only successors are never
automatic fallback targets, even when Token Economy marks them selectable and
cheaper. This is a comparable-model lookup, not an automatic migration.

The per-CLI **prefer fallback now** switch is a runtime admission input. It
routes only new runs, continuations, remote claims, review commands, chats, and
one-shots; it never interrupts running work. The preference expires at the next
known reset for that CLI. A hard cap triggers the same selection automatically.
Every switch emits the shared `modelFallback` receipt:

```json
{
  "from": "claude-opus-5 high",
  "to": "gpt-5.6-sol high",
  "reason": "quota-cap",
  "window": "Weekly",
  "usedPct": 98,
  "catalogueVersion": "TokenEconomy 0.3.5; routing 2026-09-24"
}
```

`reason` is `quota-cap`, `operator-preference`, or `provider-rejection`.
`catalogueVersion` is set only for catalogue-selected routes; operator overrides
leave it null so their receipts do not claim catalogue provenance.
Provider-rejection continuations use the same record type. Remote runners never
select a fallback independently: the task server records the admission decision
and returns the effective CLI, model, and thinking level in the claim plan.

The provider quota probe is the only listed non-reroutable caller. It runs the
provider's native introspection command and does not invoke a model. All model
calls, including summaries, enrichment, drift, pipeline/review aspects, local
and remote task execution, and orchestrator chat, pass through
`QuotaAdmissionService` directly or through the quota-aware `ICliOneShot`
registry.

### Provider request refusals

A provider-side HTTP 400, 403, or 404 model-request refusal is distinct from
quota and authentication. The versioned policy document declares same-provider
sibling routes for this condition: `gpt-6-astra` and `gpt-6-sol` to
`gpt-5.6-sol`, `gpt-6-luna` to `gpt-5.6-luna`, Claude Opus 5.5 to Opus 5,
Anthropic Opus 5 to Opus 4.8, and Anthropic Sonnet 5 to Sonnet 4.6. The
continuation keeps the original thinking level and must still clear the card's
correctness floor.

The first refusal uses a run-scoped `modelFallback` and leaves the card's model
unchanged. A second refusal of the same configured model pins the card to the
sibling and records that change. A missing sibling, a sibling below the floor,
or a refusal of the sibling escalates with the provider message; none returns
the card to Ready on the refused model. Run history stores `{from, to, reason}`,
and Execution Hosts aggregates refusals per model per UTC day.

## Roadmap: what happens next

1. **Policy visible now.** This page is canonical, linked from the documentation
   index and domain maps, and referenced by runner and orchestrator prompts.
2. **Historical benchmark becomes repeatable.** Land the AGT-2243 aggregation
   script, publish dated snapshots, retain per-cohort sample coverage, and split
   attempts by their actual route once attempt-level history supports it.
3. **Controlled comparisons move to Token Economy.** TE-10 runs identical,
   deterministic scenarios across Luna, Terra, Sol reasoning levels, Mini, and
   equivalent provider fallbacks. AGT-2200 now remains focused on remote-run
   infrastructure verification.
4. **Confidence gates replace hypotheses.** Luna and Terra become validated
   defaults only after enough controlled runs meet declared correctness,
   reissue, duration, and token thresholds. Until then the UI labels them
   provisional.
5. **Automation follows evidence.** Align `ModelQualificationService` and the
   Token Economy advisor with this score, hard floors, quota rule, and
   reissue behavior. Emit the complete worksheet in
   `model-qualification.jsonl`.
6. **Quarterly calibration.** Recompute the benchmark, inspect cohort drift,
   review false promotions and unsafe downgrades, and version this page when a
   threshold or default route changes.

## Related system contracts

- [Pipeline domain](pipeline.md)
- [CLI domain](cli.md)
- [Token aggregation](tokens.md)
- [Quota snapshot run events](../../concepts/quota-snapshot-run-events.md)
- [Model qualification event schema](../../app/schemas/model-qualification-event.schema.json)
