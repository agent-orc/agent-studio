

namespace AgentStudio.Projects;

/// <summary>
/// Cycle 5: <c>GET /api/projects/{projectName}/snapshot</c>. One round-trip
/// that returns every per-project field the project-detail panel polled
/// individually pre-Cycle-5 (settings, runner status for the project,
/// orchestrator log tail, orchestrator session, post-run review-decisions
/// pending, live runner pending decisions). The pre-Cycle-5 panel
/// fan-out was 6+ HTTP requests every 5 s; with the snapshot it is one
/// request, and the per-project call shape stays cache-friendly inside
/// the backend (every read goes through the TaskIndexCache from Cycle 1).
///
/// <para>The standalone endpoints stay live so existing consumers keep
/// working - the snapshot is purely additive. project-detail is the only
/// caller that fans out across all of them on a single tick, which is
/// why the snapshot is project-detail-shaped and not a generic
/// "everything I might ever need" mega-endpoint.</para>
/// </summary>
public static class ProjectSnapshotEndpoints
{
    // Cycle 5 review-decisions cache. The 4-auto-review scan walks every
    // job folder in the lane and reads cli-output.log to find unresolved
    // [[TASK_NEEDS_INPUT]] sentinels - that's ~225 ms p95 against the
    // real workspace and dominates the snapshot's response time. The
    // frontend polls the snapshot every 5 s, so a TTL just under that
    // means the second poll always hits cache, the first polls
    // through, and a fresh sentinel still appears within 5 s.
    // Per-(workspace, project, lane-mtime) key so the cache invalidates
    // automatically when the lane folder content changes.
    private sealed record ReviewCacheEntry(DateTime CapturedAt, long LaneMtimeTicks, List<object> Items);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ReviewCacheEntry> ReviewCache = new();
    private static readonly TimeSpan ReviewCacheTtl = TimeSpan.FromSeconds(3);
    public static long ReviewCacheHits;
    public static long ReviewCacheMisses;

