using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace AgentStudio.Runner;

/// <summary>
/// Boot-time recovery doctrine: after a backend crash mid-job, the next
/// boot must leave the system in a state where no agent evidence is lost
/// and the runner can resume cleanly. Two mechanisms cooperate (ADR-0020):
///
/// <list type="number">
///   <item><b>Completion markers.</b> The runner writes a tiny
///   <c>completion-marker.json</c> into the job folder right before the
///   <c>3-progress -&gt; 4-review</c> move. A marker that survives into
///   the next boot means the runner crashed between "decided" and
///   "moved"; we pick up where it left off via
///   <see cref="TaskTransitionService.MoveAsync"/>, then clear the
///   marker.</item>
///   <item><b>Orphan changes.</b> If the project's working tree contains
///   uncommitted changes, we queue a pending recovery item for the
///   operator. Only an explicit confirmation endpoint commits with the fixed
///   <c>crash-recovery</c> author tag. Recovery never pushes; that is still
///   the user's gate.</item>
/// </list>
///
/// Every recovery decision is appended to
/// <c>logs/backend/recovery.jsonl</c> for after-the-fact inspection and
/// also surfaced through the structured backend log so Layer 3 system
/// review picks it up.
///
/// <para>
/// Runs once at boot, before the first <see cref="ProjectRunner"/> tick,
/// so the runner sees the recovered state on its first scan and a
/// second crash mid-recovery is itself recoverable on the next boot.
/// </para>
/// </summary>
public sealed class CrashRecoveryService
{
    private readonly TaskScannerService _scanner;
    private readonly TaskTransitionService _transitions;
    private readonly TaskMutationService _mutations;
    private readonly GitService _git;
    private readonly ProjectSettingsService _settings;
    private readonly BackendFileLogSink _logSink;
    private readonly BackendFileLoggerOptions _logOptions;
    private readonly ILogger<CrashRecoveryService> _logger;
    private readonly IJsonlAppender _appender;
    private readonly PickupLockFile _pickupLock;
    private readonly CliRouter? _cliRouter;
    private readonly object _pendingLock = new();
    private readonly List<PendingCrashRecovery> _pendingOrphanRecoveries = [];

    public CrashRecoveryService(
        TaskScannerService scanner,
        TaskTransitionService transitions,
        TaskMutationService mutations,
        GitService git,
        ProjectSettingsService settings,
        BackendFileLogSink logSink,
        IOptions<BackendFileLoggerOptions> logOptions,
        ILogger<CrashRecoveryService> logger,
        IJsonlAppender? appender = null,
        PickupLockFile? pickupLock = null,
        CliRouter? cliRouter = null)
    {
        _scanner = scanner;
        _transitions = transitions;
        _mutations = mutations;
        _git = git;
        _settings = settings;
        _logSink = logSink;
        _logOptions = logOptions.Value;
        _logger = logger;
        _appender = appender ?? new JsonlAppender();
        _pickupLock = pickupLock ?? new PickupLockFile(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PickupLockFile>.Instance);
        _cliRouter = cliRouter;
    }

    /// <summary>Path of the recovery audit log. Absolute, alongside daily backend logs.</summary>
    public string RecoveryLogPath =>
        Path.Combine(Path.GetFullPath(_logOptions.LogDirectory), "recovery.jsonl");

    /// <summary>
    /// Run one full recovery sweep across all watched projects. Returns the
    /// list of recovery decisions taken so a caller (boot path, tests) can
    /// assert against the result without re-parsing the JSONL log.
    /// </summary>
    public async Task<IReadOnlyList<RecoveryDecision>> RecoverAsync(CancellationToken ct = default)
    {
        var decisions = new List<RecoveryDecision>();

        foreach (var entry in _scanner.GetWatchPaths())
        {
            if (string.IsNullOrWhiteSpace(entry.Path) || !Directory.Exists(entry.Path)) continue;

            if (!_settings.Get(entry.Name).CrashRecoveryEnabled)
            {
                _logger.LogInformation(
                    "CrashRecoveryService: recovery is disabled for project {Project}; boot sweep skipped.",
                    entry.Name);
                continue;
            }

            // Phase 1: complete pending transitions for any 3-progress jobs
            // whose marker survived the crash.
            await RecoverCompletionMarkersAsync(entry, decisions, ct);

            // Phase 2: rescue orphan working-tree changes onto the most
            // recently active job in 3-progress (if any). MUST run before
            // Phase 3, which requeues the interrupted job out of 3-progress;
            // the orphan-attribution here needs that job still in the lane to
            // pin the rescued commit to the right job.
            RecoverOrphanChanges(entry, decisions);

            // Phase 3: requeue runs that were interrupted mid-flight (a backend
            // restart / crash after the runner stamped the pickup lock but
            // before it released it). Such a job is stranded in 3-progress with
            // a stale .pickup-lock.json and (often) an empty logs/ dir. Clear
            // the stale lock and move it back to 2-ready so the next pickup
            // tick starts it cleanly, instead of letting a silent retry loop
            // feed the auto-failure circuit-breaker.
            await RecoverInterruptedRunsAsync(entry, decisions, ct);
        }

        // One INFO line per boot keeps the daily log scan-friendly even when
        // recovery had nothing to do.
        _logger.LogInformation(
            "CrashRecoveryService: completed boot sweep with {Count} decision(s).",
            decisions.Count);

        return decisions;
    }

