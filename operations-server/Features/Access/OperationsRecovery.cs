using System.Security.Cryptography;
using System.Text.Json;
using AgentStudio.Operations.Contracts;
using AgentStudio.Operations.Server.Features.Dispatch;

namespace AgentStudio.Operations.Server.Features.Access;

/// <summary>Offline host recovery. The marker is written before restore and removed only after credentials are fenced.</summary>
public static class OperationsRecovery
{
    public const string FileName = "recovery.json";

    public static OperationsRecoveryDocument Fence(string root, string marker, string backupId)
    {
        if (!File.Exists(marker)) throw new InvalidDataException("The restore recovery marker is missing.");
        var principalsPath = Path.Combine(root, "principals", "principals.json");
        var agentTokenPath = Path.Combine(root, "agent-secret", "token");
        var clientTokenPath = Path.Combine(root, "client-secret", "token");
        var data = Path.Combine(root, "data");
        foreach (var path in new[] { principalsPath, agentTokenPath, clientTokenPath, Path.Combine(data, "operations.db") })
            if (!File.Exists(path)) throw new InvalidDataException($"Operations recovery is missing {Path.GetFileName(path)}.");
        var document = JsonSerializer.Deserialize<OperationsAccessDocument>(File.ReadAllText(principalsPath), OperationsProtocol.Json)
            ?? throw new InvalidDataException("Operations principals are missing.");
        var access = new OperationsAccess(principalsPath, Path.Combine(data, OperationsAccess.RevocationsFileName));
        if (access.Authenticate(File.ReadAllText(agentTokenPath).Trim())?.Id != "container-agent"
            || access.Authenticate(File.ReadAllText(clientTokenPath).Trim())?.Id != "diagnostics-client")
            throw new InvalidDataException("Operations credentials do not match the restored principals.");

        var remote = document.Principals.Where(p => p.Kind == "agent" && p.Id != "container-agent" && !p.Revoked)
            .SelectMany(p => p.AgentIds.Select(id => (AgentId: id, p.Id)))
            .Where(item => item.AgentId != "container-agent")
            .GroupBy(item => item.AgentId, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToArray();
        var items = remote.SelectMany(group => new[]
        {
            new OperationsRecoveryItem("reenrol", group.Key, false, group.Select(item => item.Id).Distinct().ToArray()),
            new OperationsRecoveryItem("agent-revocation", group.Key, false, group.Select(item => item.Id).Distinct().ToArray()),
        }).Concat(new[]
        {
            new OperationsRecoveryItem("host-certificate-revocations", "installation", false, []),
            new OperationsRecoveryItem("operator-revocations", "installation", false, []),
        }).ToArray();
        var fenced = document with { Principals = document.Principals.Select(principal => principal with
        {
            Revoked = principal.Revoked || principal.Id is not ("container-agent" or "diagnostics-client"),
            TokenSha256 = principal.Id switch
            {
                "container-agent" => OperationsProtocol.Digest(Rotate(agentTokenPath, groupReadable: true)),
                "diagnostics-client" => OperationsProtocol.Digest(Rotate(clientTokenPath, groupReadable: true)),
                _ => principal.TokenSha256,
            },
        }).ToArray() };
        WriteAtomic(principalsPath, fenced);
        // A queued command from the old host must never reach a newly enrolled agent.
        new OperationsStore(data).TransactionAsync(state =>
        {
            foreach (var agent in remote) state.Agents.Remove(agent.Key);
            foreach (var attempt in state.Attempts.Values.ToArray())
            {
                if (attempt.State is not ("queued" or "running" or "cancelling")) continue;
                state.Attempts[attempt.Id] = attempt with
                {
                    State = "cancelled",
                    Events = [.. attempt.Events, new OperationEvent(attempt.Events.LongLength + 1,
                        DateTimeOffset.UtcNow, "host-restored", attempt.Command.InputDigest)],
                };
            }
            return Task.FromResult(0);
        }).GetAwaiter().GetResult();
        var recovery = new OperationsRecoveryDocument(backupId, DateTimeOffset.UtcNow.ToString("O"), items);
        WriteAtomic(Path.Combine(data, FileName), recovery);
        File.Delete(marker);
        return recovery;
    }

    public static OperationsRecoveryDocument Read(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, FileName);
        var document = JsonSerializer.Deserialize<OperationsRecoveryDocument>(File.ReadAllText(path), OperationsProtocol.Json);
        if (document?.Items is null || string.IsNullOrWhiteSpace(document.BackupId)
            || !DateTimeOffset.TryParse(document.FencedAt, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out _)
            || document.Items.Any(item => item is null || string.IsNullOrWhiteSpace(item.Kind)
                || item.Kind is not ("reenrol" or "agent-revocation" or "host-certificate-revocations" or "operator-revocations")
                || string.IsNullOrWhiteSpace(item.Id) || item.OldPrincipalIds is null)
            || document.Items.Select(item => (item.Kind, item.Id)).Distinct().Count() != document.Items.Length)
            throw new InvalidDataException("Operations recovery state is invalid.");
        return document;
    }

