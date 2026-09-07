using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentStudio.Review;

/// <summary>
/// Builds the post-run <c>status.md</c> protocol by handing the tail of the CLI
/// output log to a one-shot Claude Haiku subprocess. Normal completion awaits
/// the bounded Result-finalization gate. State is in-memory only; after a
/// backend restart, jobs fall back to <c>None|Ready|Degraded</c> based on the
/// presence and provenance marker of <c>status.md</c> on disk.
/// </summary>
public sealed class SummaryGenerationService
{
    public const int DefaultFinalizationMaxAttempts = 3;
    private const int MaxLogChars = 60_000;
    private const int HaikuTimeoutSeconds = 90;
    private static readonly Regex ProtocolImagePathRegex = new(
        @"(?<![\w./\\-])(?<path>(?:results|attachments)[/\\][^\s`'""<>)\]]+\.(?:png|jpe?g|gif|webp|bmp|svg))(?:[.,;:!?])?(?![\w./\\-])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly ILogger<SummaryGenerationService> _logger;
    private readonly IConfiguration _configuration;
    private readonly RuntimePromptService _prompts;
    private readonly AdHocUsageRecorder? _usage;
    private readonly FileGenerationIndex? _fileGenerationIndex;
    private readonly ConcurrentDictionary<string, TaskSummaryState> _states = new();

    public SummaryGenerationService(ILogger<SummaryGenerationService> logger, IConfiguration configuration)
        : this(logger, configuration, new RuntimePromptService(configuration, NullLogger<RuntimePromptService>.Instance), null)
    {
    }

    public SummaryGenerationService(
        ILogger<SummaryGenerationService> logger,
        IConfiguration configuration,
        RuntimePromptService prompts,
        AdHocUsageRecorder? usage = null,
        CliOneShotRegistry? oneShotRegistry = null,
        FileGenerationIndex? fileGenerationIndex = null)
    {
        _logger = logger;
        _configuration = configuration;
        _prompts = prompts;
        _usage = usage;
        _oneShotRegistry = oneShotRegistry;
        _fileGenerationIndex = fileGenerationIndex;
    }

    private readonly CliOneShotRegistry? _oneShotRegistry;

    public TaskSummaryState? GetState(string jobKey)
        => _states.TryGetValue(jobKey, out var s) ? s : null;

    /// <summary>
    /// Pure inflight check used by <see cref="GenerateAsync"/> and exposed
    /// for tests. A job is considered "still generating" when its previous
    /// state is <see cref="TaskSummaryStatus.Generating"/> AND the
    /// <see cref="TaskSummaryState.StartedAt"/> is younger than the Haiku
    /// timeout. Older Generating entries are treated as stuck and
    /// overwritten so the user can recover via the regenerate button.
    /// </summary>
    public static bool IsInflight(TaskSummaryState? prev, DateTime nowUtc, int timeoutSeconds)
    {
        if (prev is null) return false;
        if (prev.Status != TaskSummaryStatus.Generating) return false;
        if (prev.StartedAt is null) return false;
        return (nowUtc - prev.StartedAt.Value).TotalSeconds < timeoutSeconds;
    }

    public Task GenerateAsync(TaskInfo info, CancellationToken ct = default)
        => GenerateAsync(info, runOutcome: null, ct);

    /// <summary>
    /// Awaited post-core gate for the application-owned Result document. Only
    /// summary generation is retried. Exhaustion records a typed degraded state
    /// and returns normally so the already completed core run remains
    /// reviewable and is never reissued for a summary-side failure.
    /// </summary>
    public async Task<ResultFinalizationOutcome> FinalizeAsync(
        TaskInfo info,
        TerminalRunOutcome? runOutcome = null,
        CancellationToken ct = default)
    {
        var maxAttempts = Math.Clamp(
            _configuration.GetValue(
                "SummaryGeneration:FinalizationMaxAttempts",
                DefaultFinalizationMaxAttempts),
            1,
            10);
        string? error = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            await GenerateAsync(info, runOutcome, ct);
            var state = GetState(info.TaskKey);
            var statusPath = Path.Combine(info.FolderPath, "status.md");
            var statusMarkdown = File.Exists(statusPath)
                ? await File.ReadAllTextAsync(statusPath, ct)
                : string.Empty;
            var generated = state?.Status == TaskSummaryStatus.Ready
                            && !string.IsNullOrWhiteSpace(statusMarkdown)
                            && !statusMarkdown.Contains(
                                AgentStudio.Tasks.TaskTransitionService.ResultScaffoldMarker,
                                StringComparison.Ordinal);
            if (generated)
            {
                _states[info.TaskKey] = state! with
                {
                    Attempt = attempt,
                    MaxAttempts = maxAttempts,
                };
                return new ResultFinalizationOutcome(
                    TaskSummaryStatus.Ready,
                    attempt,
                    maxAttempts,
                    null);
            }

            error = state?.ErrorMessage ?? "Generated status.md is missing.";
            _logger.LogWarning(
                "Result finalization retry taskKey={TaskKey} jobId={JobId} attempt={Attempt}/{MaxAttempts} error={Error}",
                info.TaskKey,
                info.Id,
                attempt,
                maxAttempts,
                error);
        }

        var previous = GetState(info.TaskKey) ?? new TaskSummaryState();
        _states[info.TaskKey] = previous with
        {
            Status = TaskSummaryStatus.Degraded,
            FinishedAt = DateTime.UtcNow,
            ErrorMessage = error,
            Attempt = maxAttempts,
            MaxAttempts = maxAttempts,
        };
        _logger.LogWarning(
            "Result finalization degraded taskKey={TaskKey} jobId={JobId} attempts={Attempts} error={Error}",
            info.TaskKey,
            info.Id,
            maxAttempts,
            error);
        return new ResultFinalizationOutcome(
            TaskSummaryStatus.Degraded,
            maxAttempts,
            maxAttempts,
            error);
    }

