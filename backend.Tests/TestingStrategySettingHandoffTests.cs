using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Projects;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

[Trait("Category", "MachineBound")]
public sealed class TestingStrategySettingHandoffTests : BatchGatePilotServiceTests
{
    [Fact]
    public async Task Strict_to_fast_hands_pending_members_to_compile_then_integration()
    {
        using var factory = BuildFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        WithBuildStep();
        EnableBatchGate(factory, 8, "code");
        var member = SeedMember(factory, "SWITCH-PENDING", "src/switch.cs");
        _gate.Merge = request => Green(request.ExpectedSha);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();

        using var response = await client.PutAsJsonAsync($"/api/projects/{ProjectName}/testing", new { strategy = "fast" });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        await pilot.TickAsync(CancellationToken.None);

        Assert.DoesNotContain(_gate.Requests, request => request.Executor == "batch-fallback");
        Assert.Equal(0, _gate.BatchRuns);
        Assert.Contains(_gate.Requests, request => request.Executor == "merge-into-develop-build-gate"
            && request.RequiredTestLevel == TestExecutionLevels.CompileOnly);
        Assert.Equal(TaskStates.HumanReview, factory.Services.GetRequiredService<TaskScannerService>()
            .FindJob(member.Key, _watchPath)!.State);
        var receipt = Assert.Single(body.GetProperty("transition").GetProperty("cards").EnumerateArray());
        Assert.Equal("pending-batch", receipt.GetProperty("work").GetString());
        var current = factory.Services.GetRequiredService<TaskScannerService>().FindJob(member.Key, _watchPath)!;
        var entry = Assert.Single(factory.Services.GetRequiredService<TimelineLog>().ReadAll(current.FolderPath),
            entry => entry.Kind == "testing-strategy-changed");
        Assert.Equal(receipt.GetProperty("action").GetString(), entry.Details!["action"]);
    }

    [Fact]
    public async Task Switch_during_running_batch_retires_it_without_escalation()
    {
        using var factory = BuildFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        WithBuildStep();
        EnableBatchGate(factory, 2, "code");
        var members = new[] { SeedMember(factory, "SWITCH-RUN-A", "a.cs"), SeedMember(factory, "SWITCH-RUN-B", "b.cs") };
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _gate.BatchAsync = async (request, _) => { await completion.Task; return Green(request.ExpectedSha); };
        _gate.Merge = request => Green(request.ExpectedSha);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        pilot.WaitForFlightsInTick = false;
        await pilot.TickAsync(CancellationToken.None);
        await _gate.FirstBatchStarted;
        try
        {
            using var response = await client.PutAsJsonAsync($"/api/projects/{ProjectName}/testing", new { strategy = "fast" });
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(2, body.GetProperty("transition").GetProperty("cards").GetArrayLength());
            Assert.All(body.GetProperty("transition").GetProperty("cards").EnumerateArray(),
                card => Assert.Equal("running-batch", card.GetProperty("work").GetString()));
        }
        finally { completion.TrySetResult(); }
        await pilot.WaitForFlightsAsync(true, CancellationToken.None);
        pilot.WaitForFlightsInTick = true;
        await pilot.TickAsync(CancellationToken.None);
        Assert.DoesNotContain(_gate.Requests, request => request.Executor == "batch-fallback");
        foreach (var member in members)
            Assert.Equal(TaskStates.HumanReview, factory.Services.GetRequiredService<TaskScannerService>()
                .FindJob(member.Key, _watchPath)!.State);
    }
    [Fact]
    public async Task Switch_cancels_running_per_task_gate_without_charging_a_red_verdict()
    {
        using var factory = BuildFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        WithBuildStep();
        EnableBatchGate(factory, 8, "code");
        var member = SeedMember(factory, "SWITCH-FALLBACK", "src/fallback.cs");
        DisableBatchGate(factory);
        _gate.FallbackAsync = async (request, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Green(request.ExpectedSha);
        };
        _gate.Merge = request => Green(request.ExpectedSha);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        pilot.WaitForFlightsInTick = false;
        await pilot.TickAsync(CancellationToken.None);
        await _gate.FirstFallbackStarted;
        using var response = await client.PutAsJsonAsync($"/api/projects/{ProjectName}/testing", new { strategy = "fast" });
        response.EnsureSuccessStatusCode();
        var receipt = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("cancel-per-task-gate-then-integrate-with-compile-check",
            Assert.Single(receipt.GetProperty("transition").GetProperty("cards").EnumerateArray())
                .GetProperty("action").GetString());
        await pilot.WaitForFlightsAsync(true, CancellationToken.None);
        pilot.WaitForFlightsInTick = true;
        await pilot.TickAsync(CancellationToken.None);
        Assert.Single(_gate.Requests, request => request.Executor == "batch-fallback");
        Assert.Equal(TaskStates.HumanReview, factory.Services.GetRequiredService<TaskScannerService>()
            .FindJob(member.Key, _watchPath)!.State);
    }

}
