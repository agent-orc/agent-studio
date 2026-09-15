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
  detached HEAD and the branch is moved afterwards.
- **Publishing the result.** After the merge, `develop` is advanced to the new
  commit. When a checkout still holds the branch, the fast-forward is asked of
  that checkout first: git carries unrelated local modifications along and
  refuses rather than overwriting anything, so a clean checkout stays in step
  with the branch exactly as if it had pulled. Only if git refuses is the ref
  advanced directly, compare-and-swapped against the tip observed before the
  merge. A gate rollback reverses both halves and returns a checkout that was
  fast-forwarded along, while it is clean and still on the rolled-back commit.
- **Shared object store.** The worktree shares the repository's `.git`, so
  branches, tags, and the resulting commit graph are the same objects the
  developer checkout sees. The origin push
  (`post-merge-into-develop-push`) is a pure ref operation and keeps running from
  the registered checkout.

## The local worktree run

A local coding run integrates differently: its task branch was already rebased
onto the integration tip inside the run's own worktree, so nothing has to be
merged. `WorktreeTaskLifecycle.Integrate` therefore advances the integration
branch by reference through `GitService.FastForwardIntegrationBranch` and needs
no integration worktree at all. It used to run `git merge --ff-only` inside the
developer checkout, which silently required that checkout to have the
integration branch out and failed whenever a local modification touched a
delivered file. The publication rule is the same one the delivery merge uses:
fast-forward the checkout that holds the branch when git allows it, otherwise
advance the ref.

## Consequences for a dirty developer checkout

- Uncommitted changes never block an integration and are never discarded.
- If the edits do not overlap the delivery, the checkout is fast-forwarded and
  keeps them.
- If they do overlap, git refuses the fast-forward: the branch still advances,
  the edits stay exactly as they are, and that checkout is simply behind its
  branch until the person commits, stashes, or resets on their own terms.
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
