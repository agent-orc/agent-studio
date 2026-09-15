# Measures

Fix attempts and their status. Status vocabulary: `tried`, `applied`, `works`, `regressed`.

| Status | Date (UTC) | Measure | Owner | Outcome |
|---|---|---|---|---|
| works | 2026-09-15 | `IntegrationWorktreeService` prepares and resets a Studio-owned linked worktree per project; `MergeIntoDevelopRunner` runs every git mutation there instead of in the configured repository path | AGT-2832 | A dirty developer checkout no longer reaches the merge primitives at all; `MergeRunner_WithDirtyDeveloperCheckout_IntegratesInsteadOfRefusing` fails without the change |
| works | 2026-09-15 | `GitService.AdvanceBranchRef` (compare-and-swap `update-ref`) publishes a merge that ran on a detached HEAD, and replaces the branch fast-forwards in `SynchronizeIntegrationBranch`, `MergeBranchFastForward`, and `WorktreeTaskLifecycle.Integrate` | AGT-2832 | The integration branch advances even while it is checked out elsewhere; a concurrent writer fails visibly instead of silently losing a commit |
| works | 2026-09-15 | `IntegrationWorktreePolicy.Decide` covers create / reuse / recreate / unavailable from four observable facts | AGT-2832 | A crashed merge, a deleted temp folder, or a pruned registration all converge on a usable worktree; matrix-tested |
| works | 2026-09-15 | A failed worktree preparation returns a typed integration error; there is no fallback to the developer checkout | AGT-2832 | The defect cannot silently return under load or after a partial cleanup |
| works | 2026-09-15 | `GET /api/git/hygiene` gained `integrationWorktreePath` and `onIntegrationBranch` next to the existing dirty counts | AGT-2832 | The developer checkout's state is a visible hint, never a blocker |

## Before / after

- **Before:** any uncommitted file in the project's checkout - however old and
  however unrelated - refused every integration for that project, and a
  successful integration rewrote that working tree.
- **After:** integration reads and writes only Studio's own worktree. The
  developer checkout is never checked out, merged into, reset, or blocked on.
  The one remaining effect is a checkout that sits on the integration branch
  seeing newly integrated files as missing until it refreshes; the project
  hygiene hint names that state.
