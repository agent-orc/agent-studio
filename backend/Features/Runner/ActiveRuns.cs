using System.Collections.Concurrent;

namespace AgentStudio.Runner;

/// <summary>
/// One in-flight coding run's claim state. At <c>MaxParallelism == 1</c> there
/// is at most one of these; every record still points at its own worktree. The
/// registry (<see cref="ActiveRuns"/>) generalizes admission to N slots
/// (ADR-0052 slice 2) without changing isolation.
///
/// <para>
/// Construction is two-stage: <see cref="CliType"/> is only known once the
/// planner has resolved the CLI, so it is settable and stamped right after the
/// claim. The remaining fields are fixed at claim time.
/// </para>
/// </summary>
internal sealed class ActiveRun
{
    private readonly TaskCompletionSource _startHandshake =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public required string JobId { get; init; }
    /// <summary>CLI driving this run; null between claim and CLI resolution.</summary>
    public string? CliType { get; set; }
    /// <summary>Primary CLI whose exhausted quota caused this run to use a fallback.</summary>
    public string? FallbackFromCliType { get; set; }
    public string? QuotaFallbackReason { get; set; }
    /// <summary>True when this process was adopted from the durable local worker ledger.</summary>
    public bool RecoveredAfterRestart { get; init; }
    public RunIntent Intent { get; init; }
    public string? Followup { get; init; }

    /// <summary>
    /// The <see cref="ContinueModes"/> value the follow-up arrived with. Kept
    /// next to <see cref="Followup"/> so a stop path can write the pair back as
    /// a <see cref="PendingIntent"/> instead of losing the user's steer
    /// (AGT-2747). Null when this run carries no user follow-up.
    /// </summary>
    public string? FollowupMode { get; init; }
    public RunPlan? Plan { get; init; }
    public int ReissueAttempt { get; init; }
    public bool IsUiIterationPipeline { get; init; }
    public int UiIteration { get; init; }
    public int UiMaxIterations { get; init; }

    /// <summary>
    /// HEAD in the actual CLI working directory immediately before process
    /// start. Unlike the session event's integration-range SHA, this remains
    /// task-worktree-specific and lets post-processing distinguish a harmless
    /// linear worker commit from a rewrite of pre-existing history.
    /// </summary>
    public string? WorkerHeadShaBefore { get; set; }

    /// <summary>
    /// Local branch checked out in the worker directory before process start.
    /// The post-run check reads this ref directly so a harmless checkout of
    /// another branch is not mistaken for a rewrite of pre-existing history.
    /// Null means the run started detached or the branch could not be read.
    /// </summary>
    public string? WorkerBranchBefore { get; set; }

    /// <summary>
    /// Remote-tracking tips for protected branches captured immediately before
    /// the CLI starts. A changed tip is only treated as worker damage when the
    /// worker output also contains a push command/claim.
    /// </summary>
    public Dictionary<string, string?> ProtectedRemoteTipsBefore { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True only while a coding CLI process is alive. The record remains in the
    /// registry during finalisation so run-scoped data stays addressable, but
    /// loop waits and post-processing do not consume an execution slot.
    /// </summary>
    private int _holdsExecutionSlot = 1;

    public bool HoldsExecutionSlot => Volatile.Read(ref _holdsExecutionSlot) == 1;

    /// <summary>
    /// Completes after the confirmed process start has been written to the
    /// durable session, timeline, phase, and pipeline projections. A very fast
    /// CLI may exit before its StartAsync call returns; finalization must wait
    /// for this handoff so it cannot write post-processing state first.
    /// </summary>
    public Task StartHandshake => _startHandshake.Task;

    public void CompleteStartHandshake() => _startHandshake.TrySetResult();

    /// <summary>
    /// Atomically releases this run's execution seat. Multiple finish signals
    /// can race, but exactly one caller observes a successful release.
    /// </summary>
    public bool TryReleaseExecutionSlot()
        => Interlocked.Exchange(ref _holdsExecutionSlot, 0) == 1;

    /// <summary>Job folder whose process lease belongs to this run.</summary>
    public string? PickupLockFolder { get; set; }

    /// <summary>
    /// Canonical task folder captured at admission time. Watcher reconciliation
    /// uses this path directly so an external move cannot force a global task
    /// index scan merely to decide whether the active latch is still valid.
    /// </summary>
    public string? JobFolder { get; set; }

    /// <summary>
    /// ADR-0052 slice 2: parallelisability facts (exclusive / predicted scope /
    /// read-only) so <see cref="ParallelSlotPolicy"/> can prove this run disjoint
    /// from a candidate before a second slot is admitted. Default = unknown scope.
    /// </summary>
    public TaskParallelism Parallelism { get; set; } = TaskParallelism.Default;

    /// <summary>
    /// Repository root of the isolated git worktree backing this run. Every
    /// coding run has one, including the single-slot local path.
    /// </summary>
    public string? WorktreePath { get; set; }

    /// <summary>
    /// Actual CLI cwd inside <see cref="WorktreePath"/>. Usually the worktree
    /// root; for a configured monorepo subfolder it is the equivalent subfolder
    /// inside the worktree.
    /// </summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// Authoritative main-checkout repository root the worktree was cut from.
    /// Git integration and containment checks use this path, never task storage
    /// or a configured monorepo subfolder.
    /// </summary>
    public string? RepositoryRoot { get; set; }

    /// <summary>The ephemeral <c>task/&lt;id&gt;</c> branch backing the worktree, when isolated.</summary>
    public string? Branch { get; set; }

    /// <summary>
    /// True when this run RE-USED an existing task worktree (resume / reissue /
    /// recovery) rather than freshly cutting one. A recorded CLI session may only
    /// be resumed when its cwd matches this run's working directory: a reused
    /// worktree carries the same cwd the session was born in, so resume is safe; a
    /// fresh cut means the prior session lived in a different directory (the old
    /// main checkout) and must NOT be resumed (it would hang).
    /// </summary>
    public bool WorktreeReused { get; set; }

    /// <summary>
    /// Main-checkout git status captured immediately before an isolated run
    /// starts. Used as a containment guard: worktree runs may mutate their
    /// worktree only, never the shared checkout.
    /// </summary>
    public string? MainCheckoutStatusBefore { get; set; }

    /// <summary>True when this run is isolated in its own worktree.</summary>
    public bool IsWorktreeRun => !string.IsNullOrEmpty(WorktreePath);
}

/// <summary>
/// Single source of truth for active coding-run slots + the admit latch. This
/// replaces the former scattered <c>_active*</c> scalar fields on
/// <c>ProjectRunner</c> so that going from one slot to N is a localized change
/// HERE rather than across ~30 call sites (ADR-0001 latch → ADR-0052 slots).
///
/// <para>
/// At <c>MaxParallelism == 1</c> the registry holds at most one entry and every
/// helper behaves byte-for-byte like the old single-field latch:
/// <see cref="HasFreeSlot"/>(1) ⇔ <c>_activeJobId == null</c>,
/// <see cref="Count"/> ⇔ <c>(_activeJobId != null ? 1 : 0)</c>,
/// <see cref="Single"/> ⇔ the one active run.
/// </para>
/// </summary>
internal sealed class ActiveRuns
{
    private readonly ConcurrentDictionary<string, ActiveRun> _runs = new(StringComparer.Ordinal);

