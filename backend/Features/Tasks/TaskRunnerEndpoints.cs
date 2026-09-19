

using static AgentStudio.Tasks.TaskEndpointHelpers;

namespace AgentStudio.Tasks;

/// <summary>
/// CLI execution surface for one job: <c>start</c>, <c>stop</c>,
/// <c>continue</c>, the <c>output</c> buffer the protocol pane polls,
/// the session-event log that drives the "session continued / lost"
/// chip, and the manual <c>summary/regenerate</c> + <c>context-usage/refresh</c>
/// triggers. All routes funnel through <see cref="TaskRunnerService"/>;
/// this file is the HTTP shell.
/// </summary>
public static class TaskRunnerEndpoints
{
    public static void MapTaskRunnerEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/{jobId}/start", async (string jobId, string? project, string? watchPath, StartJobRequest? req, TaskRunnerService runner, TaskScannerService scanner, AgentStudio.Registry.ProjectRegistry projects, CancellationToken ct) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var job = scanner.FindJob(jobId, watchPath);
            if (job == null)
                return Results.NotFound(new { error = "Job not found" });

            if (job.State is not (TaskStates.Ready or TaskStates.Progress))
                return Results.BadRequest(new { error = $"Job is in state '{job.State}' - only jobs in 'ready' or 'progress' can be started" });