    public static void MapProjectSnapshotEndpoints(this WebApplication app)
    {
        app.MapGet("/api/projects/{projectName}/snapshot", (
            string projectName,
            TaskScannerService scanner,
            TaskRunnerService runner,
            ProjectSettingsService settingsSvc,
            OrchestratorLog log,
            OrchestratorSessionStore sessionStore,
            ITaskAccess taskAccess,
            PublishTargetService publish,
            AgentStudio.Registry.ProjectRegistry projectRegistry) =>
        {
            // Resolve the watch path once. Every per-project field below
            // either filters from cached state or reads a single
            // workspace file; none of them re-scan disk for the job
            // catalogue (TaskIndexCache from Cycle 1 covers that).
            var entries = scanner.GetWatchPaths();
            var entry = entries.FirstOrDefault(e =>
                string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });

            // Registry RootPath wins once a record exists (ADR-0042; same
            // precedence TaskRunnerService applies at boot) so the "Working
            // directory" shown here always matches what the runner actually
            // used, instead of the pre-registry WatchPaths value going stale.
            var registryRecord = projectRegistry.FindByStorageLocation(entry.Path);
            var effectiveRootPath = string.IsNullOrWhiteSpace(registryRecord?.RootPath)
                ? entry.RootPath
                : registryRecord.RootPath;

            // 1) Settings (auto-commit, runner mode, orchestrator model).
            var settings = settingsSvc.Get(projectName);

            // 2) Runner status snapshot. We pluck just this project's slot
            //    out of the full RunnerStatus to keep payload tight - the
            //    board endpoint already serves the full shape.
            var fullStatus = runner.GetStatus();
            ProjectRunnerStatus? projectRunner = null;
            if (fullStatus.Projects != null && fullStatus.Projects.TryGetValue(projectName, out var ps))
            {
                projectRunner = ps;
            }

            // 3) Orchestrator log tail (last 5 entries, newest first - matches
            //    project-detail.refreshAll's existing slice).
            var allLog = log.Read(entry.Path);
            var logTail = allLog.Skip(Math.Max(0, allLog.Count - 5)).Reverse().ToList();

            // 4) Orchestrator session.
            var session = sessionStore.Read(entry.Path);

            // 5) Post-run pending review decisions. Cached at endpoint
            //    level on (workspace, project, lane-mtime) for 3 s - the
            //    frontend polls every 5 s, so the second poll hits cache,
            //    the first pays the disk walk, and a fresh sentinel still
            //    surfaces within one poll cycle. Without the cache this
            //    one scan dominated the snapshot at ~225 ms p95.
            var reviewDecisions = ReadReviewDecisionsCached(entry.Path, projectName, taskAccess);

            // 6) Live runner pending decisions (during an active run, the
            //    sentinel emitted by the in-flight job).
            var livePending = runner.GetPendingDecisions(projectName) ?? Array.Empty<PendingDecisionEntry>();
            var liveItems = livePending.Select(e => new
            {
                jobId = e.JobId,
                title = e.Title,
                kind = e.Decision.Kind switch
                {
                    PendingDecisionKind.NeedsInput => "needs-input",
                    PendingDecisionKind.Blocked => "blocked",
                    _ => "unknown"
                },
                reason = e.Decision.Reason,
                detectedAt = e.Decision.DetectedAt
            }).ToList();

            return Results.Ok(new
            {
                project = projectName,
                capturedAt = DateTime.UtcNow,
                paths = new
                {
                    path = entry.Path,
                    rootPath = effectiveRootPath,
                    repositoryPath = registryRecord?.RepositoryPath ?? entry.RepositoryPath
                },
                settings = new
                {
                    autoCommit = settings.AutoCommit,
                    crashRecoveryEnabled = settings.CrashRecoveryEnabled,
                    autoPushStrategy = AutoPushStrategies.Normalize(settings.AutoPushStrategy),
                    runnerMode = settings.RunnerMode,
                    pickupMode = ProjectExecutionPolicy.ResolvePickupMode(settings),
                    executionLocation = ProjectExecutionPolicy.ResolveExecutionLocation(settings),
                    orchestratorModel = settings.OrchestratorModel,
                    orchestratorThinkingLevel = settings.OrchestratorThinkingLevel,
                    // AGT-2839: raw override (null = inherit) plus the value the
                    // integration gate actually applies.
                    integrationGateReviewReuse = settings.IntegrationGateReviewReuse,
                    integrationGateReviewReuseEffective = IntegrationGateReusePolicy.IsEnabled(settings),
                    maxReviewConcernRounds = settings.MaxReviewConcernRounds,
                    scopedReviewAfterFinding = settings.ScopedReviewAfterFinding,
                    scopedReviewMaximumDeltaFiles = settings.ScopedReviewMaximumDeltaFiles,
                    // F35: every lane resolved to its effective strategy.
                    // The kanban renders the lane-header icon and the
                    // drag-disabled hint from this map.
                    laneSortStrategies = TaskStates.All.ToDictionary(
                        lane => lane,
                        lane => LaneSortStrategies.Resolve(settings, lane),
                        StringComparer.OrdinalIgnoreCase)
                },
                runnerStatus = projectRunner == null ? null : new
                {
                    projectRunner.ProjectName,
                    projectRunner.Mode,
                    projectRunner.ActiveJobId,
                    projectRunner.ActiveExecution,
                    projectRunner.QueuedJobIds
                },
                orchestratorLogTail = logTail,
                orchestratorSession = session,
                reviewDecisionsPending = reviewDecisions,
                runnerPendingDecisions = liveItems,
                // PUB-1: derived publish targets + pending deltas for the Hub
                // publish badges. Read-only, repo-fact-derived, cached per project.
                publishTargets = publish.GetProjectPublishStatus(projectName).Targets,
                queueHealth = ReadQueueHealth(entry.Path, taskAccess),
                _diag = new { reviewHits = ReviewCacheHits, reviewMisses = ReviewCacheMisses }
            });
        });

