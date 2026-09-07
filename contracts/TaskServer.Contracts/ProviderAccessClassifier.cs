using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentStudio.TaskServer.Contracts;

public enum ProviderAccessEvidenceKind
{
    Authenticated,
    AuthenticationFailure,
    RateLimited,
    TransientFailure,
    IndeterminateFailure,
}

public sealed record ProviderAccessEvidence(
    ProviderAccessEvidenceKind Kind,
    string Detail,
    DateTimeOffset? LimitedUntil = null,
    bool ResetTimeReported = false);

/// <summary>
/// Shared classification for provider access output. Only explicit provider
/// login signatures are authentication failures. A generic non-zero exit is
/// indeterminate, and quota, transport, timeout, and refresh-race output stays
/// out of the sign-in-required path.
/// </summary>
public static partial class ProviderAccessClassifier
{
    public static readonly TimeSpan UnknownLimitRetry = TimeSpan.FromMinutes(15);

    private static readonly string[] RateLimitSignals =
    [
        "hit your session limit",
        "session limit",
        "usage limit",
        "quota exceeded",
        "quota exhausted",
        "rate limit exceeded",
        "rate limited",
        "rate-limited",
        "rate_limit_exceeded",
        "insufficient_quota",
        "too many requests",
        "model is at capacity",
        "at capacity",
        "status=rejected",
        "· rejected ·",
    ];

    private static readonly string[] AuthenticationSignals =
    [
        "not logged in",
        "not signed in",
        "logged out",
        "no active session",
        "not authenticated",
        "no credentials",
        "login required",
        "please log in",
        "please login",
        "re-authenticate",
        "reauthenticate",
        "oauth token expired",
        "access token expired",
        "refresh token expired",
        "token has expired",
        "invalid api key",
        "invalid_grant",
        "missing bearer authentication",
        "missing basic authentication",
        "missing bearer or basic authentication",
        "invalid authentication credentials",
    ];

    private static readonly string[] TransientSignals =
    [
        "timed out",
        "timeout",
        "temporarily unavailable",
        "temporary failure",
        "connection reset",
        "connection refused",
        "network error",
        "network is unreachable",
        "dns",
        "econnreset",
        "econnrefused",
        "etimedout",
        "token refresh in progress",
        "refresh already in progress",
        "refresh race",
    ];

    public static ProviderAccessEvidence Classify(
        int exitCode,
        string? stdout,
        string? stderr,
        bool statusProbe = false,
        DateTimeOffset? observedAt = null,
        TimeZoneInfo? localZone = null)
    {
        var text = string.Join('\n', new[] { stdout, stderr }
            .Where(value => !string.IsNullOrWhiteSpace(value)));

        if (Contains(text, RateLimitSignals) || Http429Regex().IsMatch(text))
        {
            var now = observedAt ?? DateTimeOffset.UtcNow;
            var reset = ParseResetAt(text, now, localZone ?? TimeZoneInfo.Local);
            return new ProviderAccessEvidence(
                ProviderAccessEvidenceKind.RateLimited,
                FirstMatchingLine(text, RateLimitSignals, "provider rate limit"),
                reset ?? now.Add(UnknownLimitRetry),
                reset is not null);
        }

        if (Contains(text, AuthenticationSignals)
            || ExplicitHttp401Regex().IsMatch(text)
            || ExplicitAuthenticationFailureLine(text))
        {
            return new ProviderAccessEvidence(
                ProviderAccessEvidenceKind.AuthenticationFailure,
                FirstMatchingLine(text, AuthenticationSignals, "provider authentication rejected"));
        }

        if (Contains(text, TransientSignals))
        {
            return new ProviderAccessEvidence(
                ProviderAccessEvidenceKind.TransientFailure,
                FirstMatchingLine(text, TransientSignals, "transient provider access failure"));
        }

        if (statusProbe && exitCode == 0 && !string.IsNullOrWhiteSpace(text))
        {
            return new ProviderAccessEvidence(
                ProviderAccessEvidenceKind.Authenticated,
                "The provider status command confirmed an active session.");
        }

        return new ProviderAccessEvidence(
            ProviderAccessEvidenceKind.IndeterminateFailure,
            string.IsNullOrWhiteSpace(text)
                ? $"The provider command returned no diagnostic output (exit {exitCode})."
                : FirstLine(text));
    }

    private static bool Contains(string text, IEnumerable<string> signals)
        => signals.Any(signal => text.Contains(signal, StringComparison.OrdinalIgnoreCase));

    private static bool ExplicitAuthenticationFailureLine(string text)
        => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Any(line => line.StartsWith("authentication failed", StringComparison.OrdinalIgnoreCase)
                         && !line.Contains(" for http", StringComparison.OrdinalIgnoreCase)
                         && !line.Contains("git", StringComparison.OrdinalIgnoreCase));

    private static DateTimeOffset? ParseResetAt(
        string text,
        DateTimeOffset observedAt,
        TimeZoneInfo localZone)
    {
        var epoch = EpochResetRegex().Match(text);
        if (epoch.Success
            && long.TryParse(epoch.Groups["value"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            return DateTimeOffset.FromUnixTimeSeconds(seconds);

        var iso = IsoResetRegex().Match(text);
        if (iso.Success
            && DateTimeOffset.TryParse(
                iso.Groups["value"].Value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var absolute))
            return absolute;

        var clock = ClockResetRegex().Match(text);
        if (!clock.Success
            || !DateTime.TryParse(
                clock.Groups["value"].Value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var parsed))
            return null;

        var utcNow = observedAt.UtcDateTime;
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, localZone);
        var localReset = new DateTime(
            localNow.Year,
            localNow.Month,
            localNow.Day,
            parsed.Hour,
            parsed.Minute,
            0,
            DateTimeKind.Unspecified);
        if (localReset <= localNow) localReset = localReset.AddDays(1);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localReset, localZone));
    }

    private static string FirstMatchingLine(
        string text,
        IEnumerable<string> signals,
        string fallback)
    {
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (Contains(line, signals) || Http429Regex().IsMatch(line) || ExplicitHttp401Regex().IsMatch(line))
                return Trim(line);
        }
        return fallback;
    }

    private static string FirstLine(string text)
        => Trim(text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? text);

    private static string Trim(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= 300 ? trimmed : trimmed[..300];
    }

    [GeneratedRegex(@"(?:\bhttp\s*429\b|\bstatus(?:\s+code)?\s*[:=]?\s*429\b|\berror\s*[:=]?\s*429\b|\b429\s+too\s+many\s+requests\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Http429Regex();

    [GeneratedRegex(@"(?:\bhttp\s*401\b|\bstatus(?:\s+code)?\s*[:=]?\s*401\b|\berror\s*[:=]?\s*401\b|\b401\s+(?:unauthori[sz]ed|missing\s+(?:bearer|basic)))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitHttp401Regex();

    [GeneratedRegex(@"\bresets?(?:\s+(?:at|on))?\s+(?<value>\d{4}-\d{2}-\d{2}[T ]\d{1,2}:\d{2}(?::\d{2})?(?:\s*Z|\s*[+-]\d{2}:?\d{2})?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IsoResetRegex();

    [GeneratedRegex(@"\bresetsAt\s*=\s*(?<value>\d{10})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EpochResetRegex();

    [GeneratedRegex(@"\bresets?(?:\s+(?:at|on))?\s+(?<value>\d{1,2}:\d{2}\s*(?:a\.?m\.?|p\.?m\.?)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ClockResetRegex();
}
