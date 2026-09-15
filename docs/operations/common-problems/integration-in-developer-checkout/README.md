---
id: integration-in-developer-checkout
title: "Integration refuses to merge because the developer checkout is dirty"
status: fixed
first-seen: 2026-09-15T00:00:00Z
last-seen: 2026-09-15T00:00:00Z
severity: blocker
category: git
tags: [git, integration, worktree, dirty-checkout, dev-checkout, merge, acceptance]
affects:
  - backend/Features/Git/GitService.cs
  - backend/Features/Git/IntegrationWorktree.cs
  - backend/Features/Pipeline/MergeIntoDevelopRunner.cs
  - backend/Features/Runner/WorktreeTaskLifecycle.cs
related-tasks: [AGT-2832]
related-adrs: []
---

# integration-in-developer-checkout

**What.** A reviewed card passes review and then fails to integrate with

```
Integration working tree has uncommitted changes; refusing to merge.
Dirty files: backend/tests/QualityStudio.Api.Tests/QualityRunReportFactoryTests.cs,
docs/code-review-capability-research-2026-09-12.md
```

The named files have nothing to do with the delivery. They are edits somebody
made days earlier in the project's own checkout (for QS-100 on 2026-09-15:
`C:\Projects\quality-studio`, edits from 2026-09-12). The card sits in Human
Review with a red integration badge until a human cleans a working tree that
was never part of the delivery.

The same root cause produced two other incident shapes:

- QS-102 and a separate integration-branch divergence (both reported on the
  AGT-2832 card without a date): the integration branch diverged after Studio
  fast-forwarded whatever branch the developer checkout happened to have
  checked out.
- A merge that did succeed silently rewrote files in the developer's working
  tree, so an editor with unsaved buffers fought the platform for the same
  files.

**Why.** Integration resolved its working directory with
`GitService.ResolveRepoRootForWatchPath`, which returns the project's
*configured repository path* - the developer checkout. Every integration
mutation then ran there:

- `SynchronizeIntegrationBranch` checked out the integration branch and
  `merge --ff-only`-ed it from origin,
- `MergeBranchIntoIntegration` checked it out again and merged the delivery
  into that working tree,
- the in-run path (`WorktreeTaskLifecycle.Integrate`) fast-forwarded *the
  branch that checkout had checked out*, whichever it was,
- and a rollback after a failed build gate `reset --hard`-ed it.

Because a merge into a dirty tree would entangle the operator's in-flight edits
with the delivery, the merge primitives refused up front - correctly, given
where they were running. The refusal was the symptom; running there at all was
the defect. A shared checkout has two writers (a human and the platform) and no
protocol between them, so every one of these incidents was a matter of timing.

**Fix (AGT-2832).** Studio owns its own integration checkout.

- `IntegrationWorktreeService` prepares a linked git worktree at
  `<root>/<project>/_integration` (default root:
  `%TEMP%/ass-worktrees`, override with `Integration:WorktreeRoot`). It is
  created on demand on a **detached HEAD** and reset - in-progress merge or
  rebase aborted, `reset --hard`, `clean -fd` - before every integration.
  `IntegrationWorktreePolicy` decides create / reuse / recreate / unavailable
  from four observable facts, so a crash, a deleted temp folder, or a pruned
  registration all converge on a usable worktree.
- `MergeIntoDevelopRunner` performs all of its git work in that worktree. The
  object store and refs are shared with the project repository, so local
  `task/<id>` deliveries are still visible and the integration branch still
  advances; only the working tree is different.
- When the integration branch is checked out somewhere else (typically the
  developer checkout), git refuses a second checkout of it. The merge then runs
  on a detached HEAD and publishes the result with a compare-and-swap
  `git update-ref` (`GitService.AdvanceBranchRef`). The same primitive replaced
  the in-run `merge --ff-only`, which is also why that path can no longer
  advance the wrong branch.
- If the integration worktree cannot be prepared, integration reports a typed
  error. It never falls back to the developer checkout.

**Residual, by design.** A developer whose checkout sits *on* the integration
branch keeps their files, but git reports freshly integrated files as missing
until they refresh (`git checkout .`) or move to a feature branch. Studio names
this on the project: `GET /api/git/hygiene` returns `onIntegrationBranch` and
`integrationWorktreePath` next to the existing dirty-tree counts. Those counts
are a hint only - since this fix they never block or delay an integration.

**Operating the worktree.** See
[task-integration-and-merge-workflow.md](../../../concepts/task-integration-and-merge-workflow.md#the-studio-owned-integration-worktree-agt-2832)
for the location, the reset contract, and cleanup.
