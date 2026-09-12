using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AgentStudio.Runner;

/// <summary>
/// Runs the multi-aspect quality pass over a 4-auto-review job whose
/// agent reported <c>[[TASK_DONE]]</c>. Each aspect is a separate
/// fast-model call against a small, scoped prompt; each aspect writes
/// its own <c>aspect-{name}.md</c> into the job folder. The aggregate
/// verdict drives one of three lane decisions in the orchestrator:
///
/// <list type="bullet">
///   <item>any <c>block</c> → reissue back to <c>3-progress</c> with a
///         follow-up prompt summarising the per-aspect findings</item>
///   <item>no block but at least one <c>concerns</c> → accept-as-done
///         and hang <c>{namespace}:concerns</c> tags on the job for the
///         human-review lane</item>
///   <item>all <c>pass</c> → accept-as-done with no concern tags</item>
/// </list>
///
/// <para>
/// The pipeline never owns task state: it does not move the job lane or
/// re-issue a follow-up. It may resume terminal aspect evidence from the
/// current fenced pipeline attempt after a backend restart. Lane transitions
/// and attempt authority stay with the orchestrator and task state machine.
/// </para>
/// </summary>
public sealed class AspectRunnerService
{
    private readonly RuntimePromptService _prompts;
    private readonly ILogger<AspectRunnerService> _logger;

    /// <summary>
    /// CLI runner injection point. Tests substitute a deterministic stub
    /// keyed on the aspect name (passed as the first argument so the test
    /// can return a different reply per aspect without re-parsing the
    /// prompt). Production wires it onto <see cref="ICliOneShot"/> via the
    /// constructor; tests can still overwrite the property directly.
    /// </summary>
    public Func<string, string, string, string, TimeSpan, CancellationToken, Task<string>> CliRunner { get; set; }
        = DefaultRunCliAsync;

    private Func<string, string, string, string, string?, TimeSpan, string, CancellationToken, Task<string>>? _thinkingAwareCliRunner;

    /// <summary>
    /// Backoff before the single environmental retry of an aspect whose reviewing
    /// CLI call died with no verdict (AGT-2021). Defaults to the AGT-1944
    /// environmental backoff; tests override it with a zero delay so the retry
    /// path runs instantly.
    /// </summary>
    public Func<int, TimeSpan> VerdictRetryBackoff { get; set; } = PostProcessingOutcomeTaxonomy.RetryBackoff;

    private readonly AdHocUsageRecorder? _usage;
    private readonly CliOneShotRegistry? _oneShotRegistry;
    private readonly PipelineExecutionLog? _pipelineLog;
    private readonly FileGenerationIndex? _fileGenerationIndex;

    public AspectRunnerService(
        RuntimePromptService prompts,
        ILogger<AspectRunnerService> logger,
        AdHocUsageRecorder? usage = null,
        CliOneShotRegistry? oneShotRegistry = null,
        PipelineExecutionLog? pipelineLog = null,
        FileGenerationIndex? fileGenerationIndex = null)
    {
        _prompts = prompts;
        _logger = logger;
        _usage = usage;
        _oneShotRegistry = oneShotRegistry;
        _pipelineLog = pipelineLog;
        _fileGenerationIndex = fileGenerationIndex;

        // Wire production CLI invocation to the OneShot service so prompts
        // travel via stdin (the failure mode this fix exists to prevent
        // was multi-KB prompts passed as -p argv on Windows). Tests that
        // construct the service with no registry get DefaultRunCliAsync
        // and substitute their own stub via the CliRunner property.
        if (_oneShotRegistry != null)
        {
            _thinkingAwareCliRunner = RunViaOneShotAsync;
        }
    }

    private async Task<string> RunViaOneShotAsync(string aspectId, string cli, string model, string prompt, string? thinkingLevel, TimeSpan timeout, string jobFolderPath, CancellationToken ct)
    {
        var oneShot = _oneShotRegistry?.Get(cli);
        if (oneShot == null) return await DefaultRunCliAsync(aspectId, cli, model, prompt, timeout, ct);

        var result = await oneShot.RunAsync(new CliOneShotRequest(
            CliType: cli,
            Model: model,
            Prompt: prompt)
        {
            ThinkingLevel = thinkingLevel,
            Timeout = timeout,
            Source = AdHocUsageSources.ReviewDecision,
            RecordUsage = false, // RunAsync caller (this service) records via AdHocClaudeInvoker.Record below
            // Raw-prompt capture: the central dispatch decorator writes the
            // final prompt to .metadata/prompts.jsonl keyed on this step id.
            JobFolderPath = jobFolderPath,
            StepId = $"aspect-{aspectId}",
            TemplateRef = Catalogue.TryGetValue(aspectId, out var def) ? def.PromptTemplate : null,
        }, ct);

        if (!result.Ok)
        {
            _logger.LogWarning(
                "Aspect '{AspectId}' CLI call failed: exit={ExitCode} duration={Duration}ms error={Error}",
                aspectId, result.ExitCode, result.Duration.TotalMilliseconds, result.Error);
        }
        // Keep the legacy runner seam stable: its caller expects a Claude-style
        // result envelope so it can reuse ParseOrFallback and usage recording.
        return CliOneShotCompatibility.ToClaudeResultEnvelope(result, model);
    }

