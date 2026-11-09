using AgentStudio.Secrets;
using AgentStudio.Security;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TaskServer.Contracts.GitHubApp;

namespace AgentStudio.GitHubApp;

/// <summary>
/// The Credentials page of an installation without a Task Server: what this
/// application itself knows about the GitHub App and its tokens, the hosts' keys,
/// its secret store, its enrolled service identities and the logins and Git access
/// the hosts report. Metadata only: no value, hash or token prefix enters this list.
/// </summary>
public static class LocalCredentialInventory
{
    private const string StudioHost = "studio-host";
    private static readonly TimeSpan TokenFresh = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan ExpiryWarning = TimeSpan.FromDays(14);

    public sealed record HostGitAccess(string HostId, string Status, string? Detail, DateTime? CheckedAt);

    public sealed record Sources(
        GitHubAppStatus App,
        IReadOnlyList<GitHubAppHostKey> HostKeys,
        SecretListing Secrets,
        IReadOnlyList<RunnerServiceIdentity> Runners,
        IReadOnlyList<RunnerCapabilitySnapshotDto> Hosts,
        IReadOnlyList<HostGitAccess> HostGit);

    /// <summary>Reads every local source and projects them; the Credentials page and the security view share it.</summary>
    public static async Task<CredentialInventoryDto> ReadAsync(GitHubAppBroker broker, GitHubAppHostKeys hostKeys,
        ISecretProvider secrets, AccessSecurityStore security, AgentStudio.Runner.V1ReviewExecutorRegistry hosts,
        AgentStudio.Clients.ClientIdentityStore clients, DateTime now, CancellationToken ct)
        => Project(new Sources(
            await broker.StatusAsync(false, ct),
            hostKeys.List(),
            await secrets.ListAsync(null, ct),
            security.ListRunners(),
            hosts.ListCapabilitySnapshots(),
            clients.ListAll()
                .Where(client => client.RunnerGitStatus is not null && client.Kind != AgentStudio.Shared.ClientIdentityKind.Retired)
                .Select(client => new HostGitAccess(
                    client.Id, client.RunnerGitStatus!, client.RunnerGitDetail, client.RunnerGitCheckedAt))
                .ToArray()), now);

    public static CredentialInventoryDto Project(Sources sources, DateTime now)
    {
        var items = new List<CredentialInventoryItemDto>();
        AddApp(items, sources.App);
        AddHostTokens(items, sources.App, now);
        AddHostKeys(items, sources.App, sources.HostKeys);
        AddSecrets(items, sources.Secrets);
        AddServiceIdentities(items, sources.Runners, now);
        AddProviderLogins(items, sources.Hosts);
        AddHostGit(items, sources.HostGit);
        return new CredentialInventoryDto(now, items);
    }

    private static void AddApp(List<CredentialInventoryItemDto> items, GitHubAppStatus app)
    {
        var repositories = app.Repositories
            .Where(item => item.State is not (GitHubAppCoverageStates.NoRepositoryUrl or GitHubAppCoverageStates.NotGitHub))
            .ToArray();
        items.Add(Item($"github-app:{app.AppId}", "service", StudioHost, "github_app",
            app.Connected ? $"GitHub App {app.Name}" : "GitHub App",
            app.Connected && app.Problem is null ? "ok" : "missing", repositories.Length > 0,
            app.Problem ?? (app.Connected
                ? $"Owned by {app.OwnerLogin}, {app.Installations.Count} confirmed installation(s). Its private key and client secret are in the secret store."
                : "No GitHub App is connected, so hosts use what is configured on them. Connect it under Settings > GitHub.")));
        foreach (var repository in repositories)
        {
            var name = GitHubRepositoryName.TryParse(repository.RepositoryUrl);
            items.Add(Item($"github-app-repository:{repository.Project}", "repository", "github.com", "github_app_installation",
                repository.Project, repository.State == GitHubAppCoverageStates.Covered ? "ok" : "missing", true,
                repository.State == GitHubAppCoverageStates.Covered
                    ? "The App reaches this repository."
                    : repository.Detail ?? "The App does not cover this repository yet.") with
            {
                Repository = name is null ? repository.RepositoryUrl : $"{name.Owner}/{name.Name}",
                Permission = RepositoryAccess.Write,
            });
        }
    }

    private static void AddHostTokens(List<CredentialInventoryItemDto> items, GitHubAppStatus app, DateTime now)
    {
        foreach (var host in app.Hosts)
        {
            var age = now - host.LastSyncUtc;
            items.Add(Item($"github-app-token:{host.RunnerId}:{host.Access}", "repository", host.RunnerId, "github_app_token",
                "One-hour repository tokens",
                age <= TokenFresh ? "ok" : age <= TokenLifetime ? "expiring" : "missing", false,
                age <= TokenLifetime
                    ? $"{host.Repositories} repositories, renewed every five minutes."
                    : "This host stopped fetching tokens, and the ones it had are expired.") with
            {
                Permission = host.Access,
                LastUseAt = host.LastSyncUtc,
            });
        }
    }

