namespace AgentRunner;

/// <summary>Keep Task Server and browser-edge credentials out of detached workers.</summary>
internal static class WorkerEdgeCredentialBoundary
{
    private static readonly string[] ExactNames =
    [
        "RUNNER_AUTH_TOKEN", "RUNNER_AUTH_TOKEN_FILE", "RUNNER_CLIENT_ID",
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

    public static void RemoveFrom<T>(IDictionary<string, T> environment)
    {
        foreach (var key in environment.Keys.ToArray())
            if (IsProtectedName(key))
                environment.Remove(key);
    }
}
