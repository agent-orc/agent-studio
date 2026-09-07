using System.Diagnostics;
using System.Text.Json;

namespace AgentStudio.Runner;

/// <summary>
/// Result of an orchestrator decision call: the orchestrator's reply text
/// (the follow-up the user would have typed if they were here), token
/// usage for the call, the captured Claude session id (so the runner can
/// resume the same session on the next call), and a flag for whether the
/// underlying CLI errored.
/// </summary>
/// <remarks>
/// <see cref="ParsedUsage"/> and <see cref="Latency"/> were added when the
/// bus gained context-window + latency tracking. <see cref="TokenUsage"/>
/// remains the legacy view consumed by <c>OrchestratorChatLog</c> and the
/// orchestrator.jsonl writer; new bus emits prefer the richer fields.
/// </remarks>
public sealed record OrchestratorDecisionResult(
    bool Success,
    string ReplyText,
    string Model,
    OrchestratorTokenUsage? TokenUsage,
    string? CapturedSessionId,
    string? ErrorMessage)
{
    public ParsedTurnUsage? ParsedUsage { get; init; }
    public AgentMessageLatency? Latency { get; init; }
}

/// <summary>
/// Invokes the Claude CLI in one-shot JSON mode to produce an orchestrator
/// decision for the user when the active agent emits
/// <c>[[TASK_NEEDS_INPUT:...]]</c> in auto mode (Phase E and later). The
/// CLI returns a single JSON document with the result text plus token
/// usage, both of which we capture and surface in the orchestrator log.
///
/// <para>
/// Why a separate class instead of reusing the Claude execution engine:
/// the engine is built for the long-running streaming task-execution path.
/// The orchestrator's decision calls are short, one-shot, and need exact
/// token-usage capture from the JSON envelope. Mixing those concerns into the
/// streaming engine would force every existing streaming run through the JSON
/// parser path. Cleaner to keep the orchestrator runtime as its own thin
/// shell that only borrows the engine's resolved CLI path.
/// </para>
/// </summary>
public class OrchestratorRunner
{
    public static string DefaultModel => ModelFamilyResolver.Resolve(ModelFamilies.ClaudeHaiku);

    private readonly GenericCliExecutionService _claude;
    private readonly ILogger<OrchestratorRunner> _logger;
    private readonly ICliUsageParser? _claudeUsageParser;
    private readonly ICliModelRegistry? _modelRegistry;
    private readonly CliOneShotRegistry? _oneShotRegistry;

    public OrchestratorRunner(
        GenericCliExecutionService claude,
        ILogger<OrchestratorRunner> logger,
        CliUsageParserRegistry? parsers = null,
        ICliModelRegistry? modelRegistry = null,
        CliOneShotRegistry? oneShotRegistry = null)
    {
        _claude = claude;
        _logger = logger;
        _claudeUsageParser = parsers?.Get("claude");
        _modelRegistry = modelRegistry;
        _oneShotRegistry = oneShotRegistry;
    }

    /// <summary>
    /// Run the orchestrator. <paramref name="prompt"/> is the full prompt
    /// (system framing + situation summary + question). The model defaults
    /// to <see cref="DefaultModel"/> if the caller doesn't override it
    /// from project settings.
    /// </summary>
    public virtual Task<OrchestratorDecisionResult> DecideAsync(
        string prompt,
        string? model,
        string workingDirectory,
        CancellationToken ct = default)
        => InvokeAsync(prompt, model, workingDirectory, resumeSessionId: null, inlineImages: null, ct);

    /// <summary>
    /// Variant of <see cref="DecideAsync"/> that attaches inline image
    /// content blocks to the user message. The orchestrator chat path
    /// uses this when the user pastes a screenshot into the composer: the
    /// model sees the image alongside the text, no Read tool call needed.
    /// </summary>
    public virtual Task<OrchestratorDecisionResult> DecideAsync(
        string prompt,
        string? model,
        string workingDirectory,
        IReadOnlyList<CliOneShotImage>? inlineImages,
        CancellationToken ct = default)
        => InvokeAsync(prompt, model, workingDirectory, resumeSessionId: null, inlineImages, ct);

