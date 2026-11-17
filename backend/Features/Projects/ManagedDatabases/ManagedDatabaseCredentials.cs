using System.Security.Cryptography;
using AgentStudio.Secrets;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Projects;

/// <summary>
/// Generates the password of each managed database (AGT-3877), stores it
/// through the secret provider in the environment's scope and binds it there.
/// Studio never shows, logs or returns the value; the run host resolves the
/// <c>secret://</c> reference just in time. After the host deleted a
/// database's data, the credential is removed as well.
/// </summary>
public sealed class ManagedDatabaseCredentials(ProjectEnvironmentService environments, ProjectSecretBindings bindings,
    ISecretProvider provider, ILogger<ManagedDatabaseCredentials> logger)
{
    public static readonly SecretActor Actor = SecretActor.ServiceAccount("studio-managed-database");

    // Two polls of the same host must not generate two passwords for one database.
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Generates every missing credential of the databases this host's environments declare.</summary>
    public async Task EnsureForHostAsync(string hostId, CancellationToken ct)
    {
        var needs = environments.ManagedDatabaseCredentialNeeds(hostId);
        if (needs.Count == 0) return;
        await _gate.WaitAsync(ct);
        try
        {
            foreach (var need in needs) await EnsureAsync(need, ct);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Removes the credential and its binding of each database the host has deleted.</summary>
    public async Task RetireDeletedAsync(CancellationToken ct)
    {
        var retiring = environments.RetiringDatabaseCredentials();
        if (retiring.Count == 0) return;
        await _gate.WaitAsync(ct);
        try
        {
            foreach (var item in retiring)
            {
                var scope = SecretScope.ForEnvironment(item.Project, item.EnvironmentId);
                var name = ProjectDatabaseVocabulary.CredentialName(item.Database);
                try { await provider.DeleteAsync(scope, name, Actor, ct); }
                catch (SecretNotFoundException) { logger.LogInformation("Credential {Name} of {Scope} was already gone", name, scope); }
                bindings.UnbindManagedDatabase(item.Project, item.EnvironmentId, name);
                environments.MarkDatabaseCredentialRetired(item.Project, item.EnvironmentId, item.Database);
            }
        }
        finally { _gate.Release(); }
    }

    private async Task EnsureAsync(ManagedDatabaseCredentialNeed need, CancellationToken ct)
    {
        var name = ProjectDatabaseVocabulary.CredentialName(need.Database);
        if (bindings.List(need.Project, need.EnvironmentId).Any(item => item.Name == name)) return;
        var scope = SecretScope.ForEnvironment(need.Project, need.EnvironmentId);
        // An earlier generation may have stored the value before its binding was written.
        if ((await provider.ListAsync(scope, ct)).Records.All(item => item.Name != name))
            await provider.PutAsync(scope, name, NewPassword(), Actor, ct);
        bindings.BindManagedDatabase(need.Project, need.EnvironmentId, name);
        logger.LogInformation("Generated the credential {Reference} for managed database {Database}",
            new SecretReference(scope, name), need.Database);
    }

    /// <summary>32 random bytes as base64url: no character needs escaping in a connection string.</summary>
    private static string NewPassword()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
