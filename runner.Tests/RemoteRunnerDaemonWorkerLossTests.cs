using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using AgentRunner;
using AgentStudio.TestSupport;
using Xunit;

namespace AgentRunner.Tests;

[Trait("Category", "MachineBound")]
public sealed class RemoteRunnerDaemonWorkerLossTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"daemon-worker-loss-{Guid.NewGuid():N}");

    [Fact]
    [Trait("Category", "ReviewFlaky")]
    public async Task Hard_killed_worker_is_released_and_the_same_daemon_polls_again()
    {
        var origin = Path.Combine(_root, "origin.git");
        await CreateOriginAsync(origin, Path.Combine(_root, "seed"));
        var options = Options(origin);
        var lease = new RunLeaseInfoDto(
            "AGT-WORKER-LOSS",
            options.RunnerId,
            options.RunnerName,
            options.Hostname,
            Environment.ProcessId,
            options.BackendName,
            "lease-worker-loss",
            7,
            DateTime.UtcNow,
            DateTime.UtcNow.AddMinutes(2),
            "attempt-worker-loss",
            1);
        var server = new WorkerLossServer(lease);
        using var http = new HttpClient(server)
        {
            BaseAddress = new Uri("http://task-server"),
            Timeout = Timeout.InfiniteTimeSpan,
        };
        using var client = new TaskServerClient(http, options.RunnerId, options: options);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var logs = new ConcurrentQueue<string>();
        var daemon = new RemoteRunnerDaemon(options, client, logs.Enqueue);
        var daemonTask = daemon.RunAsync(stop.Token);
        Process? worker = null;

        try
        {
            var slot = await WaitForWorkerAsync(options.StateDir, stop.Token);
            worker = Process.GetProcessById(slot.ProcessId!.Value);
            worker.Kill(entireProcessTree: true);
            await worker.WaitForExitAsync(stop.Token);

            await server.PostReleasePoll.Task.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Contains(logs, line => line.Contains(
                "detached worker lost; attempt will be released to Ready",
                StringComparison.Ordinal));
            Assert.Contains(logs, line => line.Contains(
                "lease released: Released",
                StringComparison.OrdinalIgnoreCase));
            Assert.True(
                server.ClaimPollsAfterRelease > 0,
                "the fake server did not observe a claim poll after worker-loss release");
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

    private RunnerOptions Options(string origin) => new()
    {
        ServerUrl = "http://task-server",
        RunnerId = "runner-worker-loss",
        RunnerName = "Runner worker loss",
        Hostname = "fixture-host",
        BackendName = "fake-task-server",
        GitRemote = origin,
        GitPushRemote = origin,
        WorkDir = Path.Combine(_root, "work"),
        StateDir = Path.Combine(_root, "state"),
        BaseBranch = "main",
        ClaudeCliBin = PosixShell.RequirePath(),
        TtlSeconds = 120,
        HeartbeatSeconds = 30,
        RunTimeoutSeconds = 180,
        HostMaxParallelism = 1,
        PollSeconds = 1,
        ServerRequestTimeoutSeconds = 2,
        IdleWatchdogMinutes = 1,
    };

    private static async Task<PersistedRunnerSlot> WaitForWorkerAsync(
        string stateDirectory,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var slot = new RunnerStateStore(stateDirectory).LoadAll().SingleOrDefault();
            if (slot?.ProcessId is > 0)
                return slot;
            await Task.Delay(50, cancellationToken);
        }

        throw new OperationCanceledException(cancellationToken);
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
            "git",
            arguments,
            workingDirectory,
            ct: CancellationToken.None);
        Assert.True(
            result.Success,
            $"git {string.Join(' ', arguments)} failed: {result.StdErr}");
    }

    public void Dispose()
        => ResilientDirectory.TryDelete(_root);

    private sealed class WorkerLossServer(RunLeaseInfoDto lease) : HttpMessageHandler
    {
        private int _claimCount;
        private int _releaseSeen;
        private int _claimPollsAfterRelease;

        public TaskCompletionSource PostReleasePoll { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int ClaimPollsAfterRelease => Volatile.Read(ref _claimPollsAfterRelease);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            object? response = path switch
            {
                "/api/clients/register" => new ClientRegisterResponse(
                    "fixture-client",
                    "Runner worker loss",
                    "service"),
                "/api/clients/fixture-client/runner-git-capability" => new { },
                "/api/runner/project-chat/claim" => new RemoteChatWorkClaimResponse(
                    RemoteChatWorkClaimStatuses.Empty),
                "/api/runner/claim" => Claim(),
                "/api/runner/lease/renew" => new RunLeaseResponse(
                    "Renewed",
                    true,
                    lease),
                "/api/runner/logs" => new LogIngestResponse(lease.TaskKey, 0),
                "/api/runner/lease/release" => Release(),
                _ when path == $"/api/tasks/{lease.TaskKey}/files/prompt.md" => null,
                _ => throw new InvalidOperationException(
                    $"Unexpected fake Task Server request: {request.Method} {path}"),
            };

            if (path == $"/api/tasks/{lease.TaskKey}/files/prompt.md")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("Wait until the worker is terminated."),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(response, Json)),
            });
        }

        private RunnerClaimResponse Claim()
        {
            if (Volatile.Read(ref _releaseSeen) != 0)
            {
                Interlocked.Increment(ref _claimPollsAfterRelease);
                PostReleasePoll.TrySetResult();
            }

            return Interlocked.Increment(ref _claimCount) == 1
                ? new RunnerClaimResponse(
                    RunnerClaimStatus.Claimed,
                    lease.TaskKey,
                    lease.TaskKey,
                    "Fixture project",
                    lease)
                : new RunnerClaimResponse(RunnerClaimStatus.Empty);
        }

        private RunLeaseResponse Release()
        {
            Volatile.Write(ref _releaseSeen, 1);
            return new RunLeaseResponse("Released", false, lease);
        }
    }
}
