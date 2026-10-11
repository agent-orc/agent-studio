using AgentStudio.TaskServer.Contracts.BatchGate;
using AgentStudio.Pipeline;
using System.Text.Json;
using AgentStudio.Cli;
using AgentStudio.Shared;
using AgentStudio.Runner;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class BatchGateLookAheadPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Waits_for_due_low_risk_card_then_closes_on_deadline()
    {
        var input = Input(
            [Card("A", .05), Card("B", .05)],
            [new BatchPlanUpcoming("C", "review", Now.AddMinutes(15), .05, [])]);
        var waiting = BatchGateLookAheadPlanner.Plan(input);
        Assert.Equal("wait", waiting.Decision);
        Assert.Equal(3, waiting.TargetSize);
        var closed = BatchGateLookAheadPlanner.Plan(input with
        {
            NowUtc = Now.AddMinutes(21),
        });
        Assert.Equal("close", closed.Decision);
        Assert.Equal(["A", "B"], closed.ProposedMembers);
    }

    [Fact]
    public void Urgent_tag_closes_and_high_risk_card_uses_per_task_gate()
    {
        var urgent = BatchGateLookAheadPlanner.Plan(Input(
            [Card("A", .05) with { Tags = ["urgent"] }, Card("B", .05)],
            [new BatchPlanUpcoming("C", "review", Now.AddMinutes(15), .05, [])]));
        Assert.Equal("close", urgent.Decision);
        Assert.Contains("urgent", urgent.Reason);

        var risky = BatchGateLookAheadPlanner.Plan(Input(
            [Card("A", .8), Card("B", .05)], []));
        Assert.Equal("per-task", risky.Decision);
        Assert.Equal(["A"], risky.ProposedMembers);
    }

    [Fact]
    public void Separate_overlap_reorders_eligible_cards()
    {
        var plan = BatchGateLookAheadPlanner.Plan(Input(
            [Card("A", .05, "shared.cs"), Card("B", .05, "shared.cs"),
                Card("C", .05, "other.cs")], []) with
        {
            Overlap = "separate",
            CloseNow = true,
        });
        Assert.Equal("close", plan.Decision);
        Assert.Equal(["A", "C", "B"], plan.ProposedMembers);

        var adjacent = BatchGateLookAheadPlanner.Plan(Input(
            [Card("A", .05, "shared.cs"), Card("C", .05, "other.cs"),
                Card("B", .05, "shared.cs")], []) with { CloseNow = true });
        Assert.Equal(["A", "B", "C"], adjacent.ProposedMembers);
    }

    [Fact]
    public void Expected_runs_per_card_penalizes_risky_large_batches()
    {
        Assert.True(BatchGateLookAheadPlanner.ExpectedRunsPerCard([.05, .05, .05])
            < BatchGateLookAheadPlanner.ExpectedRunsPerCard([.05, .8, .8]));
    }

    [Fact]
    public void Advisor_subset_and_hold_are_bounded()
    {
        var deterministic = BatchGateLookAheadPlanner.Plan(Input(
            [Card("A", .05), Card("B", .05), Card("C", .05)], [])
            with { CloseNow = true });
        var answer = "{\"verdict\":\"adjust\",\"order\":[\"C\",\"A\"],"
            + "\"subset\":[\"A\",\"C\"],\"holdUntil\":null,\"reasons\":[\"lower overlap\"]}";
        Assert.True(BatchGateAdvisorPolicy.TryApply(answer, deterministic,
            ["A", "B", "C"], 2, 3, Now, 45, out var adjusted, out _));
        Assert.Equal(["C", "A"], adjusted.ProposedMembers);

        var invalid = answer.Replace("\"C\",\"A\"", "\"X\",\"A\"");
        Assert.False(BatchGateAdvisorPolicy.TryApply(invalid, deterministic,
            ["A", "B", "C"], 2, 3, Now, 45, out _, out _));
        var veto = "{\"verdict\":\"veto\",\"order\":[],\"subset\":[],"
            + "\"holdUntil\":\"2026-10-11T13:00:00Z\",\"reasons\":[\"wait\"]}";
        Assert.False(BatchGateAdvisorPolicy.TryApply(veto, deterministic,
            ["A", "B", "C"], 2, 3, Now, 45, out _, out _));
    }

    [Fact]
    public void Settings_reject_invalid_field_and_state_exposes_required_shape()
    {
        Assert.Equal("maximumSize", new BatchGateFormationOptions(
            Planner: "look-ahead", MinimumSize: 2, MaximumSize: 9).InvalidField);
        Assert.Equal("advisor.timeoutSeconds", new BatchGateFormationOptions(
            Advisor: new BatchGateAdvisorOptions(TimeoutSeconds: 1)).InvalidField);

        var root = Path.Combine(Path.GetTempPath(), "batch-state-" + Guid.NewGuid().ToString("N"));
        try
        {
            var json = JsonSerializer.Serialize(BatchGateStateSnapshotReader.Read(
                new BatchGateStore(root), "project", new BatchGateFormationOptions(), 10),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            using var doc = JsonDocument.Parse(json);
            Assert.Equal("disabled", doc.RootElement.GetProperty("plan").GetProperty("decision").GetString());
            Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("pending").ValueKind);
            Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("batches").ValueKind);
            Assert.Equal(JsonValueKind.Object, doc.RootElement.GetProperty("members").ValueKind);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Advisor_timeout_uses_deterministic_plan()
    {
        var input = Input([Card("A", .05), Card("B", .05)], []) with { CloseNow = true };
        var deterministic = BatchGateLookAheadPlanner.Plan(input);
        var service = new BatchGateAdvisorService(
            new CliOneShotRegistry([new TimeoutOneShot()]),
            null!,
            new RuntimePromptService(new ConfigurationBuilder().Build(),
                NullLogger<RuntimePromptService>.Instance),
            NullLogger<BatchGateAdvisorService>.Instance);
        var settings = new ProjectSettings
        {
            BatchGate = new BatchGateFormationOptions(Enabled: true,
                Planner: "look-ahead", Advisor: new BatchGateAdvisorOptions(
                    Mode: "enforce", Model: "test-model", TimeoutSeconds: 5)),
        };

        var advised = await service.AdviseAsync("project", settings, input,
            deterministic, CancellationToken.None);

        Assert.Equal(deterministic.Decision, advised.Decision);
        Assert.Equal(deterministic.ProposedMembers, advised.ProposedMembers);
        Assert.Equal("fallback", advised.Advisor?.Verdict);
        Assert.Contains("advisor deadline", advised.Advisor!.Reasons);
    }

    [Fact]
    public async Task Integration_reservation_blocks_same_project_but_not_another()
    {
        var reservations = new BatchIntegrationReservationService();
        using var batch = await reservations.AcquireAsync("alpha", CancellationToken.None);
        var codeMerge = reservations.AcquireAsync("alpha", CancellationToken.None);
        using var otherProject = await reservations.AcquireAsync("beta", CancellationToken.None);
        Assert.False(codeMerge.IsCompleted);
        batch.Dispose();
        using var admitted = await codeMerge.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private sealed class TimeoutOneShot : ICliOneShot
    {
        public string CliType => "codex";
        public async Task<CliOneShotResult> RunAsync(CliOneShotRequest request,
            CancellationToken ct = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return CliOneShotResult.SpawnFailure("timeout", DateTime.UtcNow, DateTime.UtcNow);
        }
    }

    private static BatchPlanInput Input(IReadOnlyList<BatchPlanCard> waiting,
        IReadOnlyList<BatchPlanUpcoming> upcoming) => new(
        Now, waiting, upcoming, 2, 6, 20, 45, ["urgent"],
        "balanced", "adjacent");

    private static BatchPlanCard Card(string key, double risk, params string[] paths)
        => new(key, Now.AddMinutes(-1), risk, paths, []);
}
