using AgentStudio.OrchestrationDecisions;
using AgentStudio.Pipeline;
using AgentStudio.Prompts;
using AgentStudio.Runner;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3268: the deep-review decision is a frozen receipt per delivery, a
/// decision row between the pipeline steps, a read-only review-plane command
/// only in active mode, and a separately attributed ledger participant.
/// </summary>
public sealed class DeepReviewPlanTests : IDisposable
{
    private static readonly string Sha = new('a', 40);
    private const string ResultRef = "refs/agent-studio/results/AGT-1/run-1";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "deep-review-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 21, 8, 0, 0, TimeSpan.Zero));

    public DeepReviewPlanTests()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "prompt.md"), "Tighten the lease ownership check on run claims.");
    }

    [Fact]
    public void Active_mode_freezes_the_deep_review_at_the_decided_route_into_the_plan()
    {
        var plan = BuildPlan(DeepReviewModes.Active, ["backend/Features/Runner/RunLeaseService.cs"]);

        var command = Assert.Single(plan.Commands, item => item.Aspect == DeepReviewAspect.Id);
        Assert.Equal(DeepReviewAspect.StepId, command.StepId);
        Assert.Equal(Contract.ReviewCommandKinds.AgentAspect, command.ExecutionKind);
        Assert.Equal("codex", command.CliType);
        Assert.Equal("gpt-6-sol", command.Model);
        Assert.Equal("xhigh", command.ThinkingLevel);
        Assert.Contains("deep reviewer", command.Prompt);
        Assert.Contains(DeepReviewAspect.Id, plan.RequiredAspects);

        var entry = Assert.Single(DeepReviewLedgerStore.Read(_folder).Decisions);
        Assert.Equal(Sha, entry.ResultSha);
        Assert.True(entry.Decision.Dispatch);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, entry.DecidedAt);
    }

    [Fact]
    public void Shadow_mode_is_the_default_and_names_the_decision_without_dispatching()
    {
        var plan = BuildPlan(mode: null, ["backend/Features/Runner/RunLeaseService.cs"]);

        Assert.DoesNotContain(plan.Commands, item => item.Aspect == DeepReviewAspect.Id);
        var skipped = Assert.Single(plan.SkippedAspects!, item => item.Aspect == DeepReviewAspect.Id);
        Assert.StartsWith("Shadow mode: the engine would run a deep review at gpt-6-sol/xhigh", skipped.Reason);
        var entry = Assert.Single(DeepReviewLedgerStore.Read(_folder).Decisions);
        Assert.Equal(DeepReviewModes.Shadow, entry.Decision.Mode);
        Assert.Equal(DeepReviewActions.Run, entry.Decision.Action);
        Assert.False(entry.Decision.Dispatch);
    }

    [Fact]
    public void A_rebuilt_plan_reuses_the_frozen_receipt_of_its_delivery()
    {
        BuildPlan(DeepReviewModes.Shadow, ["backend/Features/Runner/RunLeaseService.cs"]);
        var first = Assert.Single(DeepReviewLedgerStore.Read(_folder).Decisions);

        _clock.Advance(TimeSpan.FromMinutes(5));
        var rebuilt = BuildPlan(DeepReviewModes.Active, ["backend/Features/Runner/RunLeaseService.cs"]);

        var frozen = Assert.Single(DeepReviewLedgerStore.Read(_folder).Decisions);
        Assert.Equal(first.DecidedAt, frozen.DecidedAt);
        Assert.Equal(DeepReviewModes.Shadow, frozen.Decision.Mode);
        Assert.DoesNotContain(rebuilt.Commands, item => item.Aspect == DeepReviewAspect.Id);
    }

    [Fact]
    public void Decision_is_a_rule_row_between_the_pipeline_steps()
    {
        var pipeline = new PipelineExecutionLog(TokenTestServices.Pricing(), NullLogger<PipelineExecutionLog>.Instance);
        pipeline.EnsureRun(_folder, PipelineCatalogue.Standard, "Test", "AGT-1", _clock.GetUtcNow().UtcDateTime);

        BuildPlan(DeepReviewModes.Shadow, ["backend/Features/Runner/RunLeaseService.cs"], pipeline);

        var row = Assert.Single(pipeline.Read(_folder)!.Steps, step => step.StepId == DeepReviewAspect.DecisionStepId);
        Assert.Equal(StepKind.Orchestrator, row.Kind);
        Assert.Equal(PipelineStepStatus.Passed, row.Status);
        Assert.Equal("shadow-run", row.Verdict);
        Assert.Equal(StepCostBasis.Deterministic, row.CostBasis);
        Assert.Null(row.Model);
        Assert.StartsWith("gpt-6-sol/xhigh: hard-floor:", row.VerdictSummary);
        Assert.Equal(DeepReviewLedgerStore.FileName, row.EvidenceRef);
    }

    [Fact]
    public void Small_standard_delivery_records_a_skip_with_its_reason()
    {
        File.WriteAllText(Path.Combine(_folder, "prompt.md"), "Rename a label on the settings page.");

        var plan = BuildPlan(DeepReviewModes.Active, ["frontend/src/app/settings.component.html"]);

        Assert.DoesNotContain(plan.Commands, item => item.Aspect == DeepReviewAspect.Id);
        Assert.StartsWith("no-deep-review-ground",
            Assert.Single(plan.SkippedAspects!, item => item.Aspect == DeepReviewAspect.Id).Reason);
    }

    [Fact]
    public void Deep_review_tokens_land_on_their_own_ledger_participant()
    {
        var aspect = Command("aspect-code-quality", "code-quality", 1_000);
        var deep = Command(DeepReviewAspect.StepId, DeepReviewAspect.Id, 9_000) with { ThinkingLevel = "xhigh" };
        var report = Report([aspect, deep], []);

        var aspectCalls = RemotePipelineReviewEvidenceProjector.BuildReviewUsageCalls(
            TokenTestServices.Pricing(), Attempt("rev-1"), report);
        var deepCalls = RemotePipelineReviewEvidenceProjector.BuildReviewUsageCalls(
            TokenTestServices.Pricing(), Attempt("rev-1"), report, deepReview: true);

        Assert.Equal(1_000, Assert.Single(aspectCalls).InputTokens);
        var call = Assert.Single(deepCalls);
        Assert.Equal(9_000, call.InputTokens);
        Assert.Equal("xhigh", call.ThinkingLevel);
        Assert.Equal("support:deep-review:", RemotePipelineReviewEvidenceProjector.DeepReviewParticipantPrefix);
    }

    [Fact]
    public void Executed_deep_review_round_feeds_the_repeat_check_of_the_next_report()
    {
        var verdict = new Contract.ReviewVerdictDto(DeepReviewAspect.Id, "block", "product",
            "Viewer scope can write.", "backend/Features/Auth/Scope.cs", "backend/Features/Auth/Scope.cs:42 writes");
        var report = Report([Command(DeepReviewAspect.StepId, DeepReviewAspect.Id, 500)], [verdict]);

        var task = Task();
        RemotePipelineReviewEvidenceProjector.RecordDeepReviewRound(task, Attempt("rev-1"), report,
            _clock.GetUtcNow().UtcDateTime);
        RemotePipelineReviewEvidenceProjector.RecordDeepReviewRound(task, Attempt("rev-1"), report,
            _clock.GetUtcNow().UtcDateTime);

        var round = Assert.Single(DeepReviewLedgerStore.Read(_folder).Rounds);
        Assert.Equal("block", round.Status);
        Assert.Single(round.BlockFingerprints);
        Assert.Equal(500, round.InputTokens);

        var reworded = verdict with { Summary = "Scope still lets a viewer write.", Missing = "backend/Features/Auth/Scope.cs:44" };
        var repeated = DeepReviewDecisionService.RepeatedBlock(_folder, "rev-2", [reworded]);
        Assert.NotNull(repeated);
        Assert.Equal(2, repeated!.ConsecutiveRounds);
        Assert.Null(DeepReviewDecisionService.RepeatedBlock(_folder, "rev-2",
            [verdict with { Missing = "backend/Features/Runner/Other.cs:1" }]));
    }

    private Contract.ReviewPlanDto BuildPlan(string? mode, IReadOnlyList<string> changedFiles,
        PipelineExecutionLog? pipeline = null)
    {
        var values = new Dictionary<string, string?>();
        if (mode is not null) values[$"{DeepReviewDecisionService.Section}:Mode"] = mode;
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var aspects = new AspectRunnerService(prompts, NullLogger<AspectRunnerService>.Instance);
        var deepReview = new DeepReviewDecisionService(config, _clock, new ModelRoutingPolicyRegistry(),
            pipeline: pipeline);
        return new RemoteReviewPlanBuilder(aspects, config, time: _clock, deepReview: deepReview)
            .Build(Task(), null, null, "refs/heads/main", Sha, changedFiles, ResultRef);
    }

    private TaskInfo Task() => new()
    {
        Id = "AGT-1", Key = "AGT-1", Title = "Lease check", FolderPath = _folder, ProjectName = "Test",
        Model = "gpt-6-sol", ThinkingLevel = "medium",
    };

    private Contract.ReviewCommandEvidenceDto Command(string stepId, string aspect, long input)
    {
        var at = _clock.GetUtcNow().UtcDateTime;
        return new Contract.ReviewCommandEvidenceDto(
            stepId, aspect, "/usr/local/bin/codex", [], Sha, Sha, new string('b', 40),
            at, at.AddMinutes(2), 0, null, "", "",
            ExecutionKind: Contract.ReviewCommandKinds.AgentAspect,
            Model: "gpt-6-sol", ThinkingLevel: "medium", InputTokens: input, OutputTokens: 100);
    }

    private static Contract.ReviewReportRequest Report(IReadOnlyList<Contract.ReviewCommandEvidenceDto> commands,
        IReadOnlyList<Contract.ReviewVerdictDto> verdicts)
        => new("executor", "instance-1", "lease-1", 3, "key", "ProductFailure", null, null,
            new Contract.ReviewWorkspaceProofDto("example/repository", Sha, Sha, new string('b', 40), false, false,
                "workspace", "rev-1"),
            new Contract.ReviewEnvironmentDto("agent-runner-01", "agent-runner-01-review", "instance-1", "linux", "x64",
                "10.0", new Dictionary<string, string>(), new Dictionary<string, string>(), null),
            commands, [], verdicts);

    private ReviewAttemptDto Attempt(string attemptId)
    {
        var created = _clock.GetUtcNow().UtcDateTime;
        return new ReviewAttemptDto(attemptId, "PROJ-002::AGT-1", "example/repository", "att-1", null,
            new ReviewSubjectDto("subject-1", "example/repository", Sha, "att-1", new string('d', 64),
                "policy-v1", [], created),
            AttemptLifecycleState.Leased, null, 3, 0, created, null, null, null, null, null, []);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }
}
