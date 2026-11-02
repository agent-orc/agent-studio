using AgentStudio.Pipeline;
using AgentStudio.Projects;
using AgentStudio.Registry;
using AgentStudio.Security;
using AgentStudio.Tasks;
using AgentStudio.TaskServer.Contracts.GitHubApp;

namespace AgentStudio.Readiness;

/// <summary>
/// Gathers the facts of the readiness report from the stores that already own
/// them. Git facts come through <see cref="ReadinessGitFacts"/> and its
/// one-minute cache; everything else is a file-system or in-memory read.
/// </summary>
public sealed class ServerReadinessCollector(
    IConfiguration configuration,
    TimeProvider clock,
    ReadinessGitFacts git,
    PullGateStore pullGates,
    GateSlotUsageTracker gateSlots,
    AccessSecurityStore security,
    GitHubAppBroker app,
    TaskScannerService scanner,
    ProjectRegistry projects,
    ProjectSettingsService settings,
    ILogger<ServerReadinessCollector> logger)
{
    public async Task<ServerReadinessFacts> CollectAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var (identity, commit) = git.ServiceUser();
        var (mounts, mountError) = ReadMounts();
        var inContainer = mounts is not null && ReadinessFileProbes.RunsInContainer(mounts, File.Exists("/.dockerenv"));

        var workspace = configuration["TaskRepository"];
        var home = Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } configuredHome
            ? configuredHome : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var folders = new List<(string Name, string Path)>();
        if (!string.IsNullOrWhiteSpace(workspace)) folders.Add(("workspace", workspace));
        if (!string.IsNullOrWhiteSpace(home)) folders.Add(("home", home));

        return new ServerReadinessFacts(
            now,
            identity,
            commit,
            inContainer,
            folders.Select(folder => ReadinessFileProbes.DataFolder(folder.Name, folder.Path, folder.Name, mounts, mountError)).ToArray(),
            folders.Select(folder => ReadinessFileProbes.StateFolder(folder.Name, folder.Path)).ToArray(),
            string.IsNullOrWhiteSpace(workspace) ? [] : ReadinessFileProbes.GitLocks(workspace, now),
            Gate(),
            Hosts(),
            await App(ct),
            Projects(now, mounts, mountError));
    }

    private static (IReadOnlyList<MountEntry>? Mounts, string? Error) ReadMounts()
    {
        if (!OperatingSystem.IsLinux()) return (null, null);
        try
        {
            return (ReadinessFileProbes.ParseMountInfo(File.ReadAllText(ReadinessFileProbes.MountInfoPath)), null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return (null, error.Message);
        }
    }

    private GateWorkerFact Gate()
    {
        var healthy = pullGates.HealthyHosts().Select(host => host.Id).ToHashSet(StringComparer.Ordinal);
        var workers = pullGates.Hosts()
            .Select(host => new GateWorkerHostFact(host.Name, host.Slots, host.LastHeartbeat, healthy.Contains(host.Id)))
            .ToArray();
        var sshHost = configuration["RemoteGate:SshHost"]?.Trim();
        return new GateWorkerFact(workers, string.IsNullOrWhiteSpace(sshHost) ? null : sshHost,
            gateSlots.Reachability, gateSlots.LastReachedAt, gateSlots.LastFailureAt);
    }

    private ExecutionHostsFact Hosts()
    {
        var now = clock.GetUtcNow().UtcDateTime;
        // Orchestrator engine identities enrol the same way but run no cards.
        var hosts = security.ListRunners()
            .Where(runner => runner.RevokedAt is null && runner.RetiredAt is null
                && !runner.Id.StartsWith("engine_", StringComparison.Ordinal))
            .Select(runner => new ExecutionHostFact(runner.Id, runner.Name, runner.CreatedAt, runner.LastSeenAt))
            .ToArray();
        var pending = security.ListEnrollments().Count(enrollment => enrollment.UsedAt is null && enrollment.ExpiresAt > now);
        return new ExecutionHostsFact(hosts, pending);
    }

    private async Task<GitHubAppFact> App(CancellationToken ct)
    {
        try
        {
            var status = await app.StatusAsync(refresh: false, ct);
            var coverage = new Dictionary<string, GitHubAppCoverageFact>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in status.Repositories)
                coverage[item.Project] = new GitHubAppCoverageFact(item.State, item.RepositoryUrl, item.InstallationId, item.Detail);
            return new GitHubAppFact(status.Connected, status.InstallUrl, status.Problem, coverage);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogWarning(error, "server-readiness-app-status-failed");
            return new GitHubAppFact(true, null, error.Message, new Dictionary<string, GitHubAppCoverageFact>());
        }
    }

    private IReadOnlyList<ProjectReadinessFact> Projects(DateTimeOffset now,
        IReadOnlyList<MountEntry>? mounts, string? mountError)
    {
        var facts = new List<ProjectReadinessFact>();
        foreach (var entry in scanner.GetWatchPaths())
        {
            var record = projects.FindByIdOrDisplayName(entry.Name);
            if (record?.Archived == true) continue;
            var repository = !string.IsNullOrWhiteSpace(entry.RepositoryPath) ? entry.RepositoryPath
                : !string.IsNullOrWhiteSpace(entry.RootPath) ? entry.RootPath : null;
            // A task-only project has no repository to check.
            if (repository is null) continue;
            var projectSettings = settings.Get(entry.Name);
            var leaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(repository)));
            facts.Add(new ProjectReadinessFact(
                entry.Name,
                Path.GetFullPath(repository),
                projectSettings.RepositoryVisibility,
                projectSettings.IntegrationBranch,
                git.Refs(repository),
                ReadinessFileProbes.DataFolder(entry.Name, repository, $"projects/{leaf}", mounts, mountError),
                ReadinessFileProbes.GitLocks(repository, now)));
        }
        return facts;
    }
}
