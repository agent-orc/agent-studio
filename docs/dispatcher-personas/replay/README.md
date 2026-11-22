# Dispatcher replay

Evidence for the Dossier `docs/dispatcher-personas/index.html` (AGT-3974); [../README.md](../README.md) lists every Dossier file.

- [wave-2026-11-17.json](wave-2026-11-17.json): the 57 playthrough cards
  AGT-3890 to AGT-3946 of 17 November 2026, rebuilt from git at develop
  `d3c4eed65`. Per card it holds the real code files, the cited files and the
  forecast files, the keyword areas of `IntakeRunner.DetectTaskAreas`, the
  severity, and the develop sync commit count. The `createdFrom.method` field
  explains each field. It contains repository paths only, not finding text.
  The file holds the wave fields and lists the card parts in key order, so that
  every file reaches a reviewer whole:
  [cards-1](wave-2026-11-17.cards-1.json) (AGT-3890 to AGT-3900),
  [cards-2](wave-2026-11-17.cards-2.json) (AGT-3901 to AGT-3914),
  [cards-3](wave-2026-11-17.cards-3.json) (AGT-3915 to AGT-3926),
  [cards-4](wave-2026-11-17.cards-4.json) (AGT-3927 to AGT-3943),
  [cards-5](wave-2026-11-17.cards-5.json) (AGT-3944 to AGT-3946).
- [replay.py](replay.py): runs the six strategies of the Dossier on the wave and prints
  pair detection quality, the replay results for unit and size-weighted
  durations, and the plain and gated weighted values. It then prints today's
  prompt-path rule as an unscored upper bound, and the exposure window: an
  integration lag of 1 and 2 card durations with the check modes `running`
  (today's pick-gate) and `inflight` (every started, unintegrated card).
  It joins the card parts in the listed order. Standard library only.
- [check_dossier.py](check_dossier.py): mirrors the workbench contract (keys, bounded slices,
  `dependsOn` order, concept sections), checks that slices sharing an area are
  ordered by `dependsOn`, applies today's pick-gate scope rule to the
  promoted prompts of the DS slices and PT-13 to PT-20 to show which pairs the
  platform keeps apart, checks that the card parts join to 57 distinct cards in
  key order, compares every cell of the replay results table in `replay.html`
  with the output of `replay.py`, and runs `../review_input.py`.
- [output-4-slots.txt](output-4-slots.txt), [output-6-slots.txt](output-6-slots.txt),
  [output-check.txt](output-check.txt): the full
  output of the three commands below at develop `d3c4eed65`, so the figures can
  be read without running anything.
- [remote-claim-evidence.md](remote-claim-evidence.md): the ten wave cards whose runner reports name a
  remote result ref, with the delivery commits, which all exist in git.

```sh
python3 -I docs/dispatcher-personas/replay/replay.py            # 4 slots
python3 -I docs/dispatcher-personas/replay/replay.py \
  docs/dispatcher-personas/replay/wave-2026-11-17.json 6        # 6 slots
python3 -I docs/dispatcher-personas/replay/check_dossier.py     # contract and separation
```

The model and its limits are documented at the top of `replay.py`. A shared
code file is an upper bound for a conflict. Rounds per card stand in for tokens.
