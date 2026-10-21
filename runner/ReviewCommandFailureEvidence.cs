using System.Text.RegularExpressions;

namespace AgentRunner;

/// <summary>Bounded, scrubbed command output for a delivery-owned review finding.</summary>
internal static partial class ReviewCommandFailureEvidence
{
    internal static string Tail(ProcessResult process)
    {
        var output = Scrub(process.StdOut + "\n" + process.StdErr);
        var lines = output.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n').Split('\n')
            .Where(line => !string.IsNullOrWhiteSpace(line)).TakeLast(20);
        var tail = string.Join('\n', lines);
        return tail.Length > 1600 ? tail[^1600..] : tail;
    }

    internal static string Scrub(string output)
    {
        output = Ansi().Replace(output, string.Empty);
        output = SecretHeader().Replace(output, "$1[REDACTED]");
        output = NamedSecret().Replace(output, "$1[REDACTED]");
        output = ProviderToken().Replace(output, "[REDACTED]");
        output = ProductToken().Replace(output, "[REDACTED]");
        output = UrlCredential().Replace(output, "$1[REDACTED]@");
        return output;
    }

    [GeneratedRegex(@"\x1B\[[0-9;]*[A-Za-z]")]
    private static partial Regex Ansi();

    [GeneratedRegex(@"(?im)(\b(?:authorization|proxy-authorization|cookie|set-cookie)\s*:\s*)[^\r\n]+")]
    private static partial Regex SecretHeader();

    [GeneratedRegex("""(?ix)(["']?(?:password|secret|authtoken|access_token|api[_-]?key|token)["']?\s*[:=]\s*)(?:"[^"]*"|'[^']*'|[^\s,;}&]+)""")]
    private static partial Regex NamedSecret();

    [GeneratedRegex(@"\b(?:github_pat_[A-Za-z0-9_]{16,}|gh[pousr]_[A-Za-z0-9_]{16,}|sk-[A-Za-z0-9_-]{16,})\b")]
    private static partial Regex ProviderToken();

    [GeneratedRegex(@"\b(?:rnr|enr|ssn|otl)\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b")]
    private static partial Regex ProductToken();

    [GeneratedRegex(@"(https?://)[^\s/@:]+:[^\s/@]+@", RegexOptions.IgnoreCase)]
    private static partial Regex UrlCredential();
}
