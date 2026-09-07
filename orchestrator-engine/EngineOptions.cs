using System.Net;

namespace AgentStudio.OrchestratorEngine;

public sealed class EngineOptions
{
    public required string ServerUrl { get; init; }
    public required string ClientId { get; init; }
    public string? ClientCredential { get; init; }
    public int ReviewConcurrency { get; init; } = 4;
    public int CouncilConcurrency { get; init; } = 4;
    public int PostProcessingConcurrency { get; init; } = 3;
    public int GateDispatchConcurrency { get; init; } = 2;
    public int CompletionJudgeConcurrency { get; init; } = 4;
    public int PollSeconds { get; init; } = 2;
    public int LeaseSeconds { get; init; } = 120;

    public static EngineOptions FromEnvironment()
        => Parse(Environment.GetEnvironmentVariable);

    internal static EngineOptions Parse(Func<string, string?> value)
    {
        var serverUrl = Required(value, "SERVER_URL").TrimEnd('/');
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var server))
            throw new ArgumentException("SERVER_URL must be an absolute URL.");
        var isLoopback = server.IsLoopback
                         || IPAddress.TryParse(server.Host, out var address) && IPAddress.IsLoopback(address);
        if (!string.Equals(server.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(server.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("SERVER_URL must use HTTP or HTTPS.");
        var allowContainedHttp = string.Equals(
            value("ALLOW_INSECURE_HTTP"),
            "1",
            StringComparison.Ordinal);
        if (!string.Equals(server.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && !isLoopback
            && !allowContainedHttp)
            throw new ArgumentException("SERVER_URL must use HTTPS unless it is a loopback address.");

        var credential = value("CLIENT_CREDENTIAL")?.Trim();
        if (!isLoopback && string.IsNullOrWhiteSpace(credential))
            throw new ArgumentException("CLIENT_CREDENTIAL is required for a non-loopback SERVER_URL.");

        return new EngineOptions
        {
            ServerUrl = serverUrl,
            ClientId = Required(value, "CLIENT_ID"),
            ClientCredential = string.IsNullOrWhiteSpace(credential) ? null : credential,
            ReviewConcurrency = Cap(value, "REVIEW_CONCURRENCY", 4),
            CouncilConcurrency = Cap(value, "COUNCIL_CONCURRENCY", 4),
            PostProcessingConcurrency = Cap(value, "POST_PROCESSING_CONCURRENCY", 3),
            GateDispatchConcurrency = Cap(value, "GATE_DISPATCH_CONCURRENCY", 2),
            CompletionJudgeConcurrency = Cap(value, "COMPLETION_JUDGE_CONCURRENCY", 4),
            PollSeconds = Number(value, "POLL_SECONDS", 2, 1, 60),
            LeaseSeconds = Number(value, "LEASE_SECONDS", 120, 30, 600),
        };
    }

    private static string Required(Func<string, string?> value, string key)
        => string.IsNullOrWhiteSpace(value(key))
            ? throw new ArgumentException($"{key} is required by engine.env.")
            : value(key)!.Trim();

    private static int Cap(Func<string, string?> value, string key, int fallback)
        => Number(value, key, fallback, 1, 64);

    private static int Number(
        Func<string, string?> value,
        string key,
        int fallback,
        int minimum,
        int maximum)
    {
        var raw = value(key);
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        if (!int.TryParse(raw, out var parsed) || parsed < minimum || parsed > maximum)
            throw new ArgumentException($"{key} must be an integer between {minimum} and {maximum}.");
        return parsed;
    }
}
