namespace AgentStudio.Runner;

/// <summary>
/// ADR-0052 parallelisability facts the orchestrator-prep step stores on a task
/// and the runner's pick-gate reads when a slot frees. <see cref="Exclusive"/>
/// marks a too-big / cross-cutting task that must run alone (the rare
/// exception); <see cref="PredictedScope"/> is the set of repo-relative path
/// prefixes the task is expected to touch, used to prove two tasks are disjoint
/// before admitting them concurrently. An empty scope is treated as "unknown";
/// worktree isolation lets the pick-gate admit it optimistically while still
/// serializing two declared scopes that overlap.
/// </summary>
public sealed record TaskParallelism(bool Exclusive, IReadOnlyList<string> PredictedScope)
{
    /// <summary>
    /// True for a read-only task mode (planning / research). A read-only task
    /// writes no files, so it has no predicted scope and can never collide with
    /// a running task - the pick-gate admits it as parallel-ok without any scope
    /// computation. It still counts against the slot budget like any other task.
    /// </summary>
    public bool ReadOnly { get; init; }

    /// <summary>Pipeline recovery must drain the delivery window; slot and exclusive limits still apply.</summary>
    public bool BypassScope { get; init; }

    /// <summary>Default: parallelisable, scope unknown.</summary>
    public static readonly TaskParallelism Default = new(false, Array.Empty<string>());

    /// <summary>A read-only (planning / research) task: never exclusive, no scope.</summary>
    public static readonly TaskParallelism ReadOnlyTask = new(false, Array.Empty<string>()) { ReadOnly = true };
}

/// <summary>A task currently occupying a runner slot, with its parallelism facts.</summary>
public sealed record RunningTask(string TaskId, TaskParallelism Parallelism)
{
    public string? CardKey { get; init; }
    public string? Version { get; init; }
}

public enum SlotDecision
{
    /// <summary>Admit the candidate into a free slot to run alongside the others.</summary>
    Admit,
    /// <summary>Hold the candidate; it must wait (no slot, conflict, or a running exclusive).</summary>
    Serialize,
    /// <summary>Admit the candidate but as the only task (it is exclusive and nothing else runs).</summary>
    RunExclusive,
}

/// <summary>
/// A card of the project that started a coding run and has not integrated yet,
/// without a runner slot (DS-0). It is part of the scope comparison set under
/// the <c>in-flight</c> overlap window, never of the slot count, and it never
/// blocks as exclusive: an exclusive card blocks only while it runs.
/// </summary>
public sealed record InFlightTask(string TaskId, TaskParallelism Parallelism, string Lane)
{
    public string? CardKey { get; init; }
    public string? Version { get; init; }
}

/// <summary>Result of one pick-gate evaluation, with a human-readable rationale for the timeline.</summary>
public sealed record SlotAdmission(SlotDecision Decision, string Reason)
{
    public string? DispatchDecisionId { get; init; }
    public bool Admitted => Decision is SlotDecision.Admit or SlotDecision.RunExclusive;

    /// <summary>The overlap window the scope check used (<see cref="OverlapWindows"/>).</summary>
    public string Window { get; init; } = OverlapWindows.Running;

    /// <summary>The card whose scope overlaps the candidate's, on a scope serialize.</summary>
    public string? ConflictTaskId { get; init; }

    /// <summary>The shared path prefix, on a scope serialize.</summary>
    public string? SharedPrefix { get; init; }

    /// <summary>Lane of the conflicting card: <c>running</c> for a slot holder, else its lane.</summary>
    public string? ConflictLane { get; init; }

    /// <summary>True when the candidate was serialized by a scope overlap.</summary>
    public bool IsScopeConflict => ConflictTaskId is not null;
}

