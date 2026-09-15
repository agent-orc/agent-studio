using System.Text.Json;

namespace AgentStudio.Tasks;

/// <summary>
/// State transitions for tasks in the flat storage layout: a lane change is
/// a metadata + index mutation (<c>task.json.state</c> + <c>id/by-state</c>),
/// not a folder move; plus deleting a task folder, copying a task to a
/// different watched workspace, ensuring the storage folders exist and
/// rebuilding the derived index at boot, and the bulk reorder-within-lane
/// operation.
///
/// All operations on disk; no callers should write to the storage folders
/// directly. Reads still go through <see cref="TaskScannerService"/>.
/// </summary>
public class TaskStateMachine
{
    private const int DirectoryMoveMaxAttempts = 8;
    private static readonly int[] DirectoryMoveBackoffMs = [50, 100, 200, 400, 800, 1200, 1600];

    private readonly TaskScannerService _scanner;
    private readonly LaneMutexRegistry _laneMutex;
    private readonly ILogger<TaskStateMachine> _logger;
    private readonly ProjectRegistry? _projectRegistry;
    // Optional so the many tests that construct TaskStateMachine with the
    // original 2/3-arg signature keep compiling. Production DI always
    // supplies it; when null the SignalR push is simply skipped (the
    // coarse file-watcher jobsChanged event still covers the change).
    private readonly TaskChangeNotifier? _notifier;
    // T2b (ASS-1740): tee a lane_changed row into the per-task ledger on every
    // transition so the lane-move HISTORY is captured append-only, not just the
    // enteredLaneAt of the latest move. Optional for the same test-compat reason
    // as _notifier; when null the move still lands, just without a ledger row.
    private readonly TimelineLog? _timeline;
    // Transition-Committer hook: every successful lane crossing enqueues a
    // workspace evidence-commit wish onto this queue. The commit itself is done
    // debounced, off-thread, by WorkspaceEvidenceWorker — NEVER synchronously in
    // this move path. Optional for the same test-compat reason as the others;
    // when null the move still lands, just without an evidence nudge.
    private readonly AgentStudio.Pipeline.WorkspaceEvidenceQueue? _evidenceQueue;

    public TaskStateMachine(
        TaskScannerService scanner,
        ILogger<TaskStateMachine> logger,
        LaneMutexRegistry? laneMutex = null,
        TaskChangeNotifier? notifier = null,
        ProjectRegistry? projectRegistry = null,
        TimelineLog? timeline = null,
        AgentStudio.Pipeline.WorkspaceEvidenceQueue? evidenceQueue = null)
    {
        _scanner = scanner;
        _logger = logger;
        // F21: tolerate a missing registry so existing tests that
        // construct TaskStateMachine with the original two-arg signature
        // keep compiling. Production wiring always passes the singleton.
        _laneMutex = laneMutex ?? LaneMutexRegistry.NullSingleton;
        _notifier = notifier;
        _projectRegistry = projectRegistry;
        _timeline = timeline;
        _evidenceQueue = evidenceQueue;
    }

