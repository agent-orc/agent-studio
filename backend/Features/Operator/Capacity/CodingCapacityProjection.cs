using AgentStudio.Cli;
using AgentStudio.Shared;
using AgentStudio.Tasks;

namespace AgentStudio.Operator;

public sealed record CapacityBlock(string Class, string Reason, string? Dependency = null);
public sealed record CapacityCard(string CardKey, IReadOnlyList<CapacityBlock> Blocks);
public sealed record CapacityChainHead(string CardKey, string State, int CardsBehind);
public sealed record CapacitySubscription(string CliType, string? SubscriptionKey, string Window,
    double? UsedPercent, double? ElapsedPercent, double? LeftAtResetPercent, DateTime? ResetAt, string Status);
public sealed record CapacityAllocation(string RunnerId, int? BootstrapSlots, int? ConfiguredSlots,
    int? AdoptedSlots, string State);
public sealed record CodingCapacityFact(string? Project, int TotalSlots, int UsedSlots, int FreeSlots,
    int Ready, int ClaimableNow, int DependencyWidth, int MaxConcurrentCards,
    IReadOnlyList<CapacityCard> Cards, IReadOnlyList<CapacityChainHead> Heads, string Cause)
{
    public int AdmissibleSlots { get; init; } = FreeSlots;
    public int RunnableNow { get; init; } = ClaimableNow;
    public DateTime? LastProgressAt { get; init; }
    public IReadOnlyList<CapacityAllocation> Allocations { get; init; } = [];
}
public sealed record CodingCapacityResponse(CodingCapacityFact Fleet, IReadOnlyList<CodingCapacityFact> Projects,
    IReadOnlyList<CapacitySubscription> Subscriptions);

/// <summary>Read-only graph and quota arithmetic. Does not grant a dispatch permit.</summary>
public static class CodingCapacityProjection
{
    public const int IdleMinutes = 20;
    public const double LeftoverSharePercent = 20;
    public const double MinimumElapsedPercent = 5;
    public static readonly TimeSpan HostFreshness = TimeSpan.FromMinutes(10);

