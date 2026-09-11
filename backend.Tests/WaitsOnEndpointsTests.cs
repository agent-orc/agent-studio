using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2029 HTTP-level coverage: pins the <b>wire shape</b> of the waits-on
/// feature the frontend consumes, which the pure-unit tests
/// (<see cref="WaitsOnEvaluatorTests"/>, <see cref="WaitsOnPickupGateTests"/>)
/// deliberately do not exercise. Two contracts are locked here end-to-end
/// through <see cref="Program"/>:
/// <list type="number">
/// <item>the read overlay — <c>GET /api/tasks</c>, <c>/grouped</c> and
/// <c>/{id}</c> fold a <c>waitsOn</c> object (<c>items[]</c> with
/// <c>key/resolved/fulfilled/target*</c>, plus <c>blocked</c> and
/// <c>cycleDetected</c>) onto every card that carries <c>dependsOn</c> edges,
/// resolving fulfillment CROSS-PROJECT and archive-inclusive; cards without
/// edges carry a null overlay;</item>
/// <item>the write endpoint — <c>PUT /api/tasks/{id}/references</c> echoes the
/// persisted <c>references</c> and returns unknown keys as non-blocking
/// <c>warnings</c> (200), while self-reference and dependsOn cycles stay hard
/// <c>errors</c> (400).</item>
/// </list>
/// Cross-project is real here: the dependency target lives in a second watched
/// project and keys are globally unique.
/// </summary>
public sealed class WaitsOnEndpointsTests : IDisposable
{
    private const string App = "waitson-app";   // the consumer's project
    private const string Lib = "waitson-lib";   // a second project holding the dependency

    private readonly string _workspace;
    private readonly string _appWatch;
    private readonly string _libWatch;

