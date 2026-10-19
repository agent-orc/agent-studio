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

    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value ?? string.Empty;
        var redacted = ProductSecretRegex().Replace(value, "[REDACTED_CREDENTIAL]");
        redacted = SecretHeaderRegex().Replace(redacted, "$1[REDACTED]");
        return NamedSecretRegex().Replace(redacted, "$1[REDACTED]");
    }
}
