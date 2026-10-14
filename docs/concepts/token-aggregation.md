---
id: token-aggregation-concept
title: "Token aggregation pipeline"
status: active
category: concept
updatedAt: 2026-10-13
last-updated: 2026-10-13
reason: "Record the AGT-3037 read, identity, price, fold, and projection stages"
taskKey: AGT-3037
tags: [token-aggregation, hybrid-projection, task-receipt, cost]
related-tasks: [ASS-881, ASS-1717, AGT-2542, AGT-2624, AGT-2626]
related-adrs: []
related-docs:
  - "docs/system/domains/tokens.md"
  - "docs/system/domains/token-pricing.md"
---

# Token aggregation pipeline

Token usage has one runtime composition through `ITokenAggregator` and `TokenAggregationService`.

| Stage | Owner | Responsibility |
|---|---|---|
| Read | `ProjectTokenLedger`, `ProjectTokenReceiptReader`, `BusAdHocUsageReader` | Merge historical bus rows, durable task receipts, chat fallback receipts, and ad-hoc bus records. Preserve source freshness. |
| Normalize identity | `ModelIdentity` | Resolve stored model ids, display labels, and CLI attribution. |
| Price | `TokenPricingService`, `TokenEconomyPriceProvider`, `RunModelPricing` | Estimate at the recorded timestamp and handle the existing run-model fallback before folding. |
| Aggregate | Folds in `Features/Tokens/Aggregation/` | Sum priced entries into one model bucket type, project/job summaries, and workspace timelines. |
| Project | `TokenAggregationService` and endpoints | Compose stages, cache responses, and expose existing DTOs. |

Folds receive priced entries and never call a pricing provider. Cost consumers elsewhere, including `PipelineCostCalculator`, receive `ITokenPricing`. The provider package is referenced in one production class, `TokenEconomyPriceProvider`. A fixed golden fixture in `backend.Tests/TokenUsageCharacterizationTests.cs` holds token and cost numbers across project, workspace, pipeline, ad-hoc, and timeline responses. `backend.Tests/Architecture/TokenPricingOwnershipTests.cs` guards pricing ownership.

The earlier token services read overlapping sources and folded them independently. ASS-881 moved reads to bus-backed shims, and AGT-2542 added durable remote receipts to the project union. AGT-3037 removed the shims and parity-only tests, retaining the production ledger and pure folds. This leaves one implementation path for each output surface.

The completed Stable reports `.metadata/migrations/openai-usage-input-v1.json` (2026-09-25, 605 repaired, 0 failed) and `.metadata/migrations/token-ledger-duplicates-v1.json` (2026-10-10, 0 failed) show both one-time repairs have run. AGT-3037 removed their startup registrations and classes. Read time handling for immutable historical rows remains in `StoredUsageNormalization` and `TokenLedgerDuplicates`.

The domain record in [`docs/system/domains/tokens.md`](../system/domains/tokens.md) describes source precedence, price semantics, endpoint contracts, and current files.
