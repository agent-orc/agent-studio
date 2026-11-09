using AgentStudio.Pipeline;
using AgentStudio.Prompts;
using AgentStudio.Runner;
using AgentStudio.Shared;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class VisualReviewPlanTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"visual-plan-{Guid.NewGuid():N}");

    [Theory]
    [InlineData(TaskTypes.Feature)]
    [InlineData(TaskTypes.Bug)]
    [InlineData(TaskTypes.Chore)]
    public void Frontend_template_change_seals_image_capable_visual_aspect_for_each_coding_pipeline(string taskType)
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "prompt.md"), "Improve footer layout.");
        var plan = Build(taskType, ["frontend/src/app/layout/footer.component.html"]);

        var aspect = Assert.Single(plan.Commands, command => command.Aspect == "visual-review");
        Assert.Equal(ReviewCommandKinds.AgentAspect, aspect.ExecutionKind);
        Assert.Equal(CliTypes.Codex, aspect.CliType);
        Assert.Equal(PipelineStepModelDefaults.SupportModel, aspect.Model);
        Assert.Contains("Open every relevant image", aspect.Prompt);
        Assert.Contains("visual-review", plan.RequiredAspects);
    }

    [Fact]
    public void Backend_only_change_skips_visual_aspect()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "prompt.md"), "Adjust backend.");
        var plan = Build(TaskTypes.Chore, ["backend/Program.cs"]);
        Assert.DoesNotContain(plan.Commands, command => command.Aspect == "visual-review");
        Assert.Contains(plan.SkippedAspects, item => item.Aspect == "visual-review");
    }

    private ReviewPlanDto Build(string taskType, IReadOnlyList<string> changed)
    {
        var config = new ConfigurationBuilder().Build();
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var aspects = new AspectRunnerService(prompts, NullLogger<AspectRunnerService>.Instance);
        var task = new TaskInfo
        {
            Id = "VIS-1", TaskKey = "VIS-1", Title = "Footer", Mode = TaskModes.Coding,
            TaskType = taskType, FolderPath = _folder, ProjectName = "Agent Studio",
        };
        return new RemoteReviewPlanBuilder(aspects, config)
            .Build(task, null, null, "refs/heads/develop", changedFiles: changed);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
    }
}
