

namespace AgentStudio.Drift;

/// <summary>
/// Runs the opt-in drift dimensions as automatic post-steps after a task
/// settles on the <c>3-progress -&gt; 4-auto-review</c> transition (DRIFT
/// Nachtrag). It is the missing trigger the manual <c>DriftReportEndpoints</c>
/// actions never had: per the acceptance, "the activated drift aspects run
/// automatically as a post-step and produce their reports - no manual button
/// needed", which also fixes "drift is currently never analysed automatically".
///
/// <para>
/// No duplication: this coordinator reuses the existing
/// <c>*DriftAnalysisService</c> + <see cref="DriftReportStore"/>; only the
/// trigger and per-step wiring are new. For the four LLM dimensions it walks
/// the same SelectScope -&gt; BuildPrompt -&gt; (agent) -&gt;
/// TryParseAgentResponse -&gt; BuildReport path the manual endpoint uses, but
/// feeds the agent reply from a one-shot CLI call instead of an operator POST.
/// The rule-based code-pattern dimension runs its deterministic
/// <see cref="CodePatternDriftAnalysisService.Analyze"/> and is mapped into a
/// <see cref="DriftReport"/> so it lands in the same store and project view.
/// </para>
///
/// <para>
/// Every enabled dimension records a <see cref="StepKind.Drift"/> step into the
/// triggering job's <c>pipeline-execution.json</c> (model, tokens, status) so
/// the run surfaces in the Overview pipeline telemetry next to core / aspect /
/// orchestrator spend. Drift steps default OFF (<see
/// cref="PipelineStep.DefaultEnabled"/> = false) because a drift run is an
/// expensive extra pass an operator opts into per project.
/// </para>
/// </summary>
public sealed class DriftPostStepRunner
{
    /// <summary>Model used when neither the step nor the project sets one.</summary>
    public const string DefaultCli = CliTypes.Codex;
    public static string DefaultModel => ModelFamilyResolver.Resolve(ModelFamilies.GptMini);
    public const string DefaultThinkingLevel = "high";

    private static readonly TimeSpan PerDimensionTimeout = TimeSpan.FromMinutes(5);

    private readonly RuntimePromptService _prompts;
    private readonly DriftReportStore _driftStore;
    private readonly AnalysisReportStore _analysisStore;
    private readonly AdrCodeDriftAnalysisService _adrCode;
    private readonly SoftwareArchitectureDriftAnalysisService _softwareArch;
    private readonly DocsMarketingDriftAnalysisService _docsMarketing;
    private readonly SpecTaskDriftAnalysisService _specTask;
    private readonly CodePatternDriftAnalysisService _codePattern;
    private readonly PipelineExecutionLog _pipelineLog;
    private readonly IConfiguration _config;
    private readonly ILogger<DriftPostStepRunner> _logger;
    private readonly CliOneShotRegistry? _oneShotRegistry;
    private readonly FileGenerationIndex _fileGenerationIndex;

    /// <summary>
    /// CLI invocation seam. Production wires it onto the shared
    /// <see cref="ICliOneShot"/> selected by the resolved step CLI so prompts travel via stdin and
    /// usage is recorded centrally. Tests substitute a deterministic stub so
    /// the dispatch / gating / telemetry can be asserted without a subprocess.
    /// </summary>
    public Func<string, string, string, string?, string?, TimeSpan, CancellationToken, Task<DriftCliResult>> CliRunner { get; set; }
        = (_, _, _, _, _, _, _) => Task.FromResult(new DriftCliResult(false, string.Empty, null));
    private Func<string, string, string, string?, string?, string?, TimeSpan, CancellationToken, Task<DriftCliResult>>? _thinkingAwareCliRunner;

