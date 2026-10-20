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
}

/// <summary>Configuration of the AI Operator stall interventions (<c>StallIntervention</c> section).</summary>
public sealed record StallInterventionOptions
{
    /// <summary>Kill switch, re-read every pass.</summary>
    public bool Enabled { get; init; } = true;
    public int IntervalSeconds { get; init; } = 60;
    public StallInterventionLimits Limits { get; init; } = new();

    /// <summary>Operations Agent service name per stalled role.</summary>
    public IReadOnlyDictionary<string, string> Services { get; init; } = DefaultServices;

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
        return new StallInterventionOptions
        {
            Enabled = section.GetValue("Enabled", true),
            IntervalSeconds = Math.Clamp(section.GetValue("IntervalSeconds", 60), 15, 3600),
            Services = services,
            Limits = new StallInterventionLimits(
                MaxPerHostPerHour: Math.Clamp(section.GetValue("MaxPerHostPerHour", defaults.MaxPerHostPerHour), 1, 20),
                LoopWindowHours: Math.Clamp(section.GetValue("LoopWindowHours", defaults.LoopWindowHours), 1, 48),
                MaxRepeatsPerSubject: Math.Clamp(section.GetValue("MaxRepeatsPerSubject", defaults.MaxRepeatsPerSubject), 1, 10),
                MinAdmission: Math.Clamp(section.GetValue("MinAdmission", defaults.MinAdmission), 1, 64),
                CalmMinutesBeforeRestore: Math.Clamp(section.GetValue("CalmMinutesBeforeRestore", defaults.CalmMinutesBeforeRestore), 5, 240),
                RestoreBelowTargetFraction: Math.Clamp(section.GetValue("RestoreBelowTargetFraction", defaults.RestoreBelowTargetFraction), 0.3, 1.0),
                GateMemberDeadlineHours: Math.Clamp(section.GetValue("GateMemberDeadlineHours", defaults.GateMemberDeadlineHours), 1, 168),
                MaxCalmWaitHours: Math.Clamp(section.GetValue("MaxCalmWaitHours", defaults.MaxCalmWaitHours), 1, 48)),
        };
    }
}

/// <summary>The latest decision per subject, including holds, for the Studio view.</summary>
public sealed record StallInterventionDecisionView(string Subject, string Kind, string Disposition,
    string Action, string ReasonCode, string Detail, DateTime DecidedAtUtc);