    private async Task RecoverCompletionMarkersAsync(
        WatchPathEntry entry,
        List<RecoveryDecision> decisions,
        CancellationToken ct)
    {
        var progressDir = Path.Combine(entry.Path, TaskStates.Progress);
        if (!Directory.Exists(progressDir)) return;

        foreach (var jobFolder in Directory.EnumerateDirectories(progressDir))
        {
            ct.ThrowIfCancellationRequested();
            // A valid Slice B marker owns this no-process wait. Do not let boot
            // recovery complete a different transition before its configured
            // steer timeout chooses auto-answer or blocked. Malformed markers
            // deliberately fall through to the ordinary recovery net.
            var steerMarker = SteerPendingMarker.TryRead(jobFolder, _logger);
            if (steerMarker != null
                && !string.Equals(steerMarker.Kind, SteerPendingKinds.UiIterationReview, StringComparison.OrdinalIgnoreCase))
                continue;
            var marker = CompletionMarker.TryRead(jobFolder, _logger);
            if (marker == null) continue;

            var jobJsonPath = Path.Combine(jobFolder, "task.json");
            if (!File.Exists(jobJsonPath))
            {
                CompletionMarker.Clear(jobFolder, _logger);
                continue;
            }

            var jobId = Path.GetFileName(jobFolder);

            // A run that wrote a completion marker but crashed before its
            // finally-block released the pickup lock leaves a stale lock that
            // would otherwise ride along into 4-auto-review on the move below.
            // Clear it here so "no stale .pickup-lock.json by any exit path"
            // holds for the completion-marker recovery path too.
            TryClearStaleLock(jobFolder, entry, jobId, decisions);

            try
            {
                // The transition's auto-commit hook will pick up any
                // uncommitted work for this project under the project's
                // configured author. Recovery doesn't tag those commits;
                // the marker existing is the signal that the agent's run
                // completed normally. The crash-recovery tag only applies
                // to orphan changes (Phase 2) where no completion marker
                // was found.
                var moveOutcome = await _transitions.MoveAsync(
                    jobId, marker.TargetState, entry.Path, ct,
                    transitionCause: ProjectRunner.CompletionLaneChangeCause(marker.TargetState),
                    transitionDetail: "crash-recovery-completion-marker");
                if (moveOutcome.Status == MoveJobStatus.Success)
                {
                    var movedInfo = _scanner.FindJob(jobId, entry.Path);
                    if (movedInfo != null) CompletionMarker.Clear(movedInfo.FolderPath, _logger);

                    var decision = new RecoveryDecision
                    {
                        At = DateTime.UtcNow,
                        Kind = RecoveryDecisionKinds.TransitionCompleted,
                        ProjectName = entry.Name,
                        JobId = jobId,
                        TargetState = marker.TargetState,
                        Reason = $"completion-marker survived crash (executionStatus={marker.ExecutionStatus ?? "?"}, agentOutcome={marker.AgentOutcome ?? "?"})"
                    };
                    decisions.Add(decision);
                    AppendRecoveryEntry(decision);
                    _logger.LogInformation(
                        "CrashRecoveryService: completed transition {JobId} -> {Target} (project {Project})",
                        jobId, marker.TargetState, entry.Name);
                }
                else
                {
                    var decision = new RecoveryDecision
                    {
                        At = DateTime.UtcNow,
                        Kind = RecoveryDecisionKinds.TransitionFailed,
                        ProjectName = entry.Name,
                        JobId = jobId,
                        TargetState = marker.TargetState,
                        Reason = $"transition refused: {moveOutcome.Status} {moveOutcome.Message}"
                    };
                    decisions.Add(decision);
                    AppendRecoveryEntry(decision);
                    _logger.LogWarning(
                        "CrashRecoveryService: could not finish transition for {JobId}: {Status} {Message}",
                        jobId, moveOutcome.Status, moveOutcome.Message);
                }
            }
            catch (Exception ex)
            {
                var decision = new RecoveryDecision
                {
                    At = DateTime.UtcNow,
                    Kind = RecoveryDecisionKinds.TransitionFailed,
                    ProjectName = entry.Name,
                    JobId = jobId,
                    TargetState = marker.TargetState,
                    Reason = $"exception: {ex.Message}"
                };
                decisions.Add(decision);
                AppendRecoveryEntry(decision);
                _logger.LogError(ex, "CrashRecoveryService: transition for {JobId} threw", jobId);
            }
        }
    }

