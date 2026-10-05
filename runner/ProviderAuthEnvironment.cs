namespace AgentRunner;

/// <summary>
/// Provider credentials that may cross the daemon-to-CLI process boundary.
/// Values come only from the daemon's process environment. This class never
/// reads credential files and never returns a secret for an unrelated CLI.
/// </summary>
internal static class ProviderAuthEnvironment
{
    public const string ClaudeCodeOAuthToken = "CLAUDE_CODE_OAUTH_TOKEN";

    public static bool TryGetForCli(string? cliType, out string name, out string value)
    {
        name = ClaudeCodeOAuthToken;
        value = string.Empty;
        if (!string.Equals(
                CliSelection.NormalizeCliType(cliType),
                CliSelection.ClaudeCli,
                StringComparison.Ordinal))
            return false;

        var token = Environment.GetEnvironmentVariable(ClaudeCodeOAuthToken);
        if (string.IsNullOrWhiteSpace(token)) return false;
        value = token;
        return true;
    }
}
