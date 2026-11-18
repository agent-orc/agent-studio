using System.Collections.Concurrent;
using System.Text.Json;
using AgentStudio.Publishing;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Projects;

public sealed record ProjectDeploymentCommit(
    string Sha,
    string ShortSha,
    string Subject,
    DateTime AuthorDateUtc);

public sealed record ProjectDeploymentRun(
    DateTime At,
    string Status,
    string HeadBefore,
    string HeadAfter,
    double DurationSeconds,
    int JobsSinceLastRestart,
    int ReviewCountAfter,
    IReadOnlyList<ProjectDeploymentCommit> Commits);

public sealed record ProjectDeploymentParameter(
    string Name,
    string Type,
    bool Required,
    JsonElement? Default,
    IReadOnlyList<string> Options);

public sealed record ProjectDeploymentTarget(
    string Id,
    string Title,
    string Kind,
    string? Template,
    string Summary,
    bool Runnable,
    string Source,
    string? Command,
    string? TargetHostId,
    IReadOnlyList<ProjectDeploymentParameter> Parameters);

public sealed record ProjectDeploymentSummary
{
    public string Project { get; init; } = "";
    public bool Available { get; init; }
    public string? Reason { get; init; }
    public string Source { get; init; } = ProjectDeploymentSummaryService.SourceName;
    public ProjectDeploymentRun? LastDeployment { get; init; }
    public IReadOnlyList<ProjectDeploymentRun> History { get; init; } = [];
    public int? PendingCount { get; init; }
    public IReadOnlyList<ProjectDeploymentCommit> PendingCommits { get; init; } = [];
    public IReadOnlyList<ProjectDeploymentTarget> Targets { get; init; } = [];
    public DeploymentTestRunReference? DefaultEvidenceRun { get; init; }
    /// <summary>
    /// The deploy-stable baseline is Studio's own self-deploy history; only a
    /// repository with the stable restart script (Studio itself) shows it (F19).
    /// </summary>
    public bool StableBaseline { get; init; }
    /// <summary>Deploy steps of a schema 2 project.yml; such a project deploys from Deploy, not from targets.</summary>
    public int PipelineDeploySteps { get; init; }
}

