using System.Collections.Concurrent;
using System.Diagnostics;
using AgentStudio.Diagnostics;
using AgentStudio.Git;
using AgentStudio.Security;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Registry;

/// <summary>
/// Server checkout of one project as the UI reads it. <c>State</c> is
/// <c>present</c>, <c>missing</c> (a repository URL without a checkout),
/// <c>unavailable</c> (neither), <c>running</c>, <c>ready</c> or <c>failed</c>.
/// </summary>
public sealed record ProjectServerCheckoutStatus(
    string ProjectId,
    string State,
    string? Step,
    string? RepositoryUrl,
    string? RepositoryPath,
    string? Branch,
    string? Error);

/// <summary>
/// AGT-3809: creates the server checkout of a project registered by repository
/// URL on the local profile. Before this, only the first-run checklist cloned a
/// URL project, and only its first non-archived one, so every further URL
/// project escalated its first card with <c>server-checkout-missing</c> and
/// needed a shell on the Studio host. The clone uses the project's verified
/// first-run credential when one exists, else the host's own Git access.
/// </summary>
public sealed class ProjectServerCheckoutService
{
    private readonly ProjectRegistry _projects;
    private readonly AgentStudio.Projects.ProjectSettingsService _settings;
    private readonly LocalFirstRunGitSetup _credentials;
    private readonly AgentStudio.Tasks.TaskScannerService? _scanner;
    private readonly AgentStudio.Runner.TaskRunnerService? _runners;
    private readonly IReadOnlyDictionary<string, string?> _environment;
    private readonly ConcurrentDictionary<string, ProjectServerCheckoutStatus> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public ProjectServerCheckoutService(ProjectRegistry projects,
        AgentStudio.Projects.ProjectSettingsService settings, LocalFirstRunGitSetup credentials,
        AgentStudio.Tasks.TaskScannerService scanner, AgentStudio.Runner.TaskRunnerService runners)
        : this(projects, settings, credentials, scanner, runners, null) { }

    internal ProjectServerCheckoutService(ProjectRegistry projects,
        AgentStudio.Projects.ProjectSettingsService settings, LocalFirstRunGitSetup credentials,
        AgentStudio.Tasks.TaskScannerService? scanner, AgentStudio.Runner.TaskRunnerService? runners,
        IReadOnlyDictionary<string, string?>? environment)
    {
        _projects = projects;
        _settings = settings;
        _credentials = credentials;
        _scanner = scanner;
        _runners = runners;
        _environment = environment ?? new Dictionary<string, string?>();
    }

    /// <summary>The running or last failed job, else the registry's checkout fact.</summary>
    public ProjectServerCheckoutStatus? Get(string projectId)
    {
        var project = _projects.FindById(projectId);
        if (project is null) return null;
        var observed = Observe(project);
        if (!_jobs.TryGetValue(project.Id, out var job)) return observed;
        // A finished job keeps its branch and path while that checkout stays registered.
        return job.State is "running" or "failed"
            || job.State == "ready" && observed.State == "present" && observed.RepositoryPath == job.RepositoryPath
            ? job : observed;
    }

    /// <summary>
    /// Starts the clone in the background and returns at once; callers poll
    /// <see cref="Get"/>. A second start while one runs returns the running job.
    /// </summary>
    public ProjectServerCheckoutStatus Start(string projectId)
    {
        var project = _projects.FindById(projectId)
            ?? throw new KeyNotFoundException("Project is unavailable.");
        lock (_gate)
        {
            if (_jobs.TryGetValue(project.Id, out var running) && running.State == "running")
                return running;
            var observed = Observe(project);
            if (observed.State == "present")
                throw new InvalidOperationException("This project already has a server checkout.");
            if (observed.State == "unavailable")
                throw new InvalidOperationException("Add a repository URL before creating a server checkout.");
            var started = observed with { State = "running", Step = "clone", Error = null };
            _jobs[project.Id] = started;
            _ = Task.Run(() => RunAsync(project.Id, CancellationToken.None));
            return started;
        }
    }

