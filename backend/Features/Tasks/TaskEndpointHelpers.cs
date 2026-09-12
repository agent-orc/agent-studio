

namespace AgentStudio.Tasks;

/// <summary>
/// Internal helpers shared across the job endpoint groups: the
/// <see cref="MoveJobOutcome"/> to <see cref="IResult"/> translation and the
/// runtime overlay (CLI execution + auto-loop snapshot) applied to
/// <see cref="TaskInfo"/> and <see cref="TaskDetail"/> on read.
/// </summary>
internal static class TaskEndpointHelpers
{
    /// <summary>
    /// Phase 2b of the watchPath-encapsulation cleanup (ASS-1760): the external
    /// API addresses a project by a path-free handle — a short code / Kürzel
    /// (e.g. <c>ASS</c>), a stable <c>PROJ-NNN</c> id, or the display name used
    /// by project-scoped UI tabs — and the server resolves
    /// it to the project's storage watchPath here, so the filesystem layout never
    /// has to travel over the wire as a query parameter. The deprecated
    /// <paramref name="watchPath"/> query param is still honoured for legacy
    /// callers during the migration; when <paramref name="project"/> is set it
    /// wins. An unknown handle falls through to <paramref name="watchPath"/>
    /// (usually null), so the read/mutation lands on its own not-found path
    /// rather than silently targeting the wrong project.
    /// </summary>
    internal static string? ResolveWatchPath(
        AgentStudio.Registry.ProjectRegistry projects,
        string? project,
        string? watchPath)
    {
        if (string.IsNullOrWhiteSpace(project)) return watchPath;

        var handle = project.Trim();
        var record = projects.FindByShortCode(handle)
            ?? projects.FindByIdOrDisplayName(handle);

        return record is { StorageLocation: { Length: > 0 } storage } ? storage : watchPath;
    }

    /// <summary>
    /// Resolves a scanned task back to its immutable registry identity and its
    /// canonical watch-path entry. Display names are deliberately not used:
    /// they are mutable and are not unique, while storage location is the
    /// registry-backed identity bridge used by task scanning.
    /// </summary>
    internal static TaskProjectContext? ResolveProjectContext(
        AgentStudio.Registry.ProjectRegistry projects,
        TaskScannerService scanner,
        TaskInfo task)
    {
        var project = projects.FindByStorageLocation(task.WatchPath);
        if (project == null) return null;
        var entry = scanner.GetWatchPaths().FirstOrDefault(candidate =>
            SameWatchPath(candidate.Path, project.StorageLocation));
        return entry == null ? null : new TaskProjectContext(project, entry);
    }

