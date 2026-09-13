# Branch Lifecycle Retention: Implementation Roadmap

Quick reference for implementing extended automatic cleanup of git branches in task-execution namespaces.

## First Iteration Focus

Start with **Phase 1** (policy extension) + **Phase 2** (service integration) to establish the framework. Skip escalation and fast-path triggering initially; focus on extending the periodic cleanup to all namespaces.

### Recommended MVP Scope (Phases 1-2, ~3-4 weeks)

1. **Namespace-scoped policy**: Extend `BranchRetentionPolicy` to classify `agent-studio/results/*`, `agent-studio/salvage/*`, and `agent-studio/quarantine/*`.
2. **Per-namespace retention windows**: Config-driven (results: 30 days, salvage: 14 days, quarantine: 30 days).
3. **Deletion proof recording**: Append deletion records to `.agent-studio/retention/YYYY-MM-DD.jsonl`.
4. **Batching and safety**: Batch deletions by namespace; recheck before execution; handle failures gracefully.
5. **Dry-run mode**: List proposed deletions without executing.

**Not in MVP**: escalation reference checking, fast-path task terminal triggering, bulk git push optimization.

---

## Critical Files to Modify

| File | Changes | Priority |
|------|---------|----------|
| `backend/Features/Git/GitBranchRetention.cs` | Extend `BranchRetentionFacts`, `BranchRetentionPolicy.Evaluate()`, `GitBranchRetentionService.RunRepository()` | P0 |
| `backend/Features/Git/GitBranchRetention.cs` | Add `BranchRetentionConfig` record for per-namespace windows | P0 |
| `backend/Features/Git/GitRetentionProofRecorder.cs` (new) | Record deletion proofs to `.agent-studio/retention/YYYY-MM-DD.jsonl` | P0 |
| `contracts/TaskServer.Contracts/FencedGitRefs.cs` | No changes needed; review to confirm ref structure | P2 |
| `runner/GitWorkspace.cs` | No changes needed; review fenced ref construction | P2 |
| `backend/Features/Tasks/TaskIntegrationStatusService.cs` | Review method to fetch `integration.status`; create wrapper if needed | P1 |
| `backend/Features/Tasks/TaskScannerService.cs` | Review task listing and timestamp access | P1 |
| `backend/Features/Git/GitService.cs` | Plan bulk deletion methods (Phase 2b) | P2 |
| `backend.Tests/GitBranchRetentionTests.cs` | Extend test class with namespace-specific cases | P0 |

---

## Key Design Decisions

### 1. Deletion Criterion Per Namespace

| Namespace | Criterion | Age Check | Main Integration | Terminal State | Escalation Check |
|-----------|-----------|-----------|-------------------|-----------------|-----------------|
| `task/*`, `runner/*` | Merged into both develop + main | Yes (7d) | Yes | — | — |
| `results/*` | Main integration OR archive | Age (30d) | Yes | Archive ok | — |
| `salvage/*` | Main integration + grace OR fallback age | 14d fallback | Yes (1d grace) | Required | — |
| `quarantine/*` | Age alone OR main integration | 30d | Yes (7d grace) | Required | **Yes** |

**Fallback logic**: If `main` integration fails, use age threshold (14d for salvage, 30d for quarantine).

---

### 2. Fact Resolution Order

When evaluating a ref for deletion:

1. **Namespace classification** (from ref path pattern)
2. **Fetch git metadata** (age, merge-base checks)
3. **Resolve task record** (if task key is in ref, look up task state)
4. **Fetch integration status** (if task is found)
5. **Check escalation reference** (for quarantine only)

**Performance**: Cache task records per project per pass; reuse integration status lookups.

---

### 3. Proof Recording

Record **before** deletion attempt (not after). Format: JSONL with one record per ref evaluated.

```json
{
  "timestamp": "2026-09-13T12:00:00Z",
  "ref": "refs/heads/agent-studio/results/...",
  "namespace": "results",
  "reason": "integrated-into-main",
  "deleted": true,
  "error": null
}
```

**Location**: `<project-repo>/.agent-studio/retention/YYYY-MM-DD.jsonl` (append-only).

**Durability**: Survive process crashes; replay on restart does not re-record.

---

### 4. Ref Deletion Ordering (Safety)

1. **Immutable result refs first** (safest; result indexed elsewhere)
2. **Salvage refs second** (recovery evidence; collision branches preserved longer)
3. **Quarantine refs last** (escalation evidence; oldest deleted last)

---

