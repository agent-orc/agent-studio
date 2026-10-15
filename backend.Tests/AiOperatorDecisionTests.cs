extern alias EngineAssembly;

using System.Text.Json;
using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using EngineLoop = EngineAssembly::AgentStudio.OrchestratorEngine.AiOperatorDecisionLoop;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3060 (I18): the background deciders are AI Operator occurrences in the
/// Orchestrator Engine. The monolith submits facts and applies receipts.
/// </summary>
public sealed class AiOperatorDecisionTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 8, 0, 0, TimeSpan.Zero);
    private readonly string _workspace = Path.Combine(Path.GetTempPath(),
        "ai-operator-decisions-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(true, false, 90, AiOperatorDisposition.Hold, AiOperatorEffects.None)]
    [InlineData(false, false, 10, AiOperatorDisposition.Hold, AiOperatorEffects.None)]
    [InlineData(false, false, 90, AiOperatorDisposition.Act, AiOperatorEffects.DemoteToReady)]
    [InlineData(false, true, 90, AiOperatorDisposition.Act, AiOperatorEffects.RetriggerPostProcessing)]
    public void Run_liveness_receipt_follows_the_phase_aware_policy(bool heartbeat, bool finished,
        double silentSeconds, AiOperatorDisposition disposition, string effect)
    {
        var decision = AiOperatorDecisionPolicy.Decide(new AiOperatorDecisionPayload(
            AiOperatorDeciders.RunLiveness, "progress:card",
            RunLiveness: new RunLivenessFacts(heartbeat, finished, silentSeconds, 30)));

        Assert.Equal(disposition, decision.Disposition);
        Assert.Equal(effect, decision.Effect);
        Assert.Equal(disposition == AiOperatorDisposition.Act ? AutonomyClass.A1 : AutonomyClass.A0, decision.Autonomy);
        Assert.Equal($"{disposition}:{effect}", decision.ReceiptAction);
    }

    [Fact]
    public void Steer_timeout_auto_answers_within_A1_and_asks_a_person_when_ambiguous()
    {
        var answered = AiOperatorDecisionPolicy.Decide(Steer(new SteerTimeoutFacts(130, 120, true, "Yes, it is merged.", null)));
        var ambiguous = AiOperatorDecisionPolicy.Decide(Steer(new SteerTimeoutFacts(130, 120, false, null, "no branch")));
        var waiting = AiOperatorDecisionPolicy.Decide(Steer(new SteerTimeoutFacts(30, 120, true, "Yes.", null)));

        Assert.Equal((AiOperatorDisposition.Act, AiOperatorEffects.AutoAnswer, "Yes, it is merged."),
            (answered.Disposition, answered.Effect, answered.AnswerText));
        Assert.Equal((AiOperatorDisposition.Ask, AiOperatorEffects.Escalate),
            (ambiguous.Disposition, ambiguous.Effect));
        Assert.Contains("no branch", ambiguous.Detail);
        Assert.Equal(AiOperatorDisposition.Hold, waiting.Disposition);
    }

    [Theory]
    [InlineData(false, 7200, "DONE", AiOperatorEffects.RecoverToReview)]
    [InlineData(false, 7200, null, AiOperatorEffects.RequeueToReady)]
    [InlineData(false, 60, null, AiOperatorEffects.None)]
    [InlineData(true, 7200, null, AiOperatorEffects.None)]
    public void Stale_progress_recovers_requeues_or_leaves_the_card(bool active, double age, string? sentinel, string effect)
        => Assert.Equal(effect, AiOperatorDecisionPolicy.Decide(new AiOperatorDecisionPayload(
            AiOperatorDeciders.StaleProgress, "stale:card",
            StaleProgress: new StaleProgressFacts(active, age, 3600, sentinel))).Effect);

    [Fact]
    public void Timeout_salvage_continues_once_then_asks_a_person_with_the_salvage_named()
    {
        var salvage = new RunSalvageReference("refs/heads/runner/salvage", "abc123");
        var first = AiOperatorDecisionPolicy.Decide(Timeout(new TimeoutSalvageFacts("unknown", salvage, 0, "Timeout")));
        var repeat = AiOperatorDecisionPolicy.Decide(Timeout(new TimeoutSalvageFacts("unknown", salvage, 1, "Timeout")));
        var none = AiOperatorDecisionPolicy.Decide(Timeout(new TimeoutSalvageFacts("unknown", null, 0, "Timeout")));
        var terminal = AiOperatorDecisionPolicy.Decide(Timeout(new TimeoutSalvageFacts("blocked", salvage, 0, null)));

        Assert.Equal((AiOperatorDisposition.Act, AiOperatorEffects.StartContinuation), (first.Disposition, first.Effect));
        Assert.Equal((AiOperatorDisposition.Ask, AiOperatorEffects.Escalate), (repeat.Disposition, repeat.Effect));
        Assert.Contains("salvaged as refs/heads/runner/salvage at abc123", repeat.Detail);
        Assert.Contains("1 automatic continuation round was already spent", repeat.Detail);
        Assert.Equal(AiOperatorEffects.Escalate, none.Effect);
        Assert.Equal(AiOperatorDisposition.Hold, terminal.Disposition);
    }

    [Theory]
    [InlineData(0, 6, false, true, 0, AiOperatorEffects.ReplayIntegration)]
    [InlineData(0, 4, false, true, 0, AiOperatorEffects.None)]
    [InlineData(3, 600, false, true, 0, AiOperatorEffects.ParkAndContinue)]
    [InlineData(3, 600, false, true, 1, AiOperatorEffects.ParkIntegration)]
    [InlineData(3, 600, false, false, 0, AiOperatorEffects.ParkIntegration)]
    [InlineData(3, 600, true, true, 0, AiOperatorEffects.None)]
    public void Gate_environment_ladder_replays_waits_and_parks_once(int spent, int minutesSinceFailure,
        bool parked, bool continuations, int priorRounds, string effect)
    {
        var ladder = new GateEnvironmentRetryFacts(true, true, true, false, true, true, spent,
            [TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(45)],
            null, Now, Now.AddMinutes(minutesSinceFailure));
        var decision = AiOperatorDecisionPolicy.Decide(new AiOperatorDecisionPayload(
            AiOperatorDeciders.GateEnvironmentRetry, "gate-environment:sha:0",
            GateEnvironmentRetry: new GateEnvironmentRetryOperatorFacts(ladder, parked, continuations, priorRounds)));

        Assert.Equal(effect, decision.Effect);
    }

    [Theory]
    [InlineData(AutonomyClass.A0, AiOperatorDisposition.Act)]
    [InlineData(AutonomyClass.A1, AiOperatorDisposition.Act)]
    [InlineData(AutonomyClass.A2, AiOperatorDisposition.Propose)]
    [InlineData(AutonomyClass.A3, AiOperatorDisposition.Ask)]
    public void Autonomy_limit_acts_on_A0_and_A1_proposes_A2_and_never_takes_A3(
        AutonomyClass autonomy, AiOperatorDisposition disposition)
        => Assert.Equal(disposition, AiOperatorDecisionPolicy.Limit(autonomy));

    [Fact]
    public void Unknown_decider_and_missing_facts_fail_closed()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AiOperatorDecisionPolicy.Decide(new AiOperatorDecisionPayload("unknown", "subject")));
        Assert.Throws<ArgumentException>(() =>
            AiOperatorDecisionPolicy.Decide(new AiOperatorDecisionPayload(AiOperatorDeciders.RunLiveness, "subject")));
    }

    [Fact]
    public void Production_authority_pins_every_profile_to_the_engine_and_retires_the_monolith_modes()
    {
        Directory.CreateDirectory(_workspace);
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _workspace,
            ["Orchestration:ExecutionMode"] = "Monolith",
            ["Orchestration:Transitions:GateResult"] = "Engine",
        });
        var rollout = new OrchestrationRolloutStore(configuration);
        rollout.Switch("ReviewReport", "shadow", "operator", "compare before the cutover");
        rollout.Switch("ReviewReport", "rollback", "operator", "recorded before AGT-3060");

        OrchestrationDecisionAuthority.RequireEngine(configuration);

        Assert.True(OrchestrationDecisionAuthority.IsEngineOnly(configuration));
        foreach (var profile in new[] { "CodingCompletion", "ReviewReport", "GateResult", "OperatorSweep",
                     AiOperatorDecisionGateway.Transition })
            Assert.Equal(OrchestrationExecutionMode.Engine, MonolithTransitionSwitch.For(configuration, profile));
        Assert.False(rollout.IsRolledBack("ReviewReport"));
        Assert.All(rollout.GetProfiles(), profile => Assert.Equal("engine", profile.State));
        var refused = Assert.Throws<InvalidOperationException>(() =>
            rollout.Switch("GateResult", "rollback", "operator", "engine outage"));
        Assert.Equal(OrchestrationDecisionAuthority.RetiredError, refused.Message);
        Assert.True(new AiOperatorDecisionGateway(configuration).EngineDecides);
    }

    [Theory]
    [InlineData("Monolith")]
    [InlineData("Shadow")]
    public void Production_host_refuses_a_configured_monolith_decision_mode(string mode)
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        { ["Orchestration:Transitions:OperatorSweep"] = mode });

        var error = Assert.Throws<InvalidOperationException>(() =>
            OrchestrationDecisionAuthority.RequireEngine(configuration));

        Assert.Contains($"OperatorSweep={mode}", error.Message);
        Assert.False(OrchestrationDecisionAuthority.IsEngineOnly(configuration));
    }

    [Fact]
    public void The_loop_host_switch_no_longer_selects_who_decides()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Orchestration:ExecutionMode"] = "Engine" }).Build();

        Assert.Equal(OrchestrationExecutionMode.Monolith, MonolithTransitionSwitch.For(configuration, "GateResult"));
        Assert.False(new AiOperatorDecisionGateway(configuration).EngineDecides);
    }

    [Fact]
    public void Store_keeps_one_active_occurrence_per_card_and_decider()
    {
        var time = new FakeTimeProvider(Now);
        var store = new MonolithOrchestrationStore(directory: null, time, new NoApplier());
        var period = TimeSpan.FromSeconds(15);
        var first = store.CreateOperatorDecision("project", "card", AiOperatorDeciders.RunLiveness,
            "{}", "progress:card", TaskStates.Progress, "card", 0, 0, period);
        var again = store.CreateOperatorDecision("project", "card", AiOperatorDeciders.RunLiveness,
            "{}", "progress:card", TaskStates.Progress, "card", 0, 0, period);
        var other = store.CreateOperatorDecision("project", "card", AiOperatorDeciders.StaleProgress,
            "{}", "stale:card", TaskStates.Progress, "card", 0, 0, null);
        var replay = store.CreateOperatorDecision("project", "card", AiOperatorDeciders.StaleProgress,
            "{}", "stale:card", TaskStates.Progress, "card", 0, 0, null);

        Assert.Equal(first.RunId, again.RunId);
        Assert.NotEqual(first.RunId, other.RunId);
        Assert.Equal(other.RunId, replay.RunId);
        Assert.Equal(OrchestrationStage.OperatorDecision, first.CurrentStage);
        Assert.Equal(MonolithOrchestrationStore.OperatorDecisionProfile(AiOperatorDeciders.RunLiveness),
            store.GetOccurrence(first.RunId)!.Profile);
    }

    [Fact]
    public async Task Engine_stopped_holds_the_liveness_demotion_and_engine_running_applies_it()
    {
        using var factory = LivenessHost(out var projectPath);
        var monitor = factory.Services.GetRequiredService<RunLivenessMonitor>();
        var scanner = factory.Services.GetRequiredService<TaskScannerService>();
        var store = factory.Services.GetRequiredService<MonolithOrchestrationStore>();

        // Engine stopped: the sweep submits facts, decides nothing and moves nothing.
        Assert.Empty(await monitor.SweepAsync());
        Assert.Empty(await monitor.SweepAsync());
        var occurrence = Assert.Single(store.List(LivenessProject, null));
        Assert.Equal("pending", occurrence.Status);
        Assert.Equal(OrchestrationStage.OperatorDecision, occurrence.CurrentStage);
        Assert.Equal(TaskStates.Progress, scanner.FindJob(LivenessCard, projectPath)?.State);

        // Engine running: the AI Operator decides and the authority applies the receipt.
        var runId = await CompleteWithEngineAsync(store);

        Assert.Equal(occurrence.RunId, runId);
        Assert.Equal("Act:demote-to-ready", store.GetOccurrence(runId)!.EngineReceipt!.Action);
        scanner.InvalidateCache();
        Assert.Equal(TaskStates.Ready, scanner.FindJob(LivenessCard, projectPath)?.State);
    }

    [Theory]
    [InlineData("waiting-phase")]
    [InlineData("remote-lease")]
    public async Task Liveness_receipt_keeps_a_card_that_became_waiting_or_remotely_leased_after_the_facts(
        string change)
    {
        using var factory = LivenessHost(out var projectPath);
        var monitor = factory.Services.GetRequiredService<RunLivenessMonitor>();
        var scanner = factory.Services.GetRequiredService<TaskScannerService>();
        var store = factory.Services.GetRequiredService<MonolithOrchestrationStore>();
        Assert.Empty(await monitor.SweepAsync());

        // The change becomes visible after the facts were submitted and before the receipt.
        if (change == "waiting-phase")
            TaskJsonFile.UpdateField(Path.Combine(projectPath, TaskStates.Progress, LivenessCard), "phase",
                LifecyclePhases.LoopWaiting, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        else
            Assert.True(factory.Services.GetRequiredService<RunLeaseService>().TryAcquire(
                new RunLeaseAcquireRequest(LivenessCard, "runner_remote", "remote", "remote-host", 1, "stable"))
                .Granted);
        var runId = await CompleteWithEngineAsync(store);

        Assert.Equal("completed", store.Get(runId)!.Status);
        Assert.Equal("Act:demote-to-ready", store.GetOccurrence(runId)!.EngineReceipt!.Action);
        scanner.InvalidateCache();
        Assert.Equal(TaskStates.Progress, scanner.FindJob(LivenessCard, projectPath)?.State);
    }

    private const string LivenessProject = "ai-operator";
    private const string LivenessCard = "AGT-LIVENESS";

    private WebApplicationFactory<Program> LivenessHost(out string projectPath)
    {
        projectPath = Path.Combine(_workspace, "projects", LivenessProject);
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(projectPath, state));
        var cardFolder = Path.Combine(projectPath, TaskStates.Progress, LivenessCard);
        Directory.CreateDirectory(cardFolder);
        File.WriteAllText(Path.Combine(cardFolder, "task.json"), JsonSerializer.Serialize(new
        {
            id = LivenessCard, title = "Zombie run", state = TaskStates.Progress,
            order = 1, agent = "claude", kind = TaskKinds.Task,
        }));
        File.WriteAllText(Path.Combine(cardFolder, "prompt.md"), "Build it.");

        var path = projectPath;
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["TaskRepository"] = _workspace,
                    ["WatchPaths:0:Name"] = LivenessProject,
                    ["WatchPaths:0:Path"] = path,
                    ["WatchPaths:0:RootPath"] = path,
                    ["WatchPaths:0:RepositoryPath"] = path,
                    ["ReviewDecisionOrchestrator:Enabled"] = "false",
                    ["Supervisor:StuckResumeWindowMinutes"] = "0",
                    ["Runner:RunLiveness:IntervalSeconds"] = "3600",
                    ["Orchestration:Transitions:OperatorDecision"] = "Engine",
                }));
        });
    }

    private static async Task<string> CompleteWithEngineAsync(MonolithOrchestrationStore store)
    {
        var claim = store.Claim(new OrchestrationClaimRequest("engine_test", "instance",
            [OrchestrationStage.OperatorDecision]));
        var decision = await new EngineLoop().ExecuteAsync(claim.Run!, default);
        var completed = await store.CompleteAsync(claim.Run!.RunId, new CompleteOrchestrationStageRequest(
            "engine_test", "instance", claim.Lease!.LeaseId, claim.Lease.Fence,
            OrchestrationStage.OperatorDecision, decision.Action, decision.OutputJson,
            $"engine:{claim.Run.RunId}:liveness"), default);
        Assert.Equal("completed", completed.Status);
        return claim.Run.RunId;
    }

    private static AiOperatorDecisionPayload Steer(SteerTimeoutFacts facts)
        => new(AiOperatorDeciders.SteerTimeout, "steer:card", SteerTimeout: facts);

    private static AiOperatorDecisionPayload Timeout(TimeoutSalvageFacts facts)
        => new(AiOperatorDeciders.TimeoutSalvage, "timeout:attempt:key", TimeoutSalvage: facts);

    public void Dispose()
    {
        try { if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class NoApplier : IMonolithOrchestrationApplier
    {
        public Task ApplyAsync(MonolithOrchestrationOccurrence occurrence,
            CompleteOrchestrationStageRequest completion, CancellationToken ct) => Task.CompletedTask;
    }
}
