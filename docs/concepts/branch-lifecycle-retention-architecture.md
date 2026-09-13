# Branch Lifecycle Retention Architecture

Status: Design document for extended automatic cleanup of git branches in task-execution namespaces.

## Executive Summary

This document designs an extension to the existing `BranchRetentionPolicy` and `GitBranchRetentionService` to automate cleanup of remote branches in the four task namespaces (`task/*`, `runner/*`, `delivery/*`, `results/*`, `salvage/*`, `quarantine/*`). The current system only classifies `task/*` and `runner/*` refs; the extension must handle the immutable result and salvage architecture introduced by fenced remote deliveries while maintaining evidence durability before deletion.

**Key architectural constraint**: Deletion is idempotent via proof-of-containment, not by holding refs forever. The SHA remains reachable in git history even after the named ref is deleted, so recovery systems (DurableHandoffRecovery, RemoteDeliveryRefPolicy) must tolerate missing refs but can reconstruct context from the immutable result envelope and fenced facts.

---

## Current State Analysis

### Existing Policy and Service

**`BranchRetentionPolicy` (GitBranchRetention.cs:32-61)**:
- Pure decision function: classifies a single ref based on immutable facts
- Current scope: only `task/*` and `runner/*` namespaces (hardcoded in `IsManagedBranch`)
- Deletion criteria: old enough (age > retention window) AND merged into both `develop` and `main`
- Explicitly ignores: `agent-studio/*` namespaces (97% of refs)

**`GitBranchRetentionService` (GitBranchRetention.cs:101-384)**:
- Orchestrates one retention pass per project
- Fetches origin, lists local and remote refs, evaluates policy, deletes with recheck
- Outputs: `BranchRetentionRunReport` with per-action reasons (kept/deleted)
- Current patterns: only `refs/heads/task` and `refs/heads/runner` local; `refs/remotes/origin/task` and `origin/runner` remote
- Immutability: rechecks merge status immediately before deletion (lines 255-297)

**`GitBranchRetentionHostedService` (GitBranchRetention.cs:391-449)**:
- Recurring host-owned loop, runs periodically
- Also calls `ArchivedResultRefPruner.RunOnce()` each cycle

### Existing Result Ref Cleanup

**`ArchivedResultRefPruner` (ArchivedResultRefPruner.cs)**:
- **Scope**: `agent-studio/results/*` and `agent-studio/quarantine/*` local remote-tracking copies **only**, for archived cards
- **Approach**: indexed delivery refs from `AttemptAuthorityService`, never derives refs from task keys
- **Durability**: reads indexed attempt records; does not delete remote refs, only local copies
- **Limitations**: 
  - Only archives (cards in state `7-archive`)
  - Only local copies (`refs/remotes/origin/*`)
  - Only refs explicitly indexed in `AttemptIndexedDeliveryRef`
  - Never contacts origin

### Ref Namespace Structure (from GitWorkspace)

```
runner/<runner>/<key>                                  [work branch]
  └─ canonical, collision branches (e.g., ...-collision-sha1-sha2)

agent-studio/results/<attempt>/fence-<n>/<sha>        [immutable result]
agent-studio/salvage/<runner>/<key>/<attempt>/fence-<n>/<sha>  [generation-scoped backup]
agent-studio/quarantine/<runner>/<key>/<attempt>/<fence>/<sha>  [quarantined on error]
```

Lines in GitWorkspace.cs:
- `FencedSalvageBranch` (1038-1043): `agent-studio/salvage/{runner}/{key}/{attempt}/fence-{token}/{sha}`
- `QuarantineBranch` (1045-1056): `agent-studio/quarantine/{runner}/{key}/{attempt}/{fence}/{sha}`
- `FencedGitRefs.ImmutableResult` (contracts): `refs/heads/agent-studio/results/{attempt}/fence-{fence}/{sha}`

### Integration Workflow Context

From `task-integration-and-merge-workflow.md`:
- **Sequential path** (`MaxParallelism == 1`): no task branch, agent edits shared main checkout
- **Parallel path** (`MaxParallelism >= 2`): isolated worktree on `task/<id>`, commits at run end, auto-integrates before review
- **Remote delivery**: fenced refs integrated before Human Review via `RemoteDeliveryIntegrationCoordinator`
- **Task terminal states**: 
  - `6-completed`: integrated or manual operator override
  - `7-archive`: durable task history preserved
- **Worktree cleanup**: deferred; only if `task/<id>` is ancestor of `IntegrationBranch` (WorktreeTaskLifecycle.cs)

