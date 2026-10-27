using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AgentStudio.Publishing;

/// <summary>
/// PUB-2 action layer. It deliberately drives repositories through their
/// existing tag- or workflow-dispatch-triggered GitHub Actions workflows. It
/// never handles registry credentials and never publishes a package itself.
/// </summary>
public interface IPublishEnvironmentReconciler
{
    void ReconcileRunningWebsites();
}

public sealed class PublishActionService : IPublishEnvironmentReconciler
{
    private static readonly Regex SemVerRx = new(@"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$", RegexOptions.Compiled);
    private readonly GitService _git;
    private readonly PublishTargetService _targets;
    private readonly TaskPublishableService _taskTargets;
    private readonly ProjectSettingsService _settings;
    private readonly TaskScannerService _scanner;
    private readonly ILogger<PublishActionService> _logger;
    private readonly AgentStudio.Quota.WorkspaceQuotaService? _workspaceQuota;
    private readonly TimeProvider _clock;
    private readonly object _runGate = new();
    private readonly Dictionary<string, PublishWorkflowRun> _runs = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _autoDeploying = new(StringComparer.OrdinalIgnoreCase);

    public PublishActionService(
        GitService git,
        PublishTargetService targets,
        TaskPublishableService taskTargets,
        ProjectSettingsService settings,
        TaskScannerService scanner,
        ILogger<PublishActionService> logger,
        AgentStudio.Quota.WorkspaceQuotaService? workspaceQuota = null,
        TimeProvider? clock = null)
    {
        _git = git;
        _targets = targets;
        _taskTargets = taskTargets;
        _settings = settings;
        _scanner = scanner;
        _logger = logger;
        _workspaceQuota = workspaceQuota;
        _clock = clock ?? TimeProvider.System;
    }

