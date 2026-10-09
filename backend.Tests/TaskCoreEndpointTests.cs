using System.Diagnostics;
using System.Globalization;
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
        Assert.Equal(Jobs, body.RootElement.GetProperty("watchPath").GetString());
        Assert.Equal(Path.Combine(Jobs, TaskStates.Ready, "AGT-core"),
            body.RootElement.GetProperty("folderPath").GetString());
        Assert.Equal("ready", body.RootElement.GetProperty("prompt").GetProperty("state").GetString());
        var timeline = body.RootElement.GetProperty("timeline");
        Assert.Equal(5, timeline.GetProperty("events").GetArrayLength());
        Assert.True(Encoding.UTF8.GetByteCount(timeline.GetRawText()) <= 2048);
        Assert.NotNull(response.Headers.ETag);

        using (var traced = new HttpRequestMessage(HttpMethod.Get, url))
        {
            traced.Headers.Add("X-Task-Switch-Trace", "1");
            traced.Headers.Add("X-Task-Switch-Id", Guid.NewGuid().ToString("N"));
            traced.Headers.Add("X-Task-Request-Id", Guid.NewGuid().ToString("N"));
            using var measured = await client.SendAsync(traced);
            measured.EnsureSuccessStatusCode();
            Assert.Equal("0", measured.Headers.GetValues("X-Task-Core-Git-Spawns").Single());
            Assert.Equal("0", measured.Headers.GetValues("X-Task-Core-Workspace-Scans").Single());
            var timings = string.Join(",", measured.Headers.GetValues("Server-Timing"));
            foreach (var stage in new[] { "task-core", "core-index", "core-runtime", "core-serialize" })
                Assert.Contains(stage + ";dur=", timings, StringComparison.Ordinal);
        }

        using var conditional = new HttpRequestMessage(HttpMethod.Get, url);
        conditional.Headers.IfNoneMatch.Add(response.Headers.ETag!);
        using var notModified = await client.SendAsync(conditional);
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
        Assert.Contains("core-serialize;dur=0.000", string.Join(",", notModified.Headers.GetValues("Server-Timing")),
            StringComparison.Ordinal);

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
    public async Task DetailResources_AreTypedConditionalAndGenerationBound()
    {
        Seed("AGT-core", TaskStates.Ready);
        await using var factory = Factory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        var project = factory.Services.GetRequiredService<ProjectRegistry>().FindByStorageLocation(Jobs)!;
        var index = factory.Services.GetRequiredService<TaskIndexCache>();
        index.ForceRefresh();
        using var coreResponse = await client.GetAsync($"/api/tasks/AGT-core/core?project={project.Id}");
        coreResponse.EnsureSuccessStatusCode();
        using var core = JsonDocument.Parse(await coreResponse.Content.ReadAsStringAsync());
        // The 64-bit generation is a string so JavaScript clients echo it exactly.
        Assert.Equal(JsonValueKind.String, core.RootElement.GetProperty("coreVersion").ValueKind);
        var generation = long.Parse(core.RootElement.GetProperty("coreVersion").GetString()!, CultureInfo.InvariantCulture);
        var scans = index.Misses;

        var url = $"/api/tasks/AGT-core/details/documents?project={project.Id}&generation={generation}&name=prompt";
        using var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("documents", body.RootElement.GetProperty("resource").GetString());
        Assert.Equal("ready", body.RootElement.GetProperty("state").GetString());
        Assert.Equal("AGT-core", body.RootElement.GetProperty("id").GetString());
        Assert.Equal(generation.ToString(CultureInfo.InvariantCulture), body.RootElement.GetProperty("coreVersion").GetString());
        Assert.Equal(900, body.RootElement.GetProperty("data").GetProperty("markdown")
            .GetString()!.EnumerateRunes().Count());
        Assert.False(body.RootElement.TryGetProperty("info", out _));
        Assert.NotNull(response.Headers.ETag);

        using var conditional = new HttpRequestMessage(HttpMethod.Get, url);
        conditional.Headers.IfNoneMatch.Add(response.Headers.ETag!);
        using var notModified = await client.SendAsync(conditional);
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);

        using var stale = await client.GetAsync($"/api/tasks/AGT-core/details/usage?project={project.Id}&generation=0");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var history = await client.GetAsync($"/api/tasks/AGT-core/details/history?project={project.Id}&generation={generation}");
        history.EnsureSuccessStatusCode();
        using var git = await client.GetAsync($"/api/tasks/AGT-core/details/git?project={project.Id}&generation={generation}");
        git.EnsureSuccessStatusCode();
        using var gitBody = JsonDocument.Parse(await git.Content.ReadAsStringAsync());
        Assert.Equal("git", gitBody.RootElement.GetProperty("resource").GetString());
        Assert.Equal(JsonValueKind.Object, gitBody.RootElement.GetProperty("data").ValueKind);
        Assert.False(gitBody.RootElement.TryGetProperty("info", out _));
        Assert.NotNull(git.Headers.ETag);
        Assert.Equal(scans, index.Misses);
    }

    [Fact]
    public async Task DetailResources_VersionHashesTheWireData_AndWarmingIsNotMissing()
    {
        Seed("AGT-core", TaskStates.Ready);
        await using var factory = Factory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        var project = factory.Services.GetRequiredService<ProjectRegistry>().FindByStorageLocation(Jobs)!;
        var index = factory.Services.GetRequiredService<TaskIndexCache>();
        index.ForceRefresh();

        // `version` is the hash of exactly the `data` bytes on the wire, which
        // are serialized once with the host's HTTP JSON options (string enums).
        using var response = await client.GetAsync(
            $"/api/tasks/AGT-core/details/documents?project={project.Id}&name=status");
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = body.RootElement.GetProperty("data");
        Assert.Equal(JsonValueKind.String, data.GetProperty("summaryState").GetProperty("status").ValueKind);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(data.GetRawText()))).ToLowerInvariant();
        Assert.Equal(hash, body.RootElement.GetProperty("version").GetString());

        // A task the re-hydrating index cannot place yet answers 202 like the
        // core route, so the client keeps the selection instead of revoking it.
        index.Invalidate();
        using var warming = await client.GetAsync($"/api/tasks/AGT-unknown/details/usage?project={project.Id}");
        Assert.Equal(HttpStatusCode.Accepted, warming.StatusCode);
        using var warmingBody = JsonDocument.Parse(await warming.Content.ReadAsStringAsync());
        Assert.Equal("warming", warmingBody.RootElement.GetProperty("state").GetString());
        Assert.Equal("task-index-warming", warmingBody.RootElement.GetProperty("reason").GetString());

        index.ForceRefresh();
        using var missing = await client.GetAsync($"/api/tasks/AGT-unknown/details/usage?project={project.Id}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
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

    [Fact]
    public async Task ReleaseMutation_UpdatesDependentCoreAndValidatorBeforeResponse()
    {
        Seed("AGT-target", TaskStates.Archive);
        Seed("AGT-dependent", TaskStates.Ready, "AGT-target");
        await using var factory = Factory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        var project = factory.Services.GetRequiredService<ProjectRegistry>().FindByStorageLocation(Jobs)!;
        var index = factory.Services.GetRequiredService<TaskIndexCache>();
        index.ForceRefresh();
        var url = $"/api/tasks/AGT-dependent/core?project={project.Id}";
        using var before = await client.GetAsync(url);
        before.EnsureSuccessStatusCode();
        using (var body = JsonDocument.Parse(await before.Content.ReadAsByteArrayAsync()))
            Assert.True(body.RootElement.GetProperty("blocking").GetProperty("dependencyBlocked").GetBoolean());

        using (var released = await client.PutAsJsonAsync(
            $"/api/tasks/AGT-target/release?project={project.Id}", new { released = true }))
            released.EnsureSuccessStatusCode();
        var scansBefore = index.Misses;
        using var after = await client.GetAsync(url);
        after.EnsureSuccessStatusCode();
        using var current = JsonDocument.Parse(await after.Content.ReadAsByteArrayAsync());
        var blocking = current.RootElement.GetProperty("blocking");
        Assert.Equal("ready", blocking.GetProperty("dependencyState").GetString());
        Assert.False(blocking.GetProperty("dependencyBlocked").GetBoolean());
        Assert.True(blocking.GetProperty("dependencies")[0].GetProperty("fulfilled").GetBoolean());
        Assert.NotEqual(before.Headers.ETag, after.Headers.ETag);
        Assert.Equal(scansBefore, index.Misses);
    }

    private sealed class StubRuntime : ITaskCoreRuntime
    {
        public TaskCoreRuntimeSnapshot Read(TaskCoreRecord core) =>
            new();
    }

    [Fact]
    public async Task InjectedRequestGitEvent_IsVisibleToTheCoreBudgetCounter()
    {
        Seed("AGT-core", TaskStates.Ready);
        await using var factory = Factory().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.AddSingleton<ITaskCoreRuntime>(new GitEventRuntime())));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        var project = factory.Services.GetRequiredService<ProjectRegistry>().FindByStorageLocation(Jobs)!;
        factory.Services.GetRequiredService<TaskIndexCache>().ForceRefresh();
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/tasks/AGT-core/core?project={project.Id}");
        request.Headers.Add("X-Task-Switch-Trace", "1");
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        Assert.Equal("1", response.Headers.GetValues("X-Task-Core-Git-Spawns").Single());
    }

    private sealed class GitEventRuntime : ITaskCoreRuntime
    {
        public TaskCoreRuntimeSnapshot Read(TaskCoreRecord core)
        {
            GitProcessTelemetry.Record("injected-core-request", 1, 0);
            return new();
        }
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task WarmCore_HandlerP95_OnProductionShapedSnapshot()
    {
        var source = Environment.GetEnvironmentVariable("TASK_CORE_BENCH_FIXTURE");
        Assert.True(!string.IsNullOrWhiteSpace(source) && Directory.Exists(source),
            "TASK_CORE_BENCH_FIXTURE must name a directory of captured task folders.");
        Directory.CreateDirectory(Jobs);
        var sources = Directory.GetFiles(source!, "task.json", SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName).Where(path => path is not null).Cast<string>().ToArray();
        Assert.NotEmpty(sources);
        var keys = new List<string>();
        foreach (var folder in sources)
        {
            using var task = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, "task.json")));
            var root = task.RootElement;
            var id = root.GetProperty("id").GetString()!;
            var state = root.GetProperty("state").GetString()!;
            var key = root.TryGetProperty("key", out var property) ? property.GetString() : null;
            keys.Add(string.IsNullOrWhiteSpace(key) ? id : key);
            CopyDirectory(folder, Path.Combine(Jobs, state, id));
        }
        await using var factory = Factory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        var project = factory.Services.GetRequiredService<ProjectRegistry>().FindByStorageLocation(Jobs);
        Assert.NotNull(project);
        factory.Services.GetRequiredService<TaskIndexCache>().ForceRefresh();
        var urls = keys.Select(key =>
            $"/api/tasks/{Uri.EscapeDataString(key)}/core?project={project!.Id}").ToArray();
        foreach (var url in urls)
            using (var warm = await client.GetAsync(url)) warm.EnsureSuccessStatusCode();

        var timings = new List<double>();
        var roundTrips = new List<double>();
        var tracedRoundTrips = new List<double>();
        var maxBytes = 0;
        for (var i = 0; i < 100; i++)
        {
            var url = urls[i % urls.Length];
            var sw = Stopwatch.StartNew();
            using var response = await client.GetAsync(url);
            var bytes = await response.Content.ReadAsByteArrayAsync();
            sw.Stop();
            response.EnsureSuccessStatusCode();
            var timing = Assert.Single(response.Headers.GetValues("Server-Timing")
                .SelectMany(value => value.Split(','))
                .Select(value => value.Trim()),
                value => value.StartsWith("task-core;dur=", StringComparison.Ordinal));
            timings.Add(double.Parse(timing.Split("dur=", 2)[1], System.Globalization.CultureInfo.InvariantCulture));
            roundTrips.Add(sw.Elapsed.TotalMilliseconds);
            maxBytes = Math.Max(maxBytes, bytes.Length);
            using var tracedRequest = new HttpRequestMessage(HttpMethod.Get, url);
            tracedRequest.Headers.Add("X-Task-Switch-Trace", "1");
            tracedRequest.Headers.Add("X-Task-Switch-Id", Guid.NewGuid().ToString("N"));
            tracedRequest.Headers.Add("X-Task-Request-Id", Guid.NewGuid().ToString("N"));
            var tracedStopwatch = Stopwatch.StartNew();
            using var tracedResponse = await client.SendAsync(tracedRequest);
            await tracedResponse.Content.LoadIntoBufferAsync();
            tracedStopwatch.Stop();
            tracedResponse.EnsureSuccessStatusCode();
            tracedRoundTrips.Add(tracedStopwatch.Elapsed.TotalMilliseconds);
        }
        timings.Sort();
        roundTrips.Sort();
        tracedRoundTrips.Sort();
        static double Quantile(List<double> sorted, double p)
        {
            var rank = p * (sorted.Count - 1);
            var low = (int)Math.Floor(rank);
            var high = (int)Math.Ceiling(rank);
            return sorted[low] + (sorted[high] - sorted[low]) * (rank - low);
        }
        var handlerP95 = Quantile(timings, 0.95);
        var traceOverheadP95 = Quantile(tracedRoundTrips, 0.95) - Quantile(roundTrips, 0.95);
        if (Environment.GetEnvironmentVariable("TASK_CORE_BENCH_REPORT") is { Length: > 0 } report)
            File.WriteAllText(report, JsonSerializer.Serialize(new
            {
                fixtureTaskKeys = keys,
                samples = timings.Count,
                handlerP50Ms = Quantile(timings, 0.5),
                handlerP95Ms = handlerP95,
                httpRoundTripP95Ms = Quantile(roundTrips, 0.95),
                tracedHttpRoundTripP95Ms = Quantile(tracedRoundTrips, 0.95),
                traceOverheadP95Ms = traceOverheadP95,
                maxUtf8Bytes = maxBytes,
            }, new JsonSerializerOptions { WriteIndented = true }));
        Assert.True(maxBytes <= 16 * 1024, $"Core response was {maxBytes} bytes.");
        Assert.True(handlerP95 <= 30,
            $"Core handler p95 was {handlerP95:F1} ms; HTTP round-trip p95 was {Quantile(roundTrips, 0.95):F1} ms.");
        Assert.True(traceOverheadP95 <= 1,
            $"Opt-in trace added {traceOverheadP95:F3} ms to HTTP round-trip p95 on this host.");
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

    private void Seed(string id, string state, string? gatedTarget = null)
    {
        var folder = Path.Combine(Jobs, state, id);
        Directory.CreateDirectory(Path.Combine(folder, "logs"));
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
        {
            id, key = id, title = "Task core contract", state, order = 10,
            enteredLaneAt = DateTime.UtcNow,
            mode = "coding", modelExplicit = true, thinkingLevelExplicit = true,
            references = new
            {
                dependsOn = gatedTarget is null ? Array.Empty<object>() :
                    new object[] { new { key = gatedTarget, releaseGate = true } },
            },
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
