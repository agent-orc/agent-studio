using AgentStudio.Prompts;
using AgentStudio.Runner;
using AgentStudio.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3565: research cards AGT-3496, AGT-3506, AGT-3511, AGT-3512, AGT-3515
/// and AGT-3347 could not be completed: "no content verdict
/// (requirement-fit); the current run has no review; content verdict has no
/// brief version". Report cards get no remote review, so the lightweight
/// pipeline now reviews the delivered report itself and binds the verdict to
/// the run it reviewed. Review results runs that review late for a card whose
/// run finished without it.
/// </summary>
public sealed class ReportContentReviewTests : IDisposable
{
    private const string TaskKey = "AGT-3565-FIXTURE";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "report-review-" + Guid.NewGuid().ToString("N"));
    private readonly List<(string Aspect, string Prompt)> _calls = [];
    private readonly AttemptAuthorityService _authority = new(NullLogger<AttemptAuthorityService>.Instance);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task Pass_verdict_on_the_report_lets_the_research_card_complete()
    {
        var (task, run) = RemoteResearchCard();
        var service = Service("pass");

        var result = await service.RunAsync(task, "PROJ-FIXTURE", CancellationToken.None);

        Assert.True(result.Ran);
        Assert.Equal("pass", result.Status);
        var call = Assert.Single(_calls);
        Assert.Equal("requirement-fit", call.Aspect);
        Assert.Contains("Findings: the lease renews every 30 seconds.", call.Prompt);
        Assert.DoesNotContain("<p>", call.Prompt.Split("markup removed")[1]);
        var facts = CompletionContentEvidence.Read(task, _authority);
        Assert.Equal("pass", facts.ContentStatus);
        Assert.Equal(run.BriefVersion, facts.DeliveryBriefVersion);
        Assert.Equal(facts.CurrentBriefVersion, facts.ReviewBriefVersion);
        Assert.Null(facts.Settlement);
        var decision = CompletionContentPolicy.Decide(facts);
        Assert.True(decision.Accepted, decision.Message);
        Assert.False(decision.Overridden);
    }

    [Fact]
    public async Task Concerns_verdict_keeps_completion_refused()
    {
        var (task, _) = RemoteResearchCard();

        await Service("concerns").RunAsync(task, "PROJ-FIXTURE", CancellationToken.None);

        var decision = CompletionContentPolicy.Decide(CompletionContentEvidence.Read(task, _authority));
        Assert.False(decision.Accepted);
        Assert.Contains("requirement-fit verdict was concerns", decision.Message);
    }

    [Fact]
    public void Research_card_without_report_review_names_the_action_that_fixes_it()
    {
        var (task, _) = RemoteResearchCard();

        var facts = CompletionContentEvidence.Read(task, _authority);
        var decision = CompletionContentPolicy.Decide(facts);

        Assert.Equal(CompletionSettlementProblem.NoReportReview, facts.Settlement!.Kind);
        Assert.False(decision.Accepted);
        Assert.Contains("no content verdict (requirement-fit); the report of the current run has not been reviewed; "
            + "run Review results", decision.Message);
    }

    [Fact]
    public void Report_verdict_for_another_run_does_not_count()
    {
        var (task, _) = RemoteResearchCard();
        File.WriteAllText(Path.Combine(task.FolderPath, "aspect-requirement-fit.json"),
            """{"aspect":"requirement-fit","status":"pass","summary":"Older run."}""");
        CompletionContentEvidence.WriteLocalContext(task.FolderPath,
            BriefVersionStore.ReadCurrent(task.FolderPath), "run_older", DateTime.UnixEpoch);

        var facts = CompletionContentEvidence.Read(task, _authority);

        Assert.Null(facts.ContentStatus);
        Assert.False(CompletionContentPolicy.Decide(facts).Accepted);
    }

    [Fact]
    public async Task Review_results_runs_only_the_missing_review_once()
    {
        var (task, _) = RemoteResearchCard();
        var service = Service("pass");
        Assert.Null(service.LateReviewRefusal(task));

        var first = await service.RunLateAsync(task, "PROJ-FIXTURE", CancellationToken.None);
        var second = await service.RunLateAsync(task, "PROJ-FIXTURE", CancellationToken.None);

        Assert.True(first.Ran);
        Assert.False(second.Ran);
        Assert.Contains("already has a requirement-fit verdict", second.Reason);
        Assert.Single(_calls);
        Assert.Equal(TaskStates.HumanReview, task.State);
    }

    [Fact]
    public async Task Review_results_does_not_review_a_concerns_verdict_again()
    {
        var (task, _) = RemoteResearchCard();
        var service = Service("concerns");
        await service.RunLateAsync(task, "PROJ-FIXTURE", CancellationToken.None);

        Assert.Contains("already has a requirement-fit verdict", service.LateReviewRefusal(task));
        Assert.Single(_calls);
    }

    [Theory]
    [InlineData(TaskModes.Research, TaskStates.Progress, "runs on a card in")]
    [InlineData(TaskModes.Research, TaskStates.AutoReview, "runs on a card in")]
    [InlineData(TaskModes.Coding, TaskStates.HumanReview, "planning and research cards")]
    [InlineData(TaskModes.Concept, TaskStates.HumanReview, "planning and research cards")]
    public async Task Review_results_refuses_cards_it_does_not_own(string mode, string state, string reason)
    {
        var (task, _) = RemoteResearchCard();
        task = task with { Mode = mode, State = state };
        var service = Service("pass");

        var result = await service.RunLateAsync(task, "PROJ-FIXTURE", CancellationToken.None);

        Assert.False(result.Ran);
        Assert.Contains(reason, result.Reason);
        Assert.Empty(_calls);
    }

    [Fact]
    public void Report_text_drops_markup_scripts_and_styles()
    {
        var text = ReportContentReviewService.ReportText(
            "<html><head><title>T</title><style>p{color:red}</style></head><body><h1>Result</h1>"
            + "<script>alert(1)</script><p>First &amp; second.</p><ul><li>One</li><li>Two</li></ul></body></html>");

        Assert.Equal("Result\nFirst & second.\nOne\nTwo", text.Replace(" \n", "\n").Replace("\n\n", "\n"));
    }

    private (TaskInfo Task, RunAttemptDto Run) RemoteResearchCard()
    {
        var folder = Path.Combine(_root, "card");
        Directory.CreateDirectory(Path.Combine(folder, "results"));
        File.WriteAllText(Path.Combine(folder, "prompt.md"), "# Research the lease renewal\n\nReport the renewal interval.\n");
        File.WriteAllText(Path.Combine(folder, "results", "report.html"),
            "<!doctype html><html><body><h1>Lease renewal</h1><p>Findings: the lease renews every 30 seconds.</p></body></html>");
        var brief = BriefVersionStore.ReadOrCreate(folder);
        var run = _authority.AcquireRun(TaskKey, "PROJ-FIXTURE", null, "fixture-runner", "fixture-host", 60,
            "claim-run", briefVersion: brief).RunAttempt!;
        return (new TaskInfo
        {
            Id = TaskKey,
            TaskKey = TaskKey,
            Key = TaskKey,
            Title = "Research the lease renewal",
            Mode = TaskModes.Research,
            State = TaskStates.HumanReview,
            ProjectName = "PROJ-FIXTURE",
            FolderPath = folder,
        }, run);
    }

    private ReportContentReviewService Service(string status)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ReviewDecisionOrchestrator:Cli"] = "claude",
        }).Build();
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var aspects = new AspectRunnerService(prompts, NullLogger<AspectRunnerService>.Instance)
        {
            VerdictRetryBackoff = _ => TimeSpan.Zero,
        };
        aspects.CliRunner = (aspectId, _, _, prompt, _, _) =>
        {
            lock (_calls) _calls.Add((aspectId, prompt));
            return Task.FromResult(
                $"Checked results/report.html.\n[[ASPECT_VERDICT: status={status}; summary=The report answers the brief; "
                + "evidence_checked=results/report.html; missing="
                + (status == "pass" ? "none" : "the renewal failure mode") + "]]\n[[TASK_DONE]]");
        };
        return new ReportContentReviewService(aspects, config, authority: _authority);
    }
}
