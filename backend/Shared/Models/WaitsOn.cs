namespace AgentStudio.Shared;

/// <summary>
/// AGT-2029 — read-time projection of a task's <b>waits-on</b> state, derived
/// from its <see cref="TaskReferences.DependsOn"/> edges against the whole
/// workspace (all projects, all lanes including the terminal 7-archive lane).
///
/// <para>"Waits-on" is the operator-facing name for the existing F34
/// <c>dependsOn</c> relation: a task waits on the tasks it depends on, and a
/// dependency is <b>fulfilled</b> once its target task reaches
/// <c>6-completed</c> or <c>7-archive</c>. This is the field the operator asked
/// for in AGT-2029 ("an jedem Task eine Information, auf die ich warte -
/// cross-project - auf der Karte sichtbar"); rather than duplicate the
/// dependsOn list, model, endpoint, validator and cross-project index that
/// already exist, this projection gives the existing relation scheduler teeth
/// and a card-renderable status.</para>
///
/// <para>Never persisted to <c>task.json</c>. Computed by the endpoint read
/// overlay (per card / detail) and, independently, by the runner pickup gate
/// (see <c>ProjectRunner</c>). Both go through <see cref="WaitsOnEvaluator"/> so
/// the card the operator sees and the scheduler decision stay in agreement.</para>
/// </summary>
public record WaitsOnStatus
{
    /// <summary>One entry per (de-duplicated, non-self) dependsOn key, in edge order.</summary>
    public List<WaitsOnItem> Items { get; init; } = [];

    /// <summary>
    /// True when at least one dependency is not yet fulfilled - because its
    /// target is unresolved (unknown / not yet created) or has not reached
    /// <c>6-completed</c>/<c>7-archive</c>. A blocked <c>2-ready</c> card is not
    /// auto-picked; it stays visibly "waiting" on the board rather than being
    /// silently skipped.
    /// </summary>
    public bool Blocked { get; init; }

    /// <summary>
    /// True when this task sits on a dependsOn cycle (A waits on B waits on A):
    /// a configuration error that can never be fulfilled. The runner reports it
    /// (structured log + this flag drives an error chip on the card) and skips
    /// the card instead of deadlocking. Cycles among existing keys are also
    /// rejected on write; a cycle that only closes once a not-yet-created key
    /// appears is caught here at runtime.
    /// </summary>
    public bool CycleDetected { get; init; }

    /// <summary>
    /// The explicit closed path for <see cref="CycleDetected"/>, for example
    /// <c>APP-1, APP-2, APP-3, APP-1</c>. The first node is chosen
    /// deterministically so every card on the same cycle offers the same edge
    /// decisions. Empty when there is no cycle through this card.
    /// </summary>
    public List<string> CyclePath { get; init; } = [];

    /// <summary>
    /// AGT-2818: true when at least one edge is <see cref="WaitsOnItem.Unsatisfiable"/>,
    /// i.e. a gate that no run left in the system can open. Like
    /// <see cref="CycleDetected"/> this is a configuration error rather than a
    /// wait, and the runner reports it with the same once-per-card warning.
    /// </summary>
    public bool UnsatisfiableGate { get; init; }

    /// <summary>No dependsOn edges at all - the card renders no dependency chip.</summary>
    public bool IsEmpty => Items.Count == 0;
}

/// <summary>
/// One resolved (or unresolved) waits-on dependency. Carries enough of the
/// target task for the card chip to render its state and route to it without a
/// second lookup - including for targets in lanes the board snapshot omits
/// (e.g. an archived target), which is why resolution happens server-side over
/// an archive-inclusive snapshot rather than in the browser.
/// </summary>
public record WaitsOnItem
{
    /// <summary>The dependency key exactly as stored (e.g. <c>CAR-3</c>).</summary>
    public string Key { get; init; } = "";

    /// <summary>
    /// True when <see cref="Key"/> matched a real task in the workspace. False
    /// for an unknown key - a typo or a target the operator intends to create
    /// later (allowed on write as a warning, not a hard failure).
    /// </summary>
    public bool Resolved { get; init; }

    /// <summary>
    /// True when the target is terminal and, for a release-gated edge, also
    /// carries its explicit release flag.
    /// </summary>
    public bool Fulfilled { get; init; }

    /// <summary>True when this edge requires explicit release after terminal completion.</summary>
    public bool ReleaseGate { get; init; }

    /// <summary>The target's explicit release flag; false for legacy targets.</summary>
    public bool TargetReleased { get; init; }

    /// <summary>
    /// True only for the distinct terminal-but-not-released state. The board
    /// uses this to say "waiting for release" instead of "waiting for completion".
    /// </summary>
    public bool WaitingForRelease { get; init; }

    /// <summary>
    /// AGT-2818 - true when this edge can never be fulfilled by anything the
    /// system will do on its own, so it is a configuration error rather than a
    /// wait. Today that is exactly the archived release gate: the target has
    /// come to rest in <c>7-archive</c> without its explicit release flag, and
    /// no run is left that could set it. "Waiting for release" and "this gate
    /// can never open" are different sentences, and a card that has sat in a
    /// pickup lane for a month deserves the second one.
    ///
    /// <para>Deliberately additive to <see cref="WaitingForRelease"/> rather
    /// than exclusive with it: the way out is still an explicit release, so
    /// every release affordance keyed on that flag has to keep working.</para>
    /// </summary>
    public bool Unsatisfiable { get; init; }

