namespace AgentStudio.Readiness;

/// <summary>The state of one readiness line. Ordered from best to worst by <see cref="Rank"/>.</summary>
public static class ReadinessStates
{
    public const string Pass = "pass";
    public const string Unknown = "unknown";
    public const string Warn = "warn";
    public const string Fail = "fail";

    public static int Rank(string state) => state switch
    {
        Fail => 3,
        Warn => 2,
        Unknown => 1,
        _ => 0,
    };
}

/// <summary>What a readiness line is about: the server itself, one project, or one execution host.</summary>
public static class ReadinessScopes
{
    public const string Server = "server";
    public const string Project = "project";
    public const string Host = "host";
}

/// <summary>
/// One line of the server readiness report: its state, what that state means
/// for the installation, and the exact step that fixes it. A passing line has
/// no remedy.
/// </summary>
public sealed record ReadinessCheck(
    string Id,
    string Title,
    string Scope,
    string? Subject,
    string State,
    string Meaning,
    string? Remedy);

/// <summary>
/// The readiness report the server computes about itself (AGT-3412). The
/// operations page and the move tool read the same report.
/// </summary>
public sealed record ServerReadinessReport(
    DateTimeOffset? ComputedAt,
    string State,
    int Failing,
    int Attention,
    IReadOnlyList<ReadinessCheck> Checks)
{
    /// <summary>Before the first collection finished: no line is known yet.</summary>
    public static ServerReadinessReport Pending { get; } = new(null, "pending", 0, 0, []);

    public static ServerReadinessReport From(DateTimeOffset computedAt, IReadOnlyList<ReadinessCheck> checks)
    {
        var worst = checks.Count == 0 ? ReadinessStates.Pass
            : checks.MaxBy(check => ReadinessStates.Rank(check.State))!.State;
        return new(computedAt, worst,
            checks.Count(check => check.State == ReadinessStates.Fail),
            checks.Count(check => check.State is ReadinessStates.Warn or ReadinessStates.Unknown),
            checks);
    }

    /// <summary>The same report restricted to lines a caller may see.</summary>
    public ServerReadinessReport Where(Func<ReadinessCheck, bool> visible)
        => ComputedAt is { } at ? From(at, Checks.Where(visible).ToArray()) : this;
}
