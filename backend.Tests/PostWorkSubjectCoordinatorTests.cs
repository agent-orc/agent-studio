using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tests;

/// <summary>AGT-3171 DP-S8: the backend side of post-work subjects.</summary>
public sealed class PostWorkSubjectCoordinatorTests
{
    private static readonly DateTime Entered = new(2026, 10, 28, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Subject_request_carries_lane_entry_source_run_artifacts_steps_and_project_mapping()
    {
        var coordinator = Coordinator("shadow", new FakePostWorkAuthority(), new()
        {
            ["PostWork:ProjectIds:demo"] = "prj_demo",
            ["PostWork:RequiredCapabilities:0"] = "os:linux",
            ["PostWork:Steps:0:StepId"] = "lint",
            ["PostWork:Steps:0:FileName"] = "npm",
            ["PostWork:Steps:0:Arguments:0"] = "run",
            ["PostWork:Steps:0:Arguments:1"] = "lint",
            ["PostWork:Steps:0:TimeoutSeconds"] = "120",
        });
        var request = coordinator.BuildRequest(Card("job-1", key: "AGT-1"), "progress-to-auto-review");

        Assert.Equal("prj_demo", request.ProjectId);
        Assert.Equal("AGT-1", request.TaskId);
        Assert.Equal(Entered.ToString("O"), request.LaneEntryId);
        Assert.Equal("session-2", request.SourceRunId);
        Assert.Equal(["os:linux"], request.RequiredCapabilities);
        Assert.Equal("abc123", Assert.Single(request.Artifacts).Locator);
        var step = Assert.Single(request.Steps);
        Assert.Equal(["run", "lint"], step.Arguments);
        Assert.Equal(120, step.TimeoutSeconds);
        Assert.Equal("job-2", coordinator.BuildRequest(Card("job-2"), "test").TaskId);
    }

    [Fact]
    public async Task Off_and_missing_task_server_leave_the_queue_unmanaged()
    {
        var off = Coordinator("off", new FakePostWorkAuthority());
        Assert.Same(Runner.PostWorkPassAdmission.Unmanaged, await off.AdmitAsync(Card("job"), "pass", default));
        var noAuthority = new Runner.PostWorkSubjectCoordinator(Config("canary"),
            NullLogger<Runner.PostWorkSubjectCoordinator>.Instance);
        Assert.Equal(Contract.PostWorkModes.Off, noAuthority.Mode);
    }

    [Fact]
    public async Task Shadow_never_refuses_a_backend_pass_even_without_the_authority()
    {
        var authority = new FakePostWorkAuthority { Throw = true };
        var admission = await Coordinator("shadow", authority).AdmitAsync(Card("job"), "pass", default);
        Assert.True(admission.Admitted);
        Assert.Equal(Runner.PostWorkSubjectCoordinator.AuthorityUnavailable, admission.Reason);

        var refused = new FakePostWorkAuthority { Decision = new Contract.BackendPassDecision(true, "shadow-observed:authority-held") };
        Assert.True((await Coordinator("shadow", refused).AdmitAsync(Card("job"), "pass", default)).Admitted);
    }

    [Theory]
    [InlineData("host-owned", false, false)]
    [InlineData("admission-stopped", false, false)]
    [InlineData("authority-held", true, false)]
    [InlineData("already-applied", true, false)]
    public async Task Canary_refusals_name_who_owns_the_card(string reason, bool ownedElsewhere, bool retryable)
    {
        var authority = new FakePostWorkAuthority { Decision = new Contract.BackendPassDecision(false, reason) };
        var admission = await Coordinator("canary", authority).AdmitAsync(Card("job"), "pass", default);
        Assert.False(admission.Admitted);
        Assert.Equal(ownedElsewhere, admission.OwnedElsewhere);
        Assert.Equal(retryable, admission.Retryable);
    }

    [Fact]
    public async Task Canary_without_the_authority_waits_instead_of_risking_a_second_holder()
    {
        var admission = await Coordinator("canary", new FakePostWorkAuthority { Throw = true })
            .AdmitAsync(Card("job"), "pass", default);
        Assert.False(admission.Admitted);
        Assert.True(admission.Retryable);
    }

    [Fact]
    public async Task Completion_records_the_outcome_and_only_a_deferral_keeps_its_retry_time()
    {
        var authority = new FakePostWorkAuthority();
        var coordinator = Coordinator("canary", authority);
        var admission = await coordinator.AdmitAsync(Card("job"), "pass", default);
        var retryAt = Entered.AddMinutes(2);
        await coordinator.CompleteAsync(admission, Runner.PostProcessingCardResult.Deferred("rate-limit-reached"), retryAt, default);
        await coordinator.CompleteAsync(admission, Runner.PostProcessingCardResult.Decided("noop"), retryAt, default);
        await coordinator.CompleteAsync(admission, Runner.PostProcessingCardResult.Blocked("watch-path-unresolved"), retryAt, default);

        Assert.Collection(authority.Completed,
            item => { Assert.Equal("deferred", item.Outcome); Assert.Equal(retryAt, item.RetryAt); },
            item => { Assert.Equal("decided", item.Outcome); Assert.Null(item.RetryAt); },
            item => { Assert.Equal("blocked", item.Outcome); Assert.Equal("watch-path-unresolved", item.Reason); });
        Assert.All(authority.Completed, item => Assert.Equal(coordinator.BackendInstanceId, item.BackendInstanceId));
    }

    [Fact]
    public void Rollback_requeues_unfinished_cards_once_and_keeps_a_deferral_until_its_retry_time()
    {
        var coordinator = Coordinator("canary", new FakePostWorkAuthority());
        var cards = new[] { Card("job-1"), Card("job-2"), Card("job-3"), Card("job-4") };
        var actions = new[]
        {
            Action("job-1", Contract.PostWorkReconcileActions.RequeueBackend),
            Action("job-2", Contract.PostWorkReconcileActions.RetainDeferral, Entered.AddMinutes(5)),
            Action("job-3", Contract.PostWorkReconcileActions.MarkApplied),
            Action("job-4", Contract.PostWorkReconcileActions.Retire),
            Action("gone", Contract.PostWorkReconcileActions.RequeueBackend),
        };
        var requeueCalls = new List<string>();
        var scheduledAt = new List<DateTime?>();

        var (requeued, scheduled) = Runner.PostWorkEndpoints.ApplyRollback(coordinator, actions, cards, Entered,
            card => { requeueCalls.Add(card.Id); return true; },
            (_, action) => scheduledAt.Add(action.RetryAt));

        Assert.Equal(["job-1"], requeued);
        Assert.Equal(["job-1"], requeueCalls);
        Assert.Equal(["job-2"], scheduled);
        Assert.Equal([Entered.AddMinutes(5)], scheduledAt);
    }

    private static Contract.PostWorkReconcileAction Action(string task, string action, DateTime? retryAt = null)
        => new("pws_" + task, "pw_" + task, "demo", task, action, "test", retryAt);

    internal static Runner.PostWorkSubjectCoordinator Coordinator(
        string mode, FakePostWorkAuthority authority, Dictionary<string, string?>? extra = null)
        => new(Config(mode, extra), NullLogger<Runner.PostWorkSubjectCoordinator>.Instance, authority);

    private static IConfiguration Config(string mode, Dictionary<string, string?>? extra = null)
    {
        var values = new Dictionary<string, string?> { ["PostWork:Mode"] = mode };
        foreach (var (key, value) in extra ?? []) values[key] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static TaskInfo Card(string id, string? key = null) => new()
    {
        Id = id,
        Key = key,
        ProjectName = "demo",
        State = TaskStates.AutoReview,
        EnteredLaneAt = Entered,
        SessionChain = ["session-1", "session-2"],
        Commit = new TaskCommitInfo { Sha = "abc123" },
    };
}

/// <summary>In-memory post-work authority for backend tests.</summary>
internal sealed class FakePostWorkAuthority : Runner.IPostWorkAuthorityClient
{
    private static readonly DateTime FakeNow = new(2026, 10, 28, 8, 0, 0, DateTimeKind.Utc);
    public bool Throw { get; set; }
    public Contract.BackendPassDecision? Decision { get; set; }
    public List<Contract.MaterializePostWorkSubjectRequest> Materialized { get; } = [];
    public List<Contract.CompleteBackendPassRequest> Completed { get; } = [];

    public Task<Contract.PostWorkSubject> MaterializeAsync(Contract.MaterializePostWorkSubjectRequest request, CancellationToken ct)
    {
        if (Throw) throw new HttpRequestException("authority down");
        lock (Materialized) Materialized.Add(request);
        return Task.FromResult(Subject(request));
    }

    public Task<Contract.BackendPassDecision> BeginBackendPassAsync(Contract.BeginBackendPassRequest request, CancellationToken ct)
    {
        if (Throw) throw new HttpRequestException("authority down");
        return Task.FromResult(Decision ?? new Contract.BackendPassDecision(true, "backend-owned", null,
            new Contract.PostWorkLease("pwa_1", "pws_1", "pwl_1", "backend", "backend", request.BackendInstanceId,
                null, 1, 1, FakeNow, FakeNow.AddHours(1), 0, 0)));
    }

    public Task<Contract.PostWorkSubject> CompleteBackendPassAsync(
        string idempotencyKey, Contract.CompleteBackendPassRequest request, CancellationToken ct)
    {
        lock (Completed) Completed.Add(request);
        return Task.FromResult(Subject(null));
    }

    public Task<Contract.ReconcilePostWorkResponse> ReconcileAsync(Contract.ReconcilePostWorkRequest request, CancellationToken ct)
        => Task.FromResult(new Contract.ReconcilePostWorkResponse([]));

    public Task<Contract.PostWorkCohortStatus> StartCohortAsync(Contract.StartPostWorkCohortRequest request, CancellationToken ct)
        => throw new NotSupportedException();

    public Task<Contract.PostWorkCohortStatus> AdvanceCohortAsync(
        string cohortId, Contract.AdvancePostWorkCohortRequest request, CancellationToken ct)
        => throw new NotSupportedException();

    public Task<Contract.PostWorkCohortStatus> RollbackCohortAsync(
        string cohortId, Contract.RollbackPostWorkCohortRequest request, CancellationToken ct)
        => throw new NotSupportedException();

    public Task<Contract.PostWorkShadowSummary> ShadowAsync(CancellationToken ct) => throw new NotSupportedException();

    private static Contract.PostWorkSubject Subject(Contract.MaterializePostWorkSubjectRequest? request)
        => new("pws_1", "pw_key", request?.ProjectId ?? "demo", request?.TaskId ?? "job", request?.LaneEntryId ?? "lane",
            request?.SourceRunId, [], [], [], "sha256:x", "backend", "queued", "none", null, 0, 1, null, null, 0,
            null, null, null, null, null, "eligible", [], null, FakeNow, FakeNow);
}