    private static bool SameWatchPath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch
        {
            return string.Equals(
                left.Replace('\\', '/').TrimEnd('/'),
                right.Replace('\\', '/').TrimEnd('/'),
                StringComparison.OrdinalIgnoreCase);
        }
    }

    internal sealed record TaskProjectContext(ProjectRecord Project, WatchPathEntry Entry);

    internal static IResult MoveResult(MoveJobOutcome outcome) => outcome.Status switch
    {
        MoveJobStatus.Success => Results.Ok(),
        MoveJobStatus.NotFound => Results.NotFound(),
        MoveJobStatus.TargetFolderExists => Results.Conflict(new { error = outcome.Message }),
        MoveJobStatus.IntegrationFailed => Results.Conflict(new { error = outcome.Message }),
        MoveJobStatus.DirectoryLocked => Results.Json(
            new { error = outcome.Message ?? "Task folder is temporarily locked by another process. Retry after the active process releases its file handles." },
            statusCode: StatusCodes.Status423Locked),
        _ => Results.Json(new { error = outcome.Message ?? "Failed to move job" }, statusCode: StatusCodes.Status500InternalServerError)
    };

    internal static TaskInfo WithRuntime(TaskInfo job, CliRouter router, TaskRunnerService runners)
        => WithRuntime(job, router, runners, tokensByJobId: null, verdictsByJobKey: null);

    internal static TaskInfo WithRuntime(
        TaskInfo job,
        CliRouter router,
        TaskRunnerService runners,
        IReadOnlyDictionary<string, TaskTokenSummary>? tokensByJobId)
        => WithRuntime(job, router, runners, tokensByJobId, verdictsByJobKey: null, waitsOnByJobKey: null, transitiveWaitersByJobKey: null);

    internal static TaskInfo WithRuntime(
        TaskInfo job,
        CliRouter router,
        TaskRunnerService runners,
        IReadOnlyDictionary<string, TaskTokenSummary>? tokensByJobId,
        IReadOnlyDictionary<string, string>? verdictsByJobKey)
        => WithRuntime(job, router, runners, tokensByJobId, verdictsByJobKey, waitsOnByJobKey: null, transitiveWaitersByJobKey: null);

    /// <summary>
    /// Overlay variant that also folds in per-job orchestrator token totals,
    /// the latest orchestrator-review verdict, and the AGT-2029 waits-on
    /// status. The caller is expected to have built the lookups once per
    /// request, so this stays O(1) per job — the perf contract locked by
    /// <c>JobsEndpointPerfTests.WithRuntime_Over200Jobs_FinishesWellUnderOneSecond</c>
    /// still holds.
    /// </summary>
    internal static TaskInfo WithRuntime(
        TaskInfo job,
        CliRouter router,
        TaskRunnerService runners,
        IReadOnlyDictionary<string, TaskTokenSummary>? tokensByJobId,
        IReadOnlyDictionary<string, string>? verdictsByJobKey,
        IReadOnlyDictionary<string, WaitsOnStatus>? waitsOnByJobKey,
        IReadOnlyDictionary<string, TransitiveWaitersStatus>? transitiveWaitersByJobKey)
    {
        // Lane is the single source of truth for "is this card live". A job
        // outside 3-progress has finished or been moved on; surfacing a stale
        // (or even live) Execution snapshot here lets a "running" status leak
        // onto cards in 4-auto-review / 5-human-review / 6-completed, which
        // the per-card pill then renders as a misleading "Running" badge.
        // Clearing at the wire-overlay layer keeps Lane > Execution-Status
        // > Default as the deterministic precedence for every consumer.
        var exec = job.State == TaskStates.Progress
            ? router.Get(job.CliType).GetExecution(job.TaskKey)
            : null;
        // Look up auto-loop state by ProjectName (O(1) ConcurrentDictionary
        // hit) rather than by re-scanning all jobs from disk. Locked by
        // JobsEndpointPerfTests.WithRuntime_Over200Jobs_FinishesWellUnderOneSecond.
        var loop = runners.GetStuckLoopStateForJob(job.Id, job.ProjectName);
        // The summarizer is fire-and-forget after a job lands in 4-review.
        // We surface its in-progress state on the TaskInfo so the kanban
        // card can show "auto-reviewing" instead of looking idle while
        // the Haiku call is still working. Only return non-None states
        // so the field stays absent on cards where nothing is happening.
        var summary = runners.SummaryService.GetState(job.TaskKey);
        TaskTokenSummary? tokens = null;
        if (tokensByJobId != null && tokensByJobId.TryGetValue(job.TaskKey, out var t) && t.TotalTokens > 0)
        {
            tokens = TokenSummaryService.WithModelFallback(t, exec?.Model ?? job.Model);
        }
        string? verdict = null;
        if (verdictsByJobKey != null && verdictsByJobKey.TryGetValue(job.TaskKey, out var v))
        {
            verdict = v;
        }
        // AGT-2029: fold in the pre-computed waits-on status (which dependsOn
        // targets are fulfilled / open, blocked, or on a cycle) so the kanban
        // card can render a state-aware, navigable dependency chip. Only cards
        // with dependsOn edges get an entry; the rest carry null.
        WaitsOnStatus? waitsOn = null;
        if (waitsOnByJobKey != null && waitsOnByJobKey.TryGetValue(job.TaskKey, out var w))
        {
            waitsOn = w;
        }
        TransitiveWaitersStatus? transitiveWaiters = null;
        if (transitiveWaitersByJobKey != null
            && transitiveWaitersByJobKey.TryGetValue(job.TaskKey, out var impact))
        {
            transitiveWaiters = impact;
        }
        // Reconcile a stale Warn-class outcome chip against the final verdict: an
        // accepted card must not surface a classifier-unknown/heuristic-done/
        // missing-terminal-sentinel chip that contradicts its accept (ASS-775).
        // The scanner already clears this when the accept note is in the log; this
        // covers 5-human-review accepts whose accept note never reached the log.
        var outcomeIssue = IsOutcomeIssueFromSupersededAttempt(job)
            || TaskOutcomeIssueReconciliation.ShouldSuppress(
                job.OutcomeIssue, verdictAccepted: string.Equals(verdict, "accept", StringComparison.Ordinal))
            ? null
            : job.OutcomeIssue;
        // ASS-1751: classify why a 3-progress card looks "untouched" — a live
        // run, a failed run waiting out the rapid-crash backoff, or an orphan
        // killed by a backend restart. Gated on the Progress lane (same O(1)
        // runner lookup as the auto-loop snapshot above) so the perf contract
        // holds; pure visibility, no behavior. Null on every other lane.
        TaskRunActivity? runActivity = job.State == TaskStates.Progress
            ? TaskRunActivityClassifier.Classify(
                runners.GetRunActivityForJob(job.Id, job.ProjectName),
                exec,
                outcomeIssue,
                DateTime.UtcNow)
            : null;
        // AGT-2003: project the active run-lease owner onto the card while the
        // task is in-progress. Same Progress-lane gate + O(1) in-memory peek as
        // RunActivity above, so the JobsEndpointPerfTests contract still holds. A
        // remote runner acquires this lease; a plain local in-process run holds
        // none, so this stays null and the card shows the quiet local presentation.
        TaskRunnerInfo? runner = job.State == TaskStates.Progress
            ? runners.ResolveRunnerBadge(job.TaskKey)
            : null;
        // AGT-2069: spawn-visibility + spawn-contract projection for planning
        // tasks. Gated strictly on planning mode (rare) so the two small sidecar
        // reads never touch the coding-card common case and the
        // JobsEndpointPerfTests contract holds. Null on every non-planning card.
        var planningSpawn = BuildPlanningSpawnSummary(job);
        var conceptDossier = BuildConceptDossierSummary(job);
        return job with
        {
            Execution = exec,
            // A local run tracks its fallback in ProjectRunner's in-memory
            // active-run table; a remote-claimed or review-claimed run has no
            // such instance, so it falls back to the durable
            // quota-fallback.json marker TaskScannerService already projected
            // onto `job` (AGT-2751). The in-memory value wins when both exist
            // since it is live for the exact run in progress.
            QuotaFallback = job.State == TaskStates.Progress
                ? runners.GetQuotaFallbackForJob(job.Id, job.ProjectName) ?? job.QuotaFallback
                : null,
            OutcomeIssue = outcomeIssue,
            RunActivity = runActivity,
            Runner = runner,
            ExecutionLocation = runners.ResolveExecutionLocation(job, exec, runActivity),
            AutoLoop = loop == null ? null : new AutoLoopSnapshot
            {
                Iteration = loop.IterationCount,
                MaxIterations = runners.StuckLoopBudget.MaxIterations,
                TokensUsed = loop.CumulativeOrchestratorTokens,
                MaxTokens = runners.StuckLoopBudget.MaxOrchestratorTokens,
                StartedAt = loop.FirstAt,
                LastAt = loop.LastAt,
                LastQuestion = loop.LastQuestion,
                LastReply = loop.LastReply,
                LastError = loop.LastError
            },
            SummaryState = summary != null && summary.Status != TaskSummaryStatus.None ? summary : null,
            TokenSummary = tokens,
            OrchestratorVerdict = verdict,
            WaitsOn = waitsOn,
            TransitiveWaiters = transitiveWaiters,
            PlanningSpawn = planningSpawn,
            ConceptDossier = conceptDossier
        };
    }

    /// <summary>
    /// AGT-2069 — build the read-time spawn-visibility + spawn-contract summary
    /// for a planning task from its two app-owned sidecars: the AGT-2028 spawn
    /// ledger (<c>.metadata/spawned-tasks.jsonl</c>) and the no-follow-up
    /// declaration (<c>.metadata/planning-closure.json</c>). Returns null for any
    /// non-planning task so the field stays absent on the coding-card common
    /// case; the two reads only ever run for the rare planning card, keeping the
    /// per-job overlay O(1) for the board.
    /// </summary>
    internal static PlanningSpawnSummary? BuildPlanningSpawnSummary(TaskInfo job)
    {
        if (!PlanningCompletionGate.Applies(job.Mode) && !TaskKinds.IsEpic(job.Kind)) return null;
        if (string.IsNullOrWhiteSpace(job.FolderPath)) return new PlanningSpawnSummary();

        var spawned = SpawnedTaskLedger.Read(job.FolderPath)
            .Select(r => new PlanningSpawnRef
            {
                TargetKey = r.TargetKey,
                TargetJobId = r.TargetJobId,
                TargetProject = r.TargetProject,
                Reason = r.Reason,
                At = r.At,
            })
            .ToList();

        var closure = PlanningClosureStore.Read(job.FolderPath);
        return new PlanningSpawnSummary
        {
            Spawned = spawned,
            NoFollowUpDeclared = closure?.NoFollowUpDeclared ?? false,
            NoFollowUpReason = closure?.Reason,
            DeclaredAt = closure?.DeclaredAt,
        };
    }

    /// <summary>
    /// Interim read-time dossier link for concept cards. This deliberately reads
    /// the two result documents instead of creating a second structured
    /// workbench-reference carrier before <c>references.workbenches</c> lands.
    /// </summary>
    internal static ConceptDossierSummary? BuildConceptDossierSummary(TaskInfo job)
    {
        if (!TaskModes.IsConcept(job.Mode)) return null;
        return string.IsNullOrWhiteSpace(job.FolderPath)
            ? new ConceptDossierSummary()
            : ConceptDossierContract.Read(job.FolderPath);
    }

    /// <summary>
    /// AGT-2046: folds the batched board merge signal onto each job. List routes
    /// read the lookup from <see cref="TaskListGitProjectionCache"/> while detail
    /// routes may build a bounded lookup directly. The fold itself stays an O(1)
    /// dictionary hit per job. Jobs without a committed or merged anchor are
    /// passed through untouched.
    /// </summary>
    internal static IEnumerable<TaskInfo> WithMergeSignal(
        this IEnumerable<TaskInfo> jobs,
        IReadOnlyDictionary<string, TaskMergeSignal> mergeByJobKey)
        => jobs.Select(job =>
            mergeByJobKey.TryGetValue(job.TaskKey, out var signal)
                ? job with { MergeSignal = signal }
                : job);

    /// <summary>
    /// Folds the newest-attempt live pipeline projection onto board/detail
    /// tasks. The lookup is built once per request; this method is an O(1)
    /// dictionary read per task.
    /// </summary>
    internal static IEnumerable<TaskInfo> WithLiveStatus(
        this IEnumerable<TaskInfo> jobs,
        IReadOnlyDictionary<string, TaskLiveStatus> liveByJobKey)
        => jobs.Select(job =>
            liveByJobKey.TryGetValue(job.TaskKey, out var status)
                ? job with { LiveStatus = status }
                : job);

    /// <summary>
    /// Projects the cached TokenEconomy candidate note onto Ready cards and
    /// their quota-wait reason. The route remains unchanged.
    /// </summary>
    internal static IEnumerable<TaskInfo> WithBetterCandidates(
        this IEnumerable<TaskInfo> jobs,
        AgentStudio.Pipeline.BetterModelCandidateService candidates,
        AgentStudio.Projects.ProjectSettingsService settings)
        => jobs.Select(job => WithBetterCandidates(job, candidates, settings));

    internal static TaskInfo WithBetterCandidates(
        TaskInfo job,
        AgentStudio.Pipeline.BetterModelCandidateService candidates,
        AgentStudio.Projects.ProjectSettingsService settings)
    {
        if (!string.Equals(job.State, TaskStates.Ready, StringComparison.OrdinalIgnoreCase)) return job;
        var routeCandidates = candidates.Find(
            job.Model,
            job.ThinkingLevel,
            settings.Get(job.ProjectName).BenchmarkCapabilityClass);
        return job with
        {
            BetterCandidates = routeCandidates,
            QuotaWait = job.QuotaWait is null
                ? null
                : job.QuotaWait with { BetterCandidates = routeCandidates },
        };
    }

    /// <summary>
    /// AGT-2202: folds the batched per-task integration verdict onto each accepted
    /// card. List routes use the cache-only task-list Git projection, so this is
    /// an O(1) dictionary hit per job with no request-thread Git work. Jobs without
    /// a verdict because they are outside an accepted lane pass through untouched.
    /// </summary>
    internal static IEnumerable<TaskInfo> WithIntegrationStatus(
        this IEnumerable<TaskInfo> jobs,
        IReadOnlyDictionary<string, TaskIntegrationStatus> integrationByJobKey)
        => jobs.Select(job =>
            integrationByJobKey.TryGetValue(job.TaskKey, out var status)
                ? job with { Integration = status }
                : job);

    /// <summary>
    /// PUB-1: folds the batched per-task publish signal onto each accepted card.
    /// List routes use the cache-only task-list Git projection, so this stays an
    /// O(1) dictionary hit per job. Jobs without a signal because they are not
    /// accepted or touch no derived target pass through untouched.
    /// </summary>
    internal static IEnumerable<TaskInfo> WithPublishSignal(
        this IEnumerable<TaskInfo> jobs,
        IReadOnlyDictionary<string, TaskPublishSignal> publishByJobKey)
        => jobs.Select(job =>
            publishByJobKey.TryGetValue(job.TaskKey, out var signal)
                ? job with { PublishSignal = signal }
                : job);

    /// <summary>
    /// AGT-2029 — builds a per-TaskKey lookup of waits-on status for the jobs
    /// that actually carry dependsOn edges. Resolution is <b>archive-inclusive</b>
    /// (a dependency is fulfilled when its target reaches 6-completed OR
    /// 7-archive, and the board snapshot omits archive), so the key index is
    /// read from the archive-inclusive snapshot's published reference index.
    /// Cards without dependencies are skipped, keeping the common case free.
    /// </summary>
    internal static DependencyGraphLookups BuildDependencyGraphLookups(
        IEnumerable<TaskInfo> jobs,
        TaskScannerService scanner,
        IEnumerable<TaskInfo>? eligibleWaiters = null)
    {
        var snapshot = jobs as IReadOnlyCollection<TaskInfo> ?? jobs.ToList();
        var withDeps = snapshot.Where(j => j.References?.DependsOn.Count > 0).ToList();
        var humanReview = snapshot.Where(j => j.State == TaskStates.HumanReview).ToList();
        if (withDeps.Count == 0 && humanReview.Count == 0) return new();

        // The archive-inclusive index is built once per snapshot generation
        // (AGT-2342 reference-index cache); keys are globally unique across
        // projects, so this resolves cross-project targets too.
        var index = scanner.GetReferenceIndex();
        var eligibleKeys = (eligibleWaiters ?? snapshot)
            .Where(j => !string.IsNullOrWhiteSpace(j.Key))
            .Select(j => j.Key!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var waitsOn = new Dictionary<string, WaitsOnStatus>(StringComparer.Ordinal);
        foreach (var job in withDeps)
        {
            waitsOn[job.TaskKey] = index.EvaluateWaitsOn(job);
        }
        var transitiveWaiters = new Dictionary<string, TransitiveWaitersStatus>(StringComparer.Ordinal);
        foreach (var job in humanReview)
        {
            var impact = index.FindTransitiveWaiters(job.Key, eligibleKeys);
            if (impact.Count > 0) transitiveWaiters[job.TaskKey] = impact;
        }
        return new(waitsOn, transitiveWaiters);
    }

    internal sealed record DependencyGraphLookups(
        IReadOnlyDictionary<string, WaitsOnStatus> WaitsOn,
        IReadOnlyDictionary<string, TransitiveWaitersStatus> TransitiveWaiters)
    {
        public DependencyGraphLookups() : this(
            new Dictionary<string, WaitsOnStatus>(StringComparer.Ordinal),
            new Dictionary<string, TransitiveWaitersStatus>(StringComparer.Ordinal)) { }
    }

    /// <summary>
    /// Builds a per-TaskKey lookup of the latest orchestrator-review
    /// verdict, sourced from <see cref="ReviewDecisionLog"/>. One JSONL
    /// read per (workspace, project) pair so the per-job overlay stays
    /// O(1). Maps <see cref="ReviewDecisionKind"/> to the wire enum
    /// (<c>reissue</c> / <c>escalate</c> / <c>accept</c>); skipped
    /// records do not surface a verdict.
    /// </summary>
    internal static Dictionary<string, string> BuildOrchestratorVerdictLookup(
        IEnumerable<TaskInfo> jobs,
        IConfiguration configuration)
    {
        var verdicts = new Dictionary<string, string>(StringComparer.Ordinal);
        var workspace = configuration["TaskRepository"];
        if (string.IsNullOrWhiteSpace(workspace)) return verdicts;

        // Resolve each (workspace, project) journal index at most once per
        // request. ReviewDecisionLog validates a tiny fingerprint and parses
        // only appended bytes, so a board poll never rehydrates the full JSONL.
        var byProject = new Dictionary<string, IReadOnlyDictionary<string, ReviewDecisionRecord>>(StringComparer.OrdinalIgnoreCase);
        foreach (var job in jobs)
        {
            if (string.IsNullOrWhiteSpace(job.ProjectName)) continue;
            if (!byProject.TryGetValue(job.ProjectName, out var latestByJob))
            {
                try { latestByJob = ReviewDecisionLog.ReadLatestByJob(workspace!, job.ProjectName); }
                catch { latestByJob = new Dictionary<string, ReviewDecisionRecord>(StringComparer.Ordinal); }
                byProject[job.ProjectName] = latestByJob;
            }
            if (!latestByJob.TryGetValue(job.Id, out var latest)) continue;
            var attemptEpoch = CurrentAttemptEpoch(job);
            if (attemptEpoch.HasValue && latest.CreatedAt < attemptEpoch.Value) continue;
            var verdict = latest.Kind switch
            {
                ReviewDecisionKind.Reissue      => "reissue",
                ReviewDecisionKind.Escalate     => "escalate",
                ReviewDecisionKind.AcceptAsDone => "accept",
                _                                => (string?)null
            };
            if (verdict != null) verdicts[job.TaskKey] = verdict;
        }
        return verdicts;
    }

    /// <summary>
    /// The latest transition into Progress is the claim boundary for the
    /// currently visible attempt. A decision older than that boundary belongs
    /// to the superseded attempt and must not drive card badges or banners.
    /// Legacy tasks without transition provenance retain the old projection.
    /// </summary>
    private static DateTime? CurrentAttemptEpoch(TaskInfo job)
    {
        var transitions = job.Provenance?.Transitions;
        if (transitions is not { Count: > 0 }) return null;
        for (var i = transitions.Count - 1; i >= 0; i--)
        {
            if (string.Equals(transitions[i].Lane, TaskStates.Progress, StringComparison.Ordinal))
                return transitions[i].AtUtc;
        }
        return null;
    }

    /// <summary>
    /// Runner outcome issues are derived from the output log and may survive a
    /// lane-only requeue. Once a newer Progress claim exists, an issue last
    /// observed before that boundary belongs to the superseded attempt and
    /// must not drive the current card's Blocked banner.
    /// </summary>
    internal static bool IsOutcomeIssueFromSupersededAttempt(TaskInfo job)
    {
        var issueAt = job.OutcomeIssue?.LastSeenAt;
        var attemptEpoch = CurrentAttemptEpoch(job);
        return issueAt.HasValue
            && attemptEpoch.HasValue
            && issueAt.Value < attemptEpoch.Value;
    }

    /// <summary>
    /// Builds the per-project → per-job token lookup used by
    /// <c>WithRuntime</c> in the listing endpoints. Reads each unique
    /// hybrid project projection at most once.
    /// </summary>
    internal static Dictionary<string, TaskTokenSummary> BuildTokenLookup(
        IEnumerable<TaskInfo> jobs,
        ITokenAggregator tokens)
    {
        // Read each project projection at most once. Keyed by TaskKey
        // (watchPath::jobId) so jobs that share an id across watched
        // workspaces stay distinct.
        var byProject = new Dictionary<string, Dictionary<string, TaskTokenSummary>>(StringComparer.OrdinalIgnoreCase);
        var merged = new Dictionary<string, TaskTokenSummary>(StringComparer.Ordinal);
        foreach (var job in jobs)
        {
            if (string.IsNullOrWhiteSpace(job.WatchPath)) continue;
            var projectKey = string.IsNullOrWhiteSpace(job.ProjectName)
                ? job.WatchPath
                : $"{job.ProjectName}\n{job.WatchPath}";
            if (!byProject.TryGetValue(projectKey, out var perJob))
            {
                perJob = tokens.WorkspacePerJob(job.ProjectName, job.WatchPath);
                byProject[projectKey] = perJob;
            }
            if (perJob.TryGetValue(job.Id, out var t))
            {
                merged[job.TaskKey] = t;
            }
        }
        return merged;
    }

    internal static TaskDetail WithRuntime(TaskDetail detail, CliRouter router, TaskRunnerService runners)
        => detail with { Info = WithRuntime(detail.Info, router, runners) };

    internal static TaskDetail WithRuntime(
        TaskDetail detail,
        CliRouter router,
        TaskRunnerService runners,
        IReadOnlyDictionary<string, TaskTokenSummary>? tokensByJobId)
        => detail with { Info = WithRuntime(detail.Info, router, runners, tokensByJobId) };

    internal static TaskDetail WithRuntime(
        TaskDetail detail,
        CliRouter router,
        TaskRunnerService runners,
        IReadOnlyDictionary<string, TaskTokenSummary>? tokensByJobId,
        IReadOnlyDictionary<string, string>? verdictsByJobKey)
        => detail with { Info = WithRuntime(detail.Info, router, runners, tokensByJobId, verdictsByJobKey, waitsOnByJobKey: null, transitiveWaitersByJobKey: null) };

    internal static TaskDetail WithRuntime(
        TaskDetail detail,
        CliRouter router,
        TaskRunnerService runners,
        IReadOnlyDictionary<string, TaskTokenSummary>? tokensByJobId,
        IReadOnlyDictionary<string, string>? verdictsByJobKey,
        IReadOnlyDictionary<string, WaitsOnStatus>? waitsOnByJobKey,
        IReadOnlyDictionary<string, TransitiveWaitersStatus>? transitiveWaitersByJobKey)
        => detail with { Info = WithRuntime(detail.Info, router, runners, tokensByJobId, verdictsByJobKey, waitsOnByJobKey, transitiveWaitersByJobKey) };
}
