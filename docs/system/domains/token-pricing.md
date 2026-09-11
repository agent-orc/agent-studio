# Token pricing

> **Status (2026-09-08):** Live on exactly pinned `TokenEconomy` 0.3.3,
> including historical prices for the GPT-5.6 family, the GPT-5.5 family
> (GPT-5.5, GPT-5.5 Pro, the GPT-5.5 Cyber preview), and the Claude 5 /
> Sonnet 4.6 family. Studio contains no model rates.

## Contract

Every Studio cost calculation goes through
`backend/Features/Runner/TokenPricing.cs`. The package-specific implementation
in `backend/Features/Runner/TokenEconomyPriceProvider.cs` adapts
`TokenEconomy.ModelPriceCatalog.Default` and its historical pricing API.

The adapter is exposed through `ITokenPriceProvider`; the active
`TokenPricing.Provider` configuration selects `TokenEconomyPriceProvider`, the
package-specific implementation from the exactly pinned `TokenEconomy` 0.3.3
dependency. Aggregators and frontend contracts do not depend on the provider
package directly.

`TokenPricing.CanonicalModelId` resolves a persisted model id, catalog alias,
or catalog display name back to the catalog's canonical model id. The
workspace-wide fold (`TokenSummaryService.AggregateSummaries`) uses it to key
each `byModel` row, so a receipt that persisted a display name (before the
AGT-2740 persistence fix) and a receipt that persisted the canonical id (after
it) still fold into one row instead of splitting the same model's usage across
two. `GET /api/token-pricing/unpriced-models` lists every distinct model id
currently active in recorded usage that the pinned catalog does not recognize
at all, so a catalog gap is visible instead of silently rendering "Unknown".

Callers must supply the run or event timestamp. TokenEconomy selects the catalog entry
whose `ValidFrom` applies at that time. Repricing old usage with today's rate is
not allowed. Model metadata in `CliModels.cs` exposes current rates only as a
catalog pass-through for discovery consumers; it owns no price numbers.

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
table.

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
