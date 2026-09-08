using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Regression coverage for the 2026-05-11 stuck-chat bug: the global
/// orchestrator chat looped forever on a stale Anthropic session id
/// ("No conversation found with session ID: ...") because the per-job
/// rejection-recovery had not been mirrored to the chat path.
///
/// <para>
/// The canonical recovery now lives on the runner as
/// <see cref="OrchestratorRunner.ResumeWithFallbackAsync"/>. These tests
/// pin the two contracts callers depend on:
///
///   1. The static <see cref="OrchestratorRunner.IsSessionRejection"/>
///      classifier recognises the Anthropic error shape so callers can't
///      drift on the matcher.
///   2. <see cref="OrchestratorChatService.SendAsync"/> transparently
///      re-bootstraps and persists the fresh session record when the
///      previous session id is rejected, so the next chat turn resumes
///      against the new id instead of repeating the same failure.
/// </para>
/// </summary>
public class OrchestratorChatRejectionRecoveryTests : IDisposable
{
    private readonly string _root;
    private readonly string _watchPath;

    public OrchestratorChatRejectionRecoveryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ats-chat-reject-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        _watchPath = Path.Combine(_root, "project-a");
        Directory.CreateDirectory(_watchPath);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    [Theory]
    [InlineData("No conversation found with session ID: abc-123")]
    [InlineData("session not found")]
    [InlineData("error: claude session abc-123 expired")]
    public void IsSessionRejection_RecognisesAnthropicShapes(string err)
    {
        Assert.True(OrchestratorRunner.IsSessionRejection(err));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("timeout after 300s")]
    [InlineData("rate limit exceeded")]
    public void IsSessionRejection_LetsOtherErrorsThrough(string? err)
    {
        Assert.False(OrchestratorRunner.IsSessionRejection(err));
    }

    [Fact]
    public async Task SendAsync_GptSelection_IgnoresLegacyClaudeSession()
    {
        // Arrange: a stored global session whose id will be "rejected" by
        // the runner. Configuration points TaskRepository + WatchPath at
        // temp dirs so the session-store / chat append both land on disk.
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
        var stale = new GlobalOrchestratorSession(
            SessionId: "stale-session-aaaa",
            Model: OrchestratorRunner.DefaultModel,
            BootedAt: DateTime.UtcNow.AddHours(-48),
            BootPromptPreview: "(test)",
            BootReplyPreview: "(test)",
            CumulativeInputTokens: 100,
            CumulativeOutputTokens: 20,
            CumulativeCacheReadTokens: 0,
            CumulativeCacheCreationTokens: 0,
            Calls: 1,
            LastUsedAt: DateTime.UtcNow.AddHours(-48),
            LastError: null);
        sessionStore.Write(stale);

        var summary = new AgentStudio.Review.SummaryGenerationService(
            NullLogger<AgentStudio.Review.SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);

        var runner = new RejectThenSucceedRunner(
            rejectionError: "No conversation found with session ID: stale-session-aaaa",
            freshSessionId: "fresh-session-bbbb",
            replyText: "Hi back!");

        var bootstrap = new GlobalOrchestratorBootstrap(
            NullLogger<GlobalOrchestratorBootstrap>.Instance,
            sessionStore, runner, scanner, config);

        var chat = new OrchestratorChat(NullLogger<OrchestratorChat>.Instance);
        var service = new OrchestratorChatService(
            chat, runner, sessionStore, bootstrap, scanner, config,
            NullLogger<OrchestratorChatService>.Instance);

        var req = new SendOrchestratorChatRequest(
            "Hi", Attachments: null, Model: "gpt-5.4-mini", ThinkingLevel: "low");

        // Act
        var reply = await service.SendAsync("project-a", _watchPath, req, CancellationToken.None);

        // Assert: the user sees a non-empty reply, no error surfaced.
        Assert.Equal(OrchestratorChatRoles.Orchestrator, reply.Role);
        Assert.Equal("Hi back!", reply.Text);
        Assert.Null(reply.ErrorMessage);

        // The GPT-only path never resumes or silently falls back to Claude.
        Assert.Equal(0, runner.ResumeCalls);
        Assert.Equal(1, runner.DecideCalls);
        Assert.Null(runner.LastResumeSessionId);
        Assert.Equal("gpt-5.4-mini", runner.LastModel);
        Assert.Equal("low", runner.LastThinkingLevel);

        // The unrelated legacy session is untouched.
        var afterRecovery = sessionStore.Read();
        Assert.NotNull(afterRecovery);
        Assert.Equal("stale-session-aaaa", afterRecovery!.SessionId);
        Assert.Equal(1, afterRecovery.Calls);
    }