    /// <summary>
    /// Run an operator-selected Codex model for the GPT-only Orchestrator
    /// composer. This path deliberately has no Claude fallback. The model and
    /// reasoning values come from the live Codex catalogue exposed by the UI.
    /// </summary>
    public virtual async Task<OrchestratorDecisionResult> DecideCodexAsync(
        string prompt,
        string model,
        string? thinkingLevel,
        string workingDirectory,
        CancellationToken ct = default)
    {
        var oneShot = _oneShotRegistry?.Get(CliTypes.Codex)
            ?? throw new InvalidOperationException("Codex one-shot execution is not registered.");
        var result = await oneShot.RunAsync(new CliOneShotRequest(
            CliType: CliTypes.Codex,
            Model: model,
            Prompt: prompt)
        {
            ThinkingLevel = thinkingLevel,
            WorkingDirectory = workingDirectory,
            Timeout = DefaultTimeout,
            Source = "orchestrator-chat",
            RecordUsage = false,
        }, ct).ConfigureAwait(false);

        if (!result.Ok)
        {
            var error = !string.IsNullOrWhiteSpace(result.Stderr)
                ? result.Stderr.Trim()
                : result.Error ?? result.Stdout.Trim();
            return new OrchestratorDecisionResult(false, result.ParsedText, model, result.Usage, null, error)
            {
                Latency = result.Latency,
                ParsedUsage = result.RichUsage,
            };
        }

        return new OrchestratorDecisionResult(true, result.ParsedText, model, result.Usage, null, null)
        {
            Latency = result.Latency,
            ParsedUsage = result.RichUsage,
        };
    }

    /// <summary>
    /// Resume an existing orchestrator session via <c>claude -r &lt;sessionId&gt;</c>.
    /// The session keeps the boot-time context (project facts, recent
    /// activity) and accumulates conversation history, so subsequent
    /// decisions cost less on framing.
    /// </summary>
    public virtual Task<OrchestratorDecisionResult> ResumeAsync(
        string sessionId,
        string prompt,
        string? model,
        string workingDirectory,
        CancellationToken ct = default)
        => InvokeAsync(prompt, model, workingDirectory, resumeSessionId: sessionId, inlineImages: null, ct);

    /// <summary>
    /// Variant of <see cref="ResumeAsync"/> that attaches inline image
    /// content blocks to the resumed user message.
    /// </summary>
    public virtual Task<OrchestratorDecisionResult> ResumeAsync(
        string sessionId,
        string prompt,
        string? model,
        string workingDirectory,
        IReadOnlyList<CliOneShotImage>? inlineImages,
        CancellationToken ct = default)
        => InvokeAsync(prompt, model, workingDirectory, resumeSessionId: sessionId, inlineImages, ct);

    /// <summary>
    /// Resume a session and transparently fall back to a fresh one-shot if
    /// the CLI rejects the session id (Anthropic rotates ids on retention
    /// expiry, typically &gt;24h idle). This is the canonical recovery path
    /// for every orchestrator caller — embedding it on the runner makes the
    /// rejection-recovery semantics impossible to skip and prevents the
    /// per-caller drift that produced the 2026-05-11 "No conversation found
    /// with session ID: ..." stuck-chat bug in the global orchestrator chat.
    ///
    /// <para>
    /// Contract: when <see cref="ResumeAsync"/> succeeds, the returned
    /// result is the resume result and <paramref name="onSessionRejected"/>
    /// is NOT called. When the resume fails with a rejection-shaped error,
    /// <paramref name="onSessionRejected"/> runs first (callers use it to
    /// clear their persisted session record), then
    /// <paramref name="fallbackPromptBuilder"/> is invoked to produce the
    /// prompt for a fresh one-shot <see cref="DecideAsync"/> call. The
    /// fallback's result is returned. Non-rejection failures (timeout,
    /// network) propagate as-is without firing the callback.
    /// </para>
    /// </summary>
    public virtual Task<OrchestratorDecisionResult> ResumeWithFallbackAsync(
        string sessionId,
        string resumePrompt,
        Func<string> fallbackPromptBuilder,
        Action onSessionRejected,
        string? model,
        string workingDirectory,
        CancellationToken ct = default)
        => ResumeWithFallbackAsync(sessionId, resumePrompt, fallbackPromptBuilder, onSessionRejected, model, workingDirectory, inlineImages: null, ct);