    /// <summary>
    /// One sentence naming why <see cref="Unsatisfiable"/> is set, for the chip
    /// tooltip and the sweep report. Empty when the edge is satisfiable.
    /// </summary>
    public string UnsatisfiableReason { get; init; } = "";

    /// <summary>Target task's folder id (for navigation); null when unresolved.</summary>
    public string? TargetJobId { get; init; }

    /// <summary>Target task's short title (for the chip tooltip); null when unresolved.</summary>
    public string? TargetTitle { get; init; }

    /// <summary>Target task's lane state; null when unresolved.</summary>
    public string? TargetState { get; init; }

    /// <summary>When the target entered its current lane; used to distinguish a live review from a stalled one.</summary>
    public DateTime? TargetEnteredLaneAt { get; init; }

    /// <summary>True when the canonical review authority has a pending or live-leased attempt for the target.</summary>
    public bool TargetHasActiveReviewAttempt { get; init; }

    /// <summary>True when the target has an explicit durable park/blocker record.</summary>
    public bool TargetParked { get; init; }

    /// <summary>The prerequisite's own blocker sentence, when it has one.</summary>
    public string? TargetBlockerReason { get; init; }

    /// <summary>When the prerequisite's blocker began, when recorded.</summary>
    public DateTime? TargetBlockerSinceUtc { get; init; }

    /// <summary>Target task's watch path (for navigation); null when unresolved.</summary>
    public string? TargetWatchPath { get; init; }
}

/// <summary>
/// Read-time impact projection for a human-review task: every still-open task
/// that reaches it through one or more <c>dependsOn</c> edges. Keys are unique
/// and sorted so the board can render a stable count and tooltip. Never
/// persisted to <c>task.json</c>.
/// </summary>
public record TransitiveWaitersStatus
{
    public List<string> Keys { get; init; } = [];
    public int Count => Keys.Count;
}

/// <summary>
/// Pure, dependency-free evaluation of a task's waits-on state. Lives in the
/// Shared library so it is unit-testable without the web host and is shared by
/// the endpoint overlay and the runner pickup gate. Cross-project resolution is
/// implicit: the caller supplies a whole-workspace key map, and keys
/// (<c>&lt;ShortCode&gt;-&lt;seq&gt;</c>) are globally unique across projects.
/// </summary>
public static class WaitsOnEvaluator
{
    private static readonly StringComparer KeyComparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>True when a target has reached the completed or archive lane.</summary>
    public static bool IsFulfilledState(string? state) =>
        string.Equals(state, TaskStates.Completed, StringComparison.Ordinal)
        || string.Equals(state, TaskStates.Archive, StringComparison.Ordinal);

    /// <summary>
    /// Evaluates <paramref name="job"/>'s dependsOn edges.
    /// </summary>
    /// <param name="job">The task whose waits-on state is computed.</param>
    /// <param name="byKey">
    /// Whole-workspace key → task map (all projects, all lanes incl. archive),
    /// case-insensitive. Targets absent from this map are reported unresolved.
    /// </param>
    /// <param name="dependsOnGraph">
    /// key → its dependsOn targets, for cycle detection. Case-insensitive.
    /// </param>
    public static WaitsOnStatus Evaluate(
        TaskInfo job,
        IReadOnlyDictionary<string, TaskInfo> byKey,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> dependsOnGraph)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(byKey);
        ArgumentNullException.ThrowIfNull(dependsOnGraph);

        var self = (job.Key ?? "").Trim();
        var deps = job.References?.DependsOn ?? [];
        var items = new List<WaitsOnItem>();
        var seen = new HashSet<string>(KeyComparer);
        var blocked = false;
        var unsatisfiableGate = false;

        foreach (var dependency in deps)
        {
            var key = (dependency?.Key ?? "").Trim();
            if (key.Length == 0) continue;
            var releaseGate = dependency?.ReleaseGate == true;
            // A self-edge can never gate the task and is rejected on write; skip
            // defensively so a stale self-edge on disk cannot self-block.
            if (self.Length > 0 && KeyComparer.Equals(key, self)) continue;
            if (!seen.Add(key)) continue;

            byKey.TryGetValue(key, out var target);
            var resolved = target != null;
            var terminal = resolved && IsFulfilledState(target!.State);
            var waitingForRelease = terminal && releaseGate && !target!.Released;
            var fulfilled = terminal && (!releaseGate || target!.Released);
            if (!fulfilled) blocked = true;
            var unsatisfiable = waitingForRelease && IsArchivedState(target?.State);
            if (unsatisfiable) unsatisfiableGate = true;

            items.Add(new WaitsOnItem
            {
                Key = key,
                Resolved = resolved,
                Fulfilled = fulfilled,
                ReleaseGate = releaseGate,
                TargetReleased = target?.Released == true,
                WaitingForRelease = waitingForRelease,
                Unsatisfiable = unsatisfiable,
                UnsatisfiableReason = unsatisfiable ? ArchivedGateReason(key) : "",
                TargetJobId = target?.Id,
                TargetTitle = target?.Title,
                TargetState = target?.State,
                TargetEnteredLaneAt = target?.EnteredLaneAt,
                TargetParked = target?.ParkedBlocker is not null,
                TargetBlockerReason = target?.ParkedBlocker?.Reason,
                TargetBlockerSinceUtc = target?.ParkedBlocker?.ParkedAt,
                TargetWatchPath = target?.WatchPath,
            });
        }