    /// <summary>
    /// Static catalogue of the aspects ADR-0025 ships in slice 1, mapping
    /// each id to the namespace used for its concerns tag. The orchestrator
    /// resolves which subset to run via the <c>ReviewDecisionOrchestrator:AspectRunners</c>
    /// configuration array; everything in that array must appear here.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, AspectDefinition> Catalogue =
        new Dictionary<string, AspectDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["requirement-fit"] = new AspectDefinition(
                Id: "requirement-fit",
                ConcernNamespace: "requirement",
                PromptTemplate: "review-aspect-requirement-fit.md",
                Title: "Requirement fit",
                FallbackSystem: "Does the change solve the task's acceptance criteria? Block clear goal misses, redundant already-done work, or half-finished placeholders."),
            ["code-quality"] = new AspectDefinition(
                Id: "code-quality",
                ConcernNamespace: "quality",
                PromptTemplate: "review-aspect-code-quality.md",
                Title: "Code quality",
                FallbackSystem: "Look at the diff and changed-file list. Are there regressions, broken types, redundant unused implementations, or half-finished/stubbed paths visible in the changed files?"),
            ["documentation-impact"] = new AspectDefinition(
                Id: "documentation-impact",
                ConcernNamespace: "docs",
                PromptTemplate: "review-aspect-documentation-impact.md",
                Title: "Documentation impact",
                FallbackSystem: "Does the change require an update to AGENTS.md / ROADMAP / ADRs / cli-skills / docs/start/README.md? Are those updated?"),
            ["tests-and-evidence"] = new AspectDefinition(
                Id: "tests-and-evidence",
                ConcernNamespace: "quality",
                PromptTemplate: "review-aspect-tests-and-evidence.md",
                Title: "Tests and evidence",
                FallbackSystem: "Did the agent ship tests that fail before the change and pass after? Is screenshot/log evidence present where AGENTS.md requires it?")
        };

    /// <summary>
    /// Run every configured aspect against the job, write the aspect MDs
    /// into the job folder, and return the per-aspect verdicts so the
    /// caller can decide reissue / accept / accept-with-concerns.
    ///
    /// <para>
    /// Aspects run in parallel: each is an independent CLI call with no
    /// shared state, so the wall-clock cost drops from sum-of-aspects to
    /// max-of-aspects. A <see cref="SemaphoreSlim"/> caps the fan-out so
    /// a misconfigured catalogue cannot exhaust the CLI quota in one
    /// tick. When a <see cref="PipelineExecutionLog"/> is wired, each
    /// step's start / outcome / tokens are recorded into
    /// <c>pipeline-execution.json</c> in the job folder for the Overview
    /// pipeline view to consume.
    /// </para>
    ///
    /// <para>
    /// Verdicts in the returned report are sorted to match the input
    /// <paramref name="aspects"/> order so downstream consumers see
    /// deterministic ordering regardless of which task finished first.
    /// </para>
    /// </summary>
    public async Task<AspectRunReport> RunAsync(
        AspectRunInputs inputs,
        IReadOnlyList<string> aspects,
        string cliBinary,
        string model,
        TimeSpan perAspectTimeout,
        CancellationToken ct,
        Func<string, string>? modelForAspect = null,
        Func<string, string?>? thinkingLevelForAspect = null,
        Func<string, string?>? promptForAspect = null,
        Func<string, string?>? cliForAspect = null)
    {
        var now = DateTime.UtcNow;

        // Resolve definitions up-front; unknown ids are skipped silently
        // so the input list can carry config typos without crashing the
        // run (existing behaviour, covered by AspectRunnerTests).
        var resolved = new List<(int Index, string AspectId, AspectDefinition Def)>();
        for (var i = 0; i < aspects.Count; i++)
        {
            var aspectId = aspects[i];
            if (!Catalogue.TryGetValue(aspectId, out var def))
            {
                _logger.LogWarning("Unknown aspect '{AspectId}'; skipping", aspectId);
                continue;
            }
            resolved.Add((i, aspectId, def));
        }
        if (resolved.Count == 0) return AspectRunReport.From(Array.Empty<AspectVerdict>());

        // A replacement backend resumes the same open pipeline attempt. Reuse
        // only verdict artifacts fenced by a terminal step in that attempt;
        // an artifact left by an older attempt is never sufficient on its own.
        var checkpointed = new List<(int Index, AspectVerdict Verdict)>();
        var pending = new List<(int Index, string AspectId, AspectDefinition Def)>();
        foreach (var entry in resolved)
        {
            var verdict = TryReadRestartCheckpoint(inputs.JobFolderPath, entry.Def);
            if (verdict is null) pending.Add(entry);
            else checkpointed.Add((entry.Index, verdict));
        }

        if (pending.Count == 0)
        {
            return AspectRunReport.From(
                checkpointed.OrderBy(item => item.Index).Select(item => item.Verdict).ToList());
        }

        // Bounded parallel fan-out. Default cap = 4 (today's catalogue
        // ships exactly four aspects); a future catalogue with more
        // aspects gets a real ceiling instead of unbounded WhenAll.
        var maxParallel = Math.Min(pending.Count, 4);
        using var gate = new SemaphoreSlim(maxParallel, maxParallel);

        var tasks = pending
            .Select(entry =>
            {
                // Per-step model routing: the orchestrator hands us a
                // resolver keyed on the aspect id (aspect-{id}); a null
                // resolver or a null reply means "use the run-wide model".
                var stepModel = modelForAspect?.Invoke(entry.Def.Id);
                if (string.IsNullOrWhiteSpace(stepModel)) stepModel = model;
                var stepThinkingLevel = thinkingLevelForAspect?.Invoke(entry.Def.Id);
                var stepPrompt = promptForAspect?.Invoke(entry.Def.Id);
                var stepCli = cliForAspect?.Invoke(entry.Def.Id);
                if (string.IsNullOrWhiteSpace(stepCli)) stepCli = cliBinary;
                return RunOneAspectAsync(entry.Index, entry.Def, inputs, stepCli, stepModel, stepThinkingLevel,
                    stepPrompt, perAspectTimeout, gate, now, ct);
            })
            .ToArray();

        var perIndex = await Task.WhenAll(tasks);

        // Re-sort to match the requested aspect order; WhenAll's array is
        // already index-aligned (Select preserved order), but a future
        // refactor to per-completion writing would still get deterministic
        // output via the explicit index sort.
        var verdicts = checkpointed
            .Concat(perIndex)
            .OrderBy(r => r.Index)
            .Select(r => r.Verdict)
            .ToList();

        return AspectRunReport.From(verdicts);
    }

    private AspectVerdict? TryReadRestartCheckpoint(
        string jobFolderPath,
        AspectDefinition definition)
    {
        var step = _pipelineLog?.ReadRestartCheckpoint(
            jobFolderPath,
            $"aspect-{definition.Id}");
        if (step is null) return null;

        try
        {
            var path = Path.Combine(jobFolderPath, $"aspect-{definition.Id}.json");
            if (!File.Exists(path)) return null;
            var document = AspectVerdictParsing.TryParseJson(File.ReadAllText(path));
            if (document is null
                || !string.Equals(document.Aspect, definition.Id, StringComparison.OrdinalIgnoreCase))
                return null;

            var status = document.Status.Trim().ToLowerInvariant() switch
            {
                "pass" => AspectStatus.Pass,
                "concern" or "concerns" => AspectStatus.Concerns,
                "block" or "blocked" => AspectStatus.Block,
                _ => (AspectStatus?)null,
            };
            if (status is null) return null;

            return new AspectVerdict(
                definition.Id,
                status.Value,
                document.Summary,
                document.Details,
                document.Tag)
            {
                IsInfraFailure = string.Equals(step.Verdict, "environmental", StringComparison.OrdinalIgnoreCase),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                ex,
                "Aspect restart checkpoint could not be read for {AspectId} in {JobFolder}",
                definition.Id,
                jobFolderPath);
            return null;
        }
    }

    private async Task<(int Index, AspectVerdict Verdict)> RunOneAspectAsync(
        int index,
        AspectDefinition def,
        AspectRunInputs inputs,
        string cliBinary,
        string model,
        string? thinkingLevel,
        string? promptOverride,
        TimeSpan perAspectTimeout,
        SemaphoreSlim gate,
        DateTime now,
        CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var pipelineStepId = $"aspect-{def.Id}";
            var startedAt = DateTime.UtcNow;
            _pipelineLog?.RecordStep(inputs.JobFolderPath, new PipelineStepExecution
            {
                StepId = pipelineStepId,
                Kind = StepKind.Aspect,
                Model = model,
                Status = PipelineStepStatus.Running,
                StartedAt = startedAt,
            });

            var prompt = BuildAspectPrompt(
                def,
                inputs,
                model,
                promptOverride,
                pipelineStepId);
            string response;
            OrchestratorTokenUsage? callUsage = null;
            long durationMs = 0;
            var ok = true;
            AspectVerdict verdict;

            // Environmental retry-once (AGT-2021 / AGT-1944): a missing / corrupt /
            // unparseable verdict caused by the reviewing CLI dying (the backend
            // cut that killed the aspect runner mid-run) is an INFRASTRUCTURE
            // fault, not the agent's work. Re-run the aspect exactly once with the
            // environmental backoff; only when the retry again yields no output do
            // we mark it an InfraCrash. A CLI that DID reply (even garbage) is not
            // an infra fault - it keeps the existing review:unparseable concern.
            var envRetries = 0;
            while (true)
            {
                (response, ok, callUsage, durationMs) =
                    await InvokeAspectCliAsync(def, inputs, cliBinary, model, thinkingLevel, prompt, perAspectTimeout, ct);

                var parsed = AspectVerdictParsing.ParseVerdict(response);
                var infraNoVerdict = parsed == null && (!ok || string.IsNullOrWhiteSpace(response));
                if (!infraNoVerdict)
                {
                    // Got a real verdict, or a non-empty reply we can turn into a
                    // deterministic review:unparseable concern (existing behaviour).
                    verdict = BuildVerdict(def, response);
                    break;
                }

                var decision = PostProcessingOutcomeTaxonomy.DecidePostStepVerdictRetry(envRetries);
                if (decision.Action != EnvironmentalRetryAction.RetryWithBackoff)
                {
                    // Retry budget spent: the reviewer died twice. Record it as an
                    // environmental InfraCrash, never the card's unfinished work.
                    verdict = BuildInfraFailureVerdict(def, envRetries);
                    _logger.LogWarning(
                        "Aspect runner '{AspectId}' produced no verdict for {Project}/{JobId} even after {Retries} environmental retry; recording InfraCrash flagged environmental (AGT-2021).",
                        def.Id, inputs.Project, inputs.JobId, envRetries);
                    break;
                }

                envRetries = decision.Attempt;
                var backoff = VerdictRetryBackoff(envRetries);
                _logger.LogWarning(
                    "Aspect runner '{AspectId}' produced no verdict for {Project}/{JobId} (environmental infra fault, ok={Ok}); {Reason} (backoff {Backoff})",
                    def.Id, inputs.Project, inputs.JobId, ok, decision.Reason, backoff);
                if (backoff > TimeSpan.Zero)
                {
                    try { await Task.Delay(backoff, ct); }
                    catch (OperationCanceledException)
                    {
                        verdict = BuildInfraFailureVerdict(def, envRetries);
                        break;
                    }
                }
            }

            try
            {
                var resolvedModel = callUsage?.Model ?? model;
                var endedAt = DateTime.UtcNow;
                var genMeta = new FileGenerationMeta
                {
                    File = string.Empty, // set per artefact below
                    Kind = "aspect",
                    Model = resolvedModel,
                    Cli = cliBinary,
                    TokensIn = callUsage?.InputTokens ?? 0,
                    TokensOut = callUsage?.OutputTokens ?? 0,
                    TokensTotal = (callUsage?.InputTokens ?? 0)
                        + (callUsage?.OutputTokens ?? 0)
                        + (callUsage?.CacheReadTokens ?? 0)
                        + (callUsage?.CacheCreationTokens ?? 0),
                    StartedAt = startedAt,
                    EndedAt = endedAt,
                    DurationMs = durationMs > 0 ? durationMs : (long)(endedAt - startedAt).TotalMilliseconds,
                    StepId = pipelineStepId,
                };

                // Human-readable markdown twin: unchanged, so every existing
                // reader (AspectConcernReader, orchestrator tag routing, the
                // legacy Files-tab markdown path) keeps working untouched.
                var report = AspectVerdictParsing.RenderReport(verdict, now);
                var mdName = $"aspect-{def.Id}.md";
                await File.WriteAllTextAsync(Path.Combine(inputs.JobFolderPath, mdName), report, ct);
                _fileGenerationIndex?.Upsert(inputs.JobFolderPath, genMeta with { File = mdName });

                // Structured JSON source of truth (one source, two renderings —
                // concept doc §5). Strictly additive: the Files tab prefers it
                // and suppresses the markdown twin from the list, but the twin
                // stays on disk for backend readers and older UIs.
                var jsonBody = AspectVerdictParsing.RenderJson(verdict, resolvedModel, now);
                var jsonName = $"aspect-{def.Id}.json";
                await File.WriteAllTextAsync(Path.Combine(inputs.JobFolderPath, jsonName), jsonBody, ct);
                _fileGenerationIndex?.Upsert(inputs.JobFolderPath, genMeta with { File = jsonName });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Aspect runner '{AspectId}' failed to write aspect artefacts for {JobId}",
                    def.Id, inputs.JobId);
            }

            var completedAt = DateTime.UtcNow;
            _pipelineLog?.RecordStep(inputs.JobFolderPath, new PipelineStepExecution
            {
                StepId = pipelineStepId,
                Kind = StepKind.Aspect,
                Model = callUsage?.Model ?? model,
                // An infra crash (dead reviewer, no verdict after the retry) is a
                // Failed step flagged environmental so a reviewer never reads it as
                // a failed change; a healthy run is Passed, a soft CLI error is
                // Failed. See BuildInfraFailureVerdict.
                Status = verdict.IsInfraFailure || !ok ? PipelineStepStatus.Failed : PipelineStepStatus.Passed,
                StartedAt = startedAt,
                CompletedAt = completedAt,
                DurationMs = durationMs > 0 ? durationMs : (long)(completedAt - startedAt).TotalMilliseconds,
                InputTokens = callUsage?.InputTokens ?? 0,
                OutputTokens = callUsage?.OutputTokens ?? 0,
                CacheReadTokens = callUsage?.CacheReadTokens ?? 0,
                CacheCreationTokens = callUsage?.CacheCreationTokens ?? 0,
                Verdict = verdict.IsInfraFailure ? "environmental" : AspectVerdictParsing.StatusToken(verdict.Status),
                Reason = verdict.IsInfraFailure
                    ? "aspect-runner-infra-crash"
                    : ok ? null : "aspect-runner-exception",
            });

            return (index, verdict);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Compose the per-aspect prompt by rendering the aspect's runtime
    /// template with the standard variables. Falls back to a minimal
    /// inline prompt if the template is missing so the pipeline still
    /// produces a verdict (the missing template surfaces as a build /
    /// startup smell, not as a quiet "no-op" run).
    /// </summary>
    internal string BuildAspectPrompt(
        AspectDefinition def,
        AspectRunInputs inputs,
        string model,
        string? promptOverride = null,
        string? pipelineStepId = null)
    {
        if (!string.IsNullOrWhiteSpace(promptOverride))
        {
            return _prompts.UseProjectOverride(
                def.PromptTemplate,
                promptOverride,
                new PromptCallContext(
                    inputs.Project,
                    pipelineStepId ?? $"aspect-{def.Id}",
                    model));
        }
        var values = new Dictionary<string, string?>
        {
            ["project"] = inputs.Project,
            ["job_id"] = inputs.JobId,
            ["job_title"] = inputs.JobTitle,
            ["aspect_title"] = def.Title,
            ["aspect_namespace"] = def.ConcernNamespace,
            ["task_body"] = inputs.TaskBody,
            ["acceptance_scope"] = string.IsNullOrWhiteSpace(inputs.AcceptanceScope)
                ? RequirementAcceptanceScope.Describe(null, inputs.TaskBody)
                : inputs.AcceptanceScope,
            ["recent_log"] = inputs.RecentLog,
            ["diff_summary"] = inputs.DiffSummary,
            ["status_summary"] = inputs.StatusSummary,
            ["results_inventory"] = string.IsNullOrWhiteSpace(inputs.ResultsInventory)
                ? "No results/ inventory available."
                : inputs.ResultsInventory,
            ["card_mode"] = string.IsNullOrWhiteSpace(inputs.CardMode)
                ? ReviewCardMode.Describe(null)
                : inputs.CardMode
        };
        try
        {
            return _prompts.Render(
                def.PromptTemplate,
                values,
                new PromptCallContext(inputs.Project, $"aspect-{def.Id}", model));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Aspect template '{Template}' not rendered; falling back to inline prompt",
                def.PromptTemplate);
            return BuildInlineFallbackPrompt(def, inputs);
        }
    }

    private static string BuildInlineFallbackPrompt(AspectDefinition def, AspectRunInputs inputs)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Aspect review: {def.Title}");
        sb.AppendLine();
        sb.AppendLine(def.FallbackSystem);
        sb.AppendLine();
        sb.AppendLine($"## Project / Job: {inputs.Project} / {inputs.JobId} - {inputs.JobTitle}");
        sb.AppendLine();
        sb.AppendLine(string.IsNullOrWhiteSpace(inputs.CardMode) ? ReviewCardMode.Describe(null) : inputs.CardMode);
        sb.AppendLine();
        sb.AppendLine("## Delivery acceptance scope");
        sb.AppendLine(string.IsNullOrWhiteSpace(inputs.AcceptanceScope)
            ? RequirementAcceptanceScope.Describe(null, inputs.TaskBody)
            : inputs.AcceptanceScope);
        sb.AppendLine();
        sb.AppendLine("## Task body");
        sb.AppendLine("```");
        sb.AppendLine(inputs.TaskBody);
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## Recent log");
        sb.AppendLine("```");
        sb.AppendLine(inputs.RecentLog);
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## Diff summary (task branch vs base)");
        sb.AppendLine("```");
        sb.AppendLine(inputs.DiffSummary);
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## results/ folder inventory");
        sb.AppendLine("```");
        sb.AppendLine(string.IsNullOrWhiteSpace(inputs.ResultsInventory) ? "No results/ inventory available." : inputs.ResultsInventory);
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("Only treat deliverables as missing when the diff summary shows no branch changes, the results/ inventory is empty, AND no external deliverable (e.g. a docs/ commit) is documented.");
        sb.AppendLine();
        sb.AppendLine("Reply with a short paragraph or two (under 200 words) plus EXACTLY one verdict sentinel on its own line.");
        sb.AppendLine();
        sb.AppendLine("Required sentinel format (literal characters — do NOT wrap in code fences, blockquotes, or quotes):");
        sb.AppendLine();
        sb.AppendLine("[[ASPECT_VERDICT: status=<pass|concerns|block>; summary=<one short sentence, no semicolons or brackets>]]");
        sb.AppendLine();
        sb.AppendLine("Example of a complete reply:");
        sb.AppendLine();
        sb.AppendLine("> The new file is self-contained, uses inline styles, and meets the");
        sb.AppendLine("> stated scope. No tests requested.");
        sb.AppendLine("> ");
        sb.AppendLine("> [[ASPECT_VERDICT: status=pass; summary=Self-contained HTML, scope met, no extra dependencies.]]");
        sb.AppendLine("> [[TASK_DONE]]");
        sb.AppendLine();
        sb.AppendLine("Status values:");
        sb.AppendLine("  pass     — aspect is fine, no follow-up needed");
        sb.AppendLine("  concerns — aspect has issues a human should look at; not blocking");
        sb.AppendLine("  block    — aspect has a defect that must be fixed before sign-off");
        sb.AppendLine();
        sb.AppendLine("After the sentinel, end with [[TASK_DONE]] on its own line.");
        return sb.ToString();
    }

    /// <summary>
    /// Invoke the reviewing CLI once for one aspect, parse the wrapper, and
    /// record ad-hoc usage. Returns the parsed text plus <c>ok=false</c> when the
    /// call threw (the caller treats an exception or an empty reply as an
    /// environmental infra fault worth one retry). Kept as its own method so the
    /// AGT-2021 retry loop can call it repeatedly without duplicating the
    /// timing / usage bookkeeping.
    /// </summary>
    private async Task<(string Response, bool Ok, OrchestratorTokenUsage? Usage, long DurationMs)> InvokeAspectCliAsync(
        AspectDefinition def,
        AspectRunInputs inputs,
        string cliBinary,
        string model,
        string? thinkingLevel,
        string prompt,
        TimeSpan perAspectTimeout,
        CancellationToken ct)
    {
        try
        {
            var sw = AdHocClaudeInvoker.StartTiming();
            var rawResponse = _thinkingAwareCliRunner is null
                ? await CliRunner(def.Id, cliBinary, model, prompt, perAspectTimeout, ct)
                : await _thinkingAwareCliRunner(def.Id, cliBinary, model, prompt, thinkingLevel, perAspectTimeout, inputs.JobFolderPath, ct);
            sw.Stop();
            var durationMs = sw.ElapsedMilliseconds;
            var parsed = AdHocClaudeInvoker.ParseOrFallback(rawResponse, model);
            AdHocClaudeInvoker.Record(_usage, AdHocUsageSources.ReviewDecision, model, parsed.Usage,
                durationMs, ok: true, project: inputs.Project, jobId: inputs.JobId);
            return (parsed.Text, true, parsed.Usage, durationMs);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Aspect runner '{AspectId}' invocation failed for {Project}/{JobId}",
                def.Id, inputs.Project, inputs.JobId);
            // Fail-loud-but-don't-block: on infrastructure failure the caller
            // retries once, then records an InfraCrash rather than durchwinken.
            return (string.Empty, false, null, 0);
        }
    }

    /// <summary>
    /// Build the deterministic verdict for an aspect whose reviewing CLI produced
    /// no output even after the single environmental retry (AGT-2021). This is an
    /// infrastructure crash, not the card's work: <see cref="AspectVerdict.IsInfraFailure"/>
    /// is set so the orchestrator records it as an <c>InfraCrash</c> flagged
    /// <c>environmental</c> and burns no reissue budget. No <c>review:unparseable</c>
    /// concern tag is hung - that tag means "the model replied but broke the
    /// format", which is a different (non-infra) signal.
    /// </summary>
    private static AspectVerdict BuildInfraFailureVerdict(AspectDefinition def, int envRetries)
    {
        var retried = envRetries > 0
            ? $" even after {envRetries} environmental retr{(envRetries == 1 ? "y" : "ies")}"
            : string.Empty;
        return new AspectVerdict(
            Aspect: def.Id,
            Status: AspectStatus.Concerns,
            Summary: $"Aspect runner produced no verdict{retried}; environmental infra crash (AGT-2021).",
            Body: BuildBody(string.Empty,
                $"The aspect reviewing CLI produced no output{retried}. This is an infrastructure fault (a dead reviewer), classified environmental - not the card's unfinished work."),
            ConcernTagId: null)
        {
            IsInfraFailure = true,
        };
    }

    private static AspectVerdict BuildVerdict(AspectDefinition def, string response)
    {
        var parsed = AspectVerdictParsing.ParseVerdict(response);
        if (parsed == null)
        {
            // No silent durchwinken: an unparseable reply still gets a
            // deterministic Concerns verdict so the user sees a chip and
            // can drill in. But tag it as `review:unparseable` (NOT
            // `{namespace}:concerns`) so the operator can distinguish
            // "model has a real concern" from "model didn't follow the
            // verdict format" — these are very different signals when
            // sorting / scanning the human-review lane. See F1 in the
            // 2026-05-21 probe findings.
            return new AspectVerdict(
                Aspect: def.Id,
                Status: AspectStatus.Concerns,
                Summary: "Aspect runner produced no parseable verdict.",
                Body: BuildBody(response, "No `[[ASPECT_VERDICT]]` sentinel was found in the model reply."),
                ConcernTagId: "review:unparseable");
        }
        var (status, summary) = parsed.Value;
        return new AspectVerdict(
            Aspect: def.Id,
            Status: status,
            Summary: summary,
            Body: BuildBody(response, summary),
            ConcernTagId: status == AspectStatus.Pass ? null : $"{def.ConcernNamespace}:concerns");
    }

    private static string BuildBody(string response, string fallback)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return $"_{fallback}_\n";
        }
        return $"## Model reply\n\n```\n{response.Trim()}\n```\n";
    }

    /// <summary>
    /// Legacy fallback CLI runner kept for backward compatibility with the
    /// few tests that construct <see cref="AspectRunnerService"/> without
    /// a <see cref="CliOneShotRegistry"/>. Production paths go through
    /// <see cref="RunViaOneShotAsync"/>. Stdin-piped to match the canonical
    /// pattern; we removed the previous <c>-p &lt;prompt&gt;</c> argv path
    /// that caused the 2026-05-11 empty-reply incident.
    /// </summary>
    private static async Task<string> DefaultRunCliAsync(
        string aspectId, string cli, string model, string prompt, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = cli,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("json");
        psi.ArgumentList.Add("--model");
        psi.ArgumentList.Add(model);
        psi.ArgumentList.Add("--dangerously-skip-permissions");

        using var p = System.Diagnostics.Process.Start(psi);
        if (p == null) return string.Empty;
        try
        {
            await p.StandardInput.WriteAsync(prompt.AsMemory(), ct);
            p.StandardInput.Close();
        }
        catch (Exception __ex) { SilentCatch.Note(__ex, "AspectRunnerService: CLI may have closed stdin already"); /* CLI may have closed stdin already */ }

        var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await p.WaitForExitAsync(cts.Token);
            return await stdoutTask;
        }
        catch (OperationCanceledException)
        {
            AgentStudio.Diagnostics.CliKillAudit.Trace(p, "AspectRunnerService:512 (entireProcessTree)");
            try { p.Kill(true); } catch (Exception __ex) { SilentCatch.Note(__ex, "AspectRunnerService:513"); }
            return string.Empty;
        }
    }
}

