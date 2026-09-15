# Occurrences

Chronological log. Newest at the top. UTC timestamps. One row per observation.

| When (UTC) | Task / context | Agent / CLI | Affected paths | Notes |
|---|---|---|---|---|
| 2026-09-15T00:00:00Z | QS-100 | Integration / acceptance | `backend/tests/QualityStudio.Api.Tests/QualityRunReportFactoryTests.cs`, `docs/code-review-capability-research-2026-09-12.md` | Card passed review, then integration refused: "Integration working tree has uncommitted changes; refusing to merge." Both dirty files were unrelated edits from 2026-09-12 in the developer checkout `C:\Projects\quality-studio`. Opened AGT-2832. |
| date not recorded | Integration branch divergence | Integration / in-run worktree path | `WorktreeTaskLifecycle.Integrate` | Reported on the AGT-2832 card without a date. The in-run fast-forward targeted whatever branch the developer checkout had checked out rather than the configured integration branch, so the integration branch diverged from what the board reported. |
| date not recorded | QS-102 | Integration / acceptance | developer checkout working tree | Reported on the AGT-2832 card without a date. Same shape: integration ran in the project's own checkout and collided with its state. |

## AGT-2832 fix session

| When (UTC) | Task / context | Agent / CLI | Affected paths | Notes |
|---|---|---|---|---|
| 2026-09-15 | AGT-2832 | Claude Opus 5 | `backend/Features/Git/IntegrationWorktree.cs`, `backend/Features/Git/GitService.cs`, `backend/Features/Pipeline/MergeIntoDevelopRunner.cs`, `backend/Features/Runner/WorktreeTaskLifecycle.cs`, `backend/Features/Runner/ProjectRunner.cs` | Moved every integration mutation into a Studio-owned linked worktree; replaced the checkout-bound fast-forwards with a compare-and-swap ref update; surfaced the developer checkout's state as a project hint. See [measures.md](./measures.md). |
