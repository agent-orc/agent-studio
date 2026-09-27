using System.Diagnostics;

using static AgentStudio.Tasks.TaskEndpointHelpers;
namespace AgentStudio.Tasks;

/// <summary>
/// Job CRUD + state transitions: list, detail, create, delete, move,
/// reorder, change-project, plus the "set one job field" PUTs (model,
/// cli-type, title). These are the routes that read or rewrite the
/// canonical <c>task.json</c> on disk.
/// </summary>
public static class TaskCrudEndpoints
{
    public static void MapTaskCrudEndpoints(this RouteGroupBuilder group)
    {
        // AGT-2050: one lightweight read for every task reference on a rendered
        // document/chat turn. The scanner and merge reachability service are both
        // cached, and merge membership is calculated once for the whole requested
        // set, never once per key.
        group.MapPost("/reference-status", (TaskReferenceStatusRequest req,
            HttpContext context,
            TaskScannerService scanner,
            BoardMergeStatusService mergeStatus,
            AgentStudio.Registry.ProjectRegistry projects,
            ILoggerFactory loggerFactory) =>
        {
            var started = Stopwatch.GetTimestamp();
            var requested = (req.Keys ?? [])
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(k => k.Trim().ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(200)
                .ToArray();

            var registry = projects.List()
                .Where(project => context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is not HumanPrincipal human
                                  || ProjectAccessAuthorization.Allows(human.User, project.Id, projects))
                .ToList();
            var knownCodes = registry
                .Where(p => !string.IsNullOrWhiteSpace(p.ShortCode))
                .ToDictionary(p => p.ShortCode, StringComparer.OrdinalIgnoreCase);
            var jobs = ProjectAccessAuthorization.FilterTasks(
                context, scanner.ScanAllJobsWithArchive(), projects).ToList();
            var byKey = jobs
                .Where(j => !string.IsNullOrWhiteSpace(j.Key))
                .GroupBy(j => j.Key!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var matched = requested.Where(byKey.ContainsKey).Select(k => byKey[k]).ToArray();
            var merges = mergeStatus.BuildLookup(matched);

            var items = requested.Select(key =>
            {
                var dash = key.LastIndexOf('-');
                var code = dash > 0 ? key[..dash] : "";
                if (!knownCodes.TryGetValue(code, out var project)) return null;
                if (!byKey.TryGetValue(key, out var job))
                    return TaskReferenceStatusItem.Ghost(key, project.Id, project.DisplayName, project.Color);

                merges.TryGetValue(job.TaskKey, out var merge);
                var grade = job.Tags
                    .FirstOrDefault(t => t.StartsWith("code-review:grade-", StringComparison.OrdinalIgnoreCase))?
                    .Split('-').LastOrDefault()?.ToUpperInvariant();
                return new TaskReferenceStatusItem(
                    key, true, job.TaskKey, job.Title, job.State,
                    project.Id, project.DisplayName, project.Color, merge, grade);
            }).Where(x => x is not null).ToArray();

            loggerFactory.CreateLogger("TaskReferenceStatus")
                .LogInformation("task-reference-status-batch requested={Requested} returned={Returned} elapsedMs={ElapsedMs}",
                    requested.Length, items.Length, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return Results.Ok(new TaskReferenceStatusResponse(items!));
        });

        group.MapGet("/", (string? project, bool? includeFixtures, HttpContext ctx, TaskScannerService scanner, CliRouter router, TaskRunnerService runners, AttemptAuthorityService attemptAuthority, ITokenAggregator tokens, IConfiguration configuration, TaskListGitProjectionCache gitProjection, TaskLiveStatusProjection liveStatus, AgentStudio.Registry.ProjectRegistry projects, ProjectSettingsService projectSettings, BetterCandidateService betterCandidates, BoardReadSignatureSource boardSignature, ILoggerFactory loggerFactory) =>
        {
            using var gitTelemetry = GitProcessTelemetry.BeginRequest(
                "tasks/list",
                loggerFactory.CreateLogger("TaskListProjection"),
                includeNested: true);
            var projectRequested = !string.IsNullOrWhiteSpace(project);
            var projectWatchPath = ResolveWatchPath(projects, project, watchPath: null);
            if (projectRequested && string.IsNullOrWhiteSpace(projectWatchPath))
                return Results.NotFound(new { error = $"Unknown project '{project}'" });

            var raw = ProjectAccessAuthorization.FilterTasks(ctx, scanner.ScanAllJobs(), projects).ToList();
            if (projectRequested)
                raw = raw.Where(job => WatchPathComparison.PathsEqual(job.WatchPath, projectWatchPath)).ToList();
            if (includeFixtures != true) raw = raw.Where(j => !j.Fixture).ToList();
            var freshness = gitProjection.ReadFreshness(raw);
            ApplyGitStateHeaders(ctx, freshness);

            // AGT-2703: everything above is a cache read over an already
            // scanned task set. Everything below - the token / verdict /
            // dependency / Git / live-status lookups, the per-card overlay and
            // the serialisation of the result - only runs when the client does
            // not already hold this exact list. The query string is part of the
            // variant because it selects both the filter and the response shape.
            var etag = BoardReadValidator.FormatETag(boardSignature.Compose(
                "tasks/list",
                ctx.Request.QueryString.Value ?? string.Empty,
                raw,
                freshness,
                router,
                runners));
            if (BoardReadValidator.NotModified(ctx, etag) is { } notModified) return notModified;

            var tokenLookup = BuildTokenLookup(raw, tokens);
            var verdictLookup = BuildOrchestratorVerdictLookup(raw, configuration);
            var dependencyLookups = BuildDependencyGraphLookups(raw, scanner, attemptAuthority: attemptAuthority);
            var gitLookup = gitProjection.ReadCacheOnly(raw);
            var liveLookup = liveStatus.BuildLookup(raw);
            var jobs = raw.Select(job => betterCandidates.Attach(
                              WithRuntime(job, router, runners, tokenLookup, verdictLookup, dependencyLookups.WaitsOn, dependencyLookups.TransitiveWaiters),
                              projectSettings.Get(job.ProjectName)))
                          .WithLiveStatus(liveLookup)
                          .WithMergeSignal(gitLookup.Merge)
                          .WithIntegrationStatus(gitLookup.Integration)
                          .WithPublishSignal(gitLookup.Publish)
                          .WithTestRunEvidence(gitLookup.TestRuns)
                          .WithReviewProjection(gitLookup.ReviewProjection)
                          .ToList();
            if (TaskQueryRequest.FromQuery(ctx.Request.Query) is { IsActive: true } query)
            {
                // The endpoint already resolved the path-free project handle and
                // scoped the source set. Do not compare a registry id/short code
                // with TaskInfo.ProjectName inside the analysis engine.
                query = query with { Project = [] };
                var response = TaskQueryEngine.Execute(jobs, query);
                // A rejected query is not a representation of the board, so it
                // must not be published under the board's validator.
                if (response.Error is { Length: > 0 })
                    return Results.BadRequest(new { error = response.Error });
                return BoardReadValidator.Ok(ctx, etag, response);
            }
            return BoardReadValidator.Ok(ctx, etag, jobs);
        });

        group.MapGet("/grouped", (bool? includeFixtures, bool? includeLegacyReviewLane, HttpContext context, TaskScannerService scanner, CliRouter router, TaskRunnerService runners, AttemptAuthorityService attemptAuthority, ITokenAggregator tokens, IConfiguration configuration, ProjectSettingsService projectSettings, BetterCandidateService betterCandidates, TaskListGitProjectionCache gitProjection, TaskLiveStatusProjection liveStatus, AgentStudio.Registry.ProjectRegistry projects, BoardReadSignatureSource boardSignature, ILoggerFactory loggerFactory) =>
        {
            using var gitTelemetry = GitProcessTelemetry.BeginRequest(
                "tasks/grouped",
                loggerFactory.CreateLogger("TaskListProjection"),
                includeNested: true);
            var raw = ProjectAccessAuthorization.FilterTasks(context, scanner.ScanAllJobs(), projects).ToList();
            if (includeFixtures != true) raw = raw.Where(j => !j.Fixture).ToList();
            var gitFreshness = gitProjection.ReadFreshness(raw);
            ApplyGitStateHeaders(context, gitFreshness);

            // AGT-2703: the board poll is the single biggest response this API
            // produces (~1.9 MB measured), and on an unchanged board every byte
            // of it is a byte the client already has. Everything up to here is a
            // cache read over an already scanned task set; the enrichment
            // lookups, the per-lane sort and the serialisation below only run
            // once the validator says the client's copy is out of date.
            var legacyReviewLane = includeLegacyReviewLane != false;
            var etag = BoardReadValidator.FormatETag(boardSignature.Compose(
                "tasks/grouped",
                $"fixtures={includeFixtures == true}&legacyReviewLane={legacyReviewLane}",
                raw,
                gitFreshness,
                router,
                runners));
            if (BoardReadValidator.NotModified(context, etag) is { } notModified) return notModified;

            var tokenLookup = BuildTokenLookup(raw, tokens);
            var verdictLookup = BuildOrchestratorVerdictLookup(raw, configuration);
            var dependencyLookups = BuildDependencyGraphLookups(raw, scanner, attemptAuthority: attemptAuthority);
            var gitLookup = gitProjection.ReadCacheOnly(raw);
            var liveLookup = liveStatus.BuildLookup(raw);
            var jobs = raw.Select(job => betterCandidates.Attach(
                              WithRuntime(job, router, runners, tokenLookup, verdictLookup, dependencyLookups.WaitsOn, dependencyLookups.TransitiveWaiters),
                              projectSettings.Get(job.ProjectName)))
                          .WithLiveStatus(liveLookup)
                          .WithMergeSignal(gitLookup.Merge)
                          .WithIntegrationStatus(gitLookup.Integration)
                          .WithPublishSignal(gitLookup.Publish)
                          .WithTestRunEvidence(gitLookup.TestRuns)
                          .WithReviewProjection(gitLookup.ReviewProjection)
                          .ToList();
            // F35: each lane is sorted using a per-project strategy. The kanban
            // mixes projects inside one lane, so the sort groups by project,
            // applies that project's resolved strategy, then concatenates the
            // groups in alphabetical project order for a deterministic global
            // result. Settings are cached in-memory (single lock) so the
            // per-project lookup is essentially free.
            var settingsByProject = new Dictionary<string, ProjectSettings>(StringComparer.OrdinalIgnoreCase);
            ProjectSettings SettingsFor(string projectName)
            {
                if (!settingsByProject.TryGetValue(projectName, out var s))
                {
                    s = projectSettings.Get(projectName);
                    settingsByProject[projectName] = s;
                }
                return s;
            }
            List<TaskInfo> SortLane(string lane)
                => LaneSortApplier.Sort(jobs.Where(j => j.State == lane), lane, SettingsFor).ToList();

            // ADR-0025: explicit AutoReview + HumanReview lanes. The legacy
            // "Review" key is kept (auto-review only) so older clients that
            // only know the four pre-ADR-0025 lane names keep getting a
            // populated bucket and don't crash on a missing field.
            //
            // AGT-2703: that alias serialises the whole AutoReview lane a second
            // time, and on a review-heavy board it is a large share of the
            // response. Dropping it outright would break exactly the clients it
            // exists for, so it is opt-out instead: a client that knows the
            // ADR-0025 lane names declares so with
            // "?includeLegacyReviewLane=false" and stops paying for the
            // duplicate. Omitting the parameter keeps the pre-ADR-0025 contract
            // byte for byte.
            var autoReview = SortLane(TaskStates.AutoReview);
            var humanReview = SortLane(TaskStates.HumanReview);
            var escalated = SortLane(TaskStates.Escalated);
            var grouped = new
            {
                // Backlog: triage staging area, the leftmost lane and the
                // default landing for new jobs.
                Backlog = SortLane(TaskStates.Backlog),
                Preparation = SortLane(TaskStates.Preparation),
                // ADR-0026: orchestrator-prep lane (hide-when-empty). The
                // 1b-needs-human-review bounce lane has been retired.
                OrchestratorPrep = SortLane(TaskStates.OrchestratorPrep),
                Ready = SortLane(TaskStates.Ready),
                Progress = SortLane(TaskStates.Progress),
                // ADR-0028: 3a-failed-pickup is a hide-when-empty lane that
                // surfaces orphan boot-sweep verdicts the runner used to hide
                // in 7-archive. Empty by default; clients render it only when
                // it has at least one job.
                FailedPickup = SortLane(TaskStates.FailedPickup),
                // 3b-code-not-complete: hide-when-empty park lane for tasks that
                // exhausted their auto-pickup retry budget without reaching
                // review. Empty by default; clients render it only when populated.
                CodeNotComplete = SortLane(TaskStates.CodeNotComplete),
                AutoReview = autoReview,
                HumanReview = humanReview,
                Escalated = escalated,
                // Kept as an always-present key (same reasoning as Archive
                // below) so an opted-out client still parses the payload; it
                // just stops receiving the second copy of the lane.
                Review = legacyReviewLane ? (IReadOnlyList<TaskInfo>)autoReview : Array.Empty<TaskInfo>(),
                Completed = SortLane(TaskStates.Completed),
                // ASS-1727: the board response intentionally keeps Archive
                // empty. ScanAllJobs() (cache-backed) already excludes the
                // terminal 7-archive lane, so there is nothing to filter or
                // sort here even though hundreds of archived folders exist on
                // disk. Emit an explicit empty array instead of re-running
                // SortLane over a lane the scan guarantees is absent: that
                // call only ever produced [] and reading as if it built the
                // archive lane was misleading. Eager-loading those cards
                // bloated every poll; the Archive view pages through the
                // dedicated GET /api/tasks/archive endpoint instead. The key is
                // kept (always []) so pre-existing clients that read
                // grouped.archive don't NPE on a missing field.
                Archive = Array.Empty<TaskInfo>(),
                // AGT-2726: the board-wide Git derived state (merge/integration/
                // publish/test-run signals folded into the cards above) is a
                // background-index snapshot, not something this request
                // computed. GitStateAt is the oldest index timestamp among the
                // repositories represented on the board; Stale is true while at
                // least one of them has never been indexed yet or is mid-refresh.
                GitStateAt = gitFreshness.GitStateAt,
                Stale = gitFreshness.Stale,
            };
            return BoardReadValidator.Ok(context, etag, grouped);
        });

        // ASS-1727: dedicated paged read for the terminal 7-archive lane. The
        // board /grouped response keeps Archive empty (hundreds of terminal
        // cards would bloat every poll), so the Archive view lazy-loads through
        // here. Slim by construction: it reuses the slim-hydrated archive
        // partition the index cache already built from its single shared disk
        // walk (no per-request full scan) and projects only the fields an
        // archived card renders. Query: watchPath (optional project filter),
        // offset/limit (paging), search (case-insensitive title/key/id), and
        // includeFixtures (default false, mirroring the board endpoints).
        group.MapGet("/archive", (string? project, string? watchPath, int? offset, int? limit, string? search, bool? includeFixtures, HttpContext context,
            TaskScannerService scanner, AgentStudio.Registry.ProjectRegistry projects, ILoggerFactory loggerFactory) =>
        {
            var projectRequested = !string.IsNullOrWhiteSpace(project);
            watchPath = ResolveWatchPath(projects, project, watchPath);
            // A project-scoped archive must never degrade to the workspace-wide
            // archive when a UI sends an unknown/stale handle. That leak made a
            // Token Economy tab display Agent Studio's 1,000+ archived cards.
            if (projectRequested && string.IsNullOrWhiteSpace(watchPath))
                return Results.NotFound(new { error = $"Unknown project '{project}'" });
            var logger = loggerFactory.CreateLogger("TaskArchiveEndpoint");
            var sw = Stopwatch.StartNew();
            var all = ProjectAccessAuthorization.FilterTasks(context, scanner.ScanArchivedJobs(), projects).ToList();
            IEnumerable<TaskInfo> archived = all;

            if (!string.IsNullOrWhiteSpace(watchPath))
                archived = archived.Where(j => WatchPathComparison.PathsEqual(j.WatchPath, watchPath));
            if (includeFixtures != true)
                archived = archived.Where(j => !j.Fixture);

            var term = search?.Trim();
            var hasSearch = !string.IsNullOrWhiteSpace(term);
            if (!string.IsNullOrWhiteSpace(term))
            {
                archived = archived.Where(j =>
                    j.Title.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || (j.Key?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                    || j.Id.Contains(term, StringComparison.OrdinalIgnoreCase));
            }

            // Newest-archived first. A terminal card has no live freshness
            // signal, so EnteredLaneAt (when it entered 7-archive) is the
            // natural ordering, with LastActivity as a stable tiebreaker.
            var ordered = archived
                .OrderByDescending(j => j.EnteredLaneAt)
                .ThenByDescending(j => j.LastActivity)
                .ToList();

            var total = ordered.Count;
            var off = Math.Max(0, offset ?? 0);
            var lim = Math.Clamp(limit ?? 50, 1, 200);
            var items = ordered.Skip(off).Take(lim).Select(ArchivedTaskInfo.From).ToList();

            sw.Stop();
            // A typed Archive filter is a user-visible query path, so it gets
            // its own stable event. Without this a "the filter found nothing"
            // report is undiagnosable from the api log - you can't tell an
            // empty archive from a term that simply matched no card. Logging
            // matched-vs-scanned makes both cases obvious post-hoc.
            if (hasSearch)
            {
                logger.LogInformation(
                    "task-archive-search term={SearchTerm} matched={Matched} of {ArchivedScanned} archived (watchPath={HasWatchPath})",
                    term, total, all.Count, !string.IsNullOrWhiteSpace(watchPath));
            }
            logger.LogInformation(
                "GET /api/tasks/archive returned {Returned}/{Total} archived tasks (offset={Offset}, limit={Limit}, search={HasSearch}) in {ElapsedMs}ms",
                items.Count, total, off, lim, hasSearch, sw.ElapsedMilliseconds);

            return Results.Ok(new ArchivedTasksResponse
            {
                Items = items,
                Total = total,
                Offset = off,
                Limit = lim,
            });
        });

        group.MapGet("/{jobId}", (string jobId, string? project, string? watchPath, HttpContext context, TaskScannerService scanner, AgentStudio.Registry.ProjectRegistry projects, CliRouter router, TaskRunnerService runners, AttemptAuthorityService attemptAuthority, ITokenAggregator tokens, IConfiguration configuration, GitService git, TaskSessionLog sessions, BoardMergeStatusService mergeStatus, TaskIntegrationStatusService integrationStatus, TaskPublishableService publishStatus, TestRunService testRuns, AgentStudio.Review.ReviewProjectionService reviewProjection, TaskLiveStatusProjection liveStatus, ProjectSettingsService projectSettings, BetterCandidateService betterCandidates) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var detail = scanner.GetJobDetail(jobId, watchPath);
            if (detail is null) return Results.NotFound();
            // ASS-1712: an in-progress per-task-worktree task's persisted commits[]
            // chain collapses to empty/singular (per-run ranges track the shared
            // develop HEAD; attribution only stamps once it leaves 3-progress).
            // Fold the reconstructed task-branch history into TaskInfo.Commits so
            // the git-pane chain + header badge show the full history, not one
            // commit. No-op for any other lane / an already-populated chain.
            detail = JobCommitsAggregation.WithReconstructedInProgressCommits(detail, sessions, watchPath, git);
            var tokenLookup = BuildTokenLookup(new[] { detail.Info }, tokens);
            var verdictLookup = BuildOrchestratorVerdictLookup(new[] { detail.Info }, configuration);
            var eligibleWaiters = ProjectAccessAuthorization
                .FilterTasks(context, scanner.ScanAllJobs(), projects)
                .Where(job => !job.Fixture);
            var dependencyLookups = BuildDependencyGraphLookups(new[] { detail.Info }, scanner, eligibleWaiters, attemptAuthority);
            var withRuntime = WithRuntime(detail, router, runners, tokenLookup, verdictLookup, dependencyLookups.WaitsOn, dependencyLookups.TransitiveWaiters);
            withRuntime = withRuntime with
            {
                Info = betterCandidates.Attach(withRuntime.Info, projectSettings.Get(withRuntime.Info.ProjectName)),
            };
            var liveLookup = liveStatus.BuildLookup(new[] { withRuntime.Info });
            if (liveLookup.TryGetValue(withRuntime.Info.TaskKey, out var currentLiveStatus))
                withRuntime = withRuntime with { Info = withRuntime.Info with { LiveStatus = currentLiveStatus } };
            // AGT-2046: fold the batched merge signal onto the detail's info too, so
            // a card opened from the board keeps the same [develop|main] indicator.
            var mergeLookup = mergeStatus.BuildLookup(new[] { withRuntime.Info });
            if (mergeLookup.TryGetValue(withRuntime.Info.TaskKey, out var signal))
                withRuntime = withRuntime with { Info = withRuntime.Info with { MergeSignal = signal } };
            // AGT-2202: fold the integration verdict so a completed/archived card
            // opened from the board keeps the same "integrated / not integrated"
            // badge as its board card.
            var integrationLookup = integrationStatus.BuildLookup(new[] { withRuntime.Info });
            if (integrationLookup.TryGetValue(withRuntime.Info.TaskKey, out var integration))
                withRuntime = withRuntime with { Info = withRuntime.Info with { Integration = integration } };
            // PUB-1: fold the per-task publish chip signal so a completed card opened
            // from the board shows "publishable: npm, website" in its detail too.
            var publishLookup = publishStatus.BuildLookup(new[] { withRuntime.Info });
            if (publishLookup.TryGetValue(withRuntime.Info.TaskKey, out var publishSignal))
                withRuntime = withRuntime with { Info = withRuntime.Info with { PublishSignal = publishSignal } };
            var testRunLookup = testRuns.BuildLookup(new[] { withRuntime.Info });
            if (testRunLookup.TryGetValue(withRuntime.Info.TaskKey, out var testEvidence))
                withRuntime = withRuntime with { Info = withRuntime.Info with { TestEvidence = testEvidence } };
            // AGT-2717: fold the canonical review projection so the escalation
            // banner, Evidence tab, Result header, and board chip all read the
            // same rounds/outcome/blocking-aspect facts from one card open.
            withRuntime = withRuntime with { Info = withRuntime.Info with { ReviewProjection = reviewProjection.Read(withRuntime.Info) } };
            return Results.Ok(withRuntime);
        });

        // Promote a finished planning task to a pre-filled coding task. Returns
        // a fully-populated draft (title + prompt body from the report's
        // `## Proposed task prompt` section, copyable image references,
        // mode=coding, state=1-preparation) that the frontend feeds into the
        // existing create-task modal. The modal stays the single source of
        // truth for create UX; this endpoint only reads. See
        // docs/concepts/planning-research-task-kinds-2026-05.md.
        group.MapGet("/{jobId}/promote-to-coding", (string jobId, string? project, string? watchPath, TaskScannerService scanner, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var info = scanner.FindJob(jobId, watchPath);
            if (info is null) return Results.NotFound();
            if (TaskModes.Normalize(info.Mode) != TaskModes.Planning)
                return Results.BadRequest(new { error = "Only planning tasks can be promoted to a coding task." });

            var plan = scanner.BuildPromoteToCodingPlan(jobId, watchPath);
            if (plan is null) return Results.NotFound();

            var watchPathQuery = string.IsNullOrEmpty(plan.WatchPath)
                ? ""
                : $"?watchPath={Uri.EscapeDataString(plan.WatchPath)}";
            var attachments = plan.Attachments
                .Select(a => a with
                {
                    Url = $"/api/tasks/{Uri.EscapeDataString(jobId)}/{a.Source}/{Uri.EscapeDataString(a.FileName)}{watchPathQuery}"
                })
                .ToList();

            return Results.Ok(plan with { Attachments = attachments });
        });

        // Concept promotion is an explicit sight-review action. It reads only
        // validated implementation proposals from the published Dossier,
        // creates coding cards in preparation, and completes the source concept
        // card after recording the human gate.
        group.MapGet("/{jobId}/promote-concept", (string jobId, string? project, string? watchPath,
            TaskScannerService scanner,
            AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var info = scanner.FindJob(jobId, watchPath);
            if (info is null) return Results.NotFound();
            if (!TaskModes.IsConcept(info.Mode))
                return Results.BadRequest(new { error = "Only concept tasks have implementation-card proposals." });

            var plan = scanner.BuildPromoteConceptPlan(jobId, watchPath);
            return plan is null
                ? Results.Conflict(new { error = "The concept Dossier is missing or did not pass concept review." })
                : Results.Ok(plan);
        });

        group.MapPost("/{jobId}/promote-concept", async (
            string jobId,
            string? project,
            string? watchPath,
            PromoteConceptRequest request,
            TaskScannerService scanner,
            AgentStudio.Registry.ProjectRegistry projects,
            AgentStudio.Pipeline.ConceptPromotionService promotion,
            TaskTransitionService transitions,
            AgentStudio.Pipeline.PipelineExecutionLog pipelineLog,
            CancellationToken ct) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var info = scanner.FindJob(jobId, watchPath);
            if (info is null) return Results.NotFound();
            if (!TaskModes.IsConcept(info.Mode))
                return Results.BadRequest(new { error = "Only concept tasks can be promoted." });
            if (info.State is not (TaskStates.HumanReview or TaskStates.Completed))
                return Results.Conflict(new { error = "Complete sight review before promoting implementation cards." });

            var plan = scanner.BuildPromoteConceptPlan(jobId, watchPath);
            if (plan is null)
                return Results.Conflict(new { error = "The concept Dossier is missing or did not pass concept review." });

            PromoteConceptTasksResponse result;
            try
            {
                result = promotion.Promote(info, plan, request);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }

            if (info.State == TaskStates.HumanReview)
            {
                var now = DateTime.UtcNow;
                pipelineLog.RecordStep(info.FolderPath, new PipelineStepExecution
                {
                    StepId = AgentStudio.Pipeline.PipelineCatalogue.ConceptSightReviewGateStepId,
                    Kind = StepKind.Orchestrator,
                    Status = PipelineStepStatus.Passed,
                    StartedAt = now,
                    CompletedAt = now,
                    Verdict = "sight-review-approved",
                    VerdictSummary = "Human sight review approved the concept.",
                });
                pipelineLog.RecordStep(info.FolderPath, new PipelineStepExecution
                {
                    StepId = AgentStudio.Pipeline.PipelineCatalogue.ConceptPromotionStepId,
                    Kind = StepKind.Tool,
                    Status = PipelineStepStatus.Passed,
                    StartedAt = now,
                    CompletedAt = now,
                    Verdict = result.Created.Count == 0 ? "no-implementation" : "implementation-cards-created",
                    VerdictSummary = result.Created.Count == 0
                        ? "Sight review completed without implementation cards."
                        : $"Created {result.Created.Count} implementation card(s) from the Dossier.",
                });
                pipelineLog.Complete(info.FolderPath, now);
                SteerPendingMarker.Clear(info.FolderPath);

                var move = await transitions.MoveAsync(
                    jobId,
                    TaskStates.Completed,
                    watchPath,
                    ct,
                    cause: "concept-sight-review-approved",
                    reason: "Concept sight review approved and implementation promotion completed.",
                    transitionCause: LaneChangeCauses.Accepted,
                    transitionDetail: "concept-sight-review");
                if (move.Status != MoveJobStatus.Success)
                    return Results.Conflict(new
                    {
                        error = move.Message ?? "Implementation cards were created, but the concept card could not be completed.",
                        result,
                    });
            }

            return Results.Ok(result);
        });

        // AGT-2069 — declare (or clear) "bewusst keine Umsetzung" (deliberately
        // no follow-up) for a planning task. This is the escape hatch that lets a
        // planning task satisfy the spawn-contract completion gate without
        // producing follow-up cards, by an explicit operator call rather than a
        // silent slip past the AGT-1915 trap. Writes the app-owned
        // .metadata/planning-closure.json sidecar (never task.json) and returns
        // the recomputed spawn summary so the UI updates without a re-fetch.
        group.MapPost("/{jobId}/planning-closure", (string jobId, string? project, string? watchPath,
            SetPlanningClosureRequest req,
            TaskScannerService scanner,
            AgentStudio.Registry.ProjectRegistry projects,
            ILoggerFactory loggerFactory) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var info = scanner.FindJob(jobId, watchPath);
            if (info is null) return Results.NotFound();
            if (!PlanningCompletionGate.Applies(info.Mode))
                return Results.BadRequest(new { error = "Only planning tasks carry a follow-up declaration." });
            if (string.IsNullOrWhiteSpace(info.FolderPath))
                return Results.BadRequest(new { error = "Task folder is unavailable." });

            var logger = loggerFactory.CreateLogger("PlanningClosure");
            var ok = PlanningClosureStore.Write(
                info.FolderPath, req?.Declared ?? false, req?.Reason, req?.DeclaredBy, logger);
            if (!ok)
                return Results.Json(new { error = "Failed to persist the planning declaration." },
                    statusCode: StatusCodes.Status500InternalServerError);

            logger.LogInformation(
                "planning-closure {Action} for {Project}/{Key} (declared={Declared})",
                (req?.Declared ?? false) ? "declared" : "cleared",
                info.ProjectName, info.Key ?? info.Id, req?.Declared ?? false);

            return Results.Ok(BuildPlanningSpawnSummary(info) ?? new PlanningSpawnSummary());
        });

        // Interim dossier correction surface for concept cards. The path is
        // written into both task result documents because those files are the
        // only reference carrier until references.workbenches lands. A
        // deliberate no-dossier outcome is kept in a small app-owned sidecar.
        group.MapPost("/{jobId}/concept-dossier", (string jobId, string? project, string? watchPath,
            SetConceptDossierRequest req,
            TaskScannerService scanner,
            AgentStudio.Registry.ProjectRegistry projects,
            ILoggerFactory loggerFactory) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var info = scanner.FindJob(jobId, watchPath);
            if (info is null) return Results.NotFound();
            if (!TaskModes.IsConcept(info.Mode))
                return Results.BadRequest(new { error = "Only concept tasks carry a dossier decision." });

            var logger = loggerFactory.CreateLogger("ConceptDossier");
            if (req.NoDossierNeeded)
            {
                if (!string.IsNullOrWhiteSpace(req.Path))
                    return Results.BadRequest(new { error = "Choose either a dossier path or no dossier needed." });
                if (string.IsNullOrWhiteSpace(req.Reason))
                    return Results.BadRequest(new { error = "Explain why no dossier is needed." });
                if (!ConceptDossierClosureStore.Write(info.FolderPath, req.Reason, logger))
                {
                    return Results.Json(
                        new { error = "Failed to persist the no-dossier explanation." },
                        statusCode: StatusCodes.Status500InternalServerError);
                }
            }
            else
            {
                var path = ConceptDossierContract.NormalizePath(req.Path);
                if (!ConceptDossierContract.IsDossierPath(path))
                    return Results.BadRequest(new { error = "Use a repository-relative docs/.../index.html dossier path." });

                var context = ResolveProjectContext(projects, scanner, info);
                var repositoryRoot = context?.Entry.RepositoryPath;
                if (string.IsNullOrWhiteSpace(repositoryRoot)) repositoryRoot = context?.Entry.RootPath;
                if (string.IsNullOrWhiteSpace(repositoryRoot))
                    return Results.BadRequest(new { error = "The project repository is unavailable." });

                var root = Path.GetFullPath(repositoryRoot).TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
                var dossierFile = Path.GetFullPath(Path.Combine(
                    root,
                    path!.Replace('/', Path.DirectorySeparatorChar)));
                var pathComparison = OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;
                if (!dossierFile.StartsWith(root + Path.DirectorySeparatorChar, pathComparison)
                    || !File.Exists(dossierFile))
                {
                    return Results.BadRequest(new { error = $"Dossier file does not exist in the project repository: {path}" });
                }

                try
                {
                    ConceptDossierContract.WriteReference(info.FolderPath, path);
                    ConceptDossierClosureStore.Clear(info.FolderPath, logger);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not write dossier references for {Project}/{Task}", info.ProjectName, info.Key ?? info.Id);
                    return Results.Json(
                        new { error = "Failed to write the dossier path into the task result documents." },
                        statusCode: StatusCodes.Status500InternalServerError);
                }
            }

            var summary = ConceptDossierContract.Read(info.FolderPath);
            logger.LogInformation(
                "concept-dossier updated for {Project}/{Task}: path={Path} noDossierNeeded={NoDossierNeeded}",
                info.ProjectName,
                info.Key ?? info.Id,
                summary.RepoRelativePath ?? string.Empty,
                summary.NoDossierNeeded);
            return Results.Ok(summary);
        });

        group.MapPut("/{jobId}/state", async (string jobId, string? project, string? watchPath, MoveJobRequest req,
            HttpContext ctx,
            TaskTransitionService transitions,
            AgentStudio.Registry.ProjectRegistry projects,
            CancellationToken ct) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var validation = ValidateTargetState(req.TargetState);
            if (validation != null) return validation;
            if (req.OperatorOverride && req.TargetState != TaskStates.Completed)
                return Results.BadRequest(new { error = "operatorOverride is valid only for a move to 6-completed." });
            // AGT-2817: an override is a claim the card will carry and show.
            // It needs a written reason, checked here at the boundary.
            if (req.OperatorOverride && !CompletionContractPolicy.IsUsableReason(req.Reason?.Trim()))
                return Results.BadRequest(new { error = OverrideReasonRequired });
            if (req.ArchiveOverride && req.TargetState != TaskStates.Archive)
                return Results.BadRequest(new { error = "archiveOverride is valid only for a move to 7-archive." });
            if (req.ArchiveOverride && (ctx.Items[AccessSecurityMiddleware.HumanPrincipalItem] is not HumanPrincipal owner
                || owner.User.Role != StudioRoles.Owner))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (req.ArchiveOverride && !CompletionContractPolicy.IsUsableReason(req.Reason?.Trim()))
                return Results.BadRequest(new { error = OverrideReasonRequired });

            // T2b: these two routes are the operator-initiated move (board drag /
            // detail-view lane button), so the lane-change ledger trigger is the
            // human. Auto paths (runner pickup, orchestrator, sweeps) reach
            // MoveJob without a cause and are recorded as system.
            return MoveResult(await transitions.MoveAsync(
                jobId, req.TargetState, watchPath, ct, req.TargetIndex,
                cause: OperatorActor(ctx), reason: req.Reason,
                operatorOverride: req.OperatorOverride,
                archiveOverride: req.ArchiveOverride));
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start);

        group.MapPost("/{jobId}/move", async (string jobId, string? project, string? watchPath, MoveJobRequest req,
            HttpContext ctx,
            TaskTransitionService transitions,
            AgentStudio.Registry.ProjectRegistry projects,
            CancellationToken ct) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var validation = ValidateTargetState(req.TargetState);
            if (validation != null) return validation;
            if (req.OperatorOverride && req.TargetState != TaskStates.Completed)
                return Results.BadRequest(new { error = "operatorOverride is valid only for a move to 6-completed." });
            // AGT-2817: an override is a claim the card will carry and show.
            // It needs a written reason, checked here at the boundary.
            if (req.OperatorOverride && !CompletionContractPolicy.IsUsableReason(req.Reason?.Trim()))
                return Results.BadRequest(new { error = OverrideReasonRequired });
            if (req.ArchiveOverride && req.TargetState != TaskStates.Archive)
                return Results.BadRequest(new { error = "archiveOverride is valid only for a move to 7-archive." });
            if (req.ArchiveOverride && (ctx.Items[AccessSecurityMiddleware.HumanPrincipalItem] is not HumanPrincipal owner
                || owner.User.Role != StudioRoles.Owner))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (req.ArchiveOverride && !CompletionContractPolicy.IsUsableReason(req.Reason?.Trim()))
                return Results.BadRequest(new { error = OverrideReasonRequired });

            return MoveResult(await transitions.MoveAsync(
                jobId, req.TargetState, watchPath, ct, req.TargetIndex,
                cause: OperatorActor(ctx), reason: req.Reason,
                operatorOverride: req.OperatorOverride,
                archiveOverride: req.ArchiveOverride));
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start);

        // Lift a folder out of 3a-failed-pickup back into 2-ready and
        // rename it to drop the -pickup-failed-<utc> suffix. Closes the
        // gap that previously forced operators to fall back to `mv` +
        // manual rename - exactly the bypass the architecture test +
        // AGENTS.md "API first" rule are meant to stop. The state-machine
        // owns the move + slug rewrite atomically; the endpoint logs a
        // forensics row to <workspace>/logs/pickup-failures.jsonl so the
        // dead-letter -> restore lifecycle is reviewable per-slug.
        group.MapPost("/{jobId}/restore-from-failed-pickup",
            (string jobId, string? project, string? watchPath, RestoreFromFailedPickupRequest? req,
                TaskScannerService scanner,
                TaskStateMachine states,
                AgentStudio.Registry.ProjectRegistry projects,
                PickupFailureLog pickupFailures) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var keepDeadLetterSlug = req?.KeepDeadLetterSlug ?? false;

            // Capture the project name before the move so the forensics row
            // can attribute the restore even if a post-move FindJob race
            // returns null between the move and the scanner cache refresh.
            var preMove = scanner.FindJob(jobId, watchPath);
            var projectName = preMove?.ProjectName ?? "";

            var outcome = states.RestoreFromFailedPickup(jobId, watchPath, keepDeadLetterSlug);

            switch (outcome.Status)
            {
                case RestoreFromFailedPickupStatus.Success:
                    pickupFailures.AppendRestore(new PickupRestoreRecord
                    {
                        At = DateTime.UtcNow,
                        Kind = PickupFailureKinds.PickupRestored,
                        ProjectName = projectName,
                        Slug = outcome.OriginalSlug ?? "",
                        SourceSlug = outcome.SourceSlug ?? jobId,
                        RestoredAs = outcome.RestoredSlug ?? "",
                        TargetState = TaskStates.Ready,
                        Reason = keepDeadLetterSlug
                            ? "Operator restore via API; dead-letter slug suffix preserved."
                            : "Operator restore via API; original slug recovered."
                    });
                    return Results.Ok(new
                    {
                        status = "restored",
                        restoredSlug = outcome.RestoredSlug,
                        originalSlug = outcome.OriginalSlug,
                        sourceSlug = outcome.SourceSlug,
                        targetState = TaskStates.Ready
                    });
                case RestoreFromFailedPickupStatus.NoOp:
                    return Results.Ok(new
                    {
                        status = "no-op",
                        restoredSlug = outcome.RestoredSlug,
                        originalSlug = outcome.OriginalSlug,
                        sourceSlug = outcome.SourceSlug,
                        message = outcome.Message
                    });
                case RestoreFromFailedPickupStatus.NotFound:
                    return Results.NotFound(new { error = outcome.Message ?? "Folder not found in 3a-failed-pickup." });
                case RestoreFromFailedPickupStatus.TargetFolderExists:
                    return Results.Conflict(new { error = outcome.Message });
                case RestoreFromFailedPickupStatus.InvalidSlug:
                    return Results.BadRequest(new { error = outcome.Message });
                default:
                    return Results.Problem(outcome.Message ?? "Restore failed");
            }
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start);

        group.MapDelete("/orphan-folder", ([Microsoft.AspNetCore.Mvc.FromBody] OrphanFolderDeleteRequest req, TaskStateMachine states) =>
        {
            var outcome = states.DeleteOrphanFolder(req.WatchPath ?? "", req.Lane ?? "", req.Folder ?? "");
            return outcome.Status switch
            {
                OrphanFolderDeleteStatus.Success => Results.Ok(new { status = "deleted" }),
                OrphanFolderDeleteStatus.InvalidRequest => Results.BadRequest(new { error = outcome.Message }),
                OrphanFolderDeleteStatus.NonTerminalLane => Results.BadRequest(new { error = outcome.Message }),
                OrphanFolderDeleteStatus.HasJobJson => Results.Conflict(new { error = outcome.Message }),
                OrphanFolderDeleteStatus.NotFound => Results.NotFound(new { error = outcome.Message }),
                _ => Results.Problem(outcome.Message ?? "Orphan folder deletion failed")
            };
        });

        group.MapDelete("/{jobId}", (string jobId, string? project, string? watchPath, TaskStateMachine states, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var success = states.DeleteJob(jobId, watchPath);
            return success ? Results.Ok() : Results.NotFound();
        });

        group.MapPost("/", async (
            CreateTaskRequest req,
            HttpContext ctx,
            TaskMutationService mutations,
            AgentStudio.Registry.ProjectRegistry projects,
            AgentStudio.Registry.ComponentRoutingService routing,
            ModelRoutingPolicyRegistry modelRouting,
            IModelRoutingModeProvider routingMode,
            CliRouter cliRouter,
            IConfiguration configuration,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Title))
                return Results.BadRequest("Title is required");
            if (configuration.GetValue("DeliveryChain:Guarded", true)
                && AcceptanceIntegrationPolicy.IsIntegrationRequiredAtCreation(req)
                && req.TargetState is (TaskStates.HumanReview or TaskStates.Completed or TaskStates.Archive))
                return Results.BadRequest(new { error = "Create the card in an earlier lane, then complete its integration and review before entering a protected lane." });

            // A networked human principal is the initiating identity. The
            // attribution header must never override the authenticated user.
            if (ctx.Items[AccessSecurityMiddleware.HumanPrincipalItem] is HumanPrincipal human)
            {
                var requestedProject = string.IsNullOrWhiteSpace(req.Project) ? req.WatchPath : req.Project;
                if (!ProjectAccessAuthorization.Allows(human.User, requestedProject, projects))
                    return Results.Json(new { error = "project-scope-denied", message = "This account is not a member of the requested project." }, statusCode: StatusCodes.Status403Forbidden);
                req = req with { OwnerClientId = human.User.Id };
            }
            else if (string.IsNullOrWhiteSpace(req.OwnerClientId))
            {
                var headerOwner = ctx.Request.Headers["X-Client-Id"].FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(headerOwner))
                {
                    req = req with { OwnerClientId = headerOwner };
                }
            }


