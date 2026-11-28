using System.Net.Http.Json;
using AgentStudio.Host;
using AgentStudio.Secrets;
using AgentStudio.Security;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TaskServer.Contracts.GitHubApp;

namespace AgentStudio.GitHubApp;

/// <summary>Installation inventory joins host custody metadata with Studio-owned secrets and IAM.</summary>
public sealed class CredentialInventoryService(IServiceProvider services, IConfiguration configuration,
    IHttpClientFactory clients, CredentialLifecycleMonitor monitor, TimeProvider clock)
{
    public async Task<CredentialInventoryDto> ReadAsync(bool notify, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var security = services.GetRequiredService<AccessSecurityStore>();
        var secrets = services.GetRequiredService<ISecretProvider>();
        var backends = services.GetRequiredService<SecretBackendSettings>();
        CredentialInventoryDto inventory;
        if (TaskServerPlaneProxy.IsConfigured(configuration))
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            var client = clients.CreateClient(TaskServerPlaneProxy.ClientName);
            inventory = await client.GetFromJsonAsync<CredentialInventoryDto>("/api/v1/management/credential-inventory", deadline.Token)
                ?? throw new InvalidOperationException("Task Server returned no inventory.");
            var app = await client.GetFromJsonAsync<GitHubAppStatus>("/api/v1/management/github-app", deadline.Token)
                ?? throw new InvalidOperationException("Task Server returned no App metadata.");
            var appItems = LocalCredentialInventory.Project(new(app, [], new SecretListing([], []), [], [], []), now).Items;
            var supplemental = LocalCredentialInventory.Supplemental(await secrets.ListAsync(null, ct),
                security.ReadIamSnapshot(), backends.All(), now);
            inventory = new(now, [.. inventory.Items, .. appItems, .. supplemental]);
        }
        else
        {
            inventory = await LocalCredentialInventory.ReadAsync(services.GetRequiredService<GitHubAppBroker>(),
                services.GetRequiredService<GitHubAppHostKeys>(), secrets, security,
                services.GetRequiredService<AgentStudio.Runner.V1ReviewExecutorRegistry>(),
                services.GetRequiredService<AgentStudio.Clients.ClientIdentityStore>(), now, ct);
            inventory = new(now, [.. inventory.Items,
                .. LocalCredentialInventory.Supplemental(new SecretListing([], []), security.ReadIamSnapshot(), backends.All(), now)]);
        }
        var items = inventory.Items.DistinctBy(item => item.Id).ToDictionary(item => item.Id, StringComparer.Ordinal);
        foreach (var backend in backends.All())
        {
            if (backend.CredentialReference is not { } reference ||
                !items.TryGetValue($"secret:{SecretReference.Parse(reference).Unpinned()}", out var source) ||
                !items.TryGetValue($"external-store:{backend.Scope}", out var target)) continue;
            items[target.Id] = target with
            {
                Holder = source.Holder, CreatedAt = source.CreatedAt, LastUseAt = source.LastUseAt,
                RotationBasisAt = source.RotationBasisAt, RotationIntervalDays = source.RotationIntervalDays,
            };
        }
        return await monitor.CompleteAsync(inventory with { Items = items.Values.ToArray() }, notify, ct);
    }
}

public static class CredentialInventoryEndpoints
{
    public static void MapCredentialInventoryEndpoints(this WebApplication app)
    {
        if (SecurityProfiles.IsPublicDemo(app.Configuration)) return;
        app.MapGet("/api/v1/management/credential-inventory", async Task<IResult> (HttpContext context,
            CredentialInventoryService inventory, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try { return Results.Ok(await inventory.ReadAsync(false, ct)); }
            catch (Exception exception) when (exception is HttpRequestException or SecretBackendException
                or SecretKeyUnavailableException or TaskCanceledException)
            {
                return Results.Json(new { error = "inventory-unavailable", message = "Credential metadata could not be read." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        }).RequireStudioRole(StudioRoles.Owner);
    }
}

/// <summary>Hourly threshold check, including immediately after startup. Only metadata enters the feed.</summary>
public sealed class CredentialLifecycleHostedService(CredentialInventoryService inventory, IConfiguration configuration,
    TimeProvider clock, ILogger<CredentialLifecycleHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (SecurityProfiles.IsPublicDemo(configuration)) return;
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), clock);
        do
        {
            try { await inventory.ReadAsync(true, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogWarning("Credential lifecycle sweep failed ({FailureType}); retry on the next tick", exception.GetType().Name);
            }
        }
        while (await TickAsync(timer, stoppingToken));
    }

    private static async Task<bool> TickAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }
    }
}
