# Model Routing Policy

Version: 2026-09-13

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
| `gpt-5.6-luna` / `medium` | Trivial, mechanical, locally specified changes with a small expected diff and an obvious verification path. Examples: remove one control, rename a local label, update a narrow fixture. | Unclear bugs, cross-subsystem behavior, public contracts, migrations, security, concurrency, or distributed state. | No Luna cohort existed in the 2026-07-23 benchmark. This is therefore a cost-saving hypothesis, not a validated quality claim. The empirical uncertainty adds points and keeps borderline work on Terra. |
| `gpt-5.6-terra` / `medium` | Standard features, content, and reversible UI or service changes inside one subsystem. This is the default sweet spot when requirements and test seams are clear. | P0 work, fencing, distributed authority, data-loss paths, or changes that require broad architectural reconstruction. | The historical report contained eight Terra/medium records, but none had a known grade and none formed a trustworthy terminal cohort. Keep Terra as the working default, but promote on substantive reissue until controlled data validates it. |
| `gpt-5.6-sol` / `medium` | Demanding implementation, investigation, or analysis with several interacting concepts, a broad context search, or two to three subsystems. | Correctness-critical control-plane work that meets a hard floor. | Sol/medium had seven standard chore/feature runs with zero reissues. Five had known grades and all five were A or B. This is the strongest favorable historical signal, although the sample is still small and observational. |
| `gpt-5.6-sol` / `xhigh` | Correctness-critical work: P0, fencing, leases, distributed authority, security boundaries, destructive migrations, data-loss prevention, or subtle concurrent state machines. | Routine work merely because quota is available. More thinking is not a substitute for tighter scope or deterministic tests. | The xhigh cohort was heavily selected for difficult and incident-driven work: 78 runs, 32 reissued, with only 22 known grades. Its high reissue rate is a warning about cohort and pipeline churn, not proof that xhigh causes poor outcomes. This tier is selected by the correctness floor while controlled benchmarks remain open. |
| `gpt-5.4-mini` / `high` | Bounded orchestrator and supporting-pipeline decisions over compact, structured evidence, with a deterministic output contract. Examples: aspect verdicts, the final route decision, and post-abort classification. | Core code implementation, open-ended architecture, ambiguous product decisions, or a context set too large to fit in the bounded decision prompt. | The historical task benchmark had only two Mini/medium task records, both grade B and neither reissued. That does not validate Mini for core tasks. The `high` pipeline route instead follows the existing bounded-support contract in `PipelineStepModelDefaults`; use a stronger tier when the decision itself is correctness-critical or unbounded. |

`high` and `ultra` are supported reasoning levels but are not default core-task
routes in this policy. Add a default tier only after controlled comparisons
show a repeatable benefit over `medium` or `xhigh`.

`gpt-6-astra` is **not yet tiered**. It is onboarded as a known model so the
picker can offer it (or explain its absence) when the installed codex-cli lists
it, but it has no routing tier, is not the product default, and has no cohort
in the benchmark below. Whether it becomes a tier or the default is a separate
operator decision; until then it is selectable only as an explicit pin, and an
explicit pin is not evidence that it clears any correctness floor.

`gpt-5.6-luna`, `gpt-5.6-terra`, and `gpt-5.4-mini` above were already this
policy's routing tiers, but until AGT-2707 round 2 they had no
`ModelMetadataRegistry` entry, so a codex-cli that did not offer one left it
silently invisible in the picker instead of disabled-with-a-reason. They are
now registry entries for that catalog-visibility reason only: their routing
tiers, reasoning ladders, and defaults above are unchanged, and (unlike
`gpt-5.4-mini`) `gpt-5.6-luna`/`gpt-5.6-terra`'s registry `Available` baseline
is deliberately false so a total CLI-probe failure still never assumes a
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
| `0-20` | Luna / medium |
| `21-50` | Terra / medium |
| `51-69` | Sol / medium |
| `70-100` | Sol / xhigh |

The Mini route is a role exception, not the bottom rung of the core-task
ladder. Select it only when the call is a bounded pipeline decision with
structured evidence and a parseable output contract.

### Automated card convention