    private void RecoverOrphanChanges(WatchPathEntry entry, List<RecoveryDecision> decisions)
    {
        var repoRoot = _git.ResolveRepoRootForProject(entry.Name);
        if (string.IsNullOrWhiteSpace(repoRoot)) return;
        if (!_git.RepoHasUncommittedChanges(repoRoot)) return;

        // Attribute orphan changes to the most-recently-active job in
        // 3-progress by lastProgressAt. We deliberately read task.json
        // straight from disk: at boot time the TaskScannerService's overlay
        // has not warmed up yet, and we need a single field.
        //
        // Cross-lane lookup (drift rule `orphan-detection-checks-other-lanes`,
        // 2026-05-12 boot-race): the candidate 3-progress folder is skipped
        // when the same slug already lives in a later lane. That folder is a
        // mid-move casualty whose real twin already completed; attributing a
        // rescued commit to it would write the commit-info onto a folder that
        // is about to be cleaned up.
        var (jobId, jobFolder) = FindMostRecentlyActiveProgressJob(entry);

        var message = jobId == null
            ? $"chore(crash-recovery): rescue orphan changes for project {entry.Name}\n\n" +
              "Recovered uncommitted working-tree state after a backend crash. No active job\n" +
              "found in 3-progress; review and re-attribute manually if needed."
            : $"chore(crash-recovery): rescue orphan changes for {jobId}\n\n" +
              $"Recovered uncommitted working-tree state after a backend crash. Last active\n" +
              $"3-progress job in project {entry.Name} (by lastProgressAt) was {jobId}.";

        var scope = PlanCrashRecoveryCommitScope(entry, jobId, jobFolder, repoRoot);
        if (scope.Scope == CrashRecoveryCommitScope.None)
        {
            var skipped = new RecoveryDecision
            {
                At = DateTime.UtcNow,
                Kind = RecoveryDecisionKinds.OrphanSkipped,
                ProjectName = entry.Name,
                JobId = jobId,
                Reason = "uncommitted changes present but every dirty path predates the active job's first session event; skipped to avoid sweeping foreign work into crash recovery"
            };
            decisions.Add(skipped);
            AppendRecoveryEntry(skipped);
            _logger.LogInformation(
                "CrashRecoveryService: project {Project} has uncommitted changes for active job {JobId}, but no dirty path belongs to the session window; leaving them untouched.",
                entry.Name, jobId);
            return;
        }

        // Bind operator confirmation to the exact paths visible when recovery
        // was queued. Legacy/unknown recovery must never widen to a later dirty
        // tree merely because it lacks a session window.
        var pathspecs = scope.Paths;
        if (pathspecs is { Count: > 0 })
        {
            _logger.LogInformation(
                "CrashRecoveryService: scoped orphan recovery for project {Project} job {JobId} to {Count} task-attributable path(s); foreign dirty changes left untouched.",
                entry.Name, jobId, pathspecs.Count);
        }

        var pending = QueuePendingOrphanRecovery(entry, jobId, jobFolder, repoRoot, message, pathspecs);
        var decision = new RecoveryDecision
        {
            At = DateTime.UtcNow,
            Kind = RecoveryDecisionKinds.OrphanPending,
            ProjectName = entry.Name,
            JobId = jobId,
            Reason = jobId == null
                ? $"operator confirmation required before committing orphan changes; pendingId={pending.Id}; no active 3-progress job to attribute to"
                : $"operator confirmation required before committing orphan changes for {jobId}; pendingId={pending.Id}"
        };
        decisions.Add(decision);
        AppendRecoveryEntry(decision);
        _logger.LogInformation(
            "CrashRecoveryService: queued orphan changes for project {Project} job {JobId} as pending recovery {PendingId}; waiting for operator confirmation.",
            entry.Name, jobId ?? "(none)", pending.Id);
    }

    public IReadOnlyList<PendingCrashRecovery> GetPendingOrphanRecoveries()
    {
        lock (_pendingLock)
        {
            return _pendingOrphanRecoveries
                .OrderBy(p => p.CreatedAt)
                .Select(p => p with { Files = p.Files.ToArray(), Pathspecs = p.Pathspecs?.ToArray() })
                .ToArray();
        }
    }

    public CrashRecoveryActionResult CommitPendingOrphanRecovery(string id)
    {
        var pending = TakePendingOrphanRecovery(id);
        if (pending == null)
            return new CrashRecoveryActionResult(CrashRecoveryActionStatuses.NotFound, Error: "Pending crash recovery item not found.");

        var commit = _git.CrashRecoveryCommit(
            pending.ProjectName, pending.RepoRoot, pending.Message, pending.Pathspecs,
            pending.JobId, runnerId: "crash-recovery");
        if (!commit.Success)
        {
            if (commit.Error != null && commit.Error.Contains("Nothing to commit", StringComparison.OrdinalIgnoreCase))
            {
                var skipped = new RecoveryDecision
                {
                    At = DateTime.UtcNow,
                    Kind = RecoveryDecisionKinds.OrphanSkipped,
                    ProjectName = pending.ProjectName,
                    JobId = pending.JobId,
                    Reason = $"pending orphan recovery {pending.Id} was confirmed, but the working tree no longer had matching changes"
                };
                AppendRecoveryEntry(skipped);
                return new CrashRecoveryActionResult(CrashRecoveryActionStatuses.NothingToCommit, pending);
            }

            PutPendingOrphanRecoveryBack(pending);
            var failed = new RecoveryDecision
            {
                At = DateTime.UtcNow,
                Kind = RecoveryDecisionKinds.OrphanCommitFailed,
                ProjectName = pending.ProjectName,
                JobId = pending.JobId,
                Reason = $"operator-confirmed git commit failed for pendingId={pending.Id}: {commit.Error}"
            };
            AppendRecoveryEntry(failed);
            _logger.LogWarning(
                "CrashRecoveryService: confirmed orphan commit failed for project {Project}: {Error}",
                pending.ProjectName, commit.Error);
            return new CrashRecoveryActionResult(CrashRecoveryActionStatuses.Failed, pending, Error: commit.Error);
        }

        var decision = new RecoveryDecision
        {
            At = DateTime.UtcNow,
            Kind = RecoveryDecisionKinds.OrphanCommitted,
            ProjectName = pending.ProjectName,
            JobId = pending.JobId,
            CommitSha = commit.Sha,
            Reason = pending.JobId == null
                ? $"operator confirmed orphan changes commit; pendingId={pending.Id}; no active 3-progress job to attribute to"
                : $"operator confirmed orphan changes commit for {pending.JobId}; pendingId={pending.Id}"
        };
        AppendRecoveryEntry(decision);
        _logger.LogInformation(
            "CrashRecoveryService: committed operator-confirmed orphan changes for project {Project} as {Sha}",
            pending.ProjectName, commit.Sha);

        AttachCommitToJob(pending, commit.Sha);
        return new CrashRecoveryActionResult(CrashRecoveryActionStatuses.Committed, pending, commit.Sha);
    }

