# Review and batch baseline (AGT-3400 slice S2, card AGT-3425)

Measurement record for slice S2 of the Dossier
[Review and gate on packages](../index.html) (AGT-3400): per-step review time
and tokens, the fixed token share f, the aspect share a, the Dossier wave
census with the package size k, the batch census, the attribution of the
full-suite runs, and the backward moves by cause.

Captured on 2 November 2026 on `agent-studio-runner` (runner `agent-runner-01`).

## Decisions implemented

No operator decision is recorded for PK3 or PK5, so this slice implements the
Dossier's recommended options.

| Decision | Option implemented | Where |
|---|---|---|
| PK3 Review package unit | A: cards of one Dossier (`task.json.dossier.path`); cards without a Dossier stay per card and form no package | `ReviewWaveCensus` |
| PK5 Package formation | A: members whose card check passed within 60 minutes of the first, at most 4 members, at most 200 files and 12,000 lines; a package never waits for coding; a lone member is a package of one | `ReviewWaveCensus.Form` |
| PK4 Review split (used for the tier labels only) | R-B: tool commands and requirement fit are the card check; code quality, documentation impact, tests and evidence and grade are the package tier; the decision stays per member | `ReviewSteps.Tier` |

## Where the values are read

All values are computed by the Studio backend from the task folders, so they
are read where the data lives. Each read below needs an operator credential.

| Value | Request | Field |
|---|---|---|
| Per-step wall minutes and input, output, cache-read and cache-creation tokens, per round and per card | `GET /api/projects/Agent%20Studio/token-usage/reviews?days=30&detail=baseline` | `baseline.steps[]` |
| a: catalogue-aspect wall time as a share of reviewOther | same | `baseline.aspectShareOfReviewOther.pooled` (per-card median: `.medianPerCard`) |
| f: fixed prompt-token share per aspect | same | `baseline.fixedShare[].fixedShare`; package tier weighted: `baseline.packageTierFixedShare` |
| Wave census and k | same | `baseline.waves.dossierShare`, `baseline.waves.medianPackageSize`, `baseline.waves.packageSizes` |
| Full-suite runs by origin, 7 days | `GET /api/gate-economics?window=7d` | `rows[].baseline.fullSuiteBreakdown` |
| Backward moves by cause | `GET /api/projects/Agent%20Studio/cycle-time?window=30d` | `transitions.backwardCauseGroups`, `transitions.tasksWithoutReviewCause` |

Unknown is never zero. A pipeline row of a model-backed step without token
fields (legacy rows) counts in `unknownRounds` and `unknownCards`, and is left
out of medians and totals. A fixed-share sample without a known material size
counts in `unknownSamples`. A wave census member without a known size is
marked `sizeUnknown` on its package.

## Measured in this run

### Batch census

`run-batch-census.sh` ran `batch-gate-census.py --project "Agent Studio"
--since 2026-10-13` on every instance folder of the runner account. Output:
[batch-census-agent-studio-runner-summary.txt](batch-census-agent-studio-runner-summary.txt).
None of the 71,240 instance folders has a `batch-gates` directory, so there is
no per-instance output to commit from this account.

The Studio backend runs on this host in a Docker container (`studio.slice`,
active since 20 October 2026 03:29 CEST) as uid 10001, with `HOME=/data/home`
and `TaskRepository=/data/workspace` on volumes under `/srv/agent-studio/data`.
Its batch store is therefore under
`/srv/agent-studio/data/home/.local/share/agentstudio/instances/*/batch-gates`.
That directory is not readable by the runner account (no sudo, no Docker
socket), so its census is still open. An account with access runs:

```bash
sudo -u '#10001' CENSUS_SCRIPT=docs/review-gate-packages/batch-gate-census.py \
  bash docs/review-gate-packages/measurements/run-batch-census.sh \
  /srv/agent-studio/data/home/.local/share/agentstudio/instances \
  docs/review-gate-packages/measurements
```

### Promotion full-suite runs, 26 October to 1 November 2026

Promotion runs on this host and leaves no evidence on the Studio side, so the
endpoint reports `promotion: null` and the count comes from the promotion logs
(`promotion-exec-*.log`). Each run executed the full `OrchestratorApi.Tests`
suite.

