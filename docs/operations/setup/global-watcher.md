# Global Watcher: settings, contingent, and review mode

The Global Watcher is a hosted service in the backend process. Every five
minutes it sweeps the signals the Task Server already holds, raises a
deduplicated **case** per distinct fault, and - once a fault has survived two
sweeps - drafts a **ticket proposal** for an operator to answer.

It is deliberately not autonomous. Its only writes are one proposal card in the
proposal state or one comment on a card that fault already owns. Nothing reaches
`2-ready` without an attributable human decision.

Concept and rationale: [Global Orchestrator Watcher decision
dossier](../orchestrator-waechter/index.html), especially §10.

## Kill switch

`Watcher:Enabled` is read at every tick, so toggling it takes effect within one
sweep and needs no restart. It is also in the runtime config catalogue, so it can
be flipped from Workspace settings rather than by editing a file.

Off means: no detection, no cases, no proposals, no comments, no model calls.
Proposals already in the inbox stay there and remain answerable.

## Settings

All keys live under `Watcher` in `backend/appsettings.json`. Values outside the
stated range are clamped rather than rejected, so a typo degrades the cadence
instead of taking the service down.

| Key | Default | Range | What it controls |
|---|---:|---|---|
| `Enabled` | `true` | - | The kill switch above. |
| `SweepIntervalSeconds` | `300` | 30 - 3600 | Reconciliation cadence. The dossier's five minutes. |
| `PersistenceSweeps` | `2` | 1 - 10 | Sweeps a fingerprint must survive before it may become a proposal. One sweep is a blip; two is a pattern. |
| `SuppressionDays` | `14` | 1 - 365 | How long a rejected fingerprint stays suppressed. Suppression is always visible and always expires. |
| `ProbeRepeatThreshold` | `3` | 2 - 100 | Consecutive identical probe failures that make a repetition. |
| `ReviewAttemptThreshold` | `10` | 2 - 1000 | Review attempts on one subject SHA without a state change that make a repetition. |
| `IntegrationFailureCardThreshold` | `2` | 2 - 100 | Distinct cards sharing one integration failure fingerprint. |
| `HygieneGraceHours` | `24` | 1 - 720 | How long a validation error may stand before hygiene reports it. |
| `DriftWindowHours` | `168` | 1 - 2160 | How long after a tool version change a failure still counts as dependent on it. |

## Contingent

`Watcher:Contingent` is the dedicated budget. It is enforced **before** a spend,
not accounted after it.

| Key | Default | What it bounds |
|---|---:|---|
| `DailyTokens` / `WeeklyTokens` | `200000` / `1000000` | Model tokens. |
| `DailyModelCalls` / `WeeklyModelCalls` | `20` / `100` | Bounded compression and strong-analysis calls. |
| `DailyProposals` / `WeeklyProposals` | `10` / `40` | New proposal cards. |
| `DailyComments` / `WeeklyComments` | `20` / `80` | Comments on cards a fault already owns. |

**Zero is a legitimate value and is honoured, not treated as unset.** With the
contingent at zero the Watcher keeps detecting and counting; it simply makes no
model call and writes no proposal. Those cases stay `open` with a
`contingent-exhausted` backlog reason and are reported by
`GET /api/watcher/contingent` as `backlogCases`, so the work that was found but
not drafted never becomes invisible.

Windows are rolling, derived from the durable spend rows rather than from reset
counters, so a restart cannot lose or double-count a window boundary.

## Review mode

A proposal lands in the **proposal state**: lane `1-preparation`, tag
`watcher-proposal`, plus a `watcher-<detector-class>` provenance tag and
`relatedTo` references to the cards it overlaps. It also appears in Activity
across projects as a Decision entry.

Answer it with `POST /api/watcher/proposals/{proposalId}/decision`:

| Decision | Effect |
|---|---|
| `approved` | Drops the `watcher-proposal` tag, applies the recommended model and thinking level, moves the card to `2-ready`. |
| `edited` | The same promotion, additionally recorded as "the draft needed changes" for the promotion evidence. |
| `merged` | Folds the draft into an existing card as a comment. Requires `mergeIntoTaskKey`. Moves nothing. |
| `rejected` | Requires a `reason`. Suppresses the fingerprint for `SuppressionDays` and records the reason. Moves nothing. |

A proposal can be answered exactly once. A rejection without a reason is refused,
because the reason is what the suppression entry shows an operator later.

Auto-approval does not exist. What is recorded instead is per-class acceptance
and edit counts on `GET /api/watcher/class-evidence`, which is the evidence the
dossier's promotion rule would need. Contradiction, drift, and silence are never
promotable; only repetition and hygiene ever could be.

## Endpoints

| Route | Use |
|---|---|
| `GET /api/watcher/status` | Enabled, last run, open cases, decisions required, backlog, contingent. |
| `GET /api/watcher/cases?state=` | Cases, optionally filtered by state. |
| `GET /api/watcher/proposals?decision=` | Proposals, optionally filtered by decision. |
| `GET /api/watcher/contingent` | Budget, usage per window, remaining, backlog count. |
| `GET /api/watcher/suppressions` | Suppressed fingerprints with reason, author, and expiry. |
| `GET /api/watcher/class-evidence` | Per-class acceptance and edit counts. |
| `POST /api/watcher/proposals/{id}/decision` | The single review-mode write. |
| `DELETE /api/watcher/suppressions/{fingerprint}` | Lift a suppression early. |
| `POST /api/watcher/sweep` | Run one sweep now, for the moment after a repair when waiting five minutes is the wrong experience. |

## Observing it

Each sweep writes one `watcher-run` structured log line with the same shape as
`acceptance-rail-run`:

```
watcher-run findings=8 casesOpened=0 casesUpdated=8 casesClosed=0 proposals=8
  comments=0 suppressed=0 backlog=0 modelCalls=0 failed=0 openCases=0
  decisionsRequired=8 lastRunAtUtc=...
```

Durable state is one document at
`<TaskRepository>/.metadata/watcher-state.json`: cases, proposals, suppressions,
and spend rows. If it is unreadable the Watcher starts from an empty case store
and rediscovers every live fault on the next sweep, which is what it is for.

## Troubleshooting

**No proposals appear, but cases do.** Expected on the first sweep after a fault
appears: `PersistenceSweeps` requires two. If it persists, check
`GET /api/watcher/contingent` for `backlogCases` and the `exhausted` flag.

**A fault I fixed keeps producing cases.** The signal is still live. Check the
case's evidence pack; the fingerprint names exactly what the detector still sees.

**A fingerprint I rejected came back.** Suppression expires by design. Check
`GET /api/watcher/suppressions` for the expiry, and re-reject or fix the cause.

**Detection works but nothing is found in production.** Only two of the eight
rules read live sources today; the rest wait on a durable producer signal. The
gaps are listed in §8b of the dossier.
