extern alias Runner;
extern alias EngineAssembly;

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Bus;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Orchestrator;
using AgentStudio.Projects;
using AgentStudio.Runner;
using AgentStudio.Security;
using AgentStudio.Shared;
using AgentStudio.Tasks;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.Tokens;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

using RClient = Runner::AgentRunner.TaskServerClient;
using RAcquire = Runner::AgentRunner.RunLeaseAcquireRequest;
using RHeartbeat = Runner::AgentRunner.RunLeaseHeartbeatRequest;
using RRemoteComplete = Runner::AgentRunner.RemoteRunCompletionRequest;
using EngineOptions = EngineAssembly::AgentStudio.OrchestratorEngine.EngineOptions;
using IDecisionReceiptPlane = EngineAssembly::AgentStudio.OrchestratorEngine.IDecisionReceiptPlane;
using ShadowDecisionStep = EngineAssembly::AgentStudio.OrchestratorEngine.ShadowDecisionStep;
using ReviewDecisionOrchestratorLoop = EngineAssembly::AgentStudio.OrchestratorEngine.ReviewDecisionOrchestratorLoop;

/// <summary>
/// AGT-3063 acceptance: a shadow coding completion freezes typed grounds, a
/// runner host answers the claimable model job, the engine stores one
/// immutable receipt per occurrence, and the card exposes a one-click
/// evidence ref. Duplicate and cached usage never inflate spend.
/// </summary>
public sealed class OrchestratorDecisionReceiptTests : IDisposable
{
    private static readonly DateTime At = new(2026, 10, 15, 8, 0, 0, DateTimeKind.Utc);
    private readonly string _workspace = Path.Combine(Path.GetTempPath(),
        "orchestrator-decision-receipts-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Ask_history_failure_after_turn_starts_keeps_turn_identity_through_recovery()
    {
        const string project = "agent-runner-01";
        const string card = "AGT-AI-ASK";
        const string occurrence = "orch_ai_ask";
        var projectPath = Path.Combine(_workspace, "projects", project);
        var folder = Path.Combine(projectPath, TaskStates.Progress, card);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
        {
            id = card, title = "Held active decision", state = TaskStates.Progress,
            order = 1, agent = "codex", kind = TaskKinds.Task,
        }));
        File.WriteAllText(Path.Combine(folder, "prompt.md"), "Review the held decision.");
        File.WriteAllText(Path.Combine(folder, "status.md"), "Result: pending.");
        var clock = new FakeTimeProvider(new DateTimeOffset(At));
        var dispatcher = new RecordingTurnDispatcher { FailAfterDispatch = true };
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
                    ["ReviewDecisionOrchestrator:Enabled"] = "false",
                    ["Runner:RunLiveness:Enabled"] = "false",
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAiOperatorTurnDispatcher>();
                services.AddSingleton<IAiOperatorTurnDispatcher>(dispatcher);
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
            });
        });
        using var http = factory.CreateClient();
        var services = factory.Services;
        var registry = services.GetRequiredService<OrchestratorSessionRegistry>();
        dispatcher.Registry = registry;
        dispatcher.Time = clock;
        var task = services.GetRequiredService<TaskScannerService>().FindJob(card, projectPath)!;
        services.GetRequiredService<IAiOperatorDecisionQueue>().Enqueue(task, occurrence,
            new DecisionChoiceV1(DecisionActionKinds.Ask, "operator:ai", "history-write-failed"));

        var turnId = Assert.Single(dispatcher.TurnIds);
        var pendingPath = Path.Combine(_workspace, ".orchestration-ai-asks", occurrence + ".json");
        using (var pending = JsonDocument.Parse(File.ReadAllText(pendingPath)))
        {
            Assert.Equal(turnId, pending.RootElement.GetProperty("turnId").GetString());
            Assert.Equal("dispatch-pending", pending.RootElement.GetProperty("status").GetString());
        }

        // A fresh queue instance recovers while the dispatcher still knows the
        // running turn. Repeating the same id must keep that one turn.
        dispatcher.FailAfterDispatch = false;
        var recovered = new AiOperatorDecisionQueue(dispatcher, registry,
            services.GetRequiredService<IConfiguration>(),
            NullLogger<AiOperatorDecisionQueue>.Instance, clock,
            services.GetRequiredService<TimelineLog>(),
            services.GetRequiredService<OrchestratorLog>(),
            services.GetRequiredService<TaskScannerService>());
        recovered.RecoverPending();
        Assert.Single(dispatcher.TurnIds);
        Assert.Equal(new[] { turnId, turnId }, dispatcher.DispatchIds);
        using var afterRecovery = JsonDocument.Parse(File.ReadAllText(pendingPath));
        Assert.Equal(turnId, afterRecovery.RootElement.GetProperty("turnId").GetString());
    }

    [Theory]
    [InlineData(OrchestratorTurnService.StatusFailed)]
    [InlineData(OrchestratorTurnService.StatusParked)]
    public void Active_ai_operator_ask_retries_once_after_backoff_then_escalates_on_the_card(
        string terminalStatus)
    {
        const string project = "agent-runner-01";
        const string card = "AGT-AI-ASK";
        const string occurrence = "orch_ai_ask";
        var projectPath = Path.Combine(_workspace, "projects", project);
        var folder = Path.Combine(projectPath, TaskStates.Progress, card);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
        {
            id = card, title = "Held active decision", state = TaskStates.Progress,
            order = 1, agent = "codex", kind = TaskKinds.Task,
        }));
        File.WriteAllText(Path.Combine(folder, "prompt.md"), "Review the held decision.");
        File.WriteAllText(Path.Combine(folder, "status.md"), "Result: pending.");
        var clock = new FakeTimeProvider(new DateTimeOffset(At));
        var dispatcher = new RecordingTurnDispatcher();
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
                    ["ReviewDecisionOrchestrator:Enabled"] = "false",
                    ["Runner:RunLiveness:Enabled"] = "false",
                    ["Orchestration:Transitions:CodingCompletion"] = "Engine",
                    ["Orchestration:DecisionModel:ActiveCodingCompletion"] = "true",
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAiOperatorTurnDispatcher>();
                services.AddSingleton<IAiOperatorTurnDispatcher>(dispatcher);
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
            });
        });
        using var http = factory.CreateClient();
        var queue = factory.Services.GetRequiredService<IAiOperatorDecisionQueue>();
        var task = factory.Services.GetRequiredService<TaskScannerService>().FindJob(card, projectPath)!;
        var registry = factory.Services.GetRequiredService<OrchestratorSessionRegistry>();
        dispatcher.Registry = registry;
        dispatcher.Time = clock;
        queue.Enqueue(task, occurrence, new DecisionChoiceV1(DecisionActionKinds.Ask,
            "operator:ai", "rejected:decision-choice-mismatch"));
        Assert.Single(dispatcher.TurnIds);
        Assert.Contains("decision-choice-mismatch", dispatcher.Prompts[0]);
        var pendingPath = Path.Combine(_workspace, ".orchestration-ai-asks", occurrence + ".json");
        using var initial = JsonDocument.Parse(File.ReadAllText(pendingPath));
        var context = initial.RootElement.GetProperty("contextKey").GetString()!;

        void FailLatest(string error)
        {
            registry.AppendHistory(context, new OrchestratorSessionHistoryEntry(
                clock.GetUtcNow().UtcDateTime, "turn", dispatcher.TurnIds[^1],
                terminalStatus, $"Decision occurrence {occurrence} needs",
                null, null, null, error, null));
        }

        FailLatest("provider unavailable");
        Assert.True(OrchestratorContextKey.TryParse(context, out var contextKey));
        Assert.Contains("provider unavailable", File.ReadAllText(registry.HistoryFilePath(contextKey)));
        queue.RecoverPending();
        using (var pendingAfterFailure = JsonDocument.Parse(File.ReadAllText(pendingPath)))
            Assert.Equal("retry-pending", pendingAfterFailure.RootElement.GetProperty("status").GetString());
        clock.Advance(TimeSpan.FromSeconds(29));
        queue.RecoverPending();
        Assert.Single(dispatcher.TurnIds);
        clock.Advance(TimeSpan.FromSeconds(1));
        queue.RecoverPending();
        Assert.Equal(2, dispatcher.TurnIds.Count);
        FailLatest("provider still unavailable");
        queue.RecoverPending();
        queue.RecoverPending();
        Assert.Equal(2, dispatcher.TurnIds.Count);
        var eventRow = Assert.Single(factory.Services.GetRequiredService<TimelineLog>()
            .ReadAll(folder), item => item.RunId == occurrence);
        Assert.Contains("provider still unavailable", eventRow.Summary);
        Assert.Contains(factory.Services.GetRequiredService<OrchestratorLog>().Read(projectPath),
            item => item.JobId == card && item.Summary.Contains("provider still unavailable"));
        using var pending = JsonDocument.Parse(File.ReadAllText(pendingPath));
        Assert.Equal("escalated", pending.RootElement.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("prepare", false)]
    [InlineData("retry", false)]
    [InlineData("retry-no-envelope", false)]
    [InlineData("salvage", false)]
    [InlineData("salvage-move-failure", false)]
    [InlineData("invalid", false)]
    [InlineData("forbidden", false)]
    [InlineData("over-cap", false)]
    [InlineData("adapter-reject", false)]
    [InlineData("adapter-reject-dispatch-failure", false)]
    [InlineData("prepare", true)]
    [InlineData("salvage", true)]
    public async Task Active_coding_choice_dispatches_once_or_holds_on_invalid_output_and_stale_authority(
        string scenario, bool staleAuthority)
    {
        const string project = "agent-runner-01";
        const string card = "AGT-ACTIVE-DECISION";
        const string runnerId = "coding-runner";
        const string resultSha = "589c462f589c462f589c462f589c462f589c462f";
        var projectPath = Path.Combine(_workspace, "projects", project);
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(projectPath, state));
        var folder = Path.Combine(projectPath, TaskStates.Progress, card);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
        {
            id = card, title = "Active decision", state = TaskStates.Progress,
            order = 1, agent = "codex", kind = TaskKinds.Task,
        }));
        File.WriteAllText(Path.Combine(folder, "prompt.md"), "Build and verify.");
        File.WriteAllText(Path.Combine(folder, "status.md"), "Result: pending.");
        var operatorQueue = new RecordingAiOperatorQueue();
        var dispatcher = new RecordingTurnDispatcher
        {
            FailBeforeDispatch = scenario == "adapter-reject-dispatch-failure",
        };
        var serverClock = new FakeTimeProvider(new DateTimeOffset(At));
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
                    ["Orchestration:DecisionModel:ActiveCodingCompletion"] = "true",
                }));
            builder.ConfigureTestServices(services =>
            {
                if (scenario == "adapter-reject-dispatch-failure")
                {
                    services.RemoveAll<IAiOperatorTurnDispatcher>();
                    services.AddSingleton<IAiOperatorTurnDispatcher>(dispatcher);
                }
                else
                {
                    services.RemoveAll<IAiOperatorDecisionQueue>();
                    services.AddSingleton<IAiOperatorDecisionQueue>(operatorQueue);
                }
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(serverClock);
            });
        });
        using var http = factory.CreateClient();
        if (scenario == "adapter-reject-dispatch-failure")
        {
            dispatcher.Registry = factory.Services.GetRequiredService<OrchestratorSessionRegistry>();
            dispatcher.Time = serverClock;
        }
        using var coding = new RClient(http, runnerId);
        await coding.RegisterAsync(project, "service", CancellationToken.None);
        var lease = await coding.AcquireLeaseAsync(
            new RAcquire(card, runnerId, project, "coding-host", 4242, "codex"), CancellationToken.None);
        Assert.True(lease.Granted);
        var withoutEnvelope = scenario == "retry-no-envelope";
        var completion = await coding.CompleteRunAsync(new RRemoteComplete(
            card, lease.Lease!.LeaseId, lease.Lease.FencingToken, runnerId,
            "Done",
            ResultSha: resultSha, AttemptChainId: lease.Lease.LeaseId,
            Repository: "https://example.invalid/agent-studio.git",
            AttemptId: lease.Lease.AttemptId, AuthorityEpoch: lease.Lease.AuthorityEpoch,
            IdempotencyKey: "active-coding-completion", BaseSha: new string('b', 40),
            ImmutableResultRef: FencedGitRefs.ImmutableResult(
                lease.Lease.AttemptId!, lease.Lease.FencingToken, resultSha),
            ArtifactManifestDigest: withoutEnvelope ? null : new string('a', 64)), CancellationToken.None);
        Assert.Equal(TaskStates.Progress, completion!.TargetState);

        var orchestration = factory.Services.GetRequiredService<MonolithOrchestrationStore>();
        var occurrence = Assert.Single(orchestration.List(project, null));
        var frozen = factory.Services.GetRequiredService<OrchestratorDecisionStore>()
            .GetInputs(occurrence.RunId)!;
        Assert.Equal(DecisionReceiptPolicy.ActiveMode, frozen.Mode);
        Assert.Empty(factory.Services.GetRequiredService<OrchestratorDecisionStore>().PendingReceipts(10));
        var claim = orchestration.Claim(new("engine_test", "instance", [OrchestrationStage.ReviewDecision]));
        Assert.Equal("claimed", claim.Status);

        var security = factory.Services.GetRequiredService<AccessSecurityStore>();
        var engine = security.EnrollRunner(security.CreateEnrollment(new RunnerEnrollmentRequest(
            "engine-one", [RunnerScopes.Orchestration], null, null)).Code);
        using var engineHttp = factory.CreateClient();
        engineHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", engine.Secret);
        engineHttp.DefaultRequestHeaders.Add("X-Client-Id", engine.Runner.Id);
        var plane = new HttpDecisionPlane(engineHttp);
        var engineClock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var step = new ShadowDecisionStep(plane,
            new EngineOptions { ServerUrl = "http://127.0.0.1", ClientId = engine.Runner.Id, PollSeconds = 1 },
            engineClock);
        var deciding = new ReviewDecisionOrchestratorLoop(step, plane)
            .ExecuteAsync(claim.Run!, CancellationToken.None);
        var modelJob = await ClaimAsync(coding, runnerId);
        var response = scenario switch
        {
            "invalid" => "{}",
            "forbidden" => """{"chosenActions":[{"action":"halt","target":"card:park"}],"confidence":0.8,"reasoning":"Park it.","citedFacts":["run.outcome"]}""",
            "salvage" => """{"chosenActions":[{"action":"adapt","target":"salvage-handoff"}],"confidence":0.8,"reasoning":"Continue on the immutable result.","citedFacts":["result.sha"]}""",
            "salvage-move-failure" => """{"chosenActions":[{"action":"adapt","target":"salvage-handoff"}],"confidence":0.8,"reasoning":"Continue on the immutable result.","citedFacts":["result.sha"]}""",
            "retry" or "retry-no-envelope" => """{"chosenActions":[{"action":"retry","target":"card:ready"}],"confidence":0.8,"reasoning":"Try another round.","citedFacts":["run.outcome"]}""",
            _ => """{"chosenActions":[{"action":"prepare","target":"review-subject"}],"confidence":0.8,"reasoning":"The result is settled.","citedFacts":["result.envelope"]}""",
        };
        Assert.True(await coding.CompleteOrchestratorDecisionJobAsync(new OrchestratorDecisionJobCompletion(
            modelJob.JobId, modelJob.ClaimToken, runnerId, true, response, "gpt-6-sol",
            [new("active-call", "gpt-6-sol", 500, 0, 0,
                scenario == "over-cap" ? 1_200 : 100, false)], At, At.AddSeconds(2),
            null, false), CancellationToken.None));
        for (var tick = 0; tick < 200 && !deciding.IsCompleted; tick++)
        {
            engineClock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(25);
        }
        var decision = await deciding;
        var receipt = factory.Services.GetRequiredService<OrchestratorDecisionStore>()
            .GetReceipt(DecisionReceiptPolicy.ReceiptId(occurrence.RunId,
                DecisionGroundsPolicy.Freeze(frozen.Grounds).ContentDigest))!;
        Assert.Equal(DecisionReceiptPolicy.ActiveMode, receipt.Mode);
        Assert.Equal(1, receipt.Call.Attempts);
        Assert.True(receipt.Call.Tokens.Total > 0);
        if (scenario is "invalid" or "forbidden" or "over-cap")
        {
            Assert.Equal(scenario == "over-cap" ? DecisionCallStatuses.Unavailable
                : DecisionCallStatuses.InvalidResponse, receipt.Call.Status);
            Assert.Equal("operator:ai", receipt.Chosen.Last().Target);
            Assert.Equal(DecisionReceiptPolicy.ActiveHold, receipt.DispatchOutcome);
        }
        if (scenario == "over-cap")
        {
            Assert.True(receipt.Call.CallTokenCapExceeded);
            Assert.Single(receipt.Chosen);
            Assert.Equal(DecisionActionKinds.Ask, receipt.Chosen[0].Action);
        }
        if (staleAuthority)
            factory.Services.GetRequiredService<AttemptAuthorityService>()
                .RotateAuthorityEpoch("test stale model decision");
        if (scenario == "salvage-move-failure")
            File.WriteAllText(Path.Combine(projectPath, TaskStates.Ready, card), "blocked");
        var outputJson = scenario is "adapter-reject" or "adapter-reject-dispatch-failure"
            ? JsonSerializer.Serialize(new
            {
                activeChoice = new DecisionChoiceV1(DecisionActionKinds.Retry,
                    "card:park", "submitted:authority-adapter"),
                receiptId = receipt.ReceiptId,
                groundsDigest = receipt.GroundsDigest,
            }) : decision.OutputJson;
        var request = new CompleteOrchestrationStageRequest("engine_test", "instance",
            claim.Lease!.LeaseId, claim.Lease.Fence, OrchestrationStage.ReviewDecision,
            decision.Action, outputJson, "active-decision-once");
        if (scenario == "salvage-move-failure")
            await Assert.ThrowsAsync<InvalidOperationException>(() => orchestration.CompleteAsync(
                occurrence.RunId, request, CancellationToken.None));
        else
        {
            Assert.Equal("completed", (await orchestration.CompleteAsync(
                occurrence.RunId, request, CancellationToken.None)).Status);
            Assert.Equal("completed", (await orchestration.CompleteAsync(
                occurrence.RunId, request, CancellationToken.None)).Status);
        }
        var finalState = factory.Services.GetRequiredService<TaskScannerService>()
            .FindJob(card, projectPath)?.State;
        Assert.Equal(!staleAuthority && scenario == "prepare" ? TaskStates.AutoReview
            : !staleAuthority && scenario is "salvage" or "retry" or "retry-no-envelope" ? TaskStates.Ready : TaskStates.Progress,
            finalState);
        if (withoutEnvelope)
            Assert.Null(factory.Services.GetRequiredService<AttemptAuthorityService>()
                .GetRun(lease.Lease.AttemptId!)?.ResultEnvelope);
        if (scenario == "salvage" && !staleAuthority)
        {
            var continuation = ContinuationBaseStore.Read(Path.Combine(projectPath,
                TaskStates.Ready, card));
            Assert.Equal(resultSha, continuation?.CommitSha);
            Assert.Equal(At, continuation?.SavedAtUtc);
        }
        if (scenario == "salvage" && staleAuthority)
            Assert.Null(ContinuationBaseStore.Read(Path.Combine(projectPath, TaskStates.Progress, card)));
        if (scenario == "salvage-move-failure")
            Assert.Null(ContinuationBaseStore.Read(Path.Combine(projectPath, TaskStates.Progress, card)));
        if (scenario == "adapter-reject-dispatch-failure")
        {
            Assert.Equal("completed", orchestration.Get(occurrence.RunId)?.Status);
            var pendingPath = Path.Combine(_workspace, ".orchestration-ai-asks",
                occurrence.RunId + ".json");
            using var pending = JsonDocument.Parse(File.ReadAllText(pendingPath));
            Assert.Equal("dispatch-pending", pending.RootElement.GetProperty("status").GetString());
            Assert.Equal("rejected:decision-choice-mismatch",
                pending.RootElement.GetProperty("reason").GetString());
            Assert.Empty(dispatcher.TurnIds);
            dispatcher.FailBeforeDispatch = false;
            factory.Services.GetRequiredService<IAiOperatorDecisionQueue>().RecoverPending();
            Assert.Single(dispatcher.TurnIds);
        }
        if (scenario is "invalid" or "forbidden" or "over-cap" or "adapter-reject" || staleAuthority)
        {
            Assert.Equal(1, operatorQueue.Count);
            if (scenario == "adapter-reject")
                Assert.Equal("rejected:decision-choice-mismatch", operatorQueue.LastChoice?.Outcome);
            if (staleAuthority)
                Assert.Equal("rejected:decision-stale-authority", operatorQueue.LastChoice?.Outcome);
            Assert.Equal(occurrence.RunId,
                EngineDecisionWaitMarker.Read(Path.Combine(projectPath, TaskStates.Progress, card)));
        }
        if (scenario is "adapter-reject" or "adapter-reject-dispatch-failure" || staleAuthority)
            Assert.Equal("completed", orchestration.Get(occurrence.RunId)?.Status);
    }

    [Fact]
    public async Task Scoped_operator_reads_and_answers_only_receipts_in_its_project()
    {
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
        });
        using var http = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://studio.test"), HandleCookies = false,
        });
        var decisions = factory.Services.GetRequiredService<OrchestratorDecisionStore>();
        OrchestratorDecisionReceiptV1 Add(string occurrence, string project)
        {
            var input = Shadow(occurrence);
            input = input with { Grounds = input.Grounds with
            {
                Scope = input.Grounds.Scope with { ProjectId = project },
            } };
            decisions.FreezeInputs(input);
            return decisions.PutReceipt(DecisionReceiptPolicy.Build(new(
                DecisionGroundsPolicy.Freeze(input.Grounds),
                DecisionGroundsPolicyTests.Deterministic, "settled", null, 0, false,
                "decision unavailable")));
        }
        var own = Add("orch_scoped_own", "project-a");
        var other = Add("orch_scoped_other", "project-b");
        var security = factory.Services.GetRequiredService<AccessSecurityStore>();
        security.Bootstrap(new BootstrapRequest("owner", "correct horse battery staple!", null));
        var created = security.CreateUser(new CreateUserRequest("scoped.operator", "Scoped Operator",
            StudioRoles.Operator, ["project-a"], "temporary pass phrase long enough"));
        var login = security.Login(created.User.Username, created.TemporaryPassword, "scoped|127.0.0.1");
        security.ChangePassword(new HumanPrincipal(login.User,
            security.AuthenticateSession(login.SessionToken)!.Session),
            new ChangePasswordRequest(created.TemporaryPassword, "permanent pass phrase long enough"));
        http.DefaultRequestHeaders.Add("Cookie",
            $"{AccessSecurityStore.SessionCookieName}={login.SessionToken}");
        http.DefaultRequestHeaders.Add("X-CSRF-Token", login.CsrfToken);
        http.DefaultRequestHeaders.Add("Sec-Fetch-Site", "same-origin");

        var visible = await http.GetFromJsonAsync<OrchestratorDecisionReceiptSummary[]>(
            "/api/orchestration/decision-receipts?limit=20");
        Assert.Equal(own.ReceiptId, Assert.Single(visible!).ReceiptId);
        var spend = await http.GetFromJsonAsync<OrchestratorDecisionSpendSummary>(
            "/api/orchestration/decision-receipts/spend");
        Assert.Equal(1, spend!.Occurrences);
        foreach (var path in new[] { other.EvidenceRef, $"{other.EvidenceRef}/outcome",
                     $"{other.EvidenceRef}/operator-intent" })
            Assert.Equal(System.Net.HttpStatusCode.NotFound, (await http.GetAsync(path)).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NotFound,
            (await http.PostAsJsonAsync($"{other.EvidenceRef}/operator-intent",
                new DecisionOperatorIntentRequest("stop", "Wrong project"))).StatusCode);
        Assert.Null(decisions.GetOperatorIntent(other.ReceiptId));
        var answer = await http.PostAsJsonAsync($"{own.EvidenceRef}/operator-intent",
            new DecisionOperatorIntentRequest("stop", "Stop this card"));
        Assert.Equal(System.Net.HttpStatusCode.Conflict, answer.StatusCode);
        Assert.Contains("stop-not-available", await answer.Content.ReadAsStringAsync());
        Assert.Null(decisions.GetOperatorIntent(own.ReceiptId));
        Assert.DoesNotContain($"operator-response:{own.ReceiptId}", decisions.PendingReceipts(10));
    }

    [Fact]
    public async Task Shadow_coding_completion_records_one_immutable_receipt_with_a_one_click_evidence_ref()
    {
        const string project = "agent-runner-01";
        const string card = "AGT-SHADOW-DECISION";
        const string runnerId = "coding-runner";
        const string resultSha = "589c462f589c462f589c462f589c462f589c462f";
        const string baseSha = "4136f00d4136f00d4136f00d4136f00d4136f00d";
        var projectPath = Path.Combine(_workspace, "projects", project);
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(projectPath, state));
        var cardFolder = Path.Combine(projectPath, TaskStates.Progress, card);
        Directory.CreateDirectory(cardFolder);
        // A legacy raw Codex row (cached input inside input_tokens) recorded
        // twice under two attempts: one provider call, priced once on 10k
        // uncached plus 90k cached input.
        var usage = new
        {
            ts = At, model = "gpt-5.5", participantId = "agent:remote-runner:earlier:usage:call-1",
            inputTokens = 100_000, outputTokens = 1_000, cacheReadTokens = 90_000, cacheCreationTokens = 0,
        };
        File.WriteAllText(Path.Combine(cardFolder, "task.json"), JsonSerializer.Serialize(new
        {
            id = card, title = "Shadow decision", state = TaskStates.Progress,
            order = 1, agent = "codex", kind = TaskKinds.Task,
            tokenSummary = new
            {
                calls = 2,
                entries = new object[]
                {
                    usage,
                    usage with { participantId = "agent:remote-runner:later:usage:call-1" },
                },
            },
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
                    ["Orchestration:Transitions:CodingCompletion"] = "Shadow",
                }));
        });
        using var http = factory.CreateClient();
        using var coding = new RClient(http, runnerId);
        await coding.RegisterAsync(project, "service", CancellationToken.None);
        var lease = await coding.AcquireLeaseAsync(
            new RAcquire(card, runnerId, project, "coding-host", 4242, "codex"), CancellationToken.None);
        Assert.True(lease.Granted);
        var completionRequest = new RRemoteComplete(
            card, lease.Lease!.LeaseId, lease.Lease.FencingToken, runnerId, "Done",
            ResultSha: resultSha, AttemptChainId: lease.Lease.LeaseId,
            Repository: "https://example.invalid/agent-studio.git",
            AttemptId: lease.Lease.AttemptId, AuthorityEpoch: lease.Lease.AuthorityEpoch,
            IdempotencyKey: "shadow-coding-completion", BaseSha: baseSha,
            ImmutableResultRef: FencedGitRefs.ImmutableResult(
                lease.Lease.AttemptId!, lease.Lease.FencingToken, resultSha),
            ArtifactManifestDigest: new string('a', 64));
        var completion = await coding.CompleteRunAsync(completionRequest, CancellationToken.None);
        // Shadow mode: the monolith applies its own decision; the model step changes no dispatch.
        Assert.Equal(TaskStates.AutoReview, completion!.TargetState);
        await coding.CompleteRunAsync(completionRequest, CancellationToken.None);

        var orchestration = factory.Services.GetRequiredService<MonolithOrchestrationStore>();
        var occurrence = Assert.Single(orchestration.List(project, null));
        var decisions = factory.Services.GetRequiredService<OrchestratorDecisionStore>();
        var frozen = decisions.GetInputs(occurrence.RunId)!;
        Assert.Equal([occurrence.RunId], decisions.PendingReceipts(10));
        Assert.Equal((DecisionActionKinds.Prepare, "review-subject"),
            (frozen.DeterministicAction, frozen.DeterministicTarget));
        var ledger = frozen.Grounds.RunLedger;
        Assert.Equal((2, 1, 1), (ledger.RawRows, ledger.CountedRows, ledger.ExcludedDuplicateRows));
        Assert.Equal(new DecisionTokenUsageV1(10_000, 90_000, 0, 1_000), ledger.Tokens);
        Assert.Contains(frozen.Grounds.Facts, fact => fact.Id == "result.sha" && fact.Value == resultSha);
        Assert.DoesNotContain(frozen.Grounds.Facts, fact => fact.Value.Contains("CreateReview"));

        var security = factory.Services.GetRequiredService<AccessSecurityStore>();
        var engine = security.EnrollRunner(security.CreateEnrollment(new RunnerEnrollmentRequest(
            "engine-one", [RunnerScopes.Orchestration], null, null)).Code);
        using var engineHttp = factory.CreateClient();
        engineHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", engine.Secret);
        engineHttp.DefaultRequestHeaders.Add("X-Client-Id", engine.Runner.Id);
        var plane = new HttpDecisionPlane(engineHttp);
        Assert.Equal([occurrence.RunId], await plane.GetPendingDecisionGroundsAsync(10, CancellationToken.None));
        // The engine clock stays before the job deadline and moves only when the test advances it.
        var engineClock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var step = new ShadowDecisionStep(plane,
            new EngineOptions { ServerUrl = "http://127.0.0.1", ClientId = engine.Runner.Id, PollSeconds = 1 },
            engineClock);
        var recording = step.RecordAsync(occurrence.RunId, CancellationToken.None);

        var claim = await ClaimAsync(coding, runnerId);
        Assert.Equal(DecisionDefaultsCli, claim.Cli);
        Assert.Contains(frozen.Grounds.Facts[0].Id, claim.Prompt);
        Assert.DoesNotContain("CreateReview", claim.Prompt);
        var frame = new DecisionUsageFrameV1("codex:thread-1", "gpt-6-sol", 6_000, 1_000, 0, 300, false);
        Assert.True(await coding.CompleteOrchestratorDecisionJobAsync(new OrchestratorDecisionJobCompletion(
            claim.JobId, claim.ClaimToken, runnerId, true,
            """{"chosenActions":[{"action":"prepare","target":"review-subject"}],"confidence":0.82,"reasoning":"The attempt settled an immutable envelope.","citedFacts":["result.envelope","run.outcome"],"prediction":"Review passes in one round."}""",
            "gpt-6-sol", [frame, frame], At, At.AddSeconds(12), null, false), CancellationToken.None));
        Assert.False(await coding.CompleteOrchestratorDecisionJobAsync(new OrchestratorDecisionJobCompletion(
            claim.JobId, "stale-token", runnerId, true, "{}", null, [], At, At, null, false),
            CancellationToken.None));

        for (var tick = 0; tick < 200 && !recording.IsCompleted; tick++)
        {
            engineClock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(25);
        }
        var receipt = (await recording)!;
        Assert.Equal(DecisionCallStatuses.Completed, receipt.Call.Status);
        Assert.True(receipt.MatchesDeterministic);
        Assert.Equal(DecisionReceiptPolicy.ShadowDispatch, receipt.DispatchOutcome);
        Assert.Equal(DecisionModelCustody.RunnerHostJob, receipt.Call.Custody);
        Assert.Equal(runnerId, receipt.Call.HostId);
        Assert.Equal(new DecisionTokenUsageV1(6_000, 1_000, 0, 300), receipt.Call.Tokens);
        Assert.Equal(1, receipt.Call.ExcludedDuplicateFrames);
        Assert.Equal(["codex:thread-1"], receipt.Call.ProviderCallIds);
        Assert.Equal(receipt.Call.Price.Status == DecisionPriceStatuses.Priced,
            receipt.Call.Price.EstimatedUsd is not null);
        Assert.Equal(TaskStates.AutoReview, factory.Services.GetRequiredService<TaskScannerService>()
            .FindJob(card, projectPath)?.State);

        // Replay of the same occurrence, including a degraded late writer, keeps the first receipt.
        var replay = await step.RecordAsync(occurrence.RunId, CancellationToken.None);
        var degraded = DecisionReceiptPolicy.Build(new(DecisionGroundsPolicy.Freeze(frozen.Grounds),
            new(frozen.DeterministicAction, frozen.DeterministicTarget, "applied:deterministic"),
            frozen.DeterministicReason, null, 0, false, "late writer"));
        var late = await plane.PutDecisionReceiptAsync(degraded, CancellationToken.None);
        Assert.Equal(JsonSerializer.Serialize(receipt), JsonSerializer.Serialize(replay));
        Assert.Equal(JsonSerializer.Serialize(receipt), JsonSerializer.Serialize(late));
        Assert.Single(Directory.GetFiles(Path.Combine(_workspace, ".orchestrator-decisions", "receipts")));
        Assert.Empty(decisions.PendingReceipts(10));

        // The card's receipt row links in one request to the full evidence.
        var rows = await http.GetFromJsonAsync<OrchestratorDecisionReceiptSummary[]>(
            $"/api/orchestration/decision-receipts?taskId={card}", DecisionGroundsPolicy.Json);
        var row = Assert.Single(rows!);
        Assert.Equal("prepare(review-subject)", row.ChosenAction);
        Assert.Equal(receipt.Scope.ProjectId, row.ProjectId);
        Assert.Equal("decision-step", row.Actor);
        Assert.Equal(receipt.Call.Tokens.Total, row.TotalTokens);
        Assert.Equal(receipt.Call.Tokens.UncachedInput + receipt.Call.Tokens.CachedInput
            + receipt.Call.Tokens.CacheWrite, row.InputTokens);
        Assert.Equal(receipt.Call.Tokens.Output, row.OutputTokens);
        Assert.Equal(receipt.Call.Price.EstimatedUsd, row.EstimatedUsd);
        var cardSpend = await http.GetFromJsonAsync<OrchestratorDecisionSpendSummary>(
            $"/api/orchestration/decision-receipts/spend?taskId={card}", DecisionGroundsPolicy.Json);
        Assert.Equal(1, cardSpend!.Occurrences);
        Assert.Equal(row.InputTokens, cardSpend.InputTokens);
        Assert.Equal(row.OutputTokens, cardSpend.OutputTokens);
        Assert.Equal(row.EstimatedUsd ?? 0, cardSpend.PricedUsd);
        Assert.Equal(row.UnknownPriceCalls, cardSpend.UnknownPriceCalls);
        var projectSpend = await http.GetFromJsonAsync<OrchestratorDecisionSpendSummary>(
            $"/api/orchestration/decision-receipts/spend?projectId={row.ProjectId}", DecisionGroundsPolicy.Json);
        Assert.Equal(cardSpend, projectSpend! with { Scope = "card", ScopeId = card });
        var busDecision = Assert.Single(new AgentMessageBusStore().Query(
            _workspace, project, new AgentMessageQuery(Kind: "decision",
                CorrelationId: receipt.OccurrenceId)));
        Assert.Equal("engine-decision", busDecision.Topic);
        Assert.Equal(receipt.OccurrenceId, busDecision.CorrelationId);
        Assert.Equal(receipt.Call.Model, busDecision.Tokens?.Model);
        Assert.Equal(receipt.Call.Tokens.Output, busDecision.Tokens?.Output);
        var evidence = await http.GetFromJsonAsync<OrchestratorDecisionReceiptV1>(row.EvidenceRef,
            DecisionGroundsPolicy.Json);
        Assert.Equal(receipt.ReceiptId, evidence!.ReceiptId);
        Assert.True(DecisionGroundsPolicy.IsIntact(evidence.Grounds));
        Assert.Equal(1, evidence.Grounds.RunLedger.ExcludedDuplicateRows);
        Assert.Contains("prepare", evidence.ResponseContent);
        using var outcome = JsonDocument.Parse(await http.GetStringAsync(evidence.OutcomeLink));
        Assert.Equal(nameof(TransitionAction.CreateReview), outcome.RootElement.GetProperty("appliedAction").GetString());
    }

    [Fact]
    public void Store_caps_card_calls_prices_unknown_models_explicitly_and_supersedes_open_jobs()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(At));
        var directory = Path.Combine(_workspace, "store");
        var store = new OrchestratorDecisionStore(directory, clock, new RatePricing());
        var input = Shadow("orch_cap");
        store.FreezeInputs(input);
        Assert.Same(store.GetInputs("orch_cap"), store.FreezeInputs(input with { DeterministicReason = "changed" }));

        var job = store.CreateJob(Request("orch_cap", 1, "mystery-model"));
        Assert.Equal(job, store.CreateJob(Request("orch_cap", 1, "mystery-model")));
        Assert.Throws<InvalidOperationException>(() => store.CreateJob(Request("orch_cap", 1, "other-model")));
        var claim = store.Claim(new("runner-one", "runner")).Work!;
        Assert.Equal(DecisionJobClaimStatuses.Empty, store.Claim(new("runner-two", "runner")).Status);
        Assert.False(store.Complete(Completion(claim, "runner-two")));
        Assert.True(store.Complete(Completion(claim, "runner-one")));
        Assert.True(store.Complete(Completion(claim, "runner-one")));
        var priced = store.GetJob(job.JobId)!.Result!;
        Assert.Equal(new DecisionPriceV1(DecisionPriceStatuses.UnknownModel, null), priced.Price);
        Assert.Equal(new DecisionTokenUsageV1(500, 1_500, 0, 100), priced.Tokens);
        Assert.Equal(1, priced.ExcludedDuplicateFrames);

        var open = store.CreateJob(Request("orch_cap", 2, "gpt-6-sol"));
        var grounds = DecisionGroundsPolicy.Freeze(input.Grounds);
        var receipt = DecisionReceiptPolicy.Build(new(grounds, DecisionGroundsPolicyTests.Deterministic,
            "settled", store.GetJob(job.JobId), 1, false, null));
        store.PutReceipt(receipt);
        Assert.Equal(DecisionJobStatuses.Failed, store.GetJob(open.JobId)!.Status);
        Assert.Equal(DecisionJobClaimStatuses.Empty, store.Claim(new("runner-one", "runner")).Status);
        store.FreezeInputs(Shadow("orch_other"));
        Assert.Throws<InvalidOperationException>(() => store.PutReceipt(receipt with
        {
            OccurrenceId = "orch_other",
        }));

        // The claimed call counts; the superseded job never reached a runner.
        for (var index = 0; index < OrchestratorDecisionStore.MaxCallsPerCard - 1; index++)
        {
            store.FreezeInputs(Shadow($"orch_{index}"));
            Assert.Equal(DecisionJobStatuses.Pending, store.CreateJob(Request($"orch_{index}", 1, "gpt-6-sol")).Status);
        }
        store.FreezeInputs(Shadow("orch_over"));
        var capped = store.CreateJob(Request("orch_over", 1, "gpt-6-sol"));
        Assert.Equal(DecisionJobStatuses.CapExhausted, capped.Status);
        Assert.Contains("call cap", capped.Error);

        var reloaded = new OrchestratorDecisionStore(directory, clock, new RatePricing());
        Assert.Equal(receipt.ReceiptId, reloaded.GetReceipt(receipt.ReceiptId)!.ReceiptId);
        Assert.Equal(DecisionJobStatuses.CapExhausted, reloaded.GetJob(capped.JobId)!.Status);
    }

    [Fact]
    public void Billed_completion_after_a_timeout_receipt_records_usage_price_and_card_spend()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(At));
        var store = new OrchestratorDecisionStore(Path.Combine(_workspace, "late"), clock, new RatePricing());
        var input = Shadow("orch_late");
        store.FreezeInputs(input);
        var job = store.CreateJob(Request("orch_late", 1, "gpt-6-sol"));
        var claim = store.Claim(new("runner-one", "runner")).Work!;
        Assert.Equal((DecisionGroundsPolicy.MaxCallInputTokens, DecisionGroundsPolicy.MaxCallOutputTokens),
            (claim.MaxInputTokens, claim.MaxOutputTokens));

        // The engine's wait expired: the timeout receipt holds no usage and closes the claimed job.
        var timeout = DecisionReceiptPolicy.Build(new(DecisionGroundsPolicy.Freeze(input.Grounds),
            DecisionGroundsPolicyTests.Deterministic, "settled", store.GetJob(job.JobId), 1, false,
            "decision job was not completed within 900 s"));
        store.PutReceipt(timeout);
        Assert.Equal(DecisionPriceStatuses.NoUsage, timeout.Call.Price.Status);

        var frame = new DecisionUsageFrameV1("codex:thread-late", "gpt-6-sol", 4_000, 2_000, 0, 200, false);
        Assert.True(store.Complete(new(claim.JobId, claim.ClaimToken, "runner-one", true, "{}", "gpt-6-sol",
            [frame, frame], At, At.AddMinutes(16), null, false)));
        Assert.True(store.Complete(new(claim.JobId, claim.ClaimToken, "runner-one", true, "{}", "gpt-6-sol",
            [frame], At, At.AddMinutes(17), null, false)));

        var closed = store.GetJob(job.JobId)!;
        Assert.Equal((DecisionJobStatuses.Failed, OrchestratorDecisionStore.SupersededByReceipt),
            (closed.Status, closed.Error));
        Assert.Equal(new DecisionTokenUsageV1(4_000, 2_000, 0, 200), closed.Result!.Tokens);
        var late = Assert.Single(store.LateCalls("orch_late"));
        Assert.Equal(new DecisionPriceV1(DecisionPriceStatuses.Priced, 0.052m), late.Price with { Basis = null });
        Assert.Equal(["codex:thread-late"], late.ProviderCallIds);
        Assert.Equal(timeout, store.GetReceipt(timeout.ReceiptId));
        Assert.Throws<InvalidOperationException>(() => store.PutOperatorIntent(timeout.ReceiptId,
            new DecisionOperatorIntentRequest("raise", "Same as the platform default", 24)));
        var intent = store.PutOperatorIntent(timeout.ReceiptId,
            new DecisionOperatorIntentRequest("raise", "More time for the verified build", 24),
            defaultRunLimitSeconds: 3600);
        Assert.Equal(("raise", 24), (intent.Action, intent.RequestedEmergencyHours));
        Assert.Equal(new DecisionChoiceV1(DecisionActionKinds.Adapt,
            "raise-emergency-limit:1440m", "recorded, not applied"), intent.TypedChoice);
        Assert.Equal(intent, store.LatestOperatorIntentForTask("AGT-1"));
        var responseOccurrence = $"operator-response:{timeout.ReceiptId}";
        Assert.Contains(responseOccurrence, store.PendingReceipts(10));
        var responseInput = store.GetInputs(responseOccurrence)!;
        Assert.Equal("operator-response", responseInput.Grounds.Trigger.Kind);
        Assert.Equal(intent.TypedChoice!.Action, responseInput.DeterministicAction);
        Assert.Contains(responseInput.Grounds.Facts, fact => fact.Id == "operator.intent"
            && fact.Source.Ref.EndsWith($"/{timeout.ReceiptId}/operator-intent", StringComparison.Ordinal));
        Assert.Equal(intent, store.PutOperatorIntent(timeout.ReceiptId,
            new DecisionOperatorIntentRequest("stop", "Later duplicate")));
        var row = Assert.Single(store.ListReceipts("AGT-1", 10));
        Assert.Equal((6_200L, DecisionPriceStatuses.Priced, (decimal?)0.052m, 1),
            (row.TotalTokens, row.PriceStatus, row.EstimatedUsd, row.LateCalls));
        Assert.True(DecisionGroundsCollector.IsCurrentOperatorIntent(intent, At, [timeout]));
        var nextInput = Shadow("orch_after_answer");
        store.FreezeInputs(nextInput);
        var nextReceipt = DecisionReceiptPolicy.Build(new(DecisionGroundsPolicy.Freeze(nextInput.Grounds),
            DecisionGroundsPolicyTests.Deterministic, "settled", null, 0, false, "next decision"))
            with { EndedAt = At.AddMinutes(1) };
        store.PutReceipt(nextReceipt);
        Assert.False(DecisionGroundsCollector.IsCurrentOperatorIntent(intent, At.AddMinutes(2),
            store.ReceiptsForTask("AGT-1")));

        var reloaded = new OrchestratorDecisionStore(Path.Combine(_workspace, "late"), clock, new RatePricing());
        Assert.Single(reloaded.LateCalls("orch_late"));
        Assert.Equal(intent, reloaded.GetOperatorIntent(timeout.ReceiptId));
    }

    [Theory]
    [InlineData("raise", 1500, 90000, "Monolith")]
    [InlineData("cut", 30, 1800, "Monolith")]
    [InlineData("raise", 1500, 90000, "Shadow")]
    [InlineData("raise", 1500, 90000, "Engine")]
    [InlineData("raise", 1500, 90000, "Monolith", 5400)]
    [InlineData("cut", 70, 4200, "Monolith", 5400)]
    [InlineData("raise", 1500, 90000, "Monolith", 5400, true)]
    [InlineData("cut", 70, 4200, "Monolith", 5400, true)]
    public async Task Operator_action_applies_on_the_card_through_monolith_authority(
        string action, int? minutes, int? expectedSeconds, string codingProfile,
        int? hostBaselineSeconds = null, bool classAssigned = false)
    {
        const string project = "project";
        const string card = "AGT-1";
        var projectPath = Path.Combine(_workspace, action + codingProfile, project);
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(projectPath, state));
        var cardFolder = Path.Combine(projectPath, TaskStates.Ready, card);
        Directory.CreateDirectory(cardFolder);
        File.WriteAllText(Path.Combine(cardFolder, "task.json"), JsonSerializer.Serialize(new
        {
            id = card, key = card, title = "Operator action", state = TaskStates.Ready,
            order = 1, agent = "codex", kind = TaskKinds.Task,
        }));
        File.WriteAllText(Path.Combine(cardFolder, "prompt.md"), "Build and verify.");
        File.WriteAllText(Path.Combine(cardFolder, "status.md"), "Result: pending.");
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["TaskRepository"] = Path.Combine(_workspace, action + codingProfile),
                    ["WatchPaths:0:Name"] = project,
                    ["WatchPaths:0:Path"] = projectPath,
                    ["WatchPaths:0:RootPath"] = projectPath,
                    ["WatchPaths:0:RepositoryPath"] = projectPath,
                    ["ReviewDecisionOrchestrator:Enabled"] = "false",
                    ["Runner:RunLiveness:Enabled"] = "false",
                    ["Runner:ReviewInfrastructureRetry:Enabled"] = "false",
                    ["DeliveryChain:Guarded"] = "false",
                    ["Orchestration:Transitions:CodingCompletion"] = codingProfile,
                    ["Orchestration:Transitions:ReviewReport"] = "Monolith",
                    ["Orchestration:Transitions:GateResult"] = "Monolith",
                    ["Orchestration:Transitions:OperatorSweep"] = "Monolith",
                    ["Orchestration:Transitions:BatchPlan"] = "Monolith",
                }));
        });
        using var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        if (classAssigned)
            factory.Services.GetRequiredService<ProjectSettingsService>().SetExecutionSettings(
                project, null, "class:linux");
        var store = factory.Services.GetRequiredService<OrchestratorDecisionStore>();
        var input = Shadow("operator-action-" + action + codingProfile);
        store.FreezeInputs(input);
        var receipt = DecisionReceiptPolicy.Build(new(DecisionGroundsPolicy.Freeze(input.Grounds),
            DecisionGroundsPolicyTests.Deterministic, "settled", null, 0, false,
            "operator answer needed"));
        store.PutReceipt(receipt);
        if (hostBaselineSeconds is { } hostBaseline)
        {
            var registry = factory.Services.GetRequiredService<V1ReviewExecutorRegistry>();
            registry.Register("coding-runner",
                new RegisterRunnerRequest("coding-runner", "host", "instance", "1.0.0",
                    TaskServerProtocol.Current, [ReviewCapabilities.CodingExecutor],
                    RunTimeoutSeconds: hostBaseline));
            if (classAssigned)
                registry.AdvertiseCapabilities("coding-runner", new CapabilityAdvertisementRequest(
                    "coding-runner", "instance", CapabilityProtocol.CurrentSchemaVersion,
                    factory.Services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime,
                    180, 1, [new AdvertisedCapabilityDto(CapabilityProtocol.CodingExecutor, "executor"),
                        new AdvertisedCapabilityDto("platform:linux", "platform")]));
            else
            {
                var held = factory.Services.GetRequiredService<RunLeaseService>().TryAcquire(
                    new AgentStudio.Shared.RunLeaseAcquireRequest(
                        card, "coding-runner", "coding-runner", "host", 4242, "codex"));
                Assert.True(held.Granted);
            }
        }
        if (action is "raise" or "cut")
        {
            var rejected = await http.PostAsJsonAsync($"{receipt.EvidenceRef}/operator-intent",
                new DecisionOperatorIntentRequest(action, "Wrong limit direction",
                    RequestedEmergencyMinutes: action == "raise" ? 30 : 1500), DecisionGroundsPolicy.Json);
            Assert.Equal(System.Net.HttpStatusCode.Conflict, rejected.StatusCode);
            Assert.Null(store.GetOperatorIntent(receipt.ReceiptId));
            Assert.DoesNotContain($"operator-response:{receipt.ReceiptId}", store.PendingReceipts(10));
        }
        var posted = await http.PostAsJsonAsync($"{receipt.EvidenceRef}/operator-intent",
            new DecisionOperatorIntentRequest(action, "Operator selected the next run boundary",
                RequestedEmergencyMinutes: minutes), DecisionGroundsPolicy.Json);
        Assert.True(posted.IsSuccessStatusCode, await posted.Content.ReadAsStringAsync());
        var intent = (await posted.Content.ReadFromJsonAsync<DecisionOperatorIntentV1>(
            DecisionGroundsPolicy.Json))!;
        Assert.Equal("Human Operator", intent.Actor);
        Assert.StartsWith("applied:", intent.Outcome);
        Assert.NotNull(intent.AppliedAt);
        Assert.Equal(expectedSeconds, store.AppliedRunLimitSeconds(card));
        Assert.Equal(TaskStates.Ready,
            factory.Services.GetRequiredService<TaskScannerService>().FindJob(card, projectPath)!.State);
        Assert.Equal(intent, store.GetOperatorIntent(receipt.ReceiptId));
        var outcome = await http.GetFromJsonAsync<JsonElement>($"{receipt.EvidenceRef}/outcome");
        Assert.Equal(intent.Outcome,
            outcome.GetProperty("operatorIntent").GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Stop_answer_is_rejected_without_recording_or_dispatching_a_remote_stop()
    {
        const string project = "project";
        const string card = "AGT-1";
        const string runnerId = "coding-runner";
        var projectPath = Path.Combine(_workspace, "remote-stop", project);
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(projectPath, state));
        var cardFolder = Path.Combine(projectPath, TaskStates.Progress, card);
        Directory.CreateDirectory(cardFolder);
        File.WriteAllText(Path.Combine(cardFolder, "task.json"), JsonSerializer.Serialize(new
        {
            id = card, key = card, title = "Stop active run", state = TaskStates.Progress,
            order = 1, agent = "codex", kind = TaskKinds.Task,
        }));
        File.WriteAllText(Path.Combine(cardFolder, "prompt.md"), "Build and verify.");
        File.WriteAllText(Path.Combine(cardFolder, "status.md"), "Result: pending.");
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["TaskRepository"] = Path.Combine(_workspace, "remote-stop"),
                    ["WatchPaths:0:Name"] = project,
                    ["WatchPaths:0:Path"] = projectPath,
                    ["WatchPaths:0:RootPath"] = projectPath,
                    ["WatchPaths:0:RepositoryPath"] = projectPath,
                    ["ReviewDecisionOrchestrator:Enabled"] = "false",
                    ["Runner:RunLiveness:Enabled"] = "false",
                    ["Runner:ReviewInfrastructureRetry:Enabled"] = "false",
                    ["DeliveryChain:Guarded"] = "false",
                    ["Orchestration:Transitions:CodingCompletion"] = "Monolith",
                }));
        });
        using var http = factory.CreateClient();
        using var coding = new RClient(http, runnerId);
        await coding.RegisterAsync(project, "service", CancellationToken.None);
        var lease = await coding.AcquireLeaseAsync(
            new RAcquire(card, runnerId, project, "coding-host", 4242, "codex"), CancellationToken.None);
        Assert.True(lease.Granted);
        var store = factory.Services.GetRequiredService<OrchestratorDecisionStore>();
        var input = Shadow("operator-active-stop");
        store.FreezeInputs(input);
        var receipt = DecisionReceiptPolicy.Build(new(DecisionGroundsPolicy.Freeze(input.Grounds),
            DecisionGroundsPolicyTests.Deterministic, "settled", null, 0, false,
            "operator answer needed"));
        store.PutReceipt(receipt);
        var posted = await http.PostAsJsonAsync($"{receipt.EvidenceRef}/operator-intent",
            new DecisionOperatorIntentRequest("stop", "Stop and hold this run"), DecisionGroundsPolicy.Json);
        Assert.Equal(System.Net.HttpStatusCode.Conflict, posted.StatusCode);
        Assert.Contains("stop-not-available", await posted.Content.ReadAsStringAsync());
        Assert.Null(store.GetOperatorIntent(receipt.ReceiptId));
        Assert.DoesNotContain($"operator-response:{receipt.ReceiptId}", store.PendingReceipts(10));
        Assert.Null(factory.Services.GetRequiredService<RemoteRunStopRequestStore>().Peek(card));
        Assert.NotEqual(TaskStates.Escalated,
            factory.Services.GetRequiredService<TaskScannerService>().FindJob(card, projectPath)!.State);
    }

    [Fact]
    public async Task Alternate_model_attempt_spend_is_included_once_in_occurrence_total()
    {
        var store = new OrchestratorDecisionStore((string?)null,
            new FakeTimeProvider(new DateTimeOffset(At)), new RatePricing());
        var input = Shadow("orch_alternate");
        store.FreezeInputs(input);
        var first = store.CreateJob(Request("orch_alternate", 1, "gpt-6-sol"));
        var firstClaim = store.Claim(new("runner-one", "runner")).Work!;
        Assert.True(store.Complete(new(firstClaim.JobId, firstClaim.ClaimToken, "runner-one", false,
            "", "gpt-6-sol", [new("call-first", "gpt-6-sol", 1_000, 0, 0, 50, false)],
            At, At.AddSeconds(2), "transport failed", true)));
        var second = store.CreateJob(Request("orch_alternate", 2, "gpt-6-sol"));
        var secondClaim = store.Claim(new("runner-one", "runner")).Work!;
        Assert.Equal(second.JobId, secondClaim.JobId);
        Assert.True(store.Complete(new(secondClaim.JobId, secondClaim.ClaimToken, "runner-one", true,
            "{}", "gpt-6-sol", [new("call-second", "gpt-6-sol", 2_000, 0, 0, 100, false)],
            At, At.AddSeconds(4), null, false)));
        var receipt = DecisionReceiptPolicy.Build(new(DecisionGroundsPolicy.Freeze(input.Grounds),
            DecisionGroundsPolicyTests.Deterministic, "settled", store.GetJob(second.JobId), 2, true, null));
        store.PutReceipt(receipt);
        var row = Assert.Single(store.ListReceipts("AGT-1", 10));
        Assert.Equal(first.JobId, store.GetJob(first.JobId)!.JobId);
        Assert.Equal(3_000, row.InputTokens);
        Assert.Equal(150, row.OutputTokens);
        Assert.Equal(row.EstimatedUsd ?? 0, store.SpendSummary("AGT-1", null).PricedUsd);
        Assert.Equal(0, row.LateCalls);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["TaskRepository"] = _workspace }).Build();
        var messages = new AgentMessageBusStore();
        var bridge = new AgentMessageBusBridge(messages, configuration,
            NullLogger<AgentMessageBusBridge>.Instance);
        await bridge.EmitDecisionReceiptAsync(receipt,
            otherBilledCalls: store.OtherBilledCalls(receipt));
        var projected = Assert.Single(messages.Recent(_workspace, "project", 10));
        Assert.Equal(3_000, projected.Tokens!.Input);
        Assert.Equal(150, projected.Tokens.Output);
        Assert.Equal((double)(row.EstimatedUsd ?? 0), projected.Tokens.Dollars);
    }

    [Fact]
    public async Task Late_billed_call_adds_one_bus_usage_projection()
    {
        var store = new OrchestratorDecisionStore((string?)null,
            new FakeTimeProvider(new DateTimeOffset(At)), new RatePricing());
        var input = Shadow("orch_late_bus");
        store.FreezeInputs(input);
        var job = store.CreateJob(Request("orch_late_bus", 1, "gpt-6-sol"));
        var claim = store.Claim(new("runner-one", "runner")).Work!;
        var receipt = DecisionReceiptPolicy.Build(new(DecisionGroundsPolicy.Freeze(input.Grounds),
            DecisionGroundsPolicyTests.Deterministic, "settled", null, 0, false, "call timed out"));
        store.PutReceipt(receipt);
        var completion = new OrchestratorDecisionJobCompletion(claim.JobId, claim.ClaimToken,
            "runner-one", true, "{}", "gpt-6-sol",
            [new DecisionUsageFrameV1("late-call", "gpt-6-sol", 1_000, 0, 0, 50, false)],
            At, At.AddMinutes(1), null, false);
        var added = store.CompleteWithProjection(completion);
        Assert.True(added.Accepted);
        var call = Assert.Single(added.NewLateCalls);
        Assert.Empty(store.CompleteWithProjection(completion).NewLateCalls);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["TaskRepository"] = _workspace }).Build();
        var messages = new AgentMessageBusStore();
        var bridge = new AgentMessageBusBridge(messages, configuration,
            NullLogger<AgentMessageBusBridge>.Instance);
        await bridge.EmitDecisionLateCallAsync(receipt, call);
        var projected = Assert.Single(messages.Recent(_workspace, "project", 10));
        Assert.Equal(1_000, projected.Tokens!.Input);
        Assert.Equal(50, projected.Tokens.Output);
        Assert.Equal("engine-decision-late-call", projected.Topic);
        Assert.Equal(1, store.Summary(receipt).LateCalls);
    }

    [Fact]
    public void Replaced_claim_billed_completion_is_attributed_once_and_reserved_against_card_spend()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(At));
        var directory = Path.Combine(_workspace, "reclaimed");
        var store = new OrchestratorDecisionStore(directory, clock, new RatePricing(15m));
        store.FreezeInputs(Shadow("orch_reclaimed"));
        var job = store.CreateJob(Request("orch_reclaimed", 1, "gpt-6-sol"));
        var first = store.Claim(new("runner-one", "runner")).Work!;
        clock.Advance(OrchestratorDecisionStore.ClaimTtl.Add(TimeSpan.FromSeconds(1)));
        var second = store.Claim(new("runner-two", "runner")).Work!;
        Assert.Equal(job.JobId, second.JobId);
        Assert.NotEqual(first.ClaimToken, second.ClaimToken);

        var frame = new DecisionUsageFrameV1("codex:replaced-call", "gpt-6-sol", 8_000, 0, 0, 1_000, false);
        var billed = new OrchestratorDecisionJobCompletion(first.JobId, first.ClaimToken, "runner-one",
            true, "{}", "gpt-6-sol", [frame, frame], At, At.AddMinutes(11), null, false);
        Assert.True(store.Complete(billed));
        Assert.True(store.Complete(billed));
        Assert.Null(store.GetJob(job.JobId)!.Result);
        var late = Assert.Single(store.LateCalls("orch_reclaimed"));
        Assert.Equal((job.JobId, "runner-one", (decimal?)1.95m),
            (late.JobId, late.HostId, late.Price.EstimatedUsd));

        store.FreezeInputs(Shadow("orch_followup"));
        var denied = store.CreateJob(Request("orch_followup", 1, "gpt-6-sol"));
        Assert.Equal(DecisionJobStatuses.CapExhausted, denied.Status);
        Assert.Contains("priced decision spend", denied.Error);
        var receipt = DecisionReceiptPolicy.Build(new(DecisionGroundsPolicy.Freeze(
            Shadow("orch_reclaimed").Grounds), DecisionGroundsPolicyTests.Deterministic,
            "settled", store.GetJob(job.JobId), 1, false, "replacement claim still running"));
        store.PutReceipt(receipt);
        var row = Assert.Single(store.ListReceipts("AGT-1", 10));
        Assert.Equal((1, (decimal?)1.95m), (row.LateCalls, row.EstimatedUsd));
        var reloaded = new OrchestratorDecisionStore(directory, clock, new RatePricing(15m));
        Assert.Single(reloaded.LateCalls("orch_reclaimed"));
        Assert.True(reloaded.Complete(billed));
        Assert.Single(reloaded.LateCalls("orch_reclaimed"));
    }

    [Fact]
    public void New_job_reserves_its_priced_envelope_for_card_and_host_day()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(At));
        var card = new OrchestratorDecisionStore((string?)null, clock, new RatePricing(20m));
        card.FreezeInputs(Shadow("orch_first"));
        Assert.Equal(DecisionJobStatuses.Pending, card.CreateJob(Request("orch_first", 1, "gpt-6-sol")).Status);
        card.FreezeInputs(Shadow("orch_second"));
        Assert.Equal(DecisionJobStatuses.CapExhausted,
            card.CreateJob(Request("orch_second", 1, "gpt-6-sol")).Status);

        var host = new OrchestratorDecisionStore((string?)null, clock, new RatePricing(50m));
        host.FreezeInputs(HostShadow("host_first", "periodic"));
        Assert.Equal(DecisionJobStatuses.Pending, host.CreateJob(HostRequest("host_first")).Status);
        host.FreezeInputs(HostShadow("host_second", "periodic"));
        var denied = host.CreateJob(HostRequest("host_second"));
        Assert.Equal(DecisionJobStatuses.CapExhausted, denied.Status);
        Assert.Contains("priced decision spend", denied.Error);
    }

    [Fact]
    public void Measured_usage_beyond_the_call_envelope_is_recorded_but_not_accepted_as_a_decision()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(At));
        var store = new OrchestratorDecisionStore((string?)null, clock, new RatePricing());
        store.FreezeInputs(Shadow("orch_overrun"));
        var job = store.CreateJob(Request("orch_overrun", 1, "gpt-6-sol"));
        var claim = store.Claim(new("runner-one", "runner")).Work!;
        var frame = new DecisionUsageFrameV1("codex:thread-big", "gpt-6-sol", 2_000, 6_500, 0, 1_200, false);

        Assert.True(store.Complete(new(claim.JobId, claim.ClaimToken, "runner-one", true,
            """{"chosenActions":[]}""", "gpt-6-sol", [frame], At, At.AddSeconds(30), null, false)));

        var done = store.GetJob(job.JobId)!;
        Assert.Equal(DecisionJobStatuses.Failed, done.Status);
        Assert.False(done.Result!.Success);
        Assert.StartsWith("call-token-cap-exceeded", done.Error);
        Assert.Equal(DecisionPriceStatuses.Priced, done.Result.Price.Status);
        var receipt = DecisionReceiptPolicy.Build(new(DecisionGroundsPolicy.Freeze(Shadow("orch_overrun").Grounds),
            DecisionGroundsPolicyTests.Deterministic, "settled", done, 1, false, null));
        Assert.True(receipt.Call.CallTokenCapExceeded);
        Assert.Equal(DecisionActionKinds.Ask, receipt.Chosen[^1].Action);
    }

    [Fact]
    public void Occurrences_without_a_card_are_capped_per_host_day()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(At));
        var store = new OrchestratorDecisionStore((string?)null, clock, new RatePricing());
        string Create(string occurrence, string trigger)
        {
            store.FreezeInputs(HostShadow(occurrence, trigger));
            return store.CreateJob(HostRequest(occurrence)).Status;
        }

        for (var index = 0; index < DecisionCostCapPolicy.MaxEventCallsPerHostDay; index++)
            Assert.Equal(DecisionJobStatuses.Pending, Create($"event_{index}", "capacity-event"));
        Assert.Equal(DecisionJobStatuses.CapExhausted, Create("event_over", "capacity-event"));
        // Twelve open envelopes reserve 96,000 input tokens; ten more scheduled calls reach 176,000.
        for (var index = 0; index < 10; index++)
            Assert.Equal(DecisionJobStatuses.Pending, Create($"periodic_{index}", "periodic"));
        store.FreezeInputs(HostShadow("periodic_over", "periodic"));
        var tokenCapped = store.CreateJob(HostRequest("periodic_over"));
        Assert.Equal(DecisionJobStatuses.CapExhausted, tokenCapped.Status);
        Assert.Contains("input tokens", tokenCapped.Error);

        // A new UTC day opens a new budget.
        clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(DecisionJobStatuses.Pending, Create("event_next_day", "capacity-event"));

        var periodic = HostShadow("periodic_visible", "periodic");
        store.FreezeInputs(periodic);
        var frozen = DecisionGroundsPolicy.Freeze(periodic.Grounds);
        var receipt = DecisionReceiptPolicy.Build(new(frozen,
            new DecisionChoiceV1(DecisionActionKinds.Observe, "host", "applied:deterministic"),
            "periodic check", null, 0, false, "host unavailable"));
        store.PutReceipt(receipt);
        var hostRow = Assert.Single(store.ListReceipts(null, 10));
        Assert.Null(hostRow.TaskId);
        Assert.Null(hostRow.ProjectId);
        Assert.Equal("ai-operator", hostRow.Actor);
        Assert.Equal(1, store.SpendSummary(null, null).Occurrences);
        Assert.Equal(0, store.SpendSummary("AGT-1", null).Occurrences);
    }

    [Fact]
    public void Ledger_row_mapping_reads_the_provider_call_id_and_legacy_raw_input()
    {
        var legacy = new AgentStudio.Shared.TaskTokenCall
        {
            Ts = At, Model = "gpt-5.5", ParticipantId = "agent:remote-runner:attempt-7:usage:call-9",
            InputTokens = 100_000, CacheReadTokens = 90_000, OutputTokens = 10,
        };
        var normalized = legacy with { InputTokens = 10_000, InputIncludesCached = true };

        var raw = DecisionGroundsCollector.LedgerRow(legacy);
        var settled = DecisionGroundsCollector.LedgerRow(normalized);

        Assert.Equal(("call-9", "attempt-7", true), (raw.ProviderCallId, raw.RunId, raw.InputIncludesCached));
        Assert.False(settled.InputIncludesCached);
        Assert.Equal(CardRunLedgerPolicy.Normalize(raw), CardRunLedgerPolicy.Normalize(settled));
    }

    private const string DecisionDefaultsCli = "codex";

    private static async Task<OrchestratorDecisionJobWork> ClaimAsync(RClient client, string runnerId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var claim = await client.ClaimOrchestratorDecisionJobAsync(
                new OrchestratorDecisionJobClaimRequest(runnerId, "service"), CancellationToken.None);
            if (claim.Work is { } work) return work;
            await Task.Delay(100);
        }
        throw new TimeoutException("No orchestrator decision job became claimable.");
    }

    private static DecisionShadowInputV1 Shadow(string occurrence)
        => new(DecisionGroundsPolicyTests.Input() with { OccurrenceId = occurrence },
            DecisionActionKinds.Prepare, "review-subject", "settled");

    private static DecisionShadowInputV1 HostShadow(string occurrence, string trigger)
    {
        var grounds = DecisionGroundsPolicyTests.Input();
        return new(grounds with
        {
            OccurrenceId = occurrence,
            Trigger = new(trigger, "FluidCapacity", At),
            Scope = new("host", null, null, null, null, null, "runner-host-a"),
        }, DecisionActionKinds.Observe, "host", "periodic check");
    }

    private static OrchestratorDecisionJobRequest HostRequest(string occurrence)
        => new(occurrence, string.Empty, string.Empty, 1, "digest", "prompt", "codex", "gpt-6-sol", "medium", 0, 0);

    private static OrchestratorDecisionJobRequest Request(string occurrence, int attempt, string model)
        => new(occurrence, "project", "AGT-1", attempt,
            DecisionGroundsPolicy.Freeze(Shadow(occurrence).Grounds).ContentDigest, "prompt", "codex", model,
            "medium", DecisionGroundsPolicy.MaxCallInputTokens, DecisionGroundsPolicy.MaxCallOutputTokens);

    private static OrchestratorDecisionJobCompletion Completion(OrchestratorDecisionJobWork work, string runner)
    {
        var frame = new DecisionUsageFrameV1("call-1", "mystery-model", 2_000, 1_500, 0, 100, true);
        return new(work.JobId, work.ClaimToken, runner, true, "{}", "mystery-model", [frame, frame],
            At, At.AddSeconds(3), null, false);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true);
    }

    private sealed class RecordingAiOperatorQueue : IAiOperatorDecisionQueue
    {
        public int Count { get; private set; }
        public DecisionChoiceV1? LastChoice { get; private set; }

        public void RecoverPending() { }

        public void Enqueue(AgentStudio.Shared.TaskInfo task, string occurrenceId, DecisionChoiceV1 choice)
        {
            Count++;
            LastChoice = choice;
        }
    }

    private sealed class RecordingTurnDispatcher : IAiOperatorTurnDispatcher
    {
        public bool FailBeforeDispatch { get; set; }
        public bool FailAfterDispatch { get; set; }
        public List<string> TurnIds { get; } = [];
        public List<string> DispatchIds { get; } = [];
        public List<string> Prompts { get; } = [];
        public OrchestratorSessionRegistry? Registry { get; set; }
        public TimeProvider? Time { get; set; }

        public OrchestratorTurnResponse Enqueue(string contextKey, OrchestratorTurnRequest request, string turnId)
        {
            DispatchIds.Add(turnId);
            if (FailBeforeDispatch)
                throw new IOException("The session history could not be written.");
            if (TurnIds.Contains(turnId))
                return new OrchestratorTurnResponse(contextKey, turnId,
                    OrchestratorTurnService.StatusQueued, 1, 0, 1);
            TurnIds.Add(turnId);
            Prompts.Add(request.Prompt);
            if (FailAfterDispatch)
                throw new IOException("The session history could not be written after the turn started.");
            Registry!.AppendHistory(contextKey, new OrchestratorSessionHistoryEntry(
                Time!.GetUtcNow().UtcDateTime, "turn", turnId, OrchestratorTurnService.StatusQueued,
                request.Prompt, null, null, null, null, 1));
            return new OrchestratorTurnResponse(contextKey, turnId,
                OrchestratorTurnService.StatusQueued, 1, 0, 1);
        }
    }

    private sealed class HttpDecisionPlane(HttpClient client) : IDecisionReceiptPlane
    {
        public async Task<IReadOnlyList<string>> GetPendingDecisionGroundsAsync(int limit, CancellationToken ct)
            => (await client.GetFromJsonAsync<string[]>(
                $"/api/v1/orchestration/decision-grounds/pending?limit={limit}", DecisionGroundsPolicy.Json, ct))!;

        public async Task<DecisionShadowInputV1?> GetGroundsInputAsync(string runId, CancellationToken ct)
            => await client.GetFromJsonAsync<DecisionShadowInputV1>(
                $"/api/v1/orchestration/runs/{runId}/grounds-input", DecisionGroundsPolicy.Json, ct);

        public async Task<OrchestratorDecisionJobDto> CreateDecisionJobAsync(OrchestratorDecisionJobRequest request,
            CancellationToken ct)
            => await Read<OrchestratorDecisionJobDto>(await client.PostAsJsonAsync(
                "/api/v1/orchestration/decision-jobs", request, DecisionGroundsPolicy.Json, ct));

        public async Task<OrchestratorDecisionJobDto> GetDecisionJobAsync(string jobId, CancellationToken ct)
            => (await client.GetFromJsonAsync<OrchestratorDecisionJobDto>(
                $"/api/v1/orchestration/decision-jobs/{jobId}", DecisionGroundsPolicy.Json, ct))!;

        public async Task<OrchestratorDecisionReceiptV1> PutDecisionReceiptAsync(
            OrchestratorDecisionReceiptV1 receipt, CancellationToken ct)
            => await Read<OrchestratorDecisionReceiptV1>(await client.PutAsJsonAsync(
                $"/api/v1/orchestration/runs/{receipt.OccurrenceId}/decision-receipt", receipt,
                DecisionGroundsPolicy.Json, ct));

        private static async Task<T> Read<T>(HttpResponseMessage response)
        {
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<T>(DecisionGroundsPolicy.Json))!;
        }
    }

    private sealed class RatePricing(decimal multiplier = 1m) : ITokenPricing
    {
        public TokenCostEstimate Estimate(string? modelId, long inputTokens, long outputTokens,
            long cacheReadTokens, long cacheCreationTokens, DateTime? recordedAt = null)
        {
            if (modelId == "mystery-model")
                return new(0, 0, 0, 0, 0, modelId, false, TokenEconomy.PriceStatus.UnknownModel, null);
            var input = inputTokens * 10m / 1_000_000m;
            var output = outputTokens * 50m / 1_000_000m;
            var read = cacheReadTokens * 1m / 1_000_000m;
            var write = cacheCreationTokens * 2m / 1_000_000m;
            return new(input * multiplier, output * multiplier, read * multiplier, write * multiplier,
                (input + output + read + write) * multiplier, modelId ?? "unknown", true,
                TokenEconomy.PriceStatus.Resolved, null);
        }

        public IReadOnlyList<PricedTokenEntry> Price(IReadOnlyList<OrchestratorLogEntry> ledger)
            => throw new NotSupportedException();
    }
}
