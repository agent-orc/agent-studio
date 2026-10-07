using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>Projects a proved R5 receipt into the existing metadata-only registry contract.</summary>
public static class RepositoryCredentialRegistryProjection
{
    public static CredentialRegistryRecordDto CompletedDeployKey(
        CredentialRegistryRecordDto current,
        RepositoryRotationRequest request,
        RepositoryRotationReceipt receipt,
        TimeProvider clock)
    {
        if (!receipt.Completed || receipt.Candidate is null || receipt.NewGitHubKeyId is null
            || current.Kind != "github_deploy_key" || current.Generation != request.ExpectedGeneration
            || receipt.ExpectedGeneration != request.ExpectedGeneration
            || receipt.ProvisioningCredentialId != request.ProvisioningCredentialId)
            throw new InvalidOperationException("A current, completed deploy-key proof is required for registry promotion.");
        var now = clock.GetUtcNow().UtcDateTime;
        return current with {
            Generation = receipt.Candidate.Generation,
            Supersedes = current.Generation,
            PublicFingerprint = receipt.Candidate.Fingerprint,
            GitHubKeyId = receipt.NewGitHubKeyId,
            ProvisioningCredentialId = receipt.ProvisioningCredentialId,
            RepositoryPurpose = request.Purpose,
            RepositoryWriteGrant = request.RequiresPush,
            LastOperationId = request.OperationId,
            LastOutcome = "healthy",
            LastVerifiedAt = now,
            LastRealSuccessAt = now,
            LastRenewedAt = now,
        };
    }
}