    /// <summary>
    /// Test double that returns a session-rejection failure on the first
    /// <see cref="OrchestratorRunner.ResumeAsync"/> call and a successful
    /// result with a fresh captured session id on the
    /// <see cref="OrchestratorRunner.DecideAsync"/> fallback. Bypasses the
    /// CLI entirely so the test stays hermetic.
    /// </summary>
    private sealed class RejectThenSucceedRunner : OrchestratorRunner
    {
        private readonly string _rejectionError;
        private readonly string _freshSessionId;
        private readonly string _replyText;

        public int ResumeCalls { get; private set; }
        public int DecideCalls { get; private set; }
        public string? LastResumeSessionId { get; private set; }
        public string? LastModel { get; private set; }
        public string? LastThinkingLevel { get; private set; }

        public RejectThenSucceedRunner(string rejectionError, string freshSessionId, string replyText)
            : base(
                claude: null!,
                logger: NullLogger<OrchestratorRunner>.Instance,
                parsers: null,
                modelRegistry: null,
                oneShotRegistry: null)
        {
            _rejectionError = rejectionError;
            _freshSessionId = freshSessionId;
            _replyText = replyText;
        }

        // The 6-arg overloads are the ones ResumeWithFallbackAsync actually
        // calls (the orchestrator-chat path always passes the inlineImages
        // list, even when null, since commit d28d0b3 added the multimodal
        // fast path). Overriding the 5-arg variants left them as dead code.
        public override Task<OrchestratorDecisionResult> ResumeAsync(
            string sessionId, string prompt, string? model, string workingDirectory,
            IReadOnlyList<AgentStudio.Cli.CliOneShotImage>? inlineImages,
            CancellationToken ct = default)
        {
            ResumeCalls++;
            LastResumeSessionId = sessionId;
            return Task.FromResult(new OrchestratorDecisionResult(
                Success: false,
                ReplyText: "",
                Model: model ?? DefaultModel,
                TokenUsage: null,
                CapturedSessionId: null,
                ErrorMessage: _rejectionError));
        }

        public override Task<OrchestratorDecisionResult> DecideAsync(
            string prompt, string? model, string workingDirectory,
            IReadOnlyList<AgentStudio.Cli.CliOneShotImage>? inlineImages,
            CancellationToken ct = default)
        {
            DecideCalls++;
            return Task.FromResult(new OrchestratorDecisionResult(
                Success: true,
                ReplyText: _replyText,
                Model: model ?? DefaultModel,
                TokenUsage: new OrchestratorTokenUsage
                {
                    Model = model ?? DefaultModel,
                    InputTokens = 50,
                    OutputTokens = 5,
                    CacheReadTokens = 0,
                    CacheCreationTokens = 0
                },
                CapturedSessionId: _freshSessionId,
                ErrorMessage: null));
        }

        public override Task<OrchestratorDecisionResult> DecideCodexAsync(
            string prompt, string model, string? thinkingLevel, string workingDirectory,
            CancellationToken ct = default, string? projectName = null, string? watchPath = null)
        {
            DecideCalls++;
            LastModel = model;
            LastThinkingLevel = thinkingLevel;
            return Task.FromResult(new OrchestratorDecisionResult(
                Success: true,
                ReplyText: _replyText,
                Model: model,
                TokenUsage: null,
                CapturedSessionId: null,
                ErrorMessage: null));
        }
    }
}