    /// <summary>Runs one checkout to its end; the test seam awaits it directly.</summary>
    internal async Task<ProjectServerCheckoutStatus> RunAsync(string projectId, CancellationToken ct)
    {
        var project = _projects.FindById(projectId)!;
        var origin = RepositoryUrl(project)!;
        var checkout = ManagedPath(project);
        var cloned = false;
        try
        {
            var (keyPath, credentialConfig) = _credentials.ResolveCredential(project.Id, origin);
            var transport = keyPath is null ? origin : "git@github.com:" + new Uri(origin).AbsolutePath.TrimStart('/');
            var environment = keyPath is null && credentialConfig is null
                ? _environment
                : LocalFirstRunRepositoryProbe.TransportEnvironment(keyPath, credentialConfig);
            Directory.CreateDirectory(Path.GetDirectoryName(checkout)!);
            if (Directory.Exists(checkout) || File.Exists(checkout))
            {
                // A retry after a restart adopts its own earlier clone; any
                // other folder is somebody's data and is never overwritten.
                var (remoteRead, remote) = await GitAsync(checkout, ["config", "--get", "remote.origin.url"], environment, ct);
                if (!remoteRead || remote.Trim() is var url && url != origin && url != transport)
                    return Fail(project.Id, $"A folder already exists at {checkout}. Remove it, or enter an existing checkout as the repository path in Project basics.");
            }
            else
            {
                var (ok, _) = await GitAsync(Path.GetDirectoryName(checkout)!,
                    ["clone", "--", transport, checkout], environment, ct);
                cloned = true;
                if (!ok)
                    return Fail(project.Id, $"The Studio server could not clone {origin}. Give this host Git access to the repository (Verify repository access in the first-run checklist), then try again.");
            }
            foreach (var args in LocalFirstRunGitSetup.CheckoutCredentialConfig(origin, transport, keyPath, credentialConfig))
                if (!(await GitAsync(checkout, args, environment, ct)).Success)
                    return Fail(project.Id, "The checkout was cloned, but its credential could not be configured.");

            Progress(project.Id, "checkout");
            var branch = await CheckOutIntegrationBranchAsync(project, checkout, environment, ct);
            if (branch.Error is not null) return Fail(project.Id, branch.Error);

            Progress(project.Id, "register");
            project = _projects.SetRepositoryPath(project.Id, checkout);
            if (string.IsNullOrWhiteSpace(project.RootPath))
                project = _projects.SetRootPath(project.Id, checkout);
            cloned = false;
            var entry = _scanner?.GetWatchPaths().FirstOrDefault(candidate =>
                string.Equals(candidate.Name, project.DisplayName, StringComparison.Ordinal));
            if (entry is not null) _runners?.EnsureRunner(entry);
            var ready = Observe(project) with { State = "ready", Branch = branch.Name };
            _jobs[project.Id] = ready;
            return ready;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail(project.Id, "The server checkout could not be created: " + ex.Message);
        }
        finally
        {
            if (cloned && Directory.Exists(checkout))
            {
                try { Directory.Delete(checkout, recursive: true); }
                catch (IOException ex) { SilentCatch.Note(ex, "ProjectServerCheckout: a failed clone stays for the next attempt to report"); }
                catch (UnauthorizedAccessException ex) { SilentCatch.Note(ex, "ProjectServerCheckout: a failed clone stays for the next attempt to report"); }
            }
        }
    }

