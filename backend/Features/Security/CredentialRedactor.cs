using System.Text.RegularExpressions;

namespace AgentStudio.Security;

/// <summary>
/// Masks credentials in text before Studio writes it to a log, a review
/// report, or a workspace commit. Every pattern uses the non-backtracking
/// engine, so the cost stays linear in the input length; registered literals
/// add at most one ordinal scan each.
/// </summary>
public static class CredentialRedactor
{
    private const string Masked = "[REDACTED_CREDENTIAL]";
    private const RegexOptions Linear = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant;

    private static readonly Regex PrivateKeyBlock = new(
        @"-----BEGIN [A-Z0-9 ]*PRIVATE KEY[A-Z ]*-----[\s\S]*?(?:-----END [A-Z0-9 ]*PRIVATE KEY[A-Z ]*-----|\z)",
        Linear);

    // Studio-issued tokens: <prefix>.<id>.<secret>. The newer prefixes also
    // name ordinary words (an "api." host name), so they require the issued
    // id and secret lengths.
    private static readonly Regex ProductSecret = new(
        @"\b(?:(?:rnr|enr|ssn|otl)\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+|(?:inv|sat|api)\.[0-9a-f]{32}\.[A-Za-z0-9_-]{20,})\b",
        Linear);

    // GitHub personal, OAuth, user-to-server, installation and refresh tokens,
    // fine-grained personal tokens, Anthropic keys and OAuth tokens, OpenAI keys.
    private static readonly Regex ProviderToken = new(
        @"\b(?:gh[pousr]_[A-Za-z0-9_]{20,}|github_pat_[A-Za-z0-9_]{20,}|sk-ant-[A-Za-z0-9_-]{20,}|sk-(?:proj-|svcacct-|admin-)?[A-Za-z0-9_-]{32,})",
        Linear);

    private static readonly Regex UrlCredentials = new(
        @"(?i)\b((?:https?|git|ssh|ftp)://)[^\s/:@'""]+:[^\s/@'""]+@",
        Linear);

    private static readonly Regex AccessTokenUserInfo = new(
        @"(?i)\b(x-access-token:)[^\s/@'""]+@",
        Linear);

    private static readonly Regex SecretHeader = new(
        @"(?im)(\b(?:authorization|proxy-authorization|cookie|set-cookie)\s*:\s*)[^\r\n]+",
        Linear);

    private static readonly Regex NamedSecret = new(
        """(?ix)(["']?(?:authorization|proxy-authorization|password|currentpassword|newpassword|temporarypassword|cookie|set-cookie|x-csrf-token|csrftoken|enrollmentcode|secret|authtoken|access_token|refresh_token|claude_code_oauth_token)["']?\s*[:=]\s*)(?:"[^"]*"|'[^']*'|(?:bearer\s+)?[^\s,;}&]+)""",
        Linear);

    /// <summary>Shortest literal worth registering; shorter text would mask ordinary words.</summary>
    public const int MinimumLiteralLength = 16;
    public const int MaximumLiterals = 64;
    private static readonly object LiteralGate = new();
    private static readonly List<string> Registered = [];
    private static readonly List<string> Pinned = [];
    private static string[] _literals = [];

    /// <summary>
    /// Registers an exact credential the process itself holds, such as the
    /// secrets key-encryption key, so every later <see cref="Redact"/> masks
    /// it even where no pattern matches. At most <see cref="MaximumLiterals"/>
    /// unpinned literals are kept, oldest evicted first. A
    /// <paramref name="pinned"/> literal (the key-encryption key) is never
    /// evicted and does not count toward that bound.
    /// </summary>
    public static void RegisterLiteral(string? literal, bool pinned = false)
    {
        if (string.IsNullOrWhiteSpace(literal) || literal.Length < MinimumLiteralLength) return;
        lock (LiteralGate)
        {
            if (Pinned.Contains(literal, StringComparer.Ordinal)) return;
            if (pinned)
            {
                Registered.Remove(literal);
                Pinned.Add(literal);
            }
            else
            {
                if (Registered.Contains(literal, StringComparer.Ordinal)) return;
                Registered.Add(literal);
                if (Registered.Count > MaximumLiterals) Registered.RemoveAt(0);
            }
            _literals = Pinned.Concat(Registered).OrderByDescending(item => item.Length).ToArray();
        }
    }

    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value ?? string.Empty;
        var redacted = value;
        foreach (var literal in Volatile.Read(ref _literals))
            if (redacted.Contains(literal, StringComparison.Ordinal))
                redacted = redacted.Replace(literal, Masked, StringComparison.Ordinal);
        redacted = PrivateKeyBlock.Replace(redacted, Masked);
        redacted = ProductSecret.Replace(redacted, Masked);
        redacted = ProviderToken.Replace(redacted, Masked);
        redacted = UrlCredentials.Replace(redacted, "$1[REDACTED]@");
        redacted = AccessTokenUserInfo.Replace(redacted, "$1[REDACTED]@");
        redacted = SecretHeader.Replace(redacted, "$1[REDACTED]");
        return NamedSecret.Replace(redacted, "$1[REDACTED]");
    }
}
