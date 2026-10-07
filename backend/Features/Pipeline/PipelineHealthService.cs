using System.Collections.Concurrent;
using AgentStudio.Runner;
using AgentStudio.Tasks;

namespace AgentStudio.Pipeline;

/// <summary>
/// Code-owned alarm thresholds for the pipeline sensors. These are operational
/// conventions, not project settings: every project and runner sees the same
/// definition of a hanging gate, a repeated failure, and a stalled lane.
/// </summary>
public static class PipelineHealthConventions
{
    public static readonly TimeSpan GateCompletionBudget = TimeSpan.FromMinutes(30);
    public const int RepeatedFingerprintCount = 3;
    public static readonly TimeSpan DrainWindow = TimeSpan.FromHours(1);
    public static readonly TimeSpan FilledQueueMinimumAge = TimeSpan.FromMinutes(15);
    public const int FilledQueueMinimumTasks = 2;
    public static readonly TimeSpan SensorInterval = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan AlertCooldown = TimeSpan.FromHours(1);
    public const string EvidenceFlushStalledKind = "evidence-flush-stalled";
}

public sealed record PipelineGateContext(
    string GateRunId,
    string Project,
    string WatchPath,
    string JobId,
    DateTime AcquiredAtUtc);

public sealed record PipelineGateCompletion(
    string GateRunId,
    string Project,
    string WatchPath,
    string JobId,
    DateTime CompletedAtUtc,
    string? FailureFingerprint);

public sealed record PipelineHealthAlert(
    string Kind,
    string Severity,
    string Summary,
    string Detail,
    DateTime DetectedAtUtc,
    string? JobId = null,
    string? Repository = null);

public sealed record PipelineActiveGateHealth(
    string GateRunId,
    string Project,
    string JobId,
    DateTime AcquiredAtUtc,
    int ElapsedMinutes,
    int BudgetMinutes,
    bool IsHanging);

public sealed record PipelineFingerprintHealth(
    string Fingerprint,
    int ConsecutiveFailures,
    int Threshold,
    IReadOnlyList<string> Projects,
    bool IsSystemic);

public sealed record PipelineLaneDrainHealth(
    string Lane,
    int QueueCount,
    double CompletedPerHour,
    DateTime? OldestQueuedAtUtc,
    bool IsStalled);

public sealed record PipelineHealthSnapshot(
    string Project,
    DateTime CapturedAtUtc,
    string Status,
    PipelineActiveGateHealth? ActiveGate,
    PipelineFingerprintHealth? Fingerprint,
    IReadOnlyList<PipelineLaneDrainHealth> Lanes,
    IReadOnlyList<PipelineHealthAlert> Alerts);

/// <summary>
/// Pure, deterministic state machine shared by live sensing and log replay.
/// It never changes a task, gate, or lane.
/// </summary>
public sealed class PipelineHealthDetector
{
    private readonly object _sync = new();
    private readonly Dictionary<string, PipelineGateContext> _activeGates =
        new(StringComparer.Ordinal);
    private readonly List<PipelineGateCompletion> _consecutiveFailures = [];

    public void GateAcquired(PipelineGateContext gate)
    {
        lock (_sync) _activeGates[gate.GateRunId] = gate;
    }

    public PipelineHealthAlert? GateCompleted(PipelineGateCompletion completion)
    {
        lock (_sync)
        {
            _activeGates.Remove(completion.GateRunId);
            if (string.IsNullOrWhiteSpace(completion.FailureFingerprint))
            {
                _consecutiveFailures.Clear();
                return null;
            }

            if (_consecutiveFailures.Count > 0
                && !string.Equals(
                    _consecutiveFailures[^1].FailureFingerprint,
                    completion.FailureFingerprint,
                    StringComparison.Ordinal))
            {
                _consecutiveFailures.Clear();
            }

            var repeatedCardIndex = _consecutiveFailures.FindIndex(item =>
                string.Equals(item.JobId, completion.JobId, StringComparison.OrdinalIgnoreCase));
            if (repeatedCardIndex >= 0)
            {
                // Environmental retry attempts for one card are still one card
                // failure. Do not let a single noisy task manufacture the
                // cross-card systemic signal by consuming its retry budget,
                // even when another card's gate ran between those attempts.
                _consecutiveFailures[repeatedCardIndex] = completion;
                return null;
            }

            _consecutiveFailures.Add(completion);
            if (_consecutiveFailures.Count != PipelineHealthConventions.RepeatedFingerprintCount)
                return null;

            var projects = _consecutiveFailures
                .Select(item => item.Project)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var jobs = string.Join(", ", _consecutiveFailures.Select(item => item.JobId));
            return new PipelineHealthAlert(
                "systemic-gate-failure",
                "high",
                "Systemic gate problem detected",
                $"Failure fingerprint {completion.FailureFingerprint} occurred in " +
                $"{_consecutiveFailures.Count} consecutive gates across {projects.Length} project(s). " +
                $"Tasks: {jobs}.",
                completion.CompletedAtUtc,
                completion.JobId);
        }
    }

