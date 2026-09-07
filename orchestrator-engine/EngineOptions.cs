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

    /// <summary>
    /// Explicit opt-in (<c>ALLOW_INSECURE_HTTP=1|true</c>) that allows a plain
    /// <c>http://</c> Task Server URL outside loopback. It exists for container
    /// networks where the Task Server is reachable only under its service name
    /// and never leaves the network (for example <c>http://task-server:5071</c>
    /// in docker compose). Off by default, and it never relaxes the credential
    /// requirement. Mirrors <c>RUNNER_ALLOW_INSECURE_HTTP</c> on the Agent Host.
    /// </summary>
    public bool AllowInsecureHttp { get; init; }

    public static EngineOptions FromEnvironment()
        => Parse(Environment.GetEnvironmentVariable);

    internal static EngineOptions Parse(Func<string, string?> value)
    {
        var serverUrl = Required(value, "SERVER_URL").TrimEnd('/');
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var server))
            throw new ArgumentException("SERVER_URL must be an absolute URL.");
        var isLoopback = server.IsLoopback
                         || IPAddress.TryParse(server.Host, out var address) && IPAddress.IsLoopback(address);
        var isHttp = string.Equals(server.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
        var allowInsecureHttp = OptIn(value("ALLOW_INSECURE_HTTP"));
        // Plain HTTP outside loopback stays refused unless the operator opted in
        // explicitly. The opt-in covers exactly one legitimate topology: a private
        // container network where the Task Server is addressed by service name and
        // never published outside it.
        if (!string.Equals(server.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && !isLoopback
            && !(allowInsecureHttp && isHttp))
            throw new ArgumentException(
                "SERVER_URL must use HTTPS unless it is a loopback address. "
                + "Set ALLOW_INSECURE_HTTP=1 to opt in to plain HTTP on a trusted private "
                + "network (for example a container network such as http://task-server:5071).");

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
            AllowInsecureHttp = allowInsecureHttp,
        };
    }

    // Same accepted spellings as RunnerOptions.OptIn so one operator habit works
    // for RUNNER_ALLOW_INSECURE_HTTP and ALLOW_INSECURE_HTTP alike.
    private static bool OptIn(string? raw)
        => raw?.Trim() is { Length: > 0 } flag
           && (flag == "1" || string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase));

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
