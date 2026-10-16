using static AgentStudio.Tasks.TaskEndpointHelpers;

namespace AgentStudio.Tasks;

/// <summary>
/// Endpoints around the per-job review-evidence file
/// (<c>results/review-evidence.jsonl</c>). The findings themselves are
/// already exposed inside <see cref="TaskDetail.ReviewEvidence"/> by
/// <c>GET /api/tasks/{jobId}</c>; these routes are the small set of
/// mutations the UI needs:
///
/// - acknowledge / un-acknowledge a finding,
/// - turn a finding into a queued follow-up task in the same project.
///
/// All mutations are append-only writes to the JSONL file. Readers fold
/// the file into latest-per-id, so re-acknowledging or re-creating a
/// follow-up just appends a new row with the updated fields. The file
/// stays diff-friendly and the orchestrator never needs an exclusive
/// writer lock.
/// </summary>
public static class TaskReviewEvidenceEndpoints
{
    public static void MapTaskReviewEvidenceEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/{jobId}/preview-comments",
            (string jobId, string? project, string? watchPath, PreviewCommentRequest body,
                HttpContext context, TaskScannerService scanner, TimelineLog timeline,
                TimeProvider clock, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var info = scanner.FindJob(jobId, watchPath);
            if (info is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(body.Comment) || body.Comment.Length > 4000
                || string.IsNullOrWhiteSpace(body.PreviewId) || body.PreviewId.Length > 200)
                return Results.BadRequest(new { error = "A preview id and a comment of up to 4000 characters are required." });
            var actor = context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is HumanPrincipal human
                ? TimelineActors.Human(human.User.Id)
                : TimelineActors.Human(context.Items["ClientId"] as string ?? string.Empty);
            var recorded = timeline.Append(info.FolderPath, new TimelineEvent
            {
                Ts = clock.GetUtcNow().UtcDateTime,
                Kind = "preview-comment",
                Actor = actor,
                Summary = body.Comment.Trim(),
                PayloadRef = body.PreviewId.Trim()
            });
            return recorded ? Results.Ok(new { previewId = body.PreviewId, actor }) : Results.StatusCode(500);
        });

        // POST /api/tasks/{jobId}/review-evidence/{evidenceId}/acknowledge
        // Body: { "acknowledged": true|false } — defaults to true.
        group.MapPost("/{jobId}/review-evidence/{evidenceId}/acknowledge",
            (string jobId, string evidenceId, string? project, string? watchPath, AcknowledgeEvidenceRequest? body, TaskScannerService scanner, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound();

            var existing = ReviewEvidenceLog.ReadLatestPerId(info.FolderPath)
                .FirstOrDefault(e => e.Id == evidenceId);
            if (existing == null) return Results.NotFound(new { error = $"No evidence with id '{evidenceId}'." });

            var updated = existing with
            {
                Acknowledged = body?.Acknowledged ?? true,
                CreatedAt = DateTime.UtcNow
            };
            ReviewEvidenceLog.Append(info.FolderPath, updated);
            return Results.Ok(updated);
        });

        // POST /api/tasks/{jobId}/review-evidence/{evidenceId}/follow-up
        // Body: { "title"?: string, "targetState"?: string }
        // Creates a normal queued task in the same project, prefilled with
        // the finding's title + body + linked artifacts/file refs. Default
        // landing lane is 1-preparation so the user reviews and promotes.
        group.MapPost("/{jobId}/review-evidence/{evidenceId}/follow-up",
            (string jobId, string evidenceId, string? project, string? watchPath, CreateFollowupFromEvidenceRequest? body, TaskScannerService scanner, TaskMutationService mutations, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound();

            var entry = ReviewEvidenceLog.ReadLatestPerId(info.FolderPath)
                .FirstOrDefault(e => e.Id == evidenceId);
            if (entry == null) return Results.NotFound(new { error = $"No evidence with id '{evidenceId}'." });

            var targetState = body?.TargetState switch
            {
                TaskStates.Preparation => TaskStates.Preparation,
                TaskStates.Ready => TaskStates.Ready,
                TaskStates.Backlog => TaskStates.Backlog,
                _ => TaskStates.Preparation
            };

            var title = !string.IsNullOrWhiteSpace(body?.Title)
                ? body!.Title!
                : $"Follow-up: {entry.Title}";

            var prompt = BuildFollowupPrompt(jobId, info.Title, entry);

            var req = new CreateTaskRequest
            {
                Title = title,
                Agent = info.Agent,
                CliType = info.CliType,
                WatchPath = info.WatchPath,
                PromptMarkdown = prompt,
                TargetState = targetState,
                TaskType = TaskTypes.Chore,
                OwnerClientId = info.OwnerClientId
            };

            var newId = mutations.CreateJob(req);
            if (newId is null) return Results.Conflict(new { error = "Failed to create follow-up task (slug collision or invalid input)." });
            var created = scanner.FindJob(newId, info.WatchPath);

            // Stamp the new job id back onto the source finding so the panel
            // can render a "follow-up: <id>" chip and so a second click does
            // not silently spawn a second task.
            var stamped = entry with
            {
                FollowupJobId = newId,
                CreatedAt = DateTime.UtcNow
            };
            ReviewEvidenceLog.Append(info.FolderPath, stamped);

            return Results.Ok(new CreateFollowupFromEvidenceResponse
            {
                JobId = newId,
                TaskKey = created?.Key,
                TargetState = targetState
            });
        });
    }

    public sealed record PreviewCommentRequest(string PreviewId, string Comment);

    private static string BuildFollowupPrompt(string sourceJobId, string sourceTitle, ReviewEvidenceEntry entry)
    {
        var lines = new List<string>
        {
            $"# Follow-up from review evidence",
            "",
            $"This task was created from a finding on the job **{sourceTitle}** (`{sourceJobId}`).",
            "",
            $"- Source: `{entry.Source}`",
            $"- Severity: `{entry.Severity}`",
            $"- Evidence id: `{entry.Id}`",
            $"- Recorded at: {entry.CreatedAt:yyyy-MM-ddTHH:mm:ssZ}",
            ""
        };

        if (entry.RunIndex.HasValue)
        {
            lines.Add($"- Run index: {entry.RunIndex.Value}");
            lines.Add("");
        }

        lines.Add("## Finding");
        lines.Add("");
        lines.Add($"**{entry.Title}**");
        if (!string.IsNullOrWhiteSpace(entry.Body))
        {
            lines.Add("");
            lines.Add(entry.Body!);
        }
        lines.Add("");

        if (entry.FileRefs.Count > 0)
        {
            lines.Add("## Files referenced");
            lines.Add("");
            foreach (var f in entry.FileRefs) lines.Add($"- `{f}`");
            lines.Add("");
        }

        if (entry.Artifacts.Count > 0)
        {
            lines.Add("## Linked artifacts");
            lines.Add("");
            foreach (var a in entry.Artifacts) lines.Add($"- `{a}`");
            lines.Add("");
        }

        lines.Add("## Constraints");
        lines.Add("");
        lines.Add("- Verify and fix only the finding scope above. Do not expand the work without flagging it first.");
        lines.Add("- When you finish, write a short note back into the source job's `results/review-evidence.jsonl` referencing this follow-up.");
        lines.Add("");

        return string.Join("\n", lines);
    }
}

/// <summary>
/// Body for <c>POST /api/tasks/{jobId}/review-evidence/{evidenceId}/acknowledge</c>.
/// Optional; an empty body or omitted <c>acknowledged</c> field defaults to
/// <c>true</c> so the simple "I read this" click does not need a payload.
/// </summary>
public record AcknowledgeEvidenceRequest
{
    public bool? Acknowledged { get; init; }
}
