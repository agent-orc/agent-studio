namespace AgentStudio.Watcher;

/// <summary>
/// AGT-3207: one AI Operator intervention on an operational stall, with its
/// plain-language receipt. Records of one subject form a chain through
/// <see cref="PreviousInterventionId"/>; an escalation carries the chain.
/// </summary>
public sealed record StallInterventionRecord
{
    public required string Id { get; init; }
    public required string WriteKey { get; init; }
    public required string Subject { get; init; }
    public required string Kind { get; init; }
    public required string Host { get; init; }

    /// <summary>The coding host's client identity id; admission is read and set by it, never by display name.</summary>
    public string? HostClientId { get; init; }
    public required string Action { get; init; }
    public required string Autonomy { get; init; }
    public required string Disposition { get; init; }
    public required string ReasonCode { get; init; }
    public required string Status { get; init; }
    public string Project { get; init; } = QuotaProbeSignalProbe.WorkspaceProject;
    public string? CaseId { get; init; }
    public string? Fingerprint { get; init; }
    public string Trigger { get; init; } = StallInterventionTriggers.Stall;

    /// <summary>Who decided: <c>monolith</c> in process or <c>engine</c> through an occurrence.</summary>
    public string DecidedBy { get; init; } = "monolith";
    public string? OccurrenceRunId { get; init; }

    public string Observed { get; init; } = "";
    public string Done { get; init; } = "";
    public string Why { get; init; } = "";
    public string Before { get; init; } = "";
    public string After { get; init; } = "";
    public string Undo { get; init; } = "";
    public string Receipt { get; init; } = "";
    public string? Proposal { get; init; }

    public List<string> AffectedCards { get; init; } = [];
    public List<string> Members { get; init; } = [];

    /// <summary>The batch gate review attempts frozen at decision time; only these are dropped.</summary>
    public List<string> AttemptIds { get; init; } = [];
    public string? Service { get; init; }
    public string? Provider { get; init; }
    public string? BatchId { get; init; }
    public int? TargetAdmission { get; init; }
    public int? PreviousAdmission { get; init; }

    public DateTime DecidedAtUtc { get; init; }
    public DateTime? FollowUpDueUtc { get; init; }
    public DateTime? ResolvedAtUtc { get; init; }

    /// <summary>Whether the effect was measured after the action, not only decided (17.10.2026 lesson).</summary>
    public bool? EffectObserved { get; init; }
    public string? EffectEvidence { get; init; }
    public string? Outcome { get; init; }

    public string? PreviousInterventionId { get; init; }
    public List<string> Chain { get; init; } = [];

    public bool Undoable { get; init; }
    public DateTime? UndoneAtUtc { get; init; }
    public string? UndoneBy { get; init; }

    /// <summary>
    /// What ended a lowered admission: the restoring intervention's id, an
    /// undo, or a person's own change. Null while the reduction is in force.
    /// </summary>
    public string? RevertedBy { get; init; }

    /// <summary>
    /// An AI Operator reduction of a host's admission that may still be in
    /// force, whatever the record's status (also an escalated one whose effect
    /// was not confirmed): it keeps its Undo, is never lowered further without
    /// a person, and is restored once calm. The readers compare the live
    /// admission with <see cref="TargetAdmission"/> before they rely on it.
    /// </summary>
    public bool AdmissionInForce => Action == StallInterventionActions.LowerAdmission
        && RevertedBy is null && UndoneAtUtc is null
        && Status is not (StallInterventionStatuses.Superseded or StallInterventionStatuses.Observed
            or StallInterventionStatuses.Failed);
}

/// <summary>How far the AI Operator goes on stall events (<c>StallIntervention:Mode</c>).</summary>
public static class StallInterventionModes
{
    /// <summary>Nothing is decided.</summary>
    public const string Off = "off";

    /// <summary>The default: decide and write "would do" receipts; no action changes the system.</summary>
    public const string Observe = "observe";

    /// <summary>A0/A1 interventions act, each behind its per-action switch.</summary>
    public const string Act = "act";
}

