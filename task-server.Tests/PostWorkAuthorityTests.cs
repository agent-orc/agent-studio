using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

/// <summary>AGT-3171 DP-S8: durable post-work subjects with one authority holder each.</summary>
public sealed class PostWorkAuthorityTests
{
    private static readonly DateTime Now = new(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc);
    private const string Backend = "backend-a";

    [Theory]
    [InlineData("backend", "queued", "none", null, true, "backend-owned")]
    [InlineData("backend", "waiting", "none", null, true, "backend-owned")]
    [InlineData("backend", "queued", "draining", null, false, "admission-stopped")]
    [InlineData("host", "queued", "host", null, false, "host-owned")]
    [InlineData("host", "running", "host", "host", false, "authority-held")]
    [InlineData("backend", "running", "none", "backend", false, "authority-held")]
    [InlineData("host", "reported", "host", null, true, "apply-host-report")]
    [InlineData("backend", "applied", "none", null, false, "already-applied")]
    [InlineData("backend", "retired", "none", null, false, "retired")]
    public void Backend_pass_admission_matrix(
        string owner, string state, string phase, string? liveKind, bool admitted, string reason)
    {
        var decision = PostWorkAuthorityPolicy.AdmitBackendPass(
            Facts(owner, state, phase, liveKind, Backend), Backend, Now);
        Assert.Equal(admitted, decision.Admitted);
        Assert.Equal(reason, decision.Reason);
    }

    [Fact]
    public void Backend_pass_of_an_earlier_backend_process_is_superseded_but_a_host_lease_is_not()
    {
        var stale = PostWorkAuthorityPolicy.AdmitBackendPass(
            Facts("backend", "running", "none", "backend", "backend-old"), Backend, Now);
        Assert.True(stale.Admitted);
        Assert.True(stale.SupersedesLiveAttempt);
        var host = PostWorkAuthorityPolicy.AdmitBackendPass(
            Facts("backend", "running", "none", "host", "host-instance"), Backend, Now);
        Assert.False(host.Admitted);
    }

    [Theory]
    [InlineData("host", "queued", "host", null, 0, true, "eligible")]
    [InlineData("host", "waiting", "host", null, 30, false, "retry-pending")]
    [InlineData("host", "waiting", "host", null, -1, true, "eligible")]
    [InlineData("host", "running", "host", "host", 0, false, "authority-held")]
    [InlineData("backend", "queued", "none", null, 0, false, "backend-owned")]
    [InlineData("backend", "queued", "draining", null, 0, false, "backend-owned")]
    [InlineData("host", "reported", "host", null, 0, false, "state-reported")]
    [InlineData("host", "applied", "host", null, 0, false, "closed")]
    public void Host_claim_admission_matrix(
        string owner, string state, string phase, string? liveKind, int retryInSeconds, bool admitted, string reason)
    {
        var facts = Facts(owner, state, phase, liveKind, "instance") with
        {
            RetryAt = retryInSeconds == 0 ? null : Now.AddSeconds(retryInSeconds),
        };
        var decision = PostWorkAuthorityPolicy.AdmitHostClaim(facts, Now);
        Assert.Equal(admitted, decision.Admitted);
        Assert.Equal(reason, decision.Reason);
    }

    [Theory]
    [InlineData("backend", "applied", null, true, true, false, "none")]
    [InlineData("backend", "queued", null, false, false, false, "retire")]
    [InlineData("backend", "queued", "lane-2", true, false, false, "retire")]
    [InlineData("backend", "queued", null, true, true, false, "mark-applied")]
    [InlineData("backend", "running", "backend", true, false, false, "hold")]
    [InlineData("host", "reported", null, true, false, false, "apply-host-report")]
    [InlineData("host", "queued", null, true, false, false, "await-host")]
    [InlineData("backend", "waiting", null, true, false, true, "retain-deferral")]
    [InlineData("backend", "waiting", null, true, false, false, "requeue-backend")]
    [InlineData("backend", "running", null, true, false, false, "requeue-backend")]
    public void Restart_reconciliation_matrix(
        string owner, string state, string? liveOrLane, bool inLane, bool decisionRecorded,
        bool retryPending, string action)
    {
        var liveKind = liveOrLane is "backend" or "host" ? liveOrLane : null;
        var laneEntry = liveOrLane == "lane-2" ? "lane-2" : "lane-1";
        var facts = Facts(owner, state, owner == "host" ? "host" : "none", liveKind, Backend) with
        {
            RetryAt = retryPending ? Now.AddMinutes(1) : null,
        };
        var lane = new PostWorkLaneFact("project", "task", laneEntry, inLane, decisionRecorded);
        Assert.Equal(action, PostWorkAuthorityPolicy.Reconcile(facts, lane, Now).Action);
    }

