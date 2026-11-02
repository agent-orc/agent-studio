# Review and batch baseline (AGT-3400 slice S2, card AGT-3425)

Measurement record for slice S2 of the Dossier
[Review and gate on packages](../index.html) (AGT-3400): per-step review time
and tokens, the fixed token share f, the aspect share a, the Dossier wave
census with the package size k, the batch census, the attribution of the
full-suite runs, and the backward moves by cause.

The measurement code ships with this card. The Agent Studio figures are
read on the Studio server after deployment; the runner account that built this
card has no access to the Studio's data. Every figure below is therefore
marked `measured after deployment by the operator`, together with the route or
command that produces it.

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
are read where the data lives. Every route below is a `GET` that an Owner
session can read (the Owner role holds every permission). The routes read the
task folders and the published Git facts (`GitCardFactReader`); they start no
Git process, per card or otherwise. A commit the fact store has not published
yet leaves the material size unknown instead of starting Git.

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

## Agent Studio figures, 30 days

| Figure | Value | Produced by |
|---|---|---|
| a (catalogue-aspect share of reviewOther) | measured after deployment by the operator | `token-usage/reviews?days=30&detail=baseline`, `baseline.aspectShareOfReviewOther` |
| f per package-tier aspect (code quality, documentation impact, tests and evidence, grade) | measured after deployment by the operator | same route, `baseline.fixedShare[]` (with `rSquared`, `samples`, `unknownSamples`) and `baseline.packageTierFixedShare` |
| k (median package size under PK5 A) | measured after deployment by the operator | same route, `baseline.waves.medianPackageSize` |
| Share of reviewed cards with `task.json.dossier.path` | measured after deployment by the operator | same route, `baseline.waves.dossierShare` (S5 stop rule below 0.30: `meetsWaveShareThreshold`) |
| Full-suite runs of one 7-day window by origin | measured after deployment by the operator | `gate-economics?window=7d`, `fullSuiteBreakdown`: `batchCandidates`, `isolationProbes`, `perCardFallback`, `integrationGate`, `baseChecks`, `reviewFullSelections`; promotion is `null` there and is counted from the promotion host's `promotion-exec-*.log` files of the same window |
| Backward moves by cause (review, gate, lease recovery, other) | measured after deployment by the operator | `cycle-time?window=30d`, `transitions.backwardCauseGroups` |

## Batch census

Status: measured after deployment by the operator.

The Studio keeps its batch store under its own account's
`${XDG_DATA_HOME:-$HOME/.local/share}/agentstudio/instances/*/batch-gates`
(`BatchGateStorageRoot`). As the account that owns that store, from the
repository root, run:

```bash
bash docs/review-gate-packages/measurements/run-batch-census.sh \
  <instances-root> docs/review-gate-packages/measurements
```

The script runs
`python3 -I docs/review-gate-packages/batch-gate-census.py <folder> --project "Agent Studio" --since 2026-10-13`
for every `instances/*/batch-gates` folder, writes one
`batch-census-<host>-<instance>.txt` per folder and a
`batch-census-<host>-summary.txt` into this directory. Commit those files.

## Why the batch count fell to "then none"

Status: decided by the batch census above, measured after deployment by the
operator. The code already settles which outcomes each hypothesis predicts:

- **A new instance folder.** `BatchGateStorageRoot` names the instance folder
  after a hash of the content root and the task repository path. An
  installation that moved (for example into a container with a different
  content root or task repository) writes to a new, initially empty
  `batch-gates` folder, and a census of the old folder alone shows "none" even
  if batches continued. Confirmed when the census finds a second instance
  folder whose batches start after the last batch of the old one.
- **No completed reviews.** Confirmed when the new folder, or the only folder,
  has no batch after the drop and the review baseline route shows no review
  round completed after it (`baseline.steps[]` rounds by date).
- **Fewer arrivals.** Confirmed when reviews completed after the drop but the
  gate economics window shows few or no batch candidates and per-card
  fallback gates instead.

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
