namespace AgentStudio.Operator;

/// <summary>The facts one sweep publishes and the occurrence keys it keeps as published.</summary>
public sealed record OperatorDecisionDiff(IReadOnlyList<OperatorDecisionFact> Publish, IReadOnlySet<string> Published);

/// <summary>
/// Pure publication policy. A fact is published once per occurrence: the
/// first sweep that sees its key publishes it, later sweeps that still see it
/// stay quiet. A key the sweep no longer sees is forgotten, so the same key
/// published later is a new occurrence. Keys of a type whose source failed in
/// this sweep are kept as they are, so a read error never re-publishes facts.
/// </summary>
public static class OperatorDecisionPolicy
{
    public static OperatorDecisionDiff Diff(
        IReadOnlySet<string> published,
        IReadOnlyList<OperatorDecisionFact> current,
        IReadOnlySet<string> observedTypes)
    {
        var publish = new List<OperatorDecisionFact>();
        var next = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fact in current)
        {
            if (!next.Add(fact.Key)) continue;
            if (!published.Contains(fact.Key)) publish.Add(fact);
        }
        foreach (var key in published)
            if (!observedTypes.Contains(OperatorDecisionTypes.OfKey(key)))
                next.Add(key);
        return new OperatorDecisionDiff(publish, next);
    }

    /// <summary>
    /// Whether a reader sees an event. A project event needs the project; an
    /// installation-wide event needs an unrestricted reader. The watcher
    /// heartbeat names no project data and the lease is per installation, so
    /// every reader of the stream sees it.
    /// </summary>
    public static bool Shows(OperatorDecisionEvent item, OperatorDecisionReader reader)
    {
        if (reader.Unrestricted || item.Type == OperatorDecisionTypes.WatcherHeartbeat) return true;
        return item.Project is { } project && reader.AllowsProject(project);
    }
}

/// <summary>The project scope of the principal reading the stream.</summary>
public sealed record OperatorDecisionReader(bool Unrestricted, Func<string, bool> AllowsProject)
{
    public static readonly OperatorDecisionReader Everything = new(true, _ => true);
}
