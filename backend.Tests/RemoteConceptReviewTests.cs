using AgentStudio.Prompts;
using AgentStudio.Runner;
using AgentStudio.Pipeline;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class RemoteConceptReviewTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "concept-review-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Contradictory_dossier_cannot_pass_without_concept_fit_review()
    {
        Directory.CreateDirectory(_folder);
        const string brief = "Use one implementation card per independently reviewable slice. Include a recommendation for every open decision.";
        File.WriteAllText(Path.Combine(_folder, "prompt.md"), brief);
        var plan = BuildPlan();

        var aspect = Assert.Single(plan.Commands, command => command.Aspect == "concept-fit");
        Assert.Equal(ReviewCommandKinds.AgentAspect, aspect.ExecutionKind);
        Assert.Equal(PipelineStepModelDefaults.SupportModel, aspect.Model);
        Assert.Equal(PipelineStepModelDefaults.SupportThinkingLevel, aspect.ThinkingLevel);
        Assert.Contains(brief, aspect.Prompt);
        Assert.Contains("contradict", aspect.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("concept-fit", plan.RequiredAspects);

        // The delivered Dossier says to combine every slice into one card, in
        // direct conflict with the current brief. Build and lint can pass, but
        // a report with no content verdict must not be accepted as a pass.
        var report = EmptyPassingReport();
        var checkedReport = ConceptRemoteReviewPolicy.Enforce(
            plan, plan.BriefSha256, plan.BriefSha256, report);
        Assert.NotEqual("Pass", checkedReport.Outcome);
        Assert.Contains("concept-fit", checkedReport.Summary);
    }

    [Fact]
    public void Review_against_superseded_brief_fails_with_both_versions()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "prompt.md"), "Old operator direction.");
        var plan = BuildPlan();
        var current = AttemptAuthorityService.Hash("New operator direction.");

        var report = ConceptRemoteReviewPolicy.Enforce(plan, plan.BriefSha256, current, EmptyPassingReport());

        Assert.NotEqual("Pass", report.Outcome);
        Assert.Contains(plan.BriefSha256!, report.Summary);
        Assert.Contains(current, report.Summary);
    }

    [Fact]
    public void Project_step_setting_controls_concept_model_thinking_and_skip_reason()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "prompt.md"), "Write a Concept Dossier.");
        var configured = BuildPlan(new ProjectSettings
        {
            PipelineSteps = new()
            {
                [PipelineCatalogue.RemoteConceptFitStepId] = new PipelineStepSetting
                {
                    Model = "gpt-5.6-sol", ThinkingLevel = "medium",
                },
            },
        });
        var command = Assert.Single(configured.Commands, item => item.Aspect == "concept-fit");
        Assert.Equal("gpt-5.6-sol", command.Model);
        Assert.Equal("medium", command.ThinkingLevel);

        var disabled = BuildPlan(new ProjectSettings
        {
            PipelineSteps = new()
            {
                [PipelineCatalogue.RemoteConceptFitStepId] = new PipelineStepSetting { Enabled = false },
            },
        });
        Assert.DoesNotContain(disabled.Commands, item => item.Aspect == "concept-fit");
        Assert.Contains("Disabled", Assert.Single(disabled.SkippedAspects!).Reason);
        Assert.NotEqual("Pass", ConceptRemoteReviewPolicy.Enforce(
            disabled, disabled.BriefSha256, disabled.BriefSha256, EmptyPassingReport()).Outcome);
    }

    private ReviewPlanDto BuildPlan(ProjectSettings? settings = null)
    {
        var config = new ConfigurationBuilder().Build();
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var aspects = new AspectRunnerService(prompts, NullLogger<AspectRunnerService>.Instance);
        var task = new TaskInfo
        {
            Id = "CON-1", Title = "Concept", Mode = TaskModes.Concept,
            FolderPath = _folder, ProjectName = "Test",
        };
        return new RemoteReviewPlanBuilder(aspects, config).Build(task, null, settings, "refs/heads/main");
    }

    private static ReviewReportRequest EmptyPassingReport() => new(
        "executor", "instance", "lease", 1, "key", "Pass", null,
        "All applicable remote review aspects passed.", null!, null!, [], [], []);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { }
    }
}
