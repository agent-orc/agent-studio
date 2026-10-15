using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Covers the raw step-prompt capture introduced for the "Rohdaten komplett,
/// Herleitung als Lesemodell" principle: the <see cref="StepPromptLog"/>
/// writer/reader round-trip plus the <see cref="PromptLoggingCliOneShot"/>
/// central-dispatch decorator that drives it. The decorator must capture a
/// step-call prompt on dispatch (so it survives a later CLI failure), must
/// stay silent for the main run / follow-ups (no <c>JobFolderPath</c> +
/// <c>StepId</c>), and must never alter the wrapped runner's result.
/// </summary>
public sealed class StepPromptLogTests : IDisposable
{
    private readonly string _jobFolder;
    private readonly StepPromptLog _log;

    public StepPromptLogTests()
    {
        _jobFolder = Path.Combine(Path.GetTempPath(), "step-prompt-log-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_jobFolder);
        _log = new StepPromptLog(new JsonlAppender(), NullLogger<StepPromptLog>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_jobFolder, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task Append_ThenRead_RoundTripsEntryWithProvenance()
    {
        var at = new DateTime(2026, 6, 10, 8, 30, 0, DateTimeKind.Utc);
        await _log.AppendAsync(_jobFolder, new StepPromptEntry
        {
            At = at,
            StepId = "aspect-requirement-fit",
            TemplateRef = "review-aspect-requirement-fit.md",
            Model = "claude-haiku-4-5",
            Cli = "claude",
            Source = "review-aspect",
            Prompt = "Assess the requirement fit of this change.",
        });

        Assert.True(File.Exists(Path.Combine(_jobFolder, StepPromptLog.RelativePath)));

        var entry = Assert.Single(_log.ReadForJob(_jobFolder));
        Assert.Equal("aspect-requirement-fit", entry.StepId);
        Assert.Equal("review-aspect-requirement-fit.md", entry.TemplateRef);
        Assert.Equal("claude-haiku-4-5", entry.Model);
        Assert.Equal("claude", entry.Cli);
        Assert.Equal("review-aspect", entry.Source);
        Assert.Equal("Assess the requirement fit of this change.", entry.Prompt);
        Assert.Equal(at, entry.At);
    }

    [Fact]
    public async Task Append_MultiLinePrompt_StaysParseableOnOneLine()
    {
        var multiLine = "First line of the prompt.\nSecond line.\r\nThird line with a brace { and a quote \".";
        await _log.AppendAsync(_jobFolder, new StepPromptEntry
        {
            At = DateTime.UtcNow,
            StepId = "post-code-review-grade",
            Model = "claude-sonnet-4-6",
            Cli = "claude",
            Prompt = multiLine,
        });

        // The on-disk file must remain a single JSONL line (the appender
        // flattens literal newlines), yet the prompt content round-trips
        // intact because System.Text.Json escapes newlines inside the string.
        var lines = File.ReadAllLines(Path.Combine(_jobFolder, StepPromptLog.RelativePath));
        Assert.Single(lines);

        var entry = Assert.Single(_log.ReadForJob(_jobFolder));
        Assert.Equal(multiLine, entry.Prompt);
    }

    [Fact]
    public async Task Append_PreservesChronologicalOrderAcrossSteps()
    {
        await _log.AppendAsync(_jobFolder, new StepPromptEntry { At = DateTime.UtcNow, StepId = "aspect-code-quality", Model = "m", Cli = "claude", Prompt = "p1" });
        await _log.AppendAsync(_jobFolder, new StepPromptEntry { At = DateTime.UtcNow, StepId = "aspect-security", Model = "m", Cli = "claude", Prompt = "p2" });
        await _log.AppendAsync(_jobFolder, new StepPromptEntry { At = DateTime.UtcNow, StepId = "post-code-review-grade", Model = "m", Cli = "claude", Prompt = "p3" });

        var entries = _log.ReadForJob(_jobFolder);
        Assert.Equal(new[] { "aspect-code-quality", "aspect-security", "post-code-review-grade" }, entries.Select(e => e.StepId));
    }

    [Fact]
    public void ReadForJob_NoFile_ReturnsEmpty()
    {
        Assert.Empty(_log.ReadForJob(_jobFolder));
    }

    [Fact]
    public async Task Append_MissingStepId_IsNoOp()
    {
        await _log.AppendAsync(_jobFolder, new StepPromptEntry { At = DateTime.UtcNow, StepId = "", Model = "m", Cli = "claude", Prompt = "p" });
        Assert.False(File.Exists(Path.Combine(_jobFolder, StepPromptLog.RelativePath)));
        Assert.Empty(_log.ReadForJob(_jobFolder));
    }

    [Fact]
    public async Task ReadForJob_SkipsBlankAndUnparseableLines()
    {
        await _log.AppendAsync(_jobFolder, new StepPromptEntry { At = DateTime.UtcNow, StepId = "aspect-security", Model = "m", Cli = "claude", Prompt = "good" });
        var path = Path.Combine(_jobFolder, StepPromptLog.RelativePath);
        File.AppendAllText(path, "\n{ this is not valid json }\n\n");

        var entry = Assert.Single(_log.ReadForJob(_jobFolder));
        Assert.Equal("aspect-security", entry.StepId);
    }

    [Fact]
    public async Task Decorator_LogsPrompt_WhenJobFolderAndStepIdSet()
    {
        var inner = new FakeOneShot();
        var decorator = new PromptLoggingCliOneShot(inner, _log);

        var result = await decorator.RunAsync(new CliOneShotRequest("claude", "claude-sonnet-4-6", "the final aspect prompt")
        {
            JobFolderPath = _jobFolder,
            StepId = "aspect-requirement-fit",
            TemplateRef = "review-aspect-requirement-fit.md",
            Source = "review-aspect",
        });

        Assert.Same(inner.LastResult, result);
        Assert.Equal(1, inner.Calls);

        var entry = Assert.Single(_log.ReadForJob(_jobFolder));
        Assert.Equal("aspect-requirement-fit", entry.StepId);
        Assert.Equal("review-aspect-requirement-fit.md", entry.TemplateRef);
        Assert.Equal("claude-sonnet-4-6", entry.Model);
        Assert.Equal("claude", entry.Cli);
        Assert.Equal("the final aspect prompt", entry.Prompt);
    }

    [Fact]
    public async Task Decorator_DoesNotLog_ForMainRunWithoutStepFields()
    {
        var inner = new FakeOneShot();
        var decorator = new PromptLoggingCliOneShot(inner, _log);

        // No JobFolderPath / StepId: this is the main-run / follow-up shape,
        // already logged in prompt.md / chat - must not be double-booked.
        await decorator.RunAsync(new CliOneShotRequest("claude", "claude-sonnet-4-6", "main run prompt"));

        Assert.Equal(1, inner.Calls);
        Assert.False(File.Exists(Path.Combine(_jobFolder, StepPromptLog.RelativePath)));
    }

    [Fact]
    public async Task Decorator_CapturesPrompt_EvenWhenInnerCallFails()
    {
        var inner = new FakeOneShot
        {
            ResultFactory = _ => CliOneShotResult.SpawnFailure("boom", DateTime.UtcNow, DateTime.UtcNow),
        };
        var decorator = new PromptLoggingCliOneShot(inner, _log);

        var result = await decorator.RunAsync(new CliOneShotRequest("claude", "claude-sonnet-4-6", "prompt that times out")
        {
            JobFolderPath = _jobFolder,
            StepId = "post-code-review-grade",
        });

        Assert.False(result.Ok);
        // The prompt is written at dispatch, before the inner call, so a
        // failed / timed-out CLI run still leaves the raw record behind.
        var entry = Assert.Single(_log.ReadForJob(_jobFolder));
        Assert.Equal("post-code-review-grade", entry.StepId);
        Assert.Equal("prompt that times out", entry.Prompt);
    }

    [Theory]
    [InlineData("/private/sessions/raw.jsonl", "raw-session-path")]
    [InlineData("/private/logs/cli-output.log", "raw-session-path")]
    public async Task Orchestrator_step_rejects_raw_session_paths_before_logging_or_model_call(
        string path, string reason)
    {
        var inner = new FakeOneShot();
        var decorator = new PromptLoggingCliOneShot(inner, _log);

        var result = await decorator.RunAsync(new CliOneShotRequest("claude", "model", "Read " + path)
        {
            JobFolderPath = _jobFolder,
            StepId = PipelineCatalogue.PostAbortReviewStepId,
        });

        Assert.False(result.Ok);
        Assert.Contains(reason, result.Error);
        Assert.Equal(0, inner.Calls);
        Assert.Empty(_log.ReadForJob(_jobFolder));
    }

    [Fact]
    public async Task Orchestrator_step_rejects_oversize_prompt_without_a_model_call()
    {
        var inner = new FakeOneShot();
        var decorator = new PromptLoggingCliOneShot(inner, _log);

        var result = await decorator.RunAsync(new CliOneShotRequest("claude", "model",
            new string('x', OrchestratorPromptBoundary.MaxPromptBytes + 1))
        {
            JobFolderPath = _jobFolder,
            StepId = PipelineCatalogue.PostAbortReviewStepId,
        });

        Assert.False(result.Ok);
        Assert.Contains("oversize-orchestrator-prompt", result.Error);
        Assert.Equal(0, inner.Calls);
    }

    [Fact]
    public async Task Orchestrator_step_redacts_a_secret_in_the_logged_and_dispatched_prompt()
    {
        var inner = new FakeOneShot();
        string? dispatched = null;
        inner.ResultFactory = request =>
        {
            dispatched = request.Prompt;
            var at = new DateTime(2026, 10, 15, 8, 0, 0, DateTimeKind.Utc);
            return CliOneShotResult.SpawnFailure("test", at, at);
        };
        var decorator = new PromptLoggingCliOneShot(inner, _log);
        var secret = "api_key=" + new string('v', 24);

        await decorator.RunAsync(new CliOneShotRequest("claude", "model", "Evidence " + secret)
        {
            JobFolderPath = _jobFolder,
            StepId = PipelineCatalogue.PostAbortReviewStepId,
        });

        Assert.DoesNotContain(secret, dispatched);
        Assert.Contains("[omission: secret]", dispatched);
        var logged = Assert.Single(_log.ReadForJob(_jobFolder));
        Assert.Equal(dispatched, logged.Prompt);
    }

    private sealed class FakeOneShot : ICliOneShot
    {
        public int Calls;
        public CliOneShotResult? LastResult;
        public Func<CliOneShotRequest, CliOneShotResult>? ResultFactory;

        public string CliType => "claude";

        public Task<CliOneShotResult> RunAsync(CliOneShotRequest request, CancellationToken ct = default)
        {
            Calls++;
            var now = DateTime.UtcNow;
            LastResult = ResultFactory?.Invoke(request)
                ?? new CliOneShotResult(
                    Ok: true,
                    ExitCode: 0,
                    Stdout: "{}",
                    Stderr: string.Empty,
                    Duration: TimeSpan.Zero,
                    ParsedText: "ok",
                    Usage: null,
                    RichUsage: null,
                    Latency: new AgentMessageLatency(RequestedAt: now, CompletedAt: now, TotalMs: 0),
                    Error: null);
            return Task.FromResult(LastResult);
        }
    }
}
