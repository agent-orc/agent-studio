using System.Diagnostics;
using System.Security.Cryptography;

namespace AgentStudio.Scenario;

/// <summary>
/// One deployment topology the scenario steps run against. The step catalogue
/// is identical for every target; only the way the deployment is brought up and
/// addressed differs.
/// </summary>
public interface IScenarioTarget : IAsyncDisposable
{
    ScenarioTargetKind Kind { get; }

    /// <summary>Brings the deployment up and returns how to reach it.</summary>
    Task<ScenarioEndpoints> StartAsync(CancellationToken ct);

    /// <summary>
    /// Starts a coding runner bound to the fixture's deterministic CLI. Targets
    /// that cannot host a runner return null and their runner-dependent steps
    /// are not declared for them.
    /// </summary>
    Task<ScenarioProcess?> StartRunnerAsync(string authToken, CancellationToken ct);

    /// <summary>
    /// Starts a second, empty deployment that a backup is restored into. Only
    /// targets that own their store support this.
    /// </summary>
    Task<ScenarioEndpoints> StartEmptyPeerAsync(string backupDirectory, CancellationToken ct);

    /// <summary>Diagnostic text appended to a failure, such as child output.</summary>
    string Diagnostics();
}

public static class ScenarioCredentials
{
    /// <summary>
    /// The Task Server refuses bearer credentials below 32 characters, so every
    /// generated credential is a 64 character hex string.
    /// </summary>
    public static string Generate() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
}

