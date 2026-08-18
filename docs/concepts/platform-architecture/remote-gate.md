---
id: platform-architecture-remote-gate
title: "Remote Gate target architecture"
status: proposed
category: concept
updatedAt: 2026-08-18
last-updated: 2026-08-18
reason: "Transfer the durable Remote Gate target object model out of the decision dossier so the architecture survives the dossier lifecycle"
taskKey: AGT-2671
tags: [remote-gate, gate, claim, lease, fencing, delivery]
related-tasks: [AGT-2369, AGT-2262, AGT-2229]
related-adrs: []
related-docs:
  - "docs/concepts/platform-architecture/README.md"
  - "docs/concepts/platform-architecture/fencing-leases-and-authority.md"
  - "docs/concepts/platform-architecture/batch-gate.md"
  - "docs/operations/remote-gate-zielbild/index.html"
---

# Remote Gate target architecture

> **Source dossier:** [Remote Gate Target Architecture](../../operations/remote-gate-zielbild/index.html)
> (`AGT-W18`, source card `AGT-2369`, status `active`/decision-ready).
> Index: [Platform architecture](README.md).

Status: target architecture, recommendation only. As of 2026-08-18, zero
implementation exists (verified by code search for the object names below)
and the design itself has not been formally approved — the source dossier
frames every decision (D1-D6) as open, pending operator sight review. This
page documents the settled *shape* of the recommendation so the target model
is discoverable outside that dossier's decision narrative. The dossier
remains the system-of-record for the open decisions and the rollout/cutover
plan; do not treat anything here as already running.

## Problem this replaces

Today `BuildTestGateRunner` drives a direct SSH subprocess to a remote host
chosen by alias-derived guesswork. The Task Server and Orchestrator Engine
have no claim, lease, fence, or durable attempt over that work; visibility
comes from a process-local, restart-losing `RemoteGateActivityStore`. A
nonzero SSH exit is recorded as a remote verdict rather than routed through
typed infrastructure classification, and the host fetch is an unscoped scan
of `$HOME/runner-work/*/repo` rather than a materialization of one declared
result.

## Target object model

Five durable objects, owned by the Task Server, extend the claim kind
vocabulary with `gate-step` alongside the existing `RunAttempt` and
`ReviewAttempt` kinds:

- **GateSubject** (immutable) — subject ID, repository ID/URL, expected SHA,
  result ref or source-bundle ID and digest, plan/policy hash, pipeline
  definition version, test-selection audit digest. Created idempotently from
  the source run, gate ID, and plan hash.
- **GateAttempt** — attempt number, state, executor/host, failure
  classification, outcome, timestamps. At most one live fenced attempt per
  subject and policy.
- **GateLease** — lease ID, attempt ID, executor/instance/host, fence,
  authority epoch, resource namespace. Reuses the
  [lease/fence/epoch conventions](fencing-leases-and-authority.md)
  already proven for `RunAttempt` and `ReviewAttempt`.
- **GatePlan** — catalogued gate ID, typed commands, deadlines, required
  capabilities, output limits. Versioned and bounded; the executor cannot add
  work beyond the plan.
- **GateReport** — outcome, typed classification, tested SHA/tree,
  dirty-before/after, command evidence, environment identity. The tested SHA
  must equal the expected SHA; stale or duplicate-conflicting reports are
  rejected.

State machine: `queued → claimed → materializing → running → reporting → cleaning`
into exactly one terminal state: `passed`, `product-failed`, `infra-retry`,
`infra-failed`, `timed-out`, or `cancelled`.

## Capability-based claim model

A host must advertise `executor:gate` plus repository/Git-fetch/disk/toolchain
capabilities to be admitted. The Task Server only admits a claim when the host
has fresh matching capabilities and an available gate slot; capability health
loss can drain affected gate claims. Capability is explicitly "an eligibility
predicate, not a work item" — a capability-only dispatch model was
considered and rejected because it cannot by itself represent queue position,
attempt state, or terminal outcome; it converges back into the GateAttempt
model once made safe.

## Exact-SHA materialization

The Engine resolves the authoritative SHA and creates an immutable
GateSubject via the Task API. The host fetches only the declared result ref
(or a digest-pinned source bundle) — it never scans arbitrary host
repositories by glob. It verifies repository identity and object type, builds
a fence-specific disposable workspace, checks `HEAD == expectedSha`, records
tree/dirty proof, executes the frozen plan, and submits a fenced report that
the Task Server validates before the Engine consumes the terminal outcome.

## Timeout vs. infrastructure-retry taxonomy

Product outcome and infrastructure outcome are kept in distinct typed
classifications:

| Condition | Classification |
|---|---|
| Queue deadline exceeded | `NoEligibleGateExecutor` |
| Lease/heartbeat loss | Fenced retry on another host |
| Command/cleanup deadline exceeded | Infrastructure timeout (unless the command defines a product timeout) |
| Normal test failure | Terminal `ProductFailure` — no retry-for-green |
| Infra-retry budget exhausted, no trustworthy result | Terminal `GateInfra` — never relabeled as product/task-quality failure |

## Relation to Batch Gate

This model claims and executes one gate step for one subject. It composes
with, and does not replace, [Batch Gate](batch-gate.md): a batch's single
full-suite run is a natural GateSubject, and the batch coordinator lease and
project ref-mutation lease are additional authority layered on top of a
GateLease, not a competing mechanism.

## Cutover gate

The dossier explicitly parks removal of the SSH bridge (tracked as `AGT-2262`)
until an acceptance list is satisfied: persistence across restarts,
capability-gated exact-SHA claims, typed classifications, a canary matrix, and
throughput/restart/failure parity with the bridge it replaces. Do not treat
`AGT-2262` as actionable until that list is met.

## Living knowledge log

- **2026-08-18 (AGT-2671):** Page created by the operator-mandated dossier
  curation pass, extracted from the remote-gate-zielbild dossier. Design
  only, not approved and not implemented.
