using System.Collections.Concurrent;
using System.Globalization;

namespace AgentStudio.Review;

/// <summary>One delivered result whose Result summary is still owed.</summary>
public sealed record PendingResultSummary(
    string TaskKey,
    string JobId,
    string Reason,
    int Attempts,
    DateTime NextAttemptUtc);

/// <summary>
/// AGT-2850. Application coordination for the remote Result document.
///
/// <para>
/// The delivered result is the durable thing: once artifacts are persisted the
/// runner must be acknowledged so it can tear its worktree down. Summary
/// generation is a retryable step that runs <b>behind</b> that acknowledgement.
/// It is therefore detached from the caller's request token - a runner that
/// stops waiting must never cancel the summary, and a cancelled summary must
/// never turn a delivered run into a lost one (16.09.2026, AGT-2847: two
/// finished Opus runs were discarded because the Haiku one-shot sat in the
/// load throttle until the upload request was aborted).
/// </para>
/// </summary>
public sealed class RemoteResultFinalizationService
{
    public const int DefaultAcknowledgementBudgetSeconds = 20;
    public const int DefaultRetryDelaySeconds = 60;
    public const int DefaultMaxRetries = 5;
    private const int MaxRetryDelaySeconds = 900;

    private readonly SummaryGenerationService _summaries;
    private readonly ITaskScanner _scanner;
    private readonly TimelineLog _timeline;
    private readonly IConfiguration _configuration;
    private readonly ILogger<RemoteResultFinalizationService> _logger;
    private readonly ILoadThrottleGate? _load;
    private readonly WorkspaceArtifactCommitService? _artifactCommits;
    private readonly ConcurrentDictionary<string, PendingEntry> _pending =
        new(StringComparer.OrdinalIgnoreCase);

    public RemoteResultFinalizationService(
        SummaryGenerationService summaries,
        ITaskScanner scanner,
        TimelineLog timeline,
        IConfiguration configuration,
        ILogger<RemoteResultFinalizationService> logger,
        ILoadThrottleGate? load = null,
        WorkspaceArtifactCommitService? artifactCommits = null)
    {
        _summaries = summaries;
        _scanner = scanner;
        _timeline = timeline;
        _configuration = configuration;
        _logger = logger;
        _load = load;
        _artifactCommits = artifactCommits;
    }

    /// <summary>The summaries still owed, for diagnostics and tests.</summary>
    public IReadOnlyList<PendingResultSummary> Pending => _pending.Values
        .Select(entry => new PendingResultSummary(
            entry.TaskKey,
            entry.JobId,
            entry.Reason,
            entry.Attempts,
            entry.NextAttemptUtc))
        .OrderBy(item => item.NextAttemptUtc)
        .ToList();

    /// <summary>
    /// Finalize the Result for a delivered remote run without letting the
    /// summary hold the acknowledgement hostage. Returns the plan the endpoint
    /// turns into its response; it always acknowledges.
    /// </summary>
    public async Task<ResultAcknowledgementPlan> FinalizeForAcknowledgementAsync(
        TaskInfo task,
        TerminalRunOutcome? runOutcome = null)
    {
        var admission = ResultFinalizationPolicy.Admit(
            finalizeRequested: true,
            hostThrottled: _load?.Current.Throttle == true);
        if (admission == ResultSummaryAdmission.Defer)
        {
            return Schedule(
                task,
                ResultSummaryDelivery.Pending,
                ResultSummaryPendingReasons.LoadThrottle,
                inline: null);
        }

        // CancellationToken.None on purpose: the request may be aborted by the
        // runner's delivery timeout, and that must not cancel the summary.
        var generation = Detached(task, runOutcome);
        using var budget = new CancellationTokenSource();
        var settled = await Task
            .WhenAny(
                generation,
                Task.Delay(TimeSpan.FromSeconds(AcknowledgementBudgetSeconds), budget.Token))
            .ConfigureAwait(false);
        budget.Cancel();
        if (!ReferenceEquals(settled, generation))
        {
            return Schedule(
                task,
                ResultSummaryDelivery.Pending,
                ResultSummaryPendingReasons.GenerationInFlight,
                generation);
        }

        var outcome = await generation.ConfigureAwait(false);
        if (outcome.Generated)
        {
            _pending.TryRemove(task.TaskKey, out _);
            _logger.LogInformation(
                "remote-result-finalized taskKey={TaskKey} jobId={JobId} status=generated attempt={Attempt}/{MaxAttempts}",
                task.TaskKey,
                task.Id,
                outcome.Attempt,
                outcome.MaxAttempts);
            return ResultFinalizationPolicy.Plan(ResultSummaryDelivery.Generated, null);
        }

        return Schedule(task, ResultSummaryDelivery.Degraded, outcome.Error, inline: null);
    }

