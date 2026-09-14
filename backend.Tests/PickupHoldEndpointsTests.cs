using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2818 wire shape. The acceptance the card asks for is "open AGT-2373 and
/// it says why it cannot be picked, without reading a file", so the contract
/// that matters is the one on the HTTP surface:
///
/// <list type="number">
///   <item>a board card and a task detail in a pickup lane carry a
///   <c>pickupHold</c> object with mechanism, reason, age and ways out;</item>
///   <item>a refused dispatch is rendered from the same projection, so the
///   <c>remoteDispatchRejection</c> that was written and never shown finally
///   reaches the card;</item>
///   <item><c>GET /api/pickup-holds</c> lists the whole backlog across
///   projects.</item>
/// </list>
/// </summary>
public sealed class PickupHoldEndpointsTests : IDisposable
{
    private const string App = "hold-app";
    private const string Lib = "hold-lib";

    private readonly string _workspace;
    private readonly string _appWatch;
    private readonly string _libWatch;

    public PickupHoldEndpointsTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "atp-pickup-hold-http-" + Guid.NewGuid().ToString("N"));
        _appWatch = Path.Combine(_workspace, "projects", App);
        _libWatch = Path.Combine(_workspace, "projects", Lib);
        foreach (var watch in new[] { _appWatch, _libWatch })
            foreach (var state in TaskStates.All)
                Directory.CreateDirectory(Path.Combine(watch, state));
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task Detail_ArchivedReleaseGate_CardSaysTheGateCanNeverOpenAndNamesBothWaysOut()
    {
        WriteJob(_libWatch, TaskStates.Archive, "parity-suite", "AGT-2372");
        WriteJob(_appWatch, TaskStates.Ready, "duplicate-cli-paths", "AGT-2373",
            dependsOn: "AGT-2372", releaseGate: true, enteredLaneAt: "2026-08-11T09:00:00Z");

        using var factory = BuildFactory();
        using var client = factory.CreateClient();
        var watchPath = Uri.EscapeDataString(_appWatch);

        using var doc = JsonDocument.Parse(
            await client.GetStringAsync($"/api/tasks/duplicate-cli-paths?watchPath={watchPath}"));
        var info = doc.RootElement.GetProperty("info");

        var hold = info.GetProperty("pickupHold");
        Assert.Equal("dependency-gate", hold.GetProperty("mechanism").GetString());
        Assert.True(hold.GetProperty("unsatisfiable").GetBoolean());
        Assert.Contains("AGT-2372", hold.GetProperty("reason").GetString()!, StringComparison.Ordinal);
        Assert.Contains("archived", hold.GetProperty("reason").GetString()!, StringComparison.OrdinalIgnoreCase);
        Assert.True(hold.GetProperty("heldForSeconds").GetInt64() > 0);

        var kinds = hold.GetProperty("resolutions").EnumerateArray()
            .Select(resolution => resolution.GetProperty("kind").GetString())
            .ToArray();
        Assert.Equal(new[] { "release-target", "drop-release-gate" }, kinds);

        // The distinction also rides on the dependency edge itself, so the
        // dependency chip can say "can never open" instead of "waiting".
        var item = Assert.Single(info.GetProperty("waitsOn").GetProperty("items").EnumerateArray());
        Assert.True(item.GetProperty("unsatisfiable").GetBoolean());
        Assert.False(string.IsNullOrEmpty(item.GetProperty("unsatisfiableReason").GetString()));
    }

    [Fact]
    public async Task Grouped_RefusedDispatch_CardCarriesTheRejectionAsAHold()
    {
        WriteJob(_appWatch, TaskStates.Ready, "installer-one-executable", "AGT-2738",
            enteredLaneAt: "2026-09-06T10:00:00Z",
            rejection: """
            {"code":"capability-mismatch","runnerId":"agent-runner-01","runnerName":"agent-runner-01",
             "reason":"Required capability 'task-server:connectivity' is advertised as unavailable.",
             "rejectedAtUtc":"2026-09-06T19:47:45Z"}
            """);

        using var factory = BuildFactory();
        using var client = factory.CreateClient();
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/tasks/grouped"));

        var card = FindCard(doc.RootElement.GetProperty("ready"), "installer-one-executable");
        var hold = card.GetProperty("pickupHold");

        Assert.Equal("dispatch-rejection", hold.GetProperty("mechanism").GetString());
        var reason = hold.GetProperty("reason").GetString()!;
        Assert.Contains("agent-runner-01", reason, StringComparison.Ordinal);
        Assert.Contains("capability-mismatch", reason, StringComparison.Ordinal);
        Assert.Contains("task-server:connectivity", reason, StringComparison.Ordinal);
        Assert.Equal(
            "2026-09-06T19:47:45Z",
            hold.GetProperty("sinceUtc").GetDateTime().ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"));

        // The durable record also reaches the card through the execution
        // projection the detail header already renders.
        var rejection = card.GetProperty("executionLocation").GetProperty("lastRejection");
        Assert.Equal("capability-mismatch", rejection.GetProperty("code").GetString());
    }

    [Fact]
    public async Task PickupEligibleCard_CarriesNoHold()
    {
        WriteJob(_appWatch, TaskStates.Ready, "workable", "APP-9");

        using var factory = BuildFactory();
        using var client = factory.CreateClient();
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/tasks/grouped"));

        var card = FindCard(doc.RootElement.GetProperty("ready"), "workable");
        Assert.True(!card.TryGetProperty("pickupHold", out var hold) || hold.ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task PickupHolds_ListsTheBacklogAcrossProjects()
    {
        WriteJob(_libWatch, TaskStates.Archive, "parity-suite", "AGT-2372");
        WriteJob(_appWatch, TaskStates.Ready, "duplicate-cli-paths", "AGT-2373",
            dependsOn: "AGT-2372", releaseGate: true, enteredLaneAt: "2026-08-11T09:00:00Z");
        WriteJob(_libWatch, TaskStates.Ready, "container", "LIB-7",
            kind: "epic", enteredLaneAt: "2026-09-10T09:00:00Z");
        WriteJob(_appWatch, TaskStates.Ready, "workable", "APP-9");

        using var factory = BuildFactory();
        using var client = factory.CreateClient();
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/pickup-holds"));

        Assert.Equal(2, doc.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("unsatisfiable").GetInt32());
        var keys = doc.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("key").GetString())
            .ToArray();
        Assert.Equal(new[] { "AGT-2373", "LIB-7" }, keys);

        using var filtered = JsonDocument.Parse(
            await client.GetStringAsync("/api/pickup-holds?unsatisfiableOnly=true"));
        var only = Assert.Single(filtered.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("AGT-2373", only.GetProperty("key").GetString());
    }

    private static JsonElement FindCard(JsonElement lane, string id)
    {
        foreach (var card in lane.EnumerateArray())
            if (card.TryGetProperty("id", out var cid) && cid.GetString() == id)
                return card;
        Assert.Fail($"card '{id}' not found in the response");
        return default; // unreachable
    }

    private static void WriteJob(
        string watchPath,
        string state,
        string slug,
        string key,
        string? dependsOn = null,
        bool releaseGate = false,
        string kind = "task",
        string? enteredLaneAt = null,
        string? rejection = null)
    {
        var dir = Path.Combine(watchPath, state, slug);
        Directory.CreateDirectory(dir);
        var edge = dependsOn is null
            ? ""
            : $",\"references\":{{\"dependsOn\":[{{\"key\":\"{dependsOn}\",\"releaseGate\":{releaseGate.ToString().ToLowerInvariant()}}}]}}";
        var json =
            $"{{\"id\":\"{slug}\",\"key\":\"{key}\",\"title\":\"{slug}\",\"state\":\"{state}\"," +
            $"\"kind\":\"{kind}\",\"order\":1,\"agent\":\"claude\",\"cliType\":\"claude\"," +
            $"\"ownerClientId\":\"local-default\",\"noBranchExpected\":true" +
            (enteredLaneAt is null ? "" : $",\"enteredLaneAt\":\"{enteredLaneAt}\"") +
            (rejection is null ? "" : $",\"remoteDispatchRejection\":{rejection}") +
            $"{edge}}}";
        File.WriteAllText(Path.Combine(dir, "task.json"), json);
    }

    private WebApplicationFactory<Program> BuildFactory() =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["TaskRepository"] = _workspace,
                        ["WatchPaths:0:Name"] = App,
                        ["WatchPaths:0:Path"] = _appWatch,
                        ["WatchPaths:0:RootPath"] = _appWatch,
                        ["WatchPaths:1:Name"] = Lib,
                        ["WatchPaths:1:Path"] = _libWatch,
                        ["WatchPaths:1:RootPath"] = _libWatch,
                    });
                });
            });
}
