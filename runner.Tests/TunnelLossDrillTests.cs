using System.Net;
using System.Text;
using System.Text.Json;
using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TestSupport.TunnelDrill;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// AGT-2937 (Dossier AGT-W65 D9, option "bounded-replay"): runner half of the
/// controlled tunnel-loss drill. Only the runner-to-Task-Server route of one
/// isolated in-process client is interrupted; the Task Server is an in-memory
/// authority model and every clock is synthetic drill time. The real
/// <see cref="LeaseHeartbeat"/>, <see cref="DurableLeaseAuthority"/>,
/// <see cref="DurableRunOutbox"/> and <see cref="TaskServerClient"/> decide.
/// Safety deadlines are the production defaults; the drill never changes them.
/// </summary>
public sealed class TunnelLossDrillTests
{
    private static readonly DateTime DrillStart = new(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private const int HeartbeatSeconds = 30;
    private const int TtlSeconds = 120;

    [Fact]
    public async Task Short_interruption_inside_granted_authority_keeps_the_worker_and_renews_the_same_fence()
    {
        using var drill = new Drill("short-interruption");
        var stopBeforeInitially = drill.Authority.StopBeforeUtc;
        drill.Route.InterruptAt(DrillStart.AddSeconds(HeartbeatSeconds));
        drill.Route.RestoreAt(DrillStart.AddSeconds(HeartbeatSeconds * 1.5));

        var heartbeat = drill.Heartbeat(stopAfter: DrillStart.AddSeconds(HeartbeatSeconds * 5));
        await heartbeat.RunAsync(drill.Stop, CancellationToken.None);

        Assert.False(heartbeat.LeaseLost);
        Assert.True(drill.Route.DroppedRequests > 0);
        Assert.True(drill.Clock.Max <= stopBeforeInitially || drill.Server.Renewals > 1);
        Assert.Equal("confirmed", drill.Authority.Snapshot.State);
        Assert.True(drill.Authority.StopBeforeUtc > stopBeforeInitially);
        Assert.Equal(drill.Lease.FencingToken, drill.Server.Fence);
        drill.Record("coding", teardown: null, finalReceipt: "renewed-same-fence", terminal: "running");
    }

    [Fact]
    public async Task Interruption_beyond_authority_stops_the_worker_exactly_at_stop_before()
    {
        using var drill = new Drill("beyond-authority-stop");
        var grantedExpiry = drill.Lease.ExpiresAt;
        var stopBefore = drill.Authority.StopBeforeUtc;
        drill.Route.InterruptAt(DrillStart.AddSeconds(1));

        var heartbeat = drill.Heartbeat();
        await heartbeat.RunAsync(drill.Stop, CancellationToken.None);

        Assert.True(heartbeat.LeaseLost);
        Assert.True(drill.Stop.IsCancellationRequested);
        Assert.Equal(stopBefore, drill.Clock.Now);
        Assert.True(drill.Clock.Max <= stopBefore);
        Assert.Equal(grantedExpiry.AddSeconds(-HeartbeatSeconds), stopBefore);
        Assert.Equal("rejected", drill.Authority.Snapshot.State);
        Assert.False(drill.Authority.ReplayAllowed);
        Assert.Equal(0, drill.Server.ReportEffects);
        drill.Record("coding", teardown: drill.Clock.Now, finalReceipt: "local-deadline-rejected", terminal: "worker-stopped");
    }

    [Fact]
    public async Task Daemon_restart_after_expiry_re_adopts_the_exact_current_attempt_and_replays_once()
    {
        using var drill = new Drill("beyond-authority-exact-readoption");
        var outbox = drill.OpenOutbox();
        var report = outbox.Enqueue("artifact", Artifact("final-report"));
        drill.Route.InterruptAt(DrillStart.AddSeconds(1));
        var firstGeneration = drill.Heartbeat();
        await firstGeneration.RunAsync(drill.Stop, CancellationToken.None);
        var teardown = drill.Clock.Now;
        Assert.True(firstGeneration.LeaseLost);
        Assert.Single(outbox.Pending);

        // The outage outlives the server lease; the lease row stays Leased.
        drill.Clock.Set(drill.Lease.ExpiresAt.AddMinutes(5));
        drill.Route.RestoreAt(drill.Clock.Now);
        var restarted = drill.RestartDaemon();
        var adoptedHeartbeat = restarted.Heartbeat(stopAfter: drill.Clock.Now.AddSeconds(HeartbeatSeconds));
        Assert.False(restarted.Authority.ReplayAllowed);

        await adoptedHeartbeat.RunAsync(restarted.Stop, CancellationToken.None);

        Assert.False(adoptedHeartbeat.LeaseLost);
        Assert.Equal(1, drill.Server.Registrations);
        Assert.Equal("adopted", drill.Server.LastAdoption);
        Assert.Equal("confirmed", restarted.Authority.Snapshot.State);
        Assert.True(restarted.Authority.StopBeforeUtc > drill.Clock.Now);
        var reopened = restarted.OpenOutbox();
        await reopened.ReplayAsync((item, ct) => restarted.Client.SendOutboxItemAsync(reopened.Authority, item, ct), CancellationToken.None);
        await reopened.ReplayAsync((item, ct) => restarted.Client.SendOutboxItemAsync(reopened.Authority, item, ct), CancellationToken.None);
        Assert.Empty(reopened.Pending);
        Assert.Equal(1, drill.Server.ReportEffects);
        drill.Record("coding", teardown, finalReceipt: "adopted+report-applied-once", terminal: "recovered",
            outboxKey: report.IdempotencyKey, stopBefore: restarted.Authority.StopBeforeUtc);
    }

    [Fact]
    public async Task Superseded_generation_is_rejected_and_never_replays_its_report()
    {
        using var drill = new Drill("beyond-authority-superseded");
        var outbox = drill.OpenOutbox();
        var report = outbox.Enqueue("artifact", Artifact("stale-report"));
        drill.Route.InterruptAt(DrillStart.AddSeconds(1));
        await drill.Heartbeat().RunAsync(drill.Stop, CancellationToken.None);
        var teardown = drill.Clock.Now;

        drill.Clock.Set(drill.Lease.ExpiresAt.AddMinutes(5));
        drill.Server.SupersedeWithReplacementClaim();
        drill.Route.RestoreAt(drill.Clock.Now);
        var restarted = drill.RestartDaemon();
        var heartbeat = restarted.Heartbeat();

        await heartbeat.RunAsync(restarted.Stop, CancellationToken.None);

        Assert.True(heartbeat.LeaseLost);
        Assert.Equal("stale-authority", drill.Server.LastAdoption);
        Assert.Equal("rejected", restarted.Authority.Snapshot.State);
        Assert.False(restarted.Authority.ReplayAllowed);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => restarted.Authority.WaitForConfirmedAsync(CancellationToken.None));
        Assert.Single(restarted.OpenOutbox().Pending);
        Assert.Equal(0, drill.Server.ReportEffects);
        Assert.True(drill.Server.Fence > drill.Lease.FencingToken);
        drill.Record("coding", teardown, finalReceipt: "stale-authority; replacement fence " + drill.Server.Fence,
            terminal: "superseded-rejected", outboxKey: report.IdempotencyKey);
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task Superseded_generation_preserves_its_work_only_on_a_pushed_quarantine_ref()
    {
        using var drill = new Drill("superseded-quarantine");
        drill.Route.InterruptAt(DrillStart.AddSeconds(1));
        await drill.Heartbeat().RunAsync(drill.Stop, CancellationToken.None);
        var teardown = drill.Clock.Now;
        drill.Server.SupersedeWithReplacementClaim();
        var origin = Path.Combine(drill.Options.WorkDir, "origin.git");
        await SeedOriginAsync(drill.Options.WorkDir, origin);
        var options = new RunnerOptions
        {
            ServerUrl = drill.Options.ServerUrl,
            RunnerId = drill.Options.RunnerId,
            RunnerName = drill.Options.RunnerName,
            Hostname = drill.Options.Hostname,
            BackendName = drill.Options.BackendName,
            BaseBranch = "main",
            CliBin = "test",
            CliArgs = "",
            GitRemote = origin,
            WorkDir = Path.Combine(drill.Options.WorkDir, "git"),
            StateDir = Path.Combine(drill.Options.WorkDir, "git", ".runner-state"),
        };
        var workspace = new GitWorkspace(options, drill.Lease.TaskKey, _ => { },
            sourceRunAttemptId: drill.Lease.AttemptId, fencingToken: drill.Lease.FencingToken);
        await workspace.PrepareAsync(CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(workspace.RepoPath, "stale.txt"), "preserved, never delivered");

        var quarantine = await workspace.TeardownToQuarantineAsync("LeaseLoss", drill.Lease.AttemptId, CancellationToken.None);

        Assert.True(quarantine.SecuredWork);
        Assert.Contains($"/{drill.Lease.AttemptId}/fence-{drill.Lease.FencingToken}/", quarantine.Branch);
        Assert.Null(quarantine.ImmutableResultRef);
        var remote = await ProcessRunner.RunAsync("git", ["ls-remote", origin, $"refs/heads/{quarantine.Branch}"],
            workingDirectory: drill.Options.WorkDir);
        var pushedSha = remote.StdOut.Split('\t', 2)[0].Trim();
        var pushStatus = pushedSha.Length == 40 && pushedSha == quarantine.ResultSha ? "pushed" : "local-only";
        Assert.Equal("pushed", pushStatus);
        var results = await ProcessRunner.RunAsync("git", ["ls-remote", origin, "refs/heads/agent-studio/results/*"],
            workingDirectory: drill.Options.WorkDir);
        Assert.Equal(string.Empty, results.StdOut.Trim());
        Assert.True(drill.Server.Fence > drill.Lease.FencingToken);
        drill.Record("coding", teardown, finalReceipt: "quarantined; replacement claim fence " + drill.Server.Fence,
            terminal: "superseded-quarantined", quarantine: (quarantine.Branch, quarantine.ResultSha, pushStatus));
    }

    private static async Task SeedOriginAsync(string root, string origin)
    {
        var seed = Path.Combine(root, "seed");
        await Git(root, "init", "--bare", origin);
        await Git(root, "init", seed);
        await File.WriteAllTextAsync(Path.Combine(seed, "README.md"), "seed");
        await Git(seed, "add", "--all");
        await Git(seed, "-c", "user.name=Drill", "-c", "user.email=drill@example.invalid", "commit", "-m", "seed");
        await Git(seed, "branch", "-M", "main");
        await Git(seed, "push", origin, "main");
    }

    private static async Task Git(string workingDirectory, params string[] args)
    {
        var result = await ProcessRunner.RunAsync("git", args, workingDirectory: workingDirectory);
        Assert.True(result.Success, $"git {string.Join(' ', args)} failed: {result.StdErr}");
    }

    [Fact]
    public async Task Lost_report_acknowledgement_replays_with_the_same_key_and_applies_once()
    {
        using var drill = new Drill("lost-report-ack");
        var outbox = drill.OpenOutbox();
        var report = outbox.Enqueue("artifact", Artifact("terminal-report"));
        drill.Route.DropNextResponse();

        await Assert.ThrowsAsync<HttpRequestException>(() => outbox.ReplayAsync(
            (item, ct) => drill.Client.SendOutboxItemAsync(outbox.Authority, item, ct), CancellationToken.None));
        Assert.Single(outbox.Pending);
        Assert.Equal(1, drill.Server.ReportEffects);

        var restarted = drill.RestartDaemon();
        var reopened = restarted.OpenOutbox();
        await reopened.ReplayAsync(
            (item, ct) => restarted.Client.SendOutboxItemAsync(reopened.Authority, item, ct), CancellationToken.None);

        Assert.Empty(reopened.Pending);
        Assert.Equal(1, drill.Server.ReportEffects);
        Assert.Equal(2, drill.Server.ReportDeliveries);
        drill.Record("coding", teardown: null, finalReceipt: "duplicate-acknowledged", terminal: "report-settled",
            outboxKey: report.IdempotencyKey);
    }

    private static string Artifact(string name)
    {
        var content = Encoding.UTF8.GetBytes(name);
        return JsonSerializer.Serialize(new DurableArtifactPayload(
            name + ".txt",
            "text/plain",
            Convert.ToBase64String(content),
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant()),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private sealed class DrillClock(DateTime start)
    {
        public DateTime Now { get; private set; } = start;
        public DateTime Max { get; private set; } = start;

        public void Advance(TimeSpan delta) => Set(Now + delta);

        public void Set(DateTime value)
        {
            Now = value;
            if (value > Max) Max = value;
        }
    }

    /// <summary>One isolated drill fixture: temp worker dir, route, server model, clock.</summary>
    private sealed class Drill : IDisposable
    {
        private const string LeaseInstance = "drill-lease-instance";
        private readonly string _root;
        private readonly HttpClient _http;
        private readonly List<string> _evidence = [];
        private DateTime? _stopAfter;

        public Drill(string scenario)
            : this(scenario, Path.Combine(Path.GetTempPath(), "tunnel-drill", Guid.NewGuid().ToString("N")),
                new DrillClock(DrillStart), null, null)
        {
        }

        private Drill(string scenario, string root, DrillClock clock, DrillTaskServer? server, DrillRoute? route)
        {
            Scenario = scenario;
            _root = root;
            Directory.CreateDirectory(root);
            Clock = clock;
            Options = new RunnerOptions
            {
                ServerUrl = "http://drill.invalid",
                RunnerId = "drill-runner",
                RunnerName = "drill-runner",
                Hostname = "drill-host",
                BackendName = "test",
                WorkDir = root,
                BaseBranch = "main",
                CliBin = "/bin/sh",
                CliArgs = "",
                TtlSeconds = TtlSeconds,
                HeartbeatSeconds = HeartbeatSeconds,
            };
            Lease = new RunLeaseInfoDto(
                "AGT-DRILL-1", "drill-runner", "drill-runner", "drill-host", 4242, "test",
                "lease-drill", 7, DrillStart, DrillStart.AddSeconds(TtlSeconds), "run-drill-1");
            Server = server ?? new DrillTaskServer(Lease, LeaseInstance, clock);
            Route = route ?? new DrillRoute(Server, clock, _evidence.Add);
            _http = new HttpClient(Route, disposeHandler: false) { BaseAddress = new Uri(Options.ServerUrl) };
            Client = new TaskServerClient(_http, Options.RunnerId, usesDurableTaskServer: true, options: Options,
                runnerInstanceId: $"drill-daemon-{Guid.NewGuid():N}");
            Client.RestoreRunAuthority(Lease.TaskKey, Lease.AttemptId, LeaseInstance, Lease);
            var restart = server is not null;
            Authority = DurableLeaseAuthority.Open(
                Path.Combine(root, "worker"),
                Lease.ExpiresAt,
                TimeSpan.FromSeconds(HeartbeatSeconds),
                initiallyConfirmed: !restart,
                () => clock.Now);
        }

        public string Scenario { get; }
        public DrillClock Clock { get; }
        public RunnerOptions Options { get; }
        public RunLeaseInfoDto Lease { get; }
        public DrillTaskServer Server { get; }
        public DrillRoute Route { get; }
        public TaskServerClient Client { get; }
        public DurableLeaseAuthority Authority { get; }
        public CancellationTokenSource Stop { get; } = new();

        public Drill RestartDaemon()
        {
            _evidence.Add($"{Clock.Now:o} daemon-restart persisted-authority={Authority.Snapshot.State}");
            return new Drill(Scenario, _root, Clock, Server, Route) { Parent = this };
        }

        private Drill? Parent { get; init; }

        public DurableRunOutbox OpenOutbox()
            => DurableRunOutbox.Open(Path.Combine(_root, "outbox"), new RunOutboxAuthority(
                Lease.AttemptId!, Lease.TaskKey, Options.RunnerId, LeaseInstance, Lease.LeaseId, Lease.FencingToken));

        public LeaseHeartbeat Heartbeat(DateTime? stopAfter = null)
        {
            _stopAfter = stopAfter;
            return new LeaseHeartbeat(
                Client,
                Options,
                Lease,
                line => _evidence.Add($"{Clock.Now:o} {line}"),
                (delay, _) =>
                {
                    Clock.Advance(delay);
                    if (_stopAfter is { } limit && Clock.Now >= limit)
                        Stop.Cancel();
                    return Task.CompletedTask;
                },
                authority: Authority,
                utcNow: () => Clock.Now);
        }

        public void Record(
            string kind,
            DateTime? teardown,
            string finalReceipt,
            string terminal,
            string? outboxKey = null,
            DateTime? stopBefore = null,
            (string? Ref, string? Sha, string PushStatus)? quarantine = null)
        {
            var root = Parent ?? this;
            TunnelDrillReport.Record(
                "runner",
                new TunnelDrillOutage(Scenario, Route.InterruptedAt ?? Clock.Now, Route.RestoredAt, "runner->task-server (in-process drill route)"),
                [
                    new TunnelDrillAttempt(
                        Scenario, kind, Lease.AttemptId!, Lease.FencingToken, Lease.AuthorityEpoch,
                        Lease.ExpiresAt, stopBefore ?? root.Authority.StopBeforeUtc, teardown,
                        quarantine?.Ref, quarantine?.Sha, quarantine?.PushStatus, outboxKey, finalReceipt, terminal),
                ],
                [.. root._evidence, .. Parent is null ? [] : _evidence, .. Server.Evidence]);
        }

        public void Dispose()
        {
            Stop.Dispose();
            Client.Dispose();
            _http.Dispose();
            if (Parent is null)
            {
                try { Directory.Delete(_root, recursive: true); }
                catch (IOException) { }
            }
        }
    }

    /// <summary>
    /// The only interrupted path: requests from this drill client to the
    /// in-memory Task Server. Nothing else on the host is touched.
    /// </summary>
    private sealed class DrillRoute(HttpMessageHandler server, DrillClock clock, Action<string> evidence)
        : DelegatingHandler(server)
    {
        private DateTime? _interruptAt;
        private DateTime? _restoreAt;
        private bool _dropNextResponse;

        public DateTime? InterruptedAt => _interruptAt;
        public DateTime? RestoredAt => _restoreAt;
        public int DroppedRequests { get; private set; }

        public void InterruptAt(DateTime at) => _interruptAt = at;
        public void RestoreAt(DateTime at) => _restoreAt = at;
        public void DropNextResponse() => _dropNextResponse = true;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var now = clock.Now;
            if (_interruptAt is { } down && now >= down && (_restoreAt is not { } up || now < up))
            {
                DroppedRequests++;
                evidence($"{now:o} route-drop {request.Method} {request.RequestUri!.AbsolutePath}");
                throw new HttpRequestException("drill: runner-to-Task-Server route interrupted");
            }
            var response = await base.SendAsync(request, cancellationToken);
            if (_dropNextResponse)
            {
                _dropNextResponse = false;
                evidence($"{now:o} response-lost {request.Method} {request.RequestUri!.AbsolutePath} status={(int)response.StatusCode}");
                throw new HttpRequestException("drill: acknowledgement lost on the return route");
            }
            return response;
        }
    }

    /// <summary>
    /// In-memory model of the Task Server authority rules exercised by the
    /// drill: renewals need an unexpired lease, re-registration re-adopts only
    /// the exact current identity, reports are idempotent by key.
    /// </summary>
    private sealed class DrillTaskServer(RunLeaseInfoDto lease, string leaseInstance, DrillClock clock) : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private readonly HashSet<string> _appliedKeys = new(StringComparer.Ordinal);
        private DateTime _expiresAt = lease.ExpiresAt;

        public long Fence { get; private set; } = lease.FencingToken;
        public int Renewals { get; private set; }
        public int Registrations { get; private set; }
        public int ReportEffects { get; private set; }
        public int ReportDeliveries { get; private set; }
        public string? LastAdoption { get; private set; }
        public List<string> Evidence { get; } = [];

        public void SupersedeWithReplacementClaim()
        {
            Fence++;
            Evidence.Add($"{clock.Now:o} server replacement-claim run=run-drill-2 fence={Fence}");
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var now = clock.Now;
            if (request.Method == HttpMethod.Put && path.StartsWith("/api/v1/runners/", StringComparison.Ordinal))
            {
                Registrations++;
                var body = JsonSerializer.Deserialize<RegisterRunnerRequest>(
                    await request.Content!.ReadAsStringAsync(cancellationToken), Json)!;
                var adoptions = (body.ActiveAttempts ?? []).Select(attempt =>
                {
                    var exact = attempt.AttemptId == lease.AttemptId
                                && attempt.LeaseId == lease.LeaseId
                                && attempt.Fence == Fence
                                && attempt.LeaseInstanceId == leaseInstance
                                && attempt.AuthorityEpoch == 0;
                    if (!exact)
                        return new RunnerAttemptAdoption(attempt.Kind, attempt.AttemptId, attempt.TaskKey,
                            "stale-authority", null, "RunAttempt authority does not match the durable server record.");
                    _expiresAt = now.AddSeconds(TtlSeconds);
                    return new RunnerAttemptAdoption(attempt.Kind, attempt.AttemptId, attempt.TaskKey, "adopted", _expiresAt);
                }).ToArray();
                LastAdoption = adoptions.LastOrDefault()?.Status;
                Evidence.Add($"{now:o} server registration adoption={LastAdoption} fence={Fence}");
                return Respond(HttpStatusCode.OK, new RunnerDto(
                    "drill-runner", "drill-runner", "drill-host", body.InstanceId, "1.0.0",
                    TaskServerProtocol.Current, "active", now, now, AttemptAdoptions: adoptions));
            }
            if (path.EndsWith("/lease/renew", StringComparison.Ordinal))
            {
                var renew = JsonSerializer.Deserialize<LeaseRenewRequest>(
                    await request.Content!.ReadAsStringAsync(cancellationToken), Json)!;
                if (renew.Fence != Fence)
                {
                    Evidence.Add($"{now:o} server renew-rejected stale-fence presented={renew.Fence} current={Fence}");
                    return Respond(HttpStatusCode.Conflict, new ApiError("stale-fence", "A newer generation owns the task."));
                }
                if (now >= _expiresAt)
                {
                    Evidence.Add($"{now:o} server renew-rejected lease-expired expiresAt={_expiresAt:o}");
                    return Respond(HttpStatusCode.Conflict, new ApiError("lease-expired", "Lease expired; re-register the attempt."));
                }
                Renewals++;
                _expiresAt = now.AddSeconds(TtlSeconds);
                Evidence.Add($"{now:o} server renewed fence={Fence} expiresAt={_expiresAt:o}");
                return Respond(HttpStatusCode.OK, new LeaseResponse("renewed", new LeaseDto(
                    lease.LeaseId, lease.AttemptId!, lease.TaskKey, lease.RunnerId, leaseInstance,
                    Fence, lease.AcquiredAt, _expiresAt, "active")));
            }
            if (path.EndsWith("/artifacts", StringComparison.Ordinal))
            {
                var artifact = JsonSerializer.Deserialize<AgentStudio.TaskServer.Contracts.ArtifactIngestRequest>(
                    await request.Content!.ReadAsStringAsync(cancellationToken), Json)!;
                ReportDeliveries++;
                if (artifact.Fence != Fence)
                    return Respond(HttpStatusCode.Conflict, new ApiError("stale-fence", "Report from a superseded generation."));
                var applied = _appliedKeys.Add(artifact.IdempotencyKey);
                if (applied) ReportEffects++;
                Evidence.Add($"{now:o} server report key={artifact.IdempotencyKey} {(applied ? "applied" : "duplicate")}");
                return Respond(HttpStatusCode.OK, new ArtifactDto(
                    artifact.ArtifactId, lease.AttemptId!, artifact.Name, artifact.MediaType, artifact.Sha256,
                    0, artifact.IdempotencyKey, Fence, now, artifact.Sequence));
            }
            return Respond(HttpStatusCode.NotFound, new ApiError("not-found", path));
        }

        private static HttpResponseMessage Respond<T>(HttpStatusCode status, T value)
            => new(status)
            {
                Content = new StringContent(JsonSerializer.Serialize(value, Json), Encoding.UTF8, "application/json"),
            };
    }
}
