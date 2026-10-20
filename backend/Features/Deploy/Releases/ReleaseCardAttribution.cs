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
}