        var cyclePath = FindCyclePath(self, dependsOnGraph);
        return new WaitsOnStatus
        {
            Items = items,
            Blocked = blocked,
            CycleDetected = cyclePath.Count > 0,
            CyclePath = cyclePath,
            UnsatisfiableGate = unsatisfiableGate,
        };
    }

    /// <summary>
    /// True for the terminal lane a card comes to rest in. An archived target
    /// has no run left that could set its release flag, which is what turns a
    /// release gate pointing at it from a wait into a configuration error.
    /// </summary>
    public static bool IsArchivedState(string? state) =>
        string.Equals(state, TaskStates.Archive, StringComparison.Ordinal);

    /// <summary>
    /// The operator-facing sentence for an archived release gate. Kept next to
    /// the rule so the runner warning, the sweep report, and the card chip all
    /// say the same thing.
    /// </summary>
    public static string ArchivedGateReason(string key) =>
        $"{key} is archived and was never released, so no run is left that could open this gate.";

    /// <summary>
    /// True when <paramref name="start"/> can reach itself through dependsOn
    /// edges (a cycle passing through it). O(V+E) DFS; pre-existing cycles that
    /// do not pass through <paramref name="start"/> are pruned so traversal
    /// always terminates.
    /// </summary>
    public static bool SitsOnCycle(
        string? start,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> dependsOnGraph) =>
        FindCyclePath(start, dependsOnGraph).Count > 0;

    /// <summary>
    /// Returns one explicit cycle through <paramref name="start"/> as a closed
    /// path. Only adjacent pairs in this result are safe cycle-breaking
    /// candidates. A cycle elsewhere in the graph is deliberately ignored.
    /// </summary>
    public static List<string> FindCyclePath(
        string? start,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> dependsOnGraph)
    {
        var self = (start ?? "").Trim();
        if (self.Length == 0) return [];
        if (!dependsOnGraph.TryGetValue(self, out var seedEdges) || seedEdges.Count == 0)
            return [];

        var path = new List<string> { self };
        var onPath = new HashSet<string>(KeyComparer) { self };
        var done = new HashSet<string>(KeyComparer);

        IEnumerable<string> Edges(string node) =>
            dependsOnGraph.TryGetValue(node, out var e) ? e : Array.Empty<string>();

        bool Dfs(string node)
        {
            foreach (var next in Edges(node))
            {
                var n = (next ?? "").Trim();
                if (n.Length == 0) continue;
                if (KeyComparer.Equals(n, self))
                {
                    path.Add(self);
                    return true;
                }
                if (onPath.Contains(n) || done.Contains(n)) continue;
                path.Add(n);
                onPath.Add(n);
                if (Dfs(n)) return true;
                onPath.Remove(n);
                path.RemoveAt(path.Count - 1);
            }
            done.Add(node);
            return false;
        }

        if (!Dfs(self)) return [];
        return CanonicalizeClosedCycle(path);
    }

    /// <summary>True only when the named directed edge currently participates in a cycle.</summary>
    public static bool IsCycleEdge(
        string? source,
        string? target,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> dependsOnGraph)
    {
        var from = (source ?? "").Trim();
        var to = (target ?? "").Trim();
        if (from.Length == 0 || to.Length == 0) return false;
        if (!dependsOnGraph.TryGetValue(from, out var edges)
            || !edges.Any(edge => KeyComparer.Equals((edge ?? "").Trim(), to)))
            return false;

        return CanReach(to, from, dependsOnGraph);
    }

    private static bool CanReach(
        string start,
        string target,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> graph)
    {
        var seen = new HashSet<string>(KeyComparer);
        var pending = new Stack<string>();
        pending.Push(start);
        while (pending.TryPop(out var node))
        {
            if (!seen.Add(node)) continue;
            if (KeyComparer.Equals(node, target)) return true;
            if (!graph.TryGetValue(node, out var edges)) continue;
            foreach (var edge in edges)
            {
                var next = (edge ?? "").Trim();
                if (next.Length > 0) pending.Push(next);
            }
        }
        return false;
    }

    private static List<string> CanonicalizeClosedCycle(IReadOnlyList<string> path)
    {
        var nodes = path.Take(path.Count - 1).ToList();
        var first = Enumerable.Range(0, nodes.Count)
            .OrderBy(index => nodes[index], KeyComparer)
            .First();
        var result = Enumerable.Range(0, nodes.Count)
            .Select(offset => nodes[(first + offset) % nodes.Count])
            .ToList();
        result.Add(result[0]);
        return result;
    }
}
