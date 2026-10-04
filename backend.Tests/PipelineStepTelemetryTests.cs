using AgentStudio.Pipeline;
using AgentStudio.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class PipelineStepTelemetryTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pipeline-telemetry-" + Guid.NewGuid().ToString("N"));
    private readonly PipelineExecutionLog _log = new(NullLogger<PipelineExecutionLog>.Instance);
    private static readonly DateTime Now = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void LlmReceiptRequiresExecutedModel()
    {
        _log.Begin(_folder, PipelineCatalogue.Standard, "P", "T", Now);
        Assert.Throws<ArgumentException>(() => _log.RecordUsage(_folder,
            PipelineCatalogue.OrchestratorDecisionStepId,
            new AdHocUsageRecord { Model = "", InputTokens = 20, Ts = Now }));
        _log.RecordStep(_folder, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.OrchestratorDecisionStepId,
            Kind = StepKind.Orchestrator,
            Status = PipelineStepStatus.Passed,
            StartedAt = Now,
            CompletedAt = Now,
        });
        var step = _log.Read(_folder)!.Steps.Single(item =>
            item.StepId == PipelineCatalogue.OrchestratorDecisionStepId);
        Assert.Equal("missing-model", step.CostStatus);
        Assert.Null(step.EstimatedCostUsd);
    }

    [Fact]
    public void OrchestratorReceiptRetainsKindAndCostInProjectLedger()
    {
        var entry = new OrchestratorLogEntry
        {
            JobId = "T",
            Ts = Now,
            ParticipantId = "support:adhoc",
            PipelineStepId = PipelineCatalogue.CodeReviewGradeStepId,
            TokenUsage = new OrchestratorTokenUsage
            {
                Model = "claude-haiku-4-5",
                InputTokens = 1000,
                OutputTokens = 100,
            },
        };
        var record = Assert.Single(ProjectPipelineCostService.BuildReceiptRecords("P", [entry]));
        Assert.Equal(StepKind.Orchestrator, Assert.Single(record.Steps).Kind);
        var project = ProjectPipelineCostService.BuildFromRecords("P", [record], 1, Now);
        Assert.Equal("orchestrator", Assert.Single(project.Kinds).Kind);
        Assert.Equal(1, Assert.Single(project.Steps).Occurrences);
        Assert.True(project.DecidingCostUsd > 0);
    }

    [Fact]
    public void UnknownModelIsUnpricedRatherThanZero()
    {
        _log.Begin(_folder, PipelineCatalogue.Standard, "P", "T", Now);
        var id = PipelineCatalogue.OrchestratorDecisionStepId;
        _log.RecordUsage(_folder, id, new AdHocUsageRecord
        {
            Model = "gpt-unknown-test-model",
            InputTokens = 100,
            OutputTokens = 20,
            Ts = Now,
        });
        _log.RecordStep(_folder, new PipelineStepExecution
        {
            StepId = id,
            Kind = StepKind.Orchestrator,
            Status = PipelineStepStatus.Passed,
            StartedAt = Now,
            CompletedAt = Now,
            Verdict = "accept",
        });
        var step = Assert.Single(_log.Read(_folder)!.Steps.Where(step => step.StepId == id));
        Assert.Equal("unpriced", step.CostStatus);
        Assert.False(step.ModelPriced);
        Assert.Null(step.EstimatedCostUsd);
        var cost = PipelineCostCalculator.Summarize(_log.Read(_folder));
        Assert.Equal(1, cost.DecidingUnpricedSteps);
    }

    [Fact]
    public void OccurrencesSurviveRoundsAndEpochs()
    {
        var id = PipelineCatalogue.OrchestratorDecisionStepId;
        _log.Begin(_folder, PipelineCatalogue.Standard, "P", "T", Now);
        for (var round = 0; round < 2; round++)
        {
            _log.RecordStep(_folder, new PipelineStepExecution
            {
                StepId = id, Kind = StepKind.Orchestrator, Model = "claude-haiku-4-5",
                Status = PipelineStepStatus.Passed, StartedAt = Now.AddMinutes(round),
                CompletedAt = Now.AddMinutes(round), Verdict = "accept",
            });
        }
        _log.Begin(_folder, PipelineCatalogue.Standard, "P", "T", Now.AddMinutes(2));
        _log.RecordStep(_folder, new PipelineStepExecution
        {
            StepId = id, Kind = StepKind.Orchestrator, Model = "claude-haiku-4-5",
            Status = PipelineStepStatus.Passed, StartedAt = Now.AddMinutes(2),
            CompletedAt = Now.AddMinutes(2), Verdict = "accept",
        });
        var next = _log.Read(_folder)!;
        var project = ProjectPipelineCostService.BuildFromRecords(
            "P", [next, .. next.PreviousAttempts], 1, Now.AddMinutes(3));
        Assert.Equal(3, Assert.Single(project.Steps.Where(step => step.StepId == id)).Occurrences);
    }

    [Fact]
    public void DeterministicStepHasExplicitZero()
    {
        _log.Begin(_folder, PipelineCatalogue.Standard, "P", "T", Now);
        var id = PipelineCatalogue.BuildTestGateStepId;
        _log.RecordStep(_folder, new PipelineStepExecution
        {
            StepId = id, Kind = StepKind.Tool, Status = PipelineStepStatus.Passed,
            StartedAt = Now, CompletedAt = Now,
        });
        var step = Assert.Single(_log.Read(_folder)!.Steps.Where(step => step.StepId == id));
        Assert.Equal("deterministic-zero", step.CostStatus);
        Assert.True(step.ModelPriced);
        Assert.Equal(0m, step.EstimatedCostUsd);
    }

    [Fact]
    public void LaterExecutionDoesNotInheritEarlierReceipt()
    {
        _log.Begin(_folder, PipelineCatalogue.Standard, "P", "T", Now);
        var id = PipelineCatalogue.OrchestratorDecisionStepId;
        _log.RecordUsage(_folder, id, new AdHocUsageRecord
        {
            Model = "claude-haiku-4-5", InputTokens = 100, OutputTokens = 20, Ts = Now,
        });
        _log.RecordStep(_folder, new PipelineStepExecution
        {
            StepId = id, Kind = StepKind.Orchestrator, Status = PipelineStepStatus.Passed,
            StartedAt = Now, CompletedAt = Now, Verdict = "accept",
        });
        _log.RecordStep(_folder, new PipelineStepExecution
        {
            StepId = id, Kind = StepKind.Orchestrator, Status = PipelineStepStatus.Passed,
            StartedAt = Now.AddMinutes(1), CompletedAt = Now.AddMinutes(1), Verdict = "accept",
        });
        var record = _log.Read(_folder)!;
        Assert.Equal(2, record.Occurrences.Count(step => step.StepId == id));
        Assert.Equal(0, record.Steps.Single(step => step.StepId == id).InputTokens);
        Assert.Equal("missing-model", record.Steps.Single(step => step.StepId == id).CostStatus);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
    }
}
