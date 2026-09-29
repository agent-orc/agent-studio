# Integration Worktree

## Headline

**Integration runs in a working tree Studio owns, never in the checkout a person
works in.** Every delivery merge into the configured integration branch happens
in a dedicated worktree of the same repository. Uncommitted changes in the
developer checkout no longer refuse an integration, and a successful merge no
longer switches that checkout's branch.

## Why

QS-100 passed review and then integration refused: *"Integration working tree has
uncommitted changes; refusing to merge. Dirty files:
backend/tests/QualityStudio.Api.Tests/QualityRunReportFactoryTests.cs,
docs/code-review-capability-research-2026-09-12.md"*. Both files were unrelated
edits from three days earlier in `C:\Projects\quality-studio`, the developer
checkout. Earlier incidents (QS-102, integration branch divergence) had the same
shape: integration mutated, and was blocked by, a working tree that belongs to a
person rather than to the platform.

The refusal itself is correct - merging into a dirty tree entangles someone
else's in-flight edits with the delivery. The mistake was the location. A local
project's integration has no business in the developer checkout at all.

## Where it lives

The path is derived from the repository path, never stored, so an existing
project needs no migration and a restarted backend finds the worktree it created
before ([backend/Features/Git/IntegrationWorktreePolicy.cs](../../../backend/Features/Git/IntegrationWorktreePolicy.cs)):

1. `<parent of the repository>/.agent-studio-integration/<repo-name>-<digest>` -
   a hidden sibling container next to the project, on the same volume.
   `C:\Projects\quality-studio` therefore integrates in
   `C:\Projects\.agent-studio-integration\quality-studio-1a2b3c4d`.
2. `<temp>/agent-studio-integration/<repo-name>-<digest>` - the fallback when
   the parent directory cannot be written to.

The digest is a short hash of the absolute repository path, so two projects whose
folders share a name never share a slot. Neither candidate is inside the
checkout, so the worktree can never show up as dirt in the project's own status.
When the parent directory itself lies inside a git repository (a checkout nested
in another one), the order is reversed and the temporary root is used first: a
sibling container would be untracked content in that outer repository.

## How it works

[backend/Features/Git/IntegrationWorktreeProvider.cs](../../../backend/Features/Git/IntegrationWorktreeProvider.cs)
prepares the slot before every integration, and
[backend/Features/Pipeline/MergeIntoDevelopRunner.cs](../../../backend/Features/Pipeline/MergeIntoDevelopRunner.cs)
runs the whole merge - origin synchronization, merge, build gate, rollback -
against the returned path.

- **Lifecycle.** The observed slot maps to exactly one action: create it, reuse
  it, or recreate it (registered but the directory vanished, the `.git` link is
  gone, or a leftover directory occupies the slot). The developer checkout is
  refused as a slot under every combination of facts.
- **Reset per integration.** The worktree is detached at the integration branch,
  hard reset, and cleaned (`node_modules` survives). It belongs to Studio, so
  discarding its content is always safe. A crashed integration therefore cannot
  poison the next one.
- **Always detached.** A linked worktree that checks a branch out takes that
  branch away from every other checkout of the repository - the developer could
  no longer run `git checkout develop`. The integration worktree stays on a
  detached HEAD.
- **The integration lane.** The worktree shares every branch ref with the
  developer checkout, so a merge that moved `develop` itself would sit on the
  developer's `develop` for the whole build gate. Integration therefore works on
  its own ref, `refs/agent-studio/integration/<branch>` (AGT-2996). Before each
  merge the lane is brought up to the published branch (`origin/<branch>`, or
  the local branch for a repository without an origin): it is seeded or
  fast-forwarded, keeps gated merges whose push is still pending, and reports a
  lane that diverged from origin instead of overwriting either tip. The merge,
  the build gate, and a red-gate rollback move only the lane,
  compare-and-swapped against the tip observed before the merge. Commits someone
  made on the developer checkout's local `develop` are never part of the lane.