    public CrashRecoveryActionResult DismissPendingOrphanRecovery(string id)
    {
        var pending = TakePendingOrphanRecovery(id);
        if (pending == null)
            return new CrashRecoveryActionResult(CrashRecoveryActionStatuses.NotFound, Error: "Pending crash recovery item not found.");

        var decision = new RecoveryDecision
        {
            At = DateTime.UtcNow,
            Kind = RecoveryDecisionKinds.OrphanSkipped,
            ProjectName = pending.ProjectName,
            JobId = pending.JobId,
            Reason = $"operator dismissed pending orphan recovery {pending.Id}; working tree left uncommitted"
        };
        AppendRecoveryEntry(decision);
        _logger.LogInformation(
            "CrashRecoveryService: dismissed pending orphan recovery {PendingId} for project {Project}",
            pending.Id, pending.ProjectName);
        return new CrashRecoveryActionResult(CrashRecoveryActionStatuses.Dismissed, pending);
    }

    private PendingCrashRecovery QueuePendingOrphanRecovery(
        WatchPathEntry entry,
        string? jobId,
        string? jobFolder,
        string repoRoot,
        string message,
        IReadOnlyList<string>? pathspecs)
    {
        var files = ReadPendingFiles(repoRoot, pathspecs);
        var classification = CrashRecoveryClassifications.Classify(jobId, files);
        var firstObservedAt = ResolveFirstObservedAt(jobFolder, repoRoot, files);
        var id = CrashRecoveryPendingId.Create(entry.Name, repoRoot, classification, firstObservedAt);
        var pending = new PendingCrashRecovery
        {
            Id = id,
            CreatedAt = firstObservedAt,
            ProjectName = entry.Name,
            JobId = jobId,
            RepoRoot = repoRoot,
            Files = files,
            Message = message,
            Reason = jobId == null
                ? "Uncommitted changes were found at startup with no active job attribution."
                : $"Uncommitted changes were found at startup and attributed to {jobId}.",
            JobFolder = jobFolder,
            Pathspecs = pathspecs?.ToArray()
        };

        lock (_pendingLock)
        {
            var existingIndex = _pendingOrphanRecoveries.FindIndex(p =>
                string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
            {
                _pendingOrphanRecoveries[existingIndex] = pending;
                return pending;
            }

            _pendingOrphanRecoveries.Add(pending);
            return pending;
        }
    }

    private static DateTime ResolveFirstObservedAt(
        string? jobFolder,
        string repoRoot,
        IReadOnlyList<string> files)
    {
        DateTime? earliestFileWriteAt = null;
        foreach (var file in files)
        {
            try
            {
                var fullPath = Path.Combine(repoRoot, file.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(fullPath)) continue;
                var writtenAt = File.GetLastWriteTimeUtc(fullPath);
                if (earliestFileWriteAt == null || writtenAt < earliestFileWriteAt.Value)
                    earliestFileWriteAt = writtenAt;
            }
            catch (Exception __ex)
            {
                SilentCatch.Note(__ex, "CrashRecoveryService: Ignore unreadable dirty-file timestamps when building a pending recovery ID.");
            }
        }

        if (earliestFileWriteAt != null) return earliestFileWriteAt.Value;

        if (!string.IsNullOrWhiteSpace(jobFolder))
        {
            var firstSessionEventAt = ReadFirstSessionEventAt(jobFolder);
            if (firstSessionEventAt != null) return firstSessionEventAt.Value;

            var taskCreatedAt = ReadTaskCreatedAt(jobFolder);
            if (taskCreatedAt != null) return taskCreatedAt.Value;
        }

        // A deletion-only legacy finding has no surviving file timestamp. The
        // worktree directory predates the finding and remains stable across a
        // backend restart, so it is a deterministic final fallback.
        return Directory.GetCreationTimeUtc(repoRoot);
    }

    private static DateTime? ReadTaskCreatedAt(string jobFolder)
    {
        var taskJsonPath = Path.Combine(jobFolder, "task.json");
        if (!File.Exists(taskJsonPath)) return null;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(taskJsonPath));
            if (doc.RootElement.TryGetProperty("createdAt", out var createdAt)
                && createdAt.ValueKind == JsonValueKind.String
                && DateTime.TryParse(
                    createdAt.GetString(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal
                        | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var parsed))
            {
                return parsed;
            }

            return File.GetCreationTimeUtc(taskJsonPath);
        }
        catch (Exception __ex)
        {
            SilentCatch.Note(__ex, "CrashRecoveryService: Ignore unreadable task timestamps when building a pending recovery ID.");
            return null;
        }
    }

