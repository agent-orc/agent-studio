using System.Diagnostics;

namespace AgentStudio.Review;

/// <summary>
/// User-triggered code review step. Sits next to <see cref="AspectRunnerService"/>
/// but is a separate, single-shot review that the user explicitly plans
/// against a job: pick a model (default from configuration), run one
/// review pass against the task's full change set (the aggregate diff of
/// every commit the task owns; an explicit commit pins a single one),
/// write a fresh
/// <c>code-review-{utc-ts}.md</c> into the job folder, and merge a
/// <c>code-review:&lt;verdict&gt;</c> tag onto the job so the card shows
/// the outcome. The aspect-runner pipeline that runs in 4-auto-review is
/// deliberately not reused here so the two surfaces stay independent:
/// auto-review is policy-driven and aggregates four narrow aspects;
/// the code-review step is a user-invoked single review against a
/// chosen model.
///
/// <para>
/// Lane state is never touched. Verdicts surface as tags only, so the
/// user keeps the final say on what happens next (matches AGENTS.md
/// "auto-review never moves directly to 6-completed"). Re-running
/// produces a new MD with a fresh timestamp; nothing overwrites.
/// </para>
/// </summary>
public sealed class CodeReviewStepService
{
    private readonly RuntimePromptService _prompts;
    private readonly ILogger<CodeReviewStepService> _logger;
    private readonly AdHocUsageRecorder? _usage;
    private readonly CliOneShotRegistry? _oneShotRegistry;
    private readonly FileGenerationIndex? _fileGenerationIndex;

    /// <summary>
    /// CLI invocation seam. Production wires it through <see cref="ICliOneShot"/>
    /// when a registry is present. Tests substitute a deterministic stub
    /// keyed on the model so they can verify the per-model dispatch
    /// without spinning a subprocess.
    /// </summary>
    public Func<string, string, string, TimeSpan, CancellationToken, Task<string>> CliRunner { get; set; }
        = DefaultRunCliAsync;
    private Func<string, string, string, string?, TimeSpan, string, string, string?, CancellationToken, Task<string>>? _thinkingAwareCliRunner;

    public CodeReviewStepService(
        RuntimePromptService prompts,
        ILogger<CodeReviewStepService> logger,
        AdHocUsageRecorder? usage = null,
        CliOneShotRegistry? oneShotRegistry = null,
        FileGenerationIndex? fileGenerationIndex = null)
    {
        _prompts = prompts;
        _logger = logger;
        _usage = usage;
        _oneShotRegistry = oneShotRegistry;
        _fileGenerationIndex = fileGenerationIndex;

        if (_oneShotRegistry != null)
        {
            _thinkingAwareCliRunner = RunViaOneShotAsync;
        }
    }

    /// <summary>Public for testability: runtime prompt template name (verdict mode).</summary>
    public const string PromptTemplate = "code-review-step.md";

    /// <summary>Public for testability: runtime prompt template name (grade mode).</summary>
    public const string GradePromptTemplate = "code-review-grade.md";

    /// <summary>Public for testability: ad-hoc usage source tag.</summary>
    public const string UsageSource = "code-review-step";

    /// <summary>
    /// Run one code-review pass and persist its evidence. The method is
    /// fail-loud-but-don't-block, mirroring <see cref="AspectRunnerService"/>:
    /// CLI failures and unparseable replies both produce a deterministic
    /// Concerns verdict so the user always sees a result on the card.
    /// </summary>
    public async Task<CodeReviewStepReport> RunAsync(CodeReviewStepRequest request, CancellationToken ct)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        if (string.IsNullOrWhiteSpace(request.JobFolderPath))
            throw new ArgumentException("JobFolderPath is required", nameof(request));

        var startedAt = DateTime.UtcNow;
        _logger.LogInformation(
            "code-review-step: starting project={Project} job={JobId} model={Model} commit={Commit}",
            request.Project, request.JobId, request.Model, request.Commit ?? "(HEAD)");

        var prompt = BuildPrompt(request);

