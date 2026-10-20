using System.Net;

namespace AgentStudio.OrchestratorEngine;

public sealed class EngineOptions
{
    public required string ServerUrl { get; init; }
    public required string ClientId { get; init; }
    public string? ClientCredential { get; init; }
    public string? ClientCredentialFile { get; init; }
    public string? DecisionPlaneUrl { get; init; }
    public string? DecisionPlaneClientCredential { get; init; }
    public string? DecisionPlaneClientCredentialFile { get; init; }
    public bool AllowInsecureHttp { get; init; }
    public int ReviewConcurrency { get; init; } = 4;
    public int CouncilConcurrency { get; init; } = 4;
    public int PostProcessingConcurrency { get; init; } = 3;
    public int GateDispatchConcurrency { get; init; } = 2;
    public int CompletionJudgeConcurrency { get; init; } = 4;
    public int OperatorDecisionConcurrency { get; init; } = 2;
    public int PollSeconds { get; init; } = 2;
    public int LeaseSeconds { get; init; } = 120;
    public int HealthPort { get; init; } = EngineHealthProbe.DefaultPort;
    public bool RemotePostBuildTestEnabled { get; init; }
    public int ShadowDecisionConcurrency { get; init; } = 2;
    public int ShadowDecisionTimeoutSeconds { get; init; } = 900;
    public int BatchPlanConcurrency { get; init; } = 1;
    /// <summary>
    /// Provider credentials of the batch advisor, one per advisor CLI type: Anthropic serves
    /// <c>claude</c>, OpenAI serves <c>codex</c>. The occurrence names the model; without the
    /// credential of its provider the decision keeps the deterministic plan.
    /// </summary>
    public string? AdvisorAnthropicCredential { get; init; }
    public string? AdvisorOpenAiCredential { get; init; }

    public static EngineOptions FromEnvironment()
        => Parse(Environment.GetEnvironmentVariable);