    public async Task GenerateAsync(TaskInfo info, TerminalRunOutcome? runOutcome, CancellationToken ct = default)
    {
        var key = info.TaskKey;
        var runIndex = _fileGenerationIndex?.CurrentRunIndex(info.FolderPath);

        // Inflight guard: if a previous GenerateAsync for the same job is
        // still inside its Haiku window, dropping this duplicate avoids
        // racing two subprocesses against the same status.md (manual
        // Regenerate clicked while the post-run auto-call is still in
        // flight, or the runner re-fires after a missed completion). The
        // outstanding call will publish either Ready or Failed when it
        // returns; the user-visible spinner stays where it was.
        if (_states.TryGetValue(key, out var prev) && IsInflight(prev, DateTime.UtcNow, HaikuTimeoutSeconds))
        {
            _logger.LogDebug("Skipping summary generation for {JobId}: prior call still in flight (started {StartedAt:o})",
                info.Id, prev.StartedAt);
            return;
        }

        _states[key] = new TaskSummaryState
        {
            Status = TaskSummaryStatus.Generating,
            StartedAt = DateTime.UtcNow
        };

        try
        {
            var logPath = TaskPaths.CliOutputLog(info.FolderPath);
            if (!File.Exists(logPath))
            {
                Fail(key, "No CLI output to summarise yet. The task has not been run (logs/cli-output.log is missing). Start it once, then try again.");
                return;
            }

            var rawLog = await File.ReadAllTextAsync(logPath, ct);
            var truncated = TruncateTail(rawLog, MaxLogChars);
            runOutcome ??= TerminalRunOutcomeClassifier.TryClassifyRenderedLog(rawLog)?.Outcome;
            var prompt = _prompts.Render(RuntimePromptService.SummaryProtocol,
                BuildSummarySlots(info, truncated, runOutcome?.ProtocolResult ?? "unknown"),
                new PromptCallContext(info.ProjectName, "summary", SummaryModel()));

            var result = await RunHaikuAsync(prompt, info.FolderPath, ct);
            if (!result.Ok || string.IsNullOrWhiteSpace(result.Summary))
            {
                Fail(key, result.Error ?? "Empty Haiku response");
                return;
            }

            var summary = result.Summary;
            if (runOutcome != null)
            {
                summary = ApplyOutcomeResultLine(summary, runOutcome.ProtocolResult);
            }

            if (TaskModes.IsConcept(info.Mode))
            {
                var dossier = AgentStudio.Tasks.ConceptDossierContract.Read(info.FolderPath);
                if (!string.IsNullOrWhiteSpace(dossier.RepoRelativePath))
                {
                    summary = AgentStudio.Tasks.ConceptDossierContract.PreserveReferenceInStatus(
                        summary,
                        dossier.RepoRelativePath);
                }
            }

            summary = ApplyProtocolImageReferences(summary, rawLog, info.FolderPath, out var appendedImageCount);
            if (appendedImageCount > 0)
            {
                _logger.LogInformation("Summary protocol image references appended for {JobId}: {ImageCount} references",
                    info.Id, appendedImageCount);
            }

            var target = Path.Combine(info.FolderPath, "status.md");
            WriteAllTextWithRetry(target, summary);
            RegisterGeneratedStatus(info, result, runIndex);
            RecordBrokenImageReferences(info, summary);

            _states[key] = new TaskSummaryState
            {
                Status = TaskSummaryStatus.Ready,
                StartedAt = _states[key].StartedAt,
                FinishedAt = DateTime.UtcNow,
                BytesWritten = summary.Length
            };
            _logger.LogInformation("Summary written for {JobId} ({Bytes} bytes)", info.Id, summary.Length);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Summary generation failed for {JobId}", info.Id);
            Fail(key, ex.Message);
        }
    }