        // Raw-prompt capture provenance: the grade pass is the
        // post-code-review-grade pipeline step; the legacy verdict pass is the
        // user-triggered code-review. Both render from a known runtime template.
        var promptStepId = request.Mode == CodeReviewMode.Grade
            ? AgentStudio.Pipeline.PipelineCatalogue.CodeReviewGradeStepId
            : "code-review";
        var promptTemplateRef = request.Mode == CodeReviewMode.Grade ? GradePromptTemplate : PromptTemplate;

        string rawResponse;
        AspectStatus status;
        string summary;
        OrchestratorTokenUsage? callUsage = null;
        var sw = Stopwatch.StartNew();
        var ok = true;
        string? executionError = null;
        try
        {
            rawResponse = _thinkingAwareCliRunner is null
                ? await CliRunner(request.CliType, request.Model, prompt, request.Timeout, ct)
                : await _thinkingAwareCliRunner(request.CliType, request.Model, prompt, request.ThinkingLevel, request.Timeout, request.JobFolderPath, promptStepId, promptTemplateRef, ct);
            sw.Stop();
            var (parsedText, parsedUsage) = AdHocClaudeInvoker.ParseOrFallback(rawResponse, request.Model);
            rawResponse = parsedText;
            callUsage = parsedUsage;
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            ok = false;
            executionError = ex.Message;
            _logger.LogWarning(ex,
                "code-review-step: CLI invocation failed for {Project}/{JobId}; defaulting to concerns",
                request.Project, request.JobId);
            rawResponse = string.Empty;
        }

        AdHocClaudeInvoker.Record(_usage, UsageSource, request.Model, callUsage,
            sw.ElapsedMilliseconds, ok, project: request.Project, jobId: request.JobId);

        CodeReviewGrade? grade = null;
        IReadOnlyList<string> findings = Array.Empty<string>();
        if (request.Mode == CodeReviewMode.Grade)
        {
            // Quality-grade pass: parse the A/B/C/D sentinel. An unparseable
            // reply is graded C (concerns), never silently A - the work is
            // never waved through on a missing grade.
            var parsedGrade = CodeReviewGradeParsing.ParseGrade(rawResponse);
            if (parsedGrade == null)
            {
                grade = CodeReviewGrade.C;
                summary = string.IsNullOrWhiteSpace(rawResponse)
                    ? "Quality-grade review produced no parseable reply."
                    : "Quality-grade review produced no parseable grade sentinel.";
            }
            else
            {
                grade = parsedGrade.Value.Grade;
                summary = parsedGrade.Value.Summary;
            }
            // Map onto the existing pass/concerns/block status so the report
            // and pipeline rendering reuse one severity concept.
            status = CodeReviewGradeParsing.ToAspectStatus(grade.Value);
            findings = CodeReviewFindingParsing.Parse(rawResponse);
        }
        else
        {
            var parsed = AspectVerdictParsing.ParseVerdict(rawResponse);
            if (parsed == null)
            {
                // No silent durchwinken: an unparseable reply gets a Concerns
                // verdict so the card always shows an outcome.
                status = AspectStatus.Concerns;
                summary = string.IsNullOrWhiteSpace(rawResponse)
                    ? "Code review produced no parseable reply."
                    : "Code review produced no parseable verdict sentinel.";
            }
            else
            {
                status = parsed.Value.Status;
                summary = parsed.Value.Summary;
            }
        }

        var fileNamePrefix = request.Mode == CodeReviewMode.Grade ? "code-review-grade" : "code-review";
        // Milliseconds keep rapid operator-triggered retro grades append-only;
        // a second invocation must never replace the previous report.
        var fileName = $"{fileNamePrefix}-{startedAt:yyyy-MM-ddTHH-mm-ss-fffZ}.md";
        var filePath = Path.Combine(request.JobFolderPath, fileName);

