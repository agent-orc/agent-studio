using System.Text.Json;
using System.Net;
using System.Net.Http.Json;
using AgentStudio.Dispatch;
using AgentStudio.Shared;
using AgentStudio.OrchestrationDecisions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class DispatchRecutCollectorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "packer-collector-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero));
    private string Watch => Path.Combine(_root, "cards");
    private WebApplicationFactory<Program> Factory()
    {
        foreach (var state in TaskStates.All) Directory.CreateDirectory(Path.Combine(Watch, state));
        foreach (var (id, state) in new[] { ("one", TaskStates.Backlog), ("two", TaskStates.Backlog),
            ("old-a", TaskStates.Archive), ("old-b", TaskStates.Archive), ("future", TaskStates.Archive) })
        {
            var folder = Path.Combine(Watch, state, id);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
                { id, key = "P-" + (id switch { "one" => 1, "two" => 2, "old-a" => 3, "old-b" => 4, _ => 5 }),
                    title = id, state, agent = "codex", creationSource = "playthrough", order = 999,
                    sessionChain = id == "two" ? new[] { "earlier-session" } : Array.Empty<string>() }));
        }
        return new TestHostFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root, ["WatchPaths:0:Name"] = "Wave Project",
                ["WatchPaths:0:Path"] = Watch, ["WatchPaths:0:RootPath"] = Watch,
            }));
            builder.ConfigureTestServices(services =>
            {
                TestHostFactory.RemoveBackgroundHostedServices(services);
                services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(_clock);
                services.RemoveAll<IDispatchRecutDrafting>(); services.AddSingleton<IDispatchRecutDrafting, Stubs>();
            });
        });
    }

    [Fact]
    public async Task Source_prior_comes_from_earlier_real_code_deliveries_and_shadow_never_mutates_cards()
    {
        await using var factory = Factory();
        var scanner = factory.Services.GetRequiredService<TaskScannerService>();
        var cards = scanner.ScanAllJobsWithArchive();
        var store = factory.Services.GetRequiredService<DispatchStore>();
        foreach (var card in cards)
        {
            var delivery = card.Id is "old-a" or "old-b" or "future";
            var count = card.Id == "old-a" ? 3 : card.Id == "old-b" ? 13 : 99;
            store.Write(card.ProjectName, card.TaskKey, new(["m/a.cs", "m/b.cs"], ["m/a.cs", "m/b.cs"], ["m"], [], "high", "v1")
            {
                Deliveries = !delivery ? [] : [new("result", "base", _clock.GetUtcNow().AddDays(card.Id == "future" ? 1 : -1),
                    Enumerable.Range(0, count).Select(i => "code/f" + i + ".cs").Append("docs/ignored.cs").ToArray(),
                    ["code"], new(null, null), new(null, null))],
            });
        }
        var originals = cards.Where(c => c.Id is "one" or "two").OrderBy(c => c.Id).Select(c =>
            new DispatchRecutOriginal(c, "Acceptance for " + c.Id, ["finding:" + c.Id])).ToArray();
        Assert.Equal(2, originals.Length);
        var api = new DispatchRecutTests.MemoryApi(originals);
        var request = new DispatchRecutRequest(originals.Select(o => new DispatchWaveCard(o.Info.Key!, o.FindingRefs)).ToArray(), Apply: true);
        var workspace = factory.Services.GetRequiredService<AgentStudio.Registry.ProjectRegistry>()
            .FindByIdOrDisplayName("Wave Project")!.WorkspaceId;
        var r = await factory.Services.GetRequiredService<DispatchRecutService>().PlanAsync("Wave Project", workspace, "w", request, api, default);
        Assert.All(r.Facts.Cards, c => Assert.Equal(8, c.SizePrior));
        Assert.Single(r.Plan.Actions, a => a.Kind == "package");
        Assert.Equal("proposed", r.State);
        Assert.False(r.Facts.LiveAuthority);
        Assert.Equal(0, api.Creates);
        Assert.Equal(0, api.Writes);
    }

    [Fact]
    public async Task Move_API_fences_stale_lanes_and_previously_started_cards()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        using var stale = await client.PostAsJsonAsync("/api/tasks/one/move?project=Wave%20Project",
            new MoveJobRequest { TargetState = TaskStates.Archive, ExpectedSourceState = TaskStates.Ready, RequireUnstarted = true });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("lane-changed", await stale.Content.ReadAsStringAsync());
        using var started = await client.PostAsJsonAsync("/api/tasks/two/move?project=Wave%20Project",
            new MoveJobRequest { TargetState = TaskStates.Archive, ExpectedSourceState = TaskStates.Backlog, RequireUnstarted = true });
        Assert.Equal(HttpStatusCode.Conflict, started.StatusCode);
        Assert.Contains("card-started", await started.Content.ReadAsStringAsync());
        Assert.Equal(TaskStates.Backlog, factory.Services.GetRequiredService<TaskScannerService>().FindJob("one", Watch)!.State);
        Assert.Equal(TaskStates.Backlog, factory.Services.GetRequiredService<TaskScannerService>().FindJob("two", Watch)!.State);
    }

    [Fact]
    public async Task Recorded_undo_archives_an_unstarted_replacement_without_a_delivery_override()
    {
        await using var factory = Factory();
        var scanner = factory.Services.GetRequiredService<TaskScannerService>();
        var cards = scanner.ScanAllJobsWithArchive();
        var replacement = cards.Single(c => c.Id == "one");
        var originals = cards.Where(c => c.Id is "old-a" or "old-b")
            .Select(c => new DispatchRecutOriginal(c, "", [])).ToArray();
        var action = new DispatchRecutAction("package", "package", originals.Select(o => o.Info.Key!).ToArray(), "bounded", true, [], []);
        var workspace = factory.Services.GetRequiredService<AgentStudio.Registry.ProjectRegistry>()
            .FindByIdOrDisplayName(replacement.ProjectName)!.WorkspaceId;
        var receipt = new DispatchRecutReceipt("undo", replacement.ProjectName, workspace, "wave", _clock.GetUtcNow(), "undoing",
            new(replacement.ProjectName, "wave", [], [], [], true), new([action], []), originals,
            new Dictionary<string, DispatchRecutText> { ["package"] = new(replacement.Title, "", true) },
            new Dictionary<string, string> { ["package"] = replacement.Key! }, [], _clock.GetUtcNow().AddHours(24));
        factory.Services.GetRequiredService<DispatchStore>().WriteRecut(receipt);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        using var response = await client.PostAsJsonAsync("/api/tasks/one/move?project=Wave%20Project",
            new MoveJobRequest { TargetState = TaskStates.Archive, ExpectedSourceState = TaskStates.Backlog,
                RequireUnstarted = true, DispatchRecutId = "undo" });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Assert.Equal(TaskStates.Archive, scanner.FindJob("one", Watch)!.State);
    }

    [Fact]
    public async Task References_API_refuses_a_stale_snapshot_without_overwriting_the_owner()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        using var owner = await client.PutAsJsonAsync("/api/tasks/one/references?project=Wave%20Project",
            new SetTaskReferencesRequest { RelatedTo = ["OWNER-EDIT"], ExpectedReferences = new() });
        Assert.True(owner.IsSuccessStatusCode, await owner.Content.ReadAsStringAsync());
        using var stale = await client.PutAsJsonAsync("/api/tasks/one/references?project=Wave%20Project",
            new SetTaskReferencesRequest { RelatedTo = ["PACKER-EDIT"], ExpectedReferences = new() });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("references-changed", await stale.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "OWNER-EDIT" }, factory.Services.GetRequiredService<TaskScannerService>()
            .FindJob("one", Watch)!.References.RelatedTo);
    }
    private sealed class Stubs : IDispatchRecutDrafting
    {
        public Task<IReadOnlyDictionary<string, DispatchRecutText>> DraftAsync(DispatchRecutReceipt receipt, CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<string, DispatchRecutText>>(DispatchRecutDrafting.Stubs(receipt));
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
