using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Contract = AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3617 (playthrough AGT-3560, finding H1). The probe always released the
/// card that waited longest. That card's delivery still carried the broken
/// test, so every re-check failed and the breaker cleared only after the card
/// was deleted. The re-check now releases the newest waiting delivery, or the
/// card the owner chose, so a fixed delivery clears the breaker.
/// </summary>
public sealed class CauseBreakerRecheckTests : IDisposable
{
    private const string FailureText = "test report results/test-report.json is missing";
    private static readonly DateTime T0 = new(2026, 11, 9, 8, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cause-recheck-" + Guid.NewGuid().ToString("N"));
    private readonly MutableClock _clock = new(T0);
    private readonly string _project;
    private readonly TaskScannerService _scanner;
    private readonly TaskMutationService _mutations;
    private readonly AttemptAuthorityService _authority;
    private readonly CauseBreakerService _breaker;
    private readonly RecordingRelease _release = new();
    private readonly string _repositoryId = Contract.RepositoryIdentityContract.FromUrl("https://example.invalid/demo.git")!;
    private readonly CauseFingerprint _fingerprint = CauseFingerprintPolicy.Compute(
        "ReviewInfra", "TestReportUnavailable", FailureText, 1, "tool:dotnet");

    public CauseBreakerRecheckTests()
    {
        _project = Path.Combine(_root, "projects", "demo");
        Directory.CreateDirectory(_project);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
            ["WatchPaths:0:Name"] = "demo",
            ["WatchPaths:0:Path"] = _project,
            ["WatchPaths:0:RootPath"] = _project,
        }).Build();
        _scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
        var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        registry.EnsureProjectForStorage(_project, "demo", DefaultWorkspace.Id);
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        _mutations = new TaskMutationService(_scanner,
            new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance), registry,
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance, timeline);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        _authority = new AttemptAuthorityService(config, NullLogger<AttemptAuthorityService>.Instance,
            utcNow: () => _clock.GetUtcNow().UtcDateTime);
        var interventions = new FailureInterventionService(_mutations, _scanner, timeline,
            new OrchestratorLog(NullLogger<OrchestratorLog>.Instance),
            NullLogger<FailureInterventionService>.Instance, prompts, time: _clock);
        _breaker = new CauseBreakerService(config, settings, interventions, _scanner, _authority, timeline,
            _release, new FileCauseWaitMarkerStore(NullLogger<FileCauseWaitMarkerStore>.Instance),
            NullLogger<CauseBreakerService>.Instance, time: _clock);
    }

    [Fact]
    public void Fail_three_times_fix_recheck_releases_the_fixed_delivery_and_clears_the_breaker()
    {
        // Fail x3: the first card's delivery breaks the test report.
        var broken = DeliveredCard("Broken test", T0);
        var opened = FailThreeTimes(broken);
        var causeKey = opened.CauseKey!;
        Assert.NotNull(CauseWaitMarker.TryRead(broken.FolderPath));

        // Fix: a later delivery carries the corrected test and is held before
        // its review is claimed, like every review behind the open cause.
        var fixedCard = DeliveredCard("Fixed test", T0.AddMinutes(30));
        Assert.True(_breaker.HoldBeforeClaim(fixedCard, fixedCard.Key!, plan: null));
        var wait = CauseWaitMarker.ToStatus(CauseWaitMarker.TryRead(fixedCard.FolderPath));
        Assert.Equal(causeKey, wait!.CauseKey);
        Assert.Equal("review cannot read the test report", wait.Summary);

        var view = _breaker.ForCause(causeKey)!;
        Assert.Equal("review cannot read the test report", view.Summary);
        Assert.Equal([fixedCard.Key, broken.Key], view.Held.Select(card => card.Key).ToArray());
        Assert.True(view.Held[0].RecheckFirst);
        Assert.False(view.Held[1].RecheckFirst);
        Assert.Equal(T0.AddMinutes(30), view.Held[0].DeliveredAt);

        // Re-check: the newest delivery is the probe, not the card that waited longest.
        var probe = _breaker.RequestProbe(opened.Fingerprint)!;
        Assert.Equal(fixedCard.Key, probe.ProbeTaskKey);
        Assert.Equal(fixedCard.Key, _breaker.ForCause(causeKey)!.ProbeKey);
        Assert.Null(CauseWaitMarker.ToStatus(CauseWaitMarker.TryRead(fixedCard.FolderPath)));

        // Release: the green probe closes the breaker and plans one fresh
        // review for the card that is still waiting.
        var closed = _breaker.ObserveGreen(fixedCard.Key!, probe.ProbeAttemptId!)!;
        Assert.False(closed.IsOpen);
        Assert.Equal("probe-green", closed.CloseReason);
        Assert.Equal([fixedCard.Id, broken.Id], _release.Tasks);
        Assert.Null(CauseWaitMarker.TryRead(broken.FolderPath));
        Assert.Null(CauseWaitMarker.TryRead(fixedCard.FolderPath));
        Assert.Null(_breaker.ForCause(causeKey));
    }

    [Fact]
    public void Probe_ranks_by_delivery_age_not_by_how_long_a_card_waited()
    {
        var broken = DeliveredCard("Broken test", T0);
        var opened = FailThreeTimes(broken);
        // Delivered at 08:40 and held at 08:40.
        var newest = DeliveredCard("Newest delivery", T0.AddMinutes(40));
        Assert.True(_breaker.HoldBeforeClaim(newest, newest.Key!, plan: null));
        // Delivered at 08:20, but its review was only planned and held at 09:00.
        var older = DeliveredCard("Older delivery", T0.AddMinutes(20), reviewAt: T0.AddMinutes(60));
        Assert.True(_breaker.HoldBeforeClaim(older, older.Key!, plan: null));

        Assert.Equal([newest.Key, older.Key, broken.Key],
            _breaker.ForCause(opened.CauseKey!)!.Held.Select(card => card.Key).ToArray());
        Assert.Equal(newest.Key, _breaker.RequestProbe(opened.Fingerprint)!.ProbeTaskKey);
    }

    [Fact]
    public void Owner_can_choose_the_card_to_recheck()
    {
        var broken = DeliveredCard("Broken test", T0);
        var opened = FailThreeTimes(broken);
        var fixedCard = DeliveredCard("Fixed test", T0.AddMinutes(30));
        Assert.True(_breaker.HoldBeforeClaim(fixedCard, fixedCard.Key!, plan: null));

        Assert.Null(_breaker.RequestProbe(opened.Fingerprint, "DEMO-404")!.ProbeTaskKey);
        Assert.Equal(broken.Key, _breaker.RequestProbe(opened.Fingerprint, broken.Key)!.ProbeTaskKey);
        // A running probe is not replaced by a second request.
        Assert.Equal(broken.Key, _breaker.RequestProbe(opened.Fingerprint, fixedCard.Key)!.ProbeTaskKey);
    }

    [Fact]
    public void A_card_that_holds_nothing_has_no_cause_view()
    {
        var card = DeliveredCard("Unrelated", T0);
        Assert.Null(_breaker.ForCause(card.Key!));
        Assert.Null(_breaker.ForCause(""));
    }

    [Theory]
    [InlineData("ReviewInfra/TestReportUnavailable", "review cannot read the test report")]
    [InlineData("ReviewInfra/ToolUnavailable", "a review tool is unavailable")]
    [InlineData("ReviewInfra/PreparationFailed", "review preparation fails")]
    [InlineData("ReviewInfra/NewlyInvented", "review keeps failing (ReviewInfra/NewlyInvented)")]
    [InlineData(null, "review keeps failing for the same cause")]
    public void Wording_names_the_cause_in_plain_words(string? failureClass, string expected)
        => Assert.Equal(expected, CauseBreakerWording.Summary(failureClass));

    [Fact]
    public void A_marker_written_before_the_summary_existed_still_reads_one()
    {
        var status = CauseWaitMarker.ToStatus(new CauseWaitRecord
        {
            CauseKey = "DEMO-2",
            Fingerprint = "fp",
            FailureClass = "ReviewInfra/TestReportUnavailable",
        });
        Assert.Equal("review cannot read the test report", status!.Summary);
    }

    [Fact]
    public void Owner_decision_applies_only_to_a_code_free_intervention_moved_by_an_operator()
    {
        var facts = new InterventionOwnerDecisionFacts(TaskStates.Completed, OperatorOverride: false,
            HasOperatorAuthority: true, IsInterventionFollowUp: true, HasAttributedCommits: false, HasRunAttempt: false);
        Assert.True(InterventionOwnerDecisionPolicy.Applies(facts));
        Assert.False(InterventionOwnerDecisionPolicy.Applies(facts with { TargetState = TaskStates.HumanReview }));
        Assert.False(InterventionOwnerDecisionPolicy.Applies(facts with { OperatorOverride = true }));
        Assert.False(InterventionOwnerDecisionPolicy.Applies(facts with { HasOperatorAuthority = false }));
        Assert.False(InterventionOwnerDecisionPolicy.Applies(facts with { IsInterventionFollowUp = false }));
        Assert.False(InterventionOwnerDecisionPolicy.Applies(facts with { HasAttributedCommits = true }));
        Assert.False(InterventionOwnerDecisionPolicy.Applies(facts with { HasRunAttempt = true }));
        Assert.Equal(InterventionOwnerDecisionPolicy.RecordedReason, InterventionOwnerDecisionPolicy.Reason(null));
        Assert.Equal(InterventionOwnerDecisionPolicy.RecordedReason, InterventionOwnerDecisionPolicy.Reason("ok"));
        Assert.Equal("Fixed the test fixture.", InterventionOwnerDecisionPolicy.Reason(" Fixed the test fixture. "));
    }

    private CauseBreakerRecord FailThreeTimes(TaskInfo card)
    {
        var evidence = new FailureCommandEvidence("TestReportUnavailable", "ReviewInfra", 1, 0,
            FailureText, "", OccurredAt: _clock.GetUtcNow().UtcDateTime);
        for (var attempt = 1; attempt <= 3; attempt++)
            _breaker.Observe(card, card.Key!, $"{card.Key}-review-{attempt}", _fingerprint, evidence, FailureText);
        return Assert.Single(_breaker.List(openOnly: true));
    }

    /// <summary>A card in Post Processing whose run finished at <paramref name="deliveredAt"/>.</summary>
    private TaskInfo DeliveredCard(string title, DateTime deliveredAt, DateTime? reviewAt = null)
    {
        _clock.Set(deliveredAt);
        var id = _mutations.CreateJob(new CreateTaskRequest
        {
            Title = title, WatchPath = _project, TargetState = TaskStates.AutoReview,
            PromptMarkdown = "Change the code and keep the tests green.",
        });
        var card = _scanner.FindJob(id!, _project)!;
        var resultSha = Guid.NewGuid().ToString("N") + "00000000";
        var run = _authority.AcquireRun(card.Key!, _repositoryId, null, "runner", "coding-host", 120,
            $"run-{card.Key}").RunAttempt!;
        Assert.True(_authority.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(run.AttemptId, run.LastFence, run.AuthorityEpoch, $"settle-{card.Key}"),
            Outcome = "done",
            ResultSha = resultSha,
        }).Accepted);
        if (reviewAt is { } at) _clock.Set(at);
        Assert.True(_authority.CreateReviewAttempt(new CreateReviewAttemptRequest(card.Key!, _repositoryId,
            resultSha, run.AttemptId, "requirements", "policy", [], $"review-{card.Key}")).Accepted);
        return card;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class MutableClock(DateTime now) : TimeProvider
    {
        private DateTime _now = now;
        public void Set(DateTime value) => _now = value;
        public override DateTimeOffset GetUtcNow() => new(_now, TimeSpan.Zero);
    }

    private sealed class RecordingRelease : ICauseWaitRelease
    {
        public List<string> Tasks { get; } = [];
        public string? Release(TaskInfo task, string deliveryKey)
        {
            Tasks.Add(task.Id);
            return "release-" + task.Id;
        }
    }
}
