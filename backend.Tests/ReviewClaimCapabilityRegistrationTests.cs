extern alias Runner;

using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Xunit;

using AgentStudio.Pipeline;
using Contract = AgentStudio.TaskServer.Contracts;
using RunnerOptions = Runner::AgentRunner.RunnerOptions;
using RunnerProbe = Runner::AgentRunner.RunnerCapabilityProbe;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2987: from Task Server 0.9.3 every ReviewAttempt carried a sealed
/// library-v1 plan whose dotnet and npm steps require <c>toolchain:*</c> keys,
/// while the review executor registered none of them (they lived only in the
/// minutely advertisement). The claim filter reads the registration, so 18
/// attempts sat unclaimable for 24 hours behind an HTTP 200 "nothing queued"
/// and a stagnation flag that any legacy dequeue reset. These tests pin the
/// registration, the typed empty answer, and the claim-side alarm.
/// </summary>
[Collection(WebApplicationFactorySerialCollection.Name)]
public sealed class ReviewClaimCapabilityRegistrationTests : IDisposable
{
    private const string ProjectName = "agent-runner-01";
    private const string RunnerId = "review-runner-capabilities";
    private const string Instance = "review-host:8484";

    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(), "atp-review-capabilities-" + Guid.NewGuid().ToString("N"));
    private readonly string _watchPath;

    public ReviewClaimCapabilityRegistrationTests()
    {
        _watchPath = Path.Combine(_workspace, "projects", ProjectName);
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    // ---- (a) registration carries what plans require -----------------------

    [Fact]
    public void Plan_requiring_dotnet_is_claimable_by_an_executor_whose_probe_found_dotnet()
    {
        var authority = NewAuthority();
        CreatePendingReviewAttempt(authority, "AGT-1", new string('1', 40), DotNetPlan());
        var registered = RunnerProbe
            .ReviewRegistrationCapabilities(ReviewOptions(), onPath: tool => tool == "dotnet")
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains(Contract.CapabilityProtocol.DotNet, registered);
        Assert.Empty(authority.ListUnclaimableReviews(registered));
        var claimed = authority.ClaimNextReview(RunnerId, "review-host", Instance, 60, registered);

        Assert.Equal(AttemptWriteStatus.Accepted, claimed.Status);
        Assert.Equal("AGT-1", claimed.ReviewAttempt!.TaskKey);
    }

    [Fact]
    public void Plan_requiring_dotnet_is_named_unclaimable_when_the_probe_did_not_find_dotnet()
    {
        var authority = NewAuthority();
        CreatePendingReviewAttempt(authority, "AGT-1", new string('1', 40), DotNetPlan());
        var registered = RunnerProbe
            .ReviewRegistrationCapabilities(ReviewOptions(), onPath: _ => false)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(
            AttemptWriteStatus.NotFound,
            authority.ClaimNextReview(RunnerId, "review-host", Instance, 60, registered).Status);
        var unclaimable = Assert.Single(authority.ListUnclaimableReviews(registered));
        Assert.Equal("AGT-1", unclaimable.TaskKey);
        Assert.Equal([Contract.CapabilityProtocol.DotNet], unclaimable.MissingCapabilities);
    }

    [Fact]
    public void Operator_required_capabilities_stay_an_additive_override()
    {
        var options = ReviewOptions(requiredCapabilities: ["toolchain:custom"]);

        var registered = RunnerProbe.ReviewRegistrationCapabilities(options, onPath: tool => tool == "node");

        Assert.Contains("toolchain:custom", registered);
        Assert.Contains(Contract.CapabilityProtocol.Node, registered);
        Assert.DoesNotContain(Contract.CapabilityProtocol.DotNet, registered);
    }

    // ---- (d) contract: registration is a superset of every emitted key ------

    [Fact]
    public void Registration_is_a_superset_of_every_toolchain_key_the_review_catalogue_can_require()
    {
        var repository = Path.Combine(_workspace, "mixed-repository");
        Write(repository, "agent-taskboard.sln", "Microsoft Visual Studio Solution File");
        Write(repository, "frontend/package.json",
            """{ "scripts": { "build": "ng build", "test": "ng test", "lint": "ng lint" } }""");
        var catalogue = V1ReviewPlaneEndpoints.FallbackPlan(repository, profile: null, integrationRef: "main");
        // Every invocation token the library recognises, so a key added to
        // ReviewLibraryStepPolicy.ToolchainRequirements is covered without
        // editing this test.
        var everyTool = Contract.ReviewLibraryStepPolicy.ToolchainRequirements
            .SelectMany(requirement => requirement.InvocationTools)
            .Select((tool, index) => new Contract.ReviewCommandDto(
                $"tool-{index}", "build-tests", "sh", ["-lc", $"{tool} --version"]))
            .ToArray();
        var render = new Contract.ReviewCommandDto(
            "compose-render", "build-tests", "sh",
            ["-lc", Contract.ComposeRenderGatePolicy.GuardedCommand(Contract.ComposeRenderGatePolicy.Scripts[0])]);
        var plan = Contract.ReviewLibraryStepPolicy.Seal(
            catalogue with { Commands = [.. catalogue.Commands, .. everyTool, render] },
            "sha-contract");
        var emitted = Contract.ReviewLibraryStepPolicy.RequiredCapabilities(plan)
            .Where(key => key.StartsWith("toolchain:", StringComparison.Ordinal))
            .ToArray();

        var registered = RunnerProbe.ReviewRegistrationCapabilities(
            ReviewOptions(), onPath: _ => true, composeRenderVersion: () => "2.40.3");

        Assert.Contains(Contract.CapabilityProtocol.DotNet, emitted);
        Assert.Contains(Contract.CapabilityProtocol.Node, emitted);
        Assert.Contains(Contract.CapabilityProtocol.Playwright, emitted);
        Assert.Contains(Contract.CapabilityProtocol.ComposeRender,
            Contract.ReviewLibraryStepPolicy.RequiredCapabilities(plan));
        Assert.Empty(emitted.Except(registered, StringComparer.Ordinal));
        Assert.True(Contract.ReviewLibraryStepPolicy.Supports(plan, registered.ToHashSet(StringComparer.Ordinal)));
    }

    // ---- (b) typed empty reason and one server log per attempt per hour ----

    [Fact]
    public async Task Executor_lacking_a_required_key_receives_the_typed_reason_and_the_server_logs_it_once()
    {
        SeedTask("AGT-7");
        var logs = new CapturingLoggerProvider();
        using var factory = BuildFactory(logs);
        using var http = factory.CreateClient();
        var registration = await http.PutAsJsonAsync(
            $"/api/v1/runners/{RunnerId}",
            new Contract.RegisterRunnerRequest(
                RunnerId, "review-host", Instance, "1.0.0", Contract.TaskServerProtocol.Current,
                // The 0.9.3 registration: everything but the toolchain keys.
                [.. RunnerProbe.ReviewRegistrationCapabilities(ReviewOptions(), onPath: _ => false)]));
        registration.EnsureSuccessStatusCode();
        var authority = factory.Services.GetRequiredService<AttemptAuthorityService>();
        CreatePendingReviewAttempt(authority, "AGT-7", new string('7', 40), DotNetPlan());

        var first = await ClaimAsync(http);
        var second = await ClaimAsync(http);

        Assert.Equal("empty", first.Status);
        Assert.Equal(Contract.ReviewClaimEmptyReasons.UnclaimablePlanRequirements, first.Reason);
        Assert.Equal([Contract.CapabilityProtocol.DotNet], first.MissingCapabilities);
        var attempt = Assert.Single(first.UnclaimableAttempts!);
        Assert.Equal("AGT-7", attempt.TaskKey);
        Assert.Contains(Contract.CapabilityProtocol.DotNet, first.Message!, StringComparison.Ordinal);
        Assert.Equal(first.Reason, second.Reason);
        var logged = Assert.Single(logs.Messages, message =>
            message.Contains("review-claim-unclaimable", StringComparison.Ordinal));
        Assert.Contains(attempt.AttemptId, logged, StringComparison.Ordinal);
        Assert.Contains(Contract.CapabilityProtocol.DotNet, logged, StringComparison.Ordinal);

        var queue = factory.Services.GetRequiredService<AutoReviewQueueStagnationWatchdog>().Refresh();
        Assert.Equal(attempt.AttemptId, queue.OldestPendingAttemptId);
        Assert.Equal(Contract.ReviewClaimEmptyReasons.UnclaimablePlanRequirements, queue.UnclaimableReason);
    }

    [Fact]
    public async Task An_empty_queue_answers_the_typed_queue_empty_reason()
    {
        using var factory = BuildFactory(new CapturingLoggerProvider());
        using var http = factory.CreateClient();
        var registration = await http.PutAsJsonAsync(
            $"/api/v1/runners/{RunnerId}",
            new Contract.RegisterRunnerRequest(
                RunnerId, "review-host", Instance, "1.0.0", Contract.TaskServerProtocol.Current,
                [.. RunnerProbe.ReviewRegistrationCapabilities(ReviewOptions(), onPath: _ => false)]));
        registration.EnsureSuccessStatusCode();

        var claim = await ClaimAsync(http);

        Assert.Equal("empty", claim.Status);
        Assert.Equal(Contract.ReviewClaimEmptyReasons.QueueEmpty, claim.Reason);
        Assert.Null(claim.MissingCapabilities);
    }

    [Fact]
    public void Unclaimable_log_repeats_an_attempt_only_after_an_hour()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 27, 15, 8, 0, TimeSpan.Zero));
        var logs = new CapturingLoggerProvider();
        var log = new ReviewClaimUnclaimableLog(logs.CreateLogger<ReviewClaimUnclaimableLog>(), time);
        Contract.ReviewUnclaimableAttemptDto[] attempts =
            [new("review-1", "AGT-1", time.GetUtcNow().UtcDateTime, [Contract.CapabilityProtocol.Node])];

        Assert.Equal(1, log.Record("reviewer", attempts));
        time.Advance(TimeSpan.FromMinutes(59));
        Assert.Equal(0, log.Record("reviewer", attempts));
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, log.Record("reviewer", attempts));
        Assert.Equal([Contract.CapabilityProtocol.Node], log.Latest("review-1")!.MissingCapabilities);
    }

    [Fact]
    public void Concurrent_unclaimable_records_log_once_per_attempt_per_interval()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 27, 15, 8, 0, TimeSpan.Zero));
        var logs = new CapturingLoggerProvider();
        var log = new ReviewClaimUnclaimableLog(logs.CreateLogger<ReviewClaimUnclaimableLog>(), time);
        Contract.ReviewUnclaimableAttemptDto[] attempts =
            [new("review-parallel", "AGT-1", time.GetUtcNow().UtcDateTime, [Contract.CapabilityProtocol.Node])];

        var recorded = 0;
        Parallel.For(0, 64, _ => Interlocked.Add(ref recorded, log.Record("reviewer", attempts)));
        Assert.Equal(1, recorded);
        Assert.Single(logs.Messages, message => message.Contains("review-claim-unclaimable", StringComparison.Ordinal));

        time.Advance(ReviewClaimUnclaimableLog.LogInterval);
        Parallel.For(0, 64, _ => Interlocked.Add(ref recorded, log.Record("reviewer", attempts)));
        Assert.Equal(2, recorded);
        Assert.Equal(2, logs.Messages.Count(message => message.Contains("review-claim-unclaimable", StringComparison.Ordinal)));
    }

    // ---- stagnation policy matrix -----------------------------------------

    public static TheoryData<int, int?, int?, int, bool> StagnationCases => new()
    {
        // pending, oldestCreatedMinutesAgo, lastClaimMinutesAgo, thresholdMinutes, stagnant
        { 0, null, null, 20, false },
        { 1, 25, null, 20, true },
        { 1, 19, null, 20, false },
        { 3, 600, 21, 20, true },
        { 3, 600, 5, 20, false },
        // A fresh attempt behind a long-idle lane gets the full threshold.
        { 1, 5, 600, 20, false },
    };

    [Theory]
    [MemberData(nameof(StagnationCases))]
    public void Review_claim_stagnation_policy_matrix(
        int pending, int? oldestMinutesAgo, int? lastClaimMinutesAgo, int thresholdMinutes, bool stagnant)
    {
        var now = new DateTime(2026, 9, 28, 14, 0, 0, DateTimeKind.Utc);
        var activity = new ReviewClaimActivity(
            pending,
            pending > 0 ? "review-1" : null,
            pending > 0 ? "AGT-1" : null,
            oldestMinutesAgo is { } oldest ? now.AddMinutes(-oldest) : null,
            lastClaimMinutesAgo is { } claim ? now.AddMinutes(-claim) : null);

        var verdict = ReviewClaimStagnationPolicy.Evaluate(activity, now, TimeSpan.FromMinutes(thresholdMinutes));

        Assert.Equal(stagnant, verdict.IsStagnant);
    }

    // ---- alarm names the oldest pending attempt and the reason ------------

    [Fact]
    public async Task Lane_drain_alarm_names_the_oldest_pending_attempt_and_its_unclaimable_reason()
    {
        var now = new DateTime(2026, 9, 28, 14, 0, 0, DateTimeKind.Utc);
        SeedTask("AGT-9");
        var authority = NewAuthority(() => now.AddHours(-2));
        CreatePendingReviewAttempt(authority, "AGT-9", new string('9', 40), DotNetPlan());
        var logs = new CapturingLoggerProvider();
        var unclaimable = new ReviewClaimUnclaimableLog(NullLogger<ReviewClaimUnclaimableLog>.Instance);
        unclaimable.Record(RunnerId, authority.ListUnclaimableReviews(new HashSet<string>(StringComparer.Ordinal)
        {
            Contract.ReviewCapabilities.LibraryStepV1,
            Contract.ReviewCapabilities.BaselineComparison,
        }));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = ProjectName,
            ["WatchPaths:0:Path"] = _watchPath,
            ["WatchPaths:0:RootPath"] = _watchPath,
        }).Build();
        var watchdog = new AutoReviewQueueStagnationWatchdog(
            new AutoReviewPostProcessingQueue(), authority, unclaimable, new AutoReviewStatusSnapshot(),
            configuration, NullLogger<AutoReviewQueueStagnationWatchdog>.Instance);
        var scanner = new TaskScannerService(
            configuration,
            NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, configuration));
        var orchestratorLog = new OrchestratorLog(NullLogger<OrchestratorLog>.Instance);
        var service = new PipelineHealthService(
            new PipelineHealthDetector(),
            scanner,
            new TimelineLog(NullLogger<TimelineLog>.Instance),
            orchestratorLog,
            logs.CreateLogger<PipelineHealthService>(),
            watchdog);

        await service.EvaluateAsync(now);

        var alarm = Assert.Single(logs.Messages, message =>
            message.Contains("pipeline_health_alarm kind=lane-drain-stalled", StringComparison.Ordinal)
            && message.Contains("review attempt", StringComparison.Ordinal));
        var attemptId = Assert.Single(authority.ListPendingReviewAttempts()).AttemptId;
        Assert.Contains(attemptId, alarm, StringComparison.Ordinal);
        Assert.Contains("unclaimable-plan-requirements", alarm, StringComparison.Ordinal);
        Assert.Contains(Contract.CapabilityProtocol.DotNet, alarm, StringComparison.Ordinal);
        var entry = Assert.Single(orchestratorLog.Read(_watchPath), item =>
            item.Summary.Contains("review attempt", StringComparison.Ordinal));
        Assert.Equal("AGT-9", entry.JobId);
        Assert.Equal("alarm", service.Snapshot(ProjectName, now)!.Status);
    }

    // ---- helpers -----------------------------------------------------------

    private static Contract.ReviewPlanDto DotNetPlan()
        => new(
            [new Contract.ReviewCommandDto("verify-1", "build-tests", "sh", ["-lc", "dotnet test"],
                TimeoutSeconds: 120, CompareToBaseline: true)],
            ["build-tests"],
            LibraryVersion: Contract.ReviewLibraryStepPolicy.Version);

    private static RunnerOptions ReviewOptions(IReadOnlyList<string>? requiredCapabilities = null) => new()
    {
        RequiredCapabilities = requiredCapabilities ?? [],
        ServerUrl = "http://127.0.0.1:5031",
        RunnerId = RunnerId,
        RunnerName = RunnerId,
        Hostname = "review-host",
        BackendName = "test",
        Role = "review",
        WorkDir = Path.GetTempPath(),
        BaseBranch = "main",
        CliBin = "sh",
        CliArgs = "",
    };

    private AttemptAuthorityService NewAuthority(Func<DateTime>? now = null)
    {
        Directory.CreateDirectory(_workspace);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _workspace,
        }).Build();
        return new AttemptAuthorityService(config, NullLogger<AttemptAuthorityService>.Instance, now);
    }

    private static void CreatePendingReviewAttempt(
        AttemptAuthorityService authority, string taskKey, string sha, Contract.ReviewPlanDto plan)
    {
        var run = authority.AcquireRun(
            taskKey, ProjectName, null, "coding-runner", "coding-host", 60, "acquire-" + taskKey).RunAttempt!;
        var envelope = new Contract.ImmutableResultEnvelope(
            run.RepositoryId, run.AttemptId, new string('0', 40), sha,
            "refs/agent-studio/results/" + run.AttemptId, null, new string('1', 64));
        authority.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(run.AttemptId, run.LastFence, run.AuthorityEpoch, "settle-" + taskKey),
            Outcome = "done",
            ResultSha = sha,
            ResultEnvelope = envelope,
            ResultEnvelopeDigest = Contract.ResultEnvelopeDigest.Compute(envelope),
        });
        var created = authority.CreateReviewAttempt(new CreateReviewAttemptRequest(
            taskKey, ProjectName, sha, run.AttemptId, "requirements-hash", "policy-hash", [],
            "review-create-" + taskKey, Plan: plan));
        Assert.True(created.Accepted, created.Message);
    }

    private static void Write(string root, string relativePath, string content)
    {
        var full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private void SeedTask(string taskKey)
    {
        var taskFolder = Path.Combine(_watchPath, TaskStates.AutoReview, taskKey);
        Directory.CreateDirectory(taskFolder);
        File.WriteAllText(
            Path.Combine(taskFolder, "task.json"),
            JsonSerializer.Serialize(new
            {
                id = taskKey,
                key = taskKey,
                title = "Unclaimable review",
                state = TaskStates.AutoReview,
                order = 1,
                agent = "codex",
                kind = TaskKinds.Task,
            }));
        File.WriteAllText(Path.Combine(taskFolder, "prompt.md"), "Review the delivery.");
        File.WriteAllText(Path.Combine(taskFolder, "status.md"), "Result: pending.");
    }

    private static async Task<Contract.ReviewClaimResponse> ClaimAsync(HttpClient http)
    {
        var response = await http.PostAsJsonAsync(
            $"/api/v1/runners/{RunnerId}/review-claims",
            new Contract.ReviewClaimRequest(RunnerId, Instance, 120, 1));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Contract.ReviewClaimResponse>())!;
    }

    private WebApplicationFactory<Program> BuildFactory(CapturingLoggerProvider logs) =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureLogging(logging => logging.AddProvider(logs));
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
            });

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages
        {
            get { lock (_messages) return _messages.ToArray(); }
        }

        public ILogger CreateLogger(string categoryName) => new Capturing(_messages);

        public ILogger<T> CreateLogger<T>() => new Capturing<T>(_messages);

        public void Dispose()
        {
        }

        private class Capturing(List<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel)) return;
                lock (messages) messages.Add(formatter(state, exception));
            }
        }

        private sealed class Capturing<T>(List<string> messages) : Capturing(messages), ILogger<T>;
    }
}