    [Fact]
    public async Task Materialization_is_idempotent_per_lane_entry_and_keeps_the_first_recorded_inputs()
    {
        using var temp = new TempDirectory();
        var (store, _, project) = await CanaryAsync(temp.Path);
        var first = await store.MaterializePostWorkSubjectAsync(Request(project, "AGT-1"), "backend", default);
        var replay = await store.MaterializePostWorkSubjectAsync(Request(project, "AGT-1"), "backend", default);
        Assert.Equal(first.SubjectId, replay.SubjectId);
        Assert.Equal(PostWorkIdentity.IdempotencyKey(project, "AGT-1", "lane-1"), first.IdempotencyKey);
        Assert.Equal(PostWorkOwners.Backend, first.Owner);
        Assert.Equal(["post:review"], first.RequiredCapabilities);
        Assert.StartsWith("sha256:", first.PolicyDigest);
        Assert.Equal("run-1", first.SourceRunId);
        Assert.Single(first.Artifacts);
        var changed = await store.MaterializePostWorkSubjectAsync(
            Request(project, "AGT-1") with { SourceRunId = "run-2", Steps = [] }, "backend", default);
        Assert.Equal(first.SubjectId, changed.SubjectId);
        Assert.Equal("run-1", changed.SourceRunId);
        Assert.Equal(first.PolicyDigest, changed.PolicyDigest);

        // A re-entry into the lane supersedes the idle earlier subject.
        var reentry = await store.MaterializePostWorkSubjectAsync(
            Request(project, "AGT-1") with { LaneEntryId = "lane-2" }, "backend", default);
        Assert.NotEqual(first.SubjectId, reentry.SubjectId);
        Assert.Equal(PostWorkStates.Retired, (await store.GetPostWorkSubjectAsync(first.SubjectId, default))!.State);
    }

    [Fact]
    public async Task Shadow_mode_records_eligibility_and_backend_outcomes_without_host_claims()
    {
        using var temp = new TempDirectory();
        var (store, clock, project) = await SetupAsync(temp.Path, "shadow");
        var subject = await store.MaterializePostWorkSubjectAsync(Request(project, "AGT-1"), "backend", default);
        Assert.Equal("eligible", subject.ShadowStatus);
        var uncovered = await store.MaterializePostWorkSubjectAsync(
            Request(project, "AGT-2") with { RequiredCapabilities = ["os:windows"] }, "backend", default);
        Assert.Equal("rejected", uncovered.ShadowStatus);
        Assert.Contains("os:windows-mismatch", uncovered.ShadowReasons);

        var pass = await store.BeginBackendPassAsync(Begin(subject), "backend", default);
        Assert.True(pass.Admitted);
        await store.CompleteBackendPassAsync(subject.IdempotencyKey,
            Complete(pass, PostWorkOutcomes.Decided, "done-aspects"), "backend", default);
        var uncoveredPass = await store.BeginBackendPassAsync(Begin(uncovered), "backend", default);
        await store.CompleteBackendPassAsync(uncovered.IdempotencyKey,
            Complete(uncoveredPass, PostWorkOutcomes.Deferred, "rate-limit-reached", clock.GetUtcNow().UtcDateTime.AddMinutes(1)),
            "backend", default);

        var claim = await store.ClaimPostWorkAsync(HostClaim(), "post-runner", default);
        Assert.Equal("empty", claim.Status);
        Assert.Contains("post-work-shadow", claim.RejectedReasons!);
        var summary = await store.GetPostWorkShadowSummaryAsync(10, default);
        Assert.Equal(2, summary.Subjects);
        Assert.Equal(1, summary.BackendDecided);
        Assert.Equal(1, summary.BackendDeferred);
        Assert.Equal(1, summary.Differences);
    }

