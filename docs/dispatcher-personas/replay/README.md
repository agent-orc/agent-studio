# Dispatcher replay

Evidence for the Dossier `docs/dispatcher-personas/index.html` (AGT-3974).

- `wave-2026-11-17.json`: the 57 playthrough cards AGT-3890 to AGT-3946 of
  17 November 2026, rebuilt from git at develop `d3c4eed65`. Per card it holds
  the real code files, the cited files and the forecast files, the keyword areas
  of `IntakeRunner.DetectTaskAreas`, the severity, and the develop sync commit
  count. The `createdFrom.method` field explains each field. It contains
  repository paths only, not finding text.
- `replay.py`: runs the six strategies of the Dossier on the wave and prints
  pair detection quality, the replay results for unit and size-weighted
  durations, and the plain and gated weighted values. Standard library only.

```sh
python3 -I docs/dispatcher-personas/replay/replay.py            # 4 slots
python3 -I docs/dispatcher-personas/replay/replay.py \
  docs/dispatcher-personas/replay/wave-2026-11-17.json 6        # 6 slots
```

The model and its limits are documented at the top of `replay.py`. A shared
code file is an upper bound for a conflict. Rounds per card stand in for tokens.
