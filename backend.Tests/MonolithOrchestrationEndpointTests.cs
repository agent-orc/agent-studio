extern alias Runner;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Runner;
using AgentStudio.Security;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

using RClient = Runner::AgentRunner.TaskServerClient;
using RAcquire = Runner::AgentRunner.RunLeaseAcquireRequest;
using RRemoteComplete = Runner::AgentRunner.RemoteRunCompletionRequest;
using RTaskServerException = Runner::AgentRunner.TaskServerException;

public sealed class MonolithOrchestrationEndpointTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(),
        "monolith-orchestration-endpoints-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Operator_move_supersedes_open_review_occurrence_and_clears_claim_wait()
    {
        const string project = "agent-runner-01";
        const string card = "AGT-REVIEW-FIX-ROUND";
        var projectPath = Path.Combine(_workspace, "projects", project);
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(projectPath, state));
        var cardFolder = Path.Combine(projectPath, TaskStates.AutoReview, card);
        Directory.CreateDirectory(cardFolder);
        File.WriteAllText(Path.Combine(cardFolder, "task.json"), JsonSerializer.Serialize(new
        {
            id = card, title = "Review fix round", state = TaskStates.AutoReview,
            order = 1, agent = "codex", kind = TaskKinds.Task,
        }));
        File.WriteAllText(Path.Combine(cardFolder, "prompt.md"), "Fix review findings.");
        File.WriteAllText(Path.Combine(cardFolder, "status.md"), "Review result.");
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 21, 0, 45, 0, TimeSpan.Zero));
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["TaskRepository"] = _workspace,
                    ["WatchPaths:0:Name"] = project,
                    ["WatchPaths:0:Path"] = projectPath,
                    ["WatchPaths:0:RootPath"] = projectPath,
                    ["WatchPaths:0:RepositoryPath"] = projectPath,
                    ["DeliveryChain:Guarded"] = "false",
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
            });
        });
        using var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<MonolithOrchestrationStore>();
        var scanner = factory.Services.GetRequiredService<TaskScannerService>();
        var run = store.Create(project, card, OrchestrationStage.ReviewDecision,
            "{\"reviewOutcome\":\"Pass\"}", "review:fix-round", TaskStates.AutoReview,
            "review_attempt", 3, 4);
        EngineDecisionWaitMarker.Write(cardFolder, run.RunId);
        var claim = store.Claim(new("engine_one", "instance_one",
            [OrchestrationStage.ReviewDecision]));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.CompleteAsync(run.RunId,
            new("engine_one", "instance_one", claim.Lease!.LeaseId, claim.Lease.Fence,
                OrchestrationStage.ReviewDecision, OrchestrationAction.Continue,
                "{\"decision\":\"DegradeWithFollowUp\"}", "bad-review-receipt"), default));
        Assert.Equal("applying", store.Get(run.RunId)!.Status);
        scanner.InvalidateCache();
        Assert.True(scanner.FindJob(card, projectPath)!.WaitingForEngineDecision);

        var moved = await factory.Services.GetRequiredService<TaskTransitionService>()
            .MoveAsync(card, TaskStates.Ready, projectPath, cause: "human:operator",
                reason: "Operator requested a fix round.");

        Assert.Equal(MoveJobStatus.Success, moved.Status);
        Assert.Equal("cancelled", store.Get(run.RunId)!.Status);
        Assert.Equal("empty", store.Claim(new("engine_one", "instance_one",
            [OrchestrationStage.ReviewDecision])).Status);
        var ready = scanner.FindJob(card, projectPath)!;
        Assert.Equal(TaskStates.Ready, ready.State);
        Assert.False(ready.WaitingForEngineDecision);
        Assert.Null(EngineDecisionWaitMarker.Read(ready.FolderPath));
        var audit = Assert.Single(factory.Services.GetRequiredService<TimelineLog>()
            .ReadAll(ready.FolderPath), entry => entry.Kind == TimelineEventKinds.OrchestrationDecisionSuperseded);
        Assert.Equal(run.RunId, audit.Details!["orchestrationRunId"]);
        Assert.Equal(TaskStates.Ready, audit.Details["observedState"]);
        Assert.Equal("cancelled", new MonolithOrchestrationStore(
            Path.Combine(_workspace, ".orchestration-occurrences"), clock,
            factory.Services.GetRequiredService<IMonolithOrchestrationApplier>())
            .Get(run.RunId)!.Status);
    }

    [Fact]
    public async Task Scoped_engine_client_negotiates_claims_and_applies_a_review_decision_once()
    {
        var applier = new CountingApplier();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["TaskRepository"] = _workspace,
                    ["Security:Profile"] = "networked",
                    ["AllowedHosts"] = "studio.test",
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IMonolithOrchestrationApplier>();
                services.AddSingleton<IMonolithOrchestrationApplier>(applier);
            });
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://studio.test") });
        var security = factory.Services.GetRequiredService<AccessSecurityStore>();
        var enrollment = security.CreateEnrollment(new RunnerEnrollmentRequest("engine-one",
            [RunnerScopes.Orchestration], null, null));
        var engine = security.EnrollRunner(enrollment.Code);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", engine.Secret);
        client.DefaultRequestHeaders.Add("X-Client-Id", engine.Runner.Id);

        var protocolResponse = await client.PostAsJsonAsync("/api/v1/protocol/compatibility",
            new ProtocolCompatibilityRequest(TaskServerProtocol.EngineClientKind, "1.0.0",
                TaskServerProtocol.Current));
        protocolResponse.EnsureSuccessStatusCode();
        var protocol = await protocolResponse.Content.ReadFromJsonAsync<ProtocolCompatibilityResponse>();
        Assert.True(protocol!.Supported);
        Assert.Contains("orchestration-plane", protocol.Server.Capabilities!);

        var store = factory.Services.GetRequiredService<MonolithOrchestrationStore>();
        var run = store.Create("project", "card", OrchestrationStage.ReviewDecision,
            "{\"reviewOutcome\":\"Pass\"}", "review:one", TaskStates.AutoReview,
            "review_attempt", 4, 5);
        var fakeEngine = new FakeEngineClient(client, engine.Runner.Id);
        using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://studio.test") });
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous
            .PostAsJsonAsync("/api/v1/orchestration/claims",
                new OrchestrationClaimRequest(engine.Runner.Id, "instance", [OrchestrationStage.ReviewDecision]))).StatusCode);

        var claimed = await fakeEngine.Claim();
        Assert.Equal("claimed", claimed.Status);
        Assert.Equal(run.RunId, claimed.Run!.RunId);
        var completed = await fakeEngine.Complete(claimed);
        var replay = await fakeEngine.Complete(claimed);
        Assert.Equal("completed", completed.Status);
        Assert.Equal(completed.RunId, replay.RunId);
        Assert.Equal(completed.Status, replay.Status);
        Assert.Equal(Assert.Single(completed.StageResults!), Assert.Single(replay.StageResults!));
        Assert.Equal(1, applier.Count);

        var coding = store.Create("project", "second-card", OrchestrationStage.ReviewDecision,
            "{\"agentOutcome\":\"done\",\"targetState\":\"4-auto-review\"}",
            "coding:one", TaskStates.Progress, "coding_attempt", 8, 9);
        var codingClaim = await fakeEngine.Claim();
        Assert.Equal(coding.RunId, codingClaim.Run!.RunId);
        Assert.Equal("completed", (await fakeEngine.Complete(codingClaim)).Status);
        Assert.Equal(2, applier.Count);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Engine_switch_holds_coding_and_review_reports_until_each_decision_is_applied(
        bool acceptReview, bool rejectSettlement)
    {
        const string project = "agent-runner-01";
        const string card = "AGT-ENGINE-HANDOFF";
        const string runnerId = "coding-runner";
        const string reviewRunnerId = "review-runner-engine-test";
        const string reviewInstance = "review-host:4243";
        const string resultSha = "589c462f589c462f589c462f589c462f589c462f";
        const string baseSha = "4136f00d4136f00d4136f00d4136f00d4136f00d";
        var projectPath = Path.Combine(_workspace, "projects", project);
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(projectPath, state));
        var cardFolder = Path.Combine(projectPath, TaskStates.Progress, card);
        Directory.CreateDirectory(cardFolder);
        File.WriteAllText(Path.Combine(cardFolder, "task.json"), JsonSerializer.Serialize(new
        {
            id = card, title = "Engine handoff", state = TaskStates.Progress,
            order = 1, agent = "claude", kind = TaskKinds.Task,
        }));
        File.WriteAllText(Path.Combine(cardFolder, "prompt.md"), "Build and verify.");
        File.WriteAllText(Path.Combine(cardFolder, "status.md"), "Result: pending.");

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["TaskRepository"] = _workspace,
                    ["WatchPaths:0:Name"] = project,
                    ["WatchPaths:0:Path"] = projectPath,
                    ["WatchPaths:0:RootPath"] = projectPath,
                    ["WatchPaths:0:RepositoryPath"] = projectPath,
                    ["ReviewDecisionOrchestrator:Enabled"] = "false",
                    ["Runner:RunLiveness:Enabled"] = "false",
                    ["Runner:ReviewInfrastructureRetry:Enabled"] = "false",
                    ["Supervisor:StuckResumeWindowMinutes"] = "0",
                    ["DeliveryChain:Guarded"] = "false",
                    ["Orchestration:Transitions:CodingCompletion"] = "Engine",
                    ["Orchestration:Transitions:ReviewReport"] = "Engine",
                }));
        });
        using var http = factory.CreateClient();
        using var coding = new RClient(http, runnerId);
        await coding.RegisterAsync(project, "service", CancellationToken.None);
        var lease = await coding.AcquireLeaseAsync(
            new RAcquire(card, runnerId, project, "coding-host", 4242, "codex"), CancellationToken.None);
        Assert.True(lease.Granted);
        Assert.Equal(TaskStates.Progress, factory.Services.GetRequiredService<TaskScannerService>()
            .FindJob(card, projectPath)?.State);
        var completionRequest = new RRemoteComplete(
            card, lease.Lease!.LeaseId, lease.Lease.FencingToken, runnerId, "Done",
            ResultSha: resultSha, AttemptChainId: lease.Lease.LeaseId,
            Repository: "https://example.invalid/agent-studio.git",
            AttemptId: lease.Lease.AttemptId, AuthorityEpoch: lease.Lease.AuthorityEpoch,
            IdempotencyKey: "engine-coding-completion", BaseSha: baseSha,
            ImmutableResultRef: FencedGitRefs.ImmutableResult(
                lease.Lease.AttemptId!, lease.Lease.FencingToken, resultSha),
            ArtifactManifestDigest: new string('a', 64));
        var completion = await coding.CompleteRunAsync(completionRequest, CancellationToken.None);
        Assert.True(completion!.TargetState == TaskStates.Progress,
            $"Completion reached {completion.TargetState}: {completion.Message}");

        var store = factory.Services.GetRequiredService<MonolithOrchestrationStore>();
        var codingRun = Assert.Single(store.List(project, null));
        Assert.Equal("pending", codingRun.Status);
        Assert.Equal(codingRun.RunId, EngineDecisionWaitMarker.Read(cardFolder));
        var codingReplay = await coding.CompleteRunAsync(completionRequest, CancellationToken.None);
        Assert.Equal(completion!.TargetState, codingReplay!.TargetState);
        Assert.Equal(codingRun.RunId, Assert.Single(store.List(project, null)).RunId);

        var security = factory.Services.GetRequiredService<AccessSecurityStore>();
        var enrollment = security.CreateEnrollment(new RunnerEnrollmentRequest("engine-one",
            [RunnerScopes.Orchestration], null, null));
        var engine = security.EnrollRunner(enrollment.Code);
        using var engineHttp = factory.CreateClient();
        engineHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", engine.Secret);
        engineHttp.DefaultRequestHeaders.Add("X-Client-Id", engine.Runner.Id);
        var fakeEngine = new FakeEngineClient(engineHttp, engine.Runner.Id);
        var codingClaim = await fakeEngine.Claim();
        Assert.Equal(codingRun.RunId, codingClaim.Run!.RunId);
        Assert.Null(factory.Services.GetRequiredService<OrchestratorDecisionStore>()
            .GetInputs(codingRun.RunId));
        var liveConfiguration = factory.Services.GetRequiredService<IConfiguration>();
        liveConfiguration["Orchestration:DecisionModel:ActiveCodingCompletion"] = "true";
        Assert.Equal("completed", (await fakeEngine.Complete(codingClaim)).Status);
        Assert.True(Directory.Exists(Path.Combine(projectPath, TaskStates.AutoReview, card)));

        var reviewCapabilities = new[]
        {
            ReviewCapabilities.ReviewExecutor, ReviewCapabilities.BaselineComparison,
            ReviewCapabilities.DependencyPreparation, ReviewCapabilities.GitMaterialization,
            ReviewCapabilities.SemanticReview, ReviewCapabilities.LibraryStepV1,
            CapabilityProtocol.CliExecution(CliTypes.Codex),
            CapabilityProtocol.ProviderAuthentication(CliTypes.Codex),
        };
        (await http.PutAsJsonAsync($"/api/v1/runners/{reviewRunnerId}",
            new RegisterRunnerRequest(reviewRunnerId, "review-host", reviewInstance,
                "1.0.0", TaskServerProtocol.Current, reviewCapabilities))).EnsureSuccessStatusCode();
        using var review = new RClient(http, reviewRunnerId, usesDurableTaskServer: true);
        var reviewClaim = await review.ClaimReviewAsync(
            new ReviewClaimRequest(reviewRunnerId, reviewInstance, 120), CancellationToken.None);
        Assert.Equal("claimed", reviewClaim.Status);
        var reviewLease = reviewClaim.Lease!;
        var subject = reviewClaim.Subject!;
        var evidence = subject.Plan.Commands.Select(command => new ReviewCommandEvidenceDto(
            command.StepId, command.Aspect, command.FileName, command.Arguments,
            subject.ExpectedResultSha, subject.ExpectedResultSha, new string('c', 40),
            reviewLease.AcquiredAt, reviewLease.AcquiredAt, 0, null,
            new string('d', 64), new string('e', 64),
            ExecutionKind: command.ExecutionKind, ExecutorId: reviewLease.ExecutorId,
            HostId: reviewLease.HostId, AttemptId: reviewLease.AttemptId,
            LibraryStep: command.LibraryStep)).ToArray();
        var report = new ReviewReportRequest(reviewRunnerId, reviewInstance,
            reviewLease.LeaseId, reviewLease.Fence, "engine-review-report", "Pass", null,
            "Code quality found an actionable concern.",
            new ReviewWorkspaceProofDto(subject.RepositoryId, resultSha, resultSha,
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", false, false,
                new string('c', 64), reviewLease.ResourceNamespace),
            new ReviewEnvironmentDto("review-host", reviewRunnerId, reviewInstance,
                "linux", "x64", "10.0", new Dictionary<string, string>(),
                new Dictionary<string, string>()),
            evidence, [],
            [new ReviewVerdictDto("code-quality", "concerns", "RemoteAspectVerdict",
                "Dead assertion in backend.Tests/FooTests.cs.",
                "backend.Tests/FooTests.cs", "Remove the dead assertion.")],
            reviewLease.AuthorityEpoch);
        if (rejectSettlement)
        {
            var rejected = report with
            {
                IdempotencyKey = "engine-review-rejected",
                Workspace = report.Workspace with { ActualHead = new string('f', 40) },
            };
            await Assert.ThrowsAsync<RTaskServerException>(() => review.ReportReviewAsync(
                reviewClaim.Attempt!.AttemptId, rejected, CancellationToken.None));
            var cancelled = Assert.Single(store.List(project, "cancelled"));
            Assert.NotEqual(codingRun.RunId, cancelled.RunId);
            Assert.Null(EngineDecisionWaitMarker.Read(Path.Combine(projectPath, TaskStates.AutoReview, card)));
            Assert.Equal("empty", (await fakeEngine.Claim()).Status);
            return;
        }
        var reported = await review.ReportReviewAsync(reviewClaim.Attempt!.AttemptId,
            report, CancellationToken.None);
        Assert.Equal(TaskStates.AutoReview, reported.TaskState);
        Assert.Contains(ReviewRoundBudgetStore.Read(
                Path.Combine(projectPath, TaskStates.AutoReview, card)).Rounds,
            round => round.AttemptId == reviewClaim.Attempt.AttemptId);
        var pending = Assert.Single(store.List(project, "pending"));
        Assert.NotEqual(codingRun.RunId, pending.RunId);
        EngineDecisionWaitMarker.Clear(Path.Combine(projectPath, TaskStates.AutoReview, card), pending.RunId);
        factory.Services.GetServices<IHostedService>()
            .OfType<MonolithOrchestrationRecoveryService>().Single().RecoverOnce();
        Assert.Equal(pending.RunId, EngineDecisionWaitMarker.Read(
            Path.Combine(projectPath, TaskStates.AutoReview, card)));
        var duplicate = await review.ReportReviewAsync(reviewClaim.Attempt.AttemptId,
            report, CancellationToken.None);
        Assert.Equal(reported.ReportId, duplicate.ReportId);
        Assert.Equal(2, store.List(project, null).Count);

        var reviewDecision = TransitionDecisionPolicy.Create(
            acceptReview ? TransitionAction.HumanReview : TransitionAction.ConcernRound,
            "Fix the reviewed concern.", card, reviewClaim.Attempt.AttemptId, 0, 4,
            [new ReviewFollowUpFinding("code-quality", "concerns", "Remove the dead assertion.")],
            new ContinuationBase(subject.ResultRef, resultSha, reviewClaim.Attempt.AttemptId));
        var reviewStage = await fakeEngine.Claim();
        Assert.Equal(pending.RunId, reviewStage.Run!.RunId);
        Assert.Equal("completed", (await fakeEngine.Complete(reviewStage,
            acceptReview ? OrchestrationAction.Continue : OrchestrationAction.Reissue,
            JsonSerializer.Serialize(new { transition = reviewDecision }))).Status);
        var destination = Path.Combine(projectPath,
            acceptReview ? TaskStates.HumanReview : TaskStates.Ready, card);
        Assert.True(Directory.Exists(destination));
        Assert.Null(EngineDecisionWaitMarker.Read(destination));
        if (acceptReview)
        {
            Assert.Contains(ReviewDecisionLog.ReadAll(_workspace, project), record =>
                record.JobId == card && record.Kind == ReviewDecisionKind.AcceptAsDone
                && record.FailureKind == $"remote-review-park:{reviewClaim.Attempt.AttemptId}");
        }
        else
        {
            Assert.Contains("Remove the dead assertion", File.ReadAllText(Path.Combine(destination, "prompt.md")));
            var budget = CardRoundBudget.Evaluate(
                ReviewDecisionLog.ReadAll(_workspace, project),
                factory.Services.GetRequiredService<TimelineLog>().ReadAll(destination), card, 4);
            Assert.Equal(1, budget.Used);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Recovery_releases_unaccepted_review_preparation_without_waiting_marker(bool rejected)
    {
        const string project = "agent-runner-01";
        const string card = "AGT-UNACCEPTED-REPORT";
        var projectPath = Path.Combine(_workspace, "projects", project);
        var cardFolder = Path.Combine(projectPath, TaskStates.AutoReview, card);
        Directory.CreateDirectory(cardFolder);
        File.WriteAllText(Path.Combine(cardFolder, "task.json"), JsonSerializer.Serialize(new
        {
            id = card, title = "Unaccepted report", state = TaskStates.AutoReview,
            order = 1, agent = "codex", kind = TaskKinds.Task,
        }));
        File.WriteAllText(Path.Combine(cardFolder, "prompt.md"), "Review this card.");
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["TaskRepository"] = _workspace,
                    ["WatchPaths:0:Name"] = project,
                    ["WatchPaths:0:Path"] = projectPath,
                    ["WatchPaths:0:RootPath"] = projectPath,
                    ["WatchPaths:0:RepositoryPath"] = projectPath,
                    ["ReviewDecisionOrchestrator:Enabled"] = "false",
                    ["Orchestration:Transitions:ReviewReport"] = "Engine",
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
            });
        });
        using var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<MonolithOrchestrationStore>();
        var prepared = store.Create(project, card, OrchestrationStage.ReviewDecision,
            "{}", "review-report:missing_attempt:report_key", TaskStates.AutoReview,
            "missing_attempt", 3, 4, claimable: false);
        EngineDecisionWaitMarker.Write(cardFolder, prepared.RunId);
        if (rejected)
            store.CancelUnactivated(prepared.RunId, "review-settlement-rejected");
        clock.Advance(TimeSpan.FromSeconds(31));

        ActivatorUtilities.CreateInstance<MonolithOrchestrationRecoveryService>(factory.Services)
            .RecoverOnce();

        Assert.Equal("cancelled", store.Get(prepared.RunId)!.Status);
        Assert.Null(EngineDecisionWaitMarker.Read(cardFolder));
        Assert.Empty(store.WaitingOccurrences());
        Assert.False(factory.Services.GetRequiredService<TaskScannerService>()
            .FindJob(card, projectPath)!.WaitingForEngineDecision);
    }

    [Fact]
    public void Recovery_pass_without_marker_changes_does_not_force_a_task_index_rescan()
    {
        const string project = "agent-runner-01";
        const string card = "AGT-IDLE-RECOVERY";
        var projectPath = Path.Combine(_workspace, "projects", project);
        var cardFolder = Path.Combine(projectPath, TaskStates.Ready, card);
        Directory.CreateDirectory(cardFolder);
        File.WriteAllText(Path.Combine(cardFolder, "task.json"), JsonSerializer.Serialize(new
        {
            id = card, title = "Idle recovery", state = TaskStates.Ready,
            order = 1, agent = "codex", kind = TaskKinds.Task,
        }));
        File.WriteAllText(Path.Combine(cardFolder, "prompt.md"), "Nothing to recover.");
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero));
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["TaskRepository"] = _workspace,
                    ["WatchPaths:0:Name"] = project,
                    ["WatchPaths:0:Path"] = projectPath,
                    ["WatchPaths:0:RootPath"] = projectPath,
                    ["WatchPaths:0:RepositoryPath"] = projectPath,
                }));
            builder.ConfigureTestServices(services =>
            {
                // The test owns every recovery pass; no host loop runs beside it.
                services.RemoveAll<IHostedService>();
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
            });
        });
        using var client = factory.CreateClient();
        var scanner = factory.Services.GetRequiredService<TaskScannerService>();
        var index = factory.Services.GetRequiredService<TaskIndexCache>();
        var recovery = ActivatorUtilities.CreateInstance<MonolithOrchestrationRecoveryService>(factory.Services);
        Assert.NotNull(scanner.FindJob(card, projectPath));
        var scans = index.Misses;
        var invalidations = index.MutationInvalidations;

        recovery.RecoverOnce();
        recovery.RecoverOnce();

        // Neither pass wrote or removed a wait marker, so the published snapshot
        // stays current and the next freshness read needs no workspace walk.
        Assert.Equal(invalidations, index.MutationInvalidations);
        Assert.False(scanner.FindJob(card, projectPath)!.WaitingForEngineDecision);
        Assert.Equal(scans, index.Misses);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true);
    }

    private sealed class FakeEngineClient(HttpClient client, string engineId)
    {
        public async Task<OrchestrationClaimResponse> Claim()
        {
            var response = await client.PostAsJsonAsync("/api/v1/orchestration/claims",
                new OrchestrationClaimRequest(engineId, "instance", [OrchestrationStage.ReviewDecision]));
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<OrchestrationClaimResponse>())!;
        }

        public async Task<OrchestrationRunDto> Complete(OrchestrationClaimResponse claim,
            OrchestrationAction action = OrchestrationAction.Continue, string outputJson = "{}")
        {
            var response = await client.PostAsJsonAsync(
                $"/api/v1/orchestration/runs/{claim.Run!.RunId}/stages/complete",
                new CompleteOrchestrationStageRequest(engineId, "instance",
                    claim.Lease!.LeaseId, claim.Lease.Fence, OrchestrationStage.ReviewDecision,
                    action, outputJson, $"complete:{claim.Run.RunId}"));
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<OrchestrationRunDto>())!;
        }
    }

    private sealed class CountingApplier : IMonolithOrchestrationApplier
    {
        public int Count { get; private set; }
        public Task ApplyAsync(MonolithOrchestrationOccurrence occurrence,
            CompleteOrchestrationStageRequest completion, CancellationToken ct)
        {
            Count++;
            return Task.CompletedTask;
        }
    }
}
