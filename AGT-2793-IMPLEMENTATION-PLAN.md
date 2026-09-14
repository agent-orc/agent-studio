# AGT-2793 Phases 2-4 Implementation Plan

## Overview
This document outlines the implementation of phases 2-4 for the branch lifecycle reclamation task:
- Phase 2: Trigger wiring (integration, promotion, archive)
- Phase 3: Evidence recording and task history updates
- Phase 4: Integration and replay tests + documentation updates

## Phase 2: Trigger Wiring

### Trigger Points
1. **After Integration into Develop**
   - Location: `MergeIntoDevelopRunner` after successful merge (line ~350-400)
   - Action: Call `GitBranchRetentionService.ReclaimForTaskAsync(project, taskKey, integrationBranch="develop")`

2. **After Promotion to Main**
   - Location: TBD - need to find promotion logic
   - Action: Call `GitBranchRetentionService.ReclaimForProjectAsync(project)` for all merged commits

3. **When Card is Archived**
   - Location: `TaskTransitionService` or archive handler
   - Action: Call `GitBranchRetentionService.ReclaimForTaskAsync(project, taskKey, isArchived=true)`

### Implementation Strategy
- Extend `GitBranchRetentionService` with `ReclaimForTaskAsync` method
- Create `BranchReclaimTriggerService` to coordinate trigger points
- Safety: Log failures, don't block integration/promotion/archive paths
- Rate limiting: Batch deletions (max 100 per push)

## Phase 3: Evidence Recording

### Data Collection
For each deleted ref:
- Ref name, target SHA, reason, timestamp
- Proof of reachability (ancestor check result)
- Task key (for task-related deletions)
- Project and repository name

### Storage
1. Per-project reclaim report: `reports/git-branch-reclaim-<timestamp>.jsonl`
   - One JSON line per deletion
   - Format: {ref, sha, reason, taskKey, timestamp, reachable}

2. Task history update: Append to task's `status.md` or timeline
   - Record: "Refs reclaimed after integration/promotion/archive: count, total size"

### Implementation
- Extend `BranchRetentionAction` to include `TaskKey` and proof metadata
- Create `BranchReclaimReportWriter` to write per-project reports
- Create `TaskHistoryUpdater` to update task records
- Implement batching in `GitBranchRetentionService`

## Phase 4: Tests and Documentation

### Tests
1. **Bare-Remote Integration Test** (`GitBranchRetentionBareRemoteTests`)
   - Seed all six namespaces (task, runner, delivery, results, salvage, quarantine)
   - Run integration + promotion + archive
   - Assert exactly expected refs disappear
   - Verify proof commits stay reachable
   - Check reclaim report is written

2. **Replay/Reissue Test** (`GitBranchRetentionReplayTests`)
   - Create remote delivery with results ref
   - Integrate and delete results ref
   - Verify task can be replayed with deleted results ref (SHA still reachable)
   - Check task history is not broken

### Documentation
1. Update `docs/concepts/task-integration-and-merge-workflow.md`
   - Section "Branch cleanup" (currently empty placeholder)
   - Add lifecycle table: namespace, retention policy, cleanup trigger
   - Document evidence recording and report format

2. Update deployment/promotion runbook
   - Note that automatic reclamation runs at integration/promotion/archive
   - Link to reclaim report location
   - Add troubleshooting: what to do if reclamation fails

3. Update `docs/start/README.md`
   - Add entry for new branch reclamation docs
   - Link to integration/merge workflow and runbook

## Files to Create/Modify

### New Files
- `backend/Features/Git/BranchReclaimTriggerService.cs` - Trigger coordinator
- `backend/Features/Git/BranchReclaimReportWriter.cs` - Report generation
- `backend.Tests/GitBranchRetentionBareRemoteTests.cs` - Integration test
- `backend.Tests/GitBranchRetentionReplayTests.cs` - Replay test
- `docs/concepts/branch-reclamation-lifecycle.md` - Policy documentation

### Modified Files
- `backend/Features/Git/GitBranchRetention.cs` - Add evidence recording and reclaim methods
- `backend/Features/Pipeline/MergeIntoDevelopRunner.cs` - Wire integration trigger
- `backend/Features/Tasks/TaskTransitionService.cs` - Wire archive trigger
- `backend/Features/Pipeline/*Promotion*.cs` - Wire promotion trigger (TBD)
- `docs/concepts/task-integration-and-merge-workflow.md` - Update "Branch cleanup" section
- `docs/operations/deployment-runbook.md` - Add reclamation notes (if exists)
- `docs/start/README.md` - Add documentation entries

## Implementation Order
1. Extend `GitBranchRetention.cs` with evidence recording and reclaim methods
2. Create `BranchReclaimTriggerService` coordinator
3. Create `BranchReclaimReportWriter` for output
4. Wire triggers into MergeIntoDevelopRunner, promotion, and archive paths
5. Write integration test with bare remote
6. Write replay/reissue test
7. Update documentation
8. Run tests and verify dry-run reports

## Safety Guardrails
- Reclaim failures are logged but don't block integration/promotion/archive
- Dry-run mode available for testing and auditing
- Rate limiting: max 100 refs per remote delete push
- Protected refs (main, develop, release/*, v*) never deleted
- Checked-out refs never deleted
- Proof of reachability verified before deletion
- Report written even if individual deletions fail
- Task history updated with summary (not per-ref details)

## Success Criteria
- All refs in six namespaces are properly classified
- Integration/promotion/archive trigger the reclamation
- Evidence is recorded in per-project reports
- Deletion report is integrated into task history
- Integration and replay tests pass
- Documentation is complete and accurate
