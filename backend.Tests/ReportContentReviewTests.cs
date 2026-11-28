extern alias TaskServerAssembly;

using AgentStudio.Prompts;
using AgentStudio.Runner;
using AgentStudio.Tasks;
using AgentStudio.Cli;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TaskServerStore = TaskServerAssembly::AgentStudio.TaskServer.TaskServerStore;
using TaskServerOptions = TaskServerAssembly::AgentStudio.TaskServer.TaskServerOptions;
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
    public async Task Combined_runner_wait_then_review_results_records_the_report_verdict()
    {
        var (task, _) = RemoteResearchCard();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero));
        var store = new TaskServerStore(Options.Create(new TaskServerOptions
        {
            DataDirectory = Path.Combine(_root, "one-shot-store"),
        }), clock);
        var jobs = new CombinedOneShotJobs(store);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ReviewDecisionOrchestrator:Cli"] = "codex",
            ["Security:Profile"] = "networked",
        }).Build();
        var remote = new RemoteCliOneShot("codex", new UnusedClients(), config,
            pollInterval: TimeSpan.FromMilliseconds(10), combinedJobs: jobs);
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var aspects = new AspectRunnerService(prompts, NullLogger<AspectRunnerService>.Instance,
            oneShotRegistry: new CliOneShotRegistry([remote]));
        var review = new ReportContentReviewService(aspects, config, authority: _authority);

        var waiting = await review.RunAsync(task, "PROJ-FIXTURE", CancellationToken.None);
        Assert.True(waiting.HostWaiting);
        Assert.Contains("waiting for a host with model-acceptance:codex:gpt-6.2-sol", waiting.Reason);
        Assert.True(review.IsMissing(task));

        await jobs.RegisterAsync("runner-a", new RegisterRunnerRequest("runner", "host-a", "instance-a",
            "1.0.0", TaskServerProtocol.Current, [ReviewCapabilities.CodingExecutor]), default);
        await jobs.AdvertiseAsync(new CapabilityAdvertisementRequest("runner-a", "instance-a",
            CapabilityProtocol.CurrentSchemaVersion, clock.GetUtcNow().UtcDateTime, 300, 1,
            [new AdvertisedCapabilityDto(CapabilityProtocol.ModelAcceptance("codex", CodexCliRelease.Gpt62Sol),
                "model-acceptance", "ready", CredentialGeneration: "fixture-login",
                CredentialObservedAt: clock.GetUtcNow().UtcDateTime, ProbeSequence: 1)]), default);

        var late = review.RunLateAsync(task, "PROJ-FIXTURE", CancellationToken.None);
        OneShotJobWork? work = null;
        for (var attempt = 0; attempt < 100 && work is null; attempt++)
        {
            work = await jobs.ClaimAsync(new OneShotJobClaimRequest("runner-a", "instance-a"), default);
            if (work is null) await Task.Delay(10);
        }
        Assert.NotNull(work);
        Assert.Equal(CodexCliRelease.Gpt62Sol, work.Request.Model);
        Assert.Equal("medium", work.Request.ThinkingLevel);
        Assert.Contains("results/report.html", work.Request.Prompt);
        Assert.True(await jobs.CompleteAsync(new OneShotJobCompletion("runner-a", "instance-a",
            work.JobId, work.ClaimToken, true,
            "Checked results/report.html.\n[[ASPECT_VERDICT: status=pass; summary=The report answers the brief; evidence_checked=results/report.html; missing=none]]\n[[TASK_DONE]]",
            null, null), default));
        var result = await late;
        Assert.True(result.Ran, result.Reason);
        Assert.Equal("pass", result.Status);
        Assert.Equal("pass", CompletionContentEvidence.Read(task, _authority).ContentStatus);
    }

    private sealed class UnusedClients : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("No standalone URL is configured.");
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

    [Theory]
    [InlineData(TaskStates.HumanReview)]
    [InlineData(TaskStates.Escalated)]
    public async Task Review_results_runs_only_the_missing_review_once(string state)
    {
        var (task, _) = RemoteResearchCard();
        task = task with { State = state };
        var service = Service("pass");
        Assert.Null(service.LateReviewRefusal(task));

        var first = await service.RunLateAsync(task, "PROJ-FIXTURE", CancellationToken.None);
        var second = await service.RunLateAsync(task, "PROJ-FIXTURE", CancellationToken.None);

        Assert.True(first.Ran);
        Assert.False(second.Ran);
        Assert.Contains("already has a requirement-fit verdict", second.Reason);
        Assert.Single(_calls);
        Assert.Equal(state, task.State);
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

    [Theory]
    [InlineData(null)]
    [InlineData("# Lease renewal\n\nFindings in Markdown, not the HTML report.\n")]
    public async Task Review_results_refuses_a_research_card_without_its_report(string? report)
    {
        var (task, _) = RemoteResearchCard();
        var path = Path.Combine(task.FolderPath, "results", "report.html");
        File.Delete(path);
        if (report is not null) File.WriteAllText(path, report);
        File.WriteAllText(Path.Combine(task.FolderPath, "results", "notes.md"), "Draft notes.");
        var service = Service("pass");

        var late = await service.RunLateAsync(task, "PROJ-FIXTURE", CancellationToken.None);
        var pipeline = await service.RunAsync(task, "PROJ-FIXTURE", CancellationToken.None);

        Assert.False(late.Ran);
        Assert.Contains("no required report: a valid HTML document at results/report.html", late.Reason);
        Assert.False(pipeline.Ran);
        Assert.Empty(_calls);
        var decision = CompletionContentPolicy.Decide(CompletionContentEvidence.Read(task, _authority));
        Assert.False(decision.Accepted);
        Assert.Contains("the required report is missing", decision.Message);
    }

    [Fact]
    public async Task Pass_verdict_does_not_complete_a_research_card_whose_report_is_gone()
    {
        var (task, _) = RemoteResearchCard();
        await Service("pass").RunLateAsync(task, "PROJ-FIXTURE", CancellationToken.None);
        File.Delete(Path.Combine(task.FolderPath, "results", "report.html"));

        var facts = CompletionContentEvidence.Read(task, _authority);
        var decision = CompletionContentPolicy.Decide(facts);
        var overridden = CompletionContentPolicy.Decide(facts with
        {
            OperatorOverride = true,
            OverrideReason = "Operator accepts the research findings.",
        });

        Assert.Equal("pass", facts.ContentStatus);
        Assert.Equal(ReportDeliverable.ResearchRequirement, facts.MissingReport);
        Assert.False(decision.Accepted);
        Assert.Contains("the required report is missing (a valid HTML document at results/report.html)", decision.Message);
        Assert.False(overridden.Accepted);
        Assert.Contains("An override cannot replace the missing report.", overridden.Message);
    }

    [Fact]
    public void Report_text_drops_markup_scripts_and_styles()
    {
        var text = ReportContentReviewService.ReportText(
            "<html><head><title>T</title><style>p{color:red}</style></head><body><h1>Result</h1>"
            + "<script>alert(1)</script><p>First &amp; second.</p><ul><li>One</li><li>Two</li></ul></body></html>");

        Assert.Equal("Result\nFirst & second.\nOne\nTwo", text.Replace(" \n", "\n").Replace("\n\n", "\n"));
    }

    [Fact]
    public async Task Review_reads_the_agent_delivery_summary_not_the_runner_run_record()
    {
        // AGT-3853: AGT-3347 was blocked because the review read the runner's
        // root status.md ("Duration: 5.8 min") instead of results/status.md.
        var (task, _) = RemoteResearchCard();
        File.WriteAllText(Path.Combine(task.FolderPath, "status.md"),
            "# Status\n\nResult: Success\nCase: forensics\nDuration: 5.8 min\n");
        File.WriteAllText(Path.Combine(task.FolderPath, "results", "status.md"),
            "# Delivery\n\nQ1: the lease renews every 30 seconds.\n" + new string('x', 5_000)
            + "\nRanked levers: 1. shorter renewal.\nProposed follow-up cards: AGT-FOLLOW-1.\n");

        await Service("pass").RunAsync(task, "PROJ-FIXTURE", CancellationToken.None);

        var prompt = Assert.Single(_calls).Prompt;
        Assert.Contains("Delivery summary written by the agent (results/status.md):", prompt);
        Assert.Contains("Proposed follow-up cards: AGT-FOLLOW-1.", prompt);
        var delivery = prompt.IndexOf("Delivery summary written by the agent", StringComparison.Ordinal);
        var record = prompt.IndexOf("Runner run record (task-root status.md", StringComparison.Ordinal);
        Assert.True(delivery >= 0 && record > delivery);
        Assert.Contains("It is not the deliverable", prompt);
        Assert.DoesNotContain("Head of results/status.md", prompt);
    }

    [Fact]
    public async Task Review_inventory_names_captures_in_results_subfolders_newest_first()
    {
        // AGT-3648: live captures in results/watchdog-live/ were not seen.
        var (task, _) = RemoteResearchCard();
        var results = Path.Combine(task.FolderPath, "results");
        var old = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 45; i++)
        {
            var path = Path.Combine(results, $"a-round1-{i:00}--mocked.png");
            File.WriteAllBytes(path, [1]);
            File.SetLastWriteTimeUtc(path, old);
        }
        Directory.CreateDirectory(Path.Combine(results, "watchdog-live"));
        File.WriteAllBytes(Path.Combine(results, "watchdog-live", "escalation--real.png"), [1, 2]);

        await Service("pass").RunAsync(task, "PROJ-FIXTURE", CancellationToken.None);

        var prompt = Assert.Single(_calls).Prompt;
        Assert.Contains("newest first:", prompt);
        Assert.Contains("- results/watchdog-live/escalation--real.png (2 bytes)", prompt);
        Assert.Contains("results/watchdog-live/ (1 file(s))", prompt);
        Assert.Contains("older file(s) not listed.", prompt);
    }

    [Fact]
    public void Long_report_is_read_past_the_old_clip_and_names_the_sections_it_cut()
    {
        var html = "<html><body><h1>Report</h1><p>" + new string('a', 30_000) + "</p><h2>7.1 Time budget by purpose</h2><p>split</p>"
            + "<p>" + new string('b', ReportContentReviewService.ReportTextCharacters) + "</p><h2>9 Follow-up cards</h2><p>cards</p></body></html>";
        var text = ReportContentReviewService.ReportText(html);

        var clipped = ReportContentReviewService.ClipReport(html, text);

        Assert.Contains("7.1 Time budget by purpose", clipped);
        Assert.Contains("Sections not shown above", clipped);
        Assert.EndsWith("9 Follow-up cards.", clipped);
        Assert.Equal("Short report.", ReportContentReviewService.ClipReport("<p>Short report.</p>", "Short report."));
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
