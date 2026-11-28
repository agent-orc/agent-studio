using AgentStudio.Secrets;

namespace AgentStudio.Registry;

public interface IProvisioningCredentialSource
{
    Task<string> GetTokenAsync(string purpose, CancellationToken ct);
}

public sealed class SecretProvisioningCredentialSource(ISecretProvider secrets, IConfiguration configuration)
    : IProvisioningCredentialSource
{
    public const string TokenName = SecretAccessPolicy.GitHubProvisioningTokenName;
    private static readonly SecretReference Reference = new(SecretScope.Workspace, TokenName);

    public async Task<string> GetTokenAsync(string purpose, CancellationToken ct)
    {
        try
        {
            var resolved = await secrets.GetForPlatformAsync(Reference, purpose, ct);
            if (!string.IsNullOrWhiteSpace(resolved.Value)) return resolved.Value.Trim();
        }
        catch (SecretNotFoundException)
        {
            // Rollback switch for the cutover. A revoked or unreadable secret fails closed.
            if (configuration.GetValue<bool>("ProjectOnboarding:AllowLegacyProvisioningToken")
                && Environment.GetEnvironmentVariable(TokenName) is { } legacy
                && !string.IsNullOrWhiteSpace(legacy))
                return legacy.Trim();
        }
        catch (SecretRevokedException)
        {
            throw new RepositoryProvisioningException("missing-credential",
                "The GitHub provisioning secret is revoked. Ask an operator to check the workspace secret.");
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            throw new RepositoryProvisioningException("credential-unavailable",
                "Studio could not read the GitHub provisioning secret. Ask an operator to check the secret store.");
        }
        ct.ThrowIfCancellationRequested();
        throw new RepositoryProvisioningException("missing-credential",
            "GitHub repository creation needs a dedicated workspace secret. Ask an operator to configure provisioning.");
    }
}
