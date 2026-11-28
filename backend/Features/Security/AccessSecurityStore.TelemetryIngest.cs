namespace AgentStudio.Security;

/// <summary>
/// OTLP ingest tokens (IP-15). Each token is scoped to one project environment
/// and a set of signals, can expire, and can be revoked. The access store keeps
/// only the SHA-256 hash, like Runner credentials.
/// </summary>
public sealed partial class AccessSecurityStore
{
    private const string TelemetryTokenPrefix = "otl";

    public (TelemetryIngestToken Token, string Secret) CreateTelemetryIngestToken(
        string project, string environment, string? name, IReadOnlyList<string>? signals,
        DateTime? expiresAt, string? createdBy)
    {
        if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(environment))
            throw new SecurityOperationException(400, "environment-required", "Project and environment are required.");
        var label = string.IsNullOrWhiteSpace(name) ? "otlp" : name.Trim();
        if (label.Length > 80) throw new SecurityOperationException(400, "invalid-name", "Token name must be at most 80 characters.");
        var normalized = (signals is null || signals.Count == 0 ? TelemetrySignals.All : signals)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim().ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToList();
        if (normalized.Count == 0 || normalized.Any(item => !TelemetrySignals.All.Contains(item)))
            throw new SecurityOperationException(400, "invalid-signal", "Signals must be metrics, logs, or both.");
        var now = Now;
        if (expiresAt is { } expiry && expiry.ToUniversalTime() <= now)
            throw new SecurityOperationException(400, "invalid-expiry", "Token expiry must be in the future.");
        var id = Guid.NewGuid().ToString("N");
        var secret = Issued($"{TelemetryTokenPrefix}.{id}.{PasswordSecretHasher.RandomToken()}");
        var token = new TelemetryIngestToken
        {
            Id = id, Name = label, Project = _secretProjectKeys.Canonical(project), Environment = environment, Signals = normalized,
            SecretHash = PasswordSecretHasher.HashSecret(secret), CreatedAt = now, CreatedBy = createdBy,
            ExpiresAt = expiresAt?.ToUniversalTime()
        };
        lock (_gate)
        {
            var state = StateLocked();
            state.TelemetryIngestTokens.Add(token);
            SaveLocked(state);
        }
        return (token, secret);
    }

    public IReadOnlyList<TelemetryIngestToken> ListTelemetryIngestTokens(string project, string environment)
    {
        lock (_gate)
        {
            var state = StateLocked();
            var matching = state.TelemetryIngestTokens.Where(item => SameEnvironment(item, project, environment)).ToArray();
            var migrated = matching.Select(item => MigrateTelemetryProject(state, item)).ToArray();
            if (!matching.SequenceEqual(migrated)) SaveLocked(state);
            return migrated.OrderBy(item => item.CreatedAt).ToList();
        }
    }

    public bool RevokeTelemetryIngestToken(string project, string environment, string tokenId)
    {
        lock (_gate)
        {
            var state = StateLocked();
            var token = state.TelemetryIngestTokens.FirstOrDefault(item => item.Id == tokenId && SameEnvironment(item, project, environment));
            if (token is null) return false;
            var original = token;
            token = MigrateTelemetryProject(state, token);
            if (original != token || token.RevokedAt is null)
            {
                state.TelemetryIngestTokens[state.TelemetryIngestTokens.IndexOf(token)] = token with { RevokedAt = token.RevokedAt ?? Now };
                SaveLocked(state);
            }
            return true;
        }
    }

    public TelemetryIngestPrincipal? AuthenticateTelemetryIngest(string? bearer)
    {
        if (!TryTokenParts(bearer, TelemetryTokenPrefix, out var id)) return null;
        lock (_gate)
        {
            var state = StateLocked();
            var now = Now;
            var token = state.TelemetryIngestTokens.FirstOrDefault(item => item.Id == id);
            if (token is null || !_secretProjectKeys.Owns(token.Project, token.Project)
                || token.RevokedAt is not null || token.ExpiresAt <= now
                || !PasswordSecretHasher.VerifySecret(bearer!, token.SecretHash)) return null;
            var original = token;
            token = MigrateTelemetryProject(state, token);
            // Exporters send every few seconds; persist the last use at most every five minutes.
            if (original != token || token.LastUsedAt is null || now - token.LastUsedAt >= TimeSpan.FromMinutes(5))
            {
                state.TelemetryIngestTokens[state.TelemetryIngestTokens.IndexOf(token)] = token with { LastUsedAt = now };
                SaveLocked(state);
            }
            return new TelemetryIngestPrincipal(token.Id, _secretProjectKeys.DisplayName(token.Project), token.Environment,
                new HashSet<string>(token.Signals, StringComparer.Ordinal));
        }
    }

    private TelemetryIngestToken MigrateTelemetryProject(SecurityState state, TelemetryIngestToken token)
    {
        var project = _secretProjectKeys.Canonical(token.Project);
        if (project == token.Project || !_secretProjectKeys.Owns(project, token.Project)) return token;
        var migrated = token with { Project = project };
        state.TelemetryIngestTokens[state.TelemetryIngestTokens.IndexOf(token)] = migrated;
        return migrated;
    }

    /// <summary>Explicit maintenance; hashes and token ids do not change.</summary>
    public int MigrateTelemetryProjectKeys()
    {
        lock (_gate)
        {
            var state = StateLocked();
            var count = 0;
            foreach (var token in state.TelemetryIngestTokens.ToArray())
                if (MigrateTelemetryProject(state, token) != token) count++;
            if (count > 0) SaveLocked(state);
            return count;
        }
    }

    private bool SameEnvironment(TelemetryIngestToken token, string project, string environment)
        => _secretProjectKeys.Owns(project, token.Project)
           && string.Equals(_secretProjectKeys.Canonical(token.Project), _secretProjectKeys.Canonical(project), StringComparison.OrdinalIgnoreCase)
           && string.Equals(token.Environment, environment, StringComparison.Ordinal);
}
