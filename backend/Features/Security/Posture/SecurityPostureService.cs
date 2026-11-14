using System.Net.Http.Json;
using AgentStudio.GitHubApp;
using AgentStudio.Host;
using AgentStudio.Runner;
using AgentStudio.Runtime;
using AgentStudio.Secrets;
using AgentStudio.Shared;
using AgentStudio.Tasks;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TaskServer.Contracts.GitHubApp;

namespace AgentStudio.Security;

/// <summary>
/// Collects <see cref="SecurityPostureFacts"/> from the stores this
/// installation already keeps and projects them with
/// <see cref="SecurityPostureProjection"/>. Host facts (capability snapshots
/// and the credential inventory) come from this API when it serves the v1
/// plane itself, else from the standalone Task Server within
/// <see cref="TaskServerDeadline"/>; a source that does not answer is reported
/// as unavailable instead of failing the view.
/// </summary>
public sealed class SecurityPostureService(
    IServiceProvider services,
    IConfiguration configuration,
    AccessSecurityStore store,
    ControlPlaneAuditMonitor auditMonitor,
    OperatorKillSwitch killSwitch,
    OperatorKillSwitchOptions killSwitchOptions,
    SecretAuditLog secretAudit,
    TaskScannerService scanner,
    BuildIdentity build,
    StableReleaseIdentity stable,
    NetworkedLoginAddressHealth loginAddressHealth,
    EndpointDataSource endpoints,
    IHttpClientFactory clients,
    TimeProvider clock,
    ILogger<SecurityPostureService> logger)
{
    public const string SecurityTag = "security";
    public static readonly TimeSpan TaskServerDeadline = TimeSpan.FromSeconds(5);
    private const int AuditScanLimit = 2000;

    public async Task<SecurityPostureResponse> ReadAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var since = now.AddDays(-SecurityPostureProjection.AuditWindowDays);
        var iam = store.ReadIamSnapshot();
        var proxied = TaskServerPlaneProxy.IsConfigured(configuration);
        var (hosts, hostSource, hostProblem) = await HostsAsync(proxied, ct);
        var (inventory, inventorySource, inventoryProblem) = await InventoryAsync(proxied, now, ct);
        var kills = killSwitch.Status(killSwitchOptions.DrillIntervalHours);
        return SecurityPostureProjection.Build(new SecurityPostureFacts
        {
            Now = now,
            Profile = SecurityProfiles.ActiveProfile(configuration),
            CloudflareEdge = SecurityProfiles.PublicCloudflareEdgeEnabled(configuration),
            TrustedProxyCount = (configuration["Security:TrustedProxyAddresses"] ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length,
            LoginAddressHealthy = loginAddressHealth.Healthy,
            AnonymousRoutes = AnonymousRoutes(endpoints),
            ApiTokens = store.ListApiTokens(),
            ServiceAccounts = iam.ServiceAccounts
                .Where(account => account.Origin is null && account.Purpose is null)
                .Select(account => ServiceAccountFact(account, iam.Bindings))
                .ToArray(),
            OperatorAgents = store.ListOperatorAgents()
                .Select(agent => new OperatorAgentFact(agent.Id, agent.Name, agent.DisabledAt is not null, agent.Sessions ?? []))
                .ToArray(),
            Inventory = inventory,
            InventorySource = inventorySource,
            InventoryProblem = inventoryProblem,
            Users = iam.Users,
            Server = new SecurityPostureServerVersion(build.Version, build.Tag, build.Commit, build.BuiltAt, !build.Legacy),
            Hosts = hosts,
            HostSource = hostSource,
            HostProblem = hostProblem,
            Stable = stable,
            Integrity = auditMonitor.Integrity,
            Forwarding = auditMonitor.Forwarding,
            OpenAuditAlerts = OpenAlerts(),
            RecentAudit = store.ControlPlaneAudit.ReadNewestSince(
                since, SecurityPostureProjection.IsAnomaly, AuditScanLimit),
            RecentSecretAudit = secretAudit.Read(null, AuditScanLimit),
            Kills = kills.Kills,
            Drills = kills.Drills,
            DrillIntervalHours = kills.DrillIntervalHours,
            BreakGlassWindows = store.ListBreakGlassWindows(),
            SecurityCards = scanner.ScanAllJobs()
                .Where(card => !card.Fixture && card.Tags.Any(tag => tag.Equals(SecurityTag, StringComparison.OrdinalIgnoreCase)))
                .ToArray(),
        });
    }

    private IReadOnlyList<string> OpenAlerts()
        => new[]
            {
                ControlPlaneAuditAlertCodes.ChainBroken, ControlPlaneAuditAlertCodes.SinkGap,
                ControlPlaneAuditAlertCodes.SinkMismatch, ControlPlaneAuditAlertCodes.SinkUnreachable,
            }
            .Where(auditMonitor.IsOpen)
            .ToArray();

    private static ServiceAccountFact ServiceAccountFact(ServiceAccount account, IReadOnlyList<RoleBinding> bindings)
    {
        var own = bindings
            .Where(binding => binding.PrincipalKind == IamPrincipalKind.ServiceAccount && binding.PrincipalId == account.Id)
            .ToArray();
        var role = own.Select(binding => IamRoles.Find(binding.Role))
            .Where(definition => definition is not null)
            .OrderByDescending(definition => definition!.Rank)
            .Select(definition => definition!.DisplayName)
            .FirstOrDefault() ?? "No role";
        var projects = own.Any(binding => binding.Scope.Kind == IamScopeKind.Workspace)
            ? ["*"]
            : own.Select(binding => binding.Scope.Project).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return new ServiceAccountFact(account.Id, account.Name, role, projects, account.DisabledAt is not null, account.Tokens);
    }

    /// <summary>Every mapped route open to anonymous callers, as <c>METHOD pattern</c>.</summary>
    internal static IReadOnlyList<string> AnonymousRoutes(EndpointDataSource endpoints)
        => endpoints.Endpoints.OfType<RouteEndpoint>()
            .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"])
                .Where(method => RoutePermissionCatalog.Resolve(endpoint, method)?.Permission == IamPermissions.Anonymous)
                .Select(method => $"{method} /{endpoint.RoutePattern.RawText?.TrimStart('/')}"))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private async Task<(IReadOnlyList<RunnerCapabilitySnapshotDto>, string, string?)> HostsAsync(bool proxied, CancellationToken ct)
    {
        if (!proxied)
            return (services.GetRequiredService<V1ReviewExecutorRegistry>().ListCapabilitySnapshots(), SecurityPostureSources.Local, null);
        var read = await TaskServerReadAsync<RunnerCapabilitySnapshotDto[]>("/api/v1/management/remote-hosts", ct);
        return read.Value is { } value
            ? (value, SecurityPostureSources.TaskServer, null)
            : ([], SecurityPostureSources.Unavailable, read.Problem);
    }

    private async Task<(IReadOnlyList<CredentialInventoryItemDto>, string, string?)> InventoryAsync(
        bool proxied, DateTime now, CancellationToken ct)
    {
        if (SecurityProfiles.IsPublicDemo(configuration))
            return ([], SecurityPostureSources.Local, null);
        if (proxied)
        {
            var read = await TaskServerReadAsync<CredentialInventoryDto>("/api/v1/management/credential-inventory", ct);
            return read.Value is { } value
                ? (value.Items, SecurityPostureSources.TaskServer, null)
                : ([], SecurityPostureSources.Unavailable, read.Problem);
        }
        try
        {
            var inventory = await LocalCredentialInventory.ReadAsync(
                services.GetRequiredService<GitHubAppBroker>(), services.GetRequiredService<GitHubAppHostKeys>(),
                services.GetRequiredService<ISecretProvider>(), store, services.GetRequiredService<V1ReviewExecutorRegistry>(),
                services.GetRequiredService<AgentStudio.Clients.ClientIdentityStore>(), now, ct);
            return (inventory.Items, SecurityPostureSources.Local, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Security posture: the local credential inventory could not be read");
            return ([], SecurityPostureSources.Unavailable, "The credential inventory could not be read on this server.");
        }
    }

    private async Task<(T? Value, string? Problem)> TaskServerReadAsync<T>(string path, CancellationToken ct) where T : class
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TaskServerDeadline);
        try
        {
            using var response = await clients.CreateClient(TaskServerPlaneProxy.ClientName).GetAsync(path, deadline.Token);
            if (!response.IsSuccessStatusCode)
                return (null, $"The Task Server answered {(int)response.StatusCode} for {path}.");
            return (await response.Content.ReadFromJsonAsync<T>(deadline.Token), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException
                                       || ex is TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Security posture: Task Server read {Path} failed", path);
            return (null, $"The Task Server did not answer {path}.");
        }
    }
}
