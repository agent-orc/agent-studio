using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace AgentStudio.Runner;

/// <summary>
/// Stale-progress sweep that surfaces <c>3-progress</c> folders the orchestrator
/// has lost track of (ADR-0001: at most one running task per project).
///
/// <para>Pairs with <see cref="CrashRecoveryService"/> at boot and
/// <see cref="StaleProgressSweepHostedService"/> during uptime: crash recovery
/// rescues uncommitted file changes and finishes pending transitions. This
/// sweep moves the *folders* themselves so the lane visually returns to the
/// one-job promise after a backend that died mid-run, an
/// <c>update-stable.sh</c> cycle that killed the runner, or a progress-first
/// pickup that failed to resume.</para>
///
/// Decision shape per folder:
/// <list type="bullet">
///   <item>Latest activity = max over (a) the mtime of every run-produced file
///   under <c>logs/</c> (<c>cli-output.log</c>, <c>tool-calls.jsonl</c>,
///   <c>session-events.jsonl</c>, future log types) and (b) the
///   <c>enteredLaneAt</c> value read from <c>task.json</c>'s <em>content</em>.
///   <c>task.json</c>'s file mtime is deliberately excluded: metadata edits the
///   run never makes - a bulk model switch, a tag change, the scanner stamping
///   <c>ownerClientId</c> - rewrite the file and bump its mtime to "now", which
///   would mask a genuine zombie whose run died hours ago. The run-produced log
///   mtimes and the stable <c>enteredLaneAt</c> stamp (set on lane entry, never
///   touched by a metadata edit) are the non-fragile, run-bound signals.
///   Reading any single log file as the liveness signal misclassifies a session
///   that is currently emitting only tool-use events into <c>tool-calls.jsonl</c>
///   while <c>cli-output.log</c> stays quiet. Folders with no files at all are
///   treated as <c>epoch 0</c>.</item>
///   <item>If younger than <c>Supervisor:StuckResumeWindowMinutes</c> (default
///   60), the progress-first pickup will resume it and the sweep leaves it
///   alone. Setting the window to <c>0</c> turns the sweep off.</item>
///   <item>If older AND a completion sentinel
///   (<c>[[TASK_DONE]]</c> / <c>[[TASK_BLOCKED]]</c> /
///   <c>[[TASK_NEEDS_INPUT]]</c>) appears in the last 50 lines of
///   <c>cli-output.log</c>, the sweep finishes the
///   <c>3-progress -&gt; 4-auto-review</c> transition the orchestrator missed
///   and appends a <c>[recovered-from-stuck-progress]</c> note to the chat
///   log.</item>
///   <item>If older with no sentinel but **with** a <c>task.json</c>, the run
///   was interrupted, not failed: the folder is a real task and is requeued to
///   <c>2-ready</c> so the pickup loop retries the same task. No new card is
///   minted. (ADR-0051, failed-pickup elimination, supersedes ADR-0028/0029.)</item>
///   <item>If older with no <c>task.json</c>, the folder is not a runnable task:
///   it is debris (an empty folder, a lost-metadata shell, a hand-made
///   directory) and is archived to <c>7-archive</c> under
///   <c>&lt;original-slug&gt;-debris-&lt;utc-date&gt;/</c> with its evidence
///   intact, never parked in a dead-end failure lane.</item>
/// </list>
///
/// <para>A second responsibility, <see cref="DrainFailedPickupLaneAsync"/>,
/// drains any folders that linger in the retired <c>3a-failed-pickup</c> lane
/// from before ADR-0051: real tasks back to <c>2-ready</c>, debris to
/// <c>7-archive</c>. It runs once per boot after the sweep and is idempotent
/// once the lane is empty.</para>
///
/// <para>Single-state-machine authority: every move goes through
/// <see cref="TaskStateMachine"/> or <see cref="TaskTransitionService"/>; the
/// sweep never moves folders directly.</para>
///
/// <para>Idempotent: a second run on the same lane is a no-op (the candidates
/// are no longer in <c>3-progress</c>).</para>
///
/// <para>Defensive guard: cross-checks the runner's current
/// <c>activeJobId</c> per project before any move so a folder that is the
/// active job (would be unusual at boot, but possible if invoked later) is
/// never touched.</para>
/// </summary>
public sealed class StaleProgressArchiver
{
    private readonly TaskScannerService _scanner;
    private readonly TaskStateMachine _states;
    private readonly TaskTransitionService _transitions;
    private readonly OrchestratorChatLog _chatLog;
    private readonly IServiceProvider _services;
    private readonly IConfiguration _configuration;
    private readonly ILogger<StaleProgressArchiver> _logger;
    private readonly IJsonlAppender _appender;
    private readonly ITaskAccess _taskAccess;

    public const int DefaultStuckResumeWindowMinutes = 60;
    private const int SentinelTailLineWindow = 50;
    // The sentinel sits in the last few lines; 256 KiB holds the 50-line
    // window for any realistic line length without reading the whole log.
    private const int SentinelTailByteWindow = 256 * 1024;

    /// <summary>Test seam: when set, replaces the runner-status lookup so unit
    /// tests can simulate an active job without standing up <see cref="TaskRunnerService"/>.</summary>
    internal Func<RunnerStatus?>? StatusProviderOverride { get; set; }

