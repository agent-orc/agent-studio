using AgentStudio.Projects;
using AgentStudio.Security;

namespace AgentStudio.Telemetry;

/// <summary>
/// Where ingest tokens live. The card asks for the secret provider (AGT-3222)
/// when present; it is not in this codebase yet, so the access store keeps the
/// hashed token. A secret-provider implementation replaces this registration.
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
}

/// <summary>
/// Masks credential-shaped text with the product redactor (bearer and
/// authorization headers, named secrets, Studio tokens). Values held by the
/// secret provider (AGT-3222) are added once that provider exists.
/// </summary>
public sealed class CredentialTelemetryValueMasker : ITelemetryValueMasker
{
    public string Mask(string value) => CredentialRedactor.Redact(value);
}
