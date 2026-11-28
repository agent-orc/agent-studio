using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Host;
using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Cli;

/// <summary>Projects Codex model availability from the fleet that executes runs and reviews.</summary>
internal static class CodexExecutionModelCatalogue
{
    public static async Task<CliModelCatalog> GetAsync(
        IConfiguration configuration, IHttpClientFactory clients,
        V1ReviewExecutorRegistry registry, CancellationToken ct)
    {
        if (!TaskServerPlaneProxy.IsConfigured(configuration))
            return Build(registry.ListCapabilitySnapshots(), DateTime.UtcNow);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var hosts = await clients.CreateClient(TaskServerPlaneProxy.ClientName)
                .GetFromJsonAsync<RunnerCapabilitySnapshotDto[]>("/api/v1/management/remote-hosts", deadline.Token);
            return Build(hosts ?? [], DateTime.UtcNow);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested
            && ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            return Build([], DateTime.UtcNow);
        }
    }

    internal static CliModelCatalog Build(IReadOnlyList<RunnerCapabilitySnapshotDto> hosts, DateTime now)
    {
        var cliKey = CapabilityProtocol.CliExecution(CliTypes.Codex);
        var acceptancePrefix = CapabilityProtocol.ModelAcceptance(CliTypes.Codex, "_")[..^1];
        var advertised = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var host in hosts.Where(host => host.Status == "active" && host.LastSeenAt > now.AddMinutes(-3)))
        {
            var cli = host.Capabilities.FirstOrDefault(capability => capability.Key == cliKey && Ready(capability, now));
            if (cli is null) continue;

            var acceptance = host.Capabilities
                .Where(capability => capability.Key.StartsWith(acceptancePrefix, StringComparison.Ordinal)
                    && capability.FreshUntil > now)
                .ToDictionary(capability => capability.Key[acceptancePrefix.Length..],
                    capability => Ready(capability, now), StringComparer.OrdinalIgnoreCase);
            advertised.UnionWith(acceptance.Where(entry => entry.Value).Select(entry => entry.Key));
            advertised.UnionWith((cli.SupportedModels ?? [])
                .Select(ModelMetadataRegistry.NormalizeId)
                .Where(model => !acceptance.ContainsKey(model)));
        }

        var models = ModelMetadataRegistry.ForVendor("openai")
            .Select(model => ModelMetadataRegistry.ToCliModelInfo(model, CliTypes.Codex))
            .ToDictionary(model => model.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var id in advertised.Where(id => id != CodexCliRelease.Gpt61Terra))
            models.TryAdd(id, ModelMetadataRegistry.UnknownCliModel(id, null, "openai", CliTypes.Codex));

        return new CliModelCatalog
        {
            Source = "execution-hosts",
            FetchedAt = now,
            Models = models.Values.Select(model =>
            {
                var available = advertised.Contains(model.Id) && !model.Deprecated;
                var superseded = !CodexRoutingPolicy.IsGpt62(model.Id)
                    && (ModelMetadataRegistry.Find(model.Id) is not null || model.Id == ModelIds.Gpt56Sol);
                return model with
                {
                    Available = available,
                    AvailabilityNote = (available, superseded) switch
                    {
                        (true, true) => "Superseded for automatic routing. Available as an explicit pin.",
                        (true, false) => null,
                        (false, true) => "Superseded for automatic routing. No active execution host advertises this model.",
                        _ => "No active execution host advertises this model.",
                    },
                };
            }).OrderByDescending(model => model.IsDefault).ToList()
        };
    }

    private static bool Ready(CapabilityHealthDto capability, DateTime now)
        => capability.FreshUntil > now && capability.AdvertisedStatus is "ready" or "degraded"
            && capability.HealthState is not ("draining" or "blocked" or "held");
}
