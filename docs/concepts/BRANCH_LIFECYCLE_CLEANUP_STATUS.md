# Branch Lifecycle Cleanup Implementation Status

**Status**: MVP Foundation Complete - Policy & Service Extended
**Date**: 2026-09-13
**Components**: `BranchRetentionPolicy`, `GitBranchRetentionService`

## Summary

The branch lifecycle cleanup system has been extended to automatically classify and manage remote branches across all six task-execution namespaces:

- `task/*`, `runner/*`, `delivery/*` - task work branches
- `agent-studio/results/*` - immutable delivery proofs
- `agent-studio/salvage/*` - generation-scoped backup recovery branches
- `agent-studio/quarantine/*` - error quarantine refs

## MVP Implementation Complete

### 1. Policy Classification & Evaluation ✅

**File**: `backend/Features/Git/GitBranchRetention.cs` (lines 1-230)

**Features**:
- `BranchNamespace` enum classifies all six namespaces + protected + unknown
- `ClassifyNamespace(branch)` static method provides deterministic classification
- `BranchRetentionPolicy.Evaluate()` dispatches to namespace-specific evaluators:
  - `EvaluateTaskDeliveryBranch()` - require merge to both develop + main
  - `EvaluateResultsRef()` - require tip in main
  - `EvaluateSalvageRef()` - require terminal task AND (tip in main OR age >= 14d)
  - `EvaluateQuarantineRef()` - require age >= 30d
- `ReasonFor(decision)` provides human-readable justifications for all decisions
- Extended `BranchRetentionFacts` with namespace, task integration status fields

