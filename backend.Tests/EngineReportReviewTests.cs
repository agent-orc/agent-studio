extern alias Runner;

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Prompts;
using AgentStudio.Runner;
using AgentStudio.Security;
using AgentStudio.Tasks;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.Watcher;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

using RClient = Runner::AgentRunner.TaskServerClient;
using RAcquire = Runner::AgentRunner.RunLeaseAcquireRequest;
using RRemoteComplete = Runner::AgentRunner.RemoteRunCompletionRequest;

/// <summary>
/// AGT-3797: every research delivery took <c>engine-decision 3-progress -&gt;
/// 4-auto-review (delivered, CreateReview)</c>, got no review attempt, and the
/// AI Operator's "Started the missing review step" requeued a post-processing
/// step the engine-owned queue dropped. After 30 to 61 minutes the card was
/// escalated as <c>auto-review-stranded</c> (AGT-3561, AGT-3711, AGT-3506).
/// The report review on the control plane now owns such a card.
/// </summary>
public sealed class EngineReportReviewTests : IDisposable
{
    private const string Project = "agent-runner-01";
    private const string Card = "AGT-RESEARCH-ENGINE";
    private const string ResultSha = "589c462f589c462f589c462f589c462f589c462f";
    private const string BaseSha = "4136f00d4136f00d4136f00d4136f00d4136f00d";
    private const string RepositoryUrl = "https://example.invalid/agent-studio.git";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    private readonly string _workspace = Path.Combine(Path.GetTempPath(),
        "engine-report-review-" + Guid.NewGuid().ToString("N"));
    private readonly string _projectPath;
    private readonly List<string> _calls = [];
    private string _verdict = "pass";
    // The incident day. The watchdog reads this clock for its stranding threshold.
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public EngineReportReviewTests()
    {
        _projectPath = Path.Combine(_workspace, "projects", Project);
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_projectPath, state));
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task Engine_create_review_sends_a_research_delivery_to_human_review_with_its_report_verdict()
    {
        SeedCard(TaskStates.Progress, TaskModes.Research, null);
        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        using var coding = new RClient(http, "coding-runner");
        await coding.RegisterAsync(Project, "service", CancellationToken.None);
        var lease = await coding.AcquireLeaseAsync(
            new RAcquire(Card, "coding-runner", Project, "coding-host", 4242, "codex"), CancellationToken.None);
        Assert.True(lease.Granted);
        var completion = await coding.CompleteRunAsync(new RRemoteComplete(
            Card, lease.Lease!.LeaseId, lease.Lease.FencingToken, "coding-runner", "Done",
            ResultSha: ResultSha, AttemptChainId: lease.Lease.LeaseId, Repository: RepositoryUrl,
            AttemptId: lease.Lease.AttemptId, AuthorityEpoch: lease.Lease.AuthorityEpoch,
            IdempotencyKey: "research-completion", BaseSha: BaseSha,
            ImmutableResultRef: FencedGitRefs.ImmutableResult(lease.Lease.AttemptId!, lease.Lease.FencingToken,
                ResultSha),
            ArtifactManifestDigest: new string('a', 64)), CancellationToken.None);
        Assert.True(completion!.TargetState == TaskStates.Progress, completion.Message);

        // The engine answers the settled delivery with the shared library's
        // coding decision, exactly as OrchestrationStageHandlers does.
        var engine = ConnectEngine(factory);
        var claim = await engine.Claim();
        var transition = StageDecisionPolicy.Coding(Card, lease.Lease.AttemptId!, TaskStates.AutoReview);
        Assert.Equal(TransitionAction.CreateReview, transition.Action);
        Assert.Equal("completed", (await engine.Complete(claim,
            JsonSerializer.Serialize(new { transition }))).Status);

        var scanner = factory.Services.GetRequiredService<TaskScannerService>();
        var card = await WaitForStateAsync(scanner, TaskStates.HumanReview);
        var authority = factory.Services.GetRequiredService<AttemptAuthorityService>();
        Assert.Null(authority.GetTaskProjection(Card).CurrentReviewAttempt);
        Assert.Single(_calls);
        Assert.Equal("pass", CompletionContentEvidence.Read(card, authority).ContentStatus);
        var timeline = factory.Services.GetRequiredService<TimelineLog>().ReadAll(card.FolderPath);
        Assert.Contains(timeline, entry => entry.Kind == TimelineEventKinds.PostStepStarted
            && entry.Summary.Contains("Report review started (engine-create-review)", StringComparison.Ordinal));
        Assert.Contains(timeline, entry => entry.Kind == TimelineEventKinds.PostStepFinished
            && entry.Summary.Contains("Requirement fit of the report: pass", StringComparison.Ordinal));
        Assert.Contains(timeline, entry => entry.Kind == TimelineEventKinds.LaneChanged
            && entry.Details?.GetValueOrDefault(LaneChangeCauses.DetailQualifierKey) == "report-review");
    }

    [Fact]
    public async Task Failed_report_review_parks_the_card_with_the_reason()
    {
        _verdict = "";
        SeedCard(TaskStates.AutoReview, TaskModes.Research, Now);
        using var factory = BuildFactory();
        SeedCompletedRun(factory.Services);
        var scanner = factory.Services.GetRequiredService<TaskScannerService>();
        var reviews = factory.Services.GetRequiredService<EngineReportReviewService>();

        var start = reviews.Start(scanner.FindJob(Card, _projectPath)!, "test");
        Assert.True(start.Started, start.Reason);
        var decision = await start.Completion!.WaitAsync(Deadline);

        Assert.Equal(ReportReviewAction.Escalate, decision.Action);
        var parked = scanner.FindJob(Card, _projectPath)!;
        Assert.Equal(TaskStates.Escalated, parked.State);
        var marker = ParkedBlockerMarker.TryRead(parked.FolderPath)!;
        Assert.Equal(EngineReportReviewService.ParkDetail, marker.CauseDetail);
        Assert.Contains("The report review produced no verdict", marker.Reason);
        Assert.Contains("The requirement-fit review failed", marker.Reason);
        Assert.Contains("Run Review results", marker.Reason);
        Assert.False(reviews.IsRunning(parked));
    }

    [Fact]
    public void Coding_cards_and_monolith_owned_auto_review_are_not_taken_over()
    {
        SeedCard(TaskStates.AutoReview, TaskModes.Coding, Now);
        using (var factory = BuildFactory())
        {
            var coding = factory.Services.GetRequiredService<TaskScannerService>().FindJob(Card, _projectPath)!;
            var reviews = factory.Services.GetRequiredService<EngineReportReviewService>();
            Assert.False(reviews.Owns(coding));
            var refused = reviews.Start(coding, "test");
            Assert.False(refused.Started);
            Assert.Contains("Only planning and research cards", refused.Reason);
            Assert.Empty(factory.Services.GetRequiredService<TimelineLog>().ReadAll(coding.FolderPath));
        }

        var research = new TaskInfo
        {
            Id = Card, Mode = TaskModes.Research, State = TaskStates.AutoReview, ProjectName = Project,
        };
        using var monolith = BuildFactory(engine: false);
        Assert.False(monolith.Services.GetRequiredService<EngineReportReviewService>().Owns(research));
        Assert.False(MonolithTransitionAdapter.ReportReviewHostRuns(reportOnly: false, monolithOwnsAutoReview: false));
        Assert.False(MonolithTransitionAdapter.ReportReviewHostRuns(reportOnly: true, monolithOwnsAutoReview: true));
        Assert.True(MonolithTransitionAdapter.ReportReviewHostRuns(reportOnly: true, monolithOwnsAutoReview: false));
    }

    [Fact]
    public async Task Watchdog_starts_the_report_review_of_a_research_card_stranded_in_auto_review_once()
    {
        SeedCard(TaskStates.AutoReview, TaskModes.Research, Now.AddHours(-2));
        using var factory = BuildFactory(pinClock: true);
        var run = SeedCompletedRun(factory.Services);
        var records = factory.Services.GetRequiredService<StallInterventionStore>();
        // The incident's no-op receipt: the requeue was recorded as started.
        records.Save(StrandingRecord("auto-review-stranding:legacy", "Started the missing review step.", run));
        var watchdog = factory.Services.GetRequiredService<AutoReviewStrandingWatchdog>();

        Assert.Equal(1, await watchdog.RunOnceAsync());
        var scanner = factory.Services.GetRequiredService<TaskScannerService>();
        var card = await WaitForStateAsync(scanner, TaskStates.HumanReview);
        Assert.Equal(0, await watchdog.RunOnceAsync());

        var recovery = Assert.Single(records.All(), item => item.Kind == "report-review-recovery");
        Assert.Equal(StallInterventionStatuses.Verified, recovery.Status);
        Assert.Equal(run.AttemptId, recovery.RunAttemptId);
        Assert.Contains("Started the report review", recovery.Done);
        Assert.Single(_calls);
        Assert.Equal("pass", CompletionContentEvidence.Read(card,
            factory.Services.GetRequiredService<AttemptAuthorityService>()).ContentStatus);
    }

    [Fact]
    public async Task Watchdog_returns_an_unchanged_stranded_park_to_the_report_review_once()
    {
        SeedCard(TaskStates.AutoReview, TaskModes.Research, Now.AddHours(-2));
        using var factory = BuildFactory(pinClock: true);
        var run = SeedCompletedRun(factory.Services);
        var scanner = factory.Services.GetRequiredService<TaskScannerService>();
        var moved = await factory.Services.GetRequiredService<HumanReviewEscalation>().EscalateAsync(
            Card, _projectPath, Project, HumanReviewEscalationCategories.Environmental,
            "[environmental] Auto Review had no active owner for 30 minutes. No review attempt was recorded.",
            expectedSourceState: TaskStates.AutoReview,
            parkDetail: HumanReviewEscalationCategories.AutoReviewStranded);
        Assert.Equal(MoveJobStatus.Success, moved.Status);
        var parked = scanner.FindJob(Card, _projectPath)!;
        var records = factory.Services.GetRequiredService<StallInterventionStore>();
        records.Save(StrandingRecord("auto-review-stranding:park", "Escalated.", run) with
        {
            Status = StallInterventionStatuses.Escalated, Action = nameof(AutoReviewStrandingAction.Escalate),
            ParkedAtUtc = parked.EnteredLaneAt, ParkedTaskWriteTicks = parked.TaskJsonWriteTicks,
            Undoable = true,
        });
        var watchdog = factory.Services.GetRequiredService<AutoReviewStrandingWatchdog>();

        Assert.Equal(1, await watchdog.RunOnceAsync());
        var card = await WaitForStateAsync(scanner, TaskStates.HumanReview);
        Assert.Equal(0, await watchdog.RunOnceAsync());

        Assert.Single(_calls);
        var park = Assert.Single(records.All(), item => item.Kind == "auto-review-stranded");
        Assert.Equal("report-review-recovery", park.RevertedBy);
        Assert.False(park.Undoable);
        var recovery = Assert.Single(records.All(), item => item.Kind == "report-review-recovery");
        Assert.Equal(TaskStates.Escalated, recovery.Before);
        Assert.Equal(TaskStates.HumanReview, card.State);
    }

    [Fact]
    public async Task Watchdog_leaves_a_park_it_did_not_record_alone()
    {
        SeedCard(TaskStates.Escalated, TaskModes.Research, Now.AddHours(-2));
        using var factory = BuildFactory(pinClock: true);
        SeedCompletedRun(factory.Services);
        var watchdog = factory.Services.GetRequiredService<AutoReviewStrandingWatchdog>();

        // No receipt names this park, so the watchdog did not park it.
        Assert.Equal(0, await watchdog.RunOnceAsync());
        Assert.Equal(TaskStates.Escalated,
            factory.Services.GetRequiredService<TaskScannerService>().FindJob(Card, _projectPath)!.State);
        Assert.Empty(_calls);
    }

    private StallInterventionRecord StrandingRecord(string writeKey, string done, RunAttemptDto run) => new()
    {
        Id = StallInterventionStore.IdFor(writeKey), WriteKey = writeKey,
        Subject = "auto-review:" + Card, Kind = "auto-review-stranded", Host = "task-server",
        Action = nameof(AutoReviewStrandingAction.StartStep), Autonomy = "A1", Disposition = "act",
        ReasonCode = "auto-review-no-owner", Status = StallInterventionStatuses.Verified,
        Project = Project, AffectedCards = [Card], Done = done, Receipt = done,
        DecidedAtUtc = Now.AddMinutes(-90), ResolvedAtUtc = Now.AddMinutes(-90),
        RunAttemptId = run.AttemptId, DeliveryResultSha = run.ResultSha,
    };

    private RunAttemptDto SeedCompletedRun(IServiceProvider services)
    {
        var repositoryId = RepositoryIdentityContract.FromUrl(RepositoryUrl)!;
        var authority = services.GetRequiredService<AttemptAuthorityService>();
        var run = authority.AcquireRun(Card, repositoryId, null, "coding-runner", "coding-host", 120,
            "seed-research-run", briefVersion: BriefVersionStore.ReadCurrent(CardFolder())).RunAttempt!;
        Assert.True(authority.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(run.AttemptId, run.LastFence, run.AuthorityEpoch,
                "seed-research-completion"),
            Outcome = "done",
            ResultSha = ResultSha,
            ResultEnvelope = new ImmutableResultEnvelope(repositoryId, run.AttemptId, BaseSha, ResultSha,
                "refs/heads/agent-studio/results/research", null, new string('a', 64),
                RepositoryUrl: RepositoryUrl),
        }).Accepted);
        return authority.GetRun(run.AttemptId)!;
    }

    private string CardFolder()
        => TaskStates.All.Select(state => Path.Combine(_projectPath, state, Card)).First(Directory.Exists);

    private void SeedCard(string state, string mode, DateTime? enteredLaneAt)
    {
        var folder = Path.Combine(_projectPath, state, Card);
        Directory.CreateDirectory(Path.Combine(folder, "results"));
        var task = new Dictionary<string, object?>
        {
            ["id"] = Card, ["key"] = Card, ["title"] = "Research the lease renewal", ["state"] = state,
            ["order"] = 1, ["agent"] = "claude", ["kind"] = TaskKinds.Task, ["mode"] = mode,
        };
        // A claimed card enters Progress now; only a lane wait in the past is seeded.
        if (enteredLaneAt is { } entered) task["enteredLaneAt"] = entered;
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(task));
        File.WriteAllText(Path.Combine(folder, "prompt.md"), "# Research the lease renewal\n\nReport the renewal interval.\n");
        File.WriteAllText(Path.Combine(folder, "status.md"), "Result: pending.");
        File.WriteAllText(Path.Combine(folder, "results", "report.html"),
            "<!doctype html><html><body><h1>Lease renewal</h1><p>Findings: the lease renews every 30 seconds.</p></body></html>");
        BriefVersionStore.ReadOrCreate(folder);
    }

    private async Task<TaskInfo> WaitForStateAsync(TaskScannerService scanner, string state)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (waited.Elapsed < Deadline)
        {
            scanner.InvalidateCache();
            if (scanner.FindJob(Card, _projectPath) is { } card && card.State == state) return card;
            await Task.Delay(50);
        }
        throw new TimeoutException($"{Card} did not reach {state}; it is in "
            + scanner.FindJob(Card, _projectPath)?.State + ".");
    }

    private ReportContentReviewService ReportReview(IServiceProvider services)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        var prompts = new RuntimePromptService(configuration, NullLogger<RuntimePromptService>.Instance);
        var aspects = new AspectRunnerService(prompts, NullLogger<AspectRunnerService>.Instance)
        {
            VerdictRetryBackoff = _ => TimeSpan.Zero,
        };
        aspects.CliRunner = (aspectId, _, _, _, _, _) =>
        {
            lock (_calls) _calls.Add(aspectId);
            // An empty reply is a dead reviewer: no verdict, even after the retry.
            return Task.FromResult(_verdict.Length == 0
                ? string.Empty
                : $"Checked results/report.html.\n[[ASPECT_VERDICT: status={_verdict}; summary=The report answers the brief; "
                    + "evidence_checked=results/report.html; missing=none]]\n[[TASK_DONE]]");
        };
        return new ReportContentReviewService(aspects, configuration,
            authority: services.GetRequiredService<AttemptAuthorityService>(),
            timeline: services.GetRequiredService<TimelineLog>());
    }

    private EngineClient ConnectEngine(WebApplicationFactory<Program> factory)
    {
        var security = factory.Services.GetRequiredService<AccessSecurityStore>();
        var enrollment = security.CreateEnrollment(new RunnerEnrollmentRequest("engine-one",
            [RunnerScopes.Orchestration], null, null));
        var engine = security.EnrollRunner(enrollment.Code);
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", engine.Secret);
        http.DefaultRequestHeaders.Add("X-Client-Id", engine.Runner.Id);
        return new EngineClient(http, engine.Runner.Id);
    }

    private WebApplicationFactory<Program> BuildFactory(bool engine = true, bool pinClock = false) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["TaskRepository"] = _workspace,
                    ["WatchPaths:0:Name"] = Project,
                    ["WatchPaths:0:Path"] = _projectPath,
                    ["WatchPaths:0:RootPath"] = _projectPath,
                    ["WatchPaths:0:RepositoryPath"] = _projectPath,
                    ["ReviewDecisionOrchestrator:Enabled"] = "false",
                    ["ReviewDecisionOrchestrator:Cli"] = "claude",
                    ["Runner:RunLiveness:Enabled"] = "false",
                    ["Runner:ReviewInfrastructureRetry:Enabled"] = "false",
                    ["Supervisor:StuckResumeWindowMinutes"] = "0",
                    ["Orchestration:Transitions:CodingCompletion"] = engine ? "Engine" : "Monolith",
                    ["Orchestration:Transitions:ReviewReport"] = engine ? "Engine" : "Monolith",
                }));
            builder.ConfigureTestServices(services =>
            {
                // The test drives the watchdog tick by tick.
                services.RemoveAll<IHostedService>();
                if (pinClock) services.AddSingleton<TimeProvider>(_clock);
                services.RemoveAll<ReportContentReviewService>();
                services.AddSingleton(ReportReview);
            });
        });

    private sealed class EngineClient(HttpClient client, string engineId)
    {
        public async Task<OrchestrationClaimResponse> Claim()
        {
            var response = await client.PostAsJsonAsync("/api/v1/orchestration/claims",
                new OrchestrationClaimRequest(engineId, "instance", [OrchestrationStage.ReviewDecision]));
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<OrchestrationClaimResponse>())!;
        }

        public async Task<OrchestrationRunDto> Complete(OrchestrationClaimResponse claim, string outputJson)
        {
            var response = await client.PostAsJsonAsync(
                $"/api/v1/orchestration/runs/{claim.Run!.RunId}/stages/complete",
                new CompleteOrchestrationStageRequest(engineId, "instance",
                    claim.Lease!.LeaseId, claim.Lease.Fence, OrchestrationStage.ReviewDecision,
                    OrchestrationAction.Continue, outputJson, $"complete:{claim.Run.RunId}"));
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<OrchestrationRunDto>())!;
        }
    }
}