    private PendingCrashRecovery? TakePendingOrphanRecovery(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        lock (_pendingLock)
        {
            var index = _pendingOrphanRecoveries.FindIndex(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return null;
            var pending = _pendingOrphanRecoveries[index];
            _pendingOrphanRecoveries.RemoveAt(index);
            return pending;
        }
    }

    private void PutPendingOrphanRecoveryBack(PendingCrashRecovery pending)
    {
        lock (_pendingLock)
        {
            if (_pendingOrphanRecoveries.Any(p => string.Equals(p.Id, pending.Id, StringComparison.OrdinalIgnoreCase))) return;
            _pendingOrphanRecoveries.Add(pending);
        }
    }

    private IReadOnlyList<string> ReadPendingFiles(string repoRoot, IReadOnlyList<string>? pathspecs)
    {
        if (pathspecs is { Count: > 0 })
            return pathspecs.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();

        var status = _git.GetStatusForRepoRoot(repoRoot);
        if (!status.IsRepo || status.Files.Count == 0) return [];
        return status.Files
            .Select(f => f.Path)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void AttachCommitToJob(PendingCrashRecovery pending, string? sha)
    {
        if (pending.JobId == null || string.IsNullOrWhiteSpace(sha))
            return;

        var jobFolder = ResolveCurrentJobFolder(pending) ?? pending.JobFolder;
        if (string.IsNullOrWhiteSpace(jobFolder) || !Directory.Exists(jobFolder))
            return;

        // AGT-2220: commits[] is evidence, so nothing lands there unverified -
        // not even a SHA this process believes it just created. If the commit is
        // not in the repository, the attribution is dropped and said out loud
        // rather than leaving a card pointing at a commit that does not exist.
        if (!_git.CommitExistsInRepo(pending.RepoRoot, sha))
        {
            _logger.LogWarning(
                "crash-recovery-attribution-refused job={JobId} repo={RepoRoot} sha={Sha} "
                + "reason=commit-not-found-in-repository",
                pending.JobId, pending.RepoRoot, sha);
            return;
        }

        _mutations.SetJobCommitOnFolder(jobFolder, new TaskCommitInfo
        {
            Sha = sha!,
            ShortSha = sha!.Length > 7 ? sha[..7] : sha,
            Message = $"crash-recovery: orphan changes for {pending.JobId}",
            FilesChanged = pending.Pathspecs?.Count ?? pending.Files.Count,
            Files = pending.Pathspecs?.ToList() ?? pending.Files.ToList(),
            At = DateTime.UtcNow
        });
    }

    private string? ResolveCurrentJobFolder(PendingCrashRecovery pending)
    {
        if (pending.JobId == null) return null;
        var entry = _scanner.GetWatchPaths().FirstOrDefault(e =>
            string.Equals(e.Name, pending.ProjectName, StringComparison.OrdinalIgnoreCase));
        if (entry == null) return null;
        return _scanner.FindJob(pending.JobId, entry.Path)?.FolderPath;
    }

    private enum CrashRecoveryCommitScope
    {
        All,
        None,
        Scoped,
    }

    private sealed record CrashRecoveryCommitPlan(CrashRecoveryCommitScope Scope, IReadOnlyList<string> Paths);

    private CrashRecoveryCommitPlan PlanCrashRecoveryCommitScope(
        WatchPathEntry entry,
        string? jobId,
        string? jobFolder,
        string repoRoot)
    {
        var status = _git.GetStatusForRepoRoot(repoRoot);
        var allPaths = status.IsRepo
            ? status.Files
                .Select(file => file.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];
        var all = new CrashRecoveryCommitPlan(CrashRecoveryCommitScope.Scoped, allPaths);
        if (string.IsNullOrWhiteSpace(jobId) || string.IsNullOrWhiteSpace(jobFolder))
            return all;

        var firstActivityUtc = ReadFirstSessionEventAt(jobFolder);
        if (firstActivityUtc == null)
            return all;

        if (!status.IsRepo || status.Files.Count == 0)
            return all;

        var scoped = new List<string>();
        foreach (var file in status.Files)
        {
            if (string.IsNullOrWhiteSpace(file.Path)) continue;
            var fullPath = Path.Combine(repoRoot, file.Path.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                if (!File.Exists(fullPath))
                {
                    scoped.Add(file.Path);
                    continue;
                }

                if (File.GetLastWriteTimeUtc(fullPath) >= firstActivityUtc.Value)
                    scoped.Add(file.Path);
            }
            catch
            {
                scoped.Add(file.Path);
            }
        }

        if (scoped.Count == 0)
            return new CrashRecoveryCommitPlan(CrashRecoveryCommitScope.None, []);

        return new CrashRecoveryCommitPlan(CrashRecoveryCommitScope.Scoped, scoped);
    }

    private static DateTime? ReadFirstSessionEventAt(string jobFolder)
    {
        var path = TaskPaths.SessionEventsLog(jobFolder);
        if (!File.Exists(path)) return null;

        DateTime? first = null;
        foreach (var line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var evt = JsonSerializer.Deserialize<SessionEvent>(line, TaskJsonFile.ReadOpts);
                if (evt == null || evt.Ts == default) continue;
                var ts = evt.Ts.Kind == DateTimeKind.Utc ? evt.Ts : evt.Ts.ToUniversalTime();
                if (first == null || ts < first.Value) first = ts;
            }
            catch (Exception __ex)
            {
                SilentCatch.Note(__ex, "CrashRecoveryService: Best-effort recovery: ignore torn session-event rows.");
                // Best-effort recovery: ignore torn session-event rows.
            }
        }

        return first;
    }

    /// <summary>
    /// Phase 3: requeue any 3-progress job whose run was interrupted mid-flight.
    /// The signal is a <c>.pickup-lock.json</c> left in the folder whose owning
    /// pid is no longer running on this host - the runner stamps that lock right
    /// before it spawns the CLI and releases it in a finally block, so a stale
    /// lock at boot means the backend died between those two points. We clear
    /// the lock and move the job 3-progress -> 2-ready so the next pickup tick
    /// starts it cleanly. A live foreign lock (another backend on the same
    /// workspace) is left untouched - that run is not ours to requeue.
    /// </summary>
    private async Task RecoverInterruptedRunsAsync(
        WatchPathEntry entry,
        List<RecoveryDecision> decisions,
        CancellationToken ct)
    {
        var progressDir = Path.Combine(entry.Path, TaskStates.Progress);
        if (!Directory.Exists(progressDir)) return;

        foreach (var jobFolder in Directory.EnumerateDirectories(progressDir))
        {
            ct.ThrowIfCancellationRequested();

            // The active run already ended intentionally in a bounded steer
            // wait. A stale pickup lock from the completion race is not proof
            // that the task should be requeued; SteerTimeoutMonitor owns it.
            if (SteerPendingMarker.TryRead(jobFolder, _logger) != null) continue;

            // No task.json means this is a folder-shaped orphan, not a real
            // interrupted run; leave it for the runner's own orphan sweep.
            if (!File.Exists(Path.Combine(jobFolder, "task.json"))) continue;

            // Only a present lock signals an interrupted run. A 3-progress job
            // with no lock was never mid-spawn (or was already released); the
            // strict-iteration picker resumes it in place. Don't disturb it.
            var existing = _pickupLock.Peek(jobFolder);
            if (existing == null) continue;

            var jobId = Path.GetFileName(jobFolder);
            var durableJobKey = TaskIdentity.CreateKey(entry.Path, jobId);
            if (_cliRouter?.CanReattach(durableJobKey) == true)
            {
                _logger.LogInformation(
                    "CrashRecoveryService: durable worker/result for {JobId} bridges the backend restart; leaving the run and pickup lock in place",
                    jobId);
                continue;
            }

            // ClearIfStale removes the lock only when its owner pid is dead on
            // this host. A live foreign owner returns false and is left alone.
            if (!_pickupLock.ClearIfStale(jobFolder))
            {
                _logger.LogInformation(
                    "CrashRecoveryService: pickup lock on {Folder} is held by a live owner ({Backend} pid={Pid}); leaving the run in place",
                    jobFolder, existing.BackendName, existing.Pid);
                continue;
            }

            try
            {
                var moveOutcome = await _transitions.MoveAsync(
                    jobId, TaskStates.Ready, entry.Path, ct,
                    transitionCause: LaneChangeCauses.LeaseRecovery,
                    transitionDetail: "crash-recovery-stale-pickup-lock");
                if (moveOutcome.Status == MoveJobStatus.Success)
                {
                    var moved = _scanner.FindJob(jobId, entry.Path);
                    if (string.Equals(moved?.State, TaskStates.AutoReview, StringComparison.Ordinal))
                    {
                        var recoveredDecision = new RecoveryDecision
                        {
                            At = DateTime.UtcNow,
                            Kind = RecoveryDecisionKinds.SettledRunRecovered,
                            ProjectName = entry.Name,
                            JobId = jobId,
                            TargetState = TaskStates.AutoReview,
                            Reason = "attempt authority reported a completed immutable result; recovered the existing delivery to auto-review instead of requeueing"
                        };
                        decisions.Add(recoveredDecision);
                        AppendRecoveryEntry(recoveredDecision);
                        _logger.LogWarning(
                            "CrashRecoveryService: recovered settled run {JobId} -> 4-auto-review (project {Project}); no replacement run was queued",
                            jobId,
                            entry.Name);
                        continue;
                    }

                    // Leave a human-readable trace only after the shared BP-09
                    // guard confirms that the card actually landed in Ready.
                    WriteInterruptedRunDiagnostic(
                        moved?.FolderPath ?? moveOutcome.NewFolderPath ?? jobFolder,
                        existing);
                    var decision = new RecoveryDecision
                    {
                        At = DateTime.UtcNow,
                        Kind = RecoveryDecisionKinds.RunInterruptedRequeued,
                        ProjectName = entry.Name,
                        JobId = jobId,
                        TargetState = TaskStates.Ready,
                        Reason = $"run interrupted mid-flight (stale pickup lock from {existing.BackendName} pid={existing.Pid}); requeued to 2-ready and stale lock cleared"
                    };
                    decisions.Add(decision);
                    AppendRecoveryEntry(decision);
                    _logger.LogInformation(
                        "CrashRecoveryService: requeued interrupted run {JobId} -> 2-ready (project {Project}); cleared stale lock from {Backend} pid={Pid}",
                        jobId, entry.Name, existing.BackendName, existing.Pid);
                }
                else
                {
                    var decision = new RecoveryDecision
                    {
                        At = DateTime.UtcNow,
                        Kind = RecoveryDecisionKinds.RunInterruptedRequeueFailed,
                        ProjectName = entry.Name,
                        JobId = jobId,
                        TargetState = TaskStates.Ready,
                        Reason = $"requeue refused: {moveOutcome.Status} {moveOutcome.Message}"
                    };
                    decisions.Add(decision);
                    AppendRecoveryEntry(decision);
                    _logger.LogWarning(
                        "CrashRecoveryService: could not requeue interrupted run {JobId}: {Status} {Message}",
                        jobId, moveOutcome.Status, moveOutcome.Message);
                }
            }
            catch (Exception ex)
            {
                var decision = new RecoveryDecision
                {
                    At = DateTime.UtcNow,
                    Kind = RecoveryDecisionKinds.RunInterruptedRequeueFailed,
                    ProjectName = entry.Name,
                    JobId = jobId,
                    TargetState = TaskStates.Ready,
                    Reason = $"exception: {ex.Message}"
                };
                decisions.Add(decision);
                AppendRecoveryEntry(decision);
                _logger.LogError(ex, "CrashRecoveryService: requeue for {JobId} threw", jobId);
            }
        }
    }

    /// <summary>
    /// Clear a stale pickup lock if one is present and its owner pid is dead.
    /// Records a <see cref="RecoveryDecisionKinds.StalePickupLockCleared"/>
    /// decision so the cleanup is auditable. Used by the completion-marker
    /// path; the interrupted-run path clears inline because it also moves the
    /// folder. Best-effort: a live foreign lock is left untouched.
    /// </summary>
    private void TryClearStaleLock(
        string jobFolder,
        WatchPathEntry entry,
        string jobId,
        List<RecoveryDecision> decisions)
    {
        var existing = _pickupLock.Peek(jobFolder);
        if (existing == null) return;
        if (!_pickupLock.ClearIfStale(jobFolder)) return;

        var decision = new RecoveryDecision
        {
            At = DateTime.UtcNow,
            Kind = RecoveryDecisionKinds.StalePickupLockCleared,
            ProjectName = entry.Name,
            JobId = jobId,
            Reason = $"cleared stale pickup lock from {existing.BackendName} pid={existing.Pid}"
        };
        decisions.Add(decision);
        AppendRecoveryEntry(decision);
    }

    /// <summary>
    /// Append the one compact recovery line to the job's cli-output.log so an
    /// operator reading the Activity Log sees the run was requeued, instead of
    /// an empty logs/ dir. The long form (which backend / pid held the stale
    /// lock) stays in recovery.jsonl — not the chat. Best-effort.
    /// </summary>
    private void WriteInterruptedRunDiagnostic(string jobFolder, PickupLockInfo lockInfo)
    {
        try
        {
            var logsDir = Path.Combine(jobFolder, TaskPaths.LogsDirName);
            Directory.CreateDirectory(logsDir);
            var logPath = Path.Combine(logsDir, TaskPaths.CliOutputLogFileName);
            var line = RecoveryChatLine.PersistedLine(
                DateTime.UtcNow,
                RecoveryChatLine.ReasonCrash,
                "backend restart during run",
                $"requeued to {TaskStates.Ready}");
            CliOutputLogFile.Append(logPath, line);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "CrashRecoveryService: failed to write interrupted-run diagnostic for {Folder}", jobFolder);
        }
    }

