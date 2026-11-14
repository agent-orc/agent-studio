using System.Text.Json;
using AgentStudio.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

public sealed class BatchGateMalformedStateRouteTests : BatchGatePilotServiceTests
{
    [Fact]
    public async Task State_route_keeps_valid_project_available_when_a_manifest_is_malformed()
    {
        using var factory = BuildFactory();
        using var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var folder = store.BatchDirectory("malformed-state-route");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "manifest.json"), "{broken");

        using var response = await client.GetAsync($"/api/projects/{ProjectName}/batch-gate/state");
        response.EnsureSuccessStatusCode();
        using var state = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Empty(state.RootElement.GetProperty("batches").EnumerateArray());
    }
}