    public StaleProgressArchiver(
        TaskScannerService scanner,
        TaskStateMachine states,
        TaskTransitionService transitions,
        OrchestratorChatLog chatLog,
        IServiceProvider services,
        IConfiguration configuration,
        ITaskAccess taskAccess,
        ILogger<StaleProgressArchiver> logger,
        IJsonlAppender? appender = null)
    {
        _scanner = scanner;
        _states = states;
        _transitions = transitions;
        _chatLog = chatLog;
        _services = services;
        _configuration = configuration;
        _taskAccess = taskAccess;
        _logger = logger;
        _appender = appender ?? new JsonlAppender();
    }

    /// <summary>
    /// Runs the sweep across all watched projects. Returns one decision per
    /// folder examined (including <c>fresh</c> verdicts) so callers and tests
    /// can assert on what happened without re-parsing the JSONL log.
    /// </summary>
    public async Task<IReadOnlyList<StaleProgressDecision>> SweepAsync(CancellationToken ct = default)
    {
        var decisions = new List<StaleProgressDecision>();

        var windowMinutes = _configuration.GetValue("Supervisor:StuckResumeWindowMinutes", DefaultStuckResumeWindowMinutes);
        if (windowMinutes <= 0)
        {
            _logger.LogInformation(
                "StaleProgressArchiver: disabled (Supervisor:StuckResumeWindowMinutes = {Window}); skipping sweep.",
                windowMinutes);
            return decisions;
        }

        var threshold = TimeSpan.FromMinutes(windowMinutes);
        var now = DateTime.UtcNow;

        var activeByProject = SafeGetActiveJobIds();

        foreach (var entry in _scanner.GetWatchPaths())
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(entry.Path) || !Directory.Exists(entry.Path)) continue;

            activeByProject.TryGetValue(entry.Name, out var activeJobId);

            // ADR-0024: enumerate 3-progress through the typed layer.
            // ListLaneFolders includes orphan folders (no task.json),
            // which is the case this sweep was designed to catch.
            //
            // Measure every folder BEFORE acting on any of them. The requeue
            // and recover paths call _scanner.FindJob, which stamps
            // ownerClientId onto sibling legacy task.json files and bumps their
            // mtime; acting on one folder must not reclassify a sibling that has
            // not been processed yet (it would look freshly active and be
            // skipped). Snapshotting the age verdict up front judges each folder
            // on its pre-sweep state. (See JobScannerService.FindJob mtime side
            // effect.)
            var candidates = new List<(string Slug, string JobFolder, DateTime LastActivity)>();
            foreach (var laneFolder in _taskAccess.ListLaneFolders(entry.Path, TaskStates.Progress))
            {
                ct.ThrowIfCancellationRequested();
                if (_scanner.FindJob(laneFolder.Slug, entry.Path)?.Fixture == true)
                    continue;
                // Valid steer-pending markers are bounded by Slice B. This
                // legacy stale-progress sweep must not promote/requeue them
                // first based on an old NEEDS_INPUT sentinel. A malformed
                // marker falls through to normal recovery.
                if (SteerPendingMarker.TryRead(laneFolder.FolderPath, _logger) != null)
                    continue;
                var (measured, _) = MeasureFolder(laneFolder.FolderPath);
                candidates.Add((laneFolder.Slug, laneFolder.FolderPath, measured));
            }

