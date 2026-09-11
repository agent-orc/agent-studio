
namespace AgentStudio.Tasks;

/// <summary>
/// F34 reverse-index over the cross-reference graph. Built once when
/// <see cref="TaskIndexCache"/> publishes a scanner snapshot, so claim polls
/// and request overlays share the same generation. Answered queries are
/// O(1) / O(deg).
///
/// <para>It answers two questions the endpoints need:</para>
/// <list type="bullet">
/// <item>forward — the inputs the <see cref="TaskReferenceValidator"/> needs:
/// the set of known stable keys and the dependsOn adjacency for cycle
/// detection;</item>
/// <item>reverse - "who references X?" across task and document relation kinds, which
/// drives the detail-view dependents list and the "depends on X" board
/// filter.</item>
/// </list>
///
/// Keys are F33 stable keys (<c>ATP-19</c>); tasks without a key (pre-F33)
/// are skipped as graph nodes but still scanned for outgoing edges so a
/// keyless task can still depend on a keyed one.
/// </summary>
public sealed class TaskReferenceIndex
{
    private readonly Dictionary<string, TaskInfo> _byKey;
    private readonly Dictionary<string, IReadOnlyCollection<string>> _dependsOn;
    private readonly Dictionary<string, List<TaskReferenceLink>> _incoming;
    private readonly Dictionary<string, List<string>> _incomingDependsOn;

    private static readonly StringComparer KeyComparer = StringComparer.OrdinalIgnoreCase;

    private TaskReferenceIndex(
        Dictionary<string, TaskInfo> byKey,
        Dictionary<string, IReadOnlyCollection<string>> dependsOn,
        Dictionary<string, List<TaskReferenceLink>> incoming,
        Dictionary<string, List<string>> incomingDependsOn)
    {
        _byKey = byKey;
        _dependsOn = dependsOn;
        _incoming = incoming;
        _incomingDependsOn = incomingDependsOn;
        KnownKeys = new HashSet<string>(byKey.Keys, KeyComparer);
    }

    /// <summary>Every stable key present in the workspace.</summary>
    public IReadOnlySet<string> KnownKeys { get; }

    /// <summary>key → its dependsOn targets, for cycle detection.</summary>
    public IReadOnlyDictionary<string, IReadOnlyCollection<string>> DependsOnGraph => _dependsOn;

    /// <summary>Resolve a stable key to its task, or null when unknown.</summary>
    public TaskInfo? Resolve(string? key) =>
        !string.IsNullOrWhiteSpace(key) && _byKey.TryGetValue(key.Trim(), out var t) ? t : null;

    /// <summary>
    /// AGT-2029 — computes <paramref name="job"/>'s waits-on status (which
    /// dependsOn targets are fulfilled, whether the card is blocked, whether it
    /// sits on a cycle) against this index. For fulfillment to see targets that
    /// have reached the terminal <c>7-archive</c> lane, build the index from an
    /// archive-inclusive snapshot (<c>ScanAllJobs().Concat(ScanArchivedJobs())</c>).
    /// Delegates to the pure <see cref="WaitsOnEvaluator"/> so the runner pickup
    /// gate and the endpoint read overlay share one implementation.
    /// </summary>
    public WaitsOnStatus EvaluateWaitsOn(TaskInfo job) =>
        WaitsOnEvaluator.Evaluate(job, _byKey, _dependsOn);

    /// <summary>
    /// Finds every active keyed task that transitively waits on
    /// <paramref name="key"/>. Completed and archived sources are fulfillment
    /// boundaries, so neither they nor tasks behind them are counted. The
    /// visited set de-duplicates diamonds and makes stale on-disk cycles safe.
    /// </summary>
    public TransitiveWaitersStatus FindTransitiveWaiters(
        string? key,
        IReadOnlySet<string>? eligibleKeys = null)
    {
        var root = key?.Trim();
        if (string.IsNullOrEmpty(root)) return new();

        var visited = new HashSet<string>(KeyComparer) { root };
        var queue = new Queue<string>();
        var keys = new List<string>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var target = queue.Dequeue();
            if (!_incomingDependsOn.TryGetValue(target, out var sources)) continue;

            foreach (var sourceKey in sources)
            {
                if (!visited.Add(sourceKey)) continue;
                if (!_byKey.TryGetValue(sourceKey, out var source)) continue;
                if (WaitsOnEvaluator.IsFulfilledState(source.State)) continue;

                if (eligibleKeys == null || eligibleKeys.Contains(sourceKey))
                    keys.Add(source.Key!.Trim());
                queue.Enqueue(sourceKey);
            }
        }

