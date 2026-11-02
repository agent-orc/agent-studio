using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3410 regression. On 01.11.2026 the closing summary prompt failed 259
/// times in three hours with <c>decision-input-incomplete:oversize-orchestrator-prompt</c>:
/// the 60,000-character log tail alone was about twice the 32 KB orchestrator
/// prompt limit, and three identical attempts left the card with a scaffold.
/// The summary prompt now fits the limit, keeps its most important sections,
/// names what it left out, and a refused input is not retried.
/// </summary>
public sealed class SummaryPromptFitTests : IDisposable
{
    private const string FinalMessage = "FINAL-MESSAGE: fitted the summary prompt and verified it. [[TASK_DONE]]";
    private const string TaskPromptHead = "# Fit the summary prompt into the orchestrator limit";
    private const string TaskPromptTail = "TASK-PROMPT-TAIL-LEFT-OUT";
    private const string ExitLine = "CLI exited 0; launchFailed=False typedOutcome=SuccessfulCompletion";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "summary-prompt-fit-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception) { /* best-effort cleanup of an isolated test folder */ }
    }

    [Fact]
    public async Task Oversize_run_fits_the_limit_keeps_the_key_sections_and_names_the_omissions()
    {
        var task = WriteCard(OversizeCodexLog(), OversizeTaskPrompt());
        var inner = new CapturingOneShot(SummaryMarkdown);
        var logger = new CapturingLogger<SummaryGenerationService>();
        var service = Service(Boundary(inner), logger);

        var outcome = await service.FinalizeAsync(task, Success());

        Assert.True(outcome.Generated);
        Assert.Equal(1, inner.Calls);
        var prompt = Assert.Single(inner.Prompts);
        Assert.InRange(Encoding.UTF8.GetByteCount(prompt), 1, OrchestratorPromptBoundary.MaxPromptBytes);
        Assert.Null(OrchestratorPromptBoundary.Check(PipelineCatalogue.SummaryAssessmentStepId, prompt).Failure);

        // The sections that carry the result survive.
        Assert.Contains(TaskPromptHead, prompt, StringComparison.Ordinal);
        Assert.Contains(ExitLine, prompt, StringComparison.Ordinal);
        Assert.Contains("Last terminal sentinel in the log: [[TASK_DONE]]", prompt, StringComparison.Ordinal);
        Assert.Contains("`results/report.html`", prompt, StringComparison.Ordinal);
        Assert.Contains("`results/proof.png`", prompt, StringComparison.Ordinal);
        Assert.Contains(FinalMessage, prompt, StringComparison.Ordinal);
        Assert.Contains("\"aggregated_output\":\"out-399", prompt, StringComparison.Ordinal);

        // What came before them is shortened, and the prompt says so.
        Assert.DoesNotContain(TaskPromptTail, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("out-000", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("[runner-log-delivery:", prompt, StringComparison.Ordinal);
        Assert.Contains("OMITTED TO FIT THE PROMPT LIMIT", prompt, StringComparison.Ordinal);
        Assert.Contains("- task prompt: ", prompt, StringComparison.Ordinal);
        Assert.Contains("- run log: the first ", prompt, StringComparison.Ordinal);
        Assert.Contains("runner-log-delivery marker lines", prompt, StringComparison.Ordinal);
        Assert.Contains("long lines (tool calls and their output) cut to", prompt, StringComparison.Ordinal);
        Assert.True(prompt.IndexOf("OMITTED TO FIT", StringComparison.Ordinal)
            < prompt.IndexOf("TASK PROMPT (head):", StringComparison.Ordinal));

        Assert.Equal(1, service.ShortenedPromptCount);
        var shortened = Assert.Single(logger.Lines, line => line.StartsWith("Summary prompt shortened", StringComparison.Ordinal));
        Assert.Contains($"promptBytes={Encoding.UTF8.GetByteCount(prompt)}", shortened, StringComparison.Ordinal);
        Assert.Contains($"limitBytes={OrchestratorPromptBoundary.MaxPromptBytes}", shortened, StringComparison.Ordinal);
        Assert.Contains("shortenedTotal=1", shortened, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_prompt_is_not_retried_and_the_degraded_count_is_logged_with_the_size()
    {
        var task = WriteCard("[12:00:00.000] [stdout] did some work\n", "# Small task\n");
        var inner = new CapturingOneShot(error: OrchestratorPromptBoundary.FailurePrefix + "oversize-orchestrator-prompt");
        var logger = new CapturingLogger<SummaryGenerationService>();
        var service = Service(inner, logger);

        var outcome = await service.FinalizeAsync(task, Success());

        Assert.Equal(TaskSummaryStatus.Degraded, outcome.Status);
        Assert.Equal(1, outcome.Attempt);
        Assert.Equal(3, outcome.MaxAttempts);
        Assert.Equal(1, inner.Calls);
        Assert.Equal(1, service.DegradedSummaryCount);
        var degraded = Assert.Single(logger.Lines, line => line.StartsWith("Result finalization degraded", StringComparison.Ordinal));
        Assert.Contains($"promptBytes={Encoding.UTF8.GetByteCount(Assert.Single(inner.Prompts))}", degraded, StringComparison.Ordinal);
        Assert.Contains("degradedTotal=1", degraded, StringComparison.Ordinal);
        Assert.Contains("oversize-orchestrator-prompt", degraded, StringComparison.Ordinal);
        Assert.DoesNotContain(logger.Lines, line => line.StartsWith("Result finalization retry", StringComparison.Ordinal));
    }

    [Fact]
    public void A_log_that_fits_passes_through_whole_without_an_omission_note()
    {
        const string log = "[12:00:00.000] [stdout] did some work\n[12:00:01.000] [system] [runner] " + ExitLine + "\n";

        var excerpt = SummaryPromptFit.Fit(new SummaryLogSources(log, "# Small task", []), 16 * 1024);

        Assert.False(excerpt.Shortened);
        Assert.Empty(excerpt.Omissions);
        Assert.DoesNotContain("OMITTED", excerpt.Text, StringComparison.Ordinal);
        Assert.Contains(log.TrimEnd('\n'), excerpt.Text, StringComparison.Ordinal);
        Assert.Contains("# Small task", excerpt.Text, StringComparison.Ordinal);
        Assert.Contains("DELIVERABLES (files under results/):\nNone.", excerpt.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Claude_assistant_text_is_quoted_as_a_final_message()
    {
        var line = "[07:58:07.790] [stdout] " + JsonSerializer.Serialize(new
        {
            type = "assistant",
            message = new
            {
                role = "assistant",
                content = new object[]
                {
                    new { type = "tool_use", name = "Bash" },
                    new { type = "text", text = FinalMessage },
                },
            },
        });

        var excerpt = SummaryPromptFit.Fit(new SummaryLogSources(line, null, []), 16 * 1024);

        Assert.Contains("FINAL AGENT MESSAGES (newest last):\n" + FinalMessage, excerpt.Text, StringComparison.Ordinal);
        Assert.Contains("(no task prompt on the card)", excerpt.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_raw_session_path_is_replaced_and_named_so_the_boundary_accepts_the_prompt()
    {
        const string log = "[12:00:00.000] [stdout] Read /home/agent/.claude/projects/x/sessions/abc.jsonl\n";

        var excerpt = SummaryPromptFit.Fit(new SummaryLogSources(log, null, []), 16 * 1024);

        Assert.DoesNotContain("/sessions/", excerpt.Text, StringComparison.Ordinal);
        Assert.Contains("[omitted: raw session path]", excerpt.Text, StringComparison.Ordinal);
        Assert.Contains(excerpt.Omissions, omission => omission.StartsWith("secrets or raw session paths", StringComparison.Ordinal));
        Assert.Null(OrchestratorPromptBoundary.Check(PipelineCatalogue.SummaryAssessmentStepId, excerpt.Text).Failure);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(300)]
    [InlineData(2_000)]
    [InlineData(8_000)]
    [InlineData(20_000)]
    public void The_slot_never_exceeds_its_budget_and_keeps_utf8_intact(int maxBytes)
    {
        var log = string.Join('\n', Enumerable.Range(0, 400).Select(i =>
            $"[12:00:00.000] [stdout] Zeile {i}: Größe geprüft ✓ 🚀 " + new string('ä', 300)));
        var sources = new SummaryLogSources(
            log,
            "# Aufgabe 🚀\n" + new string('ü', 10_000),
            Enumerable.Range(0, 100).Select(i => new SummaryDeliverable($"results/datei-{i}-ö.md", i)).ToList());

        var excerpt = SummaryPromptFit.Fit(sources, maxBytes);

        Assert.InRange(excerpt.Bytes, 0, maxBytes);
        var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        Assert.Equal(excerpt.Text, strict.GetString(strict.GetBytes(excerpt.Text)));
        if (maxBytes >= 8_000)
        {
            Assert.Contains("# Aufgabe 🚀", excerpt.Text, StringComparison.Ordinal);
            Assert.Contains("Zeile 399:", excerpt.Text, StringComparison.Ordinal);
            Assert.Contains(excerpt.Omissions, omission => omission.StartsWith("deliverables: ", StringComparison.Ordinal));
        }
    }

    private TaskInfo WriteCard(string log, string taskPrompt)
    {
        Directory.CreateDirectory(Path.Combine(_root, "logs"));
        Directory.CreateDirectory(Path.Combine(_root, "results"));
        File.WriteAllText(Path.Combine(_root, "logs", "cli-output.log"), log);
        File.WriteAllText(Path.Combine(_root, "prompt.md"), taskPrompt);
        File.WriteAllText(Path.Combine(_root, "results", "report.html"), "<html></html>");
        File.WriteAllBytes(Path.Combine(_root, "results", "proof.png"), [1, 2, 3]);
        return new TaskInfo
        {
            Id = "AGT-3410",
            TaskKey = "AGT-3410",
            Title = "Fit the summary prompt",
            TaskType = "bug",
            Mode = TaskModes.Coding,
            State = TaskStates.Progress,
            FolderPath = _root,
            WatchPath = _root,
            ProjectName = "agent-taskboard",
        };
    }

    /// <summary>
    /// Shaped like the Codex logs measured on 02.11.2026: tool-call JSON with
    /// long output, a delivery marker after every event, occasional agent
    /// messages, then the final message and the runner's exit line.
    /// </summary>
    private static string OversizeCodexLog()
    {
        var log = new StringBuilder();
        for (var i = 0; i < 400; i++)
        {
            log.Append("[12:00:00.000] [stdout] ").Append(JsonSerializer.Serialize(new
            {
                type = "item.completed",
                item = new
                {
                    id = $"item_{i}",
                    type = "command_execution",
                    command = "/bin/bash -lc 'dotnet test'",
                    aggregated_output = $"out-{i:000} " + new string('o', 3_000),
                    exit_code = 0,
                },
            })).Append('\n');
            log.Append("[12:00:00.000] [system] [runner-log-delivery:").Append(i.ToString("x64")).Append("]\n");
            if (i % 50 == 0) log.Append(AgentMessageLine($"Progress note {i}."));
        }
        log.Append(AgentMessageLine(FinalMessage));
        log.Append("[12:30:00.000] [stdout] {\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":1}}\n");
        log.Append("[12:30:01.000] [system] [runner] ").Append(ExitLine).Append(" classifier=execution-outcome/v1\n");
        return log.ToString();
    }

    private static string AgentMessageLine(string text)
        => "[12:00:00.000] [stdout] "
           + JsonSerializer.Serialize(new { type = "item.completed", item = new { id = "msg", type = "agent_message", text } })
           + "\n";

    private static string OversizeTaskPrompt()
        => TaskPromptHead + "\n\n" + string.Concat(Enumerable.Repeat("Context line for the run.\n", 800)) + TaskPromptTail;

    private static TerminalRunOutcome Success() => new(
        TerminalRunOutcomeKinds.Success,
        "Success",
        ShouldMoveToReview: true,
        ShouldShowFailureToast: false,
        Reason: "agent emitted TASK_DONE");

    private static ICliOneShot Boundary(ICliOneShot inner)
        => new PromptLoggingCliOneShot(inner, new StepPromptLog(new JsonlAppender(), NullLogger<StepPromptLog>.Instance));

    private static SummaryGenerationService Service(ICliOneShot oneShot, ILogger<SummaryGenerationService> logger)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PromptTemplates:RuntimePath"] = Path.Combine(Directory.GetCurrentDirectory(), "prompts", "runtime"),
                ["SummaryGeneration:FinalizationMaxAttempts"] = "3",
            })
            .Build();
        return new SummaryGenerationService(
            logger,
            configuration,
            new RuntimePromptService(configuration, NullLogger<RuntimePromptService>.Instance),
            oneShotRegistry: new CliOneShotRegistry([oneShot]));
    }

    private const string SummaryMarkdown = """
        # Status

        - Result: Success
        - Case: bugfix
        - Duration: 30 min

        ## Overview

        - Problem: The summary prompt exceeded the orchestrator limit.
        - Solution: The prompt is fitted into the limit.

        ## What Was Done

        - Fitted the summary prompt.

        ## Open Items

        - None.
        """;

    private sealed class CapturingOneShot(string? markdown = null, string? error = null) : ICliOneShot
    {
        public string CliType => CliTypes.Claude;

        public int Calls { get; private set; }

        public List<string> Prompts { get; } = [];

        public Task<CliOneShotResult> RunAsync(CliOneShotRequest request, CancellationToken ct = default)
        {
            Calls++;
            Prompts.Add(request.Prompt);
            var now = DateTime.UnixEpoch;
            if (error is not null) return Task.FromResult(CliOneShotResult.SpawnFailure(error, now, now));
            return Task.FromResult(new CliOneShotResult(
                Ok: true,
                ExitCode: 0,
                Stdout: markdown!,
                Stderr: string.Empty,
                Duration: TimeSpan.FromMilliseconds(1),
                ParsedText: markdown!,
                Usage: null,
                RichUsage: null,
                Latency: new AgentMessageLatency(RequestedAt: now, CompletedAt: now, TotalMs: 1),
                Error: null));
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines
        {
            get { lock (_lines) return _lines.ToList(); }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_lines) _lines.Add(formatter(state, exception));
        }
    }
}
