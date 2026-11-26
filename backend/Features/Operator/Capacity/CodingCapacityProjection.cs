using AgentStudio.Cli;
using AgentStudio.Shared;
using AgentStudio.Tasks;

namespace AgentStudio.Operator;

public sealed record CapacityBlock(string Class, string Reason, string? Dependency = null);
public sealed record CapacityCard(string CardKey, IReadOnlyList<CapacityBlock> Blocks);
public sealed record CapacityChainHead(string CardKey, string State, int CardsBehind);
public sealed record CapacitySubscription(string CliType, string? SubscriptionKey, string Window,
    double? UsedPercent, double? ElapsedPercent, double? LeftAtResetPercent, DateTime? ResetAt, string Status);
public sealed record CodingCapacityFact(string? Project, int TotalSlots, int UsedSlots, int FreeSlots,
    int Ready, int ClaimableNow, int DependencyWidth, int MaxConcurrentCards,
    IReadOnlyList<CapacityCard> Cards, IReadOnlyList<CapacityChainHead> Heads, string Cause);
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
        Func<TaskInfo, WaitsOnStatus>? waitsOf = null)
    {
        var ready = tasks.Where(task => task.State == TaskStates.Ready).ToArray();
        var waits = tasks.ToDictionary(Key, task => waitsOf?.Invoke(task) ?? references.EvaluateWaitsOn(task),
            StringComparer.OrdinalIgnoreCase);
        var cards = ready.Select(task => new CapacityCard(Key(task),
            waits[Key(task)].Items.Where(edge => !edge.Fulfilled)
                .Select(edge => new CapacityBlock("dependency", $"waits for {edge.Key}", edge.Key))
                .Concat(waits[Key(task)].Items.Where(edge => !edge.Fulfilled && references.Resolve(edge.Key)?.ParkedBlocker is not null)
                    .Select(edge => new CapacityBlock("parked", $"{edge.Key} is parked", edge.Key)))
                .Concat(waits[Key(task)].CycleDetected ? [new CapacityBlock("dependency", "dependency cycle")] : [])
                .Concat(blocksOf(task)).ToArray())).ToArray();
        var readyKeys = ready.Select(Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ancestors = Ancestors(tasks.Where(task => task.State is TaskStates.Ready or TaskStates.Progress).Select(Key), references, task =>
            waits.GetValueOrDefault(Key(task)) ?? waitsOf?.Invoke(task) ?? references.EvaluateWaitsOn(task));
        var heads = references.KnownKeys.Select(references.Resolve).OfType<TaskInfo>()
            .Where(task => !WaitsOnEvaluator.IsFulfilledState(task.State)
                && !(waits.GetValueOrDefault(Key(task)) ?? waitsOf?.Invoke(task) ?? references.EvaluateWaitsOn(task)).Blocked)
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
        var free = Math.Max(0, totalSlots - usedSlots);
        var cause = ready.Length == 0 ? "Ready is empty; plan more cards."
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
            Math.Min(totalSlots, width), cards, heads, cause);
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
        var matched = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool Match(string left, HashSet<string> seen)
        {
            foreach (var right in nodes.Where(right => ancestors[left].Contains(right)))
                if (seen.Add(right) && (!matched.TryGetValue(right, out var prior) || Match(prior, seen)))
                {
                    matched[right] = left;
                    return true;
                }
            return false;
        }
        return nodes.Length - nodes.Count(node => Match(node, new(StringComparer.OrdinalIgnoreCase)));
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
    private readonly Dictionary<string, DateTime> _since = new(StringComparer.OrdinalIgnoreCase);
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
            if (!_since.TryGetValue(key, out var since) || since > now) _since[key] = since = now;
            var surplus = quota.FirstOrDefault(row => row.LeftAtResetPercent > CodingCapacityProjection.LeftoverSharePercent);
            var shortage = reportShortage && (fact.ClaimableNow == 0 || fact.Ready > 0 && fact.ClaimableNow < fact.FreeSlots);
            if (now - since <= TimeSpan.FromMinutes(CodingCapacityProjection.IdleMinutes) || !shortage && surplus is null) return null;
            var reason = shortage ? fact.Cause
                : FormattableString.Invariant($"{surplus!.CliType} {surplus.Window} projects {surplus.LeftAtResetPercent:0.#}% left at reset, above {CodingCapacityProjection.LeftoverSharePercent}% (used {surplus.UsedPercent:0.#}%, elapsed {surplus.ElapsedPercent:0.#}%); plan work for this subscription.");
            return new(OperatorDecisionTypes.CapacityUnderused, $"{OperatorDecisionTypes.CapacityUnderused}|{key}|{since:O}",
                fact.Project, shortage ? fact.Heads.FirstOrDefault()?.CardKey : null, reason, since,
                [new("Capacity facts", "/api/operator/coding-capacity")], shortage ? fact.Heads.Select(head => head.CardKey).ToArray() : []);
        }
    }
}
