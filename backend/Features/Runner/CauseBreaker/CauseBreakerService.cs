using System.Text.Json;
using AgentStudio.Pipeline;
using AgentStudio.Tasks;

namespace AgentStudio.Runner;

public static class CauseBreakerStates
{
    public const string Closed = "closed";
    public const string Open = "open";
}

/// <summary>One counted failure of one attempt.</summary>
public sealed record CauseBreakerObservation(
    string TaskKey,
    string Project,
    string AttemptId,
    DateTime At,
    IReadOnlyList<string> EvidencePointers);

/// <summary>
/// One card parked behind the cause card. <see cref="Released"/> marks a card
/// whose release already happened while its marker could not be deleted: it
/// is no longer held, and a later close only retries the delete.
/// </summary>
public sealed record CauseBreakerWaitingCard(
    string TaskKey, DateTime Since, string? FolderPath = null, string? WatchPath = null,
    string? Project = null, bool Released = false);

/// <summary>The fleet-wide breaker of one cause fingerprint.</summary>
public sealed record CauseBreakerRecord
{
    public string Fingerprint { get; init; } = "";
    public string FailureClass { get; init; } = "";
    public string Toolchain { get; init; } = "";
    public string NormalizedText { get; init; } = "";
    public string State { get; init; } = CauseBreakerStates.Closed;
    public DateTime FirstSeenAt { get; init; }
    public DateTime? OpenedAt { get; init; }
    public string? OpenReason { get; init; }
    public DateTime? ClosedAt { get; init; }
    public string? CloseReason { get; init; }
    public int Opens { get; init; }
    public string? CauseKey { get; init; }
    public string? CauseTaskId { get; init; }
    public string? CauseWatchPath { get; init; }
    public string? ProbeTaskKey { get; init; }
    public string? ProbeAttemptId { get; init; }
    public List<CauseBreakerObservation> Observations { get; init; } = [];
    public List<CauseBreakerWaitingCard> Waiting { get; init; } = [];
    /// <summary>
    /// Distinct card-level failure texts merged under this fingerprint. More
    /// than one is normal (each aspect wraps the message differently); many is
    /// the signal that the fingerprint is too coarse.
    /// </summary>
    public List<string> Variants { get; init; } = [];

    public bool IsOpen => State == CauseBreakerStates.Open;
}

/// <summary>What the review report path has to do with one infrastructure failure.</summary>
public sealed record CauseBreakerOutcome(
    CauseBreakerDecision Decision,
    CauseBreakerRecord Breaker,
    FailureInterventionResult? Intervention)
{
    /// <summary>True when the reporting card is parked and must neither retry nor escalate.</summary>
    public bool Parked => Intervention is not null;
}

/// <summary>Mints a freshly planned review attempt for a released card.</summary>
public interface ICauseWaitRelease
{
    string? Release(TaskInfo task, string deliveryKey);
}

/// <summary>
/// Fleet-wide cause breaker (AGT-W57 §3 A, §4, §5 E1). The JSON file under
/// <c>.metadata/</c> is the cross-project authority; each parked card also
/// carries a <see cref="CauseWaitMarker"/> so the board and the retry scheduler
/// see the wait without consulting this service.
/// </summary>
public sealed class CauseBreakerService
{
    public const string RelativePath = ".metadata/cause-breakers.json";
    private const int MaxObservations = 200;
    private const int MaxVariants = 20;
    private const int VariantLength = 240;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string? _path;
    private readonly AgentStudio.Projects.ProjectSettingsService _settings;
    private readonly FailureInterventionService _interventions;
    private readonly TaskScannerService _scanner;
    private readonly AttemptAuthorityService _authority;
    private readonly TimelineLog _timeline;
    private readonly ICauseWaitRelease _release;
    private readonly ICauseWaitMarkerStore _markers;
    private readonly ILogger<CauseBreakerService> _logger;
    private readonly TimeProvider _time;
    private List<CauseBreakerRecord>? _records;
    private readonly HashSet<string> _checkedPendingReviewIds = new(StringComparer.OrdinalIgnoreCase);
    private long _checkedPendingSettingsVersion = -1;