- **Publishing the result.** Only the integration push worker publishes
  `develop`: it pushes the exact SHA the gate approved from the lane to
  `origin/develop` (`merge-into-develop push enqueued ... approved=<sha>`, then
  `status=pushed`). See [When the developer checkout moves](#when-the-developer-checkout-moves).
- **Shared object store.** The worktree shares the repository's `.git`, so
  branches, tags, the lane, and the resulting commit graph are the same objects
  the developer checkout sees. The origin push
  (`post-merge-into-develop-push`) is a pure ref operation and keeps running from
  the registered checkout.

## When the developer checkout moves

The developer checkout's local `develop` (and `main`, for a release target) is
fast-forwarded exactly once per integration: after the gate passed **and** the
integration push worker published the merge. Until then it stays on the
pre-merge tip, for the whole gate window, so a push from that checkout can
never publish an un-gated merge. A failing gate rolls back the lane only; the
checkout never saw the rejected merge.

At that moment the push worker compares the checkout's branch with the
published SHA:

| Local branch | What happens |
|---|---|
| Behind the published SHA | Fast-forwarded to it. A checkout that holds the branch is asked first, so its working tree follows; git refuses rather than overwriting an overlapping local edit, and then only the ref moves. |
| Already contains the published SHA, and origin carries everything it has | Left as it is. |
| Ahead of `origin/<branch>` (someone committed there), whether or not it already contains the published SHA | Left untouched. A warning names the local SHA, the `origin/<branch>` SHA, and the published SHA. |
| On origin but on another line than the published SHA | Left untouched. |

When no push worker will ever run for the result - the project disabled the
`post-merge-into-develop-push` step - nothing is published and the checkout is
not moved at all. The gated result stays on `refs/agent-studio/integration/<branch>`;
the lane logs that and later integrations build on it.

Recovering a lane that diverged from origin (the integration error names the
lane): the gated merges on the lane were never published. Drop the lane with
`git update-ref -d refs/agent-studio/integration/<branch>` in the project
checkout; the next integration seeds it from `origin/<branch>` again, and the
affected cards retry their integration.

## The local worktree run

A local coding run integrates differently: its task branch was already rebased
onto the integration tip inside the run's own worktree, so nothing has to be
merged. `WorktreeTaskLifecycle.Integrate` therefore advances the integration
branch by reference through `GitService.FastForwardIntegrationBranch` and needs
no integration worktree at all. It used to run `git merge --ff-only` inside the
developer checkout, which silently required that checkout to have the
integration branch out and failed whenever a local modification touched a
delivered file. It has no build gate and no integration lane of its own, so it
advances the local branch directly: fast-forward the checkout that holds the
branch when git allows it, otherwise advance the ref.

## Consequences for a dirty developer checkout

- Uncommitted changes never block an integration and are never discarded.
- The checkout moves only after the published push (see
  [When the developer checkout moves](#when-the-developer-checkout-moves)).
- If the edits do not overlap the delivery, the checkout is fast-forwarded and
  keeps them.
- If they do overlap, git refuses the fast-forward: the branch still advances,
  the edits stay exactly as they are, and that checkout is simply behind its
  branch until the person commits, stashes, or resets on their own terms.
- Commits made on the checkout's `develop` are never merged, pushed, or moved
  by Studio.
- Studio never switches the branch of the developer checkout.

## Operations

- **Disk.** One additional checkout per integrated project, next to the project.
  It carries no build output: the build gates create their own isolated
  worktrees.
- **Removing it.** `git worktree remove <path>` from the project checkout, or
  delete the directory and run `git worktree prune`. The next integration
  recreates the slot; nothing durable is lost, because every result lives in the
  shared object store.
- **Moving a project.** The slot is derived from the repository path, so a moved
  or renamed project simply gets a new slot on its next integration. The old
  directory is stale and can be removed as above.
- **When preparation fails** (read-only parent, no writable temp, a repository
  without a commit), the merge step fails visibly with the reason. Integration
  never falls back to the developer checkout.

## Related

- [Commit / Push doctrine](commit-push-doctrine.md)
- [Pipeline domain map](../../system/domains/pipeline.md)
- [Parallel task execution (ADR-0052)](../../concepts/parallel-task-execution.md)
