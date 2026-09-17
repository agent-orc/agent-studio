using System.Text.Json;

namespace AgentStudio.Runner;

/// <summary>
/// One-shot startup-recovery scan for the volatile auto-review post-processing
/// queue. The queue (<see cref="AutoReviewPostProcessingQueue"/>) is an in-memory
/// channel, so any entry that was enqueued on the normal
/// <c>3-progress -&gt; 4-auto-review</c> transition but not yet drained is lost
/// when the backend restarts. The affected card then sits in <c>4-auto-review</c>
/// with no post-processing trigger and hangs indefinitely (observed:
/// AGT-2135/AGT-2139 stuck ~2.5h, only cleared by a manual
/// <c>4-auto-review -&gt; 3-progress -&gt; 4-auto-review</c> bounce that re-fires
/// the enqueue).
///
/// <para>
/// On boot - after a short warm-up so the scanner has indexed the lanes - this
/// service enumerates every card currently in <c>4-auto-review</c> whose
/// post-processing has not produced a decision outcome since it last entered the
/// lane, and re-drives it through
/// <see cref="TaskTransitionService.RequeueAutoReviewPostProcessing"/> (the same
/// queue path the live transition uses). It touches <b>only</b> 4-auto-review;
/// 5-human-review / 5e-escalated are never re-enqueued. The scan runs once and
/// exits; it is fully idempotent (the downstream worker re-scans the lane and
/// self-gates on each card's real state), so re-running it can never double a
/// card's post-processing.
/// </para>
/// </summary>
public sealed class AutoReviewPostProcessingRecoveryService : BackgroundService
{
    /// <summary>
    /// Warm-up before the scan so the initial scanner index / cache is populated
    /// after a restart. Set slightly after the <see cref="ReviewDecisionOrchestrator"/>
    /// boot sweep (5 s) so any card that sweep already cleared is out of the lane
    /// before we look; ordering is a mild optimization only - the scan is
    /// idempotent regardless.
    /// </summary>
    internal static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Post-processing outcomes that represent a completed orchestrator decision
    /// for the card's current occupancy of 4-auto-review. The mid-flight
    /// "entered post-processing" marker (<see cref="PostProcessingOutcomes.FindingsAdded"/>
    /// written with <see cref="PipelineCatalogue.GitCommitAttributionStepId"/>) is
    /// deliberately absent so it never masks an unfinished card.
    /// </summary>
    private static readonly HashSet<string> DecisionOutcomes = new(StringComparer.Ordinal)
    {
        PostProcessingOutcomes.PassToHumanReview,
        PostProcessingOutcomes.NeedsFollowUpTask,
        PostProcessingOutcomes.NeedsHumanInput,
        PostProcessingOutcomes.FailedPostProcessing,
    };

    private static readonly JsonSerializerOptions OutcomeReadOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IServiceProvider _services;
    private readonly ILogger<AutoReviewPostProcessingRecoveryService> _logger;

