using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentRunner;
using AgentStudio.TestSupport;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// AGT-2870: an operator could not stop a remotely executed run. <c>POST
/// /api/tasks/{key}/stop</c> only knew local processes, so the only way to end
/// the observed run was to kill the CLI process on the host by hand. The Task
/// Server now records the stop for the attempt and answers the runner's next
/// lease renewal with it; this proves the runner side of that contract - the
/// worker tree is terminated and the attempt hands back as <c>Stopped</c> with
/// its work salvaged.
/// </summary>
public sealed class RemoteOperatorStopTests : IDisposable
{
    /// <summary>
    /// Wire options of the runner's own client: it writes enum members as
    /// camelCase strings, so a fixture on plain web defaults cannot read the
    /// completion back and the stop looks like a timeout instead of a mismatch.
    /// </summary>
    private static readonly JsonSerializerOptions Json = CreateWireJson();

    private static JsonSerializerOptions CreateWireJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"remote-operator-stop-{Guid.NewGuid():N}");

    [Fact]
    public void A_stopped_run_returns_the_card_to_ready_rather_than_judging_the_work()
    {
        var stopped = new RunOutcome(RunOutcomeKind.Stopped, "The run was stopped by an operator (followup).");

        Assert.Equal("2-ready", stopped.TargetState);
        Assert.Equal("Remote run stopped by an operator", stopped.SummaryPrefix);
    }

    [SkippableFact]
    [Trait("Category", "MachineBound")]
    public async Task A_stop_seen_on_the_heartbeat_terminates_the_worker_and_hands_back_stopped()
    {
        PlatformGate.RequiresPosixShell();
        var origin = Path.Combine(_root, "origin.git");
        await CreateOriginAsync(origin, Path.Combine(_root, "seed"));
        var options = Options(origin);
        var lease = new RunLeaseInfoDto(
            "AGT-OPERATOR-STOP",
            options.RunnerId,
            options.RunnerName,
            options.Hostname,
            Environment.ProcessId,
            options.BackendName,
            "lease-operator-stop",
            3,
            DateTime.UtcNow,
            DateTime.UtcNow.AddMinutes(5),
            "attempt-operator-stop",
            1);
        var server = new StoppingServer(lease);
        using var http = new HttpClient(server)
        {
            BaseAddress = new Uri("http://task-server"),
            Timeout = Timeout.InfiniteTimeSpan,
        };
        using var client = new TaskServerClient(http, options.RunnerId, options: options);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var logs = new ConcurrentQueue<string>();
        var daemon = new RemoteRunnerDaemon(options, client, logs.Enqueue);
        var daemonTask = daemon.RunAsync(stop.Token);
        Process? worker = null;

        try
        {
            var slot = await WaitForWorkingAgentAsync(options.StateDir, stop.Token);
            worker = Process.GetProcessById(slot.ProcessId!.Value);

            // The operator presses Pause and Send: the server parks the stop and
            // the runner learns about it on its next renewal, not before.
            server.RequestStop(RemoteStopReasonFollowup);

            RemoteRunCompletionRequest completion;
            try
            {
                completion = await server.Completed.Task.WaitAsync(TimeSpan.FromSeconds(90));
            }
            catch (TimeoutException)
            {
                // A machine-bound stop test that only says "timed out" costs a
                // second run to learn anything, so the runner's journal goes
                // into the failure itself.
                throw new Xunit.Sdk.XunitException(
                    "no completion was delivered after the operator stop. Runner journal:"
                    + Environment.NewLine
                    + string.Join(Environment.NewLine, logs));
            }

            Assert.Equal(nameof(RunOutcomeKind.Stopped), completion.Outcome);
            Assert.Contains("stopped by an operator", completion.Reason, StringComparison.OrdinalIgnoreCase);

            // The work the stopped agent had already written is preserved under
            // this attempt's own generation ref, so the next round continues.
            Assert.NotNull(completion.SalvageBranch);
            Assert.StartsWith(
                $"agent-studio/salvage/{options.RunnerId}/{lease.TaskKey}/attempt-operator-stop/fence-3/",
                completion.SalvageBranch,
                StringComparison.Ordinal);
            Assert.Equal(
                completion.ResultSha,
                (await GitOutputAsync(origin, "rev-parse", completion.SalvageBranch!)).Trim());

            // The worker's process tree is gone; the daemon did not wait for the
            // agent to notice a cancelled token.
            await WaitForExitAsync(worker, TimeSpan.FromSeconds(30));
            Assert.True(worker.HasExited, "the stopped worker process is still running");

            Assert.Contains(logs, line => line.Contains("operator stop requested", StringComparison.Ordinal));
            Assert.Contains(logs, line => line.Contains(
                $"operator-stop attempt={slot.AttemptId}",
                StringComparison.Ordinal));
        }
        finally
        {
            await stop.CancelAsync();
            try { await daemonTask.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) { }
            if (worker is { HasExited: false })
                worker.Kill(entireProcessTree: true);
        }
    }

    private const string RemoteStopReasonFollowup = "followup";

    private RunnerOptions Options(string origin) => new()
    {
        ServerUrl = "http://task-server",
        RunnerId = "runner-operator-stop",
        RunnerName = "Runner operator stop",
        Hostname = "fixture-host",
        BackendName = "fake-task-server",
        GitRemote = origin,
        GitPushRemote = origin,
        WorkDir = Path.Combine(_root, "work"),
        StateDir = Path.Combine(_root, "state"),
        BaseBranch = "main",
        ExecEngine = RunnerOptions.ExecEngineLegacy,
        CliBin = PosixShell.RequirePath(),
        CliArgs = "-c \"printf 'half finished\\n' > stopped-work.txt; sleep 300\"",
        TtlSeconds = 300,
        // The renewal is the stop channel, so the run must heartbeat often
        // enough for the test to stay inside its own deadline.
        HeartbeatSeconds = 5,
        RunTimeoutSeconds = 300,
        HostMaxParallelism = 1,
        PollSeconds = 1,
        ServerRequestTimeoutSeconds = 5,
        IdleWatchdogMinutes = 3,
    };

    private static async Task<PersistedRunnerSlot> WaitForWorkingAgentAsync(
        string stateDirectory,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var slot = new RunnerStateStore(stateDirectory).LoadAll().SingleOrDefault();
            if (slot?.ProcessId is > 0
                && File.Exists(Path.Combine(slot.WorktreePath, "stopped-work.txt")))
                return slot;
            await Task.Delay(50, cancellationToken);
        }

        throw new OperationCanceledException(cancellationToken);
    }

    private static async Task WaitForExitAsync(Process process, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!process.HasExited && DateTime.UtcNow < deadline)
            await Task.Delay(50);
    }

    private async Task CreateOriginAsync(string origin, string seed)
    {
        Directory.CreateDirectory(_root);
        await GitAsync(_root, "init", "--bare", origin);
        await GitAsync(_root, "init", seed);
        await GitAsync(seed, "config", "user.name", "Runner Test");
        await GitAsync(seed, "config", "user.email", "runner@example.invalid");
        await File.WriteAllTextAsync(Path.Combine(seed, "README.md"), "seed\n");
        await GitAsync(seed, "add", "README.md");
        await GitAsync(seed, "commit", "-m", "seed");
        await GitAsync(seed, "branch", "-M", "main");
        await GitAsync(seed, "remote", "add", "origin", origin);
        await GitAsync(seed, "push", "-u", "origin", "main");
        await GitAsync(origin, "symbolic-ref", "HEAD", "refs/heads/main");
    }

    private static async Task GitAsync(string workingDirectory, params string[] arguments)
    {
        var result = await ProcessRunner.RunAsync(
            "git", arguments, workingDirectory, ct: CancellationToken.None);
        Assert.True(result.Success, $"git {string.Join(' ', arguments)} failed: {result.StdErr}");
    }

    private static async Task<string> GitOutputAsync(string workingDirectory, params string[] arguments)
    {
        var result = await ProcessRunner.RunAsync(
            "git", arguments, workingDirectory, ct: CancellationToken.None);
        Assert.True(result.Success, $"git {string.Join(' ', arguments)} failed: {result.StdErr}");
        return result.StdOut;
    }

    public void Dispose()
        => ResilientDirectory.TryDelete(_root);

    /// <summary>
    /// Fake Task Server that claims one card, hands out a stop request on the
    /// first renewal after the test asks for it, and records the completion the
    /// runner delivers.
    /// </summary>
    private sealed class StoppingServer(RunLeaseInfoDto lease) : HttpMessageHandler
    {
        private int _claimCount;
        private RunStopDirectiveDto? _stop;

        public TaskCompletionSource<RemoteRunCompletionRequest> Completed { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void RequestStop(string reason)
            => Volatile.Write(
                ref _stop,
                new RunStopDirectiveDto(
                    lease.TaskKey,
                    reason,
                    DateTime.UtcNow,
                    lease.AttemptId,
                    "operator@example.invalid"));

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path == $"/api/tasks/{lease.TaskKey}/files/prompt.md")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("Start the work and keep going."),
                };
            }

            object? response;
            switch (path)
            {
                case "/api/clients/register":
                    response = new ClientRegisterResponse("fixture-client", "Runner operator stop", "service");
                    break;
                case "/api/clients/fixture-client/runner-git-capability":
                    response = new { };
                    break;
                case "/api/runner/project-chat/claim":
                    response = new RemoteChatWorkClaimResponse(RemoteChatWorkClaimStatuses.Empty);
                    break;
                case "/api/runner/claim":
                    response = Interlocked.Increment(ref _claimCount) == 1
                        ? new RunnerClaimResponse(
                            RunnerClaimStatus.Claimed,
                            lease.TaskKey,
                            lease.TaskKey,
                            "Fixture project",
                            lease)
                        : new RunnerClaimResponse(RunnerClaimStatus.Empty);
                    break;
                case "/api/runner/lease/renew":
                    response = new RunLeaseResponse(
                        "Renewed", true, lease, StopRequest: Volatile.Read(ref _stop));
                    break;
                case "/api/runner/logs":
                    response = new LogIngestResponse(lease.TaskKey, 0);
                    break;
                case "/api/runner/artifacts":
                    response = new ArtifactIngestResponse(lease.TaskKey, 0, []);
                    break;
                case "/api/runner/completion":
                    response = await ObserveCompletionAsync(request, cancellationToken);
                    break;
                case "/api/runner/lease/release":
                    response = new RunLeaseResponse("Released", false, lease);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unexpected fake Task Server request: {request.Method} {path}");
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(response, Json)),
            };
        }

        private async Task<RemoteRunCompletionResponse> ObserveCompletionAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var completion = JsonSerializer.Deserialize<RemoteRunCompletionRequest>(body, Json)!;
            Completed.TrySetResult(completion);
            return new RemoteRunCompletionResponse(completion.TaskKey, completion.Outcome, "2-ready");
        }
    }
}
