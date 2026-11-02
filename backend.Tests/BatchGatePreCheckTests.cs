using System.Text.Json;
using AgentStudio.Pipeline;
using AgentStudio.Runner;
using AgentStudio.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

public sealed partial class BatchGatePilotServiceTests
{
    [Fact]
    public async Task Red_pre_check_returns_route_inventory_to_agent_and_keeps_member_out_of_batch()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnablePreCheck(factory, closeSize: 2);
        var member = SeedMember(factory, "DOC-PRE-RED", "backend/routes.cs");
        _gate.PreCheck = request => new BuildTestGateResult(BuildTestGateVerdict.Fail, 1, 1200,
            "Failed RoutePermissionInventoryTests\nPOST /api/v1/review-packages/attempts/{attemptId}/lease/renew\nPOST /api/v1/review-packages/attempts/{attemptId}/report",
            "RoutePermissionInventoryTests failed", true, false)
        {
            TestedSha = request.ExpectedSha,
            FailureKind = BuildTestGateFailureKind.Code,
        };

        await factory.Services.GetRequiredService<BatchGatePilotService>().TickAsync(CancellationToken.None);

        var store = factory.Services.GetRequiredService<BatchGateStore>();
        Assert.Empty(store.ListPending());
        Assert.Empty(store.ListManifests());
        Assert.Equal(1, _gate.PreCheckRuns);
        AssertLane(member.Key, TaskStates.Ready);
        var folder = Path.Combine(_watchPath, TaskStates.Ready, member.Key);
        var followUp = File.ReadAllText(Path.Combine(folder, "orchestrator-follow-up.md"));
        Assert.Contains("RoutePermissionInventoryTests", followUp);
        Assert.Contains("/lease/renew", followUp);
        Assert.Contains("/report", followUp);
        Assert.Equal("red", Assert.Single(store.ListPreChecks(ProjectName)).Outcome);
        var state = JsonSerializer.SerializeToElement(factory.Services
            .GetRequiredService<BatchGatePilotService>().State(ProjectName));
        Assert.Equal(1, state.GetProperty("preChecksRun").GetInt32());
        Assert.Equal(1, state.GetProperty("preChecksRed").GetInt32());
        Assert.Equal("pre-check-red", state.GetProperty("members")
            .GetProperty(member.Key).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Green_pre_checks_admit_members_to_a_full_batch()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnablePreCheck(factory, closeSize: 2);
        SeedMember(factory, "DOC-PRE-GREEN-A", "backend/a.cs");
        SeedMember(factory, "DOC-PRE-GREEN-B", "backend/b.cs");
        _gate.Batch = request => Green(request.ExpectedSha);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();

        await pilot.TickAsync(CancellationToken.None);
        Assert.Empty(factory.Services.GetRequiredService<BatchGateStore>().ListManifests());
        Assert.Equal(2, _gate.PreCheckRuns);
        await pilot.TickAsync(CancellationToken.None);

        Assert.Equal(1, _gate.BatchRuns);
        Assert.All(_gate.Requests.Where(request => request.GateId == "batch-pre-check"), request =>
        {
            Assert.True(request.ReserveBatchSlot);
            Assert.Equal(TestExecutionLevels.BatchPreCheck, request.RequiredTestLevel);
        });
        Assert.Equal(2, factory.Services.GetRequiredService<BatchGateStore>()
            .ListPreChecks(ProjectName).Count(check => check.Outcome == "green"));
    }

    [Fact]
    public async Task Unchecked_members_still_close_at_deadline_when_slots_are_busy()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnablePreCheck(factory, closeSize: 2);
        SeedMember(factory, "DOC-PRE-DEADLINE-A", "backend/deadline-a.cs");
        SeedMember(factory, "DOC-PRE-DEADLINE-B", "backend/deadline-b.cs");
        var slots = factory.Services.GetRequiredService<GateSlotUsageTracker>();
        slots.Acquired("foreign-0", 3);
        slots.Acquired("foreign-1", 3);
        slots.Acquired("foreign-2", 3);
        _gate.Batch = request => Green(request.ExpectedSha);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();

        await pilot.TickAsync(CancellationToken.None);
        Assert.Equal(0, _gate.PreCheckRuns);
        _clock = _clock.AddMinutes(21);
        await pilot.TickAsync(CancellationToken.None);

        Assert.Equal(1, _gate.BatchRuns);
        Assert.Empty(factory.Services.GetRequiredService<BatchGateStore>().ListPreChecks(ProjectName));
    }

    private void EnablePreCheck(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory,
        int closeSize)
    {
        EnableBatchGate(factory, closeSize, scope: "code");
        factory.Services.GetRequiredService<AgentStudio.Projects.ProjectSettingsService>()
            .SetBatchGate(ProjectName, new BatchGateFormationOptions(
                Enabled: true, Scope: "code", CloseSize: closeSize, PressureSize: 1,
                DeadlineMinutes: 20, PreCheckEnabled: true));
        factory.Services.GetRequiredService<GateSlotUsageTracker>().Configure(3);
    }
}
