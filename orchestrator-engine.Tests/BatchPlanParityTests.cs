using System.Text.Json;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.OrchestratorEngine;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TaskServer.Contracts.BatchGate;
using Xunit;

namespace OrchestratorEngine.Tests;

/// <summary>
/// AGT-3055 acceptance: the engine-owned batch plan equals the monolith-owned
/// one for a recorded snapshot set, and the engine calls exactly the advisor
/// CLI type and model the occurrence names.
/// </summary>
public sealed class BatchPlanParityTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-15T08:00:00Z");
    private readonly FrozenTimeProvider _clock = new(Now);

    private static readonly string[] RecordedNames =
    [
        "idle", "wait-for-target", "close-deadline-advisor-adjust", "close-advise-veto",
        "per-task-high-risk", "veto-hold-active", "veto-hold-expired", "close-now",
    ];

    public static TheoryData<string> Recorded => new(RecordedNames);

    [Theory]
    [MemberData(nameof(Recorded))]
    public async Task Engine_and_monolith_produce_identical_batch_decisions(string name)
    {
        var (facts, answer) = Snapshot(name);
        var request = new BatchAdvisorRequest("advise " + BatchPlanDecisionPolicy.EngineInputMarker,
            "codex", "gpt-5.1-codex-mini", 30);
        var codex = new FakeAdvisor("codex", answer);

        var monolith = Monolith(facts, answer, request.Model!);
        var engine = await Engine([new FakeAdvisor("claude", answer), codex], facts, request);

        Assert.Equal(JsonSerializer.Serialize(monolith, Json), JsonSerializer.Serialize(engine, Json));
        Assert.Equal(BatchPlanDecisionPolicy.Signature(monolith), BatchPlanDecisionPolicy.Signature(engine));
        if (BatchPlanDecisionPolicy.Decide(facts).ConsultAdvisor)
        {
            Assert.Equal(["gpt-5.1-codex-mini"], codex.Models);
            Assert.DoesNotContain(BatchPlanDecisionPolicy.EngineInputMarker, codex.Prompts[0]);
        }
        else Assert.Empty(codex.Models);
    }

    [Fact]
    public async Task Recorded_set_covers_every_batch_action()
    {
        var actions = new HashSet<string>();
        foreach (var name in RecordedNames)
        {
            var (facts, answer) = Snapshot(name);
            actions.Add((await Engine([new FakeAdvisor("codex", answer)], facts,
                new BatchAdvisorRequest("p", "codex", "gpt-5.1-codex-mini", 30))).Decision);
        }
        Assert.Equal(["close", "idle", "per-task", "wait"], actions.Order());
    }

    [Fact]
    public async Task Engine_never_substitutes_another_provider_or_model_for_the_requested_advisor()
    {
        var (facts, answer) = Snapshot("close-deadline-advisor-adjust");
        var claude = new FakeAdvisor("claude", answer);
        var codex = new FakeAdvisor("codex", answer, configured: false);

        var unconfigured = await Engine([claude, codex], facts,
            new BatchAdvisorRequest("p", "codex", "gpt-5.1-codex-mini", 30));
        var unknown = await Engine([claude], facts,
            new BatchAdvisorRequest("p", "gemini", "gemini-3", 30));
        var claudeRequest = await Engine([claude, codex], facts,
            new BatchAdvisorRequest("p", "claude", "claude-sonnet-5", 30));

        Assert.Equal("fallback", unconfigured.Advisor!.Verdict);
        Assert.Equal("gpt-5.1-codex-mini", unconfigured.Advisor.Model);
        Assert.Contains("'codex'", unconfigured.Advisor.Reasons[0]);
        Assert.Equal("fallback", unknown.Advisor!.Verdict);
        Assert.Equal("gemini-3", unknown.Advisor.Model);
        Assert.Equal(["claude-sonnet-5"], claude.Models);
        Assert.Equal("claude-sonnet-5", claudeRequest.Advisor!.Model);
        Assert.Empty(codex.Models);
    }

    /// <summary>The monolith-owned path of <c>BatchGatePilotService.PlanLookAheadAsync</c>.</summary>
    private static BatchPlanDecision Monolith(BatchPlanFacts facts, string answer, string model)
    {
        var step = BatchPlanDecisionPolicy.Decide(facts);
        return step.ConsultAdvisor
            ? BatchPlanDecisionPolicy.Advise(facts, step.Decision, new BatchAdvisorReply(answer, model, Now))
            : step.Decision;
    }

    private async Task<BatchPlanDecision> Engine(IEnumerable<IBatchAdvisorModel> advisors,
        BatchPlanFacts facts, BatchAdvisorRequest request)
    {
        var payload = JsonSerializer.Serialize(new BatchPlanOccurrencePayload(facts, request), Json);
        var run = new OrchestrationRunDto("run", facts.Project, "batch-plan:scope", 1, "leased",
            OrchestrationStage.BatchPlan, payload, 0, Now.UtcDateTime, Now.UtcDateTime, null);
        var result = await new BatchPlanLoop(advisors, _clock).ExecuteAsync(run, default);
        Assert.Equal(OrchestrationAction.Continue, result.Action);
        using var output = JsonDocument.Parse(result.OutputJson);
        return output.RootElement.GetProperty("batchPlan").Deserialize<BatchPlanDecision>(Json)!;
    }

    private static (BatchPlanFacts Facts, string Answer) Snapshot(string name)
    {
        const string approve = """{"verdict":"approve","order":[],"subset":[],"holdUntil":null,"reasons":["fine"]}""";
        var adjust = """{"verdict":"adjust","order":["AGT-2","AGT-1"],"subset":["AGT-1","AGT-2"],"holdUntil":null,"reasons":["AGT-3 overlaps"]}""";
        var veto = $$"""{"verdict":"veto","order":[],"subset":[],"holdUntil":"{{Now.AddMinutes(10):O}}","reasons":["release freeze"]}""";
        BatchPlanCard Card(string key, int minutesAgo, double risk = .1, params string[] tags)
            => new(key, Now.AddMinutes(-minutesAgo), risk, [$"src/{key}.cs"], tags);
        BatchPlanInput Input(BatchPlanCard[] waiting, bool closeNow = false)
            => new(Now, waiting, [new BatchPlanUpcoming("AGT-9", "coding", Now.AddHours(3), .1, ["src/AGT-9.cs"])],
                2, 4, 30, 15, ["urgent"], "balanced", "allow", closeNow);
        BatchPlanDecision VetoHold(int minutes) => new("wait", "advisor veto: release freeze",
            Now.AddMinutes(minutes), ["AGT-1", "AGT-2"], 3, [],
            new BatchAdvisorView("enforce", "veto", ["release freeze"], "gpt-5.1-codex-mini", Now.AddMinutes(-20)));
        var three = new[] { Card("AGT-1", 45), Card("AGT-2", 40), Card("AGT-3", 35) };
        return name switch
        {
            "idle" => (Facts(Input([]), null, "enforce"), approve),
            "wait-for-target" => (Facts(Input([Card("AGT-1", 5)]), null, "enforce"), approve),
            "close-deadline-advisor-adjust" => (Facts(Input(three), null, "enforce"), adjust),
            "close-advise-veto" => (Facts(Input(three), null, "advise"), veto),
            "per-task-high-risk" => (Facts(Input([Card("AGT-1", 45, .9), Card("AGT-2", 5)]), null, "enforce"), approve),
            "veto-hold-active" => (Facts(Input(three), VetoHold(5), "enforce"), approve),
            "veto-hold-expired" => (Facts(Input(three), VetoHold(-1), "enforce"), approve),
            "close-now" => (Facts(Input([Card("AGT-1", 5), Card("AGT-2", 4)], closeNow: true), null, "enforce"), approve),
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };
    }

    private static BatchPlanFacts Facts(BatchPlanInput snapshot, BatchPlanDecision? prior, string mode)
        => new("PROJ-001", "scope", snapshot, prior, mode);

    private sealed class FakeAdvisor(string cliType, string answer, bool configured = true) : IBatchAdvisorModel
    {
        public List<string> Models { get; } = [];
        public List<string> Prompts { get; } = [];
        public string CliType => cliType;
        public bool Configured => configured;

        public Task<string> AskAsync(string model, string prompt, CancellationToken ct)
        {
            Models.Add(model);
            Prompts.Add(prompt);
            return Task.FromResult(answer);
        }
    }

    private sealed class FrozenTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
