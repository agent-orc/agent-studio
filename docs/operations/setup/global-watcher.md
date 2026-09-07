# Global Watcher setup

The global Watcher is the orchestrator's autonomous problem finder. It sweeps
every five minutes, turns repeated failures and contradictions into durable
cases, and proposes tickets for the cases that survive two sweeps. It never
moves a card to Ready and never touches Git; an operator answer is the only path
from a proposal into the run queue.

The decision record is the dossier at
[../orchestrator-waechter/index.html](../orchestrator-waechter/index.html).
Read section 10 before changing any value here.

## Switching it on

The Watcher ships **off**. Two switches turn it on, and both are live: a hosted
sweep re-reads them at every tick, so no backend restart is needed.

| Key | Default | What it does |
|---|---|---|
| `Watcher:Enabled` | `false` | The kill switch. Off leaves the service resident and idle, so a switched-off Watcher and an absent one do not look the same. |
| `Watcher:AnalysisEnabled` | `true` | Allows a bounded model call for a case that declared an uncertain cause. Detection itself never uses a model. |
| `Watcher:IntervalSeconds` | `300` | Sweep cadence. Clamped to 30 s .. 1 h. |

Flip them in Workspace Settings under the orchestrator configuration, or in
`backend/appsettings.Local.json`:

```json
{
  "Watcher": {
    "Enabled": true,
    "ProposalProject": "Agent Studio"
  }
}
```

`Watcher:ProposalProject` names the project that receives proposals for
workspace-wide findings (a stale CLI probe belongs to no single project).
Without it the first watch path is used and the fallback is logged, because a
silently misfiled card is worse than a noisy one.

## The contingent

The Watcher spends from a dedicated budget, per day and per week, in tokens and
in counts. It is shown on **Workspace CLI Management** next to the usage caps.

| Key | Default |
|---|---|
| `Watcher:Contingent:ModelCallsPerDay` / `PerWeek` | 20 / 80 |
| `Watcher:Contingent:TokensPerDay` / `PerWeek` | 500000 / 2000000 |
| `Watcher:Contingent:ProposalsPerDay` / `PerWeek` | 5 / 20 |
| `Watcher:Contingent:CommentsPerDay` / `PerWeek` | 20 / 80 |

Windows are the UTC day and the ISO week. Omitting a key keeps its default;
setting it to `0` closes that dimension.

**Exhaustion is not a stop.** When the budget runs out the Watcher keeps
detecting and counting; only model calls, proposals, and comments stop. The
unanalysed backlog stays visible on the same strip, so raising the budget
tomorrow resumes from the same evidence rather than from a restarted count.
Setting every value to `0` is the supported way to run detection-only.

## Detector thresholds

Five detector classes, all pure policy over collected signals. Every value is
clamped where it is read.

| Key | Default | Class |
|---|---|---|
| `Watcher:RepetitionThreshold` | `3` | Occurrences of one fingerprint, counted only since the last state change. |
| `Watcher:RepetitionCardThreshold` | `2` | Distinct cards sharing one fingerprint. One dirty integration checkout blocking two cards is already a repetition. |
| `Watcher:SilenceCadenceFactor` | `1.0` | Multiplier on the producer's own cadence before a missing signal counts as silent. |
| `Watcher:HygieneGraceHours` | `24` | How long a known validation error may live before hygiene reports it. |
| `Watcher:DriftWindowDays` | `30` | How long after a tool version change a dependent failure still counts as drift. |

Drift wins over repetition: when a toolchain change explains a run of failures,
the operator sees one case, not two.

## The model route

Analysis uses `Watcher:AnalysisTier`, which must name a tier of
[../../system/domains/model-routing-policy.md](../../system/domains/model-routing-policy.md).
The default is `sol-medium`, the dossier's strong-analysis floor. Quota or price
may not lower it, so there is deliberately no cheaper fallback. Bounded evidence
compression uses the Mini/high role model of the pipeline defaults.

A strong call is admitted only when the case declared an uncertain cause **and**
the contingent allows it. A hygiene case whose validator already named the cause
never spends one.

## Reviewing what it proposes

Proposals land in `1-preparation` tagged `watcher-proposal` and appear in
**Activity across projects** as a Decision entry. Four answers:

- **Approve** moves the card to Ready with the recommended model.
- **Approve after edit** does the same but is counted separately, because only
  unedited acceptances count toward the auto-approval promotion rule.
- **Merge** folds the finding into an existing card.
- **Reject** requires a reason, and that reason becomes a visible suppression
  for `Watcher:SuppressionDays` (default 14). Suppression always expires.

Auto-approval is not implemented. `GET /api/watcher/promotion-evidence` reports
the per-class acceptance counts the dossier's promotion rule asks for, and
contradiction and drift are excluded from it by policy.

## Checking on it

| Endpoint | Shows |
|---|---|
| `GET /api/watcher/status` | Snapshot, sources, contingent, and whether the last sweep is stale. |
| `GET /api/watcher/cases` | Durable cases with their fingerprint, counts, and state. |
| `GET /api/watcher/proposals?decision=pending` | Proposals awaiting an answer. |
| `GET /api/watcher/suppressions` | Rejected fingerprints, expired ones included. |
| `GET /api/watcher/fixtures` | The eight findings of 6 September 2026 that the detectors are tested against. |
| `POST /api/watcher/sweep` | Runs one cycle now, through the same code path as the cadence. |

Each sweep emits a `watcher-run` structured log line with the signal, finding,
case, proposal, and backlog counts. Each proposal appends a `watcher_proposed`
timeline event to the card it created or commented on, and each answer appends
`watcher_proposal_decided`.

## When something looks wrong

- **No cases at all.** Check `Watcher:Enabled`, then `GET /api/watcher/status`
  for `storeAvailable` and the source list. A sweep that collected nothing
  closes no case, so a blind sweep never looks like a clean bill of health.
- **Cases but no proposals.** Either they have not survived two sweeps yet, or
  the contingent is exhausted. The CLI Management strip distinguishes the two.
- **A proposal that should not exist.** Reject it with a reason. The fingerprint
  is suppressed and the reason stays visible until it expires.
- **A duplicate card.** There should not be one: a fingerprint that already has
  an open card gets a comment. If you see a duplicate, check whether the first
  card lost its `watcher-fp-` tag.
