using System.Security.Cryptography;
using System.Text;

namespace AgentStudio.Registry;

/// <summary>Short lived, project-scoped access to assets requested by an opaque preview document.</summary>
public sealed class ProjectUrlPreviewAccess(TimeProvider clock)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    public string Issue(string projectId, string urlId)
    {
        var expiry = clock.GetUtcNow().Add(Lifetime).ToUnixTimeSeconds();
        var payload = $"{projectId}:{urlId}:{expiry}";
        var signature = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(payload));
        return $"{expiry:x}-{Convert.ToHexString(signature)}";
    }

    public bool Allows(string? token, string projectId, string urlId)
    {
        if (token is null) return false;
        var separator = token.IndexOf('-');
        if (separator < 1 || token.Length - separator - 1 != 64
            || !long.TryParse(token[..separator], System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var expiry)
            || expiry < clock.GetUtcNow().ToUnixTimeSeconds()) return false;
        byte[] supplied;
        try { supplied = Convert.FromHexString(token[(separator + 1)..]); }
        catch (FormatException) { return false; }
        var payload = $"{projectId}:{urlId}:{expiry}";
        var expected = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(payload));
        return CryptographicOperations.FixedTimeEquals(supplied, expected);
    }

    public bool AllowsAssetRequest(HttpRequest request)
    {
        if (!HttpMethods.IsGet(request.Method) || request.Headers.Origin is not { Count: 0 or 1 }
            || request.Headers.Origin.Count == 1 && request.Headers.Origin[0] != "null") return false;
        var parts = request.Path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts is { Length: >= 8 }
            && parts[0] == "api" && parts[1] == "projects"
            && parts[3] == "urls" && parts[5] == "preview" && parts[6] == "_asset"
            && Allows(parts[7], parts[2], parts[4]);
    }
}