    private async Task<(string? Name, string? Error)> CheckOutIntegrationBranchAsync(ProjectRecord project,
        string checkout, IReadOnlyDictionary<string, string?> environment, CancellationToken ct)
    {
        var configured = _settings.Get(project.DisplayName).IntegrationBranch?.Trim();
        if (string.IsNullOrWhiteSpace(configured))
        {
            var (_, head) = await GitAsync(checkout, ["rev-parse", "--abbrev-ref", "HEAD"], environment, ct);
            return (head.Trim(), null);
        }
        if (!(await GitAsync(checkout, ["check-ref-format", "--branch", configured], environment, ct)).Success)
            return (null, $"The integration branch '{configured}' is not a valid branch name. Fix it in the project's Git settings.");
        var remote = "refs/remotes/origin/" + configured;
        var published = (await GitAsync(checkout, ["rev-parse", "--verify", "--quiet", remote], environment, ct)).Success;
        // An unpublished integration branch starts at the default branch; the
        // first integration publishes it.
        var (checkedOut, _) = await GitAsync(checkout, published
            ? ["checkout", "-B", configured, "--track", "origin/" + configured]
            : ["checkout", "-B", configured], environment, ct);
        return checkedOut ? (configured, null)
            : (null, $"The checkout was cloned, but the integration branch '{configured}' could not be checked out.");
    }

    private ProjectServerCheckoutStatus Observe(ProjectRecord project)
    {
        var url = RepositoryUrl(project);
        var path = project.RepositoryPath;
        var present = !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);
        var state = present ? "present" : url is null ? "unavailable" : "missing";
        return new(project.Id, state, null, url, present ? path : null,
            _settings.Get(project.DisplayName).IntegrationBranch, null);
    }

    private void Progress(string projectId, string step)
    {
        if (_jobs.TryGetValue(projectId, out var job))
            _jobs[projectId] = job with { Step = step };
    }

    private ProjectServerCheckoutStatus Fail(string projectId, string error)
    {
        var project = _projects.FindById(projectId)!;
        var failed = Observe(project) with { State = "failed", Error = error };
        _jobs[projectId] = failed;
        return failed;
    }

    private static string? RepositoryUrl(ProjectRecord project)
        => project.Urls.FirstOrDefault(url =>
            string.Equals(url.Id, "repo", StringComparison.OrdinalIgnoreCase))?.Url;

    /// <summary>
    /// API-created storage is <c>&lt;task-repository&gt;/projects/&lt;id&gt;/tasks</c>,
    /// whose checkout sits next to the networked one so project delete removes
    /// it. Any other storage layout falls back to the local state root.
    /// </summary>
    internal static string ManagedPath(ProjectRecord project)
    {
        var storage = string.IsNullOrWhiteSpace(project.StorageLocation) ? null : new DirectoryInfo(project.StorageLocation);
        return storage is { Name: "tasks", Parent: { } owner } && owner.Name == project.Id && owner.Parent?.Name == "projects"
            ? NetworkedRepositoryCheckout.TryManagedPath(project.StorageLocation, project.Id)!
            : LocalStateRoot.Combine("AgentStudio", "repository-checkouts", project.Id);
    }

    private async Task<(bool Success, string Output)> GitAsync(string directory, IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string?> environment, CancellationToken ct)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GCM_INTERACTIVE"] = "Never";
        foreach (var (key, value) in environment)
            if (value is null) start.Environment.Remove(key);
            else start.Environment[key] = value;
        foreach (var arg in args) start.ArgumentList.Add(arg);
        var result = await GitProcessBoundary.RunAsync(start, null, GitProcessBoundary.LongOperationTimeout, ct);
        ct.ThrowIfCancellationRequested();
        return (result.Success, result.StandardOutput);
    }
}

public static class ProjectServerCheckoutEndpoints
{
    public static void MapProjectServerCheckoutEndpoints(this WebApplication app)
    {
        var checkout = app.MapGroup("/api/projects/{projectId}")
            .RequireStudioRole(StudioRoles.Owner);
        checkout.MapGet("/server-checkout", (string projectId, ProjectServerCheckoutService service)
            => service.Get(projectId) is { } status ? Results.Ok(status) : Results.NotFound());
        checkout.MapPost("/server-checkout", (string projectId, ProjectServerCheckoutService service) =>
        {
            try { return Results.Accepted(value: service.Start(projectId)); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });
    }
}