            try
            {
                var resp = await runner.StartJobAsync(jobId, watchPath, req?.Model, req?.CliType, req?.ThinkingLevel, ct);
                return resp.Status == "queued"
                    ? Results.Accepted(value: resp)
                    : Results.Ok(resp);
            }
            catch (TaskOperationException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: ex.Status);
            }
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start);

        // Stop works for both execution locations. A local run is signalled
        // directly; a run owned by a remote host cannot be signalled from here,
        // so the request is recorded for its attempt and the owning runner picks
        // it up on its next lease renewal, terminates the worker's process tree,
        // salvages, and hands back with outcome 'Stopped' (AGT-2870). Before
        // this, a remote run answered 404 and the only way to stop it was to
        // kill the process on the host by hand.
        group.MapPost("/{jobId}/stop", (
            string jobId,
            string? project,
            string? watchPath,
            string? reason,
            TaskRunnerService runner,
            TaskScannerService scanner,
            RunLeaseService leases,
            RemoteRunStopRequestStore stops,
            TimelineLog timeline,
            AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            // 'reason' is a hint that travels into RunStatusClassifier so the
            // resulting CliExecution.Status reads as 'stopped' instead of
            // 'failed'. UI sends 'followup' for Pause & Send so the next
            // continue does not look like a crash recovery; everything else
            // (manual Pause button, no value) is a UserStop. Unknown values
            // fall back to UserStop rather than rejecting the request.
            var parsed = (reason ?? "user").Trim().ToLowerInvariant() switch
            {
                "followup" or "followup-pause" => RunStopReason.FollowupPause,
                "watchdog" => RunStopReason.Watchdog,
                _ => RunStopReason.UserStop
            };
            var info = scanner.FindJob(jobId, watchPath);
            if (info is null) return Results.NotFound();

            var dispatch = RemoteRunStopPolicy.Decide(new RunStopFacts(
                runner.StopJob(jobId, watchPath, parsed),
                string.Equals(leases.Peek(info.TaskKey).Outcome, "Held", StringComparison.OrdinalIgnoreCase)));
            if (dispatch != RunStopDispatch.RemoteStopRequested)
                return dispatch == RunStopDispatch.StoppedLocally ? Results.Ok() : Results.NotFound();

            var lease = leases.Peek(info.TaskKey).Lease;
            var request = stops.Record(
                info.TaskKey,
                RemoteRunStopReasons.From(parsed),
                lease?.AttemptId,
                lease?.RunnerName ?? lease?.RunnerId);
            timeline.Append(
                info.FolderPath,
                TimelineEventKinds.RemoteStopRequested,
                TimelineActors.Human(string.Empty),
                $"Stop requested for the remote run on {lease?.RunnerName ?? lease?.RunnerId ?? "the assigned host"}.",
                runId: lease?.AttemptId,
                details: new Dictionary<string, string>
                {
                    ["reason"] = request.Reason,
                    ["runnerId"] = lease?.RunnerId ?? string.Empty,
                });
            return Results.Accepted(value: new
            {
                status = "stop-requested",
                taskKey = info.TaskKey,
                reason = request.Reason,
                runnerId = lease?.RunnerId,
                attemptId = lease?.AttemptId,
                message = "The owning runner stops the attempt on its next lease renewal.",
            });
        });

        group.MapPost("/{jobId}/continue", async (string jobId, string? project, string? watchPath, ContinueJobRequest req, TaskRunnerService runner, AgentStudio.Registry.ProjectRegistry projects, CancellationToken ct) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            if (string.IsNullOrWhiteSpace(req?.Prompt))
                return Results.BadRequest(new { error = "Prompt is required" });

            var mode = ContinueModes.Normalize(req.Mode);
            try
            {
                var resp = await runner.ContinueJobAsync(jobId, req.Prompt, watchPath, req.Model, req.CliType, req.ThinkingLevel, mode, req.ModeOverride, ct);
                return resp.Status == "queued"
                    ? Results.Accepted(value: resp)
                    : Results.Ok(resp);
            }
            catch (TaskOperationException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: ex.Status);
            }
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Continue);

        group.MapGet("/{jobId}/output", (string jobId, string? project, string? watchPath, TaskRunnerService runner, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var output = runner.GetJobOutput(jobId, watchPath);
            return Results.Ok(output);
        });

        // Returns the session-event log for a job: one record per start /
        // continue / recovery, with input + captured session ids and a
        // `resumed` boolean. Drives the "session continued / lost" chip and
        // gives the user a paper trail when continuations don't behave as
        // expected. Includes the current sessionChain so the frontend can
        // render a chip without a second round-trip.
        group.MapGet("/{jobId}/session-events", (string jobId, string? project, string? watchPath, TaskScannerService scanner, TaskSessionLog sessions, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound(new { error = "Job not found" });
            var events = sessions.ReadSessionEvents(jobId, watchPath);
            return Results.Ok(new
            {
                events,
                sessionChain = info.SessionChain,
                currentSessionId = info.SessionName
            });
        });

        // Agent work summary - a small derived view of "what the agent
        // actually did on this job" folded from logs/session-events.jsonl
        // (one row per CLI start / continue / recovery) and
        // logs/tool-calls.jsonl (one row per tool started / completed).
        // Drives the Overview tab's Agent Work block; replaces the inert
        // raw session-id row the operator flagged as no-value noise. The
        // current session id rides along for the debug tooltip only.
        group.MapGet("/{jobId}/agent-work-summary", (
            string jobId,
            string? project,
            string? watchPath,
            TaskScannerService scanner,
            AgentStudio.Registry.ProjectRegistry projects,
            ITokenAggregator tokens) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound(new { error = "Job not found" });
            var tokenLookup = tokens.WorkspacePerJob(info.ProjectName, info.WatchPath);
            TaskTokenSummary? tokenSummary = null;
            foreach (var key in new[] { info.TaskKey, info.Id, info.Key })
            {
                if (!string.IsNullOrWhiteSpace(key) && tokenLookup.TryGetValue(key, out var found))
                {
                    tokenSummary = TokenSummaryService.WithModelFallback(found, info.Model);
                    break;
                }
            }
            return Results.Ok(AgentWorkSummaryReader.Read(info, tokenSummary));
        });

        // Drill-down companion to agent-work-summary: the same tool-calls.jsonl
        // folded into per-tool groups, each carrying the individual calls
        // (started argument paired with the completed outcome) so the Overview
        // tab's Agent Work block can show *what* the agent did - the command /
        // file / pattern of each call - in a grouped, expandable view, not just
        // a per-tool count. Read-only; tolerant of missing / torn logs.
        group.MapGet("/{jobId}/agent-work-detail", (string jobId, string? project, string? watchPath, TaskScannerService scanner, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound(new { error = "Job not found" });
            return Results.Ok(AgentWorkSummaryReader.ReadDetail(info));
        });

        // The per-job task plan that drives the plan strip above the activity
        // log: the agent's own TodoWrite / update_plan items with derived
        // sub-actions and a soft-estimate band. Folded from
        // logs/plan-snapshots.jsonl + logs/tool-calls.jsonl by PlanReader -
        // read-only, no model call. Live updates ride the SignalR planUpdated
        // event; this endpoint is the initial fetch + refetch target.
        group.MapGet("/{jobId}/plan", (string jobId, string? project, string? watchPath, TaskScannerService scanner, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound(new { error = "Job not found" });
            return Results.Ok(PlanReader.Read(info));
        });

        // The condensed run timeline that drives the protocol-pane redesign.
        // One record per CLI invocation between user inputs, paired with the
        // [taskboard] Started/exited markers in cli-output.log so the frontend
        // can render line-spans for drill-down. See docs/quality/design-principles.md
        // for the contract this surface has to honour: top-level summary +
        // always-available drill-down.
        group.MapGet("/{jobId}/runs", (string jobId, string? project, string? watchPath, TaskReader reader, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            // T2b (ASS-1740): the run timeline is now one projection of the
            // unified per-task read model instead of a private parse here.
            var model = reader.Read(jobId, watchPath);
            if (model == null) return Results.NotFound(new { error = "Job not found" });
            return Results.Ok(model.BuildRunTimeline());
        });

        // The unified per-task event ledger (logs/timeline.jsonl, ADR-0049):
        // prompt-created, agent runs, pipeline steps, and the orchestrator's
        // completion-loop verdicts (accept / reopen / escalate, ASS-566) in
        // one greppable, time-ordered stream. Drives the Overview attempt
        // indicator and the Timeline tab. Read-only and tolerant of torn
        // trailing lines.
        group.MapGet("/{jobId}/timeline", (string jobId, string? project, string? watchPath, TaskReader reader, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            // T2b (ASS-1740): the same unified read model also projects the
            // ledger, meshing each lane_changed row with its ASS-1724 anchor.
            var model = reader.Read(jobId, watchPath);
            if (model == null) return Results.NotFound(new { error = "Job not found" });
            return Results.Ok(model.BuildLedger());
        });

        // Per-run software-side change set: the commits authored during
        // this run. Prefers the deterministic SHA range
        // HeadShaBefore..HeadShaAfter captured by ProjectRunner for the
        // run's deterministic commit range; worktree runs stamp the
        // integration-branch range that landed for this task. Falls back
        // to the wall-clock window for older runs without SHAs. The
        // wall-clock fallback is best-effort - the SHA-range path is
        // the source of truth for new runs and is what the integration
        // test pins. Index is 1-based to match RunRecord.Index.
        group.MapGet("/{jobId}/runs/{index:int}/commits", (
            string jobId, int index, string? project, string? watchPath,
            TaskReader reader, GitService git, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var model = reader.Read(jobId, watchPath);
            if (model == null) return Results.NotFound(new { error = "Job not found" });
            var run = model.ResolveRun(index, out var error);
            if (run == null) return Results.NotFound(new { error });

            var commits = !string.IsNullOrWhiteSpace(run.HeadShaBefore) && !string.IsNullOrWhiteSpace(run.HeadShaAfter)
                ? git.GetCommitsInShaRange(jobId, watchPath, run.HeadShaBefore, run.HeadShaAfter)
                : git.GetCommitsBetween(jobId, watchPath, run.StartedAt, run.EndedAt ?? DateTime.UtcNow);

            return Results.Ok(new
            {
                runIndex = run.Index,
                startedAt = run.StartedAt,
                endedAt = run.EndedAt,
                headShaBefore = run.HeadShaBefore,
                headShaAfter = run.HeadShaAfter,
                source = !string.IsNullOrWhiteSpace(run.HeadShaBefore) && !string.IsNullOrWhiteSpace(run.HeadShaAfter)
                    ? "sha-range"
                    : "wall-clock",
                commits
            });
        });

        // Aggregated file list for a run: every path touched by any
        // commit in HeadShaBefore..HeadShaAfter, with combined +/-
        // counts. Drives the file-tree side of the run's git viewer.
        group.MapGet("/{jobId}/runs/{index:int}/files", (
            string jobId, int index, string? project, string? watchPath,
            TaskReader reader, GitService git, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var model = reader.Read(jobId, watchPath);
            if (model == null) return Results.NotFound(new { error = "Job not found" });
            var run = model.ResolveRun(index, out var error);
            if (run == null) return Results.NotFound(new { error });

            if (string.IsNullOrWhiteSpace(run.HeadShaBefore) || string.IsNullOrWhiteSpace(run.HeadShaAfter))
            {
                return Results.Ok(new
                {
                    runIndex = run.Index,
                    headShaBefore = run.HeadShaBefore,
                    headShaAfter = run.HeadShaAfter,
                    files = new List<GitFileChange>(),
                    note = "Run has no captured SHAs (older run or repo unavailable). The git viewer needs the SHA range."
                });
            }

            var files = git.GetFilesChangedInShaRange(jobId, watchPath, run.HeadShaBefore, run.HeadShaAfter);
            return Results.Ok(new
            {
                runIndex = run.Index,
                headShaBefore = run.HeadShaBefore,
                headShaAfter = run.HeadShaAfter,
                files
            });
        });

        // Unified diff for one path across the run's SHA range. Returns
        // the raw diff body so the frontend's existing diff renderer
        // can consume it without re-parsing.
        group.MapGet("/{jobId}/runs/{index:int}/diff", (
            string jobId, int index, string? path, string? project, string? watchPath,
            TaskReader reader, GitService git, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var model = reader.Read(jobId, watchPath);
            if (model == null) return Results.NotFound(new { error = "Job not found" });
            var run = model.ResolveRun(index, out var error);
            if (run == null) return Results.NotFound(new { error });

            if (string.IsNullOrWhiteSpace(run.HeadShaBefore) || string.IsNullOrWhiteSpace(run.HeadShaAfter))
                return Results.Ok(new { diff = "", note = "Run has no captured SHAs." });

            var diff = git.GetDiffInShaRange(jobId, watchPath, run.HeadShaBefore, run.HeadShaAfter, path);
            return Results.Ok(new { diff });
        });

        // The exact context handed to the agent for run #index: the rendered
        // prompt template plus the task's prompt.md, attachments list, mode
        // framing, and any foregrounded reissue open-items block. Captured at
        // spawn time into logs/run-context/<ts>.md and referenced from the
        // run's session event (ContextRef). Served on demand so the polled
        // runs list stays lean. Makes reruns / escalations auditable: the run
        // card can show *what* the run was started with. Index is 1-based to
        // match RunRecord.Index.
        group.MapGet("/{jobId}/runs/{index:int}/context", (
            string jobId, int index, string? project, string? watchPath,
            TaskReader reader, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var model = reader.Read(jobId, watchPath);
            if (model == null) return Results.NotFound(new { error = "Job not found" });
            var info = model.Info;
            var run = model.ResolveRun(index, out var error);
            if (run == null) return Results.NotFound(new { error });

            if (string.IsNullOrWhiteSpace(run.ContextRef))
            {
                return Results.Ok(new
                {
                    runIndex = run.Index,
                    context = (string?)null,
                    note = "No passed-context captured for this run (recorded before context capture, or the write failed)."
                });
            }

            // ContextRef is a relative path we wrote ourselves, but it comes
            // off disk - guard against a tampered '..' escaping the job folder.
            var folderFull = Path.GetFullPath(info.FolderPath);
            var contextFull = Path.GetFullPath(Path.Combine(info.FolderPath, run.ContextRef));
            if (!contextFull.StartsWith(folderFull, StringComparison.Ordinal) || !File.Exists(contextFull))
            {
                return Results.Ok(new
                {
                    runIndex = run.Index,
                    context = (string?)null,
                    note = "Context file missing or outside the job folder."
                });
            }

            string context;
            try { context = File.ReadAllText(contextFull); }
            catch { context = string.Empty; }

            return Results.Ok(new
            {
                runIndex = run.Index,
                context,
                promptTokenEstimate = PromptTokenEstimator.EstimateOrNull(context),
                contextTokenEstimate = PromptTokenEstimator.EstimateOrNull(context)
            });
        });

        // Manual re-trigger of the task-level Result summary the runner fires
        // post-execution. Surfaced behind a button while we iterate on the prompt
        // and observe failure modes. It overwrites status.md when generation succeeds.
        // Pre-flight checks (e.g. missing cli-output.log) happen inside
        // GenerateAsync so the failure mode is recorded as a regular Failed
        // SummaryState the UI can render in-place — surfacing the precise
        // reason via the banner instead of a top-level error dialog.
        group.MapPost("/{jobId}/summary/regenerate", (string jobId, string? project, string? watchPath, TaskScannerService scanner, SummaryGenerationService summaries, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null)
                return Results.NotFound(new { error = "Job not found" });

            // Fire-and-forget — the frontend polls summaryState until it flips
            // out of "generating", same path the post-run summary uses.
            _ = summaries.GenerateAsync(info);
            return Results.Accepted();
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Review);

        // Synchronous "interim status" peek while a run is in flight. Runs a
        // one-shot routed call against the bounded task evidence and returns the
        // markdown directly; status.md on disk is NOT touched, so the
        // post-run summary still owns it. Surfaced by the "Interim status"
        // button in the protocol pane so the user can check on a long-running
        // task without stopping it.
        group.MapPost("/{jobId}/summary/interim", async (string jobId, string? project, string? watchPath, TaskScannerService scanner, SummaryGenerationService summaries, AgentStudio.Registry.ProjectRegistry projects, CancellationToken ct) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null)
                return Results.NotFound(new { error = "Job not found" });

            var result = await summaries.GenerateInterimAsync(info, ct);
            if (!result.Ok)
                return Results.BadRequest(new { error = result.Error ?? "Interim summary failed" });

            return Results.Ok(new { markdown = result.Markdown, durationMs = result.DurationMs });
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Preview);

        group.MapPost("/{jobId}/context-usage/refresh", async (string jobId, string? project, string? watchPath, TaskRunnerService runner, AgentStudio.Registry.ProjectRegistry projects, CancellationToken ct) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var (snapshot, error) = await runner.RefreshContextUsageAsync(jobId, watchPath, ct);
            return snapshot is not null
                ? Results.Ok(snapshot)
                : Results.BadRequest(new { error = error ?? "Cannot refresh context usage" });
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Preview);
    }
}
