namespace AgentStudio.Projects;

/// <summary>Shared ledger rule for a first completion and a completion after coding rework.</summary>
public static class CompletionAnchors
{
    public sealed record Anchor(DateTime At, string Kind);

    public sealed record Result(IReadOnlyList<Anchor> Real, IReadOnlyList<DateTime> Artefacts)
    {
        public Anchor? LastReal => Real.Count == 0 ? null : Real[^1];
    }

    public static Result Find(IEnumerable<TimelineEvent> events)
    {
        var real = new List<Anchor>();
        var artefacts = new List<DateTime>();
        var sawProgressSinceCompletion = false;
        var sawCompletion = false;
        foreach (var row in events.Where(row => row.Kind == TimelineEventKinds.LaneChanged)
                     .OrderBy(row => row.Ts))
        {
            var to = row.Details is not null && row.Details.TryGetValue("to", out var target)
                ? target : null;
            if (to == TaskStates.Progress && sawCompletion) sawProgressSinceCompletion = true;
            if (to != TaskStates.Completed) continue;
            if (!sawCompletion || sawProgressSinceCompletion)
                real.Add(new Anchor(Utc(row.Ts), sawCompletion ? "after-rework" : "first"));
            else
                artefacts.Add(Utc(row.Ts));
            sawCompletion = true;
            sawProgressSinceCompletion = false;
        }
        return new Result(real, artefacts);
    }

    private static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
