using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using AgentRunner;
using AgentStudio.TestSupport;
using Xunit;

namespace AgentRunner.Tests;

[Trait("Category", "MachineBound")]
[Trait("Category", "ReviewFlaky")]
public sealed class CodingDaemonRestartTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"coding-daemon-restart-{Guid.NewGuid():N}");
    private Process? _worker;

    [SkippableFact]
    [Trait(PlatformGate.TraitName, PlatformGate.Linux)]
    public async Task Planned_restart_keeps_detached_worker_and_reattaches_before_output_continues()
    {
        PlatformGate.LinuxOnly("the restart proof verifies the detached PID through /proc");

        var origin = Path.Combine(_root, "origin.git");
        await CreateOriginAsync(origin, Path.Combine(_root, "seed"));
        var continueFile = Path.Combine(_root, "continue");
        var options = Options(origin, continueFile);
        var lease = Lease(options);
        var server = new CodingRestartServer(lease, options.HandoffLeaseTtlSeconds);
        var logs = new ConcurrentQueue<string>();

        using (var firstClient = Client(server, options))
        using (var stopFirst = new CancellationTokenSource(TimeSpan.FromSeconds(45)))
        {
            var firstDaemon = new RemoteRunnerDaemon(options, firstClient, logs.Enqueue);
            var firstRun = firstDaemon.RunAsync(stopFirst.Token);
            var slot = await WaitForWorkerAsync(options.StateDir, stopFirst.Token);
            _worker = Process.GetProcessById(slot.ProcessId!.Value);
            await WaitForOutputAsync(slot, "before-restart", stopFirst.Token);

            await stopFirst.CancelAsync();
            await firstRun.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.False(_worker.HasExited);
            Assert.Contains(logs, line => line.Contains(
                "coding handoff lease extended",
                StringComparison.Ordinal));
            Assert.Contains(logs, line => line.Contains(
                "coding daemon handoff",
                StringComparison.Ordinal));
            Assert.Equal(options.HandoffLeaseTtlSeconds, server.LastHandoffTtl);
        }

        using var replacementClient = Client(server, options);
        using var stopReplacement = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var replacement = new RemoteRunnerDaemon(options, replacementClient, logs.Enqueue);
        var replacementRun = replacement.RunAsync(stopReplacement.Token);

        await WaitForLogAsync(
            logs,
            line => line.Contains("coding-slot-reconciliation", StringComparison.Ordinal)
                    && line.Contains("outcome=reattached", StringComparison.Ordinal),
            stopReplacement.Token);

        Assert.False(_worker.HasExited);
        var reattachedSlot = Assert.Single(
            new RunnerStateStore(options.StateDir).LoadAll());
        Assert.Equal(_worker.Id, reattachedSlot.ProcessId);
        await WaitForLogAsync(
            logs,
            line => line.Contains("recovered 1 persisted slot(s)", StringComparison.Ordinal),
            stopReplacement.Token);
        await File.WriteAllTextAsync(continueFile, "continue", stopReplacement.Token);
        await server.Completion.Task.WaitAsync(TimeSpan.FromSeconds(30));

        await stopReplacement.CancelAsync();
        await replacementRun.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Contains("after-restart", server.LogBodies.ToString());
        Assert.Empty(new RunnerStateStore(options.StateDir).LoadAll());
    }

    [Fact]
    public async Task Startup_logs_a_purge_reason_for_each_dead_persisted_coding_slot()
    {
        var origin = Path.Combine(_root, "dead-origin.git");
        await CreateOriginAsync(origin, Path.Combine(_root, "dead-seed"));
        var options = Options(origin, Path.Combine(_root, "unused-continue"));
        var lease = Lease(options) with
        {
            TaskKey = "AGT-DEAD-PERSISTED",
            LeaseId = "lease-dead-persisted",
            ExpiresAt = DateTime.UtcNow.AddMinutes(2),
        };
        var worktree = Path.Combine(options.WorkDir, "dead-worktree");
        Directory.CreateDirectory(worktree);
        new RunnerStateStore(options.StateDir).Create(
            lease.TaskKey,
            lease,
            worktree);
        var server = new CodingRestartServer(
            lease,
            options.HandoffLeaseTtlSeconds,
            offerClaim: false);
        var logs = new ConcurrentQueue<string>();
        using var client = Client(server, options);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var daemon = new RemoteRunnerDaemon(options, client, logs.Enqueue);
        var run = daemon.RunAsync(stop.Token);

        await WaitForLogAsync(
            logs,
            line => line.Contains("coding-slot-reconciliation", StringComparison.Ordinal)
                    && line.Contains("task=AGT-DEAD-PERSISTED", StringComparison.Ordinal)
                    && line.Contains("outcome=purged", StringComparison.Ordinal)
                    && line.Contains("reason=no persisted process identity", StringComparison.Ordinal),
            stop.Token);

        await stop.CancelAsync();
        try { await run.WaitAsync(TimeSpan.FromSeconds(15)); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        Assert.Empty(new RunnerStateStore(options.StateDir).LoadAll());
    }

    private RunnerOptions Options(string origin, string continueFile) => new()
    {
        ServerUrl = "http://task-server",
        RunnerId = "coding-restart-runner",
        RunnerName = "Coding restart runner",
        Hostname = "fixture-host",
        BackendName = "fake-task-server",
        GitRemote = origin,
        GitPushRemote = origin,
        WorkDir = Path.Combine(_root, "work"),
        StateDir = Path.Combine(_root, "state"),
        BaseBranch = "main",
        ExecEngine = RunnerOptions.ExecEngineLegacy,
        CliBin = PosixShell.RequirePath(),
        CliArgs = $"-c \"printf 'before-restart\\n'; while [ ! -f '{continueFile}' ]; do sleep 0.05; done; printf 'after-restart\\n[[TASK_DONE]]\\n'\"",
        // The ordinary lease is deliberately inside the startup safety margin.
        // Only a shutdown handoff renewal gives the replacement time to adopt.
        TtlSeconds = 4,
        HeartbeatSeconds = 5,
        HandoffLeaseTtlSeconds = 300,
        RunTimeoutSeconds = 60,
        HostMaxParallelism = 1,
        PollSeconds = 1,
        ServerRequestTimeoutSeconds = 2,
        IdleWatchdogMinutes = 1,
    };

    private static RunLeaseInfoDto Lease(RunnerOptions options) => new(
        "AGT-CODING-RESTART",
        options.RunnerId,
        options.RunnerName,
        options.Hostname,
        Environment.ProcessId,
        options.BackendName,
        "lease-coding-restart",
        7,
        DateTime.UtcNow,
        DateTime.UtcNow.AddSeconds(options.TtlSeconds),
        "attempt-coding-restart",
        1);

    private static TaskServerClient Client(HttpMessageHandler handler, RunnerOptions options)
    {
        var http = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = new Uri(options.ServerUrl),
            Timeout = Timeout.InfiniteTimeSpan,
        };
        return new TaskServerClient(http, options.RunnerId, options: options);
    }

    private static async Task<PersistedRunnerSlot> WaitForWorkerAsync(
        string stateDirectory,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var slot = new RunnerStateStore(stateDirectory).LoadAll().SingleOrDefault();
            if (slot?.ProcessId is > 0
                && DurableAgentProcess.VerifyLive(slot, out _))
                return slot;
            await Task.Delay(50, cancellationToken);
        }
        throw new OperationCanceledException(cancellationToken);
    }

    private static async Task WaitForOutputAsync(
        PersistedRunnerSlot slot,
        string expected,
        CancellationToken cancellationToken)
    {
        var process = DurableAgentProcess.Attach(slot);
        while (!cancellationToken.IsCancellationRequested)
        {
            if (process.ReadAfter(0).Any(line => line.Text == expected)) return;
            await Task.Delay(50, cancellationToken);
        }
        throw new OperationCanceledException(cancellationToken);
    }

    private static async Task WaitForLogAsync(
        ConcurrentQueue<string> logs,
        Func<string, bool> predicate,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (logs.Any(predicate)) return;
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
    {
        if (_worker is { HasExited: false })
        {
            try { _worker.Kill(entireProcessTree: true); }
            catch { }
        }
        _worker?.Dispose();
        ResilientDirectory.TryDelete(_root);
    }

    private sealed class CodingRestartServer(
        RunLeaseInfoDto initialLease,
        int handoffTtl,
        bool offerClaim = true) : HttpMessageHandler
    {
        private readonly object _gate = new();
        private int _claimCount;

        public int? LastHandoffTtl { get; private set; }
        public StringBuilder LogBodies { get; } = new();
        public TaskCompletionSource Completion { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            object? response = path switch
            {
                "/api/clients/register" => new ClientRegisterResponse(
                    "coding-restart-client",
                    "Coding restart runner",
                    "service"),
                "/api/clients/coding-restart-client/runner-git-capability" => new { },
                "/api/runner/project-chat/claim" => new RemoteChatWorkClaimResponse(
                    RemoteChatWorkClaimStatuses.Empty),
                "/api/runner/claim" => Claim(),
                "/api/runner/lease/renew" => Renew(body),
                "/api/runner/logs" => AcceptLogs(body),
                "/api/runner/artifacts" => new ArtifactIngestResponse(
                    initialLease.TaskKey,
                    0,
                    [],
                    ResultDocumentGenerated: true,
                    ResultDocumentStatus: "generated"),
                "/api/runner/completion" => Complete(),
                "/api/runner/lease/release" => new RunLeaseResponse(
                    "Released",
                    false,
                    initialLease),
                _ when path == $"/api/tasks/{initialLease.TaskKey}/files/prompt.md" => null,
                _ => throw new InvalidOperationException(
                    $"Unexpected fake Task Server request: {request.Method} {path}"),
            };

            if (path == $"/api/tasks/{initialLease.TaskKey}/files/prompt.md")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("Continue across the daemon restart."),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(response, Json),
                    Encoding.UTF8,
                    "application/json"),
            };
        }

        private RunnerClaimResponse Claim()
            => offerClaim && Interlocked.Increment(ref _claimCount) == 1
                ? new RunnerClaimResponse(
                    RunnerClaimStatus.Claimed,
                    initialLease.TaskKey,
                    initialLease.TaskKey,
                    "Fixture project",
                    initialLease)
                : new RunnerClaimResponse(RunnerClaimStatus.Empty);

        private RunLeaseResponse Renew(string body)
        {
            var request = JsonSerializer.Deserialize<RunLeaseHeartbeatRequest>(body, Json)
                          ?? throw new InvalidDataException("Missing lease renewal body.");
            var ttl = request.RequestedTtlSeconds ?? 120;
            if (ttl == handoffTtl) LastHandoffTtl = ttl;
            var renewed = initialLease with
            {
                ExpiresAt = DateTime.UtcNow.AddSeconds(ttl),
                LastHeartbeatAt = DateTime.UtcNow,
            };
            return new RunLeaseResponse("Renewed", true, renewed);
        }

        private LogIngestResponse AcceptLogs(string body)
        {
            lock (_gate) LogBodies.Append(body);
            return new LogIngestResponse(initialLease.TaskKey, 2);
        }

        private RemoteRunCompletionResponse Complete()
        {
            Completion.TrySetResult();
            // The shape the endpoint being faked actually returns; the runner's
            // own duplicate external-completion wire records are gone (AGT-2820).
            return new RemoteRunCompletionResponse(
                TaskKey: initialLease.TaskKey,
                Outcome: "Done",
                TargetState: "4-auto-review",
                Message: "remote-runner");
        }
    }
}
