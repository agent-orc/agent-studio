namespace AgentRunner;

/// <summary>Repository purpose is independent of the credential used to reach its origin.</summary>
public sealed record RepositoryAccessBinding(string Purpose, string Origin, bool RequiresPush);
public sealed record RepositoryAccessProof(
    string Purpose, string Origin, bool FetchVerified, bool PushVerified,
    bool PushRequired, string Status)
{
    public bool Ready => FetchVerified && (!PushRequired || PushVerified);
}
public sealed record RepositoryBindingProbeResult(IReadOnlyList<RepositoryAccessProof> Proofs)
{
    public bool Ready => Proofs.Count > 0 && Proofs.All(proof => proof.Ready);
}

/// <summary>All fields are metadata; an administration bearer and private key never enter a request.</summary>
public sealed record RepositoryRotationRequest(
    string OperationId, string HostId, string ExpectedGeneration,
    string Owner, string Repository, string Purpose, string Origin,
    bool RequiresPush, long? OldGitHubKeyId, string ProvisioningCredentialId,
    string? OldHostLocalRef = null);
public sealed record RepositoryKeyCandidate(
    string Generation, string PublicKey, string Fingerprint, string HostLocalRef);
public sealed record RegisteredRepositoryKey(long GitHubKeyId, string Fingerprint, bool ReadOnly);
public sealed record RepositoryRotationReceipt(
    string OperationId, string ExpectedGeneration, RepositoryKeyCandidate? Candidate,
    long? NewGitHubKeyId, string? ProvisioningCredentialId,
    bool FetchVerified, bool PushVerified, bool Switched, bool OldRetired,
    string Status)
{
    public bool Completed => Switched && OldRetired;
}

public interface IRepositoryKeyHost
{
    Task<string> ActiveGenerationAsync(RepositoryRotationRequest request, CancellationToken ct);
    Task<RepositoryKeyCandidate> GenerateOrLoadAsync(RepositoryRotationRequest request, CancellationToken ct);
    Task<bool> DrainTransportsAsync(RepositoryRotationRequest request, CancellationToken ct);
    Task SwitchAsync(RepositoryRotationRequest request, RepositoryKeyCandidate candidate, CancellationToken ct);
    Task RetireAsync(RepositoryRotationRequest request, CancellationToken ct);
}

/// <summary>Implemented only in an authorized, protected administration session.</summary>
public interface IRepositoryKeyAdministration
{
    Task<RegisteredRepositoryKey?> FindByFingerprintAsync(RepositoryRotationRequest request, string fingerprint, CancellationToken ct);
    Task<RegisteredRepositoryKey?> RegisterAsync(RepositoryRotationRequest request, RepositoryKeyCandidate candidate, CancellationToken ct);
    Task DeleteAsync(RepositoryRotationRequest request, long keyId, CancellationToken ct);
}

/// <summary>Git operations remain with the platform Git layer, never the worker CLI.</summary>
public interface IRepositoryGitProof
{
    Task<RepositoryAccessProof> ProveAsync(RepositoryAccessBinding binding, string? generation, CancellationToken ct);
}

public interface IRepositoryRotationJournal
{
    Task<RepositoryRotationReceipt?> LoadAsync(string operationId, CancellationToken ct);
    Task SaveAsync(RepositoryRotationReceipt receipt, CancellationToken ct);
}

