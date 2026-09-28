using System.Security.Cryptography;
using System.Text.Json;
using AgentStudio.Operations.Contracts;

namespace AgentStudio.Operations.Server.Features.Access;

public sealed record OperationsPrincipal(string Id, string Audience, string TokenSha256, string Kind,
    string[] Scopes, string[] AgentIds, string[] Roots, bool Revoked = false);
public sealed record OperationsAccessDocument(OperationsPrincipal[] Principals);

public sealed class OperationsAccess(string path)
{
    // Reload atomically replaced files on every request: revocation and rotation
    // cannot be delayed by a process-lifetime credential cache.
    public OperationsPrincipal? Authenticate(string token)
    {
        var document = JsonSerializer.Deserialize<OperationsAccessDocument>(File.ReadAllText(path), OperationsProtocol.Json)
                       ?? throw new InvalidDataException("Operations principal configuration is empty.");
        if (document.Principals is null || document.Principals.Length > 1000
            || document.Principals.Any(p => p is null || string.IsNullOrWhiteSpace(p.Id)
                || p.TokenSha256 is null || p.Scopes is null || p.AgentIds is null || p.Roots is null
                || p.Kind is not ("service" or "agent"))
            || document.Principals.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != document.Principals.Length
            || document.Principals.Select(p => p.TokenSha256).Distinct(StringComparer.OrdinalIgnoreCase).Count() != document.Principals.Length)
            throw new InvalidDataException("Operations principal configuration is invalid.");
        var digest = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
        return document.Principals.SingleOrDefault(p => !p.Revoked && p.Audience == OperationsProtocol.Audience
            && p.TokenSha256.Length == 64
            && p.TokenSha256.All(Uri.IsHexDigit)
            && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(p.TokenSha256), digest));
    }
}

public static class OperationsAccessPolicy
{
    public static string? Denial(OperationsPrincipal principal, string role, string scope, string? agentId = null)
    {
        if (principal.Kind != role || !principal.Scopes.Contains(scope, StringComparer.Ordinal))
            return "insufficient-scope";
        if (agentId is not null && !principal.AgentIds.Contains(agentId, StringComparer.Ordinal))
            return "agent-identity-mismatch";
        return null;
    }
}
