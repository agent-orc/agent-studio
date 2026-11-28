using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentStudio.Persistence;

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
    private readonly object _pendingLock = new();
    private bool _restoredPending;
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
        TerminalRunOutcome? runOutcome = null,
        string? resultSha = null)
    {
        resultSha = ResolveResultSha(task, resultSha);
        var previous = FindPending(task);
        // CancellationToken.None on purpose: the request may be aborted by the
        // runner's delivery timeout, and that must not cancel the summary.
        var generation = previous is { Inline: { IsCompleted: false } active } && previous.ResultSha == resultSha
            ? active : Detached(task, runOutcome, resultSha);
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
                runOutcome, resultSha, digest: null, generation);
        }

        var outcome = await generation.ConfigureAwait(false);
        if (outcome.Generated)
        {
            if (previous is not null) Remove(task, previous);
            _logger.LogInformation(
                "remote-result-finalized taskKey={TaskKey} jobId={JobId} status=generated attempt={Attempt}/{MaxAttempts}",
                task.TaskKey,
                task.Id,
                outcome.Attempt,
                outcome.MaxAttempts);
            return ResultFinalizationPolicy.Plan(ResultSummaryDelivery.Generated, null);
        }

        return Schedule(task,
            outcome.Deferred ? ResultSummaryDelivery.Pending : ResultSummaryDelivery.Degraded,
            outcome.Error, runOutcome, resultSha, outcome.EvidenceDigest, inline: null);
    }

    /// <summary>
    /// Run every summary retry that is due. Public and awaitable so tests can
    /// drive one deterministic pass instead of racing the background loop.
    /// Returns the number of summaries that became real.
    /// </summary>
    public async Task<int> RunDueRetriesAsync(DateTime asOfUtc, CancellationToken ct = default)
    {
        if (!_restoredPending && _scanner is AgentStudio.Tasks.TaskScannerService scanner)
            await scanner.WaitForFirstPublicationAsync(ct);
        RestorePending();
        var recovered = 0;
        foreach (var entry in _pending.Values.Where(item => item.NextAttemptUtc <= asOfUtc).ToList())
        {
            ct.ThrowIfCancellationRequested();
            // An inline generation that outran the acknowledgement budget still
            // owns this task's status.md; a second one-shot would race it.
            if (entry.Inline is { IsCompleted: false }) continue;
            var task = Resolve(entry);
            if (task is null)
            {
                Drop(null, entry, "task-not-found");
                continue;
            }

            var currentSha = ResolveResultSha(task, null);
            if (currentSha is not null && entry.ResultSha is not null && currentSha != entry.ResultSha)
            {
                Drop(task, entry, "superseded-result");
                continue;
            }

            ResultFinalizationOutcome outcome;
            try
            {
                outcome = await _summaries.FinalizeAsync(task, entry.RunOutcome, ct, entry.ResultSha,
                    hostThrottled: _load?.Current.Throttle == true).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Reschedule(task, entry, ex.Message, asOfUtc, entry.EvidenceDigest);
                continue;
            }

            if (!outcome.Generated)
            {
                if (ResultFinalizationPolicy.IsUnretryable(outcome.Error))
                    Drop(task, entry, "unretryable-summary");
                else if (outcome.Deferred)
                    Defer(task, entry, outcome.Error, asOfUtc, outcome.EvidenceDigest);
                else
                    Reschedule(task, entry, outcome.Error, asOfUtc, outcome.EvidenceDigest);
                continue;
            }

            if (!Remove(task, entry)) continue;
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

    private Task<ResultFinalizationOutcome> Detached(
        TaskInfo task, TerminalRunOutcome? runOutcome, string? resultSha)
        => Task.Run(async () =>
        {
            try
            {
                return await _summaries
                    .FinalizeAsync(task, runOutcome, CancellationToken.None, resultSha,
                        hostThrottled: _load?.Current.Throttle == true)
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
        TerminalRunOutcome? runOutcome,
        string? resultSha,
        string? digest,
        Task<ResultFinalizationOutcome>? inline)
    {
        if (!Directory.Exists(task.FolderPath))
            task = Resolve(task.TaskKey, task.Id) ?? task;
        var plan = ResultFinalizationPolicy.Plan(
            delivery,
            CredentialRedactor.Redact(ResultFinalizationPolicy.Limit(reason) ?? string.Empty));
        var next = DateTime.UtcNow.AddSeconds(RetryDelaySeconds);
        PendingEntry entry;
        bool known;
        lock (_pendingLock)
        {
            var existing = FindPending(task);
            known = existing is not null && existing.ResultSha == resultSha
                && (digest is null || existing.EvidenceDigest is null || existing.EvidenceDigest == digest);
            if (!plan.ScheduleRetry)
            {
                if (existing is not null && existing.ResultSha == resultSha) Remove(task, existing);
                if (!known && plan.TimelineSummary is not null)
                    RecordPending(task, plan, dueAtUtc: null);
                return plan;
            }

            entry = new PendingEntry(
                task.TaskKey, task.Id, plan.ResultDocumentStatus ?? string.Empty, known ? existing!.Attempts : 0,
                known && existing!.NextAttemptUtc < next ? existing.NextAttemptUtc : next,
                resultSha, digest ?? (known ? existing!.EvidenceDigest : null),
                runOutcome ?? (known ? existing!.RunOutcome : null), inline);
            Save(task, entry);
        }
        if (inline is not null) ClearWhenInlineSucceeds(entry, inline);

        if (!known && plan.TimelineSummary is not null) RecordPending(task, plan, entry.NextAttemptUtc);

        return plan;
    }

    internal async Task FinalizeLocalAsync(TaskInfo task, TerminalRunOutcome? runOutcome)
    {
        var outcome = await _summaries.FinalizeAsync(task, runOutcome);
        if (outcome.Deferred)
            Schedule(task, ResultSummaryDelivery.Pending, outcome.Error, runOutcome, ResolveResultSha(task, null),
                outcome.EvidenceDigest, inline: null);
    }

    private void RecordPending(TaskInfo task, ResultAcknowledgementPlan plan, DateTime? dueAtUtc)
    {
        var details = new Dictionary<string, string> { ["status"] = plan.ResultDocumentStatus ?? string.Empty };
        if (dueAtUtc is { } due) details["dueAtUtc"] = due.ToString("O", CultureInfo.InvariantCulture);
        try
        {
            _timeline.Append(task.FolderPath, TimelineEventKinds.ResultSummaryPending, TimelineActors.System,
                plan.TimelineSummary!, details: details);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not record pending Result timeline event for {TaskKey}", task.TaskKey);
        }
    }

    /// <summary>Push a retry out without consuming its budget.</summary>
    private void Defer(TaskInfo task, PendingEntry entry, string? reason, DateTime asOfUtc, string? digest)
        => Update(task, entry, entry with
        {
            Reason = SafeReason(reason, entry.Reason),
            Attempts = AttemptsFor(entry, digest),
            EvidenceDigest = digest ?? entry.EvidenceDigest,
            NextAttemptUtc = asOfUtc.AddSeconds(RetryDelaySeconds),
            Inline = null,
        });

    private void Reschedule(TaskInfo task, PendingEntry entry, string? reason, DateTime asOfUtc, string? digest)
    {
        var attempts = AttemptsFor(entry, digest) + 1;
        if (attempts >= MaxRetries)
        {
            Drop(task, entry, "retry-budget-exhausted");
            return;
        }

        var delay = Math.Min(MaxRetryDelaySeconds, RetryDelaySeconds * (int)Math.Pow(2, Math.Min(attempts, 8)));
        Update(task, entry, entry with
        {
            Reason = SafeReason(reason, entry.Reason),
            Attempts = attempts,
            EvidenceDigest = digest ?? entry.EvidenceDigest,
            NextAttemptUtc = asOfUtc.AddSeconds(delay),
            Inline = null,
        });
    }

    private static int AttemptsFor(PendingEntry entry, string? digest)
        => digest is null || entry.EvidenceDigest is null || entry.EvidenceDigest == digest ? entry.Attempts : 0;

    private static string SafeReason(string? reason, string fallback)
        => CredentialRedactor.Redact(ResultFinalizationPolicy.Limit(reason) ?? fallback);

    private void Drop(TaskInfo? task, PendingEntry entry, string cause)
    {
        if (!Remove(task, entry)) return;
        _logger.LogWarning(
            "remote-result-summary-abandoned taskKey={TaskKey} jobId={JobId} attempts={Attempts} cause={Cause} reason={Reason}",
            entry.TaskKey, entry.JobId, entry.Attempts, cause, entry.Reason);
    }

    private void ClearWhenInlineSucceeds(PendingEntry entry, Task<ResultFinalizationOutcome> inline)
        => _ = inline.ContinueWith(
            completed =>
            {
                if (completed.Status != TaskStatus.RanToCompletion || !completed.Result.Generated) return;
                var task = Resolve(entry);
                if (Remove(task, entry) && task is not null) CommitRecoveredSummary(task);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private static string? ResolveResultSha(TaskInfo task, string? sha)
    {
        sha ??= AgentStudio.Pipeline.ReviewSubjectStore.Read(task.FolderPath)?.ResultSha;
        return AgentStudio.Pipeline.ReviewSubjectStore.IsValidResultSha(sha) ? sha!.ToLowerInvariant() : null;
    }

    internal const string PendingFileName = "result-summary-pending.json";

    private static string PendingPath(TaskInfo task) => Path.Combine(TaskPaths.LogsDir(task.FolderPath), PendingFileName);

    private PendingEntry? ReadPending(TaskInfo task)
    {
        try
        {
            var path = PendingPath(task);
            if (!File.Exists(path)) return null;
            var entry = JsonSerializer.Deserialize<PendingEntry>(File.ReadAllText(path));
            return entry is not null && entry.TaskKey == task.TaskKey ? entry : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "Could not restore pending Result for {TaskKey}", task.TaskKey);
            return null;
        }
    }

    private PendingEntry? FindPending(TaskInfo task)
    {
        lock (_pendingLock)
        {
            if (_pending.TryGetValue(task.TaskKey, out var active)) return active;
            var saved = ReadPending(task);
            if (saved is not null) _pending.TryAdd(task.TaskKey, saved);
            return saved;
        }
    }

    private void RestorePending()
    {
        lock (_pendingLock)
        {
            if (_restoredPending) return;
            foreach (var task in _scanner.ScanAllJobs())
                if (ReadPending(task) is { } entry) _pending.TryAdd(task.TaskKey, entry);
            _restoredPending = true;
        }
    }

    private void Update(TaskInfo task, PendingEntry expected, PendingEntry updated)
    {
        lock (_pendingLock)
            if (_pending.TryGetValue(expected.TaskKey, out var active) && ReferenceEquals(active, expected))
                Save(task, updated);
    }

    private void Save(TaskInfo task, PendingEntry entry)
    {
        _pending[entry.TaskKey] = entry;
        try
        {
            new AtomicJsonFileWriter().ReplaceExisting(PendingPath(task), JsonSerializer.Serialize(entry));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not persist pending Result for {TaskKey}", task.TaskKey);
        }
    }

    private bool Remove(TaskInfo? task, PendingEntry expected)
    {
        lock (_pendingLock)
        {
            if (!_pending.TryRemove(new KeyValuePair<string, PendingEntry>(expected.TaskKey, expected)))
                return false;
            try
            {
                if (task is not null) File.Delete(PendingPath(task));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not clear pending Result for {TaskKey}", expected.TaskKey);
            }
            return true;
        }
    }

    /// <summary>
    /// Re-resolve the task by key: a summary that lands after the run was
    /// acknowledged belongs to the card's <b>current</b> lane folder, not to
    /// the folder it occupied while the runner was still delivering.
    /// </summary>
    private TaskInfo? Resolve(PendingEntry entry)
        => Resolve(entry.TaskKey, entry.JobId);

    private TaskInfo? Resolve(string taskKey, string jobId)
        => _scanner.ScanAllJobs().FirstOrDefault(task =>
            string.Equals(task.TaskKey, taskKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(task.Key, taskKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(task.Id, jobId, StringComparison.OrdinalIgnoreCase));

    private void CommitRecoveredSummary(TaskInfo task)
    {
        if (_artifactCommits is null) return;
        try
        {
            _artifactCommits.TryCommitArtifactUpload(null, task.Id, task.FolderPath, ["status.md", SummaryAssessmentStore.FileName]);
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
        string? ResultSha,
        string? EvidenceDigest,
        TerminalRunOutcome? RunOutcome,
        [property: JsonIgnore] Task<ResultFinalizationOutcome>? Inline = null);
}
