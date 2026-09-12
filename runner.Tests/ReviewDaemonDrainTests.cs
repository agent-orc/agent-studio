using System.Net;
using System.Text.Json;
using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// The daemon half of the restart guard: a drain request stops claiming at once
/// and the daemon exits only when its slot set is empty, so a deploy never lands
/// on running gate work.
/// </summary>
public sealed class ReviewDaemonDrainTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "review-daemon-drain-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public async Task A_drain_request_stops_claiming_and_exits_the_daemon()
    {
        var server = new IdleReviewPlane();
        using var http = new HttpClient(server) { BaseAddress = new Uri("http://task-server") };
        var options = Options();
        using var client = new TaskServerClient(
            http, options.RunnerId, usesDurableTaskServer: true, options: options);
        var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var daemon = new RemoteReviewDaemon(
            options,
            client,
            logs.Enqueue,
            // Deterministic admission: an idle host that always admits, so a
            // stopped claim can only be the drain.
            (activeSlots, _) => new HostTelemetrySample(
                DateTime.UtcNow,
                CpuPercent: 1.0,
                Load1: 0.0,
                Load5: 0.0,
                Load15: 0.0,
                MemoryUsedBytes: null,
                MemoryTotalBytes: null,
                SwapInBytesPerSecond: null,
                SwapOutBytesPerSecond: null,
                CpuStealPercent: null,
                IoWaitPercent: null,
                CpuCores: Environment.ProcessorCount,
                ActiveSlots: activeSlots));

        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var run = daemon.RunAsync(shutdown.Token);
        while (server.ClaimAttempts == 0) await Task.Delay(20, shutdown.Token);

        var request = ReviewDrainGuard.RequestDrain(options.StateDir, "release promotion");
        await run.WaitAsync(TimeSpan.FromSeconds(30));
        var claimsAtDrain = server.ClaimAttempts;
        await Task.Delay(50);

        Assert.Equal(claimsAtDrain, server.ClaimAttempts);
        Assert.Contains(logs, line =>
            line.Contains("review daemon draining", StringComparison.Ordinal)
            && line.Contains("release promotion", StringComparison.Ordinal));
        Assert.Contains(logs, line => line.Contains("review daemon drained", StringComparison.Ordinal));
        var acknowledgement = ReviewDrainGuard.ReadDrainAcknowledgement(options.StateDir);
        Assert.Equal(request.RequestId, acknowledgement?.RequestId);
        Assert.Equal(0, acknowledgement?.ActiveSlots);
        // The marker remains authoritative until an explicit replacement
        // clears it. An accidental new generation must stop without claiming.
        Assert.NotNull(ReviewDrainGuard.ReadDrainRequest(options.StateDir));
        await daemon.RunAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);
        Assert.Equal(claimsAtDrain, server.ClaimAttempts);
        Assert.NotNull(ReviewDrainGuard.ReadDrainRequest(options.StateDir));
    }

    [Fact]
    public async Task Drain_waits_for_the_daemon_barrier_when_a_claim_is_in_flight()
    {
        var server = new IdleReviewPlane(blockFirstClaim: true);
        using var http = new HttpClient(server) { BaseAddress = new Uri("http://task-server") };
        var options = Options(drainTimeoutSeconds: 10);
        using var client = new TaskServerClient(
            http, options.RunnerId, usesDurableTaskServer: true, options: options);
        var daemon = new RemoteReviewDaemon(
            options,
            client,
            _ => { },
            (activeSlots, _) => new HostTelemetrySample(
                DateTime.UtcNow,
                CpuPercent: 1.0,
                Load1: 0.0,
                Load5: 0.0,
                Load15: 0.0,
                MemoryUsedBytes: null,
                MemoryTotalBytes: null,
                SwapInBytesPerSecond: null,
                SwapOutBytesPerSecond: null,
                CpuStealPercent: null,
                IoWaitPercent: null,
                CpuCores: Environment.ProcessorCount,
                ActiveSlots: activeSlots));

        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var run = daemon.RunAsync(shutdown.Token);
        await server.WaitForClaimAsync().WaitAsync(TimeSpan.FromSeconds(20));

        var drain = ReviewDrainCommand.RunDrainAsync(
            options,
            _ => { },
            CancellationToken.None);
        while (ReviewDrainGuard.ReadDrainRequest(options.StateDir) is null)
            await Task.Delay(10);

        await Task.Delay(100);
        Assert.False(drain.IsCompleted);
        Assert.Null(ReviewDrainGuard.ReadDrainAcknowledgement(options.StateDir));

        server.ReleaseClaim();

        Assert.Equal(0, await drain.WaitAsync(TimeSpan.FromSeconds(20)));
        await run.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(1, server.ClaimAttempts);
    }

    [Fact]
    public async Task A_restart_barrier_keeps_the_daemon_alive_and_stops_claims_until_withdrawn()
    {
        var server = new IdleReviewPlane();
        using var http = new HttpClient(server) { BaseAddress = new Uri("http://task-server") };
        var options = Options();
        using var client = new TaskServerClient(
            http, options.RunnerId, usesDurableTaskServer: true, options: options);
        var daemon = new RemoteReviewDaemon(
            options,
            client,
            _ => { },
            (activeSlots, _) => new HostTelemetrySample(
                DateTime.UtcNow,
                CpuPercent: 1.0,
                Load1: 0.0,
                Load5: 0.0,
                Load15: 0.0,
                MemoryUsedBytes: null,
                MemoryTotalBytes: null,
                SwapInBytesPerSecond: null,
                SwapOutBytesPerSecond: null,
                CpuStealPercent: null,
                IoWaitPercent: null,
                CpuCores: Environment.ProcessorCount,
                ActiveSlots: activeSlots));

        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var run = daemon.RunAsync(shutdown.Token);
        while (server.ClaimAttempts == 0) await Task.Delay(20, shutdown.Token);

        var request = ReviewDrainGuard.RequestRestartGuard(
            options.StateDir,
            "release replacement");
        ReviewDrainGuard.DrainAcknowledgement? acknowledgement;
        while (!string.Equals(
                   (acknowledgement = ReviewDrainGuard.ReadDrainAcknowledgement(options.StateDir))?.RequestId,
                   request.RequestId,
                   StringComparison.Ordinal))
        {
            await Task.Delay(20, shutdown.Token);
        }
        var claimsAtBarrier = server.ClaimAttempts;
        await Task.Delay(TimeSpan.FromSeconds(2), shutdown.Token);

        Assert.False(run.IsCompleted);
        Assert.Equal(0, acknowledgement!.ActiveSlots);
        Assert.Equal(claimsAtBarrier, server.ClaimAttempts);

        Assert.True(ReviewDrainGuard.WithdrawRequest(options.StateDir, request.RequestId));
        while (server.ClaimAttempts == claimsAtBarrier)
            await Task.Delay(20, shutdown.Token);
        shutdown.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task An_idle_daemon_acknowledges_drain_during_permanent_registration_failure()
    {
        var server = new UnavailableRegistrationPlane();
        using var http = new HttpClient(server) { BaseAddress = new Uri("http://task-server") };
        var options = Options(drainTimeoutSeconds: 5);
        using var client = new TaskServerClient(
            http, options.RunnerId, usesDurableTaskServer: true, options: options);
        var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var daemon = new RemoteReviewDaemon(options, client, logs.Enqueue);

        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = daemon.RunAsync(shutdown.Token);
        while (server.RegistrationAttempts == 0)
            await Task.Delay(20, shutdown.Token);

        var drain = ReviewDrainCommand.RunDrainAsync(
            options,
            logs.Enqueue,
            CancellationToken.None);

        Assert.Equal(0, await drain.WaitAsync(TimeSpan.FromSeconds(10)));
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(logs, line =>
            line.Contains("drained before Task Server startup", StringComparison.Ordinal));
    }

    private RunnerOptions Options(int drainTimeoutSeconds = 3600) => new()
    {
        ServerUrl = "http://task-server",
        RunnerId = "review-runner",
        RunnerName = "review-runner",
        Hostname = "review-host",
        BackendName = "test",
        Role = "review",
        WorkDir = Path.Combine(_root, "coding-work"),
        ReviewWorkDir = Path.Combine(_root, "review-work"),
        StateDir = Path.Combine(_root, "state"),
        BaseBranch = "main",
        CliBin = "test",
        CliArgs = "",
        TtlSeconds = 120,
        HeartbeatSeconds = 30,
        PollSeconds = 1,
        HostMaxParallelism = 2,
        IdleWatchdogMinutes = 60,
        DrainTimeoutSeconds = drainTimeoutSeconds,
        ServerRequestTimeoutSeconds = 1,
    };

    /// <summary>A review plane that registers the executor and never has work.</summary>
    private sealed class IdleReviewPlane(bool blockFirstClaim = false) : HttpMessageHandler
    {
        private static readonly DateTime Now = new(2026, 9, 7, 3, 10, 0, DateTimeKind.Utc);
        private readonly TaskCompletionSource _claimEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseClaim =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _claimAttempts;

        public int ClaimAttempts => Volatile.Read(ref _claimAttempts);
        public Task WaitForClaimAsync() => _claimEntered.Task;
        public void ReleaseClaim() => _releaseClaim.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.EndsWith("/review-claims", StringComparison.Ordinal))
            {
                var attempt = Interlocked.Increment(ref _claimAttempts);
                _claimEntered.TrySetResult();
                if (blockFirstClaim && attempt == 1)
                    await _releaseClaim.Task.WaitAsync(cancellationToken);
                return JsonResponse(new ReviewClaimResponse("empty", Message: "No queued review."));
            }
            if (path.EndsWith("/capabilities", StringComparison.Ordinal))
            {
                var advertisement = await ReadAsync<CapabilityAdvertisementRequest>(request);
                return JsonResponse(new RunnerCapabilitySnapshotDto(
                    advertisement.RunnerId, "review-runner", "review-host",
                    advertisement.InstanceId, "test", TaskServerProtocol.Current, "active",
                    Now, Now,
                    new RemoteHostAdmissionDto("review-host", "open", null, null, null, null),
                    [],
                    null));
            }
            if (request.Method == HttpMethod.Put && path.StartsWith("/api/v1/runners/", StringComparison.Ordinal))
            {
                var registration = await ReadAsync<RegisterRunnerRequest>(request);
                return JsonResponse(new RunnerDto(
                    "review-runner", registration.Name, registration.HostId,
                    registration.InstanceId, registration.RunnerVersion,
                    registration.ProtocolVersion, "active", Now, Now));
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($"unexpected idle review plane request: {path}"),
            };
        }

        private static async Task<T> ReadAsync<T>(HttpRequestMessage request)
            => JsonSerializer.Deserialize<T>(await request.Content!.ReadAsStringAsync(), Json)
               ?? throw new InvalidDataException($"Request body was not valid {typeof(T).Name} JSON.");

        private static HttpResponseMessage JsonResponse<T>(T value)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(value, Json)),
            };
    }

    private sealed class UnavailableRegistrationPlane : HttpMessageHandler
    {
        private int _registrationAttempts;

        public int RegistrationAttempts => Volatile.Read(ref _registrationAttempts);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Put
                && (request.RequestUri?.AbsolutePath ?? string.Empty)
                    .StartsWith("/api/v1/runners/", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _registrationAttempts);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("temporarily unavailable"),
            });
        }
    }
}