    /// <summary>
    /// Variant of <see cref="ResumeWithFallbackAsync"/> that carries inline
    /// image content blocks on both the resume attempt and the fallback
    /// one-shot. Used by the orchestrator chat multimodal path.
    /// </summary>
    public virtual async Task<OrchestratorDecisionResult> ResumeWithFallbackAsync(
        string sessionId,
        string resumePrompt,
        Func<string> fallbackPromptBuilder,
        Action onSessionRejected,
        string? model,
        string workingDirectory,
        IReadOnlyList<CliOneShotImage>? inlineImages,
        CancellationToken ct = default)
    {
        var result = await ResumeAsync(sessionId, resumePrompt, model, workingDirectory, inlineImages, ct).ConfigureAwait(false);
        if (result.Success || !IsSessionRejection(result.ErrorMessage))
            return result;

        _logger.LogWarning(
            "[orchestrator] resume rejected for session {SessionId}; falling back to one-shot. error={Err}",
            sessionId, result.ErrorMessage);

        try { onSessionRejected(); }
        catch (Exception ex) { _logger.LogDebug(ex, "onSessionRejected callback threw"); }

        var fallbackPrompt = fallbackPromptBuilder();
        return await DecideAsync(fallbackPrompt, model, workingDirectory, inlineImages, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// True when an <see cref="OrchestratorDecisionResult.ErrorMessage"/>
    /// looks like a rejected resume target. Claude reports these as
    /// "No conversation found with session ID: ..." on stdout; the runner
    /// surfaces stdout as the error string when the CLI exits non-zero
    /// so this matcher catches the exact shape plus the looser variants
    /// observed across CLI versions.
    /// </summary>
    public static bool IsSessionRejection(string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage)) return false;
        return errorMessage.Contains("No conversation found", StringComparison.OrdinalIgnoreCase)
            || errorMessage.Contains("session", StringComparison.OrdinalIgnoreCase)
            || errorMessage.Contains("not found", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
     /// Hard ceiling for one orchestrator decision call. The boot prompt
     /// embeds README/AGENTS/ROADMAP and the boot reply does real reading,
     /// so even Opus can take ~60-90s. Keep this generous; the watchdog
     /// surfaces "still running" via the orchestrator log instead. Caller
     /// can override per call (tests use a low value to force the timeout
     /// path deterministically).
     /// </summary>
     public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    private async Task<OrchestratorDecisionResult> InvokeAsync(
        string prompt,
        string? model,
        string workingDirectory,
        string? resumeSessionId,
        IReadOnlyList<CliOneShotImage>? inlineImages,
        CancellationToken ct)
    {
        var modelId = string.IsNullOrWhiteSpace(model) ? DefaultModel : model!.Trim();

        // Production path: route through ICliOneShot (single point of code).
        // The OneShot service captures latency, parses tokens via
        // ClaudeUsageParser, and feeds the prompt via stdin (the failure
        // mode this file's long comment block exists to prevent).
        var oneShot = _oneShotRegistry?.Get("claude");
        if (oneShot != null)
        {
            var extras = string.IsNullOrWhiteSpace(resumeSessionId)
                ? null
                : new[] { "-r", resumeSessionId! };

            var r = await oneShot.RunAsync(new CliOneShotRequest(
                CliType: "claude", Model: modelId, Prompt: prompt)
            {
                WorkingDirectory = workingDirectory,
                Timeout = DefaultTimeout,
                ExtraArgs = extras,
                InlineImages = inlineImages,
                RecordUsage = false, // The orchestrator path has its own bookkeeping
            }, ct).ConfigureAwait(false);

            if (!r.Ok)
            {
                // Resume errors land on stdout (claude quirk); surface as the error string
                // so the policy layer can detect and fall back to a fresh one-shot.
                var combined = string.IsNullOrWhiteSpace(r.Stderr)
                    ? (string.IsNullOrWhiteSpace(r.Stdout) ? r.Error ?? "CLI failure" : r.Stdout.Trim())
                    : r.Stderr.Trim();
                _logger.LogWarning(
                    "Orchestrator decision failed via OneShot: exit={Exit}, stdout={Stdout}, stderr={Stderr}",
                    r.ExitCode, r.Stdout?.Trim(), r.Stderr?.Trim());
                return new OrchestratorDecisionResult(false, "", modelId, null, null, combined)
                {
                    Latency = r.Latency,
                };
            }

            var parsed = ParseResult(r.Stdout, modelId);
            // OneShot already produced ParsedTurnUsage with the context-window
            // snapshot from the same parser. Prefer that over a re-derivation.
            return parsed with
            {
                Latency = r.Latency,
                ParsedUsage = r.RichUsage ?? parsed.ParsedUsage,
            };
        }

        // Fallback (legacy tests): inline implementation, still stdin-piped.
        var (args, _) = BuildArgs(modelId, resumeSessionId);

        var psi = new ProcessStartInfo
        {
            FileName = GenericCliExecutionService.ResolveExecutable(_claude.GetCliPath()),
            Arguments = string.Join(' ', args),
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        psi.Environment["LC_ALL"] = "C.UTF-8";
        psi.Environment["LANG"] = "C.UTF-8";

        // Bound the call so a hung CLI cannot pin the orchestrator forever.
        // The token chains the caller's ct with the timeout. Cancellation on
        // either source surfaces a typed timeout/cancelled error so the
        // policy layer can decide what to do (boot retries; auto-mode
        // surfaces the question to the user).
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(DefaultTimeout);
        var effectiveCt = timeoutCts.Token;

        // Latency capture: requestedAt = moment we send the prompt to the CLI;
        // completedAt = moment the CLI exits. firstTokenAt is unavailable on
        // -p (one-shot, the CLI buffers and emits a single JSON blob at exit),
        // so we leave it null on this path; the streaming task agent path
        // (Claude stream-json) populates it from the first OutputDelta.
        var requestedAt = DateTime.UtcNow;
        try
        {
            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.Start();

            // Pipe the prompt via stdin instead of a quoted -p argument.
            // The boot prompt embeds README/AGENTS/ROADMAP markdown plus
            // recent activity, easily pushing into the multi-KB range
            // with newlines, backticks, double quotes, and backslashes.
            // Passing that as a single quoted argument on Windows breaks
            // through cmd.exe's command-line length and quoting limits;
            // the failure mode in production was the CLI receiving the
            // prompt with --output-format dropped from the args, then
            // returning prose ("I'll wait for...") that ParseResult
            // rejected with "'I' is an invalid start of a value". stdin
            // sidesteps the entire quoting/length surface.
            try
            {
                await process.StandardInput.WriteAsync(prompt.AsMemory(), effectiveCt);
                await process.StandardInput.FlushAsync(effectiveCt);
            }
            finally
            {
                try { process.StandardInput.Close(); } catch (Exception __ex) { SilentCatch.Note(__ex, "OrchestratorRunner:331"); }
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(effectiveCt);
            var stderrTask = process.StandardError.ReadToEndAsync(effectiveCt);

            await process.WaitForExitAsync(effectiveCt);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                // claude puts session-resume errors ("No conversation found
                // with session ID: ...") on STDOUT, not stderr, then exits 1.
                // We surface stdout into the error string so the policy layer
                // (ProjectRunner.RunOrchestratorDecisionAsync) can detect and
                // fall back to a fresh one-shot. Without this, the runner
                // silently dropped the auto-mode decision instead of
                // recovering.
                var combined = string.IsNullOrWhiteSpace(stderr)
                    ? (string.IsNullOrWhiteSpace(stdout)
                        ? $"claude CLI exited with code {process.ExitCode}"
                        : stdout.Trim())
                    : stderr.Trim();
                _logger.LogWarning(
                    "Orchestrator decision failed: exit={Exit}, stdout={Stdout}, stderr={Stderr}",
                    process.ExitCode, stdout?.Trim(), stderr?.Trim());
                return new OrchestratorDecisionResult(false, "", modelId, null, null, combined);
            }

            var completedAt = DateTime.UtcNow;
            var result = ParseResult(stdout, modelId);
            return EnrichWithLatencyAndContext(result, requestedAt, completedAt);
        }
        catch (OperationCanceledException)
        {
            // Distinguish caller-cancelled from timeout so the policy layer
            // can react: timeout means the CLI hung; cancellation means the
            // app is shutting down. Either way the process is killed below
            // by the using-dispose; we just surface the right reason.
            var reason = !ct.IsCancellationRequested && timeoutCts.IsCancellationRequested
                ? $"timeout after {DefaultTimeout.TotalSeconds:F0}s"
                : "cancelled";
            _logger.LogWarning("Orchestrator decision {Reason}", reason);
            return new OrchestratorDecisionResult(false, "", modelId, null, null, reason);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Orchestrator decision call failed to spawn or read");
            return new OrchestratorDecisionResult(false, "", modelId, null, null, ex.Message);
        }
    }

    /// <summary>
    /// Build the argv list for one orchestrator invocation. Public so the
    /// args contract can be locked by a unit test - the load-bearing rule
    /// is "no prompt content in argv; prompt is piped via stdin", because
    /// embedding multi-KB markdown into a Windows command line is the bug
    /// class this rewrite exists to prevent.
    /// </summary>
    public static (List<string> Args, string ModelId) BuildArgs(string? model, string? resumeSessionId)
    {
        var modelId = string.IsNullOrWhiteSpace(model) ? DefaultModel : model!.Trim();
        var args = new List<string>
        {
            "-p",                                     // print mode; reads prompt from stdin (no positional arg)
            "--output-format", "json",
            "--model", Quote(modelId),
            "--dangerously-skip-permissions"
        };
        if (!string.IsNullOrWhiteSpace(resumeSessionId))
        {
            args.Add("-r");
            args.Add(Quote(resumeSessionId!));
        }
        return (args, modelId);
    }

    /// <summary>
    /// Parse the single JSON document <c>claude -p ... --output-format json</c>
    /// emits at completion. Shape (validated against live CLI):
    /// <c>{ type: "result", subtype: "success", is_error, result, session_id,
    /// total_cost_usd, usage: { input_tokens, cache_creation_input_tokens,
    /// cache_read_input_tokens, output_tokens }, model? }</c>.
    /// Tolerant: missing fields default to zero / empty.
    /// </summary>
    public static OrchestratorDecisionResult ParseResult(string stdout, string modelId)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return new OrchestratorDecisionResult(false, "", modelId, null, null, "empty stdout");

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;

            var resultText = root.TryGetProperty("result", out var r) ? r.GetString() ?? "" : "";
            var isError = root.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True;
            string? declaredModel = root.TryGetProperty("model", out var md) ? md.GetString() : null;
            string? sessionId = root.TryGetProperty("session_id", out var sid) ? sid.GetString() : null;

            OrchestratorTokenUsage? usage = null;
            if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
            {
                usage = new OrchestratorTokenUsage
                {
                    Model = declaredModel ?? modelId,
                    InputTokens          = TryGetInt(u, "input_tokens"),
                    OutputTokens         = TryGetInt(u, "output_tokens"),
                    CacheReadTokens      = TryGetInt(u, "cache_read_input_tokens"),
                    CacheCreationTokens  = TryGetInt(u, "cache_creation_input_tokens")
                };
            }

            return new OrchestratorDecisionResult(
                Success: !isError && !string.IsNullOrWhiteSpace(resultText),
                ReplyText: resultText.Trim(),
                Model: declaredModel ?? modelId,
                TokenUsage: usage,
                CapturedSessionId: string.IsNullOrWhiteSpace(sessionId) ? null : sessionId,
                ErrorMessage: isError ? "is_error=true in CLI output" : null);
        }
        catch (Exception ex)
        {
            return new OrchestratorDecisionResult(false, "", modelId, null, null, $"parse failed: {ex.Message}");
        }
    }

    private static int TryGetInt(JsonElement obj, string name)
    {
        if (obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n))
            return n;
        return 0;
    }

