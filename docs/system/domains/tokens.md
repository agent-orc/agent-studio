# Token usage and pricing

The runtime token path has five stages. `ProjectTokenLedger` reads a merged project ledger from historical bus rows, task token receipts, and chat fallback receipts. `BusAdHocUsageReader` reads workspace ad-hoc usage. `ModelIdentity` owns the stored model id, display label, and CLI attribution. `TokenPricingService` prices normalized usage at its recorded timestamp through `TokenEconomyPriceProvider`. Pure folds in `backend/Features/Tokens/Aggregation/` produce summaries, job views, and timelines. `TokenAggregationService` composes these stages and projects endpoint DTOs through `ITokenAggregator`.

Project and workspace model totals use the same `ModelUsageFold` bucket. `PipelineCostCalculator` receives `ITokenPricing` and delegates each estimate. Cost consumers outside token aggregation receive that same service through dependency injection. The frontend builds model usage rows once in its shared token model helper; modal and detail views render those rows using CLI and provider fields from the backend.

The fixed receipts and bus fixture in [`TokenUsageCharacterizationTests`](../../../backend.Tests/TokenUsageCharacterizationTests.cs) locks project, workspace, pipeline, ad-hoc, and timeline numbers across the refactor. [`TokenPricingOwnershipTests`](../../../backend.Tests/Architecture/TokenPricingOwnershipTests.cs) guards the sole TokenEconomy owner and the injected pricing path.

## Source and precedence

`ProjectTokenLedger.LoadSnapshot` merges project bus history, durable `task.json.tokenSummary` calls, and chat fallback receipts. `ProjectTokenReceiptReader` reads both legacy lane folders and bucketed task folders, including archived tasks. `TokenLedgerMerge` removes bus/receipt overlap; `TokenLedgerDuplicates` collapses duplicate calls within a source. Receipt calls are authoritative for a task's coding run. A task without a coding receipt keeps its historical `pipeline-execution.json` cost record. A failed source is represented in `ProjectTokenDataFreshness` as partial or unavailable instead of silently becoming a zero.

The writers are `ProjectRunner.MirrorAgentTurnUsageToBus` for local coding calls, `RemoteTokenReceiptService` for remote coding completion, `RemotePipelineReviewEvidenceProjector` for remote review, and `OrchestratorChatService.RecordChatUsageAsync` for chat. Chat fallback receipts are written when the bus append fails. The merged ledger retains participant, host, CLI, model, timestamp, and token dimensions.

`TokenAggregationService.WorkspaceTimeline` filters to the requested UTC window before pricing entries. The fold applies the same half-open interval `[start, end)` and builds project, model, host, and bucket rows from the selected entries. `WorkspaceTimelineRange` reads bus rows for its exact calendar interval. Both preserve the recorded timestamp for historical pricing.

## Model identity and cost

`ModelIdentity` is the production owner of model normalization and labeling. It preserves observed model identity, including dated aliases and unknown ids. Routing equivalence does not rewrite an observed generation. `TaskTokenSummary.LastModelId` stores the id behind its display label. API model rows carry the backend CLI/provider fields; consumers do not infer them from substrings in the model name.

Canonical stored input is uncached input. `cacheRead` is the cached subset and is priced separately. Codex/OpenAI provider input that includes cached tokens is normalized at the parser boundary with `ProviderUsageNormalization.OpenAi`. Claude provider input and cache read arrive as separate dimensions. `StoredUsageNormalization` handles legacy immutable bus rows and legacy receipts on read. No fold subtracts cached input or prices a call. `TokenPricingService` is the only application pricing seam; `TokenEconomyPriceProvider` adapts the pinned historical catalog. A missing historical price remains an unknown price, with the existing API decimal fields retained for wire compatibility.

The project pipeline cost view uses receipt calls when they contain the coding run, mapping `agent:*` to core, `support:*` to aspect, and `orchestrator:*` to orchestrator. Calls without such receipts keep their pipeline execution records. The token usage cockpit joins the same ledger with route and quota evidence. CLI quota windows and slot occupancy are separate measurements; they are never added to ledger token totals.

## Completed migrations

The one-time `OpenAiUsageHistoryRepair` completed on Stable on 2026-09-25: `.metadata/migrations/openai-usage-input-v1.json` reports 605 repaired rows and 0 failures. The one-time `TokenLedgerDuplicateRepair` completed on Stable on 2026-10-10: `.metadata/migrations/token-ledger-duplicates-v1.json` reports 0 failures. AGT-3037 removed both startup services and their dedicated tests after those completion reports were verified. The reports remain historical operator evidence. Immutable bus files still need read time normalization and duplicate collapse; those read policies remain in `StoredUsageNormalization` and `TokenLedgerDuplicates`.

## Current files

| Stage | Files |
|---|---|
| Read | `Ledger/ProjectTokenLedger.cs`, `Ledger/ProjectTokenReceiptReader.cs`, `Ledger/BusTokenEntryConverter.cs`, `Ledger/BusAdHocUsageReader.cs`, `Ledger/ChatUsageFallbackReceipts.cs` |
| Identity | `Identity/ModelIdentity.cs`, `Identity/TokenParticipants.cs`, `Identity/TokenUsageHost.cs` |
| Price | `Pricing/TokenPricing.cs`, `Pricing/TokenEconomyPriceProvider.cs`, `Pricing/RunModelPricing.cs`, `Pricing/PricedTokenEntry.cs` |
| Aggregate | `Aggregation/ModelUsageFold.cs`, `Aggregation/TokenSummaryFold.cs`, `Aggregation/ProjectTokenUsageFold.cs`, `Aggregation/ProjectTokenJobFold.cs`, `Aggregation/WorkspaceTimelineFold.cs`, `Aggregation/AdHocUsageFold.cs` |
| Project | `TokenAggregationService.cs`, `ITokenAggregator.cs`, token endpoints and cache stores |

Paths in this table are relative to `backend/Features/Tokens/`. The earlier bus-backed shims and their parity-only tests were removed in AGT-3037. The golden characterization now covers the production composition directly. See [the token aggregation concept](../../concepts/token-aggregation.md) for the stage contract and [the token pricing domain](token-pricing.md) for catalog policy.
