namespace AgentStudio.Docs;

/// <summary>
/// The card that runs a Dossier revision. <paramref name="Available"/> is false
/// when no source concept card can take a follow-up round; <paramref name="Reason"/>
/// then says why, and nothing may be written for the rework request.
/// </summary>
public sealed record WorkbenchRevisionCard(
    bool Available,
    string? Key,
    string? JobId,
    string? Title,
    string? Lane,
    string? Mode,
    string? Reason)
{
    public static WorkbenchRevisionCard Unavailable(string reason) =>
        new(false, null, null, null, null, null, reason);
}

/// <summary>
/// What happened to the follow-up round a confirmed rework request asked for:
/// <c>started</c>, <c>queued</c> (with <paramref name="QueueReason"/> and a
/// readable <paramref name="Detail"/>), <c>saved</c> (the prompt is on the card
/// but the card could not be promoted), or <c>failed</c>.
/// </summary>
public sealed record WorkbenchRevisionRoundResult(
    string Status,
    string TaskKey,
    string? Title,
    string? QueueReason,
    string? Detail,
    int? QueuePosition = null);

/// <summary>
/// Picks the source concept card of a Dossier (AGT-3703). The first source key
/// that names a live card of the same project wins; an archived source card is
/// reported instead of silently falling through to another card.
/// </summary>
public static class WorkbenchRevisionCardPolicy
{
    public static WorkbenchRevisionCard Resolve(
        string projectName, IReadOnlyList<string> sourceTaskKeys, IReadOnlyList<TaskInfo> tasks)
    {
        var keys = sourceTaskKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (keys.Length == 0)
            return WorkbenchRevisionCard.Unavailable(
                "This Dossier names no source concept card, so no card can run the revision.");

        string? archived = null;
        foreach (var key in keys)
        {
            var matches = tasks
                .Where(task => string.Equals(task.ProjectName, projectName, StringComparison.OrdinalIgnoreCase)
                    && (string.Equals(task.Key, key, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(task.Id, key, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            var live = matches.Where(task => task.State != TaskStates.Archive).ToArray();
            if (live.Length == 1)
            {
                var card = live[0];
                return new WorkbenchRevisionCard(true, card.Key ?? card.Id, card.Id, card.Title,
                    card.State, card.Mode, null);
            }
            if (live.Length > 1)
                return WorkbenchRevisionCard.Unavailable(
                    $"The source card key {key} names more than one card in {projectName}.");
            if (matches.Length > 0) archived ??= key;
        }

        return WorkbenchRevisionCard.Unavailable(archived != null
            ? $"The source concept card {archived} is archived. Restore it to run the revision."
            : $"The source concept card {keys[0]} was not found in {projectName}.");
    }

    /// <summary>
    /// The round prompt when the caller sent none. The browser normally sends
    /// one with option labels; this keeps an API caller's request actionable.
    /// </summary>
    public static string FallbackPrompt(
        string workbenchId, string? request, IReadOnlyList<WorkbenchDecisionResponse> responses)
    {
        var lines = new List<string>
        {
            $"Revise Dossier {workbenchId}.",
            "",
            "The owner requested a new Dossier revision. Revise index.html and workbench.json through the normal delivery path.",
            "Keep the pending rework receipt in workbench.json, and mark the owner's recorded selections below as the checked options in the revised decision markup.",
        };
        if (!string.IsNullOrWhiteSpace(request))
            lines.AddRange(["", "Owner request:", request.Trim()]);
        var recorded = responses
            .Where(response => response.SelectedOptionIds.Count > 0 || !string.IsNullOrWhiteSpace(response.Comment))
            .Select(response => $"- {response.DecisionId}: {string.Join(", ", response.SelectedOptionIds)}"
                + (string.IsNullOrWhiteSpace(response.Comment) ? "" : $". Note: {response.Comment.Trim()}"))
            .ToArray();
        if (recorded.Length > 0)
            lines.AddRange(["", "Owner selections:", .. recorded]);
        return string.Join('\n', lines);
    }

    /// <summary>The readable reason a queued round waits; the wire value stays in the result.</summary>
    public static string QueueDetail(string? reason) => reason switch
    {
        FollowUpQueueReasons.ProjectBusy =>
            "Another card holds the project's runner slot. The round starts when the slot is free.",
        FollowUpQueueReasons.LaneNotRunnable =>
            "The card moved to Ready. The next pickup runs the round.",
        FollowUpQueueReasons.DeliveryUnderReview =>
            "The card's delivery is under review. The round runs after the review.",
        FollowUpQueueReasons.RemoteExecution =>
            "The project runs on a remote runner. The round starts when that runner claims the card.",
        _ => "The round waits for the next runner pickup.",
    };
}

/// <summary>
/// Hands a confirmed rework request to the source concept card as an ordinary
/// follow-up round, so the run starts or queues through the same admission as
/// any operator continue instead of depending on an orchestrator chat turn.
/// </summary>
public sealed class WorkbenchRevisionRoundService(TaskScannerService scanner, TaskRunnerService runner)
{
    public WorkbenchRevisionCard Target(string projectName, IReadOnlyList<string> sourceTaskKeys) =>
        WorkbenchRevisionCardPolicy.Resolve(projectName, sourceTaskKeys, scanner.ScanAllJobsWithArchive());

    public async Task<WorkbenchRevisionRoundResult> StartAsync(
        string projectName, WorkbenchRevisionCard card, string prompt, string actor,
        string? cliType, string? model, string? thinkingLevel, CancellationToken ct)
    {
        var key = card.Key ?? card.JobId ?? "";
        if (!card.Available || card.JobId == null)
            return new("failed", key, card.Title, null, card.Reason);
        // Job ids are only unique per project; resolve inside the Dossier's project.
        var info = scanner.ScanAllJobs().FirstOrDefault(task => task.Id == card.JobId
            && string.Equals(task.ProjectName, projectName, StringComparison.OrdinalIgnoreCase));
        if (info == null)
            return new("failed", key, card.Title, null, $"The source concept card {key} was not found.");

        // A Dossier revision is concept work by definition, so the card keeps
        // its read-only mode instead of tripping the implementation-prompt guard.
        var modeOverride = info.Mode is TaskModes.Concept or TaskModes.Planning ? info.Mode : null;
        try
        {
            var response = await runner.ContinueJobAsync(
                info.Id, prompt, info.WatchPath,
                string.IsNullOrWhiteSpace(model) ? null : model.Trim(),
                string.IsNullOrWhiteSpace(cliType) ? null : cliType.Trim(),
                string.IsNullOrWhiteSpace(thinkingLevel) ? null : thinkingLevel.Trim(),
                ContinueModes.Continue, modeOverride,
                author: TimelineActors.Human(actor), ct: ct,
                reason: "dossier-rework", triggeredBy: actor);
            return response.Status switch
            {
                "started" => new("started", key, info.Title, null, "The revision round is running."),
                "queued" => new("queued", key, info.Title, response.Queued?.Reason,
                    WorkbenchRevisionCardPolicy.QueueDetail(response.Queued?.Reason),
                    response.Queued?.Position),
                _ => new("saved", key, info.Title, response.Queued?.Reason,
                    "The request is saved on the card, but the card could not be moved to Ready."),
            };
        }
        catch (TaskOperationException ex)
        {
            return new("failed", key, info.Title, null, ex.Message);
        }
        catch (ExecutionAdmissionDeniedException ex)
        {
            return new("failed", key, info.Title, null, ex.Message);
        }
    }
}