        keys.Sort(StringComparer.OrdinalIgnoreCase);
        return new TransitiveWaitersStatus { Keys = keys };
    }

    /// <summary>
    /// Tasks that reference <paramref name="key"/>. Optionally filtered to a
    /// single relation kind (<see cref="TaskReferenceKinds"/>); null returns
    /// every incoming link. Empty when nothing points at the key.
    /// </summary>
    public IReadOnlyList<TaskReferenceLink> Dependents(string? key, string? kind = null)
    {
        if (string.IsNullOrWhiteSpace(key) || !_incoming.TryGetValue(key.Trim(), out var links))
            return Array.Empty<TaskReferenceLink>();
        if (string.IsNullOrWhiteSpace(kind))
            return links;
        return links.Where(l => KeyComparer.Equals(l.Kind, kind)).ToList();
    }

    public static TaskReferenceIndex Build(IEnumerable<TaskInfo> tasks)
    {
        var byKey = new Dictionary<string, TaskInfo>(KeyComparer);
        var dependsOn = new Dictionary<string, IReadOnlyCollection<string>>(KeyComparer);
        var incoming = new Dictionary<string, List<TaskReferenceLink>>(KeyComparer);
        var incomingDependsOn = new Dictionary<string, List<string>>(KeyComparer);

        var all = tasks as IReadOnlyCollection<TaskInfo> ?? tasks.ToList();

        // First pass: index every keyed task as a graph node. Duplicate keys
        // (rare, e.g. a stale folder copy) keep the first occurrence.
        foreach (var t in all)
        {
            var key = t.Key?.Trim();
            if (string.IsNullOrEmpty(key)) continue;
            byKey.TryAdd(key, t);
        }

        // Second pass: record outgoing edges + reverse links for every task,
        // keyed or not (a keyless task can still reference a keyed one).
        foreach (var t in all)
        {
            var refs = t.References ?? new TaskReferences();
            var sourceKey = t.Key?.Trim() ?? "";
            if (!string.IsNullOrEmpty(sourceKey))
                dependsOn[sourceKey] = refs.DependsOn.Select(edge => edge.Key).ToList();

            // AGT-2709: the reverse link carries whether the incoming edge is
            // release-gated, so the target card can name the dependents its
            // explicit release would unblock without re-reading their task.json.
            // Allocated only for the rare task that actually gates an edge.
            HashSet<string>? gatedTargets = null;
            foreach (var edge in refs.DependsOn)
            {
                if (!edge.ReleaseGate || string.IsNullOrWhiteSpace(edge.Key)) continue;
                (gatedTargets ??= new HashSet<string>(KeyComparer)).Add(edge.Key.Trim());
            }

            foreach (var (kind, target) in refs.Enumerate())
            {
                if (string.IsNullOrWhiteSpace(target)) continue;
                if (!incoming.TryGetValue(target, out var list))
                    incoming[target] = list = new List<TaskReferenceLink>();
                list.Add(new TaskReferenceLink(
                    SourceKey: sourceKey.Length > 0 ? sourceKey : null,
                    SourceJobId: t.Id,
                    SourceTitle: t.Title,
                    SourceState: t.State,
                    SourceWatchPath: t.WatchPath,
                    Kind: kind,
                    ReleaseGate: kind == TaskReferenceKinds.DependsOn
                        && gatedTargets?.Contains(target.Trim()) == true));

                if (kind == TaskReferenceKinds.DependsOn && sourceKey.Length > 0)
                {
                    if (!incomingDependsOn.TryGetValue(target, out var sources))
                        incomingDependsOn[target] = sources = [];
                    if (!sources.Contains(sourceKey, KeyComparer))
                        sources.Add(sourceKey);
                }
            }
            foreach (var target in refs.Workbenches)
            {
                if (string.IsNullOrWhiteSpace(target)) continue;
                if (!incoming.TryGetValue(target, out var list))
                    incoming[target] = list = new List<TaskReferenceLink>();
                list.Add(new TaskReferenceLink(
                    SourceKey: sourceKey.Length > 0 ? sourceKey : null,
                    SourceJobId: t.Id,
                    SourceTitle: t.Title,
                    SourceState: t.State,
                    SourceWatchPath: t.WatchPath,
                    Kind: TaskReferenceKinds.Workbenches));
            }
        }

        return new TaskReferenceIndex(byKey, dependsOn, incoming, incomingDependsOn);
    }
}

/// <summary>
/// One incoming reference: a task (<see cref="SourceJobId"/> /
/// <see cref="SourceKey"/>) points at the queried key via <see cref="Kind"/>.
/// Carries enough of the source task for the UI to render a chip and route to
/// it without a second lookup.
/// </summary>
/// <param name="ReleaseGate">
/// AGT-2709 - true when this is a <c>dependsOn</c> edge with
/// <c>releaseGate: true</c>, i.e. the source stays blocked until the queried
/// task carries its explicit <c>released</c> flag. Always false for the other
/// relation kinds.
/// </param>
public record TaskReferenceLink(
    string? SourceKey,
    string SourceJobId,
    string SourceTitle,
    string SourceState,
    string SourceWatchPath,
    string Kind,
    bool ReleaseGate = false);