    /// <summary>
    /// Run every summary retry that is due. Public and awaitable so tests can
    /// drive one deterministic pass instead of racing the background loop.
    /// Returns the number of summaries that became real.
    /// </summary>
    public async Task<int> RunDueRetriesAsync(DateTime asOfUtc, CancellationToken ct = default)
    {
        var recovered = 0;
        foreach (var entry in _pending.Values.Where(item => item.NextAttemptUtc <= asOfUtc).ToList())
        {
            ct.ThrowIfCancellationRequested();
            // An inline generation that outran the acknowledgement budget still
            // owns this task's status.md; a second one-shot would race it.
            if (entry.Inline is { IsCompleted: false }) continue;
            if (_load?.Current.Throttle == true)
            {
                // Still saturated. Waiting is not a spent attempt, so the retry
                // budget stays intact for real summary failures.
                Defer(entry, ResultSummaryPendingReasons.LoadThrottle, asOfUtc);
                continue;
            }

            var task = Resolve(entry);
            if (task is null)
            {
                Drop(entry, "task-not-found");
                continue;
            }

            ResultFinalizationOutcome outcome;
            try
            {
                outcome = await _summaries.FinalizeAsync(task, null, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Reschedule(entry, ex.Message, asOfUtc);
                continue;
            }

            if (!outcome.Generated)
            {
                Reschedule(entry, outcome.Error, asOfUtc);
                continue;
            }

            _pending.TryRemove(entry.TaskKey, out _);
            CommitRecoveredSummary(task);
            recovered++;
            _logger.LogInformation(
                "remote-result-summary-recovered taskKey={TaskKey} jobId={JobId} attempts={Attempts} source=retry",
                entry.TaskKey,
                task.Id,
                entry.Attempts + 1);
        }
        return recovered;
    }

    private Task<ResultFinalizationOutcome> Detached(TaskInfo task, TerminalRunOutcome? runOutcome)
        => Task.Run(async () =>
        {
            try
            {
                return await _summaries
                    .FinalizeAsync(task, runOutcome, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "remote-result-summary-threw taskKey={TaskKey} jobId={JobId}",
                    task.TaskKey,
                    task.Id);
                return new ResultFinalizationOutcome(TaskSummaryStatus.Degraded, 0, 0, ex.Message);
            }
        });

    private ResultAcknowledgementPlan Schedule(
        TaskInfo task,
        ResultSummaryDelivery delivery,
        string? reason,
        Task<ResultFinalizationOutcome>? inline)
    {
        var plan = ResultFinalizationPolicy.Plan(
            delivery,
            CredentialRedactor.Redact(ResultFinalizationPolicy.Limit(reason) ?? string.Empty));
        var next = DateTime.UtcNow.AddSeconds(RetryDelaySeconds);
        var known = _pending.TryGetValue(task.TaskKey, out var existing);
        _pending[task.TaskKey] = new PendingEntry(
            task.TaskKey,
            task.Id,
            plan.ResultDocumentStatus ?? string.Empty,
            known ? existing!.Attempts : 0,
            next,
            inline);
        if (inline is not null) ClearWhenInlineSucceeds(task.TaskKey, inline);

        if (!known && plan.TimelineSummary is not null)
        {
            _timeline.Append(
                task.FolderPath,
                TimelineEventKinds.ResultSummaryPending,
                TimelineActors.System,
                plan.TimelineSummary,
                details: new Dictionary<string, string>
                {
                    ["status"] = plan.ResultDocumentStatus ?? string.Empty,
                    ["dueAtUtc"] = next.ToString("O", CultureInfo.InvariantCulture),
                });
        }

        _logger.LogWarning(
            "remote-result-summary-pending taskKey={TaskKey} jobId={JobId} status={Status} dueAtUtc={DueAtUtc}",
            task.TaskKey,
            task.Id,
            plan.ResultDocumentStatus,
            next.ToString("O", CultureInfo.InvariantCulture));
        return plan;
    }

    /// <summary>Push a retry out without consuming its budget.</summary>
    private void Defer(PendingEntry entry, string reason, DateTime asOfUtc)
        => _pending[entry.TaskKey] = entry with
        {
            Reason = reason,
            NextAttemptUtc = asOfUtc.AddSeconds(RetryDelaySeconds),
            Inline = null,
        };

    private void Reschedule(PendingEntry entry, string? reason, DateTime asOfUtc)
    {
        var attempts = entry.Attempts + 1;
        if (attempts >= MaxRetries)
        {
            Drop(entry with { Attempts = attempts }, "retry-budget-exhausted");
            return;
        }

        var delay = Math.Min(
            MaxRetryDelaySeconds,
            RetryDelaySeconds * (int)Math.Pow(2, Math.Min(attempts, 8)));
        _pending[entry.TaskKey] = entry with
        {
            Reason = CredentialRedactor.Redact(
                ResultFinalizationPolicy.Limit(reason) ?? entry.Reason),
            Attempts = attempts,
            NextAttemptUtc = asOfUtc.AddSeconds(delay),
            Inline = null,
        };
        _logger.LogInformation(
            "remote-result-summary-retry-scheduled taskKey={TaskKey} attempt={Attempt}/{MaxRetries} delaySeconds={Delay}",
            entry.TaskKey,
            attempts,
            MaxRetries,
            delay);
    }

    private void Drop(PendingEntry entry, string cause)
    {
        _pending.TryRemove(entry.TaskKey, out _);
        _logger.LogWarning(
            "remote-result-summary-abandoned taskKey={TaskKey} jobId={JobId} attempts={Attempts} cause={Cause} reason={Reason}",
            entry.TaskKey,
            entry.JobId,
            entry.Attempts,
            cause,
            entry.Reason);
    }

    private void ClearWhenInlineSucceeds(string taskKey, Task<ResultFinalizationOutcome> inline)
        => _ = inline.ContinueWith(
            completed =>
            {
                if (completed.Status != TaskStatus.RanToCompletion || !completed.Result.Generated) return;
                if (!_pending.TryRemove(taskKey, out var entry)) return;
                var task = Resolve(entry);
                if (task is not null) CommitRecoveredSummary(task);
                _logger.LogInformation(
                    "remote-result-summary-recovered taskKey={TaskKey} source=inline",
                    taskKey);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    /// <summary>
    /// Re-resolve the task by key: a summary that lands after the run was
    /// acknowledged belongs to the card's <b>current</b> lane folder, not to
    /// the folder it occupied while the runner was still delivering.
    /// </summary>
    private TaskInfo? Resolve(PendingEntry entry)
        => _scanner.ScanAllJobs().FirstOrDefault(task =>
            string.Equals(task.TaskKey, entry.TaskKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(task.Key, entry.TaskKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(task.Id, entry.JobId, StringComparison.OrdinalIgnoreCase));

    private void CommitRecoveredSummary(TaskInfo task)
    {
        if (_artifactCommits is null) return;
        try
        {
            _artifactCommits.TryCommitArtifactUpload(null, task.Id, task.FolderPath, ["status.md"]);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "remote-result-summary-commit-failed taskKey={TaskKey} jobId={JobId}",
                task.TaskKey,
                task.Id);
        }
    }

    private int AcknowledgementBudgetSeconds => Math.Clamp(
        _configuration.GetValue(
            "SummaryGeneration:AcknowledgementBudgetSeconds",
            DefaultAcknowledgementBudgetSeconds),
        1,
        120);

    private int RetryDelaySeconds => Math.Clamp(
        _configuration.GetValue("SummaryGeneration:RetryDelaySeconds", DefaultRetryDelaySeconds),
        5,
        MaxRetryDelaySeconds);

    private int MaxRetries => Math.Clamp(
        _configuration.GetValue("SummaryGeneration:MaxRetries", DefaultMaxRetries),
        1,
        20);

    private sealed record PendingEntry(
        string TaskKey,
        string JobId,
        string Reason,
        int Attempts,
        DateTime NextAttemptUtc,
        Task<ResultFinalizationOutcome>? Inline);
}
