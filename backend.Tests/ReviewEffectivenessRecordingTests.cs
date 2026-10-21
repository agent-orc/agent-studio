using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3269: reviews and gates recorded through <see cref="PipelineExecutionLog"/>
/// land in the review-effectiveness ledger, later evidence attributes misses
/// to the approvals of the same delivery, and the reads aggregate them.
/// </summary>
public sealed class ReviewEffectivenessRecordingTests : IDisposable
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero));
    private readonly string _root = Path.Combine(Path.GetTempPath(), "review-effectiveness-" + Guid.NewGuid().ToString("N"));
    private readonly string _jobFolder;
    private readonly ReviewEffectivenessStore _store;
    private readonly ReviewEffectivenessService _service;
    private readonly PipelineExecutionLog _log;

    public ReviewEffectivenessRecordingTests()
    {
        _jobFolder = Path.Combine(_root, "jobs", "AGT-1");
        Directory.CreateDirectory(_jobFolder);
        _store = new ReviewEffectivenessStore(Path.Combine(_root, "ledger"));
        _service = new ReviewEffectivenessService(_store, _clock, NullLogger<ReviewEffectivenessService>.Instance);
        _log = new PipelineExecutionLog(TokenTestServices.Pricing(), NullLogger<PipelineExecutionLog>.Instance, _service);
        _log.Begin(_jobFolder, PipelineCatalogue.Standard, project: "demo", jobId: "AGT-1", nowUtc: Now);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private DateTime Tick()
    {
        _clock.Advance(TimeSpan.FromMinutes(1));
        return Now;
    }

    private void CodingRun()
    {
        var started = Tick();
        _log.RecordStep(_jobFolder, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.CoreAgentRunStepId,
            Kind = StepKind.Core,
            Status = PipelineStepStatus.Passed,
            StartedAt = started,
            CompletedAt = Tick(),
        });
    }

    private void Aspect(string aspect, string verdict, string model = "gpt-6-sol", string thinking = "high",
        string? carriedOverFrom = null, PipelineStepStatus status = PipelineStepStatus.Passed)
    {
        var started = Tick();
        _log.RecordStep(_jobFolder, new PipelineStepExecution
        {
            StepId = $"aspect-{aspect}",
            Kind = StepKind.Aspect,
            Status = status,
            Model = model,
            ThinkingLevel = thinking,
            StartedAt = started,
            CompletedAt = Tick(),
            DurationMs = 60_000,
            InputTokens = 12_000,
            OutputTokens = 800,
            CacheReadTokens = 4_000,
            CacheCreationTokens = 100,
            Verdict = verdict,
            CarriedOverFrom = carriedOverFrom,
            ExecutionLocation = carriedOverFrom is null ? "remote" : "carried-over",
        });
    }

    private void Grade(string grade, string report = "")
    {
        var file = $"code-review-grade-{Guid.NewGuid():N}.md";
        File.WriteAllText(Path.Combine(_jobFolder, file), report);
        var started = Tick();
        _log.RecordStep(_jobFolder, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.CodeReviewGradeStepId,
            Kind = StepKind.Orchestrator,
            Status = grade == "D" ? PipelineStepStatus.Failed : PipelineStepStatus.Passed,
            Model = "claude-opus-5-5",
            ThinkingLevel = "xhigh",
            StartedAt = started,
            CompletedAt = Tick(),
            DurationMs = 90_000,
            InputTokens = 30_000,
            OutputTokens = 2_000,
            Verdict = grade,
            EvidenceRef = file,
        });
    }

    private void Gate(PipelineStepStatus status, string? verdict, string? location = null)
    {
        var started = Tick();
        _log.RecordStep(_jobFolder, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.BuildTestGateStepId,
            Kind = StepKind.Tool,
            Status = status,
            StartedAt = started,
            CompletedAt = Tick(),
            Verdict = verdict,
            Reason = "OrchestratorApi.Tests failed",
            ExecutionLocation = location,
        });
    }

    private IReadOnlyList<ReviewObservation> Observations() => _store.ReadObservations(sinceUtc: null);

    private IReadOnlyList<ReviewMiss> Misses()
        => _store.ReadMisses(Observations().Select(item => item.Id).ToArray());

    [Fact]
    public void Aspect_and_grade_steps_record_route_tokens_duration_cost_verdict_and_findings()
    {
        CodingRun();
        Aspect("code-quality", "concerns");
        Grade("B", "[[CODE_REVIEW_FINDING: text=[major] Store swallows write errors]]\n"
                   + "[[CODE_REVIEW_FINDING: text=[nit] Rename the helper]]\n"
                   + "[[CODE_REVIEW_FINDING: text=Consider a test]]");

        var recorded = _log.Read(_jobFolder)!.Steps;
        var observations = Observations();

        Assert.Equal(2, observations.Count);
        var aspect = observations.Single(item => item.Level == ReviewLevels.Aspect);
        var aspectStep = recorded.Single(step => step.StepId == "aspect-code-quality");
        Assert.Equal(new ReviewDelivery("demo", "AGT-1", 1, 1), aspect.Delivery);
        Assert.Equal(("code-quality", "gpt-6-sol", "high"), (aspect.Subject, aspect.Model, aspect.ReasoningLevel));
        Assert.Equal((12_000L, 800L, 4_000L, 100L, 60_000L),
            (aspect.InputTokens, aspect.OutputTokens, aspect.CacheReadTokens, aspect.CacheCreationTokens, aspect.DurationMs));
        Assert.Equal(aspectStep.EstimatedCostUsd, aspect.CostUsd);
        Assert.Equal(("concerns", ReviewOutcomes.Concerns), (aspect.Verdict, aspect.Outcome));
        Assert.Equal(new ReviewFindingCounts(Minor: 1), aspect.Findings);

        var grade = observations.Single(item => item.Level == ReviewLevels.Grade);
        Assert.Equal(("claude-opus-5-5", "xhigh", "B"), (grade.Model, grade.ReasoningLevel, grade.Verdict));
        Assert.Equal(new ReviewFindingCounts(Major: 1, Nit: 1, Unclassified: 1), grade.Findings);
        Assert.Empty(Misses());
    }

    [Fact]
    public void Carried_over_failed_and_environmental_aspects_and_running_rows_are_not_reviews()
    {
        CodingRun();
        Aspect("code-quality", "pass", carriedOverFrom: "attempt-0");
        Aspect("requirement-fit", "environmental", status: PipelineStepStatus.Failed);
        // The runner's fail-closed block after a failed command is not a review verdict.
        Aspect("code-quality", "block", status: PipelineStepStatus.Failed);
        _log.RecordStep(_jobFolder, new PipelineStepExecution
        {
            StepId = "aspect-tests-and-evidence",
            Kind = StepKind.Aspect,
            Status = PipelineStepStatus.Running,
            StartedAt = Tick(),
            Verdict = "pass",
        });

        Assert.Empty(Observations());
    }

    [Fact]
    public void A_failed_gate_records_a_miss_against_every_approval_of_the_delivery()
    {
        CodingRun();
        Aspect("code-quality", "pass");
        Aspect("requirement-fit", "block");
        Grade("A");
        Gate(PipelineStepStatus.Failed, "fail");

        var misses = Misses();
        var approvals = Observations().Where(item => item.Approved).Select(item => item.Id).ToHashSet();

        Assert.All(misses.Where(miss => miss.Source == ReviewDefectSources.Gate),
            miss => Assert.Contains(miss.ObservationId, approvals));
        Assert.Equal(2, misses.Count(miss => miss.Source == ReviewDefectSources.Gate));
        // The aspect block is also later-review evidence against the grade, never against the other aspect.
        var laterReview = Assert.Single(misses, miss => miss.Source == ReviewDefectSources.LaterReview);
        Assert.Equal(ReviewLevels.Grade, laterReview.Level);
    }

    [Theory]
    [InlineData("cancelled", null)]
    [InlineData("shutdown", null)]
    [InlineData(null, null)]
    public void Cancelled_or_local_failures_without_a_fail_verdict_are_not_gate_evidence(string? verdict, string? location)
    {
        CodingRun();
        Aspect("code-quality", "pass");
        Gate(PipelineStepStatus.Failed, verdict, location);
        Gate(PipelineStepStatus.Passed, "ok");

        Assert.Empty(Misses());
    }

    [Fact]
    public void A_remote_gate_failure_and_a_cached_failure_are_gate_evidence()
    {
        CodingRun();
        Aspect("code-quality", "pass");
        Gate(PipelineStepStatus.Failed, null, "remote");
        Gate(PipelineStepStatus.Failed, "cached-fail");

        Assert.Equal(2, Misses().Count(miss => miss.Source == ReviewDefectSources.Gate));
    }

    [Fact]
    public void A_fix_round_is_a_new_delivery_and_old_evidence_does_not_follow_it()
    {
        CodingRun();
        Aspect("code-quality", "pass");
        Gate(PipelineStepStatus.Failed, "fail");
        CodingRun();
        Aspect("code-quality", "pass");

        var observations = Observations();
        var misses = Misses();

        Assert.Equal([1, 2], observations.Select(item => item.Delivery.DeliveryRound));
        var miss = Assert.Single(misses);
        Assert.Equal(observations[0].Id, miss.ObservationId);
        Assert.Equal(new ReviewDelivery("demo", "AGT-1", 1, 2), _service.LatestDelivery("demo", "AGT-1"));
    }

    [Fact]
    public void A_replayed_step_or_evidence_never_counts_twice()
    {
        CodingRun();
        Aspect("code-quality", "pass");
        var step = _log.Read(_jobFolder)!.Steps.Single(item => item.StepId == "aspect-code-quality");
        var record = _log.Read(_jobFolder)!;
        Gate(PipelineStepStatus.Failed, "fail");

        _service.StepRecorded(_jobFolder, record, step);
        var gate = _log.Read(_jobFolder)!.Steps.Single(item => item.StepId == PipelineCatalogue.BuildTestGateStepId);
        _service.StepRecorded(_jobFolder, _log.Read(_jobFolder)!, gate);

        Assert.Single(Observations());
        Assert.Single(Misses());
    }

    [Fact]
    public void A_blocking_deep_review_through_the_api_contract_misses_the_standard_approvals()
    {
        CodingRun();
        Aspect("code-quality", "pass");
        Grade("A");
        var delivery = _service.LatestDelivery("demo", "AGT-1");
        var request = new RecordDeepReviewRequest("demo", "AGT-1", "gpt-6-sol", "xhigh", "block",
            InputTokens: 80_000, OutputTokens: 6_000, DurationMs: 600_000, CostUsd: 1.25m,
            Findings: new ReviewFindingCounts(Major: 1, Minor: 2));

        var validated = ReviewEffectivenessRequests.DeepReview(request, delivery, Tick());
        Assert.Null(validated.Error);
        Assert.True(_service.RecordReview(validated.Value!));
        Assert.False(_service.RecordReview(validated.Value!));

        var misses = Misses();
        Assert.Equal(2, misses.Count);
        Assert.All(misses, miss => Assert.Equal(ReviewDefectSources.LaterReview, miss.Source));
        Assert.Equal([ReviewLevels.Aspect, ReviewLevels.Grade], misses.Select(miss => miss.Level).Order());
        var deep = Observations().Single(item => item.Level == ReviewLevels.Deep);
        Assert.Equal((1.25m, 600_000L, 3), (deep.CostUsd!.Value, deep.DurationMs, deep.Findings.Real));
    }

    [Fact]
    public void Operator_and_production_evidence_attribute_misses_and_unknown_sources_are_rejected()
    {
        CodingRun();
        Aspect("code-quality", "pass");
        var delivery = _service.LatestDelivery("demo", "AGT-1");

        var rollback = ReviewEffectivenessRequests.Evidence(
            new RecordDefectEvidenceRequest("demo", "AGT-1", ReviewDefectSources.Rollback, "Rolled back after 500s",
                Severity: "critical", Reference: "deploy/42"), delivery, "operator", Tick());
        var gateFromApi = ReviewEffectivenessRequests.Evidence(
            new RecordDefectEvidenceRequest("demo", "AGT-1", ReviewDefectSources.Gate, "x"), delivery, "operator", Tick());
        var noDelivery = ReviewEffectivenessRequests.Evidence(
            new RecordDefectEvidenceRequest("demo", "AGT-2", ReviewDefectSources.Operator, "x"), null, "operator", Tick());

        Assert.NotNull(gateFromApi.Error);
        Assert.NotNull(noDelivery.Error);
        Assert.Equal(ReviewFindingSeverities.Blocker, rollback.Value!.Severity);
        Assert.True(_service.RecordEvidence(rollback.Value));
        var miss = Assert.Single(Misses());
        Assert.Equal(ReviewDefectSources.Rollback, miss.Source);
    }

    [Theory]
    [InlineData(TaskStates.HumanReview, 1)]
    [InlineData(TaskStates.Escalated, 0)]
    public void An_operator_send_back_from_human_review_is_operator_evidence(string fromState, int expectedMisses)
    {
        CodingRun();
        Aspect("code-quality", "pass");
        Grade("B");
        var requeue = new OperatorReviewRequeueService(_root, NullLogger<OperatorReviewRequeueService>.Instance,
            reviewEffectiveness: _service);

        requeue.Apply(_jobFolder, "AGT-1", "demo", fromState, TaskStates.Ready, "Wrong endpoint contract", "human:op");

        var misses = Misses();
        Assert.Equal(expectedMisses * 2, misses.Count);
        Assert.All(misses, miss => Assert.Equal(ReviewDefectSources.Operator, miss.Source));
    }

    [Fact]
    public void Summary_aggregates_recorded_reviews_and_misses_inside_the_window()
    {
        CodingRun();
        Aspect("code-quality", "pass");
        Aspect("requirement-fit", "pass", thinking: "medium");
        Gate(PipelineStepStatus.Failed, "fail");

        var summary = _service.Summary(Now.AddDays(-1));
        var later = _service.Summary(Now.AddMinutes(1));

        Assert.Equal(["high", "medium"], summary.Rows.Select(row => row.ReasoningLevel));
        Assert.All(summary.Rows, row => Assert.Equal(1.0, row.MissRate));
        Assert.Equal(new ReviewEffectivenessCoverage(2, summary.Rows.Sum(row => row.PricedReviews), 2, 2, 1, 2), summary.Coverage);
        Assert.Empty(later.Rows);
        Assert.Empty(_service.Summary(null, project => project != "demo").Rows);
        Assert.Equal(ReviewDepthAction.InsufficientEvidence,
            _service.AdviseDepth(ReviewLevels.Aspect, "gpt-6-sol", "high", null).Action);
        Assert.Equal(ReviewEffectivenessExport.Kind, _service.Export(null).Kind);
    }

    [Fact]
    public void A_store_without_a_root_records_nothing_and_reads_empty()
    {
        var disabled = new ReviewEffectivenessService(new ReviewEffectivenessStore((string?)null), _clock,
            NullLogger<ReviewEffectivenessService>.Instance);
        var log = new PipelineExecutionLog(TokenTestServices.Pricing(), NullLogger<PipelineExecutionLog>.Instance, disabled);
        var folder = Path.Combine(_root, "jobs", "AGT-9");
        Directory.CreateDirectory(folder);
        log.Begin(folder, PipelineCatalogue.Standard, project: "demo", jobId: "AGT-9", nowUtc: Now);

        log.RecordStep(folder, new PipelineStepExecution
        {
            StepId = "aspect-code-quality", Kind = StepKind.Aspect, Status = PipelineStepStatus.Passed,
            StartedAt = Tick(), CompletedAt = Tick(), Verdict = "pass",
        });

        Assert.Empty(disabled.Summary(null).Rows);
        Assert.Null(disabled.LatestDelivery("demo", "AGT-9"));
    }

    [Fact]
    public void The_registered_pipeline_log_receives_the_review_observer()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ReviewEffectiveness:StoreRoot"] = Path.Combine(_root, "di-ledger"),
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<TimeProvider>(_clock);
        services.AddSingleton(TokenTestServices.Pricing());
        services.AddLogging();
        services.AddReviewEffectiveness();
        services.AddSingleton<PipelineExecutionLog>();
        using var provider = services.BuildServiceProvider();
        var log = provider.GetRequiredService<PipelineExecutionLog>();
        var folder = Path.Combine(_root, "jobs", "AGT-7");
        Directory.CreateDirectory(folder);
        log.Begin(folder, PipelineCatalogue.Standard, project: "demo", jobId: "AGT-7", nowUtc: Now);

        log.RecordStep(folder, new PipelineStepExecution
        {
            StepId = "aspect-code-quality", Kind = StepKind.Aspect, Status = PipelineStepStatus.Passed,
            Model = "gpt-6-sol", StartedAt = Tick(), CompletedAt = Tick(), Verdict = "pass",
        });

        var row = Assert.Single(provider.GetRequiredService<ReviewEffectivenessService>().Summary(null).Rows);
        Assert.Equal((ReviewLevels.Aspect, "gpt-6-sol", 1), (row.Level, row.Model, row.Reviews));
    }
}
