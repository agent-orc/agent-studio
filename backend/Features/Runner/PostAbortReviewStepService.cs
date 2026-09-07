using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentStudio.Runner;

/// <summary>
/// The "Abbruch-Review" pipeline step: a single LLM pass that runs after a
/// non-clean CLI run end (watchdog timeout, non-zero exit, unexpected stop)
/// to decide whether the abort was legitimate or whether the pipeline should
/// be kept alive with a rerun.
///
/// <para>
/// This is the agent zone of the ADR-0032 contract pattern. The service
/// gathers the structured evidence the operator listed - abort reason +
/// phase, cli-output tail, <c>tool-calls.jsonl</c> liveness, git state, the
/// task goal, and session usage - writes a typed input contract under the
/// job folder's <c>contracts/</c>, asks the model for a structured verdict,
/// and writes the parsed verdict back as an output contract plus a
/// human-readable <c>post-abort-review-{utc}.md</c> report. The
/// escalate-vs-rerun <em>decision</em> is not the model's: it is made by the
/// pure <see cref="PostAbortReviewDecider"/> and recorded here for the
/// orchestrator to apply. Fail-closed: any CLI failure or unparseable reply
/// yields a null verdict, which the decider routes to human review.
/// </para>
///
/// <para>
/// Mirrors <see cref="AgentStudio.Review.CodeReviewStepService"/>'s
/// CLI seam so tests substitute a deterministic runner without a subprocess,
/// and never touches lane state - the orchestrator owns transitions.
/// </para>
/// </summary>
public sealed class PostAbortReviewStepService
{
    private readonly RuntimePromptService _prompts;
    private readonly ILogger<PostAbortReviewStepService> _logger;
    private readonly AdHocUsageRecorder? _usage;
    private readonly CliOneShotRegistry? _oneShotRegistry;

    /// <summary>
    /// CLI invocation seam. Production wires it through <see cref="ICliOneShot"/>
    /// when a registry is present; tests overwrite it with a deterministic stub.
    /// </summary>
    public Func<string, string, string, TimeSpan, CancellationToken, Task<string>> CliRunner { get; set; }
        = DefaultRunCliAsync;
    private Func<string, string, string, string?, TimeSpan, CancellationToken, Task<string>>? _thinkingAwareCliRunner;

    public PostAbortReviewStepService(
        RuntimePromptService prompts,
        ILogger<PostAbortReviewStepService> logger,
        AdHocUsageRecorder? usage = null,
        CliOneShotRegistry? oneShotRegistry = null)
    {
        _prompts = prompts;
        _logger = logger;
        _usage = usage;
        _oneShotRegistry = oneShotRegistry;

        if (_oneShotRegistry != null)
        {
            _thinkingAwareCliRunner = RunViaOneShotAsync;
        }
    }

    public const string PromptTemplate = "post-abort-review.md";
    public const string UsageSource = "post-abort-review-step";
    public const string ContractsDirName = "contracts";
    public const string InputContractName = "post-abort-review-input.json";
    public const string OutputContractName = "post-abort-review-output.json";

    private static readonly JsonSerializerOptions ContractJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>
    /// Run one abort-review pass and persist its evidence. Never throws on a
    /// CLI / parse failure: it returns a report whose <see cref="PostAbortReviewStepReport.Verdict"/>
    /// is null and whose <see cref="PostAbortReviewStepReport.Action"/> is the
    /// fail-closed <see cref="PostAbortAction.EscalateHuman"/>.
    /// </summary>
    public async Task<PostAbortReviewStepReport> RunAsync(PostAbortReviewRequest request, CancellationToken ct)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        if (string.IsNullOrWhiteSpace(request.JobFolderPath))
            throw new ArgumentException("JobFolderPath is required", nameof(request));

        var startedAt = DateTime.UtcNow;
        _logger.LogInformation(
            "post-abort-review: starting project={Project} job={JobId} model={Model} phase={Phase} budgetRemaining={Budget}",
            request.Project, request.JobId, request.Model, request.AbortPhase, request.RerunBudgetRemaining);

        WriteInputContract(request, startedAt);

        var prompt = BuildPrompt(request);

        string rawResponse;
        OrchestratorTokenUsage? callUsage = null;
        var sw = Stopwatch.StartNew();
        var ok = true;
        try
        {
            rawResponse = _thinkingAwareCliRunner is null
                ? await CliRunner(request.CliType, request.Model, prompt, request.Timeout, ct)
                : await _thinkingAwareCliRunner(request.CliType, request.Model, prompt, request.ThinkingLevel, request.Timeout, ct);
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
            _logger.LogWarning(ex,
                "post-abort-review: CLI invocation failed for {Project}/{JobId}; failing closed to human review",
                request.Project, request.JobId);
            rawResponse = string.Empty;
        }

