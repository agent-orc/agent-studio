using System.Security.Cryptography;
using System.Text.Json;
using AgentStudio.Operations.Contracts;

namespace AgentStudio.Operations.Server.Features.Access;

/// <summary>
/// One bearer principal. <paramref name="OperatorAgentId"/> names the Studio
/// operator agent a principal belongs to (security model SM-2); the control
/// plane's kill switch revokes every principal that names one.
/// </summary>
public sealed record OperationsPrincipal(string Id, string Audience, string TokenSha256, string Kind,
    string[] Scopes, string[] AgentIds, string[] Roots, bool Revoked = false, string? OperatorAgentId = null);
public sealed record OperationsAccessDocument(OperationsPrincipal[] Principals);

/// <summary>
/// Operator principals the kill switch revoked, kept apart from the principal
/// file so that file can stay read-only: this server can add revocations but
/// never grant access. An operator agent once revoked stays revoked, including
/// principals added for it later.
/// </summary>
public sealed record OperatorRevocationDocument(string[] RevokedPrincipalIds, string[] RevokedOperatorAgentIds);

public sealed class OperationsAccess(string path, string? revocationsPath = null)
{
    public const string RevocationsFileName = "operator-revocations.json";
    private static readonly object WriteGate = new();

    // Reload atomically replaced files on every request: revocation and rotation
    // cannot be delayed by a process-lifetime credential cache.
    public OperationsPrincipal? Authenticate(string token)
    {
        var document = Read();
        var revoked = ReadRevocations();
        var digest = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
        return document.Principals.SingleOrDefault(p => !p.Revoked && p.Audience == OperationsProtocol.Audience
            && !IsRevoked(p, revoked)
            && p.TokenSha256.Length == 64
            && p.TokenSha256.All(Uri.IsHexDigit)
            && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(p.TokenSha256), digest));
    }

    /// <summary>
    /// Revokes operator principals (security model SM-2): with
    /// <paramref name="all"/> every principal that names an operator agent,
    /// otherwise those naming one of <paramref name="operatorAgentIds"/>. The
    /// revocation list is replaced atomically, so the next request already sees
    /// it. Returns the ids of the matching principals, revoked now or before.
    /// </summary>
    public string[] RevokeOperators(IReadOnlyCollection<string> operatorAgentIds, bool all)
    {
        var target = revocationsPath ?? throw new InvalidOperationException("Operations:RevocationsFile is required to revoke operator principals.");
        lock (WriteGate)
        {
            var document = Read();
            var matched = document.Principals.Where(principal => principal.OperatorAgentId is { Length: > 0 } owner
                && (all || operatorAgentIds.Contains(owner, StringComparer.Ordinal))).ToArray();
            var current = ReadRevocations();
            var next = new OperatorRevocationDocument(
                [.. current.RevokedPrincipalIds.Union(matched.Select(principal => principal.Id), StringComparer.Ordinal).Order(StringComparer.Ordinal)],
                [.. current.RevokedOperatorAgentIds
                    .Union(all ? matched.Select(principal => principal.OperatorAgentId!) : operatorAgentIds, StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)]);
            if (!next.RevokedPrincipalIds.SequenceEqual(current.RevokedPrincipalIds)
                || !next.RevokedOperatorAgentIds.SequenceEqual(current.RevokedOperatorAgentIds))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
                var temp = target + ".tmp";
                var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Options = FileOptions.WriteThrough };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using (var stream = new FileStream(temp, options))
                {
                    JsonSerializer.Serialize(stream, next, OperationsProtocol.Json);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temp, target, overwrite: true);
            }
            return [.. matched.Select(principal => principal.Id)];
        }
    }

    private static bool IsRevoked(OperationsPrincipal principal, OperatorRevocationDocument revoked)
        => revoked.RevokedPrincipalIds.Contains(principal.Id, StringComparer.Ordinal)
           || (principal.OperatorAgentId is { } owner && revoked.RevokedOperatorAgentIds.Contains(owner, StringComparer.Ordinal));

    private OperatorRevocationDocument ReadRevocations()
    {
        if (revocationsPath is null || !File.Exists(revocationsPath)) return new([], []);
        var document = JsonSerializer.Deserialize<OperatorRevocationDocument>(File.ReadAllText(revocationsPath), OperationsProtocol.Json);
        if (document?.RevokedPrincipalIds is null || document.RevokedOperatorAgentIds is null)
            throw new InvalidDataException("Operator revocation list is invalid.");
        return document;
    }

    private OperationsAccessDocument Read()
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
        return document;
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

    /// <summary>Only a control plane service principal revokes operator principals; an operator principal never does.</summary>
    public static string? RevocationDenial(OperationsPrincipal principal)
        => principal.OperatorAgentId is not null ? "operator-principal" : Denial(principal, "service", OperatorPrincipalRevocation.Scope);
}
