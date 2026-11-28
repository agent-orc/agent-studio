using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.Operator;
using AgentStudio.Registry;
using AgentStudio.Review;
using AgentStudio.Shared;
using AgentStudio.Tasks;

namespace AgentStudio.Security;

/// <summary>ES-D10 A: typed operations run in Studio, through the ordinary guarded move path.</summary>
public sealed class OperatorBatchService(AccessSecurityStore store, TaskScannerService scanner,
    TaskIntegrationStatusService integration, ProjectRegistry projects, TimelineLog timeline,
    TaskTransitionService transitions, RunLeaseService leases, AttemptAuthorityService authority,
    AgentStudio.Pipeline.BatchIsolationGuard isolation, ILogger<OperatorBatchService> logger)
{
    public OperatorBatch Submit(ServiceAccountPrincipal principal, SubmitOperatorBatchRequest request)
    {
        store.RequireBatchSession(principal.AccountId, principal.TokenId, request.Class);
        if (request.Items is null || request.Items.Count is < 1 or > 25)
            throw new SecurityOperationException(400, "batch-size-invalid", "A batch contains 1 to 25 items.");
        if (request.Items.Any(item => item is null || string.IsNullOrWhiteSpace(item.Card) || item.Card.Length > 200
            || item.Project?.Length > 200 || item.Reason?.Length > 2000 || item.Reservation?.Length > 1000))
            throw new SecurityOperationException(400, "batch-item-invalid", "Each item needs a card and bounded reason and reservation text.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = request.Items.Select((item, index) =>
        {
            var path = WatchPath(item.Project);
            var card = item.Project is not null && path is null ? null : scanner.FindJob(item.Card, path);
            if (card is null || !CanWrite(principal.AccountId, card.ProjectName))
                return new OperatorBatchItem(index, item, item.Card, null, null, "Card not found or outside this session's projects.");
            var qualified = Inspect(request.Class, item, card);
            return qualified with { Index = index, Refusal = !seen.Add(card.TaskKey) ? "Duplicate card in batch." : qualified.Refusal };
        }).ToList();
        return store.SubmitOperatorBatch(principal, request, items);
    }

    private string? WatchPath(string? project) => project is null ? null
        : (projects.FindByIdOrDisplayName(project) ?? projects.FindByShortCode(project) ?? projects.FindByStorageLocation(project))?.StorageLocation;

    private bool CanWrite(string agentId, string project)
    {
        var (subject, bindings) = store.PrincipalBindings(IamPrincipalKind.ServiceAccount, agentId);
        return PermissionEvaluator.Evaluate(IamPermissions.ProjectCardsWrite, subject, bindings, PermissionTarget.ForProject(project),
            (left, right) => ProjectAccessAuthorization.SameProject(left, right, projects)).Allowed;
    }

    private OperatorBatchItem Inspect(string classId, OperatorBatchItemRequest item, TaskInfo card)
    {
        var review = ReviewProjectionReader.Read(card, timeline.ReadAll(card.FolderPath), null);
        var status = integration.BuildForCompletion(card);
        var reservation = OneLine(string.Join("; ", review.BlockingAspects.Select(aspect => aspect.Reason)));
        if (string.IsNullOrWhiteSpace(reservation)) reservation = OneLine(item.Reservation);
        string? refusal = !CompletionContractPolicy.IsUsableReason(item.Reason?.Trim()) ? "A written reason is required." : null;
        if (classId == "cards.accept-override")
        {
            refusal ??= item.TargetLane != TaskStates.Completed ? "This class only accepts cards." : null;
            refusal ??= card.State != TaskStates.HumanReview ? "The card must be in Human review." : null;
            refusal ??= status.Status != IntegrationStatuses.Integrated || status.ReachUnavailable ? "The delivery is not integrated." : null;
            refusal ??= review.Attempts.Count == 0 ? "The review has not run." : null;
            refusal ??= string.IsNullOrWhiteSpace(reservation) ? "Supply the review's reservation in one line." : null;
        }
        else
        {
            refusal ??= item.TargetLane != TaskStates.Archive ? "This class only archives cards." : null;
            refusal ??= card.State != TaskStates.Completed ? "Archive override requires a completed card." : null;
            // Fail closed if reachability is unavailable or any integrated delivery is recorded.
            refusal ??= status.ReachUnavailable == true || IntegrationStatuses.IsMerged(status.Status) || status.Released == true
                || status.Repositories.Any(repository => repository.OnIntegrationBranch || repository.OnReleaseBranch
                    || repository.Commits.Any(commit => commit.OnIntegrationBranch || commit.OnReleaseBranch)) == true
                || card.Commits.Any(commit => commit.IntegrationRule == CommitIntegrationRules.IntegratedByContent)
                ? "A protected-branch delivery exists or could not be ruled out." : null;
        }
        var snapshot = JsonSerializer.Serialize(new { card.TaskKey, revision = OperatorCoordinationStore.Revision(card.FolderPath), card.State,
            review, integration = status });
        return new(0, item with { Card = card.Key ?? card.Id, Project = card.ProjectName }, card.Title,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot))), reservation, refusal);
    }

    private static string MoveRefusal(IResult result, int status)
    {
        if ((result as Microsoft.AspNetCore.Http.IValueHttpResult)?.Value is { } value)
        {
            var body = JsonSerializer.SerializeToElement(value);
            if (body.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String) return message.GetString()!;
            if (body.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String) return error.GetString()!;
        }
        return $"Move refused ({status}).";
    }

    private static string? OneLine(string? value) => value is null ? null : string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public async Task<OperatorBatch> DecideAsync(string id, DecideOperatorBatchRequest request, HumanPrincipal owner,
        HttpContext context, CancellationToken ct)
    {
        var batch = store.DecideOperatorBatch(id, request, owner);
        if (!request.Approve) return batch;
        using var scope = OperatorActorScope.Enter(new(batch.PrincipalId, batch.SessionId, batch.OwnerId)
            { ApprovalId = batch.Id, ApprovedBy = owner.User.Id, BatchId = batch.Id });
        // Do not forward the deciding human to the move path. The service account is the actor.
        var execution = new DefaultHttpContext { RequestServices = context.RequestServices };
        execution.Items[AccessSecurityMiddleware.ServiceAccountPrincipalItem] = new ServiceAccountPrincipal(batch.PrincipalId,
            batch.PrincipalId, batch.SessionId, Operator: OperatorActorScope.Current);
        var (subject, bindings) = store.PrincipalBindings(IamPrincipalKind.ServiceAccount, batch.PrincipalId);
        execution.Items[AccessSecurityMiddleware.RequestAccessItem] = new RequestAccess(subject, bindings,
            IamPermissions.ProjectCardsWrite, (left, right) => ProjectAccessAuthorization.SameProject(left, right, projects),
            IamRoles.OperatorAgent, new HashSet<string> { IamPermissions.ProjectCardsOverride });
        OperatorBatch? finished = null;
        try
        {
            foreach (var item in batch.Items.Where(item => request.Selected.Contains(item.Index) && item.Refusal is null))
            {
                bool executed;
                string reason;
                try
                {
                    var result = await TaskCrudEndpoints.MoveOperatorWithRunIntentAsync(item.Request.Card, WatchPath(item.Request.Project),
                        new MoveJobRequest { TargetState = item.Request.TargetLane, Reason = item.Request.Reason,
                            OperatorOverride = batch.Class == "cards.accept-override", ArchiveOverride = batch.Class == "cards.archive-override" },
                        execution, transitions, scanner, leases, authority, timeline, isolation, CancellationToken.None,
                        approvalItemGuard: card =>
                        {
                            var current = Inspect(batch.Class, item.Request, card);
                            var refusal = current.Revision != item.Revision ? "Card changed since submission."
                                : current.Refusal ?? (!CanWrite(batch.PrincipalId, card.ProjectName) ? "The session no longer holds this project." : null)
                                ?? store.ConsumeBatchItem(batch, card.ProjectName);
                            return refusal is null ? null : Results.Conflict(new { error = "batch-item-skipped", message = refusal });
                        });
                    var status = (result as Microsoft.AspNetCore.Http.IStatusCodeHttpResult)?.StatusCode ?? 200;
                    executed = status is >= 200 and < 300;
                    reason = executed ? item.Request.Reason : MoveRefusal(result, status);
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Operator batch {BatchId} item {Index} failed", id, item.Index);
                    executed = false;
                    reason = "Execution failed; inspect the card timeline before submitting again.";
                }
                // Result persistence is outside the move catch: recording failures cannot undo a move.
                batch = store.RecordBatchItem(id, item.Index, executed, reason);
            }
        }
        finally { finished = store.FinishOperatorBatch(id); }
        return finished;
    }
}
