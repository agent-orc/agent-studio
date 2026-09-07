using System.Text.Json;
using AgentStudio.Bus;
using AgentStudio.Runner;

namespace AgentStudio.Clients;

public sealed record ClientAttemptAuthorityFacts(bool HasActiveLease, bool HasProcessUnknownAttempt);

public sealed record PurgeRetiredClientsRequest(string? NamePrefix = null, bool DryRun = true);

public sealed record RetiredClientDeletionItem(string Id, string Name, bool CanDelete, string? BlockedBy);

public sealed record PurgeRetiredClientsResponse(
    bool DryRun,
    string? NamePrefix,
    IReadOnlyList<RetiredClientDeletionItem> Clients,
    int DeletedCount);

public sealed record ClientIdentityDeletionResult(
    bool Found,
    bool Deleted,
    ClientIdentity? Identity,
    string? Error,
    string? Message);

public static class ClientIdentityDeletionPolicy
{
    public static string? BlockedBy(ClientIdentity identity, ClientAttemptAuthorityFacts authority, DateTime now)
    {
        if (identity.Kind != ClientIdentityKind.Retired) return "client-must-be-retired-before-delete";
        if (string.Equals(identity.RunnerDaemonState, "running", StringComparison.OrdinalIgnoreCase)
            && identity.LastSeenAt is { } seen && now - seen <= TimeSpan.FromSeconds(90))
            return "runner-online";
        if (authority.HasProcessUnknownAttempt) return "process-unknown-attempt";
        if (identity.RunnerActiveSlots.GetValueOrDefault() > 0 || authority.HasActiveLease)
            return "active-lease";
        return null;
    }
}

public sealed class ClientIdentityDeletionService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ClientIdentityStore _identities;
    private readonly AttemptAuthorityService _attempts;
    private readonly AgentMessageBusBridge _bus;
    private readonly IConfiguration _configuration;
    private readonly object _auditGate = new();

    public ClientIdentityDeletionService(
        ClientIdentityStore identities,
        AttemptAuthorityService attempts,
        AgentMessageBusBridge bus,
        IConfiguration configuration)
    {
        _identities = identities;
        _attempts = attempts;
        _bus = bus;
        _configuration = configuration;
    }

    public ClientIdentityDeletionResult Delete(string id, string actor)
    {
        var identity = _identities.Find(id);
        if (identity is null) return new(false, false, null, "client-not-found", "The client identity was not found.");
        var blocked = ClientIdentityDeletionPolicy.BlockedBy(identity, _attempts.InspectClientAuthority(id), DateTime.UtcNow);
        if (blocked is not null)
            return new(true, false, identity, blocked, MessageFor(blocked, identity.DisplayName));

        if (!_identities.PermanentlyDelete(id))
            return new(true, false, identity, "delete-failed", "The retired client identity could not be deleted.");

        AppendAudit(identity, actor);
        _ = _bus.EmitClientIdentityDeletedAsync(identity.Id, identity.DisplayName, actor);
        return new(true, true, identity, null, null);
    }

    public PurgeRetiredClientsResponse Purge(PurgeRetiredClientsRequest request, string actor)
    {
        var prefix = string.IsNullOrWhiteSpace(request.NamePrefix) ? null : request.NamePrefix.Trim();
        var matches = _identities.ListAll()
            .Where(identity => identity.Kind == ClientIdentityKind.Retired)
            .Where(identity => prefix is null
                || identity.DisplayName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || identity.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(identity => identity.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(identity => identity.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var items = new List<RetiredClientDeletionItem>(matches.Count);
        var deleted = 0;
        foreach (var identity in matches)
        {
            var blocked = ClientIdentityDeletionPolicy.BlockedBy(
                identity, _attempts.InspectClientAuthority(identity.Id), DateTime.UtcNow);
            if (!request.DryRun && blocked is null)
            {
                var result = Delete(identity.Id, actor);
                blocked = result.Error;
                if (result.Deleted) deleted++;
            }
            items.Add(new(identity.Id, identity.DisplayName, blocked is null, blocked));
        }
        return new(request.DryRun, prefix, items, deleted);
    }

    private void AppendAudit(ClientIdentity identity, string actor)
    {
        var root = _configuration["TaskRepository"] ?? Path.Combine(AppContext.BaseDirectory, "workspace");
        var path = Path.Combine(root, ".audit", "client-identities.jsonl");
        var row = JsonSerializer.Serialize(new
        {
            timestamp = DateTime.UtcNow.ToString("O"),
            kind = "client-identity-deleted",
            clientId = identity.Id,
            displayName = identity.DisplayName,
            actor,
        }, Json);
        lock (_auditGate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, row + Environment.NewLine);
        }
    }

    private static string MessageFor(string error, string name) => error switch
    {
        "runner-online" => $"Runner '{name}' is online. Stop it before deleting the retired identity.",
        "active-lease" => $"Runner '{name}' still has an active lease.",
        "process-unknown-attempt" => $"Runner '{name}' holds an unresolved process-unknown attempt.",
        _ => $"Runner '{name}' must be retired before it can be deleted.",
    };
}
