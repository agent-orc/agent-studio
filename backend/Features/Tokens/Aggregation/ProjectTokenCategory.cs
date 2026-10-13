namespace AgentStudio.Tokens;

/// <summary>
/// Token Usage category of a ledger row: <see cref="Job"/>,
/// <see cref="Supporting"/>, or <see cref="Orchestrator"/>. The participant
/// prefix decides first; rows without one fall back to the job id and the
/// job title prefix.
/// </summary>
public static class ProjectTokenCategory
{
    public const string Job = "job";
    public const string Supporting = "supporting";
    public const string Orchestrator = "orchestrator";

    /// <summary>
    /// Title prefixes that mark a job as a "supporting" loop in the
    /// Token-Usage split. These are the job kinds taxonomy.md explicitly
    /// names as supporting analysis loops (audits, councils, drift, etc.);
    /// new prefixes should be added here as the queue grows new
    /// orchestrator-spawned job categories.
    /// </summary>
    public static readonly string[] SupportingJobTitlePrefixes =
    [
        "Security audit",
        "Drift analysis",
        "Output pattern analysis",
        "Task check",
        "Traceability check",
        "Council review",
        "Source map",
        "Screenshot critique",
    ];

    /// <summary>Category of a job id without a ledger row (participant unknown).</summary>
    public static string Of(string? jobId, IReadOnlyDictionary<string, TaskInfo> jobsById)
        => Of(new OrchestratorLogEntry { JobId = jobId }, jobsById);

    /// <summary>Category of one ledger row.</summary>
    public static string Of(OrchestratorLogEntry entry, IReadOnlyDictionary<string, TaskInfo> jobsById)
    {
        if (TokenParticipants.IsAgent(entry.ParticipantId)) return Job;
        if (TokenParticipants.IsSupporting(entry.ParticipantId)) return Supporting;
        if (TokenParticipants.IsOrchestrator(entry.ParticipantId)) return Orchestrator;

        var jobId = entry.JobId;
        if (string.IsNullOrWhiteSpace(jobId)) return Orchestrator;
        if (jobsById.TryGetValue(jobId!, out var info))
        {
            var title = info.Title ?? "";
            foreach (var prefix in SupportingJobTitlePrefixes)
            {
                if (title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return Supporting;
            }
        }
        return Job;
    }

    /// <summary>The stronger of two categories for one job (Job over Supporting over Orchestrator).</summary>
    public static string Prefer(string? current, string candidate)
    {
        if (string.IsNullOrWhiteSpace(current)) return candidate;
        return CategoryRank(candidate) > CategoryRank(current) ? candidate : current;
    }

    private static int CategoryRank(string category) => category switch
    {
        Job => 3,
        Supporting => 2,
        Orchestrator => 1,
        _ => 0
    };
}
