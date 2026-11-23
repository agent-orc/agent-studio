# Token pricing

> **Status (2026-11-18):** Live on exactly pinned `TokenEconomy` 0.3.9,
> including historical prices for the GPT-5.6 family, the GPT-5.5 family
> (GPT-5.5, GPT-5.5 Pro, the GPT-5.5 Cyber preview), and the Claude 5 /
> Sonnet 4.6 family. Dated Fable 5.2 and GPT-6.2 seeds supplement this
> version until Token Economy TE-82 is available in the pinned package.
> Studio contains no model rates. Haiku 5.5 aliases resolve through the
> shared model registry; the catalog owns its historical prices and
> prompt-size tiers.

## Contract

All historical cost callers use injectable `ITokenPricing`, implemented by
`TokenPricingService` in `backend/Features/Tokens/Pricing/TokenPricing.cs`.
The package-specific implementation in
`backend/Features/Tokens/Pricing/TokenEconomyPriceProvider.cs` adapts
`TokenEconomy.ModelPriceCatalog.Default` and its historical pricing API.

Fable 5.2 uses the published catalogue entry when present. With 0.3.7,
`TokenEconomyPriceProvider` adds one local listing effective 2026-11-09,
verified 2026-11-17: USD 8 input, 0.20 cache read, 40 output, and 10 for
5-minute cache writes per million tokens. It carries the provider source URL
and retains `NoPriceForDate` before release. The provider publishes fast mode
at USD 16 input / 80 output per million tokens; Studio's existing ledger is a
Standard API list-price estimate and does not infer fast mode from a model ID.
Fable 5.2 has a 1M context window and 128K maximum output.
[Provider pricing](https://platform.claude.com/docs/en/about-claude/pricing).

GPT-6.2 uses the published catalogue entry when present. TokenEconomy 0.3.7
(and the newer 0.3.8 and 0.3.9) carry none, so `TokenEconomyPriceProvider`
adds one local listing per model effective 2026-11-17, verified 2026-11-18
against the [provider pricing page](https://developers.openai.com/api/docs/pricing).
Per million tokens, standard short-context tariff (up to 272K input):

| Model | Input | Cached input | Cache write (30 min) | Output |
|---|---:|---:|---:|---:|
| `gpt-6.2-sol` | 2.00 | 0.10 | 2.50 | 10.00 |
| `gpt-6.2-astra` | 8.00 | 0.40 | 10.00 | 40.00 |

The long-context tariff above 272K input (Sol 4 / 0.20 / 5 / 15, Astra
16 / 0.80 / 20 / 60) is recorded in the price note and not priced, as for
GPT-6.1 Sol. Ultrafast service tiers (3x the standard rate until 2027-02-28)
are not inferred from a model id. Each note also states the Codex credit rate,
list price x `TokenEconomyPriceProvider.CodexCreditsPerUsd` (25). Studio has no
Codex credit meter, so the rate is carried on the price basis only. Calls
before 2026-11-17 stay `NoPriceForDate`.

The adapter is exposed through `ITokenPriceProvider`. `AddTokenUsage` registers
one singleton provider and one singleton `ITokenPricing` service. There is no
static pricing facade or independently constructed runtime pricing instance.
`TokenEconomyPriceProvider` is the only production class that reads the price
catalog or calls its historical cost API. Aggregators and frontend contracts do not depend on the provider
package directly.

`ModelIdentity.Resolve` owns the model id, display label, and CLI projection for
catalog ids, aliases, display labels, and bare `sonnet` / `opus` aliases.
`ITokenPricing.Estimate` resolves through it before calling TokenEconomy.
Per-project, workspace, and ad-hoc `byModel` rows carry `modelId` and `cli`;
`model` is a display label. Historical receipts that stored labels resolve on
read, without changing `task.json`. `GET /api/token-pricing/unpriced-models`
returns ids for models with an unpriced call in the current workspace calendar
week, including `NoPriceForDate` and attributed or unattributed workspace
one-shot receipts. Its `weekStartUtc`, `weekEndUtc`, and `timeZone` describe the
same period as the usage cost detail. Row calls and tokens include only the
unpriced receipts, even when that model has priced calls in the same week.
GPT-4.1 and GPT-4o are absent from the curated CLI
registry because the pinned catalog has no listings for them; old usage
remains explicitly unpriced.

Callers must supply the run or event timestamp. TokenEconomy selects the catalog entry
whose `ValidFrom` applies at that time. Recorded aggregates never reprice old
usage with today's rate. Model metadata in `CliModels.cs` exposes current rates
only as a catalog pass-through for discovery consumers; it owns no price numbers.

TokenEconomy distinguishes `Resolved`, `UnknownModel`, and `NoPriceForDate`.
Studio maps only `Resolved` to a dollar value. The other states keep the token
count and set the existing unknown-price flags so UI surfaces render an explicit
missing-price state, never a silent `$0.00`.

Active `UnknownModel` usage also increments `unknownModelCount` in the project
Token Summary, renders an acute `N models without price data` badge, and emits
one warning per project and model per backend process. `NoPriceForDate` remains
explicitly unpriced but does not trigger the catalog-drift signal because the
model id is present in the pinned catalog.

Pipeline cost contracts also carry `unpricedRuns` and grouped `pricingGaps`
with the original display model id, resolver status, and affected-run count.
An entirely unpriced non-empty amount renders `- no price data`; a mixed
aggregate renders its priced subtotal plus
`incomplete (n runs without price)`. `$0.00` is reserved for zero-token usage.
Tooltips expose the model id and resolver reason, including `NoPriceForDate`.

Missing token telemetry is a different state from missing catalog pricing.
Pipeline run totals carry `tokenUsageAvailable`, and the task aggregate carries
`missingTokenRuns`. When some visible runs have ledger calls, the UI renders the
priced recorded subtotal plus `incomplete (n runs without usage)`. When no
visible run has telemetry, token and cost values render `-` / `- no usage data`,
never `$0.00`. A recorded zero-token call remains a genuine `$0.00` value.

Ledger rows that were persisted before a model entered the catalog are checked
again against the historical catalog on read. Once a later TokenEconomy release
resolves the model at the original call timestamp, Studio uses that historical
estimate and removes the missing-price marker. Already-priced ledger amounts
remain authoritative.

## Cost surfaces

The shared adapter feeds:

- task-detail pipeline step, run, model, and total costs;
- code-review and quality-grade rows from their generated-file usage
  provenance;
- project pipeline cost columns and time series;
- project expensive-jobs, heatmap, and drill-down aggregates;
- workspace token timeline and Token Summary;
- CLI Usage modal and detail table, including "Cost per model";
- board task and project token badges;
- ad-hoc/supporting usage summaries.
- prompt-registry call history, where the rendered prompt's estimated input
  tokens are priced at the event timestamp and grouped by content hash.
- orchestrator decision receipts (AGT-3063), where the deduplicated usage of a
  shadow decision call is priced at its completion time and the card run
  ledger in the decision grounds is priced row by row. Both keep
  `unknown-model` and `no-price-for-date` with a null amount, never zero; see
  [orchestration decisions](../contracts/orchestration-decisions.md).

These surfaces may aggregate resolved costs, but an aggregate containing an
unpriced call remains explicitly marked unavailable or incomplete according to
its wire contract. A per-model row with a missing price always renders
`no price data`.

The workspace quota meter and footer cost projection carry unpriced receipt
counts beside their priced subtotals. The quota token count still includes those
receipts. Partial markers link to Workspace Settings > Token usage, where owners
can request the active unpriced-model list with calls and tokens. The Card
analysis By model row renders `no price data` for an unpriced model.

Prompt-registry cost is a narrower estimate than a completed model call. It
prices only the rendered prompt input, uses the existing four characters per
token estimator, and has no output or cache counters. Its UI must retain the
theoretical API-equivalent and CLI-subscription disclaimer and must mark
unpriced calls instead of treating them as zero-cost calls.

## Calculation transparency

`POST /api/token-pricing/calculate` accepts up to 100 model/token/timestamp
items and returns the exact historical catalog entry used for each item:
input, output, cache-read, and cache-write rates per million tokens, component
costs, currency, price source, and effective date. The shared frontend cost
breakdown dialog is the only renderer for this contract. Cost launchers pass
the recorded run timestamp where one exists so the dialog and aggregate use
the same historical period.

The dialog is mounted once at app level and opened through
`CostBreakdownService` / `CostBreakdownTriggerDirective`. New theoretical-cost
surfaces must use that shared path instead of adding a local modal or a price
table. Aggregate rows have no individual event dates, so their dialog explicitly
labels the current-rate reprice and shows the recorded historical cost beside it.

Compact token displays use `buildTokenCostTooltip` from the frontend tokens
feature. The helper owns USD formatting, the mandatory estimate caveat, partial
aggregate wording, and the explicit `no price data` fallback. Review rows,
task-detail pipeline totals, project Pipeline `Tokens / 90d`, and board badges
must use this helper instead of composing local tooltip strings.

## Verification

`backend.Tests/TokenPricingTests.cs` pins the provider adapter to the
TokenEconomy catalog, including alias normalization, unknown-model behavior,
no-price behavior, selection by run timestamp, and the configured provider
identity. The adapter also preserves the four effective rates, source, and
effective date returned to the shared calculation modal. Aggregator tests cover
the shared consumers.

## Updating the exact package pin

1. Verify that the intended published TokenEconomy release contains prices and `ValidFrom` history for every newly active model family.
2. Update the exact `TokenEconomy` `PackageReference` and the public-package check in `scripts/check-public-docs.mjs` to the same version.
3. Restore and build the backend so any `ITokenPriceProvider` or `TokenEconomyPriceProvider` API change fails at the adapter boundary.
4. Run `TokenPricingTests` plus the token-summary and pipeline-cost regressions, including a historical timestamp for each newly priced family.
5. Run the focused task-detail Playwright proof and preserve light- and dark-theme screenshots when aggregate rendering changes.

## Diagnostic HTTP reads (AGT-3952)

Project token summaries and the unpriced-model diagnostic share a bounded,
single-flight read cache. Requests wait up to five seconds. A scan that is
still running returns HTTP 503 with a JSON Problem Details body; retrying
joins the same scan. Successful results remain cached for thirty seconds.
The unpriced-model key includes the visible project set, so restricted
callers never reuse another permission scope's result. Failures are evicted
and can be retried. Neither a slow scan nor an empty model list produces an
empty success body.

Prompt-size tariffs require the size of one request's prompt. The pricing
adapter can safely price an aggregate below the catalog's first tier because
all its constituent prompts fit the base tariff. A larger aggregate without
request-size evidence remains `AmbiguousPromptSize`. The pricing calculation
endpoint accepts optional `promptTokens` for a known request and reports the
applied tariff in `priceBasis`; it does not use a session total as prompt length.

Catalog updates also carry routing knowledge. Automatic Codex equivalence
candidates remain restricted to Studio's versioned routing tiers and declared
provider-rejection fallbacks; a new model in TokenEconomy does not grant it
an automatic Studio route.

## Receipt evidence gaps (AGT-3973)

Scalar price lookup still resolves the historical catalog price. Receipt-aware
pricing additionally requires evidence for the input dimensions: OpenAI
receipts with positive input, zero cache read and no normalization provenance
are unpriced with `IncompleteReason = "missing-cache-split"`. Counters stay
visible, and saved estimated costs cannot override this evidence gap. A known
zero cache count with explicit parser evidence is priceable. For remote
receipts, the legacy input-semantics boolean alone does not prove that the
cache field was reported; zero-cache rows require the versioned marker. No cache
ratio is inferred from neighboring runs or rounded USD estimates. See the
[token domain](tokens.md#remote-receipt-replay-and-missing-cache-evidence-agt-3973).

Pricing fixtures that expect an OpenAI receipt to be priced must declare their
known input semantics or normalization provenance. Project and per-card
summaries keep a cataloged model known when cache evidence is absent, but
mark its cost unpriced. `TokenSummaryTests` covers missing evidence alongside
explicit zero-cache input semantics.
