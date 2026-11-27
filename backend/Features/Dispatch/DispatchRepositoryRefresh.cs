using System.Text.Json.Serialization;

namespace AgentStudio.Dispatch;

public sealed record DispatchRepositoryState(
    [property: JsonRequired] bool Available,
    [property: JsonRequired] DateTimeOffset NextProbeAt,
    [property: JsonRequired] int Failures)
{
    internal const int MaxFailures = 6;
}

/// <summary>Bounds Git probes for projects without an integration tip and logs state transitions.</summary>
internal sealed class DispatchRepositoryRefresh(DispatchStore store, ILogger logger)
{
    public bool IsDue(string project, DateTimeOffset now) => store.ReadRepositoryState(project) is not { Available: false } state
        || now >= state.NextProbeAt;

    public (string Repo, string Tip)? Probe(string project, DateTimeOffset now, Func<(string Repo, string Tip)?> collect)
    {
        if (!IsDue(project, now)) return null;
        var previous = store.ReadRepositoryState(project);
        var result = collect();
        if (result is null)
        {
            var failures = Math.Min(DispatchRepositoryState.MaxFailures, (previous?.Failures ?? 0) + 1);
            store.WriteRepositoryState(project, new(false, now.AddMinutes(Math.Min(60, 1 << failures)), failures));
            if (previous is not { Available: false })
                logger.LogInformation("Dispatch forecast skipped for {Project}: no repository or reachable integration tip", project);
        }
        else if (previous is { Available: false })
        {
            store.WriteRepositoryState(project, new(true, now, 0));
            logger.LogInformation("Dispatch forecast repository available again for {Project}", project);
        }
        return result;
    }
}
