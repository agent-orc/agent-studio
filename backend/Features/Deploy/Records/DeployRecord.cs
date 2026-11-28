namespace AgentStudio.Deploy;

public sealed record DeployRecordStep(string Name, DateTimeOffset? StartedAtUtc = null,
    DateTimeOffset? EndedAtUtc = null, string? Outcome = null, string? Note = null);

/// <summary>The cumulative observations supplied by the chain or by one import row.</summary>
public record DeployRecordReport
{
    public string Id { get; init; } = "";
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset? EndedAtUtc { get; init; }
    public string? FromCommit { get; init; }
    public string ToCommit { get; init; } = "";
    public string? Version { get; init; }
    public string? Outcome { get; init; }
    public int? OutageSeconds { get; init; }
    public DeployRecordStep[]? Steps { get; init; }
    public string[]? Machines { get; init; }
    public string[]? Components { get; init; }
    public string? Note { get; init; }

    internal string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || Id.Length > 128 || Id.Any(char.IsControl))
            return "id must contain 1 to 128 printable characters.";
        if (StartedAtUtc == default) return "startedAtUtc is required.";
        if (!IsSha(ToCommit) || FromCommit is not null && !IsSha(FromCommit))
            return "toCommit and any supplied fromCommit must be full Git commit SHAs.";
        if (EndedAtUtc < StartedAtUtc) return "endedAtUtc precedes startedAtUtc.";
        if (Outcome is not (null or "succeeded" or "rolled-back" or "stopped-before-switch"))
            return "outcome must be succeeded, rolled-back or stopped-before-switch.";
        if ((Outcome is null) != (EndedAtUtc is null)) return "Supply endedAtUtc and outcome together.";
        if (OutageSeconds < 0) return "outageSeconds must be nonnegative.";
        if (Machines?.Any(string.IsNullOrWhiteSpace) == true || Components?.Any(string.IsNullOrWhiteSpace) == true)
            return "Each reported machine and component needs a name.";
        foreach (var step in Steps ?? [])
        {
            if (string.IsNullOrWhiteSpace(step?.Name)) return "Each step needs a name.";
            if (step.StartedAtUtc < StartedAtUtc || step.EndedAtUtc < StartedAtUtc ||
                step.EndedAtUtc < step.StartedAtUtc || step.StartedAtUtc > EndedAtUtc || step.EndedAtUtc > EndedAtUtc)
                return "Step times must be ordered and within the deployment.";
            if (step.Outcome is not (null or "running" or "succeeded" or "failed" or "skipped"))
                return "Step outcome must be running, succeeded, failed or skipped.";
            if (step.EndedAtUtc is not null && step.Outcome == "running")
                return "A running step cannot have an end time.";
        }
        return null;
    }

    private static bool IsSha(string? value) => value is { Length: 40 } && value.All(char.IsAsciiHexDigit);
}

public sealed record DeployRecord : DeployRecordReport
{
    public DeployRecord() { }
    internal DeployRecord(DeployRecordReport report) : base(report) { }

    public string[]? CardsInRange { get; init; }
    public string[]? CardsShipped => Outcome switch
    {
        "succeeded" => CardsInRange,
        "rolled-back" or "stopped-before-switch" => [],
        _ => null,
    };
    public string? CardRangeDetail { get; init; }

    internal DeployRecord At(DateTimeOffset now) => Outcome is not null ? this : this with
    {
        Outcome = now - StartedAtUtc >= TimeSpan.FromHours(2) ? "unknown-outcome" : "running",
    };
}

public sealed record DeployRecordPage(IReadOnlyList<DeployRecord> Items, int Total, int Offset, int Limit);
public sealed record DeployRecordImport(int Imported, int Skipped);
