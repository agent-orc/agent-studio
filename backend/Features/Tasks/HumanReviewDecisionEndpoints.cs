using AgentStudio.Prompts;
using AgentStudio.Runner;
using AgentStudio.Security;
using AgentStudio.Shared;
using static AgentStudio.Tasks.TaskEndpointHelpers;

namespace AgentStudio.Tasks;

/// <summary>
/// The Human Review decision (AGT-3670): accept a delivered card, or send it
/// back to Ready with a note that becomes the next run's follow-up prompt.
/// Both routes declare <see cref="IamPermissions.ProjectCardsReview"/>, the one
/// write the Reviewer role holds, so a reviewer decides the review and nothing
/// else. Each decision lands on the card timeline and in the control-plane
/// audit with the deciding person as actor.
/// </summary>
public static class HumanReviewDecisionEndpoints
{
    public const int MaxNoteLength = 4000;
    public const string AcceptOperation = "card.review.accept";
    public const string SendBackOperation = "card.review.send-back";
    public const string SendBackReason = "human-review-send-back";

    public static void MapHumanReviewDecisionEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/{jobId}/human-review/accept", async (string jobId, string? project, string? watchPath,
            HumanReviewDecisionRequest? request, HttpContext context,
            TaskTransitionService transitions, TaskScannerService scanner, RunLeaseService leases,
            AttemptAuthorityService authority, TimelineLog timeline, AccessSecurityStore security,
            AgentStudio.Registry.ProjectRegistry projects, AgentStudio.Pipeline.BatchIsolationGuard isolation,
            CancellationToken ct) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var note = request?.Note?.Trim();
            if (note?.Length > MaxNoteLength) return NoteTooLong();
            var card = scanner.FindJob(jobId, watchPath);
            if (card is null) return Results.NotFound();
            if (card.State != TaskStates.HumanReview) return NotInHumanReview(card.State);

            var moved = await TaskCrudEndpoints.MoveOperatorWithRunIntentAsync(jobId, watchPath,
                new MoveJobRequest { TargetState = TaskStates.Completed, Reason = string.IsNullOrEmpty(note) ? null : note },
                context, transitions, scanner, leases, authority, timeline, isolation, ct,
                expectedState: TaskStates.HumanReview);
            if (moved is not IStatusCodeHttpResult { StatusCode: StatusCodes.Status200OK }) return moved;

            var folder = scanner.FindJob(jobId, watchPath)?.FolderPath ?? card.FolderPath;
            var reviewer = Reviewer(context);
            Record(timeline, security, folder, card, reviewer, "accept", AcceptOperation, "accepted",
                string.IsNullOrEmpty(note) ? $"{reviewer.Name} accepted the delivery." : $"{reviewer.Name} accepted the delivery: {note}",
                note);
            return Results.Ok(new HumanReviewDecisionResponse("accept", TaskStates.Completed, null));
        }).RequirePermission(IamPermissions.ProjectCardsReview)
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Start);

        group.MapPost("/{jobId}/human-review/send-back", async (string jobId, string? project, string? watchPath,
            HumanReviewDecisionRequest? request, HttpContext context,
            TaskRunnerService runner, TaskScannerService scanner, TimelineLog timeline, AccessSecurityStore security,
            MonolithOrchestrationStore orchestration, AgentStudio.Registry.ProjectRegistry projects,
            AgentStudio.Pipeline.BatchIsolationGuard isolation, CancellationToken ct) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var note = request?.Note?.Trim();
            if (string.IsNullOrEmpty(note))
                return Results.BadRequest(new { error = "note-required", message = "Write what the next run should change." });
            if (note.Length > MaxNoteLength) return NoteTooLong();

            var reviewer = Reviewer(context);
            // The claim gate keeps the card in Human Review until the follow-up
            // is queued: from Human Review a follow-up never starts a process,
            // so the reviewer gains no run control.
            await LeaseEndpoints.ClaimGate.WaitAsync(ct);
            try
            {
                var card = scanner.FindJob(jobId, watchPath);
                if (card is null) return Results.NotFound();
                if (card.State != TaskStates.HumanReview) return NotInHumanReview(card.State);
                if (isolation.Find(card) is { } isolating)
                    return Results.Json(AgentStudio.Pipeline.BatchIsolationGuardPolicy.Body(isolating),
                        statusCode: StatusCodes.Status409Conflict);
                var openDecisions = orchestration.OpenLaneDecisionIds(card.ProjectName, card.Id);
                ContinueJobResponse queued;
                try
                {
                    queued = await runner.ContinueJobAsync(jobId, FollowUpPrompt(reviewer.Name, note), watchPath,
                        mode: ContinueModes.Continue, author: reviewer.TimelineActor, ct: ct,
                        reason: SendBackReason, triggeredBy: reviewer.Id);
                }
                catch (TaskOperationException ex)
                {
                    return Results.Json(new { error = ex.Code ?? "send-back-refused", message = ex.Message },
                        statusCode: ex.Status);
                }
                var current = scanner.FindJob(jobId, watchPath);
                if (current is not null)
                    await orchestration.SupersedeRunsAsync(card.ProjectName, card.Id, openDecisions, current.State,
                        SendBackReason, ct);
                Record(timeline, security, current?.FolderPath ?? card.FolderPath, card, reviewer, "send-back",
                    SendBackOperation, "sent-back", $"{reviewer.Name} sent the card back: {note}", note);
                return Results.Ok(new HumanReviewDecisionResponse("send-back", current?.State ?? TaskStates.Ready,
                    queued.Status));
            }
            finally { LeaseEndpoints.ClaimGate.Release(); }
        }).RequirePermission(IamPermissions.ProjectCardsReview)
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Continue);
    }

    /// <summary>The follow-up the next run receives; the reviewer's note is quoted unchanged.</summary>
    public static string FollowUpPrompt(string reviewer, string note)
        => $"{reviewer} reviewed this delivery in Human Review and sent the card back with this note:"
            + PromptText.NewLine + PromptText.NewLine
            + string.Join(PromptText.NewLine, note.Split('\n').Select(line => "> " + line.TrimEnd('\r')))
            + PromptText.NewLine + PromptText.NewLine
            + "Address the note in this run, then deliver again.";

    private static void Record(TimelineLog timeline, AccessSecurityStore security, string folder, TaskInfo card,
        ReviewerIdentity reviewer, string decision, string operation, string outcome, string summary, string? note)
    {
        var details = new Dictionary<string, string> { ["decision"] = decision, ["reviewer"] = reviewer.Name };
        if (!string.IsNullOrEmpty(note)) details["note"] = note;
        timeline.Append(folder, TimelineEventKinds.HumanReviewDecided, reviewer.TimelineActor, summary, details: details);
        security.ControlPlaneAudit.Append(new ControlPlaneAuditRecord(
            ControlPlaneAuditStreams.Review, reviewer.AuditActor, operation, outcome,
            Target: $"project:{card.ProjectName}/task:{card.Key ?? card.Id}",
            ParameterDigest: string.IsNullOrEmpty(note) ? null : ControlPlaneAuditLog.Sha256Hex(note),
            Details: new { decision, taskKey = card.Key ?? card.Id, project = card.ProjectName, reviewer = reviewer.Name,
                noteLength = note?.Length ?? 0 }));
    }

    /// <summary>
    /// The deciding person. The timeline shows the display name, as every
    /// operator move does; the audit names the stable user id.
    /// </summary>
    private static ReviewerIdentity Reviewer(HttpContext context)
    {
        if (context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is HumanPrincipal human)
            return new(human.User.Id, human.User.DisplayName, TimelineActors.Human(human.User.DisplayName),
                TimelineActors.Human(human.User.Id));
        var actor = TaskCrudEndpoints.OperatorActor(context);
        var name = TimelineActors.IsHuman(actor) ? actor["human:".Length..] : actor;
        return new(name, string.IsNullOrWhiteSpace(name) ? "The operator" : name, actor, actor);
    }

    private static IResult NotInHumanReview(string state) => Results.Json(new
    {
        error = "not-in-human-review",
        message = "Only a card in Human Review can be accepted or sent back.",
        state,
    }, statusCode: StatusCodes.Status409Conflict);

    private static IResult NoteTooLong() => Results.BadRequest(new
    {
        error = "note-too-long",
        message = $"Keep the note under {MaxNoteLength} characters.",
    });

    private sealed record ReviewerIdentity(string Id, string Name, string TimelineActor, string AuditActor);
}

public sealed record HumanReviewDecisionRequest(string? Note);

/// <summary><paramref name="FollowUp"/> is the queued follow-up status of a send back (queued or saved).</summary>
public sealed record HumanReviewDecisionResponse(string Decision, string State, string? FollowUp);
