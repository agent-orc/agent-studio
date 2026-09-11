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

        ReviewDrainGuard.RequestDrain(options.StateDir, "release promotion");
        await run.WaitAsync(TimeSpan.FromSeconds(30));
        var claimsAtDrain = server.ClaimAttempts;
        await Task.Delay(50);

        Assert.Equal(claimsAtDrain, server.ClaimAttempts);
        Assert.Contains(logs, line =>
            line.Contains("review daemon draining", StringComparison.Ordinal)
            && line.Contains("release promotion", StringComparison.Ordinal));
        Assert.Contains(logs, line => line.Contains("review daemon drained", StringComparison.Ordinal));
        // The marker belongs to the generation that was asked to stop: the
        // replacement must claim again.
        Assert.NotNull(ReviewDrainGuard.ReadDrainRequest(options.StateDir));
        await daemon.RunAsync(new CancellationTokenSource(TimeSpan.FromMilliseconds(400)).Token);
        Assert.Null(ReviewDrainGuard.ReadDrainRequest(options.StateDir));
    }

    private RunnerOptions Options() => new()
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
    };

    /// <summary>A review plane that registers the executor and never has work.</summary>
    private sealed class IdleReviewPlane : HttpMessageHandler
    {
        private static readonly DateTime Now = new(2026, 9, 7, 3, 10, 0, DateTimeKind.Utc);
        private int _claimAttempts;

        public int ClaimAttempts => Volatile.Read(ref _claimAttempts);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.EndsWith("/review-claims", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _claimAttempts);
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
}