        try
        {
            var report = RenderReport(request, status, summary, grade, rawResponse, startedAt);
            await File.WriteAllTextAsync(filePath, report, ct);
            _fileGenerationIndex?.Upsert(request.JobFolderPath, new FileGenerationMeta
            {
                File = fileName,
                Kind = request.Mode == CodeReviewMode.Grade ? "code-review-grade" : "code-review",
                Model = callUsage?.Model ?? request.Model,
                Cli = request.CliType,
                TokensIn = callUsage?.InputTokens ?? 0,
                TokensOut = callUsage?.OutputTokens ?? 0,
                CacheReadTokens = callUsage?.CacheReadTokens ?? 0,
                CacheCreationTokens = callUsage?.CacheCreationTokens ?? 0,
                TokensTotal = (callUsage?.InputTokens ?? 0)
                    + (callUsage?.OutputTokens ?? 0)
                    + (callUsage?.CacheReadTokens ?? 0)
                    + (callUsage?.CacheCreationTokens ?? 0),
                StartedAt = startedAt,
                EndedAt = startedAt.AddMilliseconds(sw.ElapsedMilliseconds),
                DurationMs = sw.ElapsedMilliseconds,
                StepId = UsageSource,
                HeadShaAfter = request.Commit,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "code-review-step: failed to write {Path}", filePath);
        }

        string? concernTagId;
        if (request.Mode == CodeReviewMode.Grade)
        {
            // A transport/runtime failure did not produce an authoritative
            // grade. Keep the diagnostic report, but do not turn the fallback
            // C used for rendering into a durable grade tag.
            concernTagId = executionError is null
                ? CodeReviewGradeParsing.TagFor(grade!.Value)
                : null;
            if (concernTagId is not null)
            {
                // Authoritative single grade tag: drop any stale
                // code-review:grade-* so a re-graded card carries exactly one.
                ConcernTagWriter.ReplaceCodeReviewGradeTag(request.JobFolderPath, concernTagId, _logger);
            }
            else
            {
                ConcernTagWriter.ClearCodeReviewGradeTags(request.JobFolderPath, _logger);
            }
        }
        else
        {
            concernTagId = TagFor(status);
            if (concernTagId != null)
            {
                ConcernTagWriter.MergeConcernTags(request.JobFolderPath, new[] { concernTagId }, _logger);
            }
        }

        _logger.LogInformation(
            "code-review-step: finished project={Project} job={JobId} model={Model} mode={Mode} verdict={Verdict} grade={Grade} durationMs={DurationMs} file={File}",
            request.Project, request.JobId, request.Model, request.Mode, AspectVerdictParsing.StatusToken(status),
            grade is null ? "-" : CodeReviewGradeParsing.GradeToken(grade.Value),
            sw.ElapsedMilliseconds, fileName);

        return new CodeReviewStepReport(
            FileName: fileName,
            FilePath: filePath,
            Status: status,
            Summary: summary,
            Model: request.Model,
            CliType: request.CliType,
            ThinkingLevel: request.ThinkingLevel,
            Commit: request.Commit,
            ConcernTagId: concernTagId,
            DurationMs: sw.ElapsedMilliseconds,
            StartedAt: startedAt,
            Grade: grade,
            ExecutionError: executionError,
            Findings: findings);
    }

    /// <summary>Tag id for the given verdict, or null when no tag should be hung.</summary>
    public static string? TagFor(AspectStatus status) => status switch
    {
        AspectStatus.Pass => "code-review:pass",
        AspectStatus.Concerns => "code-review:concerns",
        AspectStatus.Block => "code-review:block",
        _ => null
    };