    public DriftPostStepRunner(
        RuntimePromptService prompts,
        DriftReportStore driftStore,
        AnalysisReportStore analysisStore,
        AdrCodeDriftAnalysisService adrCode,
        SoftwareArchitectureDriftAnalysisService softwareArch,
        DocsMarketingDriftAnalysisService docsMarketing,
        SpecTaskDriftAnalysisService specTask,
        CodePatternDriftAnalysisService codePattern,
        PipelineExecutionLog pipelineLog,
        IConfiguration config,
        ILogger<DriftPostStepRunner> logger,
        CliOneShotRegistry? oneShotRegistry = null,
        FileGenerationIndex? fileGenerationIndex = null)
    {
        _prompts = prompts;
        _driftStore = driftStore;
        _analysisStore = analysisStore;
        _adrCode = adrCode;
        _softwareArch = softwareArch;
        _docsMarketing = docsMarketing;
        _specTask = specTask;
        _codePattern = codePattern;
        _pipelineLog = pipelineLog;
        _config = config;
        _logger = logger;
        _oneShotRegistry = oneShotRegistry;
        _fileGenerationIndex = fileGenerationIndex ?? new FileGenerationIndex(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FileGenerationIndex>.Instance,
            pipelineLog);

        if (_oneShotRegistry != null)
        {
            _thinkingAwareCliRunner = RunViaOneShotAsync;
        }
    }

    private async Task<DriftCliResult> RunViaOneShotAsync(
        string cliType, string model, string prompt, string? project, string? jobId, string? thinkingLevel, TimeSpan timeout, CancellationToken ct)
    {
        var oneShot = _oneShotRegistry?.Get(cliType);
        if (oneShot == null) return new DriftCliResult(false, string.Empty, null);

        var result = await oneShot.RunAsync(new CliOneShotRequest(
            CliType: cliType,
            Model: model,
            Prompt: prompt)
        {
            ThinkingLevel = thinkingLevel,
            Timeout = timeout,
            Source = AdHocUsageSources.DriftAnalysis,
            RecordUsage = true,
            Project = project,
            JobId = jobId,
        }, ct).ConfigureAwait(false);

        if (!result.Ok)
        {
            _logger.LogWarning(
                "Drift one-shot CLI call failed: exit={ExitCode} duration={Duration}ms error={Error}",
                result.ExitCode, result.Duration.TotalMilliseconds, result.Error);
        }
        return new DriftCliResult(result.Ok, result.ParsedText ?? string.Empty, result.Usage);
    }

    /// <summary>
    /// Run every enabled drift dimension for one just-completed task. Gating,
    /// model resolution, persistence, and telemetry happen per dimension; one
    /// dimension's failure never aborts the others (each is guarded), and the
    /// whole method is safe to fire-and-forget from the transition path.
    /// </summary>
    public async Task RunAsync(
        string project,
        string jobId,
        string jobFolderPath,
        ProjectSettings? settings,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(jobFolderPath)) return;

        var ctx = new PipelineStepConditionContext
        {
            Aborted = false,
            ExitCode = 0,
            AnyAspectFailed = false,
        };
        var enabled = ProjectPipelineOrder.Apply(PipelineCatalogue.Standard, settings).Post
            .Where(s => s.Kind == StepKind.Drift && PipelineStepConfigResolver.ShouldRun(settings, s, ctx))
            .ToList();
        if (enabled.Count == 0) return;

        var workspace = _config["TaskRepository"];
        if (string.IsNullOrWhiteSpace(workspace))
        {
            _logger.LogWarning("Drift post-step skipped for {Project}/{JobId}: TaskRepository not configured", project, jobId);
            return;
        }

        var projectRoot = Path.Combine(workspace!, "projects", project);
        if (!Directory.Exists(projectRoot))
        {
            _logger.LogWarning("Drift post-step skipped for {Project}/{JobId}: project root not found at {Root}", project, jobId, projectRoot);
            return;
        }

        var repoRoot = DriftRepoRootLocator.Resolve();