public sealed class RepositoryAccessRenewal(
    IRepositoryKeyHost host,
    IRepositoryKeyAdministration administration,
    IRepositoryGitProof git,
    IRepositoryRotationJournal journal)
{
    private readonly SemaphoreSlim _rotationGate = new(1, 1);
    public static async Task<RepositoryBindingProbeResult> ProbeBindingsAsync(
        IReadOnlyList<RepositoryAccessBinding> bindings, IRepositoryGitProof git, CancellationToken ct)
    {
        var proofs = new List<RepositoryAccessProof>(bindings.Count);
        foreach (var binding in bindings)
        {
            ValidateBinding(binding);
            proofs.Add(await git.ProveAsync(binding, null, ct));
        }
        return new(proofs);
    }

    public async Task<RepositoryRotationReceipt> RotateAsync(RepositoryRotationRequest request, CancellationToken ct)
    {
        await _rotationGate.WaitAsync(ct);
        try { return await RotateCoreAsync(request, ct); }
        finally { _rotationGate.Release(); }
    }

    private async Task<RepositoryRotationReceipt> RotateCoreAsync(RepositoryRotationRequest request, CancellationToken ct)
    {
        ValidateBinding(new(request.Purpose, request.Origin, request.RequiresPush));
        var sshBase = $"git@github.com:{request.Owner}/{request.Repository}";
        var httpsBase = $"https://github.com/{request.Owner}/{request.Repository}";
        if (request.Origin != sshBase && request.Origin != sshBase + ".git"
            && request.Origin != httpsBase && request.Origin != httpsBase + ".git")
            throw new ArgumentException("Deploy-key registration repository must match the exact proved origin.");
        if (string.IsNullOrWhiteSpace(request.OperationId) || string.IsNullOrWhiteSpace(request.ExpectedGeneration)
            || string.IsNullOrWhiteSpace(request.ProvisioningCredentialId)
            || request.OperationId.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not ('-' or '_' or '.')))
            throw new ArgumentException("Operation, generation and provisioning credential metadata are required.");

        var receipt = await journal.LoadAsync(request.OperationId, ct)
            ?? new RepositoryRotationReceipt(request.OperationId, request.ExpectedGeneration, null,
                null, request.ProvisioningCredentialId, false, false, false, false, "started");
        if (receipt.ExpectedGeneration != request.ExpectedGeneration
            || receipt.ProvisioningCredentialId != request.ProvisioningCredentialId)
            throw new InvalidOperationException("Rotation receipt does not match the expected generation and provisioning authority.");
        if (receipt.Completed) return receipt;
        var active = await host.ActiveGenerationAsync(request, ct);
        if (active != request.ExpectedGeneration)
        {
            if (receipt.Candidate is null || active != receipt.Candidate.Generation
                || !receipt.FetchVerified || request.RequiresPush && !receipt.PushVerified)
                throw new InvalidOperationException("Host repository credential generation changed during rotation.");
            receipt = receipt with { Switched = true, Status = "switched" };
            await journal.SaveAsync(receipt, ct);
        }

        if (receipt.Candidate is null)
        {
            var candidate = await host.GenerateOrLoadAsync(request, ct);
            receipt = receipt with { Candidate = candidate, Status = "candidate-generated" };
            await journal.SaveAsync(receipt, ct);
        }

        if (receipt.NewGitHubKeyId is null)
        {
            // GitHub creation may have succeeded before a lost receipt. Search by
            // public fingerprint on every retry before attempting another create.
            RegisteredRepositoryKey? registered;
            try
            {
                registered = await administration.FindByFingerprintAsync(
                    request, receipt.Candidate.Fingerprint, ct);
                if (registered is null)
                    registered = await administration.RegisterAsync(request, receipt.Candidate, ct)
                        ?? await administration.FindByFingerprintAsync(
                            request, receipt.Candidate.Fingerprint, ct);
            }
            catch (RepositoryAdminAuthorityException)
            {
                return await SaveFailure(receipt, "administrator-action-required", ct);
            }
            if (registered is null)
                return await SaveFailure(receipt, "administrator-action-required", ct);
            if (registered.Fingerprint != receipt.Candidate.Fingerprint
                || registered.ReadOnly == request.RequiresPush
                || registered.GitHubKeyId == request.OldGitHubKeyId)
                return await SaveFailure(receipt, "repository-key-policy-mismatch", ct);
            receipt = receipt with { NewGitHubKeyId = registered.GitHubKeyId, Status = "registered" };
            await journal.SaveAsync(receipt, ct);
        }

        if (!receipt.FetchVerified || request.RequiresPush && !receipt.PushVerified)
        {
            var proof = await git.ProveAsync(
                new(request.Purpose, request.Origin, request.RequiresPush), receipt.Candidate.Generation, ct);
            if (!string.Equals(proof.Origin, request.Origin, StringComparison.Ordinal)
                || !string.Equals(proof.Purpose, request.Purpose, StringComparison.Ordinal))
                return await SaveFailure(receipt, "exact-origin-proof-mismatch", ct);
            receipt = receipt with {
                FetchVerified = proof.FetchVerified, PushVerified = proof.PushVerified,
                Status = proof.Ready ? "verified" : "repository-proof-failed"
            };
            await journal.SaveAsync(receipt, ct);
            if (!proof.Ready) return receipt;
        }

        if (!receipt.Switched)
        {
            if (!await host.DrainTransportsAsync(request, ct))
                return await SaveFailure(receipt, "active-transports-remain", ct);
            await host.SwitchAsync(request, receipt.Candidate, ct);
            receipt = receipt with { Switched = true, Status = "switched" };
            await journal.SaveAsync(receipt, ct);
        }

        if (!receipt.OldRetired)
        {
            await host.RetireAsync(request, ct);
            if (request.OldGitHubKeyId is long oldId)
                await administration.DeleteAsync(request, oldId, ct);
            receipt = receipt with { OldRetired = true, Status = "completed" };
            await journal.SaveAsync(receipt, ct);
        }
        return receipt;
    }

    private async Task<RepositoryRotationReceipt> SaveFailure(
        RepositoryRotationReceipt receipt, string status, CancellationToken ct)
    {
        receipt = receipt with { Status = status };
        await journal.SaveAsync(receipt, ct);
        return receipt;
    }

    internal static void ValidateBinding(RepositoryAccessBinding binding)
    {
        if (binding.Purpose is not ("workspace" or "product"))
            throw new ArgumentException("A credential-free, exact GitHub origin and repository purpose are required.");
        string path;
        if (binding.Origin.StartsWith("git@github.com:", StringComparison.Ordinal))
            path = binding.Origin["git@github.com:".Length..];
        else if (Uri.TryCreate(binding.Origin, UriKind.Absolute, out var uri)
            && uri.Scheme == "https" && uri.Host == "github.com" && uri.Port == 443
            && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0)
            path = uri.AbsolutePath.TrimStart('/');
        else
            throw new ArgumentException("A credential-free, exact GitHub origin and repository purpose are required.");
        var segments = path.EndsWith(".git", StringComparison.Ordinal) ? path[..^4].Split('/') : path.Split('/');
        if (segments.Length != 2 || segments.Any(segment => segment.Length is < 1 or > 100
            || segment.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not ('-' or '_' or '.'))))
            throw new ArgumentException("A credential-free, exact GitHub origin and repository purpose are required.");
    }
}