    public WaitsOnEndpointsTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "atp-waitson-http-" + Guid.NewGuid().ToString("N"));
        _appWatch = Path.Combine(_workspace, "projects", App);
        _libWatch = Path.Combine(_workspace, "projects", Lib);
        foreach (var wp in new[] { _appWatch, _libWatch })
            foreach (var state in TaskStates.All)
                Directory.CreateDirectory(Path.Combine(wp, state));
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    // ---- READ overlay: card / grouped / detail wire shape ----------------

    [Fact]
    public async Task Grouped_OpenCrossProjectDependency_CardCarriesBlockedWaitsOnShape()
    {
        // LIB-1 is still open (2-ready) in the OTHER project; APP-1 waits on it.
        WriteJob(_libWatch, TaskStates.Ready, "dep", "LIB-1");
        WriteJob(_appWatch, TaskStates.Ready, "consumer", "APP-1", dependsOn: new[] { "LIB-1" });

        using var factory = BuildFactory();
        using var client = factory.CreateClient();

        using var resp = await client.GetAsync("/api/tasks/grouped");
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());

        // The card lands in the (camelCase) "ready" bucket of the grouped shape.
        var card = FindCard(doc.RootElement.GetProperty("ready"), "consumer");
        var waitsOn = card.GetProperty("waitsOn");

        var item = Assert.Single(waitsOn.GetProperty("items").EnumerateArray());
        Assert.Equal("LIB-1", item.GetProperty("key").GetString());
        Assert.True(item.GetProperty("resolved").GetBoolean());       // target exists
        Assert.False(item.GetProperty("fulfilled").GetBoolean());     // but is still open
        Assert.Equal(TaskStates.Ready, item.GetProperty("targetState").GetString());
        Assert.True(waitsOn.GetProperty("blocked").GetBoolean());
        Assert.False(waitsOn.GetProperty("cycleDetected").GetBoolean());
    }

    [Fact]
    public async Task List_FulfilledCrossProjectDependency_CardCarriesUnblockedWaitsOnWithNav()
    {
        // Same shape, but LIB-1 has reached 6-completed in the OTHER project.
        WriteJob(_libWatch, TaskStates.Completed, "dep", "LIB-1");
        WriteJob(_appWatch, TaskStates.Ready, "consumer", "APP-1", dependsOn: new[] { "LIB-1" });

        using var factory = BuildFactory();
        using var client = factory.CreateClient();

        // The flat list endpoint returns a bare array of cards.
        using var resp = await client.GetAsync("/api/tasks");
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());

        var card = FindCard(doc.RootElement, "consumer");
        var waitsOn = card.GetProperty("waitsOn");

        var item = Assert.Single(waitsOn.GetProperty("items").EnumerateArray());
        Assert.True(item.GetProperty("fulfilled").GetBoolean());
        Assert.False(waitsOn.GetProperty("blocked").GetBoolean());
        // Navigation payload the chip needs to route to the target without a
        // second lookup — the target is in a different project's watch path.
        Assert.Equal("dep", item.GetProperty("targetJobId").GetString());
        Assert.Equal(_libWatch, item.GetProperty("targetWatchPath").GetString());
    }

    [Fact]
    public async Task List_ReleaseGateDistinguishesTerminalAwaitingRelease()
    {
        WriteJob(_libWatch, TaskStates.Completed, "dep", "LIB-1");
        WriteJob(_appWatch, TaskStates.Ready, "consumer", "APP-1",
            dependsOn: new[] { "LIB-1" }, releaseGate: true);

        using var factory = BuildFactory();
        using var client = factory.CreateClient();
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/tasks"));

        var waitsOn = FindCard(doc.RootElement, "consumer").GetProperty("waitsOn");
        var item = Assert.Single(waitsOn.GetProperty("items").EnumerateArray());
        Assert.True(item.GetProperty("releaseGate").GetBoolean());
        Assert.True(item.GetProperty("waitingForRelease").GetBoolean());
        Assert.False(item.GetProperty("targetReleased").GetBoolean());
        Assert.False(item.GetProperty("fulfilled").GetBoolean());
        Assert.True(waitsOn.GetProperty("blocked").GetBoolean());
    }

    [Fact]
    public async Task Detail_ArchivedCrossProjectTarget_ResolvesFulfilled()
    {
        // Fulfilled includes the terminal 7-archive lane, which the board scan
        // omits — the overlay must still resolve it via the archive-inclusive
        // index, or an already-archived dependency would look "open" forever.
        WriteJob(_libWatch, TaskStates.Archive, "dep", "LIB-1");
        WriteJob(_appWatch, TaskStates.Ready, "consumer", "APP-1", dependsOn: new[] { "LIB-1" });

        using var factory = BuildFactory();
        using var client = factory.CreateClient();
        var watchPath = Uri.EscapeDataString(_appWatch);

        using var resp = await client.GetAsync($"/api/tasks/consumer?watchPath={watchPath}");
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());

        var waitsOn = doc.RootElement.GetProperty("info").GetProperty("waitsOn");
        var item = Assert.Single(waitsOn.GetProperty("items").EnumerateArray());
        Assert.True(item.GetProperty("resolved").GetBoolean());
        Assert.True(item.GetProperty("fulfilled").GetBoolean());
        Assert.False(waitsOn.GetProperty("blocked").GetBoolean());
    }

    [Fact]
    public async Task Detail_DependencyCycle_SurfacesCycleDetectedWithoutDeadlock()
    {
        // APP-1 waits on APP-2 waits on APP-1: a config error the endpoint must
        // surface as cycleDetected so the card can render an error chip. (Such a
        // cycle can only exist on disk when a not-yet-created key later appears;
        // the write endpoint rejects cycles among existing keys — see below.)
        WriteJob(_appWatch, TaskStates.Ready, "a", "APP-1", dependsOn: new[] { "APP-2" });
        WriteJob(_appWatch, TaskStates.Ready, "b", "APP-2", dependsOn: new[] { "APP-1" });

        using var factory = BuildFactory();
        using var client = factory.CreateClient();
        var watchPath = Uri.EscapeDataString(_appWatch);

        using var resp = await client.GetAsync($"/api/tasks/a?watchPath={watchPath}");
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());

        var waitsOn = doc.RootElement.GetProperty("info").GetProperty("waitsOn");
        Assert.True(waitsOn.GetProperty("cycleDetected").GetBoolean());
    }

    [Fact]
    public async Task Grouped_TaskWithoutDependencies_CarriesNoWaitsOnOverlay()
    {
        // The common case: a card with no dependsOn edges carries no chip. The
        // overlay is null (or absent) so the FE renders nothing.
        WriteJob(_appWatch, TaskStates.Ready, "free", "APP-1");

        using var factory = BuildFactory();
        using var client = factory.CreateClient();

        using var resp = await client.GetAsync("/api/tasks/grouped");
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());

        var card = FindCard(doc.RootElement.GetProperty("ready"), "free");
        if (card.TryGetProperty("waitsOn", out var waitsOn))
            Assert.Equal(JsonValueKind.Null, waitsOn.ValueKind);
    }

    [Fact]
    public async Task Grouped_HumanReviewCard_CarriesSortedTransitiveDecisionBacklogImpact()
    {
        WriteJob(_libWatch, TaskStates.HumanReview, "decision", "LIB-1");
        WriteJob(_appWatch, TaskStates.Ready, "direct", "APP-1", dependsOn: new[] { "LIB-1" });
        WriteJob(_appWatch, TaskStates.Ready, "branch-a", "APP-2", dependsOn: new[] { "APP-1" });
        WriteJob(_appWatch, TaskStates.Ready, "branch-b", "APP-3", dependsOn: new[] { "APP-1" });

        using var factory = BuildFactory();
        using var client = factory.CreateClient();

        using var resp = await client.GetAsync("/api/tasks/grouped");
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());

        var card = FindCard(doc.RootElement.GetProperty("humanReview"), "decision");
        var impact = card.GetProperty("transitiveWaiters");
        Assert.Equal(3, impact.GetProperty("count").GetInt32());
        Assert.Equal(
            new[] { "APP-1", "APP-2", "APP-3" },
            impact.GetProperty("keys").EnumerateArray().Select(x => x.GetString()).ToArray());

        using var detailResp = await client.GetAsync(
            $"/api/tasks/decision?watchPath={Uri.EscapeDataString(_libWatch)}");
        detailResp.EnsureSuccessStatusCode();
        using var detailDoc = JsonDocument.Parse(await detailResp.Content.ReadAsStringAsync());
        Assert.Equal(
            3,
            detailDoc.RootElement.GetProperty("info")
                .GetProperty("transitiveWaiters").GetProperty("count").GetInt32());
    }

    // ---- WRITE endpoint: PUT /references response shape ------------------

    [Fact]
    public async Task PutReferences_UnknownKey_Persists200_WithWarning_ThenReadShowsOpenChip()
    {
        WriteJob(_appWatch, TaskStates.Ready, "consumer", "APP-1");

        using var factory = BuildFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        var watchPath = Uri.EscapeDataString(_appWatch);

        // AGT-2029: an unknown waits-on target does NOT fail the write — the
        // target may be created later. It persists and comes back as a warning.
        using var putResp = await client.PutAsJsonAsync(
            $"/api/tasks/consumer/references?watchPath={watchPath}",
            new { dependsOn = new[] { "GHOST-9" } });

        Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);
        using var putDoc = JsonDocument.Parse(await putResp.Content.ReadAsStringAsync());

        // references echoed back with the persisted (camelCase) dependsOn list.
        var dependsOn = putDoc.RootElement.GetProperty("references").GetProperty("dependsOn");
        Assert.Equal("GHOST-9", Assert.Single(dependsOn.EnumerateArray()).GetString());

        // warnings carry the per-edge shape the FE surfaces as an open chip.
        var warn = Assert.Single(putDoc.RootElement.GetProperty("warnings").EnumerateArray());
        Assert.Equal("UnknownKey", warn.GetProperty("code").GetString());
        Assert.Equal("dependsOn", warn.GetProperty("kind").GetString());
        Assert.Equal("GHOST-9", warn.GetProperty("target").GetString());

        // Read-back: the persisted edge surfaces as an unresolved, blocking chip
        // (the scanner cache is invalidated on write, so this is immediate).
        using var getResp = await client.GetAsync($"/api/tasks/consumer?watchPath={watchPath}");
        getResp.EnsureSuccessStatusCode();
        using var getDoc = JsonDocument.Parse(await getResp.Content.ReadAsStringAsync());
        var waitsOn = getDoc.RootElement.GetProperty("info").GetProperty("waitsOn");
        var item = Assert.Single(waitsOn.GetProperty("items").EnumerateArray());
        Assert.Equal("GHOST-9", item.GetProperty("key").GetString());
        Assert.False(item.GetProperty("resolved").GetBoolean());
        Assert.True(waitsOn.GetProperty("blocked").GetBoolean());
    }

    [Fact]
    public async Task PutReferences_ReleaseGateObject_PersistsAndEchoesObjectShape()
    {
        WriteJob(_libWatch, TaskStates.Completed, "dep", "LIB-1");
        WriteJob(_appWatch, TaskStates.Ready, "consumer", "APP-1");

        using var factory = BuildFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        var watchPath = Uri.EscapeDataString(_appWatch);

        using var response = await client.PutAsJsonAsync(
            $"/api/tasks/consumer/references?watchPath={watchPath}",
            new { dependsOn = new[] { new { key = "LIB-1", releaseGate = true } } });
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var edge = Assert.Single(document.RootElement.GetProperty("references")
            .GetProperty("dependsOn").EnumerateArray());
        Assert.Equal("LIB-1", edge.GetProperty("key").GetString());
        Assert.True(edge.GetProperty("releaseGate").GetBoolean());

        using var read = JsonDocument.Parse(await client.GetStringAsync(
            $"/api/tasks/consumer?watchPath={watchPath}"));
        var item = Assert.Single(read.RootElement.GetProperty("info")
            .GetProperty("waitsOn").GetProperty("items").EnumerateArray());
        Assert.True(item.GetProperty("waitingForRelease").GetBoolean());
    }

    [Fact]
    public async Task PutReferences_SelfReference_Returns400_WithErrorShape()
    {
        WriteJob(_appWatch, TaskStates.Ready, "consumer", "APP-1");

        using var factory = BuildFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        var watchPath = Uri.EscapeDataString(_appWatch);

        using var resp = await client.PutAsJsonAsync(
            $"/api/tasks/consumer/references?watchPath={watchPath}",
            new { dependsOn = new[] { "APP-1" } });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var err = Assert.Single(doc.RootElement.GetProperty("errors").EnumerateArray());
        Assert.Equal("SelfReference", err.GetProperty("code").GetString());
        Assert.Equal("APP-1", err.GetProperty("target").GetString());
    }

    [Fact]
    public async Task PutReferences_CycleAmongExistingKeys_Returns400_WithErrorShape()
    {
        // APP-2 already dependsOn APP-1; proposing APP-1 dependsOn APP-2 closes a
        // cycle among existing keys — a hard error on write (dependsOn is a DAG).
        WriteJob(_appWatch, TaskStates.Ready, "consumer", "APP-1");
        WriteJob(_appWatch, TaskStates.Ready, "other", "APP-2", dependsOn: new[] { "APP-1" });

        using var factory = BuildFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        var watchPath = Uri.EscapeDataString(_appWatch);

        using var resp = await client.PutAsJsonAsync(
            $"/api/tasks/consumer/references?watchPath={watchPath}",
            new { dependsOn = new[] { "APP-2" } });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var err = Assert.Single(doc.RootElement.GetProperty("errors").EnumerateArray());
        Assert.Equal("DependsOnCycle", err.GetProperty("code").GetString());
    }

    [Fact]
    public async Task WorkbenchReference_ResolvesPersistsAndAppearsInReverseApi()
    {
        WriteJob(_appWatch, TaskStates.Ready, "consumer", "APP-1");
        WriteWorkbench(_appWatch, "reference-source", relatedTaskKeys: new[] { "APP-LEGACY" });

        using var factory = BuildFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");

        var directCatalogue = factory.Services
            .GetRequiredService<WorkbenchCatalogueService>()
            .List(App, includeHistory: true);
        var cachedCatalogue = factory.Services
            .GetRequiredService<ProjectDocsService>()
            .GetWikiWorkbenchCatalogue(App, includeHistory: true);
        Assert.NotNull(directCatalogue);
        Assert.NotNull(cachedCatalogue);

        using var catalogue = JsonDocument.Parse(await client.GetStringAsync(
            $"/api/projects/{App}/workbenches?history=true"));
        var item = Assert.Single(catalogue.RootElement.GetProperty("items").EnumerateArray());
        var key = item.GetProperty("key").GetString();
        Assert.NotNull(key);
        Assert.Contains("-W", key, StringComparison.Ordinal);

        using var put = await client.PutAsJsonAsync(
            $"/api/tasks/consumer/references?watchPath={Uri.EscapeDataString(_appWatch)}",
            new { workbenches = new[] { key } });
        put.EnsureSuccessStatusCode();
        using var written = JsonDocument.Parse(await put.Content.ReadAsStringAsync());
        Assert.Equal(key, Assert.Single(written.RootElement.GetProperty("references")
            .GetProperty("workbenches").EnumerateArray()).GetString());

        using var reverse = JsonDocument.Parse(await client.GetStringAsync(
            $"/api/projects/{App}/workbenches/{Uri.EscapeDataString(key!)}/references"));
        Assert.Equal(key, reverse.RootElement.GetProperty("workbenchKey").GetString());
        Assert.Equal("APP-LEGACY", Assert.Single(reverse.RootElement.GetProperty("legacyTaskKeys")
            .EnumerateArray()).GetString());
        var link = Assert.Single(reverse.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("APP-1", link.GetProperty("sourceKey").GetString());
        Assert.Equal("workbenches", link.GetProperty("kind").GetString());

        using var rejected = await client.PutAsJsonAsync(
            $"/api/tasks/consumer/references?watchPath={Uri.EscapeDataString(_appWatch)}",
            new { workbenches = new[] { key![..key.LastIndexOf('W')] + "W999" } });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        using var error = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
        Assert.Equal("UnknownWorkbenchKey", Assert.Single(error.RootElement.GetProperty("errors")
            .EnumerateArray()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task PutRelease_ExplicitFlagUnblocksReleaseGatedDependency()
    {
        WriteJob(_libWatch, TaskStates.Completed, "dep", "LIB-1");
        WriteJob(_appWatch, TaskStates.Ready, "consumer", "APP-1",
            dependsOn: new[] { "LIB-1" }, releaseGate: true);

        using var factory = BuildFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        var libWatchPath = Uri.EscapeDataString(_libWatch);
        var appWatchPath = Uri.EscapeDataString(_appWatch);

        using var release = await client.PutAsJsonAsync(
            $"/api/tasks/dep/release?watchPath={libWatchPath}",
            new { released = true });
        release.EnsureSuccessStatusCode();

        using var detail = JsonDocument.Parse(await client.GetStringAsync(
            $"/api/tasks/consumer?watchPath={appWatchPath}"));
        var waitsOn = detail.RootElement.GetProperty("info").GetProperty("waitsOn");
        var item = Assert.Single(waitsOn.GetProperty("items").EnumerateArray());
        Assert.True(item.GetProperty("targetReleased").GetBoolean());
        Assert.False(item.GetProperty("waitingForRelease").GetBoolean());
        Assert.True(item.GetProperty("fulfilled").GetBoolean());
        Assert.False(waitsOn.GetProperty("blocked").GetBoolean());
    }

    [Fact]
    public async Task PutRelease_WritesTaskReleasedTimelineEventWithActorAndDependents()
    {
        // AGT-2709: the release decision has no other producer in the lifecycle,
        // so the operator affordance must leave an auditable row naming who
        // decided and which release-gated dependents the decision moves.
        WriteJob(_libWatch, TaskStates.Completed, "dep", "LIB-1");
        WriteJob(_appWatch, TaskStates.Ready, "consumer", "APP-1",
            dependsOn: new[] { "LIB-1" }, releaseGate: true);

        using var factory = BuildFactory();
        using var client = factory.CreateClient();
        // A freshly registered identity, so the row proves the actor is taken
        // from the request rather than a hardcoded system attribution.
        using var registration = await client.PostAsJsonAsync(
            "/api/clients/register", new { displayName = "Release Operator" });
        registration.EnsureSuccessStatusCode();
        using var registered = JsonDocument.Parse(await registration.Content.ReadAsStringAsync());
        var operatorId = registered.RootElement.GetProperty("id").GetString()!;
        client.DefaultRequestHeaders.Add("X-Client-Id", operatorId);
        var libWatchPath = Uri.EscapeDataString(_libWatch);

        using var released = await client.PutAsJsonAsync(
            $"/api/tasks/dep/release?watchPath={libWatchPath}", new { released = true });
        released.EnsureSuccessStatusCode();

        var events = ReadTimeline(Path.Combine(_libWatch, TaskStates.Completed, "dep"));
        var granted = Assert.Single(events);
        Assert.Equal(TimelineEventKinds.TaskReleased, granted.GetProperty("kind").GetString());
        Assert.Equal($"human:{operatorId}", granted.GetProperty("actor").GetString());
        var details = granted.GetProperty("details");
        Assert.Equal("true", details.GetProperty("released").GetString());
        Assert.Equal("APP-1", details.GetProperty("dependents").GetString());

        // Reversible: withdrawing the release records its own row rather than
        // silently rewriting the grant.
        using var withdrawn = await client.PutAsJsonAsync(
            $"/api/tasks/dep/release?watchPath={libWatchPath}", new { released = false });
        withdrawn.EnsureSuccessStatusCode();

        var after = ReadTimeline(Path.Combine(_libWatch, TaskStates.Completed, "dep"));
        Assert.Equal(2, after.Count);
        Assert.Equal("false", after[1].GetProperty("details").GetProperty("released").GetString());
    }

    [Fact]
    public async Task GetDependents_FlagsReleaseGatedIncomingEdge()
    {
        // The target card names the dependents its release unblocks; the reverse
        // link therefore has to carry the gate, not just the relation kind.
        WriteJob(_libWatch, TaskStates.Completed, "dep", "LIB-1");
        WriteJob(_appWatch, TaskStates.Ready, "gated", "APP-1",
            dependsOn: new[] { "LIB-1" }, releaseGate: true);
        WriteJob(_appWatch, TaskStates.Ready, "plain", "APP-2", dependsOn: new[] { "LIB-1" });

        using var factory = BuildFactory();
        using var client = factory.CreateClient();
        using var doc = JsonDocument.Parse(await client.GetStringAsync(
            $"/api/tasks/dep/dependents?kind=dependsOn&watchPath={Uri.EscapeDataString(_libWatch)}"));

        var links = doc.RootElement.EnumerateArray()
            .ToDictionary(l => l.GetProperty("sourceKey").GetString()!, l => l.GetProperty("releaseGate").GetBoolean());
        Assert.True(links["APP-1"]);
        Assert.False(links["APP-2"]);
    }

    // ---- helpers --------------------------------------------------------

    private static List<JsonElement> ReadTimeline(string jobFolder)
    {
        var path = Path.Combine(jobFolder, "logs", "timeline.jsonl");
        if (!File.Exists(path)) return [];
        return File.ReadAllLines(path)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToList();
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
        string[]? dependsOn = null,
        bool releaseGate = false,
        bool released = false)
    {
        var dir = Path.Combine(watchPath, state, slug);
        Directory.CreateDirectory(dir);
        var refs = dependsOn is { Length: > 0 }
            ? $",\"references\":{{\"dependsOn\":[{string.Join(",", dependsOn.Select(k => releaseGate ? $"{{\"key\":\"{k}\",\"releaseGate\":true}}" : $"\"{k}\""))}]}}"
            : "";
        var release = released ? ",\"released\":true" : "";
        var json =
            $"{{\"id\":\"{slug}\",\"key\":\"{key}\",\"title\":\"{slug}\",\"state\":\"{state}\"," +
            $"\"order\":1,\"agent\":\"claude\",\"cliType\":\"claude\",\"ownerClientId\":\"local-default\"," +
            $"\"noBranchExpected\":true{release}{refs}}}";
        File.WriteAllText(Path.Combine(dir, "task.json"), json);
    }

    private static void WriteWorkbench(string root, string id, string[] relatedTaskKeys)
    {
        var dir = Path.Combine(root, "docs", "operations", id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "index.html"), $"<h1>{id}</h1>");
        File.WriteAllText(Path.Combine(dir, "workbench.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            id,
            title = "Reference source",
            summary = "A source used by cards.",
            entrypoint = "index.html",
            status = "active",
            phase = "testing",
            updatedAt = "2026-08-09T10:00:00Z",
            relatedTaskKeys,
        }));
    }

    private WebApplicationFactory<Program> BuildFactory() =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Test");
                b.ConfigureAppConfiguration((_, cfg) =>
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
