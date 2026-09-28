# Navigation integration for AGT-2906

Status: pending publication through I10. This file is a reviewable insertion specification, not a claim that navigation is already changed. The concept-only instruction restricts this run to one Dossier directory.

## North star map

In `docs/operations/nordstern/index.html`, section 5 (Map of current Dossiers), insert immediately after the AGT-W49 link and before the AGT-W51 link. Reuse the existing map entry markup. Do not alter section 3 decisions or section 6 history. Use this text:

- Title: Deployment story
- Path now: `../../deployment-story/index.html` (relative to the North star).
- Catalogue label: `deployment-story/` and source `AGT-2906`; replace or supplement with the actual assigned Dossier key after discovery. Never invent an AGT-W key.
- Description: Operator and administrator journeys from one-box installation to additional runner hosts and remote authority, with availability, gaps and acceptance checkpoints. Authority and bus decisions remain in AGT-W49 and AGT-2905.

If the platform has placed the Dossier at `docs/operations/deployment-story/`, use `../deployment-story/index.html` and the label `operations/deployment-story/` instead. Keep one directory and catalogue identity. Check all relative source links after any move.

## Documentation index

Add one row to the Load-Bearing Entry Points table of `docs/start/README.md`, beside the getting-started/deployment entries:

| Deployment story: operator and administrator journeys from one box to many runner hosts, current availability, option C reconciliation and recovery gates (AGT-2906) | [decision dossier](../deployment-story/index.html) |

After operations placement, the row target becomes `../operations/deployment-story/index.html`.

## Related decisions

Link the journey from AGT-W49's deployment section and align the three-form vocabulary after accepting the bus/journey decisions. Preserve its existing operations-boundary decisions and their pending status. Link the accepted AGT-2905 Dossier when its path and key are known. Keep AGT-W18 as the remote-publication authority and AGT-W57 as delivery/recovery context.

## Card references

The task references must name `docs/deployment-story/index.html` in both `results/deliverables.md` and `status.md` until placement changes the path. Use the application API for any managed-workspace mutation. After placement, update both references atomically through the platform's concept-Dossier contract. Do not keep an obsolete results-only copy as the canonical artefact.
