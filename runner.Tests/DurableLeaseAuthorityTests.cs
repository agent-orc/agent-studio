using System.Net;
using System.Text;
using System.Text.Json;
using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

public sealed class DurableLeaseAuthorityTests
{
    [Fact]
    public async Task Confirmed_worker_start_is_carried_by_the_next_heartbeat()
    {
        using var temp = new TempDirectory();
        var now = new DateTime(2026, 9, 27, 8, 0, 0, DateTimeKind.Utc);
        var options = Options(temp.Path);
        var lease = Lease(now, now.AddMinutes(15));
        var handler = new CapturingRenewHandler(lease);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        using var client = new TaskServerClient(http, options.RunnerId);
        using var stop = new CancellationTokenSource();
        var heartbeat = new LeaseHeartbeat(
            client,
            options,
            lease,
            _ => { },
            (_, _) =>
            {
                stop.Cancel();
                return Task.CompletedTask;
            }, utcNow: () => now);
        var promptHash = new string('a', 64);
        heartbeat.ConfirmWorkerStartedWithPrompt(promptHash);

        await heartbeat.RunAsync(stop, CancellationToken.None);

        Assert.Equal(promptHash, handler.Request!.StartedPromptSha256);
    }

    [Fact]
    public async Task Granted_renewal_delivers_the_fenced_stop_without_losing_authority()
    {
        using var temp = new TempDirectory();
        var now = DateTime.UtcNow;
        var options = Options(temp.Path);
        var lease = Lease(now, now.AddMinutes(5)) with { AttemptId = "attempt-stop", AuthorityEpoch = 3 };
        var directive = new RunStopDirectiveDto(lease.TaskKey, "followup", now,
            lease.AttemptId, "operator", "stop-command", lease.FencingToken);
        using var http = new HttpClient(new StopRenewalHandler(lease, directive))
        {
            BaseAddress = new Uri("http://localhost"),
        };
        using var client = new TaskServerClient(http, options.RunnerId);
        using var stop = new CancellationTokenSource();
        // The lease is dated 2026-09-27; without an injected clock the renewal
        // answer arrives "after the local autonomy deadline" on any later day and
        // the heartbeat reports the lease lost (the test turned red on 27.09.).
        var heartbeat = new LeaseHeartbeat(client, options, lease, _ => { }, utcNow: () => now);

        await heartbeat.RunAsync(stop, CancellationToken.None);

        Assert.True(stop.IsCancellationRequested);
        Assert.False(heartbeat.LeaseLost);
        Assert.Equal("stop-command", heartbeat.StopRequest?.CommandId);
        Assert.Equal(lease.FencingToken, heartbeat.StopRequest?.FencingToken);
    }

    [Fact]
    public async Task Controlled_time_keeps_the_generation_alive_for_ten_minutes_and_stops_before_expiry()
    {
        using var temp = new TempDirectory();
        var now = new DateTime(2026, 7, 29, 8, 0, 0, DateTimeKind.Utc);
        var options = Options(temp.Path);
        var lease = Lease(now, expiresAt: now.AddMinutes(15));
        var authority = DurableLeaseAuthority.Open(
            temp.Path,
            lease.ExpiresAt,
            TimeSpan.FromMinutes(1),
            initiallyConfirmed: true,
            () => now);
        using var http = new HttpClient(new OfflineHandler())
        {
            BaseAddress = new Uri("http://localhost"),
        };
        using var client = new TaskServerClient(http, options.RunnerId);
        using var stop = new CancellationTokenSource();
        var observed = new List<DateTime>();
        var heartbeat = new LeaseHeartbeat(
            client,
            options,
            lease,
            _ => { },
            (delay, _) =>
            {
                now += delay;
                observed.Add(now);
                return Task.CompletedTask;
            },
            authority: authority,
            utcNow: () => now);

        await heartbeat.RunAsync(stop, CancellationToken.None);

        Assert.Contains(observed, value => value >= lease.AcquiredAt.AddMinutes(10));
        Assert.Equal(lease.ExpiresAt.AddMinutes(-1), authority.StopBeforeUtc);
        Assert.Equal(authority.StopBeforeUtc, now);
        Assert.True(heartbeat.LeaseLost);
        Assert.Equal("rejected", authority.Snapshot.State);
        Assert.Contains("deadline exhausted", authority.Snapshot.Detail);
    }

