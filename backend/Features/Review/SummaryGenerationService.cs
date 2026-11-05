using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using AgentStudio.Pipeline;
using AgentStudio.Shared;
using AgentStudio.Runner;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentStudio.Review;

/// <summary>
/// The closing Result assessment step (<c>post-summary-assessment</c>,
/// AGT-3046). Builds the post-run <c>status.md</c> protocol and the structured
/// <c>summary-assessment.json</c> by handing the tail of the CLI output log and
/// the card's evidence bundle to a one-shot CLI call whose CLI, model and
/// thinking level come from the project's pipeline step settings (default
/// Claude Opus 5.5, low). The log, task prompt head, deliverables and final
/// agent messages are fitted into the orchestrator prompt limit by
/// <see cref="SummaryPromptFit"/>. Normal completion awaits the bounded
/// Result-finalization gate. State is in-memory only; after a
/// backend restart, jobs fall back to <c>None|Ready|Degraded</c> based on the
/// presence and provenance marker of <c>status.md</c> on disk.
/// </summary>
public sealed class SummaryGenerationService
{
    public const int DefaultFinalizationMaxAttempts = 3;
    /// <summary>Kept below the boundary limit for the boundary's own credential markers.</summary>
    private const int PromptHeadroomBytes = 256;
    private const int MaxTaskPromptReadBytes = 256 * 1024;
    private const int ModelTimeoutSeconds = 180;
    private const int MaxProtocolAttempts = 2;
    private static readonly Regex ProtocolImagePathRegex = new(
        @"(?<![\w./\\-])(?<path>(?:results|attachments)[/\\][^\s`'""<>)\]]+\.(?:png|jpe?g|gif|webp|bmp|svg))(?:[.,;:!?])?(?![\w./\\-])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly ILogger<SummaryGenerationService> _logger;
    private readonly IConfiguration _configuration;
    private readonly RuntimePromptService _prompts;
    private readonly AdHocUsageRecorder? _usage;
    private readonly FileGenerationIndex? _fileGenerationIndex;
    private readonly ResultVersionStore? _resultVersions;
    private readonly PipelineExecutionLog? _pipelineLog;
    private readonly ProjectSettingsService? _projectSettings;
    private readonly TimelineLog? _timeline;
    private readonly ConcurrentDictionary<string, TaskSummaryState> _states = new();
    private readonly ConcurrentDictionary<string, int> _promptBytes = new();
    private int _shortenedPrompts;
    private int _degradedSummaries;

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
        FileGenerationIndex? fileGenerationIndex = null,
        ResultVersionStore? resultVersions = null,
        PipelineExecutionLog? pipelineLog = null,
        ProjectSettingsService? projectSettings = null,
        TimelineLog? timeline = null)
    {
        _logger = logger;
        _configuration = configuration;
        _prompts = prompts;
        _usage = usage;
        _oneShotRegistry = oneShotRegistry;
        _fileGenerationIndex = fileGenerationIndex;
        _resultVersions = resultVersions;
        _pipelineLog = pipelineLog;
        _projectSettings = projectSettings;
        _timeline = timeline;
    }

    private readonly CliOneShotRegistry? _oneShotRegistry;

    public TaskSummaryState? GetState(string jobKey)
        => _states.TryGetValue(jobKey, out var s) ? s : null;

    /// <summary>Summary prompts shortened to fit the limit since start; logged with each one.</summary>
    public int ShortenedPromptCount => Volatile.Read(ref _shortenedPrompts);

    /// <summary>Result finalizations that ended degraded since start; logged with each one.</summary>
    public int DegradedSummaryCount => Volatile.Read(ref _degradedSummaries);