/// <summary>
/// Static metadata for one aspect: its id, the prompt template file
/// name, the namespace it uses for its concerns tag, and a short
/// fallback prompt body used when the template file is missing.
/// </summary>
public sealed record AspectDefinition(
    string Id,
    string ConcernNamespace,
    string PromptTemplate,
    string Title,
    string FallbackSystem);

/// <summary>
/// Inputs needed by the aspect runner: per-job context that the
/// orchestrator pre-loads once and reuses across aspects so the cost
/// stays bounded.
/// </summary>
public sealed record AspectRunInputs(
    string Project,
    string JobId,
    string JobTitle,
    string JobFolderPath,
    string TaskBody,
    string RecentLog,
    string DiffSummary,
    string StatusSummary)
{
    /// <summary>
    /// Inventory of the job's <c>results/</c> folder (file list + short
    /// excerpts). Completes the evidence source so a reviewer never reads an
    /// empty git diff as "deliverables missing" when the deliverable is a
    /// results/ artefact (AGT-2022). Defaults to empty so existing callers /
    /// tests that build inputs positionally keep compiling.
    /// </summary>
    public string ResultsInventory { get; init; } = string.Empty;

    /// <summary>
    /// One-line framing of the card's execution mode (coding vs read-only
    /// planning / research) so an empty code diff on a concept / doc / research
    /// card is read as legitimate rather than as missing work.
    /// </summary>
    public string CardMode { get; init; } = string.Empty;

    /// <summary>
    /// Rendered card-owned delivery boundary. Requirement-fit treats this as
    /// authoritative over broader Dossier or wishlist text in the task body.
    /// </summary>
    public string AcceptanceScope { get; init; } = string.Empty;
}

