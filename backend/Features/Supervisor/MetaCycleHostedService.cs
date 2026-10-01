using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentStudio.Prompts;

namespace AgentStudio.Supervisor;

/// <summary>
/// Per-project hosted ticker that owns the pause-inspect-resume loop above
/// the runner. Off by default (<c>Supervisor:MetaCycleEnabled = false</c>)
/// and additionally gated per project. Routes pause / resume side effects
/// through <see cref="SupervisorInterventionService"/> so the runner state
/// machine remains the single authority. The full design lives in
/// <c>docs/mockups/orchestrator-meta-cycle/</c> and ADR-0022.
/// </summary>
/// <remarks>
/// What this service is NOT:
/// <list type="bullet">
/// <item>It does not move jobs between lanes; <see cref="TaskStateMachine"/> owns that.</item>
/// <item>It does not edit source code; queued fix-tasks describe what needs fixing.</item>
/// <item>It does not call <c>SetMode</c> directly; pause and resume go through the supervisor.</item>
/// <item>It does not invoke <c>update-stable.sh</c> from the dev process unless explicitly opted in per project.</item>
/// </list>
/// </remarks>
public sealed class MetaCycleHostedService : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly TaskRunnerService _taskRunner;
    private readonly TaskScannerService _scanner;
    private readonly SupervisorInterventionService _interventions;
    private readonly ProjectSettingsService _projectSettings;
    private readonly OrchestratorChatLog _chatLog;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MetaCycleHostedService> _logger;
    private readonly TimeProvider _time;
    private readonly ITaskAccess _taskAccess;

    /// <summary>
    /// Per-project mutable cycle state: which jobs we have already counted
    /// toward the current cycle, the trailing fix queue rate-limit, and the
    /// last cycle's report id. The dictionary is the only mutable state in
    /// the service; everything else is computed per tick.
    /// </summary>
    private readonly ConcurrentDictionary<string, ProjectCycleState> _state = new(StringComparer.OrdinalIgnoreCase);

    public MetaCycleHostedService(
        TaskRunnerService taskRunner,
        TaskScannerService scanner,
        SupervisorInterventionService interventions,
        ProjectSettingsService projectSettings,
        OrchestratorChatLog chatLog,
        IConfiguration configuration,
        ITaskAccess taskAccess,
        ILogger<MetaCycleHostedService> logger,
        TimeProvider? time = null)
    {
        _taskRunner = taskRunner;
        _scanner = scanner;
        _interventions = interventions;
        _projectSettings = projectSettings;
        _chatLog = chatLog;
        _configuration = configuration;
        _taskAccess = taskAccess;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workspace = _configuration["TaskRepository"];
        if (string.IsNullOrWhiteSpace(workspace))
        {
            _logger.LogWarning("TaskRepository not configured; MetaCycleHostedService idle.");
            return;
        }

        var intervalSeconds = _configuration.GetValue("Supervisor:MetaCycleTickSeconds", 30);

        try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); } catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (MetaCycleConfig.FromConfiguration(_configuration).Enabled)
                    await TickOnceAsync(workspace!, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogWarning(ex, "MetaCycle tick failed"); }

            intervalSeconds = _configuration.GetValue("Supervisor:MetaCycleTickSeconds", 30);
            try { await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// One pass over every project. Public so tests can drive the loop
    /// deterministically without spinning up a hosted service.
    /// </summary>
    public async Task TickOnceAsync(string workspace, CancellationToken ct)
    {
        var status = _taskRunner.GetStatus();
        if (status?.Projects == null) return;

        var allJobs = _scanner.ScanAllAutomationJobs();
        var byProject = allJobs.GroupBy(j => j.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var (project, projectStatus) in status.Projects)
        {
            ct.ThrowIfCancellationRequested();

            var config = ResolveConfig(project);
            if (!config.Enabled) continue;

            byProject.TryGetValue(project, out var projectJobs);
            projectJobs ??= new List<TaskInfo>();

            try
            {
                await EvaluateProjectAsync(workspace, project, projectStatus, projectJobs, config, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MetaCycle EvaluateProject failed for {Project}", project);
            }
        }
    }

    /// <summary>
    /// Decision pass for one project. Tracks newly-closed jobs since the last
    /// cycle and fires the cycle when the count crosses N.
    /// </summary>
    public async Task<MetaCycleReport?> EvaluateProjectAsync(
        string workspace,
        string project,
        ProjectRunnerStatus projectStatus,
        IReadOnlyList<TaskInfo> projectJobs,
        MetaCycleConfig config,
        CancellationToken ct)
    {
        var state = _state.GetOrAdd(project, _ => new ProjectCycleState());

        var closedJobs = projectJobs
            .Where(j => IsClosedForCycle(j.State))
            .OrderBy(j => j.LastActivity)
            .ToList();

        // Carry-forward: only count jobs that closed AFTER the last cycle.
        if (state.LastCycleAt is { } cutoff)
        {
            closedJobs = closedJobs.Where(j => j.LastActivity > cutoff).ToList();
        }

        // First-tick bootstrap: lock in the current set as already-counted so
        // we do not fire on existing reviewed jobs the moment the service starts.
        if (state.LastCycleAt == null && state.SeededFromBootstrap == false)
        {
            state.SeededFromBootstrap = true;
            state.LastCycleAt = _time.GetUtcNow().UtcDateTime;
            return null;
        }

        if (closedJobs.Count < config.CycleLengthN) return null;

        // Take the first N. Anything above N rolls into the next cycle.
        var window = closedJobs.Take(config.CycleLengthN).ToList();

        var startedAt = _time.GetUtcNow().UtcDateTime;
        var cycleId = NewCycleId(startedAt);

        try
        {
            // Pause through the supervisor so the runner remains the single
            // state-machine authority. Reason includes the cycle id so the
            // pause is traceable from the interventions log.
            await _interventions.PausePickupAsync(
                project,
                $"meta-cycle:{cycleId} inspecting {window.Count} job(s)",
                ttl: null,
                source: SupervisorSource.AutoIntervention,
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MetaCycle PausePickup failed for {Project}; aborting cycle", project);
            return null;
        }

        // Pause prevents new pickups but does not abort the active CLI run.
        // Inspection and any UpdateStableThenResume action would otherwise
        // race a mid-flight job and either misclassify or get killed when
        // the script tears down the backend. Wait for the runner to report
        // active=null before continuing; on timeout we still proceed but
        // log loudly so the operator can see a stuck job blocked the cycle.
        var quiescenceTimeout = TimeSpan.FromSeconds(
            _configuration.GetValue("Supervisor:MetaCyclePauseQuiescenceTimeoutSeconds", 1800));
        await WaitForProjectQuiescenceAsync(project, cycleId, quiescenceTimeout, ct);

        var inspection = BuildInspection(workspace, project, projectStatus, window, projectJobs, config, state);
        var jobObservations = window
            .Select(j => new MetaCycleJobObservation(
                JobId: j.Id,
                Title: j.Title,
                NewCommits: 0,
                HasArtefacts: HasArtefacts(j)))
            .ToList();

        var autoCommit = _projectSettings.Get(project).AutoCommit;
        var fixesInTrailingHour = state.CountFixesSince(_time.GetUtcNow().UtcDateTime - TimeSpan.FromHours(1));

        var report = MetaCycleRules.BuildReport(
            cycleId: cycleId,
            project: project,
            startedAt: startedAt,
            completedAt: _time.GetUtcNow().UtcDateTime,
            config: config,
            jobs: jobObservations,
            inspection: inspection,
            autoCommitEnabled: autoCommit,
            autoFixesInTrailingHour: fixesInTrailingHour);

        try
        {
            await ApplyActionAsync(workspace, project, report, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MetaCycle ApplyAction failed for {Project} cycle {CycleId}", project, report.CycleId);
        }

        WriteReport(workspace, report);
        AppendTailLog(workspace, report);

        state.LastCycleAt = report.CompletedAt;
        state.LastCycleId = report.CycleId;
        if (report.Action.Kind == MetaCycleActionKind.QueueFix)
        {
            state.RecordFix(report.CompletedAt);
        }
        return report;
    }

    private MetaCycleConfig ResolveConfig(string project)
    {
        // Per-project override path is reserved for a follow-up; for now the
        // default config plus the per-project Enabled flag is enough to drive
        // the loop. A project marked enabled on the global flag inherits the
        // default knobs unless a future SetMetaCycleConfig endpoint writes
        // them into ProjectSettings.
        var baseConfig = MetaCycleConfig.FromConfiguration(_configuration);

        // Per-project override: a project may explicitly enable / disable.
        // Treated as "use defaults but flip Enabled" in this first cut.
        // ProjectSettingsService does not yet expose a MetaCycle block, so
        // we honour the global flag plus the per-project AutoCommit hint
        // (auto-commit projects are exactly the ones that benefit from this
        // loop). Future per-project knobs land on ProjectSettings.
        return baseConfig;
    }

    private static bool IsClosedForCycle(string state)
        => state == TaskStates.AutoReview
        || state == TaskStates.HumanReview
        || state == TaskStates.Completed
        || state == TaskStates.Archive;

    private static bool HasArtefacts(TaskInfo job)
    {
        try
        {
            var resultsDir = Path.Combine(job.FolderPath, "results");
            if (Directory.Exists(resultsDir) && Directory.EnumerateFileSystemEntries(resultsDir).Any()) return true;
            var logPath = Path.Combine(job.FolderPath, "logs", "cli-output.log");
            if (File.Exists(logPath) && new FileInfo(logPath).Length > 0) return true;
        }
        catch (Exception __ex) { SilentCatch.Note(__ex, "MetaCycleHostedService: swallow; observation is best-effort"); /* swallow; observation is best-effort */ }
        return false;
    }

    private MetaCycleInspection BuildInspection(
        string workspace,
        string project,
        ProjectRunnerStatus projectStatus,
        IReadOnlyList<TaskInfo> windowJobs,
        IReadOnlyList<TaskInfo> allProjectJobs,
        MetaCycleConfig config,
        ProjectCycleState state)
    {
        // 1. Crash marker
        var crashMarkerPath = Path.Combine(workspace, "logs", "last-crash.json");
        var crash = new MetaCycleCrashMarker(false, null, null);
        try
        {
            if (File.Exists(crashMarkerPath))
            {
                var fi = new FileInfo(crashMarkerPath);
                if (state.LastCycleAt == null || fi.LastWriteTimeUtc > state.LastCycleAt)
                {
                    crash = new MetaCycleCrashMarker(
                        Present: true,
                        At: fi.LastWriteTimeUtc,
                        Details: ReadFirstChars(crashMarkerPath, 280));
                }
            }
        }
        catch (Exception __ex) { SilentCatch.Note(__ex, "MetaCycleHostedService: best-effort"); /* best-effort */ }

        // 2. Advisories at or above threshold since the last cycle
        var advisorySummary = ReadAdvisoriesSince(workspace, project, state.LastCycleAt, config.AdvisorySeverityThreshold);

        // 3. Stuck-in-progress
        var stuckJobs = allProjectJobs
            .Where(j => j.State == TaskStates.Progress)
            .Where(j => _time.GetUtcNow().UtcDateTime - j.LastActivity > config.StuckInProgressThreshold)
            .Select(j => j.Id)
            .ToList();
        var stuck = new MetaCycleStuckInProgress(stuckJobs.Count, stuckJobs);

        // 4. Expected artefacts on the closed jobs in the window
        var missing = windowJobs.Where(j => !HasArtefacts(j)).Select(j => j.Id).ToList();
        var artefacts = new MetaCycleExpectedArtefacts(missing.Count, missing);

        // 5. Runner-mode drift: compare persisted vs current
        var savedMode = _projectSettings.Get(project).RunnerMode;
        var actualMode = projectStatus.Mode;
        var drift = new MetaCycleRunnerModeDrift(
            Drifted: savedMode != null && actualMode != null && savedMode != actualMode,
            Expected: savedMode,
            Actual: actualMode);

        // 6. Commit-log diff: not implemented in first cut without a guarantee
        // of a clean SHA boundary; we emit zero with no SHA pair so the
        // commit-log finding only fires when the inspection was definitely
        // empty AND auto-commit is on. Conservative on purpose.
        var commitLog = new MetaCycleCommitLogDiff(0, null, null);

        // 7. Extras: extension hooks reserved for follow-up wiring.
        IReadOnlyDictionary<string, object>? extras = null;

        return new MetaCycleInspection(
            CommitLogDiff: commitLog,
            LastCrashMarker: crash,
            SupervisorAdvisories: advisorySummary,
            StuckInProgress: stuck,
            ExpectedArtefacts: artefacts,
            RunnerModeDrift: drift,
            Extras: extras);
    }

    private static MetaCycleAdvisorySummary ReadAdvisoriesSince(
        string workspace,
        string project,
        DateTime? since,
        SupervisorSeverity threshold)
    {
        var path = SupervisorLogPaths.ObservationsFile(workspace, project);
        if (!File.Exists(path)) return new MetaCycleAdvisorySummary(0, Array.Empty<string>());

        var topics = new List<string>();
        var count = 0;
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                SupervisorAdvisory? adv;
                try { adv = JsonSerializer.Deserialize<SupervisorAdvisory>(line, Json); }
                catch { continue; }
                if (adv == null) continue;
                if (since.HasValue && adv.CreatedAt <= since.Value) continue;
                if ((int)adv.Severity < (int)threshold) continue;
                count++;
                if (!topics.Contains(adv.Topic)) topics.Add(adv.Topic);
            }
        }
        catch (Exception __ex) { SilentCatch.Note(__ex, "MetaCycleHostedService: best-effort"); /* best-effort */ }
        return new MetaCycleAdvisorySummary(count, topics);
    }

    private async Task ApplyActionAsync(string workspace, string project, MetaCycleReport report, CancellationToken ct)
    {
        switch (report.Action.Kind)
        {
            case MetaCycleActionKind.Resume:
                await ResumeWithVerificationAsync(workspace, project, report,
                    $"meta-cycle:{report.CycleId} {report.Action.Reason}", ct);
                break;

            case MetaCycleActionKind.UpdateStableThenResume:
                TryRunUpdateStable(workspace, project, report);
                await ResumeWithVerificationAsync(workspace, project, report,
                    $"meta-cycle:{report.CycleId} {report.Action.Reason}", ct);
                break;

            case MetaCycleActionKind.QueueFix:
                report = report with { Action = report.Action with { FollowUpJobId = QueueFollowUpTask(workspace, project, report) } };
                await ResumeWithVerificationAsync(workspace, project, report,
                    $"meta-cycle:{report.CycleId} resumed-after-fix-queued", ct);
                break;

            case MetaCycleActionKind.EscalateToUser:
                report = report with { Action = report.Action with { FollowUpJobId = QueueFollowUpTask(workspace, project, report) } };
                // Stay paused; user must resume manually.
                break;

            case MetaCycleActionKind.NoOp:
                // Aborted; do nothing. Pause is left in place so the user can inspect.
                break;
        }
    }

    /// <summary>
    /// Wraps <see cref="SupervisorInterventionService.ResumeAsync"/> with a
    /// verification loop. After every resume call the cycle reads back the
    /// project's runner mode through <see cref="TaskRunnerService.GetStatus"/>
    /// (in-process; no HTTP self-call) and only declares success when the
    /// observed mode is <c>auto-continuous</c>. If the mode is still
    /// <c>paused</c> after <c>Supervisor:MetaCycleResumeMaxAttempts</c> tries,
    /// a high-severity <c>cycle-resume-failed</c> advisory plus a
    /// <c>[supervisor]</c> chat-note are emitted so the operator is alerted
    /// rather than silently stuck. Without this loop a single missed resume
    /// (transient SetMode failure, runner not yet wired for the project)
    /// leaves the project paused forever.
    /// </summary>
    private async Task ResumeWithVerificationAsync(
        string workspace,
        string project,
        MetaCycleReport report,
        string reason,
        CancellationToken ct)
    {
        var maxAttempts = Math.Max(1, _configuration.GetValue("Supervisor:MetaCycleResumeMaxAttempts", 5));
        var backoffMs = Math.Max(0, _configuration.GetValue("Supervisor:MetaCycleResumeBackoffBaseMs", 1000));
        var baseBackoff = TimeSpan.FromMilliseconds(backoffMs);

        var outcome = await VerifyResumeWithRetryAsync(
            resumeAttempt: async (attempt, c) =>
            {
                _logger.LogInformation(
                    "MetaCycle:{CycleId} {Project} resume attempt {Attempt}/{Max} reason={Reason}",
                    report.CycleId, project, attempt, maxAttempts, reason);
                await _interventions.ResumeAsync(project, reason, SupervisorSource.AutoIntervention, c);
            },
            getCurrentMode: () => GetProjectMode(project),
            expectedMode: "auto-continuous",
            maxAttempts: maxAttempts,
            baseBackoff: baseBackoff,
            time: _time,
            ct: ct);

        switch (outcome.Result)
        {
            case ResumeVerificationResult.VerifiedFirstTry:
                _logger.LogInformation(
                    "MetaCycle:{CycleId} {Project} resume verified on first attempt; mode=auto-continuous.",
                    report.CycleId, project);
                break;
            case ResumeVerificationResult.VerifiedAfterRetries:
                _logger.LogWarning(
                    "MetaCycle:{CycleId} {Project} resume needed {Attempts} attempts; mode=auto-continuous. Treat as drift signal.",
                    report.CycleId, project, outcome.AttemptsMade);
                break;
            case ResumeVerificationResult.ExhaustedRetries:
                _logger.LogError(
                    "MetaCycle:{CycleId} {Project} resume FAILED after {Attempts} attempts; last observed mode='{Mode}'. Project remains paused; emitting advisory.",
                    report.CycleId, project, outcome.AttemptsMade, outcome.LastObservedMode ?? "(unknown)");
                EmitResumeFailureSignals(workspace, project, report, outcome);
                break;
        }
    }

    private string? GetProjectMode(string project)
    {
        try
        {
            var status = _taskRunner.GetStatus();
            if (status?.Projects == null) return null;
            return status.Projects.TryGetValue(project, out var p) ? p.Mode : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MetaCycle GetProjectMode probe failed for {Project}", project);
            return null;
        }
    }

    private void EmitResumeFailureSignals(
        string workspace,
        string project,
        MetaCycleReport report,
        ResumeVerificationOutcome outcome)
    {
        var advisory = BuildResumeFailedAdvisory(project, report, outcome, _time.GetUtcNow().UtcDateTime);
        try
        {
            HardHealthCheckHostedService.AppendObservationRecord(workspace, advisory);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MetaCycle resume-failed advisory write failed for {Project}", project);
        }

        // Best-effort chat-note: pin to the most recent observed job in the
        // window so the operator sees the alert next to the work that
        // triggered the cycle. If we have no job context we still wrote the
        // advisory above, which the supervisor panel + Layer 3 review pick up.
        var lastJobId = report.JobsObserved.LastOrDefault()?.JobId;
        if (string.IsNullOrWhiteSpace(lastJobId)) return;
        try
        {
            var info = _scanner.FindJob(lastJobId);
            if (info != null)
            {
                _chatLog.AppendSupervisor(info, "cycle-resume-failed",
                    BuildResumeFailedChatNoteText(project, report, outcome));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MetaCycle resume-failed chat-note write failed for {Project}", project);
        }
    }

    /// <summary>
    /// Pure builder for the high-severity advisory raised when the
    /// resume-with-verification loop gives up. Public + static so tests can
    /// assert the shape without standing up the hosted service.
    /// </summary>
    public static SupervisorAdvisory BuildResumeFailedAdvisory(
        string project,
        MetaCycleReport report,
        ResumeVerificationOutcome outcome,
        DateTime atUtc)
        => new(
            CreatedAt: atUtc,
            Project: project,
            Severity: SupervisorSeverity.High,
            Source: SupervisorSource.AutoIntervention,
            Topic: "cycle-resume-failed",
            Message: $"meta-cycle:{report.CycleId} resume to auto-continuous failed after {outcome.AttemptsMade} attempts; last observed mode='{outcome.LastObservedMode ?? "(unknown)"}'. Project remains paused; user must resume manually.");

    /// <summary>
    /// Pure builder for the supervisor chat-note text rendered next to the
    /// last job in the cycle window. Kept separate so the formatting is
    /// asserted directly in tests without a TaskInfo / OrchestratorChatLog.
    /// </summary>
    public static string BuildResumeFailedChatNoteText(
        string project,
        MetaCycleReport report,
        ResumeVerificationOutcome outcome)
        => $"meta-cycle:{report.CycleId} could not resume {project} after {outcome.AttemptsMade} attempts (last mode={outcome.LastObservedMode ?? "?"}). Project stays paused; resume manually after fixing the underlying cause.";

    /// <summary>
    /// Calls <paramref name="resumeAttempt"/> and immediately reads the
    /// project's mode through <paramref name="getCurrentMode"/>. Re-attempts
    /// up to <paramref name="maxAttempts"/> with exponential backoff
    /// (<paramref name="baseBackoff"/>, doubling each time) until the mode
    /// observation matches <paramref name="expectedMode"/>. Pure helper: no
    /// DI, no IO, deterministic against a fake <see cref="TimeProvider"/>.
    /// Exceptions thrown by <paramref name="resumeAttempt"/> are captured and
    /// surface on the final outcome; cancellation propagates.
    /// </summary>
    internal static async Task<ResumeVerificationOutcome> VerifyResumeWithRetryAsync(
        Func<int, CancellationToken, Task> resumeAttempt,
        Func<string?> getCurrentMode,
        string expectedMode,
        int maxAttempts,
        TimeSpan baseBackoff,
        TimeProvider time,
        CancellationToken ct)
    {
        if (maxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(maxAttempts), "must be >= 1");

        Exception? lastException = null;
        string? lastMode = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await resumeAttempt(attempt, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                lastException = ex;
            }

            lastMode = getCurrentMode();
            if (string.Equals(lastMode, expectedMode, StringComparison.Ordinal))
            {
                var verifiedResult = attempt == 1
                    ? ResumeVerificationResult.VerifiedFirstTry
                    : ResumeVerificationResult.VerifiedAfterRetries;
                return new ResumeVerificationOutcome(verifiedResult, attempt, lastMode, null);
            }

            if (attempt < maxAttempts && baseBackoff > TimeSpan.Zero)
            {
                // Exponential backoff: base, 2*base, 4*base, ... Cap the
                // shift to avoid overflow on absurdly large maxAttempts.
                var shift = Math.Min(attempt - 1, 30);
                var delay = TimeSpan.FromTicks(baseBackoff.Ticks * (1L << shift));
                try { await Task.Delay(delay, time, ct); }
                catch (OperationCanceledException) { throw; }
            }
        }

        return new ResumeVerificationOutcome(
            ResumeVerificationResult.ExhaustedRetries,
            maxAttempts,
            lastMode,
            lastException);
    }

    public enum ResumeVerificationResult
    {
        VerifiedFirstTry,
        VerifiedAfterRetries,
        ExhaustedRetries
    }

    public sealed record ResumeVerificationOutcome(
        ResumeVerificationResult Result,
        int AttemptsMade,
        string? LastObservedMode,
        Exception? LastException);

    private async Task WaitForProjectQuiescenceAsync(string project, string cycleId, TimeSpan timeout, CancellationToken ct)
    {
        var pollInterval = TimeSpan.FromSeconds(_configuration.GetValue("Supervisor:MetaCyclePauseQuiescencePollSeconds", 5));
        var outcome = await WaitForQuiescenceAsync(
            getActiveJobId: () =>
            {
                var status = _taskRunner.GetStatus();
                if (status?.Projects == null) return null;
                return status.Projects.TryGetValue(project, out var p) ? p.ActiveJobId : null;
            },
            timeout: timeout,
            pollInterval: pollInterval,
            time: _time,
            ct: ct);

        switch (outcome.Result)
        {
            case QuiescenceWaitResult.AlreadyIdle:
                break;
            case QuiescenceWaitResult.BecameIdle:
                _logger.LogInformation(
                    "MetaCycle:{CycleId} {Project} active job '{JobId}' finished after {Waited:g}; proceeding with inspection.",
                    cycleId, project, outcome.LastSeenActiveJobId, outcome.Waited);
                break;
            case QuiescenceWaitResult.TimedOut:
                _logger.LogWarning(
                    "MetaCycle:{CycleId} {Project} pause-quiescence timed out after {Timeout:g}; active job '{JobId}' still running. Proceeding anyway; mid-flight run may be killed by UpdateStableThenResume.",
                    cycleId, project, timeout, outcome.LastSeenActiveJobId);
                break;
        }
    }

    /// <summary>
    /// Polls <paramref name="getActiveJobId"/> until it returns <c>null</c> or
    /// the timeout elapses. Used by the meta-cycle so a freshly-paused project
    /// finishes its active CLI run before the cycle inspects state or
    /// triggers an <c>update-stable</c> teardown that would kill the run.
    /// Pure helper: no DI, no IO, deterministic against a fake
    /// <see cref="TimeProvider"/>.
    /// </summary>
    internal static async Task<QuiescenceWaitOutcome> WaitForQuiescenceAsync(
        Func<string?> getActiveJobId,
        TimeSpan timeout,
        TimeSpan pollInterval,
        TimeProvider time,
        CancellationToken ct)
    {
        var start = time.GetUtcNow();
        var first = getActiveJobId();
        if (first == null) return new QuiescenceWaitOutcome(QuiescenceWaitResult.AlreadyIdle, null, TimeSpan.Zero);

        var lastSeen = first;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var elapsed = time.GetUtcNow() - start;
            if (elapsed >= timeout)
            {
                return new QuiescenceWaitOutcome(QuiescenceWaitResult.TimedOut, lastSeen, elapsed);
            }

            var remaining = timeout - elapsed;
            var delay = pollInterval < remaining ? pollInterval : remaining;
            try { await Task.Delay(delay, time, ct); }
            catch (OperationCanceledException) { throw; }

            var current = getActiveJobId();
            if (current == null)
            {
                return new QuiescenceWaitOutcome(QuiescenceWaitResult.BecameIdle, lastSeen, time.GetUtcNow() - start);
            }
            lastSeen = current;
        }
    }

    internal enum QuiescenceWaitResult { AlreadyIdle, BecameIdle, TimedOut }

    internal sealed record QuiescenceWaitOutcome(
        QuiescenceWaitResult Result,
        string? LastSeenActiveJobId,
        TimeSpan Waited);

    private void TryRunUpdateStable(string workspace, string project, MetaCycleReport report)
    {
        var helperPath = _configuration["Supervisor:MetaCycleUpdateStableScript"];
        if (string.IsNullOrWhiteSpace(helperPath) || !File.Exists(helperPath))
        {
            _logger.LogWarning("MetaCycle update-stable requested but Supervisor:MetaCycleUpdateStableScript is missing or unreadable; skipping for {Project}", project);
            return;
        }
        try
        {
            var psi = new ProcessStartInfo("sh", $"\"{helperPath}\"")
            {
                WorkingDirectory = workspace,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return;
            // Bounded wait; the helper itself owns its own lifecycle.
            proc.WaitForExit(60_000);
            _logger.LogInformation("MetaCycle update-stable helper for {Project} exit={Code}", project, proc.HasExited ? proc.ExitCode : -1);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MetaCycle update-stable helper failed for {Project}", project);
        }
    }

    private string? QueueFollowUpTask(string workspace, string project, MetaCycleReport report)
    {
        // ADR-0024: route the new 1-preparation folder through the
        // typed TaskAccess Create mutation. The watch-path lookup
        // still goes through the scanner (config), but the folder
        // mint flows through TaskMutationService inside the layer.
        try
        {
            var topic = ExtractTopic(report.Action);
            var baseSlug = $"auto-fix-{topic}-{report.StartedAt:yyyyMMddHHmmss}";
            var entry = _scanner.GetWatchPaths().FirstOrDefault(e =>
                string.Equals(e.Name, project, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                _logger.LogWarning("MetaCycle queue-fix: no watch-path entry for {Project}", project);
                return null;
            }

            // Collision avoidance: a 1-preparation slug minted in the
            // same wall-clock second would otherwise refuse to land.
            // Append a short random suffix until the slug is free.
            var slug = baseSlug;
            if (_taskAccess.SlugExistsInLane(entry.Path, TaskStates.Preparation, slug))
            {
                slug = $"{baseSlug}-" + Guid.NewGuid().ToString("N")[..6];
            }

            var prompt = BuildFollowUpPrompt(project, report);
            var createResult = _taskAccess.MutateAsync(new TaskMutationRequest
            {
                Kind = TaskMutationKind.Create,
                CreateRequest = new CreateTaskRequest
                {
                    Id = slug,
                    Title = $"Meta-cycle: review {topic} ({report.CycleId})",
                    Agent = "claude",
                    TargetState = TaskStates.Preparation,
                    Order = 999,
                    WatchPath = entry.Path,
                    PromptMarkdown = prompt,
                },
            }).GetAwaiter().GetResult();

            if (createResult.Status != TaskMutationStatus.Applied)
            {
                _logger.LogWarning("MetaCycle queue-fix create refused for {Project}: {Status} {Message}",
                    project, createResult.Status, createResult.Message);
                return null;
            }
            return slug;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MetaCycle queue-fix folder creation failed for {Project}", project);
            return null;
        }
    }

    private static string ExtractTopic(MetaCycleAction action)
    {
        // Reasons are formatted as "queue-fix:<topic>" or "escalate:<topic>".
        var reason = action.Reason ?? string.Empty;
        var colon = reason.IndexOf(':');
        if (colon >= 0 && colon < reason.Length - 1) return reason[(colon + 1)..];
        return "needs-human";
    }

    private static string BuildFollowUpPrompt(string project, MetaCycleReport report)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLf("# Meta-cycle follow-up");
        sb.AppendLf();
        sb.AppendLf($"The orchestrator meta-cycle for **{project}** flagged this batch as **{report.Verdict}**.");
        sb.AppendLf();
        sb.AppendLf($"- Cycle id: `{report.CycleId}`");
        sb.AppendLf($"- Started: {report.StartedAt:u}");
        sb.AppendLf($"- Action: `{report.Action.Kind}` (reason: `{report.Action.Reason}`)");
        sb.AppendLf();
        sb.AppendLf("## Findings");
        foreach (var f in report.Findings)
        {
            sb.AppendLf($"- **{f.Severity}** `{f.Topic}` - {f.Message}");
        }
        sb.AppendLf();
        sb.AppendLf("## What to do");
        sb.AppendLf();
        sb.AppendLf("Review the findings above and decide whether they reflect a real problem or a false positive. The meta-cycle does not act on this task automatically; a human must move it to `2-ready` after deciding the right scope.");
        sb.AppendLf();
        sb.AppendLf("If the meta-cycle was wrong, capture why so the rules can be tightened.");
        sb.AppendLf();
        sb.AppendLf("If the meta-cycle was right, scope the fix and queue it as a separate task; this folder records the trigger.");
        return sb.ToString();
    }

    private void WriteReport(string workspace, MetaCycleReport report)
    {
        try
        {
            var dir = SupervisorLogPaths.MetaCycleDir(workspace, report.Project);
            Directory.CreateDirectory(dir);
            var path = SupervisorLogPaths.MetaCycleReportFile(workspace, report.Project, report.CycleId);
            File.WriteAllText(path, JsonSerializer.Serialize(report, Json));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MetaCycle WriteReport failed for {Project} cycle {CycleId}", report.Project, report.CycleId);
        }
    }

    private void AppendTailLog(string workspace, MetaCycleReport report)
    {
        try
        {
            var dir = SupervisorLogPaths.MetaCycleDir(workspace, report.Project);
            Directory.CreateDirectory(dir);
            var line = $"{report.CompletedAt:u}\t{report.CycleId}\t{report.Verdict}\t{report.Action.Kind}\t{report.Action.Reason}";
            File.AppendAllText(SupervisorLogPaths.MetaCycleTailLog(workspace, report.Project), line + Environment.NewLine);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MetaCycle AppendTailLog failed for {Project}", report.Project);
        }
    }

    private static string ReadFirstChars(string path, int max)
    {
        try
        {
            using var reader = File.OpenText(path);
            var buf = new char[max];
            var read = reader.Read(buf, 0, buf.Length);
            return new string(buf, 0, read).Replace("\r", "").Replace("\n", " ").Trim();
        }
        catch { return string.Empty; }
    }

    private static string NewCycleId(DateTime nowUtc)
    {
        // ULID-like: 12-char timestamp + 8 random chars. Lexically sortable.
        var ts = nowUtc.ToString("yyyyMMddHHmm");
        var rand = Guid.NewGuid().ToString("N")[..8];
        return $"mc-{ts}-{rand}";
    }

    /// <summary>
    /// Per-project, in-memory cycle bookkeeping. Recreated on backend restart
    /// (the cycle is best-effort; on restart the bootstrap logic seeds a new
    /// "now" cutoff so we do not double-count older `4-review` jobs).
    /// </summary>
    private sealed class ProjectCycleState
    {
        public DateTime? LastCycleAt;
        public string? LastCycleId;
        public bool SeededFromBootstrap;
        private readonly List<DateTime> _fixTimestamps = new();

        public void RecordFix(DateTime at)
        {
            lock (_fixTimestamps)
            {
                _fixTimestamps.Add(at);
                _fixTimestamps.RemoveAll(t => t < at - TimeSpan.FromHours(2));
            }
        }

        public int CountFixesSince(DateTime cutoff)
        {
            lock (_fixTimestamps)
            {
                return _fixTimestamps.Count(t => t > cutoff);
            }
        }
    }
}
