using System.Text;
using System.Text.Json;

namespace AgentStudio.Runner;

/// <summary>
/// Per-project chronological log of orchestrator activity: decisions made,
/// actions taken, observations recorded, user interventions. Lives next
/// to the watched project's job tree as
/// <c>&lt;watchPath&gt;/.orchestrator/orchestrator.jsonl</c>. One JSONL line
/// per entry; tolerant to torn writes (a torn line is one lost entry, not
/// a corrupted document). The frontend reads this directly to render the
/// orchestrator feed in the project detail view.
///
/// <para>
/// This is the foundation for the larger orchestrator-as-CLI vision:
/// today the entries here are written by the runner itself (queued
/// follow-ups, watchdog escalations, recovery fallbacks). Phase D and
/// later will add a dedicated orchestrator process that writes its own
/// reasoning into the same file with the same shape, so the feed is one
/// timeline regardless of who took the action.
/// </para>
/// </summary>
public class OrchestratorLog
{
    private readonly ILogger<OrchestratorLog> _logger;

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public OrchestratorLog(ILogger<OrchestratorLog> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Append a single entry to the project's orchestrator log. Best-effort:
    /// a write failure is logged and swallowed so the log never blocks a
    /// runtime decision.
    /// </summary>
    public bool Append(string watchPath, OrchestratorLogEntry entry)
    {
        if (string.IsNullOrWhiteSpace(watchPath)) return false;
        try
        {
            var path = ResolvePath(watchPath);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            var line = JsonSerializer.Serialize(entry, WriteOpts) + Environment.NewLine;
            File.AppendAllText(path, line, Encoding.UTF8);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to append orchestrator log entry under {WatchPath}", watchPath);
            return false;
        }
    }

    /// <summary>
    /// Read all entries for a project, oldest first. Tolerant to torn
    /// trailing lines: a line that fails to parse is skipped.
    /// </summary>
    public List<OrchestratorLogEntry> Read(string watchPath)
    {
        var result = new List<OrchestratorLogEntry>();
        if (string.IsNullOrWhiteSpace(watchPath)) return result;
        var path = ResolvePath(watchPath);
        if (!File.Exists(path)) return result;
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var entry = JsonSerializer.Deserialize<OrchestratorLogEntry>(line, ReadOpts);
                if (entry != null) result.Add(entry);
            }
            catch (Exception __ex)
            {
                SilentCatch.Note(__ex, "OrchestratorLog: Best-effort: skip torn / malformed lines.");
                // Best-effort: skip torn / malformed lines.
            }
        }
        return result;
    }

    private static string ResolvePath(string watchPath) =>
        Path.Combine(watchPath, ".orchestrator", "orchestrator.jsonl");
}

/// <summary>
/// One entry in <see cref="OrchestratorLog"/>. Schema is intentionally
/// flat; consumer rendering keys off <see cref="Kind"/> + <see cref="Topic"/>.
/// New kinds can be added without breaking older consumers - unknown
/// values render with the generic shape.
/// </summary>
public record OrchestratorLogEntry
{
    public DateTime Ts { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// One of <see cref="OrchestratorLogKinds"/>. Decision = the orchestrator
    /// chose between options. Action = it actually did something visible.
    /// Observation = it noted something without acting. Intervention = the
    /// user overruled or directed the orchestrator.
    /// </summary>
    public string Kind { get; init; } = OrchestratorLogKinds.Action;

    /// <summary>
    /// Short topic tag for grouping in the feed UI. See
    /// <see cref="OrchestratorLogTopics"/>.
    /// </summary>
    public string Topic { get; init; } = "general";

    /// <summary>One-line headline shown in the feed.</summary>
    public string Summary { get; init; } = "";

    /// <summary>Optional longer prose, rendered when the entry is expanded.</summary>
    public string? Reasoning { get; init; }

    /// <summary>Job that was the subject of this entry, when applicable.</summary>
    public string? JobId { get; init; }

    /// <summary>
    /// Agent-message-bus participant that produced this token event, when the
    /// entry was projected from the bus. Legacy orchestrator.jsonl entries
    /// leave this null and keep their historical job-title categorisation.
    /// </summary>
    public string? ParticipantId { get; init; }

    /// <summary>
    /// Token usage for this orchestrator action, when the orchestrator
    /// itself made an LLM call. Today most entries are written by the
    /// runner without an LLM call and leave this null.
    /// </summary>
    public OrchestratorTokenUsage? TokenUsage { get; init; }

    /// <summary>
    /// Informational TokenEconomy alternatives considered beside this
    /// decision. Their presence never implies that Agent Studio switched the
    /// selected route.
    /// </summary>
    public BetterCandidateNote? BetterCandidates { get; init; }

    /// <summary>
    /// Future hook (Phase F): user override on this entry. Today always
    /// null; the data shape is forward-compatible.
    /// </summary>
    public OrchestratorIntervention? UserOverride { get; init; }
}

// OrchestratorTokenUsage moved to AgentTaskboard.Shared (Runner/OrchestratorTokenUsage.cs)
// so the executor-side OneShot result envelope can reference it without depending
// on the server-side orchestrator log types. Namespace preserved.

public record OrchestratorIntervention
{
    public DateTime At { get; init; }
    public string NewDirection { get; init; } = "";
}

public static class OrchestratorLogKinds
{
    public const string Alert = "alert";
    public const string Decision = "decision";
    public const string Action = "action";
    public const string Observation = "observation";
    public const string Intervention = "intervention";
}

public static class OrchestratorLogTopics
{
    public const string PipelineHealth = "pipeline-health";
    public const string FailureIntervention = "failure-intervention";
    public const string TaskQueued = "task-queued";
    public const string Watchdog = "watchdog";
    public const string Recovery = "recovery";
    public const string TaskPicked = "task-picked";
    public const string General = "general";
    /// <summary>
    /// AGT-2055: the algorithmic pre-launch quota / load-steering decisions
    /// (switch model / throttle / quiet wait / normal launch) with their
    /// burn-rate and projection numbers. These feed lines are the data source
    /// for the separate load-distribution view.
    /// </summary>
    public const string LoadDistribution = "load-distribution";
}