    [Fact]
    public async Task Resume_after_workstation_sleep_stops_before_another_renewal_or_replay()
    {
        using var temp = new TempDirectory();
        var now = new DateTime(2026, 9, 27, 8, 0, 0, DateTimeKind.Utc);
        var options = Options(temp.Path);
        var lease = Lease(now, now.AddMinutes(15));
        var authority = DurableLeaseAuthority.Open(
            temp.Path, lease.ExpiresAt, TimeSpan.FromMinutes(1), true, () => now);
        var offline = new OfflineHandler();
        using var http = new HttpClient(offline) { BaseAddress = new Uri("http://localhost") };
        using var client = new TaskServerClient(http, options.RunnerId);
        using var stop = new CancellationTokenSource();
        var heartbeat = new LeaseHeartbeat(
            client, options, lease, _ => { },
            (_, _) =>
            {
                now = now.AddMinutes(20);
                return Task.CompletedTask;
            },
            authority: authority,
            utcNow: () => now);

        await heartbeat.RunAsync(stop, CancellationToken.None);

        Assert.Equal(1, offline.Calls);
        Assert.True(heartbeat.LeaseLost);
        Assert.False(authority.ReplayAllowed);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => authority.WaitForConfirmedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Delayed_renewal_cannot_restore_authority_after_the_previous_stop_before_deadline()
    {
        using var temp = new TempDirectory();
        var now = new DateTime(2026, 9, 27, 8, 0, 0, DateTimeKind.Utc);
        var options = Options(temp.Path);
        var lease = Lease(now, now.AddMinutes(15));
        var renewed = lease with { ExpiresAt = now.AddMinutes(30) };
        var authority = DurableLeaseAuthority.Open(
            temp.Path, lease.ExpiresAt, TimeSpan.FromMinutes(1), true, () => now);
        var stopBefore = authority.StopBeforeUtc;
        var handler = new CapturingRenewHandler(renewed, () => now = stopBefore);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        using var client = new TaskServerClient(http, options.RunnerId);
        using var stop = new CancellationTokenSource();
        var heartbeat = new LeaseHeartbeat(
            client, options, lease, _ => { },
            authority: authority,
            utcNow: () => now);

        await heartbeat.RunAsync(stop, CancellationToken.None);

        Assert.NotNull(handler.Request);
        Assert.True(stop.IsCancellationRequested);
        Assert.True(heartbeat.LeaseLost);
        Assert.Equal("rejected", authority.Snapshot.State);
        Assert.Equal(lease.ExpiresAt, authority.Snapshot.LeaseExpiresAtUtc);
        Assert.Equal(stopBefore, authority.StopBeforeUtc);
        Assert.False(authority.ReplayAllowed);
    }

    [Fact]
    public async Task Reconnection_renews_fence_before_replay_is_allowed()
    {
        using var temp = new TempDirectory();
        var now = new DateTime(2026, 7, 29, 9, 0, 0, DateTimeKind.Utc);
        var options = Options(temp.Path);
        var lease = Lease(now, now.AddMinutes(15));
        var renewed = lease with { ExpiresAt = now.AddMinutes(16) };
        var authority = DurableLeaseAuthority.Open(
            temp.Path,
            lease.ExpiresAt,
            TimeSpan.FromMinutes(1),
            initiallyConfirmed: true,
            () => now);
        using var stop = new CancellationTokenSource();
        using var http = new HttpClient(new FailThenRenewHandler(renewed))
        {
            BaseAddress = new Uri("http://localhost"),
        };
        using var client = new TaskServerClient(http, options.RunnerId);
        var replayAllowedDuringBackoff = true;
        var delayCalls = 0;
        var heartbeat = new LeaseHeartbeat(
            client,
            options,
            lease,
            _ => { },
            (delay, _) =>
            {
                if (delayCalls++ == 0)
                    replayAllowedDuringBackoff = authority.ReplayAllowed;
                else
                    stop.Cancel();
                now += delay;
                return Task.CompletedTask;
            },
            authority: authority,
            utcNow: () => now);

        await heartbeat.RunAsync(stop, CancellationToken.None);

        Assert.False(replayAllowedDuringBackoff);
        Assert.True(authority.ReplayAllowed);
        Assert.Equal("confirmed", authority.Snapshot.State);
        Assert.Equal(
            "fenced lease renewal reconciled before report replay",
            authority.Snapshot.Detail);
        Assert.Equal(renewed.ExpiresAt.AddMinutes(-1), authority.StopBeforeUtc);
    }

    [Fact]
    public void Stop_before_and_uncertain_replay_state_survive_runner_restart()
    {
        using var temp = new TempDirectory();
        var now = new DateTime(2026, 7, 29, 10, 0, 0, DateTimeKind.Utc);
        var expires = now.AddMinutes(15);
        var authority = DurableLeaseAuthority.Open(
            temp.Path,
            expires,
            TimeSpan.FromSeconds(30),
            initiallyConfirmed: true,
            () => now);
        authority.MarkUncertain("Task Server partitioned");

        var restarted = DurableLeaseAuthority.Open(
            temp.Path,
            expires.AddHours(1),
            TimeSpan.FromSeconds(30),
            initiallyConfirmed: false,
            () => now.AddMinutes(1));

        Assert.False(restarted.ReplayAllowed);
        Assert.Equal(expires.AddSeconds(-30), restarted.StopBeforeUtc);
        Assert.Equal(
            restarted.Snapshot,
            DurableLeaseAuthority.Read(temp.Path));
    }

    [Fact]
    public void A_new_live_claim_replaces_stale_authority_in_a_reused_worker_directory()
    {
        using var temp = new TempDirectory();
        var now = new DateTime(2026, 7, 29, 11, 0, 0, DateTimeKind.Utc);
        var oldExpiry = now.AddMinutes(2);
        var newExpiry = now.AddMinutes(15);
        var old = DurableLeaseAuthority.Open(
            temp.Path,
            oldExpiry,
            TimeSpan.FromSeconds(30),
            initiallyConfirmed: true,
            () => now);
        old.MarkUncertain("old attempt lost transport");

        var current = DurableLeaseAuthority.Open(
            temp.Path,
            newExpiry,
            TimeSpan.FromSeconds(30),
            initiallyConfirmed: true,
            () => now.AddSeconds(1));

        Assert.True(current.ReplayAllowed);
        Assert.Equal(newExpiry, current.Snapshot.LeaseExpiresAtUtc);
        Assert.Equal(newExpiry.AddSeconds(-30), current.StopBeforeUtc);
    }

    [Fact]
    public async Task Unknown_attempt_renewal_re_registers_exact_authority_before_declaring_lease_loss()
    {
        using var temp = new TempDirectory();
        var now = new DateTime(2026, 8, 11, 20, 0, 0, DateTimeKind.Utc);
        var options = Options(temp.Path);
        var lease = Lease(now, now.AddMinutes(15)) with
        {
            AttemptId = "run-active",
            AuthorityEpoch = 3,
        };
        var handler = new RestartedServerHandler(lease, now);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        using var client = new TaskServerClient(
            http,
            options.RunnerId,
            usesDurableTaskServer: true,
            options: options,
            runnerInstanceId: "replacement-daemon");
        client.RestoreRunAuthority(
            lease.TaskKey,
            lease.AttemptId,
            "original-lease-instance",
            lease);
        using var stop = new CancellationTokenSource();
        var logs = new List<string>();
        var heartbeat = new LeaseHeartbeat(
            client,
            options,
            lease,
            logs.Add,
            (_, _) =>
            {
                stop.Cancel();
                return Task.CompletedTask;
            },
            utcNow: () => now);

        await heartbeat.RunAsync(stop, CancellationToken.None);

        Assert.False(heartbeat.LeaseLost);
        Assert.Equal(
            [
                "/api/v1/runs/run-active/lease/renew",
                "/api/v1/runners/autonomy-runner",
                "/api/v1/runs/run-active/lease/renew",
            ],
            handler.Paths);
        Assert.Contains("\"attemptId\":\"run-active\"", handler.RegistrationBody);
        Assert.Contains("\"leaseInstanceId\":\"original-lease-instance\"", handler.RegistrationBody);
        Assert.Contains(logs, line => line.Contains(
            "lease authority re-adopted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Restart_gap_beyond_grace_expires_the_lease_once_and_stops_retries()
    {
        using var temp = new TempDirectory();
        var now = new DateTime(2026, 9, 12, 11, 0, 0, DateTimeKind.Utc);
        var options = Options(temp.Path);
        var lease = Lease(now.AddMinutes(-16), now.AddMinutes(-1)) with
        {
            AttemptId = "run-expired",
            AuthorityEpoch = 4,
        };
        var handler = new ExpiredRestartHandler(lease, now);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        using var client = new TaskServerClient(
            http,
            options.RunnerId,
            usesDurableTaskServer: true,
            options: options,
            runnerInstanceId: "replacement-daemon");
        client.RestoreRunAuthority(lease.TaskKey, lease.AttemptId, "expired-instance", lease);
        using var stop = new CancellationTokenSource();
        var logs = new List<string>();
        var heartbeat = new LeaseHeartbeat(client, options, lease, logs.Add);

        await heartbeat.RunAsync(stop, CancellationToken.None);

        Assert.True(heartbeat.LeaseLost);
        Assert.True(stop.IsCancellationRequested);
        Assert.Equal(1, handler.RenewCalls);
        Assert.Equal(1, handler.RegistrationCalls);
        Assert.Single(logs, line => line.StartsWith("lease lost;", StringComparison.Ordinal));
    }

    private static RunnerOptions Options(string root) => new()
    {
        ServerUrl = "http://localhost",
        RunnerId = "autonomy-runner",
        RunnerName = "autonomy-runner",
        Hostname = "autonomy-host",
        BackendName = "test",
        WorkDir = root,
        BaseBranch = "main",
        CliBin = "/bin/sh",
        CliArgs = "",
        TtlSeconds = 900,
        HeartbeatSeconds = 60,
    };

    private static RunLeaseInfoDto Lease(DateTime acquiredAt, DateTime expiresAt)
        => new(
            "AGT-2396",
            "autonomy-runner",
            "autonomy-runner",
            "autonomy-host",
            1234,
            "test",
            "lease-autonomy",
            17,
            acquiredAt,
            expiresAt);

    private sealed class OfflineHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            throw new HttpRequestException("Task Server partitioned");
        }
    }

    private sealed class CapturingRenewHandler(
        RunLeaseInfoDto lease,
        Action? beforeResponse = null) : HttpMessageHandler
    {
        public RunLeaseHeartbeatRequest? Request { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = JsonSerializer.Deserialize<RunLeaseHeartbeatRequest>(
                await request.Content!.ReadAsStringAsync(cancellationToken),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            beforeResponse?.Invoke();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new RunLeaseResponse("Renewed", true, lease)),
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }

    private sealed class StopRenewalHandler(
        RunLeaseInfoDto lease,
        RunStopDirectiveDto directive) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var payload = JsonSerializer.Serialize(new RunLeaseResponse(
                "Renewed", true, lease, StopRequest: directive));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class FailThenRenewHandler(
        RunLeaseInfoDto renewed) : HttpMessageHandler
    {
        private int _calls;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
                throw new HttpRequestException("Task Server partitioned");
            var payload = JsonSerializer.Serialize(new RunLeaseResponse(
                "Renewed",
                true,
                renewed));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class RestartedServerHandler(
        RunLeaseInfoDto renewed,
        DateTime now) : HttpMessageHandler
    {
        private int _renewCalls;
        public List<string> Paths { get; } = [];
        public string RegistrationBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            if (request.Method == HttpMethod.Put)
            {
                RegistrationBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                var registered = new RunnerDto(
                    renewed.RunnerId,
                    renewed.RunnerName,
                    renewed.Hostname,
                    "replacement-daemon",
                    "1.0.0",
                    TaskServerProtocol.Current,
                    "active",
                    now,
                    now,
                    AttemptAdoptions:
                    [
                        new RunnerAttemptAdoption(
                            RunnerAttemptKinds.Coding,
                            renewed.AttemptId!,
                            renewed.TaskKey,
                            "adopted",
                            now.AddMinutes(15)),
                    ]);
                return Json(HttpStatusCode.OK, registered);
            }
            if (Interlocked.Increment(ref _renewCalls) == 1)
                return Json(HttpStatusCode.Conflict, new ApiError(
                    "unknown-attempt", "Task Server restarted."));
            return Json(HttpStatusCode.OK, new LeaseResponse(
                "renewed",
                new LeaseDto(
                    renewed.LeaseId,
                    renewed.AttemptId!,
                    renewed.TaskKey,
                    renewed.RunnerId,
                    "original-lease-instance",
                    renewed.FencingToken,
                    renewed.AcquiredAt,
                    now.AddMinutes(15),
                    "active")));
        }

        private static HttpResponseMessage Json<T>(HttpStatusCode status, T value)
            => new(status)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(value),
                    Encoding.UTF8,
                    "application/json"),
            };
    }

    private sealed class ExpiredRestartHandler(
        RunLeaseInfoDto lease,
        DateTime now) : HttpMessageHandler
    {
        public int RenewCalls { get; private set; }
        public int RegistrationCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Put)
            {
                RegistrationCalls++;
                return Task.FromResult(Json(HttpStatusCode.OK, new RunnerDto(
                    lease.RunnerId,
                    lease.RunnerName,
                    lease.Hostname,
                    "replacement-daemon",
                    "1.0.0",
                    TaskServerProtocol.Current,
                    "active",
                    now,
                    now,
                    AttemptAdoptions:
                    [
                        new RunnerAttemptAdoption(
                            RunnerAttemptKinds.Coding,
                            lease.AttemptId!,
                            lease.TaskKey,
                            "expired",
                            null,
                            "restart grace elapsed"),
                    ])));
            }

            RenewCalls++;
            return Task.FromResult(Json(HttpStatusCode.Conflict, new ApiError(
                "lease-expired-process-unknown",
                "Restart grace elapsed.")));
        }

        private static HttpResponseMessage Json<T>(HttpStatusCode status, T value)
            => new(status)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(value),
                    Encoding.UTF8,
                    "application/json"),
            };
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "runner-authority-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