    /// <summary>Number of slots backed by a live coding CLI process.</summary>
    public int Count => _runs.Values.Count(r => r.HoldsExecutionSlot);

    /// <summary>
    /// True while any run still belongs to the runner, including a run whose
    /// CLI seat was released but whose final post-processing has not finished.
    /// </summary>
    public bool HasInFlight => !_runs.IsEmpty;

    /// <summary>True when another run may be admitted under <paramref name="maxParallelism"/>.</summary>
    public bool HasFreeSlot(int maxParallelism) => Count < ParallelSlotPolicy.ClampMax(maxParallelism);

    public bool Contains(string jobId) => _runs.ContainsKey(jobId);

    public bool HoldsExecutionSlot(string jobId)
        => _runs.TryGetValue(jobId, out var run) && run.HoldsExecutionSlot;

    public ActiveRun? Get(string jobId) => _runs.TryGetValue(jobId, out var r) ? r : null;

    public bool TryGet(string jobId, out ActiveRun? run) => _runs.TryGetValue(jobId, out run);

    /// <summary>
    /// The single active run, or null when idle. Valid usage while
    /// <c>MaxParallelism == 1</c>; multi-slot callers use <see cref="Snapshot"/>
    /// or address a run by job id.
    /// </summary>
    public ActiveRun? Single => _runs.Values.FirstOrDefault(r => r.HoldsExecutionSlot);

    /// <summary>Job id of the single active run, or null when idle.</summary>
    public string? SingleJobId => Single?.JobId;

    /// <summary>Snapshot of all active runs (stable copy for iteration).</summary>
    public IReadOnlyCollection<ActiveRun> Snapshot()
        => _runs.Values.Where(r => r.HoldsExecutionSlot).ToArray();

    /// <summary>
    /// The currently-running tasks as <see cref="RunningTask"/> facts for
    /// <see cref="ParallelSlotPolicy.Decide"/> — lets the pick-gate prove a
    /// candidate disjoint from everything already in a slot.
    /// </summary>
    public IReadOnlyList<RunningTask> RunningTasks()
        => _runs.Values
            .Where(r => r.HoldsExecutionSlot)
            .Select(r => new RunningTask(r.JobId, r.Parallelism ?? TaskParallelism.Default))
            .ToArray();

    /// <summary>
    /// Free the execution seat when the CLI exits while retaining the run record
    /// for post-processing. Idempotent so competing finish paths release once.
    /// </summary>
    public bool ReleaseExecutionSlot(string jobId)
    {
        return _runs.TryGetValue(jobId, out var run) && run.TryReleaseExecutionSlot();
    }

    /// <summary>Find the active run whose job key matches, or null.</summary>
    public ActiveRun? ByJobKey(Func<string, string> jobKeyOf, string jobKey)
    {
        foreach (var r in _runs.Values)
            if (string.Equals(jobKeyOf(r.JobId), jobKey, StringComparison.Ordinal)) return r;
        return null;
    }

    /// <summary>
    /// Claim a slot for <paramref name="run"/>. Returns false when a run with the
    /// same job id is already claimed (defensive; the pick-gate already enforces
    /// free-slot before calling).
    /// </summary>
    public bool TryClaim(ActiveRun run) => _runs.TryAdd(run.JobId, run);

    /// <summary>Release the slot held by <paramref name="jobId"/>; returns the removed run or null.</summary>
    public ActiveRun? Release(string jobId) => _runs.TryRemove(jobId, out var r) ? r : null;
}
