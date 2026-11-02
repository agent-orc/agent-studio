using System.Text.Json;
using AgentStudio.Registry;
using AgentStudio.Secrets;
using AgentStudio.TaskServer.Contracts.GitHubApp;

namespace AgentStudio.GitHubApp;

/// <summary>
/// Keeps the App's private key and client secret in the secret provider, so
/// they follow the selected secret backend, its rotation and its audit. The
/// connection's public metadata lives beside the access state.
/// </summary>
public sealed class SecretProviderGitHubAppVault(ISecretProvider secrets, string securityFolder) : IGitHubAppVault
{
    internal const string PrivateKeyName = SecretAccessPolicy.GitHubAppPrivateKeyName;
    internal const string ClientSecretName = SecretAccessPolicy.GitHubAppClientSecretName;
    private const string Purpose = "github-app-token-issue";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private string ConnectionPath => Path.Combine(securityFolder, "github-app", "connection.json");

    public async Task<GitHubAppConnection?> LoadConnectionAsync(CancellationToken ct)
    {
        if (!File.Exists(ConnectionPath)) return null;
        await using var stream = File.OpenRead(ConnectionPath);
        return await JsonSerializer.DeserializeAsync<GitHubAppConnection>(stream, Json, ct);
    }

    public Task SaveConnectionAsync(GitHubAppConnection connection, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RepositoryGitCredentialFiles.WriteProtected(ConnectionPath, JsonSerializer.Serialize(connection, Json));
        return Task.CompletedTask;
    }

    public Task DeleteConnectionAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (File.Exists(ConnectionPath)) File.Delete(ConnectionPath);
        return Task.CompletedTask;
    }

    public async Task<GitHubAppSecrets?> ReadSecretsAsync(CancellationToken ct)
    {
        var key = await ReadAsync(PrivateKeyName, ct);
        var clientSecret = await ReadAsync(ClientSecretName, ct);
        return key is null || clientSecret is null ? null : new GitHubAppSecrets(key, clientSecret);
    }

    public async Task WriteSecretsAsync(GitHubAppSecrets values, string actor, CancellationToken ct)
    {
        var owner = SecretActor.Human(actor);
        await secrets.PutAsync(SecretScope.Workspace, PrivateKeyName, values.PrivateKeyPem, owner, ct);
        await secrets.PutAsync(SecretScope.Workspace, ClientSecretName, values.ClientSecret, owner, ct);
    }

    public Task EnsureWritableAsync(CancellationToken ct) => secrets.EnsureWritableAsync(SecretScope.Workspace, ct);

    public async Task DeleteSecretsAsync(string actor, CancellationToken ct)
    {
        var owner = SecretActor.Human(actor);
        foreach (var name in new[] { PrivateKeyName, ClientSecretName })
        {
            try { await secrets.DeleteAsync(SecretScope.Workspace, name, owner, ct); }
            catch (SecretNotFoundException exception)
            {
                // Removed by hand from the secret list; the goal is reached.
                SilentCatch.Note(exception, "SecretProviderGitHubAppVault: secret already gone");
            }
        }
    }

    private async Task<string?> ReadAsync(string name, CancellationToken ct)
    {
        try
        {
            var resolved = await secrets.GetForPlatformAsync(new SecretReference(SecretScope.Workspace, name), Purpose, ct);
            return resolved.Value;
        }
        catch (SecretNotFoundException exception)
        {
            SilentCatch.Note(exception, "SecretProviderGitHubAppVault: secret not stored");
            return null;
        }
        catch (SecretRevokedException exception)
        {
            // Revoking the key in the secret list stops new tokens at once.
            SilentCatch.Note(exception, "SecretProviderGitHubAppVault: secret revoked");
            return null;
        }
    }
}

/// <summary>
/// Every non-archived project and the repository it registers, plus the workspace
/// repository this host pushes its evidence to.
/// </summary>
public sealed class ProjectRegistryRepositoryCatalog(ProjectRegistry projects, IConfiguration configuration)
    : IRegisteredRepositoryCatalog
{
    public Task<IReadOnlyList<RegisteredRepository>> ListAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var repositories = projects.List()
            .Where(project => !project.Archived)
            .Select(project => new RegisteredRepository(project.DisplayName,
                project.Urls.FirstOrDefault(url => url.Id == "repo")?.Url))
            .ToList();
        if (WorkspaceOrigin(configuration["TaskRepository"]) is { } workspace)
            repositories.Add(new RegisteredRepository("Workspace evidence", workspace, StudioOnly: true));
        return Task.FromResult<IReadOnlyList<RegisteredRepository>>(repositories);
    }

    /// <summary>Read from the file: the settings page may not start Git on a read request.</summary>
    internal static string? WorkspaceOrigin(string? taskRepository)
    {
        if (string.IsNullOrWhiteSpace(taskRepository)) return null;
        var config = Path.Combine(taskRepository, ".git", "config");
        if (!File.Exists(config)) return null;
        var inOrigin = false;
        foreach (var raw in File.ReadLines(config))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) inOrigin = line.Equals("[remote \"origin\"]", StringComparison.Ordinal);
            else if (inOrigin && line.StartsWith("url", StringComparison.Ordinal) && line.IndexOf('=') is var equals and > 0
                     && line[..equals].Trim() == "url")
                return line[(equals + 1)..].Trim();
        }
        return null;
    }
}
