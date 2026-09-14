using System.Text.Json;

namespace AgentStudio.Git;

/// <summary>
/// Per-project audit trail for branch reclamation (AGT-2793 requirement 3):
/// one append-only JSONL row per deleted ref, so the reclaim history survives
/// even after the ref itself is gone and does not depend on git reflog or log
/// retention. Mirrors the workspace-scoped <c>logs/analysis/&lt;project&gt;/index.jsonl</c>
/// convention (<see cref="AgentStudio.Analysis.AnalysisReportPaths"/>), but
/// synchronous and lock-guarded to fit inline into the retention service's
/// existing synchronous call path instead of adding an async store for a
/// low-volume, best-effort write.
/// </summary>
public sealed class BranchRetentionEvidenceWriter
{
    private static readonly object WriteLock = new();
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = false };

    private readonly IConfiguration _configuration;
    private readonly ILogger<BranchRetentionEvidenceWriter> _logger;

    public BranchRetentionEvidenceWriter(
        IConfiguration configuration, ILogger<BranchRetentionEvidenceWriter> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Returns the report file path for a project, or null when
    /// <c>TaskRepository</c> is not configured (fixtures/tests without a
    /// workspace root - evidence recording is skipped, never fatal).
    /// </summary>
    public string? ReportFile(string project)
    {
        var workspace = _configuration["TaskRepository"];
        if (string.IsNullOrWhiteSpace(workspace) || string.IsNullOrWhiteSpace(project)) return null;
        return Path.Combine(workspace, "projects", project, "reports", "git-branch-reclaim.jsonl");
    }

    /// <summary>
    /// Appends one JSONL row per deleted action. No-op for kept actions and
    /// for dry runs (which never set <see cref="BranchRetentionAction.Deleted"/>).
    /// Failures are logged and swallowed - evidence recording never blocks or
    /// reverses a deletion that already happened.
    /// </summary>
    public void AppendDeleted(string project, IEnumerable<BranchRetentionAction> actions)
    {
        var deleted = actions.Where(a => a.Deleted).ToList();
        if (deleted.Count == 0) return;

        var file = ReportFile(project);
        if (file is null) return;

        try
        {
            var now = DateTimeOffset.UtcNow;
            var lines = deleted.Select(action => JsonSerializer.Serialize(
                new BranchRetentionEvidenceRow(
                    action.Branch,
                    action.TipSha,
                    action.Namespace?.ToString(),
                    action.Reason,
                    action.TaskKey,
                    now),
                JsonOptions));

            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            lock (WriteLock)
            {
                File.AppendAllLines(file, lines);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "branch-reclaim-evidence-write-failed project={Project} file={File}", project, file);
        }
    }

    private sealed record BranchRetentionEvidenceRow(
        string Ref,
        string Sha,
        string? Class,
        string Reason,
        string? TaskKey,
        DateTimeOffset TimestampUtc);
}
