using System.Text.RegularExpressions;

namespace AgentStudio.Deploy;

/// <summary>A card key in a retained commit message identifies work after rebase or squash.</summary>
public static class ReleaseCardAttribution
{
    private static readonly Regex KeyPattern = new(
        @"(?<![A-Za-z0-9-])(?<key>[A-Za-z][A-Za-z0-9]*(?:-[A-Za-z0-9]+)*-[0-9]+)(?![A-Za-z0-9-])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static IReadOnlySet<string> FindKeys(IEnumerable<string> messages)
        => messages.SelectMany(message => KeyPattern.Matches(message).Select(match => match.Groups["key"].Value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static bool Includes(string key, IEnumerable<string> integrationShas,
        IReadOnlySet<string> revisions, IReadOnlySet<string> mentioned, IReadOnlySet<string> priorKeys)
        => integrationShas.Any(revisions.Contains) || !priorKeys.Contains(key) && mentioned.Contains(key);

    public static bool ContainsKey(string message, string key)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(message)) return false;
        return Regex.IsMatch(message, $@"(?<![A-Za-z0-9-]){Regex.Escape(key)}(?![A-Za-z0-9-])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
