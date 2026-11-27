namespace AgentRunner;

/// <summary>Credential policies for worker process boundaries.</summary>
internal static class WorkerEdgeCredentialBoundary
{
    // Deliberately exact names, never provider/deployment prefixes. Unknown
    // environment inputs require an explicit decision before crossing this boundary.
    private static readonly HashSet<string> RuntimeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "PATH", "PATHEXT", "HOME", "USER", "LOGNAME", "SHELL",
        "SystemRoot", "WINDIR", "COMSPEC", "USERPROFILE", "HOMEDRIVE", "HOMEPATH",
        "APPDATA", "LOCALAPPDATA", "PROGRAMDATA", "ProgramFiles", "ProgramFiles(x86)",
        "TEMP", "TMP", "TMPDIR", "LANG", "LC_ALL", "LC_CTYPE", "TZ",
        "TERM", "COLORTERM", "NO_COLOR", "COLUMNS", "LINES",
        "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT_X86", "DOTNET_CLI_HOME",
        "DOTNET_NOLOGO", "DOTNET_SKIP_FIRST_TIME_EXPERIENCE", "DOTNET_CLI_TELEMETRY_OPTOUT",
        "NUGET_PACKAGES", "NUGET_HTTP_CACHE_PATH", "NPM_CONFIG_CACHE",
        "PNPM_HOME", "COREPACK_HOME", "YARN_CACHE_FOLDER", "BUN_INSTALL",
        "CARGO_HOME", "RUSTUP_HOME", "GOPATH", "GOCACHE", "GOMODCACHE",
        "JAVA_HOME", "GRADLE_USER_HOME", "PYTHONPYCACHEPREFIX", "PIP_CACHE_DIR", "UV_CACHE_DIR",
        "PLAYWRIGHT_BROWSERS_PATH", "XDG_CACHE_HOME", "XDG_CONFIG_HOME", "XDG_DATA_HOME",
        "XDG_STATE_HOME", "JOB_RESULTS_DIR",
    };

    internal static bool IsCodingRuntimeName(string key, string? cliType)
        => RuntimeNames.Contains(key)
           || (CliSelection.NormalizeCliType(cliType) switch
           {
               CliSelection.ClaudeCli => key.Equals("CLAUDE_CONFIG_DIR", StringComparison.OrdinalIgnoreCase)
                   || key.Equals("CLAUDE_CODE_EFFORT_LEVEL", StringComparison.OrdinalIgnoreCase),
               CliSelection.CodexCli => key.Equals("CODEX_HOME", StringComparison.OrdinalIgnoreCase),
               _ => false,
           });

    /// <summary>
    /// Build the coding environment from runtime names and exactly one selected
    /// provider auth input. This narrows environment exposure, not shared-UID file access.
    /// </summary>
    internal static void ApplyCodingEnvironment(IDictionary<string, string?> environment, string? cliType)
    {
        var auth = ProviderAuthEnvironment.SelectForWorker(environment, cliType);
        foreach (var key in environment.Keys.ToArray())
            if (!IsCodingRuntimeName(key, cliType)) environment.Remove(key);
        if (auth is { } selected) environment[selected.Key] = selected.Value;
        WorkerBuildServerHygiene.ApplyTo(environment);
    }

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
