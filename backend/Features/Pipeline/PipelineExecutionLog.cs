using System.Collections.Concurrent;
using System.Text.Json;

namespace AgentStudio.Pipeline;

/// <summary>
/// Per-job append-mostly writer for <c>pipeline-execution.json</c>. One
/// record per pipeline run, with one entry per step inside it. The file
/// lives next to <c>aspect-*.md</c> in the job folder so consumers
/// (Overview tab, future Pipeline View, audit tooling) read the same
/// folder they already read for the per-aspect MD reports.
///
/// Concurrency model: aspect steps run in parallel and finish in
/// non-deterministic order, so the log holds an in-memory record per
/// (jobFolder, pipelineId) keyed pair under a lock; finishing a step
/// merges into that record and rewrites the file atomically. Failures
/// to write are logged and swallowed - the persistence is observability,
/// not a state-machine input.
/// </summary>
public sealed class PipelineExecutionLog
{
    public const string FileName = "pipeline-execution.json";

    /// <summary>
    /// Cap on how many prior runs we keep in <see cref="PipelineExecutionRecord.PreviousAttempts"/>.
    /// A restarted task rarely needs more than a couple of runs of step history;
    /// the bound keeps <c>pipeline-execution.json</c> from growing without limit
    /// when an operator re-issues the same task many times.
    /// </summary>
    private const int MaxArchivedAttempts = 10;

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ILogger<PipelineExecutionLog> _logger;
    private readonly ConcurrentDictionary<string, object> _locks = new(StringComparer.OrdinalIgnoreCase);
    // Read-model memoization keyed on the file's (mtime, length). Board polls
    // call Read per active card every request; every WriteAtomic here advances
    // the file's mtime, so a changed record misses the cache on the very next
    // Read while an unchanged one is served without a ReadAllText + parse +
    // normalize. The normalized record is pure given the file bytes, so caching
    // it is safe for every consumer.
    private readonly ConcurrentDictionary<string, ReadCacheEntry> _readCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly AsyncLocal<AttemptFence?> _attemptFence = new();

