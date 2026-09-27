using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AgentStudio.Cli;

/// <summary>
/// Canonical Claude one-shot CLI runner. Stdin-piped prompt, JSON output
/// envelope, stderr captured, exit code surfaced, latency measured, token
/// + context-window data parsed via <see cref="ClaudeUsageParser"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the prompt goes through stdin, not <c>-p &lt;prompt&gt;</c>.</b>
/// Boot prompts, aspect prompts, and orchestrator-decision prompts
/// regularly embed README/AGENTS/ROADMAP markdown plus recent activity,
/// easily pushing into the multi-KB range with newlines, backticks,
/// double quotes, and backslashes. Passing that as a single quoted
/// argument on Windows breaks through cmd.exe's command-line length
/// and quoting limits; the failure mode in production is the CLI
/// receiving the prompt with <c>--output-format</c> dropped from the
/// args, then returning prose ("I'll wait for...") that ParseResult
/// rejects with "'I' is an invalid start of a value". stdin sidesteps
/// the entire quoting/length surface.
/// </para>
/// <para>
/// All eight legacy one-shot helpers used to duplicate this lifecycle;
/// three of them got the stdin detail wrong. This service is the single
/// point of code so future call sites cannot drift.
/// </para>
/// </remarks>
public sealed class ClaudeOneShot : ICliOneShot
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    private readonly IConfiguration _configuration;
    private readonly ILogger<ClaudeOneShot> _logger;
    private readonly ICliUsageParser _claudeUsage;
    private readonly ICliModelRegistry _modelRegistry;
    private readonly AdHocUsageRecorder? _usage;

    public ClaudeOneShot(
        IConfiguration configuration,
        ILogger<ClaudeOneShot> logger,
        CliUsageParserRegistry parsers,
        ICliModelRegistry modelRegistry,
        AdHocUsageRecorder? usage = null)
    {
        _configuration = configuration;
        _logger = logger;
        _claudeUsage = parsers.Get("claude") ?? new ClaudeUsageParser();
        _modelRegistry = modelRegistry;
        _usage = usage;
    }

    public string CliType => "claude";

    public async Task<CliOneShotResult> RunAsync(CliOneShotRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var cliPath = _configuration["ClaudeCli:Path"] ?? "claude";
        var executable = GenericCliExecutionService.ResolveExecutable(cliPath);
        var timeout = request.Timeout ?? DefaultTimeout;

        // CAR owns npm-shim healing for CAR-backed agent runs. One-shot calls
        // do not use CAR yet, so retain their existing best-effort repair until
        // T4 removes this temporary non-agent invocation exception.
        if (!QuickProbe(executable))
        {
            _logger.LogWarning(
                "claude --version failed pre-OneShot at '{Path}'; running rollback NpmShimHealer", executable);
            var outcome = await NpmShimHealer.TryHealClaudeAsync(_logger, ct);
            if (outcome.Actions.Count > 0)
            {
                _logger.LogInformation(
                    "Rollback NpmShimHealer (one-shot) actions for claude: {Actions}",
                    string.Join("; ", outcome.Actions));
            }
            // Preserve the established one-shot contract: a failed repair does
            // not abort here. The spawn below returns the existing SpawnFailure
            // result and its callers apply their own fallback policy.
        }

        var psi = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = request.WorkingDirectory ?? Directory.GetCurrentDirectory(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["LC_ALL"] = "C.UTF-8";
        psi.Environment["LANG"] = "C.UTF-8";
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("json");
        psi.ArgumentList.Add("--model");
        psi.ArgumentList.Add(request.Model);
        foreach (var arg in CodingAgentRunner.Model.CliReasoningFlags.For(CliTypes.Claude, request.Model, request.ThinkingLevel))
            psi.ArgumentList.Add(arg);
        psi.ArgumentList.Add("--dangerously-skip-permissions");
        var multimodal = request.InlineImages is { Count: > 0 };
        if (multimodal)
        {
            // Switch to stream-json input so the prompt + image content
            // blocks land on the model as one user message instead of a
            // text-only stdin pipe. See BuildStreamJsonUserMessage for the
            // exact envelope shape.
            psi.ArgumentList.Add("--input-format");
            psi.ArgumentList.Add("stream-json");
            psi.ArgumentList.Add("--verbose");
        }
        if (request.ExtraArgs is { Count: > 0 } extras)
        {
            foreach (var arg in extras) psi.ArgumentList.Add(arg);
        }

        var requestedAt = DateTime.UtcNow;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var effectiveCt = timeoutCts.Token;

        Process? p = null;
        try
        {
            p = Process.Start(psi);
            if (p == null)
            {
                var completedAt = DateTime.UtcNow;
                var fail = CliOneShotResult.SpawnFailure("Process.Start returned null", requestedAt, completedAt);
                RecordIfRequested(request, fail);
                return fail;
            }

            // stdin-piped prompt. See class-level remarks for the why.
            // Multimodal path emits a single NDJSON line wrapping the
            // prompt + image content blocks in the stream-json envelope
            // the Claude CLI expects when --input-format stream-json is
            // active. Text-only path stays as a raw prompt string so the
            // existing CLI defaults (input-format text) apply unchanged.
            try
            {
                if (multimodal)
                {
                    var envelope = BuildStreamJsonUserMessage(request.Prompt, request.InlineImages!);
                    await p.StandardInput.WriteAsync(envelope.AsMemory(), effectiveCt).ConfigureAwait(false);
                }
                else
                {
                    await p.StandardInput.WriteAsync(request.Prompt.AsMemory(), effectiveCt).ConfigureAwait(false);
                }
                await p.StandardInput.FlushAsync(effectiveCt).ConfigureAwait(false);
            }
            finally
            {
                try { p.StandardInput.Close(); } catch (Exception __ex) { SilentCatch.Note(__ex, "ClaudeOneShot: idempotent"); /* idempotent */ }
            }

            var stdoutTask = p.StandardOutput.ReadToEndAsync(effectiveCt);
            var stderrTask = p.StandardError.ReadToEndAsync(effectiveCt);
            await p.WaitForExitAsync(effectiveCt).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            var completedAtOk = DateTime.UtcNow;

            var result = BuildResult(p.ExitCode, stdout, stderr, requestedAt, completedAtOk, request.Model);
            if (!result.Ok && !string.IsNullOrWhiteSpace(stderr))
            {
                _logger.LogWarning(
                    "Claude one-shot non-zero exit {ExitCode} ({Source}, job={Job}): {Stderr}",
                    p.ExitCode, request.Source ?? "(none)", request.JobId ?? "(none)", stderr.Trim());
            }
            RecordIfRequested(request, result);
            return result;
        }
        catch (OperationCanceledException)
        {
            var completedAt = DateTime.UtcNow;
            var reason = !ct.IsCancellationRequested && timeoutCts.IsCancellationRequested
                ? $"timeout after {timeout.TotalSeconds:F0}s"
                : "cancelled";
            _logger.LogWarning("Claude one-shot {Reason} (source={Source}, job={Job})",
                reason, request.Source ?? "(none)", request.JobId ?? "(none)");
            AgentStudio.Diagnostics.CliKillAudit.Trace(p, "ClaudeOneShot:201 timeout/cancel (entireProcessTree)");
            try { p?.Kill(entireProcessTree: true); } catch (Exception __ex) { SilentCatch.Note(__ex, "ClaudeOneShot: best-effort"); /* best-effort */ }
            var fail = CliOneShotResult.SpawnFailure(reason, requestedAt, completedAt);
            RecordIfRequested(request, fail);
            return fail;
        }
        catch (Exception ex)
        {
            var completedAt = DateTime.UtcNow;
            // ExceptionType is named explicitly because the bare message
            // ("The pipe is being closed.", "Cannot access a closed pipe.")
            // is ambiguous across IOException / InvalidOperationException /
            // ObjectDisposedException - the type narrows the post-mortem.
            _logger.LogError(ex,
                "Claude one-shot crashed (source={Source}, job={Job}, exceptionType={ExceptionType}): {Raw}",
                request.Source ?? "(none)", request.JobId ?? "(none)", ex.GetType().Name, ex.Message);
            AgentStudio.Diagnostics.CliKillAudit.Trace(p, "ClaudeOneShot:216 crash (entireProcessTree)");
            try { p?.Kill(entireProcessTree: true); } catch (Exception __ex) { SilentCatch.Note(__ex, "ClaudeOneShot:220"); }
            var fail = CliOneShotResult.SpawnFailure(ex.Message, requestedAt, completedAt);
            RecordIfRequested(request, fail);
            return fail;
        }
        finally
        {
            p?.Dispose();
        }
    }

    private CliOneShotResult BuildResult(int exitCode, string stdout, string stderr, DateTime requestedAt, DateTime completedAt, string fallbackModel)
    {
        var totalMs = (long)(completedAt - requestedAt).TotalMilliseconds;
        var latency = new AgentMessageLatency(
            RequestedAt: requestedAt,
            FirstTokenAt: null, // one-shot mode buffers; streaming path would populate this
            CompletedAt: completedAt,
            TtfbMs: null,
            TotalMs: totalMs);

        // ParseOrFallback: when the CLI returned the JSON wrapper, extract
        // the .result text and .usage; otherwise treat stdout as the reply
        // verbatim. Pre-existing helper; we reuse it so test fakes that
        // return raw text still work.
        var (parsedText, usage) = AdHocClaudeInvoker.ParseOrFallback(stdout, fallbackModel);

        // Rich usage with context-window snapshot (new bus dimension).
        ParsedTurnUsage? rich = null;
        string? providerSessionId = null;
        if (!string.IsNullOrWhiteSpace(stdout) && stdout.TrimStart().StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(stdout);
                if (doc.RootElement.TryGetProperty("session_id", out var sessionId))
                    providerSessionId = sessionId.GetString();
                if (_claudeUsage.TryParse(doc.RootElement, fallbackModel, _modelRegistry, out var parsed))
                {
                    rich = parsed;
                }
            }
            catch (JsonException __ex)
            {
                SilentCatch.Note(__ex, "ClaudeOneShot: Wrapper present but malformed; fall through. ParseOrFallback");
                // Wrapper present but malformed; fall through. ParseOrFallback
                // already returned the raw stdout as ParsedText so the call
                // site still sees the text.
            }
        }

        var ok = exitCode == 0;
        return new CliOneShotResult(
            Ok: ok,
            ExitCode: exitCode,
            Stdout: stdout ?? string.Empty,
            Stderr: stderr ?? string.Empty,
            Duration: completedAt - requestedAt,
            ParsedText: parsedText ?? string.Empty,
            Usage: usage,
            RichUsage: rich,
            Latency: latency,
            Error: ok ? null : $"exitCode={exitCode}{(string.IsNullOrWhiteSpace(stderr) ? "" : $"; stderr={stderr.Trim()}")}")
        { ProviderSessionId = providerSessionId };
    }

    private void RecordIfRequested(CliOneShotRequest request, CliOneShotResult result)
    {
        if (!request.RecordUsage || _usage == null) return;
        AdHocClaudeInvoker.Record(
            _usage,
            request.Source ?? AdHocUsageSources.ReviewDecision,
            request.Model,
            result.Usage,
            (long)result.Duration.TotalMilliseconds,
            ok: result.Ok,
            project: request.Project,
            jobId: request.JobId);
    }

    /// <summary>
    /// Build the single-line NDJSON envelope the Claude CLI expects when
    /// <c>--input-format stream-json</c> is active. The envelope wraps the
    /// prompt as a text content block plus one image content block per
    /// supplied <see cref="CliOneShotImage"/>; this is the exact shape the
    /// Anthropic SDK uses for multimodal user messages, so the model sees
    /// the image alongside the text in the same turn.
    /// <para>
    /// Public so a unit test can pin the envelope shape - a regression
    /// here is silently caught by the CLI as "Invalid input format" and
    /// the orchestrator chat falls back to a content-less reply, which is
    /// hard to bisect after the fact.
    /// </para>
    /// </summary>
    public static string BuildStreamJsonUserMessage(string prompt, IReadOnlyList<CliOneShotImage> images)
    {
        var content = new List<object>
        {
            new { type = "text", text = prompt ?? string.Empty }
        };
        foreach (var img in images)
        {
            if (img == null || string.IsNullOrEmpty(img.Base64)) continue;
            content.Add(new
            {
                type = "image",
                source = new
                {
                    type = "base64",
                    media_type = string.IsNullOrWhiteSpace(img.MediaType) ? "image/png" : img.MediaType,
                    data = img.Base64
                }
            });
        }
        var envelope = new
        {
            type = "user",
            message = new
            {
                role = "user",
                content = content
            }
        };
        return JsonSerializer.Serialize(envelope) + "\n";
    }

    /// <summary>
    /// Fast smoke-test of the resolved <c>claude</c> executable. Returns
    /// <c>true</c> only if the binary spawns, exits 0, and finishes within
    /// 5 seconds. Used as the gate for the pre-spawn heal hook so the heal
    /// is paid for only when the install is actually broken.
    /// </summary>
    private static bool QuickProbe(string executable)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return false;
            if (!p.WaitForExit(5000))
            {
                try { p.Kill(entireProcessTree: true); } catch (Exception __ex) { SilentCatch.Note(__ex, "ClaudeOneShot: best-effort"); /* best-effort */ }
                return false;
            }
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
