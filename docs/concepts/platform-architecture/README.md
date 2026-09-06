---
id: platform-architecture-index
title: "Platform architecture index"
status: active
category: concept
updatedAt: 2026-08-17
last-updated: 2026-08-17
reason: "One coherent home for the durable execution and delivery architecture extracted from decision dossiers"
taskKey: AGT-2671
tags: [architecture, index, platform, distributed, delivery]
related-tasks: [AGT-2671]
related-adrs: []
related-docs:
  - "docs/concepts/README.md"
  - "docs/system/architecture/README.md"
  - "docs/system/domains/README.md"
---

# Platform architecture index

The durable architecture of the execution and delivery platform: who may write,
how a delivery reaches the integration branch, which processes own which state,
and what the organization telemetry contract would be.

Most pages here were **extracted from a decision dossier** under
`docs/operations/`; one ([Task Server topology](task-server-topology.md)) is
consolidated from several documents and has no single source dossier. Dossiers
are decision instruments with a lifecycle; they move to History once their
decision is settled and their slices are delivered. Architecture must outlive
that lifecycle, so the durable mechanisms, invariants and contracts live here
and the dossier keeps the decision drama, the option comparisons and the
approval record. Each page links back to its source dossier, and each source
dossier carries a pointer to its page in its `workbench.json` summary.

## Where this sits

| Layer | Owns | Location |
|---|---|---|
| Decision dossiers | The open question, the options, the recommendation, the operator decision | `docs/operations/<topic>/`, listed under Dossiers |
| **Platform architecture (this folder)** | The durable mechanism, invariants, contracts, failure modes, delivered versus open | `docs/concepts/platform-architecture/` |
| Domain maps | The current system of record for an area | [`docs/system/domains/`](../../system/domains/README.md) |
| ADRs | Load-bearing decisions and deliberate non-goals | [`docs/system/architecture/decisions/`](../../system/architecture/decisions/adr-archive.md) |

A page graduates out of this folder when its area gets a domain map or an ADR.
Until then this is the single place to read before changing any of these
mechanisms.

## Pages

| Page | What it covers | Source dossier | Status |
|---|---|---|---|
| [Fencing, leases, and attempt authority](fencing-leases-and-authority.md) | The single-writer contract: lease lifecycle and TTLs, per-task monotonic fences, the global authority epoch as a soft drain, the ten-step write gate, at-least-once outbox replay with destination-side dedupe, and the failure and recovery matrix. | [Hardening distributed execution](../../operations/haertung-verteilte-ausfuehrung/index.html) (`AGT-W7`) | Delivered behaviour, with named gaps |
| [Rebase, merge, and bounce steering](rebase-merge-and-integration-invariants.md) | Thirteen integration invariants, the stage matrix that explains when SHAs are preserved and when they may be rewritten, the canonical integration ladder, the typed failure classification policy, the one-round bounce budget, and the integration record contract. | [Rebase vs merge and bounce steering](../../operations/rebase-merge-and-steering/index.html) (`AGT-W37`) | Delivered behaviour, steering questions open |
| [Task Server topology](task-server-topology.md) | Process topology, state ownership, the three switches that decide a deployment shape, the full `/api/v1` route surface versus the legacy planes, the Git evidence model, and the honest state of the AGT-2663 cutover. | Consolidated from four documents plus the sources | Cutover in progress |
| [Batch Gate](batch-gate.md) | One suite for a delivery wave: closed manifest, deterministic ordering, mechanical replay, single-candidate authorisation, publish-or-isolate, bounded halving, and the full typed failure table. | [Batch Gate](../../operations/batch-gate-concept/index.html) (`AGT-W36`) | Proposed, decision pending |
| [Organization-wide telemetry layer](telemetry-layer.md) | The inventory of signals that exist today, the proposed `OrgTelemetryEvent/v1` envelope, the hard privacy boundary, level mapping, the hybrid transport contract, and the bounded signal-to-action vocabulary. | [Telemetry layer](../../operations/telemetry-layer/index.html) (`AGT-W38`) | Proposed, decision pending |
| [Remote Gate target architecture](remote-gate.md) | The GateSubject/GateAttempt/GateLease/GatePlan/GateReport object model, the capability-based claim model, exact-SHA materialization, the timeout-versus-infra-retry taxonomy, and the AGT-2262 SSH-bridge cutover gate. | [Remote Gate Target Architecture](../../operations/remote-gate-zielbild/index.html) (`AGT-W18`, `active`/decision-ready) | Proposed, not implemented |

## How to read a status column

- **Delivered behaviour** means the page was verified against the code during
  extraction and describes what the platform does today. Where the source
  dossier disagreed with the code, the code won and the divergence is recorded
  in that page's living knowledge log.
- **Proposed** means no operator decision exists yet. Nothing in those sections
  is implemented. Do not build against them without an approval.
- **In progress** means part is live and part is not; the page separates the
  two explicitly.

## Adding a page

1. The content must be durable: a mechanism, an invariant, a contract, a
   failure mode or a naming rule. Decision drama stays in the dossier.
2. Verify every cited path, route and symbol against the checkout before
   writing it down. Prefer the code over the dossier when they disagree, and
   note the divergence.
3. Add a pointer to this page in the source dossier's `workbench.json` summary,
   add a row to the table above, and add a row to
   [`docs/start/README.md`](../../start/README.md).
4. End the page with a **Living knowledge log** section, newest entry on top.
