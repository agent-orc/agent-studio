using System.Text.Json;

namespace AgentStudio.Git;

/// <summary>
/// Persists one sweep run as <c>reports/branch-sweep/&lt;project&gt;/&lt;timestamp&gt;.json</c>
/// plus a sibling <c>.md</c> summary under the workspace root, and reads the
/// latest run back for the operator UI. The reports are workspace-scoped rather
/// than project-folder-scoped because a sweep is a repository fact: two projects
/// can share one repository, and the operator compares runs across repositories.
///
/// <para>
/// Writing is best-effort in the same sense as
/// <see cref="BranchRetentionEvidenceWriter"/>: without a configured
/// <c>TaskRepository</c> there is nowhere to write and the sweep still returns
/// its report to the caller.
/// </para>
/// </summary>
public sealed class BranchSweepReportStore
{
    /// <summary>Workspace-relative root for every sweep report.</summary>
    public const string RelativeRoot = "reports/branch-sweep";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly IConfiguration _configuration;
    private readonly ILogger<BranchSweepReportStore> _logger;

    public BranchSweepReportStore(IConfiguration configuration, ILogger<BranchSweepReportStore> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>Report folder for a project, or null when the workspace root is not configured.</summary>
    public string? DirectoryFor(string project)
    {
        var workspace = _configuration["TaskRepository"];
        if (string.IsNullOrWhiteSpace(workspace) || string.IsNullOrWhiteSpace(project)) return null;
        var safe = SafeSegment(project);
        if (safe.Length == 0) return null;
        return Path.Combine(workspace, "reports", "branch-sweep", safe);
    }

    /// <summary>
    /// Writes the JSON report and its markdown summary. Returns the JSON path,
    /// or null when nothing was written.
    /// </summary>
    public string? Write(BranchSweepReport report)
    {
        var directory = DirectoryFor(report.Project);
        if (directory is null) return null;

        var stamp = BranchSweepReportBuilder.Stamp(report.StartedAtUtc);
        var jsonPath = Path.Combine(directory, stamp + ".json");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, JsonOptions));
            File.WriteAllText(
                Path.Combine(directory, stamp + ".md"),
                BranchSweepReportBuilder.RenderMarkdown(report));
            return jsonPath;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "branch-sweep-report-write-failed project={Project} file={File}", report.Project, jsonPath);
            return null;
        }
    }

    /// <summary>Newest stored report for a project, or null when there is none.</summary>
    public BranchSweepReport? Latest(string project)
    {
        var directory = DirectoryFor(project);
        if (directory is null || !Directory.Exists(directory)) return null;
        var newest = Directory.EnumerateFiles(directory, "*.json")
            .OrderByDescending(path => Path.GetFileName(path), StringComparer.Ordinal)
            .FirstOrDefault();
        if (newest is null) return null;
        try
        {
            return JsonSerializer.Deserialize<BranchSweepReport>(File.ReadAllText(newest), JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "branch-sweep-report-read-failed project={Project} file={File}", project, newest);
            return null;
        }
    }

    /// <summary>Stamps of the stored runs, newest first, for the run history strip.</summary>
    public IReadOnlyList<string> History(string project, int limit = 20)
    {
        var directory = DirectoryFor(project);
        if (directory is null || !Directory.Exists(directory)) return [];
        return Directory.EnumerateFiles(directory, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(stamp => !string.IsNullOrEmpty(stamp))
            .Select(stamp => stamp!)
            .OrderByDescending(stamp => stamp, StringComparer.Ordinal)
            .Take(Math.Clamp(limit, 1, 200))
            .ToList();
    }

    /// <summary>
    /// Project names become one path segment, so anything that could escape the
    /// report root is replaced rather than rejected.
    /// </summary>
    private static string SafeSegment(string value)
    {
        var characters = value.Trim()
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-')
            .ToArray();
        return new string(characters).Trim('.', '-');
    }
}
