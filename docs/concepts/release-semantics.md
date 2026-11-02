# Release semantics: integration, acceptance, release, and stable freeze

Status: decided model, 2026-07-13. This page supersedes the release assumptions
in the older `git-branching-integration-zielbild.md` draft (since retired).

## Decision

Agent Studio keeps its fast **merge-on-complete** integration stream. A task may
already be present on `develop` while its card is still in Auto Review, Human
Review, or Escalated. A release does not pretend otherwise: the manual
`develop -> main` action must show every included task and its current acceptance
state before the operator confirms it.

`main` is the continuous release line. It is not, by itself, a claim that a
revision is a known-good stable version. Stability is a separate, explicit
**freeze** action that tags a verified `main` revision and records the evidence
used for that decision.

This is the **transparent watering-can model**: release the integrated stream as
one coherent graph, but expose exactly which reviewed and unreviewed work is in
the stream. Do not assemble an allegedly clean release by silently cherry-picking
individual accepted cards.

## Four events that must not be conflated

| Event | What it means | Git effect | Operator promise |
|---|---|---|---|
| Task integration | The finished task revision joins the shared integration graph. | Task branch is merged or fast-forwarded into `develop`. | The work is durable and available to dependent tasks. |
| Acceptance | Human evidence for a task is accepted. | No required Git mutation; the task may already be on `develop`. | The card's result and evidence were reviewed. |
| Release | The current integration graph is promoted. | Manual, auditable `develop -> main` merge or fast-forward. | The shown release manifest is exactly what moved to `main`. |
| Stable freeze | A released revision is declared known-good. | Annotated tag on an exact `main` SHA, plus a durable freeze record. | This exact revision passed the project's stable criteria. |

The distinction is load-bearing. A merge badge answers “where is the code?”;
the review state answers “has a person accepted the evidence?”; a release entry
answers “what reached `main`?”; and a stable tag answers “which released SHA did
we deliberately freeze?”

## Why integration stays early

Agent Studio runs several agents in parallel and frequently chains tasks. Early
integration gives follow-up tasks one shared base, exposes conflicts while the
context is still fresh, and avoids a queue of long-lived branches waiting for a
human. Moving the merge behind Human Review would trade visible release risk for
hidden integration drift and would throttle unattended throughput.

The cost is that `develop` is an integration truth, not a reviewed-only truth.
That cost is acceptable only when the release surface makes it impossible to
mistake the two.

## Release manifest and confirmation

Before `develop -> main`, the product computes a manifest over the exact Git
range `merge-base(main, develop)..develop`. The dialog shows:

- the target `develop` and `main` SHAs and whether the promotion is a clean
  fast-forward or a merge;
- every attributed task in the range, grouped by **accepted**, **still in
  review**, **escalated**, and **unknown/unattributed**;
- task key, title, current lane, task type or criticality, merge SHA, and links
  to the task evidence;
- commits that cannot be attributed to a task as a separate, never-hidden
  group;
- dependency or ordering warnings when the task graph says that an included
  card waits on work outside the range;
- the resulting release SHA and rollback target.

The primary confirmation copy is explicit, for example:

> Release 18 included tasks to `main`: 13 accepted, 3 still in Human Review,
> 1 escalated, and 1 unattributed commit.

Unreviewed work produces a strong warning, not an invented “accepted” state.
Projects may configure a block for named criticality classes or unresolved
escalations. An authorized override remains possible, but the actor, timestamp,
reason, manifest hash, and exact SHAs are written to the release record. The
product never changes task lanes merely to make the dialog green.

### Manifest truth rules

1. Git ancestry decides inclusion; task lanes never rewrite Git history.
2. Task-to-commit attribution may enrich the manifest but cannot hide an
   unattributed commit.
3. The manifest is recalculated immediately before confirmation. If either
   branch moved, the operator reviews the new manifest.
4. The release record stores the manifest, not only counts, so later review can
   reconstruct the decision.
5. A failed push or merge creates no successful release record.
6. The exact release subject must pass `PreMainTestGate` before the write to
   `main`. This forces every declared test command at the `full` level in hard
   fail mode. Lane settings, a small diff, Test Hub history, and model advice
   cannot reduce this suite, and there is no release override for a red or
   incomplete full-suite result. The existing deferred integration merge
   enforces this when a project's configured integration target is `main`: it
   tests the exact subject and advances `main` only to that tested SHA: the
   delivery itself by fast-forward when it descends from `main`, otherwise the
   merge of the delivery onto `main`, which the full suite tested and which
   still contains the published `main` (AGT-3435). This safety boundary does not replace the REL-1 manifest
   and confirmation surface.

## Main is released; a stable tag is frozen

