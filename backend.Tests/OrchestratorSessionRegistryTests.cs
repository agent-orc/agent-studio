using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class OrchestratorSessionRegistryTests : IDisposable
{
    private readonly string _root;
    private readonly IConfiguration _config;
    private readonly CentralOrchestratorChatPersistenceStub _central = new();

    public OrchestratorSessionRegistryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atp-orch-sessions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _root })
            .Build();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Theory]
    [InlineData("global", "global", "global", null, null)]
    [InlineData("project:PROJ-001", "project~3APROJ-001", "project", "PROJ-001", null)]
    [InlineData("task:PROJ-001/AGT-1930", "task~3APROJ-001~2FAGT-1930", "task", "PROJ-001", "AGT-1930")]
    public void GetOrCreate_PersistsSessionJsonAndHistory(string raw, string encoded, string kind, string? projectId, string? taskKey)
    {
        var registry = Build();

        var record = registry.GetOrCreate(raw);

        Assert.Equal(raw, record.ContextKey);
        Assert.Equal(encoded, record.EncodedKey);
        Assert.Equal(kind, record.Kind);
        Assert.Equal(projectId, record.ProjectId);
        Assert.Equal(taskKey, record.TaskKey);
        Assert.Null(record.SessionId);
        Assert.True(File.Exists(Path.Combine(_root, ".metadata", "orchestrator-sessions", encoded, "session.json")));
        Assert.True(File.Exists(Path.Combine(_root, ".metadata", "orchestrator-sessions", encoded, "history.jsonl")));
    }

    [Fact]
    public void GetOrCreate_IsLazyAndIdempotent()
    {
        var registry = Build();
        var first = registry.GetOrCreate("project:PROJ-123");
        var second = registry.GetOrCreate("project:PROJ-123");

        Assert.Equal(first.CreatedAt, second.CreatedAt);
        Assert.Single(registry.List(), s => s.ContextKey == "project:PROJ-123");
    }

    [Fact]
    public void List_EnsuresGlobalSessionAndMigratesLegacyGlobalStore()
    {
        var legacy = new GlobalOrchestratorSessionStore(_config, NullLogger<GlobalOrchestratorSessionStore>.Instance);
        legacy.Write(new GlobalOrchestratorSession(
            SessionId: "claude-global-1",
            Model: "claude-test",
            BootedAt: new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            BootPromptPreview: "boot",
            BootReplyPreview: "reply",
            CumulativeInputTokens: 11,
            CumulativeOutputTokens: 22,
            CumulativeCacheReadTokens: 33,
            CumulativeCacheCreationTokens: 44,
            Calls: 2,
            LastUsedAt: new DateTime(2026, 1, 3, 3, 4, 5, DateTimeKind.Utc),
            LastError: null));

        var registry = Build();
        var global = Assert.Single(registry.List(), s => s.ContextKey == "global");

        Assert.Equal("claude-global-1", global.SessionId);
        Assert.Equal("claude-test", global.Model);
        Assert.Equal(11, global.CumulativeInputTokens);
        Assert.True(File.Exists(Path.Combine(_root, ".metadata", "orchestrator-sessions", "global", "session.json")));
    }

    [Fact]
    public async Task Endpoints_ListAndGetOrCreateByContextKey()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var created = await client.GetAsync("/api/orchestrator/sessions/task:PROJ-001/AGT-1930");
        created.EnsureSuccessStatusCode();
        using (var doc = JsonDocument.Parse(await created.Content.ReadAsStringAsync()))
        {
            Assert.Equal("task:PROJ-001/AGT-1930", doc.RootElement.GetProperty("contextKey").GetString());
            Assert.Equal("task~3APROJ-001~2FAGT-1930", doc.RootElement.GetProperty("encodedKey").GetString());
        }

        _central.SeedContext("project:PROJ-001", "Project context");
        _central.SeedContext("task:PROJ-001/AGT-1930", "Task context");

        using var list = await client.GetAsync("/api/orchestrator/sessions");
        list.EnsureSuccessStatusCode();
        using (var doc = JsonDocument.Parse(await list.Content.ReadAsStringAsync()))
        {
            var sessions = doc.RootElement.GetProperty("sessions").EnumerateArray().ToList();
            Assert.Contains(sessions, s =>
                s.GetProperty("contextKey").GetString() == "project:PROJ-001"
                && s.GetProperty("summary").GetString() == "Project context");
            Assert.Contains(sessions, s => s.GetProperty("contextKey").GetString() == "task:PROJ-001/AGT-1930");
        }

        using var bad = await client.GetAsync("/api/orchestrator/sessions/task:PROJ-001/bad/path");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Sessions_endpoint_without_Task_Server_configuration_uses_local_context_store()
    {
        using var factory = CreateFactory(
            new BlockingFakeOrchestratorRunner(block: false),
            useCentralStore: false);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/orchestrator/sessions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var sessions = document.RootElement.GetProperty("sessions").EnumerateArray().ToList();
        Assert.Contains(sessions, session =>
            session.GetProperty("contextKey").GetString() == "project:agent-taskboard");
    }

    [Fact]
    public async Task PostTurn_UsesActiveCapAndReportsQueuedPosition()
    {
        var runner = new BlockingFakeOrchestratorRunner();
        using var factory = CreateFactory(runner, new Dictionary<string, string?>
        {
            ["Orchestrator:SessionTurns:ActiveLimit"] = "1"
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");

        using var first = await client.PostAsJsonAsync("/api/orchestrator/sessions/project:PROJ-001/turns", new { prompt = "first" });
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        using var firstDoc = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        Assert.Equal("active", firstDoc.RootElement.GetProperty("status").GetString());
        Assert.Equal(1, firstDoc.RootElement.GetProperty("activeCount").GetInt32());

        using var second = await client.PostAsJsonAsync("/api/orchestrator/sessions/project:PROJ-001/turns", new { prompt = "second" });
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        using var secondDoc = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        Assert.Equal("queued", secondDoc.RootElement.GetProperty("status").GetString());
        Assert.Equal(1, secondDoc.RootElement.GetProperty("queuePosition").GetInt32());

        using var park = await client.PostAsync("/api/orchestrator/sessions/project:PROJ-001/park", content: null);
        park.EnsureSuccessStatusCode();
        using (var parkDoc = JsonDocument.Parse(await park.Content.ReadAsStringAsync()))
        {
            Assert.Equal("project:PROJ-001", parkDoc.RootElement.GetProperty("contextKey").GetString());
            Assert.True(parkDoc.RootElement.GetProperty("cancelledActiveTurn").GetBoolean());
            Assert.Equal(1, parkDoc.RootElement.GetProperty("parkedQueuedTurns").GetInt32());
        }
        runner.ReleaseAll();
    }

    [Fact]
    public async Task PostTurn_WithStoredSessionId_ResumesAndUpdatesSessionRecord()
    {
        var runner = new BlockingFakeOrchestratorRunner(block: false);
        using var factory = CreateFactory(runner);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");

        using var created = await client.GetAsync("/api/orchestrator/sessions/task:PROJ-001/AGT-1930");
        created.EnsureSuccessStatusCode();

        var registry = factory.Services.GetRequiredService<AgentStudio.Orchestrator.OrchestratorSessionRegistry>();
        registry.Update("task:PROJ-001/AGT-1930", r => r with
        {
            SessionId = "stored-session",
            Model = "test-model"
        });

        using var post = await client.PostAsJsonAsync("/api/orchestrator/sessions/task:PROJ-001/AGT-1930/turns", new { prompt = "continue" });
        Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);

        await WaitUntilAsync(() => runner.ResumeCalls == 1);
        await WaitUntilAsync(() => registry.GetOrCreate("task:PROJ-001/AGT-1930").Calls == 1);

        var record = registry.GetOrCreate("task:PROJ-001/AGT-1930");
        Assert.Equal("stored-session", runner.LastResumeSessionId);
        Assert.Equal("captured-session-1", record.SessionId);
        Assert.Equal(1, record.Calls);
        Assert.Equal(7, record.CumulativeInputTokens);
        Assert.Equal(3, record.CumulativeOutputTokens);
    }

    private OrchestratorSessionRegistry Build()
    {
        var legacy = new GlobalOrchestratorSessionStore(_config, NullLogger<GlobalOrchestratorSessionStore>.Instance);
        return new OrchestratorSessionRegistry(_config, legacy, NullLogger<OrchestratorSessionRegistry>.Instance);
    }

    private WebApplicationFactory<Program> CreateFactory(
        OrchestratorRunner? runner = null,
        Dictionary<string, string?>? extraConfig = null,
        bool useCentralStore = true)
    {
        var projectRoot = Path.Combine(_root, "projects", "agent-taskboard");
        var codeRoot = Path.Combine(_root, "code");
        Directory.CreateDirectory(projectRoot);
        Directory.CreateDirectory(codeRoot);

        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration((_, cfg) =>
                {
                    var values = new Dictionary<string, string?>
                    {
                        ["TaskRepository"] = _root,
                        ["WatchPaths:0:Name"] = "agent-taskboard",
                        ["WatchPaths:0:Path"] = projectRoot,
                        ["WatchPaths:0:RootPath"] = codeRoot,
                        ["WatchPaths:0:RepositoryPath"] = codeRoot,
                    };
                    if (extraConfig != null)
                    {
                        foreach (var pair in extraConfig)
                            values[pair.Key] = pair.Value;
                    }
                    cfg.AddInMemoryCollection(values);
                });
                if (useCentralStore)
                {
                    builder.ConfigureTestServices(services =>
                    {
                        services.RemoveAll<IOrchestratorChatPersistence>();
                        services.AddSingleton<IOrchestratorChatPersistence>(_central);
                    });
                }
                if (runner != null)
                {
                    builder.ConfigureTestServices(services =>
                    {
                        services.RemoveAll<OrchestratorRunner>();
                        services.AddSingleton(runner);
                    });
                }
            });
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
                return;
            await Task.Delay(25);
        }
        Assert.True(predicate(), "Timed out waiting for condition.");
    }

    private sealed class BlockingFakeOrchestratorRunner : OrchestratorRunner
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int DecideCalls { get; private set; }
        public int ResumeCalls { get; private set; }
        public string? LastResumeSessionId { get; private set; }

        public BlockingFakeOrchestratorRunner(bool block = true)
            : base(NullLogger<OrchestratorRunner>.Instance)
        {
            if (!block)
                _release.SetResult();
        }

        public void ReleaseAll() => _release.TrySetResult();

        public override async Task<OrchestratorDecisionResult> DecideAsync(
            string prompt,
            string? model,
            string workingDirectory,
            CancellationToken ct = default)
        {
            DecideCalls++;
            await _release.Task.WaitAsync(ct);
            return Result(model);
        }

        public override async Task<OrchestratorDecisionResult> ResumeAsync(
            string sessionId,
            string prompt,
            string? model,
            string workingDirectory,
            IReadOnlyList<CliOneShotImage>? inlineImages,
            CancellationToken ct = default)
        {
            ResumeCalls++;
            LastResumeSessionId = sessionId;
            await _release.Task.WaitAsync(ct);
            return Result(model);
        }

        private static OrchestratorDecisionResult Result(string? model) =>
            new(
                Success: true,
                ReplyText: "ok",
                Model: model ?? "test-model",
                TokenUsage: new OrchestratorTokenUsage
                {
                    Model = model ?? "test-model",
                    InputTokens = 7,
                    OutputTokens = 3,
                    CacheReadTokens = 2,
                    CacheCreationTokens = 1
                },
                CapturedSessionId: "captured-session-1",
                ErrorMessage: null);
    }
}
