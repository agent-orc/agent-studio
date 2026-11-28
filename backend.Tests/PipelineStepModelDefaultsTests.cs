using Xunit;

namespace AgentStudio.Tests;

public class PipelineStepModelDefaultsTests
{
    private static PipelineStep Step(string id) =>
        PipelineCatalogue.Standard.AllSteps.First(s =>
            string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void DeterministicAndCoreSteps_ResolveNoModel()
    {
        Assert.Null(PipelineStepModelDefaults.Resolve(null, Step(PipelineCatalogue.DriftCodePatternStepId)));
        Assert.Null(PipelineStepModelDefaults.Resolve(null, Step(PipelineCatalogue.CoreAgentRunStepId)));
        Assert.Null(PipelineStepModelDefaults.Resolve(null, Step(PipelineCatalogue.LoopGuardStepId)));
        Assert.Null(PipelineStepModelDefaults.Resolve(null, Step(PipelineCatalogue.LintScssStepId)));
        Assert.Null(PipelineStepModelDefaults.Resolve(null, Step(PipelineCatalogue.GitCommitAttributionStepId)));
    }

    [Fact]
    public void AspectStep_FallsBackToSupportModel_WhenNoOverride()
    {
        var r = PipelineStepModelDefaults.Resolve(null, Step("aspect-code-quality"));
        Assert.NotNull(r);
        Assert.Equal(PipelineStepModelDefaults.SupportModel, r!.Model);
        Assert.Equal(PipelineStepConfigResolver.ModelSourceRuntime, r.Source);
    }

    [Fact]
    public void DriftStep_FallsBackToDriftDefault_WhenNoOverride()
    {
        var r = PipelineStepModelDefaults.Resolve(null, Step(PipelineCatalogue.DriftAdrCodeStepId));
        Assert.NotNull(r);
        Assert.Equal(DriftPostStepRunner.DefaultModel, r!.Model);
    }

    [Fact]
    public void PrepStep_FallsBackToPrepFallback_WhenNoOverride()
    {
        var r = PipelineStepModelDefaults.Resolve(null, Step(PipelineCatalogue.PreOrchestratorPrepStepId));
        Assert.NotNull(r);
        Assert.Equal(PipelineStepModelDefaults.SupportModel, r!.Model);
        Assert.Equal(PipelineStepModelDefaults.SupportModel, OrchestratorPrepHostedService.PrepFallbackModel);
    }

    [Fact]
    public void CodeReviewGradeStep_FallsBackToGradeDefault_WhenNoOverride()
    {
        var r = PipelineStepModelDefaults.Resolve(null, Step(PipelineCatalogue.CodeReviewGradeStepId));
        Assert.NotNull(r);
        Assert.Equal(CodeReviewGradeModelSelector.DefaultModel, r!.Model);
    }

    [Fact]
    public void SupportingOrchestratorSteps_FallBackToSol_WhenNoOverride()
    {
        Assert.Equal(PipelineStepModelDefaults.SupportModel,
            PipelineStepModelDefaults.Resolve(null, Step(PipelineCatalogue.OrchestratorDecisionStepId))!.Model);
        Assert.Equal(PipelineStepModelDefaults.SupportModel,
            PipelineStepModelDefaults.Resolve(null, Step(PipelineCatalogue.ConflictResolutionStepId))!.Model);
        Assert.Equal(PipelineStepModelDefaults.SupportModel,
            PipelineStepModelDefaults.Resolve(null, PipelineCatalogue.AbortReviewStep)!.Model);
    }

    [Fact]
    public void QualitySteps_UseSolAtMedium()
    {
        foreach (var step in new[]
                 {
                     Step(PipelineCatalogue.CodeReviewGradeStepId),
                     Step(PipelineCatalogue.TaskSpawnerStepId),
                 })
        {
            Assert.Equal(PipelineStepModelDefaults.QualityModel,
                PipelineStepModelDefaults.Resolve(null, step)!.Model);
            Assert.Equal(CliTypes.Codex, PipelineStepModelDefaults.RuntimeDefaultCliFor(step));
            Assert.Equal(PipelineStepModelDefaults.QualityThinkingLevel,
                PipelineStepModelDefaults.RuntimeDefaultThinkingLevelFor(step));
        }
    }

    [Fact]
    public void AspectUsesSolAtMedium()
    {
        var step = Step("aspect-code-quality");
        Assert.Equal(CliTypes.Codex, PipelineStepModelDefaults.RuntimeDefaultCliFor(step));
        Assert.Equal(ModelIds.Gpt62Sol, PipelineStepModelDefaults.RuntimeDefaultFor(step));
        Assert.Equal("medium", PipelineStepModelDefaults.RuntimeDefaultThinkingLevelFor(step));
    }

    [Fact]
    public void ProjectModel_OverridesRuntimeDefault_ForAspect()
    {
        var settings = new ProjectSettings { OrchestratorModel = "claude-sonnet-4-6" };
        var r = PipelineStepModelDefaults.Resolve(settings, Step("aspect-code-quality"));
        Assert.NotNull(r);
        Assert.Equal("claude-sonnet-4-6", r!.Model);
        Assert.Equal(PipelineStepConfigResolver.ModelSourceProject, r.Source);
    }

    [Fact]
    public void StepOverride_WinsOverProjectModel()
    {
        var settings = new ProjectSettings
        {
            OrchestratorModel = "claude-sonnet-4-6",
            PipelineSteps = new Dictionary<string, PipelineStepSetting>
            {
                ["aspect-code-quality"] = new() { Model = "claude-opus-4-7" },
            },
        };
        var r = PipelineStepModelDefaults.Resolve(settings, Step("aspect-code-quality"));
        Assert.NotNull(r);
        Assert.Equal("claude-opus-4-7", r!.Model);
        Assert.Equal(PipelineStepConfigResolver.ModelSourceStep, r.Source);
    }
}
