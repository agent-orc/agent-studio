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
    public string? CliType { get; init; }
    public string? ConfiguredModel { get; init; }
    public bool QuotaFallback { get; init; }
    public string? QuotaFallbackReason { get; init; }
    public QuotaAdmissionPlan? QuotaAdmission { get; init; }
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
        _ = claude; // Retained in the constructor for source compatibility with test hosts.
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
    /// Runs an explicitly selected non-Claude CLI for a context-session turn.
    /// Claude keeps using the resumable path above; other CLIs share the
    /// one-shot registry and retain their model and thinking-level receipt.
    /// </summary>
    public virtual async Task<OrchestratorDecisionResult> DecideWithCliAsync(
        string cliType,
        string prompt,
        string? model,
        string? thinkingLevel,
        string workingDirectory,
        CancellationToken ct = default)
    {
        var cli = cliType.Trim().ToLowerInvariant();
        if (cli == CliTypes.Claude)
            return await DecideAsync(prompt, model, workingDirectory, ct).ConfigureAwait(false);
        if (!CliTypes.IsValid(cli))
            throw new InvalidOperationException($"Unsupported orchestrator CLI '{cliType}'.");
        var oneShot = _oneShotRegistry?.Get(cli)
            ?? throw new InvalidOperationException($"{cli} one-shot execution is not registered.");
        var configuredModel = string.IsNullOrWhiteSpace(model)
            ? ModelMetadataRegistry.DefaultForCli(cli) ?? ""
            : model.Trim();
        var result = await oneShot.RunAsync(new CliOneShotRequest(
            CliType: cli,
            Model: configuredModel,
            Prompt: prompt)
        {
            ThinkingLevel = thinkingLevel,
            WorkingDirectory = workingDirectory,
            Timeout = DefaultTimeout,
            Source = "orchestrator-session",
            Project = ProjectNameFromWorkingDirectory(workingDirectory),
            RecordUsage = false,
        }, ct).ConfigureAwait(false);
        var error = result.Ok
            ? null
            : !string.IsNullOrWhiteSpace(result.Stderr)
                ? result.Stderr.Trim()
                : result.Error ?? result.Stdout.Trim();
        return new OrchestratorDecisionResult(
            result.Ok,
            result.ParsedText,
            result.EffectiveModel ?? configuredModel,
            result.Usage,
            CapturedSessionId: null,
            error)
        {
            Latency = result.Latency,
            ParsedUsage = result.RichUsage,
            CliType = result.EffectiveCliType ?? cli,
            ConfiguredModel = configuredModel,
            QuotaFallback = result.QuotaAdmission?.IsFallback == true,
            QuotaFallbackReason = result.QuotaAdmission?.IsFallback == true
                ? result.QuotaAdmission.Reason
                : null,
            QuotaAdmission = result.QuotaAdmission,
        };
    }

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
    /// Run an operator-selected Codex model for the GPT-first Orchestrator
    /// composer. Quota admission may select an equivalence-tier provider for
    /// this invocation without changing the operator's configured model.
    /// </summary>
    public virtual async Task<OrchestratorDecisionResult> DecideCodexAsync(
        string prompt,
        string model,
        string? thinkingLevel,
        string workingDirectory,
        CancellationToken ct = default,
        string? projectName = null,
        string? watchPath = null)
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
            Project = string.IsNullOrWhiteSpace(projectName)
                ? ProjectNameFromWorkingDirectory(workingDirectory)
                : projectName,
            WatchPath = watchPath,
            RecordUsage = false,
        }, ct).ConfigureAwait(false);

        if (!result.Ok)
        {
            var error = !string.IsNullOrWhiteSpace(result.Stderr)
                ? result.Stderr.Trim()
                : result.Error ?? result.Stdout.Trim();
            return new OrchestratorDecisionResult(
                false,
                result.ParsedText,
                result.EffectiveModel ?? model,
                result.Usage,
                null,
                error)
            {
                Latency = result.Latency,
                ParsedUsage = result.RichUsage,
                CliType = result.EffectiveCliType ?? CliTypes.Codex,
                ConfiguredModel = model,
                QuotaFallback = result.QuotaAdmission?.IsFallback == true,
                QuotaFallbackReason = result.QuotaAdmission?.IsFallback == true
                    ? result.QuotaAdmission.Reason
                    : null,
                QuotaAdmission = result.QuotaAdmission,
            };
        }

        return new OrchestratorDecisionResult(
            true,
            result.ParsedText,
            result.EffectiveModel ?? model,
            result.Usage,
            null,
            null)
        {
            Latency = result.Latency,
            ParsedUsage = result.RichUsage,
            CliType = result.EffectiveCliType ?? CliTypes.Codex,
            ConfiguredModel = model,
            QuotaFallback = result.QuotaAdmission?.IsFallback == true,
            QuotaFallbackReason = result.QuotaAdmission?.IsFallback == true
                ? result.QuotaAdmission.Reason
                : null,
            QuotaAdmission = result.QuotaAdmission,
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
                Source = "orchestrator-decision",
                Project = ProjectNameFromWorkingDirectory(workingDirectory),
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
                return new OrchestratorDecisionResult(
                    false,
                    "",
                    r.EffectiveModel ?? modelId,
                    null,
                    null,
                    combined)
                {
                    Latency = r.Latency,
                    CliType = r.EffectiveCliType ?? CliTypes.Claude,
                    ConfiguredModel = modelId,
                    QuotaFallback = r.QuotaAdmission?.IsFallback == true,
                    QuotaFallbackReason = r.QuotaAdmission?.IsFallback == true
                        ? r.QuotaAdmission.Reason
                        : null,
                    QuotaAdmission = r.QuotaAdmission,
                };
            }

            var effectiveCli = r.EffectiveCliType ?? CliTypes.Claude;
            var effectiveModel = r.EffectiveModel ?? modelId;
            var parsed = string.Equals(effectiveCli, CliTypes.Claude, StringComparison.OrdinalIgnoreCase)
                ? ParseResult(r.Stdout, effectiveModel)
                : new OrchestratorDecisionResult(
                    true,
                    r.ParsedText,
                    effectiveModel,
                    r.Usage,
                    CapturedSessionId: null,
                    ErrorMessage: null);
            // OneShot already produced ParsedTurnUsage with the context-window
            // snapshot from the same parser. Prefer that over a re-derivation.
            return parsed with
            {
                Latency = r.Latency,
                ParsedUsage = r.RichUsage ?? parsed.ParsedUsage,
                CliType = effectiveCli,
                ConfiguredModel = modelId,
                QuotaFallback = r.QuotaAdmission?.IsFallback == true,
                QuotaFallbackReason = r.QuotaAdmission?.IsFallback == true
                    ? r.QuotaAdmission.Reason
                    : null,
                QuotaAdmission = r.QuotaAdmission,
            };
        }

        return new OrchestratorDecisionResult(
            false,
            "",
            modelId,
            null,
            null,
            "Claude one-shot registry is not configured");
    }

    private static string? ProjectNameFromWorkingDirectory(string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory)) return null;
        return Path.GetFileName(Path.TrimEndingDirectorySeparator(workingDirectory));
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
                    CacheCreationTokens  = TryGetInt(u, "cache_creation_input_tokens"),
                    InputIncludesCached  = false,
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
                ContextWindow: BuildContextWindow(result.TokenUsage, _modelRegistry),
                InputIncludesCached: result.TokenUsage.InputIncludesCached ?? false);
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
