using System.Text.Json;
using AgentStudio.Cli;
using AgentStudio.Dispatch;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Personas;
using AgentStudio.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class DispatchRecutDraftingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "packer-drafting-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero));
    private PersonaActivationV1 Activation()
    {
        var d = new PersonaCatalogue().Find("packer")!.Descriptor;
        return new(d.Id, PersonaSources.Catalogue, PersonaStates.Shadow, 1, d.Revision, PersonaDescriptorDigest.Compute(d), d,
            new(null, null, null), d.Attributes, d.Budget, d.Scope, ["observe", "dispatch.shadow.record"],
            _clock.GetUtcNow().UtcDateTime, "owner", _clock.GetUtcNow().UtcDateTime.AddDays(1), null, null, []);
    }
    private DispatchRecutReceipt Receipt()
    {
        var originals = new[] { "A", "B" }.Select(k => new DispatchRecutOriginal(new TaskInfo
            { Key = k, Title = k, ProjectName = "p" }, "Acceptance criteria for " + k, ["finding:" + k])).ToArray();
        var plan = new DispatchRecutPlan([new("package:0", "package", ["A", "B"], "shared-files", false, [], [])], []);
        return new("draft", "p", "workspace", "wave", _clock.GetUtcNow(), "drafted",
            new("p", "wave", [], [], [], false), plan, originals, new Dictionary<string, DispatchRecutText>(),
            new Dictionary<string, string>(), []);
    }
    private DispatchRecutDrafting Drafter(PersonaSchedulingService ledger, Model model, PersonaActivationV1 a)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "workspace.json"), JsonSerializer.Serialize(
            new PersonaWorkspaceStateV1(1, "workspace", [a], []), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var activations = new PersonaActivationStore(_root, new(), null!, _ => "workspace", _clock);
        return new(ledger, activations, new CliOneShotRegistry([model]), new ConfigurationBuilder().Build(),
            _clock, NullLogger<DispatchRecutDrafting>.Instance);
    }

    [Fact]
    public async Task One_model_call_drafts_the_whole_recut_and_usage_is_charged_to_Packer()
    {
        var a = Activation();
        var ledger = new PersonaSchedulingService((string?)null);
        var model = new Model();
        var draft = await Drafter(ledger, model, a).DraftAsync(Receipt(), default);
        Assert.Equal(1, model.Calls);
        Assert.False(draft["package:0"].Stub);
        Assert.Equal("Shared fix", draft["package:0"].Title);
        Assert.Contains("Acceptance criteria for A", draft["package:0"].Prompt);
        Assert.Contains("Acceptance criteria for B", draft["package:0"].Prompt);
        Assert.Equal(120, ledger.Budget("workspace", _clock.GetUtcNow().UtcDateTime, null, [a]).Personas["packer"].UsedTokens);
        Assert.True((await Drafter(ledger, model, a).DraftAsync(Receipt(), default))["package:0"].Stub);
        Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task Exhaustion_and_pausing_keep_full_deterministic_source_stubs_without_a_call()
    {
        var model = new Model();
        foreach (var a in new[] { Activation() with { Allowance = Activation().Allowance with { TokensPerWeek = 0 } },
            Activation() with { State = PersonaStates.Paused } })
        {
            var draft = await Drafter(new PersonaSchedulingService((string?)null), model, a).DraftAsync(Receipt(), default);
            Assert.True(draft["package:0"].Stub);
            Assert.Contains("finding:A", draft["package:0"].Prompt);
            Assert.Contains("Acceptance criteria for B", draft["package:0"].Prompt);
        }
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public void Reservations_share_the_weekly_pool_and_are_repeat_safe_after_restart()
    {
        var path = Path.Combine(_root, "ledger");
        var a = Activation() with { Allowance = Activation().Allowance with { TokensPerWeek = 20_000 } };
        var ledger = new PersonaSchedulingService(path);
        var now = _clock.GetUtcNow().UtcDateTime;
        Assert.True(ledger.ReserveRecut("workspace", "one", "p", now, [a], 16_000));
        Assert.False(new PersonaSchedulingService(path).ReserveRecut("workspace", "one", "p", now, [a], 16_000));
        Assert.False(new PersonaSchedulingService(path).ReserveRecut("workspace", "two", "p", now, [a], 16_000));
        Assert.Equal(16_000, new PersonaSchedulingService(path).Budget("workspace", now, null, [a]).UsedTokens);
    }

    private sealed class Model : ICliOneShot
    {
        public string CliType => "claude";
        public int Calls { get; private set; }
        public Task<CliOneShotResult> RunAsync(CliOneShotRequest request, CancellationToken ct = default)
        {
            Calls++;
            // Exercise the real host argument admission, so the fake cannot accept
            // an option that would force every networked call into its fallback.
            HostOneShotIsolation.AddClaudeArguments(new List<string>(), request.Model, request.ExtraArgs);
            return Task.FromResult(new CliOneShotResult(true, 0, "", "", TimeSpan.Zero,
                "{\"package:0\":{\"title\":\"Shared fix\",\"prompt\":\"Coordinate these findings.\"}}",
                new OrchestratorTokenUsage { InputTokens = 100, OutputTokens = 20 }, null, new(null, null), null));
        }
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
