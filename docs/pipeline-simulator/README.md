# Pipeline simulator (AGT-4028)

Concept Dossier: a world in the Studio where cards walk through Ready, Coding,
Review and Integration, live and as what-if runs, with throughput, bottleneck,
token and quota charts. Start at [index.html](index.html). Decisions PS-D1 to
PS-D12 and slices SIM-1 to SIM-9 are in [workbench.json](workbench.json).

| File | Content |
|---|---|
| `index.html` | Decision page: scope, questions, first answers, Alternatives, Recommendation, Evidence, Open decisions, Slices. |
| `model.html` | Entities, phases, loops, strategies, parameters (measured or assumed, source, refresh), replay check, research cards, cost. |
| `world.html` | Live and what-if modes, the isometric world, following one card, visual language mapped to Studio tokens. |
| `surfaces.html` | Seven charts including the raw-token timeline contract, comparing two runs, placement in the project hub, neighbouring surfaces, judging dispatcher and guardian personas. |
| `evidence/wave_model.py` | First-order discrete-event model of one wave under four strategies. Standard library only. |
| `evidence/output-6ad4f5f0e.txt` | Its recorded output at develop 6ad4f5f0e. |
| `assets/throughput-cycle-time-history-only-{light,dark}--mocked.png` | Throughput & cycle time, captured 24 November 2026 by the unchanged `frontend/e2e/project/project-cycle-time-panel.spec.ts` (route mock), cropped to the content panel. |
| `assets/token-analysis-recorded-spend-{light,dark}--pinned-fixture.png` | Token analysis, captured 24 November 2026 by the unchanged `frontend/e2e/workspace/token-analysis.spec.ts` (pinned fixture), cropped to four panels. |

The recorded output is reproduced exactly by these six runs from the repository
root, in this order, separated by an empty line:

```sh
M="python3 -I docs/pipeline-simulator/evidence/wave_model.py --runs 200"
$M
$M --set review_slots=9
$M --set codex_round_share=0.5
$M --set review_min=60 --set review_max=75
$M --set wave=50
$M --set review_min=60 --set review_max=75 --set review_slots=9
```

The captures came from one run of
`npm --prefix frontend run e2e -- e2e/project/project-cycle-time-panel.spec.ts e2e/workspace/token-analysis.spec.ts`
with `PROJECT_CYCLE_TIME_RESULTS_DIR` and `JOB_RESULTS_DIR` pointing at a
scratch folder. No spec was added or changed.

Other Dossiers are named by repository path, not linked, so that the concept
review snapshot holds only this directory. Each HTML file stays below the 40,000-character cut of the review diff.

The published evidence inventory is [evidence/publication.md](evidence/publication.md).
It records all four inherited product captures with SHA-256 and dimensions.
This revision preserves the original captures; it does not claim a new product
capture run. Current document-render screenshots and validation results are
collected under the AGT-4028 job results, alongside a byte-identical review
copy of this Dossier. The repository directory remains the canonical delivery.