    internal static EngineOptions Parse(Func<string, string?> value)
    {
        var serverUrl = Required(value, "SERVER_URL").TrimEnd('/');
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var server))
            throw new ArgumentException("SERVER_URL must be an absolute URL.");
        var isLoopback = server.IsLoopback
                         || IPAddress.TryParse(server.Host, out var address) && IPAddress.IsLoopback(address);
        var allowInsecureHttp = OptIn(value("ENGINE_ALLOW_INSECURE_HTTP"));
        var insecureHttpPermitted = allowInsecureHttp
                                    && string.Equals(
                                        server.Scheme,
                                        Uri.UriSchemeHttp,
                                        StringComparison.OrdinalIgnoreCase);
        if (!string.Equals(server.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && !isLoopback
            && !insecureHttpPermitted)
            throw new ArgumentException(
                "SERVER_URL must use HTTPS unless it is a loopback address. "
                + "Set ENGINE_ALLOW_INSECURE_HTTP=1 to opt in to plain HTTP on a trusted private "
                + "container network.");

        var credential = value("CLIENT_CREDENTIAL")?.Trim();
        var credentialFile = value("CLIENT_CREDENTIAL_FILE")?.Trim();
        if (!string.IsNullOrWhiteSpace(credential) && !string.IsNullOrWhiteSpace(credentialFile))
            throw new ArgumentException("Configure only one of CLIENT_CREDENTIAL or CLIENT_CREDENTIAL_FILE.");
        if (!string.IsNullOrWhiteSpace(credentialFile))
        {
            credential = ReadCredentialFile(credentialFile);
        }
        var anthropicCredential = OptionalCredential(value, "ADVISOR_ANTHROPIC_CREDENTIAL");
        var openAiCredential = OptionalCredential(value, "ADVISOR_OPENAI_CREDENTIAL");
        if (!isLoopback && string.IsNullOrWhiteSpace(credential))
            throw new ArgumentException("CLIENT_CREDENTIAL is required for a non-loopback SERVER_URL.");
        var decisionUrl = value("DECISION_PLANE_URL")?.Trim().TrimEnd('/');
        // The same origin as SERVER_URL is not a separate decision plane.
        if (string.Equals(decisionUrl, serverUrl, StringComparison.OrdinalIgnoreCase)) decisionUrl = null;
        var decisionLoopback = false;
        if (!string.IsNullOrWhiteSpace(decisionUrl))
        {
            if (!Uri.TryCreate(decisionUrl, UriKind.Absolute, out var decisionUri)
                || decisionUri.Scheme is not ("http" or "https"))
                throw new ArgumentException("DECISION_PLANE_URL must be an absolute HTTP or HTTPS URL.");
            decisionLoopback = decisionUri.IsLoopback
                               || IPAddress.TryParse(decisionUri.Host, out var decisionAddress)
                               && IPAddress.IsLoopback(decisionAddress);
            if (decisionUri.Scheme == "http" && !decisionLoopback && !allowInsecureHttp)
                throw new ArgumentException("DECISION_PLANE_URL must use HTTPS unless it is loopback or ENGINE_ALLOW_INSECURE_HTTP=1.");
        }
        var decisionCredential = value("DECISION_PLANE_CLIENT_CREDENTIAL")?.Trim();
        var decisionCredentialFile = value("DECISION_PLANE_CLIENT_CREDENTIAL_FILE")?.Trim();
        if (!string.IsNullOrWhiteSpace(decisionCredential) && !string.IsNullOrWhiteSpace(decisionCredentialFile))
            throw new ArgumentException("Configure only one of DECISION_PLANE_CLIENT_CREDENTIAL or DECISION_PLANE_CLIENT_CREDENTIAL_FILE.");
        if (!string.IsNullOrWhiteSpace(decisionCredentialFile))
            decisionCredential = ReadCredentialFile(decisionCredentialFile, "DECISION_PLANE_CLIENT_CREDENTIAL_FILE");
        // The Task Server credential is never sent to another origin, and Studio's
        // decision routes require an engine bearer even on loopback.
        if (!string.IsNullOrWhiteSpace(decisionUrl) && string.IsNullOrWhiteSpace(decisionCredential))
            throw new ArgumentException(
                "DECISION_PLANE_CLIENT_CREDENTIAL or DECISION_PLANE_CLIENT_CREDENTIAL_FILE is required for a "
                + "DECISION_PLANE_URL that differs from SERVER_URL.");

        return new EngineOptions
        {
            ServerUrl = serverUrl,
            ClientId = Required(value, "CLIENT_ID"),
            ClientCredential = string.IsNullOrWhiteSpace(credential) ? null : credential,
            ClientCredentialFile = string.IsNullOrWhiteSpace(credentialFile) ? null : credentialFile,
            DecisionPlaneUrl = decisionUrl,
            DecisionPlaneClientCredential = string.IsNullOrWhiteSpace(decisionCredential) ? null : decisionCredential,
            DecisionPlaneClientCredentialFile = string.IsNullOrWhiteSpace(decisionCredentialFile) ? null : decisionCredentialFile,
            AllowInsecureHttp = allowInsecureHttp,
            ReviewConcurrency = Cap(value, "REVIEW_CONCURRENCY", 4),
            CouncilConcurrency = Cap(value, "COUNCIL_CONCURRENCY", 4),
            PostProcessingConcurrency = Cap(value, "POST_PROCESSING_CONCURRENCY", 3),
            GateDispatchConcurrency = Cap(value, "GATE_DISPATCH_CONCURRENCY", 2),
            CompletionJudgeConcurrency = Cap(value, "COMPLETION_JUDGE_CONCURRENCY", 4),
            OperatorDecisionConcurrency = Cap(value, "OPERATOR_DECISION_CONCURRENCY", 2),
            PollSeconds = Number(value, "POLL_SECONDS", 2, 1, 60),
            LeaseSeconds = Number(value, "LEASE_SECONDS", 120, 30, 600),
            HealthPort = EngineHealthProbe.ResolvePort(value),
            RemotePostBuildTestEnabled = OptIn(value("REMOTE_POST_BUILD_TEST_GATE_ENABLED")),
            ShadowDecisionConcurrency = Cap(value, "SHADOW_DECISION_CONCURRENCY", 2),
            ShadowDecisionTimeoutSeconds = Number(value, "SHADOW_DECISION_TIMEOUT_SECONDS", 900, 60, 3600),
            BatchPlanConcurrency = Cap(value, "BATCH_PLAN_CONCURRENCY", 1),
            AdvisorAnthropicCredential = anthropicCredential,
            AdvisorOpenAiCredential = openAiCredential,
        };
    }

    private static string? OptionalCredential(Func<string, string?> value, string key)
    {
        var credential = value(key)?.Trim();
        var file = value(key + "_FILE")?.Trim();
        if (!string.IsNullOrWhiteSpace(credential) && !string.IsNullOrWhiteSpace(file))
            throw new ArgumentException($"Configure only one of {key} or {key}_FILE.");
        if (!string.IsNullOrWhiteSpace(file))
            credential = ReadCredentialFile(file, key + "_FILE");
        return string.IsNullOrWhiteSpace(credential) ? null : credential;
    }

    internal static string ReadCredentialFile(string path, string key = "CLIENT_CREDENTIAL_FILE")
    {
        if (!File.Exists(path))
            throw new ArgumentException($"{key} does not exist: {path}");
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(path);
            if ((mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite)) != 0)
                throw new ArgumentException($"{key} must be owner-readable only.");
        }
        var credential = File.ReadAllText(path).Trim();
        if (credential.Length == 0)
            throw new ArgumentException($"{key} is empty.");
        return credential;
    }

    private static string Required(Func<string, string?> value, string key)
        => string.IsNullOrWhiteSpace(value(key))
            ? throw new ArgumentException($"{key} is required by engine.env.")
            : value(key)!.Trim();

    private static int Cap(Func<string, string?> value, string key, int fallback)
        => Number(value, key, fallback, 1, 64);

    private static bool OptIn(string? value)
        => string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
           || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

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
