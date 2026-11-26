# Dispatcher replay

Evidence for the Dossier `docs/dispatcher-personas/index.html` (AGT-3974);
[../README.md](../README.md) lists every Dossier file.

- `wave-2026-11-17.json`: the 57 playthrough cards AGT-3890 to AGT-3946 of
  17 November 2026, rebuilt from git at develop `d3c4eed65`. Per card it holds
  the real code files, the cited files and the forecast files, the keyword areas
  of `IntakeRunner.DetectTaskAreas`, the severity, and the develop sync commit
  count. The `createdFrom.method` field explains each field. It contains
  repository paths only, not finding text.
- `replay.py`: runs the six strategies of the Dossier on the wave and prints
  pair detection quality, the replay results for unit and size-weighted
  durations, and the plain and gated weighted values. It then prints today's
  prompt-path rule as an unscored upper bound, and the exposure window: an
  integration lag of 1 and 2 card durations with the check modes `running`
  (today's pick-gate) and `inflight` (every started, unintegrated card).
  Standard library only.
- `check_dossier.py`: mirrors the workbench contract (keys, bounded slices,
  `dependsOn` order, concept sections, relative links of `index.html`), checks that slices sharing an area are
  ordered by `dependsOn`, applies today's pick-gate scope rule to the
  promoted prompts of the DS slices and PT-13 to PT-20 to show which pairs the
  platform keeps apart, and compares every cell of the replay results table in
  `index.html` with the output of `replay.py`, and runs `../review_input.py`.
- `output-4-slots.txt`, `output-6-slots.txt`, `output-check.txt`: the full
  output of the three commands below at develop `d3c4eed65`, so the figures can
  be read without running anything.
- `remote-claim-evidence.md`: the ten wave cards whose runner reports name a
  remote result ref, with the delivery commits, which all exist in git.

```sh
python3 -I docs/dispatcher-personas/replay/replay.py            # 4 slots
python3 -I docs/dispatcher-personas/replay/replay.py \
  docs/dispatcher-personas/replay/wave-2026-11-17.json 6        # 6 slots
python3 -I docs/dispatcher-personas/replay/check_dossier.py     # contract and separation
```

The model and its limits are documented at the top of `replay.py`. A shared
code file is an upper bound for a conflict. Rounds per card stand in for tokens.

DS-1 backend checks live in `backend.Tests/DispatchReplayTests.cs`. They load this
unchanged wave for pair F1, and use
`backend.Tests/Fixtures/dispatch/source-findings.json` for the original problem,
cause and acceptance criteria from the three source card lists. That fixture
also freezes all 10,365 tracked paths at source integration tip `a4c7a149c`, so
unique filename resolution cannot change as the repository grows. The three
collection cards preserve their source findings, rather than synthesizing text
from the expected citations. The timing check uses this full inventory and is
marked `MachineBound`.

Calibration distinguishes recovery of the published citations from exact set
equality. The historical sets omit valid unique filenames and documentation
citations, and expand two ambiguous basenames (`Program.cs` and
`review-host-update.ts`). DS-1 keeps unique filename resolution as specified,
reports both measurements, and does not rewrite the published wave to match the
implementation. The original 50/55 exact-set acceptance target therefore remains
a historical-data discrepancy, separate from citation recovery and pair F1.
