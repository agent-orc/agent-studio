using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Regression coverage for F42: the 2026-05-24 operator-screenshot
/// incident where the orchestrator chat bubble rendered the raw .NET
/// <c>IOException.Message</c> string <c>"The pipe is being closed."</c>
/// as if it were the orchestrator's reply.
///
/// <para>
/// Two layers are pinned here:
///
///   1. <see cref="OrchestratorChatErrorTranslator"/> is a pure switch
///      and is exercised directly with the raw shapes the runner emits
///      (pipe IOException, timeout, cancellation, spawn failure, session
///      rejection, rate-limit, generic). Every shape produces a friendly
///      message and the raw text is preserved as
///      <see cref="OrchestratorChatErrorTranslation.RawDetail"/>.
///   2. <see cref="OrchestratorChatService.SendAsync"/> integrates the
///      translator at both error paths: a thrown exception on the resume
///      attempt, and a non-success runner result. In both cases the
///      persisted <see cref="OrchestratorChatTurn"/> carries the friendly
///      message in <see cref="OrchestratorChatTurn.ErrorMessage"/> and
///      the raw text in <see cref="OrchestratorChatTurn.ErrorDetail"/>
///      so a future "expand detail" expander has the bytes it needs
///      without re-introducing the original UX leak.
/// </para>
/// </summary>
public class OrchestratorChatErrorTranslatorTests : IDisposable
{
    private readonly string _root;
    private readonly string _watchPath;

    public OrchestratorChatErrorTranslatorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ats-chat-err-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        _watchPath = Path.Combine(_root, "project-a");
        Directory.CreateDirectory(_watchPath);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    [Theory]
    [InlineData("The pipe is being closed.")]
    [InlineData("the pipe is being closed")]
    [InlineData("Cannot access a closed pipe.")]
    [InlineData("Pipe has been ended.")]
    [InlineData("broken pipe")]
    public void Translate_PipeFamily_ReturnsFriendlyMessageAndFlagsSessionLost(string raw)
    {
        var t = OrchestratorChatErrorTranslator.Translate(raw);

        Assert.DoesNotContain("pipe is being closed", t.FriendlyMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("session", t.FriendlyMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("re-send", t.FriendlyMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(t.SessionLikelyLost, "pipe-broken implies the CLI exited; the next turn must re-bootstrap.");
        Assert.Equal(raw.Trim(), t.RawDetail);
    }

    [Fact]
    public void Translate_Timeout_ExplainsAndDoesNotMarkSessionLost()
    {
        var t = OrchestratorChatErrorTranslator.Translate("timeout after 300s");

        Assert.Contains("did not reply in time", t.FriendlyMessage);
        Assert.False(t.SessionLikelyLost);
        Assert.Equal("timeout after 300s", t.RawDetail);
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("canceled")]
    public void Translate_Cancelled_ExplainsShutdown(string raw)
    {
        var t = OrchestratorChatErrorTranslator.Translate(raw);
        Assert.Contains("cancelled", t.FriendlyMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(t.SessionLikelyLost);
    }

    [Theory]
    [InlineData("The system cannot find the file specified.")]
    [InlineData("Process.Start returned null")]
    [InlineData("An error occurred trying to start process 'claude'")]
    public void Translate_SpawnFailure_FlagsSessionLost(string raw)
    {
        var t = OrchestratorChatErrorTranslator.Translate(raw);
        Assert.Contains("could not start", t.FriendlyMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(t.SessionLikelyLost);
    }

    [Fact]
    public void Translate_SessionRejection_FlagsSessionLost()
    {
        var t = OrchestratorChatErrorTranslator.Translate("No conversation found with session ID: abc-123");
        Assert.Contains("session expired", t.FriendlyMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(t.SessionLikelyLost);
    }

    [Theory]
    [InlineData("rate limit exceeded")]
    [InlineData("usage limit reached")]
    public void Translate_RateLimit_ExplainsRetry(string raw)
    {
        var t = OrchestratorChatErrorTranslator.Translate(raw);
        Assert.Contains("rate limit", t.FriendlyMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(t.SessionLikelyLost);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Translate_EmptyOrNull_FallsBackToGenericEnvelope(string? raw)
    {
        var t = OrchestratorChatErrorTranslator.Translate(raw);
        Assert.Contains("did not return a reply", t.FriendlyMessage);
        Assert.Null(t.RawDetail);
    }

    [Fact]
    public void Translate_Unknown_ShapesStillProduceFriendlyEnvelope()
    {
        // Unknown non-CLI shapes keep a friendly primary message and preserve
        // the raw detail separately.
        const string raw = "Some weird new failure we have not seen before.";
        var t = OrchestratorChatErrorTranslator.Translate(raw);
        Assert.NotEqual(raw, t.FriendlyMessage);
        Assert.Contains("could not produce a reply", t.FriendlyMessage);
        Assert.Equal(raw, t.RawDetail);
    }

    [Fact]
    public void Translate_UnknownCodexStderr_AddsOneLineCliCause()
    {
        const string raw = "Not inside a trusted directory and --skip-git-repo-check was not specified.\nAdditional detail.";

        var t = OrchestratorChatErrorTranslator.Translate(raw, "codex");

        Assert.Contains("could not produce a reply", t.FriendlyMessage);
        Assert.Contains(
            "codex: Not inside a trusted directory and --skip-git-repo-check was not specified. Additional detail.",
            t.FriendlyMessage);
        Assert.Equal(2, t.FriendlyMessage.Split('\n').Length);
        Assert.Equal(raw, t.RawDetail);
    }

    [Fact]
    public async Task SendAsync_RunnerReturnsUnknownCodexStderr_PersistsVisibleCliCause()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root,
                ["WatchPaths:0:Name"] = "project-a",
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _watchPath
            })
            .Build();
        var sessionStore = new GlobalOrchestratorSessionStore(
            config, NullLogger<GlobalOrchestratorSessionStore>.Instance);
        var summary = new AgentStudio.Review.SummaryGenerationService(
            NullLogger<AgentStudio.Review.SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var runner = new PipeClosedRunner(
            "Not inside a trusted directory and --skip-git-repo-check was not specified.");
        var bootstrap = new GlobalOrchestratorBootstrap(
            NullLogger<GlobalOrchestratorBootstrap>.Instance,
            sessionStore, runner, scanner, config);
        var chat = new OrchestratorChat(NullLogger<OrchestratorChat>.Instance);
        var service = new OrchestratorChatService(
            chat, runner, sessionStore, bootstrap, scanner, config,
            NullLogger<OrchestratorChatService>.Instance);

        var reply = await service.SendAsync(
            "project-a", _watchPath,
            new SendOrchestratorChatRequest("Hi", Attachments: null),
            CancellationToken.None);

        Assert.Contains("codex: Not inside a trusted directory", reply.ErrorMessage);
        Assert.Equal(
            "Not inside a trusted directory and --skip-git-repo-check was not specified.",
            reply.ErrorDetail);
        var persisted = chat.Read(_watchPath).Single(t => t.Role == OrchestratorChatRoles.Orchestrator);
        Assert.Contains("codex: Not inside a trusted directory", persisted.ErrorMessage);
    }

    [Fact]
    public async Task SendAsync_RunnerReturnsPipeClosedError_PersistsFriendlyMessageAndDetail()
    {
        // Arrange: a stored session so SendAsync does not short-circuit
        // on the "session not booted" branch, plus a fake runner that
        // returns the 2026-05-24 pipe-closed shape as its ErrorMessage.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root,
                ["WatchPaths:0:Name"] = "project-a",
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _watchPath
            })
            .Build();

        var sessionStore = new GlobalOrchestratorSessionStore(
            config, NullLogger<GlobalOrchestratorSessionStore>.Instance);
        sessionStore.Write(new GlobalOrchestratorSession(
            SessionId: "session-aaaa",
            Model: OrchestratorRunner.DefaultModel,
            BootedAt: DateTime.UtcNow,
            BootPromptPreview: "(test)",
            BootReplyPreview: "(test)",
            CumulativeInputTokens: 0,
            CumulativeOutputTokens: 0,
            CumulativeCacheReadTokens: 0,
            CumulativeCacheCreationTokens: 0,
            Calls: 0,
            LastUsedAt: DateTime.UtcNow,
            LastError: null));

        var summary = new AgentStudio.Review.SummaryGenerationService(
            NullLogger<AgentStudio.Review.SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var runner = new PipeClosedRunner("The pipe is being closed.");
        var bootstrap = new GlobalOrchestratorBootstrap(
            NullLogger<GlobalOrchestratorBootstrap>.Instance,
            sessionStore, runner, scanner, config);
        var chat = new OrchestratorChat(NullLogger<OrchestratorChat>.Instance);
        var service = new OrchestratorChatService(
            chat, runner, sessionStore, bootstrap, scanner, config,
            NullLogger<OrchestratorChatService>.Instance);

        // Act
        var reply = await service.SendAsync(
            "project-a", _watchPath,
            new SendOrchestratorChatRequest("Hi", Attachments: null),
            CancellationToken.None);

        // Assert: the persisted turn carries the friendly message; the
        // raw .NET text is preserved in ErrorDetail (not ErrorMessage).
        Assert.Equal(OrchestratorChatRoles.Orchestrator, reply.Role);
        Assert.NotNull(reply.ErrorMessage);
        Assert.DoesNotContain("pipe is being closed", reply.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("session", reply.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("The pipe is being closed.", reply.ErrorDetail);

        // GPT-only composer calls do not resume or mutate the legacy Claude
        // session record.
        Assert.NotNull(sessionStore.Read());

        // The chat log on disk must also store the friendly message so
        // re-reading history does not resurrect the raw text.
        var persisted = chat.Read(_watchPath);
        var orchestratorTurn = persisted.Single(t => t.Role == OrchestratorChatRoles.Orchestrator);
        Assert.DoesNotContain("pipe is being closed", orchestratorTurn.ErrorMessage ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Equal("The pipe is being closed.", orchestratorTurn.ErrorDetail);
    }

    [Fact]
    public async Task SendAsync_RunnerThrowsPipeIOException_PersistsFriendlyMessageAndDetail()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root,
                ["WatchPaths:0:Name"] = "project-a",
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _watchPath
            })
            .Build();

        var sessionStore = new GlobalOrchestratorSessionStore(
            config, NullLogger<GlobalOrchestratorSessionStore>.Instance);
        sessionStore.Write(new GlobalOrchestratorSession(
            SessionId: "session-aaaa",
            Model: OrchestratorRunner.DefaultModel,
            BootedAt: DateTime.UtcNow,
            BootPromptPreview: "(test)",
            BootReplyPreview: "(test)",
            CumulativeInputTokens: 0,
            CumulativeOutputTokens: 0,
            CumulativeCacheReadTokens: 0,
            CumulativeCacheCreationTokens: 0,
            Calls: 0,
            LastUsedAt: DateTime.UtcNow,
            LastError: null));

        var summary = new AgentStudio.Review.SummaryGenerationService(
            NullLogger<AgentStudio.Review.SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var runner = new ThrowingRunner(new IOException("The pipe is being closed."));
        var bootstrap = new GlobalOrchestratorBootstrap(
            NullLogger<GlobalOrchestratorBootstrap>.Instance,
            sessionStore, runner, scanner, config);
        var chat = new OrchestratorChat(NullLogger<OrchestratorChat>.Instance);
        var service = new OrchestratorChatService(
            chat, runner, sessionStore, bootstrap, scanner, config,
            NullLogger<OrchestratorChatService>.Instance);

        var reply = await service.SendAsync(
            "project-a", _watchPath,
            new SendOrchestratorChatRequest("Hi", Attachments: null),
            CancellationToken.None);

        Assert.NotNull(reply.ErrorMessage);
        Assert.DoesNotContain("pipe is being closed", reply.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("The pipe is being closed.", reply.ErrorDetail);
        Assert.NotNull(sessionStore.Read());
    }

    /// <summary>
    /// Test double: returns Success=false with a pre-canned error message.
    /// Overrides the 6-arg <see cref="OrchestratorRunner.ResumeAsync"/>
    /// because <see cref="OrchestratorRunner.ResumeWithFallbackAsync"/>
    /// (the entry point used by <see cref="OrchestratorChatService.SendAsync"/>)
    /// always calls the inline-images variant; the 5-arg overload is dead
    /// weight on this path.
    /// </summary>
    private sealed class PipeClosedRunner : OrchestratorRunner
    {
        private readonly string _error;

        public PipeClosedRunner(string error) : base(
            claude: null!,
            logger: NullLogger<OrchestratorRunner>.Instance,
            parsers: null,
            modelRegistry: null,
            oneShotRegistry: null)
        {
            _error = error;
        }

        public override Task<OrchestratorDecisionResult> DecideCodexAsync(
            string prompt, string model, string? thinkingLevel, string workingDirectory,
            CancellationToken ct = default, string? projectName = null, string? watchPath = null)
            => Task.FromResult(new OrchestratorDecisionResult(
                Success: false,
                ReplyText: "",
                Model: model,
                TokenUsage: null,
                CapturedSessionId: null,
                ErrorMessage: _error));

        public override Task<OrchestratorDecisionResult> ResumeAsync(
            string sessionId, string prompt, string? model, string workingDirectory,
            IReadOnlyList<AgentStudio.Cli.CliOneShotImage>? inlineImages,
            CancellationToken ct = default)
            => Task.FromResult(new OrchestratorDecisionResult(
                Success: false,
                ReplyText: "",
                Model: model ?? DefaultModel,
                TokenUsage: null,
                CapturedSessionId: null,
                ErrorMessage: _error));

        public override Task<OrchestratorDecisionResult> DecideAsync(
            string prompt, string? model, string workingDirectory,
            IReadOnlyList<AgentStudio.Cli.CliOneShotImage>? inlineImages,
            CancellationToken ct = default)
            => ResumeAsync("", prompt, model, workingDirectory, inlineImages, ct);
    }

    /// <summary>Test double: throws the configured exception inside ResumeAsync (6-arg overload).</summary>
    private sealed class ThrowingRunner : OrchestratorRunner
    {
        private readonly Exception _exception;

        public ThrowingRunner(Exception exception) : base(
            claude: null!,
            logger: NullLogger<OrchestratorRunner>.Instance,
            parsers: null,
            modelRegistry: null,
            oneShotRegistry: null)
        {
            _exception = exception;
        }

        public override Task<OrchestratorDecisionResult> DecideCodexAsync(
            string prompt, string model, string? thinkingLevel, string workingDirectory,
            CancellationToken ct = default, string? projectName = null, string? watchPath = null)
            => throw _exception;

        public override Task<OrchestratorDecisionResult> ResumeAsync(
            string sessionId, string prompt, string? model, string workingDirectory,
            IReadOnlyList<AgentStudio.Cli.CliOneShotImage>? inlineImages,
            CancellationToken ct = default)
            => throw _exception;

        public override Task<OrchestratorDecisionResult> DecideAsync(
            string prompt, string? model, string workingDirectory,
            IReadOnlyList<AgentStudio.Cli.CliOneShotImage>? inlineImages,
            CancellationToken ct = default)
            => throw _exception;
    }
}