/// <summary>Configuration of the AI Operator stall interventions (<c>StallIntervention</c> section).</summary>
public sealed record StallInterventionOptions
{
    /// <summary>One of <see cref="StallInterventionModes"/>, re-read every pass. <c>Enabled=false</c> still means off.</summary>
    public string Mode { get; init; } = StallInterventionModes.Observe;
    public int IntervalSeconds { get; init; } = 60;
    public StallInterventionLimits Limits { get; init; } = new();

    /// <summary>Per-action switches (<c>StallIntervention:Actions:&lt;action&gt;</c>); a missing entry is on.</summary>
    public IReadOnlyDictionary<string, bool> Actions { get; init; } = new Dictionary<string, bool>(StringComparer.Ordinal);

    /// <summary>Operations Agent service name per stalled role.</summary>
    public IReadOnlyDictionary<string, string> Services { get; init; } = DefaultServices;

    public bool Enabled => Mode != StallInterventionModes.Off;

    /// <summary>True when the mutating action may change the system now; otherwise it writes a "would do" receipt.</summary>
    public bool Acts(string action) => Mode == StallInterventionModes.Act && Actions.GetValueOrDefault(action, true);

    public static readonly IReadOnlyDictionary<string, string> DefaultServices =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["review"] = "review-daemon",
            ["gate"] = "gate-worker",
            ["engine"] = "orchestrator-engine",
        };

    public static StallInterventionOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("StallIntervention");
        var defaults = new StallInterventionLimits();
        var services = new Dictionary<string, string>(DefaultServices, StringComparer.Ordinal);
        foreach (var child in section.GetSection("Services").GetChildren())
            if (!string.IsNullOrWhiteSpace(child.Value)) services[child.Key] = child.Value.Trim();
        var actions = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var child in section.GetSection("Actions").GetChildren())
            if (bool.TryParse(child.Value, out var on)) actions[child.Key] = on;
        var mode = section["Mode"]?.Trim().ToLowerInvariant() switch
        {
            StallInterventionModes.Act => StallInterventionModes.Act,
            StallInterventionModes.Off => StallInterventionModes.Off,
            _ => StallInterventionModes.Observe,
        };
        return new StallInterventionOptions
        {
            Mode = section.GetValue("Enabled", true) ? mode : StallInterventionModes.Off,
            IntervalSeconds = Math.Clamp(section.GetValue("IntervalSeconds", 60), 15, 3600),
            Actions = actions,
            Services = services,
            Limits = new StallInterventionLimits(
                MaxPerHostPerHour: Math.Clamp(section.GetValue("MaxPerHostPerHour", defaults.MaxPerHostPerHour), 1, 20),
                LoopWindowHours: Math.Clamp(section.GetValue("LoopWindowHours", defaults.LoopWindowHours), 1, 48),
                MaxRepeatsPerSubject: Math.Clamp(section.GetValue("MaxRepeatsPerSubject", defaults.MaxRepeatsPerSubject), 1, 10),
                MinAdmission: Math.Clamp(section.GetValue("MinAdmission", defaults.MinAdmission), 1, 64),
                CalmMinutesBeforeRestore: Math.Clamp(section.GetValue("CalmMinutesBeforeRestore", defaults.CalmMinutesBeforeRestore), 5, 240),
                RestoreBelowTargetFraction: Math.Clamp(section.GetValue("RestoreBelowTargetFraction", defaults.RestoreBelowTargetFraction), 0.3, 1.0),
                GateMemberDeadlineHours: Math.Clamp(section.GetValue("GateMemberDeadlineHours", defaults.GateMemberDeadlineHours), 1, 168),
                MaxCalmWaitHours: Math.Clamp(section.GetValue("MaxCalmWaitHours", defaults.MaxCalmWaitHours), 1, 48),
                MaxActionsPerCard: Math.Clamp(section.GetValue("MaxActionsPerCard", defaults.MaxActionsPerCard), 1, 10)),
        };
    }
}

/// <summary>The latest decision per subject, including holds, for the Studio view.</summary>
public sealed record StallInterventionDecisionView(string Subject, string Kind, string Disposition,
    string Action, string ReasonCode, string Detail, DateTime DecidedAtUtc, string? Project = null);
