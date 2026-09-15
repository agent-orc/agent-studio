

using System.Text;

using static AgentStudio.Tasks.TaskEndpointHelpers;

namespace AgentStudio.Tasks;

/// <summary>
/// Git surface scoped to one job: live status / diff against the
/// project's working tree, manual + LLM-assisted commit, the cached
/// commit detail (file list + diff) that backs the "Commit" view in
/// the protocol pane, and the IDE handoff. All routes operate on the
/// project's RootPath repository.
/// </summary>
public static class TaskGitEndpoints
{
    public static void MapTaskGitEndpoints(this RouteGroupBuilder group)
    {
        // preferRunLocation: the live per-task Git view must read the task's own
        // run-location - its task/<id> worktree when it has one - not the shared
        // main checkout, so a parallel run's dirty files are never cross-attributed
        // to this task (ASS-1731). Falls back to the main checkout when the task
        // has no live worktree.
        group.MapGet("/{jobId}/git/status", (string jobId, string? project, string? watchPath, GitService git, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            return Results.Ok(git.GetStatus(jobId, watchPath, preferRunLocation: true));
        });

        group.MapGet("/{jobId}/git/diff", (string jobId, string? project, string? watchPath, string? path, GitService git, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            return DiffTextResult(git.GetDiffResult(jobId, watchPath, path, preferRunLocation: true));
        });

        // Full working-tree text of one file, for the git-pane's rendered
        // md/html preview (AGT-2008). Reads the task's own run location so a
        // per-task worktree previews its own copy, matching /git/diff.
        group.MapGet("/{jobId}/git/file", (string jobId, string? project, string? watchPath, string? path, GitService git, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            return FileContentResult(git.GetFileContentResult(jobId, watchPath, path, sha: null, preferRunLocation: true));
        });

        group.MapPost("/{jobId}/git/commit", (string jobId, string? project, string? watchPath, GitCommitRequest req, GitService git, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var result = git.Commit(jobId, watchPath, req.Message);
            return result.Success
                ? Results.Ok(new { sha = result.Sha })
                : Results.BadRequest(new { error = result.Error });
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.PostStep);

        group.MapPost("/{jobId}/git/generate-message", async (string jobId, string? project, string? watchPath, GitService git, AgentStudio.Registry.ProjectRegistry projects, CancellationToken ct) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var result = await git.GenerateCommitMessageAsync(jobId, watchPath, ct);
            return result.Message is not null
                ? Results.Ok(new { message = result.Message })
                : Results.BadRequest(new { error = result.Error });
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Preview);

        // Per-job commit details: returns the cached snapshot from task.json plus
        // a live re-derivation of the file list from `git show --name-status`,
        // so the detail view stays accurate even after history rewrites.
        group.MapGet("/{jobId}/commit", (string jobId, string? project, string? watchPath, TaskScannerService scanner, GitService git, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound();
            if (info.Commit == null) return Results.Ok(new { commit = (object?)null, files = Array.Empty<GitFileChange>() });

            var live = git.GetCommitFiles(jobId, watchPath, info.Commit.Sha);
            var files = live.Count > 0 ? live : info.Commit.Files.Select(p => new GitFileChange("?", p, 0, 0)).ToList();
            return Results.Ok(new { commit = info.Commit, files });
        });

        // Diff for the recorded commit, optionally scoped to one path. Lets
        // the detail view show the exact changes the task produced even long
        // after the working tree has moved on.
        group.MapGet("/{jobId}/commit/diff", (string jobId, string? project, string? watchPath, string? path, TaskScannerService scanner, GitService git, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound(new { error = "Job not found" });
            if (info.Commit == null) return Results.NotFound(new { error = "This task has no recorded commit." });
            return DiffTextResult(git.GetCommitDiffResult(jobId, watchPath, info.Commit.Sha, path));
        });

        // Job-level commit aggregation: every commit attributed to this
        // job across all of its runs (deduped by SHA), plus the
        // auto-commit when present. Drives the protocol-pane "Commits
        // and change set" panel - the user must be able to see what
        // landed without having to drill into individual runs first.
        group.MapGet("/{jobId}/commits", (
            string jobId, string? project, string? watchPath,
            TaskScannerService scanner, TaskSessionLog sessions, GitService git,
            TaskMutationService mutations, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound(new { error = "Job not found" });

            // Lazy attribution backfill for legacy folders. A job that left
            // 3-progress before the commit-attribution step existed carries an
            // empty commits[] even though its runs moved
            // HEAD; the kanban card's commit total (derived from commits[]) then
            // reads 0 while the change set below clearly shows work. On first
            // view we run the same deterministic rule engine the transition
            // uses and persist the result, so the SSOT catches up. Idempotent:
            // once either list is populated the guard skips, and an
            // analysis-only job (no code activity) is skipped without touching
            // git. See ADR "Commit-Attribution-Regel".
            info = TryBackfillAttribution(info, watchPath, scanner, sessions, git, mutations);

            var aggregate = BuildJobCommitsAggregate(info, sessions, jobId, watchPath, git);

            return Results.Ok(aggregate);
        });

        // API-owned repair for accepted integration commits that already exist
        // in Git but were not part of the task's automatic attribution window.
        // This route never creates or rewrites Git history. It only appends the
        // resolved commit metadata to task.json through TaskMutationService.
        // The full-SHA and task-key-in-message fences keep it narrower than the
        // retired generic operator include/exclude surface.
        group.MapPost("/{jobId}/commits/integration", (
            string jobId, string? project, string? watchPath,
            AppendIntegrationCommitRequest req,
            TaskScannerService scanner, GitService git,
            TaskMutationService mutations, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound(new { error = "Task not found." });

            var sha = req?.Sha?.Trim() ?? "";
            if (sha.Length != 40 || sha.Any(c => !Uri.IsHexDigit(c)))
                return Results.BadRequest(new { error = "A full 40-character commit SHA is required." });

            var metadata = git.GetCommitMeta(jobId, watchPath, [sha]);
            if (!metadata.TryGetValue(sha, out var commitMeta))
                return Results.BadRequest(new { error = "Commit does not exist in the task repository." });

            var taskKey = string.IsNullOrWhiteSpace(info.Key) ? info.Id : info.Key;
            if (string.IsNullOrWhiteSpace(taskKey)
                || !commitMeta.Body.Contains(taskKey, StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new
                {
                    error = $"Integration commit message must name task key '{taskKey}'."
                });
            }

            var files = git.GetCommitFiles(jobId, watchPath, sha);
            var commit = new TaskCommitInfo
            {
                Sha = sha,
                ShortSha = sha[..8],
                Message = commitMeta.Body.Trim(),
                FilesChanged = files.Count,
                Files = files.Select(file => file.Path).ToList(),
                At = commitMeta.AuthorDateUtc,
                Attribution = CommitAttributionKinds.Manual,
                Confidence = 1.0,
            };

            if (!mutations.SetJobCommit(jobId, commit, watchPath))
                return Results.Json(
                    new { error = "Failed to append the integration commit." },
                    statusCode: StatusCodes.Status500InternalServerError);

            var refreshed = scanner.FindJob(jobId, watchPath);
            return Results.Ok(new
            {
                commit = refreshed?.Commit ?? commit,
                commits = refreshed?.Commits ?? [commit],
            });
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.PostStep);

        group.MapGet("/{jobId}/commits/files", (
            string jobId, string? project, string? watchPath,
            TaskScannerService scanner, TaskSessionLog sessions, GitService git,
            TaskMutationService mutations, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound(new { error = "Job not found" });
            info = TryBackfillAttribution(info, watchPath, scanner, sessions, git, mutations);

            var shas = JobCommitShas(info, sessions, jobId, watchPath, git);
            var files = git.GetAggregateCommitFiles(jobId, watchPath, shas);
            return Results.Ok(new { files });
        });

        group.MapGet("/{jobId}/commits/diff", (
            string jobId, string? project, string? path, string? watchPath,
            TaskScannerService scanner, TaskSessionLog sessions, GitService git,
            TaskMutationService mutations, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound(new { error = "Job not found" });
            info = TryBackfillAttribution(info, watchPath, scanner, sessions, git, mutations);

            var shas = JobCommitShas(info, sessions, jobId, watchPath, git);
            if (shas.Count == 0)
            {
                return Results.Ok(DiffJsonResponse("", "This task has no attributed commits."));
            }
            var result = git.GetAggregateCommitDiffResult(jobId, watchPath, shas, path);
            return result.Success
                ? Results.Ok(DiffJsonResponse(result.Diff, "No diff for this path in the task's attributed commits."))
                : Results.BadRequest(new { error = result.Error ?? "Could not load diff." });
        });

        // File list for one of the job's commits. Validates the SHA is
        // actually a known commit on this job before calling git so the
        // endpoint can't be coaxed into showing arbitrary repo history.
        group.MapGet("/{jobId}/commits/{sha}/files", (
            string jobId, string sha, string? project, string? watchPath,
            TaskScannerService scanner, TaskSessionLog sessions, GitService git, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound(new { error = "Job not found" });
            if (!IsKnownJobCommit(info, sessions, jobId, watchPath, git, sha))
                return Results.NotFound(new { error = "Commit is not associated with this job." });
            var files = git.GetCommitFiles(jobId, watchPath, sha);
            return Results.Ok(new { sha, files });
        });

        group.MapGet("/{jobId}/commits/{sha}/diff", (
            string jobId, string sha, string? project, string? path, string? watchPath,
            TaskScannerService scanner, TaskSessionLog sessions, GitService git, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound(new { error = "Job not found" });
            if (!IsKnownJobCommit(info, sessions, jobId, watchPath, git, sha))
                return Results.NotFound(new { error = "Commit is not associated with this job." });
            var result = git.GetCommitDiffResult(jobId, watchPath, sha, path);
            return result.Success
                ? Results.Ok(DiffJsonResponse(result.Diff, "No diff for this path in the selected commit."))
                : Results.BadRequest(new { error = result.Error ?? "Could not load diff." });
        });

        // File content at one of the job's commits, for the commit-mode md/html
        // preview (AGT-2008). Same known-commit gate as the diff endpoint so the
        // blob can only be read from a commit that actually belongs to this job.
        group.MapGet("/{jobId}/commits/{sha}/file", (
            string jobId, string sha, string? project, string? path, string? watchPath,
            TaskScannerService scanner, TaskSessionLog sessions, GitService git, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound(new { error = "Job not found" });
            if (!IsKnownJobCommit(info, sessions, jobId, watchPath, git, sha))
                return Results.NotFound(new { error = "Commit is not associated with this job." });
            return FileContentResult(git.GetFileContentResult(jobId, watchPath, path, sha));
        });

        // Commit-provenance & landed-state (ASS-1724). Returns the persisted
        // append-only provenance facts plus everything derived live off the
        // graph: the landed-state (on-branch-only / merged-to-develop /
        // released-to-main via merge-base --is-ancestor), the landed ladder
        // (task/<id> -> develop @sha -> main @sha with "HEAD now"), and per-commit
        // branch membership. Recomputed on every read so it never lies about
        // where develop / main currently are.
        group.MapGet("/{jobId}/provenance", (
            string jobId, string? project, string? watchPath,
            TaskScannerService scanner, TaskProvenanceService provenance, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound(new { error = "Job not found" });
            return Results.Ok(provenance.BuildView(info));
        });

        // Per-job hygiene: project-level snapshot overlaid with whether the
        // job carries a platform-owned commit stamp and whether accepted task
        // work appears uncommitted. The job-detail review/completed strip
        // polls this; the project header polls /api/git/hygiene instead.
        //
        // Worktree-isolation rule: the working tree is shared across the
        // whole repository, so `acceptedTaskUncommitted` must only fire on
        // the task that owns whatever the agent is currently editing -
        // i.e. the runner's active job for the project. We resolve that
        // via TaskRunnerService and pass `isActiveJob` to GitService so
        // non-active tasks get the warning suppressed at the data layer,
        // not just hidden in the UI.
        group.MapGet("/{jobId}/git/hygiene", (
            string jobId, string? project, string? watchPath,
            GitService git, TaskScannerService scanner, TaskRunnerService runner, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var info = scanner.FindJob(jobId, watchPath);
            var status = runner.GetStatus();
            var isActive = info != null
                && status.Projects.TryGetValue(info.ProjectName, out var projectStatus)
                && string.Equals(projectStatus.ActiveJobId, info.Id, StringComparison.Ordinal);
            var hygiene = git.GetJobHygiene(jobId, watchPath, isActive);
            return string.IsNullOrEmpty(hygiene.Error)
                ? Results.Ok(hygiene)
                : Results.NotFound(new { error = hygiene.Error });
        });

        // Manual "commit accepted task evidence" action. Re-uses the same
        // platform-owned commit-message path the auto-commit on
        // 3-progress -> 4-auto-review uses (Haiku via runtime/commit-message.md
        // with a deterministic fallback) so the user gets one consistent
        // commit voice regardless of which CLI did the work. Stamps the
        // produced SHA onto TaskInfo.Commit so the detail view picks it up,
        // and writes a [commit] orchestrator-chat entry into the activity
        // log so the action is visible in the protocol pane.
        group.MapPost("/{jobId}/git/commit-accepted-evidence",
            async (string jobId, string? project, string? watchPath,
                   GitService git, TaskScannerService scanner, TaskMutationService mutations,
                   OrchestratorChatLog chat, AgentStudio.Registry.ProjectRegistry projects, CancellationToken ct) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound(new { error = "Job not found" });

            var (result, message) = await git.AutoCommitAsync(jobId, watchPath, ct);
            if (!result.Success || string.IsNullOrWhiteSpace(result.Sha))
            {
                return Results.BadRequest(new { error = result.Error ?? "Commit failed", message });
            }

            var files = git.GetCommitFiles(jobId, watchPath, result.Sha);
            var commitInfo = new TaskCommitInfo
            {
                Sha = result.Sha,
                ShortSha = result.Sha.Length > 7 ? result.Sha[..7] : result.Sha,
                Message = message ?? "",
                FilesChanged = files.Count,
                Files = files.Select(f => f.Path).ToList(),
                At = DateTime.UtcNow
            };
            mutations.SetJobCommitOnFolder(info.FolderPath, commitInfo);
            git.InvalidateHygieneCache();

            // Refresh and chat-log entry: the protocol-pane activity-log
            // reader will pick this up as a [commit] line and render it
            // alongside agent + orchestrator messages.
            var refreshed = scanner.FindJob(jobId, watchPath) ?? info;
            try
            {
                chat.Append(refreshed, OrchestratorMessageKind.Decision,
                    $"Committed accepted task evidence: {commitInfo.ShortSha} \"{(message ?? "").Split('\n')[0]}\" ({commitInfo.FilesChanged} file{(commitInfo.FilesChanged == 1 ? "" : "s")})");
            }
            catch (Exception __ex) { SilentCatch.Note(__ex, "TaskGitEndpoints: chat-log is best-effort"); /* chat-log is best-effort */ }

            return Results.Ok(new { commit = commitInfo });
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.PostStep);

        // What the commit candidate gate withheld from this card's platform
        // commit: the file list plus the finding code behind each one. The
        // parked card summarizes this in one sentence; this route is the "which
        // files and why" an operator reads before committing them.
        group.MapGet("/{jobId}/git/withheld-candidates", (
            string jobId, string? project, string? watchPath,
            TaskScannerService scanner, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound(new { error = "Job not found" });
            var report = CommitWithholdingMarker.TryRead(info.FolderPath);
            return Results.Ok(new { report });
        });

        // Operator action for the WEB-21 failure: a complete delivery that the
        // commit candidate gate refused stays dirty in the worktree until a
        // person reviews it. This records that review and commits through the
        // same manifest-bound path every platform commit uses - hard blocks
        // still block, deterministic exclusions still exclude.
        group.MapPost("/{jobId}/git/commit-withheld-candidates", (
            string jobId, string? project, string? watchPath,
            CommitWithheldCandidatesRequest? req,
            GitService git, TaskScannerService scanner, TaskMutationService mutations,
            OrchestratorChatLog chat, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound(new { error = "Job not found" });

            var result = git.CommitWithheldCandidates(
                jobId, watchPath, req?.Paths, req?.Message);
            if (!result.Success || result.Commit?.Sha is null)
                return Results.BadRequest(new
                {
                    error = result.Error ?? "Commit failed",
                    findings = result.Commit?.Gate?.Findings ?? [],
                });

            var sha = result.Commit.Sha!;
            var files = git.GetCommitFiles(jobId, watchPath, sha);
            var commitInfo = new TaskCommitInfo
            {
                Sha = sha,
                ShortSha = sha.Length > 7 ? sha[..7] : sha,
                Message = $"Operator-reviewed commit candidates ({result.Committed.Count})",
                FilesChanged = files.Count > 0 ? files.Count : result.Committed.Count,
                Files = files.Count > 0
                    ? files.Select(f => f.Path).ToList()
                    : result.Committed.ToList(),
                At = DateTime.UtcNow,
            };
            mutations.SetJobCommitOnFolder(info.FolderPath, commitInfo);
            git.InvalidateHygieneCache();

            var refreshed = scanner.FindJob(jobId, watchPath) ?? info;
            try
            {
                chat.Append(refreshed, OrchestratorMessageKind.Decision,
                    $"Operator committed {result.Committed.Count} candidate(s) the commit candidate gate had withheld: {commitInfo.ShortSha}.");
            }
            catch (Exception __ex) { SilentCatch.Note(__ex, "TaskGitEndpoints: chat-log is best-effort"); }

            return Results.Ok(new
            {
                commit = commitInfo,
                committed = result.Committed,
                stillWithheld = result.StillWithheld,
            });
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.PostStep);

        group.MapPost("/{jobId}/open-in-vscode", (string jobId, string? project, string? watchPath, GitService git, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            return git.OpenInVsCode(jobId, watchPath, out var error)
                ? Results.Ok()
                : Results.BadRequest(new { error });
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Preview);
    }

    // Lanes where commit attribution is considered final: the job has left
    // 3-progress, so the transition post-step should already have stamped a
    // chain. A legacy folder that predates the step lands here with empty
    // lists and is the backfill target. (Includes the pre-ADR-0025 "4-review"
    // alias for folders that never migrated.)
    private static readonly HashSet<string> AttributionFinalLanes = new(StringComparer.OrdinalIgnoreCase)
    {
        TaskStates.AutoReview, TaskStates.HumanReview, TaskStates.Escalated, "4-review",
        TaskStates.Completed, TaskStates.Archive,
    };

    /// <summary>
    /// Repopulates <c>commits[]</c> for a legacy job whose attribution never
    /// ran, then returns the refreshed <see cref="TaskInfo"/>. A no-op (returns
    /// <paramref name="info"/> unchanged) unless the job is in an
    /// attribution-final lane, has an empty chain, and shows code activity.
    /// Best-effort: any failure is swallowed so a read never fails on a
    /// backfill hiccup.
    /// </summary>
    private static TaskInfo TryBackfillAttribution(
        TaskInfo info, string? watchPath,
        TaskScannerService scanner, TaskSessionLog sessions, GitService git,
        TaskMutationService mutations)
    {
        if (!AttributionFinalLanes.Contains(info.State)) return info;
        if (info.Commits.Count > 0) return info;
        if (!info.CodeActivityDetected) return info;

        try
        {
            var result = CommitAttributionRunner.Run(info, watchPath, sessions, git);
            if (result == null) return info;
            if (result.Attributed.Count == 0 && result.Excluded.Count == 0) return info;

            mutations.SetCommitAttributionOnFolder(info.FolderPath, result.Attributed);
            return scanner.FindJob(info.Id, watchPath) ?? info;
        }
        catch
        {
            return info;
        }
    }

    /// <summary>
    /// Builds the job-level commit aggregate from all three sources: per-run
    /// SHA ranges, the reconstructed task-branch run commits (durable trailer),
    /// and the persisted attribution chain + auto-commit. Shared by the
    /// <c>/commits</c> list, drill-down validation, and the combined files/diff
    /// endpoints so every surface agrees on which commits belong to the job.
    /// Delegates to <see cref="JobCommitsAggregation.Build"/> - the single
    /// binding reused by the task-detail endpoint (ASS-1712) so both surfaces
    /// agree on a job's commit set.
    /// </summary>
    private static TaskCommitsAggregate BuildJobCommitsAggregate(
        TaskInfo info, TaskSessionLog sessions, string jobId, string? watchPath, GitService git)
        => JobCommitsAggregation.Build(info, sessions, jobId, watchPath, git);

    private static bool IsKnownJobCommit(
        TaskInfo info, TaskSessionLog sessions,
        string jobId, string? watchPath, GitService git, string sha)
    {
        if (string.IsNullOrWhiteSpace(sha)) return false;
        if (info.Commit != null && string.Equals(info.Commit.Sha, sha, StringComparison.OrdinalIgnoreCase))
            return true;
        var aggregate = BuildJobCommitsAggregate(info, sessions, jobId, watchPath, git);
        return aggregate.Commits.Any(c => string.Equals(c.Sha, sha, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// SHA set for the combined files/diff endpoints. Derives from the full
    /// aggregate (same source as the displayed <c>/commits</c> list) so the
    /// combined change set matches what the user sees - including the
    /// reconstructed per-run commits of an in-progress per-task-worktree job,
    /// whose persisted chain is still empty.
    /// </summary>
    private static IReadOnlyList<string> JobCommitShas(
        TaskInfo info, TaskSessionLog sessions, string jobId, string? watchPath, GitService git)
    {
        var aggregate = BuildJobCommitsAggregate(info, sessions, jobId, watchPath, git);
        var superseded = info.Commits
            .Where(TaskCommitSupersession.IsSuperseded)
            .Select(commit => commit.Sha)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return aggregate.Commits
            .Where(commit => !superseded.Contains(commit.Sha))
            .Select(c => c.Sha)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IResult DiffTextResult(GitDiffLookupResult result)
    {
        if (!result.Success)
            return Results.BadRequest(new { error = result.Error ?? "Could not load diff." });
        if (string.IsNullOrWhiteSpace(result.Diff))
            return Results.NoContent();
        return Results.Text(result.Diff, "text/plain", Encoding.UTF8);
    }

    /// <summary>
    /// Shapes a <see cref="GitFileContentResult"/> into the preview JSON the
    /// git-pane consumes: <c>{ content, isBinary }</c> on success, a 400 with an
    /// error message otherwise. A binary blob is a success with empty content +
    /// <c>isBinary: true</c> so the UI shows a "not previewable" note.
    /// </summary>
    private static IResult FileContentResult(GitFileContentResult result)
    {
        if (!result.Success)
            return Results.BadRequest(new { error = result.Error ?? "Could not load file." });
        return Results.Ok(new { content = result.Content, isBinary = result.IsBinary });
    }

    private static object DiffJsonResponse(string diff, string emptyReason)
    {
        var hasDiff = !string.IsNullOrWhiteSpace(diff);
        return new
        {
            diff,
            hasDiff,
            emptyReason = hasDiff ? null : emptyReason
        };
    }
}

public sealed record AppendIntegrationCommitRequest(string Sha);