The machine-readable registry is
[`backend/Policies/model-routing-policy.v1.json`](../../../backend/Policies/model-routing-policy.v1.json).
Its `version` must match this page. The registry owns tier ids, concrete Codex
routes, task-type intake defaults, and correctness floors; appsettings must not
redefine them.

When a new task has no explicit model pin (`modelExplicit=false`), model
qualification starts from this convention:

| Task type | Intake score | Normal tier | Economy mode | Correctness floor |
|---|---:|---|---|---|
| Chore | 15 | Luna / medium | Luna / medium | None |
| Feature | 25 | Terra / medium | Luna / medium | None |
| Bug | 49 | Terra / medium | Terra / medium | Terra / medium |

This is an intake fallback, not permission to ignore better task evidence.
Security boundaries, distributed authority, credible data-loss paths, and
subtle concurrent state machines promote to Sol/xhigh. Public protocols,
persistent-state migrations, and changes spanning three or more runtime
subsystems promote to at least Sol/medium. These promotions become correctness
floors and economy mode cannot lower them.

The create-task UI shows the recommendation, policy version, task type, tier,
and whether economy mode caused a safe one-step downgrade. Choosing a model or
thinking level marks the card explicit in one action. Explicit pins remain
untouched by qualification, while the policy recommendation stays visible for
comparison.

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
query from package version 0.3.4. The benchmark library recommends alternatives;
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
| `gpt-mini` | gpt-5.4-mini | gpt-5.4-mini |
| `gpt-flagship` | detected gpt-5.6-\* else gpt-5.5 | alias of the existing Codex detection layer (`ModelMetadataRegistry.DefaultForCli`) |

Every former hardcoded `ModelIds.ClaudeHaiku45` / `ModelIds.Gpt54Mini` runtime
default (`OrchestratorRunner.DefaultModel`, `SummaryGenerationService`,
`TitleGenerationService`, `PromptEnhancementService`, `WikiSearchService`,
`SoftReasoningHostedService`, `CodePatternDriftAnalysisService`,
`ProjectProposalDraftingService`, `GenericCliExecutionService.DefaultOpusModel`,
`WikiMaintenanceModelService.DefaultModel`, `PipelineStepModelDefaults.SupportModel`,
`DriftPostStepRunner.DefaultModel`, `GitService`'s commit-message model, and both
Codex supporting-call defaults in `ReviewDecisionOrchestrator`) now resolves
through this family layer instead of a pinned literal. Configuration keys
(e.g. `ClaudeCli:SummaryModel`) still win when an operator sets one - a
configuration pin is an explicit choice and is never overridden.

There is deliberately no Haiku-5 or newer gpt-mini entry: the 2026-09-06 fact
check against the installed Claude Code 2.1.263 `/model` picker found no
Haiku generation beyond 4.5, and whether a cheap pipeline step should leave
the gpt-mini family for Sonnet is a Token Economy cost decision, not a
family-generation rule.

### Migration catalog

`backend/Policies/model-migration-catalog.v1.json` (loaded by
`ModelMigrationCatalogRegistry`, served at `GET /api/cli/model-migrations`) is
the versioned list of known-safe "from model -> to model" replacements: same
family, newer generation, `safeAuto: true` when the orchestrator may apply it
without operator confirmation. Today it holds exactly the superseded Opus and
Sonnet generations pointing at `claude-opus-5` / `claude-sonnet-5`; it holds no
Haiku or gpt-mini entry for the reason above.

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
may still use Mini/high. The table selects the core implementation route.

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

The interim equivalence table covers only the tiers already named above: Codex
Sol/high with Claude Opus 5/high, and Codex Mini/high with Claude Sonnet
5/medium. If quota fallback is needed, an explicit model or thinking pin that
has no exact cross-family equivalence does not fall through to a weaker route;
admission waits. Explicit operator fallback overrides remain available and win
over the derived table.
Token Economy's `model-migration-catalog-safe-auto-rules` is the eventual
system of record for equivalence tiers. A nearby quota reset is only worth a
quiet wait when the run is cheap, with no explicit
`high`/`xhigh`/`ultra`/`max` reasoning pin. An expensive run switches
immediately when the equivalent provider has headroom, and waits when no route
can preserve its floor.

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
