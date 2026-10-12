# AGT-2798 summary-assessment fixture

Reconstructed card state of AGT-2798 (North star rewrite) as the Result tab
showed it on 2026-10-11: header `Pipeline failure`, `Files: 6`, `no code
change`, lane `6-completed`. The concept run parked on two operator questions
(N1, N2). The runner salvaged the work as `7aa00e27f` ("wip(runner): salvage
before teardown - outcome NeedsInput") and the operator delivered it to `main`
as the card-scoped merge `5d480157f`, which touches the six Dossier files. The
card itself carries no commits. `logs/timeline.jsonl` holds the lane history
and the salvage ref of the parked run.

Used by `SummaryAssessmentTests` (AGT-3046).