    private static (string? JobId, string? TaskFolder) FindMostRecentlyActiveProgressJob(WatchPathEntry entry)
    {
        var progressDir = Path.Combine(entry.Path, TaskStates.Progress);
        if (!Directory.Exists(progressDir)) return (null, null);

        string? bestId = null;
        string? bestFolder = null;
        DateTime bestAt = DateTime.MinValue;

        foreach (var jobFolder in Directory.EnumerateDirectories(progressDir))
        {
            var jobJsonPath = Path.Combine(jobFolder, "task.json");
            if (!File.Exists(jobJsonPath)) continue;
            if (SteerPendingMarker.TryRead(jobFolder) != null) continue;

            var slug = Path.GetFileName(jobFolder);
            // Mid-move casualty: same slug already lives in a later lane, so
            // the real run already finished. Attributing the rescued commit
            // here would tag a folder that is about to be reconciled away.
            if (SlugExistsInDownstreamLane(entry.Path, slug)) continue;

            try
            {
                var json = File.ReadAllText(jobJsonPath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                DateTime at;
                if (root.TryGetProperty("lastProgressAt", out var lpEl)
                    && lpEl.ValueKind == JsonValueKind.String
                    && DateTime.TryParse(lpEl.GetString(),
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                        out var parsedLp))
                {
                    at = parsedLp;
                }
                else
                {
                    // Fall back to task.json mtime; better than nothing.
                    at = File.GetLastWriteTimeUtc(jobJsonPath);
                }

                if (at > bestAt)
                {
                    bestAt = at;
                    bestId = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : Path.GetFileName(jobFolder);
                    bestFolder = jobFolder;
                }
            }
            catch (Exception __ex)
            {
                SilentCatch.Note(__ex, "CrashRecoveryService: Ignore unreadable task.json entries; recovery is best-effort.");
                // Ignore unreadable task.json entries; recovery is best-effort.
            }
        }

        return (bestId, bestFolder);
    }

    /// <summary>
    /// Cross-lane lookup matched to the one in
    /// <see cref="StaleProgressArchiver"/>: lanes where a finished twin of a
    /// 3-progress slug can live. 3a-failed-pickup is deliberately excluded so
    /// a pre-fix phantom marker can never mask a real attribution candidate.
    /// Drift-watchlist rule <c>orphan-detection-checks-other-lanes</c>.
    /// </summary>
    private static readonly string[] DownstreamLanesForOrphanReconciliation =
    {
        TaskStates.AutoReview,
        TaskStates.HumanReview,
        TaskStates.Escalated,
        TaskStates.Completed,
        TaskStates.Archive,
    };

    private static bool SlugExistsInDownstreamLane(string watchPath, string slug)
    {
        foreach (var lane in DownstreamLanesForOrphanReconciliation)
        {
            if (Directory.Exists(Path.Combine(watchPath, lane, slug))) return true;
        }
        return false;
    }

    private void AppendRecoveryEntry(RecoveryDecision decision)
    {
        try
        {
            _appender.AppendAsync(RecoveryLogPath, decision, RecoveryJsonOptions).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CrashRecoveryService: failed to append recovery.jsonl");
        }

        // Mirror onto the structured backend log so daily-log scrapers
        // and Layer 3 review pick it up alongside the rest of boot.
        try
        {
            var line = $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} INFO  Backend.CrashRecovery " +
                       $"{decision.Kind} project={decision.ProjectName} jobId={decision.JobId ?? "(none)"} " +
                       $"sha={decision.CommitSha ?? "-"} target={decision.TargetState ?? "-"} reason=\"{decision.Reason}\"";
            _logSink.WriteRaw(line);
        }
        catch (Exception __ex)
        {
            SilentCatch.Note(__ex, "CrashRecoveryService: Logging the recovery must never crash the boot path.");
            // Logging the recovery must never crash the boot path.
        }
    }

