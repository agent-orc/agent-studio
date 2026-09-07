using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TestSupport;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// The planned-restart path against a fake Task Server: the outgoing instance
/// keeps the lease alive across the restart window, the replacement verifies
/// that authority before its first heartbeat, and a refused verification is
/// repaired by a takeover instead of killing an expensive report.
/// </summary>
public sealed class ReviewHandoffAdoptionTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "review-handoff-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public async Task Adoption_renews_the_handed_off_lease_before_the_first_heartbeat()
    {
        var server = new FakeReviewServer();
        using var http = new HttpClient(server) { BaseAddress = new Uri("http://task-server") };
        var options = Options();
        using var client = new TaskServerClient(
            http, options.RunnerId, usesDurableTaskServer: true, options: options);
        var logs = new List<string>();
        var state = new ReviewStateStore(options.StateDir);
        var slot = await CreateHandedOffSlotAsync(state);

        var exitCode = await new RemoteReviewExecutor(options, client, state, logs.Add)
            .ReattachAsync(slot, CancellationToken.None);

        Assert.Equal(0, exitCode);
        // The heartbeat interval is an hour: this renewal can only be the
        // adoption check, and it carries the persisted handoff authority.
        var renew = Assert.Single(server.Renewals);
        Assert.Equal("lease-1", renew.LeaseId);
        Assert.Equal(17, renew.Fence);
        Assert.Equal("instance-1", renew.InstanceId);
        Assert.Contains(logs, line =>
            line.Contains("review adoption lease verified", StringComparison.Ordinal)
            && line.Contains("fence=17", StringComparison.Ordinal));
        Assert.Equal(17, Assert.Single(server.Reports).Fence);
    }

    [Fact]
    public async Task Refused_adoption_renew_re_claims_the_attempt_instead_of_dropping_the_report()
    {
        var server = new FakeReviewServer
        {
            RenewFailure = (HttpStatusCode.Conflict, "review-attempt-not-leased"),
            ReAdoptionStatus = "stale-authority",
        };
        using var http = new HttpClient(server) { BaseAddress = new Uri("http://task-server") };
        var options = Options();
        using var client = new TaskServerClient(
            http, options.RunnerId, usesDurableTaskServer: true, options: options);
        var logs = new List<string>();
        var state = new ReviewStateStore(options.StateDir);
        var slot = await CreateHandedOffSlotAsync(state);

        var exitCode = await new RemoteReviewExecutor(options, client, state, logs.Add)
            .ReattachAsync(slot, CancellationToken.None);

        Assert.Equal(0, exitCode);
        var reclaim = Assert.Single(server.ReClaims);
        Assert.Equal("lease-1", reclaim.PreviousLeaseId);
        Assert.Equal(17, reclaim.PreviousFence);
        Assert.Equal(client.RunnerInstanceId, reclaim.InstanceId);
        // The gate work survives: the report lands under the new fence rather
        // than dying as a LeaseExpired rejection.
        var report = Assert.Single(server.Reports);
        Assert.Equal(18, report.Fence);
        Assert.Equal("lease-2", report.LeaseId);
        Assert.Equal("Pass", report.Outcome);
        Assert.Contains(logs, line =>
            line.Contains("review lease re-claimed", StringComparison.Ordinal)
            && line.Contains("previousFence=17 fence=18", StringComparison.Ordinal));
        Assert.DoesNotContain(logs, line =>
            line.Contains("review-report-terminal", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_deliberately_superseded_attempt_is_never_re_claimed()
    {
        var server = new FakeReviewServer
        {
            RenewFailure = (HttpStatusCode.Conflict, "Superseded"),
            ReportFailure = (HttpStatusCode.Conflict, "Superseded"),
        };
        using var http = new HttpClient(server) { BaseAddress = new Uri("http://task-server") };
        var options = Options();
        using var client = new TaskServerClient(
            http, options.RunnerId, usesDurableTaskServer: true, options: options);
        var logs = new List<string>();
        var state = new ReviewStateStore(options.StateDir);
        var slot = await CreateHandedOffSlotAsync(state);

        var exitCode = await new RemoteReviewExecutor(options, client, state, logs.Add)
            .ReattachAsync(slot, CancellationToken.None);

        Assert.Equal(3, exitCode);
        Assert.Empty(server.ReClaims);
        Assert.Empty(server.Registrations);
        Assert.Contains(logs, line =>
            line.Contains("review lease authority lost", StringComparison.Ordinal)
            && line.Contains("scope=adoption", StringComparison.Ordinal));
        Assert.Contains(logs, line =>
            line.Contains("review-report-terminal", StringComparison.Ordinal)
            && line.Contains("classification=Superseded", StringComparison.Ordinal));
    }

    // Linux-only: proving the detached worker alive reads /proc/<pid>/cwd.
    [SkippableFact]
    [Trait(PlatformGate.TraitName, PlatformGate.Linux)]
    public async Task Handoff_extends_the_lease_before_leaving_the_worker_to_its_replacement()
    {
        PlatformGate.LinuxOnly("the review worker liveness proof reads /proc/<pid>/cwd");
        var server = new FakeReviewServer();
        using var http = new HttpClient(server) { BaseAddress = new Uri("http://task-server") };
        var options = Options();
        using var client = new TaskServerClient(
            http, options.RunnerId, usesDurableTaskServer: true, options: options);
        var logs = new List<string>();
        var state = new ReviewStateStore(options.StateDir);
        var workspace = Path.Combine(_root, "review-attempt-1-f17", "repository");
        Directory.CreateDirectory(workspace);
        using var worker = StartLivingWorker(workspace);
        var slot = state.Save(state.Create(Claim(), workspace) with
        {
            ProcessId = worker.Id,
            ProcessStartedAtUtc = worker.StartTime.ToUniversalTime(),
            Phase = "running",
        });

        using var shutdown = new CancellationTokenSource();
        var execution = new RemoteReviewExecutor(options, client, state, logs.Add)
            .ReattachAsync(slot, shutdown.Token);
        // The adoption renewal proves the executor reached its polling loop.
        while (server.Renewals.Count == 0) await Task.Delay(10);
        await shutdown.CancelAsync();

        Assert.Equal(0, await execution.WaitAsync(TimeSpan.FromSeconds(10)));
        var handoff = server.Renewals.Last();
        Assert.Equal(options.HandoffLeaseTtlSeconds, handoff.RequestedTtlSeconds);
        Assert.Equal("lease-1", handoff.LeaseId);
        Assert.Contains(logs, line =>
            line.Contains("review handoff lease extended", StringComparison.Ordinal)
            && line.Contains($"requestedTtlSeconds={options.HandoffLeaseTtlSeconds}", StringComparison.Ordinal));
        Assert.Contains(logs, line =>
            line.Contains("review daemon handoff", StringComparison.Ordinal));
        // The record the replacement adopts carries the extended expiry.
        var persisted = Assert.Single(state.LoadAll());
        Assert.Equal("handed-off", persisted.Phase);
        Assert.Equal(
            server.LeaseExpiry(options.HandoffLeaseTtlSeconds),
            persisted.Claim.Lease!.ExpiresAt);
    }

    private static Process StartLivingWorker(string workspace)
    {
        var worker = Process.Start(new ProcessStartInfo("/bin/sleep", "120")
        {
            WorkingDirectory = workspace,
            RedirectStandardOutput = true,
        }) ?? throw new InvalidOperationException("The fixture worker did not start.");
        return worker;
    }

    private async Task<PersistedReviewSlot> CreateHandedOffSlotAsync(ReviewStateStore state)
    {
        var workspace = Path.Combine(_root, "review-attempt-1-f17", "repository");
        Directory.CreateDirectory(workspace);
        var slot = state.Create(Claim(), workspace);
        await File.WriteAllTextAsync(
            Path.Combine(slot.WorkerDirectory, "review-result.json"),
            JsonSerializer.Serialize(
                new DetachedReviewResult(
                    new ReviewExecutionEvidence(
                        "Pass",
                        new ReviewWorkspaceProofDto(
                            "example/repository",
                            new string('a', 40),
                            new string('a', 40),
                            "main",
                            false,
                            false,
                            new string('b', 64),
                            "review-attempt-1-f17"),
                        [],
                        [],
                        []),
                    null,
                    null,
                    DateTime.UtcNow),
                Json));
        return state.Save(slot with { Phase = "handed-off" });
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
        ReviewWorkDir = _root,
        StateDir = Path.Combine(_root, "state"),
        BaseBranch = "main",
        CliBin = "test",
        CliArgs = "",
        TtlSeconds = 120,
        // An hour of heartbeat silence: every renewal these tests observe is an
        // adoption check or a handoff extension, never a timer tick.
        HeartbeatSeconds = 3600,
        HandoffLeaseTtlSeconds = 300,
        PollSeconds = 1,
    };

    private static ReviewClaimResponse Claim()
    {
        var now = new DateTime(2026, 9, 7, 3, 10, 0, DateTimeKind.Utc);
        var attempt = new ReviewAttemptDto(
            "attempt-1", "subject-1", "AGT-2753", 1, "leased",
            "review-runner", "review-host", 17, now, null, null, null, null);
        var subject = new ReviewSubjectDto(
            "subject-1", "AGT-2753", "run-1", "example/repository", null,
            new string('a', 40), null, "bundle", new string('b', 64),
            "coding-host", "policy-v1", new ReviewPlanDto([], []), now);
        var lease = new ReviewLeaseDto(
            "lease-1", "attempt-1", "subject-1", "review-runner", "instance-1",
            "review-host", 17, now, now.AddMinutes(2), "active",
            "review-attempt-1-f17", 25000, 23);
        return new ReviewClaimResponse("claimed", attempt, subject, lease);
    }

    /// <summary>
    /// Minimal review plane: it records every authority mutation and can be told
    /// to refuse renewals or re-adoption the way the production server did on
    /// 2026-09-07.
    /// </summary>
    private sealed class FakeReviewServer : HttpMessageHandler
    {
        private static readonly DateTime Now = new(2026, 9, 7, 3, 12, 0, DateTimeKind.Utc);

        public ConcurrentQueue<ReviewLeaseRenewRequest> RenewalQueue { get; } = new();
        public IReadOnlyList<ReviewLeaseRenewRequest> Renewals => RenewalQueue.ToArray();
        public List<ReviewReClaimRequest> ReClaims { get; } = [];
        public List<ReviewReportRequest> Reports { get; } = [];
        public List<RegisterRunnerRequest> Registrations { get; } = [];

        public (HttpStatusCode Status, string Code)? RenewFailure { get; init; }
        public (HttpStatusCode Status, string Code)? ReportFailure { get; init; }
        public string ReAdoptionStatus { get; init; } = "adopted";

        public DateTime LeaseExpiry(int ttlSeconds) => Now.AddSeconds(ttlSeconds);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (request.Method == HttpMethod.Put && path.StartsWith("/api/v1/runners/", StringComparison.Ordinal))
            {
                var registration = await ReadAsync<RegisterRunnerRequest>(request);
                Registrations.Add(registration);
                return JsonResponse(Runner(registration));
            }

            if (path.EndsWith("/lease/renew", StringComparison.Ordinal))
            {
                var renew = await ReadAsync<ReviewLeaseRenewRequest>(request);
                RenewalQueue.Enqueue(renew);
                if (RenewFailure is { } refusal) return ApiError(refusal.Status, refusal.Code);
                return JsonResponse(Lease(
                    renew.LeaseId,
                    renew.Fence,
                    renew.InstanceId,
                    LeaseExpiry(renew.RequestedTtlSeconds)));
            }

            if (path.EndsWith("/reclaim", StringComparison.Ordinal))
            {
                var reclaim = await ReadAsync<ReviewReClaimRequest>(request);
                ReClaims.Add(reclaim);
                var fence = reclaim.PreviousFence + 1;
                return JsonResponse(new ReviewClaimResponse(
                    "claimed",
                    Attempt(fence),
                    Lease: Lease(
                        "lease-2",
                        fence,
                        reclaim.InstanceId,
                        LeaseExpiry(reclaim.RequestedTtlSeconds))));
            }

            if (path.EndsWith("/report", StringComparison.Ordinal))
            {
                var report = await ReadAsync<ReviewReportRequest>(request);
                Reports.Add(report);
                if (ReportFailure is { } rejected) return ApiError(rejected.Status, rejected.Code);
                // Only the current fence may write. This is what turned lost
                // adoptions into ReportRejected before the takeover existed.
                if (report.Fence != CurrentFence)
                    return ApiError(HttpStatusCode.Conflict, "LeaseExpired");
                return JsonResponse(new ReviewReportDto(
                    "report-1", "attempt-1", "subject-1", report.Outcome,
                    report.FailureClassification, "accepted", new string('c', 64),
                    Now, false, "5-human-review"));
            }

            if (path.EndsWith("/cleanup", StringComparison.Ordinal))
            {
                return JsonResponse(new ReviewCleanupResponse("cleaned", "attempt-1", Now, false));
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($"unexpected fake review server request: {path}"),
            };
        }

        private long CurrentFence => ReClaims.Count == 0 ? 17 : ReClaims[^1].PreviousFence + 1;

        private RunnerDto Runner(RegisterRunnerRequest registration)
            => new(
                "review-runner",
                registration.Name,
                registration.HostId,
                registration.InstanceId,
                registration.RunnerVersion,
                registration.ProtocolVersion,
                "active",
                Now,
                Now,
                AttemptAdoptions: (registration.ActiveAttempts ?? [])
                    .Select(attempt => new RunnerAttemptAdoption(
                        attempt.Kind,
                        attempt.AttemptId,
                        attempt.TaskKey,
                        ReAdoptionStatus,
                        ReAdoptionStatus == "adopted" ? Now.AddMinutes(2) : null,
                        null))
                    .ToArray());

        private static ReviewAttemptDto Attempt(long fence)
            => new(
                "attempt-1", "subject-1", "AGT-2753", 1, "leased",
                "review-runner", "review-host", fence, Now, null, null, null, null);

        private static ReviewLeaseDto Lease(
            string leaseId,
            long fence,
            string instanceId,
            DateTime expiresAt)
            => new(
                leaseId, "attempt-1", "subject-1", "review-runner", instanceId,
                "review-host", fence, Now, expiresAt, "active",
                $"review-attempt-1-f{fence}", 25000, 23);

        private static async Task<T> ReadAsync<T>(HttpRequestMessage request)
            => JsonSerializer.Deserialize<T>(await request.Content!.ReadAsStringAsync(), Json)
               ?? throw new InvalidDataException($"Request body was not valid {typeof(T).Name} JSON.");

        private static HttpResponseMessage ApiError(HttpStatusCode status, string code)
            => new(status)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    code,
                    message = "synthetic review authority refusal",
                    detail = (string?)null,
                }, Json)),
            };

        private static HttpResponseMessage JsonResponse<T>(T value)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(value, Json)),
            };
    }
}
