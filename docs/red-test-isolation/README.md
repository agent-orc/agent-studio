# Red test isolation (AGT-3988)

Concept Dossier: a failing test stops its area, not the line. Entry point:
[index.html](index.html). Decisions and implementation slices:
[workbench.json](workbench.json).

| File | Content |
|---|---|
| `index.html` | The Dossier (Alternatives, Recommendation, Evidence, Open decisions, Slices). |
| `workbench.json` | Status, open decisions RT-D1 to RT-D13, slices RT-1 to RT-9 with `dependsOn`. |
| `assets/studio-header-develop-red-{light,dark}--mocked-route.png` | Studio header with a mocked red develop receipt, 23 November 2026, develop 801cf4ca9. |
| `evidence/test-folder-reach.py` and `-801cf4ca9.txt` | Source folders a test file names, per test project. |
| `evidence/hold-reach.py` and `-801cf4ca9.txt` | Share of 16 to 23 November integrations a hold would stop, by granularity. |
| `evidence/capture/` | Out-of-tree Playwright config, spec and runner used for the header captures. |

Run the scripts from the repository root with `python3 -I <script>`.