    /// <summary>
    /// Pure inflight check used by <see cref="GenerateAsync"/> and exposed
    /// for tests. A job is considered "still generating" when its previous
    /// state is <see cref="TaskSummaryStatus.Generating"/> AND the
    /// <see cref="TaskSummaryState.StartedAt"/> is younger than the model
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
        CancellationToken ct = default,
        string? resultSha = null)
    {
        var maxAttempts = Math.Clamp(
            _configuration.GetValue(
                "SummaryGeneration:FinalizationMaxAttempts",
                DefaultFinalizationMaxAttempts),
            1,
            10);
        string? error = null;
        var attempts = 0;
        // The degraded log reports the size of this finalization's prompt only.
        _promptBytes.TryRemove(info.TaskKey, out _);
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            attempts = attempt;
            await GenerateAsync(info, runOutcome, ct, resultSha);
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
            if (error.StartsWith("waiting for a host with ", StringComparison.Ordinal)) break;
            // Structural failures have already consumed their one retry with
            // identical inputs. Do not multiply that budget in finalization.
            if (error.StartsWith(SummaryProtocolValidation.ErrorPrefix, StringComparison.Ordinal)) break;
            // The boundary refuses the same prompt again (AGT-3410).
            if (ResultFinalizationPolicy.IsInputRefusal(error)) break;
            _logger.LogWarning(
                "Result finalization retry taskKey={TaskKey} jobId={JobId} attempt={Attempt}/{MaxAttempts} error={Error}",
                info.TaskKey,
                info.Id,
                attempt,
                maxAttempts,
                error);
        }

        var previous = GetState(info.TaskKey) ?? new TaskSummaryState();
        if (TaskModes.IsReportOnly(info.Mode)
            && TryUseAgentReportStatus(info, error, out var fallback))
        {
            _states[info.TaskKey] = previous with
            {
                Status = TaskSummaryStatus.Ready,
                FinishedAt = DateTime.UtcNow,
                ErrorMessage = error,
                Attempt = attempts,
                MaxAttempts = maxAttempts,
            };
            _logger.LogWarning(
                "Result finalization used agent-authored {Source} for {JobId} after summary failure: {Error}",
                fallback, info.Id, error);
            return new ResultFinalizationOutcome(TaskSummaryStatus.Ready, attempts, maxAttempts, error);
        }
        _states[info.TaskKey] = previous with
        {
            Status = TaskSummaryStatus.Degraded,
            FinishedAt = DateTime.UtcNow,
            ErrorMessage = error,
            Attempt = attempts,
            MaxAttempts = maxAttempts,
        };
        var degradedTotal = Interlocked.Increment(ref _degradedSummaries);
        _logger.LogWarning(
            "Result finalization degraded taskKey={TaskKey} jobId={JobId} attempts={Attempts} promptBytes={PromptBytes} limitBytes={LimitBytes} degradedTotal={DegradedTotal} error={Error}",
            info.TaskKey,
            info.Id,
            attempts,
            _promptBytes.TryGetValue(info.TaskKey, out var promptBytes) ? promptBytes : null,
            OrchestratorPromptBoundary.MaxPromptBytes,
            degradedTotal,
            error);
        return new ResultFinalizationOutcome(
            TaskSummaryStatus.Degraded,
            attempts,
            maxAttempts,
            error);
    }

    private bool TryUseAgentReportStatus(TaskInfo info, string? error, out string source)
    {
        var restored = TryRestoreAgentReportStatus(info, out source, _resultVersions);
        if (restored)
        {
            try
            {
                _timeline?.Append(info.FolderPath, TimelineEventKinds.ResultSummaryPending,
                    TimelineActors.System,
                    $"Summary assessment failed ({ResultFinalizationPolicy.Limit(error) ?? "unknown error"}); using {source} for review.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not record report summary fallback event for {JobId}", info.Id);
            }
        }
        return restored;
    }

    /// <summary>Restore agent-authored report evidence after a failed summary, including on a later recovery pass.</summary>
    public static bool TryRestoreAgentReportStatus(
        TaskInfo info, out string source, ResultVersionStore? resultVersions = null)
    {
        source = string.Empty;
        try
        {
            var target = Path.Combine(info.FolderPath, "status.md");
            var candidate = Path.Combine(info.FolderPath, "results", "status.md");
            string? markdown = null;
            if (File.Exists(candidate)
                && BoundedFileRead.TryReadAllText(candidate, BoundedFileRead.EvidenceTextBytes, out var agentStatus)
                && !string.IsNullOrWhiteSpace(agentStatus))
            {
                markdown = agentStatus;
                source = "results/status.md";
            }
            if (markdown is null)
            {
                var logPath = TaskPaths.CliOutputLog(info.FolderPath);
                if (File.Exists(logPath))
                {
                    var lines = BoundedFileRead.ReadTailLines(logPath, ReviewDecisionEvidenceReader.SourceWindowBytes);
                    markdown = SummaryPromptFit.LatestAgentMessage(lines);
                    if (!string.IsNullOrWhiteSpace(markdown)) source = "final agent message";
                }
            }

            if (string.IsNullOrWhiteSpace(markdown)) return false;
            markdown = SummaryPromptFit.ClipHead(markdown.Trim(), BoundedFileRead.EvidenceTextBytes);
            if (!File.Exists(target)
                || !BoundedFileRead.TryReadAllText(target, BoundedFileRead.EvidenceTextBytes, out var current)
                || !string.Equals(current, markdown, StringComparison.Ordinal))
            {
                if (resultVersions is not null)
                    resultVersions.Replace(info.FolderPath, markdown, ResultProducer.RunAttempt(),
                        info.State, DateTime.UtcNow, TimelineActors.Agent);
                else
                    File.WriteAllText(target, markdown);
            }
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task GenerateAsync(TaskInfo info, TerminalRunOutcome? runOutcome,
        CancellationToken ct = default, string? resultSha = null)
    {
        var key = info.TaskKey;
        var runIndex = _fileGenerationIndex?.CurrentRunIndex(info.FolderPath);

        // Inflight guard: if a previous GenerateAsync for the same job is
        // still inside its generation and format-retry window, dropping this duplicate avoids
        // racing two subprocesses against the same status.md (manual
        // Regenerate clicked while the post-run auto-call is still in
        // flight, or the runner re-fires after a missed completion). The
        // outstanding call will publish either Ready or Failed when it
        // returns; the user-visible spinner stays where it was.
        if (_states.TryGetValue(key, out var prev) && IsInflight(prev, DateTime.UtcNow, ModelTimeoutSeconds * MaxProtocolAttempts))
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
            runOutcome ??= TerminalRunOutcomeClassifier.TryClassifyRenderedLog(rawLog)?.Outcome;
            var outcomeLabel = runOutcome?.ProtocolResult ?? "unknown";
            var selection = ResolveStepSelection(info);
            var evidence = SummaryEvidenceAssembler.ReadFromFolder(info, _pipelineLog, _timeline, outcomeLabel);
            var prompt = BuildPrompt(info, rawLog, outcomeLabel, evidence, selection);

            var result = await RunValidatedSummaryAsync(prompt, info, selection, recordPipeline: true, ct);
            if (!result.Ok || string.IsNullOrWhiteSpace(result.Summary))
            {
                Fail(key, result.Error ?? "Empty summary response");
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
            summary = AppendImmutableResultSha(
                summary, resultSha ?? ReviewSubjectStore.Read(info.FolderPath)?.ResultSha);
            if (appendedImageCount > 0)
            {
                _logger.LogInformation("Summary protocol image references appended for {JobId}: {ImageCount} references",
                    info.Id, appendedImageCount);
            }

            // The assessment is bound to this exact status.md text, so writing it first
            // closes the window where a fetch sees the new status.md without its block.
            WriteAssessment(info, evidence, summary, result);
            var target = Path.Combine(info.FolderPath, "status.md");
            if (_resultVersions is not null)
            {
                _resultVersions.Replace(
                    info.FolderPath,
                    summary,
                    ResultProducer.RunAttempt(runIndex?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    info.State,
                    result.EndedAt,
                    TimelineActors.Agent);
            }
            else
            {
                WriteAllTextWithRetry(target, summary);
            }
            RegisterGeneratedStatus(info, result, selection, runIndex);
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
    ///   - returns the summary markdown to the caller instead of writing it to
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

        // Interim peek: the run is still alive, so there is no terminal outcome
        // to feed the classifier. Say so explicitly instead of guessing one.
        var selection = ResolveStepSelection(info);
        var evidence = SummaryEvidenceAssembler.ReadFromFolder(info, _pipelineLog, _timeline, "in progress");
        var prompt = BuildPrompt(info, rawLog, "in progress", evidence, selection);

        var sw = Stopwatch.StartNew();
        var result = await RunValidatedSummaryAsync(prompt, info, selection, recordPipeline: false, ct);
        sw.Stop();

        if (!result.Ok || string.IsNullOrWhiteSpace(result.Summary))
        {
            _logger.LogInformation("Interim summary failed for {JobId} after {ElapsedMs}ms: {Error}",
                info.Id, sw.ElapsedMilliseconds, result.Error);
            return InterimSummaryResult.Failure(result.Error ?? "Empty summary response");
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

    /// <summary>
    /// Renders the summary prompt with its <c>{{log}}</c> slot fitted into the
    /// bytes the rest of the prompt leaves under
    /// <see cref="OrchestratorPromptBoundary.MaxPromptBytes"/>. A shortened
    /// prompt is logged with its size and the running count.
    /// </summary>
    private string BuildPrompt(
        TaskInfo info, string rawLog, string outcome, SummaryEvidenceBundle evidence, SummaryStepSelection selection)
    {
        var slots = BuildSummarySlots(info, "", outcome, evidence);
        var fixedBytes = Encoding.UTF8.GetByteCount(
            _prompts.RenderUnrecorded(RuntimePromptService.SummaryProtocol, slots));
        var excerpt = SummaryPromptFit.Fit(
            ReadLogSources(info, rawLog),
            OrchestratorPromptBoundary.MaxPromptBytes - fixedBytes - PromptHeadroomBytes);
        slots["log"] = excerpt.Text;
        var prompt = _prompts.Render(RuntimePromptService.SummaryProtocol, slots,
            new PromptCallContext(info.ProjectName, PipelineCatalogue.SummaryAssessmentStepId, selection.Model));
        var promptBytes = Encoding.UTF8.GetByteCount(prompt);
        _promptBytes[info.TaskKey] = promptBytes;
        if (excerpt.Shortened)
        {
            _logger.LogInformation(
                "Summary prompt shortened taskKey={TaskKey} jobId={JobId} promptBytes={PromptBytes} limitBytes={LimitBytes} fixedBytes={FixedBytes} sourceLogBytes={SourceLogBytes} shortenedTotal={ShortenedTotal} omissions={Omissions}",
                info.TaskKey,
                info.Id,
                promptBytes,
                OrchestratorPromptBoundary.MaxPromptBytes,
                fixedBytes,
                excerpt.SourceLogBytes,
                Interlocked.Increment(ref _shortenedPrompts),
                string.Join("; ", excerpt.Omissions));
        }
        return prompt;
    }

    /// <summary>Reads the task prompt and the <c>results/</c> inventory; an unreadable source stays empty.</summary>
    private SummaryLogSources ReadLogSources(TaskInfo info, string rawLog)
    {
        string? taskPrompt = null;
        var deliverables = new List<SummaryDeliverable>();
        try
        {
            var promptPath = Path.Combine(info.FolderPath, "prompt.md");
            if (File.Exists(promptPath))
                taskPrompt = BoundedFileRead.ReadHeadText(promptPath, MaxTaskPromptReadBytes);
            var resultsDir = TaskPaths.ResultsDir(info.FolderPath);
            foreach (var file in TaskPaths.ResultArtifacts(info.FolderPath))
            {
                var name = Path.GetFileName(file);
                if (name is TaskPaths.ReviewEvidenceFileName or TaskPaths.ReviewObservationsFileName) continue;
                deliverables.Add(new SummaryDeliverable(
                    Path.Combine(TaskPaths.ResultsDirName, Path.GetRelativePath(resultsDir, file)),
                    new FileInfo(file).Length));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Summary prompt sources partly unreadable for {JobId}", info.Id);
        }
        return new SummaryLogSources(rawLog, taskPrompt, deliverables);
    }

    /// <summary>
    /// Builds the placeholder set for <c>summary-protocol.md</c>. Besides the
    /// log tail, it feeds the task metadata (<c>taskType</c> / <c>mode</c>) and
    /// the run <c>outcome</c> so the summarizer can pick the right result
    /// <c>Case</c> and frame the overview honestly (a blocked run reads as
    /// "where it stopped", not "shipped"). The frontend result view classifies
    /// the same signals independently, so these slots only need to nudge the
    /// model; a missing or wrong value degrades to the client-side heuristic.
    /// The <c>evidence</c> slot carries the card's <see cref="SummaryEvidenceBundle"/>
    /// as JSON so the closing assessment can reconcile it with the log.
    /// Exposed for the prompt-contract test that pins this wiring without a
    /// billable model round-trip.
    /// </summary>
    public static Dictionary<string, string?> BuildSummarySlots(
        TaskInfo info, string log, string outcome, SummaryEvidenceBundle? evidence = null)
        => new()
        {
            ["log"] = log,
            ["taskType"] = info.TaskType,
            ["mode"] = info.Mode,
            ["outcome"] = outcome,
            ["evidence"] = evidence is null ? "{}" : SummaryEvidenceAssembler.ToPromptJson(evidence),
        };

    /// <summary>CLI, model and reasoning level the summary step runs with, and where the model came from.</summary>
    public sealed record SummaryStepSelection(string Cli, string Model, string? ThinkingLevel, string ModelSource);

    /// <summary>
    /// Resolves the <c>post-summary-assessment</c> step through the shared
    /// pipeline resolver: step override, project orchestrator model, then the
    /// catalogue default (Claude Opus 5.5, low), the same chain the project
    /// pipeline settings display. The shared <c>ClaudeCli:SummaryModel</c> key
    /// (titles, prompt enhancement) no longer applies here. A model inherited
    /// from the project level that the chosen CLI cannot run falls back to the
    /// catalogue default, or the CLI's own default when the CLI is overridden.
    /// </summary>
    public static SummaryStepSelection ResolveStepSelection(ProjectSettings? settings)
    {
        var step = PipelineCatalogue.SummaryAssessmentStep;
        var cli = PipelineStepConfigResolver.ResolveCliType(settings, step) ?? step.CliType ?? CliTypes.Claude;
        var resolution = PipelineStepConfigResolver.ResolveModelWithSource(settings, step, step.Model!);
        var model = resolution.Model;
        var source = resolution.Source;
        if (source != PipelineStepConfigResolver.ModelSourceStep && !ModelMetadataRegistry.IsCompatibleWithCli(cli, model))
        {
            model = ModelMetadataRegistry.IsCompatibleWithCli(cli, step.Model)
                ? step.Model!
                : ModelMetadataRegistry.DefaultForCli(cli) ?? step.Model!;
            source = PipelineStepConfigResolver.ModelSourceCatalogue;
        }
        var thinking = PipelineStepConfigResolver.ResolveThinkingLevel(
            settings, step, cli, model, PipelineCatalogue.SummaryAssessmentThinkingLevel);
        return new SummaryStepSelection(cli, model, thinking, source);
    }

    private SummaryStepSelection ResolveStepSelection(TaskInfo info)
    {
        ProjectSettings? settings = null;
        try
        {
            if (_projectSettings is not null && !string.IsNullOrWhiteSpace(info.ProjectName))
                settings = PipelineTypeSettings.ForTask(_projectSettings.Get(info.ProjectName), info);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Summary step settings unreadable for {JobId}; using catalogue defaults", info.Id);
        }
        return ResolveStepSelection(settings);
    }

    /// <summary>
    /// Reconciles the evidence bundle with the generated status.md header and
    /// the model's structured block, then persists the result. Best-effort: an
    /// assessment failure never fails the Result document.
    /// </summary>
    private void WriteAssessment(TaskInfo info, SummaryEvidenceBundle evidence, string statusMarkdown, SummaryModelResult result)
    {
        try
        {
            var assessment = BuildAssessment(evidence, statusMarkdown, result.AssessmentJson, result.Usage?.Model ?? result.Model, DateTime.UtcNow);
            SummaryAssessmentStore.Write(info.FolderPath, assessment, statusMarkdown);
            if (assessment.FallbackReason is not null)
            {
                _logger.LogInformation("Summary assessment for {JobId} used the policy fallback: {Reason}",
                    info.Id, assessment.FallbackReason);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Summary assessment could not be written for {JobId}", info.Id);
        }
    }

    /// <summary>Pure assembly of the persisted assessment; exposed for tests.</summary>
    public static SummaryAssessment BuildAssessment(
        SummaryEvidenceBundle evidence, string statusMarkdown, string? assessmentJson, string? model, DateTime generatedAt)
    {
        var withHeader = SummaryEvidenceAssembler.WithStatusHeader(evidence, statusMarkdown);
        var policy = SummaryAssessmentPolicy.Assess(withHeader, generatedAt);
        var parsed = SummaryAssessmentParser.TryParse(assessmentJson, out var parseError);
        return SummaryAssessmentParser.Merge(policy, parsed, model, parseError);
    }

    private async Task<SummaryModelResult> RunValidatedSummaryAsync(
        string prompt, TaskInfo info, SummaryStepSelection selection, bool recordPipeline, CancellationToken ct)
    {
        var evidence = new List<string>();
        var pipelineAttempt = _pipelineLog?.Read(info.FolderPath)?.Attempt;
        var step = new PipelineStepExecution
        {
            StepId = PipelineCatalogue.SummaryAssessmentStepId, Kind = StepKind.Orchestrator, Attempt = pipelineAttempt,
            StartedAt = DateTime.UtcNow,
        };
        var usage = new OrchestratorTokenUsage();
        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var result = await RunModelAsync(prompt, info, selection, ct);
            if (result.Ok)
            {
                // The structured block is peeled off before validation so the
                // prose is checked exactly as status.md will store it.
                var split = SummaryAssessmentParser.Split(result.Summary);
                result = result with { Summary = split.Markdown.Trim(), AssessmentJson = split.AssessmentJson };
            }
            var validationError = result.Ok ? SummaryProtocolValidation.Validate(result.Summary) : null;
            if (validationError is not null)
                result = result with { Ok = false, Error = validationError };

            AdHocClaudeInvoker.Record(_usage, AdHocUsageSources.SummaryGeneration, result.Model, result.Usage,
                result.DurationMs, ok: result.Ok, project: info.ProjectName, jobId: info.Id);
            evidence.Add($"Attempt {attempt}: {(result.Ok ? "passed" : result.Error ?? "generation failed")}");
            usage = Accumulate(usage, result.Usage);
            step = StepModelUsage.From(usage, result.Model, selection.ThinkingLevel, selection.ModelSource).ApplyTo(step with
            {
                Status = result.Ok ? PipelineStepStatus.Passed : PipelineStepStatus.Failed,
                CompletedAt = result.EndedAt,
                DurationMs = step.DurationMs + result.DurationMs,
                Reason = result.Error,
                VerdictSummary = string.Join("\n", evidence),
            });
            if (recordPipeline) _pipelineLog?.RecordStep(info.FolderPath, step);
            if (validationError is null || attempt == MaxProtocolAttempts) return result;

            _logger.LogWarning(
                "Summary protocol rejected taskKey={TaskKey} attempt={Attempt}; retrying once: {Error}",
                info.TaskKey, attempt, validationError);
        }
    }

    private static OrchestratorTokenUsage Accumulate(OrchestratorTokenUsage total, OrchestratorTokenUsage? next)
        => next is null ? total : total with
        {
            Model = string.IsNullOrWhiteSpace(next.Model) ? total.Model : next.Model,
            ThinkingLevel = string.IsNullOrWhiteSpace(next.ThinkingLevel) ? total.ThinkingLevel : next.ThinkingLevel,
            InputTokens = total.InputTokens + next.InputTokens,
            OutputTokens = total.OutputTokens + next.OutputTokens,
            CacheReadTokens = total.CacheReadTokens + next.CacheReadTokens,
            CacheCreationTokens = total.CacheCreationTokens + next.CacheCreationTokens,
            InputIncludesCached = next.InputIncludesCached ?? total.InputIncludesCached,
        };

    private async Task<SummaryModelResult> RunModelAsync(
        string prompt, TaskInfo info, SummaryStepSelection selection, CancellationToken ct)
    {
        var model = selection.Model;
        var workingDirectory = info.FolderPath;
        var startedAt = DateTime.UtcNow;
        var sw = Stopwatch.StartNew();

        var oneShot = _oneShotRegistry?.Get(selection.Cli);
        if (oneShot != null)
        {
            var r = await oneShot.RunAsync(new CliOneShotRequest(
                CliType: selection.Cli, Model: model, Prompt: prompt)
            {
                ThinkingLevel = selection.ThinkingLevel,
                WorkingDirectory = Directory.Exists(workingDirectory) ? workingDirectory : null,
                Timeout = TimeSpan.FromSeconds(ModelTimeoutSeconds),
                Source = AdHocUsageSources.SummaryGeneration,
                Project = info.ProjectName,
                JobId = info.Id,
                StepId = PipelineCatalogue.SummaryAssessmentStepId,
                RecordUsage = false, // We record below with parsed text + usage
            }, ct).ConfigureAwait(false);

            sw.Stop();
            var endedAt = DateTime.UtcNow;
            var effectiveModel = string.IsNullOrWhiteSpace(r.EffectiveModel) ? model : r.EffectiveModel!;
            if (!r.Ok) return SummaryModelResult.Failure(effectiveModel, r.Error, startedAt, endedAt, sw.ElapsedMilliseconds) with { Usage = r.Usage };
            return SummaryModelResult.Success(effectiveModel, r.Usage, r.ParsedText.Trim(), startedAt, endedAt,
                (long)r.Duration.TotalMilliseconds);
        }

        if (!string.Equals(selection.Cli, CliTypes.Claude, StringComparison.OrdinalIgnoreCase))
        {
            return SummaryModelResult.Failure(model, $"CLI '{selection.Cli}' is not available for the summary step.",
                startedAt, DateTime.UtcNow, 0);
        }

        var claudePath = _configuration["ClaudeCli:Path"] ?? "claude";

        // Feed the prompt via stdin instead of a positional `-p <prompt>`
        // argument. See OneShot service for the production path; this
        // fallback is for tests that build the service without DI.
        using var workspace = new HostOneShotIsolation.Workspace();
        var psi = new ProcessStartInfo
        {
            FileName = GenericCliExecutionService.ResolveExecutable(claudePath),
            WorkingDirectory = workspace.Path,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = System.Text.Encoding.UTF8,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        var isolated = !SecurityProfiles.IsLocal(_configuration);
        foreach (var arg in AdHocClaudeInvoker.BuildArgs(model, isolated)) psi.ArgumentList.Add(arg);

        try
        {
            using var p = HostCliSpawnGuard.Start(psi, !SecurityProfiles.IsLocal(_configuration), CliTypes.Claude);
            if (p == null) return SummaryModelResult.Failure(model, "Process.Start returned null", startedAt, DateTime.UtcNow, sw.ElapsedMilliseconds);

            // Write the prompt up front, then close stdin so Claude can finalise
            // the request. WriteAsync is awaited so the OS pipe buffer can drain
            // before we move on to reading stdout.
            await p.StandardInput.WriteAsync(AdHocClaudeInvoker.PreparePrompt(prompt, isolated).AsMemory(), ct);
            p.StandardInput.Close();

            var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = p.StandardError.ReadToEndAsync(ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(ModelTimeoutSeconds));
            await p.WaitForExitAsync(cts.Token);

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            sw.Stop();
            var endedAt = DateTime.UtcNow;
            if (p.ExitCode != 0)
            {
                return SummaryModelResult.Failure(model, $"claude exited {p.ExitCode}: {stderr.Trim()}", startedAt, endedAt, sw.ElapsedMilliseconds);
            }

            var (text, usage) = AdHocClaudeInvoker.ParseOrFallback(stdout, model);
            return SummaryModelResult.Success(model, usage, text.Trim(), startedAt, endedAt, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return SummaryModelResult.Failure(model, $"Summary model timed out after {ModelTimeoutSeconds}s", startedAt, DateTime.UtcNow, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return SummaryModelResult.Failure(model, ex.Message, startedAt, DateTime.UtcNow, sw.ElapsedMilliseconds);
        }
    }

    private void RegisterGeneratedStatus(TaskInfo info, SummaryModelResult result, SummaryStepSelection selection, int? runIndex)
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
                Cli = selection.Cli,
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
                StepId = PipelineCatalogue.SummaryAssessmentStepId,
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

    /// <summary>Records the immutable delivery identity on the card's Result document.</summary>
    public static string AppendImmutableResultSha(string markdown, string? resultSha)
    {
        if (!ReviewSubjectStore.IsValidResultSha(resultSha)) return markdown;
        var line = $"- Immutable result SHA: `{resultSha}`";
        if (markdown.Contains(line, StringComparison.Ordinal)) return markdown;
        return markdown.TrimEnd('\r', '\n') + "\n\n" + line + "\n";
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

    private sealed record SummaryModelResult(
        bool Ok,
        string? Summary,
        string? Error,
        string Model,
        OrchestratorTokenUsage? Usage,
        long DurationMs,
        DateTime StartedAt,
        DateTime EndedAt)
    {
        /// <summary>Raw JSON of the trailing assessment block, peeled off the prose.</summary>
        public string? AssessmentJson { get; init; }

        public static SummaryModelResult Success(
            string model,
            OrchestratorTokenUsage? usage,
            string? summary,
            DateTime startedAt,
            DateTime endedAt,
            long durationMs)
            => new(true, summary, null, model, usage, durationMs, startedAt, endedAt);

        public static SummaryModelResult Failure(
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
/// On success carries the summary markdown and the call duration so the UI
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
