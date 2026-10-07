namespace AgentRunner;

/// <summary>
/// Issuer subtype, repository grant and repository purpose are independent facts.
/// Unknown issuer expiry stays null. This record contains no token material.
/// </summary>
public sealed record GitHubHttpsCredentialMetadata(
    string CredentialId, string TokenSubtype, string RepositoryPurpose,
    string Origin, bool RequiresPush, bool? FetchGrant, bool? PushGrant, DateTimeOffset? IssuerExpiresAt,
    string ExpiryKnowledge);

public sealed record RepositoryHttpsRotationRequest(
    string OperationId, string ExpectedGeneration, GitHubHttpsCredentialMetadata Metadata,
    string OldCredentialId);

public sealed record RepositoryHttpsRotationReceipt(
    string OperationId, string ExpectedGeneration, string? CandidateGeneration,
    bool FetchVerified, bool PushVerified, bool Switched, bool OldRetired, string Status)
{
    public bool Completed => Switched && OldRetired;
}

/// <summary>Protected host adapter. A replacement bearer is passed only inside its session.</summary>
public interface IRepositoryHttpsCredentialHost
{
    Task<string> ActiveGenerationAsync(RepositoryHttpsRotationRequest request, CancellationToken ct);
    Task<string?> StageAsync(RepositoryHttpsRotationRequest request, CancellationToken ct);
    Task<bool> DrainTransportsAsync(RepositoryHttpsRotationRequest request, CancellationToken ct);
    Task SwitchAsync(RepositoryHttpsRotationRequest request, string generation, CancellationToken ct);
    Task RetireAsync(RepositoryHttpsRotationRequest request, CancellationToken ct);
}

public interface IRepositoryHttpsRotationJournal
{
    Task<RepositoryHttpsRotationReceipt?> LoadAsync(string operationId, CancellationToken ct);
    Task SaveAsync(RepositoryHttpsRotationReceipt receipt, CancellationToken ct);
}

public sealed class RepositoryHttpsRenewal(
    IRepositoryHttpsCredentialHost host,
    IRepositoryGitProof git,
    IRepositoryHttpsRotationJournal journal)
{
    private readonly SemaphoreSlim _rotationGate = new(1, 1);

    public async Task<RepositoryHttpsRotationReceipt> RotateAsync(
        RepositoryHttpsRotationRequest request, CancellationToken ct)
    {
        await _rotationGate.WaitAsync(ct);
        try { return await RotateCoreAsync(request, ct); }
        finally { _rotationGate.Release(); }
    }

    private async Task<RepositoryHttpsRotationReceipt> RotateCoreAsync(
        RepositoryHttpsRotationRequest request, CancellationToken ct)
    {
        var metadata = request.Metadata;
        if (metadata.TokenSubtype is not ("fine-grained-pat" or "classic-pat" or "oauth-app" or "unknown")
            || metadata.ExpiryKnowledge is not ("issuer" or "unknown")
            || metadata.ExpiryKnowledge == "unknown" && metadata.IssuerExpiresAt is not null
            || !metadata.Origin.StartsWith("https://github.com/", StringComparison.Ordinal))
            throw new ArgumentException("HTTPS credential metadata is incomplete or contradictory.");
        RepositoryAccessRenewal.ValidateBinding(
            new(metadata.RepositoryPurpose, metadata.Origin, metadata.RequiresPush));

        var receipt = await journal.LoadAsync(request.OperationId, ct)
            ?? new(request.OperationId, request.ExpectedGeneration, null,
                false, false, false, false, "started");
        if (receipt.ExpectedGeneration != request.ExpectedGeneration)
            throw new InvalidOperationException("HTTPS rotation expected generation changed.");
        if (receipt.Completed) return receipt;
        var active = await host.ActiveGenerationAsync(request, ct);
        if (active != request.ExpectedGeneration)
        {
            if (receipt.CandidateGeneration is null || active != receipt.CandidateGeneration
                || !receipt.FetchVerified || metadata.RequiresPush && !receipt.PushVerified)
                throw new InvalidOperationException("Host HTTPS credential generation changed during rotation.");
            receipt = await Save(receipt with { Switched = true, Status = "switched" }, ct);
        }
        if (metadata.FetchGrant != true || metadata.RequiresPush && metadata.PushGrant != true)
            return await Save(receipt with {
                Status = metadata.FetchGrant is null || metadata.RequiresPush && metadata.PushGrant is null
                    ? "repository-grant-unverified" : "repository-grant-required"
            }, ct);

        if (receipt.CandidateGeneration is null)
        {
            var staged = await host.StageAsync(request, ct);
            if (staged is null)
                return await Save(receipt with { Status = "guided-consent-required" }, ct);
            receipt = await Save(receipt with { CandidateGeneration = staged, Status = "staged" }, ct);
        }

        if (!receipt.FetchVerified || metadata.RequiresPush && !receipt.PushVerified)
        {
            var proof = await git.ProveAsync(
                new(metadata.RepositoryPurpose, metadata.Origin, metadata.RequiresPush),
                receipt.CandidateGeneration, ct);
            if (proof.Origin != metadata.Origin || proof.Purpose != metadata.RepositoryPurpose)
                return await Save(receipt with { Status = "exact-origin-proof-mismatch" }, ct);
            receipt = await Save(receipt with {
                FetchVerified = proof.FetchVerified, PushVerified = proof.PushVerified,
                Status = proof.Ready ? "verified" : "repository-proof-failed"
            }, ct);
            if (!proof.Ready) return receipt;
        }

        if (!receipt.Switched)
        {
            if (!await host.DrainTransportsAsync(request, ct))
                return await Save(receipt with { Status = "active-transports-remain" }, ct);
            await host.SwitchAsync(request, receipt.CandidateGeneration!, ct);
            receipt = await Save(receipt with { Switched = true, Status = "switched" }, ct);
        }
        if (!receipt.OldRetired)
        {
            // Token revocation is deliberately not implied by local retirement:
            // an OAuth/PAT may own deploy keys or other repository bindings.
            await host.RetireAsync(request, ct);
            receipt = await Save(receipt with { OldRetired = true, Status = "completed" }, ct);
        }
        return receipt;
    }

    private async Task<RepositoryHttpsRotationReceipt> Save(
        RepositoryHttpsRotationReceipt receipt, CancellationToken ct)
    {
        await journal.SaveAsync(receipt, ct);
        return receipt;
    }
}
