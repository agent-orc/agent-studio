using System.Net.Http.Json;
using System.Text.Json;

using AgentStudio.OrchestrationDecisions;
using AgentStudio.Watcher;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

using Xunit;

using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3691 (AGT-3648 finding S6-F15): the review host restarted, the report
/// with ExecutorRestarted scheduled retry 1/3 for one minute later, and nine
/// seconds after the report the stranding watchdog escalated the card because
/// a terminal review with a scheduled retry did not count as an owner. At the
/// due time the scheduler found the card outside Auto Review and cleared the
/// retry without a word. No review ran again.
/// </summary>
public sealed class ReviewInfrastructureRetryStrandingTests : IDisposable
{
    private const string ProjectName = "agent-runner-01";
    private const string TaskKey = "AGT-STRANDED-RETRY";
    private const string RunnerId = "review-runner-restart";
    private const string Instance = "review-host:restart";
    private const string ResultSha = "589c462f589c462f589c462f589c462f589c462f";
    private const string RepositoryUrl = "https://example.invalid/agent-studio.git";

    private static readonly string[] ReviewExecutorCapabilities =
    [
        Contract.ReviewCapabilities.ReviewExecutor,
        Contract.ReviewCapabilities.BaselineComparison,
        Contract.ReviewCapabilities.DependencyPreparation,
        Contract.ReviewCapabilities.GitMaterialization,
        Contract.ReviewCapabilities.SemanticReview,
        Contract.ReviewCapabilities.LibraryStepV1,
        Contract.CapabilityProtocol.CliExecution(CliTypes.Codex),
        Contract.CapabilityProtocol.ProviderAuthentication(CliTypes.Codex),
    ];

    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(), "atp-stranded-retry-" + Guid.NewGuid().ToString("N"));
    private readonly string _watchPath;
    // The incident's report time. The report's lease check, the authority and
    // the watchdog all read this one clock.
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 10, 1, 27, 17, TimeSpan.Zero));

    public ReviewInfrastructureRetryStrandingTests()
    {
        _watchPath = Path.Combine(_workspace, "projects", ProjectName);
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task Watchdog_tick_inside_the_backoff_leaves_the_card_in_auto_review_and_the_retry_runs()
    {
        // Far beyond the default 30 minute threshold: every card that sat in
        // Auto Review that long when its review host restarted was escalated.
        SeedTask(TaskStates.AutoReview, _time.GetUtcNow().UtcDateTime.AddHours(-2));
        using var factory = BuildFactory();
        var authority = factory.Services.GetRequiredService<AttemptAuthorityService>();
        var failed = await ReportExecutorRestartedAsync(factory);
        Assert.True(authority.HasScheduledReviewInfrastructureRetry(failed));

        // The watchdog ticks inside the one-minute backoff.
        _time.Advance(TimeSpan.FromSeconds(9));
        var watchdog = factory.Services.GetRequiredService<AutoReviewStrandingWatchdog>();
        Assert.Equal(0, await watchdog.RunOnceAsync());
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.AutoReview, TaskKey)));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Escalated, TaskKey)));
        Assert.True(authority.HasScheduledReviewInfrastructureRetry(failed));
        Assert.Empty(factory.Services.GetRequiredService<StallInterventionStore>().All());

        // At the due time the scheduler mints the successor review.
        _time.Advance(TimeSpan.FromMinutes(1));
        var scheduler = factory.Services.GetRequiredService<ReviewInfrastructureRetryScheduler>();
        Assert.Equal(1, scheduler.RunOnce());
        var successor = authority.GetTaskProjection(TaskKey).CurrentReviewAttempt!;
        Assert.NotEqual(failed, successor.AttemptId);
        Assert.Equal(failed, successor.SourceReviewAttemptId);
        Assert.Equal(AttemptLifecycleState.Pending, successor.State);
        Assert.DoesNotContain(ReadTimeline(TaskStates.AutoReview),
            entry => entry.GetProperty("kind").GetString() == TimelineEventKinds.ReviewInfrastructureRetryDropped);
    }

    [Fact]
    public async Task Retry_that_no_tick_fires_within_the_threshold_no_longer_owns_the_card()
    {
        SeedTask(TaskStates.AutoReview, _time.GetUtcNow().UtcDateTime.AddHours(-2));
        using var factory = BuildFactory();
        var failed = await ReportExecutorRestartedAsync(factory);

        // The scheduler is down: the retry is overdue by more than the threshold.
        _time.Advance(TimeSpan.FromMinutes(32));
        var watchdog = factory.Services.GetRequiredService<AutoReviewStrandingWatchdog>();

        Assert.Equal(1, await watchdog.RunOnceAsync());
        Assert.Contains(factory.Services.GetRequiredService<StallInterventionStore>().All(),
            record => record.Action == AutoReviewStrandingAction.Escalate.ToString());
        Assert.True(factory.Services.GetRequiredService<AttemptAuthorityService>()
            .HasScheduledReviewInfrastructureRetry(failed));
    }

    [Fact]
    public async Task Scheduler_names_the_lane_on_the_card_when_it_drops_a_retry()
    {
        SeedTask(TaskStates.AutoReview, _time.GetUtcNow().UtcDateTime);
        using var factory = BuildFactory();
        var authority = factory.Services.GetRequiredService<AttemptAuthorityService>();
        var failed = await ReportExecutorRestartedAsync(factory);
        // The card leaves Auto Review inside the backoff, as the watchdog's
        // escalation did in the incident.
        var escalated = await factory.Services.GetRequiredService<HumanReviewEscalation>().EscalateAsync(
            TaskKey, _watchPath, ProjectName, HumanReviewEscalationCategories.Environmental,
            "Escalated inside the retry backoff.", expectedSourceState: TaskStates.AutoReview);
        Assert.Equal(MoveJobStatus.Success, escalated.Status);

        _time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(0, factory.Services.GetRequiredService<ReviewInfrastructureRetryScheduler>().RunOnce());

        Assert.False(authority.HasScheduledReviewInfrastructureRetry(failed));
        var card = factory.Services.GetRequiredService<TaskScannerService>().FindJob(TaskKey, _watchPath)!;
        Assert.Equal(TaskStates.Escalated, card.State);
        var dropped = Assert.Single(ReadTimelineAt(card.FolderPath),
            entry => entry.GetProperty("kind").GetString() == TimelineEventKinds.ReviewInfrastructureRetryDropped);
        var details = dropped.GetProperty("details");
        Assert.Equal(failed, details.GetProperty("attemptId").GetString());
        Assert.Equal("card-left-auto-review", details.GetProperty("reason").GetString());
        Assert.Equal(TaskStates.Escalated, details.GetProperty("lane").GetString());
        Assert.Contains("1/3 dropped", dropped.GetProperty("summary").GetString(), StringComparison.Ordinal);
    }

    /// <summary>Claims the seeded review and reports the review host's restart loss.</summary>
    private async Task<string> ReportExecutorRestartedAsync(WebApplicationFactory<Program> factory)
    {
        SeedReviewAttempt(factory.Services);
        using var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);
        (await http.PutAsJsonAsync($"/api/v1/runners/{RunnerId}",
            new Contract.RegisterRunnerRequest(RunnerId, "review-host", Instance, "1.0.0",
                Contract.TaskServerProtocol.Current, ReviewExecutorCapabilities))).EnsureSuccessStatusCode();
        var claimed = await http.PostAsJsonAsync($"/api/v1/runners/{RunnerId}/review-claims",
            new Contract.ReviewClaimRequest(RunnerId, Instance, 120));
        claimed.EnsureSuccessStatusCode();
        var claim = (await claimed.Content.ReadFromJsonAsync<Contract.ReviewClaimResponse>())!;
        Assert.Equal("claimed", claim.Status);

        var reported = await http.PostAsJsonAsync(
            $"/api/v1/reviews/attempts/{claim.Attempt!.AttemptId}/report",
            ExecutorRestartedReport(claim));
        reported.EnsureSuccessStatusCode();
        var report = (await reported.Content.ReadFromJsonAsync<Contract.ReviewReportDto>())!;
        Assert.True(report.RetryScheduled);
        Assert.Equal(TaskStates.AutoReview, report.TaskState);
        return claim.Attempt.AttemptId;
    }

    private static Contract.ReviewReportRequest ExecutorRestartedReport(Contract.ReviewClaimResponse claim)
        => new(
            RunnerId,
            Instance,
            claim.Lease!.LeaseId,
            claim.Lease.Fence,
            "review-executor-restarted",
            "ReviewInfra",
            "ExecutorRestarted",
            "The review host restarted; no persisted review process identity. "
            + "Lost work extent: 0 of 0 review commands completed.",
            new Contract.ReviewWorkspaceProofDto(
                claim.Subject!.RepositoryId,
                claim.Subject.ExpectedResultSha,
                claim.Subject.ExpectedResultSha,
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                false,
                false,
                new string('c', 64),
                claim.Lease.ResourceNamespace),
            new Contract.ReviewEnvironmentDto(
                "review-host",
                RunnerId,
                Instance,
                "linux",
                "x64",
                "10.0",
                new Dictionary<string, string>(),
                new Dictionary<string, string>()),
            [],
            [],
            [],
            claim.Lease.AuthorityEpoch);

    private void SeedReviewAttempt(IServiceProvider services)
    {
        var repositoryId = Contract.RepositoryIdentityContract.FromUrl(RepositoryUrl)!;
        var authority = services.GetRequiredService<AttemptAuthorityService>();
        var run = authority.AcquireRun(TaskKey, repositoryId, null, "coding-runner", "coding-host", 120,
            "seed-review-run").RunAttempt!;
        Assert.True(authority.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(run.AttemptId, run.LastFence, run.AuthorityEpoch,
                "seed-review-completion"),
            Outcome = "done",
            ResultSha = ResultSha,
            ResultEnvelope = new Contract.ImmutableResultEnvelope(
                repositoryId, run.AttemptId, "4136f00d4136f00d4136f00d4136f00d4136f00d", ResultSha,
                "refs/heads/agent-studio/results/stranded-retry", null, new string('a', 64),
                RepositoryUrl: RepositoryUrl),
        }).Accepted);
        Assert.True(authority.CreateReviewAttempt(new CreateReviewAttemptRequest(
            TaskKey, repositoryId, ResultSha, run.AttemptId, "requirements", "policy", [],
            "seed-review-attempt", RepositoryUrl: RepositoryUrl)).Accepted);
    }

    private void SeedTask(string state, DateTime enteredLaneAt)
    {
        var folder = Path.Combine(_watchPath, state, TaskKey);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = TaskKey,
            ["key"] = TaskKey,
            ["title"] = "Review host restarted",
            ["state"] = state,
            ["order"] = 1,
            ["agent"] = "claude",
            ["kind"] = TaskKinds.Task,
            ["enteredLaneAt"] = enteredLaneAt,
        }));
        File.WriteAllText(Path.Combine(folder, "prompt.md"), "Build and verify.");
        File.WriteAllText(Path.Combine(folder, "status.md"), "Result: pending.");
    }

    private List<JsonElement> ReadTimeline(string state)
        => ReadTimelineAt(Path.Combine(_watchPath, state, TaskKey));

    private static List<JsonElement> ReadTimelineAt(string folder)
    {
        var path = Path.Combine(folder, "logs", "timeline.jsonl");
        if (!File.Exists(path)) return [];
        return File.ReadAllLines(path)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToList();
    }

    private WebApplicationFactory<Program> BuildFactory() =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["TaskRepository"] = _workspace,
                        ["WatchPaths:0:Name"] = ProjectName,
                        ["WatchPaths:0:Path"] = _watchPath,
                        ["WatchPaths:0:RootPath"] = _watchPath,
                        ["WatchPaths:0:RepositoryPath"] = _watchPath,
                        ["ReviewDecisionOrchestrator:Enabled"] = "false",
                    }));
                builder.ConfigureTestServices(services =>
                {
                    // The fixture drives the watchdog and the scheduler tick by
                    // tick; no periodic worker may act between two ticks.
                    services.RemoveAll<IHostedService>();
                    services.AddSingleton<TimeProvider>(_time);
                    services.RemoveAll<AttemptAuthorityService>();
                    services.AddSingleton(provider => new AttemptAuthorityService(
                        provider.GetRequiredService<IConfiguration>(),
                        provider.GetRequiredService<ILogger<AttemptAuthorityService>>(),
                        () => _time.GetUtcNow().UtcDateTime,
                        provider.GetRequiredService<IAtomicJsonFileWriter>()));
                });
            });
}
