using AgentStudio.Operations.Contracts;
using AgentStudio.Operations.Server.Features.Dispatch;

namespace AgentStudio.Operations.Server.Features.Access;

/// <summary>
/// The Operations Server half of the out-of-band kill switch (security model
/// SM-2): the same revocation and cancellation as the control plane's call,
/// run on the host against the principal file, the revocation list in the data
/// directory, and the operations store.
/// </summary>
public static class OperatorRevocationCommand
{
    public static async Task<OperatorPrincipalRevocationResponse> RunAsync(
        string principalsFile, string dataDirectory, IReadOnlyCollection<string> operatorAgentIds, TimeProvider? clock = null)
    {
        var revoked = new OperationsAccess(principalsFile, Path.Combine(dataDirectory, OperationsAccess.RevocationsFileName))
            .RevokeOperators(operatorAgentIds, all: operatorAgentIds.Count == 0);
        var cancelled = await new OperationsCoordinator(new OperationsStore(dataDirectory), new NoPermits(), clock ?? TimeProvider.System)
            .CancelForPrincipalsAsync(revoked);
        return new OperatorPrincipalRevocationResponse(revoked, cancelled);
    }

    /// <summary>Cancellation never needs a permit; any other use stays closed.</summary>
    private sealed class NoPermits : IOperationPermitAuthority
    {
        public Task<DateTimeOffset?> ValidateAsync(string principalId, OperationCommand command, CancellationToken cancellationToken)
            => Task.FromResult<DateTimeOffset?>(null);
    }
}
