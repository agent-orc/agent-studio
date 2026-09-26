using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using AgentStudio.Security;
using Xunit;

namespace AgentStudio.Tests;

[Collection(WebApplicationFactorySerialCollection.Name)]
public sealed class TaskCoreEndpointTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "task-core-http-" + Guid.NewGuid().ToString("N"));
    private string Jobs => Path.Combine(_root, "jobs");

    [Fact]
    public async Task WarmCore_IsBoundedValidatedAndIndependentOfDirtyBoard()
    {
        Seed("AGT-core", TaskStates.Ready);
        Seed("AGT-archive", TaskStates.Archive);
        await using var factory = Factory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        var registry = factory.Services.GetRequiredService<ProjectRegistry>();
        var project = registry.FindByStorageLocation(Jobs);
        Assert.NotNull(project);
        var index = factory.Services.GetRequiredService<TaskIndexCache>();
        index.ForceRefresh();
        var scansBefore = index.Misses;
        var url = $"/api/tasks/AGT-core/core?project={project!.Id}";

        using var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length <= 16 * 1024);
        using var body = JsonDocument.Parse(bytes);
        Assert.Equal("ready", body.RootElement.GetProperty("state").GetString());
        Assert.Equal("AGT-core", body.RootElement.GetProperty("id").GetString());
        Assert.Equal("ready", body.RootElement.GetProperty("prompt").GetProperty("state").GetString());
        var timeline = body.RootElement.GetProperty("timeline");
        Assert.Equal(5, timeline.GetProperty("events").GetArrayLength());
        Assert.True(Encoding.UTF8.GetByteCount(timeline.GetRawText()) <= 2048);
        Assert.NotNull(response.Headers.ETag);

        using var conditional = new HttpRequestMessage(HttpMethod.Get, url);
        conditional.Headers.IfNoneMatch.Add(response.Headers.ETag!);
        using var notModified = await client.SendAsync(conditional);
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);

        using (var changed = await client.PutAsJsonAsync(
            $"/api/tasks/AGT-core/title?project={project.Id}", new { title = "New title" }))
            changed.EnsureSuccessStatusCode();
        using (var updated = await client.GetAsync(url))
        {
            updated.EnsureSuccessStatusCode();
            using var latest = JsonDocument.Parse(await updated.Content.ReadAsByteArrayAsync());
            Assert.Equal("New title", latest.RootElement.GetProperty("title").GetString());
            Assert.NotEqual(response.Headers.ETag, updated.Headers.ETag);
        }

        index.Invalidate();
        scansBefore = index.Misses;
        using var dirty = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, dirty.StatusCode);
        Assert.Equal(scansBefore, index.Misses);

        using var archive = await client.GetAsync($"/api/tasks/AGT-archive/core?project={project.Id}");
        Assert.Equal(HttpStatusCode.OK, archive.StatusCode);
        using var archived = JsonDocument.Parse(await archive.Content.ReadAsByteArrayAsync());
        Assert.Equal(TaskStates.Archive, archived.RootElement.GetProperty("lane").GetString());

        using var unknown = await client.GetAsync($"/api/tasks/AGT-unknown/core?project={project.Id}");
        Assert.Equal(HttpStatusCode.Accepted, unknown.StatusCode);
    }

    [Fact]
    public async Task CoreRoute_UsesCacheOnly_WithThrowingGitRegistration()
    {
        Seed("AGT-core", TaskStates.Ready);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["TaskRepository"] = Path.Combine(_root, "workspace") }).Build();
        var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        var project = registry.EnsureProjectForStorage(Jobs, "Core Project", "workspace");
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var task = scanner.ScanJobFolder(Path.Combine(Jobs, TaskStates.Ready, "AGT-core"),
            new WatchPathEntry { Name = project.DisplayName, Path = Jobs }, TaskStates.Ready);
        Assert.NotNull(task);
        var scans = 0;
        var index = new TaskIndexCache(scanner, NullLogger<TaskIndexCache>.Instance, config,
            () => { Interlocked.Increment(ref scans); return [task!]; });
        index.GetSnapshot();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Test" });
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(registry);
        builder.Services.AddSingleton(index);
        builder.Services.AddSingleton<ITaskCoreRuntime>(new StubRuntime());
        builder.Services.AddSingleton<GitService>(_ => throw new InvalidOperationException("Git was resolved by core"));
        await using var app = builder.Build();
        var allowed = true;
        app.Use(async (context, next) =>
        {
            context.Items[AccessSecurityMiddleware.HumanPrincipalItem] = new HumanPrincipal(
                new StudioUser
                {
                    Id = "viewer", Username = "viewer", DisplayName = "Viewer", Role = StudioRoles.Viewer,
                    PasswordHash = "unused", Projects = [allowed ? project.Id : "PROJ-revoked"],
                },
                new StudioSession
                {
                    Id = "session", UserId = "viewer", TokenHash = "unused", CsrfHash = "unused",
                    CreatedAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.AddHours(1), AbsoluteExpiresAt = DateTime.UtcNow.AddHours(1),
                });
            await next();
        });
        app.MapGroup("/api/tasks").MapTaskCoreEndpoint();
        await app.StartAsync();

        using var response = await app.GetTestClient().GetAsync($"/api/tasks/AGT-core/core?project={project.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync()))
            Assert.False(body.RootElement.GetProperty("actions").GetProperty("canEdit").GetBoolean());
        allowed = false;
        using var revoked = await app.GetTestClient().GetAsync($"/api/tasks/AGT-core/core?project={project.Id}");
        Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
        Assert.Equal(1, scans);
    }

    private sealed class StubRuntime : ITaskCoreRuntime
    {
        public TaskCoreRuntimeSnapshot Read(TaskCoreRecord core) =>
            new();
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task WarmCore_HandlerP95_OnProductionShapedSnapshot()
    {
        var source = Environment.GetEnvironmentVariable("TASK_CORE_BENCH_FIXTURE");
        if (string.IsNullOrWhiteSpace(source) || !Directory.Exists(source)) return;
        Directory.CreateDirectory(Jobs);
        var target = Path.Combine(Jobs, TaskStates.Ready, "AGT-bench");
        CopyDirectory(source, target);
        await using var factory = Factory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        var project = factory.Services.GetRequiredService<ProjectRegistry>().FindByStorageLocation(Jobs);
        Assert.NotNull(project);
        factory.Services.GetRequiredService<TaskIndexCache>().ForceRefresh();
        var url = $"/api/tasks/AGT-bench/core?project={project!.Id}";
        using (var warm = await client.GetAsync(url)) warm.EnsureSuccessStatusCode();

        var timings = new List<double>();
        var roundTrips = new List<double>();
        var maxBytes = 0;
        for (var i = 0; i < 100; i++)
        {
            var sw = Stopwatch.StartNew();
            using var response = await client.GetAsync(url);
            var bytes = await response.Content.ReadAsByteArrayAsync();
            sw.Stop();
            response.EnsureSuccessStatusCode();
            var timing = Assert.Single(response.Headers.GetValues("Server-Timing"),
                value => value.StartsWith("task-core;dur=", StringComparison.Ordinal));
            timings.Add(double.Parse(timing.Split("dur=", 2)[1], System.Globalization.CultureInfo.InvariantCulture));
            roundTrips.Add(sw.Elapsed.TotalMilliseconds);
            maxBytes = Math.Max(maxBytes, bytes.Length);
        }
        timings.Sort();
        roundTrips.Sort();
        if (Environment.GetEnvironmentVariable("TASK_CORE_BENCH_REPORT") is { Length: > 0 } report)
            File.WriteAllText(report, JsonSerializer.Serialize(new
            {
                fixture = source,
                samples = timings.Count,
                handlerP50Ms = timings[49],
                handlerP95Ms = timings[94],
                httpRoundTripP95Ms = roundTrips[94],
                maxUtf8Bytes = maxBytes,
            }, new JsonSerializerOptions { WriteIndented = true }));
        Assert.True(maxBytes <= 16 * 1024, $"Core response was {maxBytes} bytes.");
        Assert.True(timings[94] <= 30,
            $"Core handler p95 was {timings[94]:F1} ms; HTTP round-trip p95 was {roundTrips[94]:F1} ms.");
    }

    private WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = Path.Combine(_root, "workspace"),
                ["WatchPaths:0:Name"] = "Core Project",
                ["WatchPaths:0:Path"] = Jobs,
                ["WatchPaths:0:RootPath"] = _root,
            }));
        });

    private void Seed(string id, string state)
    {
        var folder = Path.Combine(Jobs, state, id);
        Directory.CreateDirectory(Path.Combine(folder, "logs"));
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
        {
            id, key = id, title = "Task core contract", state, order = 10,
            enteredLaneAt = DateTime.UtcNow,
            mode = "coding", modelExplicit = true, thinkingLevelExplicit = true,
        }));
        File.WriteAllText(Path.Combine(folder, "prompt.md"), string.Concat(Enumerable.Repeat("🧭", 900)));
        File.WriteAllText(Path.Combine(folder, "status.md"), string.Concat(Enumerable.Repeat("é", 700)));
        File.WriteAllText(Path.Combine(folder, "logs", "timeline.jsonl"),
            string.Join('\n', Enumerable.Range(1, 9).Select(i => JsonSerializer.Serialize(new
            { ts = DateTime.UtcNow, kind = "note", actor = "system", summary = $"Event {i}" }))) + "\n", Encoding.UTF8);
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* Watcher disposal may complete after the test. */ }
    }
}