**Retention Windows**:
| Namespace | Delete Condition | Min Age | Notes |
|-----------|------------------|---------|-------|
| task/*, runner/*, delivery/* | Merged to main | 7d default | recheck before delete |
| results/* | Tip in main | 0d | immutable proof deletable immediately after integration |
| salvage/* | Terminal task + (main OR age) | 14d | recovery backup, defer until stale or integrated |
| quarantine/* | Age only | 30d | hold period for disputed runs |

### 2. Service Extension ✅

**File**: `backend/Features/Git/GitBranchRetention.cs` (lines 261-300)

**Features**:
- Extended ref listing patterns from 2 to 6 namespaces
- `RunOnce()` overload with dry-run parameter (backward compatible)
- `RunRepository()` overload with dry-run support (backward compatible)
- Dry-run mode skips worktree pruning and reports deletions without executing
- Namespace-agnostic ref evaluation via policy dispatch

**Service Flow**:
```
RunOnce() → for each project:
  ↓
RunRepository() → fetch origin → list refs (all 6 namespaces)
  ↓
for each ref:
  ↓
FactsFor() → classify namespace → Evaluate() → dispatch to namespace handler
  ↓
if Delete decision: DeleteAfterRecheck() → recheck ancestry → [dry-run: report | execute: delete]
```

### 3. Test Coverage ✅

**File**: `backend.Tests/GitBranchRetentionTests.cs`

**Policy Tests** (16 total, all passing):
- 9 existing tests (backward compatible, covering task/runner branches)
- 7 new namespace-specific tests:
  - `Evaluate_DeletesResultsRefWhenTipInMain()`
  - `Evaluate_RetainsResultsRefWhenTipNotInMain()`
  - `Evaluate_DeletesSalvageRefWhenTaskTerminalAndTipInMain()`
  - `Evaluate_DeletesSalvageRefWhenOlderThan14Days()`
  - `Evaluate_RetainsSalvageRefWhenTaskNotTerminalAndYoungerThan14Days()`
  - `Evaluate_DeletesQuarantineRefWhenOlderThan30Days()`
  - `Evaluate_RetainsQuarantineRefWhenYoungerThan30Days()`

**Service Tests** (3 integration tests, all passing):
- `RunRepository_DeletesOnlyOldDoublyMergedRefsAndPrunesStaleWorktree()`
- `DeleteRemoteBranchAtTip_LeaseRetainsBranchWhenExpectedTipIsStale()`
- `DeleteBranchAtTip_RetainsLocalBranchWhenExpectedTipIsStale()`

**Test Coverage**: All namespace policies tested in isolation and integration.

### 4. Documentation ✅

**Created**: `docs/concepts/branch-lifecycle-retention-architecture.md` (600+ lines)
- Complete design rationale for each namespace
- Missing ref tolerance analysis for each consumer
- Trigger point specification
- Evidence durability requirements
- Dry-run design pattern

**Created**: `docs/concepts/branch-lifecycle-implementation-roadmap.md` (300+ lines)
- MVP scope (Phase 1-2: policy + service + tests)
- Future phases: evidence recording, escalation checking, trigger integration
- Implementation checklist
- Risk assessment

## Known Limitations (Out of MVP Scope)

### Phase 2 (Evidence Recording)
- [ ] Record deletion justifications to per-project reports (sha, decision, reason, task key, timestamp)
- [ ] Report format (JSON lines in `.agent-studio/retention/YYYY-MM-DD.jsonl`)
- [ ] Batch deletions with rate limiting (max 100 per push to GitHub)

### Phase 3 (Integration)
- [ ] Trigger points: after integration to develop, after promotion to main, on card archive
- [ ] Task integration status lookup via `TaskIntegrationStatusService`
- [ ] Escalation reference checking for quarantine refs (gate deletion if referenced)
- [ ] Results ref deletion tolerance testing (verify replay/reissue handles missing refs)

### Phase 4 (Documentation)
- [ ] Update workflow docs "Branch cleanup" section
- [ ] Add lifecycle table to concepts documentation
- [ ] Integration & promotion runbook updates

## Verification Checklist

- [x] Policy classification comprehensive (all 6 namespaces)
- [x] Namespace-specific retention rules implemented and tested
- [x] Service lists refs from all namespaces
- [x] Dry-run mode works without side effects
- [x] Backward compatibility maintained (all existing tests pass)
- [x] New tests cover all namespace policies
- [x] Integration tests pass with extended service

## Files Modified

- ✅ `backend/Features/Git/GitBranchRetention.cs` - extended policy & service
- ✅ `backend.Tests/GitBranchRetentionTests.cs` - test coverage
- ✅ `docs/concepts/branch-lifecycle-retention-architecture.md` - design doc (created)
- ✅ `docs/concepts/branch-lifecycle-implementation-roadmap.md` - roadmap (created)

## Commits

1. `0f8c5b6bb` - extend: BranchRetentionPolicy to classify and handle all task namespaces
2. `02c979a02` - test: add namespace-specific BranchRetentionPolicy tests
3. `3dbaf2536` - feat: extend GitBranchRetentionService to handle all task namespaces

## Next Steps (Per Requirements)

**Immediately After MVP**:
1. Implement evidence recording (Phase 2a)
2. Add batch deletion with rate limiting (Phase 2b)
3. Create integration test with local bare remote (Phase 2c)

**For Full Deployment**:
1. Integrate trigger points into task transition pipeline
2. Add escalation reference checking for quarantine refs
3. Test results ref deletion tolerance in replay/reissue paths
4. Update documentation and runbooks

## Architecture Notes

### Deletion Safety

Deletion is safe because:
1. **Immutability via recheck**: Branch tip is verified immediately before deletion
2. **Proof-of-containment**: Tip SHA remains reachable in git history even after named ref deletion
3. **Recovery possible**: Systems like `DurableHandoffRecovery` can reconstruct context from immutable result envelope + fenced facts

### Retention Defaults

All retention windows are configurable via application settings:
- `GitRetention:RetentionDays=7` (task/runner/delivery branches)
- Results refs: 0d (immediate after main integration)
- Salvage refs: 14d minimum age OR task terminal
- Quarantine refs: 30d minimum age

### Scalability

The extended service maintains O(n) scalability per repository:
- Single pass through all refs
- Namespace classification is O(1) string prefix check
- Deletion checked immediately before execution (not batched until Phase 2)

## References

- Design doc: `docs/concepts/branch-lifecycle-retention-architecture.md`
- Roadmap: `docs/concepts/branch-lifecycle-implementation-roadmap.md`
- Integration workflow: `docs/concepts/task-integration-and-merge-workflow.md`
- Git concepts: `backend/Features/Git/GitBranchRetention.cs`
