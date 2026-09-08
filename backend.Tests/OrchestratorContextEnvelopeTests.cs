using System.Net;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class OrchestratorContextEnvelopeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "orchestrator-context-envelope-" + Guid.NewGuid().ToString("N"));
    private readonly string _watchPath;

    public OrchestratorContextEnvelopeTests()
    {
        _watchPath = Path.Combine(_root, "project-a");
        Directory.CreateDirectory(Path.Combine(_watchPath, "docs"));
        File.WriteAllText(
            Path.Combine(_watchPath, "docs", "proof.md"),
            "CENTRAL_CONTEXT_PROOF_BODY");
        File.WriteAllText(
            Path.Combine(_watchPath, "docs", "second-proof.md"),
            "SECOND_EXPLICIT_PROOF_BODY");
    }

    [Fact]
    public void Snapshot_UsesConversationRouteInsteadOfIncidentalNavigation()
    {
        Assert.True(OrchestratorContextKey.TryParse("project:project-a", out var route));
        var request = new SendOrchestratorChatRequest(
            "Question",
            null,
            new ChatNavigationContext(CurrentTaskKey: "A-2"));

        var result = OrchestratorContextEnvelopePolicy.Snapshot(
            "project-a", route, request, DateTime.UtcNow);

        Assert.Equal("project", result.Scope.Kind);
        Assert.Equal("project:project-a", result.Scope.ContextKey);
        Assert.Null(result.Scope.TaskKey);
    }

    [Fact]
    public void Snapshot_RejectsCrossProjectReferenceAndMismatchedActiveTask()
    {
        Assert.True(OrchestratorContextKey.TryParse("task:project-a/A-1", out var route));
        var capturedAt = new DateTime(2026, 8, 10, 10, 15, 0, DateTimeKind.Utc);
        var crossProject = Envelope(
            "task:project-a/A-1",
            "project-a",
            "A-1",
            capturedAt,
            [new OrchestratorContextReference("repository-file", "README.md", "project-b")]);

        var crossProjectError = Assert.Throws<OrchestratorContextEnvelopeException>(() =>
            OrchestratorContextEnvelopePolicy.Snapshot(
                "project-a", route,
                new SendOrchestratorChatRequest("Question", null, ContextEnvelope: crossProject),
                DateTime.UtcNow));
        Assert.Equal("context-reference-cross-project", crossProjectError.Code);

        var wrongTask = crossProject with
        {
            ExplicitReferences = [],
            ActiveSurface = new OrchestratorActiveSurface("task", "A-2", TaskKey: "A-2"),
        };
        var activeTaskError = Assert.Throws<OrchestratorContextEnvelopeException>(() =>
            OrchestratorContextEnvelopePolicy.Snapshot(
                "project-a", route,
                new SendOrchestratorChatRequest("Question", null, ContextEnvelope: wrongTask),
                DateTime.UtcNow));
        Assert.Equal("context-active-task-mismatch", activeTaskError.Code);
    }

    [Fact]
    public void Snapshot_PreservesSubmitTimestampAndEnforcesDossierBudgetCeilings()
    {
        Assert.True(OrchestratorContextKey.TryParse("project:project-a", out var route));
        var capturedAt = new DateTime(2026, 8, 10, 10, 15, 0, DateTimeKind.Utc);
        var envelope = Envelope("project:project-a", "project-a", null, capturedAt, []);
        var result = OrchestratorContextEnvelopePolicy.Snapshot(
            "project-a", route,
            new SendOrchestratorChatRequest("Question", null, ContextEnvelope: envelope),
            DateTime.UtcNow);
        Assert.Equal(capturedAt, result.CapturedAt);

        var invalid = envelope with
        {
            Budget = new OrchestratorContextBudget(4000, 6001, 8000, 4),
        };
        var error = Assert.Throws<OrchestratorContextEnvelopeException>(() =>
            OrchestratorContextEnvelopePolicy.Snapshot(
                "project-a", route,
                new SendOrchestratorChatRequest("Question", null, ContextEnvelope: invalid),
                DateTime.UtcNow));
        Assert.Equal("context-budget-invalid", error.Code);
    }

    [Fact]
    public void Snapshot_AcceptsProjectBoundCommitReferences()
    {
        Assert.True(OrchestratorContextKey.TryParse("project:project-a", out var route));
        var envelope = Envelope(
            route.Value,
            "project-a",
            null,
            DateTime.UtcNow,
            [new OrchestratorContextReference("commit", "commit:project-a/0123456789abcdef", "project-a")]);

        var result = OrchestratorContextEnvelopePolicy.Snapshot(
            "project-a", route,
            new SendOrchestratorChatRequest("Question", null, ContextEnvelope: envelope),
            DateTime.UtcNow);

        var reference = Assert.Single(result.ExplicitReferences);
        Assert.Equal("commit", reference.Kind);
        Assert.Equal("commit:project-a/0123456789abcdef", reference.Reference);
    }

    [Fact]
    public async Task SendAsync_AssemblesEveryStatelessTurnInDossierOrderAndPersistsLinkedReceipt()
    {
        var persistence = new MemoryPersistence([
            new OrchestratorChatTurn { Id = "prior_user", Role = "user", Text = "Earlier question" },
            new OrchestratorChatTurn { Id = "prior_reply", Role = "orchestrator", Text = "Earlier answer" },
        ]);
        var runner = new CapturingRunner();
        var service = BuildService(runner, persistence);
        Assert.True(OrchestratorContextKey.TryParse("task:project-a/A-1", out var context));
        var capturedAt = new DateTime(2026, 8, 10, 10, 15, 0, DateTimeKind.Utc);
        var envelope = Envelope(
            context.Value,
            "project-a",
            "A-1",
            capturedAt,
            [new OrchestratorContextReference("repository-file", "docs/proof.md", "project-a")]);
        var navigation = new ChatNavigationContext(
            CurrentPage: "task-detail",
            CurrentTaskKey: "A-1",
            CurrentTaskTitle: "Foundation");

        var reply = await service.SendAsync(
            "project-a",
            _watchPath,
            new SendOrchestratorChatRequest(
                "Current question",
                null,
                navigation,
                Model: "gpt-5.4-mini",
                ThinkingLevel: "low",
                ContextEnvelope: envelope),
            clientId: null,
            context,
            CancellationToken.None);

        Assert.Equal("Answer", reply.Text);
        var prompt = Assert.IsType<string>(runner.Prompt);
        Assert.True(Index(prompt, "=== SCOPED ORCHESTRATOR CHAT PREAMBLE ===")
                    < Index(prompt, "=== CONTEXT LEDGER ==="));
        Assert.True(Index(prompt, "=== CONTEXT LEDGER ===")
                    < Index(prompt, "=== AUTOMATIC EVIDENCE ==="));
        Assert.True(Index(prompt, "=== AUTOMATIC EVIDENCE ===")
                    < Index(prompt, "=== EXPLICIT ATTACHMENTS ==="));
        Assert.True(Index(prompt, "=== EXPLICIT ATTACHMENTS ===")
                    < Index(prompt, "=== CONVERSATION CONTINUITY ==="));
        Assert.True(Index(prompt, "=== CONVERSATION CONTINUITY ===")
                    < Index(prompt, "=== USER MESSAGE ==="));
        Assert.EndsWith("Current question", prompt, StringComparison.Ordinal);
        Assert.Contains("Earlier question", prompt);
        Assert.Contains("Earlier answer", prompt);
        Assert.Equal(1, Count(prompt, "CENTRAL_CONTEXT_PROOF_BODY"));
        Assert.DoesNotContain("all watched projects", prompt, StringComparison.OrdinalIgnoreCase);

        var storedUser = Assert.Single(
            persistence.Turns.Skip(2), turn => turn.Role == OrchestratorChatRoles.User);
        var storedReply = Assert.Single(
            persistence.Turns.Skip(2), turn => turn.Role == OrchestratorChatRoles.Orchestrator);
        var receipt = Assert.IsType<OrchestratorContextReceipt>(storedReply.ContextReceipt);
        Assert.Equal(storedUser.Id, receipt.UserTurnId);
        Assert.Equal(capturedAt, receipt.CapturedAt);
        Assert.Equal("task:project-a/A-1", receipt.ContextKey);
        Assert.Equal(8000, receipt.Budget?.TotalHardCapTokens);
        var fileSource = Assert.Single(
            receipt.Sources!, source => source.SourceId == "file:project-a/docs/proof.md");
        Assert.Equal("included", fileSource.Status);
        Assert.Equal(64, fileSource.Sha256?.Length);
        Assert.DoesNotContain(
            "CENTRAL_CONTEXT_PROOF_BODY",
            JsonSerializer.Serialize(receipt),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendAsync_BlocksTraversalBeforePersistingTheUserTurnOrInvokingTheModel()
    {
        var persistence = new MemoryPersistence([]);
        var runner = new CapturingRunner();
        var service = BuildService(runner, persistence);
        Assert.True(OrchestratorContextKey.TryParse("project:project-a", out var context));
        var envelope = Envelope(
            context.Value,
            "project-a",
            null,
            DateTime.UtcNow,
            [new OrchestratorContextReference("repository-file", "../outside.md", "project-a")]);

        var error = await Assert.ThrowsAsync<OrchestratorContextEnvelopeException>(() =>
            service.SendAsync(
                "project-a",
                _watchPath,
                new SendOrchestratorChatRequest(
                    "Question", null, Model: "gpt-5.4-mini", ContextEnvelope: envelope),
                clientId: null,
                context,
                CancellationToken.None));

        Assert.Equal("context-path-traversal", error.Code);
        Assert.Empty(persistence.Turns);
        Assert.Null(runner.Prompt);
    }

    [Fact]
    public async Task SendAsync_PrioritizesExplicitEvidenceAndReceiptsMissingFilesWithoutModelClaims()
    {
        var persistence = new MemoryPersistence([]);
        var runner = new CapturingRunner();
        var service = BuildService(runner, persistence);
        Assert.True(OrchestratorContextKey.TryParse("project:project-a", out var context));
        var baseEnvelope = Envelope(
            context.Value,
            "project-a",
            null,
            DateTime.UtcNow,
            [
                new OrchestratorContextReference("repository-file", "docs/proof.md", "project-a"),
                new OrchestratorContextReference("repository-file", "docs/missing.md", "project-a"),
            ]);
        var envelope = baseEnvelope with
        {
            Budget = new OrchestratorContextBudget(1, 1, 2, 4),
        };

        var reply = await service.SendAsync(
            "project-a",
            _watchPath,
            new SendOrchestratorChatRequest(
                "Question", null, Model: "gpt-5.4-mini", ContextEnvelope: envelope),
            clientId: null,
            context,
            CancellationToken.None);

        var sources = Assert.IsType<OrchestratorContextReceipt>(reply.ContextReceipt).Sources!;
        var explicitFile = Assert.Single(
            sources, source => source.SourceId == "file:project-a/docs/proof.md");
        Assert.Equal("excerpted", explicitFile.Status);
        Assert.Equal(8, explicitFile.IncludedCharacters);
        var missingFile = Assert.Single(
            sources, source => source.SourceId == "file:project-a/docs/missing.md");
        Assert.Equal("unresolved", missingFile.Status);
        Assert.Contains("does not exist", missingFile.Reason);
        Assert.All(
            sources.Where(source => source.Kind is "project-base" or "active-surface"),
            source => Assert.Equal(0, source.IncludedCharacters));
        Assert.Contains("status=unresolved", runner.Prompt);
    }

    [Fact]
    public async Task SendAsync_GivesEveryExplicitSourceAnExcerptOrAsksForNarrowerInput()
    {
        Assert.True(OrchestratorContextKey.TryParse("project:project-a", out var context));
        var references = new OrchestratorContextReference[]
        {
            new("repository-file", "docs/proof.md", "project-a"),
            new("repository-file", "docs/second-proof.md", "project-a"),
        };
        var persistence = new MemoryPersistence([]);
        var runner = new CapturingRunner();
        var service = BuildService(runner, persistence);
        var sharedExcerptEnvelope = Envelope(
            context.Value, "project-a", null, DateTime.UtcNow, references) with
        {
            Budget = new OrchestratorContextBudget(1, 1, 2, 4),
        };

        var reply = await service.SendAsync(
            "project-a",
            _watchPath,
            new SendOrchestratorChatRequest(
                "Question", null, Model: "gpt-5.4-mini", ContextEnvelope: sharedExcerptEnvelope),
            clientId: null,
            context,
            CancellationToken.None);

        var explicitSources = Assert.IsType<OrchestratorContextReceipt>(reply.ContextReceipt)
            .Sources!
            .Where(source => source.Kind == "repository-file")
            .ToArray();
        Assert.Equal(2, explicitSources.Length);
        Assert.All(explicitSources, source =>
        {
            Assert.Equal("excerpted", source.Status);
            Assert.Equal(4, source.IncludedCharacters);
        });
        Assert.Contains("CENT", runner.Prompt);
        Assert.Contains("SECO", runner.Prompt);

        var tooSmallPersistence = new MemoryPersistence([]);
        var tooSmallService = BuildService(new CapturingRunner(), tooSmallPersistence);
        var tooSmallEnvelope = sharedExcerptEnvelope with
        {
            Budget = new OrchestratorContextBudget(1, 1, 1, 1),
        };
        var error = await Assert.ThrowsAsync<OrchestratorContextEnvelopeException>(() =>
            tooSmallService.SendAsync(
                "project-a",
                _watchPath,
                new SendOrchestratorChatRequest(
                    "Question", null, Model: "gpt-5.4-mini", ContextEnvelope: tooSmallEnvelope),
                clientId: null,
                context,
                CancellationToken.None));
        Assert.Equal("context-explicit-budget-insufficient", error.Code);
        Assert.Empty(tooSmallPersistence.Turns);
    }

    [Fact]
    public async Task LegacyMigration_ReadsProjectJsonl_ImportsItCentrally_AndRetainsTheSourceFile()
    {
        var legacy = new OrchestratorChat(NullLogger<OrchestratorChat>.Instance);
        Assert.True(legacy.Append(_watchPath, new OrchestratorChatTurn
        {
            Id = "legacy_user",
            Role = OrchestratorChatRoles.User,
            Text = "Legacy context question",
        }));
        var sourcePath = OrchestratorChat.ResolveContextPath(_watchPath, context: null);
        var sourceBefore = await File.ReadAllTextAsync(sourcePath);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskServer:BaseUrl"] = "http://task-server.test",
                ["WatchPaths:0:Name"] = "project-a",
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _watchPath,
            })
            .Build();
        var summary = new SummaryGenerationService(
            NullLogger<SummaryGenerationService>.Instance,
            configuration);
        var scanner = new TaskScannerService(
            configuration,
            NullLogger<TaskScannerService>.Instance,
            summary);
        var handler = new LegacyImportHandler();
        var persistence = new TaskServerOrchestratorChatPersistence(
            new TestHttpClientFactory(handler));
        var migration = new OrchestratorChatLegacyMigrationHostedService(
            persistence,
            scanner,
            legacy,
            NullLogger<OrchestratorChatLegacyMigrationHostedService>.Instance);

        await migration.StartAsync(CancellationToken.None);
        var importBody = await handler.ImportBody.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await migration.StopAsync(CancellationToken.None);

        using var document = JsonDocument.Parse(importBody);
        Assert.Equal(64, document.RootElement.GetProperty("sourceSha256").GetString()?.Length);
        var importedTurn = Assert.Single(document.RootElement.GetProperty("turns").EnumerateArray());
        Assert.Equal("legacy_user", importedTurn.GetProperty("turnId").GetString());
        Assert.Equal(sourceBefore, await File.ReadAllTextAsync(sourcePath));
    }

    [Fact]
    public async Task LocalPersistence_RoundTripsProjectAndTaskContextsWithoutHTTP()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root,
                ["WatchPaths:0:Name"] = "project-a",
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _watchPath,
            })
            .Build();
        var summary = new SummaryGenerationService(
            NullLogger<SummaryGenerationService>.Instance,
            configuration);
        var scanner = new TaskScannerService(
            configuration,
            NullLogger<TaskScannerService>.Instance,
            summary);
        var persistence = new LocalOrchestratorChatPersistence(
            new OrchestratorChat(NullLogger<OrchestratorChat>.Instance),
            scanner);
        Assert.True(OrchestratorContextKey.TryParse("task:project-a/A-1", out var taskContext));
        var projectTurn = new OrchestratorChatTurn
        {
            Id = "local_project_user",
            Role = OrchestratorChatRoles.User,
            Text = "Local project question",
        };
        var taskTurn = new OrchestratorChatTurn
        {
            Id = "local_task_user",
            Role = OrchestratorChatRoles.User,
            Text = "Local task question",
        };

        await persistence.AppendAsync(
            "project-a", _watchPath, context: null, projectTurn, CancellationToken.None);
        await persistence.AppendAsync(
            "project-a", _watchPath, taskContext, taskTurn, CancellationToken.None);

        Assert.False(persistence.IsCentralTaskServerStore);
        Assert.Equal(
            taskTurn,
            Assert.Single(await persistence.ReadAsync(
                "project-a", _watchPath, taskContext, 20, CancellationToken.None)));
        var contexts = await persistence.ListContextsAsync(
            includeHidden: false,
            CancellationToken.None);
        Assert.Contains(contexts, context =>
            context.ContextKey == "project:project-a"
            && context.Summary == projectTurn.Text);
        Assert.Contains(contexts, context =>
            context.ContextKey == taskContext.Value
            && context.Summary == taskTurn.Text);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private OrchestratorChatService BuildService(
        CapturingRunner runner,
        IOrchestratorChatPersistence persistence)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root,
                ["WatchPaths:0:Name"] = "project-a",
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _watchPath,
            })
            .Build();
        var sessionStore = new GlobalOrchestratorSessionStore(
            config, NullLogger<GlobalOrchestratorSessionStore>.Instance);
        var summary = new SummaryGenerationService(
            NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(
            config, NullLogger<TaskScannerService>.Instance, summary);
        var bootstrap = new GlobalOrchestratorBootstrap(
            NullLogger<GlobalOrchestratorBootstrap>.Instance,
            sessionStore,
            runner,
            scanner,
            config);
        return new OrchestratorChatService(
            new OrchestratorChat(NullLogger<OrchestratorChat>.Instance),
            runner,
            sessionStore,
            bootstrap,
            scanner,
            config,
            NullLogger<OrchestratorChatService>.Instance,
            persistence: persistence);
    }

    private static OrchestratorContextEnvelope Envelope(
        string contextKey,
        string project,
        string? taskKey,
        DateTime capturedAt,
        IReadOnlyList<OrchestratorContextReference> references)
        => new(
            new OrchestratorConversationScope(
                taskKey is null ? "project" : "task",
                contextKey,
                project,
                taskKey),
            taskKey is null
                ? null
                : new OrchestratorActiveSurface("task", taskKey, TaskKey: taskKey),
            references,
            new OrchestratorContextBudget(),
            capturedAt);

    private static int Index(string source, string value)
        => source.IndexOf(value, StringComparison.Ordinal);

    private static int Count(string source, string value)
    {
        var count = 0;
        for (var index = 0; (index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0; index += value.Length)
            count++;
        return count;
    }

    private sealed class CapturingRunner : OrchestratorRunner
    {
        public CapturingRunner() : base(
            null!, NullLogger<OrchestratorRunner>.Instance, null, null, null)
        {
        }

        public string? Prompt { get; private set; }

        public override Task<OrchestratorDecisionResult> DecideCodexAsync(
            string prompt,
            string model,
            string? thinkingLevel,
            string workingDirectory,
            CancellationToken ct = default,
            string? projectName = null,
            string? watchPath = null)
        {
            Prompt = prompt;
            return Task.FromResult(new OrchestratorDecisionResult(
                true, "Answer", model, null, null, null));
        }
    }

    private sealed class MemoryPersistence(
        IEnumerable<OrchestratorChatTurn> initial) : IOrchestratorChatPersistence
    {
        public List<OrchestratorChatTurn> Turns { get; } = [.. initial];

        public bool IsCentralTaskServerStore => true;

        public Task<IReadOnlyList<OrchestratorChatTurn>> ReadAsync(
            string projectName,
            string watchPath,
            OrchestratorContextKey? context,
            int limit,
            CancellationToken ct)
            => Task.FromResult<IReadOnlyList<OrchestratorChatTurn>>(
                Turns.TakeLast(limit).ToArray());

        public Task AppendAsync(
            string projectName,
            string watchPath,
            OrchestratorContextKey? context,
            OrchestratorChatTurn turn,
            CancellationToken ct)
        {
            Turns.Add(turn);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<OrchestratorContextDto>> ListContextsAsync(
            bool includeHidden,
            CancellationToken ct)
            => Task.FromResult<IReadOnlyList<OrchestratorContextDto>>([]);
    }

    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://task-server.test"),
            };
    }

    private sealed class LegacyImportHandler : HttpMessageHandler
    {
        public TaskCompletionSource<string> ImportBody { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post
                && request.RequestUri?.AbsolutePath.EndsWith("/legacy-import", StringComparison.Ordinal) == true)
            {
                ImportBody.TrySetResult(await request.Content!.ReadAsStringAsync(cancellationToken));
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"contextKey\":\"project:project-a\",\"imported\":1,\"alreadyPresent\":0,\"rejected\":0}"),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
