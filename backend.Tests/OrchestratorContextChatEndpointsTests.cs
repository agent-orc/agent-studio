using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Orchestrator;
using AgentStudio.Runner;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// MC-2 (Concept §4): per-context transcript history. The side sheet's
/// context follows navigation — the board yields a <c>project:&lt;PROJ&gt;</c>
/// context, a task page a <c>task:&lt;PROJ&gt;/&lt;KEY&gt;</c> one — and the new
/// <c>GET /api/runner/{contextKey}/orchestrator-chat</c> endpoint returns the
/// transcript for exactly that context.
///
/// Two layers are pinned here:
///
///   1. Legacy JSONL context paths remain readable migration inputs while the
///      active endpoint persistence boundary is a central-store test double.
///   2. The literal-prefixed runner routes resolve without colliding with the
///      pre-existing <c>{projectName}/orchestrator-chat</c> route and return
///      the correct per-context turns.
/// </summary>
public sealed class OrchestratorContextChatEndpointsTests : IDisposable
{
    private const string Project = "agent-taskboard";

    private readonly string _root;
    private readonly string _projectRoot;
    private readonly string _codeRoot;
    private readonly CentralOrchestratorChatPersistenceStub _central = new();

    public OrchestratorContextChatEndpointsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atp-ctx-chat-" + Guid.NewGuid().ToString("N"));
        _projectRoot = Path.Combine(_root, "projects", Project);
        _codeRoot = Path.Combine(_root, "code");
        Directory.CreateDirectory(_projectRoot);
        Directory.CreateDirectory(_codeRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    // ---- store layer -------------------------------------------------------

    [Fact]
    public void ResolveContextPath_TaskGetsOwnFile_ProjectAndNullShareLegacyFile()
    {
        var legacy = Path.Combine(_projectRoot, ".orchestrator", "orchestrator-chat.jsonl");

        OrchestratorContextKey.TryParse("project:" + Project, out var project);
        OrchestratorContextKey.TryParse($"task:{Project}/AGT-1930", out var task);

        Assert.Equal(legacy, OrchestratorChat.ResolveContextPath(_projectRoot, context: null));
        Assert.Equal(legacy, OrchestratorChat.ResolveContextPath(_projectRoot, project));
        Assert.Equal(legacy, OrchestratorChat.ResolveContextPath(_projectRoot, OrchestratorContextKey.Global));

        var taskPath = OrchestratorChat.ResolveContextPath(_projectRoot, task);
        Assert.NotEqual(legacy, taskPath);
        Assert.Contains(Path.Combine(".orchestrator", "context-chats"), taskPath, StringComparison.Ordinal);
        // Reversible, filesystem-safe encoding: no colon / slash in the leaf.
        Assert.EndsWith(task!.Encode() + ".jsonl", taskPath, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveContextPath_WorkbenchGetsOwnFile_DistinctFromTaskAndProject()
    {
        OrchestratorContextKey.TryParse($"task:{Project}/AGT-1930", out var task);
        OrchestratorContextKey.TryParse($"workbench:{Project}/AGT-W43", out var workbench);

        var workbenchPath = OrchestratorChat.ResolveContextPath(_projectRoot, workbench);
        var taskPath = OrchestratorChat.ResolveContextPath(_projectRoot, task);

        Assert.Contains(Path.Combine(".orchestrator", "context-chats"), workbenchPath, StringComparison.Ordinal);
        Assert.EndsWith(workbench!.Encode() + ".jsonl", workbenchPath, StringComparison.Ordinal);
        Assert.NotEqual(taskPath, workbenchPath);
    }

    [Fact]
    public void Append_KeepsTaskAndProjectTranscriptsIsolated()
    {
        var chat = new OrchestratorChat(NullLogger<OrchestratorChat>.Instance);
        OrchestratorContextKey.TryParse("project:" + Project, out var project);
        OrchestratorContextKey.TryParse($"task:{Project}/AGT-1930", out var task);

        Assert.True(chat.Append(_projectRoot, Turn("user", "board question"), project));
        Assert.True(chat.Append(_projectRoot, Turn("orchestrator", "board answer"), project));
        Assert.True(chat.Append(_projectRoot, Turn("user", "task-only question"), task));

        var boardTurns = chat.Read(_projectRoot, project);
        var taskTurns = chat.Read(_projectRoot, task);

        Assert.Equal(new[] { "board question", "board answer" }, boardTurns.Select(t => t.Text));
        Assert.Equal(new[] { "task-only question" }, taskTurns.Select(t => t.Text));

        // The legacy context-free read is the project thread — unchanged.
        Assert.Equal(boardTurns.Select(t => t.Text), chat.Read(_projectRoot).Select(t => t.Text));
    }

    [Fact]
    public void Append_KeepsWorkbenchTranscriptIsolatedFromProjectAndTask()
    {
        var chat = new OrchestratorChat(NullLogger<OrchestratorChat>.Instance);
        OrchestratorContextKey.TryParse("project:" + Project, out var project);
        OrchestratorContextKey.TryParse($"task:{Project}/AGT-1930", out var task);
        OrchestratorContextKey.TryParse($"workbench:{Project}/AGT-W43", out var workbench);

        Assert.True(chat.Append(_projectRoot, Turn("user", "board question"), project));
        Assert.True(chat.Append(_projectRoot, Turn("user", "task-only question"), task));
        Assert.True(chat.Append(_projectRoot, Turn("user", "what does this Dossier decide?"), workbench));

        Assert.Equal(new[] { "board question" }, chat.Read(_projectRoot, project).Select(t => t.Text));
        Assert.Equal(new[] { "task-only question" }, chat.Read(_projectRoot, task).Select(t => t.Text));
        Assert.Equal(
            new[] { "what does this Dossier decide?" },
            chat.Read(_projectRoot, workbench).Select(t => t.Text));
        // The legacy context-free (board) read never sees the Dossier turn.
        Assert.Equal(new[] { "board question" }, chat.Read(_projectRoot).Select(t => t.Text));
    }

    // ---- endpoint layer ----------------------------------------------------

    [Fact]
    public async Task Get_ReturnsPerContextTranscript_ForProjectAndTaskContexts()
    {
        SeedTranscript("project:" + Project, ("user", "on the board"), ("orchestrator", "board reply"));
        SeedTranscript($"task:{Project}/AGT-1930", ("user", "on the task"));

        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var projectResp = await client.GetAsync($"/api/runner/project:{Project}/orchestrator-chat");
        projectResp.EnsureSuccessStatusCode();
        using (var doc = JsonDocument.Parse(await projectResp.Content.ReadAsStringAsync()))
        {
            Assert.Equal("project:" + Project, doc.RootElement.GetProperty("contextKey").GetString());
            Assert.Equal(Project, doc.RootElement.GetProperty("project").GetString());
            var texts = doc.RootElement.GetProperty("turns").EnumerateArray()
                .Select(t => t.GetProperty("text").GetString()).ToArray();
            Assert.Equal(new[] { "on the board", "board reply" }, texts);
        }

        using var taskResp = await client.GetAsync($"/api/runner/task:{Project}/AGT-1930/orchestrator-chat");
        taskResp.EnsureSuccessStatusCode();
        using (var doc = JsonDocument.Parse(await taskResp.Content.ReadAsStringAsync()))
        {
            Assert.Equal($"task:{Project}/AGT-1930", doc.RootElement.GetProperty("contextKey").GetString());
            var texts = doc.RootElement.GetProperty("turns").EnumerateArray()
                .Select(t => t.GetProperty("text").GetString()).ToArray();
            // The task context sees only its own thread, not the board's.
            Assert.Equal(new[] { "on the task" }, texts);
        }
    }

    [Fact]
    public async Task Get_TaskContext_IsEmpty_WhenNothingWrittenYet()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var resp = await client.GetAsync($"/api/runner/task:{Project}/AGT-2001/orchestrator-chat");
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Empty(doc.RootElement.GetProperty("turns").EnumerateArray());
        var execution = doc.RootElement.GetProperty("executionContext");
        Assert.Equal("local", execution.GetProperty("executionKind").GetString());
        Assert.Equal("local", execution.GetProperty("hostName").GetString());
        Assert.Equal(_codeRoot, execution.GetProperty("repoPath").GetString());
    }

    [Fact]
    public async Task Get_RemoteProject_QueuesAssignedRunnerAndReturnsItsExactCheckoutContext()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var registry = factory.Services.GetRequiredService<ProjectRegistry>();
        var settings = factory.Services.GetRequiredService<ProjectSettingsService>();
        var broker = factory.Services.GetRequiredService<RemoteChatWorkBroker>();
        var project = registry.FindByStorageLocation(_projectRoot)
                      ?? registry.EnsureProjectForStorage(
                          _projectRoot,
                          Project,
                          "default");
        registry.AddUrl(project.Id, "repo", "https://git.example.invalid/agent-studio.git");
        settings.SetExecutionRunner(Project, "runner-01", remoteExecutionEnabled: true);

        using (var resolvingResponse =
               await client.GetAsync($"/api/runner/project:{Project}/orchestrator-chat"))
        {
            resolvingResponse.EnsureSuccessStatusCode();
            using var resolving = JsonDocument.Parse(
                await resolvingResponse.Content.ReadAsStringAsync());
            var execution = resolving.RootElement.GetProperty("executionContext");
            Assert.Equal("remote", execution.GetProperty("executionKind").GetString());
            Assert.Equal("runner-01", execution.GetProperty("hostName").GetString());
            Assert.Equal("resolving", execution.GetProperty("state").GetString());
        }

        var claim = broker.TryClaim(new RemoteChatWorkClaimRequest(
            "runner-01", "agent-runner-01", "agent-runner-01"));
        Assert.Equal(RemoteChatWorkClaimStatuses.Claimed, claim.Status);
        Assert.Equal("https://git.example.invalid/agent-studio.git", claim.Work?.RepositoryUrl);

        var hostContext = new ChatExecutionContext(
            "remote",
            "agent-runner-01",
            "/srv/agent-runner/work/PROJ-002/project-chat",
            "develop",
            "0123456789abcdef0123456789abcdef01234567",
            "ready",
            DateTime.UtcNow);
        Assert.True(broker.Complete(new RemoteChatWorkCompletionRequest(
            claim.Work!.WorkId,
            claim.Work.ClaimToken,
            "runner-01",
            true,
            "",
            null,
            null,
            null,
            hostContext)));

        using var readyResponse =
            await client.GetAsync($"/api/runner/project:{Project}/orchestrator-chat");
        readyResponse.EnsureSuccessStatusCode();
        using var ready = JsonDocument.Parse(await readyResponse.Content.ReadAsStringAsync());
        var reported = ready.RootElement.GetProperty("executionContext");
        Assert.Equal("agent-runner-01", reported.GetProperty("hostName").GetString());
        Assert.Equal(hostContext.RepoPath, reported.GetProperty("repoPath").GetString());
        Assert.Equal(hostContext.Branch, reported.GetProperty("branch").GetString());
        Assert.Equal(hostContext.HeadSha, reported.GetProperty("headSha").GetString());
        Assert.Equal("ready", reported.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Get_WorkbenchContext_IsIsolatedFromTheProjectThread()
    {
        SeedTranscript("project:" + Project, ("user", "on the board"), ("orchestrator", "board reply"));
        SeedTranscript($"workbench:{Project}/AGT-W43", ("user", "what does this Dossier decide?"));

        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var workbenchResp = await client.GetAsync($"/api/runner/workbench:{Project}/AGT-W43/orchestrator-chat");
        workbenchResp.EnsureSuccessStatusCode();
        using (var doc = JsonDocument.Parse(await workbenchResp.Content.ReadAsStringAsync()))
        {
            Assert.Equal($"workbench:{Project}/AGT-W43", doc.RootElement.GetProperty("contextKey").GetString());
            var texts = doc.RootElement.GetProperty("turns").EnumerateArray()
                .Select(t => t.GetProperty("text").GetString()).ToArray();
            // The Dossier context sees only its own thread, not the board's.
            Assert.Equal(new[] { "what does this Dossier decide?" }, texts);
        }

        using var projectResp = await client.GetAsync($"/api/runner/project:{Project}/orchestrator-chat");
        projectResp.EnsureSuccessStatusCode();
        using var projectDoc = JsonDocument.Parse(await projectResp.Content.ReadAsStringAsync());
        var projectTexts = projectDoc.RootElement.GetProperty("turns").EnumerateArray()
            .Select(t => t.GetProperty("text").GetString()).ToArray();
        // A Dossier turn must never leak back into the project thread.
        Assert.Equal(new[] { "on the board", "board reply" }, projectTexts);
    }

    [Fact]
    public async Task Get_WorkbenchContext_IsEmpty_OnFirstVisit()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var resp = await client.GetAsync($"/api/runner/workbench:{Project}/AGT-W99/orchestrator-chat");
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Empty(doc.RootElement.GetProperty("turns").EnumerateArray());
    }

    [Fact]
    public async Task Get_UnknownProject_Returns404()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var resp = await client.GetAsync("/api/runner/project:does-not-exist/orchestrator-chat");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ---- helpers -----------------------------------------------------------

    private static OrchestratorChatTurn Turn(string role, string text) =>
        new() { Role = role, Text = text };

    private void SeedTranscript(string rawContextKey, params (string Role, string Text)[] turns)
    {
        Assert.True(OrchestratorContextKey.TryParse(rawContextKey, out var key));
        _central.SeedContext(
            key.Value,
            turns.FirstOrDefault(item => item.Role == OrchestratorChatRoles.User).Text
                ?? key.TaskKey
                ?? key.ProjectId!,
            [.. turns.Select(item => Turn(item.Role, item.Text))]);
    }

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TaskRepository"] = _root,
                    ["WatchPaths:0:Name"] = Project,
                    ["WatchPaths:0:Path"] = _projectRoot,
                    ["WatchPaths:0:RootPath"] = _codeRoot,
                    ["WatchPaths:0:RepositoryPath"] = _codeRoot,
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IOrchestratorChatPersistence>();
                services.AddSingleton<IOrchestratorChatPersistence>(_central);
            });
        });
}
