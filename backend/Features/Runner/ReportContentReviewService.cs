using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AgentStudio.Pipeline;
using AgentStudio.Prompts;
using AgentStudio.Tasks;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

/// <summary>Outcome of one report content review.</summary>
public sealed record ReportContentReviewResult(
    bool Ran,
    string? Status,
    string Reason,
    bool QuotaDeferred = false,
    bool HostWaiting = false);

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
    // AGT-3853: 24,000 characters cut long reports before their later
    // sections; the review now reads 64,000 and names the headings it cut.
    internal const int ReportTextCharacters = 64_000;
    // The agent's delivery summary gets the bound of the remote review plan.
    internal const int DeliverySummaryCharacters = 16_000;
    private const int RunRecordCharacters = 1_500;
    private const string DeliverySummaryFile = "status.md";

    private static readonly Regex HiddenBlocks = new(
        @"<(script|style|head)\b[^>]*>.*?</\1\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex Tags = new(@"<[^>]+>", RegexOptions.CultureInvariant);
    private static readonly Regex Blank = new(@"[ \t\r\f\v]+", RegexOptions.CultureInvariant);
    private static readonly Regex BlankLines = new(@"\n\s*\n+", RegexOptions.CultureInvariant);
    private static readonly Regex Headings = new(
        @"<h([1-3])\b[^>]*>(.*?)</h\1\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

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
    /// The content verdict the current delivery already has for its current
    /// brief, or null while the review step is missing (AGT-3797).
    /// </summary>
    public ReportContentReviewResult? CurrentVerdict(TaskInfo task)
    {
        if (IsMissing(task)) return null;
        var facts = CompletionContentEvidence.Read(task, _authority);
        return new(true, facts.ContentStatus,
            $"Requirement fit of the report: {facts.ContentStatus}. {facts.ContentSummary}".Trim());
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
        if (ReportDeliverable.MissingRequiredReport(task) is { } report)
            return $"The card has no required report: {report}. Review results reviews a delivered report and never completes a card without it.";
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
        if (ReportDeliverable.MissingRequiredReport(task) is { } missing)
            return new(false, null, $"The required report is missing: {missing}.");

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
            StatusSummary: DeliverySummary(task.FolderPath))
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
        if (report.HasHostWaiting)
            return new(false, null, report.Verdicts.First(item => item.IsHostWaiting).Summary, HostWaiting: true);
        var verdict = report.Verdicts.FirstOrDefault(item =>
            string.Equals(item.Aspect, AspectId, StringComparison.OrdinalIgnoreCase));
        if (verdict is null)
            return new(false, null, "The requirement-fit review returned no verdict.");
        // AGT-3797: a dead reviewer is not a verdict on the report.
        if (verdict.IsInfraFailure)
            return new(false, null, "The requirement-fit review failed: " + verdict.Summary);
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
    /// The status the review reads (AGT-3853). The agent's own summary is
    /// <c>results/status.md</c>; the task-root <c>status.md</c> is the runner's
    /// run record (result, case, duration) written after the agent finished,
    /// so it follows under a label that says it is not the deliverable.
    /// </summary>
    internal static string DeliverySummary(string folder)
    {
        var delivery = ReadBounded(Path.Combine(folder, "results", DeliverySummaryFile), DeliverySummaryCharacters);
        var record = ReadBounded(Path.Combine(folder, "status.md"), RunRecordCharacters);
        if (record.Contains(TaskTransitionService.ResultScaffoldMarker, StringComparison.Ordinal)) record = string.Empty;
        var text = new StringBuilder();
        text.AppendLf(delivery.Length > 0
            ? "Delivery summary written by the agent (results/status.md):"
            : "The agent wrote no results/status.md delivery summary; judge the report and results/ inventory.");
        if (delivery.Length > 0) text.AppendLf(delivery.Trim());
        if (record.Length > 0)
        {
            text.AppendLf();
            text.AppendLf("Runner run record (task-root status.md, written by the runner after the run). "
                + "It is not the deliverable and does not summarize the report; do not judge the report by it:");
            text.AppendLf(record.Trim());
        }
        return text.ToString().TrimEnd();
    }

    private static string ReadBounded(string path, int characters)
    {
        try
        {
            if (!File.Exists(path)) return string.Empty;
            var text = BoundedFileRead.ReadHeadText(path, (characters + 1) * 4);
            return text.Length <= characters ? text : text[..characters] + "\n... (truncated)";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SilentCatch.Note(ex, "ReportContentReviewService: status text is best-effort review evidence");
            return string.Empty;
        }
    }

    /// <summary>
    /// The bounded results/ inventory plus the visible text of the primary
    /// report, so the review judges the report and not only its first lines.
    /// The inventory lists newest files first with every subfolder, and leaves
    /// out the excerpt of results/status.md, which the status summary carries.
    /// </summary>
    internal static string RenderEvidence(string folder)
    {
        var inventory = ResultsInventory.RenderForReview(folder, newestFirst: true,
            excerptExclusions: new HashSet<string>(StringComparer.Ordinal) { DeliverySummaryFile });
        var reportPath = ReportDeliverable.ResearchReportFile(folder);
        if (!File.Exists(reportPath)) return inventory;
        string html;
        bool partialRead;
        try
        {
            html = BoundedFileRead.ReadHeadText(reportPath, ReportTextCharacters * 16);
            partialRead = new FileInfo(reportPath).Length > ReportTextCharacters * 16;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SilentCatch.Note(ex, "ReportContentReviewService: report text is best-effort review evidence");
            return inventory;
        }
        var text = ReportText(html);
        if (string.IsNullOrWhiteSpace(text)) return inventory;
        return inventory + "\n\nText of results/report.html (markup removed):\n" + ClipReport(html, text, partialRead);
    }

    /// <summary>
    /// The report text up to <see cref="ReportTextCharacters"/>. A cut report
    /// names its headings past the clip, so the review knows which sections it
    /// did not see instead of reading them as missing.
    /// </summary>
    internal static string ClipReport(string html, string text, bool partialRead = false)
    {
        if (text.Length <= ReportTextCharacters && !partialRead) return text;
        var clipped = text.Length <= ReportTextCharacters ? text : text[..ReportTextCharacters];
        var cut = new List<string>();
        var from = 0;
        foreach (Match heading in Headings.Matches(html))
        {
            var title = ReportText(heading.Groups[2].Value);
            if (title.Length == 0) continue;
            var at = text.IndexOf(title, from, StringComparison.Ordinal);
            if (at < 0) continue;
            from = at + title.Length;
            if (at + title.Length > ReportTextCharacters) cut.Add(title);
        }
        var note = new StringBuilder(clipped).AppendLf().Append(
            $"... (truncated: the review read the first {ReportTextCharacters} characters of the report text");
        note.Append(partialRead ? " from the head of an oversized file)." : ").");
        if (cut.Count > 0)
            note.AppendLf().Append("Sections not shown above, read them in results/report.html before judging them missing: "
                + string.Join("; ", cut.Take(40)) + (cut.Count > 40 ? $"; and {cut.Count - 40} more." : "."));
        return note.ToString();
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
