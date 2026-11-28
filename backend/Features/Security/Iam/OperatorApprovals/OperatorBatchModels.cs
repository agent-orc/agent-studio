namespace AgentStudio.Security;

public sealed record OperatorBatchItemRequest(string Card, string TargetLane, string Reason, string? Project = null, string? Reservation = null);
public sealed record SubmitOperatorBatchRequest(string Class, IReadOnlyList<OperatorBatchItemRequest> Items, string? InstructionSource);
public sealed record DecideOperatorBatchRequest(IReadOnlyList<int> Selected, bool Approve = true);
public sealed record OperatorBatchItem(int Index, OperatorBatchItemRequest Request, string Title, string? Revision,
    string? Reservation, string? Refusal, string State = "pending", string? ResultReason = null);
public sealed record OperatorBatch(string Id, string PrincipalId, string SessionId, string OwnerId, string Actor,
    string Class, string InstructionSource, DateTime SubmittedAt, DateTime ExpiresAt, IReadOnlyList<OperatorBatchItem> Items,
    string State = "pending", string? ApprovedBy = null, DateTime? DecidedAt = null)
{
    public string? PrincipalName { get; init; }
    public string Link => $"/workspace/settings/access?batch={Uri.EscapeDataString(Id)}";
}
