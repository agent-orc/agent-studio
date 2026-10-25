using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

public sealed class ProjectUrlProcessServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "project-url-process-" + Guid.NewGuid().ToString("N"));

    public ProjectUrlProcessServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void ResolveWorkingDirectory_PrefersExplicitCwd()
    {
        var explicitCwd = Directory.CreateDirectory(Path.Combine(_root, "explicit")).FullName;
        var repository = Directory.CreateDirectory(Path.Combine(_root, "repository")).FullName;

        var actual = ProjectUrlProcessService.ResolveWorkingDirectory(
            Project(repositoryPath: repository), Rule(cwd: explicitCwd));

        Assert.Equal(explicitCwd, actual);
    }

    [Fact]
    public void ResolveWorkingDirectory_FallsBackFromMissingRepositoryToRoot()
    {
        var root = Directory.CreateDirectory(Path.Combine(_root, "root")).FullName;

        var actual = ProjectUrlProcessService.ResolveWorkingDirectory(
            Project(repositoryPath: Path.Combine(_root, "missing"), rootPath: root), Rule());

        Assert.Equal(root, actual);
    }

    [Fact]
    public void ResolveWorkingDirectory_WithoutSourceRoot_ExplainsHowToFixIt()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            ProjectUrlProcessService.ResolveWorkingDirectory(Project(), Rule()));

        Assert.Contains("Set a URL cwd or project repository/root path", error.Message);
    }

    [Fact]
    public void BuildStartInfo_DetachesTheCommandFromInteractiveInput()
    {
        var info = ProjectUrlProcessService.BuildStartInfo("echo ready", _root, isolated: true);

        Assert.True(info.CreateNoWindow);
        Assert.True(info.RedirectStandardInput);
        Assert.True(info.RedirectStandardOutput);
        Assert.True(info.RedirectStandardError);
    }

    [Fact]
    public void BuildStartInfo_PassesOnlyProcessBootstrapVariables()
    {
        var info = ProjectUrlProcessService.BuildStartInfo("echo ready", _root, isolated: true);

        Assert.All(info.Environment.Keys, key => Assert.Contains(key,
            new[] { "PATH", "SystemRoot", "ComSpec", "PATHEXT", "TMPDIR", "TEMP", "TMP" }));
        Assert.DoesNotContain("HOME", info.Environment.Keys);
        Assert.DoesNotContain("GIT_CONFIG_COUNT", info.Environment.Keys);
        Assert.DoesNotContain("AGENT_STUDIO_GITHUB_PROVISIONING_TOKEN", info.Environment.Keys);
    }

    [Fact]
    public void BuildStartInfo_LocalProfilePreservesHome()
    {
        var info = ProjectUrlProcessService.BuildStartInfo("echo ready", _root);

        info.Environment.TryGetValue("HOME", out var home);
        Assert.Equal(Environment.GetEnvironmentVariable("HOME"), home);
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public void Start_ReturnsCommandAndEffectiveWorkingDirectory()
    {
        using var service = Service();
        var url = new ProjectUrlRecord
        {
            Id = "url-1",
            Url = "http://localhost:4201",
            StartRule = Rule(command: OperatingSystem.IsWindows() ? "exit /b 0" : "exit 0"),
        };

        var result = service.Start(Project(repositoryPath: _root), url);

        Assert.True(result.Started);
        Assert.Equal("url-1", result.UrlId);
        Assert.Equal(url.StartRule.Command, result.Command);
        Assert.Equal(_root, result.Cwd);
        Assert.True(result.ProcessId > 0);
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task Start_CapturesOutputAndCompletionInTheSessionSnapshot()
    {
        using var service = Service();
        var started = service.Start(Project(repositoryPath: _root), Url("url-output", EchoCommand()));

        var settled = await WaitForAsync(service, started.UrlId, snapshot =>
            snapshot.State == ProjectUrlProcessStates.Exited
            && snapshot.Output.Any(line => line.Contains("embed-ready", StringComparison.Ordinal)));

        Assert.Equal(0, settled.ExitCode);
        Assert.Contains(settled.Output, line => line.Contains("embed-ready", StringComparison.Ordinal));
        Assert.Contains(settled.Output, line => line.Contains("exited with code 0", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task StartWithReadiness_ReturnsStartingAndPublishesSilenceFailureInSession()
    {
        using var service = Service();
        var candidate = Url("url-readiness", LongRunningCommand()) with
        {
            StartRule = Rule(LongRunningCommand()) with
            {
                Port = 4202,
                ReadinessTimeoutSeconds = 2,
                StartupTimeoutSeconds = 10,
            },
        };

        var started = service.StartWithReadiness(Project(repositoryPath: _root), candidate);
        var settled = await WaitForAsync(
            service,
            candidate.Id,
            snapshot => snapshot.State == ProjectUrlProcessStates.Failed);

        Assert.Equal(ProjectUrlProcessStates.Starting, started.State);
        Assert.Contains(settled.Output, line => line.Contains("no console output", StringComparison.Ordinal));
        Assert.Equal(
            ProjectUrlStartupFailureReasons.SilenceTimeout,
            service.Latest(Project(repositoryPath: _root), candidate)?.StartupFailureReason);
        service.Stop(settled.ProjectId, settled.UrlId);
    }

    [Fact]
    public void StartWithReadiness_WhenTargetPortIsOccupied_DoesNotSpawnAndNamesOwner()
    {
        var inspector = new StubPortInspector(new ProjectUrlPortOccupant(9123, "marketing-app"));
        using var service = new ProjectUrlProcessService(
            NullLogger<ProjectUrlProcessService>.Instance,
            new PassiveHttpClientFactory(),
            inspector);
        var candidate = Url("url-occupied", LongRunningCommand()) with
        {
            Url = "http://localhost:4200",
            StartRule = Rule(LongRunningCommand()) with { Port = 4200 },
        };
        var project = Project(repositoryPath: _root);

        var error = Assert.Throws<ProjectUrlPortOccupiedException>(() =>
            service.StartWithReadiness(project, candidate));

        Assert.Equal("Port 4200 is already in use by marketing-app (PID 9123).", error.Message);
        Assert.Null(service.Get(project.Id, candidate.Id));
        var diagnostic = Assert.IsType<ProjectUrlDiagnostic>(service.Latest(project, candidate));
        Assert.Equal(ProjectUrlDiagnosisClasses.PortInUse, diagnostic.Classification);
        Assert.Equal(ProjectUrlStartupFailureReasons.PortInUse, diagnostic.StartupFailureReason);
        Assert.False(diagnostic.ProcessCreated);
        Assert.Equal(9123, diagnostic.OccupyingProcessId);
        Assert.Equal("marketing-app", diagnostic.OccupyingProcessName);
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public void Stop_TerminatesTheOwnedProcessTreeAndRetainsItsSnapshot()
    {
        using var service = Service();
        var started = service.Start(Project(repositoryPath: _root), Url("url-stop", LongRunningCommand()));

        var stopped = service.Stop(started.ProjectId, started.UrlId);

        Assert.NotNull(stopped);
        Assert.Equal(ProjectUrlProcessStates.Stopped, stopped.State);
        Assert.NotNull(stopped.FinishedAtUtc);
        Assert.Contains(stopped.Output, line => line.Contains("stopped by operator", StringComparison.Ordinal));
        var retained = service.Get(started.ProjectId, started.UrlId);
        Assert.NotNull(retained);
        Assert.Equal(stopped.ProcessId, retained.ProcessId);
        Assert.Equal(stopped.State, retained.State);
        Assert.Equal(stopped.Output, retained.Output);
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public void StopProject_TerminatesEveryOwnedUrlProcess()
    {
        using var service = Service();
        var project = Project(repositoryPath: _root);
        service.Start(project, Url("url-one", LongRunningCommand()));
        service.Start(project, Url("url-two", LongRunningCommand()));

        var stopped = service.StopProject(project.Id);

        Assert.Equal(2, stopped.Count);
        Assert.All(stopped, snapshot => Assert.Equal(ProjectUrlProcessStates.Stopped, snapshot.State));
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public void Dispose_TerminatesOwnedProcessesSoHostShutdownCannotOrphanThem()
    {
        var service = Service();
        var started = service.Start(
            Project(repositoryPath: _root),
            Url("url-shutdown", LongRunningCommand()));

        service.Dispose();

        var settled = service.Get(started.ProjectId, started.UrlId);
        Assert.NotNull(settled);
        Assert.False(ProjectUrlProcessStates.IsActive(settled.State));
    }

    [Fact]
    public void PackageJsonDiscovery_PersistsRepositoryWorkingDirectory()
    {
        File.WriteAllText(Path.Combine(_root, "package.json"),
            "{\"scripts\":{\"website\":\"vite --port 4202\"}}");
        var detection = new ProjectUrlDetectionService(
            NullLogger<ProjectUrlDetectionService>.Instance);

        var suggestion = Assert.Single(detection.Detect(Project(repositoryPath: _root)));

        Assert.Equal(_root, suggestion.Cwd);
        Assert.Equal("npm run website", suggestion.Command);
    }

    private static ProjectRecord Project(string? repositoryPath = null, string? rootPath = null) => new()
    {
        Id = "PROJ-001",
        RepositoryPath = repositoryPath,
        RootPath = rootPath,
        StorageLocation = @"C:\task-store\must-not-be-used",
    };

    private static ProjectUrlStartRule Rule(string command = "npm start", string? cwd = null) => new()
    {
        Command = command,
        Cwd = cwd,
    };

    private static ProjectUrlRecord Url(string id, string command) => new()
    {
        Id = id,
        Label = id,
        Url = "http://localhost:4202",
        StartRule = Rule(command),
    };

    private static string EchoCommand() => OperatingSystem.IsWindows()
        ? "echo embed-ready"
        : "printf 'embed-ready\\n'";

    private static string LongRunningCommand() => OperatingSystem.IsWindows()
        ? "ping -n 30 127.0.0.1 > nul"
        : "sleep 30";

    private static async Task<ProjectUrlProcessSnapshot> WaitForAsync(
        ProjectUrlProcessService service,
        string urlId,
        Func<ProjectUrlProcessSnapshot, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!timeout.IsCancellationRequested)
        {
            var snapshot = service.Get("PROJ-001", urlId);
            if (snapshot != null && predicate(snapshot)) return snapshot;
            await Task.Delay(25, timeout.Token);
        }
        throw new TimeoutException("Process did not reach the expected state.");
    }

    private static ProjectUrlProcessService Service() => new(
        NullLogger<ProjectUrlProcessService>.Instance,
        new PassiveHttpClientFactory(),
        new StubPortInspector(null));

    /// <summary>The direct-lifecycle tests never issue HTTP probes.</summary>
    private sealed class PassiveHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class StubPortInspector(ProjectUrlPortOccupant? occupant) : IProjectUrlPortInspector
    {
        public ProjectUrlPortOccupant? FindListener(int port) => occupant;
    }
}