    private string BuildPrompt(CodeReviewStepRequest request)
    {
        var values = new Dictionary<string, string?>
        {
            ["project"] = request.Project,
            ["job_id"] = request.JobId,
            ["job_title"] = request.JobTitle,
            ["task_body"] = request.TaskBody,
            ["commit"] = request.Commit ?? "(HEAD)",
            ["diff"] = request.Diff,
            ["model"] = request.Model,
            ["results_inventory"] = string.IsNullOrWhiteSpace(request.ResultsInventory)
                ? "No results/ inventory available."
                : request.ResultsInventory,
            ["card_mode"] = string.IsNullOrWhiteSpace(request.CardMode)
                ? AgentStudio.Runner.ReviewCardMode.Describe(null)
                : request.CardMode,
        };
        var template = request.Mode == CodeReviewMode.Grade ? GradePromptTemplate : PromptTemplate;
        try
        {
            return _prompts.Render(
                template,
                values,
                new PromptCallContext(
                    request.Project,
                    request.Mode == CodeReviewMode.Grade
                        ? PipelineCatalogue.CodeReviewGradeStepId
                        : "code-review",
                    request.Model));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "code-review-step: prompt template '{Template}' not rendered; using inline fallback",
                template);
            return BuildInlineFallbackPrompt(request);
        }
    }

    private static string BuildInlineFallbackPrompt(CodeReviewStepRequest request)
    {
        var cardMode = string.IsNullOrWhiteSpace(request.CardMode)
            ? AgentStudio.Runner.ReviewCardMode.Describe(null)
            : request.CardMode;
        var resultsInventory = string.IsNullOrWhiteSpace(request.ResultsInventory)
            ? "No results/ inventory available."
            : request.ResultsInventory;

        if (request.Mode == CodeReviewMode.Grade)
        {
            return
                $"# Code review — quality grade\n\n" +
                $"Grade the change set below for **{request.Project}/{request.JobId}** ({request.JobTitle}).\n" +
                $"Commit: `{request.Commit ?? "(HEAD)"}`. Model: `{request.Model}`.\n\n" +
                $"{cardMode}\n\n" +
                $"## Task body\n\n```\n{request.TaskBody}\n```\n\n" +
                $"## Diff (task branch vs base)\n\n```\n{request.Diff}\n```\n\n" +
                $"## results/ folder inventory\n\n```\n{resultsInventory}\n```\n\n" +
                "Treat deliverables as missing only when the diff has no branch changes, the results/ inventory is empty, and no external deliverable is documented.\n\n" +
                "Assign a single quality grade using this rubric:\n" +
                "- **A** — solves the goal clearly, complete, with tests / evidence.\n" +
                "- **B** — solid, small gaps.\n" +
                "- **C** — concerns: half-done or unclear.\n" +
                "- **D**: misses the goal, or redundantly redoes already-present code.\n\n" +
                "For every concrete deficiency named in the paragraph, emit one self-contained actionable finding on its own line:\n" +
                "[[CODE_REVIEW_FINDING: text=<one concrete deficiency and its required outcome>]]\n" +
                "Emit no finding when nothing is open. Then emit exactly one grade sentinel on its own line:\n\n" +
                "[[CODE_REVIEW_GRADE: grade=<A|B|C|D>; summary=<one short sentence>]]\n";
        }

        return
            $"# Code review step\n\n" +
            $"Review the diff below for **{request.Project}/{request.JobId}** ({request.JobTitle}).\n" +
            $"Commit: `{request.Commit ?? "(HEAD)"}`. Model: `{request.Model}`.\n\n" +
            $"{cardMode}\n\n" +
            $"## Task body\n\n```\n{request.TaskBody}\n```\n\n" +
            $"## Diff (task branch vs base)\n\n```\n{request.Diff}\n```\n\n" +
            $"## results/ folder inventory\n\n```\n{resultsInventory}\n```\n\n" +
            "Treat deliverables as missing only when the diff has no branch changes, the results/ inventory is empty, and no external deliverable is documented. " +
            "Block clear task-goal misses, redundant reimplementations of already-present behavior, " +
            "regressions, broken types, and half-finished/stubbed work visible in the diff. " +
            "Use concerns only for shippable issues a human can review without another agent run.\n\n" +
            "Reply with a short paragraph plus exactly one sentinel on its own line:\n\n" +
            "[[ASPECT_VERDICT: status=<pass|concerns|block>; summary=<one short sentence>]]\n";
    }

    private static string RenderReport(
        CodeReviewStepRequest request,
        AspectStatus status,
        string summary,
        CodeReviewGrade? grade,
        string body,
        DateTime startedAt)
    {
        var statusToken = AspectVerdictParsing.StatusToken(status);
        var safeSummary = EscapeYaml(summary);

        if (grade is not null)
        {
            // Quality-grade report: lead with the grade so the Markdown renders
            // prominently (ASS-1657 "nicht billo"). Keep `verdict` in the
            // frontmatter too so the existing list parser still has a value.
            var gradeToken = CodeReviewGradeParsing.GradeToken(grade.Value);
            var gradeTag = CodeReviewGradeParsing.TagFor(grade.Value);
            return
                "---\n" +
                "type: code-review-grade\n" +
                $"runAt: {startedAt:O}\n" +
                $"model: {request.Model}\n" +
                $"cliType: {request.CliType}\n" +
                (string.IsNullOrWhiteSpace(request.ThinkingLevel) ? string.Empty : $"thinkingLevel: {request.ThinkingLevel}\n") +
                $"commit: {request.Commit ?? "(HEAD)"}\n" +
                $"grade: {gradeToken}\n" +
                $"verdict: {statusToken}\n" +
                $"summary: {safeSummary}\n" +
                $"tag: {gradeTag}\n" +
                "---\n\n" +
                $"# Code Review — Quality Grade: {gradeToken}\n\n" +
                (string.IsNullOrWhiteSpace(summary) ? string.Empty : $"> {summary}\n\n") +
                $"**Grade:** {gradeToken} &nbsp;·&nbsp; **Model:** `{request.Model}` (`{request.CliType}`) &nbsp;·&nbsp; **Commit:** `{request.Commit ?? "(HEAD)"}`\n\n" +
                "| Grade | Meaning |\n" +
                "| :---: | --- |\n" +
                $"| {(gradeToken == "A" ? "**A**" : "A")} | Solves the goal clearly, complete, with tests / evidence |\n" +
                $"| {(gradeToken == "B" ? "**B**" : "B")} | Solid, small gaps |\n" +
                $"| {(gradeToken == "C" ? "**C**" : "C")} | Concerns: half-done or unclear |\n" +
                $"| {(gradeToken == "D" ? "**D**" : "D")} | Misses the goal, or redundantly redoes existing code |\n\n" +
                "## Reviewer reply\n\n" +
                (string.IsNullOrWhiteSpace(body)
                    ? "_No reply text was returned by the model._\n"
                    : body.Trim() + "\n");
        }

        var tag = TagFor(status);
        return
            "---\n" +
            "type: code-review-step\n" +
            $"runAt: {startedAt:O}\n" +
            $"model: {request.Model}\n" +
            $"cliType: {request.CliType}\n" +
            (string.IsNullOrWhiteSpace(request.ThinkingLevel) ? string.Empty : $"thinkingLevel: {request.ThinkingLevel}\n") +
            $"commit: {request.Commit ?? "(HEAD)"}\n" +
            $"verdict: {statusToken}\n" +
            $"summary: {safeSummary}\n" +
            (tag is null ? string.Empty : $"tag: {tag}\n") +
            "---\n\n" +
            "# Code Review Step\n\n" +
            $"**Verdict:** {statusToken}\n\n" +
            (string.IsNullOrWhiteSpace(summary) ? string.Empty : $"**Summary:** {summary}\n\n") +
            $"**Model:** `{request.Model}` (`{request.CliType}`)\n\n" +
            $"**Commit:** `{request.Commit ?? "(HEAD)"}`\n\n" +
            "## Reviewer reply\n\n" +
            (string.IsNullOrWhiteSpace(body)
                ? "_No reply text was returned by the model._\n"
                : "```\n" + body.Trim() + "\n```\n");
    }

    private static string EscapeYaml(string s)
    {
        if (string.IsNullOrEmpty(s)) return "''";
        var oneLine = s.Replace("\r", " ").Replace("\n", " ").Trim();
        if (oneLine.Contains(':') || oneLine.Contains('#') || oneLine.StartsWith('-'))
        {
            return "\"" + oneLine.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }
        return oneLine;
    }

    private async Task<string> RunViaOneShotAsync(string cliType, string model, string prompt, string? thinkingLevel, TimeSpan timeout, string jobFolderPath, string stepId, string? templateRef, CancellationToken ct)
    {
        var oneShot = _oneShotRegistry?.Get(cliType);
        if (oneShot == null) return await DefaultRunCliAsync(cliType, model, prompt, timeout, ct);

        var result = await oneShot.RunAsync(new CliOneShotRequest(
            CliType: cliType,
            Model: model,
            Prompt: prompt)
        {
            ThinkingLevel = thinkingLevel,
            Timeout = timeout,
            Source = UsageSource,
            RecordUsage = false, // RunAsync caller (this service) records via AdHocClaudeInvoker.Record
            // Raw-prompt capture: central dispatch decorator writes the final
            // prompt to .metadata/prompts.jsonl keyed on this step id.
            JobFolderPath = jobFolderPath,
            StepId = stepId,
            TemplateRef = templateRef,
        }, ct);

        if (!result.Ok)
        {
            _logger.LogWarning(
                "code-review-step: CLI '{Cli}' returned exit={Exit} duration={DurationMs}ms error={Error}",
                cliType, result.ExitCode, result.Duration.TotalMilliseconds, result.Error);
        }
        return CliOneShotCompatibility.ToClaudeResultEnvelope(result, model);
    }

    private static Task<string> DefaultRunCliAsync(
        string cliType, string model, string prompt, TimeSpan timeout, CancellationToken ct)
    {
        // Tests construct the service without an ICliOneShot registry and
        // overwrite CliRunner directly. Production always wires through
        // RunViaOneShotAsync; this fallback exists so the service is
        // constructible in unit tests that only need the parsing path.
        return Task.FromResult(string.Empty);
    }
}