/// <summary>
/// Sibling processes started from the local build: Task Server, Studio BFF,
/// orchestrator engine, and a coding runner with the fixture CLI. This is the
/// target that runs on Windows and Linux without Docker.
/// </summary>
public sealed class InProcScenarioTarget(
    string repositoryRoot,
    ScenarioFixture fixture,
    string workRoot) : IScenarioTarget
{
    private const string RunnerId = "scenario-runner";

    private readonly List<ScenarioProcess> _processes = [];
    private string _studioToken = string.Empty;
    private string _serverUrl = string.Empty;

    public ScenarioTargetKind Kind => ScenarioTargetKind.InProc;

    public async Task<ScenarioEndpoints> StartAsync(CancellationToken ct)
    {
        var data = Directory.CreateDirectory(Path.Combine(workRoot, "store")).FullName;
        var backups = Directory.CreateDirectory(Path.Combine(workRoot, "backups")).FullName;
        _studioToken = ScenarioCredentials.Generate();
        var runnerToken = ScenarioCredentials.Generate();
        var serverUrl = $"http://127.0.0.1:{ScenarioProcess.FreePort()}";
        var studioUrl = $"http://127.0.0.1:{ScenarioProcess.FreePort()}";
        _serverUrl = serverUrl;

        var server = Track(ScenarioProcess.StartBuilt(
            repositoryRoot,
            "task-server",
            "task-server.dll",
            new Dictionary<string, string?>
            {
                ["LISTEN_URL"] = serverUrl,
                ["STORE_PATH"] = data,
                ["BACKUP_PATH"] = backups,
                ["AUTH"] = "bearer",
                ["STUDIO_AUTH_TOKEN"] = _studioToken,
                ["BOOTSTRAP_RUNNER_ID"] = RunnerId,
                ["BOOTSTRAP_RUNNER_AUTH_TOKEN"] = runnerToken,
            }));
        await WaitForReadyAsync(serverUrl + "/readyz", server, ct);

        var studio = Track(ScenarioProcess.StartBuilt(
            repositoryRoot,
            "studio-bff",
            "agent-studio-bff.dll",
            null,
            "--urls", studioUrl,
            "--TaskServer:BaseUrl", serverUrl,
            "--TaskServer:BearerToken", _studioToken));
        await WaitForReadyAsync(studioUrl + "/healthz", studio, ct);

        // The orchestrator engine is deliberately not started, and no engine
        // credential is issued for it. Its claim request serializes
        // OrchestrationStage as a string while the Task Server binds the enum
        // numerically, so every claim is rejected and starting it would add a
        // rejected-claim storm without proving anything. The scenario gains a
        // review-settlement step once that mismatch is fixed; see
        // docs/operations/testing/deployment-scenario.md.
        return new ScenarioEndpoints(
            serverUrl, studioUrl, _studioToken, RunnerId, runnerToken, backups);
    }

    public Task<ScenarioProcess?> StartRunnerAsync(string authToken, CancellationToken ct)
    {
        var runnerWork = Directory.CreateDirectory(Path.Combine(workRoot, "runner")).FullName;
        var runner = Track(ScenarioProcess.StartBuilt(
            repositoryRoot,
            "runner",
            "agent-host.dll",
            new Dictionary<string, string?>
            {
                ["RUNNER_AUTH_TOKEN"] = authToken,
                ["RUNNER_HEARTBEAT_SECONDS"] = "5",
                ["RUNNER_RUN_TIMEOUT_SECONDS"] = "120",
                ["SCENARIO_INVOCATION_COUNTER"] = fixture.InvocationCounter,
            },
            "--poll",
            "--server", _serverUrl,
            "--runner-id", RunnerId,
            "--runner-name", RunnerId,
            "--hostname", "scenario-host",
            "--git-remote", fixture.BareRepository,
            "--workdir", runnerWork,
            "--cli", fixture.CodingCli,
            "--ttl", "60",
            "--max-parallelism", "1",
            "--poll-seconds", "1"));
        return Task.FromResult<ScenarioProcess?>(runner);
    }

    public async Task<ScenarioEndpoints> StartEmptyPeerAsync(
        string backupDirectory,
        CancellationToken ct)
    {
        var peerData = Directory.CreateDirectory(Path.Combine(workRoot, "restore-store")).FullName;
        var peerUrl = $"http://127.0.0.1:{ScenarioProcess.FreePort()}";
        var peer = Track(ScenarioProcess.StartBuilt(
            repositoryRoot,
            "task-server",
            "task-server.dll",
            new Dictionary<string, string?>
            {
                ["LISTEN_URL"] = peerUrl,
                ["STORE_PATH"] = peerData,
                ["BACKUP_PATH"] = backupDirectory,
                ["AUTH"] = "bearer",
                ["STUDIO_AUTH_TOKEN"] = _studioToken,
            }));
        await WaitForReadyAsync(peerUrl + "/readyz", peer, ct);
        return new ScenarioEndpoints(peerUrl, null, _studioToken, RunnerId, null, backupDirectory);
    }

    public string Diagnostics()
        => string.Join(
            Environment.NewLine,
            _processes.Select(process =>
                $"--- {process.Name} (running={process.IsRunning}) ---{Environment.NewLine}{process}"));

    private ScenarioProcess Track(ScenarioProcess process)
    {
        _processes.Add(process);
        return process;
    }

    private async Task WaitForReadyAsync(string url, ScenarioProcess process, CancellationToken ct)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var deadline = DateTime.UtcNow.AddSeconds(45);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            process.EnsureRunning();
            try
            {
                using var response = await client.GetAsync(url, ct);
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException exception) { last = exception; }
            catch (TaskCanceledException exception) when (!ct.IsCancellationRequested) { last = exception; }
            await Task.Delay(ScenarioWait.PollInterval, ct);
        }
        throw new ScenarioTargetException(
            $"{process.Name} did not become ready at {url}: {last?.Message}"
            + $"{Environment.NewLine}{process}");
    }

    public ValueTask DisposeAsync()
    {
        foreach (var process in Enumerable.Reverse(_processes)) process.Dispose();
        _processes.Clear();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// The docker-compose distributed control plane. Task Server, Studio BFF, and
/// the orchestrator engine run as containers from docker-compose.yml; the
/// coding runner runs as a host process against the published Task Server port
/// with the fixture CLI, so the deterministic CLI needs no separate image.
/// </summary>
public sealed class ComposeScenarioTarget(
    string repositoryRoot,
    ScenarioFixture fixture,
    string workRoot,
    string projectName) : IScenarioTarget
{
    private const string RunnerId = "distributed-runner";

    private readonly List<ScenarioProcess> _processes = [];
    private readonly List<string> _diagnostics = [];
    private string _runnerToken = string.Empty;
    private string _studioToken = string.Empty;
    private string _serverUrl = string.Empty;
    private bool _up;

    public ScenarioTargetKind Kind => ScenarioTargetKind.Compose;

    public async Task<ScenarioEndpoints> StartAsync(CancellationToken ct)
    {
        _studioToken = ScenarioCredentials.Generate();
        _runnerToken = ScenarioCredentials.Generate();
        var engineToken = ScenarioCredentials.Generate();
        var taskServerPort = ScenarioProcess.FreePort();
        var studioBffPort = ScenarioProcess.FreePort();
        // Compose reads every one of these through docker-compose.yml, and the
        // engine token is a required variable there even when the engine service
        // is not started. Recorded before the first call so a teardown or a
        // diagnostics dump after a failed start still has them.
        _composeEnvironment = new Dictionary<string, string?>
        {
            ["DISTRIBUTED_STUDIO_TOKEN"] = _studioToken,
            ["DISTRIBUTED_ENGINE_TOKEN"] = engineToken,
            ["DISTRIBUTED_RUNNER_TOKEN"] = _runnerToken,
            ["STUDIO_TASKSERVER_PORT"] = taskServerPort.ToString(),
            ["STUDIO_BFF_PORT"] = studioBffPort.ToString(),
        };

        await ComposeAsync(_composeEnvironment, ct, "down", "--volumes", "--remove-orphans");
        _up = true;
        // Only the Task Server and the Studio BFF run in containers. The
        // distributed agent host is left out because it ships the real coding
        // CLIs and this scenario must stay deterministic; the orchestrator
        // engine is left out for the claim-serialization defect recorded in
        // docs/operations/testing/deployment-scenario.md.
        await ComposeAsync(
            _composeEnvironment, ct,
            "up", "--build", "--detach", "--wait",
            "task-server", "studio-bff");

        _serverUrl = $"http://127.0.0.1:{taskServerPort}";
        var studioUrl = $"http://127.0.0.1:{studioBffPort}";
        await WaitForHttpAsync(_serverUrl + "/readyz", ct);
        await WaitForHttpAsync(studioUrl + "/healthz", ct);
        return new ScenarioEndpoints(
            _serverUrl, studioUrl, _studioToken, RunnerId, _runnerToken, null);
    }

    private Dictionary<string, string?> _composeEnvironment = [];

    public Task<ScenarioProcess?> StartRunnerAsync(string authToken, CancellationToken ct)
    {
        var runnerWork = Directory.CreateDirectory(Path.Combine(workRoot, "runner")).FullName;
        var runner = ScenarioProcess.StartBuilt(
            repositoryRoot,
            "runner",
            "agent-host.dll",
            new Dictionary<string, string?>
            {
                ["RUNNER_AUTH_TOKEN"] = authToken,
                ["RUNNER_HEARTBEAT_SECONDS"] = "5",
                ["RUNNER_RUN_TIMEOUT_SECONDS"] = "120",
                ["SCENARIO_INVOCATION_COUNTER"] = fixture.InvocationCounter,
            },
            "--poll",
            "--server", _serverUrl,
            "--runner-id", RunnerId,
            "--runner-name", RunnerId,
            "--hostname", "scenario-compose-host",
            "--git-remote", fixture.BareRepository,
            "--workdir", runnerWork,
            "--cli", fixture.CodingCli,
            "--ttl", "60",
            "--max-parallelism", "1",
            "--poll-seconds", "1");
        _processes.Add(runner);
        return Task.FromResult<ScenarioProcess?>(runner);
    }

    public Task<ScenarioEndpoints> StartEmptyPeerAsync(string backupDirectory, CancellationToken ct)
        => throw new ScenarioTargetException(
            "The compose target does not own a second store. Restore-into-empty-store is "
            + "declared for the inproc target only.");

    public string Diagnostics()
        => string.Join(
            Environment.NewLine,
            _diagnostics.Concat(_processes.Select(process =>
                $"--- {process.Name} ---{Environment.NewLine}{process}")));

    private async Task WaitForHttpAsync(string url, CancellationToken ct)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var deadline = DateTime.UtcNow.AddSeconds(120);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var response = await client.GetAsync(url, ct);
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException exception) { last = exception; }
            catch (TaskCanceledException exception) when (!ct.IsCancellationRequested) { last = exception; }
            await Task.Delay(ScenarioWait.PollInterval, ct);
        }
        await CaptureComposeDiagnosticsAsync(ct);
        throw new ScenarioTargetException(
            $"The compose stack did not become ready at {url}: {last?.Message}");
    }

    private async Task CaptureComposeDiagnosticsAsync(CancellationToken ct)
    {
        try
        {
            _diagnostics.Add(await ComposeOutputAsync(_composeEnvironment, ct, "ps"));
            _diagnostics.Add(await ComposeOutputAsync(_composeEnvironment, ct, "logs", "--no-color"));
        }
        catch (ScenarioTargetException exception)
        {
            _diagnostics.Add($"compose diagnostics unavailable: {exception.Message}");
        }
    }

    private Task ComposeAsync(
        IReadOnlyDictionary<string, string?> environment,
        CancellationToken ct,
        params string[] arguments)
        => ComposeOutputAsync(environment, ct, arguments);

    private async Task<string> ComposeOutputAsync(
        IReadOnlyDictionary<string, string?> environment,
        CancellationToken ct,
        params string[] arguments)
    {
        var start = new ProcessStartInfo("docker")
        {
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("compose");
        start.ArgumentList.Add("--project-name");
        start.ArgumentList.Add(projectName);
        start.ArgumentList.Add("--profile");
        start.ArgumentList.Add("distributed");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        foreach (var (key, value) in environment) start.Environment[key] = value;

        using var process = Process.Start(start)
            ?? throw new ScenarioTargetException(
                "Could not start 'docker compose'. Install Docker Engine with the Compose plugin.");
        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0 && arguments is not ["down", ..])
            throw new ScenarioTargetException(
                $"docker compose {string.Join(' ', arguments)} exited {process.ExitCode}."
                + $"{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
        return stdout + stderr;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var process in Enumerable.Reverse(_processes)) process.Dispose();
        _processes.Clear();
        if (!_up) return;
        try
        {
            await ComposeAsync(
                _composeEnvironment, CancellationToken.None,
                "down", "--volumes", "--remove-orphans");
        }
        catch (ScenarioTargetException exception)
        {
            Console.Error.WriteLine($"scenario: compose teardown failed: {exception.Message}");
        }
    }
}

/// <summary>
/// An already running deployment addressed by URL and credential. The scenario
/// creates and uses its own workspace and project, never touches existing data,
/// and hosts no runner, so the run is non-destructive on a production control
/// plane.
/// </summary>
public sealed class RemoteScenarioTarget(
    string taskServerUrl,
    string? studioUrl,
    string token) : IScenarioTarget
{
    public ScenarioTargetKind Kind => ScenarioTargetKind.Remote;

    public Task<ScenarioEndpoints> StartAsync(CancellationToken ct)
        => Task.FromResult(
            new ScenarioEndpoints(taskServerUrl, studioUrl, token, "remote-runner", null, null));

    public Task<ScenarioProcess?> StartRunnerAsync(string authToken, CancellationToken ct)
        => Task.FromResult<ScenarioProcess?>(null);

    public Task<ScenarioEndpoints> StartEmptyPeerAsync(string backupDirectory, CancellationToken ct)
        => throw new ScenarioTargetException(
            "The remote target must not restore into a store it does not own. "
            + "Restore-into-empty-store is declared for the inproc target only.");

    public string Diagnostics() => $"remote target at {taskServerUrl}";

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
