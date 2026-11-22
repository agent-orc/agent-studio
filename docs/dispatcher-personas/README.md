# Dispatcher personas (AGT-3974)

Concept Dossier. Start at [index.html](index.html), the decision page with the
scope, the role, the starter personas, the replay, the alternatives, the
recommendation, the evidence and the open decisions. Decisions and slices are in
[workbench.json](workbench.json). The replay data, script and transcripts are in
[replay/](replay/README.md).

## What the concept review reads

The concept review gets two inputs, and `review_input.py` ports both with their
limits:

- The snapshot of `index.html`, `workbench.json` and every file they link
  (`backend/Features/Runner/ConceptDossierReviewMaterial.cs`): 64 files,
  256,000 characters a file, 512,000 in total. The Studio reads it from its own
  checkout, which holds this Dossier since AGT-3974 was integrated.
- The diff of a delivery against the integration branch
  (`runner/SourceFacts/MergeBaseDiffMaterial.cs`), cut to 40,000 characters a
  file (`runner/ReviewAspectInputBudget.cs`).

The first delivery of AGT-3974 had no snapshot, so its page of 117,000
characters, its wave file of 135,000 and its descriptor reached the review only
as a cut diff. Since integration the snapshot holds every file whole (about
335,000 characters), and a later delivery diff shows only its changed lines.
These rules keep it that way:

- A follow-up delivery edits files in place and keeps each file's diff under
  38,000 characters, a margin under the cut. Splitting or moving an integrated
  file would put its whole old text into the diff.
- Other Dossiers are named by repository path, never linked: one relative link
  pulls that Dossier and everything it links into the snapshot.
- Every file of this directory is reached by a link from `index.html`, so the
  snapshot names it.
- A concept delivery changes only this directory.

## Files and commands

- [review_input.py](review_input.py) prints both review inputs and fails when a
  file a reviewer must read would be missing, cut, over the margin or not
  reached.
- [replay/check_dossier.py](replay/check_dossier.py) checks the workbench
  contract, the links of `index.html`, the slice separation and the replay
  table, then runs `review_input.py`; its output is
  [replay/output-check.txt](replay/output-check.txt).

From the repository root, standard library only:

```sh
python3 -I docs/dispatcher-personas/review_input.py    # review input only
python3 -I docs/dispatcher-personas/replay/check_dossier.py
```
