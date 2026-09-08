using System.Text.Json;
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
/// Executes one claimed project-chat turn in a dedicated read-only worktree of
/// the same project cache used by card runs. The checkout is persistent across
/// turns, but every claim fetches and resets it to the configured project branch
/// before the quota-resolved CLI starts.
/// </summary>
public sealed class RemoteProjectChatRunner
{
    private readonly RunnerOptions _options;
    private readonly TaskServerClient _client;
    private readonly Action<string> _log;

    public RemoteProjectChatRunner(
        RunnerOptions options,
        TaskServerClient client,
        Action<string> log)
    {
        _options = options;
        _client = client;
        _log = log;
    }

    public async Task<int> RunAsync(RemoteChatWorkItem work, CancellationToken shutdown)
    {
        ChatExecutionContext? executionContext = null;
        using var renewStop = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        var renewTask = RenewUntilStoppedAsync(work, renewStop.Token);
        try
        {
            var workspace = new ProjectChatWorkspace(
                _options, work.ProjectId, work.RepositoryUrl, work.DefaultBranch, _log);
            var checkout = await workspace.PrepareAsync(shutdown);
            executionContext = new ChatExecutionContext(
                "remote",
                _options.Hostname,
                checkout.RepoPath,
                checkout.Branch,
                checkout.HeadSha,
                "ready",
                DateTime.UtcNow);

            _log(
                $"project-chat-execution-context host={_options.Hostname} " +
                $"path={checkout.RepoPath} branch={checkout.Branch} head={checkout.HeadSha}");

            if (work.Kind == RemoteChatWorkKinds.Inspect)
            {
                return await CompleteAsync(
                    work, success: true, replyText: "", error: null,
                    work.Model, tokenUsage: null, executionContext, shutdown);
            }

            if (string.IsNullOrWhiteSpace(work.Prompt) || string.IsNullOrWhiteSpace(work.Model))
                throw new InvalidOperationException("Claimed project-chat turn has no prompt or model.");

            var contextPrompt = $"""
                === EXECUTION CONTEXT ===
                This project chat is executing on remote host "{_options.Hostname}".
                Repository checkout: {checkout.RepoPath}
                Branch context: {checkout.Branch}
                HEAD: {checkout.HeadSha}
                Tool calls run from that exact checkout. When the user asks where you are executing, use these values.

                {work.Prompt}
                """;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.RunTimeoutSeconds));
            var cliType = string.IsNullOrWhiteSpace(work.CliType)
                ? "codex"
                : work.CliType.Trim().ToLowerInvariant();
            _log($"project-chat-agent-start engine=car cli={cliType} path={checkout.RepoPath} model={work.Model} thinking={work.ThinkingLevel ?? "default"}");
            // T1c (AGT-2370): this CLI start path runs through CAR with
            // PermissionMode=read-only. AGT-2751 selects the driver from the
            // server-resolved CLI family while preserving the same posture.
            var process = await RunAgentThroughCarAsync(
                work, cliType, checkout, contextPrompt, timeout.Token, shutdown);
            var parsed = string.Equals(cliType, "claude", StringComparison.OrdinalIgnoreCase)
                ? ParseClaude(process, work.Model!)
                : ParseCodex(process, work.Model!);
            return await CompleteAsync(
                work, parsed.Success, parsed.ReplyText, parsed.ErrorMessage,
                work.Model, parsed.TokenUsage, executionContext, shutdown);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log($"project-chat-work-failed workId={work.WorkId} error={ex.GetType().Name}: {ex.Message}");
            return await CompleteAsync(
                work, success: false, replyText: "", error: ex.Message,
                work.Model, tokenUsage: null, executionContext, CancellationToken.None);
        }
        finally
        {
            renewStop.Cancel();
            try { await renewTask; }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _log($"project-chat-renew-loop-ended error={ex.Message}"); }
        }
    }

    /// <summary>
    /// One chat turn through the quota-resolved CAR driver. The read-only
    /// sandbox, shared provider configuration, disabled delegation, and prompt
    /// transport stay identical across CLI families. Returns one raw
    /// <see cref="ProcessResult"/> for the provider-specific response parser.
    /// </summary>
    private async Task<ProcessResult> RunAgentThroughCarAsync(
        RemoteChatWorkItem work,
        string cliType,
        ProjectChatCheckout checkout,
        string contextPrompt,
        CancellationToken timeoutToken,
        CancellationToken shutdown)
    {
        var logDirectory = Path.Combine(Path.GetTempPath(), "agent-chat-car", work.WorkId);
        var runner = new CliRunner(
            new CliOptions
            {
                CodexPath = _options.CodexCliBin,
                ClaudePath = _options.ClaudeCliBin,
                AllowAgentGitMutation = true,
                Delegation = new DelegationOptions { Enabled = false },
            },
            logPaths: new ChatRunLogPathProvider(logDirectory));
        var driver = runner.Get(cliType);
        var stdout = new System.Text.StringBuilder();
        var stderr = new System.Text.StringBuilder();
        var finished = new TaskCompletionSource<CliRunInfo>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnOutput(string id, CarOutputLine line)
        {
            if (!string.Equals(id, work.WorkId, StringComparison.Ordinal)) return;
            if (line.Stream == "stdout") stdout.AppendLine(line.Text);
            else if (line.Stream == "stderr")
            {
                stderr.AppendLine(line.Text);
                _log($"project-chat-{cliType} stderr: {line.Text}");
            }
        }

        void OnFinished(string id, CliRunInfo info)
        {
            if (string.Equals(id, work.WorkId, StringComparison.Ordinal)) finished.TrySetResult(info);
        }

        driver.OnOutput += OnOutput;
        driver.OnFinished += OnFinished;
        try
        {
            var (run, error) = await driver.StartAsync(
                new CliRunRequest
                {
                    RunId = work.WorkId,
                    Prompt = contextPrompt,
                    WorkingDirectory = checkout.RepoPath,
                    Model = work.Model,
                    ThinkingLevel = work.ThinkingLevel,
                    PermissionMode = CliPermissionModes.ReadOnly,
                    ContextMode = CliContextModes.Shared,
                },
                shutdown);
            if (run is null)
                throw new InvalidOperationException($"{cliType} chat run failed to start: {error}");

            var deadline = Task.Delay(Timeout.InfiniteTimeSpan, timeoutToken)
                .ContinueWith(_ => { }, TaskScheduler.Default);
            var completed = await Task.WhenAny(finished.Task, deadline);
            if (!ReferenceEquals(completed, finished.Task))
            {
                driver.Stop(work.WorkId, RunStopReason.Watchdog);
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
            driver.Forget(work.WorkId);
            try { if (Directory.Exists(logDirectory)) Directory.Delete(logDirectory, recursive: true); }
            catch { /* per-turn log hygiene is best effort */ }
        }
    }

    /// <summary>Keeps CAR's per-run output log inside a per-turn temp folder that is deleted with the turn.</summary>
    private sealed class ChatRunLogPathProvider(string directory) : IRunLogPathProvider
    {
        public string GetRunLogDirectory(string runId) => directory;
        public string GetActiveJobsFile() => Path.Combine(directory, "active-runs.json");
    }

    private async Task<int> CompleteAsync(
        RemoteChatWorkItem work,
        bool success,
        string replyText,
        string? error,
        string? model,
        OrchestratorTokenUsage? tokenUsage,
        ChatExecutionContext? executionContext,
        CancellationToken ct)
    {
        var accepted = await _client.CompleteProjectChatWorkAsync(
            new RemoteChatWorkCompletionRequest(
                work.WorkId,
                work.ClaimToken,
                _options.RunnerId,
                success,
                replyText,
                model,
                tokenUsage,
                error,
                executionContext,
                work.CliType,
                work.ConfiguredCliType,
                work.ConfiguredModel,
                work.QuotaFallbackReason),
            ct);
        if (!accepted)
        {
            _log($"project-chat-completion-rejected workId={work.WorkId} reason=stale-claim");
            return 3;
        }
        return success ? 0 : 1;
    }

    private async Task RenewUntilStoppedAsync(RemoteChatWorkItem work, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            var renewed = await _client.RenewProjectChatWorkAsync(
                new RemoteChatWorkRenewRequest(work.WorkId, work.ClaimToken, _options.RunnerId),
                ct);
            if (!renewed)
                throw new InvalidOperationException($"Project-chat claim '{work.WorkId}' became stale.");
        }
    }

    internal static RemoteProjectChatResult ParseCodex(ProcessResult process, string model)
    {
        var replies = new List<string>();
        string? turnError = null;
        OrchestratorTokenUsage? usage = null;
        foreach (var line in process.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var type = root.TryGetProperty("type", out var typeNode) ? typeNode.GetString() : null;
                if (type == "item.completed"
                    && root.TryGetProperty("item", out var item)
                    && item.TryGetProperty("type", out var itemType)
                    && itemType.GetString() == "agent_message"
                    && item.TryGetProperty("text", out var text)
                    && !string.IsNullOrWhiteSpace(text.GetString()))
                {
                    replies.Add(text.GetString()!);
                }
                else if (type == "turn.failed")
                {
                    turnError = root.TryGetProperty("error", out var error)
                                && error.TryGetProperty("message", out var message)
                        ? message.GetString()
                        : "turn failed";
                }
                else if (type == "turn.completed"
                         && root.TryGetProperty("usage", out var usageNode))
                {
                    usage = new OrchestratorTokenUsage
                    {
                        Model = model,
                        InputTokens = ReadInt(usageNode, "input_tokens"),
                        OutputTokens = ReadInt(usageNode, "output_tokens"),
                        CacheReadTokens = ReadInt(usageNode, "cached_input_tokens"),
                    };
                }
            }
            catch (JsonException)
            {
                // stderr and the process exit remain the authoritative failure
                // envelope when the CLI emits a non-protocol line.
            }
        }

        var success = process.ExitCode == 0 && turnError == null;
        var errorMessage = success
            ? null
            : turnError ?? (string.IsNullOrWhiteSpace(process.StdErr)
                ? $"exitCode={process.ExitCode}"
                : process.StdErr.Trim());
        return new RemoteProjectChatResult(
            success,
            string.Join("\n", replies),
            errorMessage,
            usage);
    }

    internal static RemoteProjectChatResult ParseClaude(ProcessResult process, string model)
    {
        string? reply = null;
        string? error = null;
        OrchestratorTokenUsage? usage = null;
        foreach (var line in process.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.TryGetProperty("type", out var type)
                    && type.GetString() == "result")
                {
                    reply = root.TryGetProperty("result", out var resultText)
                        ? resultText.GetString()
                        : reply;
                    if (root.TryGetProperty("is_error", out var isError)
                        && isError.ValueKind == JsonValueKind.True)
                        error = "Claude reported is_error=true.";
                    if (root.TryGetProperty("usage", out var usageNode))
                    {
                        usage = new OrchestratorTokenUsage
                        {
                            Model = model,
                            InputTokens = ReadInt(usageNode, "input_tokens"),
                            OutputTokens = ReadInt(usageNode, "output_tokens"),
                            CacheReadTokens = ReadInt(usageNode, "cache_read_input_tokens"),
                            CacheCreationTokens = ReadInt(usageNode, "cache_creation_input_tokens"),
                        };
                    }
                }
            }
            catch (JsonException)
            {
                // Protocol traffic can contain non-JSON diagnostic lines.
            }
        }

        var success = process.ExitCode == 0 && error is null && !string.IsNullOrWhiteSpace(reply);
        if (!success && error is null)
        {
            error = string.IsNullOrWhiteSpace(process.StdErr)
                ? $"exitCode={process.ExitCode}"
                : process.StdErr.Trim();
        }
        return new RemoteProjectChatResult(success, reply?.Trim() ?? "", error, usage);
    }

    private static int ReadInt(JsonElement node, string property)
        => node.TryGetProperty(property, out var value) && value.TryGetInt64(out var parsed)
            ? (int)Math.Clamp(parsed, 0, int.MaxValue)
            : 0;
}

