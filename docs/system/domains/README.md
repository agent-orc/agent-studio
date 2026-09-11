# Domain Maps

Current system-of-record maps for the major runtime domains.

| File | Owns |
|---|---|
| [runner.md](runner.md) | Pickup, CLI run loop, outcome policy, supervisor loops, recovery, and migrated attempt authority. |
| [pipeline.md](pipeline.md) | Pre/core/post steps, pipeline history, step contracts, and cost. |
| [tasks.md](tasks.md) | Job folders, lanes, API mutations, task access, review evidence, attribution, and standalone legacy migration. |
| [orchestrator.md](orchestrator.md) | Orchestrator decisions, failure interventions, attribution, feed events, and project sessions. |
| [review.md](review.md) | Remote Review subject material, aspect evidence, verdict citations, grading, and acceptance-rail behavior. |
| [frontend.md](frontend.md) | Angular surfaces, design system, polling, optimistic mutation, and Playwright proof. |
| [cli.md](cli.md) | CLI adapters, stream parsing, prompt handoff, quota probes, and models. |
| [model-routing-policy.md](model-routing-policy.md) | Canonical model and thinking-level tiers, weighted selection, correctness floors, benchmark evidence, and quota handling. |
| [tokens.md](tokens.md) | Token aggregation domain contract and bus-backed shims. |
| [token-pricing.md](token-pricing.md) | Single per-model price table and cost derivation. |
