using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TaskServer.Contracts.GitHubApp;

namespace AgentStudio.Publishing;

/// <summary>
/// PUB-2 action layer. It deliberately drives repositories through their
/// release commands and tag- or workflow-dispatch-triggered GitHub Actions workflows. It
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
    private readonly GitHubPublishClient _github;
    private readonly AgentStudio.Quota.WorkspaceQuotaService? _workspaceQuota;
    private readonly TimeProvider _clock;
    private readonly TimelineLog? _timeline;
    private readonly AgentStudio.Management.StructureLibraryService? _library;
    private readonly object _runGate = new();
    private readonly ConcurrentDictionary<string, object> _releaseGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PublishWorkflowRun> _runs = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _autoDeploying = new(StringComparer.OrdinalIgnoreCase);

    public PublishActionService(
        GitService git,
        PublishTargetService targets,
        TaskPublishableService taskTargets,
        ProjectSettingsService settings,
        TaskScannerService scanner,
        ILogger<PublishActionService> logger,
        GitHubPublishClient github,
        AgentStudio.Quota.WorkspaceQuotaService? workspaceQuota = null,
        TimeProvider? clock = null,
        AgentStudio.Management.StructureLibraryService? library = null,
        TimelineLog? timeline = null)
    {
        _git = git;
        _targets = targets;
        _taskTargets = taskTargets;
        _settings = settings;
        _scanner = scanner;
        _logger = logger;
        _github = github;
        _workspaceQuota = workspaceQuota;
        _clock = clock ?? TimeProvider.System;
        _library = library;
        _timeline = timeline;
    }

    public PublishActionPanel GetPanel(string project, string targetId)
    {
        var target = _targets.GetProjectPublishStatus(project).Targets
            .FirstOrDefault(x => string.Equals(x.Id, targetId, StringComparison.OrdinalIgnoreCase));
        if (target is null) throw new InvalidOperationException($"Unknown publish target '{targetId}'.");

        var jobs = _scanner.ScanAllJobsWithArchive();
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
                ? SuggestNextVersion(target.CurrentVersion, pending.Select(x => x.TaskType)) ?? "0.1.0"
                : null,
            Notice = target.FirstPublishPending && target.ReleaseCommand is null
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
        CancellationToken cancellationToken = default,
        bool dryRun = false)
    {
        _ = RequireRepo(project);
        lock (_releaseGates.GetOrAdd(project, static _ => new object()))
            return PublishPackageCore(project, targetId, version, cancellationToken, dryRun);
    }

    private PublishWorkflowRun PublishPackageCore(string project, string targetId, string version, CancellationToken cancellationToken, bool dryRun)
    {
        var admission = _workspaceQuota?.Admit(project);
        if (admission is { Allowed: false }) throw new InvalidOperationException(admission.Message);
        var panel = GetPanel(project, targetId);
        var target = panel.Target!;
        if (target.Kind != PublishTargetKind.Package) throw new InvalidOperationException("Target is not a package.");
        if (target.FirstPublishPending && target.ReleaseCommand is null) throw new InvalidOperationException(panel.Notice);
        if (!TryParseVersion(version, out _, out _, out _) || target.CurrentVersion is not null && !IsGreater(version, target.CurrentVersion))
            throw new InvalidOperationException($"Version '{version}' must be valid SemVer and greater than {target.CurrentVersion}.");
        if (panel.PendingTasks.Count == 0 && target.ReleaseCommand is null) throw new InvalidOperationException("No pending package tasks to publish.");

        var root = RequireRepo(project);
        if (target.ReleaseCommand is not null)
        {
            var plan = ProjectReleaseExecution.Execute(root, version, dryRun, cancellationToken);
            if (dryRun) return new PublishWorkflowRun
            {
                Project = project, TargetId = target.Id, Workflow = plan, Version = version,
                Status = "preview", TriggeredAt = _clock.GetUtcNow().UtcDateTime,
            };
            foreach (var pending in panel.PendingTasks)
            {
                var job = _scanner.FindJob(pending.TaskKey);
                if (job is not null && BoardMergeStatusService.AnchorFor(job) is { } anchor
                    && _git.IsAncestor(root, anchor, $"v{version}"))
                    _timeline?.Append(job.FolderPath, TimelineEventKinds.PackageReleased, TimelineActors.System, $"Released as v{version}",
                        details: new() { ["tag"] = $"v{version}", ["branch"] = target.ReleaseBranch! });
            }
            _targets.InvalidateCache();
            return StoreRun(new PublishWorkflowRun
            {
                Project = project, TargetId = target.Id, Workflow = target.ReleaseCommand,
                Version = version, Status = "completed", Conclusion = "success",
                TriggeredAt = _clock.GetUtcNow().UtcDateTime,
                PackageName = target.PackageName, Ecosystem = target.Ecosystem,
            });
        }
        if (dryRun) throw new InvalidOperationException("Dry run requires a project release command.");
        var workflow = FindWorkflow(root, package: true)
            ?? throw new InvalidOperationException("No existing version-tag-triggered package workflow was found.");
        var repository = RequireGitHubRepo(project, root);
        RequirePackagePushDestination(root, repository);
        var triggeredAt = _clock.GetUtcNow().UtcDateTime;
        var headSha = PublishPackageTagPath(root, target.Ecosystem!, version, cancellationToken);

        _targets.InvalidateCache();
        var run = StoreRun(new PublishWorkflowRun
        {
            Project = project,
            TargetId = target.Id,
            Workflow = workflow,
            Status = "queued",
            Version = version,
            TriggeredAt = triggeredAt,
            Repository = repository.Canonical,
            HeadSha = headSha,
            PackageName = target.PackageName,
            Ecosystem = target.Ecosystem,
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
        var repo = RequireGitHubRepo(project, root);
        var branch = RunRequired(root, "current branch could not be resolved", "branch", "--show-current").Trim();
        if (string.IsNullOrWhiteSpace(branch)) throw new InvalidOperationException("Website deploy requires a named branch.");

        // Reserve before dispatch so concurrent deploys cannot both consume the last slot.
        var reserved = _workspaceQuota?.ReserveProjectEnvironment(project, "website") ?? false;
        var dispatchStartedAt = _clock.GetUtcNow();
        GitHubPublishDispatch dispatch;
        try
        {
            dispatch = _github.DispatchAsync(repo, workflow, branch).GetAwaiter().GetResult();
        }
        catch
        {
            if (reserved) _workspaceQuota?.SetProjectEnvironment(project, "website", false);
            throw;
        }
        var run = StoreRun(new PublishWorkflowRun
        {
            Project = project,
            TargetId = "website",
            Workflow = workflow,
            Status = "queued",
            RunId = dispatch.RunId,
            Url = dispatch.Url,
            Repository = repo.Canonical,
            GitRef = branch,
            // The dispatch start also identifies the persisted slot ownership of this run.
            TriggeredAt = dispatchStartedAt.UtcDateTime,
        });
        _workspaceQuota?.RecordProjectEnvironmentDispatch(project, "website", workflow, dispatchStartedAt, reserved,
            run.RunId, run.Repository, run.GitRef);
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
        if (stored.Status == "completed" && targetId != "website") return stored;
        try
        {
            // Poll the repository selected at dispatch or push time. Recheck its binding without starting Git on a GET.
            var repo = _github.RequireRepository(project, $"https://{stored.Repository}");
            var runId = stored.RunId ?? FindTriggeredRun(repo, stored);
            if (runId is null)
            {
                if (targetId == "website") ReconcileWebsiteDeployment(repo, project);
                return stored;
            }
            // Save the identity before status or deployment reads that can fail independently.
            stored = stored with { RunId = runId, Repository = repo.Canonical };
            if (targetId == "website")
                _workspaceQuota?.RememberProjectEnvironmentDispatchRun(project, "website", stored.TriggeredAt, runId.Value);
            var status = _github.GetRunAsync(repo, runId.Value).GetAwaiter().GetResult();
            var refreshed = stored with
            {
                Status = status.Status,
                Conclusion = status.Conclusion,
                Url = status.Url ?? stored.Url,
                Error = null,
            };
            stored = refreshed;
            if (targetId == "website")
            {
                SettleWebsiteRun(project, stored.TriggeredAt, refreshed.Status, refreshed.Conclusion);
                if (refreshed.Status == "completed" && refreshed.Conclusion == "success")
                    ReconcileWebsiteDeployment(repo, project);
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
                    var repo = RequireGitHubRepo(project, root);
                    ReconcileWebsiteDispatch(repo, project);
                    ReconcileWebsiteDeployment(repo, project);
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
    private void ReconcileWebsiteDispatch(GitHubRepositoryName repo, string project)
    {
        var dispatch = _workspaceQuota?.PendingDispatches("website").FirstOrDefault(item =>
            string.Equals(item.Project, project, StringComparison.OrdinalIgnoreCase));
        if (dispatch is null) return;
        RequireOriginalRepository(repo, dispatch.Repository);
        var runId = dispatch.RunId ?? FindTriggeredRun(repo, new PublishWorkflowRun
        {
            Project = project, TargetId = "website", Workflow = dispatch.Workflow, TriggeredAt = dispatch.DispatchedAt,
            GitRef = dispatch.GitRef,
        });
        if (runId is null) return;
        _workspaceQuota?.RememberProjectEnvironmentDispatchRun(project, "website", dispatch.DispatchedAt, runId.Value);
        var status = _github.GetRunAsync(repo, runId.Value).GetAwaiter().GetResult();
        SettleWebsiteRun(project, dispatch.DispatchedAt, status.Status, status.Conclusion);
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

    private void ReconcileWebsiteDeployment(GitHubRepositoryName repo, string project)
    {
        if (_workspaceQuota is null || !_workspaceQuota.HasProjectEnvironment(project, "website")) return;
        var reservedAt = _workspaceQuota.ProjectEnvironmentReservedAt(project, "website");
        if (_github.IsWebsiteDeploymentInactiveAsync(repo, reservedAt).GetAwaiter().GetResult())
            _workspaceQuota.SetProjectEnvironment(project, "website", false);
    }

    internal static bool DeploymentCanReleaseReservation(DateTimeOffset? reservedAt, DateTimeOffset? deployedAt)
        => reservedAt is null || deployedAt >= reservedAt;

    private long? FindTriggeredRun(GitHubRepositoryName repo, PublishWorkflowRun stored)
        => _github.FindRunAsync(repo, stored).GetAwaiter().GetResult();

    /// <summary>Fixture-testable mutation path used after all product guards pass.</summary>
    internal static string PublishPackageTagPath(
        string root,
        string ecosystem,
        string version,
        CancellationToken cancellationToken = default,
        string releaseBranch = "main")
    {
        var currentBranch = RunRequired(root, "branch lookup failed", "branch", "--show-current").Trim();
        if (currentBranch != releaseBranch)
            throw new InvalidOperationException($"Switch to release branch {releaseBranch} before using the legacy tag workflow, or declare a project release command.");
        RequireClean(root);
        var manifest = BumpManifest(root, ecosystem, version);
        RunRequired(root, "git add failed", new[] { "add", "--" }.Concat(manifest).ToArray());
        RunRequired(root, "release commit failed", "commit", "-m", $"chore(release): v{version}");
        RunRequired(root, "release tag failed", "tag", $"v{version}");
        var headSha = RunRequired(root, "release commit could not be resolved", "rev-parse", "HEAD").Trim();
        RunRequired(
            root,
            "atomic release push failed",
            cancellationToken,
            "push",
            "--atomic",
            "origin",
            $"HEAD:refs/heads/{releaseBranch}",
            $"refs/tags/v{version}");
        return headSha;
    }

    private static string[] BumpManifest(string root, string ecosystem, string version)
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
            var paths = new List<string> { Path.GetRelativePath(root, path) };
            foreach (var name in new[] { "package-lock.json", "npm-shrinkwrap.json" })
            {
                var lockPath = Path.Combine(dir, name);
                if (!File.Exists(lockPath)) continue;
                var locked = JsonNode.Parse(File.ReadAllText(lockPath))?.AsObject()
                    ?? throw new InvalidOperationException($"{name} is not a JSON object.");
                locked["version"] = version;
                if (locked["packages"] is JsonObject packages && packages[""] is JsonObject package)
                    package["version"] = version;
                File.WriteAllText(lockPath, locked.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
                paths.Add(Path.GetRelativePath(root, lockPath));
            }
            return paths.ToArray();
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
        return [Path.GetRelativePath(root, pathCsproj)];
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
        var status = RunRequired(root, "worktree status could not be resolved", "status", "--porcelain");
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

    private GitHubRepositoryName RequireGitHubRepo(string project, string root)
    {
        var remote = RunRequired(root, "origin URL could not be resolved", "remote", "get-url", "origin").Trim();
        return _github.RequireRepository(project, remote);
    }

    private static void RequireOriginalRepository(GitHubRepositoryName repository, string? original)
    {
        if (original is not null && original != repository.Canonical)
            throw new InvalidOperationException("The project's GitHub repository changed after this workflow was triggered.");
    }

    private static void RequirePackagePushDestination(string root, GitHubRepositoryName repository)
    {
        var push = RunRequired(root, "origin push URL could not be resolved", "remote", "get-url", "--push", "--all", "origin")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (push.Length != 1 || !Uri.TryCreate(push[0], UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || uri.UserInfo.Length != 0 || GitHubRepositoryName.TryParse(push[0])?.Canonical != repository.Canonical)
            throw new InvalidOperationException("Tag-triggered publishing requires a single HTTPS push URL for the authorized repository.");
    }

    private PublishWorkflowRun StoreRun(PublishWorkflowRun run)
    {
        lock (_runGate) _runs[$"{run.Project}\n{run.TargetId}"] = run;
        if (run.Status == "completed" && run.Conclusion == "success" && run.Version is { } version
            && run.PackageName is { } package && run.Ecosystem is { } provider)
        {
            try
            {
                _library?.RecordPublish(run.Project, package, provider, version, _clock.GetUtcNow().UtcDateTime);
            }
            catch (Exception ex)
            { _logger.LogWarning(ex, "Registry publish receipt unavailable for {Project} {Target}", run.Project, run.TargetId); }
        }
        return run;
    }

    private PublishWorkflowRun? GetStoredRun(string project, string targetId)
    {
        lock (_runGate) return _runs.GetValueOrDefault($"{project}\n{targetId}");
    }

    /// <summary>Repository shells run through the bounded process runner.</summary>
    internal static void RunReleaseCommand(string root, string command, ProjectExecutionDefinition definition, string version, CancellationToken token)
    {
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "bash")
        { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(OperatingSystem.IsWindows() ? "/c" : "-c");
        start.ArgumentList.Add(command);
        foreach (var pair in definition.Environment) start.Environment[pair.Key] = pair.Value;
        start.Environment["RELEASE_VERSION"] = version;
        var result = GitNetworkProcessRunner.Run(start, timeout: TimeSpan.FromMinutes(30), cancellationToken: token);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Release command failed: {command}\n{result.StandardOutput}\n{result.StandardError}");
    }

    private static string RunRequired(string root, string error, params string[] args)
        => RunRequired(root, error, CancellationToken.None, args);

    private static string RunRequired(
        string root,
        string error,
        CancellationToken cancellationToken,
        params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        var result = GitProcessBoundary.Run(psi, timeout: GitNetworkProcessRunner.DefaultTimeout,
            cancellationToken: cancellationToken);
        if (result.ExitCode != 0) throw new InvalidOperationException($"{error}: {result.StandardError.Trim()}");
        return result.StandardOutput;
    }
}