/// <summary>
/// Aggregated verdict for one job's full multi-aspect pass: the
/// individual <see cref="AspectVerdict"/>s plus a derived overall
/// outcome ready for the orchestrator to route on.
/// </summary>
public sealed record AspectRunReport(
    IReadOnlyList<AspectVerdict> Verdicts,
    AspectStatus Overall,
    IReadOnlyList<string> ConcernTagIds,
    string FollowUpSummary)
{
    /// <summary>
    /// True when at least one aspect infra-crashed even after its single
    /// environmental retry (AGT-2021). The orchestrator short-circuits on this
    /// BEFORE the accept / reissue routing: a dead reviewer is an infra fault, so
    /// the card must not be accepted, reissued, or counted as unfinished work.
    /// </summary>
    public bool HasInfraFailure => Verdicts.Any(v => v.IsInfraFailure);

    /// <summary>The aspects that produced no verdict even after the retry.</summary>
    public IReadOnlyList<AspectVerdict> InfraFailures =>
        Verdicts.Where(v => v.IsInfraFailure).ToList();

    public static AspectRunReport From(IReadOnlyList<AspectVerdict> verdicts)
    {
        var overall = AspectStatus.Pass;
        foreach (var v in verdicts)
        {
            if (v.Status == AspectStatus.Block) { overall = AspectStatus.Block; break; }
            if (v.Status == AspectStatus.Concerns) overall = AspectStatus.Concerns;
        }

        // Concern tag set: union across all non-pass verdicts. We
        // de-duplicate so a job that has both `code-quality:concerns`
        // and `tests-and-evidence:concerns` (both share the `quality`
        // namespace) ends up with one `quality:concerns` chip rather
        // than two.
        var tags = verdicts
            .Where(v => v.Status != AspectStatus.Pass && v.ConcernTagId is not null)
            .Select(v => v.ConcernTagId!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sb = new StringBuilder();
        foreach (var v in verdicts.Where(v => v.Status != AspectStatus.Pass))
        {
            sb.AppendLine($"- **{v.Aspect}** [{AspectVerdictParsing.StatusToken(v.Status)}]: {v.Summary}");
        }
        var followUp = sb.Length == 0 ? string.Empty : sb.ToString().TrimEnd();

        return new AspectRunReport(verdicts, overall, tags, followUp);
    }
}

/// <summary>
/// Helpers that the orchestrator uses to update the job's tags array
/// without going through <c>TaskMutationService.NormalizeTagId</c>:
/// concern tags use a different grammar (<c>{namespace}:concerns</c>)
/// that the standard tag normaliser would strip the colon from.
/// </summary>
internal static class ConcernTagWriter
{
    /// <summary>
    /// Read the tags array from the job's <c>task.json</c>, merge in the
    /// supplied concern tag ids (deduped, case-insensitive), and write
    /// the result back. Used by the orchestrator after a multi-aspect
    /// run produces concern verdicts.
    /// </summary>
    public static void MergeConcernTags(string jobFolderPath, IReadOnlyList<string> concernTagIds, ILogger logger)
    {
        if (concernTagIds.Count == 0) return;
        var jobJsonPath = Path.Combine(jobFolderPath, "task.json");
        if (!File.Exists(jobJsonPath)) return;

        try
        {
            var json = File.ReadAllText(jobJsonPath);
            var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, TaskJsonFile.ReadOpts)
                      ?? new Dictionary<string, JsonElement>();
            var existing = new List<string>();
            if (doc.TryGetValue("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in tagsEl.EnumerateArray())
                {
                    if (t.ValueKind == JsonValueKind.String)
                    {
                        var s = t.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) existing.Add(s);
                    }
                }
            }
            var merged = existing
                .Concat(concernTagIds)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            TaskJsonFile.UpdateField(jobFolderPath, "tags", merged, logger);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "ConcernTagWriter: failed to merge concern tags into {TaskFolder}",
                jobFolderPath);
        }
    }

    /// <summary>
    /// Authoritatively reconcile the job's aspect-concern tags against the
    /// supplied current set. Unlike <see cref="MergeConcernTags"/> this is NOT
    /// additive: it keeps every non-concern tag untouched, then sets the
    /// aspect-concern tags (<c>{namespace}:concerns</c> / <c>review:unparseable</c>)
    /// to exactly <paramref name="currentConcernTagIds"/> - which may be empty.
    /// That is what strips stale concern chips an earlier auto-review pass left
    /// behind once a later pass accepts cleanly (or with fewer concerns). Runs
    /// even when the current set is empty - removing stale tags is the whole
    /// point. A no-op when nothing changes, to avoid churning the folder mtime.
    /// </summary>
    public static void ReconcileConcernTags(string jobFolderPath, IReadOnlyList<string> currentConcernTagIds, ILogger logger)
    {
        var jobJsonPath = Path.Combine(jobFolderPath, "task.json");
        if (!File.Exists(jobJsonPath)) return;

        try
        {
            var json = File.ReadAllText(jobJsonPath);
            var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, TaskJsonFile.ReadOpts)
                      ?? new Dictionary<string, JsonElement>();
            var existing = new List<string>();
            if (doc.TryGetValue("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in tagsEl.EnumerateArray())
                {
                    if (t.ValueKind == JsonValueKind.String)
                    {
                        var s = t.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) existing.Add(s!);
                    }
                }
            }

            // Keep non-concern tags in place; replace the concern set with the
            // current one (deduped, case-insensitive).
            var reconciled = existing
                .Where(t => !TagDriftRule.IsAspectConcernTag(t))
                .Concat(currentConcernTagIds.Where(s => !string.IsNullOrWhiteSpace(s)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var before = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
            var after = new HashSet<string>(reconciled, StringComparer.OrdinalIgnoreCase);
            if (before.SetEquals(after)) return;

            TaskJsonFile.UpdateField(jobFolderPath, "tags", reconciled, logger);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "ConcernTagWriter: failed to reconcile concern tags into {TaskFolder}",
                jobFolderPath);
        }
    }

    /// <summary>
    /// Prefix shared by the four quality-grade tags
    /// (<c>code-review:grade-a</c> … <c>code-review:grade-d</c>) the
    /// automatic code-review step hangs on a card. Used to reconcile the
    /// grade so a re-graded task carries exactly one grade tag.
    /// </summary>
    public const string CodeReviewGradeTagPrefix = "code-review:grade-";

    /// <summary>
    /// Set the card's single quality-grade tag to <paramref name="gradeTagId"/>,
    /// dropping any other <c>code-review:grade-*</c> tag a prior run left behind.
    /// Every non-grade tag is untouched. A grade is carried on every pipelined
    /// task, so unlike the concern tags this is authoritative for exactly one
    /// value. No-op when nothing changes, to avoid churning the folder mtime.
    /// </summary>
    public static void ReplaceCodeReviewGradeTag(string jobFolderPath, string gradeTagId, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(gradeTagId)) return;
        ReconcileCodeReviewGradeTag(jobFolderPath, gradeTagId, logger);
    }

    /// <summary>
    /// Remove every stale quality-grade tag while preserving all unrelated
    /// tags. Used when a new grade dispatch failed before producing an
    /// authoritative grade, so the card cannot keep advertising an older A-D
    /// result beside a failed pipeline row.
    /// </summary>
    public static void ClearCodeReviewGradeTags(string jobFolderPath, ILogger logger)
        => ReconcileCodeReviewGradeTag(jobFolderPath, gradeTagId: null, logger);

    private static void ReconcileCodeReviewGradeTag(
        string jobFolderPath,
        string? gradeTagId,
        ILogger logger)
    {
        var jobJsonPath = Path.Combine(jobFolderPath, "task.json");
        if (!File.Exists(jobJsonPath)) return;

        try
        {
            var json = File.ReadAllText(jobJsonPath);
            var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, TaskJsonFile.ReadOpts)
                      ?? new Dictionary<string, JsonElement>();
            var existing = new List<string>();
            if (doc.TryGetValue("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in tagsEl.EnumerateArray())
                {
                    if (t.ValueKind == JsonValueKind.String)
                    {
                        var s = t.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) existing.Add(s!);
                    }
                }
            }

            var reconciled = existing
                .Where(t => !t.StartsWith(CodeReviewGradeTagPrefix, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (!string.IsNullOrWhiteSpace(gradeTagId))
            {
                reconciled.Add(gradeTagId);
            }

            var before = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
            var after = new HashSet<string>(reconciled, StringComparer.OrdinalIgnoreCase);
            if (before.SetEquals(after)) return;

            TaskJsonFile.UpdateField(jobFolderPath, "tags", reconciled, logger);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "ConcernTagWriter: failed to reconcile code-review grade tag in {TaskFolder}",
                jobFolderPath);
        }
    }
}
