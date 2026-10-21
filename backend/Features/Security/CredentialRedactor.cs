using System.Text.RegularExpressions;

namespace AgentStudio.Security;

public static partial class CredentialRedactor
{
    [GeneratedRegex(@"(?im)(\b(?:authorization|proxy-authorization|cookie|set-cookie)\s*:\s*)[^\r\n]+")]
    private static partial Regex SecretHeaderRegex();

    [GeneratedRegex("""(?ix)(["']?(?:authorization|proxy-authorization|password|currentpassword|newpassword|temporarypassword|cookie|set-cookie|x-csrf-token|csrftoken|enrollmentcode|secret|authtoken|access_token)["']?\s*[:=]\s*)(?:"[^"]*"|'[^']*'|(?:bearer\s+)?[^\s,;}&]+)""")]
    private static partial Regex NamedSecretRegex();

    [GeneratedRegex(@"\b(?:rnr|enr|ssn|otl)\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b")]
    private static partial Regex ProductSecretRegex();

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
                redacted = redacted.Replace(literal, "[REDACTED_CREDENTIAL]", StringComparison.Ordinal);
        redacted = ProductSecretRegex().Replace(redacted, "[REDACTED_CREDENTIAL]");
        redacted = SecretHeaderRegex().Replace(redacted, "$1[REDACTED]");
        return NamedSecretRegex().Replace(redacted, "$1[REDACTED]");
    }
}