        AdHocClaudeInvoker.Record(_usage, UsageSource, request.Model, callUsage,
            sw.ElapsedMilliseconds, ok, project: request.Project, jobId: request.JobId);

        var verdict = PostAbortReviewVerdictParsing.Parse(rawResponse);
        var action = PostAbortReviewDecider.Decide(verdict, request.RerunBudgetRemaining);

        var fileName = $"post-abort-review-{startedAt:yyyy-MM-ddTHH-mm-ssZ}.md";
        var filePath = Path.Combine(request.JobFolderPath, fileName);
        try
        {
            await File.WriteAllTextAsync(filePath, RenderReport(request, verdict, action, rawResponse, startedAt), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "post-abort-review: failed to write {Path}", filePath);
        }

        WriteOutputContract(request, verdict, action, rawResponse, startedAt);

        var tagId = TagFor(action);
        ConcernTagWriter.MergeConcernTags(request.JobFolderPath, new[] { tagId }, _logger);

        _logger.LogInformation(
            "post-abort-review: finished project={Project} job={JobId} verdict={Verdict} action={Action} confidence={Confidence} durationMs={DurationMs} file={File}",
            request.Project, request.JobId,
            verdict is null ? "(unparseable)" : RecommendationToken(verdict.Recommendation),
            action, verdict?.Confidence ?? 0, sw.ElapsedMilliseconds, fileName);

