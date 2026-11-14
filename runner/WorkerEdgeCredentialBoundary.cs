namespace AgentRunner;

/// <summary>Keep Task Server and browser-edge credentials out of detached workers.</summary>
internal static class WorkerEdgeCredentialBoundary
{
    private static readonly string[] ExactNames =
    [
        "RUNNER_AUTH_TOKEN", "RUNNER_AUTH_TOKEN_FILE", "RUNNER_CLIENT_ID",
        RunnerOptions.RepositoryKeyFileVariable,
        "RUNNER_STATE_DIR", "AGENT_HOST_STATE_DIR",
        "STUDIO_COOKIE", "STUDIO_CSRF",
    ];

    private static readonly string[] Prefixes =
    [
        "CONNECTOR_", "BROWSER_", "STUDIO_SESSION_", "STUDIO_CSRF_",
        "TASK_SERVER_AUTH_",
    ];

    public static bool IsProtectedName(string key)
        => ExactNames.Contains(key, StringComparer.OrdinalIgnoreCase)
           || Prefixes.Any(prefix => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// For a CLI started inside the daemon, whose driver inherits this process's
    /// environment: blanks every protected name on top of <paramref name="extra"/>.
    /// </summary>
    public static Dictionary<string, string> BlankedOver(IReadOnlyDictionary<string, string>? extra)
    {
        var environment = extra is null ? new Dictionary<string, string>() : new Dictionary<string, string>(extra);
        foreach (var key in Environment.GetEnvironmentVariables().Keys.OfType<string>())
            if (IsProtectedName(key))
                environment[key] = string.Empty;
        return environment;
    }

    public static void RemoveFrom<T>(IDictionary<string, T> environment)
    {
        foreach (var key in environment.Keys.ToArray())
            if (IsProtectedName(key))
                environment.Remove(key);
    }

    public static void RemovePersonaDeploymentCredentials<T>(IDictionary<string, T> environment)
    {
        foreach (var key in environment.Keys.ToArray())
            if (key.StartsWith("AWS_", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("AZURE_", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("GCP_", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("GITHUB_TOKEN", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("GH_TOKEN", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("GOOGLE_APPLICATION_CREDENTIALS", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("KUBECONFIG", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("SSH_AUTH_SOCK", StringComparison.OrdinalIgnoreCase))
                environment.Remove(key);
    }
}