    public PipelineExecutionLog(ILogger<PipelineExecutionLog> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Start a new pipeline run record in the job folder. Overwrites any
    /// existing file - a re-run is a new pipeline execution. When the existing
    /// file is a prior run of the SAME job + pipeline, it is archived into the
    /// new record's <see cref="PipelineExecutionRecord.PreviousAttempts"/> and
    /// <see cref="PipelineExecutionRecord.Attempt"/> increments, so a restart
    /// stays visible rather than silently clobbering the previous run's steps.
    /// Steps are pre-populated from the supplied <see cref="TaskPipeline"/> in
    /// <see cref="PipelineStepStatus.Pending"/> / <see cref="PipelineStepStatus.Planned"/>
    /// state so the file is observable from t=0.
    /// </summary>
    public PipelineExecutionRecord Begin(
        string jobFolderPath,
        TaskPipeline pipeline,
        string project,
        string jobId,
        DateTime? nowUtc = null)
    {
        var lockObj = _locks.GetOrAdd(NormalizeKey(jobFolderPath), _ => new object());
        lock (lockObj)
        {
            var prior = PriorAttemptOf(TryRead(jobFolderPath), pipeline, jobId);
            var record = BuildFresh(pipeline, project, jobId, nowUtc ?? DateTime.UtcNow, prior);
            WriteAtomic(jobFolderPath, record);
            return record;
        }
    }

    /// <summary>
    /// Return the in-flight execution record for this job, or begin a fresh
    /// one when none exists yet or the prior run already completed. Unlike
    /// <see cref="Begin"/> (which always overwrites), this preserves an
    /// in-progress record so a step recorded by an earlier stage of the SAME
    /// run is not clobbered: the core agent run marks its step
    /// <see cref="PipelineStepStatus.Running"/> at spawn and
    /// <see cref="PipelineStepStatus.Passed"/> / <see cref="PipelineStepStatus.Failed"/>
    /// at exit (in <c>ProjectRunner</c>), then the later aspect stage opens
    /// the same file in <c>ReviewDecisionOrchestrator</c>; without this the
    /// aspect stage's <see cref="Begin"/> reset the core step back to
    /// <see cref="PipelineStepStatus.Pending"/> and it never showed as done.
    ///
    /// A prior record that is already
    /// <see cref="PipelineExecutionRecord.IsComplete"/> (or that belongs to a
    /// different pipeline / job) is treated as a finished run, so the next
    /// call starts a new execution. That makes a pipeline re-run / re-issue
    /// observable as a fresh record with a new <see cref="PipelineExecutionRecord.StartedAt"/>
    /// rather than a silent overwrite of the previous run's values.
    /// </summary>
    public PipelineExecutionRecord EnsureRun(
        string jobFolderPath,
        TaskPipeline pipeline,
        string project,
        string jobId,
        DateTime? nowUtc = null)
    {
        var lockObj = _locks.GetOrAdd(NormalizeKey(jobFolderPath), _ => new object());
        lock (lockObj)
        {
            var current = TryRead(jobFolderPath);
            if (current != null
                && !current.IsComplete
                && string.Equals(current.PipelineId, pipeline.Id, StringComparison.OrdinalIgnoreCase)
                && string.Equals(current.JobId, jobId, StringComparison.Ordinal))
            {
                return current;
            }
            var prior = PriorAttemptOf(current, pipeline, jobId);
            var record = BuildFresh(pipeline, project, jobId, nowUtc ?? DateTime.UtcNow, prior);
            WriteAtomic(jobFolderPath, record);
            return record;
        }
    }

    /// <summary>
    /// Return the execution record for a just-starting agent run. This is
    /// stricter than <see cref="EnsureRun"/>: a pre-only record from
    /// orchestrator prep is reused, but any record that already reached the
    /// core or post bracket is archived and a new attempt is opened even when
    /// <see cref="PipelineExecutionRecord.CompletedAt"/> was never stamped.
    /// Re-open / reissue paths can short-circuit before post-processing marks
    /// the previous record complete; the next Ready pickup still must be a new
    /// pipeline run, not a continuation of the old step table.
    /// </summary>
    public PipelineExecutionRecord EnsureAgentRunStart(
        string jobFolderPath,
        TaskPipeline pipeline,
        string project,
        string jobId,
        DateTime? nowUtc = null)
    {
        var lockObj = _locks.GetOrAdd(NormalizeKey(jobFolderPath), _ => new object());
        lock (lockObj)
        {
            var current = TryRead(jobFolderPath);
            if (current != null
                && !current.IsComplete
                && string.Equals(current.PipelineId, pipeline.Id, StringComparison.OrdinalIgnoreCase)
                && string.Equals(current.JobId, jobId, StringComparison.Ordinal)
                && !HasReachedAgentRunBoundary(current, pipeline))
            {
                return current;
            }

            var prior = PriorAttemptOf(current, pipeline, jobId);
            var record = BuildFresh(pipeline, project, jobId, nowUtc ?? DateTime.UtcNow, prior);
            WriteAtomic(jobFolderPath, record);
            return record;
        }
    }

    /// <summary>
    /// Treat an existing on-disk record as the prior attempt of THIS job only
    /// when it belongs to the same job + pipeline. A leftover record from a
    /// different job or pipeline is not a restart of this one, so it is ignored
    /// (the new record begins at attempt 1) rather than archived as history.
    /// </summary>
    private static PipelineExecutionRecord? PriorAttemptOf(
        PipelineExecutionRecord? existing,
        TaskPipeline pipeline,
        string jobId)
    {
        if (existing == null) return null;
        var sameJob = string.Equals(existing.JobId, jobId, StringComparison.Ordinal)
            && string.Equals(existing.PipelineId, pipeline.Id, StringComparison.OrdinalIgnoreCase);
        return sameJob ? existing : null;
    }

    public static bool HasReachedAgentRunBoundary(PipelineExecutionRecord record, TaskPipeline pipeline)
    {
        var coreStepIds = pipeline.Core
            .Select(s => s.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var postStepIds = pipeline.Post
            .Select(s => s.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var step in record.Steps)
        {
            if (coreStepIds.Contains(step.StepId)
                && step.Status is not (PipelineStepStatus.Pending or PipelineStepStatus.Planned))
            {
                return true;
            }

            if (postStepIds.Contains(step.StepId)
                && step.Status is not (PipelineStepStatus.Pending or PipelineStepStatus.Planned))
            {
                return true;
            }
        }

        return false;
    }

    private static PipelineExecutionRecord BuildFresh(
        TaskPipeline pipeline,
        string project,
        string jobId,
        DateTime started,
        PipelineExecutionRecord? prior = null)
    {
        var attempt = 1;
        var previous = new List<PipelineExecutionRecord>();
        if (prior != null)
        {
            attempt = prior.Attempt + 1;
            // Flatten: archive the prior run with its steps but no nested
            // history, then carry forward its own archive, newest first, bounded.
            previous.Add(prior with { PreviousAttempts = [] });
            previous.AddRange(prior.PreviousAttempts);
            if (previous.Count > MaxArchivedAttempts)
            {
                previous = previous.Take(MaxArchivedAttempts).ToList();
            }
        }

        var steps = new List<PipelineStepExecution>();
        foreach (var step in pipeline.AllSteps)
        {
            steps.Add(new PipelineStepExecution
            {
                StepId = step.Id,
                Kind = step.Kind,
                Attempt = attempt,
                Model = step.Model,
                Status = step.Stub ? PipelineStepStatus.Planned : PipelineStepStatus.Pending,
            });
        }

        return new PipelineExecutionRecord
        {
            PipelineId = pipeline.Id,
            PipelineVersion = pipeline.Version,
            Project = project,
            JobId = jobId,
            StartedAt = started,
            Steps = steps,
            Attempt = attempt,
            PreviousAttempts = previous,
        };
    }

    /// <summary>
    /// Fence all step writes and completion stamps in the current async flow to
    /// one pipeline attempt. The scope flows through awaited and parallel
    /// post-steps via <see cref="AsyncLocal{T}"/>. If a rerun opens a newer
    /// attempt while older work is still finishing, those late writes are
    /// ignored instead of contaminating the fresh step table.
    /// </summary>
    public IDisposable EnterAttempt(string jobFolderPath, int attempt)
    {
        var previous = _attemptFence.Value;
        _attemptFence.Value = new AttemptFence(NormalizeKey(jobFolderPath), attempt, previous);
        return new AttemptFenceScope(this, previous);
    }

    /// <summary>
    /// Record one step's outcome (or in-flight start). Merges into the
    /// in-memory record under a per-folder lock, rewrites the file. Safe
    /// to call from concurrent parallel step tasks.
    /// </summary>
    public void RecordStep(string jobFolderPath, PipelineStepExecution stepResult)
    {
        var lockObj = _locks.GetOrAdd(NormalizeKey(jobFolderPath), _ => new object());
        lock (lockObj)
        {
            var current = TryRead(jobFolderPath);
            if (current == null) return;
            if (!WriteBelongsToCurrentAttempt(
                    jobFolderPath, current, stepResult.Attempt, stepResult.StartedAt))
            {
                _logger.LogInformation(
                    "PipelineExecutionLog: ignored stale step update for {Folder}/{StepId}; current attempt is {Attempt}",
                    jobFolderPath, stepResult.StepId, current.Attempt);
                return;
            }

            stepResult = stepResult with { Attempt = current.Attempt };

            var updatedSteps = new List<PipelineStepExecution>(current.Steps.Count);
            var replaced = false;
            foreach (var existing in current.Steps)
            {
                if (!replaced && string.Equals(existing.StepId, stepResult.StepId, StringComparison.OrdinalIgnoreCase))
                {
                    updatedSteps.Add(stepResult);
                    replaced = true;
                }
                else
                {
                    updatedSteps.Add(existing);
                }
            }
            if (!replaced) updatedSteps.Add(stepResult);

            WriteAtomic(jobFolderPath, current with { Steps = updatedSteps });
        }
    }

    /// <summary>
    /// Stamp <see cref="PipelineExecutionRecord.CompletedAt"/> on the
    /// record so the UI can show "ran for X" instead of "still running".
    /// Any ordinary step that the completed bracket never reached is
    /// terminalized as <see cref="PipelineStepStatus.Skipped"/> with an honest
    /// reason; an ordinary step still marked Running becomes Failed. Deferred
    /// operator-triggered steps deliberately remain Pending, and catalogue
    /// stubs remain Planned.
    /// </summary>
    public void Complete(
        string jobFolderPath,
        DateTime? nowUtc = null,
        string? pendingStepReason = null)
    {
        var lockObj = _locks.GetOrAdd(NormalizeKey(jobFolderPath), _ => new object());
        lock (lockObj)
        {
            var current = TryRead(jobFolderPath);
            if (current == null) return;
            if (!WriteBelongsToCurrentAttempt(
                    jobFolderPath, current, stampedAttempt: null, startedAt: null))
            {
                _logger.LogInformation(
                    "PipelineExecutionLog: ignored stale completion for {Folder}; current attempt is {Attempt}",
                    jobFolderPath, current.Attempt);
                return;
            }
            var completed = current with { CompletedAt = nowUtc ?? DateTime.UtcNow };
            WriteAtomic(jobFolderPath, NormalizeCompletedRecord(completed, pendingStepReason));
        }
    }

    /// <summary>
    /// Read the current execution record for the job, or null if no
    /// pipeline run has been recorded yet.
    /// </summary>
    public PipelineExecutionRecord? Read(string jobFolderPath)
    {
        var path = Path.Combine(jobFolderPath, FileName);
        long mtimeTicks;
        long length;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            mtimeTicks = info.LastWriteTimeUtc.Ticks;
            length = info.Length;
        }
        catch
        {
            // Stat failed (path issue, race). Fall back to an uncached read so
            // this path stays exactly as forgiving as it was before.
            var direct = TryRead(jobFolderPath);
            return direct is null ? null : NormalizeCompletedRecord(direct);
        }

        var key = NormalizeKey(jobFolderPath);
        if (_readCache.TryGetValue(key, out var cached)
            && cached.MtimeTicks == mtimeTicks
            && cached.Length == length)
        {
            return cached.Record;
        }

        var record = TryRead(jobFolderPath);
        var normalized = record is null ? null : NormalizeCompletedRecord(record);
        _readCache[key] = new ReadCacheEntry(mtimeTicks, length, normalized);
        return normalized;
    }

    /// <summary>
    /// Returns a terminal step from the current, still-open pipeline attempt.
    /// This is the restart checkpoint used by aspect and post-step executors:
    /// completed evidence is reused, while Pending and interrupted Running
    /// entries are eligible to execute after the replacement host starts.
    /// A completed pipeline is deliberately excluded because its rows belong
    /// to a terminal attempt, not a continuation.
    /// </summary>
    public PipelineStepExecution? ReadRestartCheckpoint(
        string jobFolderPath,
        string stepId)
    {
        var record = Read(jobFolderPath);
        if (record is null || record.IsComplete) return null;
        return record.Steps.FirstOrDefault(step =>
            string.Equals(step.StepId, stepId, StringComparison.OrdinalIgnoreCase)
            && step.CompletedAt.HasValue
            && step.Status is PipelineStepStatus.Passed
                or PipelineStepStatus.Failed
                or PipelineStepStatus.Skipped
                or PipelineStepStatus.NotApplicable);
    }

    /// <summary>
    /// Compatibility projection for records written before <see cref="Complete"/>
    /// terminalized unreached rows. It is deliberately pure: callers receive an
    /// honest read model, while the historical JSON file remains untouched.
    /// Previous attempts are normalized recursively so the Run Switcher cannot
    /// resurrect a stale Pending grade from an older completed attempt.
    /// </summary>
    private static PipelineExecutionRecord NormalizeCompletedRecord(
        PipelineExecutionRecord record,
        string? pendingStepReason = null)
    {
        var previousAttempts = record.PreviousAttempts
            .Select(previous => NormalizeCompletedRecord(previous))
            .ToList();

        if (!record.IsComplete)
        {
            return record with { PreviousAttempts = previousAttempts };
        }

        var pipeline = PipelineCatalogue.Get(record.PipelineId);
        if (pipeline is null)
        {
            return record with { PreviousAttempts = previousAttempts };
        }

        var definitions = pipeline.AllSteps.ToDictionary(
            step => step.Id,
            StringComparer.OrdinalIgnoreCase);
        var reason = string.IsNullOrWhiteSpace(pendingStepReason)
            ? LegacyPendingCompletionReason(record)
            : pendingStepReason.Trim();

        var steps = record.Steps.Select(step =>
        {
            if (step.Status is not (PipelineStepStatus.Pending or PipelineStepStatus.Running)) return step;
            if (!definitions.TryGetValue(step.StepId, out var definition)) return step;
            if (definition.Deferred || definition.Stub) return step;

            if (step.Status == PipelineStepStatus.Running)
            {
                return step with
                {
                    Status = PipelineStepStatus.Failed,
                    CompletedAt = record.CompletedAt,
                    DurationMs = step.StartedAt.HasValue && record.CompletedAt.HasValue
                        ? Math.Max(0L, (long)(record.CompletedAt.Value - step.StartedAt.Value).TotalMilliseconds)
                        : step.DurationMs,
                    Reason = "Pipeline attempt ended while this step was still running.",
                };
            }

            return step with
            {
                Status = PipelineStepStatus.Skipped,
                CompletedAt = record.CompletedAt,
                Reason = string.IsNullOrWhiteSpace(step.Reason) ? reason : step.Reason,
            };
        }).ToList();

        return record with
        {
            Steps = steps,
            PreviousAttempts = previousAttempts,
        };
    }

    private static string LegacyPendingCompletionReason(PipelineExecutionRecord record)
    {
        var failed = record.Steps.FirstOrDefault(step => step.Status == PipelineStepStatus.Failed);
        if (failed is null)
        {
            return "Not run before this pipeline attempt ended.";
        }

        var detail = string.IsNullOrWhiteSpace(failed.Reason)
            ? string.Empty
            : $": {failed.Reason.Trim()}";
        return $"Not run because this pipeline attempt ended after {failed.StepId} failed{detail}.";
    }

    private PipelineExecutionRecord? TryRead(string jobFolderPath)
    {
        var path = Path.Combine(jobFolderPath, FileName);
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<PipelineExecutionRecord>(json, ReadOpts);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PipelineExecutionLog: failed to read {Path}", path);
            return null;
        }
    }