    private static void AddHostKeys(List<CredentialInventoryItemDto> items, GitHubAppStatus app, IReadOnlyList<GitHubAppHostKey> keys)
    {
        foreach (var key in keys)
        {
            var sync = app.Hosts.FirstOrDefault(host => host.RunnerId == key.RunnerId && host.Access == key.Access);
            items.Add(Item($"host-key:{key.RunnerId}:{key.Access}", "service", key.RunnerId, "host_key",
                $"Host key ({key.Access})", app.Connected && sync is null ? "unused" : "ok", false,
                "Lets this host fetch repository tokens. The key stays on the host, this machine keeps only its hash.") with
            {
                Permission = key.Access,
                LastUseAt = sync?.LastSyncUtc,
            });
        }
    }

    private static void AddSecrets(List<CredentialInventoryItemDto> items, SecretListing secrets)
    {
        foreach (var record in secrets.Records)
        {
            var active = SecretVersionPolicy.CurrentVersion(record) is not null;
            items.Add(Item($"secret:{record.Reference}", "service", StudioHost, "secret", record.Name,
                !active ? "missing" : record.LastUsedAt is null ? "unused" : "ok", false,
                active ? $"{record.Reference} in the secret store." : $"{record.Reference} is revoked and has no active version.") with
            {
                Scopes = [record.Scope],
                LastUseAt = record.LastUsedAt,
            });
        }
        foreach (var unreadable in secrets.Unreadable)
        {
            items.Add(Item($"secret-unreadable:{unreadable.Id}", "service", StudioHost, "secret",
                unreadable.Name ?? unreadable.Id, "unexpected", false, $"The secret store cannot read this item: {unreadable.Problem}."));
        }
    }

    private static void AddServiceIdentities(List<CredentialInventoryItemDto> items, IReadOnlyList<RunnerServiceIdentity> runners, DateTime now)
    {
        foreach (var runner in runners.Where(runner => runner.RevokedAt is null && runner.RetiredAt is null))
        foreach (var credential in runner.Credentials.Where(credential => credential.RevokedAt is null))
        {
            var state = credential.ExpiresAt is not { } expiry ? "ok"
                : expiry <= now ? "missing"
                : expiry - now <= ExpiryWarning ? "expiring"
                : "ok";
            items.Add(Item($"service-identity:{runner.Id}:{credential.Id}", "service", runner.Id, "service_credential",
                $"{runner.Name} service credential", state, false,
                "Service credential of an enrolled identity. This machine keeps only its hash.") with
            {
                Scopes = credential.Scopes,
                ExpiresAt = credential.ExpiresAt,
                LastUseAt = credential.LastUsedAt,
            });
        }
    }

    private static void AddProviderLogins(List<CredentialInventoryItemDto> items, IReadOnlyList<RunnerCapabilitySnapshotDto> hosts)
    {
        const string prefix = "provider-auth:";
        foreach (var host in hosts)
        foreach (var capability in host.Capabilities.Where(item => item.Key.StartsWith(prefix, StringComparison.Ordinal)))
        {
            var provider = capability.Key[prefix.Length..];
            var healthy = capability.IsFresh && capability.HealthState == "healthy";
            items.Add(Item($"provider-login:{host.RunnerId}:{provider}", "provider", host.HostId,
                provider == "codex" ? "codex_chatgpt_login" : provider + "_login",
                $"{provider} login on {host.Name}", healthy ? "ok" : "missing", false,
                capability.Reason ?? capability.Detail ?? (healthy ? "The host reports a working login." : "The host reports no working login.")) with
            {
                ExpiresAt = capability.ExpiresAt,
                LastUseAt = capability.AdvertisedAt,
            });
        }
    }

    private static void AddHostGit(List<CredentialInventoryItemDto> items, IReadOnlyList<HostGitAccess> hosts)
    {
        foreach (var host in hosts)
        {
            items.Add(Item($"host-git:{host.HostId}", "repository", host.HostId, "host_git_access", "Git access of this host",
                host.Status == "ready" ? "ok" : host.Status == "ready-no-workflow-scope" ? "expiring" : "missing", false,
                host.Detail ?? "The host's own probe of what it can push.") with
            {
                Permission = host.Status == "read-only" ? RepositoryAccess.Read : RepositoryAccess.Write,
                LastUseAt = host.CheckedAt,
            });
        }
    }

    private static CredentialInventoryItemDto Item(
        string id, string category, string hostId, string kind, string label, string state, bool required, string detail)
        => new(id, category, hostId, kind, label, [], null, null, null, null, state, required, detail, []);
}
