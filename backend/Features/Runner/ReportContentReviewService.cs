using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using AgentStudio.Pipeline;
using AgentStudio.Tasks;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

/// <summary>Outcome of one report content review.</summary>
public sealed record ReportContentReviewResult(
    bool Ran,
    string? Status,
    string Reason,
    bool QuotaDeferred = false);

/// <summary>
/// The content verdict path of planning and research cards (AGT-3565). The
/// lightweight report pipeline reviews the delivered report under
/// <c>results/</c> with the same requirement-fit aspect that reviews coding
/// cards, and binds the verdict to the run attempt and brief it reviewed, so
/// completion can read it. The pipeline runs it after the summary step, and the
/// Review results action runs it later for a card whose run finished without one.
/// </summary>
public sealed class ReportContentReviewService
{
    public const string AspectId = "requirement-fit";

    private const int TaskBodyCharacters = 8_000;
    private const int ReportTextCharacters = 24_000;

    private static readonly Regex HiddenBlocks = new(
        @"<(script|style|head)\b[^>]*>.*?</\1\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex Tags = new(@"<[^>]+>", RegexOptions.CultureInvariant);
    private static readonly Regex Blank = new(@"[ \t\r\f\v]+", RegexOptions.CultureInvariant);
    private static readonly Regex BlankLines = new(@"\n\s*\n+", RegexOptions.CultureInvariant);

    private readonly AspectRunnerService _aspects;
    private readonly IConfiguration _configuration;
    private readonly ProjectSettingsService? _projectSettings;
    private readonly AttemptAuthorityService? _authority;
    private readonly TimelineLog? _timeline;
    private readonly ConcurrentDictionary<string, byte> _lateReviews = new(StringComparer.OrdinalIgnoreCase);

    public ReportContentReviewService(
        AspectRunnerService aspects,
        IConfiguration configuration,
        ProjectSettingsService? projectSettings = null,
        AttemptAuthorityService? authority = null,
        TimelineLog? timeline = null)
    {
        _aspects = aspects;
        _configuration = configuration;
        _projectSettings = projectSettings;
        _authority = authority;
        _timeline = timeline;
    }

    /// <summary>
    /// True when the card's current delivery has no content verdict for its
    /// current brief, so the review step is still missing.
    /// </summary>
    public bool IsMissing(TaskInfo task)
    {
        var facts = CompletionContentEvidence.Read(task, _authority);
        return string.IsNullOrWhiteSpace(facts.ContentStatus)
            || !string.Equals(facts.ReviewBriefVersion, facts.CurrentBriefVersion, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Why Review results cannot run for this card, or null when it can: a
    /// planning or research card waiting for a person, whose current delivery
    /// still lacks its content verdict. A present verdict, also concerns, is
    /// never reviewed again for the same delivery and brief.
    /// </summary>
    public string? LateReviewRefusal(TaskInfo task)
    {
        if (!TaskModes.IsReportOnly(task.Mode))
            return "Review results applies to planning and research cards.";
        if (task.State is not (TaskStates.HumanReview or TaskStates.Escalated))
            return $"Review results runs on a card in {TaskStates.HumanReview} or {TaskStates.Escalated}, not {task.State}.";
        if (!ResultsInventory.HasActiveArtifacts(task.FolderPath))
            return "The card has no delivered result under results/.";
        if (!IsMissing(task))
            return "The current delivery already has a requirement-fit verdict for the current brief.";
        return null;
    }

    /// <summary>
    /// The Review results action: runs only the missing requirement-fit step
    /// for a card whose run finished without it. The research is not repeated
    /// and the card stays in its lane.
    /// </summary>
    public async Task<ReportContentReviewResult> RunLateAsync(TaskInfo task, string project, CancellationToken ct)
    {
        if (LateReviewRefusal(task) is { } refusal) return new(false, null, refusal);
        var key = Path.GetFullPath(task.FolderPath);
        if (!_lateReviews.TryAdd(key, 0))
            return new(false, null, "A review of this card's results is already running.");
        try
        {
            _timeline?.Append(task.FolderPath, TimelineEventKinds.PostStepStarted, TimelineActors.Human(string.Empty),
                "Review results started the missing requirement-fit review of the delivered report.");
            var result = await RunAsync(task, project, ct);
            _timeline?.Append(task.FolderPath, TimelineEventKinds.PostStepFinished, TimelineActors.System,
                "Review results: " + result.Reason);
            return result;
        }
        finally
        {
            _lateReviews.TryRemove(key, out _);
        }
    }

    public async Task<ReportContentReviewResult> RunAsync(
        TaskInfo task,
        string project,
        CancellationToken ct)
    {
        if (!TaskModes.IsReportOnly(task.Mode))
            return new(false, null, "Only planning and research cards have a report review.");

        var settings = PipelineTypeSettings.ForTask(_projectSettings?.Get(project), task);
        var step = PipelineCatalogue.ReadOnly.Post.Single(item => item.Id == PipelineCatalogue.RequirementFitStepId);
        if (!PipelineStepConfigResolver.ShouldRun(settings, step, new PipelineStepConditionContext
            {
                ExitCode = 0,
                TaskType = task.TaskType,
                Tags = task.Tags,
            }))
            return new(false, null, "The requirement-fit step is disabled for this pipeline type.");

        // A remote run binds the verdict to its attempt and to the brief the
        // review reads now. A local run keeps the brief its run stamped.
        var run = _authority?.GetTaskProjection(task.Key ?? task.Id).CurrentRunAttempt;
        var (shortBody, recentLog) = ReviewDecisionOrchestrator.LoadTaskContext(task.FolderPath);
        var taskBody = ReadTaskBody(task.FolderPath, shortBody);
        var inputs = new AspectRunInputs(
            Project: project,
            JobId: task.Id,
            JobTitle: task.Title ?? task.Id,
            JobFolderPath: task.FolderPath,
            TaskBody: taskBody,
            RecentLog: recentLog,
            DiffSummary: "Report card: the run leaves no repository diff by contract. "
                + "The deliverable is the report under results/.",
            StatusSummary: ReviewDecisionOrchestrator.LoadStatusSummary(task.FolderPath))
        {
            ResultsInventory = RenderEvidence(task.FolderPath),
            CardMode = ReviewCardMode.Describe(task.Mode),
            AcceptanceScope = RequirementAcceptanceScope.Describe(task.AcceptanceScope, taskBody),
            ContentBriefVersion = run is null ? null : BriefVersionStore.ReadCurrent(task.FolderPath),
            ContentRunAttemptId = run?.AttemptId,
        };

        var cliBinary = _configuration.GetValue("ReviewDecisionOrchestrator:Cli", CliTypes.Codex)!;
        var model = _configuration.GetValue("ReviewDecisionOrchestrator:Model",
            ModelFamilyResolver.Resolve(ModelFamilies.GptMini))!;
        var aspectModel = _configuration.GetValue("ReviewDecisionOrchestrator:AspectModel", model)!;
        var stepModel = PipelineStepConfigResolver.ResolveModelWithSource(settings, step.Id, aspectModel);
        var stepCli = PipelineStepConfigResolver.ResolveCliType(settings, step)
            ?? ReviewDecisionOrchestrator.NormalizeReviewCliType(cliBinary);
        var thinking = PipelineStepConfigResolver.ResolveThinkingLevel(settings, step.Id, stepCli, stepModel.Model);
        var timeout = TimeSpan.FromSeconds(ReviewAspectTimeoutPolicy.Derive(stepCli, stepModel.Model, thinking,
            Contract.ReviewAspectBudgetPolicy.MaterialCharacters(
                inputs.TaskBody + inputs.ResultsInventory + inputs.StatusSummary + inputs.RecentLog),
            _configuration).Seconds);

        var report = await _aspects.RunAsync(inputs, [AspectId], cliBinary, aspectModel, timeout, ct,
            modelForAspect: _ => stepModel.Model,
            thinkingLevelForAspect: _ => thinking,
            promptForAspect: _ => PipelineStepConfigResolver.ResolvePrompt(settings, step.Id),
            cliForAspect: _ => stepCli,
            modelSourceForAspect: _ => stepModel.Source);
        if (report.HasQuotaDeferred)
            return new(false, null, "The requirement-fit review waits for model quota.", QuotaDeferred: true);
        var verdict = report.Verdicts.FirstOrDefault(item =>
            string.Equals(item.Aspect, AspectId, StringComparison.OrdinalIgnoreCase));
        if (verdict is null)
            return new(false, null, "The requirement-fit review returned no verdict.");
        var status = AspectVerdictParsing.StatusToken(verdict.Status);
        return new(true, status, $"Requirement fit of the report: {status}. {verdict.Summary}".Trim());
    }

    private static string ReadTaskBody(string folder, string truncated)
    {
        var path = Path.Combine(folder, "prompt.md");
        var full = File.Exists(path) ? BoundedFileRead.ReadAllTextOrNull(path, 256 * 1024) : null;
        if (string.IsNullOrEmpty(full)) return truncated;
        return full.Length <= TaskBodyCharacters ? full : full[..TaskBodyCharacters] + "\n... (truncated)";
    }

    /// <summary>
    /// The bounded results/ inventory plus the visible text of the primary
    /// report, so the review judges the report and not only its first lines.
    /// </summary>
    internal static string RenderEvidence(string folder)
    {
        var inventory = ResultsInventory.RenderForReview(folder);
        var reportPath = Path.Combine(folder, "results", "report.html");
        if (!File.Exists(reportPath)) return inventory;
        string text;
        try
        {
            text = ReportText(BoundedFileRead.ReadHeadText(reportPath, ReportTextCharacters * 16));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SilentCatch.Note(ex, "ReportContentReviewService: report text is best-effort review evidence");
            return inventory;
        }
        if (string.IsNullOrWhiteSpace(text)) return inventory;
        var clipped = text.Length <= ReportTextCharacters
            ? text
            : text[..ReportTextCharacters] + "\n... (truncated)";
        return inventory + "\n\nText of results/report.html (markup removed):\n" + clipped;
    }

    internal static string ReportText(string html)
    {
        var text = HiddenBlocks.Replace(html, " ");
        text = Regex.Replace(text, @"<(br|/p|/div|/li|/h[1-6]|/tr|/section)\b[^>]*>", "\n",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        text = WebUtility.HtmlDecode(Tags.Replace(text, " "));
        text = Blank.Replace(text, " ");
        return BlankLines.Replace(text.Replace("\n ", "\n"), "\n\n").Trim();
    }
}