internal sealed class ProjectChatWorkspace
{
    private readonly RunnerOptions _options;
    private readonly string _projectId;
    private readonly string _repositoryUrl;
    private readonly string _branch;
    private readonly Action<string> _log;

    public ProjectChatWorkspace(
        RunnerOptions options,
        string projectId,
        string repositoryUrl,
        string branch,
        Action<string> log)
    {
        _options = options;
        _projectId = projectId;
        _repositoryUrl = repositoryUrl;
        _branch = branch;
        _log = log;
    }

    private string ProjectCachePath => GitWorkspace.CachePathForProject(_options.WorkDir, _projectId);
    private string SharedRepoPath => Path.Combine(ProjectCachePath, "repo");
    private string RepoPath => Path.Combine(ProjectCachePath, "project-chat");

    public async Task<ProjectChatCheckout> PrepareAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(ProjectCachePath);
        await GitWorkspace.GitMetadataGate.WaitAsync(ct);
        try
        {
            if (!Directory.Exists(Path.Combine(SharedRepoPath, ".git")))
            {
                _log($"project-chat git clone origin -> {SharedRepoPath}");
                await Git(["clone", _repositoryUrl, SharedRepoPath], ProjectCachePath, ct);
            }
            else
            {
                await Git(["remote", "set-url", "origin", _repositoryUrl], SharedRepoPath, ct);
                await Git(["fetch", "origin", "--prune"], SharedRepoPath, ct);
            }

            var remoteRef = $"origin/{_branch}";
            var refCheck = await ProcessRunner.RunAsync(
                "git", ["rev-parse", "--verify", remoteRef], SharedRepoPath, ct: ct);
            if (!refCheck.Success)
                throw new InvalidOperationException($"Project chat branch '{remoteRef}' does not exist.");

            if (Directory.Exists(RepoPath))
            {
                await Git(["reset", "--hard", remoteRef], RepoPath, ct);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(RepoPath)!);
                await Git(["worktree", "add", "--detach", RepoPath, remoteRef], SharedRepoPath, ct);
            }

            var head = (await Git(["rev-parse", "HEAD"], RepoPath, ct)).StdOut.Trim();
            return new ProjectChatCheckout(RepoPath, _branch, head);
        }
        finally
        {
            GitWorkspace.GitMetadataGate.Release();
        }
    }

    private static async Task<ProcessResult> Git(
        IReadOnlyList<string> args,
        string workingDirectory,
        CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync("git", args, workingDirectory, ct: ct);
        if (!result.Success)
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} failed ({result.ExitCode}): {result.StdErr.Trim()}");
        return result;
    }
}

internal sealed record ProjectChatCheckout(string RepoPath, string Branch, string HeadSha);

internal sealed record RemoteProjectChatResult(
    bool Success,
    string ReplyText,
    string? ErrorMessage,
    OrchestratorTokenUsage? TokenUsage);
