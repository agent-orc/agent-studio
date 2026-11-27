using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>Each mutation family must invalidate both board variants and the shared body.</summary>
public sealed class BoardReadMutationTests : IClassFixture<BoardReadMutationTests.Host>
{
    private readonly Host _host;
    public BoardReadMutationTests(Host host) { _host = host; host.Reset(); }

    public sealed class Host : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "board-read-mutations-" + Guid.NewGuid().ToString("N"));
        public string Watch => Path.Combine(Root, "projects", "board");
        public string Other => Path.Combine(Root, "projects", "other");
        public Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Factory { get; }
        public TaskIndexCache Index => Factory.Services.GetRequiredService<TaskIndexCache>();
        public Host()
        {
            foreach (var path in new[] { Watch, Other })
            foreach (var state in TaskStates.All) Directory.CreateDirectory(Path.Combine(path, state));
            Factory = new TestHostFactory().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TaskRepository"] = Root, ["WatchPaths:0:Name"] = "board", ["WatchPaths:0:Path"] = Watch,
                    ["WatchPaths:1:Name"] = "other", ["WatchPaths:1:Path"] = Other,
                    ["TaskIndexCache:ReconciliationSeconds"] = "3600", ["DeliveryChain:Guarded"] = "false",
                }));
            });
        }
        public void Reset()
        {
            foreach (var path in new[] { Watch, Other })
            foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
            {
                if (entry is DirectoryInfo lane && TaskStates.All.Contains(lane.Name))
                {
                    foreach (var child in lane.EnumerateFileSystemInfos()) Delete(child);
                }
                else Delete(entry);
            }
            Seed("one", TaskStates.Backlog, "TEST-1", 10);
            Seed("two", TaskStates.Backlog, "TEST-2", 20);
            Seed("done", TaskStates.Completed, "TEST-3", 30);
            Index.ForceRefresh();
        }
        private void Seed(string id, string state, string key, int order)
        {
            TaskStorageLayout.TryParseKeyNumber(key, out var number);
            var folder = TaskStorageLayout.JobDir(Watch, number, key);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
            {
                id, state, key, order, title = id, ownerClientId = "local-default", agent = "codex", cliType = "codex",
                tags = new[] { "before" },
            }));
            TaskLayoutIndex.Upsert(Watch, key, TaskStorageLayout.Location(number, key), state,
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        }
        private static void Delete(FileSystemInfo entry)
        {
            if (entry is DirectoryInfo directory) directory.Delete(recursive: true);
            else entry.Delete();
        }
        public void Dispose() { Factory.Dispose(); Directory.Delete(Root, true); }
    }

    [Fact]
    public async Task Completed_summary_is_opt_in_and_detail_retains_full_fields()
    {
        using var client = _host.Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        using var full = await client.GetAsync("/api/tasks/grouped");
        using var fullJson = JsonDocument.Parse(await full.Content.ReadAsStringAsync());
        using var summary = await client.GetAsync("/api/tasks/grouped?completedSummary=true");
        using var summaryJson = JsonDocument.Parse(await summary.Content.ReadAsStringAsync());
        var card = Assert.Single(summaryJson.RootElement.GetProperty("completed").EnumerateArray());
        Assert.True(card.GetProperty("summary").GetBoolean());
        Assert.Equal("done", card.GetProperty("id").GetString());
        Assert.Equal("TEST-3", card.GetProperty("key").GetString());
        Assert.False(card.TryGetProperty("model", out _));
        Assert.False(fullJson.RootElement.GetProperty("completed")[0].TryGetProperty("summary", out _));
        Assert.NotEqual(full.Headers.ETag, summary.Headers.ETag);
        Assert.Equal(fullJson.RootElement.GetProperty("backlog").GetRawText(),
            summaryJson.RootElement.GetProperty("backlog").GetRawText());
        using var detail = await client.GetAsync("/api/tasks/done?watchPath=" + Uri.EscapeDataString(_host.Watch));
        detail.EnsureSuccessStatusCode();
        using var detailJson = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        Assert.Equal("done", detailJson.RootElement.GetProperty("info").GetProperty("id").GetString());
        Assert.True(detailJson.RootElement.GetProperty("info").TryGetProperty("model", out _));
    }

    [Fact]
    public async Task Completed_title_edit_replaces_the_summary_and_keeps_detail_current()
    {
        using var client = _host.Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        var route = "/api/tasks/grouped?completedSummary=true";
        using var before = await client.GetAsync(route);
        using var edit = await client.PutAsJsonAsync("/api/tasks/done/title?watchPath="
            + Uri.EscapeDataString(_host.Watch), new { title = "New completed title" });
        edit.EnsureSuccessStatusCode();
        _host.Factory.Services.GetRequiredService<TaskScannerService>().ScanAllJobs();
        using var request = new HttpRequestMessage(HttpMethod.Get, route);
        request.Headers.TryAddWithoutValidation("If-None-Match", before.Headers.ETag!.ToString());
        using var after = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        using var json = JsonDocument.Parse(await after.Content.ReadAsStringAsync());
        Assert.Equal("New completed title", json.RootElement.GetProperty("completed")[0].GetProperty("title").GetString());
        using var detail = await client.GetAsync("/api/tasks/done?watchPath=" + Uri.EscapeDataString(_host.Watch));
        using var detailJson = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        Assert.Equal("New completed title", detailJson.RootElement.GetProperty("info").GetProperty("title").GetString());
    }

    [Theory]
    [InlineData("title")]
    [InlineData("task-type")]
    [InlineData("tags")]
    [InlineData("model")]
    [InlineData("thinking-level")]
    [InlineData("cli-type")]
    [InlineData("references")]
    [InlineData("requires-integration")]
    [InlineData("release")]
    [InlineData("move")]
    [InlineData("delete")]
    [InlineData("reorder")]
    [InlineData("create")]
    [InlineData("change-project")]
    public async Task Mutations_replace_cached_board_bytes_and_validator(string kind)
    {
        using var client = _host.Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        var routes = new[] { "/api/tasks/grouped?includeLegacyReviewLane=false",
            "/api/tasks/grouped?includeLegacyReviewLane=false&completedSummary=true", "/api/tasks/" };
        var before = new List<(string Tag, string Body)>();
        foreach (var route in routes)
        {
            using var response = await client.GetAsync(route);
            response.EnsureSuccessStatusCode();
            before.Add((response.Headers.ETag!.ToString(), await response.Content.ReadAsStringAsync()));
        }
        var probesBefore = _host.Index.FolderStampProbes;
        var address = "/api/tasks/one";
        var scope = "?watchPath=" + Uri.EscapeDataString(_host.Watch);
        using var mutation = kind switch
        {
            "title" => await client.PutAsJsonAsync(address + "/title" + scope, new { title = "Changed title" }),
            "task-type" => await client.PutAsJsonAsync(address + "/task-type" + scope, new { taskType = "bug" }),
            "tags" => await client.PutAsJsonAsync(address + "/tags" + scope, new { tags = Array.Empty<string>() }),
            "model" => await client.PutAsJsonAsync(address + "/model" + scope, new { model = ModelIds.Gpt6Sol }),
            "thinking-level" => await client.PutAsJsonAsync(address + "/thinking-level" + scope, new { thinkingLevel = "high" }),
            "cli-type" => await client.PutAsJsonAsync(address + "/cli-type" + scope, new { cliType = "claude" }),
            "references" => await client.PutAsJsonAsync(address + "/references" + scope, new { dependsOn = new[] { "TEST-2" } }),
            "requires-integration" => await client.PutAsJsonAsync(address + "/requires-integration" + scope, new { requiresIntegration = false }),
            "release" => await client.PutAsJsonAsync(address + "/release" + scope, new { released = true }),
            "move" => await client.PostAsJsonAsync(address + "/move" + scope, new { targetState = TaskStates.Ready }),
            "delete" => await client.DeleteAsync(address + scope),
            "reorder" => await client.PostAsJsonAsync("/api/tasks/reorder", new { jobs = new[] {
                new { jobId = "two", watchPath = _host.Watch }, new { jobId = "one", watchPath = _host.Watch } } }),
            "create" => await client.PostAsJsonAsync("/api/tasks/", new { title = "Created card", watchPath = _host.Watch }),
            "change-project" => await client.PostAsJsonAsync(address + "/change-project" + scope, new { targetWatchPath = _host.Other }),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        Assert.True(mutation.IsSuccessStatusCode, await mutation.Content.ReadAsStringAsync());
        // Drain only work the mutation actually scheduled. A forced refresh
        // would hide a missing invalidation and make this test pass on stale caches.
        _host.Factory.Services.GetRequiredService<TaskScannerService>().ScanAllJobs();
        if (kind is "title" or "task-type" or "tags" or "references" or "requires-integration" or "release")
            Assert.Equal(1, _host.Index.FolderStampProbes - probesBefore);
        for (var i = 0; i < routes.Length; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, routes[i]);
            request.Headers.TryAddWithoutValidation("If-None-Match", before[i].Tag);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotEqual(before[i].Tag, response.Headers.ETag!.ToString());
            Assert.NotEqual(before[i].Body, await response.Content.ReadAsStringAsync());
            using var unchanged = new HttpRequestMessage(HttpMethod.Get, routes[i]);
            unchanged.Headers.TryAddWithoutValidation("If-None-Match", response.Headers.ETag.ToString());
            using var validated = await client.SendAsync(unchanged);
            Assert.Equal(HttpStatusCode.NotModified, validated.StatusCode);
        }
    }
}