    private void WriteAtomic(string jobFolderPath, PipelineExecutionRecord record)
    {
        try
        {
            if (!Directory.Exists(jobFolderPath))
            {
                Directory.CreateDirectory(jobFolderPath);
            }
            var path = Path.Combine(jobFolderPath, FileName);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(record, WriteOpts));
            // Replace via move so a partial write never leaves a truncated
            // pipeline-execution.json on disk for the next reader.
            if (File.Exists(path))
            {
                File.Replace(tmp, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tmp, path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PipelineExecutionLog: failed to persist record for {Folder}", jobFolderPath);
        }
    }

    private static string NormalizeKey(string folder) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

    private bool WriteBelongsToCurrentAttempt(
        string jobFolderPath,
        PipelineExecutionRecord current,
        int? stampedAttempt,
        DateTime? startedAt)
    {
        var key = NormalizeKey(jobFolderPath);
        for (var fence = _attemptFence.Value; fence != null; fence = fence.Parent)
        {
            if (!string.Equals(fence.FolderKey, key, StringComparison.OrdinalIgnoreCase)) continue;
            return fence.Attempt == current.Attempt;
        }

        if (stampedAttempt.HasValue) return stampedAttempt.Value == current.Attempt;

        // Direct and legacy callers may not have an ambient scope. On a rerun,
        // a step that started before the fresh record can only belong to an
        // archived attempt. Attempt 1 remains fully compatible with older
        // callers and fixtures whose timestamps predate the record itself.
        return current.Attempt <= 1
            || !startedAt.HasValue
            || startedAt.Value >= current.StartedAt;
    }

    private sealed record ReadCacheEntry(long MtimeTicks, long Length, PipelineExecutionRecord? Record);

    private sealed record AttemptFence(string FolderKey, int Attempt, AttemptFence? Parent);

    private sealed class AttemptFenceScope : IDisposable
    {
        private PipelineExecutionLog? _owner;
        private readonly AttemptFence? _previous;

        public AttemptFenceScope(PipelineExecutionLog owner, AttemptFence? previous)
        {
            _owner = owner;
            _previous = previous;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner != null) owner._attemptFence.Value = _previous;
        }
    }
}