            AgentStudio.Registry.ComponentRoutingResolution? resolvedRouting = null;
            if (req.Routing != null)
            {
                var routingRequest = req.Routing with
                {
                    NavigationProjectId = string.IsNullOrWhiteSpace(req.Routing.NavigationProjectId)
                        ? req.Project
                        : req.Routing.NavigationProjectId,
                };
                resolvedRouting = routing.Resolve(routingRequest);
                if (resolvedRouting.RequiresQuestion || resolvedRouting.PrimaryProject == null)
                {
                    return Results.Conflict(new
                    {
                        error = "Task ownership must be resolved before creation.",
                        routing = resolvedRouting,
                    });
                }
                if (!string.IsNullOrWhiteSpace(req.RequestedTaskPrefix)
                    && !string.Equals(req.RequestedTaskPrefix, resolvedRouting.AllowedTicketPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return Results.BadRequest(new
                    {
                        error = $"Task prefix '{req.RequestedTaskPrefix}' is not valid for destination project {resolvedRouting.StorageProjectId}; expected '{resolvedRouting.AllowedTicketPrefix}'.",
                        routing = resolvedRouting,
                    });
                }

                var prompt = AppendDeliveryAcceptanceCriteria(req.PromptMarkdown, resolvedRouting);
                req = req with
                {
                    Project = resolvedRouting.StorageProjectId,
                    WatchPath = "",
                    PromptMarkdown = prompt,
                    RequestedTaskPrefix = resolvedRouting.AllowedTicketPrefix,
                };
            }

            var modelExplicit = req.ModelExplicit ?? !string.IsNullOrWhiteSpace(req.Model);
            var thinkingExplicit = req.ThinkingLevelExplicit ?? !string.IsNullOrWhiteSpace(req.ThinkingLevel);
            if ((!modelExplicit || !thinkingExplicit)
                && !string.IsNullOrWhiteSpace(req.CliType)
                && CliTypes.IsValid(req.CliType))
            {
                try
                {
                    var catalogue = await cliRouter.Get(req.CliType).GetModelCatalogAsync(false, ct);
                    var recommendation = modelRouting.Recommend(
                        req.TaskType,
                        catalogue,
                        routingMode.EconomyMode,
                        req.Title,
                        req.PromptMarkdown);
                    var effectiveModel = modelExplicit ? req.Model : recommendation.Model;
                    var effectiveThinking = thinkingExplicit
                        ? req.ThinkingLevel
                        : string.Equals(effectiveModel, recommendation.Model, StringComparison.OrdinalIgnoreCase)
                            ? recommendation.ThinkingLevel
                            : ModelMetadataRegistry.ResolveThinkingLevel(
                                req.CliType,
                                effectiveModel,
                                recommendation.ThinkingLevel);
                    req = req with
                    {
                        Model = effectiveModel,
                        ThinkingLevel = effectiveThinking,
                        ModelExplicit = modelExplicit,
                        ThinkingLevelExplicit = thinkingExplicit,
                    };
                }
                catch (Exception ex)
                {
                    // Creation stays available when a live CLI catalogue is
                    // temporarily unavailable. The pre-run qualification step
                    // retries the same policy before execution.
                    SilentCatch.Note(ex, "TaskCrudEndpoints: create-time model policy preview unavailable");
                }
            }

            var jobId = mutations.CreateJob(req);
            return jobId is null
                ? Results.Conflict("Job already exists or invalid input")
                : Results.Ok(new { id = jobId, routing = resolvedRouting });
        });

        group.MapPost("/reorder", (ReorderRequest req, HttpContext ctx, TaskStateMachine states,
            TaskScannerService scanner, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            var jobs = req.Jobs.Count > 0
                ? req.Jobs
                : req.JobIds.Select(id => new TaskOrderItem { JobId = id }).ToList();

            // reorder is body-addressed, so the networked middleware defers
            // project-scope enforcement to here. A scoped non-owner human may only
            // reorder tasks inside its own projects; resolve every affected task's
            // project and fail closed on any that is out of scope or unresolvable.
            if (!ProjectAccessAuthorization.AllowsTasks(
                    ctx,
                    jobs.Select(j => scanner.FindJob(j.JobId, string.IsNullOrWhiteSpace(j.WatchPath) ? null : j.WatchPath)?.ProjectName),
                    projects))
                return Results.Json(
                    new { error = "project-scope-denied", message = "This account is not a member of every task in the reorder set." },
                    statusCode: StatusCodes.Status403Forbidden);

            var success = states.ReorderJobs(jobs);
            return success ? Results.Ok() : Results.BadRequest("Reorder failed");
        });

        // "Do Next" from the detail view: surface TaskStateMachine.PromoteToReadyTop
        // so the user can push a queued task to the head of the project's ready
        // queue with one click. The state machine handles the reorder atomically
        // on disk and preserves the relative position of any other queued
        // jobs that already carry a PendingIntent.
        group.MapPost("/{jobId}/move-to-top", (string jobId, string? project, string? watchPath, HttpContext ctx, TaskStateMachine states, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            // Operator-initiated like the two move routes: the human actor lets
            // the ledger derive the operator cause of a lane change into Ready.
            var position = states.PromoteToReadyTop(jobId, watchPath, cause: OperatorActor(ctx));
            return position == 0 ? Results.NotFound() : Results.Ok(new { position });
        });

        group.MapPost("/{jobId}/change-project", (string jobId, string? project, string? watchPath, ChangeProjectRequest req, TaskStateMachine states, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            // The target project is likewise addressable by a path-free handle;
            // resolve it so a caller can move a task with {"targetProject":"ASS"}.
            var targetWatchPath = ResolveWatchPath(projects, req.TargetProject, req.TargetWatchPath);
            if (string.IsNullOrWhiteSpace(targetWatchPath))
                return Results.BadRequest("A valid target project is required");
            var success = states.ChangeProject(jobId, targetWatchPath, watchPath);
            return success ? Results.Ok() : Results.BadRequest("Failed to change project");
        });

        group.MapPut("/{jobId}/model", (string jobId, string? project, string? watchPath, SetJobModelRequest req, TaskMutationService mutations, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var success = mutations.SetJobModel(jobId, req?.Model, watchPath);
            return success ? Results.Ok() : Results.NotFound();
        });

        group.MapPut("/{jobId}/thinking-level", (string jobId, string? project, string? watchPath, SetJobThinkingLevelRequest req, TaskMutationService mutations, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var success = mutations.SetJobThinkingLevel(jobId, req?.ThinkingLevel, watchPath);
            return success ? Results.Ok() : Results.NotFound();
        });

        group.MapPut("/{jobId}/cli-type", (string jobId, string? project, string? watchPath, SetJobCliTypeRequest req, TaskMutationService mutations, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            if (req is null || !CliTypes.IsValid(req.CliType))
                return Results.BadRequest(new { error = $"cliType must be one of {string.Join(", ", CliTypes.All)}" });
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var ok = mutations.SetJobCliType(jobId, req.CliType, watchPath);
            if (!ok) return Results.NotFound();
            if (req.UseOwnSession.HasValue)
                mutations.SetJobUseOwnSession(jobId, req.UseOwnSession.Value, watchPath);
            return Results.Ok();
        });

        // Epics assignment way 2 (post-hoc): attach/detach a task to a parent epic.
        // Body { epicId }: null or empty detaches. (Way 1 is CreateTaskRequest.EpicId
        // at create time; way 3 is an epic's decomposition run creating sub-tasks
        // with epicId via the create path.)
        group.MapPut("/{jobId}/epic", (string jobId, string? project, string? watchPath, SetJobEpicRequest req, TaskMutationService mutations, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var ok = mutations.SetJobEpic(jobId, req?.EpicId, watchPath);
            return ok ? Results.Ok() : Results.NotFound();
        });

        group.MapPut("/{jobId}/title", (string jobId, string? project, string? watchPath, SetJobTitleRequest req, TaskMutationService mutations, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Title))
                return Results.BadRequest(new { error = "Title is required" });

            watchPath = ResolveWatchPath(projects, project, watchPath);
            var success = mutations.SetJobTitle(jobId, req.Title, watchPath);
            return success ? Results.Ok() : Results.NotFound();
        });

        group.MapPut("/{jobId}/task-type", (string jobId, string? project, string? watchPath, SetJobTaskTypeRequest req, TaskMutationService mutations, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.TaskType))
                return Results.BadRequest(new { error = "taskType is required" });
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var success = mutations.SetJobTaskType(jobId, req.TaskType, watchPath);
            return success ? Results.Ok() : Results.NotFound();
        });

        group.MapPut("/{jobId}/requires-integration", (string jobId, string? project,
            string? watchPath, SetJobRequiresIntegrationRequest req,
            TaskMutationService mutations, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            return mutations.SetJobRequiresIntegration(jobId, req.RequiresIntegration, watchPath)
                ? Results.Ok() : Results.NotFound();
        });

        // Explicit content release for references.dependsOn edges with
        // releaseGate=true. Completion never sets this implicitly: the endpoint
        // is the operator/release-step seam that records the additional approval.
        group.MapPut("/{jobId}/release", (HttpContext ctx, string jobId, string? project, string? watchPath, SetJobReleasedRequest req, TaskMutationService mutations, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var success = mutations.SetJobReleased(jobId, req?.Released == true, watchPath, OperatorActor(ctx));
            return success ? Results.Ok(new { released = req?.Released == true }) : Results.NotFound();
        });

        // Replace-all: the request's Tags array becomes the new full set on
        // the job. Empty list clears tags. AGT-2803: unknown tag ids are refused
        // at this boundary against the project's effective vocabulary (workspace
        // registry plus the project's areas). Reads stay lenient - a tag retired
        // after the write still renders as a ghost chip rather than breaking the
        // card - and platform stamps keep using the merge-add writer.
        group.MapPut("/{jobId}/tags", (string jobId, string? project, string? watchPath, SetJobTagsRequest req,
            TaskMutationService mutations, TaskScannerService scanner,
            AgentStudio.Areas.AreaRegistryService areas,
            AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var job = scanner.FindJob(jobId, watchPath);
            if (job == null) return Results.NotFound();
            var validation = areas.ValidateTags(job.ProjectName, req?.Tags);
            if (!validation.Ok) return Results.BadRequest(new { error = validation.Error });
            var success = mutations.SetJobTags(jobId, validation.TagIds, watchPath);
            return success ? Results.Ok(new { tags = validation.TagIds }) : Results.NotFound();
        });

        // F34 / AGT-2029: replace-all write of the structured cross-reference
        // object. Validated against the whole workspace (archive-inclusive so an
        // already-completed/archived waits-on target still resolves): a task may
        // not reference itself and the dependsOn graph must stay a DAG (no
        // cycles) - both hard errors returning 400 with a per-edge list. An
        // unknown key is NOT a hard failure (AGT-2029): the waits-on target may
        // be created later, so the write persists and the unknown edges come
        // back as `warnings` for the FE to surface as an open dependency chip.
        group.MapPut("/{jobId}/references", (string jobId, string? project, string? watchPath, SetTaskReferencesRequest req,
            TaskScannerService scanner, TaskMutationService mutations, AgentStudio.Registry.ProjectRegistry projects,
            AgentStudio.Docs.WorkbenchCatalogueService workbenches) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound();

            var proposed = (req ?? new SetTaskReferencesRequest()).ToReferences();
            var index = scanner.GetReferenceIndex();
            var owningProject = !string.IsNullOrWhiteSpace(info.ProjectName)
                ? info.ProjectName
                : project;
            var validation = TaskReferenceValidator.Validate(
                info.Key ?? "", proposed, index.KnownKeys, index.DependsOnGraph,
                string.IsNullOrWhiteSpace(owningProject)
                    ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    : workbenches.KnownKeys(owningProject));

            if (!validation.IsValid)
                return Results.BadRequest(new
                {
                    error = "Invalid references",
                    errors = validation.Errors.Select(e => new
                    {
                        code = e.Code.ToString(),
                        kind = e.Kind,
                        target = e.Target,
                        message = e.Message
                    })
                });

            var success = mutations.SetTaskReferences(jobId, proposed, watchPath);
            if (!success) return Results.NotFound();
            return Results.Ok(new
            {
                references = proposed,
                warnings = validation.Warnings.Select(w => new
                {
                    code = w.Code.ToString(),
                    kind = w.Kind,
                    target = w.Target,
                    message = w.Message
                })
            });
        });

        // Incremental and audited dependency edit. Add + remove in one request
        // is the atomic re-point used by an unsatisfiable-hold decision.
        group.MapPut("/{jobId}/waits-on", (string jobId, string? project, string? watchPath,
            EditTaskWaitsOnRequest req, HttpContext ctx,
            TaskScannerService scanner, TaskMutationService mutations,
            AgentStudio.Registry.ProjectRegistry projects) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Reason))
                return Results.BadRequest(new { error = "A reason is required for a waits-on edit." });
            if ((req.Add?.Count ?? 0) == 0 && (req.Remove?.Count ?? 0) == 0)
                return Results.BadRequest(new { error = "Add or remove at least one dependency." });

            watchPath = ResolveWatchPath(projects, project, watchPath);
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound();
            var removed = (req.Remove ?? []).Where(key => !string.IsNullOrWhiteSpace(key))
                .Select(key => key.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var index = scanner.GetReferenceIndex();
            if (req.RequireCycleEdge)
            {
                if ((req.Add?.Count ?? 0) != 0 || removed.Count != 1)
                    return Results.BadRequest(new
                    {
                        error = "A cycle-edge decision must remove exactly one dependency and cannot add one.",
                    });

                var target = removed.Single();
                if (!WaitsOnEvaluator.IsCycleEdge(info.Key, target, index.DependsOnGraph))
                    return Results.Ok(new
                    {
                        waitsOn = info.References?.DependsOn ?? [],
                        changed = false,
                        cycleResolved = !WaitsOnEvaluator.SitsOnCycle(info.Key, index.DependsOnGraph),
                        message = "The cycle was already broken; no dependency was changed.",
                        warnings = Array.Empty<object>(),
                    });
            }

            var proposed = TaskReferenceValidator.Normalize((info.References ?? new TaskReferences()) with
            {
                DependsOn = (info.References?.DependsOn ?? [])
                    .Where(edge => !removed.Contains(edge.Key))
                    .Concat(req.Add ?? [])
                    .ToList(),
            });
            var validation = TaskReferenceValidator.Validate(
                info.Key ?? "", proposed, index.KnownKeys, index.DependsOnGraph);
            if (!validation.IsValid)
                return Results.BadRequest(new
                {
                    error = "Invalid waits-on edit",
                    errors = validation.Errors.Select(error => new
                    {
                        code = error.Code.ToString(),
                        kind = error.Kind,
                        target = error.Target,
                        message = error.Message,
                    }),
                });

            var changed = mutations.EditTaskWaitsOn(
                jobId, req.Add ?? [], removed, req.Reason, OperatorActor(ctx), watchPath);
            if (!changed) return Results.NotFound();
            var cycleResolved = !req.RequireCycleEdge
                || !WaitsOnEvaluator.SitsOnCycle(
                    info.Key,
                    scanner.GetReferenceIndex().DependsOnGraph);
            return Results.Ok(new
            {
                waitsOn = proposed.DependsOn,
                changed = true,
                cycleResolved,
                message = req.RequireCycleEdge && cycleResolved
                    ? "The cycle is resolved."
                    : "The waits-on dependencies were updated.",
                warnings = validation.Warnings.Select(warning => new
                {
                    code = warning.Code.ToString(),
                    target = warning.Target,
                    message = warning.Message,
                }),
            });
        });

        // F34 reverse-index: tasks that reference this one. Optional ?kind=
        // narrows to a single relation (dependsOn / relatedTo / blockedBy /
        // supersedes / followUpOf / raisedFollowUps). Drives the detail-view "referenced by" list and the
        // "show dependents of X" board filter. A keyless task (pre-F33) can
        // never be referenced, so it returns an empty list.
        group.MapGet("/{jobId}/dependents", (string jobId, string? project, string? watchPath, string? kind,
            TaskScannerService scanner, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(info.Key))
                return Results.Ok(Array.Empty<TaskReferenceLink>());

            var index = scanner.GetReferenceIndex();
            return Results.Ok(index.Dependents(info.Key, kind));
        });
    }

    /// <summary>
    /// AGT-2817 - an operator override completes a card against the completion
    /// contract. The reason is stored on the card and shown wherever the card
    /// claims completion, so an empty one is refused at the boundary.
    /// </summary>
    internal static readonly string OverrideReasonRequired =
        "operatorOverride needs a written reason of at least "
        + $"{CompletionContractPolicy.MinimumOverrideReasonLength} characters. It is stored on the "
        + "card and shown wherever the card claims completion.";

    private static string OperatorActor(HttpContext context)
    {
        var clientId = context.Items["ClientId"] as string
            ?? context.Request.Headers["X-Client-Id"].FirstOrDefault();
        return TimelineActors.Human(clientId ?? string.Empty);
    }

    internal static string? AppendDeliveryAcceptanceCriteria(
        string? prompt,
        AgentStudio.Registry.ComponentRoutingResolution route)
    {
        if (route.ConsumerProjects.Count == 0 && route.DeploymentSteps.Count == 0) return prompt;
        var sb = new System.Text.StringBuilder(prompt?.TrimEnd() ?? "");
        if (sb.Length > 0) sb.AppendLine().AppendLine();
        sb.AppendLine("## Ownership and delivery acceptance criteria");
        sb.AppendLine();
        sb.AppendLine($"- Primary implementation: {route.PrimaryProject!.Id} ({route.PrimaryProject.ShortCode}), repository/package `{route.Repository ?? route.PackageOrModule ?? "unspecified"}`.");
        if (route.ConsumerProjects.Count > 0)
            sb.AppendLine($"- Integrate in consumer project(s): {string.Join(", ", route.ConsumerProjects.Select(p => $"{p.Id} ({p.ShortCode})"))}.");
        foreach (var step in route.DeploymentSteps) sb.AppendLine($"- {step.Trim().TrimEnd('.')}.");
        if (route.Environments.Count > 0)
            sb.AppendLine($"- Verify integration in: {string.Join(", ", route.Environments)}.");
        sb.AppendLine($"- Routing evidence: {string.Join("; ", route.Evidence)} (mapping {route.MappingId ?? "local"} v{route.MappingVersion?.ToString() ?? "1"}).");
        return sb.ToString();
    }

    /// <summary>
    /// Validates the target state for move/state endpoints. Returns null on
    /// success. Surfaces a directed error when the caller used a pre-ADR-0025
    /// numbered lane name (<c>4-review</c>, <c>5-completed</c>,
    /// <c>6-archive</c>) so client code can be migrated without guessing.
    /// </summary>
    private static IResult? ValidateTargetState(string targetState)
    {
        if (TaskStates.All.Contains(targetState)) return null;

        if (TaskStates.NumberedLegacyMap.TryGetValue(targetState, out var newName))
        {
            return Results.BadRequest(
                $"Lane '{targetState}' was renamed in ADR-0025. " +
                $"Use '{newName}' instead. Full lane order: {string.Join(", ", TaskStates.All)}.");
        }

        return Results.BadRequest($"Invalid state. Allowed: {string.Join(", ", TaskStates.All)}");
    }

    /// <summary>
    /// Surfaces the background Git-index freshness stamp on a response whose
    /// body shape is a bare array (<c>GET /api/tasks</c>) or otherwise not
    /// worth widening, so a client can still show "as of" without a wire
    /// contract change. <c>GET /api/tasks/grouped</c> carries the same values
    /// as body fields as well, since its response is already an object.
    /// </summary>
    private static void ApplyGitStateHeaders(HttpContext context, GitProjectionFreshness freshness)
    {
        if (freshness.GitStateAt is { } at)
            context.Response.Headers["X-Git-State-At"] = at.ToString("O");
        context.Response.Headers["X-Git-State-Stale"] = freshness.Stale ? "true" : "false";
    }
}

/// <summary>
/// AGT-2069 — body for <c>POST /api/tasks/{id}/planning-closure</c>.
/// <see cref="Declared"/> true records a deliberate "no follow-up intended"
/// declaration (with an optional <see cref="Reason"/>); false clears it.
/// </summary>
public sealed record SetPlanningClosureRequest(bool Declared, string? Reason, string? DeclaredBy);

public sealed record TaskReferenceStatusRequest(IReadOnlyList<string>? Keys);

public sealed record TaskReferenceStatusResponse(IReadOnlyList<TaskReferenceStatusItem> Items);

public sealed record TaskReferenceStatusItem(
    string Key,
    bool Exists,
    string? TaskKey,
    string? Title,
    string? Lane,
    string ProjectId,
    string ProjectName,
    string? ProjectColor,
    TaskMergeSignal? Merge,
    string? ReviewGrade)
{
    public static TaskReferenceStatusItem Ghost(string key, string projectId, string projectName, string? projectColor) =>
        new(key, false, null, null, null, projectId, projectName, projectColor, null, null);
}