    private static readonly JsonSerializerOptions RecoveryJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

/// <summary>One row in <c>logs/backend/recovery.jsonl</c>.</summary>
public sealed record RecoveryDecision
{
    [JsonPropertyName("at")] public DateTime At { get; init; }
    [JsonPropertyName("kind")] public string Kind { get; init; } = "";
    [JsonPropertyName("projectName")] public string ProjectName { get; init; } = "";
    [JsonPropertyName("jobId")] public string? JobId { get; init; }
    [JsonPropertyName("targetState")] public string? TargetState { get; init; }
    [JsonPropertyName("commitSha")] public string? CommitSha { get; init; }
    [JsonPropertyName("reason")] public string Reason { get; init; } = "";
}

/// <summary>Operator-confirmable crash recovery item held in memory after boot.</summary>
public sealed record PendingCrashRecovery
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; init; }
    [JsonPropertyName("projectName")] public string ProjectName { get; init; } = "";
    [JsonPropertyName("jobId")] public string? JobId { get; init; }
    [JsonPropertyName("repoRoot")] public string RepoRoot { get; init; } = "";
    [JsonPropertyName("files")] public IReadOnlyList<string> Files { get; init; } = [];
    [JsonPropertyName("message")] public string Message { get; init; } = "";
    [JsonPropertyName("reason")] public string Reason { get; init; } = "";
    [JsonPropertyName("classification")]
    public string Classification => CrashRecoveryClassifications.Classify(JobId, Files);

    [JsonIgnore] public string? JobFolder { get; init; }
    [JsonIgnore] public IReadOnlyList<string>? Pathspecs { get; init; }
}