    /// <summary>
    /// One-shot interim summary against the current cli-output.log. Unlike
    /// <see cref="GenerateAsync"/>, this method:
    ///   - returns the Haiku markdown to the caller instead of writing it to
    ///     <c>status.md</c> (the post-run summary still owns that file),
    ///   - does not update <see cref="_states"/>, so the protocol-pane's
    ///     "Ready / Generating / Failed" state stays anchored to the real run
    ///     summary,
    ///   - does not apply the deterministic <c>Result:</c> rewrite, because
    ///     the run is still in flight and there is no terminal outcome yet.
    /// Used by the "Interim status" button surfaced in the protocol pane
    /// while a run is alive so the user can peek at progress without
    /// stopping the agent.
    /// </summary>
    public async Task<InterimSummaryResult> GenerateInterimAsync(TaskInfo info, CancellationToken ct = default)
    {
        var logPath = TaskPaths.CliOutputLog(info.FolderPath);
        if (!File.Exists(logPath))
        {
            return InterimSummaryResult.Failure("No CLI output to summarise yet. Start the task once, then try again.");
        }

        string rawLog;
        try
        {
            rawLog = await File.ReadAllTextAsync(logPath, ct);
        }
        catch (Exception ex)
        {
            return InterimSummaryResult.Failure($"Could not read cli-output.log: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(rawLog))
        {
            return InterimSummaryResult.Failure("cli-output.log is empty - the agent hasn't streamed any output yet.");
        }

        var truncated = TruncateTail(rawLog, MaxLogChars);
        // Interim peek: the run is still alive, so there is no terminal outcome
        // to feed the classifier. Say so explicitly instead of guessing one.
        var prompt = _prompts.Render(RuntimePromptService.SummaryProtocol,
            BuildSummarySlots(info, truncated, "in progress"),
            new PromptCallContext(info.ProjectName, "summary", SummaryModel()));

        var sw = Stopwatch.StartNew();
        var result = await RunHaikuAsync(prompt, info.FolderPath, ct);
        sw.Stop();

        if (!result.Ok || string.IsNullOrWhiteSpace(result.Summary))
        {
            _logger.LogInformation("Interim summary failed for {JobId} after {ElapsedMs}ms: {Error}",
                info.Id, sw.ElapsedMilliseconds, result.Error);
            return InterimSummaryResult.Failure(result.Error ?? "Empty Haiku response");
        }

        var markdown = ApplyProtocolImageReferences(result.Summary, rawLog, info.FolderPath, out var appendedImageCount);
        _logger.LogInformation("Interim summary produced for {JobId} ({Bytes} bytes, {ElapsedMs}ms, {ImageCount} appended images)",
            info.Id, markdown.Length, sw.ElapsedMilliseconds, appendedImageCount);
        return InterimSummaryResult.Success(markdown, sw.ElapsedMilliseconds);
    }

    private void Fail(string key, string error)
    {
        var prev = _states.TryGetValue(key, out var s) ? s : new TaskSummaryState();
        _states[key] = prev with
        {
            Status = TaskSummaryStatus.Failed,
            FinishedAt = DateTime.UtcNow,
            ErrorMessage = error
        };
    }

    private static string TruncateTail(string text, int maxChars)
    {
        if (text.Length <= maxChars) return text;
        var tail = text[^maxChars..];
        return "[earlier output truncated]\n" + tail;
    }

    /// <summary>
    /// Builds the placeholder set for <c>summary-protocol.md</c>. Besides the
    /// log tail, it feeds the task metadata (<c>taskType</c> / <c>mode</c>) and
    /// the run <c>outcome</c> so the summarizer can pick the right result
    /// <c>Case</c> and frame the overview honestly (a blocked run reads as
    /// "where it stopped", not "shipped"). The frontend result view classifies
    /// the same signals independently, so these slots only need to nudge the
    /// model; a missing or wrong value degrades to the client-side heuristic.
    /// Exposed for the prompt-contract test that pins this wiring without a
    /// billable Haiku round-trip.
    /// </summary>
    public static Dictionary<string, string?> BuildSummarySlots(TaskInfo info, string log, string outcome)
        => new()
        {
            ["log"] = log,
            ["taskType"] = info.TaskType,
            ["mode"] = info.Mode,
            ["outcome"] = outcome,
        };

    private async Task<HaikuSummaryResult> RunHaikuAsync(
        string prompt, string workingDirectory, CancellationToken ct)
    {
        var model = SummaryModel();
        var startedAt = DateTime.UtcNow;
        var sw = Stopwatch.StartNew();

        var oneShot = _oneShotRegistry?.Get("claude");
        if (oneShot != null)
        {
            var r = await oneShot.RunAsync(new CliOneShotRequest(
                CliType: "claude", Model: model, Prompt: prompt)
            {
                WorkingDirectory = Directory.Exists(workingDirectory) ? workingDirectory : null,
                Timeout = TimeSpan.FromSeconds(HaikuTimeoutSeconds),
                Source = AdHocUsageSources.SummaryGeneration,
                RecordUsage = false, // We record below with parsed text + usage
            }, ct).ConfigureAwait(false);

            sw.Stop();
            var endedAt = DateTime.UtcNow;
            AdHocClaudeInvoker.Record(_usage, AdHocUsageSources.SummaryGeneration, model, r.Usage,
                (long)r.Duration.TotalMilliseconds, ok: r.Ok);
            if (!r.Ok) return HaikuSummaryResult.Failure(model, r.Error, startedAt, endedAt, sw.ElapsedMilliseconds);
            return HaikuSummaryResult.Success(model, r.Usage, SanitizeMarkdown(r.ParsedText), startedAt, endedAt,
                (long)r.Duration.TotalMilliseconds);
        }

        var claudePath = _configuration["ClaudeCli:Path"] ?? "claude";

        // Feed the prompt via stdin instead of a positional `-p <prompt>`
        // argument. See OneShot service for the production path; this
        // fallback is for tests that build the service without DI.
        var psi = new ProcessStartInfo
        {
            FileName = GenericCliExecutionService.ResolveExecutable(claudePath),
            WorkingDirectory = Directory.Exists(workingDirectory) ? workingDirectory : Directory.GetCurrentDirectory(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = System.Text.Encoding.UTF8,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        foreach (var arg in AdHocClaudeInvoker.BuildArgs(model)) psi.ArgumentList.Add(arg);

        try
        {
            using var p = Process.Start(psi);
            if (p == null) return HaikuSummaryResult.Failure(model, "Process.Start returned null", startedAt, DateTime.UtcNow, sw.ElapsedMilliseconds);

            // Write the prompt up front, then close stdin so Claude can finalise
            // the request. WriteAsync is awaited so the OS pipe buffer can drain
            // before we move on to reading stdout.
            await p.StandardInput.WriteAsync(prompt.AsMemory(), ct);
            p.StandardInput.Close();

            var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = p.StandardError.ReadToEndAsync(ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(HaikuTimeoutSeconds));
            await p.WaitForExitAsync(cts.Token);

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            sw.Stop();
            var endedAt = DateTime.UtcNow;
            if (p.ExitCode != 0)
            {
                AdHocClaudeInvoker.Record(_usage, AdHocUsageSources.SummaryGeneration, model, null, sw.ElapsedMilliseconds, ok: false);
                return HaikuSummaryResult.Failure(model, $"claude exited {p.ExitCode}: {stderr.Trim()}", startedAt, endedAt, sw.ElapsedMilliseconds);
            }

            var (text, usage) = AdHocClaudeInvoker.ParseOrFallback(stdout, model);
            AdHocClaudeInvoker.Record(_usage, AdHocUsageSources.SummaryGeneration, model, usage, sw.ElapsedMilliseconds, ok: true);
            return HaikuSummaryResult.Success(model, usage, SanitizeMarkdown(text), startedAt, endedAt, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return HaikuSummaryResult.Failure(model, $"Haiku timed out after {HaikuTimeoutSeconds}s", startedAt, DateTime.UtcNow, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return HaikuSummaryResult.Failure(model, ex.Message, startedAt, DateTime.UtcNow, sw.ElapsedMilliseconds);
        }
    }

    private string SummaryModel() =>
        _configuration["ClaudeCli:SummaryModel"] ?? ModelFamilyResolver.Resolve(ModelFamilies.ClaudeHaiku);

    private void RegisterGeneratedStatus(TaskInfo info, HaikuSummaryResult result, int? runIndex)
    {
        if (_fileGenerationIndex == null) return;
        try
        {
            var usage = result.Usage;
            _fileGenerationIndex.Upsert(info.FolderPath, new FileGenerationMeta
            {
                File = "status.md",
                Kind = "status",
                Model = usage?.Model ?? result.Model,
                Cli = CliTypes.Claude,
                TokensIn = usage?.InputTokens ?? 0,
                TokensOut = usage?.OutputTokens ?? 0,
                TokensTotal = (usage?.InputTokens ?? 0)
                    + (usage?.OutputTokens ?? 0)
                    + (usage?.CacheReadTokens ?? 0)
                    + (usage?.CacheCreationTokens ?? 0),
                StartedAt = result.StartedAt,
                EndedAt = result.EndedAt,
                DurationMs = result.DurationMs,
                RunIndex = runIndex,
                StepId = AdHocUsageSources.SummaryGeneration,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SummaryGenerationService: failed to register generated status.md for {JobId}", info.Id);
        }
    }

    /// <summary>
    /// Validates the image references in the freshly written <c>status.md</c>
    /// against the files on disk and appends a review-evidence finding for
    /// every broken reference so a missing screenshot surfaces as a visible
    /// review finding instead of a silently empty image. Findings carry a
    /// stable per-path id so a regenerate over the same broken reference does
    /// not stack duplicate rows (the reader folds latest-per-id). Best-effort:
    /// a failure here never fails summary generation.
    /// </summary>
    private void RecordBrokenImageReferences(TaskInfo info, string statusMarkdown)
    {
        try
        {
            var broken = ProtocolImageReferenceValidator.FindBrokenReferences(statusMarkdown, info.FolderPath);
            if (broken.Count == 0) return;

            var knownIds = new HashSet<string>(
                ReviewEvidenceLog.ReadLatestPerId(info.FolderPath, _logger).Select(e => e.Id),
                StringComparer.Ordinal);

            var appended = 0;
            foreach (var rel in broken)
            {
                var id = $"broken-image-ref:{rel}";
                if (!knownIds.Add(id)) continue;
                ReviewEvidenceLog.Append(info.FolderPath, new ReviewEvidenceEntry
                {
                    Id = id,
                    Source = ReviewEvidenceSources.TaskCheck,
                    Severity = ReviewEvidenceSeverities.Warn,
                    Title = $"Broken screenshot reference: {rel}",
                    Body = $"status.md links the image `{rel}`, but no such file exists under the job folder. "
                        + "The protocol would render a silently empty image. Fix the path or capture the "
                        + "screenshot into `results/`.",
                    CreatedAt = DateTime.UtcNow,
                    Artifacts = [rel],
                });
                appended++;
            }

            if (appended > 0)
            {
                _logger.LogWarning(
                    "status.md for {JobId} references {Count} missing image file(s): {Paths}",
                    info.Id, appended, string.Join(", ", broken));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to validate status.md image references for {JobId}", info.Id);
        }
    }

    private static string SanitizeMarkdown(string raw)
    {
        var trimmed = raw.Trim();
        // Strip a wrapping ```markdown ... ``` fence if Haiku adds one despite instructions.
        if (trimmed.StartsWith("```"))
        {
            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline > 0) trimmed = trimmed[(firstNewline + 1)..];
            if (trimmed.EndsWith("```")) trimmed = trimmed[..^3].TrimEnd();
        }
        return trimmed;
    }

    public static string ApplyOutcomeResultLine(string markdown, string protocolResult)
    {
        if (string.IsNullOrWhiteSpace(markdown) || string.IsNullOrWhiteSpace(protocolResult)) return markdown;

        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].TrimStart().StartsWith("- Result:", StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = $"- Result: {protocolResult}";
                return string.Join(Environment.NewLine, lines).TrimEnd() + Environment.NewLine;
            }
        }

        var statusIndex = lines.FindIndex(l => string.Equals(l.Trim(), "# Status", StringComparison.OrdinalIgnoreCase));
        if (statusIndex >= 0)
        {
            lines.Insert(statusIndex + 1, "");
            lines.Insert(statusIndex + 2, $"- Result: {protocolResult}");
            return string.Join(Environment.NewLine, lines).TrimEnd() + Environment.NewLine;
        }

        lines.Insert(0, $"- Result: {protocolResult}");
        return string.Join(Environment.NewLine, lines).TrimEnd() + Environment.NewLine;
    }

    public static string ApplyProtocolImageReferences(string markdown, string log, string taskFolder)
        => ApplyProtocolImageReferences(markdown, log, taskFolder, out _);

    /// <summary>
    /// Injects a deterministic <c>## Images</c> section built from the image paths
    /// mentioned in the run log. Every extracted path is resolved against
    /// <paramref name="taskFolder"/> and kept only when it points at a file that
    /// actually exists on disk (glob/wildcard patterns and paths that escape the
    /// job folder are always dropped). This is what keeps example/glob paths that
    /// litter a log - e.g. the Artifact-Upload card's <c>results/*.png</c> - out of
    /// the protocol: with nothing left after filtering, no Images section is added
    /// at all rather than a run of empty rows.
    /// </summary>
    public static string ApplyProtocolImageReferences(string markdown, string log, string taskFolder, out int appendedCount)
    {
        appendedCount = 0;
        if (string.IsNullOrWhiteSpace(markdown) || string.IsNullOrWhiteSpace(log)) return markdown;

        var imageRefs = ExtractProtocolImageReferences(log)
            .Where(path => ProtocolImageReferenceValidator.ResolvesToExistingFile(path, taskFolder))
            .ToList();
        if (imageRefs.Count == 0) return markdown;

        var existing = new HashSet<string>(ExtractProtocolImageReferences(markdown), StringComparer.OrdinalIgnoreCase);
        var missing = imageRefs.Where(existing.Add).ToList();
        if (missing.Count == 0) return markdown;
        appendedCount = missing.Count;

        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
        var imagesIndex = lines.FindIndex(l => string.Equals(l.Trim(), "## Images", StringComparison.OrdinalIgnoreCase));
        var additions = missing.Select(path => $"- ![]({path}){SourceHintSuffix(path)}").ToList();

        if (imagesIndex < 0)
        {
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1])) lines.Add("");
            lines.Add("## Images");
            lines.AddRange(additions);
            return string.Join(Environment.NewLine, lines).TrimEnd() + Environment.NewLine;
        }

        var insertIndex = imagesIndex + 1;
        while (insertIndex < lines.Count && !lines[insertIndex].StartsWith("## ", StringComparison.Ordinal))
        {
            insertIndex++;
        }

        lines.InsertRange(insertIndex, additions);
        return string.Join(Environment.NewLine, lines).TrimEnd() + Environment.NewLine;
    }

    /// <summary>
    /// Appends a plain-text source hint to a deterministically-injected image
    /// bullet so the reviewer can read the provenance straight from
    /// <c>status.md</c> (the same label the Task-Detail strip shows). The hint
    /// is derived purely from the filename suffix; an unlabeled filename gets no
    /// hint, so the protocol never claims a source it cannot prove.
    /// </summary>
    private static string SourceHintSuffix(string path)
    {
        var info = ScreenshotSourceParser.Parse(Path.GetFileName(path));
        return info.Source switch
        {
            ScreenshotSources.Real => " (source: real)",
            ScreenshotSources.Mocked => " (source: mocked)",
            ScreenshotSources.Composite => info.Parts.Count > 0
                ? $" (source: composite of {string.Join(", ", info.Parts)})"
                : " (source: composite)",
            ScreenshotSources.Pinned => " (source: pinned)",
            _ => "" // unlabeled: do not claim a source
        };
    }

    private static List<string> ExtractProtocolImageReferences(string text)
    {
        var refs = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in ProtocolImagePathRegex.Matches(text))
        {
            var path = match.Groups["path"].Value.Replace('\\', '/');
            if (seen.Add(path)) refs.Add(path);
        }

        return refs;
    }

    private static void WriteAllTextWithRetry(string filePath, string content)
    {
        const int maxAttempts = 8;
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        IOException? last = null;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                using var stream = new FileStream(
                    filePath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                stream.Write(bytes, 0, bytes.Length);
                return;
            }
            catch (IOException ex) when (attempt < maxAttempts - 1)
            {
                last = ex;
                Thread.Sleep(50 * (attempt + 1));
            }
        }
        if (last != null) throw last;
    }

    private sealed record HaikuSummaryResult(
        bool Ok,
        string? Summary,
        string? Error,
        string Model,
        OrchestratorTokenUsage? Usage,
        long DurationMs,
        DateTime StartedAt,
        DateTime EndedAt)
    {
        public static HaikuSummaryResult Success(
            string model,
            OrchestratorTokenUsage? usage,
            string? summary,
            DateTime startedAt,
            DateTime endedAt,
            long durationMs)
            => new(true, summary, null, model, usage, durationMs, startedAt, endedAt);

        public static HaikuSummaryResult Failure(
            string model,
            string? error,
            DateTime startedAt,
            DateTime endedAt,
            long durationMs)
            => new(false, null, error, model, null, durationMs, startedAt, endedAt);
    }
}

public sealed record ResultFinalizationOutcome(
    TaskSummaryStatus Status,
    int Attempt,
    int MaxAttempts,
    string? Error)
{
    public bool Generated => Status == TaskSummaryStatus.Ready;
}

/// <summary>
/// Result of <see cref="SummaryGenerationService.GenerateInterimAsync"/>.
/// On success carries the Haiku markdown and the call duration so the UI
/// can show how long the peek took; on failure carries a user-facing error
/// string that the frontend renders in the interim-summary banner.
/// </summary>
public sealed record InterimSummaryResult(bool Ok, string? Markdown, string? Error, long DurationMs)
{
    public static InterimSummaryResult Success(string markdown, long durationMs)
        => new(true, markdown, null, durationMs);

    public static InterimSummaryResult Failure(string error)
        => new(false, null, error, 0);
}