## Confidence Levels: Missing Ref Tolerance

| Component | Can Tolerate Missing Ref? | Confidence | Notes |
|-----------|---------------------------|-----------|-------|
| `FencedGitRefs.ImmutableResult` | Yes (pure fn) | High | Construction only; no ref lookup |
| `DurableHandoffRecovery` | Yes | High | Uses SHA from envelope, not ref name |
| `RemoteTaskRunner` | Yes | High | Publishes envelope; deleted afterwards |
| `RemoteDeliveryRefPolicy` | Yes (fallback) | Medium | Falls back to salvage ref; test required |
| Task-server replay | ? | Low | Unknown; needs task-server team confirmation |

**Required validation**:
- Test `RemoteDeliveryRefPolicy.Select()` with missing immutable result ref; confirm salvage fallback works
- Confirm task server accepts SHA-only recovery if ref is deleted

---

## Blocking Dependencies

### Hard Dependencies (must resolve before Phase 1)

1. **Task integration status availability**: Confirm `TaskIntegrationStatusService` or equivalent exists and returns `integration.status`.
   - **Mitigate**: If not available, read `<task-folder>/integration.jsonl` directly.

2. **Task scanner service**: Confirm can list tasks by project and read timestamps.
   - **Mitigate**: Use git commit time as fallback for age checks.

### Soft Dependencies (Phase 2+)

3. **Escalation service**: Confirm can query live escalations without O(n) penalty.
   - **Mitigate**: Defer escalation checking to Phase 3; treat quarantine refs as always deletable in MVP.

4. **Git service bulk operations**: Confirm can extend to support batch deletions.
   - **Mitigate**: Keep single-ref deletions in MVP; add bulk optimization in Phase 2b.

---

## Testing Strategy

### Unit Tests (Phase 1)

- `BranchRetentionPolicy.Evaluate()` for each namespace with all combinations of facts
- Test matrix: namespace × (age, integration, terminal state, escalation)
- Mock all dependency queries

### Integration Tests (Phase 2)

- Temporary git repo with refs in all namespaces
- Mock task records, integration status
- Run retention pass; verify correct deletions
- Verify deletion proof records match execution

### End-to-End Tests (Phase 5, optional)

- Real git repo, real task-server client
- Full task lifecycle: run → integrate → archive → cleanup
- Measure time to cleanup after task terminal state
- Verify escalation queries work under load

---

## Configuration Strategy

**MVP config** (conservative):
```json
{
  "GitRetention": {
    "Enabled": true,
    "DryRun": false,
    "Namespaces": {
      "immutableResults": { "RetentionDays": 30 },
      "salvage": { "RetentionDays": 14 },
      "quarantine": { "RetentionDays": 30, "BlockIfEscalated": false }
    }
  }
}
```

**Before production**: Adjust retention windows based on metrics (avg integration time, escalation recovery time).

---

## Rollout Plan

### Week 1: DryRun Phase

- Deploy with `DryRun: true` and `Enabled: false`
- Operator manually invokes `/api/git/retention/dry-run?project=X`
- Inspect proposals; refine thresholds if needed

### Week 2: Single Project Pilot

- Enable on one low-volume project
- Monitor deletion reports and system behavior
- Confirm escalation fallback chain works

### Week 3: Global Rollout

- Enable on all projects with conservative windows
- Monitor deletion rates and escalation impact
- Alert if escalation rate increases

### Week 4+: Optimization

- Analyze deletion timing vs. task lifecycle
- Tighten retention windows if safe
- Enable fast-path task terminal triggering (Phase 4)

---

## Rollback Triggers

If any of these occur, rollback to `Enabled: false`:
- **Escalation recovery fails** (deleted immutable result ref, salvage ref not available)
- **Premature deletion** (refs deleted before main integration completed)
- **Proof records corrupt** (retention JSONL unreadable)
- **Query performance issue** (retention pass takes > 10 minutes)

**Recovery**: Query `.agent-studio/retention/YYYY-MM-DD.jsonl` to identify deleted SHAs; restore refs via `git push origin <sha>:<ref>` if still in git history.

---

## Success Metrics

- ✅ All four namespaces are cleaned automatically
- ✅ No premature deletions (zero escalations due to missing refs)
- ✅ Cleanup completes < 5 minutes per project
- ✅ Deletion proof records are accurate and auditable
- ✅ Missing ref fallback chain works (RemoteDeliveryRefPolicy)
- ✅ Retention window adjustments are safe (based on metrics)