---

## Policy Design: Extended Classification

### Namespace-Specific Policies

Extend `BranchRetentionPolicy` to classify refs into six categories, each with distinct deletion criteria:

#### 1. **Task Integration Branches** (`task/<key>`, `runner/<runner>/<key>*`)

**Deletion rule**: Delete when:
- Ref is old enough (age > retention window, default 7 days)
- Tip is ancestor of `develop` (or `IntegrationBranch`)
- Tip is ancestor of `main`
- Checked-out branch is not blocked
- (Unchanged from current policy)

**Reasoning**:
- These are transient working branches; once integrated, they're redundant
- Parallel execution and collision branches share the same criterion
- Develop + main containment ensures no loss of history

**Note**: `task/*` is obsolete in the parallel path; `runner/*` is the canonical work branch. Both remain eligible for the same policy for backward compatibility.

---

#### 2. **Immutable Result Refs** (`agent-studio/results/<attempt>/fence-<n>/<sha>`)

**Deletion rule**: Delete when:
- Ref is **old enough** (age > 30 days, configurable; or based on task terminal state)
- **Either**:
  - Tip is ancestor of `main` (or latest stable boundary), **OR**
  - Task is archived and creation timestamp > 30 days old
- Remote ref only (never delete the local copy during recheck; the remote is the source of truth)