/// <summary>
/// Which review the step runs. <see cref="Verdict"/> is the legacy
/// user-triggered pass/concerns/block review; <see cref="Grade"/> is the
/// automatic pipeline pass that assigns a quality grade A/B/C/D
/// (ASS-1657). Same service, prompt-and-parse differ by mode.
/// </summary>
public enum CodeReviewMode
{
    Verdict,
    Grade,
}

/// <summary>One code-review request: who to review, with which model, against which commit.</summary>
public sealed record CodeReviewStepRequest(
    string Project,
    string JobId,
    string JobTitle,
    string JobFolderPath,
    string TaskBody,
    string Diff,
    string CliType,
    string Model)
{
    public string? ThinkingLevel { get; init; }

    /// <summary>Optional commit SHA. When null, the prompt records HEAD.</summary>
    public string? Commit { get; init; }

    /// <summary>Wall-clock cap for the CLI invocation. Defaults to 10 minutes.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Verdict (default, user-triggered) or Grade (automatic quality grade).
    /// Defaults to <see cref="CodeReviewMode.Verdict"/> so every existing
    /// caller keeps its current behaviour unchanged.
    /// </summary>
    public CodeReviewMode Mode { get; init; } = CodeReviewMode.Verdict;

    /// <summary>
    /// Inventory of the job's <c>results/</c> folder (file list + short excerpts).
    /// Completes the evidence source so the grade / verdict never reads an empty
    /// diff as "deliverables missing" when the deliverable is a results/ artefact
    /// (AGT-2022). Defaults to empty for existing callers.
    /// </summary>
    public string ResultsInventory { get; init; } = string.Empty;

    /// <summary>
    /// One-line framing of the card's execution mode so an empty code diff on a
    /// read-only planning / research card is read as legitimate.
    /// </summary>
    public string CardMode { get; init; } = string.Empty;
}

/// <summary>Per-call report returned by <see cref="CodeReviewStepService.RunAsync"/>.</summary>
public sealed record CodeReviewStepReport(
    string FileName,
    string FilePath,
    AspectStatus Status,
    string Summary,
    string Model,
    string CliType,
    string? ThinkingLevel,
    string? Commit,
    string? ConcernTagId,
    long DurationMs,
    DateTime StartedAt,
    CodeReviewGrade? Grade = null,
    string? ExecutionError = null,
    IReadOnlyList<string>? Findings = null);