    /// <summary>
    /// Attach latency + context-window data parsed from the raw CLI JSON to
    /// the decision result. Best-effort: if the parser registry was not
    /// injected (legacy test ctor) the result is returned unchanged.
    /// </summary>
    private OrchestratorDecisionResult EnrichWithLatencyAndContext(
        OrchestratorDecisionResult result,
        DateTime requestedAtUtc,
        DateTime completedAtUtc)
    {
        var totalMs = (long)(completedAtUtc - requestedAtUtc).TotalMilliseconds;
        var latency = new AgentMessageLatency(
            RequestedAt: requestedAtUtc,
            FirstTokenAt: null,
            CompletedAt: completedAtUtc,
            TtfbMs: null,
            TotalMs: totalMs);

        ParsedTurnUsage? parsed = null;
        if (_claudeUsageParser is not null && _modelRegistry is not null && result.TokenUsage is not null)
        {
            // Re-shape via the parser so the context-window block is computed
            // from the same model-registry the rest of the bus uses.
            parsed = new ParsedTurnUsage(
                Model: result.TokenUsage.Model,
                Input: result.TokenUsage.InputTokens,
                Output: result.TokenUsage.OutputTokens,
                CacheRead: result.TokenUsage.CacheReadTokens,
                CacheWrite: result.TokenUsage.CacheCreationTokens,
                ReasoningOutput: null,
                ContextWindow: BuildContextWindow(result.TokenUsage, _modelRegistry));
        }

        return result with { Latency = latency, ParsedUsage = parsed };
    }

    private static AgentMessageContextWindow? BuildContextWindow(OrchestratorTokenUsage usage, ICliModelRegistry registry)
    {
        var total = registry.TotalContextSize(usage.Model);
        long used = usage.InputTokens + usage.CacheReadTokens;
        if (total is null && used == 0) return null;
        return new AgentMessageContextWindow(
            TotalSize: total,
            Used: used,
            Remaining: total is { } t ? Math.Max(0, t - used) : null);
    }

    private static string Quote(string s) => $"\"{s.Replace("\"", "\\\"")}\"";
}
