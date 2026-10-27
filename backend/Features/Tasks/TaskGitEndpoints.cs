

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

        // AGT-2828: what the commit candidate gate withheld from this card, and
        // why. Read-only projection of the durable marker in the job folder, so
        // the parked card can name the files instead of only the count.
        group.MapGet("/{jobId}/git/withheld-candidates", (string jobId, string? project, string? watchPath, TaskScannerService scanner, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound();
            var record = WithheldCommitCandidateStore.TryRead(info.FolderPath);
            return Results.Ok(new
            {
                withheld = record?.Count ?? 0,
                nothingCommitted = record?.NothingCommitted ?? false,
                decision = record?.Decision,
                candidates = record?.Candidates ?? [],
            });
        });

        // The operator action that ends the WEB-21 failure: after reviewing the
        // list above, commit it. The gate re-inspects the exact bytes and still
        // refuses anything that BLOCKS - explicit review clears warnings, never
        // secret material.
        group.MapPost("/{jobId}/git/withheld-candidates/commit", (string jobId, string? project, string? watchPath, CommitWithheldCandidatesRequest? req, GitService git, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var result = git.CommitWithheldCandidates(jobId, watchPath, req?.Message, req?.Paths);
            return result.Success
                ? Results.Ok(new { sha = result.Sha, committed = result.Gate?.IncludedPaths ?? [] })
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

        // Per-job commit details: the cached snapshot from task.json plus the
        // file list from the published commit facts (Git facts S3, AGT-3323),
        // so the detail view stays accurate even after history rewrites. No
        // read starts Git: a commit the store does not hold yet answers warming.
        group.MapGet("/{jobId}/commit", (string jobId, string? project, string? watchPath, TaskScannerService scanner,
            GitCardFactReader facts, ProjectSettingsService settings, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound();
            if (info.Commit == null) return Results.Ok(new { commit = (object?)null, files = Array.Empty<GitFileChange>() });

            var published = TaskGitFactReads.Facts(facts, info, settings);
            var fallback = info.Commit.Files.Select(p => new GitFileChange("?", p, 0, 0)).ToList();
            if (published is null)
                return GitFactsResults.Warming(new { commit = info.Commit, files = fallback }, null);
            var misses = new List<GitFactWork>();
            var fact = GitCardFactJoins.Facts([info.Commit.Sha], published, misses).FirstOrDefault();
            if (TaskGitFactReads.Queue(facts, info, misses))
                return GitFactsResults.Warming(new { commit = info.Commit, files = fallback }, published);
            var live = fact?.ToFileChanges() ?? [];
            var files = live.Count > 0 ? live : fallback;
            return GitFactsResults.Ok(new { commit = info.Commit, files }, published);
        });

        // Diff for the recorded commit, optionally scoped to one path, from the
        // commit's immutable diff object. Lets the detail view show the exact
        // changes the task produced even long after the working tree has moved on.
        group.MapGet("/{jobId}/commit/diff", (string jobId, string? project, string? watchPath, string? path,
            TaskScannerService scanner, GitCardFactReader facts, ProjectSettingsService settings,
            AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound(new { error = "Job not found" });
            if (info.Commit == null) return Results.NotFound(new { error = "This task has no recorded commit." });
            var published = TaskGitFactReads.Facts(facts, info, settings);
            if (published is null) return GitFactsResults.TextWarming(null);
            var misses = new List<GitFactWork>();
            var fact = GitCardFactJoins.Facts([info.Commit.Sha], published, misses).FirstOrDefault();
            if (fact is null && misses.Count == 0)
                return Results.BadRequest(new { error = "The recorded commit is not in the task repository." });
            var diff = fact is null ? null : TaskGitFactReads.CommitDiff(facts, published, fact, path, misses, out _);
            if (TaskGitFactReads.Queue(facts, info, misses) || diff is null) return GitFactsResults.TextWarming(published);
            return GitFactsResults.Text(diff, published);
        });

        // Job-level commit aggregation: every commit attributed to this
        // job across all of its runs (deduped by SHA), plus the
        // auto-commit when present. Drives the protocol-pane "Commits
        // and change set" panel - the user must be able to see what
        // landed without having to drill into individual runs first.
        // AGT-3323: run ranges, the run trailer index and commit facts come
        // from the store; the legacy attribution backfill this read used to
        // run (and persist) now runs in the Git fact producer.
        group.MapGet("/{jobId}/commits", (
            string jobId, string? project, string? watchPath,
            TaskScannerService scanner, GitCardFactReader facts, ProjectSettingsService settings,
            AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound(new { error = "Job not found" });

            var published = TaskGitFactReads.Facts(facts, info, settings);
            if (published is null) return GitFactsResults.Warming(new TaskCommitsAggregate(), null);
            var misses = new List<GitFactWork>();
            var aggregate = TaskGitFactReads.Aggregate(info, published, misses);
            return TaskGitFactReads.Queue(facts, info, misses)
                ? GitFactsResults.Warming(aggregate, published)
                : GitFactsResults.Ok(aggregate, published);
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
            TaskScannerService scanner, GitCardFactReader facts, ProjectSettingsService settings,
            AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound(new { error = "Job not found" });

            var published = TaskGitFactReads.Facts(facts, info, settings);
            if (published is null) return GitFactsResults.Warming(new { files = Array.Empty<GitFileChange>() }, null);
            var misses = new List<GitFactWork>();
            var aggregate = TaskGitFactReads.Aggregate(info, published, misses);
            var commits = GitCardFactJoins.Facts(GitCardFactJoins.ActiveShas(info, aggregate), published, misses);
            if (TaskGitFactReads.Queue(facts, info, misses))
                return GitFactsResults.Warming(new { files = Array.Empty<GitFileChange>() }, published);
            return GitFactsResults.Ok(new { files = GitCardFactJoins.AggregateFiles(commits) }, published);
        });

        // The default diff of a multi-commit card (git-pane applyCommitDefault):
        // served from the aggregate diff object the producer writes at
        // publication. A missing object answers warming and is queued with
        // priority.
        group.MapGet("/{jobId}/commits/diff", (
            string jobId, string? project, string? path, string? watchPath,
            TaskScannerService scanner, GitCardFactReader facts, ProjectSettingsService settings,
            AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound(new { error = "Job not found" });

            var warming = TaskGitFactReads.DiffJson("", "");
            var published = TaskGitFactReads.Facts(facts, info, settings);
            if (published is null) return GitFactsResults.Warming(warming, null);
            var misses = new List<GitFactWork>();
            var aggregate = TaskGitFactReads.Aggregate(info, published, misses);
            var members = GitCardFactJoins.Facts(GitCardFactJoins.ActiveShas(info, aggregate), published, misses);
            if (misses.Count == 0 && members.Count == 0)
                return GitFactsResults.Ok(TaskGitFactReads.DiffJson("", "This task has no attributed commits."), published);
            var tooLarge = false;
            var diff = misses.Count > 0
                ? null
                : TaskGitFactReads.AggregateDiff(facts, published, members, path, misses, out tooLarge);
            if (TaskGitFactReads.Queue(facts, info, misses) || diff is null) return GitFactsResults.Warming(warming, published);
            return GitFactsResults.Ok(TaskGitFactReads.DiffJson(diff, tooLarge
                ? TaskGitFactReads.TooLargeReason
                : "No diff for this path in the task's attributed commits."), published);
        });

        // File list for one of the job's commits. Validates the SHA is
        // actually a known commit on this job before reading its facts so the
        // endpoint can't be coaxed into showing arbitrary repo history.
        group.MapGet("/{jobId}/commits/{sha}/files", (
            string jobId, string sha, string? project, string? watchPath,
            TaskScannerService scanner, GitCardFactReader facts, ProjectSettingsService settings,
            AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound(new { error = "Job not found" });
            var published = TaskGitFactReads.Facts(facts, info, settings);
            if (published is null) return GitFactsResults.Warming(new { sha, files = Array.Empty<GitFileChange>() }, null);
            var misses = new List<GitFactWork>();
            var known = TaskGitFactReads.IsKnownCommit(info, published, sha, misses);
            if (known == false) return Results.NotFound(new { error = "Commit is not associated with this job." });
            var fact = known == true ? GitCardFactJoins.Facts([sha], published, misses).FirstOrDefault() : null;
            if (TaskGitFactReads.Queue(facts, info, misses))
                return GitFactsResults.Warming(new { sha, files = Array.Empty<GitFileChange>() }, published);
            return GitFactsResults.Ok(new { sha, files = fact?.ToFileChanges() ?? [] }, published);
        });

        // Diff of one of the job's commits, from its immutable diff object.
        group.MapGet("/{jobId}/commits/{sha}/diff", (
            string jobId, string sha, string? project, string? path, string? watchPath,
            TaskScannerService scanner, GitCardFactReader facts, ProjectSettingsService settings,
            AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound(new { error = "Job not found" });
            var warming = TaskGitFactReads.DiffJson("", "");
            var published = TaskGitFactReads.Facts(facts, info, settings);
            if (published is null) return GitFactsResults.Warming(warming, null);
            var misses = new List<GitFactWork>();
            var known = TaskGitFactReads.IsKnownCommit(info, published, sha, misses);
            if (known == false) return Results.NotFound(new { error = "Commit is not associated with this job." });
            var fact = known == true ? GitCardFactJoins.Facts([sha], published, misses).FirstOrDefault() : null;
            if (known == true && fact is null && misses.Count == 0)
                return Results.BadRequest(new { error = "The commit is not in the task repository." });
            var tooLarge = false;
            var diff = fact is null ? null : TaskGitFactReads.CommitDiff(facts, published, fact, path, misses, out tooLarge);
            if (TaskGitFactReads.Queue(facts, info, misses) || diff is null) return GitFactsResults.Warming(warming, published);
            return GitFactsResults.Ok(TaskGitFactReads.DiffJson(diff, tooLarge
                ? TaskGitFactReads.TooLargeReason
                : "No diff for this path in the selected commit."), published);
        });

        // File content at one of the job's commits, for the commit-mode md/html
        // preview (AGT-2008). Same known-commit gate as the diff endpoint so the
        // blob can only be read from a commit that actually belongs to this job.
        // The blob read stays a frozen S4 exception (historical file objects);
        // the known-commit gate joins published facts and starts no Git.
        group.MapGet("/{jobId}/commits/{sha}/file", (
            string jobId, string sha, string? project, string? path, string? watchPath,
            TaskScannerService scanner, GitCardFactReader facts, ProjectSettingsService settings, GitService git,
            AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound(new { error = "Job not found" });
            var published = TaskGitFactReads.Facts(facts, info, settings);
            if (published is null) return GitFactsResults.Warming(new { content = "", isBinary = false }, null);
            var misses = new List<GitFactWork>();
            var known = TaskGitFactReads.IsKnownCommit(info, published, sha, misses);
            if (known is null)
            {
                TaskGitFactReads.Queue(facts, info, misses);
                return GitFactsResults.Warming(new { content = "", isBinary = false }, published);
            }
            if (known == false)
                return Results.NotFound(new { error = "Commit is not associated with this job." });
            return FileContentResult(git.GetFileContentResult(jobId, watchPath, path, sha));
        });

        // Commit-provenance & landed-state (ASS-1724). Returns the persisted
        // append-only provenance facts plus the graph-derived landed-state
        // (on-branch-only / merged-to-develop / released-to-main), the landed
        // ladder (task/<id> -> develop @sha -> main @sha with "HEAD now") and
        // per-commit branch membership. AGT-3323 (Git facts S3): joined from
        // the published generation - ladder heads from its ref object IDs,
        // membership from the reachability sets of the integration and release
        // refs - so it tracks develop / main per generation without a single
        // rev-parse or merge-base on the request.
        group.MapGet("/{jobId}/provenance", (
            string jobId, string? project, string? watchPath,
            TaskScannerService scanner, GitCardFactReader facts, ProjectSettingsService settings,
            AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound(new { error = "Job not found" });
            var published = TaskGitFactReads.Facts(facts, info, settings);
            return published is null
                ? GitFactsResults.Warming(new { branch = WorktreeTaskLifecycle.BranchFor(info.Id) }, null)
                : GitFactsResults.Ok(GitCardFactJoins.Provenance(info, published), published);
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

        group.MapPost("/{jobId}/open-in-vscode", (string jobId, string? project, string? watchPath, GitService git, AgentStudio.Registry.ProjectRegistry projects, IConfiguration configuration) =>
        {
            if (!SecurityProfiles.IsLocal(configuration))
                return Results.StatusCode(StatusCodes.Status404NotFound);
            watchPath = ResolveWatchPath(projects, project, watchPath);
            return git.OpenInVsCode(jobId, watchPath, out var error)
                ? Results.Ok()
                : Results.BadRequest(new { error });
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Preview);
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
}

public sealed record AppendIntegrationCommitRequest(string Sha);
