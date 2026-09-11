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
        Assert.Equal(client.RunnerInstanceId, report.InstanceId);
        Assert.Equal(client.RunnerInstanceId, report.Environment.InstanceId);
        Assert.Equal("review-attempt-1-f17", report.Workspace.ResourceNamespace);
        Assert.Equal("review-attempt-1-f17", report.Environment.Isolation["containers"]);
        Assert.Equal("Pass", report.Outcome);
        Assert.Contains(logs, line =>
            line.Contains("review lease re-claimed", StringComparison.Ordinal)
            && line.Contains("previousFence=17 fence=18", StringComparison.Ordinal));
        Assert.DoesNotContain(logs, line =>
            line.Contains("review-report-terminal", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Successful_re_registration_is_renewed_before_the_report_continues()
    {
        var server = new FakeReviewServer
        {
            RenewFailure = (HttpStatusCode.Conflict, "review-attempt-not-leased"),
            RenewFailuresBeforeSuccess = 1,
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
        Assert.Single(server.Registrations);
        Assert.Equal(2, server.Renewals.Count);
        Assert.Empty(server.ReClaims);
        Assert.Single(server.Reports);
        Assert.Contains(logs, line =>
            line.Contains("re-adopted and renewed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Re_adoption_that_still_cannot_renew_falls_back_to_a_fenced_re_claim()
    {
        var server = new FakeReviewServer
        {
            RenewFailure = (HttpStatusCode.Conflict, "review-attempt-not-leased"),
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
        Assert.Single(server.Registrations);
        Assert.Equal(2, server.Renewals.Count);
        Assert.Single(server.ReClaims);
        Assert.Equal(18, Assert.Single(server.Reports).Fence);
        Assert.Contains(logs, line =>
            line.Contains("re-adoption did not renew", StringComparison.Ordinal));
        Assert.Contains(logs, line =>
            line.Contains("review lease re-claimed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Ambiguous_re_claim_response_replays_the_same_delivery_and_keeps_its_higher_fence()
    {
        var server = new FakeReviewServer
        {
            RenewFailure = (HttpStatusCode.Conflict, "review-attempt-not-leased"),
            ReAdoptionStatus = "stale-authority",
            LoseFirstReClaimResponseAfterCommit = true,
        };
        using var http = new HttpClient(server) { BaseAddress = new Uri("http://task-server") };
        var options = Options();
        using var client = new TaskServerClient(
            http, options.RunnerId, usesDurableTaskServer: true, options: options);
        var logs = new List<string>();
        var state = new ReviewStateStore(options.StateDir);
        var slot = await CreateHandedOffSlotAsync(state);
        var executor = new RemoteReviewExecutor(options, client, state, logs.Add)
        {
            AuthorityRetryDelayOverride = _ => TimeSpan.Zero,
        };

        var exitCode = await executor.ReattachAsync(slot, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(2, server.ReClaims.Count);
        Assert.Equal(server.ReClaims[0], server.ReClaims[1]);
        Assert.Equal(server.ReClaims[0].IdempotencyKey, server.ReClaims[1].IdempotencyKey);
        var report = Assert.Single(server.Reports);
        Assert.Equal(18, report.Fence);
        Assert.Equal("lease-2", report.LeaseId);
        Assert.Contains(logs, line =>
            line.Contains("re-claim response ambiguous", StringComparison.Ordinal)
            && line.Contains("submissionAttempts=1", StringComparison.Ordinal));
        Assert.DoesNotContain(logs, line =>
            line.Contains("review-report-terminal", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("runner-instance-stale")]
    [InlineData("review-executor-not-registered")]
    public async Task Uncommitted_pending_re_claim_rotates_to_the_current_daemon_and_reports(
        string staleGenerationCode)
    {
        var server = new FakeReviewServer
        {
            RenewFailure = (HttpStatusCode.Conflict, "review-attempt-not-leased"),
            ReAdoptionStatus = "stale-authority",
            EnforceReClaimInstanceGeneration = true,
            StaleReClaimCode = staleGenerationCode,
            BlockCurrentGenerationReClaimBeforeCommit = true,
        };
        using var http = new HttpClient(server) { BaseAddress = new Uri("http://task-server") };
        var options = Options();
        using var client = new TaskServerClient(
            http,
            options.RunnerId,
            usesDurableTaskServer: true,
            options: options,
            runnerInstanceId: "replacement-2");
        var logs = new List<string>();
        var state = new ReviewStateStore(options.StateDir);
        var slot = await CreateHandedOffSlotAsync(state);
        var previous = slot.Claim.Lease!;
        var pendingFromPreviousDaemon = new ReviewReClaimRequest(
            previous.ExecutorId,
            "replacement-1",
            previous.LeaseId,
            previous.Fence,
            $"review-reclaim:{previous.AttemptId}:{previous.Fence}:replacement-1",
            options.TtlSeconds);
        slot = state.Save(slot with { PendingReClaim = pendingFromPreviousDaemon });

        var execution = new RemoteReviewExecutor(options, client, state, logs.Add)
            .ReattachAsync(slot, CancellationToken.None);
        await server.CurrentGenerationReClaimStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var rotated = Assert.IsType<ReviewReClaimRequest>(
            Assert.Single(state.LoadAll()).PendingReClaim);
        Assert.Equal("replacement-2", rotated.InstanceId);
        Assert.NotEqual(pendingFromPreviousDaemon.IdempotencyKey, rotated.IdempotencyKey);
        server.ReleaseCurrentGenerationReClaim.TrySetResult();

        var exitCode = await execution.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, exitCode);
        Assert.Equal(2, server.ReClaims.Count);
        Assert.Equal(pendingFromPreviousDaemon, server.ReClaims[0]);
        var currentRequest = server.ReClaims[1];
        Assert.Equal("replacement-2", currentRequest.InstanceId);
        Assert.NotEqual(pendingFromPreviousDaemon.IdempotencyKey, currentRequest.IdempotencyKey);
        Assert.Equal(previous.LeaseId, currentRequest.PreviousLeaseId);
        Assert.Equal(previous.Fence, currentRequest.PreviousFence);
        var report = Assert.Single(server.Reports);
        Assert.Equal(18, report.Fence);
        Assert.Equal("replacement-2", report.InstanceId);
        Assert.Empty(state.LoadAll());
        Assert.Contains(logs, line =>
            line.Contains("replay missed for stale daemon generation", StringComparison.Ordinal)
            && line.Contains("staleInstance=replacement-1", StringComparison.Ordinal)
            && line.Contains("currentInstance=replacement-2", StringComparison.Ordinal));
        Assert.DoesNotContain(logs, line =>
            line.Contains("review-report-terminal", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Successful_old_lease_renewal_clears_an_uncommitted_pending_re_claim()
    {
        var server = new FakeReviewServer();
        using var http = new HttpClient(server) { BaseAddress = new Uri("http://task-server") };
        var options = Options();
        using var client = new TaskServerClient(
            http,
            options.RunnerId,
            usesDurableTaskServer: true,
            options: options,
            runnerInstanceId: "replacement-2");
        var logs = new List<string>();
        var state = new ReviewStateStore(options.StateDir);
        var slot = await CreateHandedOffSlotAsync(state);
        var previous = slot.Claim.Lease!;
        slot = state.Save(slot with
        {
            PendingReClaim = new ReviewReClaimRequest(
                previous.ExecutorId,
                "replacement-1",
                previous.LeaseId,
                previous.Fence,
                $"review-reclaim:{previous.AttemptId}:{previous.Fence}:replacement-1",
                options.TtlSeconds),
        });
        var beforeReport = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReport = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new RemoteReviewExecutor(options, client, state, logs.Add)
        {
            BeforeReportSubmissionOverride = async ct =>
            {
                beforeReport.TrySetResult();
                await releaseReport.Task.WaitAsync(ct);
            },
        };

        var execution = executor.ReattachAsync(slot, CancellationToken.None);
        await beforeReport.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var renewed = Assert.Single(state.LoadAll());
        Assert.Null(renewed.PendingReClaim);
        Assert.Equal(previous.Fence, renewed.Claim.Lease!.Fence);
        releaseReport.TrySetResult();

        Assert.Equal(0, await execution.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Empty(server.ReClaims);
        Assert.Equal(previous.Fence, Assert.Single(server.Reports).Fence);
        Assert.Contains(logs, line =>
            line.Contains("renewal disproved pending re-claim", StringComparison.Ordinal)
            && line.Contains("journal cleared", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_report_side_lease_expiry_recovers_before_terminal_cleanup()
    {
        var server = new FakeReviewServer
        {
            ReAdoptionStatus = "stale-authority",
            ReportFailureSequence =
            [
                (HttpStatusCode.Conflict, "LeaseExpired"),
                null,
            ],
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
        Assert.Single(server.Registrations);
        Assert.Single(server.ReClaims);
        Assert.Equal([17L, 18L], server.Reports.Select(report => report.Fence));
        Assert.Contains(logs, line =>
            line.Contains("scope=report", StringComparison.Ordinal)
            && line.Contains("previousFence=17 fence=18", StringComparison.Ordinal));
        Assert.DoesNotContain(logs, line =>
            line.Contains("review-report-terminal", StringComparison.Ordinal));
        Assert.Empty(state.LoadAll());
    }

    [Fact]
    public async Task A_lost_re_registration_response_replays_registration_instead_of_re_claiming_a_live_lease()
    {
        var server = new FakeReviewServer
        {
            RenewFailure = (HttpStatusCode.Conflict, "review-attempt-not-leased"),
            RenewFailuresBeforeSuccess = 1,
            LoseFirstRegistrationResponseAfterCommit = true,
        };
        using var http = new HttpClient(server) { BaseAddress = new Uri("http://task-server") };
        var options = Options();
        using var client = new TaskServerClient(
            http, options.RunnerId, usesDurableTaskServer: true, options: options);
        var logs = new List<string>();
        var state = new ReviewStateStore(options.StateDir);
        var slot = await CreateHandedOffSlotAsync(state);
        var executor = new RemoteReviewExecutor(options, client, state, logs.Add)
        {
            AuthorityRetryDelayOverride = _ => TimeSpan.Zero,
        };

        var exitCode = await executor.ReattachAsync(slot, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(2, server.Registrations.Count);
        Assert.Equal(server.Registrations[0].ActiveAttempts, server.Registrations[1].ActiveAttempts);
        Assert.Empty(server.ReClaims);
        Assert.Equal(17, Assert.Single(server.Reports).Fence);
        Assert.Contains(logs, line =>
            line.Contains("re-adoption response ambiguous", StringComparison.Ordinal)
            && line.Contains("submissionAttempts=1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Shutdown_interrupts_ambiguous_re_claim_retry_and_the_next_daemon_replays_it()
    {
        var server = new FakeReviewServer
        {
            RenewFailure = (HttpStatusCode.Conflict, "review-attempt-not-leased"),
            ReAdoptionStatus = "stale-authority",
            LoseFirstReClaimResponseAfterCommit = true,
            EnforceReClaimInstanceGeneration = true,
        };
        using var http = new HttpClient(server) { BaseAddress = new Uri("http://task-server") };
        var options = Options();
        using var client = new TaskServerClient(
            http,
            options.RunnerId,
            usesDurableTaskServer: true,
            options: options,
            runnerInstanceId: "replacement-1");
        var logs = new List<string>();
        var state = new ReviewStateStore(options.StateDir);
        var slot = await CreateHandedOffSlotAsync(state);
        var executor = new RemoteReviewExecutor(options, client, state, logs.Add)
        {
            AuthorityRetryDelayOverride = _ => TimeSpan.FromMinutes(5),
        };
        using var shutdown = new CancellationTokenSource();

        var execution = executor.ReattachAsync(slot, shutdown.Token);
        await WaitUntilAsync(() => server.ReClaims.Count > 0);
        await shutdown.CancelAsync();

        Assert.Equal(0, await execution.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Empty(server.Reports);
        var persisted = Assert.Single(state.LoadAll());
        Assert.Equal("handed-off", persisted.Phase);
        Assert.Equal(17, persisted.Claim.Lease!.Fence);
        var pending = Assert.IsType<ReviewReClaimRequest>(persisted.PendingReClaim);
        Assert.Equal("replacement-1", pending.InstanceId);
        Assert.Contains(logs, line =>
            line.Contains("re-claim response ambiguous", StringComparison.Ordinal));
        Assert.Contains(logs, line =>
            line.Contains("review daemon handoff", StringComparison.Ordinal));

        // A distinct daemon generation replays the persisted request. The fake
        // server returns the already-committed fence 18, after which the result
        // lands once and terminal cleanup removes the slot.
        using var replacementHttp = new HttpClient(server)
        {
            BaseAddress = new Uri("http://task-server"),
        };
        using var replacementClient = new TaskServerClient(
            replacementHttp,
            options.RunnerId,
            usesDurableTaskServer: true,
            options: options,
            runnerInstanceId: "replacement-2");
        var replacementLogs = new List<string>();

        var exitCode = await new RemoteReviewExecutor(
                options,
                replacementClient,
                state,
                replacementLogs.Add)
            .ReattachAsync(persisted, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(2, server.ReClaims.Count);
        Assert.Equal(server.ReClaims[0], server.ReClaims[1]);
        Assert.Equal("replacement-1", server.ReClaims[1].InstanceId);
        var report = Assert.Single(server.Reports);
        Assert.Equal(18, report.Fence);
        Assert.Equal("replacement-1", report.InstanceId);
        Assert.Empty(state.LoadAll());
        Assert.Contains(replacementLogs, line =>
            line.Contains("scope=adoption", StringComparison.Ordinal)
            && line.Contains("previousFence=17 fence=18", StringComparison.Ordinal));
    }

    // Linux-only: keeping the main execution path alive until its result is
    // written uses the persisted /proc process-generation proof.
    [SkippableFact]
    [Trait(PlatformGate.TraitName, PlatformGate.Linux)]
    public async Task Concurrent_phase_save_preserves_pending_re_claim_for_the_next_daemon()
    {
        PlatformGate.LinuxOnly("the review worker liveness proof reads /proc/<pid>/cwd");
        var refusal = (HttpStatusCode.Conflict, "review-attempt-not-leased");
        var server = new FakeReviewServer
        {
            // Adoption succeeds. The heartbeat, handoff extension, and next
            // daemon adoption are then refused so recovery must use reclaim.
            RenewFailureSequence = [null, refusal, refusal, refusal],
            ReAdoptionStatus = "stale-authority",
            LoseFirstReClaimResponseAfterCommit = true,
        };
        using var http = new HttpClient(server) { BaseAddress = new Uri("http://task-server") };
        var options = Options(heartbeatSeconds: 1);
        using var client = new TaskServerClient(
            http,
            options.RunnerId,
            usesDurableTaskServer: true,
            options: options,
            runnerInstanceId: "replacement-1");
        var state = new ReviewStateStore(options.StateDir);
        var workspace = Path.Combine(_root, "review-attempt-1-f17", "repository");
        Directory.CreateDirectory(workspace);
        using var worker = StartLivingWorker(workspace);
        var slot = state.Save(state.Create(Claim(), workspace) with
        {
            ProcessId = worker.Id,
            ProcessStartedAtUtc = worker.StartTime.ToUniversalTime(),
            Phase = "handed-off",
        });
        var executor = new RemoteReviewExecutor(options, client, state, _ => { })
        {
            AuthorityRetryDelayOverride = _ => TimeSpan.FromMinutes(5),
        };
        using var shutdown = new CancellationTokenSource();

        var execution = executor.ReattachAsync(slot, shutdown.Token);
        await WaitUntilAsync(() =>
            server.ReClaims.Count > 0
            && state.LoadAll().SingleOrDefault()?.PendingReClaim is not null);

        // The main worker path now observes completion and saves "finalizing"
        // from the snapshot it held before the heartbeat journaled reclaim.
        await WriteCompletedResultAsync(slot);
        await WaitUntilAsync(() =>
            string.Equals(
                state.LoadAll().SingleOrDefault()?.Phase,
                "finalizing",
                StringComparison.Ordinal));
        var concurrentSave = Assert.Single(state.LoadAll());
        var pending = Assert.IsType<ReviewReClaimRequest>(concurrentSave.PendingReClaim);
        Assert.Equal(server.ReClaims[0], pending);

        await shutdown.CancelAsync();
        Assert.Equal(0, await execution.WaitAsync(TimeSpan.FromSeconds(10)));
        var handedOff = Assert.Single(state.LoadAll());
        Assert.Equal("handed-off", handedOff.Phase);
        Assert.Equal(pending, handedOff.PendingReClaim);

        using var replacementHttp = new HttpClient(server)
        {
            BaseAddress = new Uri("http://task-server"),
        };
        using var replacementClient = new TaskServerClient(
            replacementHttp,
            options.RunnerId,
            usesDurableTaskServer: true,
            options: options,
            runnerInstanceId: "replacement-2");

        var exitCode = await new RemoteReviewExecutor(
                options,
                replacementClient,
                state,
                _ => { })
            .ReattachAsync(handedOff, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(2, server.ReClaims.Count);
        Assert.Equal(server.ReClaims[0], server.ReClaims[1]);
        Assert.Equal(18, server.Reports.Last().Fence);
        Assert.Empty(state.LoadAll());
    }

    [Fact]
    public async Task Transient_re_adoption_verification_recovers_a_report_side_expiry()
    {
        var server = new FakeReviewServer
        {
            RenewFailureSequence =
            [
                (HttpStatusCode.Conflict, "review-attempt-not-leased"),
                (HttpStatusCode.ServiceUnavailable, "task-server-unavailable"),
            ],
            ReportFailureSequence =
            [
                (HttpStatusCode.Conflict, "LeaseExpired"),
                null,
            ],
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
        Assert.Equal(2, server.Registrations.Count);
        Assert.Equal(3, server.Renewals.Count);
        Assert.Empty(server.ReClaims);
        Assert.Equal([17L, 17L], server.Reports.Select(report => report.Fence));
        Assert.Contains(logs, line =>
            line.Contains("re-adoption verification transient", StringComparison.Ordinal));
        Assert.Contains(logs, line =>
            line.Contains("scope=report", StringComparison.Ordinal)
            && line.Contains("re-adopted and renewed", StringComparison.Ordinal));
        Assert.DoesNotContain(logs, line =>
            line.Contains("attempting a fenced re-claim", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Heartbeat_re_fencing_is_serialized_before_terminal_report_submission()
    {
        var server = new FakeReviewServer
        {
            RenewFailureSequence =
            [
                null,
                (HttpStatusCode.Conflict, "review-attempt-not-leased"),
            ],
            ReAdoptionStatus = "stale-authority",
            BlockRenewalNumber = 2,
            WaitForReClaimBeforeValidatingStaleReport = true,
        };
        using var http = new HttpClient(server) { BaseAddress = new Uri("http://task-server") };
        var options = Options(heartbeatSeconds: 1);
        using var client = new TaskServerClient(
            http, options.RunnerId, usesDurableTaskServer: true, options: options);
        var logs = new List<string>();
        var state = new ReviewStateStore(options.StateDir);
        var slot = await CreateHandedOffSlotAsync(state);
        var executor = new RemoteReviewExecutor(options, client, state, logs.Add)
        {
            BeforeReportSubmissionOverride = ct =>
                server.BlockedRenewalStarted.Task.WaitAsync(ct),
        };

        var execution = executor.ReattachAsync(slot, CancellationToken.None);
        await server.BlockedRenewalStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        server.ReleaseBlockedRenewal.TrySetResult();

        Assert.Equal(0, await execution.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Single(server.ReClaims);
        var report = Assert.Single(server.Reports);
        Assert.Equal(18, report.Fence);
        Assert.Equal("lease-2", report.LeaseId);
        Assert.Contains(logs, line =>
            line.Contains("scope=heartbeat", StringComparison.Ordinal)
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

    [Fact]
    public async Task Cancelling_report_backoff_extends_and_persists_the_handoff_authority()
    {
        var server = new FakeReviewServer
        {
            ReportFailure = (HttpStatusCode.ServiceUnavailable, "task-server-unavailable"),
        };
        using var http = new HttpClient(server) { BaseAddress = new Uri("http://task-server") };
        var options = Options();
        using var client = new TaskServerClient(
            http, options.RunnerId, usesDurableTaskServer: true, options: options);
        var logs = new List<string>();
        var state = new ReviewStateStore(options.StateDir);
        var slot = await CreateHandedOffSlotAsync(state);
        var executor = new RemoteReviewExecutor(options, client, state, logs.Add)
        {
            ReportRetryDelayOverride = _ => TimeSpan.FromMinutes(5),
        };

        using var shutdown = new CancellationTokenSource();
        var execution = executor.ReattachAsync(slot, shutdown.Token);
        await WaitUntilAsync(() =>
            server.Reports.Count == 1
            && state.LoadAll().SingleOrDefault()?.Phase == "report-pending");

        // The successful adoption renewal is durable before report backoff.
        var adopted = Assert.Single(state.LoadAll());
        Assert.Equal("report-pending", adopted.Phase);
        Assert.Equal(
            server.LeaseExpiry(options.TtlSeconds),
            adopted.Claim.Lease!.ExpiresAt);

        await shutdown.CancelAsync();
        Assert.Equal(0, await execution.WaitAsync(TimeSpan.FromSeconds(10)));

        var handoff = server.Renewals.Last();
        Assert.Equal(options.HandoffLeaseTtlSeconds, handoff.RequestedTtlSeconds);
        var persisted = Assert.Single(state.LoadAll());
        Assert.Equal("report-pending", persisted.Phase);
        Assert.Equal(
            server.LeaseExpiry(options.HandoffLeaseTtlSeconds),
            persisted.Claim.Lease!.ExpiresAt);
        Assert.Contains(logs, line =>
            line.Contains("review report retry handed off", StringComparison.Ordinal));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("Timed out waiting for the fake review server.");
            await Task.Delay(10);
        }
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
        await WriteCompletedResultAsync(slot);
        return state.Save(slot with { Phase = "handed-off" });
    }

    private static async Task WriteCompletedResultAsync(PersistedReviewSlot slot)
    {
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
    }

    private RunnerOptions Options(int heartbeatSeconds = 3600) => new()
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
        HeartbeatSeconds = heartbeatSeconds,
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
        public IReadOnlyList<(HttpStatusCode Status, string Code)?>? RenewFailureSequence { get; init; }
        public int? RenewFailuresBeforeSuccess { get; init; }
        public (HttpStatusCode Status, string Code)? ReportFailure { get; init; }
        public IReadOnlyList<(HttpStatusCode Status, string Code)?>? ReportFailureSequence { get; init; }
        public string ReAdoptionStatus { get; init; } = "adopted";
        public bool LoseFirstReClaimResponseAfterCommit { get; init; }
        public bool EnforceReClaimInstanceGeneration { get; init; }
        public string StaleReClaimCode { get; init; } = "runner-instance-stale";
        public bool BlockCurrentGenerationReClaimBeforeCommit { get; init; }
        public bool LoseFirstRegistrationResponseAfterCommit { get; init; }
        public int? BlockRenewalNumber { get; init; }
        public bool WaitForReClaimBeforeValidatingStaleReport { get; init; }
        public TaskCompletionSource BlockedRenewalStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseBlockedRenewal { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CurrentGenerationReClaimStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseCurrentGenerationReClaim { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _renewalCount;
        private int _registrationCount;
        private int _reportCount;
        private string? _registeredInstanceId;
        private ReviewReClaimRequest? _committedReClaimRequest;
        private ReviewClaimResponse? _committedReClaim;
        private readonly TaskCompletionSource _reClaimCommitted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

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
                _registeredInstanceId = registration.InstanceId;
                var response = JsonResponse(Runner(registration));
                if (LoseFirstRegistrationResponseAfterCommit
                    && Interlocked.Increment(ref _registrationCount) == 1)
                {
                    throw new HttpRequestException(
                        "synthetic lost registration response after committed adoption");
                }
                return response;
            }

            if (path.EndsWith("/lease/renew", StringComparison.Ordinal))
            {
                var renew = await ReadAsync<ReviewLeaseRenewRequest>(request);
                RenewalQueue.Enqueue(renew);
                var renewalNumber = Interlocked.Increment(ref _renewalCount);
                if (BlockRenewalNumber == renewalNumber)
                {
                    BlockedRenewalStarted.TrySetResult();
                    await ReleaseBlockedRenewal.Task.WaitAsync(cancellationToken);
                }
                var sequencedFailure = RenewFailureSequence is { } sequence
                                       && renewalNumber <= sequence.Count
                    ? sequence[renewalNumber - 1]
                    : null;
                if (sequencedFailure is { } sequencedRefusal)
                {
                    return ApiError(sequencedRefusal.Status, sequencedRefusal.Code);
                }
                if (RenewFailure is { } refusal
                    && (RenewFailuresBeforeSuccess is null
                        || renewalNumber <= RenewFailuresBeforeSuccess.Value))
                {
                    return ApiError(refusal.Status, refusal.Code);
                }
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
                if (EnforceReClaimInstanceGeneration
                    && _committedReClaimRequest is not null)
                {
                    if (reclaim == _committedReClaimRequest)
                        return JsonResponse(_committedReClaim!);
                    if (string.Equals(
                            reclaim.IdempotencyKey,
                            _committedReClaimRequest.IdempotencyKey,
                            StringComparison.Ordinal))
                    {
                        return ApiError(HttpStatusCode.Conflict, "idempotency-conflict");
                    }
                }
                if (EnforceReClaimInstanceGeneration
                    && !string.Equals(
                        reclaim.InstanceId,
                        _registeredInstanceId,
                        StringComparison.Ordinal))
                {
                    return ApiError(HttpStatusCode.Conflict, StaleReClaimCode);
                }
                if (BlockCurrentGenerationReClaimBeforeCommit)
                {
                    CurrentGenerationReClaimStarted.TrySetResult();
                    await ReleaseCurrentGenerationReClaim.Task.WaitAsync(cancellationToken);
                }
                if (_committedReClaim is null)
                {
                    var fence = reclaim.PreviousFence + 1;
                    _committedReClaimRequest = reclaim;
                    _committedReClaim = new ReviewClaimResponse(
                        "claimed",
                        Attempt(fence),
                        Lease: Lease(
                            "lease-2",
                            fence,
                            reclaim.InstanceId,
                            LeaseExpiry(reclaim.RequestedTtlSeconds)));
                    _reClaimCommitted.TrySetResult();
                }
                if (LoseFirstReClaimResponseAfterCommit && ReClaims.Count == 1)
                    throw new HttpRequestException("synthetic lost re-claim response after commit");
                return JsonResponse(_committedReClaim);
            }

            if (path.EndsWith("/report", StringComparison.Ordinal))
            {
                var report = await ReadAsync<ReviewReportRequest>(request);
                Reports.Add(report);
                if (WaitForReClaimBeforeValidatingStaleReport && report.Fence == 17)
                    await _reClaimCommitted.Task.WaitAsync(cancellationToken);
                // Mirror the monolith's authority checks, including environment
                // attribution. The detached workspace itself must still name the
                // original physical namespace after a fenced takeover.
                if (report.Fence != CurrentFence
                    || !string.Equals(report.ExecutorId, "review-runner", StringComparison.Ordinal)
                    || !string.Equals(report.InstanceId, CurrentInstanceId, StringComparison.Ordinal)
                    || !string.Equals(report.Environment.ExecutorId, "review-runner", StringComparison.Ordinal)
                    || !string.Equals(report.Environment.InstanceId, CurrentInstanceId, StringComparison.Ordinal)
                    || !string.Equals(report.Environment.HostId, "review-host", StringComparison.Ordinal))
                {
                    return ApiError(HttpStatusCode.Conflict, "review-execution-attribution-mismatch");
                }
                if (!string.Equals(
                        report.Workspace.ResourceNamespace,
                        "review-attempt-1-f17",
                        StringComparison.Ordinal)
                    || !string.Equals(
                        report.Environment.Isolation["containers"],
                        "review-attempt-1-f17",
                        StringComparison.Ordinal)
                    || !string.Equals(
                        report.Environment.Isolation["databases"],
                        "review-attempt-1-f17",
                        StringComparison.Ordinal)
                    || !string.Equals(
                        report.Environment.Isolation["ports"],
                        "25000-25007",
                        StringComparison.Ordinal))
                {
                    return ApiError(HttpStatusCode.Conflict, "review-workspace-attribution-mismatch");
                }
                var reportNumber = Interlocked.Increment(ref _reportCount);
                var sequencedReportFailure = ReportFailureSequence is { } reportSequence
                                             && reportNumber <= reportSequence.Count
                    ? reportSequence[reportNumber - 1]
                    : null;
                if (sequencedReportFailure is { } sequencedReportRefusal)
                    return ApiError(sequencedReportRefusal.Status, sequencedReportRefusal.Code);
                if (ReportFailure is { } rejected) return ApiError(rejected.Status, rejected.Code);
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

        private long CurrentFence => _committedReClaim?.Lease?.Fence ?? 17;

        private string CurrentInstanceId => _committedReClaim?.Lease?.InstanceId ?? "instance-1";

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
