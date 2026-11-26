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

## DS-4 policy replay command

The offline test host runs the five DS-3 strategies through `DispatchPlanPolicy`.
It does not start the backend or change project runtime settings. Build the host
once, then use `--no-build` so stdout contains only JSON:

```sh
dotnet build tools/dispatch-replay/DispatchReplay.Host.csproj
dotnet run --no-build --project tools/dispatch-replay/DispatchReplay.Host.csproj -- \
  --project AgentStudio \
  --fixture backend.Tests/Fixtures/dispatch/replay/wave-2026-11-17.json \
  --slots 4 --durations unit --lag 1 --check in-flight
```

For another recorded wave, export historical card facts from the application API
into a `DispatchReplayRecording` JSON file. Use the web JSON names of
`backend/Features/Dispatch/Replay/DispatchReplayModels.cs`: `project`, `cards`,
and optional `hubFiles`. Each card includes `waveId`, the DS-3 `ready` facts,
`forecastText`, `capturedAt`, `baseSha` and `resultSha`. The forecast text must
have been captured at or before the wave start. Cited paths can stand in for
text when those are the recorded historical input. Never export a current
prompt and present it as historical evidence. `baseSha` is the recorded delivery
diff base, excluding changes synced from integration; both revisions must be
full object ids. When DS-1 has delivery evidence for the same result SHA, its
recorded merge base takes precedence. The diff includes deleted code files.
The recording must include every card of the selected cohort, including cards
that never conflicted. The conflict ledger alone cannot identify that cohort.

```sh
dotnet run --no-build --project tools/dispatch-replay/DispatchReplay.Host.csproj -- \
  --project AgentStudio --recording /tmp/waves.json \
  --repo /path/to/project-repo --store /path/to/dispatch-store \
  --wave wave-id --slots 4 --durations size --lag 1 --check running
```

Replace `--wave wave-id` with `--from 2026-11-17T00:00:00Z --to
2026-11-18T00:00:00Z` for a half-open lane-entry window. A missing dated index
fails explicitly. Forecasts use the latest index at or before wave start, never
a current forecast. Only pre-start conflict records seed the Historian. During
simulation, measured shared-file exposures update its private ledger when cards
integrate. Earlier source waves in the recording supply source partner means.
The command reads the store and Git without writing either. Export through the
application API; do not read or edit workspace task folders.

Output is one JSON document with `schema: dispatch-replay.v1`, `mode: replay`,
wave identity and start, index revision, simulation options, a greedy real-file
conflict-free bound, and every strategy's raw criteria, schedule, policy plans,
scores, plain value and gated value. Exit code 2 reports invalid options or
missing evidence on stderr. Options accept `--name value` pairs. Duration is
`unit` or `size` (1 plus real code files divided by 15), lag is non-negative,
and check mode is `running` or `in-flight` (the default, DS-D10 B).

DS-D7 B uses weights 30, 25, 10, 10, 10, 5, 5, 5 and gate 0.8. As in the
Dossier replay, own cost and explanation are unmeasured and excluded from the
weighted denominator. Rounds per card is a token proxy, shared real files are a
conflict upper bound, and lead time is the median simulated time to integration.
Values are null below 40 cards. The bound is greedy and uses the same slots,
durations, dependencies, exposure mode and lag, with real file forecasts and no
hub exclusions. With a running-only check and lag, even this bound can conflict.

The checked-in 17 November fixture is a byte-for-byte copy of the evidence.
Its recorded forecasts are used directly. Its lane entries are tied, pins and
dependencies absent, and the ledger starts cold. Source priors use the Dossier
stand-ins: 17 files for DP/IN and 5 for RV. These assumptions are in the JSON.
DS-3 includes exclusive marks, confidence fallback and dependency chains; its
Packer does not recut or package cards. Consequently the five policy rows are
not the six exploratory Python rows. The original script, evidence and published
figures remain unchanged. DS-9 owns recut and the later slices own runtime
admission, shadow and live rollout.