        return new PostAbortReviewStepReport(
            FileName: fileName,
            FilePath: filePath,
            Verdict: verdict,
            Action: action,
            TagId: tagId,
            Model: request.Model,
            CliType: request.CliType,
            DurationMs: sw.ElapsedMilliseconds,
            StartedAt: startedAt);
    }

    /// <summary>Tag id hung on the card for the decided action.</summary>
    public static string TagFor(PostAbortAction action) => action switch
    {
        PostAbortAction.Rerun => "abort-review:rerun",
        PostAbortAction.RerunWithStrongerFraming => "abort-review:rerun-stronger",
        PostAbortAction.AcceptAndContinue => "abort-review:accept",
        _ => "abort-review:human-review",
    };

    public static string RecommendationToken(PostAbortRecommendation rec) => rec switch
    {
        PostAbortRecommendation.Rerun => "rerun",
        PostAbortRecommendation.StrongerReissue => "stronger-reissue",
        PostAbortRecommendation.HumanReview => "human-review",
        PostAbortRecommendation.Accept => "accept",
        _ => "human-review",
    };

    public static string ActionToken(PostAbortAction action) => action switch
    {
        PostAbortAction.Rerun => "rerun",
        PostAbortAction.RerunWithStrongerFraming => "rerun-stronger",
        PostAbortAction.AcceptAndContinue => "accept",
        _ => "human-review",
    };

    private void WriteInputContract(PostAbortReviewRequest request, DateTime startedAt)
    {
        var dto = new PostAbortReviewInputContract
        {
            Project = request.Project,
            JobId = request.JobId,
            CreatedAt = startedAt,
            AbortReason = request.AbortReason,
            AbortPhase = request.AbortPhase,
            CliOutputTail = request.CliOutputTail,
            ToolCallsLiveness = request.ToolCallsLiveness,
            GitState = request.GitState,
            TranscriptUsage = request.TranscriptUsage,
            TaskTitle = request.TaskTitle,
            TaskBody = request.TaskBody,
            RerunBudgetRemaining = request.RerunBudgetRemaining,
            Model = request.Model,
            CliType = request.CliType,
        };
        WriteContract(request.JobFolderPath, InputContractName, dto);
    }

    private void WriteOutputContract(
        PostAbortReviewRequest request,
        PostAbortReviewVerdict? verdict,
        PostAbortAction action,
        string rawResponse,
        DateTime startedAt)
    {
        var dto = new PostAbortReviewOutputContract
        {
            Project = request.Project,
            JobId = request.JobId,
            CreatedAt = startedAt,
            Parsed = verdict != null,
            LegitimateAbort = verdict?.LegitimateAbort,
            Recommendation = verdict is null ? null : RecommendationToken(verdict.Recommendation),
            Confidence = verdict?.Confidence,
            Reasoning = verdict?.Reasoning,
            Action = ActionToken(action),
            RerunBudgetRemaining = request.RerunBudgetRemaining,
            RawReply = string.IsNullOrWhiteSpace(rawResponse) ? null : rawResponse.Trim(),
        };
        WriteContract(request.JobFolderPath, OutputContractName, dto);
    }

    private void WriteContract(string jobFolderPath, string name, object dto)
    {
        try
        {
            var dir = Path.Combine(jobFolderPath, ContractsDirName);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, name);
            File.WriteAllText(path, JsonSerializer.Serialize(dto, dto.GetType(), ContractJsonOptions));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "post-abort-review: failed to write contract {Name}", name);
        }
    }

    private string BuildPrompt(PostAbortReviewRequest request)
    {
        var values = new Dictionary<string, string?>
        {
            ["project"] = request.Project,
            ["job_id"] = request.JobId,
            ["task_title"] = request.TaskTitle,
            ["task_body"] = request.TaskBody,
            ["abort_reason"] = request.AbortReason,
            ["abort_phase"] = request.AbortPhase,
            ["cli_output_tail"] = request.CliOutputTail,
            ["tool_calls_liveness"] = request.ToolCallsLiveness,
            ["git_state"] = request.GitState,
            ["transcript_usage"] = request.TranscriptUsage,
            ["rerun_budget_remaining"] = request.RerunBudgetRemaining.ToString(),
            ["model"] = request.Model,
        };
        try
        {
            return _prompts.Render(
                PromptTemplate,
                values,
                new PromptCallContext(
                    request.Project,
                    PipelineCatalogue.PostAbortReviewStepId,
                    request.Model));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "post-abort-review: prompt template '{Template}' not rendered; using inline fallback",
                PromptTemplate);
            return BuildInlineFallbackPrompt(request);
        }
    }

    private static string BuildInlineFallbackPrompt(PostAbortReviewRequest request)
    {
        return
            "# Post-abort review\n\n" +
            $"A CLI agent run for **{request.Project}/{request.JobId}** ended in a non-clean state.\n" +
            "Decide whether the abort was legitimate or whether re-running is worthwhile.\n" +
            "A legitimate long-running operation (ng serve / build / test-server wait / poll loop) " +
            "must NOT be treated as a hang.\n\n" +
            $"## Task goal\n\n{request.TaskTitle}\n\n```\n{request.TaskBody}\n```\n\n" +
            $"## Abort\n\nReason: {request.AbortReason}\nPhase: {request.AbortPhase}\n\n" +
            $"## Tool-call liveness\n\n{request.ToolCallsLiveness}\n\n" +
            $"## Git state\n\n{request.GitState}\n\n" +
            $"## Session usage\n\n{request.TranscriptUsage}\n\n" +
            $"## CLI output tail\n\n```\n{request.CliOutputTail}\n```\n\n" +
            "Reply with a short paragraph plus exactly one sentinel on its own line:\n\n" +
            "[[ABORT_REVIEW: legitimate=<true|false>; recommendation=<rerun|stronger-reissue|human-review|accept>; confidence=<0.0-1.0>; reason=<one short sentence>]]\n";
    }

    private static string RenderReport(
        PostAbortReviewRequest request,
        PostAbortReviewVerdict? verdict,
        PostAbortAction action,
        string rawResponse,
        DateTime startedAt)
    {
        var recommendation = verdict is null ? "(unparseable)" : RecommendationToken(verdict.Recommendation);
        var actionToken = ActionToken(action);
        var legitimate = verdict?.LegitimateAbort;
        var confidence = verdict?.Confidence;
        var reason = verdict?.Reasoning ?? string.Empty;

        return
            "---\n" +
            "type: post-abort-review\n" +
            $"runAt: {startedAt:O}\n" +
            $"model: {request.Model}\n" +
            $"cliType: {request.CliType}\n" +
            $"abortPhase: {request.AbortPhase}\n" +
            $"recommendation: {recommendation}\n" +
            $"action: {actionToken}\n" +
            (legitimate is null ? string.Empty : $"legitimateAbort: {(legitimate.Value ? "true" : "false")}\n") +
            (confidence is null ? string.Empty : $"confidence: {confidence.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}\n") +
            $"rerunBudgetRemaining: {request.RerunBudgetRemaining}\n" +
            $"summary: {EscapeYaml(reason)}\n" +
            $"tag: {TagFor(action)}\n" +
            "---\n\n" +
            "# Post-abort Review\n\n" +
            $"**Recommendation:** {recommendation}\n\n" +
            $"**Decided action:** {actionToken} (rerun budget remaining: {request.RerunBudgetRemaining})\n\n" +
            (legitimate is null ? string.Empty : $"**Legitimate abort:** {(legitimate.Value ? "yes" : "no")}\n\n") +
            (confidence is null ? string.Empty : $"**Confidence:** {confidence.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}\n\n") +
            (string.IsNullOrWhiteSpace(reason) ? string.Empty : $"**Reasoning:** {reason}\n\n") +
            $"**Abort reason:** {request.AbortReason}\n\n" +
            $"**Model:** `{request.Model}` (`{request.CliType}`)\n\n" +
            "## Evidence\n\n" +
            $"- Tool-call liveness: {request.ToolCallsLiveness}\n" +
            $"- Git state: {request.GitState}\n" +
            $"- Session usage: {request.TranscriptUsage}\n\n" +
            "## Reviewer reply\n\n" +
            (string.IsNullOrWhiteSpace(rawResponse)
                ? "_No reply text was returned by the model; failed closed to human review._\n"
                : "```\n" + rawResponse.Trim() + "\n```\n");
    }

    private static string EscapeYaml(string s)
    {
        if (string.IsNullOrEmpty(s)) return "''";
        var oneLine = s.Replace("\r", " ").Replace("\n", " ").Trim();
        if (oneLine.Contains(':') || oneLine.Contains('#') || oneLine.StartsWith('-'))
            return "\"" + oneLine.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        return oneLine;
    }

    private async Task<string> RunViaOneShotAsync(string cliType, string model, string prompt, string? thinkingLevel, TimeSpan timeout, CancellationToken ct)
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
            RecordUsage = false,
        }, ct);

        if (!result.Ok)
        {
            _logger.LogWarning(
                "post-abort-review: CLI '{Cli}' returned exit={Exit} duration={DurationMs}ms error={Error}",
                cliType, result.ExitCode, result.Duration.TotalMilliseconds, result.Error);
        }
        return CliOneShotCompatibility.ToClaudeResultEnvelope(result, model);
    }

    private static Task<string> DefaultRunCliAsync(
        string cliType, string model, string prompt, TimeSpan timeout, CancellationToken ct)
        => Task.FromResult(string.Empty);
}