    /// <summary>
    /// Transition-Committer hook. Best-effort, non-blocking: a channel write
    /// that must never throw into — and therefore never break — the lane move
    /// that already landed on disk. The actual git commit is debounced and runs
    /// on <see cref="AgentStudio.Pipeline.WorkspaceEvidenceWorker"/>.
    /// </summary>
    private void EnqueueEvidence(string? watchPath, string? project, string? slug, string? fromState, string? toState)
    {
        if (_evidenceQueue == null || string.IsNullOrWhiteSpace(watchPath)) return;
        try
        {
            _evidenceQueue.Enqueue(new AgentStudio.Pipeline.WorkspaceEvidenceRequest(
                watchPath!, project ?? string.Empty, slug ?? string.Empty,
                fromState ?? string.Empty, toState ?? string.Empty));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "workspace-evidence enqueue failed for {Slug} ({From}->{To})", slug, fromState, toState);
        }
    }

    /// <param name="cause">
    /// T2b: coarse trigger for the lane-change ledger row (the "ausloeser").
    /// One of <see cref="TimelineActors"/> (or <c>human:&lt;email&gt;</c>);
    /// null is recorded as <see cref="TimelineActors.System"/>. Threaded from
    /// the caller that knows who initiated the move (operator drag, orchestrator
    /// decision, runner pickup); the default keeps every existing call site
    /// compiling.
    /// </param>
    /// <param name="transitionCause">
    /// Why the lane changed, as one of <see cref="LaneChangeCauses"/>; written
    /// to <c>lane_changed.details.cause</c> so the cycle-time analysis reads the
    /// taxonomy exactly instead of inferring it from neighbouring rows. Null
    /// for a human actor derives the operator cause from the lane pair; null
    /// for any other actor leaves the field absent (legacy row, inferred).
    /// </param>
    /// <param name="transitionDetail">
    /// Short qualifier for the cause (quality-loop cause id, integration
    /// outcome, escalation category, retry counter); written to
    /// <c>lane_changed.details.causeDetail</c>.
    /// </param>
    public MoveJobOutcome MoveJob(
        string jobId,
        string targetState,
        string? watchPath = null,
        string? cause = null,
        AttemptWriteReference? authorityWrite = null,
        string? expectedSourceState = null,
        string? reason = null,
        string? transitionCause = null,
        string? transitionDetail = null)
    {
        if (!TaskStates.All.Contains(targetState))
            return new MoveJobOutcome(MoveJobStatus.Failure, $"Invalid state: {targetState}");

        // When the caller supplied the project path, take its lane lock before
        // the first scan. An uncached scan enumerates every lane separately;
        // without this early lock it can miss a different task that another
        // writer moves between those enumerations.
        using var requestedPathLock = string.IsNullOrWhiteSpace(watchPath)
            ? null
            : _laneMutex.Acquire(watchPath);
        var info = FindJobForLaneMutation(jobId, watchPath);
        if (info == null) return new MoveJobOutcome(MoveJobStatus.NotFound);
        if (!string.IsNullOrWhiteSpace(expectedSourceState)
            && !string.Equals(info.State, expectedSourceState, StringComparison.OrdinalIgnoreCase))
        {
            return new MoveJobOutcome(
                MoveJobStatus.SourceStateMismatch,
                $"Expected source state {expectedSourceState}, current state is {info.State}.");
        }
        if (info.State == targetState) return new MoveJobOutcome(MoveJobStatus.Success, NewFolderPath: info.FolderPath);

        // A path-less internal caller still needs the original resolve-then-lock
        // flow. Re-check existence inside the mutex because another writer may
        // have moved the source while that caller resolved its project.
        using var resolvedPathLock = requestedPathLock is null
            ? _laneMutex.Acquire(info.WatchPath)
            : null;

        var recheck = requestedPathLock is null
            ? FindJobForLaneMutation(jobId, watchPath)
            : info;
        if (recheck == null) return new MoveJobOutcome(MoveJobStatus.NotFound);
        if (!string.IsNullOrWhiteSpace(expectedSourceState)
            && !string.Equals(recheck.State, expectedSourceState, StringComparison.OrdinalIgnoreCase))
        {
            return new MoveJobOutcome(
                MoveJobStatus.SourceStateMismatch,
                $"Expected source state {expectedSourceState}, current state is {recheck.State}.");
        }
        if (recheck.State == targetState) return new MoveJobOutcome(MoveJobStatus.Success, NewFolderPath: recheck.FolderPath);

        // BP-03: Ready opens a reissue generation and Progress opens an
        // execution generation. A folder-scoped remote review subject belongs
        // to the delivery that preceded either transition and must stop being
        // canonical before the new attempt can start. The store retains one
        // invalidated copy for diagnosis; review and integration only read the
        // canonical filename.
        if (targetState is TaskStates.Ready or TaskStates.Progress)
        {
            try
            {
                AgentStudio.Pipeline.ReviewSubjectStore.InvalidateForNewAttempt(recheck.FolderPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "review-subject-invalidation-failed job={JobId} from={From} to={To}",
                    jobId,
                    recheck.State,
                    targetState);
                return new MoveJobOutcome(
                    MoveJobStatus.Failure,
                    "The previous review subject could not be invalidated before starting a new attempt.");
            }
        }

        if (IsFlatLayoutJobDir(recheck.FolderPath))
        {
            try
            {
                var key = FlatStorageKey(recheck);
                if (string.IsNullOrWhiteSpace(key))
                    return new MoveJobOutcome(MoveJobStatus.Failure, "Flat-layout task has no key");

                // If the index is missing or stale after a crash, rebuild it
                // before applying the metadata-only transition.
                var byKey = TaskLayoutIndex.ReadByKey(recheck.WatchPath);
                if (!byKey.ContainsKey(key))
                    TaskLayoutIndex.Rebuild(recheck.WatchPath, _logger);

                // Diagnostic (19.07., temporary): an unidentified caller keeps
                // re-escalating 5-human-review cards with actor "system" and no
                // matching log line or decision record. Capture the full stack
                // for every system-side move INTO 5e so the next flip names its
                // caller. Remove once the source is found.
                if (string.Equals(targetState, TaskStates.Escalated, StringComparison.Ordinal))
                    _logger.LogWarning(
                        "escalation-diagnostic key={Key} from={From} to={To} stack={Stack}",
                        key, recheck.State, targetState, Environment.StackTrace);

                var result = TaskLayoutTransition.ChangeState(recheck.WatchPath, key, targetState, _logger);
                if (result.Location == null) return new MoveJobOutcome(MoveJobStatus.NotFound);
                if (result.Changed)
                {
                    TaskJsonFile.UpdateField(recheck.FolderPath, "enteredLaneAt", DateTime.UtcNow.ToString("o"), _logger);
                    ClearIncompatiblePhase(recheck.FolderPath, targetState);
                    RecordLaneChange(recheck.FolderPath, recheck.State, targetState, cause, authorityWrite, reason,
                        transitionCause, transitionDetail);
                    RecordParkedBlocker(recheck.FolderPath, targetState, reason, transitionCause, transitionDetail);
                    _scanner.InvalidateCache();
                    EnqueueEvidence(recheck.WatchPath, recheck.ProjectName, recheck.Id, recheck.State, targetState);
                }
                return new MoveJobOutcome(MoveJobStatus.Success, NewFolderPath: recheck.FolderPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to metadata-move job {JobId} to {State}", jobId, targetState);
                return new MoveJobOutcome(MoveJobStatus.Failure, ex.Message);
            }
        }

        var jobFolderName = Path.GetFileName(recheck.FolderPath);
        var targetSlug = jobFolderName;
        var targetDir = Path.Combine(recheck.WatchPath, targetState, targetSlug);

        // Layer 2 of the duplicate-slug root-cause fix (belt-and-suspenders).
        // A pre-existing target folder means a stale duplicate of this slug was
        // left behind in the target lane. Hard-failing here (the old 409 /
        // TargetFolderExists) stranded Archive-all and forced an operator to
        // fall back to a manual mv/rename. Instead, give the moved folder a
        // globally-unique suffixed slug and proceed. The folder name is the
        // canonical task id, so the scanner self-heals the id field to match
        // the new folder — we also rewrite it eagerly below to avoid a
        // transient divergence warning on the next scan.
        if (Directory.Exists(targetDir))
        {
            targetSlug = LaneSlug.EnsureUnique(recheck.WatchPath, jobFolderName);
            targetDir = Path.Combine(recheck.WatchPath, targetState, targetSlug);
            _logger.LogWarning(
                "move-slug-deduped jobId={JobId} targetState={State} baseSlug={BaseSlug} resolvedSlug={ResolvedSlug}",
                jobId, targetState, jobFolderName, targetSlug);
        }
        else if (File.Exists(targetDir))
        {
            _logger.LogWarning(
                "Cannot move job {JobId} to {State}/{Slug}: target path already exists as a file at {Target}",
                jobId, targetState, targetSlug, targetDir);
            return new MoveJobOutcome(
                MoveJobStatus.TargetFolderExists,
                $"A non-folder path named '{targetSlug}' already exists in {targetState}.");
        }

        try
        {
            var moveFailure = MoveDirectoryWithRetry(
                recheck.FolderPath,
                targetDir,
                operation: "move-job",
                subject: jobId,
                targetState);
            if (moveFailure != null) return moveFailure with { NewFolderPath = null };

            TaskJsonFile.UpdateField(targetDir, "state", targetState, _logger);
            // Lane-entry sort anchor: the task just entered targetState, so
            // re-stamp its entry time. Drives the lane-entry default sort
            // (newest entry on top). Migration paths deliberately skip this.
            TaskJsonFile.UpdateField(targetDir, "enteredLaneAt", DateTime.UtcNow.ToString("o"), _logger);
            ClearIncompatiblePhase(targetDir, targetState);
            // T2b: write the lane-change ledger row to the *new* folder (the
            // source folder is gone after the move above).
            RecordLaneChange(targetDir, recheck.State, targetState, cause, authorityWrite, reason,
                transitionCause, transitionDetail);
            RecordParkedBlocker(targetDir, targetState, reason, transitionCause, transitionDetail);
            // Keep the canonical id in lockstep with the (possibly suffixed)
            // folder name so FindJob resolves the moved folder immediately,
            // without waiting for the scanner's self-heal pass.
            if (!string.Equals(targetSlug, jobFolderName, StringComparison.Ordinal))
                TaskJsonFile.UpdateField(targetDir, "id", targetSlug, _logger);
            // Cycle 2: invalidate the cache synchronously so a POST-then-GET
            // sequence (e.g. drag a card, frontend re-polls) never sees the
            // pre-move snapshot. The 250 ms FileSystemWatcher debounce alone
            // is too slow for that round-trip.
            _scanner.InvalidateCache();
            EnqueueEvidence(recheck.WatchPath, recheck.ProjectName, targetSlug, recheck.State, targetState);
            // Hand the post-move path back to the caller so chat-log writes
            // and follow-up files cannot land in the now-vanished source
            // folder via a stale FindJob result (see MoveJobOutcome docs).
            return new MoveJobOutcome(MoveJobStatus.Success, NewFolderPath: targetDir);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to move job {JobId} to {State}", jobId, targetState);
            return new MoveJobOutcome(MoveJobStatus.Failure, ex.Message);
        }
    }

    private TaskInfo? FindJobForLaneMutation(string jobId, string? watchPath)
    {
        if (!string.IsNullOrWhiteSpace(watchPath)
            && string.Equals(Path.GetFileName(jobId), jobId, StringComparison.Ordinal)
            && jobId.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
        {
            var entry = _scanner.GetWatchPaths()
                .FirstOrDefault(candidate => WatchPathComparison.PathsEqual(candidate.Path, watchPath));
            if (entry is not null)
            {
                foreach (var state in TaskStates.All)
                {
                    var folder = Path.Combine(entry.Path, state, jobId);
                    if (!File.Exists(Path.Combine(folder, "task.json"))) continue;
                    var direct = _scanner.ScanJobFolder(folder, entry, state);
                    if (direct is not null) return direct;
                }
            }
        }

        return _scanner.FindJob(jobId, watchPath);
    }

    /// <summary>
    /// Move a job to <c>2-ready</c> and reorder it to position 1 (next pickup).
    /// Used by the busy-project queue path: when a follow-up arrives for a
    /// non-active job, this promotes the target so the auto-pickup loop will
    /// run it next.
    ///
    /// <para>Other queued jobs that already carry a <see cref="TaskInfo.PendingIntent"/>
    /// keep their relative order in front of this one, so the user's earlier
    /// queued intents are not overtaken. Plain queued jobs (no pending
    /// intent) shuffle down by one.</para>
    /// </summary>
    /// <param name="cause">Ledger actor of the lane change when the task is not yet in <c>2-ready</c> (see <see cref="MoveJob"/>).</param>
    /// <param name="transitionCause">Ledger cause id of that lane change, one of <see cref="LaneChangeCauses"/>.</param>
    /// <param name="transitionDetail">Short qualifier for <paramref name="transitionCause"/>.</param>
    /// <returns>The 1-based position of the target in the new <c>2-ready</c> ordering, or 0 on failure.</returns>
    public int PromoteToReadyTop(
        string jobId,
        string? watchPath = null,
        string? cause = null,
        string? transitionCause = null,
        string? transitionDetail = null,
        string? expectedSourceState = null)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return 0;

        if (info.State != TaskStates.Ready)
        {
            var moved = MoveJob(jobId, TaskStates.Ready, watchPath, cause,
                expectedSourceState: expectedSourceState,
                transitionCause: transitionCause, transitionDetail: transitionDetail);
            if (moved.Status != MoveJobStatus.Success) return 0;
        }

        // Recompute order across all 2-ready jobs in the same project, with
        // the rule above. We only need to bump the moved job; everyone else
        // keeps relative order.
        var ready = _scanner.ScanAllJobs()
            .Where(j => j.WatchPath == info.WatchPath && j.State == TaskStates.Ready)
            .OrderBy(j => j.Order)
            .ToList();

        // Build the new ordering: existing pending-intent jobs first (keep
        // their relative order), then the promoted job, then the rest.
        var pendingHead = ready.Where(j => j.Id != jobId && j.PendingIntent != null).ToList();
        var rest = ready.Where(j => j.Id != jobId && j.PendingIntent == null).ToList();
        var target = ready.FirstOrDefault(j => j.Id == jobId)
                     ?? _scanner.FindJob(jobId, watchPath); // post-move re-fetch
        if (target == null) return 0;

        var ordered = new List<TaskInfo>();
        ordered.AddRange(pendingHead);
        ordered.Add(target);
        ordered.AddRange(rest);

        var step = 10;
        for (int i = 0; i < ordered.Count; i++)
        {
            TaskJsonFile.UpdateOrder(ordered[i].FolderPath, (i + 1) * step, _logger);
        }
        _scanner.InvalidateCache();
        _notifier?.PublishBulkChanged();

        return ordered.FindIndex(j => j.Id == jobId) + 1;
    }

    /// <summary>
    /// Archive a job folder by absolute source path under <c>7-archive/</c>
    /// with a new folder slug. Used by the boot-time stale-progress sweep
    /// (ADR-0020 follow-up) for the residual case where a folder is genuinely
    /// nothing but a directory entry (no <c>task.json</c>, no
    /// <c>cli-output.log</c>): see <see cref="MoveFolderToFailedPickup"/> for
    /// the loud path that handles real orphans.
    /// </summary>
    /// <remarks>
    /// Takes a folder path rather than a jobId because empty stale folders have
    /// no task.json and therefore are not visible to <see cref="TaskScannerService"/>.
    /// Still routes the move + state-field update through this state machine so
    /// callers never write to the state folders directly.
    /// </remarks>
    public MoveJobOutcome ArchiveFolder(string sourceFolder, string newSlug)
        => MoveFolderToState(
            sourceFolder, newSlug, TaskStates.Archive,
            transitionCause: LaneChangeCauses.Archived, transitionDetail: "stale-folder-archive");

    /// <summary>
    /// Move a stale <c>3-progress</c> folder into <c>3a-failed-pickup</c> with
    /// a new folder slug. ADR-0028: pickup failures are loud, not silent;
    /// orphan and empty folders that the boot sweep used to hide in
    /// <c>7-archive</c> now land in the visible failed-pickup lane so the
    /// user sees what the runner could not finish.
    /// </summary>
    /// <remarks>
    /// Same shape as <see cref="ArchiveFolder"/> but targets
    /// <see cref="TaskStates.FailedPickup"/>. Empty stale folders may not have
    /// a <c>task.json</c>; in that case a placeholder is written so the lane
    /// can render a card and the state-field invariant holds.
    /// </remarks>
    public MoveJobOutcome MoveFolderToFailedPickup(string sourceFolder, string newSlug)
        => MoveFolderToState(
            sourceFolder, newSlug, TaskStates.FailedPickup, writePlaceholderJobJson: true,
            transitionCause: LaneChangeCauses.SystemMove, transitionDetail: "dead-letter-failed-pickup");

    /// <summary>
    /// Inverse of <see cref="MoveFolderToFailedPickup"/>: lift a folder
    /// out of <c>3a-failed-pickup</c> back into a live lane (default
    /// <c>2-ready</c>) and rename it back to its pre-dead-letter slug.
    /// Surfaced as <c>POST /api/tasks/{id}/restore-from-failed-pickup</c>
    /// to close the gap that previously forced an operator to fall back
    /// to <c>mv</c> + manual rename - exactly the bypass the
    /// <see cref="AgentStudio.Tests.TaskFolderAccessIsolationTest"/>
    /// and the AGENTS.md "API first" rule are meant to stop.
    ///
    /// <para>Single-state-machine principle: the move + the slug rewrite
    /// both flow through <see cref="MoveFolderToState"/>, the same helper
    /// the dead-letter path uses. No new <see cref="Directory.Move"/>
    /// call site, so the architecture test stays green.</para>
    ///
    /// <para>Idempotency: if the slug is not in
    /// <see cref="TaskStates.FailedPickup"/> the call returns
    /// <see cref="RestoreFromFailedPickupStatus.NoOp"/> when the original
    /// slug already exists in <paramref name="targetState"/> (the
    /// expected "already restored" case) and
    /// <see cref="RestoreFromFailedPickupStatus.NotFound"/> when the
    /// slug is genuinely unknown.</para>
    ///
    /// <para>The pickup-attempt counter is held in
    /// <c>ProjectRunner._pickupAttempts</c> and was already cleared at
    /// dead-letter time (see <c>DeadLetterUnrecoverableFolder</c>), so
    /// the next pickup attempt on the restored slug starts at 0 without
    /// any extra reset here. If a future schema adds a persisted counter
    /// to <c>task.json</c>, reset it inside this method.</para>
    /// </summary>
    /// <param name="jobId">The dead-letter slug under
    /// <c>3a-failed-pickup</c>, e.g. <c>foo-pickup-failed-2026-05-08</c>.</param>
    /// <param name="watchPath">Workspace root that disambiguates the slug
    /// when the same id appears in two projects.</param>
    /// <param name="keepDeadLetterSlug">When <c>true</c>, keep the
    /// <c>-pickup-failed-&lt;utc&gt;</c> suffix on the restored folder.
    /// Useful when the operator wants the trail visible in the slug
    /// itself.</param>
    /// <param name="targetState">Target lane. Defaults to
    /// <see cref="TaskStates.Ready"/>.</param>
    public RestoreFromFailedPickupOutcome RestoreFromFailedPickup(
        string jobId,
        string? watchPath,
        bool keepDeadLetterSlug,
        string? targetState = null)
    {
        var resolvedTargetState = string.IsNullOrWhiteSpace(targetState) ? TaskStates.Ready : targetState!;
        if (!TaskStates.All.Contains(resolvedTargetState))
        {
            return new RestoreFromFailedPickupOutcome(
                RestoreFromFailedPickupStatus.Failure,
                Message: $"Invalid target state: {resolvedTargetState}");
        }

        // Parse the original slug back out of the dead-letter name. When
        // the input does not match the dead-letter shape the call may
        // still be a legitimate restore (operator renamed manually before
        // calling this); fall back to the input slug as both source and
        // original so the move can still proceed.
        var hasDeadLetterShape = PickupFailureLog.TryParseFailedPickupSlug(jobId, out var parsedOriginal);
        var originalSlug = hasDeadLetterShape ? parsedOriginal : jobId;
        var restoredSlug = keepDeadLetterSlug ? jobId : originalSlug;

        var info = _scanner.FindJob(jobId, watchPath);

        // Idempotency: nothing to restore in 3a-failed-pickup; check
        // whether the original slug is already in the target lane.
        if (info == null || info.State != TaskStates.FailedPickup)
        {
            var existing = _scanner.FindJob(restoredSlug, watchPath);
            if (existing != null && existing.State == resolvedTargetState)
            {
                return new RestoreFromFailedPickupOutcome(
                    RestoreFromFailedPickupStatus.NoOp,
                    RestoredSlug: restoredSlug,
                    OriginalSlug: originalSlug,
                    SourceSlug: jobId,
                    Message: $"Folder is already in {resolvedTargetState} under '{restoredSlug}'.");
            }

            return new RestoreFromFailedPickupOutcome(
                RestoreFromFailedPickupStatus.NotFound,
                OriginalSlug: originalSlug,
                SourceSlug: jobId,
                Message: $"No folder found in {TaskStates.FailedPickup} with slug '{jobId}'.");
        }

        if (!hasDeadLetterShape && !keepDeadLetterSlug)
        {
            // The folder is in 3a-failed-pickup but its slug does not
            // match the auto-generated dead-letter pattern. Refuse to
            // guess at a rename; surface the slug-shape problem to the
            // caller, who can retry with keepDeadLetterSlug=true.
            return new RestoreFromFailedPickupOutcome(
                RestoreFromFailedPickupStatus.InvalidSlug,
                OriginalSlug: originalSlug,
                SourceSlug: jobId,
                Message: $"Slug '{jobId}' does not match the dead-letter shape '<slug>-pickup-failed-<yyyy-mm-dd>'. " +
                         "Retry with keepDeadLetterSlug=true to restore without a rename.");
        }

        var outcome = MoveFolderToState(
            info.FolderPath, restoredSlug, resolvedTargetState,
            transitionCause: LaneChangeCauses.OperatorMove, transitionDetail: "restore-from-failed-pickup");
        return outcome.Status switch
        {
            MoveJobStatus.Success => new RestoreFromFailedPickupOutcome(
                RestoreFromFailedPickupStatus.Success,
                RestoredSlug: restoredSlug,
                OriginalSlug: originalSlug,
                SourceSlug: jobId),
            MoveJobStatus.NotFound => new RestoreFromFailedPickupOutcome(
                RestoreFromFailedPickupStatus.NotFound,
                OriginalSlug: originalSlug,
                SourceSlug: jobId,
                Message: outcome.Message),
            MoveJobStatus.TargetFolderExists => new RestoreFromFailedPickupOutcome(
                RestoreFromFailedPickupStatus.TargetFolderExists,
                RestoredSlug: restoredSlug,
                OriginalSlug: originalSlug,
                SourceSlug: jobId,
                Message: outcome.Message),
            _ => new RestoreFromFailedPickupOutcome(
                RestoreFromFailedPickupStatus.Failure,
                OriginalSlug: originalSlug,
                SourceSlug: jobId,
                Message: outcome.Message)
        };
    }

    private MoveJobOutcome MoveFolderToState(
        string sourceFolder,
        string newSlug,
        string targetState,
        bool writePlaceholderJobJson = false,
        string? transitionCause = null,
        string? transitionDetail = null)
    {
        if (string.IsNullOrWhiteSpace(newSlug))
            return new MoveJobOutcome(MoveJobStatus.Failure, "Slug must not be empty");
        if (!TaskStates.All.Contains(targetState))
            return new MoveJobOutcome(MoveJobStatus.Failure, $"Invalid state: {targetState}");
        if (!Directory.Exists(sourceFolder))
            return new MoveJobOutcome(MoveJobStatus.NotFound);

        var stateDir = Path.GetDirectoryName(sourceFolder);
        var watchPath = stateDir != null ? Path.GetDirectoryName(stateDir) : null;
        if (string.IsNullOrEmpty(watchPath))
            return new MoveJobOutcome(MoveJobStatus.Failure, "Source folder is not under a state directory");

        // F21: serialise lane writers on this watch path. Re-check the
        // source existence inside the mutex because the StaleProgressArchiver
        // and the runner pickup loop can both target the same folder at boot.
        using var _ = _laneMutex.Acquire(watchPath);
        if (!Directory.Exists(sourceFolder))
            return new MoveJobOutcome(MoveJobStatus.NotFound);

        var targetDir = Path.Combine(watchPath, targetState, newSlug);
        if (Directory.Exists(targetDir))
        {
            _logger.LogWarning(
                "Cannot move {Source} to {State}/{Slug}: target folder already exists at {Target}",
                sourceFolder, targetState, newSlug, targetDir);
            return new MoveJobOutcome(
                MoveJobStatus.TargetFolderExists,
                $"A folder named '{newSlug}' already exists in {targetState}.");
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetDir)!);
            var moveFailure = MoveDirectoryWithRetry(
                sourceFolder,
                targetDir,
                operation: "move-folder-to-state",
                subject: newSlug,
                targetState);
            if (moveFailure != null) return moveFailure;

            var jobJsonPath = Path.Combine(targetDir, "task.json");
            var enteredLaneAt = DateTime.UtcNow.ToString("o");
            if (File.Exists(jobJsonPath))
            {
                TaskJsonFile.UpdateField(targetDir, "state", targetState, _logger);
                // Lane-entry anchor: archive/failed-pickup/restore are genuine
                // lane changes, so re-stamp the entry time like a normal move.
                TaskJsonFile.UpdateField(targetDir, "enteredLaneAt", enteredLaneAt, _logger);
                ClearIncompatiblePhase(targetDir, targetState);
                // T2b: archive / dead-letter / restore are real lane crossings;
                // record them in the ledger like a normal move. From-lane is the
                // source folder's parent directory name.
                RecordLaneChange(targetDir, Path.GetFileName(stateDir) ?? "", targetState, cause: null,
                    transitionCause: transitionCause, transitionDetail: transitionDetail);
            }
            else if (writePlaceholderJobJson)
            {
                // The empty-stale path lacks any metadata. Synthesize a minimal
                // task.json so the scanner sees the card and the state-field
                // invariant ("every job folder has a state field on disk") holds.
                var placeholder = $"{{\"id\":\"{newSlug}\",\"title\":\"{newSlug}\",\"state\":\"{targetState}\",\"order\":1,\"agent\":\"unknown\",\"enteredLaneAt\":\"{enteredLaneAt}\"}}";
                File.WriteAllText(jobJsonPath, placeholder);
            }
            _scanner.InvalidateCache();
            // Archive / dead-letter / restore are real lane crossings: capture
            // their evidence too. Project label falls back to the watch-path
            // leaf since this path has no TaskInfo/ProjectName.
            EnqueueEvidence(watchPath, Path.GetFileName(watchPath), newSlug, Path.GetFileName(stateDir) ?? string.Empty, targetState);
            return new MoveJobOutcome(MoveJobStatus.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to move {Source} to {State}/{Slug}", sourceFolder, targetState, newSlug);
            return new MoveJobOutcome(MoveJobStatus.Failure, ex.Message);
        }
    }

    public bool DeleteJob(string jobId, string? watchPath = null)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info != null)
        {
            // F21: serialise with concurrent lane writers (move, archive,
            // dead-letter) so we cannot delete a folder a peer thread is in
            // the middle of renaming.
            using var _ = _laneMutex.Acquire(info.WatchPath);
            var recheck = _scanner.FindJob(jobId, watchPath);
            if (recheck == null) return false;
            // F21: a peer lane-writer may have moved the job while we waited
            // on the mutex. Deleting it at its NEW location would let both
            // writers report success (lost update: the mover believes the
            // card now lives in the target lane). The delete targeted the
            // folder resolved before the wait — if the job no longer lives
            // there, fail cleanly and let the caller re-issue.
            if (!WatchPathComparison.PathsEqual(recheck.FolderPath, info.FolderPath)) return false;

            try
            {
                Directory.Delete(recheck.FolderPath, true);
                if (IsUnderFlatLayout(recheck.FolderPath))
                    TaskLayoutIndex.Rebuild(recheck.WatchPath, _logger);
                _scanner.InvalidateCache();
                _notifier?.PublishDeleted(recheck.ProjectName, recheck.Id, recheck.WatchPath);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete job {JobId}", jobId);
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Delete a scanner-invisible residue folder from a terminal lane.
    /// </summary>
    public OrphanFolderDeleteResult DeleteOrphanFolder(string watchPath, string lane, string folder)
    {
        if (string.IsNullOrWhiteSpace(watchPath)
            || string.IsNullOrWhiteSpace(lane)
            || string.IsNullOrWhiteSpace(folder)
            || folder.Contains('/') || folder.Contains('\\')
            || folder.Contains("..", StringComparison.Ordinal)
            || folder.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return new OrphanFolderDeleteResult(OrphanFolderDeleteStatus.InvalidRequest, "watchPath, lane, and folder are required; folder must be a single folder name.");
        }

        if (!IsOrphanDeleteLane(lane))
        {
            return new OrphanFolderDeleteResult(OrphanFolderDeleteStatus.NonTerminalLane, "Orphan folder deletion is only allowed under terminal lanes.");
        }

        var entry = _scanner.GetWatchPaths()
            .FirstOrDefault(e => WatchPathComparison.PathsEqual(e.Path, watchPath));
        if (entry == null)
        {
            return new OrphanFolderDeleteResult(OrphanFolderDeleteStatus.NotFound, "Watch path not found.");
        }

        var lanePath = Path.GetFullPath(Path.Combine(entry.Path, lane));
        var target = Path.GetFullPath(Path.Combine(lanePath, folder));
        var lanePrefix = lanePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(lanePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return new OrphanFolderDeleteResult(OrphanFolderDeleteStatus.InvalidRequest, "Folder must resolve inside the requested lane.");
        }

        using var _ = _laneMutex.Acquire(entry.Path);
        if (!Directory.Exists(target))
            return new OrphanFolderDeleteResult(OrphanFolderDeleteStatus.NotFound, "Folder not found.");
        if (File.Exists(Path.Combine(target, "task.json")))
            return new OrphanFolderDeleteResult(OrphanFolderDeleteStatus.HasJobJson, "Folder contains task.json; use normal task deletion.");

        try
        {
            Directory.Delete(target, true);
            _scanner.InvalidateCache();
            _notifier?.PublishDeleted(entry.Name, folder, entry.Path);
            _logger.LogInformation(
                "task-orphan-folder-deleted watchPath={WatchPath} lane={Lane} folder={Folder} path={Path}",
                entry.Path, lane, folder, target);
            return new OrphanFolderDeleteResult(OrphanFolderDeleteStatus.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "task-orphan-folder-delete-failed watchPath={WatchPath} lane={Lane} folder={Folder} path={Path}",
                entry.Path, lane, folder, target);
            return new OrphanFolderDeleteResult(OrphanFolderDeleteStatus.Failure, ex.Message);
        }
    }

    private static bool IsOrphanDeleteLane(string lane)
        => string.Equals(lane, TaskStates.Archive, StringComparison.Ordinal)
           || string.Equals(lane, TaskStates.Completed, StringComparison.Ordinal);

    private void ClearIncompatiblePhase(string folderPath, string targetState)
    {
        var jobJsonPath = Path.Combine(folderPath, "task.json");
        if (!File.Exists(jobJsonPath)) return;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(jobJsonPath));
            if (!doc.RootElement.TryGetProperty("phase", out var phaseEl)
                || phaseEl.ValueKind != JsonValueKind.String)
            {
                return;
            }

            var phase = phaseEl.GetString();
            if (string.IsNullOrWhiteSpace(phase) || LifecyclePhases.IsAllowed(targetState, phase)) return;

            TaskJsonFile.UpdateField(folderPath, "phase", "", _logger);
            _logger.LogInformation(
                "task-phase-cleared jobFolder={Folder} targetState={State} previousPhase={Phase}",
                folderPath, targetState, phase);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clear incompatible phase in {Folder}", folderPath);
        }
    }

    /// <summary>
    /// T2b (ASS-1740): append one <see cref="TimelineEventKinds.LaneChanged"/> row
    /// to the task's ledger for a lane crossing. This is the append-only HISTORY
    /// the task.json <c>enteredLaneAt</c> field could only ever hold for the latest
    /// move. The row carries from / to / when (the event <c>Ts</c>) and the trigger
    /// (the <see cref="TimelineEvent.Actor"/>); the ASS-1724 branch-tip /
    /// work-branch-head anchors are recorded alongside in <c>task.json.provenance</c>
    /// and meshed back in at read time by the unified task reader. Best-effort and
    /// fully guarded - a ledger write must never undo the move that already landed.
    ///
    /// <para>The row also names WHY the lane changed (<c>details.cause</c>, one of
    /// <see cref="LaneChangeCauses"/>, plus an optional <c>details.causeDetail</c>
    /// qualifier) when the caller knows; a human actor without an explicit cause
    /// gets the operator cause derived from the lane pair. Rows without the
    /// field are legacy rows, which the analysis still classifies from the
    /// neighbouring rows. Additive: readers tolerate absence.</para>
    /// </summary>
    private void RecordLaneChange(
        string jobFolderPath,
        string fromState,
        string toState,
        string? cause,
        AttemptWriteReference? authorityWrite = null,
        string? reason = null,
        string? transitionCause = null,
        string? transitionDetail = null)
    {
        if (_timeline == null) return;
        try
        {
            var actor = string.IsNullOrWhiteSpace(cause) ? TimelineActors.System : cause!.Trim();
            var details = new Dictionary<string, string>
            {
                ["from"] = fromState ?? "",
                ["to"] = toState ?? "",
            };
            if (!string.IsNullOrWhiteSpace(reason))
                details["reason"] = reason.Trim();
            var resolvedCause = ResolveTransitionCause(actor, fromState, toState, transitionCause);
            if (resolvedCause is not null)
                details[LaneChangeCauses.DetailKey] = resolvedCause;
            if (!string.IsNullOrWhiteSpace(transitionDetail))
                details[LaneChangeCauses.DetailQualifierKey] = transitionDetail.Trim();
            if (authorityWrite is not null)
            {
                details["attemptId"] = authorityWrite.AttemptId;
                details["fence"] = authorityWrite.Fence.ToString(System.Globalization.CultureInfo.InvariantCulture);
                details["authorityEpoch"] = authorityWrite.AuthorityEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture);
                details["idempotencyKey"] = authorityWrite.IdempotencyKey;
            }
            _timeline.Append(
                jobFolderPath,
                TimelineEventKinds.LaneChanged,
                actor,
                summary: $"{fromState} → {toState}",
                runId: authorityWrite?.AttemptId,
                details: details);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record lane-change ledger row for {Folder}", jobFolderPath);
        }
    }

    /// <summary>
    /// The cause id written onto the ledger row: the explicit one when the
    /// transition site supplied it, the lane-pair derived operator cause for a
    /// human actor, and nothing for an automatic move whose site does not (yet)
    /// name its cause - that row stays inferable by the analysis instead of
    /// carrying a generic id that would hide the real cause.
    /// </summary>
    internal static string? ResolveTransitionCause(string actor, string? fromState, string? toState, string? transitionCause)
    {
        if (!string.IsNullOrWhiteSpace(transitionCause)) return transitionCause.Trim();
        return IsHumanActor(actor) ? LaneChangeCauses.ForOperatorMove(fromState, toState) : null;
    }

    private static bool IsHumanActor(string actor)
        => actor.StartsWith("human", StringComparison.OrdinalIgnoreCase)
           && (actor.Length == 5 || actor[5] == ':');

    /// <summary>
    /// AGT-2492: keep the machine-readable park marker in step with the lane.
    /// Entering a human-decision lane records WHAT the card waits for (blocker
    /// type plus a condition a sweep can re-check); leaving one clears the
    /// marker. Doing it here, at the single lane-change choke point, means every
    /// park path gets the marker - the escalation funnel, the remote review
    /// park, the UI-iteration gate, an operator drag - without each of the
    /// ~15 call sites having to remember.
    ///
    /// <para>Best-effort by design: the move has already landed when this runs,
    /// so a marker write must never undo it.</para>
    /// </summary>
    private void RecordParkedBlocker(
        string jobFolderPath,
        string toState,
        string? reason,
        string? transitionCause = null,
        string? transitionDetail = null)
    {
        try
        {
            var record = ParkedBlockerCatalog.Build(
                toState, reason, DateTime.UtcNow, transitionCause, transitionDetail);
            if (record is null) ParkedBlockerMarker.Clear(jobFolderPath, _logger);
            else ParkedBlockerMarker.Write(jobFolderPath, record, _logger);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record parked-blocker marker for {Folder}", jobFolderPath);
        }
    }

    public bool ChangeProject(string jobId, string targetWatchPath, string? watchPath = null)
    {
        var entries = _scanner.GetWatchPaths();
        // Path-aware target resolution, and carry the RESOLVED entry path
        // forward so the copy lands under the canonical project directory even
        // when the caller passed a differently-spelled watchPath (D1: change
        // project by PROJ-ID/path). A raw ordinal `==` matched no entry when
        // the spelling differed. See WatchPathComparison (AGT-1940).
        var targetEntry = entries.FirstOrDefault(e => WatchPathComparison.PathsEqual(e.Path, targetWatchPath));
        if (targetEntry == null) return false;
        var canonicalTarget = targetEntry.Path;

        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return false;
        if (WatchPathComparison.PathsEqual(info.WatchPath, canonicalTarget)) return true;

        // F21: take both source and target lane mutexes. Lock order is
        // ordinal-lowercased ascending so two simultaneous cross-project
        // moves (A->B and B->A) cannot deadlock.
        var (firstKey, secondKey) = string.CompareOrdinal(
            info.WatchPath.ToLowerInvariant(),
            canonicalTarget.ToLowerInvariant()) <= 0
            ? (info.WatchPath, canonicalTarget)
            : (canonicalTarget, info.WatchPath);
        using var _outerLock = _laneMutex.Acquire(firstKey);
        using var _innerLock = _laneMutex.Acquire(secondKey);

        var recheck = _scanner.FindJob(jobId, watchPath);
        if (recheck == null) return false;
        if (WatchPathComparison.PathsEqual(recheck.WatchPath, canonicalTarget)) return true;

        var destinationProject = _projectRegistry?.FindByStorageLocation(canonicalTarget);
        if (destinationProject == null)
        {
            _logger.LogWarning("change-project-rejected job={JobId} reason=destination-not-in-registry target={Target}", jobId, canonicalTarget);
            return false;
        }

        // A cross-project move is a re-key, not a raw folder copy. Reserve a
        // fresh destination key before touching the source. The monotonic
        // counter may advance on a failed attempt, which is intentional: keys
        // are never re-used.
        string destinationKey;
        int destinationNumber;
        string targetDir;
        do
        {
            var destinationSeq = _projectRegistry!.IssueNextTaskKey(destinationProject.Id);
            destinationKey = $"{destinationProject.ShortCode}-{destinationSeq}";
            TaskStorageLayout.TryParseKeyNumber(destinationKey, out destinationNumber);
            targetDir = TaskStorageLayout.JobDir(canonicalTarget, destinationNumber, destinationKey);
        } while (Directory.Exists(targetDir));

        var operationId = Guid.NewGuid().ToString("N");
        var sourceParent = Path.GetDirectoryName(recheck.FolderPath)!;
        var sourceStage = Path.Combine(sourceParent, $".moving-{Path.GetFileName(recheck.FolderPath)}-{operationId}");
        var targetParent = Path.GetDirectoryName(targetDir)!;
        var targetStage = Path.Combine(targetParent, $".incoming-{destinationKey}-{operationId}");
        var sourceStaged = false;
        var targetPromoted = false;
        var referenceUpdates = BuildReferenceUpdates(recheck.Key, destinationKey, recheck.FolderPath, targetDir);
        var appliedReferenceUpdates = new List<TaskReferenceUpdate>();

        try
        {
            Directory.CreateDirectory(targetParent);
            // Rename first on the source volume. If any later step fails the
            // original folder can be restored atomically, so no task can land
            // as an archived orphan or disappear between copy and delete.
            Directory.Move(recheck.FolderPath, sourceStage);
            sourceStaged = true;
            CopyDirectory(sourceStage, targetStage);
            TaskJsonFile.UpdateFieldOrThrow(targetStage, "key", destinationKey);
            TaskJsonFile.UpdateFieldOrThrow(targetStage, "previousKey", recheck.Key ?? "");
            Directory.Move(targetStage, targetDir);
            targetPromoted = true;

            // References are part of the migration transaction. Apply every
            // rewrite before retiring the source so a failed write can restore
            // the old references, remove the destination, and rename the
            // staged source back into place.
            foreach (var update in referenceUpdates)
            {
                TaskJsonFile.UpdateFieldOrThrow(update.FolderPath, "references", update.Rewritten);
                appliedReferenceUpdates.Add(update);
            }

            // This is the commit point. Nothing after the source deletion may
            // roll the operation back because the original tree no longer
            // exists.
            Directory.Delete(sourceStage, true);
            sourceStaged = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "change-project-rollback job={JobId} oldKey={OldKey} attemptedKey={NewKey} target={Target}",
                jobId, recheck.Key, destinationKey, canonicalTarget);
            try
            {
                foreach (var update in appliedReferenceUpdates.AsEnumerable().Reverse())
                    TaskJsonFile.UpdateFieldOrThrow(update.FolderPath, "references", update.Original);
                if (targetPromoted && Directory.Exists(targetDir)) Directory.Delete(targetDir, true);
                if (Directory.Exists(targetStage)) Directory.Delete(targetStage, true);
                if (sourceStaged && Directory.Exists(sourceStage) && !Directory.Exists(recheck.FolderPath))
                    Directory.Move(sourceStage, recheck.FolderPath);
                TaskLayoutIndex.Rebuild(recheck.WatchPath, _logger);
                TaskLayoutIndex.Rebuild(canonicalTarget, _logger);
                _scanner.InvalidateCache();
            }
            catch (Exception rollbackEx)
            {
                _logger.LogCritical(rollbackEx,
                    "change-project-rollback-failed job={JobId} sourceStage={SourceStage} target={Target}",
                    jobId, sourceStage, targetDir);
            }
            return false;
        }

        try
        {
            TaskLayoutIndex.Rebuild(recheck.WatchPath, _logger);
            TaskLayoutIndex.Rebuild(canonicalTarget, _logger);
            _scanner.InvalidateCache();
            _notifier?.PublishMoved(destinationProject.DisplayName, jobId, canonicalTarget, recheck.State, recheck.State);
        }
        catch (Exception ex)
        {
            // The move and its reference rewrites are already committed. A
            // cache, index, or notification failure must not delete the only
            // durable task copy.
            _logger.LogError(ex,
                "change-project-post-commit-refresh-failed job={JobId} newKey={NewKey} destinationProject={DestinationProject}",
                jobId, destinationKey, destinationProject.Id);
            _scanner.InvalidateCache();
        }

        _logger.LogInformation(
            "change-project-complete job={JobId} oldKey={OldKey} newKey={NewKey} source={Source} destinationProject={DestinationProject}",
            jobId, recheck.Key, destinationKey, recheck.WatchPath, destinationProject.Id);
        return true;
    }

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(sourceDir))
            CopyDirectory(dir, Path.Combine(targetDir, Path.GetFileName(dir)));
    }

    private List<TaskReferenceUpdate> BuildReferenceUpdates(
        string? oldKey,
        string newKey,
        string movedSourceFolder,
        string movedDestinationFolder)
    {
        var updates = new List<TaskReferenceUpdate>();
        if (string.IsNullOrWhiteSpace(oldKey)) return updates;
        foreach (var task in _scanner.ScanAllJobsWithArchive())
        {
            var refs = task.References;
            if (refs == null || !refs.Enumerate().Any(edge =>
                    string.Equals(edge.Target, oldKey, StringComparison.OrdinalIgnoreCase))) continue;
            var rewritten = new TaskReferences
            {
                DependsOn = ReplaceDependencies(refs.DependsOn, oldKey, newKey),
                RelatedTo = Replace(refs.RelatedTo, oldKey, newKey),
                BlockedBy = Replace(refs.BlockedBy, oldKey, newKey),
                Supersedes = Replace(refs.Supersedes, oldKey, newKey),
                Workbenches = [.. refs.Workbenches],
            };
            var folder = string.Equals(task.FolderPath, movedSourceFolder, StringComparison.OrdinalIgnoreCase)
                ? movedDestinationFolder
                : task.FolderPath;
            updates.Add(new TaskReferenceUpdate(folder, refs, rewritten));
        }
        return updates;
    }

    private static List<string> Replace(IEnumerable<string> values, string oldKey, string newKey)
        => values.Select(value => string.Equals(value, oldKey, StringComparison.OrdinalIgnoreCase) ? newKey : value).ToList();

    private static List<TaskDependencyReference> ReplaceDependencies(
        IEnumerable<TaskDependencyReference> values,
        string oldKey,
        string newKey)
        => values.Select(value => string.Equals(value.Key, oldKey, StringComparison.OrdinalIgnoreCase)
            ? value with { Key = newKey }
            : value).ToList();

    private sealed record TaskReferenceUpdate(
        string FolderPath,
        TaskReferences Original,
        TaskReferences Rewritten);

    public void EnsureStateFoldersAndMigrate()
    {
        foreach (var entry in _scanner.GetWatchPaths())
        {
            var watchPath = entry.Path;
            if (!Directory.Exists(watchPath))
            {
                Directory.CreateDirectory(watchPath);
            }

            Directory.CreateDirectory(TaskStorageLayout.JobsRoot(watchPath));
            Directory.CreateDirectory(TaskStorageLayout.IndexRoot(watchPath));

            // Derived index (id/by-key.json + by-state.json) is rebuilt from
            // the task.json source-of-truth so it always reflects on-disk state.
            // No data-moving migration runs at boot: the layout is fixed and
            // there is a single local instance (an auto-migrating boot against
            // the shared workspace is exactly what corrupted it twice).
            TaskLayoutIndex.Rebuild(watchPath, _logger);
        }
    }

    private static bool IsFlatLayoutJobDir(string jobDir)
        => IsUnderFlatLayout(jobDir) && File.Exists(Path.Combine(jobDir, "task.json"));

    private static bool IsUnderFlatLayout(string jobDir)
    {
        var bucketDir = Path.GetDirectoryName(jobDir);
        var jobsDir = bucketDir == null ? null : Path.GetDirectoryName(bucketDir);
        return string.Equals(Path.GetFileName(jobsDir), TaskStorageLayout.JobsDirName, StringComparison.Ordinal);
    }

    private static string FlatStorageKey(TaskInfo info)
        => !string.IsNullOrWhiteSpace(info.Key) ? info.Key! : Path.GetFileName(info.FolderPath);

    /// <summary>
    /// Place <paramref name="jobId"/> at slot <paramref name="targetIndex"/>
    /// in its current lane (within the same project / watch path) and
    /// rewrite every job's <c>order</c> field to a dense 1..N sequence so
    /// the resulting order is stable. Used by the move endpoint when a
    /// drag-and-drop cross-lane drop carries a desired insertion slot:
    /// without this the moved folder keeps its source-lane <c>order</c>
    /// value and snaps to a position the user did not choose.
    /// </summary>
    /// <returns><c>true</c> when the job was found and the lane was
    /// rewritten; <c>false</c> when the job cannot be located.</returns>
    public bool SetOrderInLane(string jobId, string? watchPath, int targetIndex)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return false;

        var laneJobs = _scanner.ScanAllJobs()
            .Where(j => j.WatchPath == info.WatchPath && j.State == info.State)
            .OrderBy(j => j.Order)
            .ThenBy(j => j.Id, StringComparer.Ordinal)
            .ToList();

        var moved = laneJobs.FirstOrDefault(j => j.Id == jobId);
        if (moved == null) return false;

        var others = laneJobs.Where(j => j.Id != jobId).ToList();
        var slot = Math.Clamp(targetIndex, 0, others.Count);
        var ordered = new List<TaskInfo>(others.Count + 1);
        ordered.AddRange(others.Take(slot));
        ordered.Add(moved);
        ordered.AddRange(others.Skip(slot));

        for (int i = 0; i < ordered.Count; i++)
        {
            TaskJsonFile.UpdateOrder(ordered[i].FolderPath, i + 1, _logger);
        }
        _scanner.InvalidateCache();
        return true;
    }

    public bool ReorderJobs(List<TaskOrderItem> jobs)
    {
        if (jobs.Count == 0) return true;

        // One scan, then dict lookup per item. The previous shape called
        // _scanner.FindJob(jobId, watchPath) inside the loop, and FindJob
        // re-runs ScanAllJobs (full disk walk) every time — O(N x M) for an
        // N-card reorder on an M-job board, which made consecutive drags
        // and the click-after-drop interaction lag visibly. Lookup key is
        // (watchPath, jobId) so the same id in two workspaces still resolves
        // to the right folder. Case-insensitive watchPath match mirrors
        // FindJob's own comparison.
        var byKey = new Dictionary<(string watchPath, string jobId), string>();
        foreach (var info in _scanner.ScanAllJobs())
        {
            byKey[(info.WatchPath.ToLowerInvariant(), info.Id)] = info.FolderPath;
        }

        for (int i = 0; i < jobs.Count; i++)
        {
            var job = jobs[i];
            var watchKey = (job.WatchPath ?? string.Empty).ToLowerInvariant();
            string? folder;
            if (!byKey.TryGetValue((watchKey, job.JobId), out folder))
            {
                // Fall back to id-only lookup when the caller omits watchPath
                // (legacy clients) — pick the first match in a deterministic
                // order so the operation is still useful.
                folder = byKey
                    .Where(kv => kv.Key.jobId == job.JobId)
                    .Select(kv => kv.Value)
                    .FirstOrDefault();
                if (folder == null) continue;
            }
            TaskJsonFile.UpdateOrder(folder, i + 1, _logger);
        }
        _scanner.InvalidateCache();
        // A bulk reorder rewrites many order fields, possibly across lanes;
        // a single "re-pull suggested" nudge is cheaper and more robust than
        // emitting a per-row event for each touched card.
        _notifier?.PublishBulkChanged();
        return true;
    }

    /// <summary>
    /// One-shot maintenance sweep for the duplicate-slug root cause: finds
    /// folders that share the same slug across two or more lanes (the state
    /// that produced the recurring <c>409 … 'slug' already exists in
    /// 7-archive</c>), keeps the richest copy in place (largest on disk, with
    /// newest mtime as the tie-break), and neutralises every other copy by
    /// renaming it with a leading underscore. The scanner skips
    /// <c>_</c>-prefixed folders, so the stale shell drops off the board
    /// without deleting any data — an operator can inspect or remove it later.
    ///
    /// <para>Idempotent: a second run finds nothing because each survivor is
    /// the only un-prefixed folder for its slug. The rename routes through
    /// this state machine — the single <see cref="Directory.Move"/> owner — so
    /// the API-first folder-isolation contract holds. Lane mutex is held per
    /// watch path so the sweep cannot race a live move/create.</para>
    /// </summary>
    public SlugDedupeReport DedupeSlugFolders()
    {
        var groups = new List<SlugDedupeGroup>();
        var renamedTotal = 0;

        foreach (var entry in _scanner.GetWatchPaths())
        {
            var watchPath = entry.Path;
            if (!Directory.Exists(watchPath)) continue;

            using var _ = _laneMutex.Acquire(watchPath);

            // slug -> every live (un-neutralised) folder carrying that slug,
            // across all lanes of this watch path.
            var bySlug = new Dictionary<string, List<DedupeCandidate>>(StringComparer.Ordinal);
            foreach (var state in TaskStates.All)
            {
                var laneDir = Path.Combine(watchPath, state);
                if (!Directory.Exists(laneDir)) continue;
                foreach (var folder in Directory.GetDirectories(laneDir))
                {
                    var slug = Path.GetFileName(folder);
                    if (slug.StartsWith('_')) continue; // already neutralised / scanner-ignored
                    if (!bySlug.TryGetValue(slug, out var list))
                        bySlug[slug] = list = new List<DedupeCandidate>();
                    list.Add(new DedupeCandidate(folder, state, FolderSizeBytes(folder), FolderLastWriteUtc(folder)));
                }
            }

            var sweptHere = false;
            foreach (var (slug, candidates) in bySlug)
            {
                if (candidates.Count < 2) continue;

                var winner = candidates
                    .OrderByDescending(c => c.SizeBytes)
                    .ThenByDescending(c => c.LastWriteUtc)
                    .First();

                var neutralised = new List<string>();
                foreach (var loser in candidates.Where(c => !ReferenceEquals(c, winner)))
                {
                    var renamed = NeutralizeFolder(loser.FolderPath);
                    if (renamed != null)
                    {
                        neutralised.Add(renamed);
                        renamedTotal++;
                    }
                }

                if (neutralised.Count == 0) continue;
                sweptHere = true;
                groups.Add(new SlugDedupeGroup(slug, watchPath, winner.State, winner.SizeBytes, neutralised));
                _logger.LogWarning(
                    "slug-dedupe-sweep slug={Slug} watchPath={WatchPath} keptLane={KeptLane} keptBytes={KeptBytes} neutralised={Neutralised}",
                    slug, watchPath, winner.State, winner.SizeBytes, neutralised.Count);
            }

            if (sweptHere) _scanner.InvalidateCache();
        }

        return new SlugDedupeReport(groups.Count, renamedTotal, groups);
    }

    private sealed record DedupeCandidate(string FolderPath, string State, long SizeBytes, DateTime LastWriteUtc);

    private static long FolderSizeBytes(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
        }
        catch { return 0L; }
    }

    private static DateTime FolderLastWriteUtc(string folder)
    {
        try { return Directory.GetLastWriteTimeUtc(folder); }
        catch { return DateTime.MinValue; }
    }

    /// <summary>
    /// Rename <paramref name="folderPath"/> in place with a leading underscore
    /// so the scanner ignores it. Resolves an underscore-name collision with a
    /// numeric suffix. Returns the new folder name, or null on failure.
    /// </summary>
    private string? NeutralizeFolder(string folderPath)
    {
        var parent = Path.GetDirectoryName(folderPath);
        var name = Path.GetFileName(folderPath);
        if (string.IsNullOrEmpty(parent)) return null;

        var target = Path.Combine(parent, "_" + name);
        for (var n = 2; Directory.Exists(target); n++)
            target = Path.Combine(parent, $"_{name}-{n}");

        try
        {
            var moveFailure = MoveDirectoryWithRetry(
                folderPath,
                target,
                operation: "slug-dedupe-neutralize",
                subject: name,
                targetState: Path.GetFileName(parent));
            if (moveFailure != null) return null;
            return Path.GetFileName(target);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "slug-dedupe-sweep failed to neutralise {Folder}", folderPath);
            return null;
        }
    }

    private MoveJobOutcome? MoveDirectoryWithRetry(
        string source,
        string target,
        string operation,
        string subject,
        string targetState)
    {
        Exception? last = null;

        for (var attempt = 1; attempt <= DirectoryMoveMaxAttempts; attempt++)
        {
            try
            {
                Directory.Move(source, target);
                if (attempt > 1)
                {
                    _logger.LogInformation(
                        "task-folder-move-recovered operation={Operation} subject={Subject} targetState={TargetState} attempts={Attempts}",
                        operation, subject, targetState, attempt);
                }
                return null;
            }
            catch (Exception ex) when (IsRetriableDirectoryMoveException(ex) && attempt < DirectoryMoveMaxAttempts)
            {
                last = ex;
                var delayMs = DirectoryMoveBackoffMs[Math.Min(attempt - 1, DirectoryMoveBackoffMs.Length - 1)];
                _logger.LogWarning(
                    ex,
                    "task-folder-move-retry operation={Operation} subject={Subject} targetState={TargetState} attempt={Attempt}/{MaxAttempts} delayMs={DelayMs}",
                    operation, subject, targetState, attempt, DirectoryMoveMaxAttempts, delayMs);
                Thread.Sleep(delayMs);
            }
            catch (Exception ex)
            {
                if (Directory.Exists(target))
                {
                    return new MoveJobOutcome(
                        MoveJobStatus.TargetFolderExists,
                        $"A folder named '{Path.GetFileName(target)}' already exists in {targetState}.");
                }
                return FailureForMoveException(ex, operation, subject, targetState);
            }
        }

        return FailureForMoveException(last, operation, subject, targetState);
    }

    private MoveJobOutcome FailureForMoveException(Exception? ex, string operation, string subject, string targetState)
    {
        var message = ex?.Message ?? "unknown error";
        if (ex != null && IsRetriableDirectoryMoveException(ex))
        {
            _logger.LogWarning(
                ex,
                "task-folder-move-locked operation={Operation} subject={Subject} targetState={TargetState} attempts={Attempts}",
                operation, subject, targetState, DirectoryMoveMaxAttempts);
            return new MoveJobOutcome(
                MoveJobStatus.DirectoryLocked,
                $"Task folder is locked by another process after {DirectoryMoveMaxAttempts} move attempts. " +
                $"Close the active CLI/log handle and retry. Last error: {message}");
        }

        return new MoveJobOutcome(MoveJobStatus.Failure, message);
    }

    private static bool IsRetriableDirectoryMoveException(Exception ex)
        => ex is IOException or UnauthorizedAccessException;
}

/// <summary>
/// Summary of a <see cref="TaskStateMachine.DedupeSlugFolders"/> run.
/// <paramref name="SlugsDeduped"/> is the number of slugs that had more than
/// one live folder; <paramref name="FoldersNeutralised"/> is the total number
/// of stale shells renamed with a leading underscore across all of them.
/// </summary>
public sealed record SlugDedupeReport(
    int SlugsDeduped,
    int FoldersNeutralised,
    IReadOnlyList<SlugDedupeGroup> Groups);

/// <summary>Per-slug detail for a dedup sweep: which copy was kept and which
/// shells were neutralised.</summary>
public sealed record SlugDedupeGroup(
    string Slug,
    string WatchPath,
    string KeptLane,
    long KeptSizeBytes,
    IReadOnlyList<string> Neutralised);

public sealed record OrphanFolderDeleteResult(
    OrphanFolderDeleteStatus Status,
    string? Message = null);

public enum OrphanFolderDeleteStatus
{
    Success,
    InvalidRequest,
    NonTerminalLane,
    NotFound,
    HasJobJson,
    Failure
}