            foreach (var (slug, jobFolder, lastActivity) in candidates)
            {
                ct.ThrowIfCancellationRequested();

                if (!string.IsNullOrEmpty(activeJobId)
                    && string.Equals(slug, activeJobId, StringComparison.OrdinalIgnoreCase))
                {
                    decisions.Add(Skip(entry.Name, slug, "matches active job", at: now));
                    continue;
                }

                var age = now - lastActivity;
                if (age < threshold)
                {
                    decisions.Add(new StaleProgressDecision
                    {
                        At = now,
                        Kind = StaleProgressDecisionKinds.Fresh,
                        ProjectName = entry.Name,
                        Slug = slug,
                        AgeSeconds = (long)age.TotalSeconds,
                        Reason = "within resume window"
                    });
                    continue;
                }

                // Routing (failed-pickup elimination, supersedes ADR-0028/0029):
                //   * mid-move casualty (no task.json AND same slug already in
                //     a later lane) -> silently delete the empty source folder.
                //     This is the 2026-05-12 boot-race: a Lane-Move
                //     3-progress -> 4-auto-review was already complete when the
                //     backend stopped, but the source folder's residue (the
                //     skeleton left after Directory.Move side effects, or a
                //     half-populated re-use of the same slug) survived. The
                //     real job lives in the later lane; archiving the residue
                //     as debris would mint a phantom card under
                //     <slug>-debris-<date>.
                //   * has task.json + completion sentinel -> finish the missed
                //     transition to 4-auto-review (unchanged).
                //   * has task.json, no sentinel            -> the run was
                //     interrupted, not failed; requeue the same task to 2-ready
                //     so the pickup loop retries it. No new orphan card.
                //   * no task.json                          -> debris (an empty
                //     folder, a lost-metadata shell, a hand-made directory).
                //     Not a runnable task; archive it to 7-archive with its
                //     evidence intact instead of parking a card in a dead-end
                //     lane.
                //
                // Cross-lane lookup before orphan-marking (drift rule
                // `orphan-detection-checks-other-lanes`): every code path that
                // decides "this is an orphan" must first check whether the same
                // slug already exists in a later lane.
                var hasJobJson = File.Exists(Path.Combine(jobFolder, "task.json"));
                StaleProgressDecision decision;
                if (!hasJobJson && TryFindSlugInLaterLane(entry.Path, slug, out var twinLane))
                {
                    decision = RemoveMidMoveCasualty(entry, slug, twinLane!, now);
                }
                else if (hasJobJson && TryFindSentinel(jobFolder, out var keyword))
                {
                    decision = await RecoverViaTransitionAsync(entry, jobFolder, slug, keyword!, now, ct);
                }
                else if (hasJobJson)
                {
                    decision = await RequeueOrphanToReadyAsync(entry, jobFolder, slug, now, lastActivity, ct);
                }
                else
                {
                    decision = ArchiveDebrisFolder(entry, jobFolder, slug, now, lastActivity);
                }

                decisions.Add(decision);
                AppendOrphanRecoveryEntry(decision);
            }
        }

        var actionable = decisions.Count(d =>
            d.Kind == StaleProgressDecisionKinds.RequeuedToReady ||
            d.Kind == StaleProgressDecisionKinds.RequeueFailed ||
            d.Kind == StaleProgressDecisionKinds.ArchivedDebris ||
            d.Kind == StaleProgressDecisionKinds.ArchiveFailed ||
            d.Kind == StaleProgressDecisionKinds.RecoveredToReview ||
            d.Kind == StaleProgressDecisionKinds.RecoveryFailed ||
            d.Kind == StaleProgressDecisionKinds.MidMoveCasualtyRemoved ||
            d.Kind == StaleProgressDecisionKinds.MidMoveCasualtyRemovalFailed);
        _logger.LogInformation(
            "StaleProgressArchiver: completed stale-progress sweep with {Total} candidate(s), {Actionable} actionable.",
            decisions.Count, actionable);

        return decisions;
    }

    /// <summary>
    /// One-time-per-boot drain of folders that linger in
    /// <c>3a-failed-pickup</c> from before the failed-pickup-elimination
    /// change (failed-pickup-elimination, supersedes ADR-0028/0029, row 10).
    /// The lane is no longer populated by any live path; this drains the
    /// historical backlog so the lane reaches and stays empty, then can be
    /// retired:
    /// <list type="bullet">
    ///   <item>A folder that carries a <c>task.json</c> is a real task. It is
    ///   restored to <c>2-ready</c> (original slug recovered when the
    ///   dead-letter shape is recognised, otherwise kept) so the pickup loop
    ///   retries the same task.</item>
    ///   <item>A folder with no <c>task.json</c> is debris and is archived to
    ///   <c>7-archive</c> with its evidence intact.</item>
    /// </list>
    /// Idempotent: a boot with an already-empty lane is a no-op. Runs after
    /// <see cref="SweepAsync"/> so a folder requeued from <c>3-progress</c> is
    /// never also drained.
    /// </summary>
    public async Task<IReadOnlyList<StaleProgressDecision>> DrainFailedPickupLaneAsync(CancellationToken ct = default)
    {
        var decisions = new List<StaleProgressDecision>();
        var now = DateTime.UtcNow;

        foreach (var entry in _scanner.GetWatchPaths())
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(entry.Path) || !Directory.Exists(entry.Path)) continue;

            // ListLaneFolders includes folders with no task.json, which is the
            // debris case the drain must catch.
            foreach (var laneFolder in _taskAccess.ListLaneFolders(entry.Path, TaskStates.FailedPickup))
            {
                ct.ThrowIfCancellationRequested();
                if (_scanner.FindJob(laneFolder.Slug, entry.Path)?.Fixture == true)
                    continue;
                var slug = laneFolder.Slug;
                var jobFolder = laneFolder.FolderPath;
                var hasJobJson = File.Exists(Path.Combine(jobFolder, "task.json"));

                var decision = hasJobJson
                    ? DrainRealTaskToReady(entry, slug, now)
                    : ArchiveAsDebris(
                        entry, jobFolder, slug, now,
                        $"folder left in {TaskStates.FailedPickup} with no task.json (debris); archived to {TaskStates.Archive} on the failed-pickup-lane drain");

                decisions.Add(decision);
                AppendOrphanRecoveryEntry(decision);
            }
        }

        if (decisions.Count > 0)
        {
            _logger.LogInformation(
                "StaleProgressArchiver: drained {Total} folder(s) out of {Lane}; the lane is being retired.",
                decisions.Count, TaskStates.FailedPickup);
        }

        return decisions;
    }

    /// <summary>
    /// Restore a real task (a folder carrying <c>task.json</c>) out of
    /// <c>3a-failed-pickup</c> and back to <c>2-ready</c>. The state machine
    /// recovers the original slug when the dead-letter shape is recognised; for
    /// the older <c>-orphan-</c> / <c>-empty-</c> / <c>orphan-</c> shapes that
    /// do not match, it falls back to keeping the slug so the move still
    /// happens.
    /// </summary>
    private StaleProgressDecision DrainRealTaskToReady(WatchPathEntry entry, string slug, DateTime now)
    {
        var outcome = _states.RestoreFromFailedPickup(slug, entry.Path, keepDeadLetterSlug: false);
        if (outcome.Status == RestoreFromFailedPickupStatus.InvalidSlug)
        {
            outcome = _states.RestoreFromFailedPickup(slug, entry.Path, keepDeadLetterSlug: true);
        }

        if (outcome.Status != RestoreFromFailedPickupStatus.Success
            && outcome.Status != RestoreFromFailedPickupStatus.NoOp)
        {
            return new StaleProgressDecision
            {
                At = now,
                Kind = StaleProgressDecisionKinds.RequeueFailed,
                ProjectName = entry.Name,
                Slug = slug,
                Reason = $"drain to {TaskStates.Ready} refused: {outcome.Status} {outcome.Message}"
            };
        }

        var restoredSlug = outcome.RestoredSlug ?? slug;
        var moved = _scanner.FindJob(restoredSlug, entry.Path);
        if (moved != null)
        {
            _chatLog.AppendSupervisor(
                moved,
                "drained-from-failed-pickup",
                "Boot drain found this real task lingering in the retired 3a-failed-pickup lane. " +
                "Failing pickup is treated as a bug in the pickup path, not a state a task should sit in; " +
                "the task was returned to 2-ready so the orchestrator retries it.");
        }

        return new StaleProgressDecision
        {
            At = now,
            Kind = StaleProgressDecisionKinds.RequeuedToReady,
            ProjectName = entry.Name,
            Slug = slug,
            JobId = restoredSlug,
            TargetState = TaskStates.Ready,
            Reason = $"real task drained from {TaskStates.FailedPickup} to {TaskStates.Ready} (failed-pickup lane retired)"
        };
    }

    private Dictionary<string, string?> SafeGetActiveJobIds()
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // Resolve TaskRunnerService lazily so the archiver can run before
            // the runner host has wired itself fully (DI graph still holds it).
            // Tests inject StatusProviderOverride directly to avoid the heavy
            // service.
            RunnerStatus? status = StatusProviderOverride != null
                ? StatusProviderOverride()
                : (_services.GetService(typeof(TaskRunnerService)) as TaskRunnerService)?.GetStatus();
            if (status?.Projects == null) return map;
            foreach (var (projectName, projectStatus) in status.Projects)
            {
                map[projectName] = projectStatus?.ActiveJobId;
            }
        }
        catch (Exception ex)
        {
            // The defensive guard must never block the sweep; an empty map
            // means "do not skip anything" which is safe at boot time.
            _logger.LogWarning(ex, "StaleProgressArchiver: could not read runner status; treating no jobs as active.");
        }
        return map;
    }

    private static (DateTime LastActivity, bool IsEmpty) MeasureFolder(string jobFolder)
    {
        // Liveness must come from RUN-PRODUCED activity, never from task.json's
        // file mtime. task.json is rewritten by edits the run never makes - a
        // bulk model switch, a tag change, the scanner stamping ownerClientId -
        // each of which bumps its mtime to "now". Folding that mtime into the
        // signature let a metadata edit "rescue" a genuine zombie whose run died
        // hours ago, so the sweep reported 0 actionable while real tasks sat
        // stranded in 3-progress (bug-3-progress-zombies). The robust, run-bound
        // signals are:
        //   (a) the mtime of every run-produced file under logs/
        //       (cli-output.log, tool-calls.jsonl, session-events.jsonl, plus
        //       any future log type) - only the runner appends to these, so they
        //       track real agent activity. Reading only cli-output.log misses
        //       sessions that emit primarily tool-use events into
        //       tool-calls.jsonl and stay quiet on stdout for tens of minutes;
        //       the union catches every form of runner-side append.
        //   (b) the enteredLaneAt VALUE inside task.json (read as content, not
        //       mtime): a stable run-lifecycle stamp set on lane entry and never
        //       touched by a metadata edit. It floors the signal so a folder
        //       that only just entered 3-progress (e.g. a parallel pickup whose
        //       run has not streamed its first log line yet) is not misjudged
        //       stale, while still letting a folder that entered the lane long
        //       ago with no fresh log writes cross the threshold.
        var jobJson = Path.Combine(jobFolder, "task.json");
        var logsDir = Path.Combine(jobFolder, "logs");
        var hasJson = File.Exists(jobJson);
        var hasAnyLogFile = false;
        var maxStamp = DateTime.MinValue.ToUniversalTime();

        if (Directory.Exists(logsDir))
        {
            foreach (var file in Directory.EnumerateFiles(logsDir))
            {
                try
                {
                    var stamp = File.GetLastWriteTimeUtc(file);
                    if (stamp > maxStamp) maxStamp = stamp;
                    hasAnyLogFile = true;
                }
                catch (Exception __ex)
                {
                    SilentCatch.Note(__ex, "StaleProgressArchiver: Best-effort: an unreadable file does not contribute to");
                    // Best-effort: an unreadable file does not contribute to
                    // the signature but does not disqualify the folder either.
                }
            }
        }

        if (!hasAnyLogFile && !hasJson)
        {
            // Truly empty folder: nothing for the runner to resume from.
            // lastActivity = epoch so it always crosses any configured
            // threshold; the debris branch handles the archive.
            return (DateTime.MinValue.ToUniversalTime(), IsEmpty: true);
        }

        // Stable run-lifecycle stamp from task.json CONTENT. Its file mtime is
        // deliberately NOT read here (that is the metadata-edit-fragile signal
        // this sweep was failing on); only the enteredLaneAt field value is.
        if (hasJson && TryReadEnteredLaneAt(jobJson) is { } enteredLaneAt && enteredLaneAt > maxStamp)
        {
            maxStamp = enteredLaneAt;
        }

        return (maxStamp, IsEmpty: false);
    }

    /// <summary>
    /// Reads the <c>enteredLaneAt</c> value out of a folder's <c>task.json</c>
    /// content - the wall-clock UTC instant the task entered its current lane,
    /// stamped on every lane move and preserved verbatim across metadata edits
    /// (model / tags / cli-type / ownerClientId all go through a field-level
    /// rewrite that keeps sibling fields). Returned in UTC. Null for a legacy
    /// task.json written before the field existed or any unreadable file; the
    /// caller then relies on the run-produced log mtimes alone.
    /// </summary>
    private static DateTime? TryReadEnteredLaneAt(string jobJsonPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(jobJsonPath));
            if (doc.RootElement.TryGetProperty("enteredLaneAt", out var el)
                && el.ValueKind == JsonValueKind.String
                && el.TryGetDateTime(out var dt))
            {
                return dt.ToUniversalTime();
            }
        }
        catch (Exception __ex)
        {
            SilentCatch.Note(__ex, "StaleProgressArchiver: Best-effort: a torn or schema-less task.json contributes no");
            // Best-effort: a torn or schema-less task.json contributes no
            // enteredLaneAt floor; the log mtimes still drive the verdict.
        }
        return null;
    }

    private static readonly Regex SentinelRegex = new(
        @"\[\[TASK_(?<keyword>DONE|BLOCKED|NEEDS_INPUT)(?::[^\]]*)?\]\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool TryFindSentinel(string jobFolder, out string? keyword)
    {
        keyword = null;
        var path = TaskPaths.CliOutputLog(jobFolder);
        if (!File.Exists(path)) return false;

        try
        {
            var tail = ReadTailLines(path, SentinelTailLineWindow);
            for (int i = tail.Count - 1; i >= 0; i--)
            {
                var match = SentinelRegex.Match(tail[i]);
                if (match.Success)
                {
                    keyword = match.Groups["keyword"].Value.ToUpperInvariant();
                    return true;
                }
            }
        }
        catch (Exception __ex)
        {
            SilentCatch.Note(__ex, "StaleProgressArchiver: Best-effort. Treat unreadable logs as 'no sentinel'; the folder");
            // Best-effort. Treat unreadable logs as "no sentinel"; the folder
            // will be archived as orphan rather than recovered.
        }
        return false;
    }

    private static List<string> ReadTailLines(string path, int maxLines)
    {
        // cli-output.log is capped at 10 MiB per file, so the sweep reads only
        // the newest byte window instead of the whole log (AGT-2991).
        var window = BoundedFileRead.ReadTailLines(path, SentinelTailByteWindow);
        var start = Math.Max(0, window.Count - maxLines);
        return window.Skip(start).ToList();
    }

    private async Task<StaleProgressDecision> RecoverViaTransitionAsync(
        WatchPathEntry entry, string jobFolder, string slug, string keyword,
        DateTime now, CancellationToken ct)
    {
        // Use jobId from task.json when available; folder name is the canonical
        // fallback (the application uses folder name as jobId everywhere).
        var jobId = TryReadJobId(jobFolder) ?? slug;
        try
        {
            var outcome = await _transitions.MoveAsync(
                jobId, TaskStates.AutoReview, entry.Path, ct,
                transitionCause: LaneChangeCauses.Delivered,
                transitionDetail: $"stale-progress-sentinel:{keyword}");
            if (outcome.Status != MoveJobStatus.Success)
            {
                return new StaleProgressDecision
                {
                    At = now,
                    Kind = StaleProgressDecisionKinds.RecoveryFailed,
                    ProjectName = entry.Name,
                    Slug = slug,
                    JobId = jobId,
                    SentinelKeyword = keyword,
                    Reason = $"transition to {TaskStates.AutoReview} refused: {outcome.Status} {outcome.Message}"
                };
            }

            // Append the chat-log note on the moved folder so the protocol
            // pane shows the recovery alongside the agent's own output.
            var movedInfo = _scanner.FindJob(jobId, entry.Path);
            if (movedInfo != null)
            {
                _chatLog.AppendSupervisor(
                    movedInfo,
                    "recovered-from-stuck-progress",
                    $"Stale-progress sweep finished a missed transition: agent had emitted [[TASK_{keyword}]] " +
                    "in 3-progress and the orchestrator never moved the folder. Promoted to 4-auto-review.");
            }

            return new StaleProgressDecision
            {
                At = now,
                Kind = StaleProgressDecisionKinds.RecoveredToReview,
                ProjectName = entry.Name,
                Slug = slug,
                JobId = jobId,
                SentinelKeyword = keyword,
                TargetState = TaskStates.AutoReview,
                Reason = $"sentinel TASK_{keyword} survived; finished missed transition"
            };
        }
        catch (Exception ex)
        {
            return new StaleProgressDecision
            {
                At = now,
                Kind = StaleProgressDecisionKinds.RecoveryFailed,
                ProjectName = entry.Name,
                Slug = slug,
                JobId = jobId,
                SentinelKeyword = keyword,
                Reason = $"exception: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// A stale <c>3-progress</c> folder that still carries a <c>task.json</c> is
    /// a real task whose run was interrupted (a backend that died mid-run, an
    /// <c>update-stable.sh</c> cycle that killed the runner, a pickup that never
    /// streamed a sentinel). The task is not failed; requeue it to <c>2-ready</c>
    /// so the pickup loop retries the same task. No new orphan card is created.
    /// (Failed-pickup elimination, supersedes ADR-0028/0029.)
    /// </summary>
    private async Task<StaleProgressDecision> RequeueOrphanToReadyAsync(
        WatchPathEntry entry,
        string jobFolder,
        string slug,
        DateTime now,
        DateTime lastActivity,
        CancellationToken ct)
    {
        var jobId = TryReadJobId(jobFolder) ?? slug;

        try
        {
            var outcome = await _transitions.MoveAsync(
                jobId, TaskStates.Ready, entry.Path, ct,
                transitionCause: LaneChangeCauses.RunnerRequeue,
                transitionDetail: "stale-progress-orphan");
            if (outcome.Status != MoveJobStatus.Success)
            {
                return new StaleProgressDecision
                {
                    At = now,
                    Kind = StaleProgressDecisionKinds.RequeueFailed,
                    ProjectName = entry.Name,
                    Slug = slug,
                    JobId = jobId,
                    Reason = $"requeue to {TaskStates.Ready} refused: {outcome.Status} {outcome.Message}"
                };
            }

            var moved = _scanner.FindJob(jobId, entry.Path);
            if (string.Equals(moved?.State, TaskStates.AutoReview, StringComparison.Ordinal))
            {
                return new StaleProgressDecision
                {
                    At = now,
                    Kind = StaleProgressDecisionKinds.RecoveredToReview,
                    ProjectName = entry.Name,
                    Slug = slug,
                    JobId = jobId,
                    TargetState = TaskStates.AutoReview,
                    Reason = "attempt authority reported a completed immutable result; recovered the existing delivery to auto-review instead of requeueing"
                };
            }

            // Only describe a requeue after the shared transition guard confirms
            // that the card actually landed in Ready. A settled-envelope recovery
            // goes to Auto Review and must never leave a contradictory log line.
            WriteRequeueDiagnostic(moved?.FolderPath ?? outcome.NewFolderPath ?? jobFolder, lastActivity, now);

            // The single compact [recovery] line was written by
            // WriteRequeueDiagnostic after the move in the Ready folder; the
            // long-form rationale lives in orphan-recoveries.jsonl
            // (AppendOrphanRecoveryEntry). No second chat note here.
            return new StaleProgressDecision
            {
                At = now,
                Kind = StaleProgressDecisionKinds.RequeuedToReady,
                ProjectName = entry.Name,
                Slug = slug,
                JobId = jobId,
                TargetState = TaskStates.Ready,
                Reason = "stale progress run interrupted with no completion sentinel; requeued to 2-ready to retry the same task"
            };
        }
        catch (Exception ex)
        {
            return new StaleProgressDecision
            {
                At = now,
                Kind = StaleProgressDecisionKinds.RequeueFailed,
                ProjectName = entry.Name,
                Slug = slug,
                JobId = jobId,
                Reason = $"exception: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// A stale <c>3-progress</c> folder with no <c>task.json</c> is not a runnable
    /// task: it is debris (an empty folder, a lost-metadata shell, a hand-made
    /// directory). Archive it to <c>7-archive</c> so its evidence is preserved
    /// without parking a card in a dead-end lane. (Failed-pickup elimination.)
    /// </summary>
    private StaleProgressDecision ArchiveDebrisFolder(
        WatchPathEntry entry,
        string jobFolder,
        string slug,
        DateTime now,
        DateTime lastActivity)
        => ArchiveAsDebris(
            entry, jobFolder, slug, now,
            "stale folder with no task.json (debris, not a runnable task); archived to 7-archive");

    /// <summary>
    /// Move <paramref name="jobFolder"/> to <c>7-archive</c> under a
    /// collision-safe <c>&lt;slug&gt;-debris-&lt;utc-date&gt;</c> name. Shared
    /// by the stale-progress sweep (a no-<c>task.json</c> <c>3-progress</c> folder)
    /// and the one-time failed-pickup drain (a no-<c>task.json</c> folder left
    /// in the retired lane). The move goes through
    /// <see cref="TaskStateMachine.ArchiveFolder"/>, which works without a
    /// <c>task.json</c>.
    /// </summary>
    private StaleProgressDecision ArchiveAsDebris(
        WatchPathEntry entry,
        string jobFolder,
        string slug,
        DateTime now,
        string reason)
    {
        var datePart = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var newSlug = $"{slug}-debris-{datePart}";
        var attempt = newSlug;
        for (int i = 2; _taskAccess.SlugExistsInLane(entry.Path, TaskStates.Archive, attempt) && i < 1000; i++)
        {
            attempt = $"{newSlug}-{i}";
        }

        var outcome = _states.ArchiveFolder(jobFolder, attempt);
        if (outcome.Status != MoveJobStatus.Success)
        {
            return new StaleProgressDecision
            {
                At = now,
                Kind = StaleProgressDecisionKinds.ArchiveFailed,
                ProjectName = entry.Name,
                Slug = slug,
                Reason = $"archive to {TaskStates.Archive} refused: {outcome.Status} {outcome.Message}"
            };
        }

        return new StaleProgressDecision
        {
            At = now,
            Kind = StaleProgressDecisionKinds.ArchivedDebris,
            ProjectName = entry.Name,
            Slug = slug,
            FailedPickupSlug = attempt,
            TargetState = TaskStates.Archive,
            Reason = reason
        };
    }

    private void WriteRequeueDiagnostic(string jobFolder, DateTime lastActivity, DateTime sweepAt)
    {
        try
        {
            var logsDir = Path.Combine(jobFolder, TaskPaths.LogsDirName);
            Directory.CreateDirectory(logsDir);
            var logPath = Path.Combine(logsDir, TaskPaths.CliOutputLogFileName);
            var line = RecoveryChatLine.PersistedLine(
                sweepAt,
                RecoveryChatLine.ReasonHostRestart,
                "run stuck in 3-progress after restart",
                $"requeued to {TaskStates.Ready}");
            CliOutputLogFile.Append(logPath, line);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "StaleProgressArchiver: failed to write requeue diagnostic for {Folder}", jobFolder);
        }
    }

    /// <summary>
    /// Lanes the stale-progress sweep checks for a downstream twin of a no-<c>task.json</c>
    /// 3-progress folder. A match means the real task already completed its
    /// move and the source-side residue is a mid-move casualty, not an orphan.
    /// 3a-failed-pickup is deliberately excluded so a previous phantom marker
    /// cannot mask a new genuine orphan.
    /// </summary>
    private static readonly string[] DownstreamLanesForOrphanReconciliation =
    {
        TaskStates.AutoReview,
        TaskStates.HumanReview,
        TaskStates.Escalated,
        TaskStates.Completed,
        TaskStates.Archive,
    };

    /// <summary>
    /// Cross-lane lookup that prevents the 2026-05-12 boot-race phantom: when
    /// a 3-progress folder has no <c>task.json</c>, but the same slug already
    /// lives in a later lane, the source folder is a mid-move casualty and
    /// must be silently removed instead of being archived as debris (or, in
    /// the pre-ADR-0051 world, dead-lettered into 3a-failed-pickup).
    ///
    /// Drift-watchlist rule <c>orphan-detection-checks-other-lanes</c> tracks
    /// this contract: every code path that decides "this is an orphan" must
    /// route through a cross-lane check before acting.
    /// </summary>
    private bool TryFindSlugInLaterLane(string watchPath, string slug, out string? foundLane)
    {
        foundLane = null;
        foreach (var lane in DownstreamLanesForOrphanReconciliation)
        {
            if (_taskAccess.SlugExistsInLane(watchPath, lane, slug))
            {
                foundLane = lane;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Silently delete a mid-move casualty source folder. The real task lives
    /// in <paramref name="twinLane"/>; the residue in 3-progress carries no
    /// <c>task.json</c> and would otherwise be archived as debris and mint a
    /// phantom card. The delete goes through the typed layer so the
    /// architecture test stays green.
    /// </summary>
    private StaleProgressDecision RemoveMidMoveCasualty(
        WatchPathEntry entry, string slug, string twinLane, DateTime now)
    {
        var outcome = _taskAccess.DeleteLaneFolder(entry.Path, TaskStates.Progress, slug);
        if (outcome.Status != TaskMutationStatus.Applied)
        {
            _logger.LogWarning(
                "StaleProgressArchiver: could not delete mid-move casualty {Slug} in {Project} (twin in {Twin}): {Status} {Message}",
                slug, entry.Name, twinLane, outcome.Status, outcome.Message);
            return new StaleProgressDecision
            {
                At = now,
                Kind = StaleProgressDecisionKinds.MidMoveCasualtyRemovalFailed,
                ProjectName = entry.Name,
                Slug = slug,
                TargetState = twinLane,
                Reason = $"delete of mid-move casualty refused: {outcome.Status} {outcome.Message}"
            };
        }

        _logger.LogInformation(
            "StaleProgressArchiver: silently removed mid-move casualty 3-progress/{Slug} (twin lives in {Twin}) for project {Project}",
            slug, twinLane, entry.Name);
        return new StaleProgressDecision
        {
            At = now,
            Kind = StaleProgressDecisionKinds.MidMoveCasualtyRemoved,
            ProjectName = entry.Name,
            Slug = slug,
            TargetState = twinLane,
            Reason = $"mid-move casualty: same slug already in {twinLane}; deleted residue instead of minting a phantom card"
        };
    }

    private static StaleProgressDecision Skip(string projectName, string slug, string reason, DateTime at)
        => new()
        {
            At = at,
            Kind = StaleProgressDecisionKinds.Skipped,
            ProjectName = projectName,
            Slug = slug,
            Reason = reason
        };

    private static string? TryReadJobId(string jobFolder)
    {
        var path = Path.Combine(jobFolder, "task.json");
        if (!File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        }
        catch { return null; }
    }

    private void AppendOrphanRecoveryEntry(StaleProgressDecision decision)
    {
        // Resolve the workspace root the same way the supervisor logs do.
        // Without TaskRepository configured the JSONL is silently skipped;
        // the structured backend logger still mirrors the decision.
        var workspaceRoot = _configuration["TaskRepository"];
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            _logger.LogDebug(
                "StaleProgressArchiver: TaskRepository not configured; skipping orphan-recoveries.jsonl entry for {Slug}.",
                decision.Slug);
            return;
        }

        try
        {
            var path = Path.Combine(workspaceRoot, "logs", "orphan-recoveries.jsonl");
            _appender.AppendAsync(path, decision, JsonOptions).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StaleProgressArchiver: failed to append orphan-recoveries.jsonl");
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

/// <summary>One row in <c>&lt;workspace&gt;/logs/orphan-recoveries.jsonl</c>.</summary>
/// <remarks>
/// Schema: <c>docs/app/schemas/orphan-recovery.schema.json</c>.
/// </remarks>
public sealed record StaleProgressDecision
{
    [JsonPropertyName("at")] public DateTime At { get; init; }
    [JsonPropertyName("kind")] public string Kind { get; init; } = "";
    [JsonPropertyName("projectName")] public string ProjectName { get; init; } = "";
    [JsonPropertyName("slug")] public string Slug { get; init; } = "";
    [JsonPropertyName("jobId")] public string? JobId { get; init; }
    /// <summary>Legacy ADR-0028 field: renamed slug under <c>3a-failed-pickup</c> after a dead-letter move. Null under the ADR-0051 routings (the folder keeps its slug as it moves to <c>2-ready</c> / <c>7-archive</c>).</summary>
    [JsonPropertyName("failedPickupSlug")] public string? FailedPickupSlug { get; init; }
    /// <summary>Legacy ADR-0028 field (<c>orphan</c> or <c>empty</c>): which boot-sweep verdict produced a dead-letter move. Null under ADR-0051.</summary>
    [JsonPropertyName("failureKind")] public string? FailureKind { get; init; }
    [JsonPropertyName("targetState")] public string? TargetState { get; init; }
    [JsonPropertyName("sentinelKeyword")] public string? SentinelKeyword { get; init; }
    [JsonPropertyName("ageSeconds")] public long? AgeSeconds { get; init; }
    [JsonPropertyName("reason")] public string Reason { get; init; } = "";
}

/// <summary>String constants for <see cref="StaleProgressDecision.Kind"/>.</summary>
public static class StaleProgressDecisionKinds
{
    /// <summary>Folder is younger than the resume window; left for progress-first pickup.</summary>
    public const string Fresh = "fresh";
    /// <summary>Folder is the runner's currently active job; left alone.</summary>
    public const string Skipped = "skipped";
    /// <summary>Sentinel found in tail; folder moved to <c>4-auto-review</c>.</summary>
    public const string RecoveredToReview = "recovered-to-review";
    /// <summary>Sentinel found but the transition refused or threw.</summary>
    public const string RecoveryFailed = "recovery-failed";
    /// <summary>Stale folder with <c>task.json</c> (interrupted real task) requeued to <c>2-ready</c> (failed-pickup elimination).</summary>
    public const string RequeuedToReady = "requeued-to-ready";
    /// <summary>Requeue of an interrupted task to <c>2-ready</c> refused or threw.</summary>
    public const string RequeueFailed = "requeue-failed";
    /// <summary>Stale folder with no <c>task.json</c> (debris) archived to <c>7-archive</c> (failed-pickup elimination).</summary>
    public const string ArchivedDebris = "archived-debris";
    /// <summary>Archive of a no-<c>task.json</c> debris folder refused (e.g. target slug already exists).</summary>
    public const string ArchiveFailed = "archive-failed";
    /// <summary>
    /// Source folder in <c>3-progress</c> with no <c>task.json</c> was a leftover
    /// from a Lane-Move that already completed (twin lives in a later lane).
    /// The residue was silently deleted instead of being archived as debris
    /// to avoid minting a phantom card. 2026-05-12 boot-race fix.
    /// </summary>
    public const string MidMoveCasualtyRemoved = "mid-move-casualty-removed";
    /// <summary>Delete of a recognised mid-move casualty refused (e.g. Windows file-handle race).</summary>
    public const string MidMoveCasualtyRemovalFailed = "mid-move-casualty-removal-failed";
}