/// <summary>
/// Evidence the orchestrator hands the abort-review step. Every field is a
/// pre-rendered string so the pure service does no job-folder reading of its
/// own - the runner, which already holds this evidence, populates it.
/// </summary>
public sealed record PostAbortReviewRequest(
    string Project,
    string JobId,
    string JobFolderPath,
    string TaskTitle,
    string TaskBody,
    string AbortReason,
    string AbortPhase,
    string CliOutputTail,
    string ToolCallsLiveness,
    string GitState,
    string TranscriptUsage,
    string CliType,
    string Model)
{
    public string? ThinkingLevel { get; init; }

    /// <summary>Automatic reruns this job has left. Drives the decider.</summary>
    public int RerunBudgetRemaining { get; init; } = PostAbortReviewDecider.DefaultRerunBudget;

    /// <summary>Wall-clock cap for the CLI invocation. Defaults to 5 minutes.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>Per-call report returned by <see cref="PostAbortReviewStepService.RunAsync"/>.</summary>
public sealed record PostAbortReviewStepReport(
    string FileName,
    string FilePath,
    PostAbortReviewVerdict? Verdict,
    PostAbortAction Action,
    string TagId,
    string Model,
    string CliType,
    long DurationMs,
    DateTime StartedAt);

/// <summary>
/// Serialized input contract written to
/// <c>contracts/post-abort-review-input.json</c> (ADR-0032). Schema lives at
/// <c>docs/app/schemas/post-abort-review-input.schema.json</c>.
/// </summary>
public sealed record PostAbortReviewInputContract
{
    public string Project { get; init; } = string.Empty;
    public string JobId { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public string AbortReason { get; init; } = string.Empty;
    public string AbortPhase { get; init; } = string.Empty;
    public string CliOutputTail { get; init; } = string.Empty;
    public string ToolCallsLiveness { get; init; } = string.Empty;
    public string GitState { get; init; } = string.Empty;
    public string TranscriptUsage { get; init; } = string.Empty;
    public string TaskTitle { get; init; } = string.Empty;
    public string TaskBody { get; init; } = string.Empty;
    public int RerunBudgetRemaining { get; init; }
    public string Model { get; init; } = string.Empty;
    public string CliType { get; init; } = string.Empty;
}

/// <summary>
/// Serialized output contract written to
/// <c>contracts/post-abort-review-output.json</c> (ADR-0032). Schema lives at
/// <c>docs/app/schemas/post-abort-review-output.schema.json</c>.
/// </summary>
public sealed record PostAbortReviewOutputContract
{
    public string Project { get; init; } = string.Empty;
    public string JobId { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public bool Parsed { get; init; }
    public bool? LegitimateAbort { get; init; }
    public string? Recommendation { get; init; }
    public double? Confidence { get; init; }
    public string? Reasoning { get; init; }
    public string Action { get; init; } = string.Empty;
    public int RerunBudgetRemaining { get; init; }
    public string? RawReply { get; init; }
}