| Started (UTC) | OrchestratorApi.Tests | Duration |
|---|---|---|
| 2026-10-26 07:17 | 1 failed of 12,435 | 38 m 56 s |
| 2026-10-26 08:03 | passed, 12,435 | 37 m 58 s |
| 2026-10-26 10:56 | passed, 12,436 | 37 m 50 s |
| 2026-10-29 07:53 | 1 failed of 12,971 | 40 m 23 s |
| 2026-10-29 08:48 | passed, 12,971 | 39 m 39 s |
| 2026-10-30 10:07 | passed, 12,981 | 38 m 8 s |
| 2026-11-01 15:05 | passed, 13,150 | 47 m 39 s |
| 2026-11-01 17:15 | passed, 13,162 | 46 m 56 s |

Promotion: 8 full-suite runs in the window, 2 of them red and repeated.

### Review runner on this host

`journalctl -u agent-runner-review` from 10 October 2026 names a distinct review
attempt on these days: 8, 31, 45, 15, 32, 30 and 34 attempts on 10 to 16
October, 4 on 17 October, and none after that: no review attempt id appears
in the journal after 17 October 01:44 CEST. From 25 October the unit logged
14,160 "agent-host starting" lines and 14,148 "could not reach the task server
at http://127.0.0.1:15031" lines (14,147 of them "Connection refused"); since
10 October it logged 38,716 such lines.

## Why the batch count fell to "then none"

The three hypotheses of the Dossier, against the evidence this run can read:

- **A new instance folder: supported.** The figures are labelled "before the
  move". The Studio backend has run in the `studio.slice` container since
  20 October 2026, with content root `/app` and task repository
  `/data/workspace`. `BatchGateStorageRoot` hashes exactly these two paths, so
  the containerised installation writes to a new, initially empty
  `batch-gates` folder under its own `HOME`, and no runner-account instance
  folder holds a batch store. A census of the old folder alone shows "none"
  even if batches continued.
- **No completed reviews: contributes, at least on this host.** The review
  runner here named no review attempt after 17 October and has been unable to
  reach its task server since. Reviews that ran on another review host are
  not visible here.
- **Fewer arrivals: not decidable here.** It needs the gate economics or the
  new store's census.

The census of the container store separates the first two: batches closed
after 20 October in the new folder confirm the move, and an empty new folder
with no completed reviews confirms the second.

## Still open

These values need the Studio data (the container volume or an operator
credential) and are not reported here: a, f and k for Agent Studio over 30
days, the wave share, the Studio-side full-suite origins of the 7-day window
(batch candidates, isolation probes, per-card fallback, integration gate,
review-side full selections), the backward moves by cause, and the census of
the container's `batch-gates` folder. The requests in "Where the values are
read" return them.

## Method notes

- Review steps come from `pipeline-execution.json`: every pipeline attempt,
  every earlier run of a step and the current row. Verdicts carried over from
  an earlier round ran nothing and are left out. Review observations recorded
  through the observation API are added as their own samples.
- Tool commands are the Remote Review verification tool commands
  (`post-build-test-gate`); they call no model, so their tokens are a measured
  zero. The decision covers `post-orchestrator-review` and
  `post-orchestrator-decision`.
- f fits prompt tokens (input plus cache read plus cache creation, the whole
  prompt the model read) against the material lines by least squares, per
  aspect, and reports the intercept over the mean prompt, clamped to [0, 1],
  with R². It needs at least 3 known samples of different sizes. The material
  lines are the added plus removed lines of the card's attributed commits up
  to the reviewed Result-SHA, from the published Git facts. That is a proxy
  for the merge-base diff Remote Review renders: it leaves out context lines,
  and it overstates a card whose later commits revert earlier ones. Files come
  from the reviewed file count of the round when present.
- a divides catalogue-aspect wall minutes over all review rounds of a card by
  the card's cycle-time reviewOther, for cards completed in the window.
- The card check time for the wave census is the completion of the latest
  round in which every card-check step (tool commands, requirement fit)
  passed.
- A review attempt counts as a review-side full selection when its executed
  candidate `dotnet test` commands carry no `FullyQualifiedName`, `ClassName`
  or `Name` selection filter. Attempts known only from their grade report
  count as unknown.
- The existing `fullSuiteRuns` counter counts integration receipts at level
  full plus batch runs. It does not count per-card fallback gates or base
  checks; `fullSuiteBreakdown` lists them separately.