        foreach (var step in enabled)
        {
            ct.ThrowIfCancellationRequested();
            var pipelineRecord = EnsureRunRecord(jobFolderPath, project, jobId);
            using var pipelineAttempt = _pipelineLog.EnterAttempt(jobFolderPath, pipelineRecord.Attempt);
            var cliType = PipelineStepConfigResolver.ResolveCliType(settings, step) ?? DefaultCli;
            var model = PipelineStepConfigResolver.ResolveModel(settings, step, DefaultModel);
            var thinkingLevel = PipelineStepConfigResolver.ResolveThinkingLevel(
                settings, step, cliType, model, DefaultThinkingLevel);
            try
            {
                var promptOverride = PipelineStepConfigResolver.ResolvePrompt(settings, step);
                await RunDimensionAsync(step, cliType, model, thinkingLevel, promptOverride, project, jobId, jobFolderPath, projectRoot, repoRoot, workspace!, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Drift dimension '{StepId}' failed for {Project}/{JobId}", step.Id, project, jobId);
                RecordStep(jobFolderPath, step.Id, model, PipelineStepStatus.Failed, null, "drift-dimension-exception");
            }
        }
    }

    private async Task RunDimensionAsync(
        PipelineStep step,
        string cliType,
        string model,
        string? thinkingLevel,
        string? promptOverride,
        string project,
        string jobId,
        string jobFolderPath,
        string projectRoot,
        string repoRoot,
        string workspace,
        CancellationToken ct)
    {
        var startedAt = DateTime.UtcNow;
        RecordStep(jobFolderPath, step.Id, model, PipelineStepStatus.Running, null, null, startedAt);

        // The code-pattern dimension is deterministic (no LLM call). The other
        // four are LLM dimensions sharing the manual endpoint's flow.
        if (string.Equals(step.Id, PipelineCatalogue.DriftCodePatternStepId, StringComparison.OrdinalIgnoreCase))
        {
            var cpReport = _codePattern.Analyze(repoRoot);
            var markdown = _codePattern.RenderMarkdown(cpReport, project);
            var report = MapCodePatternReport(cpReport, project, NewReportId(), DateTime.UtcNow, DriftReportTrigger.Scheduled);
            await _driftStore.AppendAsync(workspace, project, report, markdown, ct).ConfigureAwait(false);
            var completedAt = DateTime.UtcNow;
            RecordStep(jobFolderPath, step.Id, model, PipelineStepStatus.Passed, null, null, startedAt, completedAt);
            RegisterGenerated(jobFolderPath, step.Id, model, null, null, startedAt, completedAt, report.ReportId);
            return;
        }

        var (prompt, persist) = BuildDimension(
            step.Id,
            project,
            projectRoot,
            repoRoot,
            workspace,
            promptOverride,
            model);
        if (persist == null)
        {
            // Unknown drift step id - record a no-op so the telemetry is honest.
            RecordStep(jobFolderPath, step.Id, model, PipelineStepStatus.Failed, null, "unknown-drift-step");
            return;
        }
        var cli = _thinkingAwareCliRunner is null
            ? await CliRunner(cliType, model, prompt, project, jobId, PerDimensionTimeout, ct).ConfigureAwait(false)
            : await _thinkingAwareCliRunner(cliType, model, prompt, project, jobId, thinkingLevel, PerDimensionTimeout, ct).ConfigureAwait(false);
        var agentText = cli.Text ?? string.Empty;
        var reportId = NewReportId();
        var markdownBody = !string.IsNullOrWhiteSpace(agentText)
            ? agentText
            : $"# {step.DisplayName} (automatic post-step)\n\nNo agent narrative was produced (CLI ok={cli.Ok}). Evidence-only drift report.";

        await persist(agentText, reportId, markdownBody, ct).ConfigureAwait(false);

        var usage = cli.Usage;
        var endedAt = DateTime.UtcNow;
        RecordStep(
            jobFolderPath, step.Id, usage?.Model ?? model,
            cli.Ok ? PipelineStepStatus.Passed : PipelineStepStatus.Failed,
            usage, cli.Ok ? null : "drift-cli-failed", startedAt, endedAt);
        RegisterGenerated(jobFolderPath, step.Id, usage?.Model ?? model, usage, cliType, startedAt, endedAt, reportId);
    }

