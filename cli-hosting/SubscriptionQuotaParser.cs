using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentStudio.CliHosting;

/// <summary>Parsed subscription data shared by the Studio probe and execution hosts.</summary>
public sealed record SubscriptionQuotaWindow(
    string Label, double? UsedPct, DateTime? ResetAt = null, string? ResetLabel = null);

public static class SubscriptionQuotaParser
{
    private static readonly Regex ClaudeSession = new(
        @"Current\s*session[^\d]{0,160}?(?<pct>\d+)\s*%\s*used",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ClaudeWeek = new(
        @"Current\s*week\s*\(\s*all\s*models?\s*\)[^\d]{0,220}?(?<pct>\d+)\s*%\s*used",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ClaudeReset = new(
        @"Resets\s*(?<reset>(?:[A-Za-z]+\s*\d+\s*,?\s*)?\d{1,2}(?::\d{2})?\s*(?:am|pm)?)\s*(?:\((?<tz>[^)]+)\))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TabbedApiUsage = new(
        @"Settings\s*Status\s*Config\s*Usage\s*Stats.*?Session.*?Total\s*cost\s*:.*?Total\s*duration\s*\(\s*API\s*\)\s*:.*?Usage\s*:.*?(?:input|output|cache\s*read|cache\s*write)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex CodexAccount = new(
        @"Account\s*:?\s*(?<identity>[^()\r\n]{1,120}?)\s*\(\s*(?<plan>[A-Za-z][A-Za-z0-9 +]{0,30})\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CodexLimit = new(
        @"(?<kind>5h|Weekly)\s*limit\s*:?\s*(?:\[[^\]]*\]\s*)?(?<left>\d+)\s*%\s*left",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CodexReset = new(
        @"\(?\s*resets\s*(?<reset>[^)\r\n│]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Spark = new(@"Spark\s*limit\s*:?", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Footer = new(
        @"5h\s*(?<h5>\d+)\s*%\s*[·•]\s*weekly\s*(?<week>\d+)\s*%",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static IReadOnlyList<SubscriptionQuotaWindow> ParseClaude(string text, DateTime nowUtc)
    {
        var windows = new List<SubscriptionQuotaWindow>();
        var session = ClaudeSession.Match(text);
        var week = ClaudeWeek.Match(text);
        if (session.Success) windows.Add(ClaudeWindow(text, session, week.Success ? week.Index : text.Length,
            "Current session (5h)", nowUtc));
        if (week.Success) windows.Add(ClaudeWindow(text, week, text.Length, "Weekly (all models)", nowUtc));
        if (windows.Count == 0 && TabbedApiUsage.IsMatch(text))
            windows.Add(new("Quota", null));
        return windows;
    }

    private static SubscriptionQuotaWindow ClaudeWindow(string text, Match match, int end,
        string label, DateTime nowUtc)
    {
        var pct = double.Parse(match.Groups["pct"].Value, CultureInfo.InvariantCulture);
        var tail = text[(match.Index + match.Length)..Math.Max(end, match.Index + match.Length)];
        if (tail.Length > 200) tail = tail[..200];
        var reset = ClaudeReset.Match(tail);
        if (!reset.Success) return new(label, Math.Min(100, pct));
        var raw = reset.Groups["reset"].Value.Trim();
        var tz = reset.Groups["tz"].Success ? reset.Groups["tz"].Value.Trim() : null;
        var at = Regex.IsMatch(raw, @"[A-Za-z]+\s*\d+", RegexOptions.IgnoreCase)
            ? ClaudeDateReset(raw, tz, nowUtc) : ClaudeTimeReset(raw, tz, nowUtc);
        return new(label, Math.Min(100, pct), at, tz is null ? raw : $"{raw} ({tz})");
    }

    public static DateTime? ClaudeTimeReset(string raw, string? ianaTz, DateTime nowUtc)
    {
        if (!DateTime.TryParseExact(raw.Replace(" ", ""),
                ["h:mmtt", "hh:mmtt", "htt", "hhtt", "H:mm", "HH:mm"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)) return null;
        var tz = Zone(ianaTz);
        var now = TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), tz);
        var candidate = new DateTime(now.Year, now.Month, now.Day, local.Hour, local.Minute, 0);
        if (candidate <= now) candidate = candidate.AddDays(1);
        return TimeZoneInfo.ConvertTimeToUtc(candidate, tz);
    }

    private static DateTime? ClaudeDateReset(string raw, string? ianaTz, DateTime nowUtc)
    {
        var spaced = Regex.Replace(raw, @"(?<=[A-Za-z])(?=\d)", " ").Replace(",", " ");
        var clean = Regex.Replace(spaced, @"\s+", " ").Trim();
        if (!DateTime.TryParseExact(clean,
                ["MMM d htt", "MMM d hh tt", "MMM d h:mmtt", "MMM d hh:mmtt",
                 "MMM d HH:mm", "MMM d H:mm", "MMMM d htt", "MMMM d h:mmtt", "MMMM d HH:mm"],
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed)) return null;
        var tz = Zone(ianaTz);
        var now = TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), tz);
        var candidate = new DateTime(now.Year, parsed.Month, parsed.Day, parsed.Hour, parsed.Minute, 0);
        if (candidate < now.AddDays(-1)) candidate = candidate.AddYears(1);
        return TimeZoneInfo.ConvertTimeToUtc(candidate, tz);
    }

    private static TimeZoneInfo Zone(string? id)
    {
        try { return id is null ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.Local; }
        catch (InvalidTimeZoneException) { return TimeZoneInfo.Local; }
    }

    public static IReadOnlyList<SubscriptionQuotaWindow> ParseCodex(string text, DateTime nowUtc)
    {
        var windows = new List<SubscriptionQuotaWindow>();
        var spark = Spark.Match(text);
        AddCodex(windows, spark.Success ? text[..spark.Index] : text, "", nowUtc);
        if (spark.Success) AddCodex(windows, text[(spark.Index + spark.Length)..], "Spark ", nowUtc);
        if (windows.Count == 0 && Footer.Match(text) is { Success: true } footer)
        {
            windows.Add(new("5-hour", 100 - int.Parse(footer.Groups["h5"].Value, CultureInfo.InvariantCulture)));
            windows.Add(new("Weekly", 100 - int.Parse(footer.Groups["week"].Value, CultureInfo.InvariantCulture)));
        }
        return windows;
    }

    private static void AddCodex(List<SubscriptionQuotaWindow> windows, string text, string prefix, DateTime nowUtc)
    {
        var matches = CodexLimit.Matches(text);
        for (var i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            var end = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            var tail = text[(match.Index + match.Length)..end];
            if (tail.Length > 240) tail = tail[..240];
            var reset = CodexReset.Match(tail);
            var resetRaw = reset.Success ? reset.Groups["reset"].Value.Trim() : null;
            var label = match.Groups["kind"].Value.Equals("5h", StringComparison.OrdinalIgnoreCase)
                ? "5-hour" : "Weekly";
            windows.Add(new(prefix + label,
                100 - int.Parse(match.Groups["left"].Value, CultureInfo.InvariantCulture),
                resetRaw is null ? null : CodexResetAt(resetRaw, nowUtc), resetRaw));
        }
    }

    private static DateTime? CodexResetAt(string raw, DateTime nowUtc)
    {
        var tz = TimeZoneInfo.Local;
        var now = TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), tz);
        var date = Regex.Match(raw, @"^(?<time>\d{1,2}:\d{2})\s*on\s*(?<day>\d{1,2})\s*(?<mon>[A-Za-z]+)$", RegexOptions.IgnoreCase);
        if (date.Success && DateTime.TryParseExact(date.Groups["time"].Value, "H:mm",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            && DateTime.TryParseExact(date.Groups["mon"].Value, ["MMM", "MMMM"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var month)
            && int.TryParse(date.Groups["day"].Value, out var day))
        {
            var candidate = new DateTime(now.Year, month.Month, day, time.Hour, time.Minute, 0);
            if (candidate < now.AddDays(-1)) candidate = candidate.AddYears(1);
            return TimeZoneInfo.ConvertTimeToUtc(candidate, tz);
        }
        if (!DateTime.TryParseExact(raw, ["H:mm", "HH:mm", "h:mmtt", "hh:mmtt"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var clock)) return null;
        var next = new DateTime(now.Year, now.Month, now.Day, clock.Hour, clock.Minute, 0);
        if (next <= now) next = next.AddDays(1);
        return TimeZoneInfo.ConvertTimeToUtc(next, tz);
    }

    public static string? CodexPlan(string text)
        => CodexAccount.Match(text) is { Success: true } account
            ? account.Groups["plan"].Value.Trim() : null;

    public static string? CodexAccountIdentity(string text)
        => CodexAccount.Match(text) is { Success: true } account
            ? account.Groups["identity"].Value.Trim() : null;
}