/// <summary>
/// ADR-0052 §5 pick-gate + slot accounting, as pure logic. The runner owns the
/// N slots; this policy decides, deterministically, whether a candidate task may
/// take a free slot given what is already running. The single
/// <c>_activeJobId</c> latch of the sequential runner is the
/// <c>maxParallelism == 1</c> special case: one task is admitted, every further
/// candidate serializes until it drains - byte-for-byte today's behaviour.
///
/// <para>
/// Rules, in order: (1) no free slot -&gt; serialize; (2) an exclusive task is
/// running -&gt; serialize everyone; (3) the candidate is exclusive -&gt; run
/// alone if the project is idle, else wait for it to drain; (4) scope overlap
/// with any running task or, under the <c>in-flight</c> overlap window, any
/// card that started and has not integrated -&gt; serialize (no cross-talk); (5) otherwise admit as
/// parallel-ok. Scope comparison is path-prefix based. Unknown (empty) scopes
/// are admitted optimistically because coding runs are worktree-isolated; only
/// two declared, overlapping scopes conflict.
/// </para>
/// </summary>
public static class ParallelSlotPolicy
{
    /// <summary>Clamp a configured parallelism to the valid <c>&gt;= 1</c> range.</summary>
    public static int ClampMax(int maxParallelism) => maxParallelism < 1 ? 1 : maxParallelism;

    /// <summary>How many slots are still free for new admissions.</summary>
    public static int FreeSlots(int maxParallelism, int occupied)
        => Math.Max(0, ClampMax(maxParallelism) - Math.Max(0, occupied));

    /// <summary>
    /// Decide whether <paramref name="candidate"/> may take a slot right now,
    /// given the <paramref name="running"/> tasks and the project's
    /// <paramref name="maxParallelism"/>.
    /// </summary>
    public static SlotAdmission Decide(
        string candidateId,
        TaskParallelism candidate,
        IReadOnlyList<RunningTask> running,
        int maxParallelism)
        => Decide(candidateId, candidate, running, maxParallelism,
            Array.Empty<InFlightTask>(), OverlapWindows.Running);

    /// <summary>
    /// DS-0: the same rules, with the scope check extended over the overlap
    /// window. Slots, a running exclusive card and an exclusive candidate are
    /// judged on <paramref name="running"/> alone. Under
    /// <see cref="OverlapWindows.InFlight"/> the scope check also covers
    /// <paramref name="inFlight"/>: cards that started and have not integrated.
    /// Under <see cref="OverlapWindows.Running"/> that list is ignored. A scope
    /// serialize names the window, the conflicting card and the shared prefix.
    /// </summary>
    public static SlotAdmission Decide(
        string candidateId,
        TaskParallelism candidate,
        IReadOnlyList<RunningTask> running,
        int maxParallelism,
        IReadOnlyList<InFlightTask> inFlight,
        string? overlapWindow)
    {
        var window = OverlapWindows.Normalize(overlapWindow) ?? OverlapWindows.Default;
        return DecideInWindow(candidateId, candidate, running, maxParallelism, inFlight, window)
            with { Window = window };
    }

    /// <summary>
    /// DS-0: the scope rule alone, for a host that admits slots itself (the
    /// remote claim checks host and project concurrency before it asks). The
    /// slot count never serializes here; a running exclusive card and the
    /// scope overlap over the window do, as in
    /// <see cref="Decide(string, TaskParallelism, IReadOnlyList{RunningTask}, int, IReadOnlyList{InFlightTask}, string?)"/>.
    /// </summary>
    public static SlotAdmission DecideOverlap(
        string candidateId,
        TaskParallelism candidate,
        IReadOnlyList<RunningTask> running,
        IReadOnlyList<InFlightTask> inFlight,
        string? overlapWindow)
        => Decide(candidateId, candidate, running, int.MaxValue, inFlight, overlapWindow);