**Reasoning**:
- Immutable result refs are proof of reviewed delivery
- After integration into main, the ref is redundant (commit remains reachable by main's history)
- Archive state signals no further access; 30-day retention preserves recovery window
- Fenced structure (attempt + fence token + SHA) makes the ref immutable; deletion is safe once ancestry is verified

**Task integration status integration**:
- If `integration.status == integrated` and tip is in `main`, eligible
- If task is archived, age check alone suffices (task transition time, not commit time)
- `integration.deliveryRef` field identifies the canonical result ref

---

#### 3. **Salvage Refs** (`agent-studio/salvage/<runner>/<key>/<attempt>/fence-<n>/<sha>`)

**Deletion rule**: Delete when:
- **Either**:
  - Tip is ancestor of `main` **AND** tip commit age > 1 day, **OR**
  - Ref age > 14 days (measured from creation or commit timestamp)
- Task must be terminal (archived or completed with integration status `integrated`)

**Reasoning**:
- Salvage refs are recovery backups; once the result reaches main, the backup is redundant
- But main integration takes time (merge delay); 1-day grace avoids premature deletion during integration window
- 14-day fallback ensures stale salvage is cleaned even if main integration stalls (escalation case)
- Generation-scoped structure means later runs cannot reuse the same ref; it's safe to delete after its generation's task completes

**Task integration status integration**:
- Terminal state: `integration.status == integrated` (task completed) or task archived
- Canonical source for salvage facts: `reconciliation` field in `WorktreeTeardownResult`

---

#### 4. **Quarantine Refs** (`agent-studio/quarantine/<runner>/<key>/<attempt>/<fence>/<sha>`)

**Deletion rule**: Delete when:
- **Either**:
  - Ref age > 30 days **AND** no escalation/human-review reference to this ref, **OR**
  - Tip is ancestor of `main` **AND** ref age > 7 days
- Task must be terminal

**Reasoning**:
- Quarantine is a temporary hold for failed or disputed runs
- 30-day default retention allows human review and escalation workflow
- Escalations (e.g., card moved to `escalated` lane) may reference a quarantined ref for recovery; deletion is gated on absence of such references
- Main integration + 7-day grace: if error was recovered and integrated, cleanup accelerates
- Generation-scoped like salvage; not reused across attempts

**Implementation challenge**: need to query escalation records or task-server state to check for live references to the quarantine ref before deletion.

---

#### 5. **Delivery Refs** (Proposed, not yet in codebase)

**Note**: The task description mentions `delivery/<key>` namespace, but the codebase shows no such namespace. The task likely meant `results/*` or the canonical `runner/*` delivery. This analysis assumes no separate `delivery/*` namespace; if it exists as a separate convention, apply the same rules as Immutable Result Refs.

---

### Extended `BranchRetentionFacts` Record

Add fields to support per-namespace criteria:

```csharp
public sealed record BranchRetentionFacts(
    string Branch,
    DateTimeOffset? TipCommittedAtUtc,
    bool CheckedOut,
    bool DevelopAvailable,
    bool MainAvailable,
    bool MergedIntoDevelop,
    bool MergedIntoMain,
    // Extended fields:
    string? Namespace,                  // "task", "runner", "results", "salvage", "quarantine", etc.
    DateTimeOffset? RefCreatedAtUtc,    // from git object creation time or task metadata
    TaskTerminalState? TaskTerminalState,  // "completed", "archived", null
    string? IntegrationStatus,          // from integration.status: "integrated", "pending", etc.
    bool HasEscalationReference,        // true if escalation/review chain references this ref
    bool IsRemoteRef);                  // true if origin/* tracking ref
```

---

### Extended `BranchRetentionPolicy.Evaluate()`

Refactor to dispatch on namespace:

```csharp
public static BranchRetentionDecision Evaluate(
    BranchRetentionFacts facts,
    DateTimeOffset now,
    TimeSpan minimumAge,
    BranchRetentionConfig config)  // config supplies per-namespace retention windows
{
    if (!IsManagedBranch(facts.Branch, facts.Namespace))
        return BranchRetentionDecision.UnsupportedNamespace;
    
    var namespace = ClassifyNamespace(facts.Branch, facts.Namespace);
    return namespace switch
    {
        Namespace.TaskOrRunner => EvaluateTaskOrRunner(facts, now, minimumAge),
        Namespace.ImmutableResult => EvaluateImmutableResult(facts, now, config),
        Namespace.Salvage => EvaluateSalvage(facts, now, config),
        Namespace.Quarantine => EvaluateQuarantine(facts, now, config),
        _ => BranchRetentionDecision.UnsupportedNamespace,
    };
}
```

Each namespace-specific evaluator returns typed `BranchRetentionDecision` enums (existing or new).

---

## Trigger Points and Batching

### When to Run Cleanup

**Option A (Recommended)**: Unified periodic + on-state-change

1. **Scheduled cleanup** (every 24 hours, configurable):
   - `GitBranchRetentionHostedService.ExecuteAsync` existing periodic loop
   - Single call to extended `GitBranchRetentionService.RunOnce()`
   - Low-latency; all namespaces covered

2. **On task terminal state transition** (event-driven):
   - When task moves to `6-completed` (integrated) or `7-archive`:
   - Enqueue a targeted cleanup for refs associated with that task
   - Target: `runner/<runner>/<key>*`, salvage/quarantine for that key, result refs from `AttemptIndexedDeliveryRef`
   - Allows faster deletion for completed deliveries (hours, not 24h)

3. **Escalation cleanup** (new):
   - When escalation is resolved (e.g., manual review removes escalation marker):
   - Reevaluate quarantine refs; candidate for immediate cleanup if no longer referenced

**Option B (Simpler, batch cleanup only)**: Retain current 24h schedule; accept slower cleanup for results/salvage/quarantine until next periodic pass. Results refs are already handled by `ArchivedResultRefPruner` for archived cards.

**Recommendation**: Option A with fast-path for task terminal states. Keeps the system responsive while avoiding O(n) per-task subscriptions.

---

### Batching and Rate Limiting

**Problem**: Thousands of refs across projects; GitHub rate limits; transactional safety.

**Solution**:

1. **Per-project sequential batch**:
   - Process one project at a time (existing)
   - Within a project, batch deletions by namespace (results, then salvage, then quarantine)
   - Max 100 deletions per namespace per project per pass (configurable)
   - If a project exceeds the batch limit, mark it for retry on next cycle

2. **Remote deletion batching**:
   - `git push origin :refs/heads/X :refs/heads/Y ...` (multiple refs in one push)
   - Each `GitService.DeleteRemoteBranch()` should be upgraded to support bulk:
   ```csharp
   DeleteRemoteBranchesAtTips(
       string root, 
       IReadOnlyList<(string branch, string sha)> candidates)
   ```
   - Reduces network round-trips and improves throughput

3. **Local ref deletion**:
   - `git for-each-ref --format='...'` pipe to `git update-ref --delete` in batches
   - Or `git update-ref delete` in a loop with bounded batching

4. **Idempotency and safety**:
   - Each deletion is rechecked immediately before execution (existing `DeleteAfterRecheck`)
   - If ref changed or no longer exists, log and skip (not an error)
   - If deletion fails (network, permission), log and retry on next pass

5. **Progress and logging**:
   - Log per-batch: `deleted=N skipped=M failed=L reason=...`
   - Report final counts per project and namespace

---

## Evidence Durability: Recording Deletion Proofs

**Principle**: Before deleting a ref, record immutable proof that deletion was justified. If the deletion itself fails, the record documents that it was attempted.

### Proof Record Format

Create a per-project, per-run **deletion report** in a durable location:

**Path**: `<project-repo>/.agent-studio/retention/YYYY-MM-DD.jsonl` (append-only JSONL)

**Record schema**:
```json
{
  "timestamp": "2026-09-13T12:00:00Z",
  "ref": "refs/heads/agent-studio/results/attempt-1/fence-1/abc123...",
  "refNamespace": "results",
  "refCreatedAt": "2026-09-10T08:30:00Z",
  "tipSha": "abc123...",
  "tipCommittedAt": "2026-09-10T08:25:00Z",
  "taskKey": "AGT-1234",
  "reason": "integrated-into-main",
  "facts": {
    "mainAncestry": true,
    "developAncestry": true,
    "integratedAt": "2026-09-12T15:00:00Z",
    "taskTerminalState": "completed"
  },
  "deleted": true,
  "deletedAt": "2026-09-13T12:00:05Z",
  "error": null
}
```

**Fields**:
- `timestamp`, `ref`, `tipSha`: immutable proof of the attempt
- `reason`: human-readable deletion justification (e.g., "integrated-into-main", "archived-30-days", "stale-salvage")
- `facts`: the `BranchRetentionFacts` that justified the decision
- `deleted`: true if `git push origin :ref` succeeded; false if failed or skipped
- `error`: null (success) or error message (network, permission, etc.)

**Durability**:
- Append before each deletion attempt (not after)
- Use atomic file operations (write to `.tmp`, move to final)
- Survive process crashes; replay on restart reads the log to avoid re-recording

### Report Lifecycle

1. **During cleanup pass**: append one record per ref evaluated for deletion
2. **At end of pass**: emit a summary report (count of deleted/failed/skipped per project/namespace)
3. **Quarterly archival**: compress old reports; keep recent 90 days online, older in archive
4. **Query endpoint** (optional): expose `/api/git/retention-report?project=X&namespace=results&days=30` to inspect recent deletions

### Integration with Git History

**Alternative (lighter-weight)**: Instead of a file report, append a signed "retention-proof" commit to an audit branch:

```
commit <sha>
Author: Agent Studio Retention <noreply@agent-studio.invalid>
Date: 2026-09-13T12:00:00Z

    audit: delete refs/heads/agent-studio/results/...
    
    deleted: 15 refs
    namespace: results
    reason: integrated-into-main
```

**Pros**: Proof is in git history forever; recoverable via git log.
**Cons**: Extra commit per run; clutters the audit branch; requires a dedicated branch.

**Recommendation**: Use `.agent-studio/retention/YYYY-MM-DD.jsonl` in the working tree (not a git branch). Less invasive; still durable if stored in the shared project cache.

---

## Handling Missing Refs in Dependent Systems

**Question**: Which components need to tolerate missing refs after deletion?

### Analysis of Ref Consumers

#### 1. **`FencedGitRefs.ImmutableResult`** (contracts/TaskServer.Contracts)

**Usage**: Construction only. The static method builds the ref name; it does not fetch or verify the ref.

**Impact of missing ref**: None. The method is pure; deletion is downstream.

**Action**: None needed.

---

#### 2. **`DurableHandoffRecovery`** (runner/DurableHandoffRecovery.cs, lines 74-201)

**Usage**: 
- Line 112: `envelope.ImmutableResultRef` is **stored** in the durable result envelope
- Line 105: `await workspace.ReadDependencyIdentitiesAsync(ct)` fetches submodules/LFS (separate from ref)
- Line 176: `workspace.TeardownAfterHandoffAsync()` uses `secured.ResultSha`, not the ref name

**Logic flow**:
1. Recover from outbox; fetch (or reconstruct) the result envelope
2. Use `envelope.ResultSha` (the immutable commit hash) to verify worktree cleanliness
3. Use `envelope.ImmutableResultRef` as metadata for logging/auditing
4. Never queries `ls-remote` or checks ref existence

**Impact of missing ref**: None for recovery logic. The ref name is metadata; the SHA is what matters.

**Confidence**: High. DurableHandoffRecovery reconstructs context from the envelope and task metadata, not from live refs.

**Action**: None needed. Test to confirm ref-deletion does not crash recovery.

---

#### 3. **`RemoteTaskRunner`** (runner/RemoteTaskRunner.cs)

**Usage**: 
- Lines ~250-300: publishes result envelope and delivery proof to task server
- Encodes `DeliveryProof` with the ref name and SHA
- Does not query refs after publication

**Impact of missing ref**: The ref may be deleted after publication, but the envelope preserves the SHA and historic ref name for audit. The delivery itself is proven by SHA containment in main/develop, not by ref existence.

**Confidence**: High. Remote runner publishes immutable envelope; deletion is outside its lifetime.

**Action**: None needed.

---

#### 4. **`RemoteDeliveryRefPolicy`** (backend/Features/Tasks/RemoteDeliveryRefPolicy.cs)

**Usage**: 
- Lines 80-109: ranks delivery refs (immutable result, recovery branch, salvage branch)
- Line 122: `Select` walks candidates and calls `verify(candidate.Ref)` to check if the ref carries the result SHA
- Line 127: `verify()` likely calls `git ls-remote` or similar

**The `verify` function** (not shown in the snippet, but used by `DeliveryRefResolver`):
- If the ref is deleted, `ls-remote` returns empty
- The policy treats missing refs as unverifiable, not as disproof
- Falls back to next candidate in the list

**Impact of missing ref**: Graceful fallback. If immutable result ref is deleted but salvage branch still exists, the policy will accept the salvage branch as the delivery ref.

**Example scenario**:
1. Task is completed, result integrated into main
2. Retention service deletes `agent-studio/results/attempt-1/.../sha`
3. Human review later re-opens the task (escalation)
4. `RemoteDeliveryRefPolicy.Select()` tries immutable result ref → missing
5. Falls back to salvage ref → found
6. Review proceeds with salvage ref as the delivery

**Confidence**: Medium. The policy is designed to handle missing refs, but needs testing to confirm the fallback chain works end-to-end.

**Action**: 
- Test case: delete immutable result ref, then attempt human review with salvage ref fallback
- Verify `DeliveryVerificationStatus` handles missing refs as unverifiable, not as error
- Confirm order of candidates ensures a suitable fallback exists (salvage branch should be preserved longer than immutable result)

---

#### 5. **Task-Server Replay** (beyond codebase scope)

**Impact of missing ref**: Task server stores the delivery ref name in `review-subject.json`. If that ref is deleted before replay, the server must:
- Accept the stored SHA as a valid fallback
- Query git history (`git log -S <sha>`) instead of checking the ref

**Confidence**: Unknown. Depends on task server's replay logic (not in this codebase).

**Action**: Coordinate with task server team. At minimum, ensure `review-subject.json` always preserves the result SHA, not just the ref name.

---

### Deletion Safety: Ref Ordering

To maximize fallback coverage, delete refs in this order:

1. **Immutable result refs first** (`agent-studio/results/...`)
   - Safest; result is indexed and preserved elsewhere
   - If deletion fails, no impact on recovery (other refs still exist)

2. **Salvage refs second** (`agent-studio/salvage/...`)
   - Recovery branches are collision-resolution evidence; keep longer if immutable result is gone
   - If `agent-studio/salvage/.../collision-...` exists, preserve it longer (contains divergent tip evidence)

3. **Quarantine refs last** (`agent-studio/quarantine/...`)
   - Escalation evidence; oldest refs deleted last

---

## Dry-Run Mode

**Design**: Add a `dryRun` flag to the policy and service.

### Service-Level Flag

```csharp
public BranchRetentionRunReport RunOnce(
    CancellationToken cancellationToken = default,
    bool dryRun = false)
{
    // ... existing logic ...
    
    var action = dryRun
        ? new DryRunAction(...)       // Log only, do not execute git delete
        : new RealAction(...);         // Execute git delete
    
    // Emit report with dryRun indicator
}
```

### Output Changes

Extend `BranchRetentionProjectReport`:

```csharp
public sealed record BranchRetentionProjectReport(
    // ... existing fields ...
    bool DryRun,
    IReadOnlyList<(string Ref, BranchRetentionDecision Decision)> WouldDelete);
```

### Example Report (dryRun = true)

```
git-branch-retention-dry-run project=myproject repository=/repo/path 
  deleted=0 kept=42 wouldDelete=5
  
Would delete:
  - refs/heads/agent-studio/results/attempt-1/.../sha (reason: integrated-into-main)
  - refs/heads/agent-studio/salvage/runner-1/task-1/.../sha (reason: stale, 15 days old)
  ...
```

### Operator Workflow

1. Operator runs `/api/git/retention/dry-run?project=myproject` 
2. Inspect the would-delete list; confirm thresholds and selection
3. Run `/api/git/retention/execute?project=myproject&confirmed=true` to execute
4. Review deletion report in `.agent-studio/retention/YYYY-MM-DD.jsonl`

---

## Implementation Order and Critical Files

### Phase 1: Policy Extension (Week 1-2)

**Goal**: Extend `BranchRetentionPolicy` to classify all namespaces and make deletion decisions per namespace.

**Files**:
1. **`backend/Features/Git/GitBranchRetention.cs`**:
   - Extend `BranchRetentionFacts` with new fields (`Namespace`, `RefCreatedAtUtc`, `TaskTerminalState`, `IntegrationStatus`, `HasEscalationReference`, `IsRemoteRef`)
   - Refactor `BranchRetentionPolicy.Evaluate()` to dispatch by namespace
   - Add namespace-specific evaluators: `EvaluateImmutableResult()`, `EvaluateSalvage()`, `EvaluateQuarantine()`
   - Add config record: `BranchRetentionConfig` (per-namespace retention windows, etc.)
   - Update `IsManagedBranch()` to accept namespace parameter

2. **`backend/Features/Git/GitBranchRetention.cs` (decision enums)**:
   - Extend `BranchRetentionDecision` enum with new reasons if needed (e.g., `IntegratedIntoMain`, `TooYoungForQuarantine`, `HasEscalationReference`)

3. **Unit tests**:
   - `backend.Tests/GitBranchRetentionTests.cs`: add test cases for each namespace

**Acceptance criteria**:
- Policy correctly classifies `agent-studio/results/*` refs based on main integration
- Policy correctly classifies `agent-studio/salvage/*` refs based on age + terminal state
- Policy correctly classifies `agent-studio/quarantine/*` refs based on escalation reference
- All existing `task/*` and `runner/*` tests pass unchanged

---

### Phase 2: Service Integration (Week 2-3)

**Goal**: Wire extended policy into `GitBranchRetentionService` and handle deletion.

**Files**:
1. **`backend/Features/Git/GitBranchRetention.cs` (service)**:
   - Extend `RunRepository()` to list refs from all namespaces: `agent-studio/results`, `agent-studio/salvage`, `agent-studio/quarantine`
   - Build `BranchRetentionFacts` for each ref, including namespace-specific fields:
     - `Namespace` from ref path pattern matching
     - `RefCreatedAtUtc` from git object time or task metadata
     - `TaskTerminalState` from task scanner (join task key to task record)
     - `IntegrationStatus` from `TaskIntegrationStatusService`
     - `HasEscalationReference` from escalation query
     - `IsRemoteRef` from whether it's in `refs/remotes/origin/*`
   - Batch deletions by namespace (max 100 per namespace per project)
   - Update `DeleteAfterRecheck()` to support all namespaces

2. **Service dependencies**:
   - Inject `TaskScannerService` to resolve task records by key
   - Inject `TaskIntegrationStatusService` to get `integration.status`
   - Inject `EscalationService` or equivalent to check for live escalation references
   - Inject `ILogger` (already present)

3. **Durable reporting**:
   - Create `GitRetentionProofRecorder` service that appends deletion records to `.agent-studio/retention/YYYY-MM-DD.jsonl`
   - Inject into `GitBranchRetentionService`
   - Call `ProofRecorder.RecordDeletion(...)` before each deletion attempt

4. **Bulk deletion optimization** (optional, Phase 2b):
   - Upgrade `GitService.DeleteRemoteBranch()` to support bulk:
   ```csharp
   public DeleteResult DeleteRemoteBranchesAtTips(
       string root, 
       IReadOnlyList<(string branch, string sha)> candidates,
       CancellationToken ct)
   ```
   - Use `git push origin :ref1 :ref2 :ref3` in batches

**Acceptance criteria**:
- Service lists and classifies refs from all four namespaces
- Facts are correctly resolved from task records and integration status
- Deletions are batched (max 100 per namespace)
- Deletion proof is recorded before execution
- All existing tests pass; new integration tests cover mixed namespace scenarios

---

### Phase 3: Escalation Reference Checking (Week 3-4)

**Goal**: Implement `HasEscalationReference` fact and gate quarantine deletion.

**Files**:
1. **`backend/Features/Tasks/EscalationService.cs` (or equivalent)**:
   - Add method: `HasLiveReferenceToQuarantineRef(string ref, CancellationToken ct)`
   - Query escalation records (task lane history, escalation markers) for references to the ref
   - Return true if any active escalation/review holds the ref

2. **`backend/Features/Git/GitBranchRetention.cs`**:
   - Call escalation service when evaluating quarantine refs
   - Pass result in `BranchRetentionFacts.HasEscalationReference`
   - `EvaluateQuarantine()` returns `Delete` only if `!facts.HasEscalationReference`

3. **Integration test**:
   - Create task, escalate it, verify quarantine ref is retained
   - Resolve escalation, verify quarantine ref becomes eligible

**Acceptance criteria**:
- Escalation query is implemented and tested
- Quarantine refs held by active escalations are never deleted
- Resolved escalations release quarantine refs for cleanup

---

### Phase 4: Fast-Path Task Terminal Transition (Week 4, optional)

**Goal**: Delete refs promptly when task reaches terminal state, not just on 24h cycle.

**Files**:
1. **`backend/Features/Tasks/TaskTransitionService.cs`**:
   - On move to `6-completed` or `7-archive`, enqueue a targeted cleanup task
   - Pass task key and project to cleanup service

2. **`backend/Features/Git/GitBranchRetention.cs`**:
   - Add method: `RunForTaskAsync(string project, string taskKey, CancellationToken ct)`
   - Filters refs to those matching the task key
   - Runs the same policy and deletion logic, but scoped to one task
   - Returns typed result for telemetry

3. **Queuing infrastructure** (may already exist):
   - Use existing background task queue (if present) or a simple async queue
   - Bounded concurrency (e.g., 3 concurrent task cleanups per project)

**Acceptance criteria**:
- Task terminal transition enqueues cleanup
- Task-scoped cleanup runs and deletes eligible refs
- No regression in existing periodic cleanup

---

### Phase 5: Testing and Validation (Week 5-6)

**Goal**: Comprehensive end-to-end testing of all scenarios.

**Test files**:
1. **`backend.Tests/GitBranchRetentionTests.cs`**:
   - Extend existing test class with new namespaces
   - Test matrix: each namespace × deletion criterion (age, integration, escalation)
   - Test mixed scenario: multiple refs across namespaces, different eligibility

2. **Integration test fixture**:
   - Create temporary git repo with refs in all four namespaces
   - Mock task records, integration status, escalation records
   - Run retention pass, verify correct deletions

3. **End-to-end test** (may require `MachineBound` marker):
   - Real git repo, real task server client
   - Simulate task lifecycle: run → integrate → archive → cleanup
   - Verify refs are deleted at expected times
   - Verify deletion proof is recorded

4. **Regression tests**:
   - Existing `task/*` and `runner/*` cleanup must still work
   - All 97% of refs in `agent-studio/*` that were ignored must now be classified

**Acceptance criteria**:
- All new tests pass
- No regression in existing tests
- Deletion proof records are generated and readable
- Dry-run mode works and matches execution

---

### Critical Dependencies and Assumptions to Validate

#### 1. **Task Integration Status Service** (`TaskIntegrationStatusService`)

**Assumption**: `TaskIntegrationStatusService.GetStatusAsync(taskKey)` or similar returns `integration.status` field.

**Validation**:
- Read file: `/backend/Features/Tasks/TaskIntegrationStatusService.cs`
- Confirm method exists and is accessible
- If not, implement a lightweight wrapper to read `integration.status` from task folder JSON

**Risk**: If integration status is not readily available, facts resolution will block. Mitigate by reading `<task-folder>/integration.jsonl` or similar directly.

---

#### 2. **Task Scanner Service** (`TaskScannerService`)

**Assumption**: Can query tasks by project and state; returns records with creation/modification times.

**Validation**:
- Read file: `/backend/Features/Tasks/TaskScannerService.cs`
- Confirm methods to list tasks by project, filter by state, read timestamps

**Risk**: If timestamps are not precise, age-based criteria will be unreliable. Mitigate by falling back to git commit time if task time is missing.

---

#### 3. **Escalation Service**

**Assumption**: Exists and can query live escalations.

**Validation**:
- Search codebase: `grep -r "escalation" /backend --include="*.cs" | grep -i "service\|repository"`
- If no service, create a lightweight query over task lane history or escalation markers

**Risk**: Escalation queries may be expensive (O(n) projects). Mitigate by caching or indexing.

---

#### 4. **Git Service Bulk Operations**

**Assumption**: `GitService` can be extended to support bulk deletion in one push.

**Validation**:
- Read file: `/backend/Features/Git/GitService.cs`
- Confirm signature of `DeleteRemoteBranch()` and `DeleteRef()`
- Plan extension to bulk methods

**Risk**: Bulk operations require careful error handling (some refs fail, others succeed). Plan per-ref status in result.

---

#### 5. **Ref Object Timestamps**

**Assumption**: Git ref object creation time can be reliably extracted via `git for-each-ref --format='%(creatordate:iso8601)'` or similar.

**Validation**:
- Test locally: `git for-each-ref --format='%(objectname) %(creatordate:iso8601)' refs/heads/test`
- Confirm timestamp is accurate and available for all ref types

**Risk**: Some refs may have no creator date. Mitigate by falling back to commit time.

---

## Architectural Assumptions to Validate

1. **Immutability of fenced refs**: Once published, a fenced result ref never changes. Deletion is safe because the same SHA is preserved in main/develop ancestry.
   - **Validation**: Code review of `FencedGitRefs` and `SecureForHandoffAsync` to confirm no mutations.

2. **Main integration latency**: Task integration into main can take minutes to hours after completion. Deletion timing must account for this window.
   - **Validation**: Check `task-integration-and-merge-workflow.md` for exact windows; measure in production to set safe grace periods.

3. **Escalation reference lifetime**: Escalations are resolved within days. Quarantine refs do not need to survive beyond 30 days.
   - **Validation**: Query escalation metrics; confirm 30-day window is safe.

4. **Archive finality**: Cards in `7-archive` state are immutable; task metadata can be safely deleted/archived.
   - **Validation**: Code review of archive workflow; confirm no post-archive mutations to task records.

5. **Shared repo path consistency**: All projects share a consistent repo path pattern; task keys are globally unique within a project.
   - **Validation**: Review `GitWorkspace.CachePathForProject()` and `SafeSegment()` to confirm mapping is bijective.

---

## Configuration and Deployment

### Config Schema (appsettings.json)

```json
{
  "GitRetention": {
    "Enabled": true,
    "IntervalHours": 24,
    "RetentionDays": 7,
    "DryRun": false,
    "ProofRecording": {
      "Enabled": true,
      "StoragePath": ".agent-studio/retention"
    },
    "Namespaces": {
      "taskAndRunner": {
        "RetentionDays": 7,
        "RequireBothDevelopAndMain": true
      },
      "immutableResults": {
        "RetentionDays": 30,
        "DeleteAfterMainIntegration": true,
        "GracePeriodDays": 1
      },
      "salvage": {
        "RetentionDays": 14,
        "DeleteAfterMainIntegration": true,
        "GracePeriodDays": 1,
        "RequireTerminalTask": true
      },
      "quarantine": {
        "RetentionDays": 30,
        "DeleteAfterMainIntegration": true,
        "GracePeriodDays": 7,
        "RequireTerminalTask": true,
        "BlockIfEscalated": true
      }
    },
    "Batching": {
      "MaxRefsPerNamespacePerProject": 100,
      "MaxBulkDeletesPerPush": 50
    }
  }
}
```

---

## Rollout Strategy

1. **Phase 1**: Deploy with `DryRun: true` and `Enabled: false`. Operator can manually invoke `/api/git/retention/dry-run?project=X` to inspect what would be deleted.

2. **Phase 2**: Enable on a single project; monitor deletion reports and escalation rate. Ensure fallback chain works (missing immutable result, salvage available).

3. **Phase 3**: Enable globally with conservative retention windows (30 days for results, 21 for salvage, 30 for quarantine).

4. **Phase 4**: Tighten windows based on metrics (avg integration time, escalation recovery time).

---

## Rollback and Recovery

- **If refs are deleted prematurely**: Escalation team can query `.agent-studio/retention/` log to reconstruct the ref name and SHA, then manually `git push origin <sha>:<ref>` to restore if the SHA is still in git history.
- **If escalations are unexpectedly unresolvable**: Increase `quarantine.RetentionDays` and redeploy; quarantine refs will be preserved longer.
- **If bulk deletion fails**: Individual refs are retried on next cycle; no data loss.

---

## Success Criteria

1. **Coverage**: All four namespaces are classified and cleaned.
2. **Safety**: Zero premature deletions; deletion proof is recorded.
3. **Performance**: Cleanup completes in < 5 minutes per project, even with thousands of refs.
4. **Observability**: Deletion reports are queryable; escalation impact is measurable.
5. **Resilience**: Missing refs do not crash dependent systems; fallback chains work.

---

## See Also

- `task-integration-and-merge-workflow.md` - task integration lifecycle
- `GitWorkspace.cs` - ref creation patterns
- `ArchivedResultRefPruner.cs` - existing result ref cleanup (archived cards only)
- `RemoteDeliveryRefPolicy.cs` - delivery ref candidate selection and fallback
- `DurableHandoffRecovery.cs` - recovery from missing refs
- `BranchRetentionPolicy` tests - existing policy test patterns

