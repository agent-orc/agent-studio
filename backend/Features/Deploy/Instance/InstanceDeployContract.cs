namespace AgentStudio.Deploy;

// AGT-3879: the running Studio instance as the Production environment of its
// own source project. Operator scripts outside the Studio deploy it and report
// each deploy through the record API below; the Bereitstellung area reads the
// records together with the facts the background refresh derives from git.

/// <summary>Run states of an instance deploy. <c>stalled</c> is derived: no report for 30 minutes.</summary>
public static class InstanceDeployStates
{
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Stalled = "stalled";
    public const string Skipped = "skipped";

    public static bool IsStepState(string? state) => state is Running or Succeeded or Failed or Skipped;
    public static bool IsTerminal(string state) => state is Succeeded or Failed or Skipped;
}

/// <summary>The step kinds the existing deploy scripts run, in their order.</summary>
public static class InstanceDeployStepKinds
{
    public const string Build = "build";
    public const string ServerSwitch = "server-switch";
    public const string Host = "host";
    public const string ReviewHost = "review-host";
    public const string Other = "other";

    /// <summary>
    /// The kind a step name implies: <c>build:server</c>, <c>switch:server</c>,
    /// <c>host:a</c>, <c>review-host:b</c>, or the journal step names of
    /// <c>deploy/networked/deploy.py</c> (<c>server_switch</c>,
    /// <c>coding_install:a</c>, <c>review_recreate:b</c>).
    /// </summary>
    public static string FromName(string name)
    {
        var prefix = name.Split(':', 2)[0];
        return prefix switch
        {
            "build" => Build,
            "switch" or "server-switch" or "server_switch" => ServerSwitch,
            "host" or "coding_install" or "coding_health" or "model_probe" or "host_drain" or "host_resume" => Host,
            "review-host" or "review_copy" or "review_build" or "review_idle" or "review_recreate" or "review_verify"
                => ReviewHost,
            _ => Other,
        };
    }

    public static bool IsKnown(string? kind) => kind is Build or ServerSwitch or Host or ReviewHost or Other;
}

/// <param name="RunId">Chosen by the script, so a retried start names the same run.</param>
/// <param name="Components">Component name to built version: server image, orchestrator engine, runner host package, gate worker release, review host image.</param>
public sealed record InstanceDeployStartRequest(string? RunId, string? TargetCommit, string? Version,
    IReadOnlyDictionary<string, string>? Components = null, DateTime? StartedAtUtc = null);

public sealed record InstanceDeployStepRequest(string? State, string? Kind = null, DateTime? StartedAtUtc = null,
    DateTime? FinishedAtUtc = null, string? Message = null);

public sealed record InstanceDeployFinishRequest(string? Outcome, DateTime? FinishedAtUtc = null, string? Message = null);

public sealed record InstanceDeployStep
{
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public required string State { get; init; }
    public DateTime? StartedAtUtc { get; init; }
    public DateTime? FinishedAtUtc { get; init; }
    public string? Message { get; init; }
    /// <summary>When the Studio last received a report that changed this step.</summary>
    public DateTime ReportedAtUtc { get; init; }
}

/// <summary>A deploy run as stored. <see cref="Outcome"/> is set by the end report only.</summary>
public sealed record InstanceDeployRun
{
    public required string Id { get; init; }
    public required string TargetCommit { get; init; }
    public required string Version { get; init; }
    public Dictionary<string, string> Components { get; init; } = new(StringComparer.Ordinal);
    public required DateTime StartedAtUtc { get; init; }
    public DateTime LastReportAtUtc { get; init; }
    public DateTime? FinishedAtUtc { get; init; }
    public string? Outcome { get; init; }
    public string? Message { get; init; }
    public string? ReportedBy { get; init; }
    /// <summary>True for the run recorded from the version route on first start (AGT-3879 item 4).</summary>
    public bool FromVersionRoute { get; init; }
    public List<InstanceDeployStep> Steps { get; init; } = [];
}

/// <summary>
/// One built version of the instance. It is live once its server switch, or
/// its whole run, succeeded. <see cref="CardKeys"/> are the cards first landed
/// in the first-parent range since the previous live release, in landing order.
/// </summary>
public sealed record InstanceRelease
{
    public required string Id { get; init; }
    public required string Version { get; init; }
    public required string CommitSha { get; init; }
    public required DateTime CreatedAtUtc { get; init; }
    public DateTime? LiveSinceUtc { get; init; }
    public string? RunId { get; init; }
    public Dictionary<string, string> Components { get; init; } = new(StringComparer.Ordinal);
    public List<string> CardKeys { get; init; } = [];
    public Dictionary<string, string> CardTitles { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>False until the background refresh read the range from git.</summary>
    public bool CardsResolved { get; init; }
    public bool FromVersionRoute { get; init; }
}

public sealed record InstanceDeployState
{
    public string? SourceProject { get; init; }
    public List<InstanceRelease> Releases { get; init; } = [];
    public List<InstanceDeployRun> Runs { get; init; } = [];
}

/// <summary>Run as the view shows it: derived state and the last step that reported.</summary>
public sealed record InstanceDeployRunView(string Id, string ReleaseId, string Version, string TargetCommit,
    string State, DateTime StartedAtUtc, DateTime? FinishedAtUtc, DateTime LastReportAtUtc, string? LastStep,
    string? FailedStep, IReadOnlyDictionary<string, string> Components, IReadOnlyList<InstanceDeployStep> Steps,
    bool FromVersionRoute, string? Message);

public sealed record InstanceCardView(string Key, string? Title, string? LandedCommit);

public sealed record InstanceReleaseView(string Id, string Version, string CommitSha, DateTime? LiveSinceUtc,
    IReadOnlyList<InstanceCardView> Cards, bool CardsResolved, bool FromVersionRoute,
    IReadOnlyDictionary<string, string> Components);

/// <summary><c>GET /api/projects/{project}/deploy/instance</c>: what is live, what the next deploy brings, the latest run.</summary>
public sealed record InstanceDeployView(string ProjectId, string EnvironmentId, string EnvironmentName,
    InstanceReleaseView? Live, InstanceDeployRunView? LatestRun, IReadOnlyList<InstanceCardView> NotLiveYet,
    string? DevelopRef, string? DevelopCommit, DateTime? FactsComputedAtUtc, int StalledAfterMinutes);

/// <summary>Git-derived facts, refreshed in the background so read requests never start git.</summary>
public sealed record InstanceDeployFacts(string Project, string? DevelopRef, string? DevelopCommit,
    string? LiveCommit, IReadOnlyList<InstanceCardView> NotLiveYet,
    IReadOnlyDictionary<string, string> LiveCardReleases, DateTime ComputedAtUtc);
