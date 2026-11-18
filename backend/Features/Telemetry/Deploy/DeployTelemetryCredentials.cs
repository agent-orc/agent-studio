using AgentStudio.Projects;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.Secrets;
using AgentStudio.Security;

namespace AgentStudio.Telemetry;

/// <summary>
/// Mints the ingest token a deployed workload sends telemetry with (M5). The
/// token is scoped to one project environment, stored through the secret
/// provider in that environment's scope and bound there, so the deploy host
/// resolves it just in time like any other workload secret. Studio never shows
/// or logs the value. A revoked or expired deploy token is replaced at the next
/// deploy claim, and the earlier deploy tokens are revoked.
/// </summary>
public sealed class DeployTelemetryCredentials(ITelemetryIngestCredentials tokens, ProjectSecretBindings bindings,
    ISecretProvider provider, TelemetryOptions options, TimeProvider clock, ILogger<DeployTelemetryCredentials> logger)
{
    /// <summary>The <c>createdBy</c> of deploy tokens; the token page labels them as managed by deploys.</summary>
    public const string CreatedBy = "studio:deploy";
    public const string TokenName = "Studio deploy";
    public static readonly SecretActor Actor = SecretActor.ServiceAccount("studio-telemetry");

    // Two claims for one environment must not mint two tokens.
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Ensures the deploy token exists and fills the endpoint and resource
    /// attributes of a claimed job. <paramref name="requestEndpoint"/> is the
    /// intake address under the URL the host used to reach Studio; the
    /// <c>Telemetry:PublicOtlpEndpoint</c> setting replaces it when workloads reach
    /// Studio at another address.
    /// </summary>
    public async Task<DeployJob> PrepareClaimAsync(DeployJob job, string requestEndpoint, CancellationToken ct)
    {
        if (job.Telemetry is not { } telemetry) return job;
        await EnsureAsync(job.Project, job.EnvironmentId, ct);
        return job with
        {
            Telemetry = telemetry with
            {
                Endpoint = options.PublicOtlpEndpoint ?? requestEndpoint,
                ResourceAttributes = DeployTelemetryVocabulary.ResourceAttributes(job.EnvironmentId, job.ReleaseId),
            },
        };
    }

    public async Task EnsureAsync(string project, string environment, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var live = tokens.List(project, environment)
                .Where(item => item.CreatedBy == CreatedBy && item.RevokedAt is null && (item.ExpiresAt is null || item.ExpiresAt > now))
                .ToArray();
            var scope = SecretScope.ForEnvironment(project, environment);
            var name = DeployTelemetryVocabulary.SecretName;
            if (live.Length == 1 && bindings.List(project, environment).Any(item => item.Name == name)
                && (await provider.ListAsync(scope, ct)).Records.Any(item => item.Name == name && SecretVersionPolicy.CurrentVersion(item) is not null))
                return;
            var (token, secret) = tokens.Create(project, environment, TokenName, TelemetrySignals.All, null, CreatedBy);
            try
            {
                await provider.PutAsync(scope, name, secret, Actor, ct);
                bindings.BindTelemetryIngest(project, environment);
            }
            catch
            {
                tokens.Revoke(project, environment, token.Id);
                throw;
            }
            foreach (var earlier in live) tokens.Revoke(project, environment, earlier.Id);
            logger.LogInformation("Minted deploy ingest token {TokenId} for {Project}/{Environment} as {Reference}",
                token.Id, project, environment, new SecretReference(scope, name));
        }
        finally { _gate.Release(); }
    }
}
