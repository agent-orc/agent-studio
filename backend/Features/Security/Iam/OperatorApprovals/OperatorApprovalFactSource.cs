using AgentStudio.Operator;

namespace AgentStudio.Security;

public sealed class OperatorApprovalFactSource(AccessSecurityStore store) : IOperatorDecisionFactSource
{
    public IReadOnlyList<string> Types { get; } = [OperatorDecisionTypes.OperatorApprovalPending];
    public IReadOnlyList<OperatorDecisionFact> Collect(DateTime nowUtc)
        => store.ListOperatorBatches().Where(batch => batch.State == "pending").Select(batch =>
            new OperatorDecisionFact(OperatorDecisionTypes.OperatorApprovalPending, $"operator-approval.pending|{batch.Id}",
                null, null, $"{batch.Actor} asks to run {batch.Class} for {batch.Items.Count} cards.", batch.SubmittedAt,
                [new("Confirm batch", batch.Link)], Title: $"Confirm {batch.Items.Count} operator items"))
            .Concat(store.ListOperatorApprovals().Where(approval => OperatorApprovalPolicy.State(approval, nowUtc) == OperatorApprovalStates.Pending)
                .Select(approval => new OperatorDecisionFact(OperatorDecisionTypes.OperatorApprovalPending,
                    $"operator-approval.pending|{approval.Id}", null, null, approval.Reason ?? approval.OperationId, approval.RequestedAt,
                    [new("Approval request", $"/workspace/settings/access?approval={Uri.EscapeDataString(approval.Id)}")],
                    Title: $"Approve {approval.OperationId}"))).ToList();
}
