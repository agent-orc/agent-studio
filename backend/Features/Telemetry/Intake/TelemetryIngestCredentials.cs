using AgentStudio.Projects;
using AgentStudio.Security;
using AgentStudio.Secrets;
using System.Text;

namespace AgentStudio.Telemetry;

/// <summary>
/// Where ingest tokens live. The access store keeps their hashes separately
/// from application secret values held by the secret provider.
/// </summary>
public interface ITelemetryIngestCredentials
{
    TelemetryIngestPrincipal? Authenticate(string? bearer);

    (TelemetryIngestToken Token, string Secret) Create(string project, string environment, string? name,
        IReadOnlyList<string>? signals, DateTime? expiresAt, string? createdBy);

    IReadOnlyList<TelemetryIngestToken> List(string project, string environment);

    bool Revoke(string project, string environment, string tokenId);
}

public sealed class AccessStoreTelemetryIngestCredentials(AccessSecurityStore store) : ITelemetryIngestCredentials
{
    public TelemetryIngestPrincipal? Authenticate(string? bearer) => store.AuthenticateTelemetryIngest(bearer);

    public (TelemetryIngestToken Token, string Secret) Create(string project, string environment, string? name,
        IReadOnlyList<string>? signals, DateTime? expiresAt, string? createdBy)
        => store.CreateTelemetryIngestToken(project, environment, name, signals, expiresAt, createdBy);

    public IReadOnlyList<TelemetryIngestToken> List(string project, string environment)
        => store.ListTelemetryIngestTokens(project, environment);

    public bool Revoke(string project, string environment, string tokenId)
        => store.RevokeTelemetryIngestToken(project, environment, tokenId);
}

/// <summary>Whether a project and environment exist, so tokens are only issued for declared environments.</summary>
public interface ITelemetryEnvironmentDirectory
{
    /// <summary>Null when the project is unknown; otherwise the declared environment ids.</summary>
    IReadOnlyCollection<string>? Environments(string project);
}

public sealed class DeclaredTelemetryEnvironments(ProjectEnvironmentService environments) : ITelemetryEnvironmentDirectory
{
    public IReadOnlyCollection<string>? Environments(string project)
        => environments.Declarations(project)?.Select(item => item.Id).ToArray();
}

/// <summary>Masks secret values in log text before storage and export.</summary>
public interface ITelemetryValueMasker
{
    string Mask(string value);

    Task<Func<string, string>> ForEnvironmentAsync(TelemetryEnvironmentKey environment, CancellationToken cancellationToken)
        => Task.FromResult<Func<string, string>>(Mask);
}

/// <summary>
/// Masks credential-shaped text and active provider values in the scope of
/// each telemetry environment. Reads fresh values for every staged batch;
/// a resolved-value cache cannot provide this guarantee.
/// </summary>
public sealed class CredentialTelemetryValueMasker(ISecretStore? store = null) : ITelemetryValueMasker
{
    public string Mask(string value) => CredentialRedactor.Redact(value);

    public async Task<Func<string, string>> ForEnvironmentAsync(TelemetryEnvironmentKey environment, CancellationToken cancellationToken)
    {
        if (store is null) return Mask;
        var scopes = SecretScope.ForEnvironment(environment.Project, environment.Environment).InheritanceChain();
        var listing = await store.ListAsync(cancellationToken);
        if (listing.Unreadable.Any(item => item.Scope is null
            || !SecretScope.TryParse(item.Scope, out var scope) || scopes.Contains(scope)))
            throw new InvalidDataException("Telemetry cannot verify all secret values in this environment.");

        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in listing.Records.Where(item => scopes.Contains(item.ParsedScope)))
        {
            foreach (var version in record.Versions.Where(item => item.Active))
            {
                var bytes = await store.ReadValueAsync(record.ParsedScope, record.Name, version.Version, cancellationToken);
                try
                {
                    var value = Encoding.UTF8.GetString(bytes);
                    if (value.Length > 0) values.Add(value);
                    foreach (var line in value.Split(["\r\n", "\n"], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                        if (line.Length >= SecretValueScrubber.MinimumMaskedLength) values.Add(line);
                    if (value.Length >= SecretValueScrubber.MinimumMaskedLength)
                        values.Add(Convert.ToBase64String(bytes));
                }
                finally { Array.Clear(bytes); }
            }
        }
        var ordered = values.OrderByDescending(value => value.Length).ToArray();
        return value =>
        {
            var masked = value;
            foreach (var secret in ordered)
                masked = masked.Replace(secret, "[REDACTED_CREDENTIAL]", StringComparison.Ordinal);
            return CredentialRedactor.Redact(masked);
        };
    }
}
