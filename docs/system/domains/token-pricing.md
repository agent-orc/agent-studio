# Token pricing

> **Status (2026-10-11):** Live on exactly pinned `TokenEconomy` 0.3.7,
> including historical prices for the GPT-5.6 family, the GPT-5.5 family
> (GPT-5.5, GPT-5.5 Pro, the GPT-5.5 Cyber preview), and the Claude 5 /
> Sonnet 4.6 family. Studio contains no model rates.

## Contract

All historical cost callers use injectable `ITokenPricing`, implemented by
`TokenPricingService` in `backend/Features/Tokens/Pricing/TokenPricing.cs`.
The package-specific implementation in
`backend/Features/Tokens/Pricing/TokenEconomyPriceProvider.cs` adapts
`TokenEconomy.ModelPriceCatalog.Default` and its historical pricing API.

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
returns model ids. GPT-4.1 and GPT-4o are absent from the curated CLI
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