    private void RegisterGenerated(
        string jobFolderPath,
        string stepId,
        string model,
        OrchestratorTokenUsage? usage,
        string? cli,
        DateTime startedAt,
        DateTime endedAt,
        string reportId)
    {
        _fileGenerationIndex.Upsert(jobFolderPath, new FileGenerationMeta
        {
            File = $"logs/drift/{reportId}.md",
            Kind = "drift",
            Model = usage?.Model ?? model,
            Cli = cli,
            TokensIn = usage?.InputTokens ?? 0,
            TokensOut = usage?.OutputTokens ?? 0,
            TokensTotal = (usage?.InputTokens ?? 0)
                + (usage?.OutputTokens ?? 0)
                + (usage?.CacheReadTokens ?? 0)
                + (usage?.CacheCreationTokens ?? 0),
            StartedAt = startedAt,
            EndedAt = endedAt,
            DurationMs = (long)(endedAt - startedAt).TotalMilliseconds,
            StepId = stepId,
        });
    }

    /// <summary>
    /// Assemble the per-dimension prompt and return a closure that, given the
    /// agent reply, parses it and persists the structured report through the
    /// same BuildReport path the manual endpoint uses. Returns a null persist
    /// delegate for an unknown step id.
    /// </summary>
    private (string Prompt, Func<string, string, string, CancellationToken, Task>? Persist) BuildDimension(
        string stepId,
        string project,
        string projectRoot,
        string repoRoot,
        string workspace,
        string? promptOverride,
        string model)
    {
        if (string.Equals(stepId, PipelineCatalogue.DriftAdrCodeStepId, StringComparison.OrdinalIgnoreCase))
        {
            var scope = _adrCode.SelectScope(project, projectRoot, repoRoot, _driftStore, _analysisStore, workspace);
            var template = EffectiveTemplate("adr-code-drift.md", stepId, promptOverride, project, model);
            var prompt = _adrCode.BuildPrompt(scope, template);
            Func<string, string, string, CancellationToken, Task> persist = async (agent, reportId, _, ct) =>
            {
                var parse = _adrCode.TryParseAgentResponse(agent);
                var report = _adrCode.BuildReport(scope, parse, reportId, DateTime.UtcNow, DriftReportTrigger.Scheduled);
                await _driftStore.AppendAsync(workspace, project, report, MarkdownFor(agent, report), ct).ConfigureAwait(false);
            };
            return (prompt, persist);
        }

        if (string.Equals(stepId, PipelineCatalogue.DriftSoftwareArchitectureStepId, StringComparison.OrdinalIgnoreCase))
        {
            var watchedProjectRoot = _config[$"Drift:WatchedProjectRoots:{project}"];
            var scope = _softwareArch.SelectScope(project, projectRoot, repoRoot, watchedProjectRoot, workspace, _driftStore, _analysisStore);
            var template = EffectiveTemplate("software-architecture-drift.md", stepId, promptOverride, project, model);
            var prompt = _softwareArch.BuildPrompt(scope, template);
            Func<string, string, string, CancellationToken, Task> persist = async (agent, reportId, _, ct) =>
            {
                var parse = _softwareArch.TryParseAgentResponse(agent);
                var report = _softwareArch.BuildReport(scope, parse, reportId, DateTime.UtcNow, DriftReportTrigger.Scheduled);
                await _driftStore.AppendAsync(workspace, project, report, MarkdownFor(agent, report), ct).ConfigureAwait(false);
            };
            return (prompt, persist);
        }

        if (string.Equals(stepId, PipelineCatalogue.DriftDocsMarketingStepId, StringComparison.OrdinalIgnoreCase))
        {
            var marketingRepo = _config["Drift:MarketingRepoPath"];
            var scope = _docsMarketing.SelectScope(project, projectRoot, repoRoot, marketingRepo, _driftStore, _analysisStore, workspace);
            var template = EffectiveTemplate("docs-marketing-drift.md", stepId, promptOverride, project, model);
            var prompt = _docsMarketing.BuildPrompt(scope, template);
            Func<string, string, string, CancellationToken, Task> persist = async (agent, reportId, _, ct) =>
            {
                var parse = _docsMarketing.TryParseAgentResponse(agent);
                var report = _docsMarketing.BuildReport(scope, parse, reportId, DateTime.UtcNow, DriftReportTrigger.Scheduled);
                await _driftStore.AppendAsync(workspace, project, report, MarkdownFor(agent, report), ct).ConfigureAwait(false);
            };
            return (prompt, persist);
        }

        if (string.Equals(stepId, PipelineCatalogue.DriftSpecTaskJobStepId, StringComparison.OrdinalIgnoreCase))
        {
            var scope = _specTask.SelectScope(project, projectRoot, repoRoot, _driftStore, _analysisStore, workspace);
            var template = EffectiveTemplate("spec-task-job-drift.md", stepId, promptOverride, project, model);
            var prompt = _specTask.BuildPrompt(scope, template);
            Func<string, string, string, CancellationToken, Task> persist = async (agent, reportId, _, ct) =>
            {
                var parse = _specTask.TryParseAgentResponse(agent);
                var report = _specTask.BuildReport(scope, parse, reportId, DateTime.UtcNow, DriftReportTrigger.Scheduled);
                await _driftStore.AppendAsync(workspace, project, report, MarkdownFor(agent, report), ct).ConfigureAwait(false);
            };
            return (prompt, persist);
        }

        return (string.Empty, null);
    }