    public PublishActionPanel GetPanel(string project, string targetId)
    {
        var target = _targets.GetProjectPublishStatus(project).Targets
            .FirstOrDefault(x => string.Equals(x.Id, targetId, StringComparison.OrdinalIgnoreCase));
        if (target is null) throw new InvalidOperationException($"Unknown publish target '{targetId}'.");

        var jobs = _scanner.ScanAllJobsRaw();
        var signals = _taskTargets.BuildLookup(jobs);
        var pending = jobs
            .Where(x => string.Equals(x.ProjectName, project, StringComparison.OrdinalIgnoreCase))
            .Where(x => signals.TryGetValue(x.TaskKey, out var signal)
                        && signal.TargetIds.Contains(target.Id, StringComparer.OrdinalIgnoreCase))
            .Select(x => new PublishPendingTask(x.Id, x.TaskKey, x.Title, x.TaskType))
            .OrderBy(x => x.TaskKey, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var configured = _settings.Get(project).PublishAutomation?.GetValueOrDefault(target.Id);
        var mode = PublishAutomationModes.Normalize(target.Id, configured);
        return new PublishActionPanel
        {
            Project = project,
            Target = target,
            AutomationMode = mode,
            PendingTasks = pending,
            SuggestedVersion = target.Kind == PublishTargetKind.Package
                ? SuggestNextVersion(target.CurrentVersion, pending.Select(x => x.TaskType))
                : null,
            Notice = target.FirstPublishPending
                ? "First publish must be completed manually by the operator. Trusted Publishing remains keyless; no registry secret is stored here."
                : null,
            LastRun = GetStoredRun(project, target.Id),
        };
    }

    public static string? SuggestNextVersion(string? currentVersion, IEnumerable<string> taskTypes)
    {
        if (!TryParseVersion(currentVersion, out var major, out var minor, out var patch)) return null;
        var hasFeature = taskTypes.Any(x => string.Equals(TaskTypes.Normalize(x), TaskTypes.Feature, StringComparison.Ordinal));
        return hasFeature ? $"{major}.{minor + 1}.0" : $"{major}.{minor}.{patch + 1}";
    }

    public static string ResolveLadderAction(string targetId, string mode, bool hasPending)
    {
        if (!hasPending) return "none";
        return PublishAutomationModes.Normalize(targetId, mode) switch
        {
            PublishAutomationModes.Auto when string.Equals(targetId, "website", StringComparison.OrdinalIgnoreCase) => "auto",
            PublishAutomationModes.Suggest => "suggest",
            _ => "manual",
        };
    }

    /// <summary>
    /// Completion hook for the website-only auto rung. The accepted task may be
    /// merged into the integration branch asynchronously, so retry the derived
    /// pending fold for a short bounded window and dispatch only when that exact
    /// task appears in the website delta.
    /// </summary>
    public void HandleTaskAccepted(string project, string taskId)
    {
        var configured = _settings.Get(project).PublishAutomation?.GetValueOrDefault("website");
        if (PublishAutomationModes.Normalize("website", configured) != PublishAutomationModes.Auto) return;
        lock (_runGate)
        {
            if (!_autoDeploying.Add(project)) return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                for (var attempt = 1; attempt <= 5; attempt++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2));
                    _targets.InvalidateCache();
                    var panel = GetPanel(project, "website");
                    if (panel.PendingTasks.Any(x => string.Equals(x.TaskId, taskId, StringComparison.OrdinalIgnoreCase)))
                    {
                        DeployWebsite(project);
                        _logger.LogInformation(
                            "website-auto-deploy-triggered project={Project} task={Task} attempt={Attempt}",
                            project, taskId, attempt);
                        return;
                    }
                }
                _logger.LogWarning(
                    "website-auto-deploy-not-ready project={Project} task={Task} attempts=5",
                    project, taskId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "website-auto-deploy-failed project={Project} task={Task}", project, taskId);
            }
            finally
            {
                lock (_runGate) _autoDeploying.Remove(project);
            }
        });
    }

    public PublishWorkflowRun PublishPackage(
        string project,
        string targetId,
        string version,
        CancellationToken cancellationToken = default)
    {
        var admission = _workspaceQuota?.Admit(project);
        if (admission is { Allowed: false }) throw new InvalidOperationException(admission.Message);
        var panel = GetPanel(project, targetId);
        var target = panel.Target!;
        if (target.Kind != PublishTargetKind.Package) throw new InvalidOperationException("Target is not a package.");
        if (target.FirstPublishPending) throw new InvalidOperationException(panel.Notice);
        if (!TryParseVersion(version, out _, out _, out _) || !IsGreater(version, target.CurrentVersion!))
            throw new InvalidOperationException($"Version '{version}' must be valid SemVer and greater than {target.CurrentVersion}.");
        if (panel.PendingTasks.Count == 0) throw new InvalidOperationException("No pending package tasks to publish.");

        var root = RequireRepo(project);
        var workflow = FindWorkflow(root, package: true)
            ?? throw new InvalidOperationException("No existing version-tag-triggered package workflow was found.");
        PublishPackageTagPath(root, target.Ecosystem!, version, cancellationToken);

        _targets.InvalidateCache();
        var run = StoreRun(new PublishWorkflowRun
        {
            Project = project,
            TargetId = target.Id,
            Workflow = workflow,
            Status = "queued",
            Version = version,
            TriggeredAt = DateTime.UtcNow,
        });
        _logger.LogInformation(
            "package-publish-triggered project={Project} target={Target} version={Version} workflow={Workflow}",
            project, target.Id, version, run.Workflow);
        return run;
    }

    public PublishWorkflowRun DeployWebsite(string project)
    {
        lock (_runGate) return DeployWebsiteCore(project);
    }

    private PublishWorkflowRun DeployWebsiteCore(string project)
    {
        var panel = GetPanel(project, "website");
        if (panel.Target?.Kind != PublishTargetKind.Website) throw new InvalidOperationException("Website target not found.");
        if (panel.Target.PendingCount is not > 0) throw new InvalidOperationException("No pending website changes to deploy.");

        var root = RequireRepo(project);
        var workflow = FindWorkflow(root, package: false)
            ?? throw new InvalidOperationException("No existing website deploy workflow was found.");
        var repo = RequireGitHubRepo(root);
        var branch = RunRequired(root, "current branch could not be resolved", "branch", "--show-current").Trim();
        if (string.IsNullOrWhiteSpace(branch)) throw new InvalidOperationException("Website deploy requires a named branch.");

        // Reserve before dispatch so concurrent deploys cannot both consume the last slot.
        var reserved = _workspaceQuota?.ReserveProjectEnvironment(project, "website") ?? false;
        var dispatchStartedAt = _clock.GetUtcNow();
        try
        {
            RunGhRequired(root, "website workflow dispatch failed", "api", "--method", "POST",
                $"repos/{repo}/actions/workflows/{Uri.EscapeDataString(workflow)}/dispatches", "-f", $"ref={branch}");
        }
        catch
        {
            if (reserved) _workspaceQuota?.SetProjectEnvironment(project, "website", false);
            throw;
        }
        _workspaceQuota?.RecordProjectEnvironmentDispatch(project, "website", workflow, dispatchStartedAt, reserved);
        var run = StoreRun(new PublishWorkflowRun
        {
            Project = project,
            TargetId = "website",
            Workflow = workflow,
            Status = "queued",
            // The dispatch start also identifies the persisted slot ownership of this run.
            TriggeredAt = dispatchStartedAt.UtcDateTime,
        });
        _logger.LogInformation(
            "website-deploy-triggered project={Project} workflow={Workflow} branch={Branch}",
            project, workflow, branch);
        return run;
    }

    public PublishWorkflowRun? RefreshRun(string project, string targetId)
    {
        lock (_runGate) return RefreshRunCore(project, targetId);
    }

    private PublishWorkflowRun? RefreshRunCore(string project, string targetId)
    {
        var stored = GetStoredRun(project, targetId);
        if (stored is null) return null;
        var root = RequireRepo(project);
        try
        {
            var repo = RequireGitHubRepo(root);
            var runId = stored.RunId ?? FindTriggeredRun(root, repo, stored);
            if (runId is null)
            {
                if (targetId == "website") ReconcileWebsiteDeployment(root, repo, project);
                return stored;
            }
            var json = RunGhRequired(root, "workflow status lookup failed", "api", $"repos/{repo}/actions/runs/{runId}");
            using var doc = JsonDocument.Parse(json);
            var e = doc.RootElement;
            var refreshed = stored with
            {
                RunId = runId,
                Status = StringValue(e, "status") ?? stored.Status,
                Conclusion = StringValue(e, "conclusion"),
                Url = StringValue(e, "html_url"),
                Error = null,
            };
            if (targetId == "website")
            {
                SettleWebsiteRun(project, stored.TriggeredAt, refreshed.Status, refreshed.Conclusion);
                if (refreshed.Status == "completed" && refreshed.Conclusion == "success")
                    ReconcileWebsiteDeployment(root, repo, project);
            }
            return StoreRun(refreshed);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "publish-workflow-status-failed project={Project} target={Target}", project, targetId);
            return StoreRun(stored with { Error = ex.Message });
        }
    }

    public void ReconcileRunningWebsites()
    {
        // A slot only matters against a limit; without one the reconciler asks GitHub nothing.
        if (_workspaceQuota is null || !_workspaceQuota.HasLimits()) return;
        foreach (var project in _workspaceQuota.RunningWebsiteProjects())
        {
            lock (_runGate)
            {
                try
                {
                    var root = RequireRepo(project);
                    var repo = RequireGitHubRepo(root);
                    ReconcileWebsiteDispatch(root, repo, project);
                    ReconcileWebsiteDeployment(root, repo, project);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "website-environment-reconciliation-failed project={Project}", project);
                }
            }
        }
    }

    /// <summary>
    /// Settles a dispatch whose run panel is gone, for example after a restart.
    /// A failed workflow that created no GitHub Pages deployment still releases
    /// the slot its dispatch reserved.
    /// </summary>
    private void ReconcileWebsiteDispatch(string root, string repo, string project)
    {
        var dispatch = _workspaceQuota?.PendingDispatches("website").FirstOrDefault(item =>
            string.Equals(item.Project, project, StringComparison.OrdinalIgnoreCase));
        if (dispatch is null) return;
        var runId = FindTriggeredRun(root, repo, new PublishWorkflowRun
        {
            Project = project, TargetId = "website", Workflow = dispatch.Workflow, TriggeredAt = dispatch.DispatchedAt,
        });
        if (runId is null) return;
        var json = RunGhRequired(root, "workflow status lookup failed", "api", $"repos/{repo}/actions/runs/{runId}");
        using var doc = JsonDocument.Parse(json);
        SettleWebsiteRun(project, dispatch.DispatchedAt, StringValue(doc.RootElement, "status"),
            StringValue(doc.RootElement, "conclusion"));
    }

    /// <summary>
    /// A completed run settles the slot ownership of its own dispatch only: a
    /// failure releases the slot that dispatch reserved, a success leaves it to
    /// the live deployment. A redeploy that took no slot owns nothing.
    /// </summary>
    internal bool SettleWebsiteRun(string project, DateTime dispatchedAt, string? status, string? conclusion)
        => status == "completed" && _workspaceQuota is not null
            && _workspaceQuota.SettleProjectEnvironmentDispatch(project, "website", dispatchedAt,
                succeeded: conclusion == "success");

    private void ReconcileWebsiteDeployment(string root, string repo, string project)
    {
        if (_workspaceQuota is null || !_workspaceQuota.HasProjectEnvironment(project, "website")) return;
        // GitHub Pages marks its deployment inactive when the environment is stopped.
        // An unreadable API response leaves the reservation in place rather than freeing a live slot.
        var deployments = Run(root, "gh", CancellationToken.None, "api",
            $"repos/{repo}/deployments?per_page=20");
        if (deployments.Code != 0) return;
        using var document = JsonDocument.Parse(deployments.Output);
        foreach (var deployment in document.RootElement.EnumerateArray())
        {
            if (StringValue(deployment, "environment") != "github-pages") continue;
            var reservedAt = _workspaceQuota.ProjectEnvironmentReservedAt(project, "website");
            var createdAt = DateTimeOffset.TryParse(StringValue(deployment, "created_at"), out var deployedAt)
                ? deployedAt : (DateTimeOffset?)null;
            if (!DeploymentCanReleaseReservation(reservedAt, createdAt)) return;
            if (!deployment.TryGetProperty("id", out var id) || !id.TryGetInt64(out var value)) return;
            var statuses = Run(root, "gh", CancellationToken.None, "api",
                $"repos/{repo}/deployments/{value}/statuses?per_page=1");
            if (statuses.Code != 0) return;
            using var statusDocument = JsonDocument.Parse(statuses.Output);
            var latest = statusDocument.RootElement.EnumerateArray().FirstOrDefault();
            if (latest.ValueKind == JsonValueKind.Object && StringValue(latest, "state") == "inactive")
                _workspaceQuota.SetProjectEnvironment(project, "website", false);
            return;
        }
    }

    internal static bool DeploymentCanReleaseReservation(DateTimeOffset? reservedAt, DateTimeOffset? deployedAt)
        => reservedAt is null || deployedAt >= reservedAt;

    private long? FindTriggeredRun(string root, string repo, PublishWorkflowRun stored)
    {
        if (stored.Workflow == "tag-triggered release workflow") return null;
        var eventName = stored.TargetId == "website" ? "workflow_dispatch" : "push";
        var args = new List<string> { "api", "--method", "GET",
            $"repos/{repo}/actions/workflows/{Uri.EscapeDataString(stored.Workflow)}/runs",
            "-f", $"event={eventName}", "-f", "per_page=10" };
        if (stored.Version is not null) { args.Add("-f"); args.Add($"branch=v{stored.Version}"); }
        var json = RunGhRequired(root, "workflow run lookup failed", args.ToArray());
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("workflow_runs", out var runs)) return null;
        foreach (var run in runs.EnumerateArray())
        {
            if (run.TryGetProperty("created_at", out var created)
                && DateTime.TryParse(created.GetString(), out var at)
                && at.ToUniversalTime() < stored.TriggeredAt.AddMinutes(-1)) continue;
            if (run.TryGetProperty("id", out var id) && id.TryGetInt64(out var value)) return value;
        }
        return null;
    }

    /// <summary>Fixture-testable mutation path used after all product guards pass.</summary>
    internal static void PublishPackageTagPath(
        string root,
        string ecosystem,
        string version,
        CancellationToken cancellationToken = default)
    {
        RequireClean(root);
        var manifest = BumpManifest(root, ecosystem, version);
        RunRequired(root, "git add failed", "add", "--", manifest);
        RunRequired(root, "release commit failed", "commit", "-m", $"Release v{version}");
        RunRequired(root, "release tag failed", "tag", $"v{version}");
        RunRequired(
            root,
            "atomic release push failed",
            cancellationToken,
            "push",
            "--atomic",
            "origin",
            "HEAD",
            $"refs/tags/v{version}");
    }

    private static string BumpManifest(string root, string ecosystem, string version)
    {
        var websiteRoots = new[] { PublishTargetService.DefaultWebsiteRoot };
        var info = ecosystem == PublishEcosystems.Npm
            ? PublishManifestLocator.LocateNpm(root, websiteRoots)
            : PublishManifestLocator.LocateNuGet(root, websiteRoots);
        if (info is null) throw new InvalidOperationException($"No {ecosystem} version source found.");
        var dir = Path.Combine(root, info.SourceRootRelDir.Replace('/', Path.DirectorySeparatorChar));
        if (ecosystem == PublishEcosystems.Npm)
        {
            var path = Path.Combine(dir, "package.json");
            var node = JsonNode.Parse(File.ReadAllText(path))?.AsObject()
                ?? throw new InvalidOperationException("package.json is not a JSON object.");
            node["version"] = version;
            File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
            return Path.GetRelativePath(root, path);
        }

        var pathCsproj = Directory.EnumerateFiles(dir, "*.csproj")
            .FirstOrDefault(x => string.Equals(Path.GetFileNameWithoutExtension(x), info.PackageName, StringComparison.OrdinalIgnoreCase))
            ?? Directory.EnumerateFiles(dir, "*.csproj").FirstOrDefault()
            ?? throw new InvalidOperationException("NuGet project file was not found.");
        var xml = XDocument.Load(pathCsproj, LoadOptions.PreserveWhitespace);
        var versionNode = xml.Descendants().FirstOrDefault(x => x.Name.LocalName is "Version" or "PackageVersion");
        if (versionNode is null)
        {
            var group = xml.Descendants().FirstOrDefault(x => x.Name.LocalName == "PropertyGroup")
                ?? throw new InvalidOperationException("The project file has no PropertyGroup for Version.");
            group.Add(new XElement(group.Name.Namespace + "Version", version));
        }
        else versionNode.Value = version;
        xml.Save(pathCsproj, SaveOptions.DisableFormatting);
        return Path.GetRelativePath(root, pathCsproj);
    }

    private static bool TryParseVersion(string? value, out int major, out int minor, out int patch)
    {
        major = minor = patch = 0;
        var match = SemVerRx.Match(value ?? "");
        return match.Success
            && int.TryParse(match.Groups[1].Value, out major)
            && int.TryParse(match.Groups[2].Value, out minor)
            && int.TryParse(match.Groups[3].Value, out patch);
    }

    private static bool IsGreater(string candidate, string current)
    {
        TryParseVersion(candidate, out var a, out var b, out var c);
        TryParseVersion(current, out var x, out var y, out var z);
        return (a, b, c).CompareTo((x, y, z)) > 0;
    }

    private string RequireRepo(string project) => _git.ResolveProjectRepoRoot(project)
        ?? throw new InvalidOperationException($"Project '{project}' has no configured repository.");

    private static void RequireClean(string root)
    {
        var status = Run(root, "git", CancellationToken.None, "status", "--porcelain").Output;
        if (!string.IsNullOrWhiteSpace(status))
            throw new InvalidOperationException("Package publish requires a clean working tree.");
    }

    private static string? FindWorkflow(string root, bool package)
    {
        var dir = Path.Combine(root, ".github", "workflows");
        if (!Directory.Exists(dir)) return null;
        foreach (var path in Directory.EnumerateFiles(dir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            if (Path.GetExtension(path) is not (".yml" or ".yaml")) continue;
            var content = File.ReadAllText(path);
            var facts = PublishWorkflowParser.Parse(Path.GetFileName(path), content);
            if (package ? PublishWorkflowParser.HasTagPushTrigger(content) : facts.DeploysWebsite)
                return Path.GetFileName(path);
        }
        return null;
    }

    private static string RequireGitHubRepo(string root)
    {
        var remote = RunRequired(root, "origin URL could not be resolved", "remote", "get-url", "origin").Trim();
        var match = Regex.Match(remote, @"github\.com[/:](?<repo>[^/\s]+/[^/\s]+?)(?:\.git)?$");
        return match.Success ? match.Groups["repo"].Value : throw new InvalidOperationException("origin is not a GitHub repository.");
    }

    private PublishWorkflowRun StoreRun(PublishWorkflowRun run)
    {
        lock (_runGate) _runs[$"{run.Project}\n{run.TargetId}"] = run;
        return run;
    }

    private PublishWorkflowRun? GetStoredRun(string project, string targetId)
    {
        lock (_runGate) return _runs.GetValueOrDefault($"{project}\n{targetId}");
    }

    private static string? StringValue(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string RunRequired(string root, string error, params string[] args)
    {
        var result = Run(root, "git", CancellationToken.None, args);
        if (result.Code != 0) throw new InvalidOperationException($"{error}: {result.Error.Trim()}");
        return result.Output;
    }

    private static string RunRequired(
        string root,
        string error,
        CancellationToken cancellationToken,
        params string[] args)
    {
        var result = Run(root, "git", cancellationToken, args);
        if (result.Code != 0) throw new InvalidOperationException($"{error}: {result.Error.Trim()}");
        return result.Output;
    }

    private static string RunGhRequired(string root, string error, params string[] args)
    {
        var result = Run(root, "gh", CancellationToken.None, args);
        if (result.Code != 0) throw new InvalidOperationException($"{error}: {result.Error.Trim()}");
        return result.Output;
    }

    private static (string Output, string Error, int Code) Run(
        string root,
        string file,
        CancellationToken cancellationToken,
        params string[] args)
    {
        var psi = new ProcessStartInfo(file) { WorkingDirectory = root, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        // Git goes through the boundary; gh is not Git and only borrows the
        // bounded process runner.
        var result = file == "git"
            ? GitProcessBoundary.Run(psi, timeout: GitNetworkProcessRunner.DefaultTimeout,
                cancellationToken: cancellationToken)
            : GitNetworkProcessRunner.Run(psi, timeout: GitNetworkProcessRunner.DefaultTimeout,
                cancellationToken: cancellationToken);
        return (result.StandardOutput, result.StandardError, result.ExitCode);
    }
}