    public static bool BlocksOperatorAccess(string dataDirectory, string marker)
    {
        if (File.Exists(marker)) return true;
        var path = Path.Combine(dataDirectory, FileName);
        if (!File.Exists(path)) return false;
        try { return Read(dataDirectory).Items.Any(item => !item.Complete); }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException) { return true; }
    }

    public static void Complete(string root, string kind, string id)
    {
        var data = Path.Combine(root, "data");
        var recovery = Read(data);
        var item = recovery.Items.SingleOrDefault(item => item.Kind == kind && item.Id == id)
            ?? throw new InvalidDataException("That recovery check is not outstanding.");
        var accessPath = Path.Combine(root, "principals", "principals.json");
        var principals = JsonSerializer.Deserialize<OperationsAccessDocument>(File.ReadAllText(accessPath), OperationsProtocol.Json)?.Principals
            ?? throw new InvalidDataException("Operations principals are missing.");
        if (kind == "reenrol" && !principals.Any(p => p.Kind == "agent" && !p.Revoked
                && !item.OldPrincipalIds.Contains(p.Id, StringComparer.Ordinal) && p.AgentIds.Contains(id, StringComparer.Ordinal)
                && !principals.Where(old => item.OldPrincipalIds.Contains(old.Id, StringComparer.Ordinal))
                    .Any(old => old.TokenSha256.Equals(p.TokenSha256, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException($"Agent {id} has no new active principal.");
        if (kind == "reenrol")
        {
            var fencedAt = DateTimeOffset.Parse(recovery.FencedAt, System.Globalization.CultureInfo.InvariantCulture);
            var registered = new OperationsStore(data).TransactionAsync(state => Task.FromResult(
                state.Agents.TryGetValue(id, out var agent) && agent.LastSeen > fencedAt)).GetAwaiter().GetResult();
            if (!registered) throw new InvalidDataException($"Agent {id} has not registered after recovery fencing.");
        }
        if (kind == "agent-revocation" && principals.Any(p => item.OldPrincipalIds.Contains(p.Id, StringComparer.Ordinal) && !p.Revoked))
            throw new InvalidDataException($"Old credentials for {id} are active.");
        WriteAtomic(Path.Combine(data, FileName), recovery with { Items = recovery.Items.Select(check =>
            check == item ? check with { Complete = true } : check).ToArray() });
    }

    private static string Rotate(string path, bool groupReadable)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        var temp = path + ".recovery-tmp";
        File.WriteAllText(temp, token);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite
            | (groupReadable ? UnixFileMode.GroupRead : 0));
        File.Move(temp, path, true);
        return token;
    }

    private static void WriteAtomic<T>(string path, T document)
    {
        var temp = path + ".recovery-tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(document, OperationsProtocol.Json));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        File.Move(temp, path, true);
    }
}