    [Fact]
    public async Task Draining_cohort_waits_for_the_backend_pass_and_then_admits_exactly_one_holder()
    {
        using var temp = new TempDirectory();
        var (store, _, project) = await CanaryAsync(temp.Path);
        var subject = await store.MaterializePostWorkSubjectAsync(Request(project, "AGT-1"), "backend", default);
        var pass = await store.BeginBackendPassAsync(Begin(subject), "backend", default);
        Assert.True(pass.Admitted);

        var cohort = await store.StartPostWorkCohortAsync(
            new StartPostWorkCohortRequest("cohort-1", project, ["AGT-1"]), "operator", default);
        Assert.Equal(PostWorkCohortPhases.Draining, cohort.Phase);
        // New backend admissions stop, and the host cannot claim while the pass drains.
        var second = await store.BeginBackendPassAsync(Begin(subject) with { PassId = "pass-2" }, "backend", default);
        Assert.False(second.Admitted);
        Assert.Equal("authority-held", second.Reason);
        Assert.Equal("empty", (await store.ClaimPostWorkAsync(HostClaim(), "post-runner", default)).Status);
        var held = await store.AdvancePostWorkCohortAsync("cohort-1", Advance(project, "AGT-1"), "operator", default);
        Assert.Equal(PostWorkCohortPhases.Draining, held.Phase);
        Assert.Equal(1, held.Draining);

        await store.CompleteBackendPassAsync(subject.IdempotencyKey,
            Complete(pass, PostWorkOutcomes.Deferred, "awaiting-canonical-review-verdict", Now.AddMinutes(-1)),
            "backend", default);
        var afterPass = await store.BeginBackendPassAsync(Begin(subject) with { PassId = "pass-3" }, "backend", default);
        Assert.Equal("admission-stopped", afterPass.Reason);
        var admitted = await store.AdvancePostWorkCohortAsync("cohort-1", Advance(project, "AGT-1"), "operator", default);
        Assert.Equal(PostWorkCohortPhases.Host, admitted.Phase);
        var converted = (await store.GetPostWorkSubjectAsync(subject.SubjectId, default))!;
        Assert.Equal(PostWorkOwners.Host, converted.Owner);
        Assert.Equal("awaiting-canonical-review-verdict", converted.DeferredReason);

        var claim = await store.ClaimPostWorkAsync(HostClaim(), "post-runner", default);
        Assert.Equal("claimed", claim.Status);
        Assert.Equal(subject.SubjectId, claim.Lease!.SubjectId);
        Assert.Equal(2, claim.Lease.Fence);
        Assert.Equal(1, claim.Lease.AllocationVersion);
        var backendDuringClaim = await store.BeginBackendPassAsync(Begin(subject) with { PassId = "pass-4" }, "backend", default);
        Assert.False(backendDuringClaim.Admitted);
        Assert.Equal("authority-held", backendDuringClaim.Reason);
        Assert.Contains(admitted.Record, item => item.Event == "cohort-admissions-stopped");
        Assert.Contains(admitted.Record, item => item.Event == "host-claims-admitted");
    }

