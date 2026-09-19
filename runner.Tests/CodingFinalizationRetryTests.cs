using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRunner;
using AgentStudio.TestSupport;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// AGT-2869: a result transfer that fails because the Task Server is restarting
/// is retried from the persisted slot by the running daemon, not abandoned until
/// an operator restarts the service.
///
/// <para>
/// The fixture reproduces the reported incident shape: the worker reaches a
/// terminal result, the very next Task Server call dies with a transport fault,
/// and the daemon keeps polling. What used to happen then was "slot failed:
/// HttpRequestException" and nine minutes of nothing.
/// </para>
/// </summary>
[Trait("Category", "MachineBound")]
[Trait("Category", "ReviewFlaky")]
public sealed class CodingFinalizationRetryTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"coding-finalization-retry-{Guid.NewGuid():N}");

    [SkippableFact]
    [Trait(PlatformGate.TraitName, PlatformGate.Linux)]
    public async Task Transport_failure_during_transfer_keeps_the_slot_finalizing_and_the_poll_loop_redrives_it()
    {
        PlatformGate.LinuxOnly("the detached worker result is verified through /proc");

        var origin = Path.Combine(_root, "origin.git");
        await CreateOriginAsync(origin, Path.Combine(_root, "seed"));
        var options = Options(origin);
        var lease = Lease(options);
        var server = new RestartingTaskServer(lease, refuseCompletions: 1);
        var logs = new ConcurrentQueue<string>();

        using var client = Client(server, options);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var daemon = new RemoteRunnerDaemon(options, client, logs.Enqueue);
        var run = daemon.RunAsync(stop.Token);

        await WaitForLogAsync(
            logs,
            line => line.Contains("coding-finalization-deferred", StringComparison.Ordinal)
                    && line.Contains($"task={lease.TaskKey}", StringComparison.Ordinal),
            stop.Token);

        // The persisted slot is the whole recovery contract: phase, retry
        // bookkeeping and the attempt identity survive the failed transfer.
        var deferred = Assert.Single(new RunnerStateStore(options.StateDir).LoadAll());
        Assert.Equal("finalizing", deferred.Phase);
        Assert.Equal(FinalizationRetryPolicy.ResultReadyStage, deferred.FinalizationStage);
        Assert.NotNull(deferred.Finalization);
        Assert.Equal(1, deferred.Finalization!.Attempts);
        Assert.False(string.IsNullOrWhiteSpace(deferred.Finalization.LastReason));
        Assert.Equal(0, server.CompletionCount);
        // The delivery was already secured before completion failed. The retry
        // uses the persisted teardown facts and does not need the worktree.
        Assert.True(File.Exists(Path.Combine(
            options.WorkDir, "tasks", lease.TaskKey, "results", "deliverables.md")));
        Assert.NotNull(deferred.Finalization.Teardown);
        Assert.False(string.IsNullOrWhiteSpace(deferred.Finalization.Teardown!.ResultSha));

        // Fast-forward the durable backoff instead of sleeping through it. The
        // schedule is read back from disk, so this is the same decision the
        // daemon makes 15 s later.
        new RunnerStateStore(options.StateDir).Save(deferred with
        {
            Finalization = deferred.Finalization with { NextAttemptAtUtc = DateTime.UtcNow },
        });

        await WaitForLogAsync(
            logs,
            line => line.Contains("coding-slot-reconciliation scope=poll", StringComparison.Ordinal)
                    && line.Contains("outcome=redriven", StringComparison.Ordinal)
                    && line.Contains($"task={lease.TaskKey}", StringComparison.Ordinal),
            stop.Token);
        await AwaitCompletionAsync(server, logs, TimeSpan.FromSeconds(45));

        await stop.CancelAsync();
        try { await run.WaitAsync(TimeSpan.FromSeconds(20)); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }

        // Idempotence: both transfer attempts present the same fenced
        // idempotency key, and exactly one completion is recorded.
        Assert.Equal(2, server.CompletionIdempotencyKeys.Count);
        Assert.Single(server.CompletionIdempotencyKeys.Distinct(StringComparer.Ordinal));
        Assert.Equal(1, server.CompletionCount);
        Assert.DoesNotContain("none", server.ResultShas);
        Assert.Contains(logs, line => line.Contains("finalizationRetries=1", StringComparison.Ordinal));
        Assert.Contains(logs, line => line.Contains("coding-finalization-redrive", StringComparison.Ordinal)
            && line.Contains("retry=1", StringComparison.Ordinal));
        Assert.Empty(new RunnerStateStore(options.StateDir).LoadAll());
    }

    [SkippableFact]
    [Trait(PlatformGate.TraitName, PlatformGate.Linux)]
    public async Task Transport_failure_before_the_durable_result_uses_the_existing_failure_path()
    {
        PlatformGate.LinuxOnly("the runner fixture uses the Linux detached-worker boundary");

        var origin = Path.Combine(_root, "origin.git");
        await CreateOriginAsync(origin, Path.Combine(_root, "seed"));
        var options = Options(origin);
        var lease = Lease(options);
        var server = new RestartingTaskServer(
            lease,
            refuseCompletions: 0,
            refusePromptReads: 1);
        var logs = new ConcurrentQueue<string>();

        using var client = Client(server, options);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var daemon = new RemoteRunnerDaemon(options, client, logs.Enqueue);
        var run = daemon.RunAsync(stop.Token);

        await WaitForLogAsync(
            logs,
            line => line.Contains("slot failed:", StringComparison.Ordinal)
                    && line.Contains("ResponseEnded", StringComparison.Ordinal),
            stop.Token);

        await stop.CancelAsync();
        try { await run.WaitAsync(TimeSpan.FromSeconds(20)); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }

        Assert.DoesNotContain(
            logs,
            line => line.Contains("coding-finalization-deferred", StringComparison.Ordinal)
                    || line.Contains("coding-finalization-redrive", StringComparison.Ordinal));
        Assert.Empty(new RunnerStateStore(options.StateDir).LoadAll());
        Assert.Equal(1, server.ReleaseCount);
        Assert.Equal(0, server.CompletionCount);
    }

    [SkippableFact]
    [Trait(PlatformGate.TraitName, PlatformGate.Linux)]
    public async Task Startup_reconciliation_still_delivers_a_slot_whose_daemon_died_mid_retry()
    {
        PlatformGate.LinuxOnly("the detached worker result is verified through /proc");

        var origin = Path.Combine(_root, "origin.git");
        await CreateOriginAsync(origin, Path.Combine(_root, "seed"));
        var options = Options(origin);
        var lease = Lease(options);
        var server = new RestartingTaskServer(lease, refuseCompletions: 1);
        var logs = new ConcurrentQueue<string>();

        using (var firstClient = Client(server, options))
        using (var stopFirst = new CancellationTokenSource(TimeSpan.FromSeconds(90)))
        {
            var firstDaemon = new RemoteRunnerDaemon(options, firstClient, logs.Enqueue);
            var firstRun = firstDaemon.RunAsync(stopFirst.Token);
            await WaitForLogAsync(
                logs,
                line => line.Contains("coding-finalization-deferred", StringComparison.Ordinal),
                stopFirst.Token);

            // The daemon dies while the finalization is still waiting.
            await stopFirst.CancelAsync();
            try { await firstRun.WaitAsync(TimeSpan.FromSeconds(20)); }
            catch (OperationCanceledException) when (stopFirst.IsCancellationRequested) { }
        }

        var retained = Assert.Single(new RunnerStateStore(options.StateDir).LoadAll());
        Assert.Equal("finalizing", retained.Phase);
        Assert.Equal(FinalizationRetryPolicy.ResultReadyStage, retained.FinalizationStage);
        Assert.NotNull(retained.Finalization);

        // A pre-stage slot remains a valid startup-reconciliation input. Remove
        // the additive property to reproduce the exact JSON an older runner
        // persisted, then prove the replacement daemon still adopts it.
        var slotPath = Assert.Single(Directory.EnumerateFiles(options.StateDir, "*.slot.json"));
        var legacyJson = JsonNode.Parse(await File.ReadAllTextAsync(slotPath))!.AsObject();
        Assert.True(legacyJson.Remove("finalizationStage"));
        await File.WriteAllTextAsync(slotPath, legacyJson.ToJsonString(Json));
        Assert.Null(Assert.Single(new RunnerStateStore(options.StateDir).LoadAll()).FinalizationStage);

        using var replacementClient = Client(server, options);
        using var stopReplacement = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var replacement = new RemoteRunnerDaemon(options, replacementClient, logs.Enqueue);
        var replacementRun = replacement.RunAsync(stopReplacement.Token);

        await WaitForLogAsync(
            logs,
            line => line.Contains("coding-slot-reconciliation scope=startup", StringComparison.Ordinal)
                    && line.Contains("outcome=reattached", StringComparison.Ordinal),
            stopReplacement.Token);
        await AwaitCompletionAsync(server, logs, TimeSpan.FromSeconds(45));

        await stopReplacement.CancelAsync();
        try { await replacementRun.WaitAsync(TimeSpan.FromSeconds(20)); }
        catch (OperationCanceledException) when (stopReplacement.IsCancellationRequested) { }

        Assert.Equal(1, server.CompletionCount);
        Assert.Contains(logs, line => line.Contains("finalizationRetries=1", StringComparison.Ordinal));
        Assert.Contains(logs, line => line.Contains("coding-finalization-redrive", StringComparison.Ordinal)
            && line.Contains("retry=1", StringComparison.Ordinal));
        Assert.Empty(new RunnerStateStore(options.StateDir).LoadAll());
    }

    [SkippableFact]
    [Trait(PlatformGate.TraitName, PlatformGate.Linux)]
    public async Task Artifact_413_after_completion_is_partial_and_does_not_fail_the_slot()
    {
        PlatformGate.LinuxOnly("the detached worker and Git delivery use Linux process boundaries");

        var origin = Path.Combine(_root, "origin-413.git");
        await CreateOriginAsync(origin, Path.Combine(_root, "seed-413"));
        var options = Options(origin);
        var lease = Lease(options) with
        {
            TaskKey = "AGT-ARTIFACT-413",
            LeaseId = "lease-artifact-413",
            AttemptId = "attempt-artifact-413",
        };
        var server = new RestartingTaskServer(
            lease,
            refuseCompletions: 0,
            artifactResponseStatus: HttpStatusCode.RequestEntityTooLarge);
        var logs = new ConcurrentQueue<string>();

        using var client = Client(server, options);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var daemon = new RemoteRunnerDaemon(options, client, logs.Enqueue);
        var run = daemon.RunAsync(stop.Token);

        await AwaitCompletionAsync(server, logs, TimeSpan.FromSeconds(45));
        await WaitForLogAsync(
            logs,
            line => line.Contains("artifact-transfer", StringComparison.Ordinal)
                    && line.Contains("artifacts=partial", StringComparison.Ordinal),
            stop.Token);

        await stop.CancelAsync();
        try { await run.WaitAsync(TimeSpan.FromSeconds(20)); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }

        Assert.Equal(1, server.CompletionCount);
        Assert.DoesNotContain("none", server.ResultShas);
        var requests = server.RequestOrder.ToList();
        Assert.True(
            requests.IndexOf("/api/runner/completion") < requests.IndexOf("/api/runner/artifacts"),
            string.Join(Environment.NewLine, requests));
        Assert.Contains(logs, line => line.Contains("outcome=ArtifactTooLarge", StringComparison.Ordinal));
        Assert.DoesNotContain(logs, line => line.Contains("slot failed", StringComparison.Ordinal));
    }

    private RunnerOptions Options(string origin) => new()
    {
        ServerUrl = "http://task-server",
        RunnerId = "finalization-retry-runner",
        RunnerName = "Finalization retry runner",
        Hostname = "fixture-host",
        BackendName = "fake-task-server",
        GitRemote = origin,
        GitPushRemote = origin,
        WorkDir = Path.Combine(_root, "work"),
        StateDir = Path.Combine(_root, "state"),
        BaseBranch = "main",
        ExecEngine = RunnerOptions.ExecEngineLegacy,
        CliBin = PosixShell.RequirePath(),
        // The worker writes a real deliverable and a real source change, so the
        // retry has both an artifact transfer and a salvage push to repeat.
        CliArgs =
            "-c \"printf 'delivered\\n' > delivered.txt; "
            + "printf 'delivered\\n' > $JOB_RESULTS_DIR/deliverables.md; "
            + "printf 'delivered\\n[[TASK_DONE]]\\n'\"",
        TtlSeconds = 120,
        HeartbeatSeconds = 30,
        HandoffLeaseTtlSeconds = 300,
        RunTimeoutSeconds = 120,
        HostMaxParallelism = 1,
        PollSeconds = 1,
        ServerRequestTimeoutSeconds = 5,
        IdleWatchdogMinutes = 5,
    };

    private static RunLeaseInfoDto Lease(RunnerOptions options) => new(
        "AGT-FINALIZATION-RETRY",
        options.RunnerId,
        options.RunnerName,
        options.Hostname,
        Environment.ProcessId,
        options.BackendName,
        "lease-finalization-retry",
        11,
        DateTime.UtcNow,
        DateTime.UtcNow.AddSeconds(options.TtlSeconds),
        "attempt-finalization-retry",
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

    private static async Task AwaitCompletionAsync(
        RestartingTaskServer server,
        ConcurrentQueue<string> logs,
        TimeSpan timeout)
    {
        try
        {
            await server.Completion.Task.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            throw new Xunit.Sdk.XunitException(
                "No completion reached the fake Task Server. Daemon journal:\n"
                + string.Join("\n", logs));
        }
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
        throw new Xunit.Sdk.XunitException(
            "The expected journal line never appeared. Daemon journal:\n"
            + string.Join("\n", logs));
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

    public void Dispose() => ResilientDirectory.TryDelete(_root);

    /// <summary>
    /// Answers every route the coding daemon needs, but drops the first
    /// <c>refuseCompletions</c> completion responses the way a Task Server that
    /// is being stopped mid-response does. <c>/api/system/about</c> keeps
    /// answering, which is exactly the "the server is back" signal the retry
    /// waits for.
    /// </summary>
    private sealed class RestartingTaskServer(
        RunLeaseInfoDto initialLease,
        int refuseCompletions,
        int refusePromptReads = 0,
        HttpStatusCode? artifactResponseStatus = null) : HttpMessageHandler
    {
        private readonly object _gate = new();
        private int _claimCount;
        private int _artifactCount;
        private int _promptReadCount;
        private int _releaseCount;

        public TaskCompletionSource Completion { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> ArtifactIdempotencyKeys { get; } = [];
        public List<string> CompletionIdempotencyKeys { get; } = [];
        public List<string> ResultShas { get; } = [];
        public ConcurrentQueue<string> RequestOrder { get; } = new();
        public int CompletionCount { get; private set; }
        public int ReleaseCount => Volatile.Read(ref _releaseCount);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            RequestOrder.Enqueue(path);
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            if (path == $"/api/tasks/{initialLease.TaskKey}/files/prompt.md")
            {
                if (Interlocked.Increment(ref _promptReadCount) <= refusePromptReads)
                {
                    throw new HttpRequestException(
                        "The response ended prematurely. (ResponseEnded)",
                        null,
                        HttpStatusCode.ServiceUnavailable);
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("Deliver the result."),
                };
            }

            if (path == "/api/runner/artifacts" && artifactResponseStatus is { } rejectedStatus)
            {
                _ = Artifacts(body);
                return new HttpResponseMessage(rejectedStatus)
                {
                    Content = new StringContent("artifact payload refused"),
                };
            }

            object? response = path switch
            {
                "/api/system/about" => new { application = "task-server", version = "0.8.0" },
                "/api/clients/register" => new ClientRegisterResponse(
                    "finalization-retry-client",
                    "Finalization retry runner",
                    "service"),
                "/api/clients/finalization-retry-client/runner-git-capability" => new { },
                "/api/runner/project-chat/claim" => new RemoteChatWorkClaimResponse(
                    RemoteChatWorkClaimStatuses.Empty),
                "/api/runner/claim" => Claim(),
                "/api/runner/lease/renew" => Renew(body),
                "/api/runner/logs" => new LogIngestResponse(initialLease.TaskKey, 2),
                "/api/runner/artifacts" => Artifacts(body),
                "/api/runner/completion" => Complete(body),
                "/api/runner/lease/release" => Release(),
                _ => throw new InvalidOperationException(
                    $"Unexpected fake Task Server request: {request.Method} {path}"),
            };

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(response, Json),
                    Encoding.UTF8,
                    "application/json"),
            };
        }

        private RunnerClaimResponse Claim()
            => Interlocked.Increment(ref _claimCount) == 1
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
            return new RunLeaseResponse("Renewed", true, initialLease with
            {
                ExpiresAt = DateTime.UtcNow.AddSeconds(request.RequestedTtlSeconds ?? 120),
                LastHeartbeatAt = DateTime.UtcNow,
            });
        }

        private RunLeaseResponse Release()
        {
            Interlocked.Increment(ref _releaseCount);
            return new RunLeaseResponse("Released", false, initialLease);
        }

        private ArtifactIngestResponse Artifacts(string body)
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var paths = root.TryGetProperty("artifacts", out var artifacts)
                ? artifacts.EnumerateArray()
                    .Select(artifact => artifact.GetProperty("path").GetString() ?? string.Empty)
                    .ToList()
                : [];
            lock (_gate) ArtifactIdempotencyKeys.Add(Text(root, "idempotencyKey"));
            Interlocked.Increment(ref _artifactCount);

            return new ArtifactIngestResponse(
                initialLease.TaskKey,
                paths.Count,
                paths,
                ResultDocumentGenerated: true,
                ResultDocumentStatus: "generated");
        }

        private RemoteRunCompletionResponse Complete(string body)
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            lock (_gate)
            {
                CompletionIdempotencyKeys.Add(Text(root, "idempotencyKey"));
                ResultShas.Add(Text(root, "resultSha"));
            }
            if (CompletionIdempotencyKeys.Count <= refuseCompletions)
                throw new HttpRequestException(
                    "The response ended prematurely. (ResponseEnded)",
                    null,
                    HttpStatusCode.ServiceUnavailable);
            CompletionCount++;
            Completion.TrySetResult();
            return new RemoteRunCompletionResponse(
                TaskKey: initialLease.TaskKey,
                Outcome: Text(root, "outcome"),
                TargetState: "4-auto-review",
                Message: "remote-runner");
        }

        private static string Text(JsonElement root, string property)
            => root.TryGetProperty(property, out var value)
               && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? "none"
                : "none";
    }
}
