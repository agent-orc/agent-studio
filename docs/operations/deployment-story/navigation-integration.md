# Navigation integration for AGT-2906

Status: I10 publication is present. Both AGT-2906 task files name the canonical path, as verified through the stable application API on 11 October 2026. The concept-only run was restricted to one Dossier directory.

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

AGT-W49's deployment section links this journey and AGT-W65 (the AGT-2905 bus Dossier) while leaving the recorded status of its operations-boundary decisions D1 to D4 unchanged; W49's Decisions section and implementation log stay authoritative. AGT-W18 remains the remote-publication authority and AGT-W57 remains delivery/recovery context.

## Card references

The AGT-2906 task references name `docs/operations/deployment-story/index.html` in both `results/deliverables.md` and `status.md`. Both file reads returned HTTP 200 through the stable application API on 11 October 2026. The API validates a new write against its configured project repository. A repeat POST on that date returned HTTP 400 because that repository copy lacked the path; it did not change either task file. There is one canonical Dossier.


The concept-Dossier write request uses the stable Studio API with
`X-Client-Id: local-default` and `Content-Type: application/json`:

```http
POST /api/tasks/AGT-2906/concept-dossier?project=PROJ-002

{"path": "docs/operations/deployment-story/index.html"}
```

This concept-Dossier operation writes both AGT-2906 `status.md` and
`results/deliverables.md` when the configured repository contains the path. Read both using
`GET /api/tasks/AGT-2906/files/status.md?project=PROJ-002` and
`GET /api/tasks/AGT-2906/files/results/deliverables.md?project=PROJ-002`.
Those reads already return the exact canonical path in both files. A repeat
write remains unavailable until the server's repository copy contains the
published path. Do not create a second Dossier or assign a new key.