    [Fact]
    public async Task Hundred_simultaneous_claims_and_backend_passes_leave_one_holder_per_subject()
    {
        using var temp = new TempDirectory();
        var (store, _, project) = await CanaryAsync(temp.Path, maxParallelism: 8);
        var subjects = new List<PostWorkSubject>();
        foreach (var task in new[] { "AGT-1", "AGT-2", "AGT-3" })
            subjects.Add(await store.MaterializePostWorkSubjectAsync(Request(project, task), "backend", default));
        await store.StartPostWorkCohortAsync(
            new StartPostWorkCohortRequest("cohort-1", project, ["AGT-1", "AGT-2", "AGT-3"]), "operator", default);
        await store.AdvancePostWorkCohortAsync("cohort-1", new AdvancePostWorkCohortRequest(
            subjects.Select(subject => Lane(project, subject.TaskId)).ToArray()), "operator", default);

        // Raising the grant to eight slots issued allocation version 2.
        var claims = Enumerable.Range(0, 100).Select(_ => store.ClaimPostWorkAsync(
            HostClaim() with { RuntimeCapacityAppliedVersion = 2 }, "post-runner", default));
        var passes = subjects.SelectMany(subject => Enumerable.Range(0, 10).Select(index =>
            store.BeginBackendPassAsync(Begin(subject) with { PassId = $"pass-{index}" }, "backend", default)));
        var claimResults = Task.WhenAll(claims);
        var passResults = await Task.WhenAll(passes);
        var claimed = (await claimResults).Where(claim => claim.Status == "claimed").ToArray();

        Assert.Equal(3, claimed.Length);
        Assert.Equal(3, claimed.Select(claim => claim.Lease!.SubjectId).Distinct().Count());
        Assert.DoesNotContain(passResults, pass => pass.Admitted);
        await using var connection = new SqliteConnection($"Data Source={store.DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT MAX(holders) FROM (SELECT COUNT(*) holders FROM post_work_attempts
             WHERE state = 'live' GROUP BY subject_id);
            """;
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Lease_loss_makes_a_late_report_inert_and_the_subject_reclaimable()
    {
        using var temp = new TempDirectory();
        var (store, clock, project) = await CanaryAsync(temp.Path);
        var subject = await HostOwnedAsync(store, project, "AGT-1");
        var first = await store.ClaimPostWorkAsync(HostClaim(ttl: 30), "post-runner", default);
        Assert.Equal("claimed", first.Status);
        clock.Advance(TimeSpan.FromSeconds(31));

        var renew = await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            store.RenewPostWorkAsync(first.Lease!.AttemptId, new PostWorkRenewRequest(Authority(first.Lease), 60), default));
        Assert.Equal("post-work-lease-lost", renew.Code);
        var late = await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            store.ReportPostWorkAsync(first.Lease!.AttemptId, Report(first.Lease, PostWorkOutcomes.Decided), "post-runner", default));
        Assert.Equal("post-work-lease-lost", late.Code);
        Assert.Equal(PostWorkStates.Running, (await store.GetPostWorkSubjectAsync(subject.SubjectId, default))!.State);

        var second = await store.ClaimPostWorkAsync(HostClaim(), "post-runner", default);
        Assert.Equal("claimed", second.Status);
        Assert.True(second.Lease!.Fence > first.Lease!.Fence);
        var stale = await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            store.ReportPostWorkAsync(first.Lease.AttemptId, Report(first.Lease, PostWorkOutcomes.Decided), "post-runner", default));
        Assert.Equal("stale-fence", stale.Code);
        var accepted = await store.ReportPostWorkAsync(second.Lease.AttemptId,
            Report(second.Lease, PostWorkOutcomes.Decided), "post-runner", default);
        Assert.Equal(PostWorkStates.Reported, accepted.State);
        var replay = await store.ReportPostWorkAsync(second.Lease.AttemptId,
            Report(second.Lease, PostWorkOutcomes.Decided), "post-runner", default);
        Assert.Equal(accepted.SubjectId, replay.SubjectId);
    }

    [Fact]
    public async Task Host_deferral_keeps_reason_and_retry_time_and_holds_the_claim_until_then()
    {
        using var temp = new TempDirectory();
        var (store, clock, project) = await CanaryAsync(temp.Path);
        var subject = await HostOwnedAsync(store, project, "AGT-1");
        var claim = await store.ClaimPostWorkAsync(HostClaim(), "post-runner", default);
        var retryAt = Now.AddMinutes(5);
        var deferred = await store.ReportPostWorkAsync(claim.Lease!.AttemptId,
            Report(claim.Lease, PostWorkOutcomes.Deferred, "rate-limit-reached", retryAt), "post-runner", default);
        Assert.Equal(PostWorkStates.Waiting, deferred.State);
        Assert.Equal("rate-limit-reached", deferred.DeferredReason);
        Assert.Equal(retryAt, deferred.RetryAt);
        Assert.Equal(1, deferred.DeferralCount);

        var early = await store.ClaimPostWorkAsync(HostClaim(), "post-runner", default);
        Assert.Equal("empty", early.Status);
        Assert.Contains("retry-pending", early.RejectedReasons!);
        var reconcile = await store.ReconcilePostWorkAsync(new ReconcilePostWorkRequest(Backend, [project],
            [Lane(project, "AGT-1")]), "backend", default);
        Assert.Equal(PostWorkReconcileActions.AwaitHost, Assert.Single(reconcile.Actions).Action);
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal("claimed", (await store.ClaimPostWorkAsync(HostClaim(), "post-runner", default)).Status);
        Assert.Equal(subject.SubjectId, deferred.SubjectId);
    }

    [Fact]
    public async Task Server_applies_a_host_report_once_and_a_recovered_decision_is_not_rerun()
    {
        using var temp = new TempDirectory();
        var (store, _, project) = await CanaryAsync(temp.Path);
        var subject = await HostOwnedAsync(store, project, "AGT-1");
        var claim = await store.ClaimPostWorkAsync(HostClaim(), "post-runner", default);
        await store.ReportPostWorkAsync(claim.Lease!.AttemptId, Report(claim.Lease, PostWorkOutcomes.Decided),
            "post-runner", default);

        var apply = await store.BeginBackendPassAsync(Begin(subject), "backend", default);
        Assert.True(apply.Admitted);
        Assert.Equal("apply-host-report", apply.Reason);
        var applied = await store.CompleteBackendPassAsync(subject.IdempotencyKey,
            Complete(apply, PostWorkOutcomes.Decided, "done-aspects"), "backend", default);
        Assert.Equal(PostWorkStates.Applied, applied.State);
        Assert.Equal("server-apply", applied.DecidedBy);
        Assert.StartsWith("decided:", applied.HostProposal);

        var rerun = await store.BeginBackendPassAsync(Begin(subject) with { PassId = "again" }, "backend", default);
        Assert.False(rerun.Admitted);
        Assert.Equal("already-applied", rerun.Reason);
        var restart = await store.ReconcilePostWorkAsync(new ReconcilePostWorkRequest("backend-b", [project],
            [Lane(project, "AGT-1")]), "backend", default);
        Assert.Empty(restart.Actions);
        Assert.Equal("empty", (await store.ClaimPostWorkAsync(HostClaim(), "post-runner", default)).Status);
    }

    [Fact]
    public async Task Restart_reconciles_lost_backend_passes_against_lane_state_before_requeue()
    {
        using var temp = new TempDirectory();
        var (store, _, project) = await CanaryAsync(temp.Path);
        var unfinished = await store.MaterializePostWorkSubjectAsync(Request(project, "AGT-1"), "backend", default);
        var decided = await store.MaterializePostWorkSubjectAsync(Request(project, "AGT-2"), "backend", default);
        var moved = await store.MaterializePostWorkSubjectAsync(Request(project, "AGT-3"), "backend", default);
        foreach (var subject in new[] { unfinished, decided, moved })
            Assert.True((await store.BeginBackendPassAsync(Begin(subject), "backend", default)).Admitted);

        var response = await store.ReconcilePostWorkAsync(new ReconcilePostWorkRequest("backend-b", [project],
        [
            Lane(project, "AGT-1"),
            Lane(project, "AGT-2") with { DecisionRecorded = true },
        ]), "backend", default);

        var actions = response.Actions.ToDictionary(action => action.TaskId, action => action.Action);
        Assert.Equal(PostWorkReconcileActions.RequeueBackend, actions["AGT-1"]);
        Assert.Equal(PostWorkReconcileActions.MarkApplied, actions["AGT-2"]);
        Assert.Equal(PostWorkReconcileActions.Retire, actions["AGT-3"]);
        var resumed = await store.BeginBackendPassAsync(Begin(unfinished) with { BackendInstanceId = "backend-b" },
            "backend", default);
        Assert.True(resumed.Admitted);
        Assert.False((await store.BeginBackendPassAsync(Begin(decided) with { BackendInstanceId = "backend-b" },
            "backend", default)).Admitted);
    }

    [Fact]
    public async Task Rollback_revokes_the_host_lease_and_returns_unfinished_cards_to_the_backend_once()
    {
        using var temp = new TempDirectory();
        var (store, _, project) = await CanaryAsync(temp.Path);
        var running = await HostOwnedAsync(store, project, "AGT-1");
        var claim = await store.ClaimPostWorkAsync(HostClaim(), "post-runner", default);
        Assert.Equal(running.SubjectId, claim.Lease!.SubjectId);

        var rollback = await store.RollbackPostWorkCohortAsync("cohort-AGT-1",
            new RollbackPostWorkCohortRequest("canary threshold missed", [Lane(project, "AGT-1")]), "operator", default);

        Assert.Equal(PostWorkCohortPhases.RolledBack, rollback.Phase);
        var requeue = Assert.Single(rollback.Actions);
        Assert.Equal(PostWorkReconcileActions.RequeueBackend, requeue.Action);
        Assert.Contains(rollback.Record, item => item.Event == "host-lease-revoked");
        Assert.Contains(rollback.Record, item => item.Event == "rolled-back");
        var late = await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            store.ReportPostWorkAsync(claim.Lease.AttemptId, Report(claim.Lease, PostWorkOutcomes.Decided),
                "post-runner", default));
        Assert.Equal("stale-fence", late.Code);
        var backend = await store.BeginBackendPassAsync(Begin(running), "backend", default);
        Assert.True(backend.Admitted);
        Assert.False((await store.BeginBackendPassAsync(Begin(running) with { PassId = "dup" }, "backend", default)).Admitted);
        Assert.Equal("empty", (await store.ClaimPostWorkAsync(HostClaim(), "post-runner", default)).Status);
        await store.CompleteBackendPassAsync(running.IdempotencyKey,
            Complete(backend, PostWorkOutcomes.Decided, "done-aspects"), "backend", default);
        var replayedRollback = await store.RollbackPostWorkCohortAsync("cohort-AGT-1",
            new RollbackPostWorkCohortRequest("replay", [Lane(project, "AGT-1")]), "operator", default);
        Assert.Empty(replayedRollback.Actions);
    }

    [Fact]
    public async Task Host_grant_is_shared_and_requires_the_current_allocation_version()
    {
        using var temp = new TempDirectory();
        var (store, _, project) = await CanaryAsync(temp.Path, maxParallelism: 1);
        await HostOwnedAsync(store, project, "AGT-1");
        await HostOwnedAsync(store, project, "AGT-2");
        var stale = await store.ClaimPostWorkAsync(HostClaim() with { RuntimeCapacityAppliedVersion = 1 }, "post-runner", default);
        Assert.Equal("empty", stale.Status);
        Assert.Contains("allocation-version-mismatch", stale.RejectedReasons!);

        var current = HostClaim() with { RuntimeCapacityAppliedVersion = 2 };
        Assert.Equal("claimed", (await store.ClaimPostWorkAsync(current, "post-runner", default)).Status);
        var full = await store.ClaimPostWorkAsync(current, "post-runner", default);
        Assert.Equal("empty", full.Status);
        Assert.Contains("host-grant-mismatch", full.RejectedReasons!);
    }

    /// <summary>
    /// The migration drill: shadow, restart into canary, drain one cohort,
    /// host claims, a deferral, a server-applied host report, then rollback.
    /// Every card ends applied or returned to the backend exactly once. With
    /// POST_WORK_DRILL_RECORD set, the cohort record is written there.
    /// </summary>
    [Fact]
    public async Task Cohort_canary_and_rollback_drill_leaves_no_missed_card_or_duplicate_decision()
    {
        using var temp = new TempDirectory();
        var (shadow, _, project) = await SetupAsync(temp.Path, "shadow");
        var cards = new[] { "AGT-1", "AGT-2", "AGT-3", "AGT-4" };
        var subjects = new Dictionary<string, PostWorkSubject>();
        foreach (var card in cards)
            subjects[card] = await shadow.MaterializePostWorkSubjectAsync(
                Request(project, card) with { Source = card == "AGT-4" ? "startup-recovery" : "transition" },
                "backend", default);
        var shadowPass = await shadow.BeginBackendPassAsync(Begin(subjects["AGT-1"]), "backend", default);
        await shadow.CompleteBackendPassAsync(subjects["AGT-1"].IdempotencyKey,
            Complete(shadowPass, PostWorkOutcomes.Decided, "done-aspects"), "backend", default);
        var interrupted = await shadow.BeginBackendPassAsync(Begin(subjects["AGT-2"]), "backend", default);
        Assert.True(interrupted.Admitted);

        // Operator switch: the Task Server restarts in canary; the backend restarts as instance b.
        var clock = new ManualTimeProvider(new DateTimeOffset(Now.AddMinutes(1)));
        var store = new TaskServerStore(Options.Create(new TaskServerOptions
        {
            DataDirectory = temp.Path, PostWorkMode = "canary", MinimumLeaseSeconds = 30,
        }), clock);
        await store.InitializeAsync();
        var lanes = cards.Select(card => card == "AGT-1"
            ? Lane(project, card) with { DecisionRecorded = true }
            : Lane(project, card)).ToArray();
        var restart = await store.ReconcilePostWorkAsync(new ReconcilePostWorkRequest("backend-b", [project], lanes),
            "backend", default);
        Assert.DoesNotContain(restart.Actions, action => action.TaskId == "AGT-1");
        Assert.All(restart.Actions, action => Assert.Equal(PostWorkReconcileActions.RequeueBackend, action.Action));

        var running = await store.BeginBackendPassAsync(Begin(subjects["AGT-3"]) with { BackendInstanceId = "backend-b" },
            "backend", default);
        Assert.True(running.Admitted);
        await store.StartPostWorkCohortAsync(new StartPostWorkCohortRequest("canary-1", project, ["AGT-2", "AGT-3"]),
            "operator", default);
        Assert.Equal(PostWorkCohortPhases.Draining,
            (await store.AdvancePostWorkCohortAsync("canary-1", new AdvancePostWorkCohortRequest(lanes), "operator", default)).Phase);
        await store.CompleteBackendPassAsync(subjects["AGT-3"].IdempotencyKey,
            Complete(running, PostWorkOutcomes.Deferred, "awaiting-canonical-review-verdict", Now.AddMinutes(1)),
            "backend", default);
        var admitted = await store.AdvancePostWorkCohortAsync("canary-1", new AdvancePostWorkCohortRequest(lanes),
            "operator", default);
        Assert.Equal(PostWorkCohortPhases.Host, admitted.Phase);

        var claims = new List<PostWorkClaimResponse>();
        for (var i = 0; i < 2; i++) claims.Add(await store.ClaimPostWorkAsync(HostClaim(), "post-runner", default));
        Assert.All(claims, claim => Assert.Equal("claimed", claim.Status));
        var byTask = claims.ToDictionary(claim => claim.Subject!.TaskId);
        await store.ReportPostWorkAsync(byTask["AGT-2"].Lease!.AttemptId,
            Report(byTask["AGT-2"].Lease!, PostWorkOutcomes.Decided), "post-runner", default);
        await store.ReportPostWorkAsync(byTask["AGT-3"].Lease!.AttemptId,
            Report(byTask["AGT-3"].Lease!, PostWorkOutcomes.Deferred, "rate-limit-reached", Now.AddMinutes(10)),
            "post-runner", default);
        var apply = await store.BeginBackendPassAsync(Begin(subjects["AGT-2"]) with { BackendInstanceId = "backend-b" },
            "backend", default);
        Assert.Equal("apply-host-report", apply.Reason);
        await store.CompleteBackendPassAsync(subjects["AGT-2"].IdempotencyKey,
            Complete(apply, PostWorkOutcomes.Decided, "done-aspects"), "backend", default);
        // A card outside the cohort keeps the old backend queue path.
        var unconverted = await store.BeginBackendPassAsync(Begin(subjects["AGT-4"]) with { BackendInstanceId = "backend-b" },
            "backend", default);
        Assert.True(unconverted.Admitted);
        await store.CompleteBackendPassAsync(subjects["AGT-4"].IdempotencyKey,
            Complete(unconverted, PostWorkOutcomes.Decided, "noop"), "backend", default);

        var rollback = await store.RollbackPostWorkCohortAsync("canary-1",
            new RollbackPostWorkCohortRequest("drill rollback", lanes), "operator", default);
        var deferredBack = Assert.Single(rollback.Actions);
        Assert.Equal("AGT-3", deferredBack.TaskId);
        Assert.Equal(PostWorkReconcileActions.RetainDeferral, deferredBack.Action);
        var finalSubject = (await store.GetPostWorkSubjectAsync(subjects["AGT-3"].SubjectId, default))!;
        Assert.Equal("rate-limit-reached", finalSubject.DeferredReason);
        Assert.Equal(Now.AddMinutes(10), finalSubject.RetryAt);
        Assert.Equal(PostWorkOwners.Backend, finalSubject.Owner);

        var applied = new List<string>();
        foreach (var card in cards)
        {
            var subject = (await store.GetPostWorkSubjectAsync(subjects[card].SubjectId, default))!;
            if (subject.State == PostWorkStates.Applied) applied.Add(card);
        }
        Assert.Equal(["AGT-1", "AGT-2", "AGT-4"], applied);
        await using (var connection = new SqliteConnection($"Data Source={store.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM post_work_events WHERE event = 'outcome-decided';";
            Assert.Equal(3L, (long)(await command.ExecuteScalarAsync())!);
        }
        if (Environment.GetEnvironmentVariable("POST_WORK_DRILL_RECORD") is { Length: > 0 } recordPath)
            await File.WriteAllTextAsync(recordPath, System.Text.Json.JsonSerializer.Serialize(new
            {
                restart = restart.Actions,
                canary = admitted,
                rollback,
                subjects = await Task.WhenAll(cards.Select(card => store.GetPostWorkSubjectAsync(subjects[card].SubjectId, default))),
            }, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { WriteIndented = true }));
    }

    private static PostWorkSubjectFacts Facts(string owner, string state, string phase, string? liveKind, string instance)
        => new(owner, state, phase, "lane-1", null, liveKind, liveKind is null ? null : instance,
            liveKind is null ? null : Now.AddMinutes(1));

    private static Task<(TaskServerStore Store, ManualTimeProvider Clock, string ProjectId)> CanaryAsync(
        string path, int maxParallelism = 4)
        => SetupAsync(path, "canary", maxParallelism);

    private static async Task<(TaskServerStore Store, ManualTimeProvider Clock, string ProjectId)> SetupAsync(
        string path, string mode, int maxParallelism = 4)
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(Now));
        var store = new TaskServerStore(Options.Create(new TaskServerOptions
        {
            DataDirectory = path,
            PostWorkMode = mode,
            MinimumLeaseSeconds = 30,
        }), clock);
        await store.InitializeAsync();
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Workspace"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Project", "PROJ"), "test", default);
        await store.RegisterRunnerAsync("coding-runner", new RegisterRunnerRequest("coding-runner", "host",
            "instance", "1.0", TaskServerProtocol.Current, [ReviewCapabilities.CodingExecutor],
            BootstrapMaxParallelism: 4), "coding-runner", default);
        await store.RegisterRunnerAsync("post-runner", new RegisterRunnerRequest("post-runner", "host",
            "instance", "1.0", TaskServerProtocol.Current, [PostWorkCapabilities.Executor]), "post-runner", default);
        await store.UpsertHostPlacementAsync("host", new UpsertHostPlacementRequest("coding-runner", "instance",
            ["coding", "post"], "linux", "x64", ["container"], ["sha256:a"], ["codex"], [project.ProjectId], [], 0),
            "coding-runner", default);
        if (maxParallelism != 4)
        {
            var capacity = (await store.GetRuntimeCapacitySettingsAsync("host", default))!;
            await store.UpdateRuntimeCapacitySettingsAsync("host",
                new UpdateRuntimeCapacitySettingsRequest(maxParallelism, 80, "balanced", capacity.Version),
                "operator", default);
        }
        // The advertising runner acknowledges the allocation it runs under.
        var applied = (await store.GetRuntimeCapacitySettingsAsync("host", default))!.AllocationVersion;
        await store.ClaimAsync(new ClaimRequest("coding-runner", "instance", AvailableSlots: 0,
            EffectiveMaxParallelism: maxParallelism, RuntimeCapacityAppliedVersion: applied), "coding-runner", default);
        return (store, clock, project.ProjectId);
    }

    private static async Task<PostWorkSubject> HostOwnedAsync(TaskServerStore store, string project, string task)
    {
        var subject = await store.MaterializePostWorkSubjectAsync(Request(project, task), "backend", default);
        await store.StartPostWorkCohortAsync(new StartPostWorkCohortRequest("cohort-" + task, project, [task]),
            "operator", default);
        var cohort = await store.AdvancePostWorkCohortAsync("cohort-" + task, Advance(project, task), "operator", default);
        Assert.Equal(PostWorkCohortPhases.Host, cohort.Phase);
        return subject;
    }

    private static MaterializePostWorkSubjectRequest Request(string project, string task)
        => new(project, task, "lane-1", "run-1", ["post:review"],
            [new PostWorkArtifactReference("result-sha", "abc123")],
            [new PostWorkStep("lint", "npm", ["run", "lint"], 300)], "transition");

    private static BeginBackendPassRequest Begin(PostWorkSubject subject)
        => new(subject.IdempotencyKey, Backend, "pass-1", 600);

    private static CompleteBackendPassRequest Complete(
        BackendPassDecision pass, string outcome, string reason, DateTime? retryAt = null)
        => new(pass.Lease!.InstanceId, pass.Lease.LeaseId, pass.Lease.Fence, outcome, reason, retryAt);

    private static PostWorkClaimRequest HostClaim(int ttl = 120)
        => new("post-runner", "instance", ttl, 1);

    private static PostWorkAuthority Authority(PostWorkLease lease)
        => new(lease.ExecutorId, lease.InstanceId, lease.LeaseId, lease.Fence, lease.AuthorityEpoch);

    private static SubmitPostWorkReportRequest Report(
        PostWorkLease lease, string outcome, string reason = "steps-passed", DateTime? retryAt = null)
        => new(Authority(lease), new PostWorkReport(outcome, reason, retryAt, PostWorkExecutorKinds.Container,
            [new PostWorkStepEvidence("lint", "passed", 0, 1200, "sha256:out", "ok")], "clean"));

    private static PostWorkLaneFact Lane(string project, string task)
        => new(project, task, "lane-1", true, false);

    private static AdvancePostWorkCohortRequest Advance(string project, string task)
        => new([Lane(project, task)]);
}