Every successful promotion advances `main`, including preview releases. A
project calls a revision stable only through a separate freeze workflow:

1. Select an exact SHA that is already reachable from `main`.
2. Show the release manifest and any acceptance-state changes since release.
3. Attach the successful commit-bound test run used as stable evidence, plus
   deployment health, visual proof, and any critical incident gate. The test
   run commit is explicit; an older green run does not prove a newer Head.
4. Create an annotated tag such as `stable/2026-07-13` or the project's semver
   release tag. The project chooses one naming policy; the product does not
   create both automatically.
5. Persist a freeze record containing the tag, SHA, checks, evidence links,
   actor, timestamp, deployment target, and previous stable tag for rollback.

The current Agent Studio stable checkout still follows `origin/main`; that is a
deployment-seat convention, not stable evidence. The stable-freeze slice adds
the explicit tag and record without changing the fact that releases flow through
`main`.

## Rejected alternatives

### Merge only after acceptance

This creates an integration queue, keeps dependent work on stale bases, and
makes overnight parallelism wait for daytime review. It is appropriate for a
different, review-first project profile, but it is not the decided Agent Studio
default.

### Assemble a release from accepted cards

Cherry-picking only accepted task commits sounds precise until tasks overlap or
depend on one another. A later accepted task may contain assumptions from an
earlier unaccepted task; merge commits and follow-up fixes may not partition
cleanly. Reverts have the same graph-entanglement problem in reverse. The
release dialog must expose coupling, not imply that card status can safely
slice an arbitrary integration graph.

### Dark-ship everything behind feature flags

Flags can separate deployment from activation for risky user-facing slices and
remain a useful long-term option. They are not free: each flagged task needs a
stable flag owner, both-state tests, cleanup criteria, telemetry, and removal
work. Flags therefore stay an explicit engineering choice, not an automatic
wrapper around every card.

## Product boundaries

- **Update Center (AGT-2090 family):** owns the release-preview dialog and the
  exact `develop -> main` promotion. It consumes task/commit attribution and
  writes the release record.
- **Deployment (AGT-2097 / DEP family):** owns project-specific release and
  stable-freeze scripts as reusable targets. Promotion, deploy, verification,
  and freeze remain distinct steps in one visible run.
- **Project Overview (AGT-2105 family):** shows the latest release, stable tag,
  reviewed/unreviewed counts, and any current deploy or freeze block.
- **Task detail:** shows integration, acceptance, release, and stable reachability
  as separate facts. “Merged” must never be rendered as “accepted”.

## Delivery slices

| Slice | Scope | Acceptance signal |
|---|---|---|
| REL-1 — transparent release preview | Range-derived manifest, grouped acceptance states, unattributed commits, branch-moved recheck, confirmation, durable release record. | A preview fixture with accepted, Human Review, escalated, and unattributed entries produces the same manifest before and after reload; changing `develop` invalidates confirmation. |
| REL-2 — stable freeze | Project-specific checks, annotated tag, freeze record, previous-stable rollback pointer. | A tag is created only for a reachable `main` SHA after all configured checks or an audited override. |
| REL-3 — release/stable history | Project Overview history, task reachability badges, deploy/freeze run links. | Any task can answer whether and in which release/stable tag its commit first appeared. |
| REL-4 — selective dark ship | Costed feature-flag policy and one bounded pilot for a risky UI slice. | Both flag states are tested and the pilot has an owner and removal date. |

## Migration path

1. Keep current merge-on-complete behavior and make the terminology honest now:
   `develop` means integrated, Human Review means accepted evidence.
2. Ship REL-1 before adding any new one-click release action. Until then, the
   versioned operator command and checklist in
   `docs/operations/develop-main-promotion.md` are the safe manual path. They
   provide an exact commit manifest, fail-closed full gate, annotated release
   marker, and deploy handoff, but do not pretend to provide REL-1 task-lane
   attribution or acceptance groups.
3. Teach Deployment targets to execute the existing project release script and
   attach the REL-1 manifest.
4. Add REL-2 tags and freeze records; keep the existing stable checkout update
   mechanism as a consumer of `main` until it is deliberately changed.
5. Add history and optional criticality gates only after the manifest is trusted.

## Decision invariants

- Fast integration is not acceptance.
- A release promotes a Git graph, never a filtered board view.
- Every included commit is visible, even when attribution is missing.
- `main` means released; only an explicit tag plus evidence means stable.
- Warnings and overrides are durable facts, not transient dialog copy.
- Deployment and freeze evidence names a successful test run and its exact
  commit. Head deployment is a durable, reasoned exception.
- Project-specific release/freeze mechanics live in versioned Deployment
  targets, not in an operator's memory.