    private static SlotAdmission DecideInWindow(
        string candidateId,
        TaskParallelism candidate,
        IReadOnlyList<RunningTask> running,
        int maxParallelism,
        IReadOnlyList<InFlightTask> inFlight,
        string window)
    {
        var max = ClampMax(maxParallelism);
        running ??= Array.Empty<RunningTask>();
        candidate ??= TaskParallelism.Default;
        var exposed = window != OverlapWindows.Running && inFlight is not null
            ? inFlight.Where(item => item is not null
                    && !string.Equals(item.TaskId, candidateId, StringComparison.OrdinalIgnoreCase)
                    && !running.Any(r => string.Equals(r.TaskId, item.TaskId, StringComparison.OrdinalIgnoreCase)))
                .ToArray()
            : Array.Empty<InFlightTask>();

        if (running.Count >= max)
            return new SlotAdmission(SlotDecision.Serialize,
                $"no free slot ({running.Count}/{max} occupied)");

        var exclusiveRunning = running.FirstOrDefault(r => r.Parallelism.Exclusive);
        if (exclusiveRunning != null)
            return new SlotAdmission(SlotDecision.Serialize,
                $"exclusive task '{exclusiveRunning.TaskId}' is running");

        // Read-only modes (planning / research) write no files, so there is no
        // scope to prove disjoint - admit as parallel-ok and skip the scope loop
        // below. The free-slot check above already enforced the quota.
        if (candidate.ReadOnly)
            return new SlotAdmission(SlotDecision.Admit,
                "parallel-ok: read-only task (no file scope)");

        if (candidate.Exclusive)
        {
            return running.Count == 0
                ? new SlotAdmission(SlotDecision.RunExclusive, "exclusive: runs alone")
                : new SlotAdmission(SlotDecision.Serialize,
                    $"exclusive: waits for {running.Count} running task(s) to drain");
        }

        if (candidate.BypassScope)
            return new SlotAdmission(SlotDecision.Admit, "parallel-ok: delivery recovery bypasses scope serialization");

        var usedOptimisticUnknownScope = !HasDeclaredScope(candidate.PredictedScope);
        foreach (var r in running)
        {
            usedOptimisticUnknownScope |= !HasDeclaredScope(r.Parallelism.PredictedScope);
            var conflict = FirstScopeConflict(candidate.PredictedScope, r.Parallelism.PredictedScope);
            if (conflict != null)
                return ScopeSerialize(window, r.TaskId, "running", conflict);
        }

        foreach (var item in exposed)
        {
            usedOptimisticUnknownScope |= !HasDeclaredScope(item.Parallelism?.PredictedScope);
            var conflict = FirstScopeConflict(candidate.PredictedScope,
                item.Parallelism?.PredictedScope ?? Array.Empty<string>());
            if (conflict != null)
                return ScopeSerialize(window, item.TaskId, item.Lane, conflict);
        }

        var reason = running.Count == 0 && exposed.Length == 0
            ? "parallel-ok: first slot"
            : usedOptimisticUnknownScope
                ? "parallel-ok (optimistic: unknown scope, worktree-isolated)"
                : exposed.Length == 0
                    ? $"parallel-ok: disjoint from {running.Count} running task(s)"
                    : $"parallel-ok: disjoint from {running.Count} running and {exposed.Length} in-flight task(s)";
        return new SlotAdmission(SlotDecision.Admit, reason);
    }

    private static SlotAdmission ScopeSerialize(string window, string taskId, string lane, string prefix)
        => new(window == OverlapWindows.Off ? SlotDecision.Admit : SlotDecision.Serialize,
            $"{(window == OverlapWindows.Off ? "advisory overlap" : "scope conflict")} with '{taskId}' on '{prefix}' (window {window}, {lane})")
        {
            ConflictTaskId = taskId,
            SharedPrefix = prefix,
            ConflictLane = lane,
        };

    /// <summary>
    /// Returns the first overlapping declared path, else null. Empty and blank
    /// scopes are unknown and do not conflict because their runs are isolated in
    /// separate worktrees.
    /// </summary>
    public static string? FirstScopeConflict(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (!HasDeclaredScope(a) || !HasDeclaredScope(b)) return null;
        foreach (var pa in a)
        {
            foreach (var pb in b)
            {
                if (string.IsNullOrWhiteSpace(pa) || string.IsNullOrWhiteSpace(pb)) continue;
                if (PathsOverlap(pa, pb))
                    return NormalizePath(pa).Length <= NormalizePath(pb).Length ? NormalizePath(pa) : NormalizePath(pb);
            }
        }
        return null;
    }

    private static bool HasDeclaredScope(IReadOnlyList<string>? scope)
        => scope != null && scope.Any(path => !string.IsNullOrWhiteSpace(path));

    private static bool PathsOverlap(string x, string y)
    {
        var nx = NormalizePath(x);
        var ny = NormalizePath(y);
        if (nx.Length == 0 || ny.Length == 0) return false;
        return nx == ny
            || nx.StartsWith(ny + "/", StringComparison.Ordinal)
            || ny.StartsWith(nx + "/", StringComparison.Ordinal);
    }

    private static string NormalizePath(string p)
    {
        if (string.IsNullOrWhiteSpace(p)) return string.Empty;
        return p.Trim().Replace('\\', '/').Trim('/').ToLowerInvariant();
    }
}