        app.MapPost("/api/projects/{projectName}/queue-health/repair", (
            string projectName,
            TaskScannerService scanner,
            ITaskAccess taskAccess) =>
        {
            var entry = scanner.GetWatchPaths().FirstOrDefault(e =>
                string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });

            var moved = new List<object>();
            var failed = new List<object>();
            // ADR-0024: enumerate orphan folders through the layer
            // (which knows the lane shape) rather than constructing the
            // lane paths here. Failed-pickup-elimination (supersedes
            // ADR-0028/0029, cause #9): a folder with no task.json is debris,
            // not a runnable task, so the repair archives it to 7-archive with
            // its evidence intact instead of parking a card in the retired
            // 3a-failed-pickup lane. Each repair routes through
            // ArchiveOrphanFolder so the move and the reason file land in one
            // typed call.
            foreach (var entryFolder in taskAccess.ListAllLaneFolders(entry.Path).Where(e => !e.HasJobJson))
            {
                var destinationSlug = BuildRepairSlug(entryFolder.Lane, entryFolder.Slug, entry.Path, taskAccess);
                var reason =
                    "# Queue health repair\n\n" +
                    $"Original folder: `{entryFolder.Lane}/{entryFolder.Slug}`\n\n" +
                    "This folder did not contain `task.json`, so it could not be governed by the normal job API. " +
                    "It is debris, not a runnable task. The queue-health repair action archived it here through the " +
                    "application state machine and preserved its files.\n";
                var outcome = taskAccess.ArchiveOrphanFolder(
                    entry.Path,
                    entryFolder.Lane,
                    entryFolder.Slug,
                    destinationSlug,
                    reason);
                if (outcome.Status == TaskMutationStatus.Applied)
                {
                    moved.Add(new { id = entryFolder.Slug, fromLane = entryFolder.Lane, destinationSlug });
                }
                else
                {
                    failed.Add(new { id = entryFolder.Slug, fromLane = entryFolder.Lane, status = outcome.Status.ToString(), outcome.Message });
                }
            }

            return Results.Ok(new
            {
                project = projectName,
                moved,
                failed,
                queueHealth = ReadQueueHealth(entry.Path, taskAccess)
            });
        });
    }

    private static string BuildRepairSlug(string lane, string slug, string watchPath, ITaskAccess taskAccess)
    {
        var safeLane = lane.Replace(Path.DirectorySeparatorChar, '-').Replace(Path.AltDirectorySeparatorChar, '-');
        var baseSlug = $"debris-{safeLane}-{slug}-{DateTime.UtcNow:yyyy-MM-dd}";
        var candidate = baseSlug;
        var i = 2;
        while (taskAccess.SlugExistsInLane(watchPath, TaskStates.Archive, candidate))
        {
            candidate = $"{baseSlug}-{i++}";
        }
        return candidate;
    }

    private static object ReadQueueHealth(string watchPath, ITaskAccess taskAccess)
    {
        var missingJobJson = new List<object>();
        var stateMismatches = new List<object>();
        var bySlug = new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase);

        // ADR-0024: enumerate every lane folder through ITaskAccess
        // instead of building each lane path locally. The entry shape
        // already carries hasJobJson and the parsed state field, so the
        // state-mismatch detection here drops a duplicate disk read.
        foreach (var entry in taskAccess.ListAllLaneFolders(watchPath))
        {
            var record = new
            {
                id = entry.Slug,
                lane = entry.Lane,
                hasJobJson = entry.HasJobJson,
                path = entry.FolderPath
            };

            if (!bySlug.TryGetValue(entry.Slug, out var locations))
            {
                locations = new List<object>();
                bySlug[entry.Slug] = locations;
            }
            locations.Add(record);

            if (!entry.HasJobJson)
            {
                missingJobJson.Add(record);
                continue;
            }

            if (!string.IsNullOrEmpty(entry.StateInJobJson)
                && !string.Equals(entry.StateInJobJson, entry.Lane, StringComparison.OrdinalIgnoreCase))
            {
                stateMismatches.Add(new { id = entry.Slug, lane = entry.Lane, state = entry.StateInJobJson, path = entry.FolderPath });
            }
        }

        var duplicates = bySlug
            .Where(kv => kv.Value.Count > 1)
            .Select(kv => new { id = kv.Key, locations = kv.Value })
            .OrderBy(x => x.id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var issueCount = missingJobJson.Count + stateMismatches.Count + duplicates.Count;
        var severity = issueCount == 0
            ? "ok"
            : missingJobJson.Count > 0 || duplicates.Count > 0
                ? "critical"
                : "warning";

        return new
        {
            severity,
            issueCount,
            missingJobJson,
            duplicates,
            stateMismatches
        };
    }

    private static List<object> ReadReviewDecisionsCached(string watchPath, string projectName, ITaskAccess taskAccess)
    {
        // Cache invalidation key was "lane mtime" against
        // Path.Combine(watchPath, AutoReview). Replaced by a slug-count
        // + max LastActivity proxy fetched via ITaskAccess; not as
        // precise as the directory mtime but covers add / remove and
        // log-write events because LastActivity is the max mtime across
        // all files in the job folder.
        var jobs = taskAccess.ListByLaneInWorkspace(watchPath, TaskStates.AutoReview);
        var laneSignature = jobs.Count == 0
            ? 0L
            : jobs.Aggregate(0L, (acc, j) => Math.Max(acc, j.LastActivity.Ticks));

        if (ReviewCache.TryGetValue(watchPath, out var cached))
        {
            if (cached.LaneMtimeTicks == laneSignature
                && DateTime.UtcNow - cached.CapturedAt < ReviewCacheTtl)
            {
                Interlocked.Increment(ref ReviewCacheHits);
                return cached.Items;
            }
        }
        Interlocked.Increment(ref ReviewCacheMisses);

        var items = new List<object>();
        foreach (var info in jobs)
        {
            var logPath = TaskPaths.CliOutputLog(info.FolderPath);
            if (!File.Exists(logPath)) continue;
            string body;
            try { body = File.ReadAllText(logPath); } catch { continue; }
            var needs = ReviewDecisionParsing.FindUnresolvedNeedsInput(body);
            if (needs == null) continue;
            items.Add(new { jobId = info.Id, title = info.Title, reason = needs.Reason });
        }

        ReviewCache[watchPath] = new ReviewCacheEntry(DateTime.UtcNow, laneSignature, items);
        return items;
    }
}