    public CauseBreakerService(
        IConfiguration configuration,
        AgentStudio.Projects.ProjectSettingsService settings,
        FailureInterventionService interventions,
        TaskScannerService scanner,
        AttemptAuthorityService authority,
        TimelineLog timeline,
        ICauseWaitRelease release,
        ICauseWaitMarkerStore markers,
        ILogger<CauseBreakerService> logger,
        TimeProvider? time = null)
    {
        _markers = markers;
        var root = configuration["TaskRepository"];
        _path = string.IsNullOrWhiteSpace(root) ? null : Path.Combine(root, RelativePath);
        _settings = settings;
        _interventions = interventions;
        _scanner = scanner;
        _authority = authority;
        _timeline = timeline;
        _release = release;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public bool IsEnabled(string projectName) => _settings.Get(projectName).CauseBreakerEnabled;

    public IReadOnlyList<CauseBreakerRecord> List(bool openOnly = false)
    {
        lock (_gate)
        {
            return Records()
                .Where(record => !openOnly || record.IsOpen)
                .OrderByDescending(record => record.OpenedAt ?? record.FirstSeenAt)
                .ToArray();
        }
    }

    /// <summary>
    /// Counts one review infrastructure failure and decides whether the card
    /// retries, opens the breaker, or waits on the already open cause card.
    /// </summary>
    public CauseBreakerOutcome Observe(
        TaskInfo task,
        string taskKey,
        string attemptId,
        CauseFingerprint fingerprint,
        FailureCommandEvidence evidence,
        string? rawText)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow().UtcDateTime;
            var records = Records();
            var index = records.FindIndex(item => Same(item.Fingerprint, fingerprint.Value));
            var record = index >= 0
                ? records[index]
                : new CauseBreakerRecord
                {
                    Fingerprint = fingerprint.Value,
                    FailureClass = fingerprint.FailureClass,
                    Toolchain = fingerprint.Toolchain,
                    NormalizedText = fingerprint.NormalizedText,
                    FirstSeenAt = now,
                };

            var variant = Variant(rawText);
            var variants = record.Variants.Contains(variant, StringComparer.Ordinal) || record.Variants.Count >= MaxVariants
                ? record.Variants
                : [.. record.Variants, variant];
            var observations = record.Observations.Any(item => Same(item.AttemptId, attemptId))
                ? record.Observations
                : record.Observations
                    .Append(new CauseBreakerObservation(taskKey, task.ProjectName, attemptId, now,
                        evidence.EvidencePointers ?? []))
                    .TakeLast(MaxObservations)
                    .ToList();
            record = record with { Observations = observations, Variants = variants };

            var thresholds = CauseBreakerPolicy.From(_settings.Get(task.ProjectName));
            var decision = CauseBreakerPolicy.Decide(
                record.IsOpen,
                observations.Select(item => new CauseBreakerCount(item.TaskKey, item.At)).ToArray(),
                thresholds,
                now);
            LogObservation(taskKey, attemptId, fingerprint, decision, record);

            FailureInterventionResult? intervention = null;
            if (decision.Action == CauseBreakerAction.Open)
            {
                _checkedPendingReviewIds.Clear();
                var classification = Classification(fingerprint, decision);
                var cutoff = now - thresholds.Window;
                var causeEvidence = evidence with
                {
                    EvidencePointers = observations
                        .Where(item => item.At >= cutoff)
                        .SelectMany(item => item.EvidencePointers)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                };
                intervention = _interventions.RaiseCause(task, causeEvidence, classification);
                record = record with
                {
                    State = CauseBreakerStates.Open,
                    OpenedAt = now,
                    OpenReason = decision.Reason,
                    ClosedAt = null,
                    CloseReason = null,
                    Opens = record.Opens + 1,
                    CauseKey = intervention.Intervention.FollowUpKey,
                    CauseTaskId = intervention.Intervention.FollowUpTaskId,
                    CauseWatchPath = task.WatchPath,
                    ProbeTaskKey = null,
                    ProbeAttemptId = null,
                    Waiting = [],
                };
                record = Park(record, task, taskKey, decision, now);

                // Every card already counted in the window is affected by the
                // same cause: attach it to the one cause card and stop its
                // pending retry instead of letting it burn the next attempt.
                foreach (var otherKey in observations
                             .Where(item => item.At >= cutoff && !Same(item.TaskKey, taskKey))
                             .Select(item => item.TaskKey)
                             .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var other = V1ReviewPlaneEndpoints.FindTask(_scanner, otherKey);
                    if (other is null) continue;
                    _interventions.RaiseCause(other, evidence with
                    {
                        EvidencePointers = observations
                            .Where(item => Same(item.TaskKey, otherKey))
                            .SelectMany(item => item.EvidencePointers)
                            .ToArray(),
                    }, classification, record.CauseWatchPath);
                    if (string.Equals(other.State, TaskStates.AutoReview, StringComparison.OrdinalIgnoreCase))
                        record = Park(record, other, otherKey, decision, now);
                }
                _logger.LogWarning(
                    "cause-breaker-opened fingerprint={Fingerprint} class={FailureClass} toolchain={Toolchain} cause={CauseKey} "
                    + "attempts={Attempts} cards={Cards} attemptThreshold={AttemptThreshold} cardThreshold={CardThreshold} "
                    + "waiting={Waiting} variants={Variants}",
                    record.Fingerprint, record.FailureClass, record.Toolchain, record.CauseKey,
                    decision.Attempts, decision.Cards, thresholds.Attempts, thresholds.Cards,
                    string.Join(",", record.Waiting.Select(item => item.TaskKey)), record.Variants.Count);
            }
            else if (decision.Action == CauseBreakerAction.Wait)
            {
                intervention = _interventions.RaiseCause(
                    task, evidence, Classification(fingerprint, decision), record.CauseWatchPath);
                var isProbeCard = Same(record.ProbeTaskKey, taskKey);
                if (isProbeCard && Same(record.ProbeAttemptId, attemptId))
                {
                    _logger.LogWarning(
                        "cause-breaker-probe-red fingerprint={Fingerprint} cause={CauseKey} probe={TaskKey} attempt={AttemptId}",
                        record.Fingerprint, record.CauseKey, taskKey, attemptId);
                    record = record with { ProbeTaskKey = null, ProbeAttemptId = null };
                }
                // An older report from the probe card must not revoke the
                // explicitly released successor or overwrite its probe marker.
                if (!isProbeCard || record.ProbeTaskKey is null)
                    record = Park(record, task, taskKey, decision, now);
            }

            Upsert(records, record);
            Persist(records);
            return new CauseBreakerOutcome(decision, record, intervention);
        }
    }

    /// <summary>
    /// Claim-time gate. Parks every pending review whose plan would run into an
    /// open cause before it spends an attempt, and returns the task keys the
    /// claim must skip. Without it, every card reaching review after the
    /// breaker opened would still burn one attempt on the known cause.
    /// </summary>
    public IReadOnlySet<string> HoldPendingReviews()
    {
        lock (_gate)
        {
            if (!Records().Any(item => item.IsOpen))
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // A pending attempt checked while its project was disabled must be
            // reconsidered when an operator enables the breaker. Settings
            // mutations are rare; retain the fast cache on ordinary polls.
            var settingsVersion = _settings.Version;
            if (_checkedPendingSettingsVersion != settingsVersion)
            {
                _checkedPendingReviewIds.Clear();
                _checkedPendingSettingsVersion = settingsVersion;
            }
            var held = HeldLocked();
            var pending = _authority.ListPendingReviewAttempts();
            _checkedPendingReviewIds.IntersectWith(pending.Select(review => review.AttemptId));
            var uncheckedReviews = pending.Where(review =>
                !held.Contains(review.TaskKey) && !_checkedPendingReviewIds.Contains(review.AttemptId)).ToArray();
            if (uncheckedReviews.Length > 0)
            {
                var tasks = _scanner.ScanAllAutomationJobs()
                    .SelectMany(task => new[] { task.TaskKey, task.Key, task.Id }
                        .Where(key => !string.IsNullOrWhiteSpace(key))
                        .Select(key => (Key: key!, Task: task)))
                    .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First().Task, StringComparer.OrdinalIgnoreCase);
                foreach (var review in uncheckedReviews)
                {
                    if (!tasks.TryGetValue(review.TaskKey, out var task)) continue;
                    _checkedPendingReviewIds.Add(review.AttemptId);
                    HoldLocked(task, review.TaskKey, review.Subject.Plan);
                }
            }
            return HeldLocked();
        }
    }

    /// <summary>
    /// Parks one card before its review runs when an open breaker's cause
    /// applies to it. A model route is shared by the whole fleet, so a plan
    /// that calls the broken route is held in any project; any other toolchain
    /// (a preparation tool, a host binary) is held within the projects that
    /// already observed the cause.
    /// </summary>
    public bool HoldBeforeClaim(TaskInfo task, string taskKey, AgentStudio.TaskServer.Contracts.ReviewPlanDto? plan)
    {
        lock (_gate)
        {
            return HoldLocked(task, taskKey, plan);
        }
    }

    private bool HoldLocked(TaskInfo task, string taskKey, AgentStudio.TaskServer.Contracts.ReviewPlanDto? plan)
    {
        if (!string.Equals(task.State, TaskStates.AutoReview, StringComparison.OrdinalIgnoreCase)) return false;
        if (!IsEnabled(task.ProjectName)) return false;
        var records = Records();
        if (records.Any(item => item.IsOpen
                && (Same(item.ProbeTaskKey, taskKey)
                    || item.Waiting.Any(card => !card.Released && Same(card.TaskKey, taskKey)))))
            return !records.Any(item => Same(item.ProbeTaskKey, taskKey));

        var toolchains = (plan?.Commands ?? [])
            .Select(command => CauseFingerprintPolicy.Toolchain(command, null))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // A breaker with a close reason is resolved and only finishing its
        // releases; it must not park new work behind the fixed cause.
        var record = records.FirstOrDefault(item => item.IsOpen && item.CloseReason is null
            && Applies(item, task, toolchains));
        if (record is null) return false;

        var now = _time.GetUtcNow().UtcDateTime;
        var thresholds = CauseBreakerPolicy.From(_settings.Get(task.ProjectName));
        var decision = new CauseBreakerDecision(CauseBreakerAction.Wait, 0, 0, thresholds,
            $"held before claim: {record.Toolchain} has an open cause");
        _interventions.RaiseCause(task,
            new FailureCommandEvidence(record.FailureClass, "ReviewInfra", EvidencePointers: []),
            new FailureClassificationResult(FailureDomains.Infrastructure, record.FailureClass, record.Fingerprint,
                $"[{record.Toolchain}] {record.NormalizedText}", true, $"Cause breaker: {decision.Reason}"),
            record.CauseWatchPath);
        var parked = Park(record, task, taskKey, decision, now);
        _logger.LogInformation(
            "cause-breaker-held task={TaskKey} fingerprint={Fingerprint} cause={CauseKey} toolchain={Toolchain}",
            taskKey, record.Fingerprint, record.CauseKey, record.Toolchain);
        Upsert(records, parked);
        Persist(records);
        return true;
    }

    private static bool Applies(CauseBreakerRecord record, TaskInfo task, IReadOnlySet<string> planToolchains)
    {
        if (record.Toolchain.StartsWith("agent:", StringComparison.OrdinalIgnoreCase))
            return planToolchains.Contains(record.Toolchain);
        if (string.Equals(record.Toolchain, CauseFingerprintPolicy.NoToolchain, StringComparison.OrdinalIgnoreCase))
            return false;
        return record.Observations.Any(item => string.Equals(item.Project, task.ProjectName, StringComparison.OrdinalIgnoreCase));
    }

    private HashSet<string> HeldLocked()
        => Records()
            .Where(item => item.IsOpen)
            .SelectMany(item => item.Waiting
                .Where(card => !card.Released)
                .Select(card => card.TaskKey)
                .Where(key => !Same(key, item.ProbeTaskKey)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Only the successor attempt explicitly released as a probe can prove
    /// recovery. An older in-flight review of the same waiting card cannot.
    /// </summary>
    public CauseBreakerRecord? ObserveGreen(string taskKey, string attemptId)
    {
        lock (_gate)
        {
            var records = Records();
            var record = records.FirstOrDefault(item => item.IsOpen
                && Same(item.ProbeTaskKey, taskKey)
                && Same(item.ProbeAttemptId, attemptId));
            if (record is null) return null;
            if (CauseBreakerPolicy.Close(null, probeGreen: true) != CauseBreakerCloseReason.ProbeGreen) return null;
            _logger.LogInformation(
                "cause-breaker-probe-green fingerprint={Fingerprint} cause={CauseKey} probe={TaskKey} attempt={AttemptId}",
                record.Fingerprint, record.CauseKey, taskKey, attemptId);
            var closed = CloseLocked(records, record, CauseBreakerCloseReason.ProbeGreen, exceptTaskKey: taskKey);
            Persist(records);
            return closed;
        }
    }

    /// <summary>
    /// A terminal red probe proves no recovery, regardless of whether its
    /// outcome was classified as infrastructure or product failure. Release
    /// its reservation so an operator can request another probe. Match the
    /// exact attempt to keep late reports from an older review harmless.
    /// </summary>
    public bool ObserveProbeFailure(TaskInfo task, string taskKey, string attemptId, string outcome)
    {
        lock (_gate)
        {
            var records = Records();
            var record = records.FirstOrDefault(item => item.IsOpen
                && Same(item.ProbeTaskKey, taskKey)
                && Same(item.ProbeAttemptId, attemptId));
            if (record is null) return false;

            record = record with { ProbeTaskKey = null, ProbeAttemptId = null };
            var now = _time.GetUtcNow().UtcDateTime;
            var thresholds = CauseBreakerPolicy.From(_settings.Get(task.ProjectName));
            record = Park(record, task, taskKey,
                new CauseBreakerDecision(CauseBreakerAction.Wait, record.Observations.Count,
                    record.Waiting.Count, thresholds, $"probe ended with {outcome}"), now);
            Upsert(records, record);
            Persist(records);
            _logger.LogWarning(
                "cause-breaker-probe-red fingerprint={Fingerprint} cause={CauseKey} probe={TaskKey} attempt={AttemptId} outcome={Outcome}",
                record.Fingerprint, record.CauseKey, taskKey, attemptId, outcome);
            return true;
        }
    }

    /// <summary>
    /// Closes every open breaker whose cause card has been integrated. Called
    /// by <see cref="CauseBreakerHostedService"/>; returns the number closed.
    /// </summary>
    public int Sweep()
    {
        lock (_gate)
        {
            var records = Records();
            var closed = 0;
            foreach (var record in records.Where(item => item.IsOpen).ToArray())
            {
                // The mutation service returns a folder slug, while the card's
                // stable key survives a lane move. Resolve by key first.
                var cause = string.IsNullOrWhiteSpace(record.CauseKey)
                    ? null
                    : _scanner.FindJob(record.CauseKey!, record.CauseWatchPath);
                cause ??= string.IsNullOrWhiteSpace(record.CauseTaskId)
                    ? null
                    : _scanner.FindJob(record.CauseTaskId!, record.CauseWatchPath);
                var reason = record.CloseReason switch
                {
                    "probe-green" => CauseBreakerCloseReason.ProbeGreen,
                    "cause-integrated" => CauseBreakerCloseReason.CauseIntegrated,
                    _ => CauseBreakerPolicy.Close(cause?.State, probeGreen: false),
                };
                if (reason == CauseBreakerCloseReason.None) continue;
                var updated = CloseLocked(records, record, reason, exceptTaskKey: null);
                if (!updated.IsOpen) closed++;
            }
            var reconciled = ReconcileMarkersLocked(records);
            if (records.Any(item => item.IsOpen && item.CloseReason is not null) || closed > 0 || reconciled)
                Persist(records);
            return closed;
        }
    }

    /// <summary>
    /// The fleet store decides who is held; the per-card marker is what the
    /// board and the retry scheduler read. A failed marker write, a failed
    /// delete or a crash between the two can split them. Repair both
    /// directions: rewrite a held card's missing or stale marker, and adopt or
    /// release a waiting marker that no open breaker accounts for, which would
    /// otherwise withhold that card's retries with nothing left to release it.
    /// </summary>
    private bool ReconcileMarkersLocked(List<CauseBreakerRecord> records)
    {
        var changed = false;
        foreach (var record in records.Where(item => item.IsOpen).ToArray())
        {
            foreach (var waiting in record.Waiting.Where(card => !card.Released))
            {
                var folder = ResolveWaitingTask(record, waiting)?.FolderPath ?? waiting.FolderPath;
                if (folder is null || !Directory.Exists(folder)) continue;
                var probe = Same(waiting.TaskKey, record.ProbeTaskKey);
                var marker = _markers.TryRead(folder);
                if (marker is not null && marker.Probe == probe
                    && Same(marker.Fingerprint, record.Fingerprint) && Same(marker.CauseKey, record.CauseKey))
                    continue;
                if (_markers.Write(folder, Marker(record, waiting.Since, probe)))
                    _logger.LogWarning(
                        "cause-breaker-marker-repaired fingerprint={Fingerprint} cause={CauseKey} task={TaskKey} probe={Probe}",
                        record.Fingerprint, record.CauseKey, waiting.TaskKey, probe);
            }
        }

        var accounted = records
            .Where(item => item.IsOpen)
            .SelectMany(item => item.Waiting.Select(card => card.TaskKey))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var task in _scanner.ScanAllAutomationJobs().Where(item => item.CauseWait is not null))
        {
            var keys = new[] { task.Key, task.TaskKey, task.Id }
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Select(key => key!)
                .ToArray();
            if (keys.Length == 0 || keys.Any(accounted.Contains)) continue;
            // The board index can lag a release; decide on the marker on disk.
            var marker = _markers.TryRead(task.FolderPath);
            if (marker is null || marker.Probe) continue;
            var taskKey = AuthorityKey(keys);
            var owner = records.FirstOrDefault(item => item.IsOpen && item.CloseReason is null
                && Same(item.Fingerprint, marker.Fingerprint));
            if (owner is not null)
            {
                Upsert(records, owner with { Waiting = [.. owner.Waiting,
                    new CauseBreakerWaitingCard(taskKey, marker.Since, task.FolderPath, task.WatchPath, task.ProjectName)] });
                accounted.Add(taskKey);
                changed = true;
                _logger.LogWarning(
                    "cause-breaker-marker-adopted fingerprint={Fingerprint} cause={CauseKey} task={TaskKey}",
                    owner.Fingerprint, owner.CauseKey, taskKey);
                continue;
            }

            var needsSuccessor = string.Equals(task.State, TaskStates.AutoReview, StringComparison.OrdinalIgnoreCase);
            var released = needsSuccessor && !string.IsNullOrWhiteSpace(
                _release.Release(task, $"cause-breaker-release:{marker.Fingerprint}:orphan"));
            if ((needsSuccessor && !released) || !_markers.Clear(task.FolderPath))
            {
                _logger.LogWarning(
                    "cause-breaker-release-deferred fingerprint={Fingerprint} cause={CauseKey} task={TaskKey} orphan=true",
                    marker.Fingerprint, marker.CauseKey, taskKey);
                continue;
            }
            AppendReleased(task.FolderPath, marker.CauseKey, marker.Fingerprint, "orphaned-marker", released);
            _logger.LogWarning(
                "cause-breaker-marker-orphan-released fingerprint={Fingerprint} cause={CauseKey} task={TaskKey} released={Released}",
                marker.Fingerprint, marker.CauseKey, taskKey, released);
        }
        return changed;
    }

    /// <summary>
    /// Releases the longest-waiting card of an open breaker as a probe. A green
    /// review then closes the breaker; a red one parks the card again without
    /// opening a second cause card.
    /// </summary>
    public CauseBreakerRecord? RequestProbe(string fingerprint)
    {
        lock (_gate)
        {
            var records = Records();
            var record = records.FirstOrDefault(item => item.IsOpen && Same(item.Fingerprint, fingerprint));
            if (record is null || record.ProbeTaskKey is not null || record.CloseReason is not null)
                return record;
            foreach (var waiting in record.Waiting.OrderBy(item => item.Since))
            {
                if (waiting.Released) continue;
                var task = ResolveWaitingTask(record, waiting);
                if (task is null) continue;
                // A probe whose marker still reads "waiting" would be shown
                // and withheld as parked while it runs; try the next card. A
                // failed restore below is repaired by the next sweep.
                var marker = _markers.TryRead(task.FolderPath) ?? Marker(record, waiting.Since, probe: false);
                if (!_markers.Write(task.FolderPath, marker with { Probe = true })) continue;
                var probeAttemptId = _release.Release(task, $"cause-breaker-probe:{record.Fingerprint}:{record.Opens}");
                if (string.IsNullOrWhiteSpace(probeAttemptId))
                {
                    _markers.Write(task.FolderPath, marker with { Probe = false });
                    continue;
                }
                record = record with { ProbeTaskKey = waiting.TaskKey, ProbeAttemptId = probeAttemptId };
                _logger.LogInformation(
                    "cause-breaker-probe fingerprint={Fingerprint} cause={CauseKey} probe={TaskKey} attempt={AttemptId}",
                    record.Fingerprint, record.CauseKey, waiting.TaskKey, probeAttemptId);
                break;
            }
            Upsert(records, record);
            Persist(records);
            return record;
        }
    }

    private CauseBreakerRecord Park(
        CauseBreakerRecord record,
        TaskInfo task,
        string taskKey,
        CauseBreakerDecision decision,
        DateTime now)
    {
        var marker = _markers.TryRead(task.FolderPath);
        var alreadyWaiting = marker is { Probe: false }
            && Same(marker.Fingerprint, record.Fingerprint)
            && Same(marker.CauseKey, record.CauseKey);
        // The fleet store below is what holds the card at claim time, so a
        // failed marker write still parks it; the sweep rewrites the marker.
        if (!alreadyWaiting && !_markers.Write(task.FolderPath, Marker(record, now, probe: false)))
            _logger.LogWarning(
                "cause-breaker-marker-write-failed fingerprint={Fingerprint} cause={CauseKey} task={TaskKey}",
                record.Fingerprint, record.CauseKey, taskKey);

        // A pending bounded-backoff retry would spend another attempt on a
        // cause that is now owned by the cause card.
        var current = _authority.GetTaskProjection(taskKey).CurrentReviewAttempt;
        if (current is not null && _authority.HasScheduledReviewInfrastructureRetry(current.AttemptId))
            _authority.ClearScheduledReviewInfrastructureRetry(current.AttemptId);

        // A card whose release is only waiting on a marker delete is parked
        // again from scratch: its next close must plan a fresh attempt.
        var waitingCards = record.Waiting.Where(item => !(item.Released && Same(item.TaskKey, taskKey))).ToList();
        record = record with { Waiting = waitingCards };
        if (alreadyWaiting)
            return record.Waiting.Any(item => Same(item.TaskKey, taskKey))
                ? record
                : record with { Waiting = [.. record.Waiting,
                    new CauseBreakerWaitingCard(taskKey, marker!.Since, task.FolderPath, task.WatchPath,
                        task.ProjectName)] };

        _timeline.Append(task.FolderPath, TimelineEventKinds.CauseBreakerWaiting, TimelineActors.System,
            $"Waiting for {record.CauseKey}: the cause breaker for {record.FailureClass} is open ({decision.Reason})",
            details: new Dictionary<string, string>
            {
                ["causeKey"] = record.CauseKey ?? string.Empty,
                ["fingerprint"] = record.Fingerprint,
                ["failureClass"] = record.FailureClass,
                ["toolchain"] = record.Toolchain,
                ["attempts"] = decision.Attempts.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["cards"] = decision.Cards.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["attemptThreshold"] = decision.Thresholds.Attempts.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["cardThreshold"] = decision.Thresholds.Cards.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });

        if (record.Waiting.Any(item => Same(item.TaskKey, taskKey))) return record;
        return record with { Waiting = [.. record.Waiting,
            new CauseBreakerWaitingCard(taskKey, now, task.FolderPath, task.WatchPath,
                task.ProjectName)] };
    }

    private CauseBreakerRecord CloseLocked(
        List<CauseBreakerRecord> records,
        CauseBreakerRecord record,
        CauseBreakerCloseReason reason,
        string? exceptTaskKey)
    {
        _checkedPendingReviewIds.Clear();
        var now = _time.GetUtcNow().UtcDateTime;
        var closeReason = reason == CauseBreakerCloseReason.CauseIntegrated ? "cause-integrated" : "probe-green";
        var stillWaiting = new List<CauseBreakerWaitingCard>();
        foreach (var waiting in record.Waiting)
        {
            var task = ResolveWaitingTask(record, waiting);
            var folderPath = task?.FolderPath ?? waiting.FolderPath;
            if (task is null || folderPath is null || !Directory.Exists(folderPath))
            {
                stillWaiting.Add(waiting);
                continue;
            }
            // The probe card already has its green review; every other card
            // gets one fresh attempt planned from current settings, once.
            var needsSuccessor = !waiting.Released
                && !Same(waiting.TaskKey, exceptTaskKey)
                && string.Equals(task.State, TaskStates.AutoReview, StringComparison.OrdinalIgnoreCase);
            var released = needsSuccessor && !string.IsNullOrWhiteSpace(
                _release.Release(task, $"cause-breaker-release:{record.Fingerprint}:{record.Opens}"));
            if (needsSuccessor && !released)
            {
                // Keep the marker and the durable wait. The hosted sweep will
                // retry; clearing either here strands a terminal review.
                stillWaiting.Add(waiting);
                _logger.LogWarning(
                    "cause-breaker-release-deferred fingerprint={Fingerprint} cause={CauseKey} task={TaskKey}",
                    record.Fingerprint, record.CauseKey, waiting.TaskKey);
                continue;
            }
            if (!_markers.Clear(folderPath))
            {
                // A marker left behind keeps the card withheld by the retry
                // scheduler and shown as waiting. Keep the durable wait so the
                // sweep retries the delete; Released stops the retry from
                // planning a second successor and lets the claim admit the
                // one just planned.
                stillWaiting.Add(waiting with { Released = true });
                _logger.LogWarning(
                    "cause-breaker-release-deferred fingerprint={Fingerprint} cause={CauseKey} task={TaskKey} reason=marker-clear-failed",
                    record.Fingerprint, record.CauseKey, waiting.TaskKey);
                continue;
            }
            AppendReleased(folderPath, record.CauseKey, record.Fingerprint, closeReason,
                released || waiting.Released);
        }
        var closed = record with
        {
            State = stillWaiting.Count == 0 ? CauseBreakerStates.Closed : CauseBreakerStates.Open,
            ClosedAt = stillWaiting.Count == 0 ? now : null,
            CloseReason = closeReason,
            ProbeTaskKey = null,
            ProbeAttemptId = null,
            Observations = stillWaiting.Count == 0 ? [] : record.Observations,
            Waiting = stillWaiting,
        };
        if (stillWaiting.Count == 0)
            _logger.LogInformation(
                "cause-breaker-closed fingerprint={Fingerprint} cause={CauseKey} reason={Reason} released={Released}",
                record.Fingerprint, record.CauseKey, closeReason,
                string.Join(",", record.Waiting.Select(item => item.TaskKey)));
        Upsert(records, closed);
        return closed;
    }

    private TaskInfo? ResolveWaitingTask(CauseBreakerRecord record, CauseBreakerWaitingCard waiting)
    {
        var task = V1ReviewPlaneEndpoints.FindTask(_scanner, waiting.TaskKey)
            ?? _scanner.FindJob(waiting.TaskKey, waiting.WatchPath);
        if (task is not null) return task;
        if (waiting.FolderPath is null || waiting.WatchPath is null
            || !Directory.Exists(waiting.FolderPath)) return null;

        // The stored folder is the durable wait identity. A recent intervention
        // can invalidate the board index before its refreshed snapshot is
        // published, so resolve that known folder directly for probe/release.
        var project = waiting.Project
            ?? record.Observations.LastOrDefault(item => Same(item.TaskKey, waiting.TaskKey))?.Project;
        return _scanner.ScanJobFolder(waiting.FolderPath,
            new WatchPathEntry { Name = project ?? string.Empty, Path = waiting.WatchPath },
            TaskStates.AutoReview);
    }

    /// <summary>
    /// The claim gate matches the review authority's task key, which may be
    /// the stable key or the scanner identity depending on when the attempt
    /// was minted.
    /// </summary>
    private string AuthorityKey(IReadOnlyList<string> keys)
        => keys.Select(key => _authority.GetTaskProjection(key).CurrentReviewAttempt?.TaskKey)
            .FirstOrDefault(key => !string.IsNullOrWhiteSpace(key))
            ?? keys[0];

    private static CauseWaitRecord Marker(CauseBreakerRecord record, DateTime since, bool probe) => new()
    {
        CauseKey = record.CauseKey ?? string.Empty,
        Fingerprint = record.Fingerprint,
        FailureClass = record.FailureClass,
        Since = since,
        Reason = $"waiting for {record.CauseKey}: {record.FailureClass} on {record.Toolchain}",
        Probe = probe,
    };

    private void AppendReleased(string folderPath, string? causeKey, string fingerprint, string closeReason, bool released)
        => _timeline.Append(folderPath, TimelineEventKinds.CauseBreakerReleased, TimelineActors.System,
            $"{causeKey} resolved the cause ({closeReason}); "
            + (released ? "a fresh review attempt was planned." : "no new review attempt was needed."),
            details: new Dictionary<string, string>
            {
                ["causeKey"] = causeKey ?? string.Empty,
                ["fingerprint"] = fingerprint,
                ["closeReason"] = closeReason,
                ["released"] = released.ToString().ToLowerInvariant(),
            });

    private void LogObservation(
        string taskKey,
        string attemptId,
        CauseFingerprint fingerprint,
        CauseBreakerDecision decision,
        CauseBreakerRecord record)
    {
        // Both watch items of the Dossier are logged on every observation: the
        // fingerprint parts (to spot a fingerprint that merges distinct faults)
        // and the threshold numbers (to spot a threshold that halts the fleet).
        _logger.LogInformation(
            "cause-breaker-observed task={TaskKey} attempt={AttemptId} fingerprint={Fingerprint} class={FailureClass} "
            + "toolchain={Toolchain} text={Text} action={Action} attempts={Attempts}/{AttemptThreshold} "
            + "cards={Cards}/{CardThreshold} windowHours={WindowHours} variants={Variants}",
            taskKey, attemptId, fingerprint.Value, fingerprint.FailureClass, fingerprint.Toolchain,
            fingerprint.NormalizedText, decision.Action, decision.Attempts, decision.Thresholds.Attempts,
            decision.Cards, decision.Thresholds.Cards, decision.Thresholds.Window.TotalHours, record.Variants.Count);
    }

    private static FailureClassificationResult Classification(CauseFingerprint fingerprint, CauseBreakerDecision decision)
        => new(
            FailureDomains.Infrastructure,
            fingerprint.FailureClass,
            fingerprint.Value,
            $"[{fingerprint.Toolchain}] {fingerprint.NormalizedText}",
            Deterministic: true,
            $"Cause breaker: {decision.Reason}");

    private static string Variant(string? rawText)
    {
        var normalized = FailureInterventionPolicy.NormalizeSignature(
            AgentStudio.TaskServer.Contracts.FailureOutputNormalizer.StripVolatile(rawText ?? string.Empty));
        return normalized.Length <= VariantLength ? normalized : normalized[..VariantLength];
    }

    private static void Upsert(List<CauseBreakerRecord> records, CauseBreakerRecord record)
    {
        var index = records.FindIndex(item => Same(item.Fingerprint, record.Fingerprint));
        if (index >= 0) records[index] = record;
        else records.Add(record);
    }

    private static bool Same(string? left, string? right)
        => left is not null && right is not null && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private List<CauseBreakerRecord> Records()
    {
        if (_records is not null) return _records;
        if (_path is null || !File.Exists(_path)) return _records = [];
        string text;
        try
        {
            text = File.ReadAllText(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Starting empty here would persist an empty store over open
            // breakers on the next write. Fail this call and read again later.
            _logger.LogWarning(ex, "cause-breaker store read failed at {Path}", _path);
            throw new InvalidOperationException($"Cause-breaker store at {_path} is unreadable.", ex);
        }
        try
        {
            return _records = JsonSerializer.Deserialize<List<CauseBreakerRecord>>(text, Json) ?? [];
        }
        catch (JsonException ex)
        {
            // A corrupt store cannot be repaired by rereading. Keep it as
            // evidence and start empty; the sweep releases the cards whose
            // markers no breaker accounts for.
            var quarantine = $"{_path}.corrupt-{_time.GetUtcNow().UtcDateTime:yyyyMMddTHHmmssZ}";
            File.Copy(_path, quarantine, overwrite: true);
            _logger.LogError(ex, "cause-breaker store corrupt at {Path}; preserved as {Quarantine}", _path, quarantine);
            return _records = [];
        }
    }

    private void Persist(List<CauseBreakerRecord> records)
    {
        _records = records;
        if (_path is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(records, Json));
        File.Move(temp, _path, true);
    }
}

/// <summary>Releases a parked card through the review retry scheduler's fresh-plan path.</summary>
public sealed class SchedulerCauseWaitRelease : ICauseWaitRelease
{
    private readonly AttemptAuthorityService _authority;
    private readonly ReviewInfrastructureRetryScheduler _scheduler;
    private readonly ILogger<SchedulerCauseWaitRelease> _logger;

    public SchedulerCauseWaitRelease(
        AttemptAuthorityService authority,
        ReviewInfrastructureRetryScheduler scheduler,
        ILogger<SchedulerCauseWaitRelease> logger)
    {
        _authority = authority;
        _scheduler = scheduler;
        _logger = logger;
    }

    public string? Release(TaskInfo task, string deliveryKey)
    {
        // Review authority may be keyed by the stable public key or by the
        // path-qualified scanner identity, depending on when the attempt was
        // minted. A pending attempt can be admitted as it stands. Search the
        // history for terminal cards so a retry after a crash can replay the
        // same delivery key even when its successor is already pending.
        var projections = new[] { task.Key, task.TaskKey, task.Id }
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(key => _authority.GetTaskProjection(key!))
            .ToArray();
        // A card held before its first claim already owns a pending attempt.
        // Clearing its marker admits that attempt without minting another one.
        var pending = projections.Select(item => item.CurrentReviewAttempt)
            .FirstOrDefault(item => item is { Outcome: null });
        if (pending is not null)
            return pending.AttemptId;

        var review = projections
            .SelectMany(item => item.ReviewAttempts)
            .DistinctBy(candidate => candidate.AttemptId, StringComparer.OrdinalIgnoreCase)
            .Where(candidate => candidate.Outcome is ReviewTerminalOutcome.InfrastructureFailure
                or ReviewTerminalOutcome.ProductFailure or ReviewTerminalOutcome.Inconclusive
                or ReviewTerminalOutcome.Cancellation or ReviewTerminalOutcome.IntegrationBranchDefect)
            .OrderByDescending(candidate => candidate.CreatedAt)
            .FirstOrDefault();
        if (review is null)
            return null;
        // The predecessor attempt makes each explicit probe request unique,
        // including after a red probe on the same card. A product-failed probe
        // starts a fresh review of the same subject instead of using the
        // infrastructure-only retry lineage.
        var created = _scheduler.CreateFreshSuccessor(review, task,
            $"{deliveryKey}:{review.AttemptId}",
            linkTerminalReview: review.Outcome is ReviewTerminalOutcome.InfrastructureFailure
                or ReviewTerminalOutcome.Inconclusive or ReviewTerminalOutcome.Cancellation);
        if (!created.Accepted)
        {
            _logger.LogWarning(
                "cause-breaker-release-refused task={TaskKey} attempt={AttemptId} status={Status} message={Message}",
                review.TaskKey, review.AttemptId, created.Status, created.Message);
        }
        return created.Accepted ? created.ReviewAttempt?.AttemptId : null;
    }
}
