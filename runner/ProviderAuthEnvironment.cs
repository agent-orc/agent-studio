namespace AgentRunner;

/// <summary>
/// Provider credentials that may cross the daemon-to-CLI process boundary.
/// Values come from the host's launch environment or the scoped sign-in probe.
/// This class never reads credential files or selects auth for an unrelated CLI.
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

    /// <summary>Select auth from the launch environment, with the scoped Claude probe taking priority.</summary>
    internal static KeyValuePair<string, string?>? SelectForWorker(
        IDictionary<string, string?> environment, string? cliType)
    {
        string? Read(string name) => environment
            .FirstOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
        switch (CliSelection.NormalizeCliType(cliType))
        {
            case CliSelection.ClaudeCli:
                var token = CandidateClaudeToken.Value;
                if (string.IsNullOrWhiteSpace(token)) token = Read(ClaudeCodeOAuthToken);
                if (!string.IsNullOrWhiteSpace(token)) return new(ClaudeCodeOAuthToken, token);
                var apiKey = Read("ANTHROPIC_API_KEY");
                return string.IsNullOrWhiteSpace(apiKey) ? null : new("ANTHROPIC_API_KEY", apiKey);
            case CliSelection.CodexCli:
                var codexKey = Read("CODEX_API_KEY");
                if (!string.IsNullOrWhiteSpace(codexKey)) return new("CODEX_API_KEY", codexKey);
                var openAiKey = Read("OPENAI_API_KEY");
                return string.IsNullOrWhiteSpace(openAiKey) ? null : new("OPENAI_API_KEY", openAiKey);
            default:
                return null;
        }
    }

    private sealed class CandidateScope : IDisposable
    {
        public void Dispose() => CandidateClaudeToken.Value = null;
    }
}