public static class CrashRecoveryClassifications
{
    public const string Trivial = "trivial";
    public const string ReviewRequired = "review-required";

    public static string Classify(string? jobId, IReadOnlyList<string> files)
    {
        var onlyReadEvidenceSidecars = files.Count > 0
            && files.All(path => path.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(jobId) && onlyReadEvidenceSidecars
            ? Trivial
            : ReviewRequired;
    }
}

public sealed record CrashRecoveryActionResult(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("pending")] PendingCrashRecovery? Pending = null,
    [property: JsonPropertyName("commitSha")] string? CommitSha = null,
    [property: JsonPropertyName("error")] string? Error = null);

public static class CrashRecoveryActionStatuses
{
    public const string Committed = "committed";
    public const string Dismissed = "dismissed";
    public const string Failed = "failed";
    public const string NotFound = "not-found";
    public const string NothingToCommit = "nothing-to-commit";
}

/// <summary>String constants for <see cref="RecoveryDecision.Kind"/>.</summary>
public static class RecoveryDecisionKinds
{
    public const string TransitionCompleted = "transition-completed";
    public const string TransitionFailed = "transition-failed";
    public const string OrphanPending = "orphan-pending-confirmation";
    public const string OrphanCommitted = "orphan-committed";
    public const string OrphanCommitFailed = "orphan-commit-failed";
    /// <summary>
    /// C1: orphan changes were detected but skipped because no 3-progress
    /// job was active to attribute them to (= likely a human editor
    /// session, not a crashed agent run). Set
    /// ATP_CRASH_RECOVERY_AGGRESSIVE=1 to re-enable the old auto-commit.
    /// </summary>
    public const string OrphanSkipped = "orphan-skipped";
    /// <summary>An interrupted mid-flight run was requeued from 3-progress back to 2-ready.</summary>
    public const string RunInterruptedRequeued = "run-interrupted-requeued";
    /// <summary>A settled immutable result was recovered forward instead of being requeued.</summary>
    public const string SettledRunRecovered = "settled-run-recovered";
    /// <summary>An interrupted run was detected but the requeue move failed.</summary>
    public const string RunInterruptedRequeueFailed = "run-interrupted-requeue-failed";
    /// <summary>A stale .pickup-lock.json (owner pid dead on this host) was removed at boot.</summary>
    public const string StalePickupLockCleared = "stale-pickup-lock-cleared";
}