    public IReadOnlyList<(PipelineGateContext Gate, PipelineHealthAlert Alert)> DetectHangingGates(
        DateTime nowUtc)
    {
        lock (_sync)
        {
            return _activeGates.Values
                .Where(gate => nowUtc - gate.AcquiredAtUtc >= PipelineHealthConventions.GateCompletionBudget)
                .Select(gate => (
                    gate,
                    new PipelineHealthAlert(
                        "gate-hanging",
                        "high",
                        $"Build/test gate hanging for {ElapsedMinutes(nowUtc, gate.AcquiredAtUtc)} min",
                        $"Gate {gate.GateRunId} was acquired at {gate.AcquiredAtUtc:O} and has no completed event. " +
                        $"The visibility budget is {PipelineHealthConventions.GateCompletionBudget.TotalMinutes:F0} min.",
                        nowUtc,
                        gate.JobId)))
                .ToArray();
        }
    }

    public PipelineFingerprintHealth? FingerprintHealth()
    {
        lock (_sync)
        {
            if (_consecutiveFailures.Count == 0) return null;
            var fingerprint = _consecutiveFailures[^1].FailureFingerprint!;
            var projects = _consecutiveFailures
                .Select(item => item.Project)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new PipelineFingerprintHealth(
                fingerprint,
                _consecutiveFailures.Count,
                PipelineHealthConventions.RepeatedFingerprintCount,
                projects,
                _consecutiveFailures.Count >= PipelineHealthConventions.RepeatedFingerprintCount);
        }
    }

    public PipelineActiveGateHealth? ActiveGateHealth(string project, DateTime nowUtc)
    {
        lock (_sync)
        {
            var gate = _activeGates.Values
                .Where(item => string.Equals(item.Project, project, StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => item.AcquiredAtUtc)
                .FirstOrDefault();
            if (gate is null) return null;
            var elapsed = nowUtc - gate.AcquiredAtUtc;
            return new PipelineActiveGateHealth(
                gate.GateRunId,
                gate.Project,
                gate.JobId,
                gate.AcquiredAtUtc,
                ElapsedMinutes(nowUtc, gate.AcquiredAtUtc),
                (int)PipelineHealthConventions.GateCompletionBudget.TotalMinutes,
                elapsed >= PipelineHealthConventions.GateCompletionBudget);
        }
    }

    public static PipelineLaneDrainHealth MeasureLane(
        string lane,
        IReadOnlyList<DateTime> queuedAtUtc,
        int completedInWindow,
        DateTime nowUtc)
    {
        var oldest = queuedAtUtc
            .Select(value => (DateTime?)value.ToUniversalTime())
            .OrderBy(value => value)
            .FirstOrDefault();
        var rate = completedInWindow / PipelineHealthConventions.DrainWindow.TotalHours;
        var stalled = queuedAtUtc.Count >= PipelineHealthConventions.FilledQueueMinimumTasks
            && rate <= 0
            && oldest is not null
            && nowUtc - oldest.Value >= PipelineHealthConventions.FilledQueueMinimumAge;
        return new PipelineLaneDrainHealth(lane, queuedAtUtc.Count, rate, oldest, stalled);
    }

    private static int ElapsedMinutes(DateTime nowUtc, DateTime startedAtUtc)
        => Math.Max(0, (int)Math.Floor((nowUtc - startedAtUtc).TotalMinutes));
}

public interface IPipelineHealthSensor
{
    void GateAcquired(PipelineGateContext gate);
    void GateCompleted(PipelineGateCompletion completion);
    PipelineHealthSnapshot? Snapshot(string project, DateTime? nowUtc = null);
}

/// <summary>
/// Visibility-only pipeline sensor. It observes gate lifecycle, the
/// append-only lane ledger and stalled workspace evidence flushes, emits
/// deduplicated feed alarms, and exposes a compact read model. It never cancels
/// gates or moves tasks.
/// </summary>
public sealed class PipelineHealthService : BackgroundService, IPipelineHealthSensor, IEvidenceFlushAlarm
{
    private static readonly string[] ObservedLanes =
    [
        TaskStates.Ready,
        TaskStates.Progress,
        TaskStates.AutoReview,
        TaskStates.HumanReview,
    ];

