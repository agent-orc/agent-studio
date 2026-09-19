using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AgentStudio.Cli;

/// <summary>
/// Stateless Codex equivalent of <see cref="ClaudeOneShot"/>. Pipeline steps
/// use this adapter when their project override selects <c>cliType=codex</c>.
/// Prompts are piped through stdin and the JSONL protocol is reduced to the
/// final agent message plus the <c>turn.completed</c> usage frame.
/// </summary>
public sealed class CodexOneShot : ICliOneShot
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);
    private readonly IConfiguration _configuration;
    private readonly ILogger<CodexOneShot> _logger;
    private readonly ICliUsageParser _usageParser;
    private readonly ICliModelRegistry _modelRegistry;
    private readonly AdHocUsageRecorder? _usage;

    public CodexOneShot(
        IConfiguration configuration,
        ILogger<CodexOneShot> logger,
        CliUsageParserRegistry parsers,
        ICliModelRegistry modelRegistry,
        AdHocUsageRecorder? usage = null)
    {
        _configuration = configuration;
        _logger = logger;
        _usageParser = parsers.Get(CliTypes.Codex) ?? new CodexUsageParser();
        _modelRegistry = modelRegistry;
        _usage = usage;
    }

    public string CliType => CliTypes.Codex;

    public async Task<CliOneShotResult> RunAsync(CliOneShotRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var requestedAt = DateTime.UtcNow;
        var timeout = request.Timeout ?? DefaultTimeout;
        var executable = GenericCliExecutionService.ResolveExecutable(_configuration["CodexCli:Path"] ?? "codex");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        Process? process = null;
        string? imageDirectory = null;
        try
        {
            var imagePaths = MaterializeInlineImages(request.InlineImages, out imageDirectory);
            var psi = BuildStartInfo(executable, request, imagePaths);
            process = Process.Start(psi);
            if (process == null)
                return Record(request, CliOneShotResult.SpawnFailure("Process.Start returned null", requestedAt, DateTime.UtcNow));

            await process.StandardInput.WriteAsync(request.Prompt.AsMemory(), timeoutCts.Token).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(timeoutCts.Token).ConfigureAwait(false);
            process.StandardInput.Close();

            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            var result = ParseOutput(process.ExitCode, stdout, stderr, request.Model, requestedAt, DateTime.UtcNow,
                _usageParser, _modelRegistry);
            if (!result.Ok)
            {
                _logger.LogWarning(
                    "codex_one_shot_failed source={Source} job={JobId} model={Model} exit={ExitCode} error={Error}",
                    request.Source, request.JobId, request.Model, result.ExitCode, result.Error);
            }
            else
            {
                _logger.LogInformation(
                    "codex_one_shot_completed source={Source} job={JobId} model={Model} durationMs={DurationMs} inputTokens={InputTokens} outputTokens={OutputTokens}",
                    request.Source, request.JobId, request.Model, result.Duration.TotalMilliseconds,
                    result.Usage?.InputTokens ?? 0, result.Usage?.OutputTokens ?? 0);
            }
            return Record(request, result);
        }
        catch (OperationCanceledException)
        {
            var reason = !ct.IsCancellationRequested && timeoutCts.IsCancellationRequested
                ? $"timeout after {timeout.TotalSeconds:F0}s"
                : "cancelled";
            AgentStudio.Diagnostics.CliKillAudit.Trace(process, "CodexOneShot timeout/cancel (entireProcessTree)");
            try { process?.Kill(entireProcessTree: true); } catch (Exception ex) { SilentCatch.Note(ex, "CodexOneShot: best-effort kill"); }
            return Record(request, CliOneShotResult.SpawnFailure(reason, requestedAt, DateTime.UtcNow));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "codex_one_shot_crashed source={Source} job={JobId} model={Model} exceptionType={ExceptionType}",
                request.Source, request.JobId, request.Model, ex.GetType().Name);
            AgentStudio.Diagnostics.CliKillAudit.Trace(process, "CodexOneShot crash (entireProcessTree)");
            try { process?.Kill(entireProcessTree: true); } catch (Exception killEx) { SilentCatch.Note(killEx, "CodexOneShot: best-effort kill"); }
            return Record(request, CliOneShotResult.SpawnFailure(ex.Message, requestedAt, DateTime.UtcNow));
        }
        finally
        {
            process?.Dispose();
            TryDeleteImageDirectory(imageDirectory);
        }
    }

    internal static ProcessStartInfo BuildStartInfo(
        string executable,
        CliOneShotRequest request,
        IReadOnlyList<string>? imagePaths = null)
    {
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
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add("--experimental-json");
        if (string.Equals(request.Source, "orchestrator-chat", StringComparison.OrdinalIgnoreCase))
        {
            // Chat is a read-only Q&A surface and may legitimately run from a
            // non-repository fallback directory. Git trust is not a useful
            // boundary here, while the default Codex gate rejects the turn
            // before the model can answer.
            psi.ArgumentList.Add("--skip-git-repo-check");
        }
        psi.ArgumentList.Add("--sandbox");
        psi.ArgumentList.Add("read-only");
        psi.ArgumentList.Add("-m");
        psi.ArgumentList.Add(request.Model);
        foreach (var flag in CodingAgentRunner.Model.CliReasoningFlags.For(CliTypes.Codex, request.Model, request.ThinkingLevel))
            psi.ArgumentList.Add(flag);
        if (request.ExtraArgs is { Count: > 0 })
        {
            foreach (var arg in request.ExtraArgs) psi.ArgumentList.Add(arg);
        }
        foreach (var imagePath in imagePaths ?? [])
        {
            psi.ArgumentList.Add("--image");
            psi.ArgumentList.Add(imagePath);
        }
        psi.ArgumentList.Add("-");
        return psi;
    }

    private static IReadOnlyList<string> MaterializeInlineImages(
        IReadOnlyList<CliOneShotImage>? images,
        out string? directory)
    {
        directory = null;
        if (images is not { Count: > 0 }) return [];

        directory = Path.Combine(Path.GetTempPath(), $"agent-studio-codex-images-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var paths = new List<string>();
        foreach (var image in images.Take(8))
        {
            var bytes = Convert.FromBase64String(image.Base64);
            if (bytes.Length == 0 || bytes.Length > 10 * 1024 * 1024) continue;
            var extension = image.MediaType.ToLowerInvariant() switch
            {
                "image/jpeg" => ".jpg",
                "image/webp" => ".webp",
                "image/gif" => ".gif",
                _ => ".png",
            };
            var path = Path.Combine(directory, $"image-{paths.Count + 1:D2}{extension}");
            File.WriteAllBytes(path, bytes);
            paths.Add(path);
        }
        return paths;
    }

    private static void TryDeleteImageDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;
        try { Directory.Delete(directory, recursive: true); }
        catch (Exception exception) { SilentCatch.Note(exception, "CodexOneShot: temporary image cleanup"); }
    }

    internal static CliOneShotResult ParseOutput(
        int exitCode,
        string stdout,
        string stderr,
        string model,
        DateTime requestedAt,
        DateTime completedAt,
        ICliUsageParser usageParser,
        ICliModelRegistry modelRegistry)
    {
        var replies = new List<string>();
        ParsedTurnUsage? richUsage = null;
        string? turnError = null;
        foreach (var line in (stdout ?? string.Empty).Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("type", out var typeElement))
                {
                    var type = typeElement.GetString();
                    if (type == "item.completed"
                        && root.TryGetProperty("item", out var item)
                        && item.TryGetProperty("type", out var itemType)
                        && itemType.GetString() == "agent_message"
                        && item.TryGetProperty("text", out var text))
                    {
                        var reply = text.GetString();
                        if (!string.IsNullOrWhiteSpace(reply)) replies.Add(reply);
                    }
                    else if (type == "turn.failed")
                    {
                        turnError = root.TryGetProperty("error", out var error)
                            && error.TryGetProperty("message", out var message)
                            ? message.GetString()
                            : "turn failed";
                    }
                }
                if (usageParser.TryParse(root, model, modelRegistry, out var parsed)) richUsage = parsed;
            }
            catch (JsonException ex)
            {
                // stderr and the process exit still make malformed protocol output visible.
                SilentCatch.Note(ex, "CodexOneShot: malformed JSONL frame");
            }
        }

        var usage = richUsage is null ? null : new OrchestratorTokenUsage
        {
            Model = richUsage.Model ?? model,
            InputTokens = ToInt(richUsage.Input),
            OutputTokens = ToInt(richUsage.Output),
            CacheReadTokens = ToInt(richUsage.CacheRead),
            CacheCreationTokens = ToInt(richUsage.CacheWrite),
            InputIncludesCached = richUsage.InputIncludesCached,
        };
        var ok = exitCode == 0 && turnError is null;
        var duration = completedAt - requestedAt;
        return new CliOneShotResult(
            Ok: ok,
            ExitCode: exitCode,
            Stdout: stdout ?? string.Empty,
            Stderr: stderr ?? string.Empty,
            Duration: duration,
            ParsedText: string.Join("\n", replies),
            Usage: usage,
            RichUsage: richUsage,
            Latency: new AgentMessageLatency(RequestedAt: requestedAt, CompletedAt: completedAt, TotalMs: (long)duration.TotalMilliseconds),
            Error: ok ? null : turnError ?? $"exitCode={exitCode}{(string.IsNullOrWhiteSpace(stderr) ? "" : $"; stderr={stderr.Trim()}")}");
    }

    private CliOneShotResult Record(CliOneShotRequest request, CliOneShotResult result)
    {
        if (request.RecordUsage && _usage != null)
        {
            AdHocClaudeInvoker.Record(_usage, request.Source ?? AdHocUsageSources.ReviewDecision,
                request.Model, result.Usage, (long)result.Duration.TotalMilliseconds, result.Ok,
                request.Project, request.JobId);
        }
        return result;
    }

    private static int ToInt(long value) => (int)Math.Clamp(value, 0, int.MaxValue);
}
