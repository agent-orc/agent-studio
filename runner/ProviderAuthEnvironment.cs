namespace AgentRunner;

/// <summary>
/// Provider credentials that may cross the daemon-to-CLI process boundary.
/// Values come only from the daemon's process environment. This class never
/// reads credential files and never returns a secret for an unrelated CLI.
/// </summary>
internal static class ProviderAuthEnvironment
{
    public const string ClaudeCodeOAuthToken = "CLAUDE_CODE_OAUTH_TOKEN";

    // A device sign-in's unverified Claude token, visible only to CLI children started from
    // that sign-in's own async flow (its probe). Every other child keeps the accepted token.
    private static readonly AsyncLocal<string?> CandidateClaudeToken = new();

    /// <summary>
    /// Scopes <paramref name="token"/> to the probe children of the calling async flow. The
    /// daemon's process environment is left untouched until the sign-in is accepted.
    /// </summary>
    public static IDisposable UseCandidateClaudeToken(string token)
    {
        CandidateClaudeToken.Value = token;
        return new CandidateScope();
    }

    /// <summary>A Claude token for a child the daemon starts from this async flow.</summary>
    public static string? ClaudeTokenForChild()
        => CandidateClaudeToken.Value is { Length: > 0 } candidate
            ? candidate
            : Environment.GetEnvironmentVariable(ClaudeCodeOAuthToken);

    public static bool TryGetForCli(string? cliType, out string name, out string value)
    {
        name = ClaudeCodeOAuthToken;
        value = string.Empty;
        if (!string.Equals(
                CliSelection.NormalizeCliType(cliType),
                CliSelection.ClaudeCli,
                StringComparison.Ordinal))
            return false;

        var token = ClaudeTokenForChild();
        if (string.IsNullOrWhiteSpace(token)) return false;
        value = token;
        return true;
    }

    private sealed class CandidateScope : IDisposable
    {
        public void Dispose() => CandidateClaudeToken.Value = null;
    }
}