    private readonly PipelineHealthDetector _detector;
    private readonly TaskScannerService _scanner;
    private readonly TimelineLog _timeline;
    private readonly OrchestratorLog _orchestratorLog;
    private readonly ILogger<PipelineHealthService> _logger;
    private readonly ConcurrentDictionary<string, DateTime> _lastAlertAt =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PipelineHealthAlert> _currentAlerts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly AutoReviewQueueStagnationWatchdog? _reviewQueue;

    /// <summary>Alert identity of the review-claim stall, separate from the file-based lane drain.</summary>
    internal const string ReviewClaimStallIdentity = "review-claims:" + TaskStates.AutoReview;

    public PipelineHealthService(
        PipelineHealthDetector detector,
        TaskScannerService scanner,
        TimelineLog timeline,
        OrchestratorLog orchestratorLog,
        ILogger<PipelineHealthService> logger,
        AutoReviewQueueStagnationWatchdog? reviewQueue = null)
    {
        _detector = detector;
        _scanner = scanner;
        _timeline = timeline;
        _orchestratorLog = orchestratorLog;
        _logger = logger;
        _reviewQueue = reviewQueue;
    }

    public void GateAcquired(PipelineGateContext gate) => _detector.GateAcquired(gate);

    public void GateCompleted(PipelineGateCompletion completion)
    {
        var alert = _detector.GateCompleted(completion);
        if (alert is not null)
        {
            EmitAlert(
                completion.Project,
                completion.WatchPath,
                alert,
                $"fingerprint:{completion.FailureFingerprint}");
        }
    }

