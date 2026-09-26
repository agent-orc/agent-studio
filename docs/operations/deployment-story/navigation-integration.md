# Navigation integration for AGT-2906

Status: I10 publication change prepared on 26 September 2026 in the task worktree. Managed-repository integration and the AGT-2906 reference update follow this delivery. The concept-only run was restricted to one Dossier directory.

## North star map

In `docs/operations/nordstern/index.html`, section 5 (Map of current Dossiers), the entry sits immediately after the AGT-W49 link and before the AGT-W51 link. Section 3 decisions and section 6 history are unchanged. Published entry:

- Title: Deployment story
- Published path: `../deployment-story/index.html` (relative to the North star).
- Catalogue label: `operations/deployment-story/` · `AGT-W63` · source `AGT-2906`.
- Description: Operator and administrator journeys from one-box installation to additional runner hosts and remote authority, with availability, gaps and acceptance checkpoints. Authority and bus decisions remain in AGT-W49 and AGT-2905.

The single Dossier is at `docs/operations/deployment-story/`; its relative source links were updated after the move.

## Documentation index

One row was added to the Load-Bearing Entry Points table of `docs/start/README.md`, beside the deployment entries. It points to [the published Dossier](index.html) using `../operations/deployment-story/index.html` from the index.

## Related decisions

AGT-W49's deployment section links this journey and AGT-W65 (the AGT-2905 bus Dossier) while retaining its open operations-boundary decisions and pending status. AGT-W18 remains the remote-publication authority and AGT-W57 remains delivery/recovery context.

## Card references

The AGT-2906 task references must name `docs/operations/deployment-story/index.html` in both `results/deliverables.md` and `status.md` through the application concept-Dossier API. The API validates the path against the managed project repository. Its update must follow integration of this Dossier move; the pre-integration request returned HTTP 400 because that repository still lacks the new path. There is one canonical Dossier.
