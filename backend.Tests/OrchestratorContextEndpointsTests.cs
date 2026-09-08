using System.Net;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

using Xunit;

namespace AgentStudio.Tests;

public sealed class OrchestratorContextEndpointsTests : IDisposable
{
    private const string Alpha = "alpha-scope";
    private const string Beta = "beta-secret";

    private readonly string _root;
    private readonly string _alphaRoot;
    private readonly string _betaRoot;
    private readonly string _alphaCodeRoot;
    private readonly string _betaCodeRoot;

    public OrchestratorContextEndpointsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atp-orch-context-endpoints-" + Guid.NewGuid().ToString("N"));
        _alphaRoot = Path.Combine(_root, "projects", Alpha);
        _betaRoot = Path.Combine(_root, "projects", Beta);
        _alphaCodeRoot = Path.Combine(_root, "code", Alpha);
        _betaCodeRoot = Path.Combine(_root, "code", Beta);

        PrepareProject(_alphaRoot);
        PrepareProject(_betaRoot);
        Directory.CreateDirectory(_alphaCodeRoot);
        Directory.CreateDirectory(_betaCodeRoot);

        WriteTask(_alphaRoot, "alpha-one", "ALPHA-1", "Alpha visible task");
        WriteTask(_alphaRoot, "alpha-two", "ALPHA-2", "Alpha sibling task");
        WriteTask(_betaRoot, "beta-one", "BETA-1", "Beta secret task");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task Get_Global_ReturnsEnvelopeWithEveryProject()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/orchestrator/context/global");

        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;
        Assert.Equal("global", root.GetProperty("contextKey").GetString());
        Assert.True(root.GetProperty("capturedAt").TryGetDateTime(out _));

        var digest = root.GetProperty("digest").GetString();
        Assert.NotNull(digest);
        Assert.Contains($"- {Alpha}: total=2", digest, StringComparison.Ordinal);
        Assert.Contains($"- {Beta}: total=1", digest, StringComparison.Ordinal);

        var sources = root.GetProperty("sources").EnumerateArray().ToList();
        Assert.Equal(7, sources.Count);
        Assert.Contains(sources, source => source.GetProperty("name").GetString() == "lanes");
        Assert.Contains(sources, source => source.GetProperty("name").GetString() == "health");
    }

    [Fact]
    public async Task Get_ProjectAndTask_IsolateScopeAndTaskContextCarriesFocus()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var projectResponse = await client.GetAsync($"/api/orchestrator/context/project:{Alpha}");
        projectResponse.EnsureSuccessStatusCode();
        using (var projectBody = JsonDocument.Parse(await projectResponse.Content.ReadAsStringAsync()))
        {
            var projectRoot = projectBody.RootElement;
            Assert.Equal($"project:{Alpha}", projectRoot.GetProperty("contextKey").GetString());
            var digest = projectRoot.GetProperty("digest").GetString();
            Assert.NotNull(digest);
            Assert.Contains($"- {Alpha}: total=2", digest, StringComparison.Ordinal);
            Assert.DoesNotContain(Beta, digest, StringComparison.Ordinal);
            Assert.DoesNotContain("task focus:", digest, StringComparison.Ordinal);
        }

        using var taskResponse = await client.GetAsync($"/api/orchestrator/context/task:{Alpha}/ALPHA-1");
        taskResponse.EnsureSuccessStatusCode();
        using var taskBody = JsonDocument.Parse(await taskResponse.Content.ReadAsStringAsync());
        var taskRoot = taskBody.RootElement;
        Assert.Equal($"task:{Alpha}/ALPHA-1", taskRoot.GetProperty("contextKey").GetString());
        var taskDigest = taskRoot.GetProperty("digest").GetString();
        Assert.NotNull(taskDigest);
        Assert.Contains("task focus:", taskDigest, StringComparison.Ordinal);
        Assert.Contains($"- {Alpha}/ALPHA-1: Alpha visible task", taskDigest, StringComparison.Ordinal);
        Assert.Contains($"- {Alpha}: total=2", taskDigest, StringComparison.Ordinal);
        Assert.DoesNotContain(Beta, taskDigest, StringComparison.Ordinal);
        Assert.DoesNotContain("Beta secret task", taskDigest, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_UnknownProjectAndUnknownTask_Return404()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var projectResponse = await client.GetAsync("/api/orchestrator/context/project:does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, projectResponse.StatusCode);

        using var taskResponse = await client.GetAsync($"/api/orchestrator/context/task:{Alpha}/MISSING-9");
        Assert.Equal(HttpStatusCode.NotFound, taskResponse.StatusCode);
    }

    [Fact]
    public async Task Get_Workbench_ReturnsDossierOnlyDigestWithoutBoardOrTaskState()
    {
        WriteWorkbench(_alphaCodeRoot, "runner-link", "Runner Link", "AGT-W43");
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/api/orchestrator/context/workbench:{Alpha}/AGT-W43");
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;
        Assert.Equal($"workbench:{Alpha}/AGT-W43", root.GetProperty("contextKey").GetString());

        var digest = root.GetProperty("digest").GetString();
        Assert.NotNull(digest);
        Assert.Contains("Key: AGT-W43", digest, StringComparison.Ordinal);
        Assert.Contains("Title: Runner Link", digest, StringComparison.Ordinal);
        // No board/task digest sections leak into a Dossier-scoped read.
        Assert.DoesNotContain("lanes:", digest, StringComparison.Ordinal);
        Assert.DoesNotContain("task focus:", digest, StringComparison.Ordinal);
        Assert.DoesNotContain(Beta, digest, StringComparison.Ordinal);

        var source = Assert.Single(root.GetProperty("sources").EnumerateArray());
        Assert.Equal("dossier", source.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Get_UnknownWorkbenchKey_Returns404()
    {
        WriteWorkbench(_alphaCodeRoot, "runner-link", "Runner Link", "AGT-W43");
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/api/orchestrator/context/workbench:{Alpha}/AGT-W404");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static void WriteWorkbench(string codeRoot, string id, string title, string key)
    {
        var dir = Path.Combine(codeRoot, "docs", "workbenches", id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "index.html"), $"<h1>{title}</h1>");
        File.WriteAllText(Path.Combine(dir, "workbench.json"), $$"""
          {"schemaVersion":1,"id":"{{id}}","title":"{{title}}","summary":"Question","entrypoint":"index.html","status":"decided","phase":"testing","updatedAt":"2026-08-17T00:00:00Z","key":"{{key}}"}
          """);
    }

    private static void PrepareProject(string projectRoot)
    {
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(projectRoot, state));
    }

    private static void WriteTask(string projectRoot, string id, string key, string title)
    {
        var dir = Path.Combine(projectRoot, TaskStates.Backlog, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "task.json"), JsonSerializer.Serialize(new
        {
            id,
            key,
            title,
            state = TaskStates.Backlog,
            order = 1,
            agent = "codex",
            cliType = "codex",
            ownerClientId = "local-default",
        }));
    }

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TaskRepository"] = _root,
                    ["WatchPaths:0:Name"] = Alpha,
                    ["WatchPaths:0:Path"] = _alphaRoot,
                    ["WatchPaths:0:RootPath"] = _alphaCodeRoot,
                    ["WatchPaths:0:RepositoryPath"] = _alphaCodeRoot,
                    ["WatchPaths:1:Name"] = Beta,
                    ["WatchPaths:1:Path"] = _betaRoot,
                    ["WatchPaths:1:RootPath"] = _betaCodeRoot,
                    ["WatchPaths:1:RepositoryPath"] = _betaCodeRoot,
                });
            });
        });
}
