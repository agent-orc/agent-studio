# Related

- [[lineage-blocked-integration-push]] - the other half of the integration
  path: this entry is about where the merge runs, that one about why its
  result never reached `origin`.
- `docs/concepts/task-integration-and-merge-workflow.md` - the integration
  workflow of record; its "Studio-owned integration worktree" section carries
  the location, reset contract, and cleanup.
- Code: [`IntegrationWorktree.cs`](../../../../backend/Features/Git/IntegrationWorktree.cs),
  [`GitService.cs`](../../../../backend/Features/Git/GitService.cs),
  [`MergeIntoDevelopRunner.cs`](../../../../backend/Features/Pipeline/MergeIntoDevelopRunner.cs),
  [`WorktreeTaskLifecycle.cs`](../../../../backend/Features/Runner/WorktreeTaskLifecycle.cs).
- Tests: [`IntegrationWorktreeTests.cs`](../../../../backend.Tests/IntegrationWorktreeTests.cs),
  [`GitWorktreePrimitivesTests.cs`](../../../../backend.Tests/GitWorktreePrimitivesTests.cs),
  [`WorktreeTaskLifecycleTests.cs`](../../../../backend.Tests/WorktreeTaskLifecycleTests.cs).
