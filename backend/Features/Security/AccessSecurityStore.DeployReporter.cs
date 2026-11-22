namespace AgentStudio.Security;

/// <summary>
/// Deploy reporter tokens (AGT-3879): the narrow credential the operator
/// deploy scripts send to the instance deploy record API. Hash-only storage,
/// optional expiry and revocation, like the OTLP ingest tokens.
/// </summary>
public sealed partial class AccessSecurityStore
{
    private const string DeployReporterTokenPrefix = "dpr";

    public (DeployReporterToken Token, string Secret) CreateDeployReporterToken(string? name, DateTime? expiresAt,
        string? createdBy)
    {
        var label = string.IsNullOrWhiteSpace(name) ? "deploy-scripts" : name.Trim();
        if (label.Length > 80) throw new SecurityOperationException(400, "invalid-name", "Token name must be at most 80 characters.");
        var now = Now;
        if (expiresAt is { } expiry && expiry.ToUniversalTime() <= now)
            throw new SecurityOperationException(400, "invalid-expiry", "Token expiry must be in the future.");
        var id = Guid.NewGuid().ToString("N");
        var secret = Issued($"{DeployReporterTokenPrefix}.{id}.{PasswordSecretHasher.RandomToken()}");
        var token = new DeployReporterToken
        {
            Id = id, Name = label, SecretHash = PasswordSecretHasher.HashSecret(secret), CreatedAt = now,
            CreatedBy = createdBy, ExpiresAt = expiresAt?.ToUniversalTime(),
        };
        lock (_gate)
        {
            var state = StateLocked();
            state.DeployReporterTokens.Add(token);
            SaveLocked(state);
        }
        return (token, secret);
    }

    public IReadOnlyList<DeployReporterToken> ListDeployReporterTokens()
    {
        lock (_gate) return StateLocked().DeployReporterTokens.OrderBy(item => item.CreatedAt).ToList();
    }

    public bool RevokeDeployReporterToken(string tokenId)
    {
        lock (_gate)
        {
            var state = StateLocked();
            var token = state.DeployReporterTokens.FirstOrDefault(item => item.Id == tokenId);
            if (token is null) return false;
            if (token.RevokedAt is null)
            {
                state.DeployReporterTokens[state.DeployReporterTokens.IndexOf(token)] = token with { RevokedAt = Now };
                SaveLocked(state);
            }
            return true;
        }
    }

    /// <summary>The principal of a valid, unrevoked, unexpired reporter bearer; null for any other credential.</summary>
    public DeployReporterPrincipal? AuthenticateDeployReporter(string? bearer)
    {
        if (!TryTokenParts(bearer, DeployReporterTokenPrefix, out var id)) return null;
        lock (_gate)
        {
            var state = StateLocked();
            var now = Now;
            var token = state.DeployReporterTokens.FirstOrDefault(item => item.Id == id);
            if (token is null || token.RevokedAt is not null || token.ExpiresAt <= now
                || !PasswordSecretHasher.VerifySecret(bearer!, token.SecretHash)) return null;
            if (token.LastUsedAt is null || now - token.LastUsedAt >= TimeSpan.FromMinutes(1))
            {
                state.DeployReporterTokens[state.DeployReporterTokens.IndexOf(token)] = token with { LastUsedAt = now };
                SaveLocked(state);
            }
            return new DeployReporterPrincipal(token.Id, token.Name);
        }
    }
}