    public PipelineHealthSnapshot? Snapshot(string project, DateTime? nowUtc = null)
    {
        var entry = _scanner.GetWatchPaths().FirstOrDefault(item =>
            string.Equals(item.Name, project, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return null;

        var now = (nowUtc ?? DateTime.UtcNow).ToUniversalTime();
        var lanes = BuildLaneDrainHealth(project, entry.Path, now);
        var activeGate = _detector.ActiveGateHealth(project, now);
        var fingerprint = _detector.FingerprintHealth();
        var alerts = _currentAlerts
            .Where(pair => pair.Key.StartsWith(project + "\0", StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Value)
            .OrderByDescending(alert => alert.DetectedAtUtc)
            .ToArray();
        var unhealthy = activeGate?.IsHanging == true
            || fingerprint?.IsSystemic == true
            || lanes.Any(lane => lane.IsStalled)
            || alerts.Any(alert => alert.Kind == PipelineHealthConventions.EvidenceFlushStalledKind)
            || _currentAlerts.ContainsKey(project + "\0" + ReviewClaimStallIdentity);
        return new PipelineHealthSnapshot(
            project,
            now,
            unhealthy ? "alarm" : activeGate is null ? "healthy" : "running",
            activeGate,
            fingerprint,
            lanes,
            alerts);
    }

    /// <summary>
    /// Raises <c>evidence-flush-stalled</c> for every project whose watch path
    /// lives in the stalled workspace repository (AGT-3000). The alert stays in
    /// the snapshot, and keeps the status at <c>alarm</c>, until the next
    /// successful flush reports recovery.
    /// </summary>
    public void EvidenceFlushStalled(EvidenceFlushStall stall)
    {
        var minutes = Math.Max(0, (int)Math.Floor(stall.FailingFor.TotalMinutes));
        var alert = new PipelineHealthAlert(
            PipelineHealthConventions.EvidenceFlushStalledKind,
            "high",
            $"Workspace evidence flush failing for {minutes} min",
            $"Repository {stall.GitRoot}: {stall.ConsecutiveFailures} consecutive evidence flushes failed since " +
            $"{stall.FirstFailedAtUtc:O}. No evidence commit reaches the workspace repository until this clears. " +
            $"Last error: {stall.Error}",
            stall.LastFailedAtUtc,
            Repository: stall.GitRoot);
        var projects = ProjectsIn(stall.GitRoot);
        if (projects.Count == 0)
        {
            _logger.LogWarning(
                "pipeline_health_alarm kind={Kind} project=n/a repo={Repo} summary={Summary} error={Error}",
                alert.Kind, stall.GitRoot, alert.Summary, stall.Error);
            return;
        }
        foreach (var (project, watchPath) in projects)
            EmitAlert(project, watchPath, alert, EvidenceFlushIdentity(stall.GitRoot));
    }

    public void EvidenceFlushRecovered(string gitRoot, DateTime recoveredAtUtc)
    {
        var suffix = "\0" + EvidenceFlushIdentity(gitRoot);
        foreach (var key in _currentAlerts.Keys.Where(key => key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            _currentAlerts.TryRemove(key, out _);
            // Re-arm: a later stall of the same repository alarms immediately.
            _lastAlertAt.TryRemove(key, out _);
        }
        _logger.LogInformation(
            "pipeline_health_alarm_cleared kind={Kind} repo={Repo} at={At:O}",
            PipelineHealthConventions.EvidenceFlushStalledKind, gitRoot, recoveredAtUtc);
    }

    private static string EvidenceFlushIdentity(string gitRoot) => "evidence-flush:" + gitRoot;

    private IReadOnlyList<(string Project, string WatchPath)> ProjectsIn(string gitRoot)
    {
        var root = Path.GetFullPath(gitRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return _scanner.GetWatchPaths()
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Path))
            .Where(entry =>
            {
                var path = Path.GetFullPath(entry.Path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return WatchPathComparison.PathsEqual(path, root)
                    || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            })
            .Select(entry => (entry.Name, entry.Path))
            .ToArray();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await EvaluateAsync(DateTime.UtcNow, stoppingToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(PipelineHealthConventions.SensorInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await EvaluateAsync(DateTime.UtcNow, stoppingToken).ConfigureAwait(false);
        }
    }

    internal Task EvaluateAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        foreach (var (gate, alert) in _detector.DetectHangingGates(nowUtc))
        {
            EmitAlert(gate.Project, gate.WatchPath, alert, $"gate:{gate.GateRunId}");
        }

        foreach (var entry in _scanner.GetWatchPaths())
        {
            ct.ThrowIfCancellationRequested();
            foreach (var lane in BuildLaneDrainHealth(entry.Name, entry.Path, nowUtc).Where(item => item.IsStalled))
            {
                var oldest = lane.OldestQueuedAtUtc is null
                    ? "unknown"
                    : $"{Math.Max(0, (int)(nowUtc - lane.OldestQueuedAtUtc.Value).TotalMinutes)} min";
                var alert = new PipelineHealthAlert(
                    "lane-drain-stalled",
                    "high",
                    $"{lane.Lane} drain rate is 0/h with {lane.QueueCount} queued",
                    $"No task left {lane.Lane} in the last hour while the queue remained filled. " +
                    $"Oldest queued age: {oldest}.",
                    nowUtc);
                EmitAlert(entry.Name, entry.Path, alert, $"lane:{lane.Lane}");
            }
        }

        EvaluateReviewClaims(nowUtc);
        return Task.CompletedTask;
    }

    /// <summary>
    /// AGT-2987: the file-based lane drain above counts cards leaving Auto
    /// Review, which other exits (supersede, human moves) can satisfy while no
    /// ReviewAttempt is ever claimed. This raises the same alarm kind from the
    /// claim-side verdict of <see cref="AutoReviewQueueStagnationWatchdog"/>,
    /// naming the oldest pending attempt and, when known, why it is unclaimable.
    /// </summary>
    private void EvaluateReviewClaims(DateTime nowUtc)
    {
        if (_reviewQueue is null) return;
        var queue = _reviewQueue.Refresh(nowUtc);
        if (!queue.ReviewClaimStagnant)
        {
            foreach (var key in _currentAlerts.Keys.Where(key =>
                         key.EndsWith("\0" + ReviewClaimStallIdentity, StringComparison.OrdinalIgnoreCase)))
                _currentAlerts.TryRemove(key, out _);
            return;
        }

        var owner = queue.OldestPendingTaskKey is null
            ? null
            : _scanner.ScanAllAutomationJobsWithArchive().FirstOrDefault(task =>
                string.Equals(task.Id, queue.OldestPendingTaskKey, StringComparison.OrdinalIgnoreCase));
        var project = owner?.ProjectName ?? "review-plane";

        var lastClaim = queue.LastReviewClaimAt is { } claimedAt ? claimedAt.ToString("O") : "never";
        var waitingSince = Later(queue.OldestPendingAttemptCreatedAt, queue.LastReviewClaimAt);
        var waitingMinutes = waitingSince is null
            ? queue.StagnantThresholdMinutes
            : Math.Max(0, (int)(nowUtc - waitingSince.Value).TotalMinutes);
        var cause = queue.UnclaimableReason is null
            ? "No executor has reported it unclaimable; check that a review executor is registered and polling."
            : $"Unclaimable: {queue.UnclaimableReason}, missing "
              + $"{string.Join(", ", queue.UnclaimableMissingCapabilities ?? [])}.";
        var alert = new PipelineHealthAlert(
            "lane-drain-stalled",
            "high",
            $"{TaskStates.AutoReview} has {queue.PendingReviewAttempts} pending review attempt(s) "
            + $"and no claim for {waitingMinutes} min",
            $"Oldest pending attempt {queue.OldestPendingAttemptId} ({queue.OldestPendingTaskKey}) was created "
            + $"{queue.OldestPendingAttemptCreatedAt:O}. Last review claim: {lastClaim}. {cause}",
            nowUtc,
            queue.OldestPendingTaskKey);
        EmitAlert(project, owner?.WatchPath, alert, ReviewClaimStallIdentity);
    }

    private static DateTime? Later(DateTime? a, DateTime? b)
        => a is null ? b : b is null ? a : a > b ? a : b;

    internal IReadOnlyList<PipelineLaneDrainHealth> BuildLaneDrainHealth(
        string project,
        string watchPath,
        DateTime nowUtc)
    {
        var since = nowUtc - PipelineHealthConventions.DrainWindow;
        var tasks = _scanner.ScanAllAutomationJobsWithArchive()
            .Where(task => string.Equals(task.ProjectName, project, StringComparison.OrdinalIgnoreCase)
                || WatchPathComparison.PathsEqual(task.WatchPath, watchPath))
            .ToArray();
        var exits = ObservedLanes.ToDictionary(lane => lane, _ => 0, StringComparer.OrdinalIgnoreCase);

        foreach (var task in tasks)
        {
            IReadOnlyList<TimelineEvent> events;
            try
            {
                events = _timeline.ReadAll(task.FolderPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex,
                    "pipeline-health-timeline-read-failed project={Project} task={TaskId}",
                    project,
                    task.Id);
                continue;
            }

            foreach (var evt in events)
            {
                if (evt.Ts < since || evt.Ts > nowUtc
                    || !string.Equals(evt.Kind, TimelineEventKinds.LaneChanged, StringComparison.Ordinal)
                    || evt.Details is null
                    || !evt.Details.TryGetValue("from", out var from)
                    || !exits.ContainsKey(from))
                {
                    continue;
                }
                exits[from]++;
            }
        }

        return ObservedLanes.Select(lane =>
        {
            var queued = tasks
                .Where(task => string.Equals(task.State, lane, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var queuedAt = queued
                .Where(task => task.EnteredLaneAt != default)
                .Select(task => task.EnteredLaneAt.ToUniversalTime())
                .ToArray();
            return PipelineHealthDetector.MeasureLane(lane, queuedAt, exits[lane], nowUtc);
        }).ToArray();
    }

    private void EmitAlert(
        string project,
        string? watchPath,
        PipelineHealthAlert alert,
        string identity)
    {
        var key = project + "\0" + identity;
        var now = alert.DetectedAtUtc;
        _currentAlerts[key] = alert;
        if (_lastAlertAt.TryGetValue(key, out var prior)
            && now - prior < PipelineHealthConventions.AlertCooldown)
        {
            return;
        }
        _lastAlertAt[key] = now;
        if (watchPath is not null)
        {
            _orchestratorLog.Append(watchPath, new OrchestratorLogEntry
            {
                Ts = now,
                Kind = OrchestratorLogKinds.Alert,
                Topic = OrchestratorLogTopics.PipelineHealth,
                Summary = alert.Summary,
                Reasoning = alert.Detail,
                JobId = alert.JobId,
            });
        }
        _logger.LogWarning(
            "pipeline_health_alarm kind={Kind} project={Project} job_id={JobId} summary={Summary} detail={Detail}",
            alert.Kind,
            project,
            alert.JobId ?? "n/a",
            alert.Summary,
            alert.Detail);
    }
}