    public AutoReviewPostProcessingRecoveryService(
        IServiceProvider services,
        ILogger<AutoReviewPostProcessingRecoveryService> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Warm-up so a restart does not race the watcher's initial scan and read
        // 4-auto-review before the lane index resolves.
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        try
        {
            using var scope = _services.CreateScope();
            var scanner = scope.ServiceProvider.GetRequiredService<TaskScannerService>();
            var transitions = scope.ServiceProvider.GetRequiredService<TaskTransitionService>();
            var reviewAttempts = scope.ServiceProvider.GetRequiredService<ReviewAttemptTaskLifecycleService>();

            // AGT-2860: before anything is re-enqueued, finish what the killed
            // process had already earned. A card whose review passed and whose
            // integration or lane move died with the request needs the rest of
            // that sequence, not another post-processing pass - and certainly
            // not the new review an operator had to create by hand for AGT-2855.
            var resume = scope.ServiceProvider.GetService<AutoReviewDeliveryResumeService>();
            if (resume is not null)
                await resume.RunOnceAsync("startup-recovery", stoppingToken).ConfigureAwait(false);

            RunRecoveryScan(scanner, transitions, reviewAttempts, _logger);
        }
        catch (OperationCanceledException __ex)
        {
            AgentStudio.Diagnostics.SilentCatch.Note(__ex, "AutoReviewPostProcessingRecoveryService: graceful shutdown before/at the one-shot boot scan.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "post-processing startup-recovery scan failed");
        }
    }

    /// <summary>
    /// Runs the recovery scan against the live scanner snapshot and re-enqueues
    /// every 4-auto-review card whose post-processing is unfinished. Extracted
    /// (and static) so the recovery contract is unit-testable without the
    /// BackgroundService loop. Returns the outcome counts for the log line.
    /// </summary>
    internal static RecoverySummary RunRecoveryScan(
        TaskScannerService scanner,
        TaskTransitionService transitions,
        ILogger logger,
        DateTime? restartedAtUtc = null)
        => RunRecoveryScan(scanner, transitions, reviewAttempts: null, logger, restartedAtUtc);

    internal static RecoverySummary RunRecoveryScan(
        TaskScannerService scanner,
        TaskTransitionService transitions,
        ReviewAttemptTaskLifecycleService? reviewAttempts,
        ILogger logger,
        DateTime? restartedAtUtc = null)
    {
        var recoveryStartedAt = (restartedAtUtc ?? DateTime.UtcNow).ToUniversalTime();
        var reviewAttemptsReissued = reviewAttempts?.SweepSupersededAutoReviewAttempts() ?? 0;
        var candidates = scanner.ScanAllAutomationJobs()
            .Where(j => string.Equals(j.State, TaskStates.AutoReview, StringComparison.Ordinal))
            .ToList();

        var reEnqueued = 0;
        var skipped = 0;
        var failed = 0;

        foreach (var job in candidates)
        {
            var outcomes = ReadOutcomes(job.FolderPath, logger);
            var completedDecision = outcomes
                .Where(record => IsPostProcessingDecision(record) && record.At >= job.EnteredLaneAt)
                .OrderByDescending(record => record.At)
                .FirstOrDefault();
            if (completedDecision is not null)
            {
                if (transitions.ReconcileCompletedAutoReviewPostProcessing(job, completedDecision))
                    skipped++;
                else
                    failed++;
                continue;
            }

            bool accepted;
            try
            {
                accepted = transitions.RequeueAutoReviewPostProcessing(
                    job,
                    "startup-recovery",
                    recoveryStartedAt);
            }
            catch (Exception ex)
            {
                failed++;
                logger.LogWarning(ex,
                    "post-processing startup-recovery: re-enqueue threw for project={Project} job={JobId}",
                    job.ProjectName, job.Id);
                continue;
            }

            if (accepted) reEnqueued++;
            else failed++;
        }

        var summary = new RecoverySummary(candidates.Count, reEnqueued, skipped, failed);
        logger.LogInformation(
            "post-processing startup-recovery: {ReEnqueued} cards re-enqueued and {ReviewAttemptsReissued} review attempts reissued (scanned={Scanned} already-complete={Skipped} failed={Failed})",
            summary.ReEnqueued, reviewAttemptsReissued, summary.Scanned, summary.Skipped, summary.Failed);
        return summary;
    }

    /// <summary>
    /// Pure detection heuristic: is <paramref name="job"/> a 4-auto-review card
    /// whose post-processing never finished? A completed pass leaves a decision
    /// outcome (see <see cref="DecisionOutcomes"/>, or any row stamped with the
    /// <see cref="PipelineCatalogue.OrchestratorDecisionStepId"/> step) dated at or
    /// after the card most recently entered the lane
    /// (<see cref="TaskInfo.EnteredLaneAt"/>, the last-transition timestamp).
    /// Absent such a fresh decision, the in-flight queue entry was lost on restart
    /// and the card must be re-driven. A stale decision from an earlier occupancy
    /// (before a <c>4 -&gt; 3 -&gt; 4</c> reissue) has <c>At &lt; EnteredLaneAt</c>
    /// and correctly does not count as complete.
    /// </summary>
    internal static bool NeedsPostProcessingRecovery(
        TaskInfo job,
        IReadOnlyList<PostProcessingOutcomeRecord> outcomes)
    {
        if (job.Fixture
            || !string.Equals(job.State, TaskStates.AutoReview, StringComparison.Ordinal))
            return false;

        var transitionAt = job.EnteredLaneAt;
        foreach (var record in outcomes)
        {
            if (IsPostProcessingDecision(record) && record.At >= transitionAt)
                return false;
        }
        return true;
    }

    /// <summary>
    /// True when the outcome row represents a completed orchestrator decision -
    /// either a terminal <see cref="DecisionOutcomes"/> value or a row stamped with
    /// the orchestrator-decision step - as opposed to the mid-flight
    /// "entered post-processing" marker.
    /// </summary>
    internal static bool IsPostProcessingDecision(PostProcessingOutcomeRecord record)
        => string.Equals(record.StepId, PipelineCatalogue.OrchestratorDecisionStepId, StringComparison.Ordinal)
           || DecisionOutcomes.Contains(record.Outcome);

    /// <summary>
    /// Reads the append-only <c>post-processing-outcomes.jsonl</c> evidence log for
    /// a job folder. Tolerant of a partially-written trailing line and a missing
    /// file (returns an empty list).
    /// </summary>
    internal static List<PostProcessingOutcomeRecord> ReadOutcomes(string folderPath, ILogger logger)
    {
        var records = new List<PostProcessingOutcomeRecord>();
        var path = Path.Combine(folderPath, PostProcessingOutcomeLog.FileName);
        if (!File.Exists(path)) return records;

        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "post-processing startup-recovery: failed to read outcomes at {Path}", path);
            return records;
        }

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var record = JsonSerializer.Deserialize<PostProcessingOutcomeRecord>(line, OutcomeReadOpts);
                if (record != null) records.Add(record);
            }
            catch (Exception __ex)
            {
                // Tolerate a torn last line from a crash mid-append.
                AgentStudio.Diagnostics.SilentCatch.Note(__ex, "AutoReviewPostProcessingRecoveryService: skipping torn post-processing-outcomes.jsonl row.");
            }
        }
        return records;
    }

    internal sealed record RecoverySummary(int Scanned, int ReEnqueued, int Skipped, int Failed);
}