/// <summary>
/// Read-only deploy-stable projection. The restart JSONL is the run truth; two
/// fixed git range reads enrich its latest row with deployed and pending commits.
/// Results are briefly cached so the Project Overview poll never repeats git work.
/// </summary>
public sealed class ProjectDeploymentSummaryService
{
    internal const string SourceName = "logs/stable-restarts.jsonl";
    internal const int MaxPendingCommits = 12;
    internal const int MaxHistoryRuns = 20;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);

    private readonly IConfiguration _config;
    private readonly TaskScannerService _scanner;
    private readonly GitService _git;
    private readonly ProjectSettingsService _settings;
    private readonly ILogger<ProjectDeploymentSummaryService> _logger;
    private readonly PublishTargetService? _publish;
    private readonly TestRunService? _testRuns;
    private readonly ConcurrentDictionary<string, (DateTime At, ProjectDeploymentSummary Value)> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public ProjectDeploymentSummaryService(
        IConfiguration config,
        TaskScannerService scanner,
        GitService git,
        ProjectSettingsService settings,
        ILogger<ProjectDeploymentSummaryService> logger,
        PublishTargetService? publish = null,
        TestRunService? testRuns = null)
    {
        _config = config;
        _scanner = scanner;
        _git = git;
        _settings = settings;
        _logger = logger;
        _publish = publish;
        _testRuns = testRuns;
    }

    public ProjectDeploymentSummary? Build(string projectName)
    {
        if (!_scanner.GetWatchPaths().Any(e =>
                string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase)))
            return null;

        if (_cache.TryGetValue(projectName, out var cached)
            && DateTime.UtcNow - cached.At < CacheTtl)
            return cached.Value;

        var value = BuildUncached(projectName);
        _cache[projectName] = (DateTime.UtcNow, value);
        return value;
    }

    internal void InvalidateCache() => _cache.Clear();

    private ProjectDeploymentSummary BuildUncached(string projectName)
    {
        var repoRoot = _git.ResolveRepoRootForProject(projectName);
        var targets = BuildTargets(projectName, repoRoot);
        string? integrationTip = null;
        if (!string.IsNullOrWhiteSpace(repoRoot) && _git.IsGitRepo(repoRoot))
        {
            var configuredBranch = _git.ResolveIntegrationBranch(
                repoRoot, _settings.Get(projectName).IntegrationBranch);
            integrationTip = _git.GetBranchTip(repoRoot, configuredBranch);
        }
        var evidenceRun = _testRuns?.LastGreenForDeployment(projectName, integrationTip);
        return BuildBaseline(projectName, repoRoot, targets, integrationTip, evidenceRun) with
        {
            StableBaseline = targets.Any(target => target.Template == StableTemplate),
            PipelineDeploySteps = PipelineDeployStepCount(repoRoot),
        };
    }

    private const string StableTemplate = "deploy-stable";

    /// <summary>Counts the deploy steps of a schema 2 project.yml; zero for older schemas or an unreadable file.</summary>
    internal static int PipelineDeployStepCount(string? repoRoot)
    {
        if (string.IsNullOrWhiteSpace(repoRoot)) return 0;
        var path = Path.Combine(repoRoot, ProjectPreparationPaths.Definition);
        if (!File.Exists(path)) return 0;
        try
        {
            var parsed = ProjectDefinitionReader.Parse(File.ReadAllText(path)).Parsed;
            return parsed?.SchemaVersion == 2 ? parsed.Deployment?.Deploy.Count ?? 0 : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    private ProjectDeploymentSummary BuildBaseline(string projectName, string? repoRoot,
        IReadOnlyList<ProjectDeploymentTarget> targets, string? integrationTip, DeploymentTestRunReference? evidenceRun)
    {
        // stable-restarts.jsonl is Studio's own self-deploy log; another project never reads it (F19).
        if (!targets.Any(target => target.Template == StableTemplate))
            return Unavailable(projectName, "The deploy-stable baseline is Studio's own deployment history.", targets, evidenceRun);
        var taskRepository = _config["TaskRepository"];
        if (string.IsNullOrWhiteSpace(taskRepository))
            return Unavailable(projectName, "Task repository is not configured.", targets, evidenceRun);

        var path = Path.Combine(taskRepository, "logs", "stable-restarts.jsonl");
        var restartHistory = ReadRestartHistory(path, MaxHistoryRuns);
        var latest = restartHistory.FirstOrDefault();
        if (latest is null)
            return Unavailable(projectName, File.Exists(path)
                ? "No valid deploy-stable restart record is available."
                : "No deploy-stable history is available.", targets, evidenceRun);

        if (string.IsNullOrWhiteSpace(repoRoot) || !_git.IsGitRepo(repoRoot))
            return Unavailable(projectName, "Project repository is unavailable.", targets, evidenceRun);

        // stable-restarts.jsonl is workspace-wide and predates project identity.
        // Reject a row whose deployed revision is not part of this repository so
        // one project's latest stable deploy can never be attributed to another.
        if (!_git.IsAncestor(repoRoot, latest.HeadBefore, latest.HeadAfter))
            return Unavailable(projectName, "Latest deploy-stable revision range does not belong to this project repository.", targets, evidenceRun);

        var integrationBranch = _git.ResolveIntegrationBranch(
            repoRoot, _settings.Get(projectName).IntegrationBranch);
        integrationTip ??= _git.GetBranchTip(repoRoot, integrationBranch);

        var deployed = _git.GetCommitsInRangeAtRoot(repoRoot, latest.HeadBefore, latest.HeadAfter)
            .Select(ToCompact)
            .ToList();

        IReadOnlyList<ProjectDeploymentCommit> pendingItems = [];
        int? pendingCount = null;
        string? reason = null;
        if (integrationTip is null)
        {
            reason = $"Integration branch '{integrationBranch}' is unavailable; pending deployment delta is unknown.";
        }
        else
        {
            var pending = _git.GetCommitsInRangeAtRoot(repoRoot, latest.HeadAfter, integrationTip);
            pendingCount = pending.Count;
            pendingItems = pending.Take(MaxPendingCommits).Select(ToCompact).ToList();
        }

        _logger.LogInformation(
            "project-deployment-summary-read project={Project} status={Status} deployedCommits={DeployedCommits} pendingCommits={PendingCommits}",
            projectName, latest.Status, deployed.Count, pendingCount);

        var latestRun = new ProjectDeploymentRun(
            latest.At,
            latest.Status,
            latest.HeadBefore,
            latest.HeadAfter,
            latest.DurationSeconds,
            latest.JobsSinceLastRestart,
            latest.ReviewCountAfter,
            deployed);
        var history = restartHistory.Select(row => row == latest
            ? latestRun
            : new ProjectDeploymentRun(
                row.At,
                row.Status,
                row.HeadBefore,
                row.HeadAfter,
                row.DurationSeconds,
                row.JobsSinceLastRestart,
                row.ReviewCountAfter,
                [])).ToList();

        return new ProjectDeploymentSummary
        {
            Project = projectName,
            Available = true,
            Reason = reason,
            LastDeployment = latestRun,
            History = history,
            PendingCount = pendingCount,
            PendingCommits = pendingItems,
            Targets = targets,
            DefaultEvidenceRun = evidenceRun,
        };
    }

    private IReadOnlyList<ProjectDeploymentTarget> BuildTargets(string projectName, string? repoRoot)
    {
        var targets = new List<ProjectDeploymentTarget>();
        if (!string.IsNullOrWhiteSpace(repoRoot)
            && File.Exists(Path.Combine(repoRoot, "scripts", "supervisor", "restart-stable-after-batch.sh")))
        {
            targets.Add(new ProjectDeploymentTarget(
                "deploy-stable", "deploy-stable", "derived", StableTemplate,
                "Update the stable seat after confirming it is idle.", true, "repository-fact",
                "bash scripts/supervisor/restart-stable-after-batch.sh", null,
                [new("stableIdle", "boolean", true, JsonSerializer.SerializeToElement(false), [])]));
        }

        if (_publish is not null)
        {
            foreach (var target in _publish.GetProjectPublishStatus(projectName).Targets
                         .Where(target => target.Kind == PublishTargetKind.Package))
            {
                targets.Add(new ProjectDeploymentTarget(
                    $"release:{target.Ecosystem}", $"{target.Label} release", "template", "tag-push-release",
                    "Launch the existing guarded publishing flow; publishing remains authoritative.", false,
                    "publishing-workflows", null, null,
                    [new("version", "string", true,
                        target.CurrentVersion is null ? null : JsonSerializer.SerializeToElement(target.CurrentVersion), [])]));
            }
        }

        if (string.IsNullOrWhiteSpace(repoRoot)) return targets;
        var descriptorRoot = Path.Combine(repoRoot, "docs", "deployments");
        if (!Directory.Exists(descriptorRoot)) return targets;

        foreach (var file in Directory.EnumerateFiles(descriptorRoot, "deployment.json", SearchOption.AllDirectories))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                var root = document.RootElement;
                var id = String(root, "id");
                var title = String(root, "title");
                var kind = String(root, "kind");
                var template = String(root, "template");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title)
                    || kind is not ("template" or "prompt")) continue;

                var parameters = new List<ProjectDeploymentParameter>();
                if (root.TryGetProperty("parameters", out var values) && values.ValueKind == JsonValueKind.Array)
                {
                    foreach (var value in values.EnumerateArray())
                    {
                        var name = String(value, "name");
                        var type = String(value, "type");
                        if (string.IsNullOrWhiteSpace(name) || type is not ("string" or "boolean" or "branch" or "enum" or "secret-ref")) continue;
                        var required = value.TryGetProperty("required", out var requiredValue) && requiredValue.ValueKind == JsonValueKind.True;
                        JsonElement? defaultValue = value.TryGetProperty("default", out var defaultElement) ? defaultElement.Clone() : null;
                        var options = value.TryGetProperty("options", out var optionsElement) && optionsElement.ValueKind == JsonValueKind.Array
                            ? optionsElement.EnumerateArray().Where(option => option.ValueKind == JsonValueKind.String).Select(option => option.GetString()!).ToList()
                            : [];
                        parameters.Add(new(name, type, required, defaultValue, options));
                    }
                }

                var command = String(root, "command");
                targets.Add(new ProjectDeploymentTarget(
                    id, title, kind, template, String(root, "summary") ?? "Repository-owned deployment target.",
                    !string.IsNullOrWhiteSpace(command), Path.GetRelativePath(repoRoot, file).Replace('\\', '/'),
                    command, String(root, "targetHostId"), parameters));
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "project-deployment-target-invalid project={Project} descriptor={Descriptor}", projectName, file);
            }
        }

        return targets;
    }

    internal static RestartRecord? ReadLatestRestart(string path)
        => ReadRestartHistory(path, 1).FirstOrDefault();

    internal static IReadOnlyList<RestartRecord> ReadRestartHistory(string path, int limit)
    {
        if (!File.Exists(path) || limit <= 0) return [];
        var rows = new List<RestartRecord>();
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var json = JsonDocument.Parse(line);
                    var root = json.RootElement;
                    var eventName = String(root, "event");
                    if (eventName is not null
                        && !string.Equals(eventName, "restart", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var timestamp = String(root, "ts");
                    if (!DateTime.TryParse(
                            timestamp,
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.AssumeUniversal
                            | System.Globalization.DateTimeStyles.AdjustToUniversal,
                            out var at))
                        continue;
                    var before = String(root, "headBefore");
                    var after = String(root, "headAfter");
                    if (string.IsNullOrWhiteSpace(before) || string.IsNullOrWhiteSpace(after)) continue;

                    var row = new RestartRecord(
                        at.ToUniversalTime(),
                        String(root, "status") ?? "unknown",
                        before,
                        after,
                        Double(root, "durationSeconds"),
                        Int(root, "jobsSinceLastRestart"),
                        Int(root, "reviewCountAfter"));
                    rows.Add(row);
                }
                catch (JsonException ex)
                {
                    // JSONL is append-only. A torn row must not hide older truth.
                    SilentCatch.Note(ex, "ProjectDeploymentSummaryService: torn restart-history row ignored.");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        return rows
            .OrderByDescending(row => row.At)
            .Take(limit)
            .ToList();
    }

    private static ProjectDeploymentSummary Unavailable(
        string project,
        string reason,
        IReadOnlyList<ProjectDeploymentTarget>? targets = null,
        DeploymentTestRunReference? evidenceRun = null) => new()
    {
        Project = project,
        Available = false,
        Reason = reason,
        Targets = targets ?? [],
        DefaultEvidenceRun = evidenceRun,
    };

    private static ProjectDeploymentCommit ToCompact(GitCommitInfo commit)
        => new(commit.Sha, commit.ShortSha, commit.Subject, commit.AuthorDateUtc);

    private static string? String(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int Int(JsonElement root, string name)
        => root.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out var result) ? result : 0;

    private static double Double(JsonElement root, string name)
        => root.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetDouble(out var result) ? result : 0;

    internal sealed record RestartRecord(
        DateTime At,
        string Status,
        string HeadBefore,
        string HeadAfter,
        double DurationSeconds,
        int JobsSinceLastRestart,
        int ReviewCountAfter);
}
