using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentStudio.Projects;

/// <summary>
/// One external probe of an environment (IP-14). <c>http</c> uses <see cref="Url"/>,
/// <c>tcp</c> and <c>tls</c> use <see cref="Host"/> and <see cref="Port"/>.
/// </summary>
public sealed record HealthCheckDefinition(
    string Id, string Kind, string? Url, string? Host, int? Port, int ExpectedStatus, string? BodyContains,
    int IntervalSeconds, int TimeoutSeconds, int FailureThreshold, int RecoveryThreshold, bool Derived = false)
{
    public string Target => Kind == "http" ? Url ?? "" : $"{Host}:{Port}";
}

/// <summary>
/// Parses the optional <c>healthChecks</c> array of an environment declaration and
/// derives the default checks from its public address and health URL.
/// </summary>
public static class HealthCheckDeclarations
{
    public const int DefaultIntervalSeconds = 60;
    public const int DefaultTimeoutSeconds = 10;
    public const int DefaultFailureThreshold = 3;
    public const int DefaultRecoveryThreshold = 2;
    private static readonly Regex Identifier = new("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.Compiled);
    private static readonly Regex HostName = new(@"^[A-Za-z0-9]([A-Za-z0-9.-]{0,252})$", RegexOptions.Compiled);

    /// <summary>Returns null when the declaration has no <c>healthChecks</c> property.</summary>
    public static IReadOnlyList<HealthCheckDefinition>? Parse(JsonElement declaration, string file)
    {
        if (!declaration.TryGetProperty("healthChecks", out var array)) return null;
        if (array.ValueKind != JsonValueKind.Array)
            throw new JsonException($"Environment {file} healthChecks must be an array.");
        var result = new List<HealthCheckDefinition>();
        foreach (var item in array.EnumerateArray())
        {
            string? Text(string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
            int? Number(string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
                ? value.GetInt32() : null;
            var id = Text("id") ?? "";
            var kind = Text("kind") ?? "";
            if (!Identifier.IsMatch(id) || kind is not ("http" or "tcp" or "tls"))
                throw new JsonException($"Environment {file} has a health check with an invalid id or kind.");
            var url = Text("url");
            var host = Text("host");
            var port = Number("port");
            if (kind == "http" && !IsHttpUrl(url))
                throw new JsonException($"Health check {id} requires an HTTP or HTTPS url.");
            if (kind is "tcp" or "tls")
            {
                if (host is null && IsHttpUrl(url))
                {
                    var uri = new Uri(url!);
                    host = uri.Host;
                    port ??= uri.Port;
                }
                port ??= kind == "tls" ? 443 : null;
                if (host is null || !HostName.IsMatch(host) || port is not (>= 1 and <= 65535))
                    throw new JsonException($"Health check {id} requires a host and a port between 1 and 65535.");
            }
            var bodyContains = Text("bodyContains");
            var definition = new HealthCheckDefinition(id, kind, kind == "http" ? url : null,
                kind == "http" ? null : host, kind == "http" ? null : port,
                Number("expectedStatus") ?? 200, string.IsNullOrEmpty(bodyContains) ? null : bodyContains,
                Number("intervalSeconds") ?? DefaultIntervalSeconds, Number("timeoutSeconds") ?? DefaultTimeoutSeconds,
                Number("failureThreshold") ?? DefaultFailureThreshold,
                Number("recoveryThreshold") ?? DefaultRecoveryThreshold);
            if (Validate(definition) is { } error) throw new JsonException($"Health check {id}: {error}");
            result.Add(definition);
        }
        if (result.GroupBy(item => item.Id, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new JsonException($"Environment {file} health check ids must be unique.");
        return result;
    }

    public static string? Validate(HealthCheckDefinition check)
    {
        if (check.ExpectedStatus is < 100 or > 599) return "expectedStatus must be an HTTP status code.";
        if (check.BodyContains is { Length: > 200 }) return "bodyContains is limited to 200 characters.";
        // Raw samples are kept at one-minute resolution, so a check never runs more often.
        if (check.IntervalSeconds is < 60 or > 3600) return "intervalSeconds must be between 60 and 3600.";
        if (check.TimeoutSeconds < 1 || check.TimeoutSeconds > 60 || check.TimeoutSeconds >= check.IntervalSeconds)
            return "timeoutSeconds must be between 1 and 60 and below the interval.";
        if (check.FailureThreshold is < 1 or > 20) return "failureThreshold must be between 1 and 20.";
        if (check.RecoveryThreshold is < 1 or > 20) return "recoveryThreshold must be between 1 and 20.";
        return null;
    }

    /// <summary>
    /// The checks that run for an environment: the declared list when present (an empty
    /// list turns checks off), otherwise defaults derived from the health URL and address.
    /// </summary>
    public static IReadOnlyList<HealthCheckDefinition> Effective(EnvironmentDeclaration environment)
    {
        if (environment.HealthChecks is { } declared) return declared;
        var result = new List<HealthCheckDefinition>();
        var addressUrl = IsHttpUrl(environment.Address) ? new Uri(environment.Address) : null;
        var httpTarget = environment.HealthUrl ?? addressUrl?.ToString();
        if (httpTarget is not null)
            result.Add(Default("http", "http", httpTarget, null, null));
        if (addressUrl is { Scheme: "https" })
            result.Add(Default("tls", "tls", null, addressUrl.Host, addressUrl.Port));
        else if (addressUrl is null && TryHostPort(environment.Address, out var host, out var port))
            result.Add(Default("tcp", "tcp", null, host, port));
        return result;
    }

    private static HealthCheckDefinition Default(string id, string kind, string? url, string? host, int? port)
        => new(id, kind, url, host, port, 200, null, DefaultIntervalSeconds, DefaultTimeoutSeconds,
            DefaultFailureThreshold, DefaultRecoveryThreshold, Derived: true);

    private static bool IsHttpUrl(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    private static bool TryHostPort(string address, out string host, out int port)
    {
        host = "";
        port = 0;
        var value = address.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase) ? address[6..] : address;
        var separator = value.LastIndexOf(':');
        if (separator <= 0 || !int.TryParse(value[(separator + 1)..], out port) || port is < 1 or > 65535)
            return false;
        host = value[..separator];
        return HostName.IsMatch(host);
    }
}
