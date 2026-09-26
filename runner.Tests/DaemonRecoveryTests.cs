using System.Net;
using System.Text.Json;
using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

public sealed class DaemonRecoveryTests
{
    [Fact]
    public async Task Capability_conflict_reregisters_the_same_identity_and_retries_advertisement()
    {
        var requests = new List<(HttpMethod Method, string Path)>();
        var advertisements = 0;
        var now = DateTime.UtcNow;
        var handler = new DelegatingHandlerStub((request, _) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            requests.Add((request.Method, path));
            if (path.EndsWith("/capabilities", StringComparison.Ordinal)
                && ++advertisements == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
                {
                    Content = new StringContent(
                        "Register this runner identity before advertising capabilities."),
                });
            }

            var content = request.Method == HttpMethod.Put
                ? $$"""
                    {
                      "runnerId":"runner-recovery",
                      "name":"Runner recovery",
                      "hostId":"host-a",
                      "instanceId":"host-a:fixture",
                      "runnerVersion":"1.0.0",
                      "protocolVersion":{{TaskServerProtocol.Current}},
                      "status":"active",
                      "registeredAt":"{{now:O}}",
                      "lastSeenAt":"{{now:O}}"
                    }
                    """
                : $$"""
                    {
                      "runnerId":"runner-recovery",
                      "name":"Runner recovery",
                      "hostId":"host-a",
                      "instanceId":"host-a:fixture",
                      "runnerVersion":"1.0.0",
                      "protocolVersion":{{TaskServerProtocol.Current}},
                      "status":"active",
                      "registeredAt":"{{now:O}}",
                      "lastSeenAt":"{{now:O}}",
                      "generation":7,
                      "capabilities":[],
                      "telemetry":null
                    }
                    """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content),
            });
        });
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://task-server"),
        };
        var options = Options();
        using var client = new TaskServerClient(
            http,
            options.RunnerId,
            usesDurableTaskServer: true,
            options: options,
            runnerInstanceId: "host-a:fixture");
        var logs = new List<string>();
        var connectivity = new TaskServerConnectivityMonitor(logs.Add);

        await CapabilityAdvertisementRecovery.ExecuteAsync(
            "capability advertisement",
            ct => client.AdvertiseCapabilitiesAsync(
                [new AdvertisedCapabilityDto(CapabilityProtocol.CodingExecutor, "executor")],
                telemetry: null,
                generation: 7,
                ct),
            async ct =>
            {
                _ = await client.RegisterAsync(options.RunnerName, "service", ct);
            },
            connectivity,
            () => 0,
            pollSeconds: 1,
            requestTimeout: TimeSpan.FromSeconds(5),
            logs.Add,
            CancellationToken.None);

        Assert.Equal(
            [
                (HttpMethod.Put, "/api/v1/runners/runner-recovery/capabilities"),
                (HttpMethod.Put, "/api/v1/runners/runner-recovery"),
                (HttpMethod.Put, "/api/v1/runners/runner-recovery/capabilities"),
            ],
            requests);
        Assert.Contains(logs, line => line.Contains("registration=lost", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    [Trait("Category", "ReviewFlaky")]
    public async Task Capability_post_obeys_the_configured_request_timeout()
    {
        var handler = new DelegatingHandlerStub(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The cancelled fake request unexpectedly resumed.");
        });
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://task-server"),
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var options = Options(serverRequestTimeoutSeconds: 1);
        using var client = new TaskServerClient(
            http,
            options.RunnerId,
            usesDurableTaskServer: true,
            options: options,
            runnerInstanceId: "host-a:fixture");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.AdvertiseCapabilitiesAsync(
                [new AdvertisedCapabilityDto(CapabilityProtocol.CodingExecutor, "executor")],
                telemetry: null,
                generation: 1,
                CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    [Trait("Category", "ReviewFlaky")]
    public async Task Worker_loss_release_retries_a_transient_server_failure()
    {
        var options = Options();
        var lease = new RunLeaseInfoDto(
            "AGT-RELEASE-RETRY",
            options.RunnerId,
            options.RunnerName,
            options.Hostname,
            Environment.ProcessId,
            options.BackendName,
            "lease-release-retry",
            3,
            DateTime.UtcNow,
            DateTime.UtcNow.AddMinutes(2),
            "attempt-release-retry");
        var store = new RunnerStateStore(options.StateDir);
        var slot = store.Create(
            lease.TaskKey,
            lease,
            Path.Combine(options.WorkDir, "release-retry-worktree"));
        var attempts = 0;
        var handler = new DelegatingHandlerStub((_, _) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("backend restarting"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(
                        new RunLeaseResponse("Released", false, lease),
                        new JsonSerializerOptions(JsonSerializerDefaults.Web))),
            });
        });
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://task-server"),
        };
        using var client = new TaskServerClient(http, options.RunnerId, options: options);
        var runner = new RemoteTaskRunner(options, client, _ => { }, store);

        var released = await runner.ReleaseDeadAsync(slot, "worker process was hard-killed");

        Assert.True(released);
        Assert.Equal(2, attempts);
        Assert.Empty(store.LoadAll());
        ResilientDirectory.TryDelete(options.StateDir);
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    [Trait("Category", "ReviewFlaky")]
    public async Task Slot_free_poll_stall_logs_fatal_and_cancels_the_daemon()
    {
        var logs = new List<string>();
        await using var watchdog = new DaemonIdleWatchdog(
            logs.Add,
            stallAfter: TimeSpan.FromMilliseconds(80),
            checkEvery: TimeSpan.FromMilliseconds(10));
        watchdog.RecordActiveSlots(0);

        await WaitForCancellationAsync(watchdog.AbortToken, TimeSpan.FromSeconds(2));

        Assert.True(watchdog.Tripped);
        Assert.Contains(logs, line =>
            line.Contains("daemon-idle-watchdog status=fatal", StringComparison.Ordinal)
            && line.Contains("activeSlots=0", StringComparison.Ordinal)
            && line.Contains("action=exit-for-service-restart", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    [Trait("Category", "ReviewFlaky")]
    public async Task Active_slot_suppresses_idle_watchdog_until_polling_can_resume()
    {
        await using var watchdog = new DaemonIdleWatchdog(
            _ => { },
            stallAfter: TimeSpan.FromMilliseconds(80),
            checkEvery: TimeSpan.FromMilliseconds(10));
        watchdog.RecordActiveSlots(1);

        await Task.Delay(150);

        Assert.False(watchdog.Tripped);
        watchdog.RecordPollStarted();
        watchdog.RecordActiveSlots(0);
        await Task.Delay(30);
        Assert.False(watchdog.Tripped);
    }

    private static RunnerOptions Options(int serverRequestTimeoutSeconds = 5) => new()
    {
        ServerUrl = "http://task-server",
        RunnerId = "runner-recovery",
        RunnerName = "Runner recovery",
        Hostname = "host-a",
        BackendName = "test",
        WorkDir = Path.GetTempPath(),
        StateDir = Path.Combine(Path.GetTempPath(), $"runner-recovery-{Guid.NewGuid():N}"),
        BaseBranch = "main",
        ClaudeCliBin = "claude",
        TtlSeconds = 120,
        HeartbeatSeconds = 30,
        RunTimeoutSeconds = 120,
        HostMaxParallelism = 1,
        PollSeconds = 1,
        ServerRequestTimeoutSeconds = serverRequestTimeoutSeconds,
    };

    private static async Task WaitForCancellationAsync(
        CancellationToken cancellationToken,
        TimeSpan timeout)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => completion.TrySetResult());
        await completion.Task.WaitAsync(timeout);
    }

    private sealed class DelegatingHandlerStub(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }
}
