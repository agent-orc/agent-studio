using System.Text;
using System.Text.Json;

namespace AgentRunner;

public sealed record ProviderCredentialFreshness(
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? ModifiedAt,
    string Detail,
    DateTimeOffset? AccessTokenExpiresAt = null,
    string EffectiveSource = "unknown",
    bool NativeFileShadowed = false,
    string ExpiryProvenance = "unknown",
    string? CredentialGeneration = null);

/// <summary>
/// Discovers the effective source from the daemon environment before reading
/// expiry hints from a native store. The CLI remains the owner of native refresh.
/// Token values are never returned, logged, or copied. Unknown or changed file
/// formats degrade quietly and leave the active CLI status probe authoritative.
/// </summary>
public static class ProviderCredentialMonitor
{
    public static readonly TimeSpan ExpiryWarningWindow = TimeSpan.FromDays(14);

    private static readonly HashSet<string> ExpiryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "expiresAt", "expires_at", "expiry", "expires", "expiration",
    };

    private static readonly HashSet<string> JwtNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "accessToken", "access_token", "idToken", "id_token",
    };

    public static ProviderCredentialFreshness Inspect(
        string cliBinary,
        string? homeDirectory = null,
        IReadOnlyDictionary<string, string?>? daemonEnvironment = null)
    {
        var provider = RunnerCapabilityProbe.Provider(cliBinary);
        var home = homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string? Variable(string name) => daemonEnvironment is null
            ? Environment.GetEnvironmentVariable(name)
            : daemonEnvironment.GetValueOrDefault(name);
        var path = provider switch
        {
            "codex" => Path.Combine(Variable("CODEX_HOME") ?? Path.Combine(home, ".codex"), "auth.json"),
            "claude" => Path.Combine(Variable("CLAUDE_CONFIG_DIR") ?? Path.Combine(home, ".claude"), ".credentials.json"),
            _ => null,
        };
        var environmentSource = provider switch
        {
            "claude" when !string.IsNullOrWhiteSpace(Variable("CLAUDE_CODE_OAUTH_TOKEN")) ||
                          !string.IsNullOrWhiteSpace(Variable("ANTHROPIC_API_KEY")) => true,
            _ => false,
        };
        if (environmentSource)
            return new ProviderCredentialFreshness(
                null, null,
                "Daemon environment credential is active; native file metadata is shadowed.",
                EffectiveSource: "environment-file",
                NativeFileShadowed: path is not null && File.Exists(path));
        if (path is null)
            return new ProviderCredentialFreshness(null, null, "No credential metadata format is known for this provider.");
        if (provider == "codex" && !string.IsNullOrWhiteSpace(Variable("OPENAI_API_KEY")))
            return new ProviderCredentialFreshness(null, null,
                "Codex auth source is ambiguous in this daemon context; no native expiry is attributed to it.",
                EffectiveSource: "unknown");

        try
        {
            if (!File.Exists(path))
                return new ProviderCredentialFreshness(null, null, "No provider credential file was found; process authentication remains authoritative.", EffectiveSource: "absent");
            var modifiedAt = File.GetLastWriteTimeUtc(path);
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var accessExpiresAt = FindExpiry(document.RootElement, 0);
            return new ProviderCredentialFreshness(
                null,
                new DateTimeOffset(DateTime.SpecifyKind(modifiedAt, DateTimeKind.Utc)),
                accessExpiresAt is null
                    ? "Native login has no supported expiry metadata; session expiry is unknown."
                    : "Native access-token expiry is a refresh hint; session expiry is unknown.",
                AccessTokenExpiresAt: accessExpiresAt,
                EffectiveSource: "native-cli-store",
                ExpiryProvenance: accessExpiresAt is null ? "unknown" : "access-token-unverified",
                // The CLI rewrites the store whenever it installs a new secret;
                // the write time is an opaque version marker, never token material.
                CredentialGeneration: $"native-cli-store:{new DateTimeOffset(DateTime.SpecifyKind(modifiedAt, DateTimeKind.Utc)).ToUnixTimeMilliseconds()}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentOutOfRangeException)
        {
            return new ProviderCredentialFreshness(
                null,
                null,
                $"Credential freshness metadata is temporarily unreadable ({exception.GetType().Name}).",
                EffectiveSource: "native-cli-store");
        }
    }

    private static DateTimeOffset? FindExpiry(JsonElement element, int depth)
    {
        if (depth > 12) return null;
        if (element.ValueKind == JsonValueKind.Object)
        {
            DateTimeOffset? best = null;
            foreach (var property in element.EnumerateObject())
            {
                DateTimeOffset? candidate = null;
                if (ExpiryNames.Contains(property.Name))
                    candidate = ParseTimestamp(property.Value);
                else if (JwtNames.Contains(property.Name) && property.Value.ValueKind == JsonValueKind.String)
                    candidate = ParseJwtExpiry(property.Value.GetString());
                candidate ??= FindExpiry(property.Value, depth + 1);
                if (candidate is not null && (best is null || candidate < best)) best = candidate;
            }
            return best;
        }
        if (element.ValueKind == JsonValueKind.Array)
        {
            return element.EnumerateArray()
                .Select(item => FindExpiry(item, depth + 1))
                .Where(value => value is not null)
                .Min();
        }
        return null;
    }

    private static DateTimeOffset? ParseTimestamp(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var numeric))
            return numeric > 10_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(numeric)
                : DateTimeOffset.FromUnixTimeSeconds(numeric);
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (long.TryParse(text, out numeric))
                return numeric > 10_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds(numeric)
                    : DateTimeOffset.FromUnixTimeSeconds(numeric);
            if (DateTimeOffset.TryParse(text, out var parsed)) return parsed;
        }
        return null;
    }

    private static DateTimeOffset? ParseJwtExpiry(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var parts = token.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');
            using var document = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            return document.RootElement.TryGetProperty("exp", out var expiry)
                ? ParseTimestamp(expiry)
                : null;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return null;
        }
    }
}
