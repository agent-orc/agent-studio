using System.Text.RegularExpressions;

namespace AgentStudio.OrchestrationDecisions;

public sealed record IntegrationBlockerFacts(
    bool KnownCodingParent, bool ValidDependency);

/// <summary>Recognizes an integration prerequisite rather than an operator decision.</summary>
public static partial class IntegrationBlockerPolicy
{
    public const string WaitLane = TaskStates.Ready;

    public static bool MayWait(IntegrationBlockerFacts facts)
        => facts.KnownCodingParent && facts.ValidDependency;
    [GeneratedRegex(@"\b(?:(?<key>[a-z][a-z0-9]*-\d+)-implementation-not-integrated|missing-(?<key>[a-z][a-z0-9]*-\d+)-implementation|implementation\s+of\s+(?:card\s+)?(?<key>[a-z][a-z0-9]*-\d+)\s+(?:is\s+)?not\s+integrated|(?<key>[a-z][a-z0-9]*-\d+)\s+implementation\s+(?:is\s+)?not\s+integrated)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Prerequisite();

    public static string? Dependency(string category, string reason)
    {
        if (category is not ("agent-blocked" or "environment-blocker")) return null;
        var keys = Prerequisite().Matches(reason).Select(match => match.Groups["key"].Value.ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return keys.Length == 1 ? keys[0] : null;
    }
}
