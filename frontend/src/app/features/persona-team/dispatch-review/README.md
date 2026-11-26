# Dispatch review

The existing Team route hosts the per-project review at
`#/workspace/team/dispatch%3A<project-id>`. Team links use registry ids;
Dispatch API calls use the scanner's project display name. Dispatcher Sheet
uses the same projection, scoped to its workspace and persona.

DS-D1 through DS-D10 use option B. Review presents those choices and the
DS-11 lease controls; it does not grant authority, recut cards, or compute policy scores.
The server owns normalization, the comparison cohort, eligibility, and gates.
The source switch selects stored modes and always retains the baseline.
Observed live cohorts remain separate because they can cover different cards.

The project review shows the current lease independently of its historical
metrics window. A human Owner can pin an eligible live persona, stop
alternation, or undo the named guard fallback. Writes carry the current fence
and, for undo, the guard id. A failed write keeps the last confirmed lease and
asks the Owner to reload. Pin and stop retain guard monitoring; undo restores
the displaced persona and the previous controls. The backend still enforces active scoped
grants, rollout gates and every T2 bypass. Packer remains in shadow with proposals.

Review weights are workspace revisions. The eight integer weights must total
100 and the gate must be between 0.5 and 1. A human Owner can save; API
permissions remain authoritative. Reset changes the editor only. Revision
selection asks the server to rescore the window. Failed saves preserve edits.

Missing evidence stays unmeasured. The DS-7 projection does not expose cards
planned, parallel pair totals, or rounds per card, so Sheet identifies those
fields as unavailable. Sync/conflict card counts are inferred from the raw
conflict rate and integrated-card denominator. Replay conflicts are estimates,
not observed ledger entries. Strategy bounds are read-only because the current
API has no dispatcher parameter write endpoint.

The colocated `testing/dispatch.playwright.cjs` is focused Playwright coverage.
Run it with `npm --prefix frontend run e2e -- --config=src/app/features/persona-team/dispatch-review/testing/dispatch.playwright.config.cjs`.
It uses the standard isolated prebuilt stack. It covers weight revisions,
errors, sources, attribution, the sheet, both themes, and phone layout.