    public static CodingCapacityFact Project(string? project, IReadOnlyList<TaskInfo> tasks,
        TaskReferenceIndex references, int totalSlots, int usedSlots,
        Func<TaskInfo, IReadOnlyList<CapacityBlock>> blocksOf,
        Func<TaskInfo, WaitsOnStatus>? waitsOf = null, int? admissibleSlots = null,
        IReadOnlyList<CapacityBlock>? admissionBlocks = null, DateTime? lastProgressAt = null,
        IReadOnlyList<string>? admissionLimits = null)
    {
        // Match reference resolution: the first occurrence owns a stable card key.
        tasks = tasks.DistinctBy(Key, StringComparer.OrdinalIgnoreCase).ToArray();
        var ready = tasks.Where(task => task.State == TaskStates.Ready).ToArray();
        var waits = tasks.ToDictionary(Key, task => waitsOf?.Invoke(task) ?? references.EvaluateWaitsOn(task),
            StringComparer.OrdinalIgnoreCase);
        WaitsOnStatus Waits(TaskInfo task)
        {
            var key = Key(task);
            if (!waits.TryGetValue(key, out var status))
                waits[key] = status = waitsOf?.Invoke(task) ?? references.EvaluateWaitsOn(task);
            return status;
        }
        var cards = ready.Select(task => new CapacityCard(Key(task),
            waits[Key(task)].Items.Where(edge => !edge.Fulfilled)
                .Select(edge => new CapacityBlock("dependency", $"waits for {edge.Key}", edge.Key))
                .Concat(waits[Key(task)].Items.Where(edge => !edge.Fulfilled && references.Resolve(edge.Key)?.ParkedBlocker is not null)
                    .Select(edge => new CapacityBlock("parked", $"{edge.Key} is parked", edge.Key)))
                .Concat(waits[Key(task)].CycleDetected ? [new CapacityBlock("dependency", "dependency cycle")] : [])
                .Concat(blocksOf(task)).ToArray())).ToArray();
        var readyKeys = ready.Select(Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ancestors = Ancestors(tasks.Where(task => task.State is TaskStates.Ready or TaskStates.Progress).Select(Key), references, Waits);
        var heads = references.KnownKeys.Select(references.Resolve).OfType<TaskInfo>()
            .Where(task => !WaitsOnEvaluator.IsFulfilledState(task.State)
                && !Waits(task).Blocked)
            .Select(task => new CapacityChainHead(Key(task),
                task.State == TaskStates.Escalated ? "escalated"
                    : task.ParkedBlocker is not null ? "parked" : task.State == TaskStates.HumanReview ? "awaiting review" : "waiting",
                readyKeys.Count(key => ancestors[key].Contains(Key(task)))))
            .Where(head => head.CardsBehind > 0)
            .OrderBy(head => head.State == "waiting" ? 1 : 0)
            .ThenByDescending(head => head.CardsBehind).ThenBy(head => head.CardKey, StringComparer.Ordinal).ToArray();
        var nodes = tasks.Where(task => task.State is TaskStates.Ready or TaskStates.Progress
                && !waits[Key(task)].CycleDetected).Select(Key).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var width = Width(nodes, ancestors);
        var claimable = cards.Count(card => card.Blocks.Count == 0);
        usedSlots = Math.Max(0, usedSlots);
        totalSlots = Math.Max(usedSlots, Math.Max(0, totalSlots));
        var free = totalSlots - usedSlots;
        var admissible = Math.Min(claimable, Math.Clamp(admissibleSlots ?? free, 0, free));
        var runnable = cards.Count(card => card.Blocks.All(block => block.Class == "admission"));
        var holds = cards.SelectMany(card => card.Blocks).Concat(admissionBlocks ?? [])
            .Where(block => block.Class == "admission").Select(block => block.Reason).Distinct().ToArray();
        var cause = free == 0 && totalSlots > 0 ? "Coding capacity is full."
            : totalSlots == 0 ? "No fresh coding capacity is available; inspect execution hosts."
            : ready.Length == 0 ? "Ready is empty; plan more cards."
            : holds.Length == 0 && admissionLimits is { Count: > 0 } && admissible < free && claimable >= admissible
                ? $"{claimable} claimable cards for {admissible} admissible slots ({free} physical free slots): {string.Join("; ", admissionLimits)}; adding cards does not raise admission now."
            : runnable >= free || runnable > 0 && claimable < runnable
                ? $"{runnable} runnable cards awaiting admission for {admissible} admissible slots ({free} physical free slots)"
                    + (holds.Length == 0 ? "; awaiting pickup progress." : $": {string.Join("; ", holds)}.")
            : heads.Any(head => head.State != "waiting")
                ? string.Join("; ", heads.Where(head => head.State != "waiting").Select(head => $"{head.CardKey} is {head.State} and holds {head.CardsBehind} cards")) + "; resolve these chain heads."
            : claimable == 0 && cards.SelectMany(card => card.Blocks).FirstOrDefault(block => block.Class != "dependency") is { } block
                ? $"No claimable card: {block.Class} ({block.Reason}); inspect the blocked cards."
            : claimable == 0 && cards.SelectMany(card => card.Blocks).FirstOrDefault() is { } dependency
                ? $"No claimable card: dependency ({dependency.Reason}); resolve the dependency or plan independent cards."
            : width < ready.Length
                ? $"{width} chains: at most {width} cards can run; plan independent cards."
                : $"{claimable} claimable cards for {free} free slots; plan more cards.";
        return new(project, totalSlots, usedSlots, free, ready.Length, claimable, width,
            Math.Min(totalSlots, width), cards, heads, cause)
        {
            AdmissibleSlots = admissible, RunnableNow = runnable, LastProgressAt = lastProgressAt,
        };
    }

    // Dilworth's theorem: width = vertices minus maximum matching in the transitive DAG.
    // Counting roots or connected components is incorrect for forks and joins.
    private static IReadOnlyDictionary<string, HashSet<string>> Ancestors(IEnumerable<string> keys,
        TaskReferenceIndex references, Func<TaskInfo, WaitsOnStatus> waitsOf)
    {
        IEnumerable<string> Parents(string key) => references.Resolve(key) is { } task
            ? waitsOf(task).Items.Where(edge => !edge.Fulfilled).Select(edge => edge.Key) : [];
        return keys.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(key => key, key =>
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Stack<string>(Parents(key));
            while (pending.TryPop(out var next))
                if (seen.Add(next))
                    foreach (var parent in Parents(next)) pending.Push(parent);
            return seen;
        }, StringComparer.OrdinalIgnoreCase);
    }

    private static int Width(string[] nodes, IReadOnlyDictionary<string, HashSet<string>> ancestors)
    {
        // Build adjacency once, then use Hopcroft-Karp's shortest augmenting paths.
        // Matching visits edges rather than scanning all vertices at every recursion.
        var index = nodes.Select((key, i) => (key, i)).ToDictionary(node => node.key, node => node.i,
            StringComparer.OrdinalIgnoreCase);
        var edges = nodes.Select(key => ancestors[key].Where(index.ContainsKey).Select(parent => index[parent]).ToArray()).ToArray();
        var leftMatch = Enumerable.Repeat(-1, nodes.Length).ToArray();
        var rightMatch = Enumerable.Repeat(-1, nodes.Length).ToArray();
        var distance = new int[nodes.Length];
        var nextEdge = new int[nodes.Length];
        var shortest = int.MaxValue;

        bool Layer()
        {
            Array.Fill(distance, -1);
            shortest = int.MaxValue;
            var pending = new Queue<int>();
            for (var left = 0; left < nodes.Length; left++)
                if (leftMatch[left] < 0)
                {
                    distance[left] = 0;
                    pending.Enqueue(left);
                }
            while (pending.TryDequeue(out var left))
            {
                if (distance[left] >= shortest) continue;
                foreach (var right in edges[left])
                {
                    var prior = rightMatch[right];
                    if (prior < 0) shortest = distance[left] + 1;
                    else if (distance[prior] < 0)
                    {
                        distance[prior] = distance[left] + 1;
                        pending.Enqueue(prior);
                    }
                }
            }
            return shortest != int.MaxValue;
        }

        bool Match(int left)
        {
            while (nextEdge[left] < edges[left].Length)
            {
                var right = edges[left][nextEdge[left]++];
                var prior = rightMatch[right];
                if (prior < 0 ? distance[left] + 1 == shortest
                    : distance[prior] == distance[left] + 1 && Match(prior))
                {
                    leftMatch[left] = right;
                    rightMatch[right] = left;
                    return true;
                }
            }
            distance[left] = -1;
            return false;
        }

        var matched = 0;
        while (Layer())
        {
            Array.Clear(nextEdge);
            for (var left = 0; left < nodes.Length; left++)
                if (leftMatch[left] < 0 && Match(left)) matched++;
        }
        return nodes.Length - matched;
    }

