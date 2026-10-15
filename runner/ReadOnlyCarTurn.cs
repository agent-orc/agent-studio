using CodingAgentRunner;
using CodingAgentRunner.Abstractions;
using CodingAgentRunner.Delegation;
using CodingAgentRunner.Execution;
using CodingAgentRunner.Model;
// The runner's own wire model also declares a CliOutputLine; the CAR event
// payload type must resolve to the library's.
using CarOutputLine = CodingAgentRunner.Model.CliOutputLine;

namespace AgentRunner;

/// <summary>
/// One read-only CLI turn through the quota-resolved CAR driver, shared by
/// project chat and orchestrator decision jobs. The read-only sandbox, shared
/// provider configuration, disabled delegation, and prompt transport stay
/// identical across CLI families. Returns one raw <see cref="ProcessResult"/>
/// for the provider-specific response parser.
/// </summary>
internal static class ReadOnlyCarTurn
{
    public static async Task<ProcessResult> RunAsync(
        RunnerOptions options,
        string runId,
        string cliType,
        string prompt,
        string workingDirectory,
        string? model,
        string? thinkingLevel,
        string logFolder,
        string logPrefix,
        Action<int>? started,
        Action<string> log,
        CancellationToken timeoutToken,
        CancellationToken shutdown,
        int? maxOutputTokens = null,
        int? maxInputTokens = null)
    {
        var logDirectory = Path.Combine(Path.GetTempPath(), logFolder, runId);
        var runner = new CliRunner(
            new CliOptions
            {
                CodexPath = options.CodexCliBin,
                ClaudePath = options.ClaudeCliBin,
                AllowAgentGitMutation = true,
                Delegation = new DelegationOptions { Enabled = false },
            },
            logPaths: new TurnLogPathProvider(logDirectory));
        var driver = runner.Get(cliType);
        var stdout = new System.Text.StringBuilder();
        var stderr = new System.Text.StringBuilder();
        var finished = new TaskCompletionSource<CliRunInfo>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnOutput(string id, CarOutputLine line)
        {
            if (!string.Equals(id, runId, StringComparison.Ordinal)) return;
            if (line.Stream == "stdout") stdout.AppendLine(line.Text);
            else if (line.Stream == "stderr")
            {
                stderr.AppendLine(line.Text);
                log($"{logPrefix}-{cliType} stderr: {line.Text}");
            }
        }

        void OnFinished(string id, CliRunInfo info)
        {
            if (string.Equals(id, runId, StringComparison.Ordinal)) finished.TrySetResult(info);
        }

        driver.OnOutput += OnOutput;
        driver.OnFinished += OnFinished;
        try
        {
            var (run, error) = await driver.StartAsync(
                new CliRunRequest
                {
                    RunId = runId,
                    Prompt = prompt,
                    WorkingDirectory = workingDirectory,
                    Model = model,
                    ThinkingLevel = thinkingLevel,
                    PermissionMode = CliPermissionModes.ReadOnly,
                    ContextMode = CliContextModes.Shared,
                    ExtraEnvironment = OutputCapEnvironment(cliType, maxOutputTokens),
                    Tuning = DecisionCapTuning(cliType, maxOutputTokens, maxInputTokens),
                },
                shutdown);
            if (run is null)
                throw new InvalidOperationException($"{cliType} {logPrefix} run failed to start: {error}");
            started?.Invoke(run.ProcessId);

            var deadline = Task.Delay(Timeout.InfiniteTimeSpan, timeoutToken)
                .ContinueWith(_ => { }, TaskScheduler.Default);
            var completed = await Task.WhenAny(finished.Task, deadline);
            if (!ReferenceEquals(completed, finished.Task))
            {
                driver.Stop(runId, RunStopReason.Watchdog);
                await Task.WhenAny(finished.Task, Task.Delay(TimeSpan.FromSeconds(10)));
                // Preserve the raw-spawn contract: a timed-out turn surfaces as the
                // cancellation the linked token produced, handled by the caller.
                timeoutToken.ThrowIfCancellationRequested();
            }

            var info = await finished.Task;
            return new ProcessResult(info.ExitCode ?? 1, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            driver.OnOutput -= OnOutput;
            driver.OnFinished -= OnFinished;
            driver.Forget(runId);
            // Per-turn log hygiene is best effort; a leftover folder is logged, not fatal.
            try { if (Directory.Exists(logDirectory)) Directory.Delete(logDirectory, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log($"{logPrefix}-{cliType} log-cleanup-failed path={logDirectory} error={ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>Claude Code reads its response cap from the environment.</summary>
    internal static Dictionary<string, string>? OutputCapEnvironment(string cliType, int? maxOutputTokens)
        => maxOutputTokens is > 0 && string.Equals(cliType, "claude", StringComparison.OrdinalIgnoreCase)
            ? new() { ["CLAUDE_CODE_MAX_OUTPUT_TOKENS"] = maxOutputTokens.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) }
            : null;

    /// <summary>Codex applies a tuning entry as its <c>-c model_max_output_tokens=N</c> override.</summary>
    internal static Dictionary<string, string>? OutputCapTuning(string cliType, int? maxOutputTokens)
        => maxOutputTokens is > 0 && string.Equals(cliType, "codex", StringComparison.OrdinalIgnoreCase)
            ? new() { ["model_max_output_tokens"] = maxOutputTokens.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) }
            : null;

    /// <summary>Codex accepts the total context window, including output and framing.</summary>
    internal static Dictionary<string, string>? DecisionCapTuning(string cliType,
        int? maxOutputTokens, int? maxInputTokens)
    {
        var tuning = OutputCapTuning(cliType, maxOutputTokens);
        if (maxInputTokens is > 0 && string.Equals(cliType, "codex", StringComparison.OrdinalIgnoreCase))
        {
            tuning ??= new();
            tuning["model_context_window"] = (maxInputTokens.Value + Math.Max(0, maxOutputTokens ?? 0))
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return tuning;
    }

    /// <summary>Keeps CAR's per-run output log inside a per-turn temp folder that is deleted with the turn.</summary>
    private sealed class TurnLogPathProvider(string directory) : IRunLogPathProvider
    {
        public string GetRunLogDirectory(string runId) => directory;
        public string GetActiveJobsFile() => Path.Combine(directory, "active-runs.json");
    }
}