    private string EffectiveTemplate(
        string templateName,
        string stepId,
        string? promptOverride,
        string project,
        string model)
    {
        var context = new PromptCallContext(project, stepId, model);
        return string.IsNullOrWhiteSpace(promptOverride)
            ? _prompts.Render(
                templateName,
                new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase),
                context)
            : _prompts.UseProjectOverride(templateName, promptOverride, context);
    }

    private static string MarkdownFor(string agentText, DriftReport report) =>
        !string.IsNullOrWhiteSpace(agentText)
            ? agentText
            : $"# {report.Project} drift (automatic post-step)\n\nNo agent narrative supplied; evidence-only report. Summary: {report.Summary}";

    private PipelineExecutionRecord EnsureRunRecord(string jobFolderPath, string project, string jobId)
    {
        // Record into the existing run when one is present (the core + aspect
        // stages of this same run created it); only begin a fresh record when
        // none exists yet, so RecordStep is never a silent no-op.
        return _pipelineLog.Read(jobFolderPath)
            ?? _pipelineLog.EnsureRun(jobFolderPath, PipelineCatalogue.Standard, project, jobId);
    }

    private void RecordStep(
        string jobFolderPath,
        string stepId,
        string model,
        PipelineStepStatus status,
        OrchestratorTokenUsage? usage,
        string? reason,
        DateTime? startedAt = null,
        DateTime? completedAt = null)
    {
        _pipelineLog.RecordStep(jobFolderPath, new PipelineStepExecution
        {
            StepId = stepId,
            Kind = StepKind.Drift,
            Model = model,
            Status = status,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            DurationMs = startedAt != null && completedAt != null
                ? (long)(completedAt.Value - startedAt.Value).TotalMilliseconds
                : 0,
            InputTokens = usage?.InputTokens ?? 0,
            OutputTokens = usage?.OutputTokens ?? 0,
            CacheReadTokens = usage?.CacheReadTokens ?? 0,
            CacheCreationTokens = usage?.CacheCreationTokens ?? 0,
            Reason = reason,
        });
    }

    private static string NewReportId() =>
        "01" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + Guid.NewGuid().ToString("N").Substring(0, 8);

    /// <summary>
    /// Map the deterministic <see cref="CodePatternDriftReport"/> onto the
    /// shared <see cref="DriftReport"/> shape so the rule-based dimension lands
    /// in <see cref="DriftReportStore"/> and the project Drift view alongside
    /// the LLM dimensions. The findings collapse into a single
    /// <see cref="DriftDimensionType.Process"/> dimension (one
    /// <see cref="DriftFinding"/> per drifted rule). Score follows the shared
    /// convention where 100 is healthiest.
    /// </summary>
    internal static DriftReport MapCodePatternReport(
        CodePatternDriftReport cp,
        string project,
        string reportId,
        DateTime createdAt,
        DriftReportTrigger trigger)
    {
        var maxSeverity = cp.Findings.Count == 0
            ? DriftSeverity.Info
            : cp.Findings.Max(f => f.OverallSeverity);
        var band = maxSeverity switch
        {
            DriftSeverity.Critical => DriftScoreBand.Critical,
            DriftSeverity.High => DriftScoreBand.Warn,
            DriftSeverity.Warn => DriftScoreBand.Watch,
            _ => DriftScoreBand.Healthy,
        };
        var score = Math.Clamp(100 - Math.Min(cp.TotalDriftSites * 10, 100), 0, 100);

        var findings = cp.Findings
            .Where(f => f.DriftSites > 0)
            .Select(f => new DriftFinding(
                FindingId: f.RuleId,
                Severity: f.OverallSeverity,
                Summary: $"{f.Title}: {f.DriftSites} drift site(s) of {f.TotalSites}.",
                Status: DriftFindingStatus.New,
                EvidenceRefs: f.Hits
                    .Where(h => h.IsDrift)
                    .Select(h => $"{h.FilePath}:{h.LineNumber}")
                    .ToArray()))
            .ToArray();

        var evidenceRefs = findings
            .SelectMany(f => f.EvidenceRefs ?? Array.Empty<string>())
            .Distinct(StringComparer.Ordinal)
            .Take(50)
            .ToArray();

        var summary = cp.TotalDriftSites == 0
            ? "No code-pattern drift detected across the rule set."
            : $"{cp.TotalDriftSites} code-pattern drift site(s) across {findings.Length} rule(s).";

        var dimension = new DriftDimension(
            Type: DriftDimensionType.Process,
            Score: score,
            Severity: maxSeverity,
            Confidence: 1.0,
            SourceCoverage: 1.0,
            Status: DriftFindingStatus.New,
            Summary: summary,
            EvidenceRefs: evidenceRefs,
            RecommendedActions: cp.TotalDriftSites == 0
                ? Array.Empty<string>()
                : new[] { "Review the flagged sites and realign them with the canonical pattern." },
            Findings: findings.Length > 0 ? findings : null);

        return new DriftReport(
            ReportId: reportId,
            Project: project,
            CreatedAt: createdAt,
            Trigger: trigger,
            Scope: new DriftReportScope(DriftReportScopeKind.Project),
            OverallScore: score,
            ScoreBand: band,
            Dimensions: new[] { dimension },
            Summary: summary,
            FollowUpTaskSuggestions: Array.Empty<DriftFollowUpSuggestion>(),
            Producer: new DriftReportProducer(DriftReportProducerKind.Scheduled, Agent: CodePatternDriftAnalysisService.Topic),
            ParseStatus: DriftReportParseStatus.Structured);
    }
}

/// <summary>
/// Result of one drift CLI invocation: whether the call succeeded, the parsed
/// reply text, and the token usage (null when the call failed or the stub
/// returned no usage). Mirrors the slice of <c>CliOneShotResult</c> the runner
/// needs without coupling the test seam to the full one-shot type.
/// </summary>
public sealed record DriftCliResult(bool Ok, string Text, OrchestratorTokenUsage? Usage);