    public static IReadOnlyList<CapacitySubscription> Subscriptions(QuotaReport report, DateTime now)
        => report.Snapshots.OrderByDescending(snapshot => snapshot.FetchedAt).DistinctBy(snapshot => (snapshot.CliType, snapshot.SubscriptionKey))
            .SelectMany(snapshot => snapshot.Windows.Select(window =>
            {
                double? elapsed = null;
                double? left = null;
                var status = window.ResetAt is null ? "reset unknown" : "projection unavailable";
                var length = QuotaWindowProjection.InferWindowLength(window.Label);
                var start = window.ObservedStartAt ?? (window.ResetAt - length);
                if (window.ResetAt > now && start < now && length is not null)
                    elapsed = Math.Clamp((now - start!.Value).TotalSeconds / (window.ResetAt.Value - start.Value).TotalSeconds * 100, 0, 100);
                if (elapsed >= MinimumElapsedPercent && window.UsedPct is >= 0
                    && snapshot.Error is null && snapshot.ProbeFailedAt is null && !snapshot.Suspicious
                    && window.ProjectionSuspiciousReason is null && snapshot.FetchedAt <= now
                    && now - snapshot.FetchedAt <= TimeSpan.FromSeconds(report.TtlSeconds))
                {
                    left = Math.Clamp(100 - window.UsedPct.Value / (elapsed.Value / 100), 0, 100);
                    status = "projected";
                }
                return new CapacitySubscription(snapshot.CliType, snapshot.SubscriptionKey, window.Label,
                    window.UsedPct, elapsed, left, window.ResetAt, status);
            })).ToArray();

    public static string Key(TaskInfo task) => task.Key ?? (task.TaskKey.Length > 0 ? task.TaskKey : task.Id);
}

/// <summary>Tracks observed continuous free capacity, independently of browser polling.</summary>
public sealed class CapacityIdleTracker
{
    private sealed record Observation(DateTime Since, int Used, DateTime? Progress);
    private readonly Dictionary<string, Observation> _since = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public void Reset() { lock (_gate) _since.Clear(); }

    public void Retain(IReadOnlySet<string> projects)
    {
        lock (_gate)
            foreach (var key in _since.Keys.Where(key => key != "fleet" && !projects.Contains(key)).ToArray()) _since.Remove(key);
    }

    public OperatorDecisionFact? Observe(CodingCapacityFact fact, IReadOnlyList<CapacitySubscription> quota, DateTime now, bool reportShortage = true)
    {
        lock (_gate)
        {
            var key = fact.Project ?? "fleet";
            if (fact.FreeSlots <= 0) { _since.Remove(key); return null; }
            if (!_since.TryGetValue(key, out var previous) || previous.Since > now
                || previous.Used != fact.UsedSlots || previous.Progress != fact.LastProgressAt)
                _since[key] = previous = new(now, fact.UsedSlots, fact.LastProgressAt);
            var since = previous.Since;
            var surplus = quota.FirstOrDefault(row => row.LeftAtResetPercent > CodingCapacityProjection.LeftoverSharePercent);
            var shortage = reportShortage && (fact.RunnableNow < fact.FreeSlots);
            var stalled = reportShortage && fact.Ready > 0 && (fact.ClaimableNow < fact.RunnableNow
                || fact.AdmissibleSlots < fact.FreeSlots || surplus is null && fact.RunnableNow >= fact.FreeSlots);
            if (now - since <= TimeSpan.FromMinutes(CodingCapacityProjection.IdleMinutes) || !shortage && !stalled && surplus is null) return null;
            var reason = shortage || stalled ? fact.Cause
                : FormattableString.Invariant($"{surplus!.CliType} {surplus.Window} projects {surplus.LeftAtResetPercent:0.#}% left at reset, above {CodingCapacityProjection.LeftoverSharePercent}% (used {surplus.UsedPercent:0.#}%, elapsed {surplus.ElapsedPercent:0.#}%); plan work for this subscription.");
            return new(OperatorDecisionTypes.CapacityUnderused, $"{OperatorDecisionTypes.CapacityUnderused}|{key}|{since:O}",
                fact.Project, shortage ? fact.Heads.FirstOrDefault()?.CardKey : null, reason, since,
                [new("Capacity facts", "/api/operator/coding-capacity")], shortage ? fact.Heads.Select(head => head.CardKey).ToArray() : []);
        }
    }
}
