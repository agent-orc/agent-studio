using AgentStudio.Prompts;
using AgentStudio.Runner;
using AgentStudio.Tasks;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3880: a remote review plan for a coding card carries the generated
/// summary and the agent's results/status.md, so requirement-fit can check
/// what a follow-up round asked the agent to report (case AGT-3403).
/// </summary>
public sealed class RemoteReviewStatusInputTests : IDisposable
{
    private const string Summary =
        "# Status\nMerged the status indicator rollout. Documented merge decisions in results/status.md.";

    // Past the head excerpt of the results inventory, so only the status
    // section can carry it.
    private static readonly string AgentReport = new string('r', 1_000)
        + "\n## Per conflicted file\n| File | Kept from ours | Kept from theirs |\n| a.cs | guard | indicator |";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "status-input-" + Guid.NewGuid().ToString("N"));

    public RemoteReviewStatusInputTests()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "results"));
        File.WriteAllText(Path.Combine(_folder, "prompt.md"),
            "Merge round: record per conflicted file what you kept from each side in results/status.md.");
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Coding_plan_carries_the_summary_and_the_agent_report_under_their_headings()
    {
        File.WriteAllText(Path.Combine(_folder, "status.md"), Summary);
        File.WriteAllText(Path.Combine(_folder, "results", "status.md"), AgentReport);

        var plan = BuildPlan(TaskModes.Coding);

        // The visual-review template judges screenshots and has no status slot.
        string[] statusAspects = ["requirement-fit", "code-quality", "documentation-impact", "tests-and-evidence"];
        var aspects = plan.Commands.Where(command => statusAspects.Contains(command.Aspect)).ToList();
        Assert.Equal(statusAspects.Order(), aspects.Select(command => command.Aspect).Order());
        foreach (var aspect in aspects)
        {
            Assert.Contains(RemoteReviewPlanBuilder.ReviewStatus.AgentReportHeading, aspect.Prompt);
            Assert.Contains(RemoteReviewPlanBuilder.ReviewStatus.SummaryHeading, aspect.Prompt);
            Assert.Contains("| a.cs | guard | indicator |", aspect.Prompt);
            Assert.Contains("Documented merge decisions in results/status.md.", aspect.Prompt);
        }
        var fit = Assert.Single(aspects, command => command.Aspect == "requirement-fit");
        Assert.Contains(RemoteReviewPlanBuilder.ReviewStatus.AgentReportNote, fit.Prompt);
        Assert.DoesNotContain("{{status_report_note}}", fit.Prompt);
    }

    [Fact]
    public void Requirement_fit_note_needs_a_summary_that_points_to_the_agent_report()
    {
        File.WriteAllText(Path.Combine(_folder, "status.md"), "# Status\nMerged the rollout.");
        File.WriteAllText(Path.Combine(_folder, "results", "status.md"), AgentReport);

        var fit = Assert.Single(BuildPlan(TaskModes.Coding).Commands, command => command.Aspect == "requirement-fit");

        Assert.Contains("| a.cs | guard | indicator |", fit.Prompt);
        Assert.DoesNotContain(RemoteReviewPlanBuilder.ReviewStatus.AgentReportNote, fit.Prompt);
        Assert.DoesNotContain("{{status_report_note}}", fit.Prompt);
    }

    [Fact]
    public void Coding_plan_without_an_agent_report_reads_the_summary_alone()
    {
        File.WriteAllText(Path.Combine(_folder, "status.md"), Summary);

        var fit = Assert.Single(BuildPlan(TaskModes.Coding).Commands, command => command.Aspect == "requirement-fit");

        Assert.Contains("Documented merge decisions", fit.Prompt);
        Assert.DoesNotContain(RemoteReviewPlanBuilder.ReviewStatus.AgentReportHeading, fit.Prompt);
        Assert.DoesNotContain(RemoteReviewPlanBuilder.ReviewStatus.AgentReportNote, fit.Prompt);
    }

    [Fact]
    public void Scaffold_summary_still_falls_back_to_the_agent_report()
    {
        File.WriteAllText(Path.Combine(_folder, "status.md"),
            TaskTransitionService.ResultScaffoldMarker + "\n# Status\nScaffold text.");
        File.WriteAllText(Path.Combine(_folder, "results", "status.md"), AgentReport);

        var fit = Assert.Single(BuildPlan(TaskModes.Coding).Commands, command => command.Aspect == "requirement-fit");

        Assert.Contains("| a.cs | guard | indicator |", fit.Prompt);
        Assert.DoesNotContain("Scaffold text.", fit.Prompt);
        Assert.DoesNotContain(RemoteReviewPlanBuilder.ReviewStatus.SummaryHeading, fit.Prompt);
    }

    [Fact]
    public void Concept_plan_keeps_the_summary_as_its_only_status()
    {
        File.WriteAllText(Path.Combine(_folder, "status.md"), Summary);
        File.WriteAllText(Path.Combine(_folder, "results", "status.md"), AgentReport);

        var aspect = Assert.Single(BuildPlan(TaskModes.Concept).Commands, command => command.Aspect == "concept-fit");

        Assert.Contains("Documented merge decisions", aspect.Prompt);
        Assert.DoesNotContain("| a.cs | guard | indicator |", aspect.Prompt);
        Assert.DoesNotContain(RemoteReviewPlanBuilder.ReviewStatus.AgentReportHeading, aspect.Prompt);
    }

    [Fact]
    public void Combined_status_holds_the_bound_and_trims_the_summary_first()
    {
        const int bound = RemoteReviewPlanBuilder.ReviewStatus.Characters;
        var report = "report-head " + new string('r', 12_000) + " report-tail";
        var summary = "summary-head " + new string('s', 12_000) + " summary-tail results/status.md";

        var status = RemoteReviewPlanBuilder.ReviewStatus.Compose(summary, report);

        Assert.True(status.Text.Length <= bound, $"{status.Text.Length} > {bound}");
        Assert.Contains(report, status.Text);
        Assert.Contains(RemoteReviewPlanBuilder.ReviewStatus.SummaryTrimmed, status.Text);
        Assert.Contains("summary-tail", status.Text);
        Assert.DoesNotContain("summary-head", status.Text);
        Assert.Equal(RemoteReviewPlanBuilder.ReviewStatus.AgentReportNote, status.RequirementNote);
    }

    [Fact]
    public void Agent_report_at_the_bound_keeps_its_tail_and_drops_the_summary()
    {
        const int bound = RemoteReviewPlanBuilder.ReviewStatus.Characters;
        var report = "report-head " + new string('r', bound) + " report-tail";

        var status = RemoteReviewPlanBuilder.ReviewStatus.Compose("Short summary.", report);

        Assert.True(status.Text.Length <= bound, $"{status.Text.Length} > {bound}");
        Assert.Contains("report-tail", status.Text);
        Assert.DoesNotContain("report-head", status.Text);
        Assert.DoesNotContain("Short summary.", status.Text);
    }

    [Fact]
    public void Plan_status_from_oversized_files_holds_the_bound()
    {
        const int bound = RemoteReviewPlanBuilder.ReviewStatus.Characters;
        File.WriteAllText(Path.Combine(_folder, "status.md"), new string('s', 40_000) + " see results/status.md");
        File.WriteAllText(Path.Combine(_folder, "results", "status.md"), new string('r', 40_000) + " report-tail");

        var status = RemoteReviewPlanBuilder.ReviewStatus.Read(_folder);

        Assert.True(status.Text.Length <= bound, $"{status.Text.Length} > {bound}");
        Assert.Contains("report-tail", status.Text);
        Assert.StartsWith(RemoteReviewPlanBuilder.ReviewStatus.AgentReportHeading, status.Text);
    }

    private ReviewPlanDto BuildPlan(string mode)
    {
        var config = new ConfigurationBuilder().Build();
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var aspects = new AspectRunnerService(prompts, NullLogger<AspectRunnerService>.Instance);
        var task = new TaskInfo
        {
            Id = "AGT-3403", Title = "Status indicator rollout", Mode = mode,
            FolderPath = _folder, ProjectName = "Test",
        };
        return new RemoteReviewPlanBuilder(aspects, config).Build(task, null, null, "refs/heads/main");
    }
}
