# Dispatcher personas (AGT-3974)

Concept Dossier. Start at [index.html](index.html), the decision page with the
alternatives, the recommendation, the evidence and the open decisions. The
chapters carry the detail:

- [role.html](role.html): scope, coverage of the card, what exists today, and the role.
- [personas.html](personas.html): the five starter personas and the criteria with weights and gate.
- [replay.html](replay.html): the 17 November wave and the replay, with the results table and the exposure window.
- [design.html](design.html): mockups, the cut of cards, foundation and gaps, and the slice order.

Decisions and slices are in [workbench.json](workbench.json). The replay data,
script and transcripts are in [replay/](replay/README.md).

## Why several pages

The concept review reads a first delivery as a diff, cut to 40,000 characters a
file (`runner/ReviewAspectInputBudget.cs`). Its snapshot of linked files
(`backend/Features/Runner/ConceptDossierReviewMaterial.cs`) comes from the
Studio's own checkout, which holds a Dossier only after integration. A single
page of 117,000 characters, a wave file of 135,000 and a descriptor of 46,000
reached the review cut, so these rules keep the Dossier reviewable:

- Every file stays under 38,000 characters as a diff, a margin under the cut.
- The wave cards are kept in five part files; `replay.py` joins them.
- Other Dossiers are named by repository path, never linked: one relative link
  pulls that Dossier and everything it links into the snapshot.
- Every file of this directory is reached by a link from `index.html`, so the
  snapshot names it.
- A concept delivery changes only this directory.

Integration assigns a document key and rewrites `workbench.json` with escaped
characters, which brings it to about 40,000 characters. After integration a
review reads it whole from the snapshot (256,000 characters a file), and a later
delivery diff shows only its changed lines.

## Files and commands

- [build.py](build.py) refreshes the generated regions between `<!--g:KEY-->`
  and `<!--/g-->` in the pages: the canonical article style of
  `docs/app/templates/article-document-v2.html`, minified, plus the parts of
  [src/dossier.css](src/dossier.css) a page uses, and the page list. Then it
  runs `review_input.py`.
- [review_input.py](review_input.py) ports both review inputs with their limits,
  prints them, and fails when a file a reviewer must read would be missing, cut,
  over the margin or not reached.
- [replay/check_dossier.py](replay/check_dossier.py) checks the workbench contract,
  the slice separation, the wave parts and the replay table; its output is
  [replay/output-check.txt](replay/output-check.txt).

From the repository root, standard library only:

```sh
python3 -I docs/dispatcher-personas/build.py           # regions, then the review input check
python3 -I docs/dispatcher-personas/build.py --check   # fails when a page is stale
python3 -I docs/dispatcher-personas/review_input.py    # review input only
python3 -I docs/dispatcher-personas/replay/check_dossier.py
```
